import XCTest
import Darwin
@testable import Boardcast

final class MacUpdateServiceTests: XCTestCase {
    private func current() throws -> MacReleaseIdentity {
        MacReleaseIdentity(teamID: "ABCDEFGHIJ", bundleID: "local.boardcast.mac",
            version: try MacVersion("1.2.3"), build: "8", minimumSystemVersion: "13.0", runtime: "osx-universal")
    }

    private func image() -> MacUpdateImage {
        MacUpdateImage(type: "mac-update-info", version: "1.2.4", build: "9", bytes: 1024,
            sha256: String(repeating: "a", count: 64))
    }

    func testImageManifestAcceptsHigherUniversalVersion() throws {
        try image().validate(current: current(), expectedType: "mac-update-info")
    }

    func testBuildIncreaseDoesNotAuthorizeSameVersionOrDowngrade() throws {
        for version in ["1.2.3", "1.2.2", "0.99.99"] {
            var value = image(); value.version = version; value.build = "999999"
            XCTAssertThrowsError(try value.validate(current: current(), expectedType: "mac-update-info"))
        }
    }

    func testManifestRejectsForeignRuntimeProtocolAndMessage() throws {
        var wrongRuntime = image(); wrongRuntime.runtime = "win-x64"
        var wrongProtocol = image(); wrongProtocol.protocolVersion = 2
        var wrongType = image(); wrongType.type = "complete"
        for value in [wrongRuntime, wrongProtocol, wrongType] {
            XCTAssertThrowsError(try value.validate(current: current(), expectedType: "mac-update-info"))
        }
    }

    func testManifestRejectsOversizeEmptyAndInvalidChecksum() throws {
        var empty = image(); empty.bytes = 0
        var negative = image(); negative.bytes = -1
        var oversized = image(); oversized.bytes = MacUpdateService.maximumImageBytes + 1
        var invalid = image(); invalid.sha256 = String(repeating: "z", count: 64)
        for value in [empty, negative, oversized, invalid] {
            XCTAssertThrowsError(try value.validate(current: current(), expectedType: "mac-update-info"))
        }
        var maximum = image(); maximum.bytes = MacUpdateService.maximumImageBytes
        XCTAssertNoThrow(try maximum.validate(current: current(), expectedType: "mac-update-info"))
    }

    func testManifestRejectsPrereleaseAndMalformedVersion() throws {
        for version in ["1.2.4-beta", "1.2", "01.2.4", "1.2.18446744073709551616"] {
            var value = image(); value.version = version
            XCTAssertThrowsError(try value.validate(current: current(), expectedType: "mac-update-info"))
        }
    }

    func testJSONManifestKeepsMacUpdateWireFields() throws {
        let value = image()
        let data = try JSONEncoder().encode(value)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(object["type"] as? String, "mac-update-info")
        XCTAssertEqual(object["runtime"] as? String, "osx-universal")
        XCTAssertEqual(object["protocolVersion"] as? Int, 1)
        XCTAssertEqual(try JSONDecoder().decode(MacUpdateImage.self, from: data), value)
    }

    func testMountRecordMustContainExactlyRequestedMountPoint() throws {
        let mount = URL(fileURLWithPath: "/Users/test/Library/Application Support/Boardcast/Updates/Downloads/one/Mount")
        let valid: [String: Any] = ["system-entities": [["dev-entry": "/dev/disk9s1", "mount-point": mount.path]]]
        let data = try PropertyListSerialization.data(fromPropertyList: valid, format: .xml, options: 0)
        XCTAssertNoThrow(try MacUpdateService.validateMountedImage(data, at: mount))
        for points in [[], ["/Volumes/Unrequested"], [mount.path, "/Volumes/Extra"]] {
            let invalid: [String: Any] = ["system-entities": points.map { ["mount-point": $0] }]
            let data = try PropertyListSerialization.data(fromPropertyList: invalid, format: .xml, options: 0)
            XCTAssertThrowsError(try MacUpdateService.validateMountedImage(data, at: mount))
        }
    }

    func testMountRecordRejectsUnstructuredData() {
        XCTAssertThrowsError(try MacUpdateService.validateMountedImage(Data("not a plist".utf8), at: URL(fileURLWithPath: "/tmp/Mount")))
    }

    private func temporaryDirectory() throws -> URL {
        guard let resolved = realpath(FileManager.default.temporaryDirectory.path, nil) else {
            throw BoardcastError.invalid("Cannot resolve test directory")
        }
        defer { free(resolved) }
        let root = URL(fileURLWithPath: String(cString: resolved), isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        return root
    }

    func testReadyCleanupRemovesOnlyRecognizedPreviousUUID() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let oldID = UUID(), retainedID = UUID()
        let old = root.appendingPathComponent("Ready/" + oldID.uuidString)
        let retained = root.appendingPathComponent("Ready/" + retainedID.uuidString)
        try FileManager.default.createDirectory(at: old.appendingPathComponent("Boardcast.app"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: retained.appendingPathComponent("Boardcast.app"), withIntermediateDirectories: true)
        MacUpdateService.removeKnownReady(directory: root, id: oldID)
        XCTAssertFalse(FileManager.default.fileExists(atPath: old.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: retained.path))
    }

    func testReadyCleanupRetainsUnrecognizedDirectoryContents() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let id = UUID(), folder = root.appendingPathComponent("Ready/" + id.uuidString)
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("Boardcast.app"), withIntermediateDirectories: true)
        try Data("manual evidence".utf8).write(to: folder.appendingPathComponent("keep.txt"))
        MacUpdateService.removeKnownReady(directory: root, id: id)
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("keep.txt").path))
    }

    func testReadyCleanupRefusesSymbolicLinkApp() throws {
        let root = try temporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let id = UUID(), folder = root.appendingPathComponent("Ready/" + id.uuidString)
        let target = root.appendingPathComponent("UserFiles")
        try FileManager.default.createDirectory(at: target, withIntermediateDirectories: false)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: folder.appendingPathComponent("Boardcast.app"), withDestinationURL: target)
        MacUpdateService.removeKnownReady(directory: root, id: id)
        XCTAssertTrue(FileManager.default.fileExists(atPath: target.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.path))
    }

}
