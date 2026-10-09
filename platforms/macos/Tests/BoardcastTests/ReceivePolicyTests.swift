import XCTest
@testable import Boardcast

final class ReceivePolicyTests: XCTestCase {
    private let fingerprint = String(repeating: "ab", count: 32)
    private func offer(_ paths: [String] = ["photo.jpg"]) -> TransferOffer {
        TransferOffer(senderName: "Test device", senderFingerprint: fingerprint, entries: paths.map {
            TransferEntry(relativePath: $0, size: 8, isDirectory: false)
        })
    }
    private func automaticSettings() -> AppSettings {
        var settings = AppSettings()
        settings.devices = [KnownDevice(fingerprint: fingerprint, name: "Test", trusted: true, autoAccept: true)]
        settings.rules = [ReceiveRule(name: "Images", extensions: ["jpg"], autoAccept: true)]
        return settings
    }
    func testTrustRequiresDeviceAndRuleConsent() throws {
        var settings = automaticSettings()
        XCTAssertTrue(try ReceivePolicy.evaluate(settings: settings, offer: offer(), fingerprint: fingerprint).autoAccept)
        settings.devices[0].trusted = false
        XCTAssertFalse(try ReceivePolicy.evaluate(settings: settings, offer: offer(), fingerprint: fingerprint).autoAccept)
        settings.devices[0].trusted = true
        settings.devices[0].autoAccept = false
        XCTAssertFalse(try ReceivePolicy.evaluate(settings: settings, offer: offer(), fingerprint: fingerprint).autoAccept)
    }
    func testCertificateIdentityCannotBeSpoofedByJSON() {
        XCTAssertThrowsError(try ReceivePolicy.evaluate(settings: automaticSettings(), offer: offer(), fingerprint: String(repeating: "00", count: 32)))
    }
    func testAllFilesMustMatchRule() throws {
        XCTAssertFalse(try ReceivePolicy.evaluate(settings: automaticSettings(), offer: offer(["photo.jpg", "payload.exe"]), fingerprint: fingerprint).autoAccept)
    }
    func testFirstMatchingRuleWins() throws {
        var settings = automaticSettings()
        settings.rules.insert(ReceiveRule(name: "Ask", autoAccept: false), at: 0)
        XCTAssertFalse(try ReceivePolicy.evaluate(settings: settings, offer: offer(), fingerprint: fingerprint).autoAccept)
    }
    func testPathTraversalAndReservedNamesRejected() {
        for path in ["../secret", "/etc/passwd", "x/../../secret", "C:/Users/a", "safe/CON.txt", "a//b", "x/.", "x\\..\\y", "file."] {
            XCTAssertThrowsError(try ReceivePolicy.relativePath(path), path)
        }
    }
    func testUnicodeAndCaseAliasesRejected() {
        XCTAssertThrowsError(try ReceivePolicy.validate(offer(["Photo.jpg", "photo.jpg"]), settings: AppSettings()))
        XCTAssertThrowsError(try ReceivePolicy.validate(offer(["café.jpg", "cafe\u{301}.jpg"]), settings: AppSettings()))
    }
    func testFileDirectoryConflictRejected() {
        XCTAssertThrowsError(try ReceivePolicy.validate(offer(["photo.jpg", "photo.jpg/inside"]), settings: AppSettings()))
    }
    func testByteOverflowRejectedBeforeSummation() {
        let oversized = TransferOffer(senderName: "Test", senderFingerprint: fingerprint, entries: [
            .init(relativePath: "a", size: Int64.max, isDirectory: false),
            .init(relativePath: "b", size: Int64.max, isDirectory: false)
        ])
        XCTAssertThrowsError(try ReceivePolicy.validate(oversized, settings: AppSettings()))
    }
    func testFolderRulesApplyToRootsAndEveryExtension() throws {
        var settings = automaticSettings()
        settings.rules[0].scope = .folders
        settings.rules[0].folderNamePattern = "Photos*"
        XCTAssertTrue(try ReceivePolicy.evaluate(settings: settings, offer: offer(["Photos 2026/a.jpg"]), fingerprint: fingerprint).autoAccept)
        XCTAssertFalse(try ReceivePolicy.evaluate(settings: settings, offer: offer(["Photos 2026/a.jpg", "Other/b.jpg"]), fingerprint: fingerprint).autoAccept)
    }
    func testWindowsJSONUnknownTimestampFieldsAreIgnored() throws {
        let json = """
        {"type":"hello","version":1,"peer":{"fingerprint":"\(fingerprint)","name":"Windows","platform":"windows","address":"192.168.1.4","port":45831,"receivingEnabled":true,"lastSeen":"2026-10-08T08:01:02.1234567+00:00"}}
        """
        let hello = try JSONDecoder().decode(WireMessage.self, from: Data(json.utf8))
        XCTAssertEqual(hello.peer?.name, "Windows")
    }
    func testWildcardAndLANBoundary() {
        XCTAssertTrue(ReceivePolicy.wildcard("photos-??", "photos-12"))
        XCTAssertFalse(ReceivePolicy.wildcard("photos-??", "photos-123"))
        XCTAssertFalse(LANDiscovery.isLocal("127.0.0.1"))
        XCTAssertFalse(LANDiscovery.isLocal("example.com"))
        XCTAssertFalse(LANDiscovery.isLocal("255.255.255.255"))
        XCTAssertFalse(LANDiscovery.isLocal("224.0.0.1"))
    }
}
