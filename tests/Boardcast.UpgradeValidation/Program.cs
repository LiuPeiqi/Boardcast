using System.Security.Cryptography;
using System.Text.Json;
using Boardcast.Core;

if (args.Length != 6 || args[0] is not ("server" or "client"))
{
    Console.Error.WriteLine("Usage: UpgradeValidation <server|client> <own-release> <other-release> <isolated-run-root> <expected-version> <report.json>");
    return 2;
}
string mode = args[0], ownRoot = Path.GetFullPath(args[1]), otherRoot = Path.GetFullPath(args[2]);
string runRoot = Path.GetFullPath(args[3]), expectedVersion = args[4], reportPath = Path.GetFullPath(args[5]);
Directory.CreateDirectory(runRoot);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
var report = new ValidationReport { Mode = mode, StartedUtc = DateTimeOffset.UtcNow, OwnRelease = ownRoot, OtherRelease = otherRoot };
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
void Check(bool result, string description)
{
    if (!result) throw new InvalidDataException(description);
    report.Checks.Add(description);
    Console.WriteLine("PASS " + description);
}
try
{
    string loadedCore = typeof(AppRelease).Assembly.Location;
    report.LoadedCoreSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(loadedCore)));
    Check(report.LoadedCoreSha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(ownRoot, "Boardcast.Core.dll")))), "Loaded Core exactly matches the selected released binary.");
    report.AppVersion = AppRelease.CurrentVersion;
    Check(report.AppVersion == expectedVersion, "The process is running the expected released Core version " + expectedVersion + ".");
    var ownTrust = ReleaseTrust.Load(ownRoot);
    var otherTrust = ReleaseTrust.Load(otherRoot);
    ReleaseVerifier.RequireSameTrust(ownTrust, otherTrust);
    report.Checks.Add("Old and new releases have the same publisher key and key ID.");
    report.KeyId = ownTrust.KeyId;
    using (var key = ECDsa.Create())
    {
        key.ImportFromPem(ownTrust.PublicKeyPem);
        report.PublicKeySha256 = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    }
    using var identity = new IdentityStore(Path.Combine(runRoot, mode + "-identity"));
    string data = Path.Combine(runRoot, mode + "-data");
    using var service = new PeerUpdateService(identity, ownRoot, data);
    service.Diagnostic += message => { lock (report.Diagnostics) report.Diagnostics.Add(message); };
    Check(service.Status.CanUpdate && service.Status.State == "idle", "The selected release enables updates and starts idle.");
    if (mode == "server")
    {
        var endpoint = new PeerInfo { Name = "Isolated upgrade acceptance server", Address = "127.0.0.1", Fingerprint = identity.Fingerprint,
            Runtime = AppRelease.RuntimeIdentifier, AppVersion = AppRelease.CurrentVersion, UpdateVersion = service.AvailableReleaseVersion, UpdatePort = service.Port };
        report.UpdateVersion = endpoint.UpdateVersion;
        Check(endpoint.UpdateVersion == expectedVersion && endpoint.UpdatePort > 0, "The new release advertises its correct version and an active update port.");
        Task serve = service.RunAsync(deadline.Token);
        string endpointPath = Path.Combine(runRoot, "endpoint.json");
        File.WriteAllText(endpointPath + ".tmp", JsonSerializer.Serialize(endpoint, json));
        File.Move(endpointPath + ".tmp", endpointPath);
        Console.WriteLine("SERVER_READY");
        try
        {
            while (!File.Exists(Path.Combine(runRoot, "stop")))
            {
                if (serve.IsCompleted) await serve;
                await Task.Delay(100, deadline.Token);
            }
            report.Success = true;
        }
        finally
        {
            deadline.Cancel();
            try { await serve; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }
    else
    {
        var targetEnvelope = ReleaseVerifier.ReadEnvelope(Path.Combine(otherRoot, "release-manifest.json"));
        var target = ReleaseVerifier.VerifyManifest(targetEnvelope, ownTrust, AppRelease.RuntimeIdentifier, AppRelease.CurrentVersion);
        report.UpdateVersion = target.Manifest.Version;
        report.PackageBytes = target.Manifest.PackageBytes;
        report.PackageSha256 = target.Manifest.PackageSha256;
        report.Checks.Add("The actual old Core accepts the new release signature, platform and higher version under its existing publisher trust.");
        var peer = JsonSerializer.Deserialize<PeerInfo>(File.ReadAllText(Path.Combine(runRoot, "endpoint.json")), json) ?? throw new InvalidDataException("Missing endpoint.");
        Check(peer.Address == "127.0.0.1" && peer.UpdateVersion == target.Manifest.Version, "The isolated source endpoint matches the target release.");
        await service.DownloadAsync(peer, deadline.Token);
        var status = service.Status;
        report.FinalState = status.State;
        report.BytesReceived = status.BytesReceived;
        report.ReusedBytes = status.ReusedBytes;
        report.SourceCount = status.SourceCount;
        Check(status.State == "ready" && status.Version == target.Manifest.Version && status.BytesReceived == target.Manifest.PackageBytes,
            "The unmodified old release downloaded the entire new release through real mTLS and reached ready.");
        Check(status.ReusedBytes == 0, "The first isolated download fetched all package bytes rather than reusing a prior cache.");
        string package = Directory.GetFiles(Path.Combine(data, "updates"), "Boardcast-update.zip", SearchOption.AllDirectories).Single();
        await ReleaseVerifier.VerifyPackageAsync(package, target, deadline.Token);
        report.Checks.Add("The downloaded package and every signed payload entry independently pass the old release verifier.");
        Check(Directory.GetDirectories(Path.Combine(data, "updates"), "helper-*").Length == 1, "The old release prepared one verified updater helper snapshot.");
        service.Dispose();
        using var restored = new PeerUpdateService(identity, ownRoot, data);
        await restored.RestoreAsync(deadline.Token);
        Check(restored.Status.State == "ready" && restored.Status.Version == target.Manifest.Version,
            "A fresh old release service restores the fully verified pending update to ready.");
        report.Success = true;
    }
}
catch (Exception ex)
{
    report.Success = false;
    report.Error = ex.ToString();
    Console.Error.WriteLine(ex.Message);
}
finally
{
    report.CompletedUtc = DateTimeOffset.UtcNow;
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, json));
    Console.WriteLine("Report: " + reportPath);
}
return report.Success ? 0 : 1;

sealed class ValidationReport
{
    public string Mode { get; set; } = "";
    public bool Success { get; set; }
    public DateTimeOffset StartedUtc { get; set; }
    public DateTimeOffset CompletedUtc { get; set; }
    public string OwnRelease { get; set; } = "";
    public string OtherRelease { get; set; } = "";
    public string LoadedCoreSha256 { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string UpdateVersion { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string PublicKeySha256 { get; set; } = "";
    public string FinalState { get; set; } = "";
    public long PackageBytes { get; set; }
    public string PackageSha256 { get; set; } = "";
    public long BytesReceived { get; set; }
    public long ReusedBytes { get; set; }
    public int SourceCount { get; set; }
    public List<string> Checks { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public string? Error { get; set; }
}

