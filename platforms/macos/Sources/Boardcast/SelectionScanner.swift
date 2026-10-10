import Foundation

/// A snapshot shared by every recipient of a send operation.
struct PreparedTransfer: Sendable {
    let entries: [TransferEntry]
    let sources: [String: URL]
}

struct SelectionIssue: Identifiable, Sendable {
    let relativePath: String
    let reason: String
    var id: String { relativePath + "|" + reason }
}

struct DraftItem: Identifiable, Sendable {
    let id: UUID
    let url: URL
    let isDirectory: Bool
    let isPackage: Bool
    let entries: [TransferEntry]
    let sources: [String: URL]
    let issues: [SelectionIssue]
    var excluded: Set<String> = []

    func isExcluded(_ relativePath: String) -> Bool {
        var candidate = relativePath
        while true {
            if excluded.contains(candidate) { return true }
            guard let separator = candidate.lastIndex(of: "/") else { return false }
            candidate = String(candidate[..<separator])
        }
    }
    var includedEntries: [TransferEntry] { entries.filter { !isExcluded($0.relativePath) } }
    var unresolvedIssues: [SelectionIssue] { issues.filter { !isExcluded($0.relativePath) } }
    var fileCount: Int { includedEntries.filter { !$0.isDirectory }.count }
    var directoryCount: Int { includedEntries.filter(\.isDirectory).count }
    var totalBytes: Int64 {
        includedEntries.reduce(0) { total, entry in
            let result = total.addingReportingOverflow(entry.size)
            return result.overflow ? Int64.max : result.partialValue
        }
    }
}

enum SelectionScanner {
    /// An incomplete directory is recorded as an issue at its root, so a partial
    /// scan cannot be sent. The scan includes hidden files and package contents.
    static func scan(url: URL, maxEntries: Int) throws -> DraftItem {
        try Task.checkCancellation()
        guard maxEntries > 0 else { throw BoardcastError.invalid(L10n.string("The entry limit must be greater than zero.")) }
        let root = url.standardized
        let rootName = root.lastPathComponent
        var entries: [TransferEntry] = []
        var sources: [String: URL] = [:]
        var issues: [SelectionIssue] = []
        var directory = false
        var package = false
        var total: Int64 = 0
        var visited = 0
        let manager = FileManager()
        let keys: Set<URLResourceKey> = [.isSymbolicLinkKey, .isDirectoryKey, .isRegularFileKey,
                                       .fileSizeKey, .isPackageKey, .isReadableKey, .isVolumeKey,
                                       .isMountTriggerKey]

        func issue(_ path: String, _ reason: String) {
            issues.append(SelectionIssue(relativePath: path, reason: reason))
        }
        func append(_ file: URL, relative: String, isRoot: Bool) throws -> Bool {
            try Task.checkCancellation()
            visited += 1
            guard visited <= maxEntries else {
                throw BoardcastError.invalid(L10n.string("The selection exceeds the entry limit; its scan is incomplete."))
            }
            do {
                let values = try file.resourceValues(forKeys: keys)
                if isRoot {
                    directory = values.isDirectory == true
                    package = values.isPackage == true
                }
                guard values.isSymbolicLink != true else {
                    issue(relative, L10n.string("Symbolic links cannot be transferred. Exclude this item to continue."))
                    return false
                }
                guard values.isDirectory == true || values.isRegularFile == true else {
                    issue(relative, L10n.string("Special files cannot be transferred. Exclude this item to continue."))
                    return false
                }
                // Foundation's deep enumerator does not traverse nested volumes.
                // Explicit refusal prevents an offer from silently omitting them.
                if !isRoot && (values.isVolume == true || values.isMountTrigger == true) {
                    issue(relative, L10n.string("A mounted volume must be added separately. Exclude this directory to continue."))
                    return false
                }
                guard !relative.contains("\\") else {
                    issue(relative, L10n.string("A backslash in a file name cannot be preserved across platforms."))
                    return false
                }
                let normalized = try ReceivePolicy.relativePath(relative)
                guard values.isReadable != false, manager.isReadableFile(atPath: file.path) else {
                    issue(relative, L10n.string("The item cannot be read. Exclude it or change its permissions."))
                    return false
                }
                let isDirectory = values.isDirectory == true
                let size: Int64
                if isDirectory {
                    size = 0
                } else {
                    guard let fileSize = values.fileSize, fileSize >= 0 else {
                        issue(relative, L10n.string("The file size could not be read."))
                        return false
                    }
                    size = Int64(fileSize)
                }
                let addition = total.addingReportingOverflow(size)
                guard !addition.overflow else {
                    issue(relative, L10n.string("The total file size exceeds the supported range."))
                    return false
                }
                total = addition.partialValue
                entries.append(TransferEntry(relativePath: normalized, size: size, isDirectory: isDirectory))
                if !isDirectory { sources[normalized] = file }
                return isDirectory
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                issue(relative, error.localizedDescription)
                return false
            }
        }

        do {
            guard root.isFileURL else { throw BoardcastError.invalid(L10n.string("Only local files can be added.")) }
            // Do not resolve a selected link into an unrelated source directory.
            var ancestor = root
            while ancestor.path != "/" {
                try Task.checkCancellation()
                let attributes = try manager.attributesOfItem(atPath: ancestor.path)
                guard attributes[.type] as? FileAttributeType != .typeSymbolicLink else {
                    throw BoardcastError.invalid(L10n.string("The source path contains a symbolic link."))
                }
                ancestor.deleteLastPathComponent()
            }
            if try append(root, relative: rootName, isRoot: true) {
                var enumerationFailures: [(URL, Error)] = []
                guard let enumerator = manager.enumerator(at: root, includingPropertiesForKeys: Array(keys), options: [], errorHandler: { location, error in
                    enumerationFailures.append((location, error))
                    return enumerationFailures.count <= maxEntries
                }) else { throw BoardcastError.invalid(L10n.string("The directory could not be read.")) }
                while let file = enumerator.nextObject() as? URL {
                    try Task.checkCancellation()
                    guard file.path.hasPrefix(root.path + "/") else {
                        throw BoardcastError.invalid(L10n.string("The scanned source escaped the selected directory."))
                    }
                    let relative = rootName + "/" + String(file.path.dropFirst(root.path.count + 1))
                    if try append(file, relative: relative, isRoot: false) == false { enumerator.skipDescendants() }
                }
                guard enumerationFailures.count <= maxEntries else {
                    throw BoardcastError.invalid(L10n.string("The directory has too many unreadable items; its scan is incomplete."))
                }
                for (location, failure) in enumerationFailures {
                    let relative = location.path.hasPrefix(root.path + "/")
                        ? rootName + "/" + String(location.path.dropFirst(root.path.count + 1))
                        : rootName
                    issue(relative, L10n.format("The item could not be read: %@", failure.localizedDescription))
                }
            }
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            issue(rootName, error.localizedDescription)
        }
        return DraftItem(id: UUID(), url: root, isDirectory: directory, isPackage: package,
                         entries: entries, sources: sources, issues: issues)
    }
}