import Foundation
import Security
import CryptoKit
import Network

/// The private key stays in the login Keychain; only its public key is exported.
final class DeviceIdentity {
    let identity: SecIdentity
    let fingerprint: String
    private static let label = "Boardcast LAN Identity v1"
    private static let tag = Data("local.boardcast.identity.v1".utf8)

    init() throws {
        let certificate = try Self.loadCertificate() ?? Self.createCertificate()
        var found: SecIdentity?
        let status = SecIdentityCreateWithCertificate(nil, certificate, &found)
        guard status == errSecSuccess, let found else {
            throw BoardcastError.invalid(L10n.format("钥匙串中找不到证书的私钥：%d", status))
        }
        identity = found
        fingerprint = Self.fingerprint(certificate)
    }

    static func fingerprint(_ certificate: SecCertificate) -> String {
        SHA256.hash(data: SecCertificateCopyData(certificate) as Data)
            .map { String(format: "%02x", $0) }.joined()
    }

    private static func loadCertificate() throws -> SecCertificate? {
        var item: CFTypeRef?
        let query: [String: Any] = [
            kSecClass as String: kSecClassCertificate,
            kSecAttrLabel as String: label,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess else {
            throw BoardcastError.invalid(L10n.format("无法读取钥匙串证书：%d", status))
        }
        guard let bytes = item as? Data,
              let certificate = SecCertificateCreateWithData(nil, bytes as CFData) else {
            throw BoardcastError.invalid(L10n.string("钥匙串证书类型或内容无效"))
        }
        return certificate
    }

    private static func createCertificate() throws -> SecCertificate {
        // A locked or unreadable Keychain must never cause the identity to be replaced.
        var existing: CFTypeRef?
        let query: [String: Any] = [
            kSecClass as String: kSecClassKey,
            kSecAttrApplicationTag as String: tag,
            kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
            kSecAttrKeyClass as String: kSecAttrKeyClassPrivate,
            kSecReturnRef as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        let status = SecItemCopyMatching(query as CFDictionary, &existing)
        let privateKey: SecKey
        if status == errSecSuccess {
            guard let existing else { throw BoardcastError.invalid(L10n.string("钥匙串密钥为空")) }
            privateKey = try keyReference(existing)
        } else if status == errSecItemNotFound {
            var failure: Unmanaged<CFError>?
            let attributes: [String: Any] = [
                kSecAttrKeyType as String: kSecAttrKeyTypeECSECPrimeRandom,
                kSecAttrKeySizeInBits as String: 256,
                kSecPrivateKeyAttrs as String: [
                    kSecAttrIsPermanent as String: true,
                    kSecAttrIsExtractable as String: false,
                    kSecAttrApplicationTag as String: tag,
                    kSecAttrLabel as String: label
                ]
            ]
            guard let generated = SecKeyCreateRandomKey(attributes as CFDictionary, &failure) else {
                throw BoardcastError.invalid(L10n.string("无法生成钥匙串密钥"))
            }
            privateKey = generated
        } else {
            throw BoardcastError.invalid(L10n.format("无法读取钥匙串密钥：%d", status))
        }

        let certificate = try selfSignedCertificate(privateKey: privateKey, now: Date())
        let add: [String: Any] = [
            kSecClass as String: kSecClassCertificate,
            kSecValueRef as String: certificate,
            kSecAttrLabel as String: label
        ]
        let addStatus = SecItemAdd(add as CFDictionary, nil)
        if addStatus == errSecDuplicateItem, let saved = try loadCertificate() { return saved }
        guard addStatus == errSecSuccess else {
            throw BoardcastError.invalid(L10n.format("无法保存证书：%d", addStatus))
        }
        return certificate
    }

    static func keyReference(_ item: CFTypeRef) throws -> SecKey {
        guard CFGetTypeID(item) == SecKeyGetTypeID() else {
            throw BoardcastError.invalid(L10n.string("钥匙串返回了非密钥对象"))
        }
        // Security exposes an opaque CF reference, so check its runtime type before bridging.
        return unsafeBitCast(item, to: SecKey.self)
    }

    /// A fixed RFC 5280 ECDSA P-256 leaf certificate for pinned mutual TLS.
    static func selfSignedCertificate(privateKey: SecKey, now: Date) throws -> SecCertificate {
        var failure: Unmanaged<CFError>?
        guard SecKeyIsAlgorithmSupported(privateKey, .sign, .ecdsaSignatureMessageX962SHA256),
              let publicKey = SecKeyCopyPublicKey(privateKey),
              let keyBytes = SecKeyCopyExternalRepresentation(publicKey, &failure) as Data?,
              keyBytes.count == 65, keyBytes.first == 0x04 else {
            throw BoardcastError.invalid(L10n.string("无法读取 P-256 公钥"))
        }
        let algorithm = DER.sequence(DER.oid([0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02]))
        let commonName = DER.sequence(DER.tag(0x31, DER.sequence(
            DER.oid([0x55, 0x04, 0x03]) + DER.tag(0x0c, Data("Boardcast".utf8))
        )))
        let validity = try DER.sequence(
            certificateTime(now.addingTimeInterval(-86400)) +
            certificateTime(now.addingTimeInterval(10 * 365 * 86400))
        )
        let spki = DER.sequence(
            DER.sequence(
                DER.oid([0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01]) +
                DER.oid([0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07])
            ) + DER.tag(0x03, Data([0]) + keyBytes)
        )
        let extensions = DER.tag(0xa3, DER.sequence(
            // Basic constraints: a leaf, never a certificate authority.
            DER.sequence(DER.oid([0x55, 0x1d, 0x13]) + DER.tag(0x01, Data([0xff])) +
                         DER.tag(0x04, DER.sequence(Data()))) +
            // ECDSA authentication uses digitalSignature, with seven unused bits.
            DER.sequence(DER.oid([0x55, 0x1d, 0x0f]) + DER.tag(0x01, Data([0xff])) +
                         DER.tag(0x04, DER.tag(0x03, Data([7, 0x80])))) +
            DER.sequence(DER.oid([0x55, 0x1d, 0x25]) + DER.tag(0x04, DER.sequence(
                DER.oid([0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x01]) +
                DER.oid([0x2b, 0x06, 0x01, 0x05, 0x05, 0x07, 0x03, 0x02])
            )))
        ))
        var serial = [UInt8](repeating: 0, count: 16)
        guard SecRandomCopyBytes(kSecRandomDefault, serial.count, &serial) == errSecSuccess else {
            throw BoardcastError.invalid(L10n.string("无法创建证书序号"))
        }
        // Positive, non-zero and minimal DER INTEGER, at most 20 octets.
        serial[0] = (serial[0] & 0x7f) | 1
        let tbs = DER.sequence(
            DER.tag(0xa0, DER.tag(0x02, Data([2]))) + DER.tag(0x02, Data(serial)) +
            algorithm + commonName + validity + commonName + spki + extensions
        )
        guard let signature = SecKeyCreateSignature(
            privateKey, .ecdsaSignatureMessageX962SHA256, tbs as CFData, &failure
        ) as Data? else {
            throw BoardcastError.invalid(L10n.string("无法签署本机证书"))
        }
        let bytes = DER.sequence(tbs + algorithm + DER.tag(0x03, Data([0]) + signature))
        guard let certificate = SecCertificateCreateWithData(nil, bytes as CFData) else {
            throw BoardcastError.invalid(L10n.string("生成的证书无效"))
        }
        return certificate
    }

    static func certificateTime(_ date: Date) throws -> Data {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 0) ?? calendar.timeZone
        let year = calendar.component(.year, from: date)
        guard (1950...9999).contains(year) else {
            throw BoardcastError.invalid(L10n.string("证书有效期年份不受支持"))
        }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = calendar
        formatter.timeZone = calendar.timeZone
        formatter.dateFormat = year < 2050 ? "yyMMddHHmmss'Z'" : "yyyyMMddHHmmss'Z'"
        return DER.tag(year < 2050 ? 0x17 : 0x18, Data(formatter.string(from: date).utf8))
    }

    static func isFingerprint(_ value: String) -> Bool {
        value.utf8.count == 64 && value.utf8.allSatisfy {
            (0x30...0x39).contains($0) || (0x41...0x46).contains($0) || (0x61...0x66).contains($0)
        }
    }

    static func acceptsPeerCertificate(_ certificate: SecCertificate?, expectedFingerprint: String?) -> Bool {
        guard let certificate else { return false }
        // Incoming identities become trusted only through the receive policy.
        guard let expectedFingerprint else { return true }
        guard isFingerprint(expectedFingerprint) else { return false }
        return expectedFingerprint.lowercased() == fingerprint(certificate)
    }

    func parameters(expectedFingerprint: String? = nil) throws -> NWParameters {
        if let expectedFingerprint, !Self.isFingerprint(expectedFingerprint) {
            throw BoardcastError.invalid(L10n.string("对方证书指纹格式无效"))
        }
        guard let localIdentity = sec_identity_create(identity) else {
            throw BoardcastError.invalid(L10n.string("无法创建 TLS 本机身份"))
        }
        let tls = NWProtocolTLS.Options()
        let options = tls.securityProtocolOptions
        let queue = DispatchQueue.global(qos: .utility)
        sec_protocol_options_set_min_tls_protocol_version(options, .TLSv12)
        sec_protocol_options_set_local_identity(options, localIdentity)
        sec_protocol_options_set_peer_authentication_required(options, true)
        // Every connection must provide a fresh certificate chain for peerFingerprint.
        sec_protocol_options_set_tls_resumption_enabled(options, false)
        sec_protocol_options_set_challenge_block(options, { _, complete in
            complete(localIdentity)
        }, queue)
        sec_protocol_options_set_verify_block(options, { _, trust, complete in
            let reference = sec_trust_copy_ref(trust).takeRetainedValue()
            // Pinning is local; never download CA intermediates while inspecting the chain.
            guard SecTrustSetNetworkFetchAllowed(reference, false) == errSecSuccess,
                  let chain = SecTrustCopyCertificateChain(reference) as? [SecCertificate] else {
                complete(false)
                return
            }
            complete(Self.acceptsPeerCertificate(chain.first, expectedFingerprint: expectedFingerprint))
        }, queue)

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
            throw BoardcastError.invalid(L10n.string("对方未提供 TLS 身份"))
        }
        var result: String?
        let available = sec_protocol_metadata_access_peer_certificate_chain(metadata.securityProtocolMetadata) { certificate in
            if result == nil {
                result = fingerprint(sec_certificate_copy_ref(certificate).takeRetainedValue())
            }
        }
        guard available, let result else { throw BoardcastError.invalid(L10n.string("对方证书链为空")) }
        return result
    }
}

private enum DER {
    static func tag(_ tag: UInt8, _ payload: Data) -> Data {
        let count = payload.count
        var length: [UInt8] = []
        if count < 128 {
            length = [UInt8(count)]
        } else {
            var remaining = count
            while remaining > 0 {
                length.insert(UInt8(remaining & 255), at: 0)
                remaining >>= 8
            }
            length.insert(0x80 | UInt8(length.count), at: 0)
        }
        return Data([tag] + length) + payload
    }

    static func sequence(_ payload: Data) -> Data { tag(0x30, payload) }
    static func oid(_ bytes: [UInt8]) -> Data { tag(0x06, Data(bytes)) }
}
