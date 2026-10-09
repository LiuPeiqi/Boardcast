using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Boardcast.Core;

public sealed record ChunkGrant
{
    public string GrantId { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string ContentId { get; set; } = "";
    public string SenderFingerprint { get; set; } = "";
    public string RecipientFingerprint { get; set; } = "";
    public long ExpiresUnixSeconds { get; set; }
    public string Signature { get; set; } = "";
}

public sealed partial class TransferService
{
    private const string ChunkCapability = "chunks-v1";
    private readonly object preparationGate = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<string, string>, PreparedChunks> preparedChunks = new();
    private sealed class PreparedChunks
    {
        public readonly CancellationTokenSource Stop = new();
        public required Task<ChunkManifest> Build;
        public int Waiters;
    }
    private async Task<ChunkManifest> PrepareChunksAsync(TransferPlan plan, CancellationToken ct)
    {
        PreparedChunks shared;
        lock (preparationGate)
        {
            if (!preparedChunks.TryGetValue(plan.LocalFiles, out shared!) || shared.Build.IsFaulted || shared.Build.IsCanceled)
            {
                preparedChunks.Remove(plan.LocalFiles);
                shared = new PreparedChunks { Build = null! };
                var token = shared.Stop.Token;
                shared.Build = Task.Run(() => ChunkTransfer.BuildManifestAsync(plan, token), token);
                preparedChunks.Add(plan.LocalFiles, shared);
            }
            shared.Waiters++;
        }
        try { return await shared.Build.WaitAsync(ct); }
        finally
        {
            lock (preparationGate)
            {
                shared.Waiters--;
                if (shared.Waiters == 0 && !shared.Build.IsCompleted)
                {
                    shared.Stop.Cancel();
                    if (preparedChunks.TryGetValue(plan.LocalFiles, out var current) && ReferenceEquals(current, shared)) preparedChunks.Remove(plan.LocalFiles);
                    // Observe any build failure after the last waiter has departed.
                    _ = shared.Build.ContinueWith(task => { _ = task.Exception; shared.Stop.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                else if (shared.Waiters == 0 && (shared.Build.IsFaulted || shared.Build.IsCanceled))
                {
                    if (preparedChunks.TryGetValue(plan.LocalFiles, out var current) && ReferenceEquals(current, shared)) preparedChunks.Remove(plan.LocalFiles);
                    shared.Stop.Dispose();
                }
            }
        }
    }
    private readonly ConcurrentDictionary<string, ApprovedBatch> chunkBatches = new();
    private readonly ConcurrentDictionary<string, SharedChunks> sharedChunks = new();
    private readonly ConcurrentDictionary<string, byte> activeChunkRoots = new(StringComparer.OrdinalIgnoreCase);
    private int activeRelaySessions;
    public bool HasActiveRelayRequests => Volatile.Read(ref activeRelaySessions) != 0;
    private sealed class ApprovedBatch
    {
        public DateTimeOffset Expires = DateTimeOffset.UtcNow.AddHours(2);
        public readonly ConcurrentDictionary<string, PeerInfo> Recipients = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, long> VerifiedBytes = new(StringComparer.OrdinalIgnoreCase);
        public readonly ConcurrentDictionary<string, string> Grants = new(StringComparer.OrdinalIgnoreCase);
        public int ActiveRecipients;
    }
    private sealed record SharedChunks(ChunkStore Store, byte[] SenderCertificate, string SenderFingerprint, PeerInfo Sender, DateTimeOffset Expires)
    {
        public ConcurrentDictionary<string, byte> RevokedGrants { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private static string BatchId(TransferOffer offer) => string.IsNullOrEmpty(offer.BatchId) ? offer.Id : offer.BatchId;
    private static string ShareKey(string batchId, string contentId) => batchId + ":" + contentId;
    private static byte[] GrantBytes(ChunkGrant grant) => JsonSerializer.SerializeToUtf8Bytes(new[]
        { grant.GrantId, grant.BatchId, grant.ContentId, grant.SenderFingerprint, grant.RecipientFingerprint, grant.ExpiresUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    private ChunkGrant CreateGrant(TransferOffer offer, ChunkManifest manifest, string recipient)
    {
        var grant = new ChunkGrant { GrantId = Guid.NewGuid().ToString("N"), BatchId = BatchId(offer), ContentId = manifest.Id, SenderFingerprint = identity.Fingerprint,
            RecipientFingerprint = recipient, ExpiresUnixSeconds = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds() };
        using var key = identity.Certificate.GetECDsaPrivateKey() ?? throw new CryptographicException("Missing signing key.");
        grant.Signature = Convert.ToBase64String(key.SignData(GrantBytes(grant), HashAlgorithmName.SHA256));
        return grant;
    }
    private static bool ValidGrant(ChunkGrant? grant, string batchId, string contentId, string recipient, SharedChunks shared)
    {
        if (grant is null || !Guid.TryParseExact(grant.GrantId, "N", out _) || grant.BatchId != batchId || grant.ContentId != contentId ||
            !grant.SenderFingerprint.Equals(shared.SenderFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !grant.RecipientFingerprint.Equals(recipient, StringComparison.OrdinalIgnoreCase) ||
            grant.ExpiresUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
            grant.ExpiresUnixSeconds > DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds() ||
            grant.Signature is not { Length: > 0 and <= 256 }) return false;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(shared.SenderCertificate);
            using var key = certificate.GetECDsaPublicKey();
            return key is not null && key.VerifyData(GrantBytes(grant), Convert.FromBase64String(grant.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return false; }
    }
    private void PruneShares()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in sharedChunks) if (item.Value.Expires < now) sharedChunks.TryRemove(item.Key, out _);
        foreach (var item in chunkBatches) if (item.Value.Expires < now) chunkBatches.TryRemove(item.Key, out _);
        // Bound retained in-memory seed indexes even if many small batches finish during the TTL.
        foreach (var item in sharedChunks.OrderBy(item => item.Value.Expires).Take(Math.Max(0, sharedChunks.Count - 64))) sharedChunks.TryRemove(item.Key, out _);
        foreach (var item in chunkBatches.OrderBy(item => item.Value.Expires).Take(Math.Max(0, chunkBatches.Count - 64))) chunkBatches.TryRemove(item.Key, out _);
    }
    private async Task SendChunksAsync(SslStream tls, TransferOffer offer, TransferPlan plan, ChunkManifest manifest, PeerInfo recipient,
        CancellationTokenSource timer, Action<long, long, long> report, CancellationToken ct)
    {
        PruneShares();
        string batchId = BatchId(offer);
        if (!Guid.TryParse(batchId, out _)) throw new InvalidDataException("Invalid transfer batch ID.");
        var batch = chunkBatches.GetOrAdd(ShareKey(batchId, manifest.Id), _ => new());

        List<PeerInfo> Sources() => batch.Recipients.Values.Where(peer => peer.Fingerprint != recipient.Fingerprint).Take(32).ToList();
        var grant = CreateGrant(offer, manifest, recipient.Fingerprint);
        lock (batch) { batch.Recipients[recipient.Fingerprint] = recipient; batch.Grants[recipient.Fingerprint] = grant.Signature; }
        timer.CancelAfter(TimeSpan.FromSeconds(90));
        await WireProtocol.WriteAsync(tls, new WireMessage { Type = "chunk-start", Chunks = manifest, Grant = grant, Sources = Sources() }, timer.Token);
        long sent = 0;
        bool completed = false;
        Interlocked.Increment(ref batch.ActiveRecipients);
        try
        {
        while (true)
        {
            timer.CancelAfter(TimeSpan.FromSeconds(90));
            var request = await WireProtocol.ReadAsync<WireMessage>(tls, timer.Token);
            if (request.Type == "chunk-keepalive")
            {
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "pending" }, timer.Token);
                continue;
            }
            if (request.Type == "chunks-complete")
            {
                completed = true;
                batch.VerifiedBytes[recipient.Fingerprint] = offer.TotalBytes;
                report(offer.TotalBytes, request.ReusedBytes, request.PeerBytes);
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "complete", Message = "Received and verified." }, timer.Token);
                return;
            }
            if (request.Type != "chunk" || request.Chunk is not { } chunk) throw new InvalidDataException("Expected chunk request.");
            batch.VerifiedBytes[recipient.Fingerprint] = Math.Clamp(request.VerifiedBytes, 0, offer.TotalBytes);
            byte[] data = await ChunkTransfer.ReadChunkAsync(manifest, chunk, relative => plan.LocalFiles[relative], ct);
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "chunk", Chunk = chunk, ChunkBytes = data.Length, Sources = Sources(), ActiveRecipients = Volatile.Read(ref batch.ActiveRecipients), SourceVerifiedBytes = Sources().ToDictionary(peer => peer.Fingerprint, peer => batch.VerifiedBytes.GetValueOrDefault(peer.Fingerprint)) }, timer.Token);
            await tls.WriteAsync(data, timer.Token);
            await tls.FlushAsync(timer.Token);
            sent += data.Length;
            report(Math.Min(sent, offer.TotalBytes), 0, 0);
        }
        }
        finally
        {
            Interlocked.Decrement(ref batch.ActiveRecipients);
            if (!completed)
            {
                lock (batch)
                {
                    if (batch.Grants.GetValueOrDefault(recipient.Fingerprint) == grant.Signature)
                    {
                        batch.Grants.TryRemove(recipient.Fingerprint, out _);
                        batch.Recipients.TryRemove(recipient.Fingerprint, out _);
                        batch.VerifiedBytes.TryRemove(recipient.Fingerprint, out _);
                    }
                }
                await RevokeRecipientAsync(batch, grant);
            }
        }
    }
    private async Task ServeSharedChunksAsync(SslStream tls, WireMessage request, string authenticatedFingerprint, CancellationTokenSource timer)
    {
        PruneShares();
        string key = ShareKey(request.BatchId, request.ContentId);
        if (!sharedChunks.TryGetValue(key, out var shared) || (!ValidGrant(request.Grant, request.BatchId, request.ContentId, authenticatedFingerprint, shared) || shared.RevokedGrants.ContainsKey(request.Grant?.Signature ?? "")))
        {
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "reject", Message = "This recipient is not authorized for this transfer batch." }, timer.Token);
            return;
        }
        if (!await IsRecipientStillApprovedAsync(shared, request.Grant!, timer.Token))
        {
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "reject", Message = "The sender no longer authorizes this recipient." }, timer.Token);
            return;
        }
        if (Interlocked.Increment(ref activeRelaySessions) > 2)
        {
            Interlocked.Decrement(ref activeRelaySessions);
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "reject", Message = "Peer sharing is busy." }, timer.Token);
            return;
        }
        try
        {
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "share-ready" }, timer.Token);
            var authorizationExpires = DateTimeOffset.UtcNow.AddSeconds(30);
            while (true)
            {
                timer.CancelAfter(TimeSpan.FromSeconds(30));
                request = await WireProtocol.ReadAsync<WireMessage>(tls, timer.Token);
                EnsureOnline();
                if (request.Type == "bye") return;
                if (request.Type != "chunk" || request.Chunk is not { } chunk || (!ValidGrant(request.Grant, request.BatchId, request.ContentId, authenticatedFingerprint, shared) || shared.RevokedGrants.ContainsKey(request.Grant?.Signature ?? "")))
                    throw new InvalidDataException("Invalid authenticated chunk request.");
                if (DateTimeOffset.UtcNow >= authorizationExpires)
                {
                    if (!await IsRecipientStillApprovedAsync(shared, request.Grant!, timer.Token)) throw new InvalidDataException("Sender authorization was withdrawn.");
                    authorizationExpires = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                if (!shared.Store.IsVerified(chunk))
                {
                    await WireProtocol.WriteAsync(tls, new WireMessage { Type = "chunk-missing" }, timer.Token);
                    continue;
                }
                byte[] data = await shared.Store.ReadAsync(chunk, timer.Token);
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "chunk", Chunk = chunk, ChunkBytes = data.Length }, timer.Token);
                await tls.WriteAsync(data, timer.Token);
                await tls.FlushAsync(timer.Token);
            }
        }
        finally { Interlocked.Decrement(ref activeRelaySessions); }
    }
    private async Task ConfirmChunkAuthorizationAsync(SslStream stream, WireMessage message, string authenticatedSeed, CancellationToken ct)
    {
        bool authorized = false;
        var grant = message.Grant;
        if (grant is not null && grant.SenderFingerprint == identity.Fingerprint &&
            grant.ExpiresUnixSeconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
            chunkBatches.TryGetValue(ShareKey(grant.BatchId, grant.ContentId), out var batch) &&
            batch.Recipients.ContainsKey(authenticatedSeed) && batch.Recipients.ContainsKey(grant.RecipientFingerprint) &&
            batch.Grants.GetValueOrDefault(grant.RecipientFingerprint) == grant.Signature)
        {
            try
            {
                using var key = identity.Certificate.GetECDsaPublicKey();
                authorized = grant.Signature is { Length: > 0 and <= 256 } && key is not null &&
                    key.VerifyData(GrantBytes(grant), Convert.FromBase64String(grant.Signature), HashAlgorithmName.SHA256);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException) { }
        }
        await WireProtocol.WriteAsync(stream, new WireMessage { Type = authorized ? "authorized" : "reject" }, ct);
    }
    private async Task<bool> IsRecipientStillApprovedAsync(SharedChunks shared, ChunkGrant grant, CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient(LanAddress.Parse(shared.Sender.Address).AddressFamily);
            Configure(client);
            await client.ConnectAsync(LanAddress.Parse(shared.Sender.Address), shared.Sender.Port, deadline.Token);
            await using var stream = await AuthenticateClient(client, shared.SenderFingerprint, deadline.Token);
            await ExchangeHello(stream, shared.Sender.Address, deadline.Token);
            await WireProtocol.WriteAsync(stream, new WireMessage { Type = "chunk-authorize", Grant = grant }, deadline.Token);
            return (await WireProtocol.ReadAsync<WireMessage>(stream, deadline.Token)).Type == "authorized";
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { return false; }
    }
    private async Task RevokeChunkAuthorizationAsync(SslStream stream, WireMessage message, string issuer, CancellationToken ct)
    {
        if (message.Grant is { } grant && sharedChunks.TryGetValue(ShareKey(grant.BatchId, grant.ContentId), out var shared) &&
            shared.SenderFingerprint == issuer && ValidGrant(grant, grant.BatchId, grant.ContentId, grant.RecipientFingerprint, shared))
            shared.RevokedGrants[grant.Signature] = 0;
        await WireProtocol.WriteAsync(stream, new WireMessage { Type = "complete" }, ct);
    }
    private async Task RevokeRecipientAsync(ApprovedBatch batch, ChunkGrant grant)
    {
        await Task.WhenAll(batch.Recipients.Values.Take(32).Select(async peer =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var client = new TcpClient(LanAddress.Parse(peer.Address).AddressFamily);
                Configure(client);
                await client.ConnectAsync(LanAddress.Parse(peer.Address), peer.Port, deadline.Token);
                await using var stream = await AuthenticateClient(client, peer.Fingerprint, deadline.Token);
                await ExchangeHello(stream, peer.Address, deadline.Token);
                await WireProtocol.WriteAsync(stream, new WireMessage { Type = "chunk-revoke", Grant = grant }, deadline.Token);
                await WireProtocol.ReadAsync<WireMessage>(stream, deadline.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
        }));
    }
    private static int ChunkLength(ChunkManifest manifest, ChunkRequest chunk)
    {
        if (chunk.FileIndex < 0 || chunk.FileIndex >= manifest.Files.Count) throw new InvalidDataException("Invalid chunk file.");
        var file = manifest.Files[chunk.FileIndex];
        if (chunk.ChunkIndex < 0 || chunk.ChunkIndex >= file.Hashes.Count) throw new InvalidDataException("Invalid chunk index.");
        return (int)Math.Min(manifest.ChunkSize, file.Size - (long)chunk.ChunkIndex * manifest.ChunkSize);
    }
    private static async Task<byte[]> ReadChunkReply(SslStream tls, ChunkManifest manifest, ChunkRequest chunk, CancellationToken ct, Action<WireMessage>? sources = null)
    {
        var reply = await WireProtocol.ReadAsync<WireMessage>(tls, ct);
        if (reply.Type != "chunk" || reply.Chunk != chunk || reply.ChunkBytes != ChunkLength(manifest, chunk)) throw new InvalidDataException("Chunk unavailable or size mismatch.");
        if (reply.Sources is null || reply.Sources.Count > 32) throw new InvalidDataException("Too many chunk sources.");
        sources?.Invoke(reply);
        var data = new byte[reply.ChunkBytes];
        await tls.ReadExactlyAsync(data, ct);
        string hash = Convert.ToHexStringLower(SHA256.HashData(data));
        if (!hash.Equals(manifest.Files[chunk.FileIndex].Hashes[chunk.ChunkIndex], StringComparison.OrdinalIgnoreCase)) throw new CryptographicException("Chunk integrity check failed.");
        return data;
    }
    private sealed class AsyncCleanup(Func<Task> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispose();
    }
    private sealed class RelayConnection : IAsyncDisposable
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public TcpClient? Client;
        public SslStream? Stream;
        public bool Quarantined;
        public async ValueTask DisposeAsync() { if (Stream is not null) await Stream.DisposeAsync(); Client?.Dispose(); }
    }
    private async Task<string> ReceiveChunksAsync(SslStream origin, TransferOffer offer, PeerInfo sender, string destination,
        CancellationTokenSource timer, Action<long, long, long> report, Action<string> completedAction,
        Action<ReceiveStorage> storageReady, Action<int> fileCompleted)
    {
        timer.CancelAfter(TimeSpan.FromSeconds(90));
        var start = await WireProtocol.ReadAsync<WireMessage>(origin, timer.Token);
        if (start.Type != "chunk-start" || start.Chunks is not { } manifest || start.Grant is not { } grant) throw new InvalidDataException("Expected chunk manifest.");
        ChunkTransfer.ValidateManifest(manifest, offer);
        timer.CancelAfter(Timeout.InfiniteTimeSpan);
        using var originGate = new SemaphoreSlim(1, 1);
        using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(timer.Token);
        Task heartbeat = Task.CompletedTask;
        await using var heartbeatLifetime = new AsyncCleanup(async () => { heartbeatStop.Cancel(); await heartbeat; });
        string batchId = BatchId(offer);
        if (!Guid.TryParse(batchId, out _)) throw new InvalidDataException("Invalid transfer batch ID.");
        var certificate = origin.RemoteCertificate!.GetRawCertData();
        heartbeat = KeepOriginAliveAsync(origin, originGate, heartbeatStop.Token, timer);
        var entries = manifest.Files.Select(file => new TransferEntry { RelativePath = file.RelativePath, Size = file.Size, IsDirectory = file.IsDirectory }).ToList();
        var storage = await ReceiveStorage.OpenAsync(identity.DataDirectory, sender.Fingerprint, offer.Id, manifest.Id, destination, entries, fileCompleted, timer.Token);
        storageReady(storage);
        string root = storage.CacheRoot;
        if (!activeChunkRoots.TryAdd(root, 0)) throw new InvalidDataException("The same content is already being received into this directory.");
        ChunkStore? store = null;
        bool committed = false;
        var relays = new ConcurrentDictionary<string, RelayConnection>(StringComparer.OrdinalIgnoreCase);
        var sources = new ConcurrentDictionary<string, PeerInfo>(StringComparer.OrdinalIgnoreCase);
        long peerBytes = 0;
        int activeRecipients = start.ActiveRecipients;
        var sourceBytes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var scheduling = new ChunkPeerPolicy(offer.TotalBytes, manifest.ChunkSize);
        try
        {
            store = await ChunkStore.OpenReceiveAsync(manifest, storage, timer.Token);
            var shared = new SharedChunks(store, certificate, sender.Fingerprint, sender, DateTimeOffset.UtcNow.AddHours(2));
            if (!ValidGrant(grant, batchId, manifest.Id, identity.Fingerprint, shared)) throw new InvalidDataException("Invalid sender authorization for chunk transfer.");
            sharedChunks[ShareKey(batchId, manifest.Id)] = shared;
            PruneShares();
            long reused = store.VerifiedBytes;
            void UpdateSources(WireMessage message)
            {
                var peers = message.Sources;
                Volatile.Write(ref activeRecipients, message.ActiveRecipients);
                if (message.SourceVerifiedBytes is null || message.SourceVerifiedBytes.Count > 64) throw new InvalidDataException("Invalid source availability.");
                foreach (var availability in message.SourceVerifiedBytes) sourceBytes[availability.Key] = Math.Clamp(availability.Value, 0, offer.TotalBytes);
                if (peers is null || peers.Count > 32) throw new InvalidDataException("Invalid source list.");
                foreach (var peer in peers)
                    if (peer.Fingerprint is { Length: 64 } && peer.Fingerprint.All(Uri.IsHexDigit) && peer.Fingerprint != identity.Fingerprint && peer.Fingerprint != sender.Fingerprint && peer.Port is >= 1024 and <= 65535)
                        sources[peer.Fingerprint] = peer;
            }
            UpdateSources(start);
            report(reused, reused, 0);
            async Task<byte[]> Fetch(ChunkRequest chunk, CancellationToken ct)
            {
                // Large competing transfers may probe a verified LAN source after measuring origin throughput.
                var candidates = sources.Values.OrderBy(peer => peer.Fingerprint, StringComparer.Ordinal).ToArray();
                if (candidates.Length > 0 && chunk.ChunkIndex % 3 != 0 && ChunkLength(manifest, chunk) >= 256 * 1024 && offer.TotalBytes - store.VerifiedBytes >= 8L * 1024 * 1024)
                {
                    var peer = candidates[chunk.ChunkIndex % candidates.Length];
                    var relay = relays.GetOrAdd(peer.Fingerprint, _ => new());
                    if (!relay.Quarantined && scheduling.ShouldTryPeer(peer.Fingerprint, Volatile.Read(ref activeRecipients), sourceBytes.GetValueOrDefault(peer.Fingerprint)))
                    {
                        await relay.Gate.WaitAsync(ct);
                        var peerTimer = Stopwatch.StartNew();
                        int peerCount = 0;
                        bool peerSuccess = false;
                        try
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            deadline.CancelAfter(scheduling.PeerTimeout);
                            if (relay.Stream is null)
                            {
                                relay.Client = new TcpClient(LanAddress.Parse(peer.Address).AddressFamily);
                                Configure(relay.Client);
                                await relay.Client.ConnectAsync(LanAddress.Parse(peer.Address), peer.Port, deadline.Token);
                                relay.Stream = await AuthenticateClient(relay.Client, peer.Fingerprint, deadline.Token);
                                await ExchangeHello(relay.Stream, peer.Address, deadline.Token);
                                await WireProtocol.WriteAsync(relay.Stream, new WireMessage { Type = "chunk-share", BatchId = batchId, ContentId = manifest.Id, Grant = grant }, deadline.Token);
                                if ((await WireProtocol.ReadAsync<WireMessage>(relay.Stream, deadline.Token)).Type != "share-ready") throw new InvalidDataException("Peer is not ready to share.");
                            }
                            peerTimer.Restart();
                            await WireProtocol.WriteAsync(relay.Stream, new WireMessage { Type = "chunk", Chunk = chunk, BatchId = batchId, ContentId = manifest.Id, Grant = grant }, deadline.Token);
                            byte[] data = await ReadChunkReply(relay.Stream, manifest, chunk, deadline.Token);
                            peerCount = data.Length; peerSuccess = true;
                            Interlocked.Add(ref peerBytes, data.Length);
                            return data;
                        }
                        catch (Exception ex) when (ex is IOException or SocketException or System.Security.Authentication.AuthenticationException or InvalidDataException or CryptographicException or OperationCanceledException)
                        {
                            ct.ThrowIfCancellationRequested();
                            await relay.DisposeAsync(); relay.Stream = null; relay.Client = null;
                            if (ex is CryptographicException) relay.Quarantined = true;
                        }
                        finally { scheduling.RecordPeer(peer.Fingerprint, peerCount, peerTimer.Elapsed, peerSuccess); relay.Gate.Release(); }
                    }
                }
                await originGate.WaitAsync(ct);
                try
                {
                    using var originDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    originDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                    var originTimer = Stopwatch.StartNew();
                    await WireProtocol.WriteAsync(origin, new WireMessage { Type = "chunk", Chunk = chunk, VerifiedBytes = store.VerifiedBytes }, originDeadline.Token);
                    var data = await ReadChunkReply(origin, manifest, chunk, originDeadline.Token, UpdateSources);
                    scheduling.RecordOrigin(data.Length, originTimer.Elapsed);
                    return data;
                }
                finally { originGate.Release(); }
            }
            await ChunkTransfer.DownloadAsync(store, Fetch, bytes => report(bytes, reused, Interlocked.Read(ref peerBytes)), timer.Token, concurrency: 3);
            heartbeatStop.Cancel();
            await heartbeat;
            await storage.FinishAsync(timer.Token);
            lock (receiveCancellationGate)
                ReceiveStorage.PruneStored(identity.DataDirectory, incoming.Values.Where(item => item.Storage is not null).Select(item => item.Storage!.CacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase));
            committed = true;
            completedAction(destination);
            try
            {
                await WireProtocol.WriteAsync(origin, new WireMessage { Type = "chunks-complete", ReusedBytes = reused, PeerBytes = Interlocked.Read(ref peerBytes) }, timer.Token);
                await WireProtocol.ReadAsync<WireMessage>(origin, timer.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { Diagnostic?.Invoke("Transfer committed; completion acknowledgement was interrupted."); }
            return destination;
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
            foreach (var relay in relays.Values) await relay.DisposeAsync();
            activeChunkRoots.TryRemove(root, out _);
            if (!committed && store is not null)
            {
                if (sharedChunks.TryGetValue(ShareKey(batchId, manifest.Id), out var retained) && ReferenceEquals(retained.Store, store))
                    sharedChunks.TryRemove(ShareKey(batchId, manifest.Id), out _);
                await store.StopAsync();
            }
        }
    }
    private static async Task KeepOriginAliveAsync(SslStream origin, SemaphoreSlim gate, CancellationToken ct, CancellationTokenSource transfer)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await gate.WaitAsync(ct);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    await WireProtocol.WriteAsync(origin, new WireMessage { Type = "chunk-keepalive" }, deadline.Token);
                    if ((await WireProtocol.ReadAsync<WireMessage>(origin, deadline.Token)).Type != "pending") throw new InvalidDataException("Invalid chunk keepalive.");
                }
                finally { gate.Release(); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
        {
            if (!ct.IsCancellationRequested) transfer.Cancel();
        }
    }
}