import Foundation
import Darwin

struct DiscoveryMessage: Codable {
    var protocolVersion = 1
    var type = "announce"
    var fingerprint: String
    var name: String
    var groupName = ""
    var platform = "macOS"
    var port = 45831
    var receivingEnabled = true
}

extension DiscoveryMessage {
    private enum CodingKeys: String, CodingKey {
        case protocolVersion, type, fingerprint, name, groupName, platform, port, receivingEnabled
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
    }
}

/// IPv4 interface broadcasts only; no DNS, WAN discovery, relay or NAT traversal.
final class LANDiscovery {
    private var socketFD: Int32 = -1
    private var source: DispatchSourceRead?
    private var timer: DispatchSourceTimer?
    private var lastProbeReply = Date.distantPast
    private let queue = DispatchQueue(label: "Boardcast.Discovery", qos: .utility)
    private let messageLock = NSLock()
    private var currentMessage: DiscoveryMessage
    var onPeer: ((PeerInfo) -> Void)?
    var onTick: (() -> Void)?
    init(message: () -> DiscoveryMessage) { currentMessage = message() }
    private func message() -> DiscoveryMessage { messageLock.lock(); defer { messageLock.unlock() }; return currentMessage }
    func setMessage(_ value: DiscoveryMessage) { messageLock.lock(); currentMessage = value; messageLock.unlock() }
    deinit { stop() }

    static func interfaces() -> [(address: UInt32, mask: UInt32)] {
        var head: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&head) == 0, let first = head else { return [] }
        defer { freeifaddrs(head) }
        var result: [(UInt32, UInt32)] = []
        var current: UnsafeMutablePointer<ifaddrs>? = first
        while let pointer = current {
            defer { current = pointer.pointee.ifa_next }
            let value = pointer.pointee
            let name = String(cString: value.ifa_name)
            guard value.ifa_flags & UInt32(IFF_UP) != 0,
                  value.ifa_flags & UInt32(IFF_LOOPBACK) == 0,
                  !name.hasPrefix("utun"), !name.hasPrefix("ppp"),
                  let address = value.ifa_addr, let mask = value.ifa_netmask,
                  Int32(address.pointee.sa_family) == AF_INET else { continue }
            let a = UnsafeRawPointer(address).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            let m = UnsafeRawPointer(mask).assumingMemoryBound(to: sockaddr_in.self).pointee.sin_addr.s_addr
            guard m != 0 else { continue }
            result.append((a, m))
        }
        return result
    }

    static func isLocal(_ host: String) -> Bool {
        var address = in_addr()
        guard inet_pton(AF_INET, host, &address) == 1 else { return false }
        let hostOrder = UInt32(bigEndian: address.s_addr)
        guard hostOrder != 0, hostOrder >> 24 != 127, hostOrder >> 28 != 14,
              hostOrder >> 24 != 0, hostOrder != UInt32.max else { return false }
        return interfaces().contains { item in
            let network = item.address & item.mask
            return address.s_addr & item.mask == network && address.s_addr != network && address.s_addr != (network | ~item.mask)
        }
    }

    func start() throws {
        socketFD = Darwin.socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard socketFD >= 0 else { throw BoardcastError.invalid("无法创建发现 socket") }
        var yes: Int32 = 1
        setsockopt(socketFD, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(socketFD, SOL_SOCKET, SO_BROADCAST, &yes, socklen_t(MemoryLayout<Int32>.size))
        var ttl: Int32 = 1
        setsockopt(socketFD, IPPROTO_IP, IP_TTL, &ttl, socklen_t(MemoryLayout<Int32>.size))
        fcntl(socketFD, F_SETFL, O_NONBLOCK)
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(45831).bigEndian
        address.sin_addr.s_addr = INADDR_ANY
        let status = withUnsafePointer(to: &address) { ptr in
            ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { Darwin.bind(socketFD, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) }
        }
        guard status == 0 else { Darwin.close(socketFD); socketFD = -1; throw BoardcastError.invalid("发现端口 45831 被占用") }
        let read = DispatchSource.makeReadSource(fileDescriptor: socketFD, queue: queue)
        read.setEventHandler { [weak self] in self?.readAvailable() }
        read.resume()
        source = read
        let clock = DispatchSource.makeTimerSource(queue: queue)
        clock.schedule(deadline: .now() + 60, repeating: 60, leeway: .seconds(5))
        clock.setEventHandler { [weak self] in self?.broadcast(); self?.onTick?() }
        clock.resume()
        timer = clock
        queue.async { [weak self] in self?.broadcast(type: "probe") }
    }

    func stop() {
        timer?.cancel(); timer = nil
        source?.cancel(); source = nil
        if socketFD >= 0 { Darwin.close(socketFD); socketFD = -1 }
    }

    func announce() { queue.async { [weak self] in self?.broadcast() } }
    private func broadcast(type: String = "announce") {
        var packet = message(); packet.type = type
        guard socketFD >= 0, let data = try? JSONEncoder().encode(packet), data.count <= 2048 else { return }
        for item in Self.interfaces() {
            var target = sockaddr_in()
            target.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            target.sin_family = sa_family_t(AF_INET)
            target.sin_port = UInt16(45831).bigEndian
            target.sin_addr.s_addr = item.address | ~item.mask
            data.withUnsafeBytes { bytes in
                withUnsafePointer(to: &target) { ptr in
                    ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                        _ = sendto(socketFD, bytes.baseAddress, bytes.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                    }
                }
            }
        }
    }

    private func readAvailable() {
        let capacity = 2049
        var buffer = [UInt8](repeating: 0, count: capacity)
        for _ in 0..<64 {
            guard socketFD >= 0 else { return }
            var source = sockaddr_in()
            var length = socklen_t(MemoryLayout<sockaddr_in>.size)
            let received = withUnsafeMutablePointer(to: &source) { ptr in
                ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(socketFD, &buffer, capacity, 0, $0, &length) }
            }
            if received <= 0 { return }
            guard received <= 2048 else { continue }
            var ip = source.sin_addr
            var text = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            guard inet_ntop(AF_INET, &ip, &text, socklen_t(INET_ADDRSTRLEN)) != nil else { continue }
            let address = String(cString: text)
            guard Self.isLocal(address),
                  let incoming = try? JSONDecoder().decode(DiscoveryMessage.self, from: Data(buffer.prefix(received))),
                  incoming.protocolVersion == 1, ["announce", "probe"].contains(incoming.type),
                  incoming.fingerprint.range(of: "^[a-fA-F0-9]{64}$", options: .regularExpression) != nil,
                  incoming.fingerprint.caseInsensitiveCompare(message().fingerprint) != .orderedSame,
                  incoming.name.utf16.count <= 256, DeviceGroups.isValid(incoming.groupName), incoming.platform.count <= 128, (1024...65535).contains(incoming.port) else { continue }
            if incoming.type == "probe", Date().timeIntervalSince(lastProbeReply) >= 2 {
                lastProbeReply = Date(); broadcast()
            }
            onPeer?(PeerInfo(fingerprint: incoming.fingerprint.lowercased(), name: incoming.name, groupName: DeviceGroups.normalized(incoming.groupName), platform: incoming.platform, address: address, port: incoming.port, receivingEnabled: incoming.receivingEnabled))
        }
    }
}
