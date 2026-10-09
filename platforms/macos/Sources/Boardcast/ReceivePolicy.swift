import Foundation

enum ReceivePolicy {
    static func relativePath(_ value: String) throws -> String {
        let path = value.replacingOccurrences(of: "\\", with: "/")
        let components = path.split(separator: "/", omittingEmptySubsequences: false)
        guard !path.isEmpty, path.utf8.count <= 1024, !path.hasPrefix("/"),
              !components.contains(where: { $0.isEmpty || $0 == "." || $0 == ".." }),
              !path.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }),
              !path.contains(":"), !path.contains("\0") else {
            throw BoardcastError.invalid("文件清单含不安全路径")
        }
        let reserved = ["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] }
        for part in components {
            guard part.utf8.count <= 255, !part.hasSuffix(" "), !part.hasSuffix("."),
                  !part.contains(where: { "<>\"|?*".contains($0) }),
                  !reserved.contains(String(part.split(separator: ".")[0]).uppercased()) else {
                throw BoardcastError.invalid("文件名无法安全跨平台保存")
            }
        }
        return path
    }

    static func validate(_ offer: TransferOffer, settings: AppSettings) throws {
        guard !offer.entries.isEmpty, offer.entries.count <= settings.maxEntries,
              offer.id.range(of: "^[A-Za-z0-9-]{1,64}$", options: .regularExpression) != nil,
              offer.senderName.utf8.count <= 512, offer.summary.utf8.count <= 16_384 else {
            throw BoardcastError.invalid("文件清单超出限制")
        }
        var seen = Set<String>()
        var files = Set<String>()
        var all: [String] = []
        var total: Int64 = 0
        for entry in offer.entries {
            let normalized = try relativePath(entry.relativePath).precomposedStringWithCanonicalMapping.lowercased()
            guard seen.insert(normalized).inserted, entry.size >= 0,
                  !entry.isDirectory || entry.size == 0 else { throw BoardcastError.invalid("文件清单有重复项或错误大小") }
            let addition = total.addingReportingOverflow(entry.size)
            guard !addition.overflow, addition.partialValue <= settings.maxTransferBytes else { throw BoardcastError.invalid("传输超过接收限额") }
            total = addition.partialValue
            if !entry.isDirectory { files.insert(normalized) }
            all.append(normalized)
        }
        for path in all {
            var parent = (path as NSString).deletingLastPathComponent
            while !parent.isEmpty {
                guard !files.contains(parent) else { throw BoardcastError.invalid("文件清单存在文件/目录冲突") }
                parent = (parent as NSString).deletingLastPathComponent
            }
        }
    }

    static func evaluate(settings: AppSettings, offer: TransferOffer, fingerprint: String) throws -> ReceiveDecision {
        try validate(offer, settings: settings)
        guard fingerprint.range(of: "^[a-fA-F0-9]{64}$", options: .regularExpression) != nil,
              offer.senderFingerprint.caseInsensitiveCompare(fingerprint) == .orderedSame else {
            throw BoardcastError.invalid("TLS 身份与发送清单不一致")
        }
        let rule = settings.rules.first { $0.enabled && matches($0, offer: offer, fingerprint: fingerprint) }
        let destination = rule?.destination.isEmpty == false ? rule!.destination : settings.receiveDirectory
        guard destination.hasPrefix("/") else { throw BoardcastError.invalid("接收路径必须为绝对路径") }
        let devices = settings.devices.filter { $0.fingerprint.caseInsensitiveCompare(fingerprint) == .orderedSame }
        guard settings.receivingEnabled else { return .init(autoAccept: false, destination: destination, reason: "接收已暂停") }
        guard devices.count == 1, devices[0].trusted else { return .init(autoAccept: false, destination: destination, reason: "新设备，需要核对指纹并确认") }
        guard devices[0].autoAccept, let rule, rule.autoAccept else { return .init(autoAccept: false, destination: destination, reason: "需要手动确认") }
        return .init(autoAccept: true, destination: destination, reason: "匹配规则：\(rule.name)")
    }

    static func matches(_ rule: ReceiveRule, offer: TransferOffer, fingerprint: String) -> Bool {
        if !rule.deviceFingerprint.isEmpty && rule.deviceFingerprint.caseInsensitiveCompare(fingerprint) != .orderedSame { return false }
        if rule.maxBytes < 0 || (rule.maxBytes > 0 && offer.totalBytes > rule.maxBytes) { return false }
        var roots: [String: Bool] = [:]
        for entry in offer.entries {
            let parts = entry.relativePath.replacingOccurrences(of: "\\", with: "/").split(separator: "/")
            guard let first = parts.first else { return false }
            let key = String(first).lowercased()
            roots[key] = (roots[key] ?? false) || entry.isDirectory || parts.count > 1
        }
        if rule.scope == .files && roots.values.contains(true) { return false }
        if rule.scope == .folders && roots.values.contains(false) { return false }
        if !rule.folderNamePattern.isEmpty {
            guard rule.folderNamePattern.count <= 256, !roots.values.contains(false) else { return false }
            if !roots.keys.allSatisfy({ wildcard(rule.folderNamePattern.lowercased(), $0) }) { return false }
        }
        if !rule.extensions.isEmpty {
            let allowed = Set(rule.extensions.map { $0.trimmingCharacters(in: .whitespaces).lowercased().trimmingCharacters(in: CharacterSet(charactersIn: ".")) })
            guard !allowed.contains(""), !allowed.contains(where: { $0.contains(where: { "/\\:*? \t\n".contains($0) }) }) else { return false }
            if offer.entries.contains(where: { !$0.isDirectory && !allowed.contains(($0.relativePath as NSString).pathExtension.lowercased()) }) { return false }
        }
        return true
    }

    // Linear-space dynamic programming avoids regex backtracking on network input.
    static func wildcard(_ pattern: String, _ text: String) -> Bool {
        let p = Array(pattern), t = Array(text)
        var previous = [Bool](repeating: false, count: t.count + 1)
        previous[0] = true
        for c in p {
            var next = [Bool](repeating: false, count: t.count + 1)
            next[0] = c == "*" && previous[0]
            for j in 1..<(t.count + 1) {
                next[j] = c == "*" ? (previous[j] || next[j - 1]) : ((c == "?" || c == t[j - 1]) && previous[j - 1])
            }
            previous = next
        }
        return previous[t.count]
    }

    static func safeDestination(root: URL, relative: String) throws -> URL {
        let normalized = try relativePath(relative)
        let base = root.standardizedFileURL
        let result = base.appendingPathComponent(normalized).standardizedFileURL
        guard result.path.hasPrefix(base.path + "/") else { throw BoardcastError.invalid("接收路径越界") }

        // Refuse symlinks in every existing ancestor, including the selected root.

        var ancestor = URL(fileURLWithPath: "/", isDirectory: true)
        for component in result.pathComponents.dropFirst() {
            ancestor.appendPathComponent(component)
            let attributes = try? FileManager.default.attributesOfItem(atPath: ancestor.path)
            if attributes?[.type] as? FileAttributeType == .typeSymbolicLink { throw BoardcastError.invalid("接收路径含符号链接") }
        }

        return result
    }
}
