using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Boardcast.Core;

public static class ChunkTransportScenarios
{
    private const int MiB = 1024 * 1024;

    public static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
            Console.WriteLine("PASS " + message);
        }

        string scenarioRoot = Directory.CreateDirectory(Path.Combine(root, "chunk-scenarios")).FullName;
        await using var sender = new Endpoint(scenarioRoot, "origin");
        await using var first = new Endpoint(scenarioRoot, "first");
        await using var second = new Endpoint(scenarioRoot, "second");

        // Interrupt the origin connection on a verified chunk boundary; explicit receive cancellation is tested separately.
        var resumable = await CreatePlanAsync(scenarioRoot, "resume.bin", 8 * MiB + 91, sender.Settings);
        using var resumeInterruption = new CancellationTokenSource();
        bool cancelled = false;
        first.Observe = p =>
        {
            if (p.Id == resumable.Plan.Offer.Id && p.State == "receiving" && p.BytesTransferred >= MiB && !cancelled)
                { cancelled = true; resumeInterruption.Cancel(); }
        };
        await SendAsync(sender, first, resumable.Plan, resumeInterruption.Token);
        await WaitForAsync(() => first.Progress.TryGetValue(resumable.Plan.Offer.Id, out var p) && p.State is "failed" or "paused" or "cancelled", "chunk cancellation");
        Check(cancelled && first.Progress[resumable.Plan.Offer.Id].CanResume, "Interrupted chunk transfer retains recoverable state");
        Check(!File.Exists(Path.Combine(first.Settings.ReceiveDirectory, "resume.bin")), "Interrupted chunks are not exposed as completed files");
        first.Observe = null;
        await SendAsync(sender, first, resumable.Plan);
        var resumed = await CompletedAsync(first, resumable.Plan.Offer.Id);
        Check(resumed.ReusedBytes >= MiB, "Retry reuses verified chunks after an interrupted origin connection");
        Check(await MatchesAsync(resumed, "resume.bin", resumable.Bytes), "Resumed transfer commits byte-identical content");
        Check(!resumed.CanResume && !sender.Progress[resumable.Plan.Offer.Id].CanResume, "Completed transfer clears resume availability on both endpoints");

        int committedFiles = Directory.EnumerateFiles(first.Settings.ReceiveDirectory, "resume*.bin").Count();
        await SendAsync(sender, first, resumable.Plan);
        var acknowledgedAgain = await CompletedAsync(first, resumable.Plan.Offer.Id);
        Check(acknowledgedAgain.ReusedBytes == resumable.Bytes.Length && acknowledgedAgain.Destination == resumed.Destination,
            "Retry after an ambiguous completion acknowledges the already verified result");
        Check(Directory.EnumerateFiles(first.Settings.ReceiveDirectory, "resume*.bin").Count() == committedFiles,
            "Repeated completion acknowledgement does not create a duplicate destination");
        using var restartInterruption = new CancellationTokenSource();
        var restart = await CreatePlanAsync(scenarioRoot, "restart.bin", 8 * MiB + 113, sender.Settings);
        cancelled = false;
        first.Observe = p =>
        {
            if (p.Id == restart.Plan.Offer.Id && p.State == "receiving" && p.BytesTransferred >= 3 * MiB && !cancelled)
                { cancelled = true; restartInterruption.Cancel(); }
        };
        await SendAsync(sender, first, restart.Plan, restartInterruption.Token);
        await WaitForAsync(() => first.Progress.TryGetValue(restart.Plan.Offer.Id, out var p) && p.State is "failed" or "paused" or "cancelled", "restart cancellation");
        Check(cancelled, "Restart scenario stops after durable chunks are received");
        first.Observe = null;
        string fingerprint = first.Peer.Fingerprint;
        await first.RestartAsync();
        Check(first.Peer.Fingerprint == fingerprint, "Transfer service restart preserves the device identity");
        await SendAsync(sender, first, restart.Plan);
        var restarted = await CompletedAsync(first, restart.Plan.Offer.Id);
        Check(restarted.ReusedBytes >= 3 * MiB, "New service instance resumes chunks persisted before restart");
        Check(await MatchesAsync(restarted, "restart.bin", restart.Bytes), "Restart recovery commits byte-identical content");

        using var damagedInterruption = new CancellationTokenSource();
        var damaged = await CreatePlanAsync(scenarioRoot, "damaged.bin", 8 * MiB + 67, sender.Settings);
        cancelled = false;
        first.Observe = p =>
        {
            if (p.Id == damaged.Plan.Offer.Id && p.State == "receiving" && p.BytesTransferred >= 3 * MiB && !cancelled)
                { cancelled = true; damagedInterruption.Cancel(); }
        };
        await SendAsync(sender, first, damaged.Plan, damagedInterruption.Token);
        await WaitForAsync(() => first.Progress.TryGetValue(damaged.Plan.Offer.Id, out var p) && p.State is "failed" or "paused" or "cancelled", "cache-damage cancellation");
        first.Observe = null;
        string damagedCache = Directory.EnumerateFiles(Path.Combine(first.DataDirectory, "receive-tasks"), "damaged.bin", SearchOption.AllDirectories).Single();
        await using (var file = new FileStream(damagedCache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            int original = file.ReadByte();
            file.Position = 0;
            file.WriteByte((byte)(original ^ 0xff));
            await file.FlushAsync();
        }
        await first.RestartAsync();
        await SendAsync(sender, first, damaged.Plan);
        var repaired = await CompletedAsync(first, damaged.Plan.Offer.Id);
        Check(repaired.ReusedBytes > 0 && repaired.ReusedBytes < repaired.TotalBytes, "Recovery reuses valid cached chunks while re-fetching damaged chunks");
        Check(await MatchesAsync(repaired, "damaged.bin", damaged.Bytes), "Tampered durable cache cannot corrupt the committed file");
        // An unchanged length cannot identify content: rebuilding hashes must observe the new bytes.
        using var changedInterruption = new CancellationTokenSource();
        var changed = await CreatePlanAsync(scenarioRoot, "changed.bin", 6 * MiB + 31, sender.Settings);
        cancelled = false;
        first.Observe = p =>
        {
            if (p.Id == changed.Plan.Offer.Id && p.State == "receiving" && p.BytesTransferred >= MiB && !cancelled)
                { cancelled = true; changedInterruption.Cancel(); }
        };
        await SendAsync(sender, first, changed.Plan, changedInterruption.Token);
        await WaitForAsync(() => first.Progress.TryGetValue(changed.Plan.Offer.Id, out var p) && p.State is "failed" or "paused" or "cancelled", "changed-source cancellation");
        first.Observe = null;
        var newBytes = RandomNumberGenerator.GetBytes(changed.Bytes.Length);
        await File.WriteAllBytesAsync(Path.Combine(scenarioRoot, "changed.bin"), newBytes);
        var changedRetry = ManifestBuilder.BuildPlan([Path.Combine(scenarioRoot, "changed.bin")], sender.Settings);
        changedRetry.Offer.Id = changed.Plan.Offer.Id;
        changedRetry.Offer.BatchId = changed.Plan.Offer.BatchId;
        await SendAsync(sender, first, changedRetry);
        var changedResult = await CompletedAsync(first, changed.Plan.Offer.Id);
        Check(changedResult.ReusedBytes == 0, "Same-length source changes do not reuse chunks from the old content");
        Check(await MatchesAsync(changedResult, "changed.bin", newBytes), "Retry rebuilds the manifest for changed source bytes");

        var shared = await CreatePlanAsync(scenarioRoot, "shared.bin", 128 * MiB + 47, sender.Settings);
        shared.Plan.Offer.BatchId = Guid.NewGuid().ToString("N");
        var sharedManifest = await ChunkTransfer.BuildManifestAsync(shared.Plan, CancellationToken.None);
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        first.Observe = p =>
        {
            if (p.Id != shared.Plan.Offer.Id || p.State != "receiving" || p.BytesTransferred < 16 * MiB) return;
            firstReady.TrySetResult();
            if (!releaseFirst.Wait(TimeSpan.FromSeconds(40))) throw new TimeoutException("Concurrent recipient was not released");
        };
        Task firstSend = SendAsync(sender, first, shared.Plan);
        var secondPlan = shared.Plan with { Offer = shared.Plan.Offer with { Id = Guid.NewGuid().ToString("N") } };
        bool seedWentOffline = false;
        second.Observe = p =>
        {
            if (p.Id != secondPlan.Offer.Id || p.PeerBytes == 0 || seedWentOffline) return;
            seedWentOffline = true;
            first.Settings.LanOnline = false;
        };
        try
        {
            await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check((await RequestShareAsync(second, first, new WireMessage
            {
                Type = "chunk-share", BatchId = shared.Plan.Offer.BatchId, ContentId = sharedManifest.Id
            })).Type == "reject", "Authenticated but unapproved peer cannot read a seeded batch without a grant");
            Check((await RequestShareAsync(second, first, new WireMessage
            {
                Type = "chunk-share", BatchId = shared.Plan.Offer.BatchId, ContentId = sharedManifest.Id,
                Grant = new ChunkGrant
                {
                    BatchId = shared.Plan.Offer.BatchId, ContentId = sharedManifest.Id,
                    SenderFingerprint = sender.Peer.Fingerprint, RecipientFingerprint = second.Peer.Fingerprint,
                    ExpiresUnixSeconds = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                    Signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
                }
            })).Type == "reject", "A forged sender grant cannot authorize P2P chunk access");
            await SendAsync(sender, second, secondPlan);
            var accelerated = await CompletedAsync(second, secondPlan.Offer.Id);
            Check(accelerated.PeerBytes > 0, "Concurrent large-batch recipient receives verified bytes from an active recipient");
            Check(seedWentOffline && accelerated.PeerBytes < accelerated.TotalBytes, "P2P seed loss falls back to the original sender during the transfer");
            Check(await MatchesAsync(accelerated, "shared.bin", shared.Bytes), "P2P and origin fallback preserve all payload bytes");
        }
        finally
        {
            first.Settings.LanOnline = true;
            first.Observe = null;
            second.Observe = null;
            releaseFirst.Set();
            await firstSend;
        }
        var seed = await CompletedAsync(first, shared.Plan.Offer.Id);
        Check(await MatchesAsync(seed, "shared.bin", shared.Bytes), "First concurrent recipient completes after its temporary pause");

        // Completed peers do not justify extra P2P work when only one recipient remains active.
        await first.StopAsync();
        second.Settings.ReceiveDirectory = Path.Combine(scenarioRoot, "second-direct");
        var directPlan = shared.Plan with { Offer = shared.Plan.Offer with { Id = Guid.NewGuid().ToString("N") } };
        await SendAsync(sender, second, directPlan);
        var direct = await CompletedAsync(second, directPlan.Offer.Id);
        Check(direct.ReusedBytes == 0 && direct.PeerBytes == 0, "A single active recipient uses the origin even after an earlier batch completed");
        Check(await MatchesAsync(direct, "shared.bin", shared.Bytes), "Direct transfer after a completed batch preserves the full payload");

        return checks;
    }

    private static async Task<WireMessage> RequestShareAsync(Endpoint requester, Endpoint seed, WireMessage request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, seed.Peer.Port, timeout.Token);
        using var tls = new SslStream(client.GetStream(), false,
            (_, certificate, _, _) => certificate is not null && IdentityStore.GetFingerprint(certificate) == seed.Peer.Fingerprint);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "Boardcast", ClientCertificates = new X509CertificateCollection { requester.Certificate },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, timeout.Token);
        await WireProtocol.WriteAsync(tls, new WireMessage { Type = "hello", Peer = requester.Peer, Capabilities = ["chunks-v1"] }, timeout.Token);
        var hello = await WireProtocol.ReadAsync<WireMessage>(tls, timeout.Token);
        if (hello.Type != "hello") throw new Exception("Seed did not complete the authenticated hello");
        await WireProtocol.WriteAsync(tls, request, timeout.Token);
        return await WireProtocol.ReadAsync<WireMessage>(tls, timeout.Token);
    }
    private static async Task<(TransferPlan Plan, byte[] Bytes)> CreatePlanAsync(string root, string name, int size, AppSettings settings)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(size);
        string path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, bytes);
        return (ManifestBuilder.BuildPlan([path], settings), bytes);
    }

    private static async Task SendAsync(Endpoint sender, Endpoint receiver, TransferPlan plan, CancellationToken interrupted = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(interrupted);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await sender.Service.SendAsync(plan, receiver.Peer, timeout.Token).WaitAsync(TimeSpan.FromSeconds(35));
    }

    private static async Task<TransferProgress> CompletedAsync(Endpoint endpoint, string id)
    {
        await WaitForAsync(() => endpoint.Progress.TryGetValue(id, out var value) && value.State is "completed" or "failed" or "rejected", "terminal transfer state");
        var result = endpoint.Progress[id];
        if (result.State != "completed") throw new Exception($"Transfer {id} did not complete: {result.State}: {result.Message}");
        return result;
    }

    private static async Task<bool> MatchesAsync(TransferProgress transfer, string name, byte[] expected)
    {
        var actual = await File.ReadAllBytesAsync(Path.Combine(transfer.Destination, name));
        return expected.SequenceEqual(actual);
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

    private sealed class Endpoint : IAsyncDisposable
    {
        private readonly string identityDirectory;
        private IdentityStore identity;
        private CancellationTokenSource stop = new();
        private Task listener = Task.CompletedTask;
        public string DataDirectory => identityDirectory;
        public X509Certificate2 Certificate => identity.Certificate;
        public AppSettings Settings { get; }
        public PeerInfo Peer { get; private set; }
        public TransferService Service { get; private set; } = null!;
        public ConcurrentDictionary<string, TransferProgress> Progress { get; } = new();
        public Action<TransferProgress>? Observe { get; set; }

        public Endpoint(string root, string name)
        {
            identityDirectory = Path.Combine(root, name + "-identity");
            identity = new IdentityStore(identityDirectory);
            Settings = new AppSettings { DeviceName = name, Port = UnusedPort(), ReceiveDirectory = Path.Combine(root, name + "-received") };
            Peer = new PeerInfo { Name = name, Fingerprint = identity.Fingerprint, Address = "127.0.0.1", Port = Settings.Port, Platform = "windows" };
            Start();
        }

        private void Start()
        {
            Service = new TransferService(identity, () => Settings, () => Peer,
                (_, _, _) => Task.FromResult(new IncomingDecision(true, Settings.ReceiveDirectory, "Test accepted")),
                value => { Progress[value.Id] = value; Observe?.Invoke(value); }, _ => { });
            Service.Diagnostic += message => Console.WriteLine($"Chunk {Peer.Name} diagnostic: {message}");
            listener = Service.RunAsync(stop.Token);
        }

        public async Task StopAsync()
        {
            stop.Cancel();
            try { await listener.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (OperationCanceledException) { }
        }

        public async Task RestartAsync()
        {
            await StopAsync();
            stop.Dispose();
            identity.Dispose();
            identity = new IdentityStore(identityDirectory);
            Peer = Peer with { Fingerprint = identity.Fingerprint };
            stop = new CancellationTokenSource();
            Start();
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally { stop.Dispose(); identity.Dispose(); }
        }

        private static int UnusedPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}