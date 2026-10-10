import Foundation

/// Numeric, canonical RFC1918 IPv4 ranges. Discovery never grants device trust.
struct IPv4Subnet: Hashable, Sendable {
    let network: UInt32
    let prefix: Int
    var mask: UInt32 { UInt32.max << (32 - self.prefix) }
    var broadcast: UInt32 { network | ~mask }
    var cidr: String { Self.address(network) + "/" + String(prefix) }
    var firstHost: UInt32 { self.prefix >= 31 ? network : network + 1 }
    var lastHost: UInt32 { self.prefix >= 31 ? broadcast : broadcast - 1 }
    var hostCount: Int { Int(lastHost - firstHost) + 1 }

    init?(_ value: String) {
        let parts = value.split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2, let address = Self.parseAddress(String(parts[0])),
              let prefix = Int(parts[1]), (16...32).contains(prefix) else { return nil }
        let mask = UInt32.max << (32 - prefix)
        let network = address & mask
        guard Self.isPrivate(network), Self.isPrivate(network | ~mask) else { return nil }
        self.network = network
        self.prefix = prefix
    }

    func contains(_ address: String) -> Bool {
        guard let number = Self.parseAddress(address) else { return false }
        return number >= firstHost && number <= lastHost
    }

    static func parseAddress(_ value: String) -> UInt32? {
        let parts = value.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 4 else { return nil }
        var result: UInt32 = 0
        for part in parts {
            guard !part.isEmpty, part.count <= 3,
                  part.allSatisfy({ $0 >= "0" && $0 <= "9" }),
                  part.count == 1 || part.first != "0", let byte = UInt8(part) else { return nil }
            result = (result << 8) | UInt32(byte)
        }
        return result
    }

    static func address(_ value: UInt32) -> String {
        [24, 16, 8, 0].map { String((value >> $0) & 255) }.joined(separator: ".")
    }

    static func isPrivate(_ value: UInt32) -> Bool {
        value & 0xff000000 == 0x0a000000 || value & 0xfff00000 == 0xac100000 || value & 0xffff0000 == 0xc0a80000
    }

    static func isPrivateHost(_ address: String) -> Bool {
        guard let number = parseAddress(address) else { return false }
        return isPrivate(number)
    }

    static func fallback(for address: String) -> IPv4Subnet? {
        guard isPrivateHost(address), let number = parseAddress(address) else { return nil }
        return IPv4Subnet(Self.address(number & 0xffffff00) + "/24")
    }
}

/// seedAddress is local scan metadata; the wire representation only contains cidr and port.
struct DiscoverySubnet: Codable, Identifiable, Sendable, Equatable, Hashable {
    var cidr: String
    var port = 45831
    var seedAddress = ""
    var id: String { cidr + ":" + String(port) }
    var wireValue: DiscoverySubnet { DiscoverySubnet(cidr: cidr, port: port) }
}

extension DiscoverySubnet {
    private enum CodingKeys: String, CodingKey { case cidr, port, seedAddress }
    func encode(to encoder: Encoder) throws {
        var values = encoder.container(keyedBy: CodingKeys.self)
        try values.encode(cidr, forKey: .cidr)
        try values.encode(port, forKey: .port)
        if !seedAddress.isEmpty { try values.encode(seedAddress, forKey: .seedAddress) }
    }
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(cidr: try values.decode(String.self, forKey: .cidr))
        port = try values.decodeIfPresent(Int.self, forKey: .port) ?? 45831
        seedAddress = try values.decodeIfPresent(String.self, forKey: .seedAddress) ?? ""
    }
}

struct SubnetOffer: Sendable {
    var id: String
    var senderName: String
    var subnets: [DiscoverySubnet]
}

struct PendingSubnetOffer: Identifiable, Sendable {
    let offer: SubnetOffer
    let fingerprint: String
    let expiresAt = Date().addingTimeInterval(120)
    var id: String { "subnet-receive-" + fingerprint + "-" + offer.id }
}

enum SubnetValidation {
    static let maximumSubnets = 32
    static let capability = "subnet-sharing-v1"

    static func validate(_ values: [DiscoverySubnet], allowEmpty: Bool = false) throws {
        guard (allowEmpty || !values.isEmpty), values.count <= maximumSubnets else {
            throw BoardcastError.invalid(L10n.string("网段数量无效，最多允许 32 个网段。"))
        }
        var keys = Set<String>()
        for value in values {
            guard let subnet = IPv4Subnet(value.cidr), subnet.cidr == value.cidr,
                  (1024...65535).contains(value.port), keys.insert(value.id).inserted,
                  value.seedAddress.isEmpty || subnet.contains(value.seedAddress) else {
                throw BoardcastError.invalid(L10n.string("网段必须是私有 IPv4 /16 至 /32 网段，且端口有效。"))
            }
        }
    }

    static func learned(address: String, advertised: [DiscoverySubnet], port: Int) -> DiscoverySubnet? {
        guard IPv4Subnet.isPrivateHost(address), (1024...65535).contains(port) else { return nil }
        let matching = advertised.compactMap { value -> IPv4Subnet? in
            guard let subnet = IPv4Subnet(value.cidr), subnet.contains(address) else { return nil }
            return subnet
        }.sorted { $0.prefix > $1.prefix }
        guard let subnet = matching.first ?? IPv4Subnet.fallback(for: address),
              subnet.contains(address) else { return nil }
        return DiscoverySubnet(cidr: subnet.cidr, port: port, seedAddress: address)
    }
}


/// Independent streaming cursor: no host array and no dropped addresses when TLS slots are full.
struct SubnetScanCursor: Sendable {
    private let ranges: [(DiscoverySubnet, IPv4Subnet)]
    private var cursors: [UInt32]
    private var index = 0

    init(_ values: [DiscoverySubnet]) {
        ranges = values.compactMap { value in
            guard let subnet = IPv4Subnet(value.cidr) else { return nil }
            return (value, subnet)
        }
        cursors = ranges.map { $0.1.firstHost }
    }

    mutating func next() -> (address: String, port: Int)? {
        guard !ranges.isEmpty else { return nil }
        for _ in 0..<ranges.count {
            let current = index % ranges.count
            index = (index + 1) % ranges.count
            guard cursors[current] <= ranges[current].1.lastHost else { continue }
            let address = IPv4Subnet.address(cursors[current])
            cursors[current] += 1
            return (address, ranges[current].0.port)
        }
        return nil
    }
}
