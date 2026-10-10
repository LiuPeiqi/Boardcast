import Darwin
import XCTest
@testable import Boardcast

final class SelectionScannerTests: XCTestCase {
    private func fixture() throws -> URL {
        let physicalPath = try FileManager.default.temporaryDirectory.path.withCString { path in
            guard let resolved = realpath(path, nil) else {
                throw NSError(domain: NSPOSIXErrorDomain, code: Int(errno))
            }
            defer { free(resolved) }
            return String(cString: resolved)
        }
        let root = URL(fileURLWithPath: physicalPath, isDirectory: true)
            .appendingPathComponent("BoardcastScanner-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        return root
    }

    func testEmptyDirectoriesAndHiddenFilesAreIncluded() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Photos", isDirectory: true)
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("Empty"), withIntermediateDirectories: true)
        try Data().write(to: folder.appendingPathComponent("zero.txt"))
        try Data([1, 2, 3]).write(to: folder.appendingPathComponent(".hidden"))
        let result = try SelectionScanner.scan(url: folder, maxEntries: 50)
        XCTAssertTrue(result.issues.isEmpty)
        XCTAssertEqual(Set(result.entries.map(\.relativePath)), ["Photos", "Photos/Empty", "Photos/zero.txt", "Photos/.hidden"])
        XCTAssertEqual(result.fileCount, 2)
        XCTAssertEqual(result.directoryCount, 2)
        XCTAssertEqual(result.totalBytes, 3)
        XCTAssertEqual(result.sources.count, 2)
    }

    func testSymbolicLinkIsReportedWithoutFollowingIt() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Selection", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data([5]).write(to: root.appendingPathComponent("outside.txt"))
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("link.txt"), withDestinationURL: root.appendingPathComponent("outside.txt"))
        let result = try SelectionScanner.scan(url: folder, maxEntries: 50)
        XCTAssertEqual(result.issues.map(\.relativePath), ["Selection/link.txt"])
        XCTAssertEqual(result.entries.map(\.relativePath), ["Selection"])
        XCTAssertTrue(result.sources.isEmpty)
    }

    func testSymbolicLinkAncestorIsReported() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Real", isDirectory: true)
        let link = root.appendingPathComponent("Alias", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data([1]).write(to: folder.appendingPathComponent("file.txt"))
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: folder)
        let result = try SelectionScanner.scan(url: link.appendingPathComponent("file.txt"), maxEntries: 50)
        XCTAssertEqual(result.issues.map(\.relativePath), ["file.txt"])
        XCTAssertTrue(result.entries.isEmpty)
    }

    func testPhysicalPrivatePrefixIsPreserved() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("file.txt")
        try Data().write(to: source)
        let result = try SelectionScanner.scan(url: source, maxEntries: 50)
        XCTAssertTrue(result.issues.isEmpty)
        XCTAssertEqual(result.url.path, source.path)
        XCTAssertEqual(result.sources["file.txt"]?.path, source.path)
    }
    func testSelectedSymbolicLinkIsReported() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("source.txt")
        let link = root.appendingPathComponent("link.txt")
        try Data().write(to: source)
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: source)
        let result = try SelectionScanner.scan(url: link, maxEntries: 50)
        XCTAssertEqual(result.issues.map(\.relativePath), ["link.txt"])
        XCTAssertTrue(result.entries.isEmpty)
    }

    func testPortableNamesAndBackslashesAreExplicitIssues() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Selection", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data().write(to: folder.appendingPathComponent("CON.txt"))
        try Data().write(to: folder.appendingPathComponent("literal\\name.txt"))
        let result = try SelectionScanner.scan(url: folder, maxEntries: 50)
        XCTAssertEqual(Set(result.issues.map(\.relativePath)), ["Selection/CON.txt", "Selection/literal\\name.txt"])
        XCTAssertTrue(result.sources.isEmpty)
    }

    func testMissingSourceDoesNotBecomeAnEmptySuccessfulDirectory() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let result = try SelectionScanner.scan(url: root.appendingPathComponent("Missing"), maxEntries: 50)
        XCTAssertEqual(result.issues.map(\.relativePath), ["Missing"])
        XCTAssertTrue(result.entries.isEmpty)
    }

    func testEntryLimitBlocksAnIncompleteRoot() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let folder = root.appendingPathComponent("Selection", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try Data().write(to: folder.appendingPathComponent("one"))
        try Data().write(to: folder.appendingPathComponent("two"))
        let result = try SelectionScanner.scan(url: folder, maxEntries: 2)
        XCTAssertEqual(result.entries.count, 2)
        XCTAssertTrue(result.unresolvedIssues.contains { $0.relativePath == "Selection" })
    }

    func testPackageDirectoryIncludesItsContents() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let app = root.appendingPathComponent("Example.app", isDirectory: true)
        let contents = app.appendingPathComponent("Contents", isDirectory: true)
        try FileManager.default.createDirectory(at: contents, withIntermediateDirectories: true)
        try Data("<plist version=\"1.0\"><dict/></plist>".utf8).write(to: contents.appendingPathComponent("Info.plist"))
        let result = try SelectionScanner.scan(url: app, maxEntries: 50)
        XCTAssertTrue(result.isDirectory)
        XCTAssertTrue(result.isPackage)
        XCTAssertTrue(result.entries.contains { $0.relativePath == "Example.app/Contents/Info.plist" })
    }

    func testSpecialFileIsReportedWithoutOpeningIt() throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let fifo = root.appendingPathComponent("pipe")
        XCTAssertEqual(fifo.path.withCString { mkfifo($0, 0o600) }, 0)
        let result = try SelectionScanner.scan(url: fifo, maxEntries: 50)
        XCTAssertEqual(result.issues.map(\.relativePath), ["pipe"])
        XCTAssertTrue(result.entries.isEmpty)
        XCTAssertTrue(result.sources.isEmpty)
    }

    func testUnreadableFileIsReported() throws {
        guard getuid() != 0 else { throw XCTSkip("Permission checks require a non-root account.") }
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let source = root.appendingPathComponent("private.txt")
        try Data([1]).write(to: source)
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: source.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: source.path) }
        let result = try SelectionScanner.scan(url: source, maxEntries: 50)
        XCTAssertEqual(result.issues.map(\.relativePath), ["private.txt"])
        XCTAssertTrue(result.entries.isEmpty)
    }
    func testCancellationStopsScanning() async throws {
        let root = try fixture()
        defer { try? FileManager.default.removeItem(at: root) }
        let task = Task.detached {
            while !Task.isCancelled { await Task.yield() }
            return try SelectionScanner.scan(url: root, maxEntries: 50)
        }
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("Cancelled scanning should throw.")
        } catch is CancellationError {
            // Expected cooperative cancellation.
        }
    }
}