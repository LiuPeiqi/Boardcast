import XCTest
import Foundation
import Darwin
@testable import Boardcast

final class MacReleaseTests: XCTestCase {
    func testStableVersionOrderingDoesNotUseStringOrdering() throws {
        XCTAssertLessThan(try MacVersion("1.9.99"), try MacVersion("1.10.0"))
        XCTAssertLessThan(try MacVersion("0.99.99"), try MacVersion("1.0.0"))
        XCTAssertEqual(try MacVersion("2.3.4"), try MacVersion("2.3.4"))
    }
    func testStableVersionRejectsAmbiguousOrOverflowingComponents() {
        for value in ["1", "1.2", "1.2.3.4", "01.2.3", "+1.2.3", "1.-2.3", "1.2.3-beta", "1.2.３", "1.2.18446744073709551616", "1. 2.3"] {
            XCTAssertThrowsError(try MacVersion(value), value)
        }
    }
    func testVersionJSONIsAStringAndDecoderValidatesIt() throws {
        let data = try JSONEncoder().encode(MacVersion("1.2.3"))
        XCTAssertEqual(String(decoding: data, as: UTF8.self), "\"1.2.3\"")
        XCTAssertThrowsError(try JSONDecoder().decode(MacVersion.self, from: Data("\"1.2.3beta\"".utf8)))
    }
    func testIdentityRoundTripRetainsDeveloperAndUniversalRuntime() throws {
        let identity = MacReleaseIdentity(teamID: "ABCDE12345", bundleID: "local.boardcast.mac",
            version: try MacVersion("1.2.3"), build: "42", minimumSystemVersion: "13.0", runtime: "osx-universal")
        XCTAssertEqual(try JSONDecoder().decode(MacReleaseIdentity.self, from: JSONEncoder().encode(identity)), identity)
    }
    func testOnlySupportedApplicationInstallLocationsAreAccepted() {
        for path in ["/Volumes/Boardcast/Boardcast.app", "/Applications/Other.app", "/tmp/Boardcast.app", "/Applications/../Applications/Boardcast.app"] {
            XCTAssertThrowsError(try MacRelease.validateInstallTarget(URL(fileURLWithPath: path)), path)
        }
    }
    func testUpdatePathsRejectSymbolicLinkAncestors() throws {
        let root = try physicalTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let real = root.appendingPathComponent("real", isDirectory: true)
        try FileManager.default.createDirectory(at: real, withIntermediateDirectories: false)
        let link = root.appendingPathComponent("link", isDirectory: true)
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: real)
        XCTAssertThrowsError(try MacRelease.requireNoSymlinks(link.appendingPathComponent("plan.json"), allowMissing: true))
        XCTAssertNoThrow(try MacRelease.requireNoSymlinks(real.appendingPathComponent("plan.json"), allowMissing: true))
    }
    func testMetadataReadRefusesOversizedFilesAndDirectories() throws {
        let root = try physicalTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("large.json")
        try Data(repeating: 0, count: 4097).write(to: file)
        XCTAssertThrowsError(try MacRelease.boundedData(at: file, maximum: 4096))
        XCTAssertThrowsError(try MacRelease.boundedData(at: root, maximum: 4096))
    }
    func testSystemRunnerRejectsArbitraryExecutablesWithoutLaunchingThem() {
        XCTAssertThrowsError(try MacRelease.run(executable: "/bin/sh", arguments: ["-c", "false"]))
        XCTAssertThrowsError(try MacRelease.run(executable: "/usr/bin/codesign", arguments: ["bad\0path"]))
        XCTAssertThrowsError(try MacRelease.run(executable: "/usr/bin/codesign", arguments: [], timeout: 0))
    }
    func testSystemDirectoryReplacementRetainsBackupAndCanRestoreIt() throws {
        let root = try physicalTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let current = root.appendingPathComponent("Boardcast.app", isDirectory: true)
        let stage = root.appendingPathComponent(".Boardcast-install-test/Boardcast.app", isDirectory: true)
        let backup = root.appendingPathComponent(".Boardcast-backup-test.app", isDirectory: true)
        let failed = root.appendingPathComponent(".Boardcast-failed-test.app", isDirectory: true)
        try FileManager.default.createDirectory(at: current, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: stage, withIntermediateDirectories: true)
        try Data("old".utf8).write(to: current.appendingPathComponent("payload"))
        try Data("new".utf8).write(to: stage.appendingPathComponent("payload"))
        _ = try FileManager.default.replaceItemAt(current, withItemAt: stage,
            backupItemName: backup.lastPathComponent, options: [.usingNewMetadataOnly, .withoutDeletingBackupItem])
        XCTAssertEqual(try Data(contentsOf: current.appendingPathComponent("payload")), Data("new".utf8))
        XCTAssertEqual(try Data(contentsOf: backup.appendingPathComponent("payload")), Data("old".utf8))
        XCTAssertFalse(FileManager.default.fileExists(atPath: stage.path))
        _ = try FileManager.default.replaceItemAt(current, withItemAt: backup,
            backupItemName: failed.lastPathComponent, options: [.usingNewMetadataOnly, .withoutDeletingBackupItem])
        XCTAssertEqual(try Data(contentsOf: current.appendingPathComponent("payload")), Data("old".utf8))
        XCTAssertEqual(try Data(contentsOf: failed.appendingPathComponent("payload")), Data("new".utf8))
        XCTAssertFalse(FileManager.default.fileExists(atPath: backup.path))
    }
    private func physicalTemporaryDirectory() throws -> URL {
        let temporary = FileManager.default.temporaryDirectory
        guard let pointer = realpath(temporary.path, nil) else { throw MacUpdateError.invalid("The test temporary directory cannot be resolved.") }
        defer { free(pointer) }
        let root = URL(fileURLWithPath: String(cString: pointer), isDirectory: true).appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        return root
    }
}