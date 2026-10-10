import Foundation
import Combine
import CryptoKit
import Network
import Darwin

/// The discovery version is a hint. Authorization comes from the signed app in the image.
struct MacUpdateImage: Codable, Sendable, Equatable {
    var type: String
    var protocolVersion = 1
    var runtime = "osx-universal"
    var version: String
    var build: String
    var bytes: Int64
    var sha256: String

    func validate(current: MacReleaseIdentity, expectedType: String) throws {
        guard type == expectedType, protocolVersion == 1, runtime == current.runtime,
              bytes > 0, bytes <= MacUpdateService.maximumImageBytes,
              DeviceIdentity.isFingerprint(sha256), !build.isEmpty, build.utf8.count <= 32,
              try MacVersion(version) > current.version else {
            throw BoardcastError.invalid(L10n.string("Mac 更新清单无效或版本未提高"))
        }
    }
}

private struct MacUpdateRequest: Codable {
    var type: String
    var protocolVersion = 1
    var runtime = "osx-universal"
    var sha256: String? = nil
}

private struct PublishedMacRelease: Sendable {
    let image: URL
    let manifest: MacUpdateImage
}

private struct ReadyMacRelease: Codable, Sendable {
    let id: UUID
    let identity: MacReleaseIdentity
}

enum MacUpdatePhase: String { case idle, publishing, downloading, verifying, ready, failed, cancelled }

/// One explicit download and at most two LAN image readers. All UI state belongs to MainActor.
@MainActor
final class MacUpdateService: ObservableObject {
    nonisolated static let maximumImageBytes: Int64 = 2 * 1024 * 1024 * 1024
    @Published private(set) var phase: MacUpdatePhase = .idle
    @Published private(set) var status = ""
    @Published private(set) var progress = 0.0
    @Published private(set) var readyVersion: String?
    @Published private(set) var activeOperations = 0
    private(set) var advertisedPort = 0
    private(set) var advertisedVersion: String?
    let advertisedRuntime = "osx-universal"
    var onAdvertisement: (() -> Void)?
    var hasActiveOperations: Bool { activeOperations > 0 }

    private let identity: DeviceIdentity
    private let updateDirectory: URL
    private var current: MacReleaseIdentity?
    private var published: PublishedMacRelease?
    private var ready: ReadyMacRelease?
    private var listener: NWListener?
    private var listenerWaiter: CheckedContinuation<Void, Error>?
    private var listenerTimeout: Task<Void, Never>?
    private var startup: Task<(MacReleaseIdentity, PublishedMacRelease, ReadyMacRelease?), Error>?
    private var downloadTask: Task<ReadyMacRelease, Error>?
    private var downloadWire: WireConnection?
    private var sessions: [UUID: (wire: WireConnection, task: Task<Void, Never>)] = [:]
    private var installationTask: Task<(URL, URL), Error>?
    private var preparingInstall = false
    private var stopped = true
    private var quiesced = false
    private var generation = UUID()

    init(identity: DeviceIdentity, dataDirectory: URL) {
        self.identity = identity
        // AppModel passes its Application Support/Boardcast directory.
        self.updateDirectory = dataDirectory.lastPathComponent == "Updates"
            ? dataDirectory : dataDirectory.appendingPathComponent("Updates", isDirectory: true)
    }

    func candidates(peers: [PeerInfo]) -> [PeerInfo] {
        guard let current else { return [] }
        return peers.filter { peer in
            guard peer.lanOnline, !peer.departureHint, peer.runtime == advertisedRuntime,
                  (1...65535).contains(peer.updatePort),
                  DeviceIdentity.isFingerprint(peer.fingerprint), LANDiscovery.isLocal(peer.address),
                  let version = peer.updateVersion.flatMap({ try? MacVersion($0) }) else { return false }
            return version > current.version
        }.sorted {
            let a = try? MacVersion($0.updateVersion ?? "")
            let b = try? MacVersion($1.updateVersion ?? "")
            if a != b { return (a ?? current.version) > (b ?? current.version) }
            return $0.name.localizedStandardCompare($1.name) == .orderedAscending
        }
    }

    func start() async {
        guard !Task.isCancelled, startup == nil, downloadTask == nil, !preparingInstall else { return }
        stopped = false; quiesced = false
        let token = generation
        let directory = updateDirectory
        phase = .publishing
        status = L10n.string("正在验证 Mac 正式版本并准备局域网更新镜像")
        let task = Task.detached(priority: .utility) {
            try Self.checkUpdateDirectory(directory)
            let current = try MacRelease.current()
            _ = try MacRelease.verify(app: Bundle.main.bundleURL, expected: current, requireNewer: false)
            let restored = try? Self.restoreReady(directory: directory, current: current)
            let release = try Self.publish(directory: directory, current: current)
            return (current, release, restored)
        }
        startup = task; refreshActivity()
        defer { startup = nil; refreshActivity() }
        do {
            let result = try await withTaskCancellationHandler(operation: { try await task.value }, onCancel: { task.cancel() })
            guard !stopped, generation == token else { return }
            guard !task.isCancelled else { throw CancellationError() }
            current = result.0; published = result.1; ready = result.2
            readyVersion = ready?.identity.version.rawValue
            if !quiesced { try await listen() }
            phase = ready == nil ? .idle : .ready
            status = ready == nil ? L10n.string("正式 Mac 版本已可在局域网分享") : L10n.string("更新已验证，可重启安装")
        } catch {
            guard !stopped, generation == token else { return }
            phase = error is CancellationError ? .cancelled : .failed
            status = error is CancellationError ? L10n.string("Mac 更新准备已取消") : L10n.string(error.localizedDescription)
            clearAdvertisement()
        }
    }

    /// Stop accepting new work while existing readers/download verification finish.
    func quiesce() {
        quiesced = true
        listener?.cancel(); listener = nil
        completeListener(.failure(CancellationError()))
        clearAdvertisement()
    }

    func resumeSharing() async {
        guard !Task.isCancelled else { return }
        guard !stopped else { await start(); return }
        quiesced = false
        guard published != nil else { await start(); return }
        do { try await listen() }
        catch { phase = .failed; status = L10n.string(error.localizedDescription) }
    }

    func stop() {
        stopped = true; generation = UUID()
        quiesce()
        startup?.cancel(); installationTask?.cancel()
        cancel()
        for session in sessions.values { session.task.cancel(); session.wire.close() }
    }

    func cancel() {
        startup?.cancel()
        if startup != nil, listenerWaiter != nil {
            listener?.cancel(); listener = nil
            completeListener(.failure(CancellationError())); clearAdvertisement()
        }
        downloadTask?.cancel(); downloadWire?.close()
    }

    private func refreshActivity() {
        activeOperations = sessions.count + (startup == nil ? 0 : 1) + (downloadTask == nil ? 0 : 1) + (preparingInstall ? 1 : 0)
    }

    private func clearAdvertisement() {
        advertisedPort = 0; advertisedVersion = nil
        if let onAdvertisement { onAdvertisement() }
    }

    private func completeListener(_ result: Result<Void, Error>) {
        guard let waiter = listenerWaiter else { return }
        listenerWaiter = nil; listenerTimeout?.cancel(); listenerTimeout = nil
        waiter.resume(with: result)
    }

    private func completeListenerReady() {
        guard let waiter = listenerWaiter else { return }
        listenerWaiter = nil; listenerTimeout?.cancel(); listenerTimeout = nil
        waiter.resume()
    }

    private func listen() async throws {
        guard !stopped, !quiesced, published != nil, listener == nil else { return }
        let next = try NWListener(using: identity.parameters(), on: .any)
        listener = next; next.newConnectionLimit = 2
        let token = generation
        next.newConnectionHandler = { [weak self, weak next] connection in
            Task { @MainActor in
                guard let self, let next, !self.stopped, !self.quiesced, self.generation == token,
                      self.listener === next else { connection.cancel(); return }
                self.accept(connection)
            }
        }
        next.stateUpdateHandler = { [weak self, weak next] state in
            Task { @MainActor in
                guard let self, let next, self.listener === next, self.generation == token else { return }
                switch state {
                case .ready:
                    guard let port = next.port, let published = self.published else { return }
                    self.advertisedPort = Int(port.rawValue)
                    self.advertisedVersion = published.manifest.version
                    if let advertise = self.onAdvertisement { advertise() }
                    self.completeListenerReady()
                case .failed(let error):
                    self.completeListener(.failure(error)); self.listener = nil
                    self.clearAdvertisement()
                case .cancelled: self.completeListener(.failure(CancellationError()))
                default: break
                }
            }
        }
        try await withCheckedThrowingContinuation { (waiter: CheckedContinuation<Void, Error>) in
            listenerWaiter = waiter
            next.start(queue: DispatchQueue(label: "Boardcast.MacUpdate.Listener", qos: .utility))
            listenerTimeout = Task { @MainActor [weak self, weak next] in
                do { try await Task.sleep(nanoseconds: 15_000_000_000) } catch { return }
                guard let self, let next, self.listener === next, self.listenerWaiter != nil else { return }
                self.completeListener(.failure(BoardcastError.invalid(L10n.string("Mac 更新监听启动超时"))))
                self.listener = nil; next.cancel(); self.clearAdvertisement()
            }
        }
    }

    private func accept(_ connection: NWConnection) {
        guard sessions.count < 2, let release = published,
              case .hostPort(let host, _) = connection.endpoint,
              LANDiscovery.isLocal(String(describing: host)) else {
            connection.cancel()
            listener?.newConnectionLimit = max(0, 2 - sessions.count)
            return
        }
        let id = UUID(), wire = WireConnection(connection)
        let task = Task { [weak self] in
            await Self.serve(wire, release: release)
            wire.close()
            self?.sessions.removeValue(forKey: id)
            self?.listener?.newConnectionLimit = max(0, 2 - (self?.sessions.count ?? 0))
            self?.refreshActivity()
        }
        sessions[id] = (wire, task)
        listener?.newConnectionLimit = max(0, 2 - sessions.count)
        refreshActivity()
    }

    nonisolated private static func serve(_ wire: WireConnection, release: PublishedMacRelease) async {
        do {
            try await wire.start()
            _ = try DeviceIdentity.peerFingerprint(wire.connection)
            let info = try await wire.readJSON(MacUpdateRequest.self)
            guard info.type == "mac-update-info", info.protocolVersion == 1,
                  info.runtime == "osx-universal" else { throw BoardcastError.invalid(L10n.string("Invalid update request")) }
            try await wire.sendJSON(release.manifest)
            let image = try await wire.readJSON(MacUpdateRequest.self)
            guard image.type == "mac-update-image", image.protocolVersion == 1,
                  image.runtime == "osx-universal", image.sha256 == release.manifest.sha256 else {
                throw BoardcastError.invalid(L10n.string("Invalid update image request"))
            }
            let file = try openRegular(release.image, flags: O_RDONLY)
            defer { try? file.close() }
            guard try file.seekToEnd() == UInt64(release.manifest.bytes) else {
                throw BoardcastError.invalid(L10n.string("Update image changed"))
            }
            try file.seek(toOffset: 0)
            var header = release.manifest; header.type = "mac-update-image"
            try await wire.sendJSON(header)
            var remaining = release.manifest.bytes
            while remaining > 0 {
                try Task.checkCancellation()
                guard let data = try file.read(upToCount: Int(min(remaining, 128 * 1024))), !data.isEmpty else {
                    throw BoardcastError.invalid(L10n.string("Update image ended early"))
                }
                try await wire.send(data); remaining -= Int64(data.count)
            }
        } catch { /* A peer may disappear; sharing must not change the user's download state. */ }
    }

    func download(peer: PeerInfo) async {
        guard !Task.isCancelled, !stopped, !quiesced, startup == nil, downloadTask == nil, !preparingInstall, let current,
              candidates(peers: [peer]).count == 1,
              let port = NWEndpoint.Port(rawValue: UInt16(peer.updatePort)) else { return }
        let token = generation, directory = updateDirectory
        do {
            let parameters = try identity.parameters(expectedFingerprint: peer.fingerprint)
            let wire = WireConnection(NWConnection(host: NWEndpoint.Host(peer.address), port: port, using: parameters))
            downloadWire = wire; progress = 0; phase = .downloading
            status = L10n.string("正在下载 Mac 更新")
            let task = Task.detached(priority: .utility) { [self] in
                try await Self.receive(wire: wire, peer: peer, directory: directory, current: current) { value, verifying in
                    await MainActor.run {
                        guard self.generation == token else { return }
                        self.progress = value
                        if verifying { self.phase = .verifying; self.status = L10n.string("正在验证 Developer ID、公证和通用架构") }
                    }
                }
            }
            downloadTask = task; refreshActivity()
            defer { downloadTask = nil; downloadWire = nil; wire.close(); refreshActivity() }
            let result = try await withTaskCancellationHandler(operation: { try await task.value }, onCancel: { task.cancel() })
            guard generation == token, !stopped else { return }
            ready = result; readyVersion = result.identity.version.rawValue
            progress = 1; phase = .ready; status = L10n.string("更新已验证，可重启安装")
        } catch {
            guard generation == token, !stopped else { return }
            phase = error is CancellationError ? .cancelled : .failed
            status = error is CancellationError ? L10n.string("Mac 更新下载已取消") : L10n.string(error.localizedDescription)
        }
    }

    /// Starts the signed helper only after the caller has stopped new file/update work.
    func prepareInstall() async throws -> Process {
        guard !Task.isCancelled, !hasActiveOperations, !stopped, let current, let ready else {
            throw BoardcastError.invalid(L10n.string("请先等待传输结束并下载已验证的 Mac 更新"))
        }
        quiesce(); preparingInstall = true; refreshActivity(); phase = .verifying
        status = L10n.string("正在验证更新安装条件")
        let directory = updateDirectory, token = generation
        defer {
            preparingInstall = false; installationTask = nil; refreshActivity()
            if phase == .verifying { phase = .ready; status = L10n.string("更新已验证，可重启安装") }
        }
        let worker = Task.detached(priority: .utility) {
            try Self.checkUpdateDirectory(directory)
            let actual = try MacRelease.current()
            guard actual == current else { throw BoardcastError.invalid(L10n.string("Current release identity changed")) }
            let app = try MacRelease.validateInstallTarget(Bundle.main.bundleURL)
            let staged = Self.readyApp(directory: directory, id: ready.id)
            let candidate = try MacRelease.verify(app: staged, expected: current)
            guard candidate == ready.identity else { throw BoardcastError.invalid(L10n.string("Prepared release identity changed")) }
            let id = UUID()
            let install = directory.appendingPathComponent("Install", isDirectory: true).appendingPathComponent(id.uuidString, isDirectory: true)
            try MacRelease.createPrivateDirectory(install)
            let helper = install.appendingPathComponent("BoardcastUpdater")
            let source = app.appendingPathComponent("Contents/MacOS/BoardcastUpdater")
            try MacRelease.verifyHelper(at: source, expected: current)
            try FileManager.default.copyItem(at: source, to: helper)
            try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: helper.path)
            try MacRelease.verifyHelper(at: helper, expected: current)
            let pid = ProcessInfo.processInfo.processIdentifier
            let plan = MacInstallPlan(id: id, currentApp: app.path, stagedApp: staged.path,
                dataDirectory: directory.path, parentPID: pid,
                parentLaunchDate: try MacRelease.processLaunchDate(pid: pid),
                currentIdentity: current, candidateIdentity: candidate)
            let path = install.appendingPathComponent("plan.json")
            try MacRelease.writePrivateJSON(plan, at: path)
            try Task.checkCancellation()
            return (helper, path)
        }
        installationTask = worker
        let prepared = try await withTaskCancellationHandler(operation: { try await worker.value }, onCancel: { worker.cancel() })
        guard !stopped, generation == token else { throw CancellationError() }
        let process = Process()
        process.executableURL = prepared.0; process.arguments = ["--plan", prepared.1.path]
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("DYLD_") && !$0.key.hasPrefix("LD_") }
        environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin"
        process.environment = environment
        process.standardInput = FileHandle.nullDevice
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        try process.run()
        do {
            let marker = prepared.1.deletingLastPathComponent().appendingPathComponent("helper-ready.json")
            let planID = prepared.1.deletingLastPathComponent().lastPathComponent
            let deadline = Date().addingTimeInterval(60)
            var acknowledged = false
            while Date() < deadline {
                try Task.checkCancellation()
                guard process.isRunning, !stopped, generation == token else { throw CancellationError() }
                if Self.helperAcknowledged(marker: marker, planID: planID) {
                    acknowledged = true; break
                }
                try await Task.sleep(nanoseconds: 100_000_000)
            }
            guard acknowledged, process.isRunning else {
                throw BoardcastError.invalid(L10n.string("Mac 安装助手未确认就绪，应用继续运行"))
            }
            phase = .ready
            status = L10n.string("安装助手已启动，正在退出并重启应用")
            return process
        } catch {
            if process.isRunning { process.terminate() }
            phase = .ready; status = L10n.string(error.localizedDescription)
            throw error
        }
    }

    nonisolated private static func receive(wire: WireConnection, peer: PeerInfo, directory: URL,
        current: MacReleaseIdentity, report: @escaping @Sendable (Double, Bool) async -> Void) async throws -> ReadyMacRelease {
        try checkUpdateDirectory(directory)
        let id = UUID()
        let taskDirectory = directory.appendingPathComponent("Downloads", isDirectory: true).appendingPathComponent(id.uuidString, isDirectory: true)
        try MacRelease.createPrivateDirectory(taskDirectory)
        defer { wire.close(); try? FileManager.default.removeItem(at: taskDirectory) }
        try await wire.start()
        guard try DeviceIdentity.peerFingerprint(wire.connection).lowercased() == peer.fingerprint.lowercased() else {
            throw BoardcastError.invalid(L10n.string("Mac 更新来源的设备身份已改变"))
        }
        try await wire.sendJSON(MacUpdateRequest(type: "mac-update-info"))
        let info = try await wire.readJSON(MacUpdateImage.self)
        try info.validate(current: current, expectedType: "mac-update-info")
        guard info.version == peer.updateVersion else {
            throw BoardcastError.invalid(L10n.string("Mac 更新来源版本已改变，请刷新设备列表"))
        }
        try await wire.sendJSON(MacUpdateRequest(type: "mac-update-image", sha256: info.sha256))
        let header = try await wire.readJSON(MacUpdateImage.self)
        try header.validate(current: current, expectedType: "mac-update-image")
        var comparable = header; comparable.type = "mac-update-info"
        guard comparable == info else { throw BoardcastError.invalid(L10n.string("Mac 更新镜像与清单不一致")) }
        let image = taskDirectory.appendingPathComponent("Boardcast.dmg.part")
        let file = try openRegular(image, flags: O_WRONLY | O_CREAT | O_EXCL)
        var digest = SHA256(), received: Int64 = 0
        do {
            while received < header.bytes {
                try Task.checkCancellation()
                let data = try await wire.read(Int(min(header.bytes - received, 128 * 1024)))
                try file.write(contentsOf: data); digest.update(data: data); received += Int64(data.count)
                await report(Double(received) / Double(header.bytes), false)
            }
            try file.synchronize(); try file.close()
        } catch { try? file.close(); throw error }
        let actualHash = digest.finalize().map { String(format: "%02x", $0) }.joined()
        guard actualHash == header.sha256.lowercased() else {
            throw BoardcastError.invalid(L10n.string("Mac 更新镜像 SHA-256 校验失败"))
        }
        try Task.checkCancellation(); wire.close(); await report(1, true)
        let finalImage = taskDirectory.appendingPathComponent("Boardcast.dmg")
        try FileManager.default.moveItem(at: image, to: finalImage)
        let readyDirectory = directory.appendingPathComponent("Ready", isDirectory: true).appendingPathComponent(id.uuidString, isDirectory: true)
        try MacRelease.createPrivateDirectory(readyDirectory)
        var retainApp = false
        defer { if !retainApp { try? FileManager.default.removeItem(at: readyDirectory) } }
        let mount = taskDirectory.appendingPathComponent("Mount", isDirectory: true)
        try MacRelease.createPrivateDirectory(mount)
        defer { _ = try? MacRelease.run(executable: "/usr/bin/hdiutil", arguments: ["detach", mount.path], timeout: 60, cancellable: false) }
        let plist = try MacRelease.run(executable: "/usr/bin/hdiutil", arguments: ["attach", finalImage.path,
            "-readonly", "-nobrowse", "-noautoopen", "-mountpoint", mount.path, "-plist"], timeout: 120)
        try validateMountedImage(plist, at: mount)
        let contents = try FileManager.default.contentsOfDirectory(at: mount, includingPropertiesForKeys: [.isSymbolicLinkKey, .isDirectoryKey])
        let apps = try contents.filter { url in
            guard url.pathExtension == "app" else { return false }
            let values = try url.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey])
            return values.isDirectory == true && values.isSymbolicLink != true
        }
        guard apps.count == 1, apps[0].lastPathComponent == "Boardcast.app" else {
            throw BoardcastError.invalid(L10n.string("Mac 更新镜像必须只包含一个 Boardcast.app"))
        }
        let staged = readyApp(directory: directory, id: id)
        _ = try MacRelease.run(executable: "/usr/bin/ditto", arguments: [apps[0].path, staged.path], timeout: 180)
        try Task.checkCancellation()
        let candidate = try MacRelease.verify(app: staged, expected: current)
        guard candidate.version.rawValue == header.version, candidate.build == header.build else {
            throw BoardcastError.invalid(L10n.string("Mac 更新清单与已签名应用版本不一致"))
        }
        try Task.checkCancellation()
        let ready = ReadyMacRelease(id: id, identity: candidate)
        let pointer = directory.appendingPathComponent("ready.json")
        let previous = (try? MacRelease.boundedData(at: pointer, maximum: 64 * 1024))
            .flatMap { try? JSONDecoder().decode(ReadyMacRelease.self, from: $0) }
        try MacRelease.writePrivateJSON(ready, at: pointer)
        retainApp = true
        if let previous, previous.id != id { removeKnownReady(directory: directory, id: previous.id) }
        return ready
    }

    nonisolated private static func readyApp(directory: URL, id: UUID) -> URL {
        directory.appendingPathComponent("Ready", isDirectory: true)
            .appendingPathComponent(id.uuidString, isDirectory: true).appendingPathComponent("Boardcast.app", isDirectory: true)
    }

    nonisolated private static func restoreReady(directory: URL, current: MacReleaseIdentity) throws -> ReadyMacRelease? {
        let pointer = directory.appendingPathComponent("ready.json")
        let data = try MacRelease.boundedData(at: pointer, maximum: 64 * 1024)
        let ready = try JSONDecoder().decode(ReadyMacRelease.self, from: data)
        guard ready.identity.teamID == current.teamID, ready.identity.bundleID == current.bundleID,
              ready.identity.runtime == current.runtime else {
            throw BoardcastError.invalid(L10n.string("Saved update identity changed"))
        }
        if ready.identity.version <= current.version {
            removeKnownReady(directory: directory, id: ready.id)
            try MacRelease.requireNoSymlinks(pointer)
            try FileManager.default.removeItem(at: pointer)
            return nil
        }
        let actual = try MacRelease.verify(app: readyApp(directory: directory, id: ready.id), expected: current)
        guard actual == ready.identity else { throw BoardcastError.invalid(L10n.string("Saved update identity changed")) }
        return ready
    }

    nonisolated static func removeKnownReady(directory: URL, id: UUID) {
        let folder = directory.appendingPathComponent("Ready", isDirectory: true).appendingPathComponent(id.uuidString, isDirectory: true)
        removeKnownCache(folder: folder, entryName: "Boardcast.app", isDirectory: true)
    }

    nonisolated private static func removeKnownCache(folder: URL, entryName: String, isDirectory: Bool) {
        do {
            try MacRelease.requireNoSymlinks(folder)
            let contents = try FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard contents.count == 1, contents[0].lastPathComponent == entryName else { return }
            let flags = try contents[0].resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard flags.isDirectory == isDirectory, flags.isSymbolicLink != true else { return }
            try FileManager.default.removeItem(at: folder)
        } catch { /* Keep an unrecognized or inaccessible cache for manual inspection. */ }
    }

    private struct PublishedRecord: Codable {
        let id: UUID
        let manifest: MacUpdateImage
    }

    nonisolated private static func publish(directory: URL, current: MacReleaseIdentity) throws -> PublishedMacRelease {
        let cache = directory.appendingPathComponent("Published", isDirectory: true)
        try MacRelease.createPrivateDirectory(cache)
        let pointer = directory.appendingPathComponent("published.json")
        var previous: PublishedRecord?
        if let data = try? MacRelease.boundedData(at: pointer, maximum: 64 * 1024) {
            previous = try? JSONDecoder().decode(PublishedRecord.self, from: data)
        }
        if let previous, previous.manifest.version == current.version.rawValue,
           previous.manifest.build == current.build, previous.manifest.runtime == current.runtime,
           previous.manifest.type == "mac-update-info", previous.manifest.protocolVersion == 1 {
            let image = cache.appendingPathComponent(previous.id.uuidString, isDirectory: true).appendingPathComponent("Boardcast.dmg")
            if let hash = try? hashFile(image), hash.bytes == previous.manifest.bytes,
               hash.digest == previous.manifest.sha256 {
                return PublishedMacRelease(image: image, manifest: previous.manifest)
            }
        }
        try Task.checkCancellation()
        let id = UUID(), task = cache.appendingPathComponent(UUID().uuidString, isDirectory: true)
        // Only these newly allocated private directories belong to this publishing operation.
        let output = cache.appendingPathComponent(id.uuidString, isDirectory: true)
        try MacRelease.createPrivateDirectory(task); try MacRelease.createPrivateDirectory(output)
        var committed = false
        defer {
            try? FileManager.default.removeItem(at: task)
            if !committed { try? FileManager.default.removeItem(at: output) }
        }
        let source = task.appendingPathComponent("Boardcast.app", isDirectory: true)
        _ = try MacRelease.run(executable: "/usr/bin/ditto", arguments: [Bundle.main.bundleURL.path, source.path], timeout: 180)
        let copied = try MacRelease.verify(app: source, expected: current, requireNewer: false)
        guard copied == current else { throw BoardcastError.invalid(L10n.string("Published release changed during copying")) }
        try Task.checkCancellation()
        let image = output.appendingPathComponent("Boardcast.dmg")
        _ = try MacRelease.run(executable: "/usr/bin/hdiutil", arguments: ["create", "-format", "UDZO", "-fs", "HFS+",
            "-volname", "Boardcast", "-srcfolder", task.path, image.path], timeout: 180)
        try Task.checkCancellation()
        let hash = try hashFile(image)
        let manifest = MacUpdateImage(type: "mac-update-info", version: current.version.rawValue,
            build: current.build, bytes: hash.bytes, sha256: hash.digest)
        let record = PublishedRecord(id: id, manifest: manifest)
        try MacRelease.writePrivateJSON(record, at: pointer)
        committed = true
        if let previous, previous.id != id {
            let old = cache.appendingPathComponent(previous.id.uuidString, isDirectory: true)
            removeKnownCache(folder: old, entryName: "Boardcast.dmg", isDirectory: false)
        }
        return PublishedMacRelease(image: image, manifest: manifest)
    }

    nonisolated private static func checkUpdateDirectory(_ directory: URL) throws {
        let expected = try MacRelease.updateDirectory()
        guard directory.standardized.path == expected.standardized.path else {
            throw BoardcastError.invalid(L10n.string("Mac 更新缓存必须位于本用户的 Boardcast 应用支持目录"))
        }
        try MacRelease.createPrivateDirectory(directory)
    }

    nonisolated private static func openRegular(_ url: URL, flags: Int32) throws -> FileHandle {
        try MacRelease.requireNoSymlinks(url, allowMissing: (flags & O_CREAT) != 0)
        let fd = Darwin.open(url.path, flags | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK, mode_t(0o600))
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        var metadata = stat()
        guard fstat(fd, &metadata) == 0, metadata.st_mode & S_IFMT == S_IFREG else {
            Darwin.close(fd); throw BoardcastError.invalid(L10n.string("Mac 更新缓存不是普通文件"))
        }
        return FileHandle(fileDescriptor: fd, closeOnDealloc: true)
    }

    nonisolated private static func hashFile(_ url: URL) throws -> (bytes: Int64, digest: String) {
        let file = try openRegular(url, flags: O_RDONLY)
        defer { try? file.close() }
        let size = try file.seekToEnd()
        guard size > 0, size <= UInt64(maximumImageBytes) else {
            throw BoardcastError.invalid(L10n.string("Mac 更新镜像超过 2 GiB 限额或为空"))
        }
        try file.seek(toOffset: 0)
        var digest = SHA256(), count: Int64 = 0
        while let data = try file.read(upToCount: 128 * 1024), !data.isEmpty {
            try Task.checkCancellation(); digest.update(data: data); count += Int64(data.count)
            guard count <= maximumImageBytes else { throw BoardcastError.invalid(L10n.string("Update image grew while reading")) }
        }
        guard count == Int64(size) else { throw BoardcastError.invalid(L10n.string("Update image changed while reading")) }
        return (count, digest.finalize().map { String(format: "%02x", $0) }.joined())
    }

    nonisolated private static func helperAcknowledged(marker: URL, planID: String) -> Bool {
        guard let data = try? MacRelease.boundedData(at: marker, maximum: 4096),
              let value = try? JSONDecoder().decode(MacUpdateHelperReady.self, from: data) else { return false }
        return value.id.uuidString == planID && value.phase == "waiting"
    }

    nonisolated static func validateMountedImage(_ data: Data, at mount: URL) throws {
        guard let value = try PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any],
              let entities = value["system-entities"] as? [[String: Any]] else {
            throw BoardcastError.invalid(L10n.string("系统未返回有效的 Mac 更新镜像挂载记录"))
        }
        let mounted = entities.compactMap { $0["mount-point"] as? String }
        guard mounted.count == 1, mounted[0] == mount.path else {
            throw BoardcastError.invalid(L10n.string("Mac 更新镜像挂载位置不符合要求"))
        }
    }
}
