import Foundation

enum PeerGroupFilter: String, Codable, CaseIterable {
    case all, sameGroup, selectedGroups

    var title: String {
        switch self {
        case .all: return "全部设备"
        case .sameGroup: return "与我同组"
        case .selectedGroups: return "选择分组"
        }
    }
}

/// Groups affect presentation only. They never grant trust or change receive policy.
enum DeviceGroups {
    static let maximumGroupNameLength = 80
    static let maximumVisibleGroupNameLength = 256
    static let maximumVisibleGroups = 128
    static let ungrouped = "未分组"

    static func isValid(_ name: String, maximum: Int = maximumGroupNameLength) -> Bool {
        // Match .NET char.IsControl (Unicode Cc), without rejecting format characters.
        name.utf16.count <= maximum && !name.unicodeScalars.contains {
            $0.value < 32 || (127...159).contains($0.value)
        }
    }

    static func normalized(_ name: String) -> String {
        name.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func automatic(_ deviceName: String) -> String {
        let trimmed = deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        let first = trimmed.split(whereSeparator: { $0 == "-" || $0 == "_" || $0 == "." || $0.isWhitespace }).first
        return first.map(String.init) ?? ungrouped
    }

    static func effective(deviceName: String, groupName: String) -> String {
        let custom = normalized(groupName)
        return custom.isEmpty ? automatic(deviceName) : custom
    }

    static func local(_ settings: AppSettings) -> String {
        effective(deviceName: settings.deviceName, groupName: settings.groupName)
    }

    static func peer(_ peer: PeerInfo, settings: AppSettings) -> String {
        if let known = settings.devices.first(where: { $0.fingerprint.caseInsensitiveCompare(peer.fingerprint) == .orderedSame }) {
            let personal = normalized(known.groupName)
            if !personal.isEmpty { return personal }
        }
        return effective(deviceName: peer.name, groupName: peer.groupName)
    }

    static func same(_ lhs: String, _ rhs: String) -> Bool {
        normalized(lhs).caseInsensitiveCompare(normalized(rhs)) == .orderedSame
    }

    static func isVisible(_ peer: PeerInfo, settings: AppSettings) -> Bool {
        let group = self.peer(peer, settings: settings)
        switch settings.peerGroupFilter {
        case .all: return true
        case .sameGroup: return same(group, local(settings))
        case .selectedGroups: return settings.visibleGroups.contains { same($0, group) }
        }
    }

    static func available(peers: [PeerInfo], settings: AppSettings) -> [String] {
        let groups = [local(settings)] + peers.map { peer($0, settings: settings) }
            + settings.devices.map { effective(deviceName: $0.name, groupName: $0.groupName) }
            + settings.visibleGroups
        var result: [String] = []
        for group in groups {
            let name = normalized(group)
            if !result.contains(where: { same($0, name) }) { result.append(name) }
        }
        return result.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
    }

    static func label(_ group: String) -> String { group.isEmpty ? ungrouped : group }

    static func validate(_ settings: AppSettings) throws {
        guard isValid(settings.groupName), settings.devices.allSatisfy({ isValid($0.groupName) }) else {
            throw BoardcastError.invalid("分组名称最多 80 个字符，不能包含控制字符。")
        }
        guard settings.visibleGroups.count <= maximumVisibleGroups,
              settings.visibleGroups.allSatisfy({
                  isValid($0, maximum: maximumVisibleGroupNameLength) && !normalized($0).isEmpty
              }) else {
            throw BoardcastError.invalid("最多选择 128 个分组，每个名称需要 1–256 个字符，不能包含控制字符。")
        }
    }
}
