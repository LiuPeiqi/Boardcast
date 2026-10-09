using System.Security;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Boardcast.ScannerTests")]

namespace Boardcast.Core;

/// <summary>Metadata observed during a scan. FolderCount excludes the selected root.</summary>
public sealed record FolderSizeResult(long Bytes, long FileCount, long FolderCount, int SkippedCount)
{
    /// <summary>False when a link or unreadable/changing entry was skipped.</summary>
    public bool IsComplete => SkippedCount == 0;
}

/// <summary>Bounds concurrent directory scans and never reads file contents.</summary>
public sealed class FolderSizeScanner : IDisposable
{
    private readonly object _state = new();
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<string, CancellationToken, FolderSizeResult> _scan;
    private int _calls;
    private bool _disposed;
    private bool _cancellationIssued;
    private bool _resourcesDisposed;

    public int MaximumConcurrency { get; }

    public FolderSizeScanner(int? maximumConcurrency = null)
        : this(maximumConcurrency ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4), ScanDirectory) { }

    // Keeps scheduling tests independent of filesystem speed and directory size.
    internal FolderSizeScanner(int maximumConcurrency, Func<string, CancellationToken, FolderSizeResult> scan)
    {
        if (maximumConcurrency is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumConcurrency), "Use between one and four concurrent scans.");
        ArgumentNullException.ThrowIfNull(scan);
        MaximumConcurrency = maximumConcurrency;
        _slots = new(maximumConcurrency, maximumConcurrency);
        _scan = scan;
    }

    public Task<FolderSizeResult> ScanAsync(string directory, CancellationToken cancellationToken = default)
        => ScheduleAsync(directory, _scan, cancellationToken);

    public Task<FolderSizeResult> ScanAsync(string directory, IEnumerable<string> excludedPaths, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var exclusions = new TransferExclusions(excludedPaths);
        return ScheduleAsync(directory, (path, token) => ScanDirectory(path, exclusions, token), cancellationToken);
    }

    private async Task<FolderSizeResult> ScheduleAsync(string directory, Func<string, CancellationToken, FolderSizeResult> scan, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        CancellationTokenSource linked;
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _calls++;
        }

        var acquired = false;
        try
        {
            await _slots.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            // A queued request does not consume a worker thread.
            return await Task.Run(() => scan(directory, linked.Token), linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (acquired) _slots.Release();
            linked.Dispose();
            lock (_state)
            {
                _calls--;
                DisposeResourcesIfFinished();
            }
        }
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Cancellation does not wait for a pending filesystem metadata operation.
        try { _lifetime.Cancel(); }
        finally
        {
            lock (_state)
            {
                _cancellationIssued = true;
                DisposeResourcesIfFinished();
            }
        }
    }

    private void DisposeResourcesIfFinished()
    {
        if (!_disposed || !_cancellationIssued || _calls != 0 || _resourcesDisposed) return;
        _resourcesDisposed = true;
        _slots.Dispose();
        _lifetime.Dispose();
    }

    private static FolderSizeResult ScanDirectory(string directory, CancellationToken token)
        => ScanDirectory(directory, new TransferExclusions([]), token);

    private static FolderSizeResult ScanDirectory(string directory, TransferExclusions exclusions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal) || directory.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("Only an absolute local directory can be scanned.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (new DriveInfo(Path.GetPathRoot(root)!).DriveType == DriveType.Network)
            throw new InvalidDataException("Network drives cannot be scanned.");
        // Reject links in any ancestor before examining children.
        for (string? current = root; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A directory path cannot pass through a link or reparse point.");
        }
        if ((File.GetAttributes(root) & FileAttributes.Directory) == 0)
            throw new InvalidDataException("The selected path is not a directory.");

        long bytes = 0, files = 0, folders = 0;
        var skipped = 0;
        var stack = new Stack<IEnumerator<FileSystemInfo>>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };

        void Enter(string path)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                // Refresh metadata immediately before descending into a directory.
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { skipped = checked(skipped + 1); return; }
                stack.Push(new DirectoryInfo(path).EnumerateFileSystemInfos("*", options).GetEnumerator());
            }
            catch (Exception error) when (CanSkip(error)) { skipped = checked(skipped + 1); }
        }

        try
        {
            Enter(root);
            while (stack.TryPeek(out var iterator))
            {
                token.ThrowIfCancellationRequested();
                FileSystemInfo entry;
                try
                {
                    if (!iterator.MoveNext()) { stack.Pop().Dispose(); continue; }
                    entry = iterator.Current;
                }
                catch (Exception error) when (CanSkip(error))
                {
                    stack.Pop().Dispose();
                    skipped = checked(skipped + 1);
                    continue;
                }
                token.ThrowIfCancellationRequested();
                try
                {
                    if (exclusions.IsExcluded(Path.GetRelativePath(root, entry.FullName))) continue;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) { skipped = checked(skipped + 1); continue; }
                    if (entry is DirectoryInfo)
                    {
                        folders = checked(folders + 1);
                        Enter(entry.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        var length = file.Length;
                        bytes = checked(bytes + length);
                        files = checked(files + 1);
                    }
                }
                catch (Exception error) when (CanSkip(error)) { skipped = checked(skipped + 1); }
            }
            token.ThrowIfCancellationRequested();
            return new(bytes, files, folders, skipped);
        }
        finally
        {
            foreach (var iterator in stack) iterator.Dispose();
        }
    }

    private static bool CanSkip(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException;
}

