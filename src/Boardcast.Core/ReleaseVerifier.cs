using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Boardcast.Core;

public sealed record ReleaseTrust
{
    public string KeyId { get; set; } = "";
    public string PublicKeyPem { get; set; } = "";
    public static ReleaseTrust Load(string installRoot)
    {
        string path = PathSafety.ResolveUnderRoot(installRoot, "release-trust.json");
        if (!File.Exists(path)) throw new InvalidDataException("此开发版本尚未配置发行公钥，不能从其他设备执行更新。");
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("发行信任配置过大。");
        var trust = JsonSerializer.Deserialize<ReleaseTrust>(File.ReadAllBytes(path), BoardcastJson.Options) ?? throw new InvalidDataException("发行信任配置为空。");
        if (trust.KeyId is not { Length: >= 1 and <= 128 } || trust.KeyId.Any(char.IsControl) || trust.PublicKeyPem is not { Length: > 0 and < 16384 }) throw new InvalidDataException("发行公钥标识无效。");
        using var key = ECDsa.Create(); key.ImportFromPem(trust.PublicKeyPem);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new InvalidDataException("发行公钥必须使用 ECDSA P-256。");
        return trust;
    }
}
public sealed record SignedReleaseEnvelope
{
    public int Format { get; set; } = 1;
    public string Algorithm { get; set; } = "ECDSA-P256-SHA256";
    public string KeyId { get; set; } = "";
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}
public sealed record ReleaseManifest
{
    public int Format { get; set; } = 1;
    public string Product { get; set; } = "Boardcast";
    public string Version { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int MinimumWindowsBuild { get; set; } = 19041;
    public long PackageBytes { get; set; }
    public string PackageSha256 { get; set; } = "";
    public List<ReleaseFile> Files { get; set; } = [];
}
public sealed record ReleaseFile
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}
public sealed record VerifiedRelease(SignedReleaseEnvelope Envelope, ReleaseManifest Manifest);

public static class ReleaseVerifier
{
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumUnpackedBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumEnvelopeBytes = 2 * 1024 * 1024;
    public static SignedReleaseEnvelope ReadEnvelope(string path)
    {
        if (new FileInfo(path).Length > MaximumEnvelopeBytes) throw new InvalidDataException("发行清单过大。");
        return JsonSerializer.Deserialize<SignedReleaseEnvelope>(File.ReadAllBytes(path), BoardcastJson.Options) ?? throw new InvalidDataException("发行清单为空。");
    }
    public static VerifiedRelease VerifyManifest(SignedReleaseEnvelope envelope, ReleaseTrust trust, string runtime, string currentVersion, bool requireNewer = true)
    {
        if (envelope.Format != 1 || envelope.Algorithm != "ECDSA-P256-SHA256" || envelope.KeyId != trust.KeyId || envelope.Payload is not { Length: > 0 and <= MaximumEnvelopeBytes } || envelope.Signature is not { Length: > 0 and <= 256 })
            throw new InvalidDataException("更新包不是受信任发行者签署的支持格式。");
        byte[] payload, signature;
        try { payload = Convert.FromBase64String(envelope.Payload); signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException ex) { throw new InvalidDataException("更新签名格式错误。", ex); }
        using var key = ECDsa.Create(); key.ImportFromPem(trust.PublicKeyPem);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" || signature.Length != 64 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("发行签名验证失败，更新包已拒绝。");
        ReleaseManifest manifest;
        try { manifest = JsonSerializer.Deserialize<ReleaseManifest>(payload, BoardcastJson.Options) ?? throw new InvalidDataException("发行内容为空。"); }
        catch (JsonException ex) { throw new InvalidDataException("发行内容无效。", ex); }
        var target = ReleaseVersion.Parse(manifest.Version); var current = ReleaseVersion.Parse(currentVersion);
        if (manifest.Format != 1 || manifest.Product != "Boardcast" || manifest.Runtime != runtime || runtime is not ("win-x64" or "win-arm64"))
            throw new InvalidDataException("更新包不适用于当前系统或处理器。");
        if (requireNewer && (target.CompareTo(current) <= 0 || !current.IsPrerelease && target.IsPrerelease))
            throw new InvalidDataException("只允许更新到当前渠道中更高的版本，不支持降级或自动进入预览渠道。");
        if (manifest.MinimumWindowsBuild < 0 || manifest.MinimumWindowsBuild > 100000 || OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build < manifest.MinimumWindowsBuild)
            throw new InvalidDataException("当前 Windows 版本不满足此更新要求。");
        if (manifest.PackageBytes is <= 0 or > MaximumPackageBytes || !ValidHash(manifest.PackageSha256) || manifest.Files is null || manifest.Files.Count is < 1 or > 20000)
            throw new InvalidDataException("更新包大小、摘要或文件数量无效。");
        long unpacked = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Bytes < 0 || !ValidHash(file.Sha256)) throw new InvalidDataException("更新文件摘要或大小无效。");
            string safe = PathSafety.NormalizeRelativePath(file.Path);
            if (safe != file.Path || !paths.Add(safe) || safe.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase) || safe.Equals("Boardcast-update.zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新清单含重复或保留文件名。");
            unpacked = checked(unpacked + file.Bytes);
            if (unpacked > MaximumUnpackedBytes) throw new InvalidDataException("更新解包大小超出限制。");
        }
        foreach (var required in new[] { "Boardcast.exe", "Boardcast.Daemon.exe", "Boardcast.Updater.exe", "release-trust.json" })
            if (!paths.Contains(required)) throw new InvalidDataException("更新包缺少必要程序：" + required);
        return new(envelope, manifest);
    }
    private static bool ValidHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public static async Task VerifyPackageAsync(string path, VerifiedRelease release, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != release.Manifest.PackageBytes || !HashMatches(await SHA256.HashDataAsync(file, ct), release.Manifest.PackageSha256))
            throw new InvalidDataException("更新包大小或 SHA-256 验证失败。");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var expected = release.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (zip.Entries.Count > expected.Count + 20000) throw new InvalidDataException("更新包包含过多条目。");
        foreach (var entry in zip.Entries)
        {
            bool directory = entry.FullName.EndsWith('/');
            string safe = PathSafety.NormalizeRelativePath(directory ? entry.FullName.TrimEnd('/') : entry.FullName);
            int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新包不能包含链接或特殊文件。");
            if (directory) { if (entry.Length != 0) throw new InvalidDataException("更新目录不能包含数据。"); continue; }
            if (safe != entry.FullName || !expected.TryGetValue(safe, out var item) || !actual.Add(safe) || entry.Length != item.Bytes)
                throw new InvalidDataException("更新包包含未签名、重复或大小不符的文件。");
            await using var content = entry.Open();
            if (!HashMatches(await HashExactAsync(content, item.Bytes, ct), item.Sha256)) throw new InvalidDataException("更新文件验证失败：" + safe);
        }
        if (actual.Count != expected.Count) throw new InvalidDataException("更新包缺少已签名文件。");
    }
    public static async Task ExtractVerifiedAsync(string packagePath, VerifiedRelease release, string destination, CancellationToken ct = default)
    {
        await VerifyPackageAsync(packagePath, release, ct).ConfigureAwait(false);
        destination = PathSafety.ValidateReceiveRoot(destination);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new InvalidDataException("更新暂存目录必须为空。");
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(packagePath);
        foreach (var item in release.Manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(item.Path) ?? throw new InvalidDataException("更新条目丢失。");
            string target = PathSafety.ResolveUnderRoot(destination, item.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
            await CopyExactAsync(input, output, item.Bytes, ct).ConfigureAwait(false);
        }
        // A release cannot silently replace the local trust root with an unrelated publisher.
    }
    private static async Task<byte[]> HashExactAsync(Stream input, long bytes, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        while (bytes > 0)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), ct).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("更新条目实际内容小于已签名大小。");
            hash.AppendData(buffer, 0, count); bytes -= count;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0) throw new InvalidDataException("更新条目实际内容超过已签名大小。");
        return hash.GetHashAndReset();
    }
    private static async Task CopyExactAsync(Stream input, Stream output, long bytes, CancellationToken ct)
    {
        byte[] buffer = new byte[128 * 1024];
        while (bytes > 0)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, bytes)), ct).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("更新条目实际内容小于已签名大小。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); bytes -= count;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false) != 0) throw new InvalidDataException("更新条目实际内容超过已签名大小。");
    }
    public static async Task VerifyInstalledFileAsync(string installRoot, ReleaseFile item, CancellationToken ct = default)
    {
        string path = PathSafety.ResolveUnderRoot(installRoot, item.Path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != item.Bytes || !HashMatches(await SHA256.HashDataAsync(stream, ct), item.Sha256)) throw new InvalidDataException("当前安装文件已改变：" + item.Path);
    }
    public static bool HashMatches(byte[] hash, string expected)
    {
        try { return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected)); }
        catch (FormatException) { return false; }
    }
    public static void RequireSameTrust(ReleaseTrust original, ReleaseTrust candidate)
    {
        using var a = ECDsa.Create(); using var b = ECDsa.Create();
        a.ImportFromPem(original.PublicKeyPem); b.ImportFromPem(candidate.PublicKeyPem);
        if (original.KeyId != candidate.KeyId || !CryptographicOperations.FixedTimeEquals(a.ExportSubjectPublicKeyInfo(), b.ExportSubjectPublicKeyInfo()))
            throw new InvalidDataException("更新包不能更换发行信任根；公钥轮换需要单独验证的发行流程。");
    }
}
