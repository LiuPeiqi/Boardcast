import Foundation
import XCTest
import Security
import CryptoKit
@testable import Boardcast

final class IdentityTests: XCTestCase {
    private func temporaryKey() throws -> SecKey {
        // No permanent flag: tests do not read or modify the user's identity in Keychain.
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256
        ]
        var failure: Unmanaged<CFError>?
        return try XCTUnwrap(SecKeyCreateRandomKey(attributes as CFDictionary, &failure))
    }

    private func date(_ value: String) throws -> Date {
        try XCTUnwrap(ISO8601DateFormatter().date(from: value))
    }

    func testCertificateDatesUseRFC5280EncodingAt2050Boundary() throws {
        let before = try DeviceIdentity.certificateTime(date("2049-12-31T23:59:59Z"))
        XCTAssertEqual(before.first, 0x17)
        XCTAssertEqual(String(data: before.dropFirst(2), encoding: .utf8), "491231235959Z")

        let after = try DeviceIdentity.certificateTime(date("2050-01-01T00:00:00Z"))
        XCTAssertEqual(after.first, 0x18)
        XCTAssertEqual(String(data: after.dropFirst(2), encoding: .utf8), "20500101000000Z")
        XCTAssertThrowsError(try DeviceIdentity.certificateTime(date("1949-12-31T23:59:59Z")))
    }

    func testFingerprintHashesEntireDERCertificateRatherThanPublicKey() throws {
        let key = try temporaryKey()
        let now = try date("2026-10-10T00:00:00Z")
        let first = try DeviceIdentity.selfSignedCertificate(privateKey: key, now: now)
        let second = try DeviceIdentity.selfSignedCertificate(privateKey: key, now: now.addingTimeInterval(60))
        let firstKey = try XCTUnwrap(SecCertificateCopyKey(first))
        let secondKey = try XCTUnwrap(SecCertificateCopyKey(second))
        XCTAssertEqual(SecKeyCopyExternalRepresentation(firstKey, nil) as Data?,
                       SecKeyCopyExternalRepresentation(secondKey, nil) as Data?)

        let expected = SHA256.hash(data: SecCertificateCopyData(first) as Data)
            .map { String(format: "%02x", $0) }.joined()
        XCTAssertEqual(DeviceIdentity.fingerprint(first), expected)
        XCTAssertNotEqual(DeviceIdentity.fingerprint(first), DeviceIdentity.fingerprint(second))
        XCTAssertFalse(DeviceIdentity.acceptsPeerCertificate(
            second, expectedFingerprint: DeviceIdentity.fingerprint(first)
        ))
    }

    func testMissingPeerCertificateFailsClosedAndPinsAreStrict() throws {
        let certificate = try DeviceIdentity.selfSignedCertificate(privateKey: temporaryKey(), now: Date())
        let pin = DeviceIdentity.fingerprint(certificate)
        XCTAssertFalse(DeviceIdentity.acceptsPeerCertificate(nil, expectedFingerprint: nil))
        XCTAssertFalse(DeviceIdentity.acceptsPeerCertificate(nil, expectedFingerprint: pin))
        XCTAssertTrue(DeviceIdentity.acceptsPeerCertificate(certificate, expectedFingerprint: nil))
        XCTAssertTrue(DeviceIdentity.acceptsPeerCertificate(certificate, expectedFingerprint: pin.uppercased()))
        XCTAssertFalse(DeviceIdentity.acceptsPeerCertificate(certificate, expectedFingerprint: String(repeating: "0", count: 64)))
        for invalid in ["", String(pin.dropLast()), pin + "0", " " + pin, String(repeating: "g", count: 64)] {
            XCTAssertFalse(DeviceIdentity.isFingerprint(invalid), invalid)
            XCTAssertFalse(DeviceIdentity.acceptsPeerCertificate(certificate, expectedFingerprint: invalid), invalid)
        }
    }

    func testKeychainReferenceRejectsWrongCFType() throws {
        XCTAssertThrowsError(try DeviceIdentity.keyReference(Data([1, 2, 3]) as CFData))
        let key = try temporaryKey()
        let reference = try DeviceIdentity.keyReference(key)
        let message = Data("Boardcast identity type check".utf8)
        let signature = try XCTUnwrap(SecKeyCreateSignature(
            reference, .ecdsaSignatureMessageX962SHA256, message as CFData, nil
        ))
        let publicKey = try XCTUnwrap(SecKeyCopyPublicKey(key))
        XCTAssertTrue(SecKeyVerifySignature(
            publicKey, .ecdsaSignatureMessageX962SHA256, message as CFData, signature, nil
        ))
    }

    private func derElement(_ bytes: Data, at start: Int) throws -> (tag: UInt8, encoded: Range<Int>, content: Range<Int>) {
        guard start >= 0, start + 2 <= bytes.count else {
            throw BoardcastError.invalid("Truncated DER element")
        }
        let firstLength = bytes[start + 1]
        var cursor = start + 2
        var length = Int(firstLength)
        if firstLength & 0x80 != 0 {
            let lengthBytes = Int(firstLength & 0x7f)
            guard (1...4).contains(lengthBytes), cursor + lengthBytes <= bytes.count else {
                throw BoardcastError.invalid("Invalid DER length")
            }
            length = 0
            for index in cursor..<(cursor + lengthBytes) { length = (length << 8) | Int(bytes[index]) }
            cursor += lengthBytes
        }
        guard length <= bytes.count - cursor else { throw BoardcastError.invalid("Truncated DER payload") }
        return (bytes[start], start..<(cursor + length), cursor..<(cursor + length))
    }

    func testCertificateSelfSignatureIsValidAndDetectsTampering() throws {
        let key = try temporaryKey()
        let certificate = try DeviceIdentity.selfSignedCertificate(privateKey: key, now: Date())
        let publicKey = try XCTUnwrap(SecKeyCopyPublicKey(key))
        try assertValidSelfSignatureAndDetectsTampering(certificate, publicKey: publicKey)
    }

    private func assertValidSelfSignatureAndDetectsTampering(
        _ certificate: SecCertificate, publicKey: SecKey, file: StaticString = #filePath, line: UInt = #line
    ) throws {
        let bytes = SecCertificateCopyData(certificate) as Data
        let certificateElement = try derElement(bytes, at: 0)
        XCTAssertEqual(certificateElement.tag, 0x30, file: file, line: line)
        XCTAssertEqual(certificateElement.encoded.upperBound, bytes.count, file: file, line: line)
        let tbs = try derElement(bytes, at: certificateElement.content.lowerBound)
        let algorithm = try derElement(bytes, at: tbs.encoded.upperBound)
        let signatureElement = try derElement(bytes, at: algorithm.encoded.upperBound)
        XCTAssertEqual(signatureElement.tag, 0x03, file: file, line: line)
        XCTAssertEqual(bytes[signatureElement.content.lowerBound], 0, file: file, line: line)
        let signature = bytes.subdata(in: (signatureElement.content.lowerBound + 1)..<signatureElement.content.upperBound)
        let message = bytes.subdata(in: tbs.encoded)
        XCTAssertTrue(SecKeyVerifySignature(
            publicKey, .ecdsaSignatureMessageX962SHA256, message as CFData, signature as CFData, nil
        ), file: file, line: line)
        var altered = message
        altered[altered.count - 1] ^= 1
        XCTAssertFalse(SecKeyVerifySignature(
            publicKey, .ecdsaSignatureMessageX962SHA256, altered as CFData, signature as CFData, nil
        ), file: file, line: line)
    }

    func testNativeSecurityAcceptsCertificateForBothTLSRoles() throws {
        let now = try date("2026-10-10T00:00:00Z")
        let certificate = try DeviceIdentity.selfSignedCertificate(privateKey: temporaryKey(), now: now)
        try assertAcceptedForBothTLSRoles(certificate, at: now)
    }

    func testWindowsIdentityStoreCertificateParsesPinsAndPassesBothTLSRolePolicies() throws {
        // Generated on Windows on 2026-10-10 by the real Boardcast.Core.IdentityStore
        // (Release/net10.0, .NET 10.0.12), using a disposable data directory.
        // Only public DER is embedded; the temporary PFX/private key was removed.
        // Windows identities have critical leaf constraints/digitalSignature and no EKU.
        let base64 =
            "MIIBPTCB5KADAgECAghHMIiSps124jAKBggqhkjOPQQDAjAUMRIwEAYDVQQDEwlCb2Fy" +
            "ZGNhc3QwHhcNMjYxMDA5MDIyNjAyWhcNMzYxMDEwMDIyNjAyWjAUMRIwEAYDVQQD" +
            "EwlCb2FyZGNhc3QwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAARIoHDKW6G46REItT" +
            "Hi2QeCVJ/B0Pi5DW55LJvyidE3EsBNFqyBfsOrOkqtcUv4qEcTKjAvZnhHCnJpV5T" +
            "/6Fo2oyAwHjAMBgNVHRMBAf8EAjAAMA4GA1UdDwEB/wQEAwIHgDAKBggqhkjOPQQDAg" +
            "NIADBFAiAG9hzSIvNMg1u0+UoPnf2EHzg/VIB+M6H3QpOlwxmDxwIhAIEHlwl+Bv" +
            "BN46+8XTUhruqLIfJjBbJw70DMkUcYLjca"
        let expectedFingerprint = "fd15c3baabb80b732aa67065d3ad078d68bd4fc6397a2933d25806e85fce4314"
        let bytes = try XCTUnwrap(Data(base64Encoded: base64))
        let certificate = try XCTUnwrap(SecCertificateCreateWithData(nil, bytes as CFData))
        XCTAssertEqual(SecCertificateCopyData(certificate) as Data, bytes)
        XCTAssertEqual(DeviceIdentity.fingerprint(certificate), expectedFingerprint)
        XCTAssertTrue(DeviceIdentity.acceptsPeerCertificate(certificate, expectedFingerprint: expectedFingerprint))
        XCTAssertFalse(DeviceIdentity.acceptsPeerCertificate(
            certificate, expectedFingerprint: String(repeating: "0", count: 64)
        ))

        let publicKey = try XCTUnwrap(SecCertificateCopyKey(certificate))
        let keyBytes = try XCTUnwrap(SecKeyCopyExternalRepresentation(publicKey, nil) as Data?)
        XCTAssertEqual(keyBytes.count, 65)
        XCTAssertEqual(keyBytes.first, 0x04)
        XCTAssertTrue(SecKeyIsAlgorithmSupported(publicKey, .verify, .ecdsaSignatureMessageX962SHA256))
        try assertValidSelfSignatureAndDetectsTampering(certificate, publicKey: publicKey)
        // Fixed within the fixture's validity window so future test runs do not expire it.
        try assertAcceptedForBothTLSRoles(certificate, at: date("2026-10-10T00:00:00Z"))
    }

    private func assertAcceptedForBothTLSRoles(
        _ certificate: SecCertificate, at now: Date, file: StaticString = #filePath, line: UInt = #line
    ) throws {
        for isServer in [false, true] {
            var optionalTrust: SecTrust?
            XCTAssertEqual(SecTrustCreateWithCertificates(
                [certificate] as CFArray, SecPolicyCreateSSL(isServer, nil), &optionalTrust
            ), errSecSuccess, file: file, line: line)
            let trust = try XCTUnwrap(optionalTrust, file: file, line: line)
            XCTAssertEqual(SecTrustSetAnchorCertificates(trust, [certificate] as CFArray), errSecSuccess, file: file, line: line)
            XCTAssertEqual(SecTrustSetAnchorCertificatesOnly(trust, true), errSecSuccess, file: file, line: line)
            XCTAssertEqual(SecTrustSetNetworkFetchAllowed(trust, false), errSecSuccess, file: file, line: line)
            XCTAssertEqual(SecTrustSetVerifyDate(trust, now as CFDate), errSecSuccess, file: file, line: line)
            var failure: CFError?
            XCTAssertTrue(SecTrustEvaluateWithError(trust, &failure),
                          "TLS role server=\(isServer): \(String(describing: failure))", file: file, line: line)
        }
    }
}
