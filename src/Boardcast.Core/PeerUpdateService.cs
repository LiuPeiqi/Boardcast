using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Boardcast.Core;

public sealed record PeerUpdateStatus
{
    public string State { get; set; } = "idle";
    public bool CanUpdate { get; set; }
    public string Version { get; set; } = "";
    public string PeerName { get; set; } = "";
    public string Message { get; set; } = "";
    public long BytesReceived { get; set; }
    public long TotalBytes { get; set; }
    public bool CanResume { get; set; }
    public long ReusedBytes { get; set; }
    public long PeerBytes { get; set; }
    public int SourceCount { get; set; }
}

public sealed class PeerUpdateService : IDisposable
{
    private readonly IdentityStore identity;
    private readonly string installRoot, dataDirectory, currentVersion;
    private readonly ReleaseTrust? trust;
    private readonly VerifiedRelease? published;
    private readonly string initialDiagnostic = "";
    private readonly TcpListener? listener;
    private readonly int updatePort;
    private readonly SemaphoreSlim downloadGate = new(1, 1), serveSlots = new(8, 8), manifestGate = new(1, 1);
    private ChunkManifest? packageChunks;
    private CancellationTokenSource? downloadCancellation;
    private readonly ConcurrentDictionary<string, Task> connections = new();
    private readonly object statusGate = new();
    private PeerUpdateStatus status = new();
    private long resultWriteTicks = -1, resultLength = -1;
    private string resultMessage = "";
    private VerifiedRelease? downloaded;
    private string downloadedRoot = "";
    private string preparedHelperRoot = "";
    private volatile bool online = true;
    public event Action<string>? Diagnostic;
    public int Port => updatePort;
    public bool HasActiveShares => !connections.IsEmpty;
    public string AvailableReleaseVersion => online ? published?.Manifest.Version ?? "" : "";
    public PeerUpdateStatus Status
    {
        get
        {
            lock (statusGate)
            {
                if (status.State == "idle")
                {
                    string message = ReadResultMessage();
                    if (message.Length > 0) return status with { Message = message };
                }
                return status with { };
            }
        }
    }
    public void SetOnline(bool value) { online = value; if (!value) CancelDownload(); }
    public void CancelDownload() { lock (statusGate) downloadCancellation?.Cancel(); }
    public PeerUpdateService(IdentityStore identity, string installRoot, string dataDirectory) : this(identity, installRoot, dataDirectory, AppRelease.CurrentVersion) { }
    internal PeerUpdateService(IdentityStore identity, string installRoot, string dataDirectory, string currentVersion)
    {
        this.identity = identity; this.currentVersion = currentVersion; this.installRoot = Path.TrimEndingDirectorySeparator(PathSafety.ValidateReceiveRoot(installRoot)); this.dataDirectory = PathSafety.ValidateReceiveRoot(dataDirectory);
        try
        {
            trust = ReleaseTrust.Load(this.installRoot);
            var envelope = ReleaseVerifier.ReadEnvelope(Path.Combine(this.installRoot, "release-manifest.json"));
            published = ReleaseVerifier.VerifyManifest(envelope, trust, AppRelease.RuntimeIdentifier, currentVersion, requireNewer: false);
            if (ReleaseVersion.Parse(published.Manifest.Version).CompareTo(ReleaseVersion.Parse(currentVersion)) != 0)
                throw new InvalidDataException("本机发行清单版本与程序版本不一致。");
            if (!File.Exists(Path.Combine(this.installRoot, "Boardcast-update.zip"))) throw new InvalidDataException("本机发行包未包含可分享的更新包。");
            status = new() { CanUpdate = true, Message = "更新包会先验证发行签名；安装前需要结束所有传输。" };
            listener = new TcpListener(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
            if (Socket.OSSupportsIPv6) listener.Server.DualMode = true;
            listener.Start(8); updatePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or System.Security.Cryptography.CryptographicException or JsonException or ArgumentException or UnauthorizedAccessException)
        {
            listener?.Stop(); listener = null; published = null; initialDiagnostic = ex.Message; status = new() { State = "disabled", Message = "此开发版本未配置受信任的发行更新包。" };
        }
    }
    private string ReadResultMessage()
    {
        try
        {
            string path = PathSafety.ResolveUnderRoot(dataDirectory, "updates/last-update.json");
            var file = new FileInfo(path);
            if (!file.Exists) return "";
            if (file.LastWriteTimeUtc.Ticks == resultWriteTicks && file.Length == resultLength) return resultMessage;
            resultWriteTicks = file.LastWriteTimeUtc.Ticks; resultLength = file.Length; resultMessage = "";
            if (file.Length is <= 0 or > 16384) return "";
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String ||
                !json.RootElement.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String || !ReleaseVersion.TryParse(version.GetString(), out _)) return "";
            resultMessage = state.GetString() switch
            {
                "completed" when version.GetString() == currentVersion => $"已更新到 v{currentVersion}。",
                "rolled-back" => $"上次更新未通过启动检查，已恢复 v{currentVersion}。",
                "failed" => $"上次更新未完成，当前版本为 v{currentVersion}；更新详情与可用备份已保留。",
                _ => ""
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        return resultMessage;
    }
    private void SetStatus(PeerUpdateStatus value) { lock (statusGate) status = value; }
    public async Task RunAsync(CancellationToken ct)
    {
        if (listener is null) { if (initialDiagnostic.Length > 0) Diagnostic?.Invoke("发行更新不可用：" + initialDiagnostic); await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false); return; }
        await RestoreAsync(ct).ConfigureAwait(false);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (!online || !serveSlots.Wait(0)) { client.Dispose(); continue; }
                string id = Guid.NewGuid().ToString("N");
                Task operation = ServeAsync(client, ct);
                connections[id] = operation;
                _ = operation.ContinueWith(_ => { connections.TryRemove(id, out var ignored); serveSlots.Release(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(connections.Values).ConfigureAwait(false);
        }
    }
    private static TransferOffer PackageOffer(VerifiedRelease release) => new()
    {
        Entries = [new() { RelativePath = "Boardcast-update.zip", Size = release.Manifest.PackageBytes }]
    };
    private async Task<ChunkManifest> GetPackageChunksAsync(CancellationToken ct)
    {
        await manifestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (packageChunks is not null) return packageChunks;
            if (published is null) throw new InvalidDataException("此设备没有可分享的发行包。");
            string package = PathSafety.ResolveUnderRoot(installRoot, "Boardcast-update.zip");
            await ReleaseVerifier.VerifyPackageAsync(package, published, ct).ConfigureAwait(false);
            packageChunks = await ChunkTransfer.BuildManifestAsync(new TransferPlan(PackageOffer(published),
                new Dictionary<string, string> { ["Boardcast-update.zip"] = package }), ct).ConfigureAwait(false);
            return packageChunks;
        }
        finally { manifestGate.Release(); }
    }
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(10));
            var token = deadline.Token;
            try
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !LanAddress.IsAllowed(remote.Address) || published is null || !online) return;
                using var tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = identity.Certificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token).ConfigureAwait(false);
                if (tls.RemoteCertificate is null) return;
                var request = await WireProtocol.ReadAsync<UpdateMessage>(tls, token).ConfigureAwait(false);
                if (request.Type != "manifest") return;
                var chunks = await GetPackageChunksAsync(token).ConfigureAwait(false);
                await WireProtocol.WriteAsync(tls, new UpdateMessage { Type = "manifest", Release = published.Envelope, Chunks = chunks }, token).ConfigureAwait(false);
                while (online && !token.IsCancellationRequested)
                {
                    request = await WireProtocol.ReadAsync<UpdateMessage>(tls, token).ConfigureAwait(false);
                    if (request.Type == "package" && request.PackageSha256 == published.Manifest.PackageSha256)
                    {
                        // Preserve the old sender protocol for older clients; current clients use the shared chunk path below.
                        string package = PathSafety.ResolveUnderRoot(installRoot, "Boardcast-update.zip");
                        await ReleaseVerifier.VerifyPackageAsync(package, published, token).ConfigureAwait(false);
                        await using var source = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                        await WireProtocol.WriteAsync(tls, new UpdateMessage { Type = "package", PackageBytes = source.Length }, token).ConfigureAwait(false);
                        await source.CopyToAsync(tls, 128 * 1024, token).ConfigureAwait(false);
                        return;
                    }
                    if (request.Type != "package-chunk" || request.PackageSha256 != published.Manifest.PackageSha256 || request.Chunk is null) return;
                    var bytes = await ChunkTransfer.ReadChunkAsync(chunks, request.Chunk, path => PathSafety.ResolveUnderRoot(installRoot, path), token).ConfigureAwait(false);
                    await WireProtocol.WriteAsync(tls, new UpdateMessage { Type = "package-chunk", PackageBytes = bytes.Length }, token).ConfigureAwait(false);
                    await tls.WriteAsync(bytes, token).ConfigureAwait(false);
                    await tls.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (EndOfStreamException) { }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or SocketException or InvalidDataException)
            { if (!ct.IsCancellationRequested) Diagnostic?.Invoke("更新包分享未完成：" + ex.Message); }
        }
    }
    public Task DownloadAsync(PeerInfo peer, CancellationToken ct) => DownloadAsync(peer, [peer], ct);
    public async Task DownloadAsync(PeerInfo peer, IEnumerable<PeerInfo> peers, CancellationToken ct)
    {
        if (!online) throw new InvalidDataException("请先连接局域网再下载更新。");
        if (trust is null || !Status.CanUpdate) throw new InvalidDataException(Status.Message);
        if (!downloadGate.Wait(0)) throw new InvalidDataException("另一个更新操作正在进行。");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (statusGate) downloadCancellation = cancellation;
        var token = cancellation.Token;
        var sources = new ConcurrentDictionary<string, PackageSource>(StringComparer.OrdinalIgnoreCase);
        string helperRoot = "";
        bool selectedThisDownload = false;
        var previousStatus = Status;
        try
        {
            SetStatus(new() { CanUpdate = true, State = "downloading", Version = peer.UpdateVersion, PeerName = peer.Name, Message = "正在验证设备身份与发行签名…" });
            var primary = await ConnectSourceAsync(peer, token).ConfigureAwait(false);
            sources[primary.Fingerprint] = primary;
            var release = primary.Release;
            string temporary = PathSafety.ResolveUnderRoot(dataDirectory, "updates/download-" + release.Manifest.PackageSha256.ToLowerInvariant());
            Directory.CreateDirectory(temporary);
            WritePrivateJson(PathSafety.ResolveUnderRoot(temporary, "release-manifest.json"), release.Envelope);
            WritePrivateJson(PathSafety.ResolveUnderRoot(temporary, "chunks.json"), primary.Chunks);
            WritePrivateJson(SelectionPath, new SelectedUpdate { PackageSha256 = release.Manifest.PackageSha256, PeerName = peer.Name });
            selectedThisDownload = true;
            if (downloadedRoot.Length > 0 && downloadedRoot != temporary)
                UpdateInstallPlan.DeleteOwnedDirectory(downloadedRoot, Path.Combine(dataDirectory, "updates"), "download-");
            if (preparedHelperRoot.Length > 0) UpdateInstallPlan.DeleteOwnedDirectory(preparedHelperRoot, Path.Combine(dataDirectory, "updates"), "helper-");
            downloaded = null; downloadedRoot = temporary; preparedHelperRoot = "";
            async Task<PackageSource> ConnectCandidateAsync(PeerInfo candidate)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                var source = await ConnectSourceAsync(candidate, probe.Token).ConfigureAwait(false);
                if (source.Release.Manifest.PackageSha256 != release.Manifest.PackageSha256 || source.Release.Manifest.PackageBytes != release.Manifest.PackageBytes || source.Chunks.Id != primary.Chunks.Id)
                { source.Dispose(); throw new InvalidDataException("其他设备提供的更新包与所选版本内容不一致。"); }
                sources[source.Fingerprint] = source;
                return source;
            }
            var candidates = peers.Where(candidate => candidate.Fingerprint != peer.Fingerprint && candidate.LanOnline && !candidate.DepartureHint &&
                candidate.Runtime == peer.Runtime && candidate.UpdateVersion == peer.UpdateVersion).DistinctBy(candidate => candidate.Fingerprint).Take(2)
                .Select(candidate => (Peer: candidate, Source: new Lazy<Task<PackageSource>>(() => ConnectCandidateAsync(candidate)))).ToList();
            var failedCandidates = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            using var store = await ChunkStore.OpenAsync(primary.Chunks, temporary, token).ConfigureAwait(false);
            long reused = store.VerifiedBytes;
            var progress = new PeerUpdateStatus { CanUpdate = true, CanResume = true, State = "downloading", Version = release.Manifest.Version, PeerName = peer.Name,
                Message = "正在传输并校验更新包…", TotalBytes = release.Manifest.PackageBytes, ReusedBytes = reused, SourceCount = sources.Count };
            SetStatus(progress with { BytesReceived = reused });
            long remainingBytes = release.Manifest.PackageBytes - reused;
            var scheduling = new ChunkPeerPolicy(remainingBytes, primary.Chunks.ChunkSize);
            long peerBytes = 0;
            async Task<byte[]> FetchAsync(ChunkRequest request, CancellationToken chunkToken)
            {
                Exception? failure = null;
                foreach (var candidate in candidates)
                {
                    if (failedCandidates.ContainsKey(candidate.Peer.Fingerprint)) continue;
                    bool usefulProbe = release.Manifest.PackageBytes - store.VerifiedBytes >= 8L * 1024 * 1024 && ChunkTransfer.DescribeChunk(primary.Chunks, request).Length >= 256 * 1024;
                    bool reserved = !primary.Failed && usefulProbe && scheduling.ShouldTryPeer(candidate.Peer.Fingerprint, release.Manifest.PackageBytes);
                    if (!primary.Failed && !reserved) continue;
                    var timer = Stopwatch.StartNew();
                    int received = 0; bool success = false;
                    try
                    {
                        var source = await candidate.Source.Value.ConfigureAwait(false);
                        var data = await source.ReadAsync(request, chunkToken, timeout: scheduling.PeerTimeout).ConfigureAwait(false);
                        received = data.Length; success = true; Interlocked.Add(ref peerBytes, data.Length);
                        return data;
                    }
                    catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException or InvalidDataException or OperationCanceledException && !chunkToken.IsCancellationRequested)
                    { failedCandidates[candidate.Peer.Fingerprint] = 0; failure = ex; }
                    finally { if (reserved) scheduling.RecordPeer(candidate.Peer.Fingerprint, received, timer.Elapsed, success); }
                }
                if (!primary.Failed)
                {
                    try
                    {
                        return await primary.ReadAsync(request, chunkToken, elapsed => scheduling.RecordOrigin(ChunkTransfer.DescribeChunk(primary.Chunks, request).Length, elapsed)).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException or InvalidDataException or OperationCanceledException && !chunkToken.IsCancellationRequested)
                    { primary.Failed = true; failure = ex; }
                    // A verified alternative also provides failover when the selected device disappears.
                    return await FetchAsync(request, chunkToken).ConfigureAwait(false);
                }
                throw new IOException("可用更新来源已断开；已验证的内容已保留，可重新选择此版本继续。", failure);
            }
            int parallelism = remainingBytes >= ChunkPeerPolicy.MinimumPeerTransferBytes ? Math.Min(candidates.Count + 1, 3) : 1;
            await ChunkTransfer.DownloadAsync(store, FetchAsync, bytes => SetStatus(progress with { BytesReceived = bytes, PeerBytes = Interlocked.Read(ref peerBytes), SourceCount = sources.Count }), token, concurrency: parallelism).ConfigureAwait(false);
            string package = PathSafety.ResolveUnderRoot(temporary, "Boardcast-update.zip");
            SetStatus(progress with { BytesReceived = release.Manifest.PackageBytes, Message = "正在验证完整发行包与所有文件…" });
            await ReleaseVerifier.VerifyPackageAsync(package, release, token).ConfigureAwait(false);
            helperRoot = PathSafety.ResolveUnderRoot(dataDirectory, "updates/helper-" + Guid.NewGuid().ToString("N"));
            await PrepareHelperAsync(helperRoot, token).ConfigureAwait(false);
            WritePrivateJson(SelectionPath, new SelectedUpdate { PackageSha256 = release.Manifest.PackageSha256, PeerName = peer.Name, HelperName = Path.GetFileName(helperRoot), Ready = true });
            downloaded = release; preparedHelperRoot = helperRoot;
            SetStatus(progress with { State = "ready", CanResume = false, PeerBytes = Interlocked.Read(ref peerBytes), SourceCount = sources.Count, BytesReceived = release.Manifest.PackageBytes, Message = "发行签名与所有文件已验证，可在传输结束后重启更新。" });
        }
        catch (Exception ex)
        {
            bool resumable = selectedThisDownload;
            if (!selectedThisDownload && previousStatus.State is "ready" or "paused")
                SetStatus(previousStatus with { Message = "所选版本暂时无法下载；上次更新内容仍保留。" + ex.Message });
            else SetStatus(Status with { State = ex is OperationCanceledException ? "paused" : "failed", CanResume = resumable,
                Message = ex is OperationCanceledException ? "更新下载已暂停；已验证的内容已保留。" : ex.Message });
            if (helperRoot.Length > 0) UpdateInstallPlan.DeleteOwnedDirectory(helperRoot, Path.Combine(dataDirectory, "updates"), "helper-");
            throw;
        }
        finally
        {
            foreach (var source in sources.Values) source.Dispose();
            lock (statusGate) downloadCancellation = null;
            downloadGate.Release();
        }
    }
    private string SelectionPath => PathSafety.ResolveUnderRoot(dataDirectory, "updates/selected-update.json");
    private static void WritePrivateJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = PrivateFile.Create(temporary)) file.Write(JsonSerializer.SerializeToUtf8Bytes(value, BoardcastJson.Options));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        if (trust is null || !File.Exists(SelectionPath)) return;
        await downloadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (downloaded is not null || new FileInfo(SelectionPath).Length > 16384) return;
            SetStatus(Status with { State = "restoring", Message = "正在检查上次下载的更新…" });
            var selected = JsonSerializer.Deserialize<SelectedUpdate>(File.ReadAllBytes(SelectionPath), BoardcastJson.Options) ?? throw new InvalidDataException("更新记录为空。");
            if (selected.PackageSha256 is not { Length: 64 } || !selected.PackageSha256.All(Uri.IsHexDigit)) throw new InvalidDataException("更新记录的摘要无效。");
            string root = PathSafety.ResolveUnderRoot(dataDirectory, "updates/download-" + selected.PackageSha256.ToLowerInvariant());
            var release = ReleaseVerifier.VerifyManifest(ReleaseVerifier.ReadEnvelope(PathSafety.ResolveUnderRoot(root, "release-manifest.json")), trust, AppRelease.RuntimeIdentifier, currentVersion, requireNewer: false);
            if (!release.Manifest.PackageSha256.Equals(selected.PackageSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新记录与发行签名不匹配。");
            if (ReleaseVersion.Parse(release.Manifest.Version).CompareTo(ReleaseVersion.Parse(currentVersion)) <= 0)
            {
                File.Delete(SelectionPath);
                SetStatus(new() { CanUpdate = true, State = "idle" });
                return;
            }
            _ = ReleaseVerifier.VerifyManifest(release.Envelope, trust, AppRelease.RuntimeIdentifier, currentVersion);
            string chunksPath = PathSafety.ResolveUnderRoot(root, "chunks.json");
            if (new FileInfo(chunksPath).Length > WireProtocol.MaximumFrameBytes) throw new InvalidDataException("分块清单过大。");
            var chunks = JsonSerializer.Deserialize<ChunkManifest>(File.ReadAllBytes(chunksPath), BoardcastJson.Options) ?? throw new InvalidDataException("更新分块清单为空。");
            ChunkTransfer.ValidateManifest(chunks, PackageOffer(release));
            using var store = await ChunkStore.OpenAsync(chunks, root, ct).ConfigureAwait(false);
            downloadedRoot = root;
            if (selected.HelperName is { Length: > 0 } && Path.GetFileName(selected.HelperName) == selected.HelperName)
            {
                string priorHelper = PathSafety.ResolveUnderRoot(dataDirectory, "updates/" + selected.HelperName);
                UpdateInstallPlan.ValidateOwnedDirectory(priorHelper, Path.Combine(dataDirectory, "updates"), "helper-");
                preparedHelperRoot = priorHelper;
            }
            var progress = new PeerUpdateStatus { CanUpdate = true, CanResume = true, State = "paused", Version = release.Manifest.Version, PeerName = selected.PeerName ?? "",
                BytesReceived = store.VerifiedBytes, ReusedBytes = store.VerifiedBytes, TotalBytes = release.Manifest.PackageBytes, Message = "已恢复上次下载记录，选择同一版本可继续传输。" };
            if (store.VerifiedBytes == release.Manifest.PackageBytes)
            {
                await store.CompleteAsync(ct).ConfigureAwait(false);
                await ReleaseVerifier.VerifyPackageAsync(PathSafety.ResolveUnderRoot(root, "Boardcast-update.zip"), release, ct).ConfigureAwait(false);
                string helper = PathSafety.ResolveUnderRoot(dataDirectory, "updates/helper-" + Guid.NewGuid().ToString("N"));
                try { await PrepareHelperAsync(helper, ct).ConfigureAwait(false); }
                catch { UpdateInstallPlan.DeleteOwnedDirectory(helper, Path.Combine(dataDirectory, "updates"), "helper-"); throw; }
                if (preparedHelperRoot.Length > 0)
                    UpdateInstallPlan.DeleteOwnedDirectory(preparedHelperRoot, Path.Combine(dataDirectory, "updates"), "helper-");
                downloaded = release; preparedHelperRoot = helper;
                WritePrivateJson(SelectionPath, selected with { HelperName = Path.GetFileName(helper), Ready = true });
                progress = progress with { State = "ready", CanResume = false, Message = "已恢复并重新验证更新包，可在传输结束后重启更新。" };
            }
            SetStatus(progress);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        { SetStatus(new() { CanUpdate = true, State = "failed", Message = "上次更新记录无法恢复：" + ex.Message }); }
        finally { downloadGate.Release(); }
    }
    private async Task<PackageSource> ConnectSourceAsync(PeerInfo peer, CancellationToken ct)
    {
        if (peer.UpdatePort is < 1 or > 65535 || peer.Fingerprint is not { Length: 64 } || !peer.Fingerprint.All(Uri.IsHexDigit)) throw new InvalidDataException("此设备未提供可用更新端口或有效身份。");
        var client = new TcpClient();
        SslStream? tls = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var token = timeout.Token;
            await client.ConnectAsync(LanAddress.Parse(peer.Address), peer.UpdatePort, token).ConfigureAwait(false);
            tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null && IdentityStore.GetFingerprint(certificate).Equals(peer.Fingerprint, StringComparison.OrdinalIgnoreCase));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Boardcast", ClientCertificates = new X509CertificateCollection { identity.Certificate }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, token).ConfigureAwait(false);
            // Preparing the signed package manifest can involve disk verification; the connect/TLS deadline is separate.
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            await WireProtocol.WriteAsync(tls, new UpdateMessage { Type = "manifest" }, token).ConfigureAwait(false);
            var reply = await WireProtocol.ReadAsync<UpdateMessage>(tls, token).ConfigureAwait(false);
            if (reply.Type != "manifest" || reply.Release is null) throw new InvalidDataException("设备没有提供发行清单。");
            var release = ReleaseVerifier.VerifyManifest(reply.Release, trust!, AppRelease.RuntimeIdentifier, currentVersion);
            if (release.Manifest.Version != peer.UpdateVersion) throw new InvalidDataException("此设备版本已变化，请刷新后重试。");
            if (reply.Chunks is null) throw new InvalidDataException("此设备尚不支持可恢复的更新传输，请从使用新版传输协议的设备获取更新。");
            ChunkTransfer.ValidateManifest(reply.Chunks, PackageOffer(release));
            return new PackageSource(client, tls, peer.Fingerprint, release, reply.Chunks);
        }
        catch { tls?.Dispose(); client.Dispose(); throw; }
    }
    private sealed class PackageSource(TcpClient client, SslStream tls, string fingerprint, VerifiedRelease release, ChunkManifest chunks) : IDisposable
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        public string Fingerprint => fingerprint;
        public VerifiedRelease Release => release;
        public ChunkManifest Chunks => chunks;
        public volatile bool Failed;
        public async Task<byte[]> ReadAsync(ChunkRequest request, CancellationToken ct, Action<TimeSpan>? measured = null, TimeSpan? timeout = null)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(45));
                var token = deadline.Token;
                var timer = Stopwatch.StartNew();
                await WireProtocol.WriteAsync(tls, new UpdateMessage { Type = "package-chunk", PackageSha256 = release.Manifest.PackageSha256, Chunk = request }, token).ConfigureAwait(false);
                var reply = await WireProtocol.ReadAsync<UpdateMessage>(tls, token).ConfigureAwait(false);
                var (file, _, expectedBytes) = ChunkTransfer.DescribeChunk(chunks, request);
                if (reply.Type != "package-chunk" || reply.PackageBytes != expectedBytes) throw new InvalidDataException("更新分块响应无效。");
                var data = new byte[(int)reply.PackageBytes];
                await tls.ReadExactlyAsync(data, token).ConfigureAwait(false);
                if (!ChunkTransfer.Matches(data, file.Hashes[request.ChunkIndex])) throw new InvalidDataException("更新来源提供的分块校验失败。");
                measured?.Invoke(timer.Elapsed);
                return data;
            }
            finally { gate.Release(); }
        }
        public void Dispose() { tls.Dispose(); client.Dispose(); gate.Dispose(); }
    }
    private sealed record SelectedUpdate
    {
        public string PackageSha256 { get; set; } = "";
        public string PeerName { get; set; } = "";
        public string HelperName { get; set; } = "";
        public bool Ready { get; set; }
    }
    private async Task PrepareHelperAsync(string helperRoot, CancellationToken ct)
    {
        if (published is null) throw new InvalidDataException("本机缺少受信任的安装辅助程序。");
        Directory.CreateDirectory(helperRoot);
        foreach (var item in published.Manifest.Files)
        {
            string source = PathSafety.ResolveUnderRoot(installRoot, item.Path), target = PathSafety.ResolveUnderRoot(helperRoot, item.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                await input.CopyToAsync(output, 128 * 1024, ct).ConfigureAwait(false);
            await ReleaseVerifier.VerifyInstalledFileAsync(helperRoot, item, ct).ConfigureAwait(false);
        }
    }
    public async Task LaunchInstallerAsync(int uiProcessId, IReadOnlyList<int> processIds, string[] uiArgs, Func<bool> hasActiveTransfers, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || Process.GetCurrentProcess().SessionId == 0) throw new InvalidDataException("服务模式需要由管理员使用正式安装器更新。");
        if (hasActiveTransfers() || HasActiveShares) throw new InvalidDataException("请先等待当前传输结束，并处理待接收请求，再重启更新。");
        if (!downloadGate.Wait(0)) throw new InvalidDataException("另一个更新操作正在进行。");
        string helperRoot = preparedHelperRoot;
        try
        {
            if (downloaded is null || Status.State != "ready" || published is null || trust is null) throw new InvalidDataException("尚无已验证且可安装的更新包。");
            if (UpdateInstallPlan.IsUnder(dataDirectory, installRoot)) throw new InvalidDataException("用户数据必须位于程序目录之外，才能安全切换版本。");
            // The complete helper snapshot was copied and verified during download, before the short installation IPC.
            if (string.IsNullOrEmpty(helperRoot)) throw new InvalidDataException("安装辅助程序尚未准备就绪，请重新下载更新。");
            UpdateInstallPlan.ValidateOwnedDirectory(helperRoot, Path.Combine(dataDirectory, "updates"), "helper-");
            foreach (var item in published.Manifest.Files.Where(f => f.Path is "Boardcast.Updater.exe" or "Boardcast.Updater.dll" or "Boardcast.Core.dll" or "Boardcast.Updater.deps.json" or "Boardcast.Updater.runtimeconfig.json" or "release-trust.json"))
                await ReleaseVerifier.VerifyInstalledFileAsync(helperRoot, item, ct).ConfigureAwait(false);
            string parent = Path.GetDirectoryName(installRoot) ?? throw new InvalidDataException("程序不能安装在磁盘根目录。");
            string stagedRoot = PathSafety.ResolveUnderRoot(parent, ".Boardcast-update-" + Guid.NewGuid().ToString("N"));
            string backupRoot = PathSafety.ResolveUnderRoot(parent, ".Boardcast-backup-" + Guid.NewGuid().ToString("N"));
            if (uiProcessId <= 0 || !processIds.Contains(Environment.ProcessId) || !processIds.Contains(uiProcessId)) throw new InvalidDataException("更新需要确认当前界面及后台进程。");
            var processes = processIds.Distinct().Select(UpdateProcessIdentity.Capture).ToList();
            foreach (var process in processes)
                if (process.Id != Environment.ProcessId && !UpdateInstallPlan.IsUnder(process.ExecutablePath, installRoot)) throw new InvalidDataException("更新不能等待其他程序的进程。");
            var plan = new UpdateInstallPlan { InstallRoot = installRoot, StagedRoot = stagedRoot, BackupRoot = backupRoot, DownloadRoot = downloadedRoot, DataDirectory = dataDirectory, Version = downloaded.Manifest.Version, CurrentVersion = currentVersion, Processes = processes, RestartArguments = uiArgs.ToList(), TrustSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(helperRoot, "release-trust.json")))) };
            plan.Validate();
            string planPath = Path.Combine(helperRoot, "update-plan.json");
            using (var file = PrivateFile.Create(planPath)) file.Write(JsonSerializer.SerializeToUtf8Bytes(plan, BoardcastJson.Options));
            if (hasActiveTransfers() || HasActiveShares) throw new InvalidDataException("有新的传输正在进行，请结束后重试。");
            var start = new ProcessStartInfo(Path.Combine(helperRoot, "Boardcast.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helperRoot };
            start.ArgumentList.Add("--plan"); start.ArgumentList.Add(planPath);
            using var installer = Process.Start(start) ?? throw new IOException("无法启动更新安装器。");
            SetStatus(Status with { State = "installing", Message = "正在等待界面与后台退出，然后安装并重启。" });
        }
        catch
        {
            UpdateInstallPlan.DeleteOwnedDirectory(helperRoot, Path.Combine(dataDirectory, "updates"), "helper-"); preparedHelperRoot = "";
            SetStatus(Status with { State = "failed", Message = "安装准备未完成，请重新下载更新后重试。" });
            throw;
        }
        finally { downloadGate.Release(); }
    }
    public void Dispose() { CancelDownload(); listener?.Stop(); }
    private sealed record UpdateMessage
    {
        public string Type { get; set; } = "";
        public SignedReleaseEnvelope? Release { get; set; }
        public ChunkManifest? Chunks { get; set; }
        public ChunkRequest? Chunk { get; set; }
        public string PackageSha256 { get; set; } = "";
        public long PackageBytes { get; set; }
    }
}
