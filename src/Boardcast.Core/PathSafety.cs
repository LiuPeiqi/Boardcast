using System.Text;

namespace Boardcast.Core;

public static class PathSafety
{
    public const int MaximumManifestMetadataBytes = 3 * 1024 * 1024;
    public static int EstimateEntryMetadata(string path) => System.Text.Json.JsonEncodedText.Encode(path).EncodedUtf8Bytes.Length + 96;
    public static void ValidateManifest(TransferOffer offer, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(settings);
        if (!Guid.TryParseExact(offer.Id, "N", out _) && !Guid.TryParseExact(offer.Id, "D", out _)) throw new InvalidDataException("传输标识无效。");
        if (offer.Entries is null || offer.Entries.Count == 0 || settings.MaxEntries <= 0 || offer.Entries.Count > Math.Min(settings.MaxEntries, 100000))
            throw new InvalidDataException("传输项目数量无效或超出限制。");
        if (offer.SenderName is null || offer.SenderName.Length > 256 || offer.Summary is null || offer.Summary.Length > 16384)
            throw new InvalidDataException("设备名称或摘要过长。");
        if (settings.MaxTransferBytes < 0) throw new InvalidDataException("接收容量限制无效。");
        var entries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        long metadataBytes = 0;
        foreach (var entry in offer.Entries)
        {
            if (entry is null || entry.Size < 0 || (entry.IsDirectory && entry.Size != 0)) throw new InvalidDataException("文件大小无效。");
            var path = NormalizeRelativePath(entry.RelativePath);
            metadataBytes += EstimateEntryMetadata(entry.RelativePath);
            if (metadataBytes > MaximumManifestMetadataBytes) throw new InvalidDataException("文件清单过大，请分批发送。");
            if (!entries.TryAdd(path, entry.IsDirectory)) throw new InvalidDataException("传输包含重复或冲突的文件名。");
            try { total = checked(total + entry.Size); }
            catch (OverflowException) { throw new InvalidDataException("传输大小溢出。"); }
            if (total > settings.MaxTransferBytes) throw new InvalidDataException("传输大小超出接收限制。");
        }
        foreach (var path in entries.Keys)
        {
            var slash = path.IndexOf('/');
            while (slash >= 0)
            {
                if (entries.TryGetValue(path[..slash], out var directory) && !directory) throw new InvalidDataException("文件不能包含子项目。");
                slash = path.IndexOf('/', slash + 1);
            }
        }
    }

    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 2048) throw new InvalidDataException("文件路径为空或过长。");
        var path = relativePath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (path[0] == '/' || path.Contains(':')) throw new InvalidDataException("只允许相对文件路径。");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Length > 255 || segment.EndsWith('.') || segment.EndsWith(' ') || segment.Any(c => c < 32 || c is '<' or '>' or '"' or '|' or '?' or '*'))
                throw new InvalidDataException("文件路径包含不安全的名称。");
            var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException("文件路径包含系统保留名称。");
        }
        return path;
    }

    public static string ValidateReceiveRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.StartsWith("\\\\", StringComparison.Ordinal) || root.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("接收目录必须是本机绝对路径。");
        var fullRoot = Path.GetFullPath(root);
        for (var current = fullRoot; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            RejectReparsePoint(current);
        if (File.Exists(fullRoot)) throw new InvalidDataException("接收目录指向了文件。");
        return fullRoot;
    }

    public static string ResolveUnderRoot(string root, string relativePath)
    {
        var fullRoot = ValidateReceiveRoot(root);
        var relative = NormalizeRelativePath(relativePath);
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(fullRoot);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        if (!result.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("文件路径超出接收目录。");
        var current = fullRoot;
        foreach (var segment in relative.Split('/'))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
        return result;
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("接收路径不能经过链接或重解析点。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
