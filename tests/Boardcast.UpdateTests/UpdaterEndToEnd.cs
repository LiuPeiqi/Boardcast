using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Boardcast.Core;

internal static class UpdaterEndToEnd
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        string workspace = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(workspace, "Boardcast.slnx"))) workspace = Path.GetDirectoryName(workspace) ?? throw new IOException("Cannot locate updater fixture projects.");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        string fixtureOutput = Path.Combine(workspace, "tests", "Boardcast.UpdateFixtureHost", "bin", configuration, "net10.0");
        string updaterOutput = Path.Combine(workspace, "src", "Boardcast.Updater", "bin", configuration, "net10.0");
        foreach (var name in new[] { "healthy", "exit-ui", "wrong-backend-version-and-ui-exit", "tamper-before-install" }) await Scenario(name);

        async Task Scenario(string behavior)
        {
            string scenarioRoot = Path.Combine(root, "lifecycle-" + behavior), install = Path.Combine(scenarioRoot, "installed"), remote = Path.Combine(scenarioRoot, "remote"), data = Path.Combine(scenarioRoot, "user-data");
            Directory.CreateDirectory(scenarioRoot); Directory.CreateDirectory(data);
            using var publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var trust = new ReleaseTrust { KeyId = "lifecycle-tests-only", PublicKeyPem = publisher.ExportSubjectPublicKeyInfoPem() };
            await CreateFixtureRelease(install, "0.2.0", "healthy");
            await CreateFixtureRelease(remote, "0.2.1", behavior is "exit-ui" or "wrong-backend-version-and-ui-exit" ? behavior : "healthy");
            using var sourceIdentity = new IdentityStore(Path.Combine(scenarioRoot, "source-identity"));
            using var server = new PeerUpdateService(sourceIdentity, remote, Path.Combine(scenarioRoot, "source-data"), "0.2.1");
            using var sourceStop = new CancellationTokenSource(); var sourceTask = server.RunAsync(sourceStop.Token);
            using var oldUi = StartUi(install, data);
            try
            {
                await WaitForAsync(async () => (await Status(data))?.Snapshot?.AppVersion == "0.2.0", TimeSpan.FromSeconds(15), "Old fixture did not become ready.");
                var peer = new PeerInfo { Address = "127.0.0.1", UpdatePort = server.Port, Fingerprint = sourceIdentity.Fingerprint, Name = "signed fixture source", Runtime = AppRelease.RuntimeIdentifier, UpdateVersion = "0.2.1" };
                var download = await IpcClient.SendAsync(new() { Command = "update-download", Peer = peer }, data);
                if (!download.Success || download.Snapshot?.Update.State != "ready") throw new IOException("Fixture download failed: " + download.Error);
                if (behavior == "tamper-before-install")
                {
                    string package = Directory.GetFiles(Path.Combine(data, "updates"), "Boardcast-update.zip", SearchOption.AllDirectories).Single();
                    await using var file = new FileStream(package, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    file.Position = 100; int previous = file.ReadByte();
                    file.Position = 100; file.WriteByte((byte)(previous ^ 0xff));
                }
                var request = await IpcClient.SendAsync(new() { Command = "update-install", ProcessId = oldUi.Id }, data);
                if (!request.Success) throw new IOException("Fixture installer did not launch: " + request.Error);
                string resultPath = Path.Combine(data, "updates", "last-update.json");
                await WaitForAsync(() => Task.FromResult(File.Exists(resultPath)), TimeSpan.FromSeconds(55), "Standalone updater did not report a result.");
                using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(resultPath));
                string state = result.RootElement.GetProperty("state").GetString()!;
                if (behavior == "healthy")
                {
                    check(state == "completed", "Actual standalone updater completes signed portable install");
                    check((await File.ReadAllTextAsync(Path.Combine(install, "fixture-version.txt"))).Trim() == "0.2.1", "Actual updater switches the entire install directory to the signed new release");
                    string backup = result.RootElement.GetProperty("recoveryDirectory").GetString()!;
                    check((await File.ReadAllTextAsync(Path.Combine(backup, "fixture-version.txt"))).Trim() == "0.2.0", "Actual updater retains a complete recoverable previous release");
                    check((await Status(data))?.Snapshot?.AppVersion == "0.2.1" && oldUi.HasExited, "Actual updater restarts UI and authenticates target-version backend health");
                    check((await Status(data))?.Snapshot?.Update.Message.Contains("已更新到 v0.2.1", StringComparison.Ordinal) == true, "Live status observes the completion record written after new-backend startup");
                }
                else if (behavior is "exit-ui" or "wrong-backend-version-and-ui-exit")
                {
                    check(state == "rolled-back", behavior == "exit-ui" ? "Actual updater rolls back when the signed new UI immediately fails to start" : "Actual updater gracefully stops the wrong-version backend before rollback");
                    check((await File.ReadAllTextAsync(Path.Combine(install, "fixture-version.txt"))).Trim() == "0.2.0", "Startup failure restores the complete old install directory");
                    await WaitForAsync(async () => (await Status(data))?.Snapshot?.AppVersion == "0.2.0", TimeSpan.FromSeconds(15), "Rolled-back fixture did not restart.");
                    check(CountUiLaunches(data, "0.2.0") >= 2, "Startup rollback restarts the old UI and backend");
                    check((await Status(data))?.Snapshot?.Update.Message.Contains("已恢复 v0.2.0", StringComparison.Ordinal) == true, "Restarted old UI receives the rollback result through live daemon status");
                }
                else
                {
                    check(state == "failed", "Actual updater re-verifies and refuses a package tampered after download");
                    check((await File.ReadAllTextAsync(Path.Combine(install, "fixture-version.txt"))).Trim() == "0.2.0" && !Directory.GetDirectories(scenarioRoot, ".Boardcast-backup-*").Any(), "Pre-switch verification failure leaves the old install untouched");
                    await WaitForAsync(async () => CountUiLaunches(data, "0.2.0") >= 2 && (await Status(data))?.Snapshot?.AppVersion == "0.2.0", TimeSpan.FromSeconds(15), "Pre-switch failure did not restore the UI after delayed backend shutdown.");
                    check(true, "Pre-switch failure waits through delayed old-backend exit before restoring the old UI");
                }
            }
            finally
            {
                sourceStop.Cancel(); try { await sourceTask; } catch (OperationCanceledException) { }
                try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await IpcClient.SendAsync(new() { Command = "stop" }, data, deadline.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                await Task.Delay(350);
                // Only processes whose executable is inside this freshly generated fixture tree are eligible.
                foreach (var process in Process.GetProcesses())
                    using (process)
                    {
                        try
                        {
                            string? executable = process.MainModule?.FileName;
                            if (executable is not null && UpdateInstallPlan.IsUnder(executable, scenarioRoot) && !process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                    }
            }

            async Task CreateFixtureRelease(string destination, string version, string fixtureBehavior)
            {
                Directory.CreateDirectory(destination);
                foreach (string file in Directory.GetFiles(fixtureOutput)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
                foreach (string file in Directory.GetFiles(updaterOutput).Where(f => Path.GetFileName(f).StartsWith("Boardcast.Updater", StringComparison.Ordinal))) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
                File.Copy(Path.Combine(destination, "Boardcast.UpdateFixtureHost.exe"), Path.Combine(destination, "Boardcast.exe"));
                File.Copy(Path.Combine(destination, "Boardcast.UpdateFixtureHost.exe"), Path.Combine(destination, "Boardcast.Daemon.exe"));
                await File.WriteAllTextAsync(Path.Combine(destination, "fixture-version.txt"), version);
                await File.WriteAllTextAsync(Path.Combine(destination, "fixture-behavior.txt"), fixtureBehavior);
                await File.WriteAllBytesAsync(Path.Combine(destination, "release-trust.json"), JsonSerializer.SerializeToUtf8Bytes(trust, BoardcastJson.Options));
                string package = Path.Combine(destination, "Boardcast-update.zip");
                var files = Directory.GetFiles(destination).OrderBy(f => f, StringComparer.Ordinal).ToList();
                using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
                    foreach (string file in files) archive.CreateEntryFromFile(file, Path.GetFileName(file));
                var manifest = new ReleaseManifest
                {
                    Version = version, Runtime = AppRelease.RuntimeIdentifier, MinimumWindowsBuild = 0,
                    PackageBytes = new FileInfo(package).Length, PackageSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(package))),
                    Files = files.Select(file => new ReleaseFile { Path = Path.GetFileName(file), Bytes = new FileInfo(file).Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) }).ToList()
                };
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest, BoardcastJson.Options);
                var envelope = new SignedReleaseEnvelope { KeyId = trust.KeyId, Payload = Convert.ToBase64String(payload), Signature = Convert.ToBase64String(publisher.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
                await File.WriteAllBytesAsync(Path.Combine(destination, "release-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(envelope, BoardcastJson.Options));
            }
        }
    }
    private static Process StartUi(string install, string data)
    {
        var start = new ProcessStartInfo(Path.Combine(install, "Boardcast.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = install };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(data);
        return Process.Start(start) ?? throw new IOException("Cannot start isolated fixture UI.");
    }
    private static async Task<IpcResponse?> Status(string data)
    {
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)); return await IpcClient.SendAsync(new() { Command = "status" }, data, deadline.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return null; }
    }
    private static int CountUiLaunches(string data, string version)
    {
        string path = Path.Combine(data, "fixture-ui-launches.jsonl");
        if (!File.Exists(path)) return 0;
        return File.ReadAllLines(path).Count(line => { using var record = JsonDocument.Parse(line); return record.RootElement.GetProperty("version").GetString() == version; });
    }
    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string message)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout) { if (await condition()) return; await Task.Delay(50); }
        throw new IOException(message);
    }
}
