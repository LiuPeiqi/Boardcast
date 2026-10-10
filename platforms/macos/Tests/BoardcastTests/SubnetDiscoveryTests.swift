import Foundation
import XCTest
@testable import Boardcast

final class SubnetDiscoveryTests: XCTestCase {
    func testCanonicalPrivateRangesAndCompleteSmallRanges() throws {
        let subnet = try XCTUnwrap(IPv4Subnet("192.168.12.7/24"))
        XCTAssertEqual(subnet.cidr, "192.168.12.0/24")
        XCTAssertEqual(subnet.hostCount, 254)
        XCTAssertTrue(subnet.contains("192.168.12.7"))
        XCTAssertFalse(subnet.contains("192.168.12.0"))
        XCTAssertFalse(subnet.contains("192.168.12.255"))
        XCTAssertTrue(try XCTUnwrap(IPv4Subnet("10.1.2.0/31")).contains("10.1.2.0"))
        XCTAssertTrue(try XCTUnwrap(IPv4Subnet("10.1.2.0/31")).contains("10.1.2.1"))
        XCTAssertEqual(try XCTUnwrap(IPv4Subnet("10.1.2.255/32")).hostCount, 1)
        XCTAssertTrue(try XCTUnwrap(IPv4Subnet("10.1.2.255/32")).contains("10.1.2.255"))
        for invalid in ["8.8.8.0/24", "172.32.0.0/16", "10.0.0.0/8", "192.168.0.0/15",
                        "127.0.0.0/24", "169.254.0.0/16", "192.168.01.0/24", "example.com/24"] {
            XCTAssertNil(IPv4Subnet(invalid), invalid)
        }
    }

    func testAuthenticatedSeedPrefersMatchingActualInterfaceAndFallsBack() throws {
        let reported = [DiscoverySubnet(cidr: "192.168.0.0/16"), DiscoverySubnet(cidr: "10.4.0.0/20")]
        let learned = try XCTUnwrap(SubnetValidation.learned(address: "10.4.7.5", advertised: reported, port: 45831))
        XCTAssertEqual(learned.cidr, "10.4.0.0/20")
        XCTAssertEqual(learned.seedAddress, "10.4.7.5")
        XCTAssertEqual(SubnetValidation.learned(address: "10.5.7.5",
            advertised: [DiscoverySubnet(cidr: "10.0.0.0/8")], port: 45831)?.cidr, "10.5.7.0/24")
        XCTAssertEqual(SubnetValidation.learned(address: "192.168.7.4", advertised: [], port: 45831)?.cidr, "192.168.7.0/24")
        XCTAssertNil(SubnetValidation.learned(address: "8.8.8.8", advertised: reported, port: 45831))
    }

    func testRangesAreValidatedBeforeImportAndPortsRemainIndependent() throws {
        let first = DiscoverySubnet(cidr: "192.168.7.0/24")
        XCTAssertNoThrow(try SubnetValidation.validate([first, DiscoverySubnet(cidr: first.cidr, port: 47000)]))
        XCTAssertThrowsError(try SubnetValidation.validate([first, first]))
        XCTAssertThrowsError(try SubnetValidation.validate([DiscoverySubnet(cidr: "192.168.7.4/24")]))
        XCTAssertThrowsError(try SubnetValidation.validate([DiscoverySubnet(cidr: first.cidr, port: 80)]))
        XCTAssertThrowsError(try SubnetValidation.validate([DiscoverySubnet(cidr: first.cidr, seedAddress: "192.168.8.4")]))
        XCTAssertThrowsError(try SubnetValidation.validate([]))
        XCTAssertNoThrow(try SubnetValidation.validate([], allowEmpty: true))
        XCTAssertThrowsError(try SubnetValidation.validate((1...33).map { DiscoverySubnet(cidr: "10.0.\($0).0/24") }))
    }

    func testStreamingCursorDoesNotSkipHostsAcrossBatchesOrDiscardSlash31And32() throws {
        var cursor = SubnetScanCursor([DiscoverySubnet(cidr: "10.0.0.0/24")])
        var hosts = Set<String>()
        while let target = cursor.next() {
            XCTAssertTrue(hosts.insert(target.address).inserted)
            XCTAssertEqual(target.port, 45831)
        }
        XCTAssertEqual(hosts.count, 254)
        XCTAssertTrue(hosts.contains("10.0.0.33"))
        XCTAssertTrue(hosts.contains("10.0.0.128"))
        XCTAssertNil(cursor.next())
        var small = SubnetScanCursor([DiscoverySubnet(cidr: "10.0.1.0/31"),
            DiscoverySubnet(cidr: "10.0.2.255/32", port: 47000)])
        XCTAssertEqual(small.next()?.address, "10.0.1.0")
        let single = try XCTUnwrap(small.next())
        XCTAssertEqual(single.address, "10.0.2.255")
        XCTAssertEqual(single.port, 47000)
        XCTAssertEqual(small.next()?.address, "10.0.1.1")
        XCTAssertNil(small.next())
    }

    func testOldSettingsAndHelloKeepCompatibleDefaults() throws {
        let settings = try JSONDecoder().decode(AppSettings.self, from: Data("{}".utf8))
        XCTAssertTrue(settings.discoverySubnets.isEmpty)
        let identity = String(repeating: "a", count: 64)
        let json = """
        {"type":"hello","version":1,"peer":{"fingerprint":"\(identity)","name":"Old PC","port":45831}}
        """
        let old = try JSONDecoder().decode(WireMessage.self, from: Data(json.utf8))
        XCTAssertNil(old.capabilities)
        XCTAssertEqual(old.peer?.discoverySubnets, [])
        XCTAssertNoThrow(try TransferService.validateHello(old, fingerprint: identity))
    }

    func testWindowsWireContractUsesTopLevelCapabilityAndNoLocalSeedMetadata() throws {
        let identity = String(repeating: "b", count: 64)
        let hello = WireMessage(type: "hello", version: 1,
            peer: PeerInfo(fingerprint: identity, name: "Windows",
                discoverySubnets: [DiscoverySubnet(cidr: "10.2.0.0/16")]),
            capabilities: [SubnetValidation.capability])
        let data = try JSONEncoder().encode(hello)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertEqual(json["capabilities"] as? [String], [SubnetValidation.capability])
        let packet = WireMessage(type: "subnet-offer", version: 1, transferId: String(repeating: "c", count: 32),
            subnets: [DiscoverySubnet(cidr: "10.2.0.0/16", seedAddress: "10.2.1.1").wireValue])
        let packetJSON = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(packet)) as? [String: Any])
        let range = try XCTUnwrap((packetJSON["subnets"] as? [[String: Any]])?.first)
        XCTAssertEqual(Set(range.keys), Set(["cidr", "port"]))
        XCTAssertEqual(packetJSON["transferId"] as? String, String(repeating: "c", count: 32))
    }

    func testResponseRequiresVersionMatchingTransferAndBoundedReason() {
        let id = String(repeating: "a", count: 32)
        XCTAssertNoThrow(try TransferService.validateSubnetResponse(
            WireMessage(type: "subnet-accept", version: 1, transferId: id), transferId: id))
        XCTAssertThrowsError(try TransferService.validateSubnetResponse(
            WireMessage(type: "subnet-accept", version: 2, transferId: id), transferId: id))
        XCTAssertThrowsError(try TransferService.validateSubnetResponse(
            WireMessage(type: "accept", version: 1, transferId: id), transferId: id))
        XCTAssertThrowsError(try TransferService.validateSubnetResponse(
            WireMessage(type: "subnet-reject", version: 1, transferId: "other"), transferId: id))
        XCTAssertThrowsError(try TransferService.validateSubnetResponse(
            WireMessage(type: "subnet-reject", version: 1, message: String(repeating: "x", count: 4097),
                transferId: id), transferId: id))
    }

    func testSettingsRoundTripAndUnrelatedDraftPreserveImportedRanges() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let store = try SettingsStore(directory: directory)
        let original = AppSettings()
        var current = original
        current.discoverySubnets = [DiscoverySubnet(cidr: "192.168.4.0/24", seedAddress: "192.168.4.6")]
        try store.save(current)
        XCTAssertEqual(try store.load().discoverySubnets, current.discoverySubnets)
        var draft = original
        draft.deviceName = "New Mac"
        let merged = try SettingsValidation.merge(draft: draft, baseline: original, current: current)
        XCTAssertEqual(merged.discoverySubnets, current.discoverySubnets)
    }
}
