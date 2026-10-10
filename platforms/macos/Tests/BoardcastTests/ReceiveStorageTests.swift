import XCTest
import CryptoKit
import Darwin
@testable import Boardcast

final class ReceiveStorageTests: XCTestCase {
    private var temporary: URL!
    private let fingerprint = String(repeating: "ab", count: 32)
    override func setUpWithError() throws {
        guard let path = realpath(FileManager.default.temporaryDirectory.path, nil) else {
            throw BoardcastError.invalid("Cannot resolve test directory")
        }
        defer { free(path) }
        temporary = URL(fileURLWithPath: String(cString: path), isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: false)
    }
    override func tearDownWithError() throws { try FileManager.default.removeItem(at: temporary) }

    private func storage(_ entries: [TransferEntry]) throws -> ReceiveStorage {
        try ReceiveStorage(offer: TransferOffer(senderName: "Test", senderFingerprint: fingerprint, entries: entries),
            fingerprint: fingerprint, destination: temporary.appendingPathComponent("received"),
            cacheRoot: temporary.appendingPathComponent("private"))
    }
    private func receive(_ store: ReceiveStorage, entry: TransferEntry, data: Data, validHash: Bool = true) async throws {
        let digest = validHash ? Data(SHA256.hash(data: data)) : Data(repeating: 0, count: 32)
        let stream = data + digest
        var offset = 0
        try await store.receiveFile(entry, read: { count in
            guard count <= stream.count - offset else { throw BoardcastError.invalid("Test EOF") }
            defer { offset += count }
            return stream.subdata(in: offset..<(offset + count))
        }, onBytes: { _ in })
    }

    func testDirectSaveNeverOverwritesAndRecordsPrivateMapping() async throws {
        let entry = TransferEntry(relativePath: "report.txt", size: 3, isDirectory: false)
        let store = try storage([entry])
        let original = store.destination.appendingPathComponent("report.txt")
        try Data("old".utf8).write(to: original)
        try await receive(store, entry: entry, data: Data("new".utf8))
        try store.finish()
        XCTAssertEqual(try String(contentsOf: original), "old")
        XCTAssertEqual(try String(contentsOf: store.destination.appendingPathComponent("report (1).txt")), "new")
        XCTAssertEqual(store.mappings["report.txt"], "report (1).txt")
        XCTAssertEqual(store.savedFiles, 1)
        XCTAssertEqual(Set(try FileManager.default.contentsOfDirectory(atPath: store.destination.path)), ["report.txt", "report (1).txt"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: store.cacheDirectory.appendingPathComponent("receipt.json").path))
        let permissions = try FileManager.default.attributesOfItem(atPath: store.cacheDirectory.path)[.posixPermissions] as? NSNumber
        XCTAssertEqual(permissions?.intValue, 0o700)
    }

    func testFolderConflictUsesOneStableMappingForEveryChild() async throws {
        let directory = TransferEntry(relativePath: "Photos", size: 0, isDirectory: true)
        let a = TransferEntry(relativePath: "Photos/a.jpg", size: 1, isDirectory: false)
        let b = TransferEntry(relativePath: "Photos/b.jpg", size: 1, isDirectory: false)
        let store = try storage([directory, a, b])
        try FileManager.default.createDirectory(at: store.destination.appendingPathComponent("Photos"), withIntermediateDirectories: false)
        try store.receiveDirectory(directory)
        try await receive(store, entry: a, data: Data([1]))
        try await receive(store, entry: b, data: Data([2]))
        XCTAssertEqual(store.mappings["Photos"], "Photos (1)")
        XCTAssertEqual(store.mappings["Photos/a.jpg"], "Photos (1)/a.jpg")
        XCTAssertEqual(store.mappings["Photos/b.jpg"], "Photos (1)/b.jpg")
    }

    func testBadHashIsNotPublished() async throws {
        let entry = TransferEntry(relativePath: "bad.bin", size: 3, isDirectory: false)
        let store = try storage([entry])
        do { try await receive(store, entry: entry, data: Data([1, 2, 3]), validHash: false); XCTFail("Bad hash was accepted") }
        catch { }
        XCTAssertEqual(store.savedFiles, 0)
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.destination.appendingPathComponent("bad.bin").path))
        XCTAssertFalse(try FileManager.default.contentsOfDirectory(atPath: store.cacheDirectory.path).contains { $0.hasSuffix(".part") })
    }

    func testSavedFileSurvivesFailureOfLaterFileAndExplicitCancel() async throws {
        let good = TransferEntry(relativePath: "good.bin", size: 1, isDirectory: false)
        let bad = TransferEntry(relativePath: "bad.bin", size: 1, isDirectory: false)
        let store = try storage([good, bad])
        try await receive(store, entry: good, data: Data([7]))
        do { try await receive(store, entry: bad, data: Data([9]), validHash: false); XCTFail("Bad hash was accepted") }
        catch { }
        store.cancel()
        XCTAssertEqual(try Data(contentsOf: store.destination.appendingPathComponent("good.bin")), Data([7]))
        XCTAssertEqual(store.savedFiles, 1)
    }

    func testInterruptedPrivatePartialIsKeptAndCancelRemovesIt() async throws {
        let entry = TransferEntry(relativePath: "partial.bin", size: 3, isDirectory: false)
        let store = try storage([entry])
        do {
            try await store.receiveFile(entry, read: { _ in throw BoardcastError.invalid("Disconnected") }, onBytes: { _ in })
            XCTFail("Disconnected stream accepted")
        } catch { }
        store.interrupted()
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: store.cacheDirectory.path).contains { $0.hasSuffix(".part") })
        store.cancel()
        XCTAssertFalse(try FileManager.default.contentsOfDirectory(atPath: store.cacheDirectory.path).contains { $0.hasSuffix(".part") })
        XCTAssertFalse(FileManager.default.fileExists(atPath: store.destination.appendingPathComponent("partial.bin").path))
    }

    func testSymbolicLinkRootRejectedAndExistingLinkNeverFollowed() async throws {
        let outside = temporary.appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: false)
        let link = temporary.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
        let entry = TransferEntry(relativePath: "file.txt", size: 1, isDirectory: false)
        let offer = TransferOffer(senderName: "Test", senderFingerprint: fingerprint, entries: [entry])
        XCTAssertThrowsError(try ReceiveStorage(offer: offer, fingerprint: fingerprint, destination: link,
            cacheRoot: temporary.appendingPathComponent("private")))
        let store = try storage([entry])
        let target = outside.appendingPathComponent("target")
        try Data([8]).write(to: target)
        try FileManager.default.createSymbolicLink(at: store.destination.appendingPathComponent("file.txt"), withDestinationURL: target)
        try await receive(store, entry: entry, data: Data([4]))
        XCTAssertEqual(try Data(contentsOf: target), Data([8]))
        XCTAssertEqual(store.mappings["file.txt"], "file (1).txt")
    }

    func testCleanupRemovesOnlyExpiredInactiveRecognizedCache() async throws {
        let entry = TransferEntry(relativePath: "saved.bin", size: 1, isDirectory: false)
        var completed: ReceiveStorage? = try storage([entry])
        try await receive(try XCTUnwrap(completed), entry: entry, data: Data([8]))
        try completed?.finish()
        let completedCache = try XCTUnwrap(completed?.cacheDirectory)
        let savedFile = try XCTUnwrap(completed?.destination.appendingPathComponent("saved.bin"))
        completed = nil
        let privateRoot = temporary.appendingPathComponent("private")
        let unrelated = privateRoot.appendingPathComponent("notes")
        try FileManager.default.createDirectory(at: unrelated, withIntermediateDirectories: false)
        try Data("keep".utf8).write(to: unrelated.appendingPathComponent("user.txt"))
        let foreign = privateRoot.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: foreign, withIntermediateDirectories: false)
        try Data("foreign record".utf8).write(to: foreign.appendingPathComponent("receipt.json"))
        let live = try storage([entry])
        ReceiveStorage.pruneExpiredTasks(at: privateRoot, now: Date().addingTimeInterval(8 * 24 * 60 * 60))
        XCTAssertFalse(FileManager.default.fileExists(atPath: completedCache.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: unrelated.appendingPathComponent("user.txt").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: foreign.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: live.cacheDirectory.path))
        XCTAssertEqual(try Data(contentsOf: savedFile), Data([8]))
    }

    func testCleanupRefusesNestedForeignContentAndHasDeletionLimit() throws {
        let entry = TransferEntry(relativePath: "file.bin", size: 0, isDirectory: false)
        let privateRoot = temporary.appendingPathComponent("private")
        for _ in 0..<34 {
            let completed = try storage([entry])
            try completed.finish()
        }
        var withForeign: ReceiveStorage? = try storage([entry])
        let foreignCache = try XCTUnwrap(withForeign?.cacheDirectory)
        try FileManager.default.createDirectory(at: foreignCache.appendingPathComponent("other"), withIntermediateDirectories: false)
        withForeign = nil
        ReceiveStorage.pruneExpiredTasks(at: privateRoot, now: Date().addingTimeInterval(8 * 24 * 60 * 60), limit: 32)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: privateRoot.path).count, 3)
        XCTAssertTrue(FileManager.default.fileExists(atPath: foreignCache.appendingPathComponent("other").path))
    }

    func testImplicitAncestorsDoNotInflatePersistedManifestMappings() async throws {
        let entry = TransferEntry(relativePath: "Folder/sub/file.bin", size: 1, isDirectory: false)
        let store = try storage([entry])
        try await receive(store, entry: entry, data: Data([5]))
        XCTAssertEqual(store.mappings, ["Folder/sub/file.bin": "Folder/sub/file.bin"])
        XCTAssertEqual(try Data(contentsOf: store.destination.appendingPathComponent("Folder/sub/file.bin")), Data([5]))
    }

    func testCleanupReadsValidReceiptLargerThanFormerFourMiBLimit() throws {
        // A real accepted manifest remains below 3 MiB, while storing both source
        // and mapped paths in its receipt legitimately exceeds the old 4 MiB cap.
        let parent = "Folder/" + String(repeating: "a", count: 220)
        let entries = (0..<5_000).map { index in
            TransferEntry(relativePath: parent + "/" + String(repeating: "b", count: 200) + String(index) + ".txt",
                          size: 0, isDirectory: false)
        }
        let offer = TransferOffer(senderName: "Test", senderFingerprint: fingerprint, entries: entries)
        try ReceivePolicy.validate(offer, settings: AppSettings())
        XCTAssertLessThanOrEqual(try JSONEncoder().encode(offer).count, 3 * 1024 * 1024)
        struct ReceiptFixture: Encodable {
            let transferID: String
            let senderFingerprint: String
            let destination: String
            let savedPaths: [String: String]
            let state: String
            let updated: Date
        }
        let destination = temporary.appendingPathComponent("received")
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: false)
        let savedFile = destination.appendingPathComponent("sentinel")
        try Data([8]).write(to: savedFile)
        let privateRoot = temporary.appendingPathComponent("private")
        let task = privateRoot.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: task, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let receipt = try JSONEncoder().encode(ReceiptFixture(
            transferID: offer.id, senderFingerprint: fingerprint, destination: destination.path,
            savedPaths: Dictionary(uniqueKeysWithValues: entries.map { ($0.relativePath, $0.relativePath) }),
            state: "completed", updated: Date().addingTimeInterval(-8 * 24 * 60 * 60)))
        XCTAssertGreaterThan(receipt.count, 4 * 1024 * 1024)
        XCTAssertLessThanOrEqual(receipt.count, ReceiveStorage.maxReceiptBytes)
        try receipt.write(to: task.appendingPathComponent("receipt.json"))
        ReceiveStorage.pruneExpiredTasks(at: privateRoot)
        XCTAssertFalse(FileManager.default.fileExists(atPath: task.path))
        XCTAssertEqual(try Data(contentsOf: savedFile), Data([8]))
    }

    func testCollisionSuffixStaysWithinFilesystemByteLimit() {
        let name = String(repeating: "a", count: 251) + ".txt"
        let conflict = ReceiveStorage.conflictName(name, number: 123, isDirectory: false)
        XCTAssertLessThanOrEqual(conflict.utf8.count, 255)
        XCTAssertTrue(conflict.hasSuffix(" (123).txt"))
        XCTAssertEqual(ReceiveStorage.conflictName(".env", number: 1, isDirectory: false), ".env (1)")
    }
}
