using System.Collections.Concurrent;
using Boardcast.Core;

namespace Boardcast.Daemon;

public sealed partial class DaemonHost
{
    private readonly ConcurrentDictionary<string, (PendingSubnetOffer Item, TaskCompletionSource<IncomingDecision> Completion)> pendingSubnets = new();
    private async Task ProbeSubnetPeerAsync(string address, int port, CancellationToken ct)
    {
        var peer = await transport.ConnectAsync(address, port, null, ct, rememberPeer: false);
        // A removed worker must not publish a late authenticated result after a forget/offline command.
        lock (settingsGate)
        {
            ct.ThrowIfCancellationRequested();
            if (!settings.LanOnline || updateInstalling || !settings.DiscoverySubnets.Any(subnet => subnet.Port == port && DiscoverySubnets.Parse(subnet.Cidr).Contains(System.Net.IPAddress.Parse(address)))) return;
            discovery.Remember(peer);
        }
    }
    private async Task LearnConnectedSubnetAsync(PeerInfo peer)
    {
        var learned = DiscoverySubnets.FromConnectedPeer(peer);
        if (learned.Count == 0) return;
        await settingsCommands.WaitAsync(stopped.Token);
        try
        {
            var updated = Settings();
            if (!updated.LanOnline) throw new InvalidDataException("局域网连接已断开。");
            updated.DiscoverySubnets = DiscoverySubnets.Normalize(updated.DiscoverySubnets.Concat(learned));
            await ApplySettingsCoreAsync(updated);
        }
        finally { settingsCommands.Release(); }
    }
    private async Task ForgetSubnetsAsync(List<DiscoverySubnet> subnets)
    {
        var removing = DiscoverySubnets.Normalize(subnets, allowEmpty: false).Select(DiscoverySubnets.Key).ToHashSet(StringComparer.Ordinal);
        await settingsCommands.WaitAsync(stopped.Token);
        try
        {
            var updated = Settings();
            updated.DiscoverySubnets.RemoveAll(subnet => removing.Contains(DiscoverySubnets.Key(subnet)));
            await ApplySettingsCoreAsync(updated);
        }
        finally { settingsCommands.Release(); }
    }
    private async Task<IncomingDecision> DecideSubnetsAsync(PendingSubnetOffer offer, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<IncomingDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingGate)
        {
            if (!Settings().LanOnline) return new(false, "", "局域网连接已断开。");
            if (!Settings().ReceivingEnabled) return new(false, "", "接收已暂停。");
            if (pendingSubnets.Count >= 4) return new(false, "", "待确认的网段分享已满，请先处理现有请求。");
            if (!pendingSubnets.TryAdd(offer.Id, (Clone(offer), completion))) return new(false, "", "重复的网段分享请求。");
        }
        NotifyUi();
        try { return await completion.Task.WaitAsync(ct); }
        finally { pendingSubnets.TryRemove(offer.Id, out _); }
    }
    private async Task DecideSubnetRequestAsync(string id, bool accept)
    {
        await settingsCommands.WaitAsync(stopped.Token);
        try
        {
            if (!pendingSubnets.TryGetValue(id, out var waiting) || waiting.Completion.Task.IsCompleted)
                throw new InvalidDataException("此网段分享已结束。");
            if (accept)
            {
                var updated = Settings();
                if (!updated.LanOnline) throw new InvalidDataException("请先连接局域网，再接收网段。");
                if (!updated.ReceivingEnabled) throw new InvalidDataException("接收已暂停。");
                updated.DiscoverySubnets = DiscoverySubnets.Normalize(updated.DiscoverySubnets.Concat(waiting.Item.Subnets));
                // ApplySettings writes and validates first; a failed write leaves the request pending and never sends accept.
                await ApplySettingsCoreAsync(updated);
            }
            waiting.Completion.TrySetResult(new(accept, "", accept ? "已接收网段并开始发现设备。" : "已拒绝网段分享。"));
            pendingSubnets.TryRemove(id, out _);
        }
        finally { settingsCommands.Release(); }
    }
    private IpcResponse StartSubnetShares(IpcRequest request)
    {
        var current = Settings();
        if (!current.LanOnline) throw new InvalidDataException("请先连接局域网，再分享网段。");
        var selected = DiscoverySubnets.Normalize(request.Subnets.Count > 0 ? request.Subnets : current.DiscoverySubnets, allowEmpty: false);
        var permitted = current.DiscoverySubnets.Concat(DiscoverySubnets.LocalInterfaces(current.Port)).Select(DiscoverySubnets.Key).ToHashSet(StringComparer.Ordinal);
        if (selected.Any(subnet => !permitted.Contains(DiscoverySubnets.Key(subnet)))) throw new InvalidDataException("只可分享本机网段或已获许可的保存网段。");
        var recipients = Clone(request.Peers.Count > 0 ? request.Peers : request.Peer is null ? [] : new List<PeerInfo> { request.Peer });
        if (recipients.Count is < 1 or > 32) throw new InvalidDataException("请选择 1–32 台接收设备。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var peer in recipients)
        {
            if (peer is null || peer.Fingerprint is not { Length: 64 } || !peer.Fingerprint.All(Uri.IsHexDigit) || !seen.Add(peer.Fingerprint) ||
                peer.Fingerprint.Equals(identity.Fingerprint, StringComparison.OrdinalIgnoreCase) || peer.Port is < 1024 or > 65535 || peer.Name is null || peer.Name.Length > 256)
                throw new InvalidDataException("接收设备无效或重复。");
            _ = LanAddress.Parse(peer.Address);
        }
        var tickets = recipients.Select(peer => new TransferTicket { Id = Guid.NewGuid().ToString("N"), PeerName = peer.Name, PeerFingerprint = peer.Fingerprint }).ToList();
        var cancellations = new List<CancellationTokenSource>();
        lock (outgoingGate)
        {
            if (!Settings().LanOnline) throw new InvalidDataException("局域网连接已断开。");
            if (outgoing.Count + tickets.Count > 128) throw new InvalidDataException("发送队列已满，请稍候再试。");
            foreach (var ticket in tickets)
            {
                var stop = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                cancellations.Add(stop); outgoing[ticket.Id] = stop;
                OnProgress(new() { Id = ticket.Id, Kind = "subnets", Direction = "send", PeerName = ticket.PeerName, State = "queued", CurrentFile = "网段分享" });
            }
        }
        string batch = Guid.NewGuid().ToString("N");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sendingTasks[batch] = finished.Task;
        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(tickets.Select((ticket, index) => ShareQueuedSubnetsAsync(ticket, recipients[index], selected, cancellations[index].Token))); }
            finally
            {
                lock (outgoingGate)
                    for (int i = 0; i < tickets.Count; i++) { outgoing.TryRemove(tickets[i].Id, out _); cancellations[i].Dispose(); }
                finished.TrySetResult(); sendingTasks.TryRemove(batch, out _);
            }
        });
        return new() { TransferId = tickets[0].Id, Transfers = tickets };
    }
    private async Task ShareQueuedSubnetsAsync(TransferTicket ticket, PeerInfo recipient, List<DiscoverySubnet> subnets, CancellationToken ct)
    {
        bool acquired = false;
        try
        {
            await outgoingSlots.WaitAsync(ct); acquired = true;
            await transport.ShareSubnetsAsync(ticket.Id, recipient, subnets, ct);
        }
        catch (Exception ex)
        {
            OnProgress(new() { Id = ticket.Id, Kind = "subnets", Direction = "send", PeerName = recipient.Name, State = ex is OperationCanceledException ? "cancelled" : "failed", Message = ex.Message, CurrentFile = "网段分享" });
        }
        finally { if (acquired) outgoingSlots.Release(); }
    }
}
