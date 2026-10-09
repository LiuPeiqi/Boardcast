using System.Diagnostics;
using Boardcast.Core;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Boardcast.ScannerTests-" + Guid.NewGuid().ToString("N"));
    private static int _passed, _failed, _skipped;

    public static async Task<int> Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            await Check("Default concurrency is bounded", async () =>
            {
                using var scanner = new FolderSizeScanner();
                Assert(scanner.MaximumConcurrency is >= 1 and <= 4);
                await Task.CompletedTask;
            });
            await Check("Reject invalid concurrency", async () =>
            {
                Throws<ArgumentOutOfRangeException>(() => new FolderSizeScanner(0));
                Throws<ArgumentOutOfRangeException>(() => new FolderSizeScanner(5));
                await Task.CompletedTask;
            });
            var empty = Directory.CreateDirectory(Path.Combine(Root, "empty")).FullName;
            await Check("Empty folder has zero counts", async () =>
            {
                using var scanner = new FolderSizeScanner();
                var result = await scanner.ScanAsync(empty);
                Assert(result == new FolderSizeResult(0, 0, 0, 0) && result.IsComplete);
            });
            var fixture = Directory.CreateDirectory(Path.Combine(Root, "fixture")).FullName;
            Directory.CreateDirectory(Path.Combine(fixture, "nested", "空文件夹"));
            File.WriteAllBytes(Path.Combine(fixture, "five.bin"), new byte[5]);
            File.WriteAllBytes(Path.Combine(fixture, "nested", "thirteen.bin"), new byte[13]);
            File.WriteAllBytes(Path.Combine(fixture, "nested", "zero.bin"), []);
            await Check("Nested files and empty folders have exact metadata totals", async () =>
            {
                using var scanner = new FolderSizeScanner();
                Assert(await scanner.ScanAsync(fixture) == new FolderSizeResult(18, 3, 2, 0));
            });
            await Check("Directory exclusions prune entire subtrees and keep exact statistics", async () =>
            {
                using var scanner = new FolderSizeScanner();
                var result = await scanner.ScanAsync(fixture, ["nested\\"]);
                var plan = ManifestBuilder.BuildPlan([new TransferSource { Path = fixture, ExcludedPaths = ["nested/"] }], new AppSettings { MaxEntries = 2, MaxTransferBytes = 5 });
                Assert(result == new FolderSizeResult(5, 1, 0, 0) && result.IsComplete);
                Assert(plan.Offer.TotalBytes == result.Bytes && plan.LocalFiles.Count == result.FileCount);
                Assert(plan.Offer.Entries.Count == 2 && plan.Offer.Entries.All(entry => !entry.RelativePath.Contains("nested")));
            });
            await Check("Individual file exclusions retain folders and other files", async () =>
            {
                using var scanner = new FolderSizeScanner();
                string[] exclusions = ["five.bin", @"nested\zero.bin"];
                var result = await scanner.ScanAsync(fixture, exclusions);
                var plan = ManifestBuilder.BuildPlan([new TransferSource { Path = fixture, ExcludedPaths = exclusions.ToList() }], new AppSettings());
                Assert(result == new FolderSizeResult(13, 1, 2, 0) && result.IsComplete);
                Assert(plan.Offer.TotalBytes == result.Bytes && plan.LocalFiles.Count == result.FileCount);
                Assert(plan.Offer.Entries.Count(entry => entry.IsDirectory) == result.FolderCount + 1);
            });
            await Check("All excluded children still send the selected empty folder", async () =>
            {
                using var scanner = new FolderSizeScanner();
                var exclusions = new List<string> { "five.bin", "nested" };
                Assert(await scanner.ScanAsync(fixture, exclusions) == new FolderSizeResult(0, 0, 0, 0));
                var plan = ManifestBuilder.BuildPlan([new TransferSource { Path = fixture, ExcludedPaths = exclusions }], new AppSettings());
                Assert(plan.Offer.Entries.Count == 1 && plan.Offer.Entries[0].IsDirectory && plan.LocalFiles.Count == 0);
            });
            await Check("Exclusions match path segments and remain scoped to their source", async () =>
            {
                var rules = new TransferExclusions(["nested", "nested\\child", "nested"]);
                Assert(rules.IsExcluded("nested") && rules.IsExcluded("nested/child/deep.txt") && !rules.IsExcluded("nested-old/file.txt"));
                Assert(rules.Paths.Count == 2);
                var second = Directory.CreateDirectory(Path.Combine(Root, "second")).FullName;
                File.WriteAllBytes(Path.Combine(second, "five.bin"), [1]);
                var plan = ManifestBuilder.BuildPlan([
                    new TransferSource { Path = fixture, ExcludedPaths = ["five.bin", "nested"] },
                    new TransferSource { Path = second }
                ], new AppSettings());
                Assert(plan.LocalFiles.Count == 1 && plan.Offer.TotalBytes == 1);
                Assert(plan.LocalFiles.Keys.Single() == "second/five.bin");
                await Task.CompletedTask;
            });
            await Check("Reject unsafe, root and outside exclusion paths", async () =>
            {
                string[] invalid = ["", ".", "..", "../five.bin", "nested/../five.bin", "/nested", @"C:\nested", "nested/*", "nested//child"];
                foreach (var path in invalid) Throws<InvalidDataException>(() => new TransferExclusions([path]));
                var paths = TransferExclusions.RelativePaths(fixture, [Path.Combine(fixture, "nested"), Path.Combine(fixture, "five.bin")]);
                Assert(paths.SequenceEqual(new[] { "nested", "five.bin" }));
                Throws<InvalidDataException>(() => TransferExclusions.RelativePaths(fixture, [fixture]));
                Throws<InvalidDataException>(() => TransferExclusions.RelativePaths(fixture, [fixture + "-sibling\\file.txt"]));
                Throws<InvalidDataException>(() => TransferExclusions.RelativePaths(fixture, [Path.Combine(Root, "empty")]));
                Throws<InvalidDataException>(() => TransferExclusions.RelativePaths(fixture, ["relative.txt"]));
                Throws<InvalidDataException>(() => ManifestBuilder.BuildPlan([new TransferSource { Path = Path.Combine(fixture, "five.bin"), ExcludedPaths = ["child"] }], new AppSettings()));
                await Task.CompletedTask;
            });
            await Check("Exclusion scans and manifests preserve cancellation and fresh scans", async () =>
            {
                using var scanner = new FolderSizeScanner();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => scanner.ScanAsync(fixture, ["nested"], cancellation.Token));
                Throws<OperationCanceledException>(() => ManifestBuilder.BuildPlan([new TransferSource { Path = fixture, ExcludedPaths = ["nested"] }], new AppSettings(), cancellation.Token));
                Assert((await scanner.ScanAsync(fixture, ["nested"])).Bytes == 5);
                using var duringEnumeration = new CancellationTokenSource();
                var disposed = false;
                IEnumerable<TransferSource> Sources()
                {
                    try
                    {
                        yield return new TransferSource { Path = fixture, ExcludedPaths = ["nested"] };
                        duringEnumeration.Cancel();
                        yield return new TransferSource { Path = empty };
                    }
                    finally { disposed = true; }
                }
                Throws<OperationCanceledException>(() => ManifestBuilder.BuildPlan(Sources(), new AppSettings(), duringEnumeration.Token));
                Assert(disposed);
            });
            await Check("No file body is opened", async () =>
            {
                using var locked = new FileStream(Path.Combine(fixture, "five.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var scanner = new FolderSizeScanner();
                var result = await scanner.ScanAsync(fixture);
                Assert(result.Bytes == 18 && result.FileCount == 3 && result.IsComplete);
            });
            await Check("Reject a file as scan root", async () =>
            {
                using var scanner = new FolderSizeScanner();
                await ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync(Path.Combine(fixture, "five.bin")));
            });
            await Check("Reject UNC and relative paths before accessing network", async () =>
            {
                using var scanner = new FolderSizeScanner();
                await ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync(@"\\127.0.0.1\missing\folder"));
                await ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync("//127.0.0.1/missing/folder"));
                await ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync("relative-folder"));
            });
            await Check("Pre-canceled request does not scan", async () =>
            {
                var calls = 0;
                using var scanner = new FolderSizeScanner(1, (_, _) => { Interlocked.Increment(ref calls); return new(0, 0, 0, 0); });
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                await Canceled(scanner.ScanAsync(empty, cancellation.Token));
                Assert(calls == 0);
            });
            await Check("Concurrent scans share the instance limit and release slots", async () =>
            {
                using var release = new ManualResetEventSlim();
                using var started = new SemaphoreSlim(0);
                var active = 0; var peak = 0;
                using var scanner = new FolderSizeScanner(2, (_, token) =>
                {
                    var count = Interlocked.Increment(ref active);
                    UpdateMaximum(ref peak, count);
                    started.Release();
                    try { release.Wait(token); return new(0, 0, 0, 0); }
                    finally { Interlocked.Decrement(ref active); }
                });
                var scans = Enumerable.Range(0, 12).Select(_ => scanner.ScanAsync(empty)).ToArray();
                try
                {
                    await started.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await started.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    Assert(active == 2 && peak == 2 && scans.All(task => !task.IsCompleted));
                }
                finally { release.Set(); }
                await Task.WhenAll(scans).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(peak == 2 && active == 0);
                await scanner.ScanAsync(empty).WaitAsync(TimeSpan.FromSeconds(5));
            });
            await Check("Queued cancellation never starts a worker and frees the queue", async () =>
            {
                using var release = new ManualResetEventSlim();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var calls = 0;
                using var scanner = new FolderSizeScanner(1, (_, token) =>
                {
                    Interlocked.Increment(ref calls); started.TrySetResult();
                    release.Wait(token); return new(0, 0, 0, 0);
                });
                var running = scanner.ScanAsync(empty);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var cancellation = new CancellationTokenSource();
                var queued = scanner.ScanAsync(empty, cancellation.Token);
                cancellation.Cancel();
                try { await Canceled(queued); Assert(calls == 1); }
                finally { release.Set(); }
                await running.WaitAsync(TimeSpan.FromSeconds(5));
                await scanner.ScanAsync(empty).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(calls == 2);
            });
            await Check("Running cancellation releases its concurrency slot", async () =>
            {
                using var release = new ManualResetEventSlim();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var scanner = new FolderSizeScanner(1, (_, token) =>
                {
                    started.TrySetResult(); release.Wait(token); return new(0, 0, 0, 0);
                });
                using var cancellation = new CancellationTokenSource();
                var running = scanner.ScanAsync(empty, cancellation.Token);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await Canceled(running);
                release.Set();
                await scanner.ScanAsync(empty).WaitAsync(TimeSpan.FromSeconds(5));
            });
            await Check("Scan exception releases its concurrency slot", async () =>
            {
                var calls = 0;
                using var scanner = new FolderSizeScanner(1, (_, _) =>
                {
                    if (Interlocked.Increment(ref calls) == 1) throw new IOException("Expected test failure");
                    return new(0, 0, 0, 0);
                });
                await ThrowsAsync<IOException>(() => scanner.ScanAsync(empty));
                await scanner.ScanAsync(empty).WaitAsync(TimeSpan.FromSeconds(5));
                Assert(calls == 2);
            });
            await Check("Dispose cancels active and queued scans without blocking", async () =>
            {
                using var release = new ManualResetEventSlim();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var scanner = new FolderSizeScanner(1, (_, token) =>
                {
                    started.TrySetResult(); release.Wait(token); return new(0, 0, 0, 0);
                });
                var running = scanner.ScanAsync(empty);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var queued = scanner.ScanAsync(empty);
                var timer = Stopwatch.StartNew();
                scanner.Dispose();
                Assert(timer.Elapsed < TimeSpan.FromSeconds(1));
                await Canceled(running); await Canceled(queued);
                scanner.Dispose();
                await ThrowsAsync<ObjectDisposedException>(() => scanner.ScanAsync(empty));
            });
            await Check("Dispose and completion race does not release disposed semaphores", async () =>
            {
                for (var round = 0; round < 30; round++)
                {
                    var scanner = new FolderSizeScanner(2);
                    var tasks = Enumerable.Range(0, 8).Select(_ => scanner.ScanAsync(empty)).ToArray();
                    await Task.Run(scanner.Dispose);
                    foreach (var task in tasks)
                    {
                        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                        catch (OperationCanceledException) { }
                    }
                }
            });
            await Check("Child links are skipped and marked incomplete; root links are rejected", async () =>
            {
                var linkedRoot = Path.Combine(Root, "root-link");
                var cycle = Path.Combine(fixture, "cycle");
                try
                {
                    Directory.CreateSymbolicLink(cycle, fixture);
                    Directory.CreateSymbolicLink(linkedRoot, fixture);
                }
                catch (Exception error) when (error is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
                {
                    _skipped++;
                    Console.WriteLine("  Symbolic-link check unavailable: " + error.Message);
                    return;
                }
                using var scanner = new FolderSizeScanner();
                var result = await scanner.ScanAsync(fixture);
                Assert(result.Bytes == 18 && result.FileCount == 3 && result.FolderCount == 2 && result.SkippedCount == 1 && !result.IsComplete);
                var excluded = await scanner.ScanAsync(fixture, ["cycle"]);
                Assert(excluded == new FolderSizeResult(18, 3, 2, 0) && excluded.IsComplete);
                var plan = ManifestBuilder.BuildPlan([new TransferSource { Path = fixture, ExcludedPaths = ["cycle"] }], new AppSettings());
                Assert(plan.Offer.TotalBytes == 18 && plan.LocalFiles.Count == 3 && plan.Offer.Entries.All(entry => !entry.RelativePath.Contains("cycle")));
                Throws<InvalidDataException>(() => ManifestBuilder.BuildPlan([fixture], new AppSettings()));
                await ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync(linkedRoot));
                await ThrowsAsync<InvalidDataException>(() => scanner.ScanAsync(Path.Combine(linkedRoot, "nested")));
            });
        }
        finally { Directory.Delete(Root, true); }
        Console.WriteLine($"Scanner tests: {_passed} passed, {_failed} failed, {_skipped} unavailable checks.");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Check(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { _failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
    }

    private static void Assert(bool value)
    {
        if (!value) throw new InvalidOperationException("Assertion failed.");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task Canceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { Assert(task.IsCanceled); return; }
        throw new InvalidOperationException("Expected a canceled task.");
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int current;
        do { current = Volatile.Read(ref maximum); if (current >= value) return; }
        while (Interlocked.CompareExchange(ref maximum, value, current) != current);
    }
}

