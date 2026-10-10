import Combine
import Foundation

@MainActor
final class SendDraft: ObservableObject {
    @Published private(set) var items: [DraftItem] = []
    @Published var summary = ""
    @Published private(set) var isScanning = false
    @Published private(set) var error: String?

    private var scanTask: Task<[DraftItem], Error>?
    private var scanID: UUID?

    deinit { scanTask?.cancel() }

    func add(urls: [URL], settings: AppSettings) async {
        guard !isScanning else {
            error = L10n.string("Wait for the current scan, or cancel it before adding more files.")
            return
        }
        guard !urls.isEmpty else { return }
        var accepted: [URL] = []
        var rejected: [String] = []
        var paths = items.map { sourcePath($0.url) }
        for url in urls {
            let path = sourcePath(url)
            if paths.contains(where: { overlap($0, path) }) {
                rejected.append(url.path)
            } else {
                accepted.append(url.standardized)
                paths.append(path)
            }
        }
        let duplicateMessage = rejected.isEmpty ? nil : L10n.format("Duplicate or overlapping sources were not added: %@", rejected.joined(separator: "\n"))
        error = duplicateMessage
        guard !accepted.isEmpty else { return }
        let id = UUID()
        scanID = id
        isScanning = true
        let limit = settings.maxEntries
        let selections = accepted
        let task = Task.detached(priority: .utility) {
            try selections.map { try SelectionScanner.scan(url: $0, maxEntries: limit) }
        }
        scanTask = task
        do {
            let scanned = try await withTaskCancellationHandler(operation: {
                try await task.value
            }, onCancel: { task.cancel() })
            try Task.checkCancellation()
            guard scanID == id else { return }
            items.append(contentsOf: scanned)
            let issueCount = scanned.reduce(0) { $0 + $1.unresolvedIssues.count }
            if issueCount > 0 {
                let scanMessage = L10n.string("Some content could not be included. Review the issues and exclude affected items before sending.")
                error = [duplicateMessage, scanMessage].compactMap { $0 }.joined(separator: "\n")
            }
        } catch is CancellationError {
            if scanID == id { error = L10n.string("Content scanning was cancelled.") }
        } catch {
            if scanID == id { self.error = error.localizedDescription }
        }
        guard scanID == id else { return }
        scanTask = nil
        scanID = nil
        isScanning = false
    }

    func cancelScanning() {
        scanTask?.cancel()
        scanTask = nil
        scanID = nil
        isScanning = false
    }

    func remove(ids: Set<UUID>) {
        cancelScanning()
        items.removeAll { ids.contains($0.id) }
        error = nil
    }

    func clear() {
        cancelScanning()
        items.removeAll()
        summary = ""
        error = nil
    }

    func exclude(itemID: UUID, paths: Set<String>) {
        guard let index = items.firstIndex(where: { $0.id == itemID }) else { return }
        let item = items[index]
        let knownPaths = Set(item.entries.map(\.relativePath) + item.issues.map(\.relativePath) + [item.url.lastPathComponent])
        items[index].excluded.formUnion(paths.intersection(knownPaths))
        error = nil
    }

    func restoreExclusions(itemID: UUID) {
        guard let index = items.firstIndex(where: { $0.id == itemID }) else { return }
        items[index].excluded.removeAll()
        error = nil
    }

    func prepare(settings: AppSettings) throws -> PreparedTransfer {
        guard !isScanning else { throw BoardcastError.invalid(L10n.string("Content is still being scanned.")) }
        guard summary.utf8.count <= 16_384 else { throw BoardcastError.invalid(L10n.string("The send description is too long.")) }
        var entries: [TransferEntry] = []
        var sources: [String: URL] = [:]
        for item in items {
            if let issue = item.unresolvedIssues.first {
                throw BoardcastError.invalid(L10n.format("Review or exclude %@: %@", issue.relativePath, issue.reason))
            }
            let included = item.includedEntries
            entries.append(contentsOf: included)
            for entry in included where !entry.isDirectory {
                guard let source = item.sources[entry.relativePath] else {
                    throw BoardcastError.invalid(L10n.string("The local source list is incomplete."))
                }
                sources[entry.relativePath] = source
            }
        }
        // Reuse the wire policy for limits, byte overflow, NFC/case aliases,
        // portable names, and file/directory conflicts.
        let offer = TransferOffer(senderName: settings.deviceName, senderFingerprint: "", summary: summary, entries: entries)
        try ReceivePolicy.validate(offer, settings: settings)
        return PreparedTransfer(entries: entries, sources: sources)
    }

    private func sourcePath(_ url: URL) -> String {
        url.standardized.path.precomposedStringWithCanonicalMapping.lowercased()
    }
    private func overlap(_ lhs: String, _ rhs: String) -> Bool {
        lhs == rhs || lhs.hasPrefix(rhs + "/") || rhs.hasPrefix(lhs + "/")
    }
}