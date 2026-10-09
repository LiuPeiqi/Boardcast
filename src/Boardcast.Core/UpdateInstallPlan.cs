using System.Diagnostics;

namespace Boardcast.Core;

public sealed record UpdateProcessIdentity
{
    public int Id { get; set; }
    public long StartTimeUtcTicks { get; set; }
    public string ExecutablePath { get; set; } = "";
    public static UpdateProcessIdentity Capture(int id)
    {
        using var process = Process.GetProcessById(id);
        return new() { Id = id, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks, ExecutablePath = process.MainModule?.FileName ?? throw new InvalidDataException("不能确认更新进程路径。") };
    }
    public async Task WaitForExitAsync(CancellationToken ct)
    {
        try
        {
            using var process = Process.GetProcessById(Id);
            if (process.StartTime.ToUniversalTime().Ticks != StartTimeUtcTicks) return;
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (ArgumentException) { }
    }
}

public sealed record UpdateInstallPlan
{
    public int Format { get; set; } = 1;
    public string InstallRoot { get; set; } = "";
    public string StagedRoot { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public string DownloadRoot { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string CurrentVersion { get; set; } = "";
    public string Version { get; set; } = "";
    public string TrustSha256 { get; set; } = "";
    public List<UpdateProcessIdentity> Processes { get; set; } = [];
    public List<string> RestartArguments { get; set; } = [];
    public void Validate()
    {
        if (Format != 1 || TrustSha256 is not { Length: 64 } || !TrustSha256.All(Uri.IsHexDigit) || Processes is null || Processes.Count is < 2 or > 8 ||
            Processes.Any(p => p is null || p.Id <= 0 || p.StartTimeUtcTicks <= 0 || string.IsNullOrWhiteSpace(p.ExecutablePath)) || Processes.Select(p => p.Id).Distinct().Count() != Processes.Count || RestartArguments is null)
            throw new InvalidDataException("更新安装计划无效。");
        InstallRoot = Path.TrimEndingDirectorySeparator(PathSafety.ValidateReceiveRoot(InstallRoot));
        DataDirectory = PathSafety.ValidateReceiveRoot(DataDirectory);
        if (Path.GetPathRoot(InstallRoot) == InstallRoot || IsUnder(DataDirectory, InstallRoot)) throw new InvalidDataException("更新不能替换磁盘根目录或程序内用户数据。");
        if (!File.Exists(Path.Combine(InstallRoot, "Boardcast.exe")) || !File.Exists(Path.Combine(InstallRoot, "release-manifest.json"))) throw new InvalidDataException("目标目录不是已签名的 Boardcast 发行版。");
        string parent = Path.GetDirectoryName(InstallRoot)!;
        ValidateOwnedDirectory(StagedRoot, parent, ".Boardcast-update-");
        ValidateOwnedDirectory(BackupRoot, parent, ".Boardcast-backup-");
        ValidateOwnedDirectory(DownloadRoot, Path.Combine(DataDirectory, "updates"), "download-");
        if (Directory.Exists(BackupRoot)) throw new InvalidDataException("更新备份目录必须尚不存在。");
        var target = ReleaseVersion.Parse(Version);
        if (target.CompareTo(ReleaseVersion.Parse(CurrentVersion)) <= 0) throw new InvalidDataException("更新安装计划不能降级。");
        if (RestartArguments.Count != 2 || RestartArguments[0] != "--data-dir" || string.IsNullOrWhiteSpace(RestartArguments[1]) || !SamePath(RestartArguments[1], DataDirectory)) throw new InvalidDataException("更新重启参数无效。");
    }
    public static bool SamePath(string a, string b) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static bool IsUnder(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return SamePath(path, root) || Path.GetFullPath(path).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
    public static void ValidateOwnedDirectory(string target, string parent, string prefix)
    {
        string full = PathSafety.ValidateReceiveRoot(target), expected = PathSafety.ValidateReceiveRoot(parent);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
        string suffix = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : "";
        bool ownedName = Guid.TryParseExact(suffix, "N", out _) || prefix == "download-" && suffix.Length == 64 && suffix.All(Uri.IsHexDigit);
        if (!SamePath(Path.GetDirectoryName(full)!, expected) || !name.StartsWith(prefix, StringComparison.Ordinal) || !ownedName)
            throw new InvalidDataException("更新目录超出了允许范围。");
    }
    public static void DeleteOwnedDirectory(string target, string parent, string prefix)
    {
        try { ValidateOwnedDirectory(target, parent, prefix); if (Directory.Exists(target)) Directory.Delete(target, true); }
        catch (InvalidDataException) { }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>All moves stay on one local volume; the old release remains recoverable.</summary>
public static class UpdateDirectorySwap
{
    public static void Apply(string installRoot, string stagedRoot, string backupRoot)
    {
        installRoot = PathSafety.ValidateReceiveRoot(installRoot);
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(installRoot))!;
        UpdateInstallPlan.ValidateOwnedDirectory(stagedRoot, parent, ".Boardcast-update-");
        UpdateInstallPlan.ValidateOwnedDirectory(backupRoot, parent, ".Boardcast-backup-");
        if (!Directory.Exists(stagedRoot) || Directory.Exists(backupRoot)) throw new InvalidDataException("更新暂存或备份目录状态无效。");
        Directory.Move(installRoot, backupRoot);
        try { Directory.Move(stagedRoot, installRoot); }
        catch { Directory.Move(backupRoot, installRoot); throw; }
    }
    public static string Rollback(string installRoot, string backupRoot)
    {
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(PathSafety.ValidateReceiveRoot(installRoot)))!;
        UpdateInstallPlan.ValidateOwnedDirectory(backupRoot, parent, ".Boardcast-backup-");
        string failed = PathSafety.ResolveUnderRoot(parent, ".Boardcast-failed-" + Guid.NewGuid().ToString("N"));
        Directory.Move(installRoot, failed);
        try { Directory.Move(backupRoot, installRoot); }
        catch { Directory.Move(failed, installRoot); throw; }
        return failed;
    }
}
