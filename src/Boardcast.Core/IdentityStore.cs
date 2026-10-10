using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace Boardcast.Core;
public sealed class IdentityStore : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string DataDirectory { get; }
    public string Fingerprint => GetFingerprint(Certificate);
    public IdentityStore(string dataDirectory)
    {
        DataDirectory = PathSafety.ValidateReceiveRoot(dataDirectory);
        dataDirectory = DataDirectory;
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "identity.pfx");
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=Boardcast", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            // TLS server policy rejects a leaf without serverAuth, and rejects one valid for more than 825 days.
            // clientAuth is required as well once EKU is present, because the certificate is used in both roles.
            var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            using var certificate = request.CreateSelfSigned(notBefore, notBefore.AddDays(824));
            using var file = PrivateFile.Create(path);
            file.Write(certificate.Export(X509ContentType.Pfx));
        }
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        if (!Certificate.HasPrivateKey) throw new InvalidDataException("Identity has no private key.");
    }
    public static string GetFingerprint(X509Certificate certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
    public void Dispose() => Certificate.Dispose();
}
