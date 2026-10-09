import Foundation
import Network
import CryptoKit

struct WireMessage: Codable {
    var type: String
    var version: Int?
    var peer: PeerInfo?
    var offer: TransferOffer?
    var message: String?
}

/// Protocol implementation independent of the Windows source/runtime.
final class TransferService {
    private let identity: DeviceIdentity
    private let settings: () async -> AppSettings
    private let queue = DispatchQueue(label: "Boardcast.Listener", qos: .utility)
    private var listener: NWListener?
    private let gate = NSLock()
    private var active = 0
    var onStatus: ((String) -> Void)?
    var onOffer: ((TransferOffer, String, ReceiveDecision) async -> Bool)?

    init(identity: DeviceIdentity, settings: @escaping () async -> AppSettings) {
        self.identity = identity; self.settings = settings
    }
    func start(port: Int) throws {
        guard let portValue = UInt16(exactly: port), let endpointPort = NWEndpoint.Port(rawValue: portValue) else { throw BoardcastError.invalid("端口无效") }
        let server = try NWListener(using: identity.parameters(), on: endpointPort)
        server.newConnectionHandler = { [weak self] connection in
            guard let self else { connection.cancel(); return }
            guard case let .hostPort(host, _) = connection.endpoint,
                  LANDiscovery.isLocal(String(describing: host)), self.acquire() else { connection.cancel(); return }
            Task { await self.receive(connection); self.release() }
        }
        server.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state { self?.onStatus?(error.localizedDescription) }
        }
        listener = server
        server.start(queue: queue)
    }
    private func acquire() -> Bool {
        gate.lock(); defer { gate.unlock() }
        guard active < 2 else { return false }
        active += 1; return true
    }
    private func release() { gate.lock(); active -= 1; gate.unlock() }

    private func hello(_ settings: AppSettings) -> WireMessage {
        WireMessage(type: "hello", version: 1, peer: PeerInfo(fingerprint: identity.fingerprint, name: settings.deviceName, groupName: DeviceGroups.normalized(settings.groupName), port: settings.port, receivingEnabled: settings.receivingEnabled))
    }
    private func verifyHello(_ message: WireMessage, fingerprint: String) throws {
        guard message.type == "hello", message.version == 1, let peer = message.peer,
              peer.name.utf8.count <= 512, DeviceGroups.isValid(peer.groupName),
              peer.fingerprint.caseInsensitiveCompare(fingerprint) == .orderedSame else {
            throw BoardcastError.invalid("设备身份或协议版本不匹配")
        }
    }

    private func receive(_ connection: NWConnection) async {
        let wire = WireConnection(connection)
        defer { wire.close() }
        var staging: URL?
        defer { if let staging { try? FileManager.default.removeItem(at: staging) } }
        do {
            try await wire.start()
            let authenticated = try DeviceIdentity.peerFingerprint(connection)
            let clientHello = try await wire.readJSON(WireMessage.self)
            try verifyHello(clientHello, fingerprint: authenticated)
            let configuration = await settings()
            try await wire.sendJSON(hello(configuration))
            let request = try await wire.readJSON(WireMessage.self)
            if request.type == "bye" { return }
            guard request.type == "offer", let offer = request.offer else { throw BoardcastError.invalid("缺少文件清单") }
            let decision = try ReceivePolicy.evaluate(settings: configuration, offer: offer, fingerprint: authenticated)
            guard configuration.receivingEnabled else {
                try await wire.sendJSON(WireMessage(type: "reject", message: "接收已暂停")); return
            }
            var accepted = decision.autoAccept
            if !accepted {
                try await wire.sendJSON(WireMessage(type: "pending"))
                accepted = await onOffer?(offer, authenticated, decision) ?? false
            }
            guard accepted else { try await wire.sendJSON(WireMessage(type: "reject", message: "接收方已拒绝或确认超时")); return }
            let root = URL(fileURLWithPath: decision.destination, isDirectory: true)
            _ = try ReceivePolicy.safeDestination(root: root, relative: "check")
            try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
            let identifier = UUID().uuidString.lowercased()
            let temporary = try ReceivePolicy.safeDestination(root: root, relative: ".boardcast-part-" + identifier)
            try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            staging = temporary
            try await wire.sendJSON(WireMessage(type: "accept"))
            var bytes: Int64 = 0
            var lastProgress = Date.distantPast
            for entry in offer.entries {
                let destination = try ReceivePolicy.safeDestination(root: temporary, relative: entry.relativePath)
                if entry.isDirectory {
                    try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                    continue
                }
                try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
                guard FileManager.default.createFile(atPath: destination.path, contents: nil, attributes: [.posixPermissions: 0o600]) else { throw BoardcastError.invalid("无法创建接收文件") }
                let handle = try FileHandle(forWritingTo: destination)
                do {
                    var remaining = entry.size
                    var hash = SHA256()
                    while remaining > 0 {
                        let chunk = try await wire.read(Int(min(remaining, 1024 * 1024)))
                        try handle.write(contentsOf: chunk)
                        hash.update(data: chunk)
                        remaining -= Int64(chunk.count); bytes += Int64(chunk.count)
                        if Date().timeIntervalSince(lastProgress) > 0.5 {
                            onStatus?("接收 \(entry.relativePath) · \(ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file))")
                            lastProgress = Date()
                        }
                    }
                    let expected = try await wire.read(32)
                    guard Data(hash.finalize()) == expected else { throw BoardcastError.invalid("文件校验失败：\(entry.relativePath)") }
                    try handle.close()
                } catch { try? handle.close(); throw error }
            }
            // Publish the completed batch only after all hashes pass. Never overwrite an existing file.
            let final = try ReceivePolicy.safeDestination(root: root, relative: "Boardcast-" + identifier)
            try FileManager.default.moveItem(at: temporary, to: final)
            staging = nil
            try await wire.sendJSON(WireMessage(type: "complete"))
            onStatus?("已保存至 \(final.path)")
        } catch {
            try? await wire.sendJSON(WireMessage(type: "reject", message: error.localizedDescription))
            onStatus?(error.localizedDescription)
        }
    }

    func send(urls: [URL], to peer: PeerInfo) async throws {
        guard LANDiscovery.isLocal(peer.address), peer.fingerprint.range(of: "^[a-fA-F0-9]{64}$", options: .regularExpression) != nil,
              let portNumber = UInt16(exactly: peer.port), let port = NWEndpoint.Port(rawValue: portNumber) else {
            throw BoardcastError.invalid("仅允许直连当前网卡子网内的设备")
        }
        guard acquire() else { throw BoardcastError.invalid("已有两个传输在进行") }
        defer { release() }
        let configuration = await settings()
        let manifest = try buildManifest(urls: urls, settings: configuration)
        let offer = TransferOffer(senderName: configuration.deviceName, senderFingerprint: identity.fingerprint, entries: manifest.entries)
        try ReceivePolicy.validate(offer, settings: configuration)
        let connection = NWConnection(host: NWEndpoint.Host(peer.address), port: port, using: identity.parameters(expectedFingerprint: peer.fingerprint))
        let wire = WireConnection(connection)
        defer { wire.close() }
        try await wire.start()
        try await wire.sendJSON(hello(configuration))
        let serverHello = try await wire.readJSON(WireMessage.self)
        try verifyHello(serverHello, fingerprint: DeviceIdentity.peerFingerprint(connection))
        try await wire.sendJSON(WireMessage(type: "offer", offer: offer))
        var response = try await wire.readJSON(WireMessage.self)
        while response.type == "pending" {
            onStatus?("等待对方确认")
            response = try await wire.readJSON(WireMessage.self)
        }
        guard response.type == "accept" else { throw BoardcastError.invalid(response.message ?? "对方未接受传输") }
        var sent: Int64 = 0
        var lastProgress = Date.distantPast
        for entry in manifest.entries where !entry.isDirectory {
            guard let source = manifest.sources[entry.relativePath] else { throw BoardcastError.invalid("本地文件清单不完整") }
            let attributes = try FileManager.default.attributesOfItem(atPath: source.path)
            guard attributes[.type] as? FileAttributeType == .typeRegular,
                  (attributes[.size] as? NSNumber)?.int64Value == entry.size else { throw BoardcastError.invalid("文件在准备后发生变化") }
            let handle = try FileHandle(forReadingFrom: source)
            do {
                var remaining = entry.size
                var hash = SHA256()
                while remaining > 0 {
                    guard let chunk = try handle.read(upToCount: Int(min(remaining, 1024 * 1024))), !chunk.isEmpty else { throw BoardcastError.invalid("源文件提前结束") }
                    hash.update(data: chunk)
                    try await wire.send(chunk)
                    remaining -= Int64(chunk.count); sent += Int64(chunk.count)
                    if Date().timeIntervalSince(lastProgress) > 0.5 {
                        onStatus?("发送 \(entry.relativePath) · \(ByteCountFormatter.string(fromByteCount: sent, countStyle: .file))")
                        lastProgress = Date()
                    }
                }
                guard (try handle.read(upToCount: 1) ?? Data()).isEmpty else { throw BoardcastError.invalid("源文件大小已改变") }
                try await wire.send(Data(hash.finalize()))
                try handle.close()
            } catch { try? handle.close(); throw error }
        }
        let completed = try await wire.readJSON(WireMessage.self)
        guard completed.type == "complete" else { throw BoardcastError.invalid(completed.message ?? "接收方未确认完成") }
    }

    private func buildManifest(urls: [URL], settings: AppSettings) throws -> (entries: [TransferEntry], sources: [String: URL]) {
        var entries: [TransferEntry] = []
        var sources: [String: URL] = [:]
        func append(_ url: URL, relative: String) throws {
            let values = try url.resourceValues(forKeys: [.isSymbolicLinkKey, .isDirectoryKey, .isRegularFileKey, .fileSizeKey])
            guard values.isSymbolicLink != true, values.isDirectory == true || values.isRegularFile == true else { throw BoardcastError.invalid("不发送符号链接或特殊文件") }
            let directory = values.isDirectory == true
            let normalized = try ReceivePolicy.relativePath(relative)
            entries.append(TransferEntry(relativePath: normalized, size: directory ? 0 : Int64(values.fileSize ?? 0), isDirectory: directory))
            guard entries.count <= settings.maxEntries else { throw BoardcastError.invalid("条目数超出限额") }
            if !directory { sources[normalized] = url }
        }
        for root in urls {
            try append(root, relative: root.lastPathComponent)
            let values = try root.resourceValues(forKeys: [.isDirectoryKey])
            if values.isDirectory == true {
                var enumerationFailure: Error?
                guard let enumerator = FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isSymbolicLinkKey, .isDirectoryKey, .isRegularFileKey, .fileSizeKey], options: [], errorHandler: { _, error in enumerationFailure = error; return false }) else { throw BoardcastError.invalid("无法读取文件夹") }
                while let file = enumerator.nextObject() as? URL {
                    let suffix = String(file.path.dropFirst(root.path.count + 1))
                    try append(file, relative: root.lastPathComponent + "/" + suffix)
                }
                if let enumerationFailure { throw enumerationFailure }
            }
        }
        return (entries, sources)
    }
}
