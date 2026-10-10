import XCTest
@testable import Boardcast

final class SettingsStoreTests: XCTestCase {
    func testUnrelatedFormSavePreservesRevokedTrust() throws {
        var original = AppSettings()
        original.devices = [KnownDevice(fingerprint: String(repeating: "a", count: 64), name: "PC", trusted: true, autoAccept: true)]
        var current = original
        current.devices[0].trusted = false
        current.devices[0].autoAccept = false
        var draft = original
        draft.deviceName = "Edited Mac"
        let merged = try SettingsValidation.merge(draft: draft, baseline: original, current: current)
        XCTAssertEqual(merged.deviceName, "Edited Mac")
        XCTAssertFalse(merged.devices[0].trusted)
        XCTAssertFalse(merged.devices[0].autoAccept)
    }

    func testConflictingDeviceChangesRequireReload() {
        var original = AppSettings()
        original.devices = [KnownDevice(fingerprint: String(repeating: "a", count: 64), name: "PC", trusted: true)]
        var current = original
        current.devices[0].trusted = false
        var draft = original
        draft.devices[0].alias = "Office"
        XCTAssertThrowsError(try SettingsValidation.merge(draft: draft, baseline: original, current: current))
    }

    func testQuickPreferenceDoesNotCommitFormDraft() throws {
        let original = AppSettings()
        var draft = original
        draft.deviceName = "Unsaved"
        var current = original
        current.receivingEnabled = false
        XCTAssertEqual(current.deviceName, original.deviceName)
        let merged = try SettingsValidation.merge(draft: draft, baseline: original, current: current)
        XCTAssertFalse(merged.receivingEnabled)
        XCTAssertEqual(merged.deviceName, "Unsaved")
    }

    func testRoundTripUsesInjectedDirectoryAndKeepsIdentity() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = try SettingsStore(directory: directory)
        var settings = AppSettings()
        settings.devices = [KnownDevice(fingerprint: String(repeating: "b", count: 64), name: "PC", alias: "Alias")]
        try store.save(settings)
        XCTAssertEqual(try store.load(), settings)
    }

    func testNameCannotExceedDiscoveryLimit() {
        var settings = AppSettings()
        settings.deviceName = String(repeating: "a", count: 257)
        XCTAssertThrowsError(try SettingsValidation.validate(settings))
        settings.deviceName = String(repeating: "中", count: 256)
        XCTAssertNoThrow(try SettingsValidation.validate(settings))
    }

    func testKnownFingerprintCannotContainTrailingNewline() {
        var settings = AppSettings()
        settings.devices = [KnownDevice(fingerprint: String(repeating: "a", count: 64) + "\n", name: "PC")]
        XCTAssertThrowsError(try SettingsValidation.validate(settings))
    }
}
