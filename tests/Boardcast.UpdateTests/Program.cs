using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text.Json;
using Boardcast.Core;

if (args.Length == 2 && args[0] == "--verify-release")
{
    string directory = Path.GetFullPath(args[1]);
    var signed = ReleaseVerifier.ReadEnvelope(Path.Combine(directory, "release-manifest.json"));
    var release = ReleaseVerifier.VerifyManifest(signed, ReleaseTrust.Load(directory), AppRelease.RuntimeIdentifier, "0.0.0");
    await ReleaseVerifier.VerifyPackageAsync(Path.Combine(directory, "Boardcast-update.zip"), release);
    Console.WriteLine("Release signing script output verifies successfully.");
    return;
}
int checks = 0;
void Check(bool result, string label) { if (!result) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or CryptographicException) { Check(true, label); return; } throw new Exception("Accepted: " + label); }
async Task RejectAsync(Func<Task> action, string label) { try { await action(); } catch (Exception ex) when (ex is InvalidDataException or CryptographicException or System.Security.Authentication.AuthenticationException or IOException) { Check(true, label); return; } throw new Exception("Accepted: " + label); }
string root = Path.Combine(Path.GetTempPath(), "Boardcast.UpdateTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var trust = new ReleaseTrust { KeyId = "tests-only", PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() };
SignedReleaseEnvelope Sign(ReleaseManifest manifest)
{
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, BoardcastJson.Options);
    return new() { KeyId = trust.KeyId, Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
}
VerifiedRelease CreateRelease(string directory, string version, int payloadBytes = 8 * 1024 * 1024 + 7)
{
    Directory.CreateDirectory(directory);
    var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Boardcast.exe"] = [1, 2, 3], ["Boardcast.Daemon.exe"] = [4, 5, 6], ["Boardcast.Updater.exe"] = [7, 8, 9],
        ["release-trust.json"] = JsonSerializer.SerializeToUtf8Bytes(trust, BoardcastJson.Options),
        ["assets/中文.bin"] = RandomNumberGenerator.GetBytes(payloadBytes)
    };
    string packagePath = Path.Combine(directory, "Boardcast-update.zip");
    using (var zip = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        foreach (var (path, bytes) in files)
        {
            var entry = zip.CreateEntry(path); using var stream = entry.Open(); stream.Write(bytes);
            string localPath = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(localPath)!); File.WriteAllBytes(localPath, bytes);
        }
    var manifest = new ReleaseManifest { Version = version, Runtime = AppRelease.RuntimeIdentifier, MinimumWindowsBuild = 0, PackageBytes = new FileInfo(packagePath).Length, PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packagePath))), Files = files.Select(p => new ReleaseFile { Path = p.Key, Bytes = p.Value.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(p.Value)) }).ToList() };
    var signed = Sign(manifest);
    File.WriteAllBytes(Path.Combine(directory, "release-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(signed, BoardcastJson.Options));
    return new(signed, manifest);
}
try
{
    var ordered = new[] { "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.10.0", "2.0.0" };
    for (int i = 0; i + 1 < ordered.Length; i++) Check(ReleaseVersion.Parse(ordered[i]).CompareTo(ReleaseVersion.Parse(ordered[i + 1])) < 0, "SemVer precedence " + ordered[i]);
    Check(ReleaseVersion.Parse("1.0.0+abc").CompareTo(ReleaseVersion.Parse("1.0.0+other")) == 0, "Build metadata does not change precedence");
    Check(ReleaseVersion.Parse("1.0.0-99999999999999999999").CompareTo(ReleaseVersion.Parse("1.0.0-100000000000000000000")) < 0, "Numeric prerelease does not overflow");
    foreach (string invalid in new[] { "1.0", "01.2.3", "1.0.0-01", "1.0.0-", "v1.0.0", "1.0.0\n" }) Check(!ReleaseVersion.TryParse(invalid, out _), "Malformed SemVer refused " + invalid);
    string runtime = AppRelease.RuntimeIdentifier;
    PeerInfo Peer(string name, string version, string? rid = null) => new() { Name = name, Fingerprint = name.PadRight(64, '0'), Runtime = rid ?? runtime, UpdateVersion = version, UpdatePort = 1234 };
    var peers = new[] { Peer("b", "0.4.0"), Peer("a", "0.4.0"), Peer("c", "0.3.0"), Peer("d", "0.9.0", "osx-arm64"), Peer("e", "9.0.0-beta") };
    Check(PeerUpdateCatalog.Recommend(peers, "0.2.0", runtime)?.Name == "a", "One latest compatible stable bubble with deterministic duplicate selection");
    Check(PeerUpdateCatalog.Recommend(peers, "0.2.0", runtime, peers[2].Fingerprint)?.Name == "c", "Explicit peer pin overrides default newest recommendation");
    Check(PeerUpdateCatalog.Recommend(peers, "0.4.0", runtime) is null, "No same-version, incompatible, or preview upgrade recommendation");
    string newRoot = Path.Combine(root, "new"), oldRoot = Path.Combine(root, "old");
    var target = CreateRelease(newRoot, "0.2.1", 72 * 1024 * 1024 + 7);
    CreateRelease(oldRoot, "0.2.0");
    var verified = ReleaseVerifier.VerifyManifest(target.Envelope, trust, runtime, "0.2.0");
    await ReleaseVerifier.VerifyPackageAsync(Path.Combine(newRoot, "Boardcast-update.zip"), verified);
    Check(true, "Trusted manifest and complete package verify");
    Reject(() => ReleaseVerifier.VerifyManifest(target.Envelope with { Payload = null! }, trust, runtime, "0.2.0"), "Null publisher metadata is refused without crashing the daemon");
    Reject(() => ReleaseVerifier.VerifyManifest(target.Envelope with { Signature = Convert.ToBase64String(new byte[64]) }, trust, runtime, "0.2.0"), "Invalid publisher signature is refused");
    using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256)) Reject(() => ReleaseVerifier.VerifyManifest(target.Envelope, trust with { PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() }, runtime, "0.2.0"), "A peer identity or unrelated public key is not a publisher trust root");
    Reject(() => ReleaseVerifier.VerifyManifest(Sign(target.Manifest with { Version = "0.1.9" }), trust, runtime, "0.2.0"), "Signed downgrade is refused");
    Reject(() => ReleaseVerifier.VerifyManifest(Sign(target.Manifest with { Runtime = "osx-arm64" }), trust, runtime, "0.2.0"), "Signed incompatible architecture is refused");
    Reject(() => ReleaseVerifier.VerifyManifest(Sign(target.Manifest with { Files = target.Manifest.Files.Concat([new ReleaseFile { Path = "../escape", Bytes = 1, Sha256 = new string('a', 64) }]).ToList() }), trust, runtime, "0.2.0"), "Signed path traversal is refused independently of required files");
    Reject(() => ReleaseVerifier.VerifyManifest(Sign(target.Manifest with { Files = target.Manifest.Files.Concat([target.Manifest.Files[0] with { Path = "BOARDCAST.EXE" }]).ToList() }), trust, runtime, "0.2.0"), "Case-insensitive filename collision is refused");
    string extracted = Path.Combine(root, "extract"); await ReleaseVerifier.ExtractVerifiedAsync(Path.Combine(newRoot, "Boardcast-update.zip"), verified, extracted);
    Check(File.ReadAllBytes(Path.Combine(extracted, "assets", "中文.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(newRoot, "assets", "中文.bin"))), "Verified extraction preserves files and Unicode paths");
    await RejectAsync(() => ReleaseVerifier.ExtractVerifiedAsync(Path.Combine(newRoot, "Boardcast-update.zip"), verified, extracted), "Existing staging files cannot be overwritten");
    string altered = Path.Combine(root, "altered.zip"); File.Copy(Path.Combine(newRoot, "Boardcast-update.zip"), altered);
    using (var stream = File.Open(altered, FileMode.Open, FileAccess.ReadWrite))
    {
        stream.Position = 100; int previous = stream.ReadByte();
        stream.Position = 100; stream.WriteByte((byte)(previous ^ 0xff));
    }
    await RejectAsync(() => ReleaseVerifier.VerifyPackageAsync(altered, verified), "Tampered ZIP hash is refused");
    string extra = Path.Combine(root, "extra.zip"); File.Copy(Path.Combine(newRoot, "Boardcast-update.zip"), extra);
    using (var zip = ZipFile.Open(extra, ZipArchiveMode.Update)) { using var entry = zip.CreateEntry("../outside.txt").Open(); entry.WriteByte(42); }
    var extraManifest = target.Manifest with { PackageBytes = new FileInfo(extra).Length, PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(extra))) };
    await RejectAsync(() => ReleaseVerifier.VerifyPackageAsync(extra, ReleaseVerifier.VerifyManifest(Sign(extraManifest), trust, runtime, "0.2.0")), "Even signed ZIP cannot carry unlisted traversal entries");
    string symbolic = Path.Combine(root, "symbolic.zip"); File.Copy(Path.Combine(newRoot, "Boardcast-update.zip"), symbolic);
    using (var zip = ZipFile.Open(symbolic, ZipArchiveMode.Update)) zip.GetEntry("Boardcast.exe")!.ExternalAttributes = unchecked((int)0xa1ff0000);
    var symbolicManifest = target.Manifest with { PackageBytes = new FileInfo(symbolic).Length, PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(symbolic))) };
    await RejectAsync(() => ReleaseVerifier.VerifyPackageAsync(symbolic, ReleaseVerifier.VerifyManifest(Sign(symbolicManifest), trust, runtime, "0.2.0")), "ZIP symbolic links are refused");
    using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256)) Reject(() => ReleaseVerifier.RequireSameTrust(trust, trust with { PublicKeyPem = other.ExportSubjectPublicKeyInfoPem() }), "Update cannot replace publisher trust root");
    string planData = Path.Combine(root, "plan-data");
    UpdateInstallPlan ValidPlan() => new()
    {
        InstallRoot = oldRoot,
        StagedRoot = Path.Combine(root, ".Boardcast-update-" + Guid.NewGuid().ToString("N")),
        BackupRoot = Path.Combine(root, ".Boardcast-backup-" + Guid.NewGuid().ToString("N")),
        DownloadRoot = Path.Combine(planData, "updates", "download-" + Guid.NewGuid().ToString("N")),
        DataDirectory = planData, CurrentVersion = "0.2.0", Version = "0.2.1",
        TrustSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(oldRoot, "release-trust.json")))),
        Processes = [new() { Id = 1, StartTimeUtcTicks = 1, ExecutablePath = Path.Combine(oldRoot, "Boardcast.exe") }, new() { Id = 2, StartTimeUtcTicks = 1, ExecutablePath = Path.Combine(oldRoot, "Boardcast.Daemon.exe") }],
        RestartArguments = ["--data-dir", planData]
    };
    ValidPlan().Validate();
    Check(true, "Complete install plan baseline passes before single-field negative cases");
    Reject(() => (ValidPlan() with { TrustSha256 = null! }).Validate(), "Null install plan trust hash is explicitly refused");
    Reject(() => (ValidPlan() with { TrustSha256 = new string('g', 64) }).Validate(), "Non-hex install plan trust hash is refused");
    Reject(() => (ValidPlan() with { Processes = null! }).Validate(), "Null install process list is explicitly refused");
    Reject(() => (ValidPlan() with { Processes = [null!, null!] }).Validate(), "Null install process identities are explicitly refused");
    Reject(() => (ValidPlan() with { Processes = [new() { Id = 1, StartTimeUtcTicks = 1, ExecutablePath = Path.Combine(oldRoot, "Boardcast.exe") }, new() { Id = 1, StartTimeUtcTicks = 1, ExecutablePath = Path.Combine(oldRoot, "Boardcast.Daemon.exe") }] }).Validate(), "Duplicate install process IDs are refused");
    Reject(() => (ValidPlan() with { RestartArguments = null! }).Validate(), "Null restart argument list is explicitly refused");
    Reject(() => (ValidPlan() with { RestartArguments = ["--unexpected", planData] }).Validate(), "Restart command outside the allowed data-directory option is refused");
    Reject(() => (ValidPlan() with { RestartArguments = ["--data-dir", Path.Combine(root, "other-data")] }).Validate(), "Restart cannot redirect the configured data directory");
    Reject(() => (ValidPlan() with { RestartArguments = ["--data-dir", planData, "--extra"] }).Validate(), "Additional restart arguments are refused");
    Reject(() => (ValidPlan() with { StagedRoot = Path.Combine(root, "outside", ".Boardcast-update-" + Guid.NewGuid().ToString("N")) }).Validate(), "Install plan staging directory must stay beside the install root");
    Reject(() => (ValidPlan() with { DownloadRoot = Path.Combine(root, "outside", "download-" + Guid.NewGuid().ToString("N")) }).Validate(), "Install plan download directory must stay under configured updates");
    Reject(() => (ValidPlan() with { Version = "0.2.0" }).Validate(), "Install plan cannot switch to the same version");
    string install = Path.Combine(root, "install"), stage = Path.Combine(root, ".Boardcast-update-" + Guid.NewGuid().ToString("N")), backup = Path.Combine(root, ".Boardcast-backup-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(install); Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(install, "version"), "old"); File.WriteAllText(Path.Combine(stage, "version"), "new");
    UpdateDirectorySwap.Apply(install, stage, backup);
    Check(File.ReadAllText(Path.Combine(install, "version")) == "new" && File.ReadAllText(Path.Combine(backup, "version")) == "old", "Atomic directory switch keeps recoverable previous release");
    string failed = UpdateDirectorySwap.Rollback(install, backup);
    Check(File.ReadAllText(Path.Combine(install, "version")) == "old" && File.ReadAllText(Path.Combine(failed, "version")) == "new", "Rollback restores previous release and retains failed release");
    Reject(() => UpdateInstallPlan.ValidateOwnedDirectory(root, root, ".Boardcast-update-"), "Cleanup refuses unowned parent directory");
    if (OperatingSystem.IsWindows())
    {
        using var senderIdentity = new IdentityStore(Path.Combine(root, "sender-id"));
        using var mirrorIdentity = new IdentityStore(Path.Combine(root, "mirror-id"));
        using var alternateIdentity = new IdentityStore(Path.Combine(root, "alternate-id"));
        string alternateRoot = Path.Combine(root, "alternate-same-version");
        CreateRelease(alternateRoot, "0.2.1");
        using var receiverIdentity = new IdentityStore(Path.Combine(root, "receiver-id"));
        using var server = new PeerUpdateService(senderIdentity, newRoot, Path.Combine(root, "server-data"), "0.2.1");
        using var mirror = new PeerUpdateService(mirrorIdentity, newRoot, Path.Combine(root, "mirror-data"), "0.2.1");
        using var alternate = new PeerUpdateService(alternateIdentity, alternateRoot, Path.Combine(root, "alternate-data"), "0.2.1");
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token); var mirrorTask = mirror.RunAsync(stop.Token); var alternateTask = alternate.RunAsync(stop.Token);
        var peer = new PeerInfo { Name = "newer peer", Address = "127.0.0.1", Fingerprint = senderIdentity.Fingerprint, Runtime = runtime, UpdateVersion = "0.2.1", UpdatePort = server.Port };
        var mirrorPeer = peer with { Name = "mirror peer", Fingerprint = mirrorIdentity.Fingerprint, UpdatePort = mirror.Port };
        var alternatePeer = peer with { Name = "different package with the same version", Fingerprint = alternateIdentity.Fingerprint, UpdatePort = alternate.Port };
        string clientData = Path.Combine(root, "client-data"), updates = Path.Combine(clientData, "updates");
        try
        {
            using (var client = new PeerUpdateService(receiverIdentity, oldRoot, clientData, "0.2.0"))
            {
                await client.DownloadAsync(peer, [peer, mirrorPeer, alternatePeer], CancellationToken.None);
                Check(client.Status.State == "ready" && client.Status.Version == "0.2.1" && client.Status.BytesReceived == target.Manifest.PackageBytes,
                    "mTLS peer download verifies publisher, package and every file before becoming ready");
                Check(client.Status.SourceCount is >= 1 and <= 2, "Update transfer admits extra matching sources only when useful and never admits a different signed package");
                string downloadedPackage = Directory.GetFiles(updates, "Boardcast-update.zip", SearchOption.AllDirectories).Single();
                DateTime packageWrite = File.GetLastWriteTimeUtc(downloadedPackage);
                await client.DownloadAsync(peer, [peer, mirrorPeer, alternatePeer], CancellationToken.None);
                Check(Directory.GetDirectories(updates, "download-*").Length == 1 && Directory.GetDirectories(updates, "helper-*").Length == 1 && File.GetLastWriteTimeUtc(downloadedPackage) == packageWrite,
                    "Selecting an already prepared update reuses its package and keeps a single installer snapshot");
                Check(client.Status.ReusedBytes == target.Manifest.PackageBytes, "Reselecting the same update reuses every verified chunk");
                await RejectAsync(() => client.DownloadAsync(peer with { Fingerprint = new string('f', 64), UpdateVersion = "0.2.2" }, CancellationToken.None),
                    "Selecting an unauthenticated replacement fails before replacing the ready update");
                Check(client.Status.State == "ready" && client.Status.Version == "0.2.1" && client.Status.BytesReceived == target.Manifest.PackageBytes && File.Exists(downloadedPackage),
                    "A failed replacement selection preserves the previously verified installable update");
                client.SetOnline(false);
                await RejectAsync(() => client.DownloadAsync(peer, CancellationToken.None), "Offline mode refuses update download before connection");
            }
            using var unauthenticated = new PeerUpdateService(receiverIdentity, oldRoot, Path.Combine(root, "identity-rejection-data"), "0.2.0");
            await RejectAsync(() => unauthenticated.DownloadAsync(peer with { Fingerprint = new string('f', 64) }, CancellationToken.None),
                "Different peer TLS identity is refused before download");
            string failingRoot = Path.Combine(root, "failing-primary");
            Directory.CreateDirectory(failingRoot);
            foreach (string file in new[] { "Boardcast-update.zip", "release-manifest.json", "release-trust.json" }) File.Copy(Path.Combine(newRoot, file), Path.Combine(failingRoot, file));
            using var failingSource = new PeerUpdateService(senderIdentity, failingRoot, Path.Combine(root, "failing-primary-data"), "0.2.1");
            using var failoverStop = new CancellationTokenSource();
            var failingTask = failingSource.RunAsync(failoverStop.Token);
            try
            {
                // First publish the valid manifest, then simulate local source corruption before a later chunk request.
                using (var probeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                using (var probeClient = new TcpClient())
                {
                    await probeClient.ConnectAsync("127.0.0.1", failingSource.Port, probeDeadline.Token);
                    using var probeTls = new SslStream(probeClient.GetStream(), false, (_, certificate, _, _) => certificate is not null && IdentityStore.GetFingerprint(certificate) == senderIdentity.Fingerprint);
                    await probeTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Boardcast", ClientCertificates = new X509CertificateCollection { receiverIdentity.Certificate }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, probeDeadline.Token);
                    await WireProtocol.WriteAsync(probeTls, new { Type = "manifest" }, probeDeadline.Token);
                    var manifestReply = await WireProtocol.ReadAsync<JsonElement>(probeTls, probeDeadline.Token);
                    Check(manifestReply.GetProperty("type").GetString() == "manifest", "Fallback fixture starts from a valid authenticated release manifest");
                }
                using (var changed = new FileStream(Path.Combine(failingRoot, "Boardcast-update.zip"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    changed.Position = 100; int previous = changed.ReadByte(); changed.Position = 100; changed.WriteByte((byte)(previous ^ 0xff));
                }
                using var failoverClient = new PeerUpdateService(receiverIdentity, oldRoot, Path.Combine(root, "failover-client-data"), "0.2.0");
                await failoverClient.DownloadAsync(peer with { UpdatePort = failingSource.Port }, [alternatePeer, mirrorPeer], CancellationToken.None);
                Check(failoverClient.Status.State == "ready" && failoverClient.Status.SourceCount == 2 && failoverClient.Status.PeerBytes > 0,
                    "A failed primary falls back to the matching mirror while rejecting a signed different package advertised as the same version");
            }
            finally
            {
                failoverStop.Cancel(); try { await failingTask; } catch (OperationCanceledException) { }
            }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(serverTask, mirrorTask, alternateTask); } catch (OperationCanceledException) { }
        }
        using (var restored = new PeerUpdateService(receiverIdentity, oldRoot, clientData, "0.2.0"))
        using (var restoreStop = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            var restoreTask = restored.RunAsync(restoreStop.Token);
            try
            {
                while (restored.Status.State is "idle" or "downloading" or "restoring") await Task.Delay(10, restoreStop.Token);
                Check(restored.Status.State == "ready" && restored.Status.Version == "0.2.1" && restored.Status.BytesReceived == target.Manifest.PackageBytes,
                    "Service restart restores a verified ready update with every source offline");
                Check(Directory.GetDirectories(updates, "download-*").Length == 1 && Directory.GetDirectories(updates, "helper-*").Length == 1,
                    "Ready-update recovery keeps one package and one verified installer snapshot");
            }
            finally
            {
                restoreStop.Cancel(); try { await restoreTask; } catch (OperationCanceledException) { }
            }
        }
        string cachedPackage = Directory.GetFiles(updates, "Boardcast-update.zip", SearchOption.AllDirectories).Single();
        using (var damaged = new FileStream(cachedPackage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            damaged.Position = 100;
            int previous = damaged.ReadByte();
            damaged.Position = 100; damaged.WriteByte((byte)(previous ^ 0xff));
        }
        using (var partial = new PeerUpdateService(receiverIdentity, oldRoot, clientData, "0.2.0"))
        {
            await partial.RestoreAsync();
            long reusable = partial.Status.BytesReceived;
            Check(partial.Status.State == "paused" && partial.Status.CanResume && reusable > 0 && reusable < target.Manifest.PackageBytes,
                "Restart rechecks cached update chunks and preserves only verified content after one chunk is damaged");
            using var repairSource = new PeerUpdateService(senderIdentity, newRoot, Path.Combine(root, "repair-source-data"), "0.2.1");
            using var repairStop = new CancellationTokenSource();
            var repairTask = repairSource.RunAsync(repairStop.Token);
            try
            {
                await partial.DownloadAsync(peer with { UpdatePort = repairSource.Port }, CancellationToken.None);
                Check(partial.Status.State == "ready" && partial.Status.ReusedBytes == reusable && partial.Status.BytesReceived == target.Manifest.PackageBytes,
                    "Explicitly resuming an update reuses verified chunks and downloads only the damaged chunk");
                Check(Directory.GetDirectories(updates, "helper-*").Length == 1,
                    "Repairing a resumed update removes its obsolete installer snapshot");
            }
            finally
            {
                repairStop.Cancel(); try { await repairTask; } catch (OperationCanceledException) { }
            }
        }
        File.WriteAllText(Path.Combine(updates, "last-update.json"), JsonSerializer.Serialize(new { State = "completed", Version = "0.2.1" }, BoardcastJson.Options));
        using (var installed = new PeerUpdateService(receiverIdentity, newRoot, clientData, "0.2.1"))
        {
            await installed.RestoreAsync();
            Check(installed.Status.State == "idle" && !File.Exists(Path.Combine(updates, "selected-update.json")),
                "Starting the installed target version clears its obsolete selection and returns to idle");
            Check(installed.Status.Message.Contains("已更新到 v0.2.1", StringComparison.Ordinal) && Directory.GetDirectories(updates, "helper-*").Length == 1,
                "Clearing an installed update preserves the completion message and helper needed by health checks");
        }
        using var disabled = new PeerUpdateService(receiverIdentity, Path.Combine(root, "unsigned-install"), Path.Combine(root, "unsigned-data"));
        Check(!disabled.Status.CanUpdate && disabled.Port == 0 && disabled.AvailableReleaseVersion == "", "Unsigned development builds cannot advertise or execute LAN updates");
    }
    if (OperatingSystem.IsWindows()) await UpdaterEndToEnd.RunAsync(root, Check);
    Console.WriteLine($"All {checks} update checks passed.");
}
finally
{
    string normalized = Path.GetFullPath(root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (normalized.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(normalized).StartsWith("Boardcast.UpdateTests-", StringComparison.Ordinal)) Directory.Delete(normalized, true);
}
