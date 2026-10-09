using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Boardcast.Core;

/// <summary>Receive lifecycle regressions through the actual mutually authenticated TLS server.</summary>
public static class ReceiveStorageScenarios
{
    private const int MiB = 1024 * 1024;

    public static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            checks++;
            Console.WriteLine("PASS " + label);
        }
        string scenarioRoot = Directory.CreateDirectory(Path.Combine(root, "receive-storage-scenarios")).FullName;
        await using var sender = new Endpoint(scenarioRoot, "sender");
        await using var receiver = new Endpoint(scenarioRoot, "receiver");
        await using var attacker = new Endpoint(scenarioRoot, "attacker");
        Directory.CreateDirectory(receiver.Settings.ReceiveDirectory);
        string sourceRoot = Directory.CreateDirectory(Path.Combine(scenarioRoot, "sources")).FullName;

        // Ordinary receive conflicts include both file/directory type mismatches and existing trees.
        await File.WriteAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "clash.txt"), "user-owned original");
        string clash = Path.Combine(sourceRoot, "clash.txt");
        await File.WriteAllTextAsync(clash, "incoming file");
        var clashPlan = ManifestBuilder.BuildPlan([clash], sender.Settings);
        await SendAsync(sender, receiver, clashPlan);
        var clashResult = await TerminalAsync(receiver, clashPlan.Offer.Id, "completed");
        Check(clashResult.Destination == Path.GetFullPath(receiver.Settings.ReceiveDirectory), "Single received file uses the confirmed directory as its destination");
        Check(await File.ReadAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "clash.txt")) == "user-owned original", "Same-name receive preserves the original user file");
        Check(await File.ReadAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "clash (1).txt")) == "incoming file", "Same-name file is safely renamed without replacing the original");
        Check(clashResult.CompletedFiles == 1, "One verified published file is reported as completed");

        string package = Directory.CreateDirectory(Path.Combine(sourceRoot, "package")).FullName;
        Directory.CreateDirectory(Path.Combine(package, "nested", "empty"));
        await File.WriteAllTextAsync(Path.Combine(package, "nested", "document.txt"), "nested content");
        await File.WriteAllBytesAsync(Path.Combine(package, "zero.bin"), []);
        Directory.CreateDirectory(Path.Combine(receiver.Settings.ReceiveDirectory, "package"));
        await File.WriteAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "package", "keep.txt"), "keep this tree");
        var packagePlan = ManifestBuilder.BuildPlan([package], sender.Settings);
        await SendAsync(sender, receiver, packagePlan);
        var packageResult = await TerminalAsync(receiver, packagePlan.Offer.Id, "completed");
        string receivedPackage = Path.Combine(receiver.Settings.ReceiveDirectory, "package (1)");
        Check(await File.ReadAllTextAsync(Path.Combine(receivedPackage, "nested", "document.txt")) == "nested content", "Directory collision renames the incoming root while retaining its hierarchy");
        Check(await File.ReadAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "package", "keep.txt")) == "keep this tree", "Directory collision leaves the existing tree untouched");
        Check(Directory.Exists(Path.Combine(receivedPackage, "nested", "empty")) && new FileInfo(Path.Combine(receivedPackage, "zero.bin")).Length == 0, "Empty files and empty nested directories are published directly");
        Check(packageResult.CompletedFiles == 2, "Directory entries do not inflate the completed-file count");
        string onlyEmpty = Directory.CreateDirectory(Path.Combine(sourceRoot, "only-empty")).FullName;
        var onlyEmptyPlan = ManifestBuilder.BuildPlan([onlyEmpty], sender.Settings);
        await SendAsync(sender, receiver, onlyEmptyPlan);
        var onlyEmptyResult = await TerminalAsync(receiver, onlyEmptyPlan.Offer.Id, "completed");
        Check(Directory.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "only-empty")) && onlyEmptyResult.CompletedFiles == 0,
            "A batch containing only an empty directory completes without inventing a file or wrapper");

        string incomingDirectory = Directory.CreateDirectory(Path.Combine(sourceRoot, "file-blocked-directory")).FullName;
        await File.WriteAllTextAsync(Path.Combine(incomingDirectory, "child.txt"), "child");
        await File.WriteAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "file-blocked-directory"), "original blocker");
        var directoryPlan = ManifestBuilder.BuildPlan([incomingDirectory], sender.Settings);
        await SendAsync(sender, receiver, directoryPlan);
        await TerminalAsync(receiver, directoryPlan.Offer.Id, "completed");
        Check(await File.ReadAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "file-blocked-directory (1)", "child.txt")) == "child" &&
            await File.ReadAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "file-blocked-directory")) == "original blocker", "Incoming directory safely renames around an existing file");

        string incomingFile = Path.Combine(sourceRoot, "directory-blocked-file.txt");
        await File.WriteAllTextAsync(incomingFile, "incoming around directory");
        Directory.CreateDirectory(Path.Combine(receiver.Settings.ReceiveDirectory, "directory-blocked-file.txt"));
        var filePlan = ManifestBuilder.BuildPlan([incomingFile], sender.Settings);
        await SendAsync(sender, receiver, filePlan);
        await TerminalAsync(receiver, filePlan.Offer.Id, "completed");
        Check(Directory.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "directory-blocked-file.txt")) &&
            await File.ReadAllTextAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "directory-blocked-file (1).txt")) == "incoming around directory", "Incoming file safely renames around an existing directory");
        Check(NoReceiveArtifacts(receiver.Settings.ReceiveDirectory), "Successful receives leave no task folders, checksum JSON, or temporary copies beside user files");

        // Force legacy streaming, so this also covers old peers that do not negotiate chunks.
        byte[] legacyComplete = RandomNumberGenerator.GetBytes(64 * 1024 + 7);
        var legacyOffer = Offer(sender, [new() { RelativePath = "legacy-unused-empty", IsDirectory = true }, new() { RelativePath = "legacy-complete.bin", Size = legacyComplete.Length }, new() { RelativePath = "legacy-partial.bin", Size = 2 * MiB }]);
        await using (var legacy = await RawSession.ConnectAsync(sender, receiver, legacyOffer))
        {
            await legacy.WriteFileAsync(legacyComplete);
            await WaitForAsync(() => File.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "legacy-complete.bin")), "legacy per-file publication");
            Check(Enumerable.SequenceEqual(legacyComplete, await File.ReadAllBytesAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "legacy-complete.bin"))), "Legacy streaming publishes a verified file before the whole batch ends");
            await legacy.Stream.WriteAsync(RandomNumberGenerator.GetBytes(256 * 1024));
            await legacy.Stream.FlushAsync();
            Check(await receiver.Service.CancelAsync(legacyOffer.Id).WaitAsync(TimeSpan.FromSeconds(8)), "Receive cancellation waits for legacy writes and cleanup to finish");
        }
        var legacyCancelled = await TerminalAsync(receiver, legacyOffer.Id, "cancelled");
        Check(legacyCancelled.CompletedFiles == 1 && !legacyCancelled.CanResume && legacyCancelled.Message.Contains('1'), "Legacy cancellation reports the preserved completed-file count and disables resume");
        Check(File.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "legacy-complete.bin")) && !File.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "legacy-partial.bin")), "Legacy cancellation keeps the completed file and removes the unfinished file");
        Check(!InternalFiles(receiver, "legacy-partial.bin").Any() && NoReceiveArtifacts(receiver.Settings.ReceiveDirectory), "Legacy cancellation removes its intermediates without leaving artifacts in the destination");
        Check(!Directory.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "legacy-unused-empty")) && Directory.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "only-empty")),
            "Cancellation removes its own unused empty directories and preserves earlier received directories");

        var corruptOffer = Offer(sender, [new() { RelativePath = "corrupt-legacy.bin", Size = 3 }]);
        await using (var corrupt = await RawSession.ConnectAsync(sender, receiver, corruptOffer))
        {
            await corrupt.Stream.WriteAsync(new byte[] { 1, 2, 3 });
            await corrupt.Stream.WriteAsync(new byte[32]);
            var rejection = await WireProtocol.ReadAsync<WireMessage>(corrupt.Stream).WaitAsync(TimeSpan.FromSeconds(8));
            Check(rejection.Type == "reject", "Bad legacy SHA256 is rejected over TLS");
        }
        await TerminalAsync(receiver, corruptOffer.Id, "failed");
        Check(!File.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "corrupt-legacy.bin")), "Failed integrity validation never publishes a formal file");
        Check(await receiver.Service.CancelAsync(corruptOffer.Id), "Explicit discard can clean an inactive failed receive task");
        Check(!InternalFiles(receiver, "corrupt-legacy.bin").Any(), "Discarding a failed integrity task cleans the invalid temporary bytes");

        string corruptChunkSource = Path.Combine(sourceRoot, "corrupt-chunk.bin");
        byte[] validChunk = RandomNumberGenerator.GetBytes(MiB);
        await File.WriteAllBytesAsync(corruptChunkSource, validChunk);
        var corruptChunkPlan = ManifestBuilder.BuildPlan([corruptChunkSource], sender.Settings);
        corruptChunkPlan.Offer.SenderName = sender.Peer.Name;
        corruptChunkPlan.Offer.SenderFingerprint = sender.Peer.Fingerprint;
        var corruptManifest = await ChunkTransfer.BuildManifestAsync(corruptChunkPlan);
        await using (var corruptChunk = await RawSession.ConnectAsync(sender, receiver, corruptChunkPlan.Offer, chunks: true))
        {
            await WireProtocol.WriteAsync(corruptChunk.Stream, new WireMessage
            {
                Type = "chunk-start", Chunks = corruptManifest, Grant = SignGrant(sender, receiver, corruptChunkPlan.Offer, corruptManifest), ActiveRecipients = 1
            });
            var requested = await WireProtocol.ReadAsync<WireMessage>(corruptChunk.Stream).WaitAsync(TimeSpan.FromSeconds(8));
            if (requested.Type != "chunk" || requested.Chunk is null) throw new Exception("Receiver did not request the corrupt-chunk test payload");
            validChunk[0] ^= 0xff;
            await WireProtocol.WriteAsync(corruptChunk.Stream, new WireMessage { Type = "chunk", Chunk = requested.Chunk, ChunkBytes = validChunk.Length, ActiveRecipients = 1 });
            await corruptChunk.Stream.WriteAsync(validChunk);
            await corruptChunk.Stream.FlushAsync();
            var rejection = await WireProtocol.ReadAsync<WireMessage>(corruptChunk.Stream).WaitAsync(TimeSpan.FromSeconds(8));
            Check(rejection.Type == "reject", "A validly authorized TLS chunk with incorrect content is rejected");
        }
        await TerminalAsync(receiver, corruptChunkPlan.Offer.Id, "failed");
        Check(!File.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "corrupt-chunk.bin")), "Bad chunk integrity never publishes an official file");

        // Concurrent chunk workers may finish a small file while a large file is still incomplete.
        var partial = await CreateBatchAsync(sourceRoot, "cancel", sender.Settings);
        bool cancelled = false;
        receiver.Observe = value =>
        {
            if (value.Id == partial.Plan.Offer.Id && value.State == "receiving" && value.CompletedFiles >= 1 && value.BytesTransferred < value.TotalBytes && !cancelled)
                cancelled = receiver.Service.Cancel(value.Id);
        };
        await SendAsync(sender, receiver, partial.Plan);
        receiver.Observe = null;
        await receiver.Service.CancelAsync(partial.Plan.Offer.Id);
        var partialCancelled = await TerminalAsync(receiver, partial.Plan.Offer.Id, "cancelled");
        Check(cancelled && partialCancelled.CompletedFiles == 1 && !partialCancelled.CanResume, "Explicit chunk cancellation preserves the published small file and discards continuation");
        Check(Enumerable.SequenceEqual(partial.First, await File.ReadAllBytesAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "cancel-first.bin"))) &&
            !File.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "cancel-second.bin")), "Partial chunk cancellation preserves exact completed bytes and exposes no incomplete file");
        Check(!InternalFiles(receiver, "cancel-second.bin").Any() && NoReceiveArtifacts(receiver.Settings.ReceiveDirectory), "Explicit chunk cancellation deletes incomplete chunk storage");

        var interrupted = await CreateBatchAsync(sourceRoot, "network", sender.Settings);
        using var networkDrop = new CancellationTokenSource();
        var publishedBeforeDrop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseInterruptedReceive = new ManualResetEventSlim();
        int dropGateReached = 0;
        receiver.Observe = value =>
        {
            if (value.Id != interrupted.Plan.Offer.Id || value.State != "receiving" || value.CompletedFiles < 1 || value.BytesTransferred >= value.TotalBytes) return;
            if (Interlocked.CompareExchange(ref dropGateReached, 1, 0) == 0) publishedBeforeDrop.TrySetResult();
            if (!releaseInterruptedReceive.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Network interruption test gate was not released");
        };
        Task interruptedSend = SendAsync(sender, receiver, interrupted.Plan, networkDrop.Token);
        try
        {
            await publishedBeforeDrop.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(!await attacker.Service.CancelRemoteReceiveAsync(receiver.Peer, interrupted.Plan.Offer.Id, CancellationToken.None),
                "Another authenticated TLS sender cannot cancel an active transfer by knowing its ID");
            networkDrop.Cancel();
        }
        finally { receiver.Observe = null; releaseInterruptedReceive.Set(); }
        await interruptedSend;
        await WaitForAsync(() => receiver.Progress.TryGetValue(interrupted.Plan.Offer.Id, out var value) && value.State is "failed" or "paused" or "cancelled", "network interruption after publication");
        Check(dropGateReached == 1 && receiver.Progress[interrupted.Plan.Offer.Id].CanResume && InternalFiles(receiver, "network-second.bin").Any(), "Unexpected origin disconnect retains recoverable unfinished chunks");
        Check(!await attacker.Service.CancelRemoteReceiveAsync(receiver.Peer, interrupted.Plan.Offer.Id, CancellationToken.None),
            "Another authenticated TLS sender cannot discard a persisted interrupted transfer by knowing its ID");
        Check(receiver.Progress[interrupted.Plan.Offer.Id].CanResume && InternalFiles(receiver, "network-second.bin").Any(),
            "Rejected foreign cancellation leaves the original sender's recovery data available");
        string completedBeforeResume = Path.Combine(receiver.Settings.ReceiveDirectory, "network-first.bin");
        var publishedAt = File.GetLastWriteTimeUtc(completedBeforeResume);
        await receiver.RestartAsync();
        await SendAsync(sender, receiver, interrupted.Plan);
        var recovered = await TerminalAsync(receiver, interrupted.Plan.Offer.Id, "completed");
        Check(recovered.CompletedFiles == 2 && recovered.ReusedBytes >= interrupted.First.Length, "Receiver restart restores the per-file publication record and verified progress");
        Check(File.GetLastWriteTimeUtc(completedBeforeResume) == publishedAt && Directory.EnumerateFiles(receiver.Settings.ReceiveDirectory, "network-first*.bin").Count() == 1, "Network retry never republishes or duplicates a file completed before interruption");
        Check(Enumerable.SequenceEqual(interrupted.Second, await File.ReadAllBytesAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "network-second.bin"))), "Restarted network recovery publishes the remaining file with exact bytes");
        await receiver.RestartAsync();
        await SendAsync(sender, receiver, interrupted.Plan);
        await TerminalAsync(receiver, interrupted.Plan.Offer.Id, "completed");
        Check(Directory.EnumerateFiles(receiver.Settings.ReceiveDirectory, "network-*.bin").Count() == 2 && NoReceiveArtifacts(receiver.Settings.ReceiveDirectory), "Completion acknowledgement retries remain idempotent after a receiver restart");

        // Cancel from the completion notification, when the file's final publication is already durable.
        string raceSource = Path.Combine(sourceRoot, "completion-race.bin");
        byte[] raceBytes = RandomNumberGenerator.GetBytes(MiB + 13);
        await File.WriteAllBytesAsync(raceSource, raceBytes);
        var racePlan = ManifestBuilder.BuildPlan([raceSource], sender.Settings);
        receiver.Observe = value =>
        {
            if (value.Id == racePlan.Offer.Id && value.State == "completed") _ = receiver.Service.Cancel(value.Id);
        };
        await SendAsync(sender, receiver, racePlan);
        receiver.Observe = null;
        var race = await TerminalAsync(receiver, racePlan.Offer.Id, "completed");
        Check(race.CompletedFiles == 1 && Enumerable.SequenceEqual(raceBytes, await File.ReadAllBytesAsync(Path.Combine(receiver.Settings.ReceiveDirectory, "completion-race.bin"))), "Cancellation racing with completion never deletes the verified published result");
        Check(!race.CanResume && NoReceiveArtifacts(receiver.Settings.ReceiveDirectory), "Completion wins a late cancellation and leaves a clean receive destination");

        if (OperatingSystem.IsWindows())
        {
            // A real Windows sharing violation keeps the cache alive until restart retries cancellation cleanup.
            string lockedSource = Path.Combine(sourceRoot, "locked-cleanup.bin");
            await File.WriteAllBytesAsync(lockedSource, RandomNumberGenerator.GetBytes(16 * MiB + 3));
            var lockedPlan = ManifestBuilder.BuildPlan([lockedSource], sender.Settings);
            FileStream? heldFile = null;
            string? heldPath = null;
            bool lockedCancelled = false;
            receiver.Observe = value =>
            {
                if (value.Id != lockedPlan.Offer.Id || value.State != "receiving" || value.BytesTransferred < MiB || lockedCancelled) return;
                heldPath = InternalFiles(receiver, "locked-cleanup.bin").Single();
                heldFile = new FileStream(heldPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                lockedCancelled = receiver.Service.Cancel(value.Id);
            };
            try
            {
                await SendAsync(sender, receiver, lockedPlan);
                receiver.Observe = null;
                await receiver.Service.CancelAsync(lockedPlan.Offer.Id);
                var locked = await TerminalAsync(receiver, lockedPlan.Offer.Id, "cancelled");
                Check(lockedCancelled && !locked.CanResume && locked.CanDiscard && heldPath is not null && File.Exists(heldPath), "Temporary deletion failure keeps a cancelled task recorded for later cleanup");
            }
            finally { receiver.Observe = null; heldFile?.Dispose(); }
            await receiver.RestartAsync();
            await WaitForAsync(() => !InternalFiles(receiver, "locked-cleanup.bin").Any(), "restart retries cancelled-cache cleanup");
            Check(!File.Exists(Path.Combine(receiver.Settings.ReceiveDirectory, "locked-cleanup.bin")), "Restart retries pending cancellation cleanup without publishing the unfinished file");
            Check(NoReceiveArtifacts(receiver.Settings.ReceiveDirectory), "Deferred cleanup is limited to internal temporary data and preserves the clean destination");
        }
        return checks;
    }

    private static TransferOffer Offer(Endpoint sender, List<TransferEntry> entries) => new()
    {
        SenderName = sender.Peer.Name, SenderFingerprint = sender.Peer.Fingerprint,
        Entries = entries
    };
    private static ChunkGrant SignGrant(Endpoint sender, Endpoint receiver, TransferOffer offer, ChunkManifest manifest)
    {
        var grant = new ChunkGrant
        {
            GrantId = Guid.NewGuid().ToString("N"), BatchId = string.IsNullOrEmpty(offer.BatchId) ? offer.Id : offer.BatchId,
            ContentId = manifest.Id, SenderFingerprint = sender.Peer.Fingerprint, RecipientFingerprint = receiver.Peer.Fingerprint,
            ExpiresUnixSeconds = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        };
        byte[] signed = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            grant.GrantId, grant.BatchId, grant.ContentId, grant.SenderFingerprint, grant.RecipientFingerprint,
            grant.ExpiresUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        using var key = sender.Certificate.GetECDsaPrivateKey() ?? throw new Exception("Test sender has no signing key");
        grant.Signature = Convert.ToBase64String(key.SignData(signed, HashAlgorithmName.SHA256));
        return grant;
    }
    private static IEnumerable<string> InternalFiles(Endpoint endpoint, string name)
    {
        string tasks = Path.Combine(endpoint.DataDirectory, "receive-tasks");
        return Directory.Exists(tasks) ? Directory.EnumerateFiles(tasks, name, SearchOption.AllDirectories).ToArray() : [];
    }
    private static bool NoReceiveArtifacts(string destination) =>
        !Directory.EnumerateFileSystemEntries(destination, "Boardcast_*", SearchOption.TopDirectoryOnly).Any() &&
        !Directory.EnumerateFileSystemEntries(destination, ".boardcast-*", SearchOption.AllDirectories).Any();
    private static async Task<(TransferPlan Plan, byte[] First, byte[] Second)> CreateBatchAsync(string root, string prefix, AppSettings settings)
    {
        byte[] first = RandomNumberGenerator.GetBytes(256 * 1024 + 19);
        byte[] second = RandomNumberGenerator.GetBytes(24 * MiB + 31);
        string firstPath = Path.Combine(root, prefix + "-first.bin");
        string secondPath = Path.Combine(root, prefix + "-second.bin");
        await File.WriteAllBytesAsync(firstPath, first);
        await File.WriteAllBytesAsync(secondPath, second);
        return (ManifestBuilder.BuildPlan([firstPath, secondPath], settings), first, second);
    }
    private static async Task SendAsync(Endpoint sender, Endpoint receiver, TransferPlan plan, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await sender.Service.SendAsync(plan, receiver.Peer, timeout.Token).WaitAsync(TimeSpan.FromSeconds(35));
    }
    private static async Task<TransferProgress> TerminalAsync(Endpoint endpoint, string id, string expected)
    {
        await WaitForAsync(() => endpoint.Progress.TryGetValue(id, out var value) && value.State is "completed" or "failed" or "rejected" or "cancelled", "terminal receive state");
        var result = endpoint.Progress[id];
        if (result.State != expected) throw new Exception($"Receive {id}: expected {expected}, got {result.State}: {result.Message}");
        return result;
    }
    private static async Task WaitForAsync(Func<bool> condition, string operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            try { await Task.Delay(10, timeout.Token); }
            catch (OperationCanceledException) { throw new Exception("Timed out waiting for " + operation); }
        }
    }

    private sealed class RawSession(TcpClient client, SslStream stream) : IAsyncDisposable
    {
        public SslStream Stream => stream;
        public static async Task<RawSession> ConnectAsync(Endpoint sender, Endpoint receiver, TransferOffer offer, bool chunks = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var client = new TcpClient();
            SslStream? tls = null;
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, receiver.Peer.Port, timeout.Token);
                tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null && IdentityStore.GetFingerprint(certificate) == receiver.Peer.Fingerprint);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "Boardcast", ClientCertificates = new X509CertificateCollection { sender.Certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, timeout.Token);
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "hello", Peer = sender.Peer, Capabilities = chunks ? ["chunks-v1"] : [] }, timeout.Token);
                if ((await WireProtocol.ReadAsync<WireMessage>(tls, timeout.Token)).Type != "hello") throw new Exception("Receiver did not authenticate legacy TLS hello");
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "offer", Offer = offer, Capabilities = chunks ? ["chunks-v1"] : [] }, timeout.Token);
                WireMessage decision;
                do { decision = await WireProtocol.ReadAsync<WireMessage>(tls, timeout.Token); } while (decision.Type == "pending");
                if (decision.Type != "accept") throw new Exception("Receiver rejected legacy scenario: " + decision.Message);
                return new RawSession(client, tls);
            }
            catch { if (tls is not null) await tls.DisposeAsync(); client.Dispose(); throw; }
        }
        public async Task WriteFileAsync(byte[] bytes)
        {
            await stream.WriteAsync(bytes);
            await stream.WriteAsync(SHA256.HashData(bytes));
            await stream.FlushAsync();
        }
        public async ValueTask DisposeAsync() { await stream.DisposeAsync(); client.Dispose(); }
    }

    private sealed class Endpoint : IAsyncDisposable
    {
        private IdentityStore identity;
        private CancellationTokenSource stop = new();
        private Task listener = Task.CompletedTask;
        public string DataDirectory { get; }
        public X509Certificate2 Certificate => identity.Certificate;
        public AppSettings Settings { get; }
        public PeerInfo Peer { get; private set; }
        public TransferService Service { get; private set; } = null!;
        public ConcurrentDictionary<string, TransferProgress> Progress { get; } = new();
        public Action<TransferProgress>? Observe { get; set; }
        public Endpoint(string root, string name)
        {
            DataDirectory = Path.Combine(root, name + "-identity");
            identity = new IdentityStore(DataDirectory);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Settings = new AppSettings { DeviceName = name, Port = port, ReceiveDirectory = Path.Combine(root, name + "-received") };
            Peer = new PeerInfo { Name = name, Fingerprint = identity.Fingerprint, Address = "127.0.0.1", Port = port, Platform = "windows" };
            Start();
        }
        private void Start()
        {
            Service = new TransferService(identity, () => Settings, () => Peer,
                (_, _, _) => Task.FromResult(new IncomingDecision(true, Settings.ReceiveDirectory, "Test accepted")),
                value => { Progress[value.Id] = value; Observe?.Invoke(value); }, _ => { });
            Service.Diagnostic += value => Console.WriteLine($"Receive storage {Peer.Name}: {value}");
            listener = Service.RunAsync(stop.Token);
        }
        public async Task RestartAsync()
        {
            await StopAsync(); stop.Dispose(); identity.Dispose();
            identity = new IdentityStore(DataDirectory); Peer = Peer with { Fingerprint = identity.Fingerprint };
            stop = new CancellationTokenSource(); Start();
        }
        private async Task StopAsync()
        {
            stop.Cancel();
            try { await listener.WaitAsync(TimeSpan.FromSeconds(8)); } catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); } finally { stop.Dispose(); identity.Dispose(); }
        }
    }
}