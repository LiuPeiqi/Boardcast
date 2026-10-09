import Foundation

struct AppSettings: Codable {
    var version = 1
    var deviceName = Host.current().localizedName ?? "Mac"
    var groupName = ""
    var peerGroupFilter: PeerGroupFilter = .all
    var visibleGroups: [String] = []
    var receiveDirectory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Downloads/Boardcast").path
    var port = 45831
    var receivingEnabled = true
    var maxTransferBytes: Int64 = 100 * 1024 * 1024 * 1024
    var maxEntries = 50_000
    var devices: [KnownDevice] = []
    var rules: [ReceiveRule] = []
}

struct KnownDevice: Codable, Identifiable {
    var fingerprint: String
    var name: String
    var alias = ""
    var groupName = ""
    var trusted = false
    var autoAccept = false
    var lastAddress = ""
    var port = 45831
    var id: String { fingerprint }
    var displayName: String { alias.trimmingCharacters(in: .whitespaces).isEmpty ? name : alias }
}

struct PeerInfo: Codable, Identifiable {
    var fingerprint: String
    var name: String
    var groupName = ""
    var platform = "macOS"
    var address = ""
    var port = 45831
    var receivingEnabled = true
    var id: String { fingerprint }
}

// Explicit defaults preserve settings and wire compatibility with earlier releases.
// Keeping decoding in extensions preserves the memberwise initializers used by callers.
extension AppSettings {
    private enum CodingKeys: String, CodingKey {
        case version, deviceName, groupName, peerGroupFilter, visibleGroups, receiveDirectory
        case port, receivingEnabled, maxTransferBytes, maxEntries, devices, rules
    }
    init(from decoder: Decoder) throws {
        self.init()
        let values = try decoder.container(keyedBy: CodingKeys.self)
        version = try values.decodeIfPresent(Int.self, forKey: .version) ?? version
        deviceName = try values.decodeIfPresent(String.self, forKey: .deviceName) ?? deviceName
        if values.contains(.groupName) { groupName = try values.decode(String.self, forKey: .groupName) }
        if values.contains(.peerGroupFilter) { peerGroupFilter = try values.decode(PeerGroupFilter.self, forKey: .peerGroupFilter) }
        if values.contains(.visibleGroups) { visibleGroups = try values.decode([String].self, forKey: .visibleGroups) }
        receiveDirectory = try values.decodeIfPresent(String.self, forKey: .receiveDirectory) ?? receiveDirectory
        port = try values.decodeIfPresent(Int.self, forKey: .port) ?? port
        receivingEnabled = try values.decodeIfPresent(Bool.self, forKey: .receivingEnabled) ?? receivingEnabled
        maxTransferBytes = try values.decodeIfPresent(Int64.self, forKey: .maxTransferBytes) ?? maxTransferBytes
        maxEntries = try values.decodeIfPresent(Int.self, forKey: .maxEntries) ?? maxEntries
        devices = try values.decodeIfPresent([KnownDevice].self, forKey: .devices) ?? []
        rules = try values.decodeIfPresent([ReceiveRule].self, forKey: .rules) ?? []
    }
}

extension KnownDevice {
    private enum CodingKeys: String, CodingKey {
        case fingerprint, name, alias, groupName, trusted, autoAccept, lastAddress, port
    }
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(fingerprint: try values.decode(String.self, forKey: .fingerprint),
                  name: try values.decode(String.self, forKey: .name))
        alias = try values.decodeIfPresent(String.self, forKey: .alias) ?? ""
        if values.contains(.groupName) { groupName = try values.decode(String.self, forKey: .groupName) }
        trusted = try values.decodeIfPresent(Bool.self, forKey: .trusted) ?? false
        autoAccept = try values.decodeIfPresent(Bool.self, forKey: .autoAccept) ?? false
        lastAddress = try values.decodeIfPresent(String.self, forKey: .lastAddress) ?? ""
        port = try values.decodeIfPresent(Int.self, forKey: .port) ?? port
    }
}

extension PeerInfo {
    private enum CodingKeys: String, CodingKey {
        case fingerprint, name, groupName, platform, address, port, receivingEnabled
    }
    init(from decoder: Decoder) throws {
        let values = try decoder.container(keyedBy: CodingKeys.self)
        self.init(fingerprint: try values.decode(String.self, forKey: .fingerprint),
                  name: try values.decode(String.self, forKey: .name))
        if values.contains(.groupName) { groupName = try values.decode(String.self, forKey: .groupName) }
        platform = try values.decodeIfPresent(String.self, forKey: .platform) ?? platform
        address = try values.decodeIfPresent(String.self, forKey: .address) ?? ""
        port = try values.decodeIfPresent(Int.self, forKey: .port) ?? port
        receivingEnabled = try values.decodeIfPresent(Bool.self, forKey: .receivingEnabled) ?? true
    }
}

struct TransferEntry: Codable {
    var relativePath: String
    var size: Int64
    var isDirectory: Bool
}

struct TransferOffer: Codable, Identifiable {
    var id = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    var senderName: String
    var senderFingerprint: String
    var summary = ""
    var entries: [TransferEntry]
    var totalBytes: Int64 { entries.reduce(0) { $0 + $1.size } }
}

enum EntryScope: String, Codable, CaseIterable { case any, files, folders }
struct ReceiveRule: Codable, Identifiable {
    var id = UUID().uuidString
    var name = "新规则"
    var enabled = true
    var deviceFingerprint = ""
    var scope: EntryScope = .any
    var extensions: [String] = []
    var folderNamePattern = ""
    var maxBytes: Int64 = 0
    var destination = ""
    var autoAccept = false
}
struct ReceiveDecision {
    var autoAccept: Bool
    var destination: String
    var reason: String
}
struct PendingOffer: Identifiable {
    let offer: TransferOffer
    let fingerprint: String
    let decision: ReceiveDecision
    var id: String { offer.id }
}
enum BoardcastError: LocalizedError {
    case invalid(String)
    var errorDescription: String? {
        switch self { case .invalid(let reason): return reason }
    }
}

final class SettingsStore {
    private let file: URL
    init() throws {
        let directory = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true).appendingPathComponent("Boardcast", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        file = directory.appendingPathComponent("settings.json")
    }
    func load() throws -> AppSettings {
        guard FileManager.default.fileExists(atPath: file.path) else { return AppSettings() }
        let settings = try JSONDecoder().decode(AppSettings.self, from: Data(contentsOf: file))
        try DeviceGroups.validate(settings)
        return settings
    }
    func save(_ settings: AppSettings) throws {
        try DeviceGroups.validate(settings)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(settings).write(to: file, options: [.atomic])
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
}
