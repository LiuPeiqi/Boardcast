import Foundation
import Security
import CryptoKit
import Network

/// The private key never leaves the login Keychain. The certificate is public.
final class DeviceIdentity {
    let identity: SecIdentity
    let fingerprint: String
    private static let label = "Boardcast LAN Identity v1"
    private static let tag = Data("local.boardcast.identity.v1".utf8)

    init() throws {
        var item: CFTypeRef?
        let query: [String: Any] = [
            kSecClass as String: kSecClassCertificate,
            kSecAttrLabel as String: Self.label,
            kSecReturnRef as String: true
        ]
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        let certificate: SecCertificate
        if status == errSecSuccess, let item {
            certificate = (item as! SecCertificate)
        } else if status == errSecItemNotFound {
            certificate = try Self.createCertificate()
        } else { throw BoardcastError.invalid("无法读取钥匙串身份：\(status)") }
        var found: SecIdentity?
        let identityStatus = SecIdentityCreateWithCertificate(nil, certificate, &found)
        guard identityStatus == errSecSuccess, let found else {
            throw BoardcastError.invalid("钥匙串中找不到证书的私钥：\(identityStatus)")
        }
        identity = found
        fingerprint = Self.fingerprint(certificate)
    }

    static func fingerprint(_ certificate: SecCertificate) -> String {
        SHA256.hash(data: SecCertificateCopyData(certificate) as Data).map { String(format: "%02x", $0) }.joined()
    }

    private static func createCertificate() throws -> SecCertificate {
        var failure: Unmanaged<CFError>?
        let attributes: [String: Any] = [
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeySizeInBits as String: 256,
            kSecPrivateKeyAttrs as String: [
                kSecAttrIsPermanent as String: true,
                kSecAttrApplicationTag as String: tag,
                kSecAttrLabel as String: label
            ]
        ]
        // Reuse a surviving key after an interrupted first-run certificate creation.
        var existing: CFTypeRef?
        let keyQuery: [String: Any] = [
            kSecClass as String: kSecClassKey, kSecAttrApplicationTag as String: tag,
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom, kSecReturnRef as String: true
        ]
        let privateKey: SecKey
        if SecItemCopyMatching(keyQuery as CFDictionary, &existing) == errSecSuccess, let existing {
            privateKey = (existing as! SecKey)
        } else {
            guard let generated = SecKeyCreateRandomKey(attributes as CFDictionary, &failure) else {
                throw BoardcastError.invalid("无法生成钥匙串密钥")
            }
            privateKey = generated
        }
        guard let publicKey = SecKeyCopyPublicKey(privateKey),
              let keyBytes = SecKeyCopyExternalRepresentation(publicKey, &failure) as Data? else {
            throw BoardcastError.invalid("无法读取公钥")
        }
        // DER X.509 construction is limited to this fixed ECDSA P-256 certificate template.
        let algorithm = DER.sequence(DER.oid([0x2a,0x86,0x48,0xce,0x3d,0x04,0x03,0x02]))
        let commonName = DER.sequence(DER.tag(0x31, DER.sequence(DER.oid([0x55,0x04,0x03]) + DER.tag(0x0c, Data("Boardcast".utf8)))))
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = TimeZone(secondsFromGMT: 0)
        formatter.dateFormat = "yyyyMMddHHmmss'Z'"
        let validity = DER.sequence(
            DER.tag(0x18, Data(formatter.string(from: Date().addingTimeInterval(-86400)).utf8)) +
            DER.tag(0x18, Data(formatter.string(from: Date().addingTimeInterval(10 * 365 * 86400)).utf8))
        )
        let spki = DER.sequence(
            DER.sequence(DER.oid([0x2a,0x86,0x48,0xce,0x3d,0x02,0x01]) + DER.oid([0x2a,0x86,0x48,0xce,0x3d,0x03,0x01,0x07])) +
            DER.tag(0x03, Data([0]) + keyBytes)
        )
        var serial = [UInt8](repeating: 0, count: 16)
        guard SecRandomCopyBytes(kSecRandomDefault, serial.count, &serial) == errSecSuccess else { throw BoardcastError.invalid("无法创建证书序号") }
        serial[0] &= 0x7f
        serial[0] |= 1
        let tbs = DER.sequence(DER.tag(0xa0, DER.tag(0x02, Data([2]))) + DER.tag(0x02, Data(serial)) + algorithm + commonName + validity + commonName + spki)
        guard let signature = SecKeyCreateSignature(privateKey, .ecdsaSignatureMessageX962SHA256, tbs as CFData, &failure) as Data? else {
            throw BoardcastError.invalid("无法签署本机证书")
        }
        let bytes = DER.sequence(tbs + algorithm + DER.tag(0x03, Data([0]) + signature))
        guard let certificate = SecCertificateCreateWithData(nil, bytes as CFData) else { throw BoardcastError.invalid("生成的证书无效") }
        let add: [String: Any] = [kSecClass as String: kSecClassCertificate, kSecValueRef as String: certificate, kSecAttrLabel as String: label]
        let status = SecItemAdd(add as CFDictionary, nil)
        guard status == errSecSuccess || status == errSecDuplicateItem else { throw BoardcastError.invalid("无法保存证书：\(status)") }
        return certificate
    }

    func parameters(expectedFingerprint: String? = nil) -> NWParameters {
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_local_identity(tls.securityProtocolOptions, sec_identity_create(identity)!)
        sec_protocol_options_set_peer_authentication_required(tls.securityProtocolOptions, true)
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
            let reference = sec_trust_copy_ref(trust).takeRetainedValue()
            guard let certificate = SecTrustGetCertificateAtIndex(reference, 0) else { complete(false); return }
            // Self-signed identities are verified by certificate pin, then local receive policy.
            let fingerprint = Self.fingerprint(certificate)
            complete(expectedFingerprint.map { $0.caseInsensitiveCompare(fingerprint) == .orderedSame } ?? true)
        }, DispatchQueue.global(qos: .utility))
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: tls, tcp: tcp)
        parameters.includePeerToPeer = false
        parameters.prohibitExpensivePaths = false
        parameters.prohibitedInterfaceTypes = [.cellular]
        return parameters
    }

    static func peerFingerprint(_ connection: NWConnection) throws -> String {
        guard let metadata = connection.metadata(definition: NWProtocolTLS.definition) as? NWProtocolTLS.Metadata else {
            throw BoardcastError.invalid("对方未提供 TLS 身份")
        }
        var result: String?
        _ = sec_protocol_metadata_access_peer_certificate_chain(metadata.securityProtocolMetadata) { certificate in
            if result == nil { result = fingerprint(sec_certificate_copy_ref(certificate).takeRetainedValue()) }
        }
        guard let result else { throw BoardcastError.invalid("对方证书链为空") }
        return result
    }

}

private enum DER {
    static func tag(_ tag: UInt8, _ payload: Data) -> Data {
        let count = payload.count
        var length: [UInt8] = []
        if count < 128 { length = [UInt8(count)] }
        else {
            var remaining = count
            while remaining > 0 { length.insert(UInt8(remaining & 255), at: 0); remaining >>= 8 }
            length.insert(0x80 | UInt8(length.count), at: 0)
        }
        return Data([tag] + length) + payload
    }
    static func sequence(_ payload: Data) -> Data { tag(0x30, payload) }
    static func oid(_ bytes: [UInt8]) -> Data { tag(0x06, Data(bytes)) }
}
