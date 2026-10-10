import Foundation
import Network
import CryptoKit
import Darwin

struct WireMessage: Codable {
    var type: String
    var version: Int?
    var peer: PeerInfo?
    var offer: TransferOffer?
    var message: String?
}

/// Legacy v1 streaming only. Network.framework owns TLS and socket behavior.
final class TransferService: @unchecked Sendable {
    private let identity: DeviceIdentity
    private let settings: () async -> AppSettings
    private let queue = DispatchQueue(label: "Boardcast.Listener", qos: .utility)
    private let gate = NSLock()
    private var listener: NWListener?
    private var stopped = false
    private var accepting = true
    private var active = 0
    private var sessions: [String: Session] = [:]
    private var waiting: [(Session, CheckedContinuation<Void, Error>)] = []
    var onStatus: ((String) -> Void)?
    var onConnectionsChanged: (() -> Void)?
    var onProgress: ((TransferProgress) async -> Void)?
    var onOffer: ((TransferOffer, String, ReceiveDecision) async -> OfferApproval)?

    init(identity: DeviceIdentity, settings: @escaping () async -> AppSettings) {
        self.identity = identity; self.settings = settings
    }

    func start(port: Int) throws {
        guard let value = UInt16(exactly: port), let port = NWEndpoint.Port(rawValue: value) else {
            throw BoardcastError.invalid(L10n.string("端口无效"))
        }
        let server = try NWListener(using: identity.parameters(), on: port)
        gate.lock()
        guard listener == nil else { gate.unlock(); throw BoardcastError.invalid(L10n.string("监听器已启动")) }
        stopped = false; accepting = true; listener = server
        gate.unlock()
        server.newConnectionHandler = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            guard case let .hostPort(host, _) = connection.endpoint,
                  LANDiscovery.isLocal(String(describing: host)) else { connection.cancel(); return }
            let id = "connection-" + UUID().uuidString
            let session = Session()
            guard self.registerIncoming(id: id, session: session) else { connection.cancel(); return }
            session.bind(WireConnection(connection))
            Task { await self.receive(id: id, session: session) }
        }
        server.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state { self?.onStatus?(error.localizedDescription) }
        }
        server.start(queue: queue)
    }

    var hasActiveConnections: Bool {
        gate.lock(); defer { gate.unlock() }
        return !sessions.isEmpty || active > 0 || !waiting.isEmpty
    }

    /// Refuse new incoming connections while allowing UI-queued sends to register and finish.
    func quiesce() {
        gate.lock()
        accepting = false
        let server = listener
        listener = nil
        gate.unlock()
        server?.cancel()
    }

    func stop() {
        gate.lock()
        stopped = true; accepting = false
        let server = listener
        listener = nil
        let current = Array(sessions.values)
        let tickets = waiting
        waiting.removeAll()
        gate.unlock()
        server?.cancel()
        for session in current { session.cancel(explicit: false) }
        for (_, continuation) in tickets { continuation.resume(throwing: CancellationError()) }
        if !tickets.isEmpty { onConnectionsChanged?() }
    }

    func cancel(id: String) {
        gate.lock()
        let session = sessions[id]
        let cancellation = session?.cancellationAction(explicit: true)
        let ticket = session.flatMap { selected in waiting.firstIndex { $0.0 === selected } }
        let continuation = ticket.map { waiting.remove(at: $0).1 }
        gate.unlock()
        cancellation?()
        continuation?.resume(throwing: CancellationError())
    }

    private func registerIncoming(id: String, session: Session) -> Bool {
        gate.lock(); defer { gate.unlock() }
        guard !stopped, accepting, active < 2 else { return false }
        sessions[id] = session; active += 1
        return true
    }

    private func registerSend(id: String, session: Session) throws {
        gate.lock(); defer { gate.unlock() }
        guard !stopped, sessions[id] == nil, sessions.count < 130 else {
            throw BoardcastError.invalid(L10n.string("传输已停止、任务编号重复或队列已满"))
        }
        sessions[id] = session
    }

    private func adoptReceiveID(_ old: String, new: String, session: Session) throws {
        gate.lock(); defer { gate.unlock() }
        guard sessions[new] == nil else { throw BoardcastError.invalid(L10n.string("同一接收任务已经存在")) }
        sessions.removeValue(forKey: old)
        sessions[new] = session
    }

    private func unregister(id: String) {
        gate.lock()
        let removed = sessions.removeValue(forKey: id) != nil
        gate.unlock()
        if removed { onConnectionsChanged?() }
    }

    private func acquireSend(_ session: Session) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            gate.lock()
            if stopped || session.isCancelled {
                gate.unlock(); continuation.resume(throwing: CancellationError()); return
            }
            if active < 2 {
                active += 1; gate.unlock(); continuation.resume(); return
            }
            guard waiting.count < 128 else {
                gate.unlock(); continuation.resume(throwing: BoardcastError.invalid(L10n.string("传输队列已满"))); return
            }
            waiting.append((session, continuation))
            gate.unlock()
        }
    }

    private func releaseSlot() {
        gate.lock()
        active -= 1
        let next: (Session, CheckedContinuation<Void, Error>)? = stopped || waiting.isEmpty ? nil : waiting.removeFirst()
        if next != nil { active += 1 }
        gate.unlock()
        next?.1.resume()
        onConnectionsChanged?()
    }

    private func hello(_ configuration: AppSettings) -> WireMessage {
        WireMessage(type: "hello", version: 1, peer: PeerInfo(fingerprint: identity.fingerprint,
            name: configuration.deviceName, groupName: DeviceGroups.normalized(configuration.groupName),
            port: configuration.port, receivingEnabled: configuration.receivingEnabled))
    }

    static func validateHello(_ message: WireMessage, fingerprint: String) throws {
        guard message.type == "hello", message.version == 1, let peer = message.peer,
              peer.name.utf16.count <= 256, (1024...65535).contains(peer.port),
              peer.platform.utf16.count <= 128, (peer.appVersion?.utf16.count ?? 0) <= 64,
              (peer.runtime?.utf16.count ?? 0) <= 64, (peer.updateVersion?.utf16.count ?? 0) <= 64,
              peer.updatePort == 0 || (1024...65535).contains(peer.updatePort),
              DeviceGroups.isValid(peer.groupName),
              peer.fingerprint.caseInsensitiveCompare(fingerprint) == .orderedSame else {
            throw BoardcastError.invalid(L10n.string("设备身份或协议版本不匹配"))
        }
    }

    private func receive(id: String, session: Session) async {
        var localID = id
        guard let wire = session.wire else { unregister(id: id); releaseSlot(); return }
        defer { wire.close(); unregister(id: localID); releaseSlot() }
        var storage: ReceiveStorage?
        var progress: TransferProgress?
        do {
            try await wire.start()
            wire.setIdleTimeout(15)
            try session.checkCancellation()
            let authenticated = try DeviceIdentity.peerFingerprint(wire.connection)
            try Self.validateHello(try await wire.readJSON(WireMessage.self), fingerprint: authenticated)
            let initial = await settings()
            try await wire.sendJSON(hello(initial))
            let request = try await wire.readJSON(WireMessage.self)
            if request.type == "bye" { return }
            guard request.type == "offer", request.version == nil || request.version == 1,
                  let offer = request.offer else { throw BoardcastError.invalid(L10n.string("缺少文件清单")) }
            let decision = try ReceivePolicy.evaluate(settings: initial, offer: offer, fingerprint: authenticated)
            let receiveID = "receive-" + authenticated + "-" + offer.id
            try adoptReceiveID(localID, new: receiveID, session: session)
            localID = receiveID
            var state = TransferProgress(id: localID, direction: .receiving, peerFingerprint: authenticated,
                phase: .awaitingApproval, completedBytes: 0, totalBytes: offer.totalBytes, savedFiles: 0,
                destination: decision.destination, message: decision.reason, peerName: offer.senderName)
            progress = state
            await onProgress?(state)
            guard initial.receivingEnabled else { throw Rejection(L10n.string("接收已暂停")) }
            let approval: OfferApproval
            if decision.autoAccept { approval = .accept(destination: nil) }
            else {
                wire.setIdleTimeout(310)
                try await wire.sendJSON(WireMessage(type: "pending"))
                let pending = ApprovalWaiter()
                session.bindApproval(pending)
                wire.whenClosed { [weak pending] in pending?.resolve(.reject) }
                approval = await pending.wait { [weak self] in
                    await self?.onOffer?(offer, authenticated, decision) ?? .reject
                }
                session.bindApproval(nil)
            }
            try session.checkCancellation()
            guard case let .accept(chosenDestination) = approval else { throw Rejection(L10n.string("接收方已拒绝或确认超时")) }
            // Approval is a delay boundary. Re-read policy, limits and destination.
            let current = await settings()
            let updated = try ReceivePolicy.evaluate(settings: current, offer: offer, fingerprint: authenticated)
            guard current.receivingEnabled else { throw Rejection(L10n.string("接收已暂停")) }
            if decision.autoAccept && !updated.autoAccept { throw Rejection(L10n.string("自动接收权限已改变，请重新发送并确认")) }
            let selected = chosenDestination ?? updated.destination
            guard selected.hasPrefix("/") else { throw BoardcastError.invalid(L10n.string("接收路径必须为绝对路径")) }
            try session.checkCancellation()
            let receiver = try ReceiveStorage(offer: offer, fingerprint: authenticated,
                destination: URL(fileURLWithPath: selected, isDirectory: true))
            storage = receiver
            state.destination = receiver.destination.path
            wire.setIdleTimeout(90)
            try await wire.sendJSON(WireMessage(type: "accept"))
            state.phase = .transferring
            state.message = nil
            await onProgress?(state)
            var lastProgress = Date.distantPast
            for entry in offer.entries {
                try session.checkCancellation()
                if entry.isDirectory { try receiver.receiveDirectory(entry) }
                else {
                    try await receiver.receiveFile(entry, read: { try await wire.read($0) }, onBytes: { count in
                        state.completedBytes += Int64(count)
                        if Date().timeIntervalSince(lastProgress) >= 0.2 {
                            state.phase = .transferring; await self.onProgress?(state); lastProgress = Date(); progress = state
                        }
                    }, onVerifying: {
                        state.phase = .verifying; await self.onProgress?(state)
                    }, onVerified: {
                        state.phase = .saving; await self.onProgress?(state)
                    }, checkCancellation: {
                        try session.checkCancellation(); try Task.checkCancellation()
                    })
                    state.savedFiles = receiver.savedFiles
                    await onProgress?(state)
                }
                progress = state
            }
            // Once all verified files are saved, losing the receipt cannot undo them.
            try? receiver.finish()
            state.phase = .completed
            state.message = L10n.string("已接收并校验")
            progress = state
            await onProgress?(state)
            do { try await wire.sendJSON(WireMessage(type: "complete", message: "Received and verified.")) }
            catch { onStatus?(L10n.format("已保存至 %@；完成回执未送达", receiver.destination.path)) }
        } catch {
            let cancelled = session.isCancelled || error is CancellationError
            if cancelled && session.explicitCancellation { storage?.cancel() }
            else { storage?.interrupted() }
            if var state = progress {
                state.savedFiles = storage?.savedFiles ?? state.savedFiles
                state.phase = cancelled ? (session.explicitCancellation ? .cancelled : .interrupted)
                    : (error is Rejection ? .rejected : .failed)
                state.message = error.localizedDescription
                await onProgress?(state)
            }
            if !cancelled { try? await wire.sendJSON(WireMessage(type: "reject", message: error.localizedDescription)) }
            onStatus?(error.localizedDescription)
        }
    }

    func send(prepared: PreparedTransfer, summary: String, to peer: PeerInfo, id: String) async throws {
        guard LANDiscovery.isLocal(peer.address), peer.fingerprint.range(of: "^[a-fA-F0-9]{64}$", options: .regularExpression) != nil,
              let number = UInt16(exactly: peer.port), let port = NWEndpoint.Port(rawValue: number) else {
            throw BoardcastError.invalid(L10n.string("仅允许直连本地网络内的设备"))
        }
        let session = Session()
        try registerSend(id: id, session: session)
        defer { unregister(id: id) }
        var state = TransferProgress(id: id, direction: .sending, peerFingerprint: peer.fingerprint.lowercased(),
            phase: .queued, completedBytes: 0,
            totalBytes: TransferOffer(senderName: "", senderFingerprint: identity.fingerprint, entries: prepared.entries).totalBytes,
            savedFiles: 0, destination: nil,
            message: nil, peerName: peer.name)
        await onProgress?(state)
        do {
            let cancelSend: @Sendable () -> Void = { [id] in self.cancel(id: id) }
            try await withTaskCancellationHandler(operation: {
                try await acquireSend(session)
                defer { releaseSlot(); session.wire?.close() }
                try session.checkCancellation()
                let configuration = await settings()
                let offer = TransferOffer(id: id, senderName: configuration.deviceName,
                    senderFingerprint: identity.fingerprint, summary: summary, entries: prepared.entries)
                try ReceivePolicy.validate(offer, settings: configuration)
                state.totalBytes = offer.totalBytes
                let connection = NWConnection(host: NWEndpoint.Host(peer.address), port: port,
                    using: try identity.parameters(expectedFingerprint: peer.fingerprint))
                let wire = WireConnection(connection)
                session.bind(wire)
                state.phase = .preparing; await onProgress?(state)
                try await wire.start()
                wire.setIdleTimeout(15)
                try session.checkCancellation()
                let authenticated = try DeviceIdentity.peerFingerprint(connection)
                guard authenticated.caseInsensitiveCompare(peer.fingerprint) == .orderedSame else {
                    throw BoardcastError.invalid(L10n.string("对方 TLS 指纹与选定设备不一致"))
                }
                try await wire.sendJSON(hello(configuration))
                try Self.validateHello(try await wire.readJSON(WireMessage.self), fingerprint: authenticated)
                try await wire.sendJSON(WireMessage(type: "offer", version: 1, offer: offer))
                wire.setIdleTimeout(310)
                var response = try await wire.readJSON(WireMessage.self)
                while response.type == "pending" {
                    state.phase = .awaitingApproval; await onProgress?(state)
                    response = try await wire.readJSON(WireMessage.self)
                }
                guard response.type == "accept" else { throw Rejection(response.message ?? L10n.string("对方未接受传输")) }
                wire.setIdleTimeout(90)
                state.phase = .transferring; await onProgress?(state)
                var lastProgress = Date.distantPast
                for entry in prepared.entries where !entry.isDirectory {
                    try session.checkCancellation()
                    guard let source = prepared.sources[entry.relativePath] else { throw BoardcastError.invalid(L10n.string("本地文件清单不完整")) }
                    try await streamFile(source, entry: entry, wire: wire, session: session) { count in
                        state.completedBytes += Int64(count)
                        if Date().timeIntervalSince(lastProgress) >= 0.2 { await self.onProgress?(state); lastProgress = Date() }
                    }
                }
                state.phase = .verifying; await onProgress?(state)
                do {
                    let complete = try await wire.readJSON(WireMessage.self)
                    guard complete.type == "complete" else { throw Rejection(complete.message ?? L10n.string("接收方未确认完成")) }
                } catch {
                    if !session.isCancelled && !(error is Rejection) {
                        state.phase = .receiptUnconfirmed
                        state.message = L10n.string("文件已发送，完成回执未确认；重发前请检查接收方")
                        await onProgress?(state)
                    }
                    throw error
                }
                state.phase = .completed
                state.savedFiles = prepared.entries.filter { !$0.isDirectory }.count
                state.message = L10n.string("接收方已确认")
                await onProgress?(state)
            }, onCancel: cancelSend)
        } catch {
            if state.phase != .receiptUnconfirmed {
                state.phase = session.isCancelled ? (session.explicitCancellation ? .cancelled : .interrupted)
                    : (error is Rejection ? .rejected : .failed)
                state.message = error.localizedDescription; await onProgress?(state)
            }
            throw error
        }
    }

    private func streamFile(_ url: URL, entry: TransferEntry, wire: WireConnection,
                            session: Session, onBytes: (Int) async -> Void) async throws {
        // Check every ancestor, then bind reads and stat to the same no-follow descriptor.
        _ = try ReceivePolicy.safeDestination(root: url.deletingLastPathComponent(), relative: url.lastPathComponent)
        let fd = Darwin.open(url.path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
        guard fd >= 0 else { throw BoardcastError.invalid(L10n.string("无法打开源文件（禁止符号链接）")) }
        let handle = FileHandle(fileDescriptor: fd, closeOnDealloc: true)
        defer { try? handle.close() }
        var before = stat()
        guard fstat(fd, &before) == 0, (before.st_mode & S_IFMT) == S_IFREG,
              before.st_size == entry.size else { throw BoardcastError.invalid(L10n.string("文件在准备后发生变化")) }
        var remaining = entry.size
        var hash = SHA256()
        while remaining > 0 {
            try session.checkCancellation()
            try Task.checkCancellation()
            guard let bytes = try handle.read(upToCount: Int(min(remaining, 256 * 1024))), !bytes.isEmpty else {
                throw BoardcastError.invalid(L10n.string("源文件提前结束"))
            }
            hash.update(data: bytes)
            try await wire.send(bytes)
            remaining -= Int64(bytes.count)
            await onBytes(bytes.count)
        }
        var after = stat()
        guard (try handle.read(upToCount: 1) ?? Data()).isEmpty, fstat(fd, &after) == 0,
              after.st_size == before.st_size, after.st_mtimespec.tv_sec == before.st_mtimespec.tv_sec,
              after.st_mtimespec.tv_nsec == before.st_mtimespec.tv_nsec,
              after.st_ctimespec.tv_sec == before.st_ctimespec.tv_sec,
              after.st_ctimespec.tv_nsec == before.st_ctimespec.tv_nsec else {
            throw BoardcastError.invalid(L10n.string("源文件在传输中发生变化"))
        }
        try await wire.send(Data(hash.finalize()))
    }

    private struct Rejection: LocalizedError {
        let reason: String
        init(_ reason: String) { self.reason = reason }
        var errorDescription: String? { reason }
    }

    private final class Session: @unchecked Sendable {
        private let lock = NSLock()
        private var connection: WireConnection?
        private var approval: ApprovalWaiter?
        private var cancelled = false
        private var explicit = false
        var wire: WireConnection? { lock.lock(); defer { lock.unlock() }; return connection }
        var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
        var explicitCancellation: Bool { lock.lock(); defer { lock.unlock() }; return explicit }
        func checkCancellation() throws { if isCancelled { throw CancellationError() } }
        func bind(_ wire: WireConnection) {
            lock.lock(); connection = wire; let cancel = cancelled; lock.unlock()
            if cancel { wire.close() }
        }
        func bindApproval(_ value: ApprovalWaiter?) {
            lock.lock(); approval = value; let cancel = cancelled; lock.unlock()
            if cancel { value?.resolve(.reject) }
        }
        func cancellationAction(explicit: Bool) -> @Sendable () -> Void {
            lock.lock(); cancelled = true; self.explicit = self.explicit || explicit
            let wire = connection; let pending = approval; lock.unlock()
            return { wire?.close(); pending?.resolve(.reject) }
        }
        func cancel(explicit: Bool) { cancellationAction(explicit: explicit)() }
    }

    private final class ApprovalWaiter: @unchecked Sendable {
        private let lock = NSLock()
        private var result: OfferApproval?
        private var continuation: CheckedContinuation<OfferApproval, Never>?
        private var task: Task<Void, Never>?
        func wait(_ request: @escaping @Sendable () async -> OfferApproval) async -> OfferApproval {
            await withCheckedContinuation { (continuation: CheckedContinuation<OfferApproval, Never>) in
                lock.lock()
                if let result { lock.unlock(); continuation.resume(returning: result); return }
                self.continuation = continuation
                lock.unlock()
                let work = Task { [self] in self.resolve(await request()) }
                lock.lock(); task = work; let finished = result != nil; lock.unlock()
                if finished { work.cancel() }
                let timeout: @Sendable () -> Void = { [self] in self.resolve(.reject) }
                DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 300, execute: timeout)
            }
        }
        func resolve(_ result: OfferApproval) {
            lock.lock()
            guard self.result == nil else { lock.unlock(); return }
            self.result = result
            let continuation = self.continuation; self.continuation = nil
            let work = task; task = nil
            lock.unlock()
            continuation?.resume(returning: result)
            work?.cancel()
        }
    }
}
