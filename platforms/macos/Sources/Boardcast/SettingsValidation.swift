import Foundation

enum SettingsValidation {
    static func validate(_ settings: AppSettings) throws {
        try DeviceGroups.validate(settings)
        try SubnetValidation.validate(settings.discoverySubnets, allowEmpty: true)
        let name = settings.deviceName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty, name.utf16.count <= 256,
              !name.unicodeScalars.contains(where: { CharacterSet.controlCharacters.contains($0) }) else {
            throw BoardcastError.invalid(L10n.string("设备名称不能为空、过长或包含控制字符。"))
        }
        guard settings.port == 45831, settings.maxEntries > 0, settings.maxEntries <= 50_000,
              settings.maxTransferBytes > 0, settings.receiveDirectory.hasPrefix("/") else {
            throw BoardcastError.invalid(L10n.string("接收路径或传输限额无效；本版本端口固定为 45831。"))
        }
        guard settings.devices.count <= 256, settings.rules.count <= 128 else {
            throw BoardcastError.invalid(L10n.string("设备或规则数量超出限额。"))
        }
        var identities = Set<String>()
        for device in settings.devices {
            guard DeviceIdentity.isFingerprint(device.fingerprint),
                  identities.insert(device.fingerprint.lowercased()).inserted else {
                throw BoardcastError.invalid(L10n.string("设备身份重复或指纹无效。"))
            }
        }
        var ruleIDs = Set<String>()
        for rule in settings.rules {
            guard ruleIDs.insert(rule.id).inserted, !rule.name.trimmingCharacters(in: .whitespaces).isEmpty,
                  rule.name.utf8.count <= 512, rule.maxBytes >= 0, rule.folderNamePattern.count <= 256,
                  rule.destination.isEmpty || rule.destination.hasPrefix("/"),
                  rule.deviceFingerprint.isEmpty || DeviceIdentity.isFingerprint(rule.deviceFingerprint) else {
                throw BoardcastError.invalid(L10n.string("规则名称、指纹、大小或保存路径无效。"))
            }
            for ext in rule.extensions {
                let value = ext.trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "."))
                guard !value.isEmpty, value.count <= 80,
                      !value.contains(where: { "/\\:*? \t\n".contains($0) }) else {
                    throw BoardcastError.invalid(L10n.string("规则后缀无效。"))
                }
            }
        }
    }
}

extension SettingsValidation {
    /// Apply only fields edited by this form. Concurrent edits to the same field require reload.
    static func merge(draft: AppSettings, baseline: AppSettings, current: AppSettings) throws -> AppSettings {
        var result = current
        func field<Value: Equatable>(_ keyPath: WritableKeyPath<AppSettings, Value>) throws {
            let proposed = draft[keyPath: keyPath]
            let original = baseline[keyPath: keyPath]
            guard proposed != original else { return }
            let live = current[keyPath: keyPath]
            guard live == original || live == proposed else {
                throw BoardcastError.invalid(L10n.string("这些设置已在其他窗口更改。请重新载入后再保存。"))
            }
            result[keyPath: keyPath] = proposed
        }
        try field(\.deviceName)
        try field(\.groupName)
        try field(\.peerGroupFilter)
        try field(\.visibleGroups)
        try field(\.receiveDirectory)
        try field(\.port)
        try field(\.receivingEnabled)
        try field(\.maxTransferBytes)
        try field(\.maxEntries)
        try field(\.devices)
        try field(\.rules)
        try field(\.discoverySubnets)
        return result
    }
}
