using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Boardcast.Core;

public sealed record IncomingDecision(bool Accept, string Destination, string Reason);
public sealed partial class TransferService
{
    public event Action<string>? Diagnostic;
    private readonly IdentityStore identity;
    private readonly Func<AppSettings> settings;
    private readonly Func<PeerInfo> localPeer;
    private readonly Func<TransferOffer, string, CancellationToken, Task<IncomingDecision>> decide;
    private readonly Action<TransferProgress> progress;
    private readonly Action<PeerInfo> remember;
    public TransferService(IdentityStore identity, Func<AppSettings> settings, Func<PeerInfo> localPeer,
        Func<TransferOffer, string, CancellationToken, Task<IncomingDecision>> decide, Action<TransferProgress> progress, Action<PeerInfo> remember)
    {
        this.identity = identity; this.settings = settings; this.localPeer = localPeer;
        this.decide = decide; this.progress = progress; this.remember = remember;
    }
    private static void Configure(TcpClient client)
    {
        client.NoDelay = true; client.SendBufferSize = 1024 * 1024; client.ReceiveBufferSize = 1024 * 1024;
    }
    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var storage in ReceiveStorage.FindStored(identity.DataDirectory))
        {
            var wasCancelled = storage.IsCancelled;
            var clean = storage.RetryCleanup();
            if (!wasCancelled) continue;
            progress(new TransferProgress { Id = storage.TransferId, Direction = "receive", State = "cancelled", Destination = storage.Destination,
                TotalBytes = storage.TotalBytes, BytesTransferred = storage.CompletedBytes, CompletedFiles = storage.CompletedFiles,
                CanDiscard = !clean, Message = CancellationMessage(storage.CompletedFiles, clean) });
        }
        ReceiveStorage.PruneStored(identity.DataDirectory);
        var listener = new TcpListener(IPAddress.IPv6Any, settings().Port);
        listener.Server.DualMode = true;
        listener.Start(16);
        using var slots = new SemaphoreSlim(8);
        var connections = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                var address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (!settings().LanOnline || !LanAddress.IsAllowed(address) || !await slots.WaitAsync(0, ct)) { client.Dispose(); continue; }
                connections.RemoveAll(t => t.IsCompleted);
                connections.Add(HandleBounded(client));
            }
        }
        finally { listener.Stop(); await Task.WhenAll(connections); }
        async Task HandleBounded(TcpClient client)
        {
            try { await ReceiveAsync(client, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Diagnostic?.Invoke(ex.Message); }
            catch (OperationCanceledException) { }
            finally { client.Dispose(); slots.Release(); }
        }
    }
    public async Task<PeerInfo> ConnectAsync(string address, int port, string? expectedFingerprint, CancellationToken ct, bool rememberPeer = true)
    {
        EnsureOnline();
        using var client = new TcpClient(LanAddress.Parse(address).AddressFamily);
        Configure(client);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(LanAddress.Parse(address), port, timeout.Token);
        await using var tls = await AuthenticateClient(client, expectedFingerprint, timeout.Token);
        var peer = await ExchangeHello(tls, address, timeout.Token);
        EnsureOnline();
        if (!peer.LanOnline) throw new InvalidOperationException("对方已断开局域网连接。");
        await WireProtocol.WriteAsync(tls, new WireMessage { Type = "bye" }, timeout.Token);
        ct.ThrowIfCancellationRequested();
        if (rememberPeer) remember(peer);
        return peer;
    }
    public async Task SendAsync(TransferPlan plan, PeerInfo peer, CancellationToken ct)
    {
        // Keep the manifest reusable across simultaneous recipients. Each send owns its offer metadata.
        var offer = plan.Offer with { SenderFingerprint = identity.Fingerprint, SenderName = settings().DeviceName };
        PathSafety.ValidateManifest(offer, settings());
        var state = new TransferProgress { Id = offer.Id, Direction = "send", PeerName = peer.Name, TotalBytes = offer.TotalBytes, State = "preparing", CanResume = true };
        void Report(string value, string message = "") { state = state with { State = value, Message = message, UpdatedAt = DateTimeOffset.UtcNow }; progress(state); }
        Report("preparing");
        try
        {
            EnsureOnline();
            if (peer.Fingerprint.Length != 64 || !peer.Fingerprint.All(Uri.IsHexDigit)) throw new InvalidDataException("Recipient fingerprint is required.");
            var preparedManifest = await PrepareChunksAsync(plan, ct);
            using var client = new TcpClient(LanAddress.Parse(peer.Address).AddressFamily);
            Configure(client);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(LanAddress.Parse(peer.Address), peer.Port, timer.Token);
            await using var tls = await AuthenticateClient(client, peer.Fingerprint, timer.Token);
            WireMessage? remoteHello = null;
            var authenticated = await ExchangeHello(tls, peer.Address, timer.Token, value => remoteHello = value);
            EnsureOnline();
            if (!authenticated.LanOnline) throw new InvalidOperationException("对方已断开局域网连接。");
            remember(authenticated);
            timer.CancelAfter(Timeout.InfiniteTimeSpan);
            ChunkManifest? chunkManifest = remoteHello!.Capabilities.Contains(ChunkCapability)
                ? preparedManifest : null;
            timer.CancelAfter(TimeSpan.FromSeconds(90));
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "offer", Offer = offer, Capabilities = chunkManifest is null ? [] : [ChunkCapability] }, timer.Token);
            Report("waiting");
            timer.CancelAfter(TimeSpan.FromMinutes(5));
            WireMessage response;
            do { response = await WireProtocol.ReadAsync<WireMessage>(tls, timer.Token); } while (response.Type == "pending");
            if (response.Type == "reject") { Report("rejected", response.Message); return; }
            if (response.Type != "accept") throw new InvalidDataException("Recipient did not accept.");
            if (chunkManifest is not null)
            {
                await SendChunksAsync(tls, offer, plan, chunkManifest, authenticated, timer,
                    (bytes, reused, peerBytes) => { state = state with { BytesTransferred = bytes, ReusedBytes = reused, PeerBytes = peerBytes }; Report("sending"); }, ct);
                state = state with { BytesTransferred = offer.TotalBytes, CanResume = false };
                Report("completed", "Received and verified.");
                return;
            }
            Report("sending");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            var lastReport = DateTimeOffset.MinValue;
            try
            {
                foreach (var entry in offer.Entries.Where(e => !e.IsDirectory))
                {
                    string source = plan.LocalFiles[entry.RelativePath];
                    if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new IOException("Source became a link.");
                    PathSafety.ValidateReceiveRoot(Path.GetDirectoryName(source)!);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != entry.Size) throw new IOException("Source size changed; send again.");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long remaining = entry.Size;
                    state = state with { CurrentFile = entry.RelativePath };
                    while (remaining > 0)
                    {
                        timer.CancelAfter(TimeSpan.FromSeconds(90));
                        int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timer.Token);
                        if (read == 0) throw new EndOfStreamException("Source shortened.");
                        hash.AppendData(buffer, 0, read);
                        await tls.WriteAsync(buffer.AsMemory(0, read), timer.Token);
                        remaining -= read;
                        state = state with { BytesTransferred = state.BytesTransferred + read };
                        if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMilliseconds(200)) { Report("sending"); lastReport = DateTimeOffset.UtcNow; }
                    }
                    if (input.Length != entry.Size) throw new IOException("Source size changed.");
                    timer.CancelAfter(TimeSpan.FromSeconds(90));
                    await tls.WriteAsync(hash.GetHashAndReset(), timer.Token);
                }
                await tls.FlushAsync(timer.Token);
                response = await WireProtocol.ReadAsync<WireMessage>(tls, timer.Token);
                if (response.Type != "complete") throw new IOException(response.Message.Length > 0 ? response.Message : "Recipient did not confirm completion.");
                state = state with { CanResume = false };
                Report("completed", response.Message);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        catch (OperationCanceledException) { Report(ct.IsCancellationRequested ? "cancelled" : "failed", "Transfer cancelled or timed out."); }
        catch (Exception ex) { Report("failed", ex.Message); }
    }

    private sealed class IncomingTransfer(CancellationTokenSource stop, string senderFingerprint)
    {
        public CancellationTokenSource Stop { get; } = stop;
        public string SenderFingerprint { get; } = senderFingerprint;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly object Gate = new();
        public ReceiveStorage? Storage;
        public bool CancelRequested;
        public bool CleanupSucceeded = true;
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IncomingTransfer> incoming = new();
    public bool Cancel(string id)
    {
        lock (receiveCancellationGate)
        {
            if (!incoming.TryGetValue(id, out var active)) return false;
            lock (active.Gate)
            {
                if (active.Storage is not null && !active.Storage.MarkCancelled()) return false;
                active.CancelRequested = true;
                active.Stop.Cancel();
                return true;
            }
        }
    }
    public async Task<bool> CancelAsync(string id)
    {
        IncomingTransfer? active;
        bool requested;
        lock (receiveCancellationGate)
        {
            if (incoming.TryGetValue(id, out active)) requested = Cancel(id);
            else
            {
                var handled = false;
                foreach (var storage in ReceiveStorage.FindStored(identity.DataDirectory, id))
                {
                    if (!storage.MarkCancelled()) continue;
                    var clean = storage.CleanupCancelled();
                    progress(new TransferProgress { Id = id, Direction = "receive", State = "cancelled", Destination = storage.Destination,
                        CompletedFiles = storage.CompletedFiles, BytesTransferred = storage.CompletedBytes, TotalBytes = storage.TotalBytes,
                        CanDiscard = !clean, Message = CancellationMessage(storage.CompletedFiles, clean) });
                    handled = true;
                }
                return handled;
            }
        }
        if (requested) await active.Finished.Task.ConfigureAwait(false);
        return requested;
    }
    private static string CancellationMessage(int count, bool clean) => "已取消，已保留 " + count + " 个完成文件。" + (clean ? "" : "部分临时内容待清理，将自动重试。");
    private async Task ReceiveAsync(TcpClient client, CancellationToken ct)
    {
        EnsureOnline();
        Configure(client);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(TimeSpan.FromSeconds(15));
        await using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = identity.Certificate, ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        }, timer.Token);
        if (tls.RemoteCertificate is null) throw new AuthenticationException("Client certificate required.");
        string authenticatedFingerprint = IdentityStore.GetFingerprint(tls.RemoteCertificate);
        var remoteAddress = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
        string address = (remoteAddress.IsIPv4MappedToIPv6 ? remoteAddress.MapToIPv4() : remoteAddress).ToString();
        var hello = await WireProtocol.ReadAsync<WireMessage>(tls, timer.Token);
        ValidateHello(hello, authenticatedFingerprint);
        EnsureOnline();
        var peer = hello.Peer! with { Address = address, Fingerprint = authenticatedFingerprint, LastSeen = DateTimeOffset.UtcNow, Capabilities = hello.Capabilities };
        remember(peer);
        await WireProtocol.WriteAsync(tls, new WireMessage { Type = "hello", Peer = localPeer(), Capabilities = [ChunkCapability, ReceiveCancellationCapability, SubnetSharingCapability] }, timer.Token);
        var message = await WireProtocol.ReadAsync<WireMessage>(tls, timer.Token);
        if (message.Type == "bye") return;
        if (message.Type == "subnet-offer") { await ReceiveSubnetsAsync(tls, message, peer, timer); return; }
        if (message.Type == "transfer-cancel") { await HandleReceiveCancellationAsync(tls, message, authenticatedFingerprint, timer.Token); return; }
        if (message.Type == "chunk-authorize") { await ConfirmChunkAuthorizationAsync(tls, message, authenticatedFingerprint, timer.Token); return; }
        if (message.Type == "chunk-revoke") { await RevokeChunkAuthorizationAsync(tls, message, authenticatedFingerprint, timer.Token); return; }
        if (message.Type == "chunk-share") { await ServeSharedChunksAsync(tls, message, authenticatedFingerprint, timer); return; }
        if (message.Type != "offer" || message.Offer is null) throw new InvalidDataException("Expected offer.");
        var offer = message.Offer;
        var active = new IncomingTransfer(timer, authenticatedFingerprint);
        TransferProgress? state = null;
        void Report(string value, string text = "")
        {
            if (state is null) return;
            state = state with { State = value, Message = text, UpdatedAt = DateTimeOffset.UtcNow }; progress(state);
        }
        bool registered = false;
        Exception? failure = null;
        try
        {
            var current = settings();
            if (!current.LanOnline) throw new InvalidDataException("局域网连接已断开。");
            if (!current.ReceivingEnabled) throw new InvalidDataException("Receiving is paused.");
            var policy = ReceivePolicy.Evaluate(current, offer, authenticatedFingerprint);
            lock (receiveCancellationGate)
                if (!incoming.TryAdd(offer.Id, active)) throw new InvalidDataException("Transfer ID already active.");
            registered = true;
            state = new TransferProgress { Id = offer.Id, Direction = "receive", PeerName = offer.SenderName, State = "pending", TotalBytes = offer.TotalBytes };
            Report("pending", policy.Reason);
            timer.CancelAfter(TimeSpan.FromMinutes(5));
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "pending" }, timer.Token);
            var choice = await decide(offer, address, timer.Token);
            if (!choice.Accept)
            {
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "reject", Message = choice.Reason }, timer.Token);
                Report("rejected", choice.Reason); return;
            }
            current = settings();
            if (!current.LanOnline) throw new InvalidDataException("局域网连接已断开。");
            if (!current.ReceivingEnabled) throw new InvalidDataException("Receiving is paused.");
            PathSafety.ValidateManifest(offer, current);
            string destination = PathSafety.ValidateReceiveRoot(choice.Destination);
            Directory.CreateDirectory(destination);
            PathSafety.ValidateReceiveRoot(destination);
            if (message.Capabilities.Contains(ChunkCapability) && hello.Capabilities.Contains(ChunkCapability))
            {
                state = state with { Destination = destination, CanResume = true, CanDiscard = true };
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "accept" }, timer.Token);
                await ReceiveChunksAsync(tls, offer, peer, destination, timer,
                    (bytes, reused, peerBytes) => { state = state with { BytesTransferred = bytes, ReusedBytes = reused, PeerBytes = peerBytes }; Report("receiving"); },
                    completed => { state = state with { Destination = completed, CanResume = false, CanDiscard = false }; Report("completed", completed); },
                    storage =>
                    {
                        lock (active.Gate)
                        {
                            active.Storage = storage;
                            if (active.CancelRequested) storage.MarkCancelled();
                        }
                    },
                    count => { state = state with { CompletedFiles = count }; Report("receiving"); });
                return;
            }
            // Older senders stream complete files; their temporary bytes also live inside the application's data directory.
            var contentId = Convert.ToHexStringLower(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(offer.Entries, BoardcastJson.Options)));
            var storage = await ReceiveStorage.OpenAsync(identity.DataDirectory, authenticatedFingerprint, offer.Id, contentId, destination, offer.Entries,
                count => { state = state with { CompletedFiles = count }; Report("receiving"); }, timer.Token);
            lock (active.Gate)
            {
                active.Storage = storage;
                if (active.CancelRequested) storage.MarkCancelled();
            }
            state = state with { Destination = destination, CanDiscard = true };
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "accept" }, timer.Token);
            Report("receiving");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            var lastReport = DateTimeOffset.MinValue;
            try
            {
                for (var index = 0; index < offer.Entries.Count; index++)
                {
                    var entry = offer.Entries[index];
                    if (entry.IsDirectory) continue;
                    // Legacy streams cannot request a skipped file. Receive and hash it before comparing a prior completed copy.
                    string path = storage.GetStagingPath(index);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    path = storage.GetStagingPath(index);
                    byte[] digest;
                    await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        long remaining = entry.Size;
                        state = state with { CurrentFile = entry.RelativePath };
                        while (remaining > 0)
                        {
                            timer.CancelAfter(TimeSpan.FromSeconds(90));
                            int read = await tls.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timer.Token);
                            if (read == 0) throw new EndOfStreamException("Sender disconnected.");
                            hash.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), timer.Token);
                            remaining -= read;
                            state = state with { BytesTransferred = state.BytesTransferred + read };
                            if (DateTimeOffset.UtcNow - lastReport > TimeSpan.FromMilliseconds(200)) { Report("receiving"); lastReport = DateTimeOffset.UtcNow; }
                        }
                        byte[] expected = new byte[32];
                        timer.CancelAfter(TimeSpan.FromSeconds(90));
                        await tls.ReadExactlyAsync(expected, timer.Token);
                        digest = hash.GetHashAndReset();
                        if (!CryptographicOperations.FixedTimeEquals(expected, digest)) throw new InvalidDataException("File integrity check failed.");
                        await output.FlushAsync(timer.Token);
                        output.Flush(true);
                    }
                    if (storage.IsFileCompleted(index))
                    {
                        bool matches = false;
                        try
                        {
                            await using var prior = ChunkTransfer.OpenRead(storage.GetFilePath(index));
                            matches = prior.Length == entry.Size && CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(prior, timer.Token), digest);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        if (matches) { File.Delete(path); continue; }
                        await storage.InvalidateCompletedAsync(index, timer.Token);
                    }
                    await storage.CommitFileAsync(index, timer.Token, async (input, token) =>
                    {
                        if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, token), digest))
                            throw new InvalidDataException("File integrity check failed before saving.");
                    });
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            await storage.FinishAsync(timer.Token);
            lock (receiveCancellationGate)
                ReceiveStorage.PruneStored(identity.DataDirectory, incoming.Values.Where(item => item.Storage is not null).Select(item => item.Storage!.CacheRoot).ToHashSet(StringComparer.OrdinalIgnoreCase));
            state = state with { Destination = destination, CanResume = false, CanDiscard = false, BytesTransferred = offer.TotalBytes };
            Report("completed", destination);
            try { await WireProtocol.WriteAsync(tls, new WireMessage { Type = "complete", Message = "Received and verified." }, timer.Token); }
            catch (Exception) { Diagnostic?.Invoke("Transfer committed; completion acknowledgement was interrupted."); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or UnauthorizedAccessException or SocketException or CryptographicException)
        {
            failure = ex;
            if (!active.CancelRequested)
            {
                try { await WireProtocol.WriteAsync(tls, new WireMessage { Type = "reject", Message = ex.Message }, timer.Token); } catch { }
            }
        }
        finally
        {
            try
            {
                if (active.CancelRequested)
                {
                    var clean = active.Storage?.CleanupCancelled() ?? true;
                    active.CleanupSucceeded = clean;
                    if (state is not null)
                    {
                        state = state with { CanResume = false, CanDiscard = !clean, CompletedFiles = active.Storage?.CompletedFiles ?? state.CompletedFiles,
                            BytesTransferred = active.Storage?.CompletedBytes ?? 0 };
                        Report("cancelled", CancellationMessage(state.CompletedFiles, clean));
                    }
                }
                else if (failure is not null && state is not null && state.State != "completed")
                {
                    var retained = active.Storage is not null;
                    state = state with { CanResume = retained, CanDiscard = retained, CompletedFiles = active.Storage?.CompletedFiles ?? state.CompletedFiles };
                    Report(failure is OperationCanceledException ? "paused" : "failed", "传输中断，已保留 " + state.CompletedFiles + " 个完成文件。" + failure.Message);
                }
            }
            finally
            {
                lock (receiveCancellationGate)
                    if (registered) incoming.TryRemove(offer.Id, out _);
                active.Finished.TrySetResult();
            }
        }
    }
    private void EnsureOnline()
    {
        if (!settings().LanOnline) throw new InvalidOperationException("局域网连接已断开，请先连接。");
    }
    private async Task<SslStream> AuthenticateClient(TcpClient client, string? fingerprint, CancellationToken ct)
    {
        var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && (fingerprint is null || IdentityStore.GetFingerprint(certificate).Equals(fingerprint, StringComparison.OrdinalIgnoreCase)));
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "Boardcast", ClientCertificates = new X509CertificateCollection { identity.Certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, ct);
            return tls;
        }
        catch { await tls.DisposeAsync(); throw; }
    }
    private async Task<PeerInfo> ExchangeHello(SslStream tls, string address, CancellationToken ct, Action<WireMessage>? received = null)
    {
        await WireProtocol.WriteAsync(tls, new WireMessage { Type = "hello", Peer = localPeer(), Capabilities = [ChunkCapability, ReceiveCancellationCapability, SubnetSharingCapability] }, ct);
        var response = await WireProtocol.ReadAsync<WireMessage>(tls, ct);
        string fingerprint = IdentityStore.GetFingerprint(tls.RemoteCertificate!);
        ValidateHello(response, fingerprint);
        received?.Invoke(response);
        return response.Peer! with { Fingerprint = fingerprint, Address = address, LastSeen = DateTimeOffset.UtcNow, Capabilities = response.Capabilities };
    }
    private static void ValidateHello(WireMessage message, string fingerprint)
    {
        if (message.Type != "hello" || message.Version != 1 || message.Capabilities is null || message.Capabilities.Count > 16 || message.Capabilities.Any(c => c is null || c.Length > 64) || message.Peer is not { } peer ||
            peer.Fingerprint is null || !peer.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase) ||
            peer.Name is null || peer.Name.Length > 256 || peer.Port is < 1024 or > 65535 ||
            !DeviceGroups.IsValidGroupName(peer.GroupName) ||
            peer.Platform is null || peer.Platform.Length > 128 || peer.AppVersion is null || peer.AppVersion.Length > 64 ||
            peer.Runtime is null || peer.Runtime.Length > 64 || peer.UpdateVersion is null || peer.UpdateVersion.Length > 64 ||
            peer.DiscoverySubnets is null || peer.DiscoverySubnets.Count > DiscoverySubnets.MaximumCount ||
            peer.DiscoverySubnets.Any(subnet => subnet is null || subnet.Cidr is null || subnet.Cidr.Length > 32 || subnet.Port is < 1024 or > 65535) ||
            (peer.UpdatePort != 0 && peer.UpdatePort is < 1024 or > 65535))
            throw new InvalidDataException("Invalid authenticated hello.");
        peer.GroupName = peer.GroupName.Trim();
    }
}
