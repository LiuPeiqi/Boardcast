import Foundation
import XCTest
@testable import Boardcast

final class DeviceGroupsTests: XCTestCase {
    private let fingerprint = String(repeating: "ab", count: 32)

    func testAutomaticPrefixAndManualOverride() {
        for name in [" Design-Mac ", "Design_Mac", "Design.Mac", "Design Mac", "Design\tMac", "-Design-Mac"] {
            XCTAssertEqual(DeviceGroups.automatic(name), "Design", name)
        }
        XCTAssertEqual(DeviceGroups.automatic("Studio"), "Studio")
        XCTAssertEqual(DeviceGroups.automatic(" _.- "), "未分组")
        XCTAssertEqual(DeviceGroups.effective(deviceName: "Design-Mac", groupName: "  Release Team  "), "Release Team")
        XCTAssertEqual(DeviceGroups.effective(deviceName: "Design-Mac", groupName: " "), "Design")
    }

    func testPersonalGroupWinsWithoutUsingAlias() {
        let peer = PeerInfo(fingerprint: fingerprint, name: "Design-Mac", groupName: "Shared")
        var settings = AppSettings()
        settings.devices = [KnownDevice(fingerprint: fingerprint.uppercased(), name: "Old-PC", alias: "Unrelated alias", groupName: "Private")]
        XCTAssertEqual(DeviceGroups.peer(peer, settings: settings), "Private")
        settings.devices[0].groupName = ""
        XCTAssertEqual(DeviceGroups.peer(peer, settings: settings), "Shared")
        var legacy = peer
        legacy.groupName = ""
        XCTAssertEqual(DeviceGroups.peer(legacy, settings: settings), "Design")
    }

    func testFiltersAreCaseInsensitiveAndEmptySelectionShowsNothing() {
        let design = PeerInfo(fingerprint: fingerprint, name: "Design-Mac")
        let release = PeerInfo(fingerprint: "other", name: "Other-PC", groupName: "Release")
        var settings = AppSettings()
        settings.deviceName = "design-PC"
        XCTAssertTrue(DeviceGroups.isVisible(release, settings: settings))
        settings.peerGroupFilter = .sameGroup
        XCTAssertTrue(DeviceGroups.isVisible(design, settings: settings))
        XCTAssertFalse(DeviceGroups.isVisible(release, settings: settings))
        settings.peerGroupFilter = .selectedGroups
        XCTAssertFalse(DeviceGroups.isVisible(design, settings: settings))
        settings.visibleGroups = [" DESIGN ", "release"]
        XCTAssertTrue(DeviceGroups.isVisible(design, settings: settings))
        XCTAssertTrue(DeviceGroups.isVisible(release, settings: settings))
        settings.devices = [KnownDevice(fingerprint: fingerprint, name: design.name, groupName: "Private")]
        XCTAssertFalse(DeviceGroups.isVisible(design, settings: settings))
    }

    func testAvailableGroupsPreserveOfflineSelectionAndDeduplicate() {
        var settings = AppSettings()
        settings.deviceName = "Design-PC"
        settings.visibleGroups = [" design ", "Offline"]
        let groups = DeviceGroups.available(peers: [PeerInfo(fingerprint: fingerprint, name: "Design-Mac")], settings: settings)
        XCTAssertEqual(groups.count, 2)
        XCTAssertTrue(groups.contains("Offline"))
        XCTAssertEqual(groups.filter { DeviceGroups.same($0, "Design") }.count, 1)
    }

    func testLegacySettingsKeepIdentityTrustAndDefaultToAll() throws {
        let json = """
        {"version":1,"deviceName":"Design-PC","receiveDirectory":"/tmp/receive","port":45831,
        "receivingEnabled":true,"maxTransferBytes":107374182400,"maxEntries":50000,
        "devices":[{"fingerprint":"\(fingerprint)","name":"Design-Mac","alias":"Desk",
        "trusted":true,"autoAccept":true,"lastAddress":"192.168.1.4","port":45831}],"rules":[]}
        """
        let settings = try JSONDecoder().decode(AppSettings.self, from: Data(json.utf8))
        XCTAssertEqual(settings.groupName, "")
        XCTAssertEqual(settings.peerGroupFilter, .all)
        XCTAssertTrue(settings.visibleGroups.isEmpty)
        XCTAssertEqual(settings.devices[0].groupName, "")
        XCTAssertTrue(settings.devices[0].trusted)
        XCTAssertTrue(settings.devices[0].autoAccept)
        XCTAssertEqual(settings.devices[0].alias, "Desk")
    }

    func testLegacyDiscoveryAndHelloDecodeWithoutGroup() throws {
        let discoveryJSON = """
        {"protocolVersion":1,"type":"announce","fingerprint":"\(fingerprint)",
        "name":"Windows-PC","platform":"windows","port":45831,"receivingEnabled":true}
        """
        let discovery = try JSONDecoder().decode(DiscoveryMessage.self, from: Data(discoveryJSON.utf8))
        XCTAssertEqual(discovery.groupName, "")
        let helloJSON = """
        {"type":"hello","version":1,"peer":{"fingerprint":"\(fingerprint)","name":"Windows-PC",
        "platform":"windows","address":"192.168.1.4","port":45831,"receivingEnabled":true}}
        """
        let hello = try JSONDecoder().decode(WireMessage.self, from: Data(helloJSON.utf8))
        XCTAssertEqual(hello.peer?.groupName, "")
    }

    func testSettingsAndDiscoveryRoundTripNewFields() throws {
        var settings = AppSettings()
        settings.groupName = "Design"
        settings.peerGroupFilter = .selectedGroups
        settings.visibleGroups = ["Design", "Release"]
        settings.devices = [KnownDevice(fingerprint: fingerprint, name: "Windows", groupName: "Private")]
        let data = try JSONEncoder().encode(settings)
        let restored = try JSONDecoder().decode(AppSettings.self, from: data)
        XCTAssertEqual(restored.groupName, "Design")
        XCTAssertEqual(restored.peerGroupFilter, .selectedGroups)
        XCTAssertEqual(restored.visibleGroups, ["Design", "Release"])
        XCTAssertEqual(restored.devices[0].groupName, "Private")
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(json["peerGroupFilter"] as? String, "selectedGroups")
        let message = DiscoveryMessage(fingerprint: fingerprint, name: "Design-PC", groupName: "Release")
        let restoredMessage = try JSONDecoder().decode(DiscoveryMessage.self, from: JSONEncoder().encode(message))
        XCTAssertEqual(restoredMessage.groupName, "Release")
        let peer = PeerInfo(fingerprint: fingerprint, name: "Design-PC", groupName: "Release")
        let restoredPeer = try JSONDecoder().decode(PeerInfo.self, from: JSONEncoder().encode(peer))
        XCTAssertEqual(restoredPeer.groupName, "Release")
    }

    func testGroupNameLimitsAndControls() {
        XCTAssertTrue(DeviceGroups.isValid(String(repeating: "a", count: 80)))
        XCTAssertFalse(DeviceGroups.isValid(String(repeating: "a", count: 81)))
        XCTAssertTrue(DeviceGroups.isValid(String(repeating: "😀", count: 40)))
        XCTAssertFalse(DeviceGroups.isValid(String(repeating: "😀", count: 41)))
        for invalid in ["Name\nOther", "Name\tOther", "Name\u{0}Other"] {
            XCTAssertFalse(DeviceGroups.isValid(invalid))
        }
        var settings = AppSettings()
        settings.groupName = "Invalid\nGroup"
        XCTAssertThrowsError(try DeviceGroups.validate(settings))
        settings.groupName = ""
        settings.devices = [KnownDevice(fingerprint: fingerprint, name: "Other", groupName: "Invalid\tGroup")]
        XCTAssertThrowsError(try DeviceGroups.validate(settings))
    }

    func testUngroupedAndLongAutomaticGroupsCanBeSelected() throws {
        var settings = AppSettings()
        settings.deviceName = " _.- "
        settings.peerGroupFilter = .sameGroup
        XCTAssertEqual(DeviceGroups.local(settings), "未分组")
        XCTAssertTrue(DeviceGroups.isVisible(PeerInfo(fingerprint: fingerprint, name: "", groupName: "未分组"), settings: settings))
        settings.peerGroupFilter = .selectedGroups
        let longName = String(repeating: "a", count: 256)
        settings.visibleGroups = [longName]
        XCTAssertNoThrow(try DeviceGroups.validate(settings))
        XCTAssertTrue(DeviceGroups.isVisible(PeerInfo(fingerprint: fingerprint, name: longName), settings: settings))
        settings.visibleGroups = [String(repeating: "a", count: 257)]
        XCTAssertThrowsError(try DeviceGroups.validate(settings))
        settings.visibleGroups = Array(repeating: "Group", count: 129)
        XCTAssertThrowsError(try DeviceGroups.validate(settings))
        settings.visibleGroups = [" "]
        XCTAssertThrowsError(try DeviceGroups.validate(settings))
    }

    func testExplicitNullGroupFieldsAreRejected() {
        for field in ["groupName", "peerGroupFilter", "visibleGroups"] {
            XCTAssertThrowsError(try JSONDecoder().decode(AppSettings.self, from: Data("{\"\(field)\":null}".utf8)))
        }
        let peer = """
        {"fingerprint":"\(fingerprint)","name":"Windows","groupName":null}
        """
        XCTAssertThrowsError(try JSONDecoder().decode(PeerInfo.self, from: Data(peer.utf8)))
        XCTAssertThrowsError(try JSONDecoder().decode(KnownDevice.self, from: Data(peer.utf8)))
        let discovery = """
        {"protocolVersion":1,"type":"announce","fingerprint":"\(fingerprint)","name":"Windows","groupName":null}
        """
        XCTAssertThrowsError(try JSONDecoder().decode(DiscoveryMessage.self, from: Data(discovery.utf8)))
    }

    func testMaximumUnicodeNameAndGroupFitLegacyDiscoveryLimit() throws {
        let message = DiscoveryMessage(fingerprint: fingerprint, name: String(repeating: "设", count: 80), groupName: String(repeating: "组", count: 80))
        XCTAssertLessThanOrEqual(try JSONEncoder().encode(message).count, 2048)
    }

    func testFilteringDoesNotGrantTrustOrSuppressReceivePolicy() throws {
        var settings = AppSettings()
        settings.deviceName = "Design-Mac"
        settings.peerGroupFilter = .sameGroup
        settings.devices = [KnownDevice(fingerprint: fingerprint, name: "Other-PC", groupName: "Other", trusted: true, autoAccept: true)]
        settings.rules = [ReceiveRule(name: "Automatic", autoAccept: true)]
        let peer = PeerInfo(fingerprint: fingerprint, name: "Other-PC")
        XCTAssertFalse(DeviceGroups.isVisible(peer, settings: settings))
        let offer = TransferOffer(senderName: peer.name, senderFingerprint: fingerprint, entries: [.init(relativePath: "file.txt", size: 8, isDirectory: false)])
        XCTAssertTrue(try ReceivePolicy.evaluate(settings: settings, offer: offer, fingerprint: fingerprint).autoAccept)
        settings.devices[0].trusted = false
        settings.devices[0].groupName = "Design"
        XCTAssertTrue(DeviceGroups.isVisible(peer, settings: settings))
        XCTAssertFalse(try ReceivePolicy.evaluate(settings: settings, offer: offer, fingerprint: fingerprint).autoAccept)
    }
}
