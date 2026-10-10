import XCTest
@testable import Boardcast

final class TransferStoreTests: XCTestCase {
    @MainActor
    func testAccurateReceiptOutcomeCanRefineFallbackFailure() async {
        let store = TransferStore()
        let peer = PeerInfo(fingerprint: String(repeating: "a", count: 64), name: "PC")
        store.enqueue(id: "one", peer: peer, totalBytes: 10)
        store.finish(id: "one", phase: .failed, message: "fallback")
        store.update(TransferProgress(id: "one", direction: .sending, peerFingerprint: peer.fingerprint,
                                      phase: .receiptUnconfirmed, completedBytes: 10, totalBytes: 10))
        XCTAssertEqual(store.tasks.first?.phase, .receiptUnconfirmed)
        store.update(TransferProgress(id: "one", direction: .sending, peerFingerprint: peer.fingerprint,
                                      phase: .transferring, completedBytes: 5, totalBytes: 10))
        XCTAssertEqual(store.tasks.first?.phase, .receiptUnconfirmed)
    }

    @MainActor
    func testCancellationIsNotOverwrittenByLateSendingProgress() async {
        let store = TransferStore()
        let peer = PeerInfo(fingerprint: String(repeating: "a", count: 64), name: "PC")
        store.enqueue(id: "one", peer: peer, totalBytes: 10)
        store.finish(id: "one", phase: .cancelled, message: nil)
        store.update(TransferProgress(id: "one", direction: .sending, peerFingerprint: peer.fingerprint,
                                      phase: .transferring, completedBytes: 5, totalBytes: 10))
        XCTAssertEqual(store.tasks.first?.phase, .cancelled)
    }

    @MainActor
    func testSavedReceiveCompletionSurvivesCancellationRace() async {
        let store = TransferStore()
        let fingerprint = String(repeating: "a", count: 64)
        let id = "receive-" + fingerprint + "-one"
        store.update(TransferProgress(id: id, direction: .receiving, peerFingerprint: fingerprint,
                                      phase: .saving, completedBytes: 10, totalBytes: 10))
        store.finish(id: id, phase: .cancelled, message: nil)
        store.update(TransferProgress(id: id, direction: .receiving, peerFingerprint: fingerprint,
                                      phase: .completed, completedBytes: 10, totalBytes: 10, savedFiles: 1))
        XCTAssertEqual(store.tasks.first?.phase, .completed)
        XCTAssertEqual(store.tasks.first?.savedFiles, 1)
    }

    @MainActor
    func testRestartRestoresActiveHistoryAsInterrupted() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let first = TransferStore()
        first.configure(directory: directory)
        first.enqueue(id: "one", peer: PeerInfo(fingerprint: String(repeating: "a", count: 64), name: "PC"), totalBytes: 10)
        let reopened = TransferStore()
        reopened.configure(directory: directory)
        XCTAssertEqual(reopened.tasks.first?.phase, .interrupted)
        XCTAssertFalse(reopened.tasks.first?.isActive ?? true)
    }
}
