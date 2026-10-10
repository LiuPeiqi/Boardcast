using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Boardcast.Core;
namespace Boardcast.Daemon;
public sealed partial class DaemonHost : IDisposable
{
    private readonly SettingsStore store;
    private AppSettings settings;
    private readonly object settingsGate = new();
    private readonly IdentityStore identity;
    private readonly DiscoveryService discovery;
    private readonly TransferService transport;
    private readonly SubnetDiscoveryService subnetDiscovery;
    private readonly PeerUpdateService updates;
    private readonly object updateGate = new();
    private Task? updateDownloadTask;
    private volatile bool updateInstalling;
    private readonly bool noUi;
    private readonly ConcurrentDictionary<string, TransferProgress> transfers = new();
    private readonly ConcurrentDictionary<string, (PendingTransfer Item, TaskCompletionSource<IncomingDecision> Completion)> pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> outgoing = new();
    private readonly ConcurrentDictionary<string, Task> sendingTasks = new();
    private readonly object pendingGate = new();
    private readonly SemaphoreSlim outgoingSlots = new(4);
    private readonly SemaphoreSlim settingsCommands = new(1);
    private readonly object outgoingGate = new();
    private readonly ConcurrentQueue<string> diagnostics = new();
    private readonly CancellationTokenSource stopped = new();
    private DateTimeOffset lastUi;
    public DaemonHost(string? dataDirectory, int? port, bool noUi, int discoveryPort = DiscoveryService.DiscoveryPort)
    {
        store = new SettingsStore(dataDirectory);
        settings = store.Load();
        if (port.HasValue) settings.Port = port.Value;
        SettingsStore.Validate(settings);
        identity = new IdentityStore(store.DataDirectory);
        this.noUi = noUi;
        updates = new PeerUpdateService(identity, AppContext.BaseDirectory, store.DataDirectory);
        updates.SetOnline(settings.LanOnline);
        discovery = new DiscoveryService(LocalPeer, report: ReportDiagnostic, discoveryPort: discoveryPort);
        transport = new TransferService(identity, Settings, LocalPeer, DecideAsync, OnProgress, discovery.Remember);
        transport.DecideSubnets = DecideSubnetsAsync;
        subnetDiscovery = new SubnetDiscoveryService(Settings, discovery.ProbeAddressAsync, ProbeSubnetPeerAsync, () => discovery.Peers);
        transport.Diagnostic += ReportDiagnostic;
        updates.Diagnostic += ReportDiagnostic;
        LoadRecoverableSends();
    }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, BoardcastJson.Options), BoardcastJson.Options)!;
    private void ReportDiagnostic(string message) { diagnostics.Enqueue(message); while (diagnostics.Count > 20) diagnostics.TryDequeue(out _); }
    private AppSettings Settings() { lock (settingsGate) return Clone(settings) with { LanOnline = settings.LanOnline && !updateInstalling }; }
    private PeerInfo LocalPeer() { lock (settingsGate) return new() { Fingerprint = identity.Fingerprint, Name = settings.DeviceName, GroupName = settings.GroupName, Platform = "windows", Port = settings.Port, ReceivingEnabled = settings.ReceivingEnabled, LanOnline = settings.LanOnline && !updateInstalling, AppVersion = AppRelease.CurrentVersion, Runtime = AppRelease.RuntimeIdentifier, UpdatePort = updates.Port, UpdateVersion = updates.AvailableReleaseVersion, DiscoverySubnets = DiscoverySubnets.LocalInterfaces(settings.Port, advertiseActual: true) }; }
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopped.Token);
        using var single = new FileStream(Path.Combine(store.DataDirectory, "daemon.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var token = linked.Token;
        var tasks = new[] { transport.RunAsync(token), discovery.RunAsync(token), subnetDiscovery.RunAsync(token), updates.RunAsync(token), IpcServer.RunAsync(store.DataDirectory, HandleAsync, token) };
        try { await Task.WhenAny(tasks); linked.Cancel(); await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            stopped.Cancel();
            foreach (var cancellation in outgoing.Values) { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }
            await Task.WhenAll(sendingTasks.Values);
            if (updateDownloadTask is not null) await updateDownloadTask;
        }
    }
    private void OnProgress(TransferProgress progress)
    {
        progress = RecoveryProgress(progress);
        transfers[progress.Id] = progress with { };
        if (transfers.Count > 200)
            foreach (var entry in transfers.Values.Where(t => !t.CanResume && t.State is ("completed" or "cancelled" or "rejected" or "failed")).OrderBy(t => t.UpdatedAt).Take(50))
                transfers.TryRemove(entry.Id, out _);
    }
    private DaemonSnapshot Snapshot() => new() { AppVersion = AppRelease.CurrentVersion, Update = updates.Status, Fingerprint = identity.Fingerprint, Settings = Settings(), Peers = discovery.Peers, Pending = pending.Values.Select(p => Clone(p.Item)).ToList(), PendingSubnets = pendingSubnets.Values.Select(p => Clone(p.Item)).ToList(), LocalDiscoverySubnets = DiscoverySubnets.LocalInterfaces(Settings().Port), Transfers = transfers.Values.OrderByDescending(t => t.UpdatedAt).Select(t => t with { }).ToList(), Diagnostics = diagnostics.ToList() };
    public async Task<IpcResponse> HandleAsync(IpcRequest request)
    {
        try
        {
            switch (request.Command.ToLowerInvariant())
            {
                case "status": return new() { Snapshot = Snapshot() };
                case "discover": await discovery.ProbeAsync(stopped.Token); return new() { Snapshot = Snapshot() };
                case "settings":
                    if (request.Settings is null) throw new InvalidDataException("Settings are required.");
                    await ApplySettingsAsync(Clone(request.Settings));
                    return new() { Snapshot = Snapshot() };
                case "set-online":
                    await SetOnlineAsync(request.LanOnline);
                    return new() { Snapshot = Snapshot() };
                case "connect":
                    var connected = await transport.ConnectAsync(request.Address, request.Port, request.Peer?.Fingerprint, stopped.Token);
                    await LearnConnectedSubnetAsync(connected);
                    return new() { Snapshot = Snapshot() };
                case "subnet-forget":
                    await ForgetSubnetsAsync(request.Subnets);
                    return new() { Snapshot = Snapshot() };
                case "subnet-share": return StartSubnetShares(request);
                case "subnet-decide":
                    await DecideSubnetRequestAsync(request.TransferId, request.Accept);
                    return new() { Snapshot = Snapshot() };
                case "send": return StartSends(request, multiple: false);
                case "send-many": return StartSends(request, multiple: true);
                case "decide":
                    if (!pending.TryGetValue(request.TransferId, out var waiting)) throw new InvalidDataException("This request is no longer pending.");
                    if (request.Accept && !Settings().LanOnline) throw new InvalidDataException("局域网连接已断开。");
                    if (request.Accept && !Settings().ReceivingEnabled) throw new InvalidDataException("Receiving is paused.");
                    string destination = request.Destination.Length > 0 ? request.Destination : waiting.Item.Destination;
                    if (request.Accept) PathSafety.ValidateReceiveRoot(destination);
                    waiting.Completion.TrySetResult(new(request.Accept, destination, request.Accept ? "Accepted" : "Declined"));
                    return new();
                case "pause": return PauseSend(request.TransferId);
                case "resume": return ResumeSend(request.TransferId);
                case "cancel":
                    if (transfers.TryGetValue(request.TransferId, out var finished) && (finished.State is "completed" or "rejected" || finished.State == "cancelled" && !finished.CanDiscard))
                        return new() { Snapshot = Snapshot() };
                    if (await transport.CancelAsync(request.TransferId))
                        return new() { Snapshot = Snapshot() };
                    recoverableSends.TryGetValue(request.TransferId, out var discardedSend);
                    ForgetSend(request.TransferId);
                    lock (outgoingGate)
                    {
                        if (outgoing.TryGetValue(request.TransferId, out var send)) send.Cancel();
                        else if (transfers.TryGetValue(request.TransferId, out var cancelled))
                            transfers[request.TransferId] = cancelled with { State = "cancelled", CanResume = false, CanDiscard = false, Message = "已取消。" };
                    }
                    if (pending.TryGetValue(request.TransferId, out var receive)) receive.Completion.TrySetResult(new(false, "", "Cancelled"));
                    if (discardedSend is not null)
                    {
                        var target = discovery.Peers.FirstOrDefault(peer => peer.Fingerprint.Equals(discardedSend.Peer.Fingerprint, StringComparison.OrdinalIgnoreCase)) ?? discardedSend.Peer;
                        bool remoteClean = await transport.CancelRemoteReceiveAsync(target, request.TransferId, stopped.Token);
                        if (transfers.TryGetValue(request.TransferId, out var cancelledSend) && cancelledSend.State != "completed")
                            transfers[request.TransferId] = cancelledSend with { State = "cancelled", CanResume = false,
                                Message = remoteClean ? "已取消。接收方已清理未完成内容，完成文件已保留。" : "已取消。接收方清理状态尚未确认。" };
                    }
                    return new() { Snapshot = Snapshot() };
                case "update-download":
                    if (request.Peer is null) throw new InvalidDataException("请选择提供更新的设备。");
                    StartUpdateDownload(Clone(request.Peer));
                    return new() { Snapshot = Snapshot() };
                case "update-cancel":
                    updates.CancelDownload();
                    return new() { Snapshot = Snapshot() };
                case "update-install":
                    await InstallUpdateAsync(request.ProcessId);
                    stopped.CancelAfter(500);
                    return new() { Snapshot = Snapshot() };
                case "stop": stopped.CancelAfter(250); return new();
                default: throw new InvalidDataException("Unknown IPC command.");
            }
        }
        catch (Exception ex) { return new() { Success = false, Error = ex.Message }; }
    }
    private void StartUpdateDownload(PeerInfo peer)
    {
        lock (updateGate)
        {
            var current = Settings();
            var update = updates.Status;
            if (!current.LanOnline || updateInstalling) throw new InvalidDataException("请先连接局域网再下载更新。");
            if (!update.CanUpdate) throw new InvalidDataException(update.Message);
            if (updateDownloadTask is { IsCompleted: false } || update.State is "downloading" or "restoring" or "installing")
                throw new InvalidDataException("另一个更新操作正在进行。");
            if (!PeerUpdateCatalog.IsNewerCompatible(peer, AppRelease.CurrentVersion, AppRelease.RuntimeIdentifier))
                throw new InvalidDataException("请选择适合当前系统的较新版本。");
            var selected = Clone(peer);
            var sources = discovery.Peers.Select(source => Clone(source)).ToList();
            updateDownloadTask = Task.Run(async () =>
            {
                try { await updates.DownloadAsync(selected, sources, stopped.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { if (!stopped.IsCancellationRequested) ReportDiagnostic(ex.Message); }
            });
        }
    }
    private bool HasActiveTransfers() => transport.HasActiveRelayRequests || !outgoing.IsEmpty || !pending.IsEmpty || !pendingSubnets.IsEmpty || transfers.Values.Any(t => t.State is "preparing" or "queued" or "waiting" or "sending" or "pending" or "receiving");
    private async Task InstallUpdateAsync(int uiProcessId)
    {
        await settingsCommands.WaitAsync(stopped.Token);
        bool wasOnline = Settings().LanOnline;
        bool tookOffline = false;
        try
        {
            lock (outgoingGate)
            lock (pendingGate)
            {
                if (updateInstalling) throw new InvalidDataException("正在安装更新，请稍候。");
                if (HasActiveTransfers()) throw new InvalidDataException("请先等待传输完成，并处理待接收请求，再重启更新。");
                updateInstalling = true; tookOffline = true;
            }
            updates.SetOnline(false);
            await discovery.StateChangedAsync(wasOnline, stopped.Token);
            await updates.LaunchInstallerAsync(uiProcessId, [Environment.ProcessId, uiProcessId], ["--data-dir", store.DataDirectory], HasActiveTransfers, stopped.Token);
        }
        catch
        {
            if (tookOffline)
            {
                updateInstalling = false;
                updates.SetOnline(Settings().LanOnline);
                await discovery.StateChangedAsync(wasOnline: false, stopped.Token);
            }
            throw;
        }
        finally { settingsCommands.Release(); }
    }
    private async Task SetOnlineAsync(bool online)
    {
        await settingsCommands.WaitAsync(stopped.Token);
        try
        {
            var updated = Settings(); updated.LanOnline = online;
            await ApplySettingsCoreAsync(updated);
        }
        finally { settingsCommands.Release(); }
    }
    private async Task ApplySettingsAsync(AppSettings updated)
    {
        await settingsCommands.WaitAsync(stopped.Token);
        try
        {
            // Permission changes use the explicit IP/accept/forget commands; stale settings editors must not restore forgotten ranges.
            updated.DiscoverySubnets = Settings().DiscoverySubnets;
            await ApplySettingsCoreAsync(updated);
        }
        finally { settingsCommands.Release(); }
    }
    private async Task ApplySettingsCoreAsync(AppSettings updated)
    {
        if (updateInstalling) throw new InvalidDataException("正在安装更新，请稍候。");
        bool wasOnline;
        lock (settingsGate)
        {
            if (updated.Port != settings.Port) throw new InvalidDataException("Restart the daemon with --port to change its listening port.");
            wasOnline = settings.LanOnline;
            store.Save(updated); settings = updated;
        }
        if (!updated.LanOnline)
        {
            foreach (var entry in outgoing)
                if (!transfers.TryGetValue(entry.Key, out var state) || state.State is "preparing" or "queued" or "waiting")
                    try { entry.Value.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var entry in pending.Values) entry.Completion.TrySetResult(new(false, "", "局域网连接已断开。"));
            foreach (var entry in pendingSubnets.Values) entry.Completion.TrySetResult(new(false, "", "局域网连接已断开。"));
        }
        if (!updated.ReceivingEnabled)
            foreach (var entry in pendingSubnets.Values) entry.Completion.TrySetResult(new(false, "", "接收已暂停。"));
        subnetDiscovery.Refresh();
        updates.SetOnline(updated.LanOnline);
        await discovery.StateChangedAsync(wasOnline, stopped.Token);
    }
    private IpcResponse StartSends(IpcRequest request, bool multiple)
    {
        var current = Settings();
        if (!current.LanOnline) throw new InvalidDataException("请先连接局域网，再发送文件。");
        if (request.Paths is null || request.Sources is null) throw new InvalidDataException("传输项目不能为空。");
        if (request.Paths.Count > 0 && request.Sources.Count > 0) throw new InvalidDataException("Paths 和 Sources 不能同时指定。");
        var sources = Clone(request.Sources.Count > 0 ? request.Sources : request.Paths.Select(path => new TransferSource { Path = path }).ToList());
        if (sources.Count == 0) throw new InvalidDataException("请先添加文件或文件夹。");
        foreach (var source in sources)
        {
            if (source is null) throw new InvalidDataException("传输项目不能为空。");
            source.Path = Path.GetFullPath(source.Path);
        }
        if (request.Summary is null || request.Summary.Length > 16384) throw new InvalidDataException("传输说明过长。");
        var recipients = multiple ? Clone(request.Peers ?? []) : request.Peer is null ? [] : new List<PeerInfo> { Clone(request.Peer) };
        if (recipients.Count is < 1 or > 32) throw new InvalidDataException("请选择 1–32 台接收设备。");
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in recipients)
        {
            if (peer is null || peer.Fingerprint is not { Length: 64 } || !peer.Fingerprint.All(Uri.IsHexDigit) ||
                !fingerprints.Add(peer.Fingerprint) || peer.Port is < 1024 or > 65535 || peer.Name is null || peer.Name.Length > 256)
                throw new InvalidDataException("接收设备无效或重复。");
            _ = LanAddress.Parse(peer.Address);
        }
        var tickets = recipients.Select(peer => new TransferTicket { Id = Guid.NewGuid().ToString("N"), PeerFingerprint = peer.Fingerprint, PeerName = peer.Name }).ToList();
        var cancellations = new List<CancellationTokenSource>();
        string batchId = Guid.NewGuid().ToString("N");
        lock (outgoingGate)
        {
            if (!Settings().LanOnline) throw new InvalidDataException("局域网连接已断开，请先连接。");
            if (outgoing.Count + tickets.Count > 128) throw new InvalidDataException("发送队列已满，请等待现有传输完成。");
            RememberSends(batchId, tickets, recipients, sources, request.Summary);
            for (int i = 0; i < tickets.Count; i++)
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                cancellations.Add(cancellation);
                if (!outgoing.TryAdd(tickets[i].Id, cancellation)) throw new InvalidOperationException();
                OnProgress(new() { Id = tickets[i].Id, Direction = "send", PeerName = recipients[i].Name, State = "preparing" });
            }
        }
        string summary = request.Summary;
        foreach (var task in sendingTasks.Where(t => t.Value.IsCompleted)) sendingTasks.TryRemove(task.Key, out _);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sendingTasks[batchId] = finished.Task;
        _ = Task.Run(async () =>
        {
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
            var registrations = cancellations.Select(cancellation => cancellation.Token.Register(() =>
            {
                if (cancellations.All(item => item.IsCancellationRequested)) preparation.Cancel();
            })).ToList();
            try
            {
                // Enumerate the selected folders once. File mapping and immutable entries are shared by every recipient.
                var shared = ManifestBuilder.BuildPlan(sources, current, preparation.Token);
                shared.Offer.Summary = summary;
                shared.Offer.BatchId = batchId;
                await Task.WhenAll(tickets.Select((ticket, index) => SendQueuedAsync(shared, ticket, recipients[index], cancellations[index].Token)));
            }
            catch (Exception ex)
            {
                for (int i = 0; i < tickets.Count; i++)
                    OnProgress(new() { Id = tickets[i].Id, Direction = "send", PeerName = recipients[i].Name,
                        State = cancellations[i].IsCancellationRequested ? "cancelled" : "failed", Message = ex is OperationCanceledException ? "已取消。" : ex.Message });
            }
            finally
            {
                foreach (var registration in registrations) registration.Dispose();
                lock (outgoingGate)
                    for (int i = 0; i < tickets.Count; i++) { outgoing.TryRemove(tickets[i].Id, out _); cancellations[i].Dispose(); }
                finished.TrySetResult();
                sendingTasks.TryRemove(batchId, out _);
            }
        });
        return new() { TransferId = tickets[0].Id, Transfers = tickets };
    }
    private async Task SendQueuedAsync(TransferPlan shared, TransferTicket ticket, PeerInfo peer, CancellationToken ct)
    {
        bool acquired = false;
        try
        {
            OnProgress(new() { Id = ticket.Id, Direction = "send", PeerName = peer.Name, State = "queued", TotalBytes = shared.Offer.TotalBytes });
            await outgoingSlots.WaitAsync(ct); acquired = true;
            ct.ThrowIfCancellationRequested();
            if (!Settings().LanOnline) throw new InvalidDataException("局域网连接已断开。");
            var plan = new TransferPlan(shared.Offer with { Id = ticket.Id }, shared.LocalFiles);
            await transport.SendAsync(plan, peer, ct);
        }
        catch (OperationCanceledException) { OnProgress(new() { Id = ticket.Id, Direction = "send", PeerName = peer.Name, State = "cancelled", TotalBytes = shared.Offer.TotalBytes }); }
        catch (Exception ex) { OnProgress(new() { Id = ticket.Id, Direction = "send", PeerName = peer.Name, State = "failed", TotalBytes = shared.Offer.TotalBytes, Message = ex.Message }); }
        finally { if (acquired) outgoingSlots.Release(); }
    }
    private async Task<IncomingDecision> DecideAsync(TransferOffer offer, string address, CancellationToken ct)
    {
        if (!Settings().LanOnline) return new(false, "", "局域网连接已断开。");
        var policy = ReceivePolicy.Evaluate(Settings(), offer, offer.SenderFingerprint);
        if (policy.AutoAccept) return new(true, policy.Destination, policy.Reason);

        var completion = new TaskCompletionSource<IncomingDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new PendingTransfer { Offer = offer, Address = address, Destination = policy.Destination, Reason = policy.Reason };
        lock (pendingGate)
        {
            if (!Settings().LanOnline) return new(false, "", "局域网连接已断开。");
            long metadataBytes = JsonSerializer.SerializeToUtf8Bytes(item, BoardcastJson.Options).Length;
            foreach (var current in pending.Values) metadataBytes += JsonSerializer.SerializeToUtf8Bytes(current.Item, BoardcastJson.Options).Length;
            if (pending.Count >= 4 || metadataBytes > 2 * 1024 * 1024) return new(false, "", "Pending request limit reached; send fewer items per batch.");
            if (!pending.TryAdd(offer.Id, (item, completion))) return new(false, "", "Duplicate request.");
        }
        NotifyUi();
        try { return await completion.Task.WaitAsync(ct); }
        finally { pending.TryRemove(offer.Id, out _); }
    }
    private void NotifyUi()
    {
        if (noUi || !Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0) return;
        if (OperatingSystem.IsWindows() && WindowsUiActivation.TryActivate(store.DataDirectory, incoming: true)) return;
        if (DateTimeOffset.UtcNow - lastUi < TimeSpan.FromSeconds(30)) return;
        lastUi = DateTimeOffset.UtcNow;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Boardcast.exe");
            if (!File.Exists(path)) return;
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--incoming"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(store.DataDirectory);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { diagnostics.Enqueue(ex.Message); while (diagnostics.Count > 20) diagnostics.TryDequeue(out _); }
    }
    public void Dispose() { stopped.Cancel(); subnetDiscovery.Dispose(); discovery.Dispose(); updates.Dispose(); identity.Dispose(); stopped.Dispose(); }
}


