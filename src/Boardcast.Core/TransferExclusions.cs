using System.Text;

namespace Boardcast.Core;

/// <summary>Explicit paths relative to one source folder. A directory match excludes its entire subtree.</summary>
public sealed class TransferExclusions
{
    private readonly HashSet<string> _paths;
    public IReadOnlyList<string> Paths { get; }

    public TransferExclusions(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        _paths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var path in relativePaths) _paths.Add(Normalize(path));
        Paths = Array.AsReadOnly(_paths.ToArray());
    }

    public bool IsExcluded(string relativePath)
    {
        var path = relativePath.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (_paths.Contains(path)) return true;
        for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            if (_paths.Contains(path[..slash])) return true;
        return false;
    }

    public static IReadOnlyList<string> NormalizeRelativePaths(IEnumerable<string> paths) => new TransferExclusions(paths).Paths;

    /// <summary>Converts native picker results to child paths, rejecting the root and anything outside it.</summary>
    public static IReadOnlyList<string> RelativePaths(string root, IEnumerable<string> absolutePaths)
    {
        ArgumentNullException.ThrowIfNull(absolutePaths);
        var fullRoot = Path.TrimEndingDirectorySeparator(PathSafety.ValidateReceiveRoot(root));
        return NormalizeRelativePaths(absolutePaths.Select(path =>
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                throw new InvalidDataException("排除项目必须是文件夹内的完整路径。");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return Path.GetRelativePath(fullRoot, full);
        }));
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("排除路径不能为空。");
        // A trailing separator is harmless for an explicitly selected directory.
        return PathSafety.NormalizeRelativePath(path.TrimEnd('/', '\\'));
    }
}
