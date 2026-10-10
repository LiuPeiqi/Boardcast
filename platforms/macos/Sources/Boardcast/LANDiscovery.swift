import Foundation
import Darwin
import SystemConfiguration

struct DiscoveryMessage: Sendable {
    var protocolVersion = 1
    var type = "announce"
    var fingerprint: String
    var name: String
    var groupName = ""
    var platform = "macOS"
    var port = 45831
    var receivingEnabled = true
    var lanOnline = true
    var appVersion: String? = nil
    var runtime: String? = nil
    var updatePort = 0
    var updateVersion: String? = nil
}

extension DiscoveryMessage: Codable {
    private enum CodingKeys: String, CodingKey {
        case protocolVersion, type, fingerprint, name, groupName, platform, port, receivingEnabled
        case lanOnline, appVersion, runtime, updatePort, updateVersion
    }

    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(protocolVersion, forKey: .protocolVersion)
        try values.encode(type, forKey: .type)
        try values.encode(fingerprint, forKey: .fingerprint)
        try values.encode(name, forKey: .name)
        try values.encode(groupName, forKey: .groupName)
        try values.encode(platform, forKey: .platform)
        try values.encode(port, forKey: .port)
        try values.encode(receivingEnabled, forKey: .receivingEnabled)
        try values.encode(lanOnline, forKey: .lanOnline)
        try values.encodeIfPresent(appVersion, forKey: .appVersion)
        try values.encodeIfPresent(runtime, forKey: .runtime)
        // A missing update port means "no update offer". Encoding 0 would invent one.
        if updatePort != 0 { try values.encode(updatePort, forKey: .updatePort) }
        try values.encodeIfPresent(updateVersion, forKey: .updateVersion)
    }

    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(fingerprint: try values.decode(String.self, forKey: .fingerprint),
                  name: try values.decode(String.self, forKey: .name))
        protocolVersion = try values.decode(Int.self, forKey: .protocolVersion)
        type = try values.decode(String.self, forKey: .type)
        if values.contains(.groupName) { groupName = try values.decode(String.self, forKey: .groupName) }
        platform = try values.decodeIfPresent(String.self, forKey: .platform) ?? platform
        port = try values.decodeIfPresent(Int.self, forKey: .port) ?? port
        receivingEnabled = try values.decodeIfPresent(Bool.self, forKey: .receivingEnabled) ?? true
        lanOnline = try values.decodeIfPresent(Bool.self, forKey: .lanOnline) ?? true
        appVersion = try values.decodeIfPresent(String.self, forKey: .appVersion)
        runtime = try values.decodeIfPresent(String.self, forKey: .runtime)
        updatePort = try values.decodeIfPresent(Int.self, forKey: .updatePort) ?? 0
        updateVersion = try values.decodeIfPresent(String.self, forKey: .updateVersion)
    }
}

/// IPv4 broadcasts on physical Wi-Fi/Ethernet interfaces, with no DNS or routed discovery.
/// All mutable state, including callback registration, belongs to `queue`.
final class LANDiscovery: @unchecked Sendable {
    static let discoveryPort = 45831
    static let maximumPacketBytes = 2048
    typealias Interface = (address: UInt32, mask: UInt32, index: UInt32)

    private let queue = DispatchQueue(label: "Boardcast.Discovery", qos: .utility)
    private let queueKey = DispatchSpecificKey<UInt8>()
    private var socketFD: Int32 = -1
    private var source: DispatchSourceRead?
    private var socketClosure: DispatchGroup?
    private var timer: DispatchSourceTimer?
    private var lastProbeReply = Date.distantPast
    private var currentMessage: DiscoveryMessage
    private var peerCallback: (@Sendable (PeerInfo) -> Void)?
    private var departureCallback: (@Sendable (PeerInfo) -> Void)?
    private var tickCallback: (@Sendable () -> Void)?
    private var errorCallback: (@Sendable (String) -> Void)?

    /// Callbacks run on the discovery queue; UI callers must hop to the main actor.
    var onPeer: (@Sendable (PeerInfo) -> Void)? {
        get { withQueue { peerCallback } }
        set { withQueue { peerCallback = newValue } }
    }
    var onDeparture: (@Sendable (PeerInfo) -> Void)? {
        get { withQueue { departureCallback } }
        set { withQueue { departureCallback = newValue } }
    }
    var onTick: (@Sendable () -> Void)? {
        get { withQueue { tickCallback } }
        set { withQueue { tickCallback = newValue } }
    }
    var onError: (@Sendable (String) -> Void)? {
        get { withQueue { errorCallback } }
        set { withQueue { errorCallback = newValue } }
    }

    init(message: () -> DiscoveryMessage) {
        currentMessage = message()
        queue.setSpecific(key: queueKey, value: 1)
    }
    deinit { stop() }

    private func withQueue<T>(_ operation: () throws -> T) rethrows -> T {
        if DispatchQueue.getSpecific(key: queueKey) != nil { return try operation() }
        return try queue.sync(execute: operation)
    }

    func setMessage(_ value: DiscoveryMessage) { withQueue { currentMessage = value } }

    static func interfaces() -> [Interface] { (try? readInterfaces()) ?? [] }

    private static func readInterfaces() throws -> [Interface] {
        // Use the OS's interface type rather than guessing physical hardware from a BSD name.
        let configured = SCNetworkInterfaceCopyAll() as? [SCNetworkInterface] ?? []
        let physicalNames = Set(configured.compactMap { interface -> String? in
            guard let type = SCNetworkInterfaceGetInterfaceType(interface),
                  CFEqual(type, kSCNetworkInterfaceTypeEthernet) || CFEqual(type, kSCNetworkInterfaceTypeIEEE80211),
                  let name = SCNetworkInterfaceGetBSDName(interface) else { return nil }
            return name as String
        })
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0 else { throw socketFailure("读取网络接口") }
        guard let first = head else { return [] }
        defer { freeifaddrs(first) }
        var result: [Interface] = []
        var current: UnsafeMutablePointer<ifaddrs>? = first
        while let pointer = current {
            defer { current = pointer.pointee.ifa_next }
            let value = pointer.pointee
            let name = String(cString: value.ifa_name)
            let required = UInt32(IFF_UP | IFF_RUNNING | IFF_BROADCAST)
            guard physicalNames.contains(name), value.ifa_flags & required == required,
                  value.ifa_flags & UInt32(IFF_LOOPBACK | IFF_POINTOPOINT) == 0,
                  let address = value.ifa_addr, let mask = value.ifa_netmask,
                  Int32(address.pointee.sa_family) == AF_INET,
                  Int32(mask.pointee.sa_family) == AF_INET else { continue }
            let a = UnsafeRawPointer(address).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            let m = UnsafeRawPointer(mask).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            let index = if_nametoindex(name)
            guard m != 0, m != UInt32.max, index != 0 else { continue }
            result.append((a, m, index))
        }
        return result
    }

    static func isLocal(_ host: String) -> Bool { isLocal(host, interfaces: interfaces()) }

    static func isLocal(_ host: String, interfaces: [Interface]) -> Bool {
        var address = in_addr()
        guard inet_pton(AF_INET, host, &address) == 1 else { return false }
        let hostOrder = UInt32(bigEndian: address.s_addr)
        guard hostOrder >> 24 != 0, hostOrder >> 24 != 127, hostOrder < 0xe0000000 else { return false }
        return interfaces.contains { item in
            guard item.mask != 0 else { return false }
            let network = item.address & item.mask
            return address.s_addr & item.mask == network && address.s_addr != network && address.s_addr != (network | ~item.mask)
        }
    }

    func start() throws {
        try withQueue {
            guard socketFD < 0 else { return }
            do { try startOnQueue() }
            catch { errorCallback?(error.localizedDescription); throw error }
        }
    }

    private func startOnQueue() throws {
        let fd = Darwin.socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { throw Self.socketFailure("创建发现 socket") }
        var installed = false
        defer { if !installed { Darwin.close(fd) } }
        try Self.setOption(fd, level: SOL_SOCKET, name: SO_REUSEADDR, value: 1)
        try Self.setOption(fd, level: SOL_SOCKET, name: SO_BROADCAST, value: 1)
        try Self.setOption(fd, level: IPPROTO_IP, name: IP_TTL, value: 1)
        let flags = fcntl(fd, F_GETFL)
        guard flags >= 0, fcntl(fd, F_SETFL, flags | O_NONBLOCK) == 0 else {
            throw Self.socketFailure("设置非阻塞发现 socket")
        }
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(Self.discoveryPort).bigEndian
        address.sin_addr.s_addr = INADDR_ANY
        let status = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                Darwin.bind(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard status == 0 else { throw Self.socketFailure(L10n.format("绑定发现端口 %d", Self.discoveryPort)) }
        let read = DispatchSource.makeReadSource(fileDescriptor: fd, queue: queue)
        read.setEventHandler { [weak self] in
            guard let self, self.socketFD == fd else { return }
            self.readAvailable()
        }
        // Dispatch requires the cancellation handler to close an observed descriptor.
        // Capture this descriptor, so a later restart cannot close the new socket.
        let closure = DispatchGroup()
        closure.enter()
        read.setCancelHandler { _ = Darwin.close(fd); closure.leave() }
        socketClosure = closure
        socketFD = fd
        source = read
        installed = true
        read.resume()
        let clock = DispatchSource.makeTimerSource(queue: queue)
        clock.schedule(deadline: .now() + 60, repeating: 60, leeway: .seconds(5))
        clock.setEventHandler { [weak self] in
            guard let self, self.socketFD == fd else { return }
            self.broadcast()
            self.tickCallback?()
        }
        timer = clock
        clock.resume()
        lastProbeReply = .distantPast
        broadcast()
        broadcast(type: "probe")
    }

    /// Sends the departure hint before cancelling the socket. Safe to call repeatedly.
    func stop() {
        let calledOnQueue = DispatchQueue.getSpecific(key: queueKey) != nil
        let closure: DispatchGroup? = withQueue {
            guard socketFD >= 0 else { return nil }
            broadcast(type: "goodbye")
            socketFD = -1
            timer?.cancel(); timer = nil
            source?.cancel(); source = nil
            let closure = socketClosure
            socketClosure = nil
            return closure
        }
        // Allow an immediate replacement service to rebind the fixed discovery port.
        // The cancellation handler runs on queue, so it must never wait for itself.
        if !calledOnQueue { closure?.wait() }
    }

    func announce() { queue.async { [weak self] in self?.broadcast() } }
    func probe() { queue.async { [weak self] in self?.broadcast(type: "probe") } }

    private static func socketFailure(_ operation: String, code: Int32 = errno) -> BoardcastError {
        .invalid(L10n.format("%@失败：%@（%d）", L10n.string(operation), String(cString: strerror(code)), code))
    }
    private static func setOption(_ fd: Int32, level: Int32, name: Int32, value: Int32) throws {
        var option = value
        guard setsockopt(fd, level, name, &option, socklen_t(MemoryLayout<Int32>.size)) == 0 else {
            throw socketFailure("设置发现 socket")
        }
    }

    private func broadcast(type: String = "announce") {
        guard socketFD >= 0, currentMessage.lanOnline || type == "goodbye" else { return }
        var packet = currentMessage
        packet.type = type
        packet.lanOnline = type != "goodbye" && currentMessage.lanOnline
        packet.appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        let data: Data
        let active: [Interface]
        do {
            data = try JSONEncoder().encode(packet)
            guard data.count <= Self.maximumPacketBytes else {
                errorCallback?("发现信息超过 2048 字节，请缩短设备名或分组名。")
                return
            }
            active = try Self.readInterfaces()
        } catch { errorCallback?(error.localizedDescription); return }
        // Re-enumerate for every announcement/probe, including after waking or changing networks.
        // An empty interface list says nothing about the Local Network privacy decision.
        for item in active {
            do {
                try Self.setOption(socketFD, level: IPPROTO_IP, name: IP_BOUND_IF, value: Int32(bitPattern: item.index))
                var target = sockaddr_in()
                target.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                target.sin_family = sa_family_t(AF_INET)
                target.sin_port = UInt16(Self.discoveryPort).bigEndian
                target.sin_addr.s_addr = item.address | ~item.mask
                let sent = data.withUnsafeBytes { bytes in
                    withUnsafePointer(to: &target) { pointer in
                        pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                            sendto(socketFD, bytes.baseAddress, bytes.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                        }
                    }
                }
                if sent < 0 { errorCallback?(Self.socketFailure("发送局域网广播").localizedDescription) }
            } catch { errorCallback?(error.localizedDescription) }
        }
        // Keep incoming broadcasts visible on every physical interface.
        do { try Self.setOption(socketFD, level: IPPROTO_IP, name: IP_BOUND_IF, value: 0) }
        catch { errorCallback?(error.localizedDescription) }
    }

    static func validatedMessage(_ data: Data, localFingerprint: String) -> DiscoveryMessage? {
        guard data.count <= maximumPacketBytes,
              let incoming = try? JSONDecoder().decode(DiscoveryMessage.self, from: data),
              incoming.protocolVersion == 1, ["announce", "probe", "goodbye"].contains(incoming.type),
              incoming.fingerprint.utf8.count == 64,
              incoming.fingerprint.range(of: "^[a-fA-F0-9]{64}$", options: .regularExpression) != nil,
              incoming.fingerprint.caseInsensitiveCompare(localFingerprint) != .orderedSame,
              incoming.name.utf16.count <= 256, DeviceGroups.isValid(incoming.groupName),
              incoming.platform.utf16.count <= 128, (1024...65535).contains(incoming.port),
              (incoming.appVersion?.utf16.count ?? 0) <= 64, (incoming.runtime?.utf16.count ?? 0) <= 64,
              (incoming.updateVersion?.utf16.count ?? 0) <= 128,
              incoming.updatePort == 0 || (1024...65535).contains(incoming.updatePort) else { return nil }
        return incoming
    }

    private func readAvailable() {
        let capacity = Self.maximumPacketBytes + 1
        var buffer = [UInt8](repeating: 0, count: capacity)
        for _ in 0..<64 {
            guard socketFD >= 0 else { return }
            var sender = sockaddr_in()
            var length = socklen_t(MemoryLayout<sockaddr_in>.size)
            let received = buffer.withUnsafeMutableBytes { bytes in
                withUnsafeMutablePointer(to: &sender) { pointer in
                    pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        recvfrom(socketFD, bytes.baseAddress, bytes.count, 0, $0, &length)
                    }
                }
            }
            if received < 0 {
                let code = errno
                if code == EINTR { continue }
                if code != EAGAIN && code != EWOULDBLOCK {
                    errorCallback?(Self.socketFailure("读取局域网发现", code: code).localizedDescription)
                }
                return
            }
            guard received > 0, received <= Self.maximumPacketBytes,
                  Int32(sender.sin_family) == AF_INET,
                  length >= socklen_t(MemoryLayout<sockaddr_in>.size) else { continue }
            var ip = sender.sin_addr
            var text = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            guard inet_ntop(AF_INET, &ip, &text, socklen_t(INET_ADDRSTRLEN)) != nil else { continue }
            let address = String(cString: text)
            guard currentMessage.lanOnline, Self.isLocal(address),
                  let incoming = Self.validatedMessage(Data(buffer.prefix(received)), localFingerprint: currentMessage.fingerprint) else { continue }
            let peer = PeerInfo(fingerprint: incoming.fingerprint.lowercased(), name: incoming.name,
                                groupName: DeviceGroups.normalized(incoming.groupName), platform: incoming.platform,
                                address: address, port: incoming.port, receivingEnabled: incoming.receivingEnabled,
                                lanOnline: incoming.lanOnline, appVersion: incoming.appVersion, runtime: incoming.runtime,
                                updatePort: incoming.updatePort, updateVersion: incoming.updateVersion)
            if incoming.type == "goodbye" {
                // UDP is unauthenticated: the owner matches this endpoint and only sets a hint.
                // Never send goodbye through onPeer, which would refresh lastSeen and identity data.
                if Int(UInt16(bigEndian: sender.sin_port)) == Self.discoveryPort { departureCallback?(peer) }
                continue
            }
            if incoming.type == "probe", Date().timeIntervalSince(lastProbeReply) >= 2 {
                lastProbeReply = Date()
                broadcast()
            }
            peerCallback?(peer)
        }
    }
}
