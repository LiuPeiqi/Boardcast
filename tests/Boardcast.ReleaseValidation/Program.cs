using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Boardcast.Core;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: Boardcast.ReleaseValidation <extracted-release-directory> <report.json> <expected-version>");
    return 2;
}
string releaseRoot = Path.GetFullPath(args[0]);
string reportPath = Path.GetFullPath(args[1]);
string expectedVersion = args[2];
string validationRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "runtime-" + Guid.NewGuid().ToString("N")));
var report = new ValidationReport { StartedUtc = DateTimeOffset.UtcNow, ReleaseDirectory = releaseRoot };
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
var token = deadline.Token;
void Check(bool result, string description)
{
    if (!result) throw new InvalidDataException(description);
    report.Checks.Add(description);
    Console.WriteLine("PASS " + description);
}
try
{
    Check(OperatingSystem.IsWindows(), "Validation runs on Windows.");
    byte[] loadedCoreHash = SHA256.HashData(File.ReadAllBytes(typeof(AppRelease).Assembly.Location));
    byte[] releasedCoreHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(releaseRoot, "Boardcast.Core.dll")));
    Check(CryptographicOperations.FixedTimeEquals(loadedCoreHash, releasedCoreHash), "The loaded Core assembly exactly matches the distributed Core assembly.");
    report.CoreSha256 = Convert.ToHexStringLower(releasedCoreHash);
    report.ApplicationVersion = AppRelease.CurrentVersion;
    Check(AppRelease.CurrentVersion == expectedVersion, "The released Core version equals the requested release version.");
    report.Runtime = AppRelease.RuntimeIdentifier;
    var trust = ReleaseTrust.Load(releaseRoot);
    report.PublisherKeyId = trust.KeyId;
    using (var publisher = ECDsa.Create())
    {
        publisher.ImportFromPem(trust.PublicKeyPem);
        report.PublisherPublicKeySha256 = Convert.ToHexStringLower(SHA256.HashData(publisher.ExportSubjectPublicKeyInfo()));
    }
    var envelope = ReleaseVerifier.ReadEnvelope(Path.Combine(releaseRoot, "release-manifest.json"));
    var release = ReleaseVerifier.VerifyManifest(envelope, trust, AppRelease.RuntimeIdentifier, AppRelease.CurrentVersion, requireNewer: false);
    report.ReleaseVersion = release.Manifest.Version;
    report.PackageBytes = release.Manifest.PackageBytes;
    report.PackageSha256 = release.Manifest.PackageSha256;
    Check(release.Manifest.Version == AppRelease.CurrentVersion, "Signed release version equals the actual binary version.");
    report.Checks.Add("Publisher signature, release format, architecture and Windows requirements are valid.");
    await ReleaseVerifier.VerifyPackageAsync(Path.Combine(releaseRoot, "Boardcast-update.zip"), release, token);
    report.Checks.Add("The complete update ZIP and every signed entry pass the product verifier.");
    foreach (var item in release.Manifest.Files)
    {
        await ReleaseVerifier.VerifyInstalledFileAsync(releaseRoot, item, token);
        report.InstalledFilesVerified++;
    }
    Check(report.InstalledFilesVerified == release.Manifest.Files.Count, "Every installed file has the signed size and SHA-256.");
    var allowedPaths = release.Manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    allowedPaths.Add("release-manifest.json");
    allowedPaths.Add("Boardcast-update.zip");
    var actualPaths = Directory.EnumerateFiles(releaseRoot, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(releaseRoot, path).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Check(allowedPaths.SetEquals(actualPaths), "The distribution contains exactly the signed files, envelope and update ZIP.");
    Check(!Directory.EnumerateFileSystemEntries(releaseRoot, "*", SearchOption.AllDirectories)
        .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0), "The distribution contains no filesystem links.");
    Directory.CreateDirectory(validationRoot);
    using var serverIdentity = new IdentityStore(Path.Combine(validationRoot, "server-identity"));
    using var clientIdentity = new IdentityStore(Path.Combine(validationRoot, "client-identity"));
    using var service = new PeerUpdateService(serverIdentity, releaseRoot, Path.Combine(validationRoot, "service-data"));
    service.Diagnostic += message => { lock (report.Diagnostics) report.Diagnostics.Add(message); };
    report.CanUpdate = service.Status.CanUpdate;
    report.InitialUpdateState = service.Status.State;
    report.UpdatePort = service.Port;
    report.AvailableReleaseVersion = service.AvailableReleaseVersion;
    Check(report.CanUpdate && report.InitialUpdateState == "idle", "The actual release enables updates and starts idle.");
    Check(report.UpdatePort > 0 && report.AvailableReleaseVersion == report.ReleaseVersion, "The actual release advertises its own version on a usable update port.");
    using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
    Task serveTask = service.RunAsync(stop.Token);
    try
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", service.Port, token);
        using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && IdentityStore.GetFingerprint(certificate).Equals(serverIdentity.Fingerprint, StringComparison.OrdinalIgnoreCase));
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "Boardcast",
            ClientCertificates = new X509CertificateCollection { clientIdentity.Certificate },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, token);
        Check(tls.IsAuthenticated && tls.IsMutuallyAuthenticated, "A real loopback mTLS connection authenticates both temporary device identities.");
        await WireProtocol.WriteAsync(tls, new UpdateMessage { Type = "manifest" }, token);
        var reply = await WireProtocol.ReadAsync<UpdateMessage>(tls, token);
        Check(reply.Type == "manifest" && reply.Release is not null && reply.Chunks is not null, "The release serves a signed manifest and resumable chunk metadata over mTLS.");
        var served = ReleaseVerifier.VerifyManifest(reply.Release!, trust, AppRelease.RuntimeIdentifier, AppRelease.CurrentVersion, requireNewer: false);
        Check(served.Envelope.Payload == envelope.Payload && served.Envelope.Signature == envelope.Signature, "The served signed manifest is the exact distributed manifest.");
        var chunks = reply.Chunks!;
        ChunkTransfer.ValidateManifest(chunks, new TransferOffer
        {
            Entries = [new TransferEntry { RelativePath = "Boardcast-update.zip", Size = release.Manifest.PackageBytes }]
        });
        report.ChunkManifestId = chunks.Id;
        report.ChunkSize = chunks.ChunkSize;
        report.ChunkCount = chunks.Files[0].Hashes.Count;
        foreach (int chunkIndex in new[] { 0, report.ChunkCount - 1 }.Distinct())
        {
            int bytesExpected = (int)Math.Min(chunks.ChunkSize, release.Manifest.PackageBytes - (long)chunkIndex * chunks.ChunkSize);
            await WireProtocol.WriteAsync(tls, new UpdateMessage
            {
                Type = "package-chunk", PackageSha256 = release.Manifest.PackageSha256, Chunk = new ChunkRequest(0, chunkIndex)
            }, token);
            var chunkReply = await WireProtocol.ReadAsync<UpdateMessage>(tls, token);
            Check(chunkReply.Type == "package-chunk" && chunkReply.PackageBytes == bytesExpected, $"Chunk {chunkIndex} has the expected protocol response and byte count.");
            byte[] bytes = new byte[bytesExpected];
            await tls.ReadExactlyAsync(bytes, token);
            Check(ReleaseVerifier.HashMatches(SHA256.HashData(bytes), chunks.Files[0].Hashes[chunkIndex]), $"Chunk {chunkIndex} received over mTLS passes its SHA-256 check.");
            report.ChunksReceived.Add(chunkIndex);
            report.BytesReceived += bytes.Length;
        }
    }
    finally
    {
        stop.Cancel();
        try { await serveTask; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    report.Success = true;
}
catch (Exception ex)
{
    report.Success = false;
    report.Error = ex.ToString();
    Console.Error.WriteLine(ex.Message);
}
finally
{
    try
    {
        string expectedParent = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (Directory.Exists(validationRoot) && Path.GetDirectoryName(validationRoot) == expectedParent && Path.GetFileName(validationRoot).StartsWith("runtime-", StringComparison.Ordinal))
            Directory.Delete(validationRoot, recursive: true);
    }
    catch (Exception ex) { report.Diagnostics.Add("Temporary validation data cleanup: " + ex.Message); }
    report.CompletedUtc = DateTimeOffset.UtcNow;
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    Console.WriteLine("Report: " + reportPath);
}
return report.Success ? 0 : 1;

sealed record UpdateMessage
{
    public string Type { get; set; } = "";
    public SignedReleaseEnvelope? Release { get; set; }
    public ChunkManifest? Chunks { get; set; }
    public ChunkRequest? Chunk { get; set; }
    public string PackageSha256 { get; set; } = "";
    public long PackageBytes { get; set; }
}
sealed class ValidationReport
{
    public bool Success { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string ReleaseDirectory { get; set; } = "";
    public string ApplicationVersion { get; set; } = "";
    public string ReleaseVersion { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string CoreSha256 { get; set; } = "";
    public string PublisherKeyId { get; set; } = "";
    public string PublisherPublicKeySha256 { get; set; } = "";
    public long PackageBytes { get; set; }
    public string PackageSha256 { get; set; } = "";
    public int InstalledFilesVerified { get; set; }
    public bool CanUpdate { get; set; }
    public string InitialUpdateState { get; set; } = "";
    public int UpdatePort { get; set; }
    public string AvailableReleaseVersion { get; set; } = "";
    public string ChunkManifestId { get; set; } = "";
    public int ChunkSize { get; set; }
    public int ChunkCount { get; set; }
    public long BytesReceived { get; set; }
    public List<int> ChunksReceived { get; set; } = [];
    public List<string> Checks { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public string? Error { get; set; }
}

