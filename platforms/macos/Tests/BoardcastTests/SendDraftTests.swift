import Darwin
import XCTest
@testable import Boardcast

final class SendDraftTests: XCTestCase {
    private func fixture() throws -> URL {
        let physicalPath = try FileManager.default.temporaryDirectory.path.withCString { path in
            guard let resolved = realpath(path, nil) else {
                throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
            }
            defer { free(resolved) }
            return String(cString: resolved)
        }
        let root = URL(fileURLWithPath: physicalPath, isDirectory: true)
            .appendingPathComponent("BoardcastDraft-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    @MainActor
    func testExcludingDirectoryRemovesItsSubtreeFromStatisticsAndOffer() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Photos", isDirectory: true)
        let child = folder.appendingPathComponent("Private", isDirectory: true)
        try FileManager.default.createDirectory(at: child, withIntermediateDirectories: true)
        try Data([1, 2, 3]).write(to: child.appendingPathComponent("private.jpg"))
        try Data([4, 5]).write(to: folder.appendingPathComponent("public.jpg"))
        let draft = SendDraft()
        await draft.add(urls: [folder], settings: AppSettings())
        let item = try XCTUnwrap(draft.items.first)
        XCTAssertEqual(item.fileCount, 2)
        XCTAssertEqual(item.directoryCount, 2)
        draft.exclude(itemID: item.id, paths: ["Photos/Private"])
        let prepared = try draft.prepare(settings: AppSettings())
        XCTAssertEqual(Set(prepared.entries.map(\.relativePath)), ["Photos", "Photos/public.jpg"])
        XCTAssertEqual(Set(prepared.sources.keys), ["Photos/public.jpg"])
        XCTAssertEqual(draft.items[0].fileCount, 1)
        XCTAssertEqual(draft.items[0].directoryCount, 1)
        XCTAssertEqual(draft.items[0].totalBytes, 2)
        draft.restoreExclusions(itemID: item.id)
        XCTAssertEqual(draft.items[0].fileCount, 2)
    }

    @MainActor
    func testUnsupportedSubitemMustBeExplicitlyExcluded() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Selection", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data().write(to: folder.appendingPathComponent("CON.txt"))
        try Data([1]).write(to: folder.appendingPathComponent("safe.txt"))
        let draft = SendDraft()
        await draft.add(urls: [folder], settings: AppSettings())
        XCTAssertThrowsError(try draft.prepare(settings: AppSettings()))
        let item = try XCTUnwrap(draft.items.first)
        XCTAssertEqual(item.unresolvedIssues.count, 1)
        draft.exclude(itemID: item.id, paths: ["Selection/CON.txt"])
        XCTAssertTrue(draft.items[0].unresolvedIssues.isEmpty)
        XCTAssertEqual(try draft.prepare(settings: AppSettings()).entries.count, 2)
        draft.restoreExclusions(itemID: item.id)
        XCTAssertThrowsError(try draft.prepare(settings: AppSettings()))
    }

    @MainActor
    func testUnreadableSubtreeRequiresExclusionAndPreservesReadableSibling() async throws {
        guard getuid() != 0 else { throw XCTSkip("Permission checks require a non-root account.") }
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Selection", isDirectory: true)
        let unreadable = folder.appendingPathComponent("Private", isDirectory: true)
        try FileManager.default.createDirectory(at: unreadable, withIntermediateDirectories: true)
        try Data([1]).write(to: unreadable.appendingPathComponent("private.txt"))
        try Data([2]).write(to: folder.appendingPathComponent("safe.txt"))
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: unreadable.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: unreadable.path) }
        let draft = SendDraft()
        await draft.add(urls: [folder], settings: AppSettings())
        XCTAssertThrowsError(try draft.prepare(settings: AppSettings()))
        let item = try XCTUnwrap(draft.items.first)
        XCTAssertTrue(item.unresolvedIssues.contains { $0.relativePath == "Selection/Private" })
        draft.exclude(itemID: item.id, paths: ["Selection/Private"])
        let prepared = try draft.prepare(settings: AppSettings())
        XCTAssertEqual(Set(prepared.entries.map(\.relativePath)), ["Selection", "Selection/safe.txt"])
        XCTAssertEqual(Set(prepared.sources.keys), ["Selection/safe.txt"])
    }
    @MainActor
    func testDuplicateAndParentChildSourcesAreNotAddedTwice() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Selection", isDirectory: true)
        let file = folder.appendingPathComponent("file.txt")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data([1]).write(to: file)
        let draft = SendDraft()
        await draft.add(urls: [folder, folder, file], settings: AppSettings())
        XCTAssertEqual(draft.items.count, 1)
        XCTAssertNotNil(draft.error)
        XCTAssertEqual(try draft.prepare(settings: AppSettings()).entries.count, 2)
        await draft.add(urls: [root], settings: AppSettings())
        XCTAssertEqual(draft.items.count, 1)
        XCTAssertNotNil(draft.error)
    }

    @MainActor
    func testPortableRootCollisionsAreRejectedBySharedPolicy() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let first = root.appendingPathComponent("First", isDirectory: true)
        let second = root.appendingPathComponent("Second", isDirectory: true)
        try FileManager.default.createDirectory(at: first, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: second, withIntermediateDirectories: true)
        let a = first.appendingPathComponent("Photo.jpg")
        let b = second.appendingPathComponent("photo.jpg")
        try Data([1]).write(to: a)
        try Data([2]).write(to: b)
        let draft = SendDraft()
        await draft.add(urls: [a, b], settings: AppSettings())
        XCTAssertEqual(draft.items.count, 2)
        XCTAssertThrowsError(try draft.prepare(settings: AppSettings()))
    }

    @MainActor
    func testCapacityLimitUsesExcludedContentAndCurrentSettings() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Selection", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data([1, 2, 3]).write(to: folder.appendingPathComponent("large.txt"))
        let draft = SendDraft()
        await draft.add(urls: [folder], settings: AppSettings())
        var settings = AppSettings()
        settings.maxTransferBytes = 2
        XCTAssertThrowsError(try draft.prepare(settings: settings))
        let item = try XCTUnwrap(draft.items.first)
        draft.exclude(itemID: item.id, paths: ["Selection/large.txt"])
        XCTAssertEqual(try draft.prepare(settings: settings).entries.count, 1)
        XCTAssertEqual(draft.items[0].totalBytes, 0)
    }

    @MainActor
    func testPreparedSnapshotDoesNotChangeWhenDraftChanges() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("file.txt")
        try Data([1, 2]).write(to: source)
        let draft = SendDraft()
        await draft.add(urls: [source], settings: AppSettings())
        let prepared = try draft.prepare(settings: AppSettings())
        let item = try XCTUnwrap(draft.items.first)
        draft.exclude(itemID: item.id, paths: ["file.txt"])
        XCTAssertThrowsError(try draft.prepare(settings: AppSettings()))
        XCTAssertEqual(prepared.entries.map(\.relativePath), ["file.txt"])
        XCTAssertEqual(prepared.sources["file.txt"], source)
        draft.clear()
        XCTAssertTrue(draft.items.isEmpty)
        XCTAssertEqual(prepared.entries.count, 1)
    }

    @MainActor
    func testDescriptionLimitCountsUTF8Bytes() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("file.txt")
        try Data().write(to: source)
        let draft = SendDraft()
        await draft.add(urls: [source], settings: AppSettings())
        draft.summary = String(repeating: "文", count: 6_000)
        XCTAssertThrowsError(try draft.prepare(settings: AppSettings()))
        draft.summary = "说明"
        XCTAssertNoThrow(try draft.prepare(settings: AppSettings()))
    }

    @MainActor
    func testClearCancelsScanAndCannotRepopulateTheDraft() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("file.txt")
        try Data().write(to: source)
        let draft = SendDraft()
        var began = false
        let operation = Task {
            began = true
            await draft.add(urls: [source], settings: AppSettings())
        }
        while !began { await Task.yield() }
        draft.clear()
        await operation.value
        XCTAssertTrue(draft.items.isEmpty)
        XCTAssertFalse(draft.isScanning)
    }
}