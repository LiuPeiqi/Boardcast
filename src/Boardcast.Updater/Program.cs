using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Boardcast.Core;

UpdateInstallPlan? plan = null;
bool swapped = false;
bool newUiStarted = false;
bool planValidated = false;
bool recovered = false;
try
{
    if (!OperatingSystem.IsWindows() || args.Length != 2 || args[0] != "--plan") throw new InvalidDataException("更新安装器需要有效的 Windows 安装计划。");
    string planPath = Path.GetFullPath(args[1]);
    if (!UpdateInstallPlan.SamePath(Path.GetDirectoryName(planPath)!, AppContext.BaseDirectory) || new FileInfo(planPath).Length > 16384) throw new InvalidDataException("更新计划路径无效。");
    plan = JsonSerializer.Deserialize<UpdateInstallPlan>(await File.ReadAllBytesAsync(planPath), BoardcastJson.Options) ?? throw new InvalidDataException("更新安装计划为空。");
    plan.Validate();
    string helperParent = Path.Combine(plan.DataDirectory, "updates");
    UpdateInstallPlan.ValidateOwnedDirectory(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), helperParent, "helper-");
    byte[] trustBytes = await File.ReadAllBytesAsync(PathSafety.ResolveUnderRoot(AppContext.BaseDirectory, "release-trust.json"));
    if (!ReleaseVerifier.HashMatches(SHA256.HashData(trustBytes), plan.TrustSha256)) throw new InvalidDataException("原有发行信任根已改变。");
    var trust = ReleaseTrust.Load(AppContext.BaseDirectory);
    planValidated = true;
    var release = ReleaseVerifier.VerifyManifest(ReleaseVerifier.ReadEnvelope(PathSafety.ResolveUnderRoot(plan.DownloadRoot, "release-manifest.json")), trust, AppRelease.RuntimeIdentifier, plan.CurrentVersion);
    if (release.Manifest.Version != plan.Version) throw new InvalidDataException("更新目标版本不一致。");
    string package = PathSafety.ResolveUnderRoot(plan.DownloadRoot, "Boardcast-update.zip");
    await ReleaseVerifier.ExtractVerifiedAsync(package, release, plan.StagedRoot);
    foreach (var item in release.Manifest.Files) await ReleaseVerifier.VerifyInstalledFileAsync(plan.StagedRoot, item);
    ReleaseVerifier.RequireSameTrust(trust, ReleaseTrust.Load(plan.StagedRoot));
    await File.WriteAllBytesAsync(Path.Combine(plan.StagedRoot, "release-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(release.Envelope, BoardcastJson.Options));
    File.Copy(package, Path.Combine(plan.StagedRoot, "Boardcast-update.zip"));
    using (var exitDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2)))
        foreach (var process in plan.Processes) await process.WaitForExitAsync(exitDeadline.Token);
    // Never stop, kill, or overwrite an unrelated daemon; the IPC owner closes gracefully.
    string daemonLock = Path.Combine(plan.DataDirectory, "daemon.lock");
    if (File.Exists(daemonLock)) { using var free = new FileStream(daemonLock, FileMode.Open, FileAccess.Read, FileShare.None); }
    UpdateDirectorySwap.Apply(plan.InstallRoot, plan.StagedRoot, plan.BackupRoot); swapped = true;
    using var ui = Restart(plan); newUiStarted = true;
    bool healthy = false;
    using (var healthDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
    {
        while (!healthDeadline.IsCancellationRequested)
        {
            if (ui.HasExited) break;
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(healthDeadline.Token); attempt.CancelAfter(TimeSpan.FromSeconds(2));
                var reply = await IpcClient.SendAsync(new() { Command = "status" }, plan.DataDirectory, attempt.Token);
                if (reply.Success && reply.Snapshot?.AppVersion == plan.Version) { healthy = true; break; }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            try { await Task.Delay(500, healthDeadline.Token); } catch (OperationCanceledException) { }
        }
    }
    if (!healthy)
    {
        // Stop only the authenticated backend for this data directory, and only if no transfers started.
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        if (!DaemonUnlocked(daemonLock))
        {
            using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cleanupDeadline.Token);
            probeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            IpcResponse snapshot;
            try { snapshot = await IpcClient.SendAsync(new() { Command = "status" }, plan.DataDirectory, probeDeadline.Token); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            { throw new InvalidDataException("无法确认新后台是否空闲，已保留旧版本备份。", ex); }
            if (!snapshot.Success || snapshot.Snapshot is null || snapshot.Snapshot.Transfers.Any(t => t.State is not ("completed" or "failed" or "cancelled" or "rejected")) || snapshot.Snapshot.Pending.Count > 0)
                throw new InvalidDataException("新版本已开始传输或尚未确认空闲，自动回滚暂缓；旧版本备份已保留。");
            await IpcClient.SendAsync(new() { Command = "stop" }, plan.DataDirectory, cleanupDeadline.Token);
        }
        if (!ui.HasExited) { ui.Kill(); await ui.WaitForExitAsync(cleanupDeadline.Token); }
        while (!DaemonUnlocked(daemonLock)) await Task.Delay(100, cleanupDeadline.Token);
        string failed = UpdateDirectorySwap.Rollback(plan.InstallRoot, plan.BackupRoot); swapped = false;
        using var restarted = Restart(plan);
        await WriteResult(plan, "rolled-back", "新版本未通过启动检查，已恢复旧版本。", failed);
        Environment.ExitCode = 1;
    }
    else await WriteResult(plan, "completed", "更新完成，旧版本备份已保留。", plan.BackupRoot);
}
catch (Exception ex)
{
    Environment.ExitCode = 1;
    if (plan is not null && planValidated)
    {
        // If process creation itself failed, no new UI can hold the directory open.
        if (swapped && !newUiStarted)
        {
            try
            {
                string lockPath = Path.Combine(plan.DataDirectory, "daemon.lock");
                if (File.Exists(lockPath)) { using var free = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
                UpdateDirectorySwap.Rollback(plan.InstallRoot, plan.BackupRoot); swapped = false;
                using var restored = Restart(plan); recovered = true;
            }
            catch { }
        }
        try { await WriteResult(plan, "failed", ex.Message + (swapped ? "；旧版本备份已保留，未强制终止其他程序。" : ""), plan.BackupRoot); } catch { }
        if (!swapped)
        {
            UpdateInstallPlan.DeleteOwnedDirectory(plan.StagedRoot, Path.GetDirectoryName(plan.InstallRoot)!, ".Boardcast-update-");
            // Failure before the switch should bring the old UI back if its process already exited.
            try
            {
                if (!recovered)
                {
                    using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                    foreach (var process in plan.Processes) await process.WaitForExitAsync(recoveryDeadline.Token);
                    using var restored = Restart(plan);
                }
            }
            catch { }
        }
    }
}

static Process Restart(UpdateInstallPlan plan)
{
    var start = new ProcessStartInfo(Path.Combine(plan.InstallRoot, "Boardcast.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = plan.InstallRoot };
    foreach (var argument in plan.RestartArguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new IOException("无法重启邻间。");
}
static bool DaemonUnlocked(string path)
{
    if (!File.Exists(path)) return true;
    try { using var free = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return true; }
    catch (FileNotFoundException) { return true; }
    catch (IOException) { return false; }
}
static async Task WriteResult(UpdateInstallPlan plan, string state, string message, string recoveryDirectory)
{
    string path = PathSafety.ResolveUnderRoot(plan.DataDirectory, "updates/last-update.json");
    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        await using (var file = PrivateFile.Create(temporary))
            await file.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { state, version = plan.Version, message, recoveryDirectory, at = DateTimeOffset.UtcNow }, BoardcastJson.Options));
        File.Move(temporary, path, true);
    }
    finally { if (File.Exists(temporary)) File.Delete(temporary); }
}
