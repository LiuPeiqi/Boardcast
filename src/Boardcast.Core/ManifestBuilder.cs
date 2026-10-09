namespace Boardcast.Core;
public sealed record TransferPlan(TransferOffer Offer, IReadOnlyDictionary<string, string> LocalFiles);
public static class ManifestBuilder
{
    public static TransferPlan BuildPlan(IEnumerable<string> paths, AppSettings settings, CancellationToken cancellationToken = default)
        => BuildPlan(paths.Select(path => new TransferSource { Path = path }), settings, cancellationToken);

    public static TransferPlan BuildPlan(IEnumerable<TransferSource> sources, AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sources);
        var offer = new TransferOffer { SenderName = settings.DeviceName };
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<IEnumerator<(string Local, string Relative, string WithinSource, TransferExclusions Exclusions)>>();
        long metadataBytes = 0;
        stack.Push(sources.Select(source =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source is null) throw new InvalidDataException("传输项目不能为空。");
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            PathSafety.ValidateReceiveRoot(Path.GetDirectoryName(full) ?? full);
            if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network) throw new InvalidDataException("Only local source files are supported.");
            var name = Path.GetFileName(full);
            if (name.Length == 0) throw new InvalidDataException("Select a folder, not a drive root.");
            var exclusions = new TransferExclusions(source.ExcludedPaths);
            return (full, name, "", exclusions);
        }).GetEnumerator());
        try
        {
            while (stack.TryPeek(out var iterator))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!iterator.MoveNext()) { stack.Pop().Dispose(); continue; }
                var (local, name, withinSource, exclusions) = iterator.Current;
                // Excluded directories are never entered; excluded links need no metadata access.
                if (withinSource.Length > 0 && exclusions.IsExcluded(withinSource)) continue;
                if (offer.Entries.Count >= settings.MaxEntries) throw new InvalidDataException("Too many files.");
                var attributes = File.GetAttributes(local);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Links and reparse points cannot be sent.");
                var relative = PathSafety.NormalizeRelativePath(name);
                metadataBytes += PathSafety.EstimateEntryMetadata(relative);
                if (metadataBytes > PathSafety.MaximumManifestMetadataBytes) throw new InvalidDataException("File list is too large; split this transfer into smaller batches.");
                var directory = (attributes & FileAttributes.Directory) != 0;
                if (!directory && withinSource.Length == 0 && exclusions.Paths.Count > 0)
                    throw new InvalidDataException("只能排除所选文件夹内的子项目。");
                offer.Entries.Add(new TransferEntry { RelativePath = relative, IsDirectory = directory, Size = directory ? 0 : new FileInfo(local).Length });
                if (directory)
                    stack.Push(Directory.EnumerateFileSystemEntries(local).Select(child =>
                        (child, relative + "/" + Path.GetFileName(child), withinSource.Length == 0 ? Path.GetFileName(child) : withinSource + "/" + Path.GetFileName(child), exclusions)).GetEnumerator());
                else if (!files.TryAdd(relative, local)) throw new InvalidDataException("Duplicate file names.");
            }
        }
        finally { foreach (var iterator in stack) iterator.Dispose(); }
        PathSafety.ValidateManifest(offer, settings);
        return new(offer, files);
    }
}


