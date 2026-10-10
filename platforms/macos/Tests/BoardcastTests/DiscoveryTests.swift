import Foundation
import Darwin
import XCTest
@testable import Boardcast

final class DiscoveryTests: XCTestCase {
    private let localFingerprint = String(repeating: "ab", count: 32)
    private let remoteFingerprint = String(repeating: "cd", count: 32)

    func testLegacyWindowsAnnouncementKeepsCompatibleDefaults() throws {
        let json = """
        {"protocolVersion":1,"type":"announce","fingerprint":"\(remoteFingerprint)",
         "name":"Windows-PC","platform":"windows","port":45831}
        """
        let message = try XCTUnwrap(LANDiscovery.validatedMessage(Data(json.utf8), localFingerprint: localFingerprint))
        XCTAssertTrue(message.receivingEnabled)
        XCTAssertTrue(message.lanOnline)
        XCTAssertNil(message.appVersion)
        XCTAssertNil(message.runtime)
        XCTAssertEqual(message.groupName, "")
    }

    func testOptionalStateAndVersionRoundTripWithoutInventingValues() throws {
        let message = DiscoveryMessage(type: "announce", fingerprint: remoteFingerprint, name: "Windows-PC",
                                       receivingEnabled: false, lanOnline: true, appVersion: "2.3.4", runtime: ".NET 10")
        let data = try JSONEncoder().encode(message)
        let decoded = try XCTUnwrap(LANDiscovery.validatedMessage(data, localFingerprint: localFingerprint))
        XCTAssertFalse(decoded.receivingEnabled)
        XCTAssertTrue(decoded.lanOnline)
        XCTAssertEqual(decoded.appVersion, "2.3.4")
        XCTAssertEqual(decoded.runtime, ".NET 10")
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        XCTAssertNil(json["updatePort"])
        XCTAssertNil(json["updateVersion"])

        var offered = message
        offered.updatePort = 47000
        offered.updateVersion = "9.9.9"
        let offeredData = try JSONEncoder().encode(offered)
        let offeredJSON = try XCTUnwrap(JSONSerialization.jsonObject(with: offeredData) as? [String: Any])
        XCTAssertEqual(offeredJSON["updatePort"] as? Int, 47000)
        XCTAssertEqual(offeredJSON["updateVersion"] as? String, "9.9.9")
        let decodedOffer = try XCTUnwrap(LANDiscovery.validatedMessage(offeredData, localFingerprint: localFingerprint))
        XCTAssertEqual(decodedOffer.updatePort, 47000)
        XCTAssertEqual(decodedOffer.updateVersion, "9.9.9")
    }

    func testGoodbyeIsAcceptedAsADeparturePacket() throws {
        let message = DiscoveryMessage(type: "goodbye", fingerprint: remoteFingerprint, name: "Windows-PC", lanOnline: false)
        let decoded = try XCTUnwrap(LANDiscovery.validatedMessage(JSONEncoder().encode(message), localFingerprint: localFingerprint))
        XCTAssertEqual(decoded.type, "goodbye")
        XCTAssertFalse(decoded.lanOnline)
    }

    func testNullOptionalMetadataDefaultsButInvalidTypesAreRejected() throws {
        let json = """
        {"protocolVersion":1,"type":"probe","fingerprint":"\(remoteFingerprint)","name":"PC",
         "receivingEnabled":null,"lanOnline":null,"appVersion":null,"runtime":null}
        """
        let decoded = try JSONDecoder().decode(DiscoveryMessage.self, from: Data(json.utf8))
        XCTAssertTrue(decoded.receivingEnabled)
        XCTAssertTrue(decoded.lanOnline)
        XCTAssertNil(decoded.appVersion)
        for replacement in ["\"lanOnline\":\"true\"", "\"appVersion\":17", "\"runtime\":{}"] {
            let invalid = """
            {"protocolVersion":1,"type":"announce","fingerprint":"\(remoteFingerprint)","name":"PC",\(replacement)}
            """
            XCTAssertNil(LANDiscovery.validatedMessage(Data(invalid.utf8), localFingerprint: localFingerprint))
        }
    }

    func testUnsupportedProtocolInvalidIdentityAndSelfAnnouncementsAreRejected() throws {
        var message = DiscoveryMessage(fingerprint: remoteFingerprint, name: "PC")
        message.protocolVersion = 2
        XCTAssertNil(try validate(message))
        message.protocolVersion = 1
        message.type = "update"
        XCTAssertNil(try validate(message))
        message.type = "announce"
        message.fingerprint = String(repeating: "g", count: 64)
        XCTAssertNil(try validate(message))
        message.fingerprint = remoteFingerprint + "\n"
        XCTAssertNil(try validate(message))
        message.fingerprint = localFingerprint.uppercased()
        XCTAssertNil(try validate(message))
        message.fingerprint = remoteFingerprint
        for port in [0, 1023, 65536] {
            message.port = port
            XCTAssertNil(try validate(message))
        }
    }

    func testMetadataLimitsUseUTF16AndRejectOversizedDatagrams() throws {
        var message = DiscoveryMessage(fingerprint: remoteFingerprint, name: "PC")
        message.appVersion = String(repeating: "😀", count: 32)
        XCTAssertNotNil(try validate(message))
        message.appVersion = String(repeating: "😀", count: 33)
        XCTAssertNil(try validate(message))
        message.appVersion = nil
        message.runtime = String(repeating: "x", count: 65)
        XCTAssertNil(try validate(message))
        message.runtime = nil
        message.name = String(repeating: "😀", count: 129)
        XCTAssertNil(try validate(message))
        message.name = "PC"
        message.groupName = "Bad\nGroup"
        XCTAssertNil(try validate(message))
        XCTAssertNil(LANDiscovery.validatedMessage(Data(repeating: 32, count: 2049), localFingerprint: localFingerprint))
    }

    func testOnlyNumericUnicastOnThePhysicalSubnetIsLocal() {
        let physical: [LANDiscovery.Interface] = [(ipv4("192.168.10.5"), ipv4("255.255.255.0"), 1)]
        XCTAssertTrue(LANDiscovery.isLocal("192.168.10.8", interfaces: physical))
        for host in ["192.168.11.8", "10.0.0.5", "8.8.8.8", "example.com", "192.168.10.0",
                     "192.168.10.255", "255.255.255.255", "127.0.0.1", "0.0.0.0", "224.0.0.1", "::1"] {
            XCTAssertFalse(LANDiscovery.isLocal(host, interfaces: physical), host)
        }
        XCTAssertFalse(LANDiscovery.isLocal("192.168.10.8", interfaces: []))
        XCTAssertFalse(LANDiscovery.isLocal("8.8.8.8", interfaces: [(ipv4("192.168.10.5"), 0, 1)]))
        let linkLocal: [LANDiscovery.Interface] = [(ipv4("169.254.1.5"), ipv4("255.255.0.0"), 1)]
        XCTAssertTrue(LANDiscovery.isLocal("169.254.2.6", interfaces: linkLocal))
    }

    private func validate(_ message: DiscoveryMessage) throws -> DiscoveryMessage? {
        LANDiscovery.validatedMessage(try JSONEncoder().encode(message), localFingerprint: localFingerprint)
    }

    private func ipv4(_ host: String) -> UInt32 {
        var address = in_addr()
        precondition(inet_pton(AF_INET, host, &address) == 1)
        return address.s_addr
    }
}
