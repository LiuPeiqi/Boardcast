using System.Collections.Concurrent;
using System.Text.Json;
using Boardcast.Core;

namespace Boardcast.Daemon;

public sealed partial class DaemonHost
{
    private sealed record RecoverableSend
    {
        public string Id { get; set; } = "";
        public string BatchId { get; set; } = "";
        public PeerInfo Peer { get; set; } = new();
        public List<TransferSource> Sources { get; set; } = [];
        public string Summary { get; set; } = "";
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
    }
    private readonly ConcurrentDictionary<string, RecoverableSend> recoverableSends = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> pausedSends = new(StringComparer.Ordinal);
    private readonly object recoveryGate = new();
    private string RecoveryPath => Path.Combine(store.DataDirectory, "recoverable-sends.json");

    private void LoadRecoverableSends()
    {
        if (!File.Exists(RecoveryPath)) return;
        try
        {
            if (new FileInfo(RecoveryPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复任务文件过大。");
            var saved = JsonSerializer.Deserialize<List<RecoverableSend>>(File.ReadAllBytes(RecoveryPath), BoardcastJson.Options) ?? [];
            foreach (var item in saved.Take(128))
            {
                if (item is null || item.SavedAt < DateTimeOffset.UtcNow.AddDays(-7) || item.SavedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
                    !Guid.TryParseExact(item.Id, "N", out _) || !Guid.TryParseExact(item.BatchId, "N", out _) ||
                    item.Peer is null || item.Peer.Fingerprint is not { Length: 64 } || !item.Peer.Fingerprint.All(Uri.IsHexDigit) ||
                    item.Peer.Port is < 1024 or > 65535 || item.Peer.Name is null || item.Peer.Name.Length > 256 ||
                    item.Sources is null || item.Sources.Count is < 1 or > 50000 || item.Summary is null || item.Summary.Length > 16384 ||
                    item.Sources.Any(s => s is null || string.IsNullOrWhiteSpace(s.Path) || s.ExcludedPaths is null)) continue;
                if (!System.Net.IPAddress.TryParse(item.Peer.Address, out var address) || !LanAddress.IsAllowed(address)) continue;
                recoverableSends[item.Id] = item;
                transfers[item.Id] = new TransferProgress { Id = item.Id, Direction = "send", PeerName = item.Peer.Name,
                    State = "paused", CanResume = true, Message = "上次传输未完成，可继续传输。", UpdatedAt = item.SavedAt };
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
        { ReportDiagnostic("无法读取传输恢复记录：" + ex.Message); }
    }
    private void SaveRecoverableSends()
    {
        lock (recoveryGate)
        {
            string temporary = RecoveryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(recoverableSends.Values.OrderByDescending(s => s.SavedAt).Take(128), BoardcastJson.Options);
                if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("恢复任务记录过大，请减少发送项目。");
                using (var file = PrivateFile.Create(temporary)) { file.Write(bytes); file.Flush(true); }
                File.Move(temporary, RecoveryPath, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private void RememberSends(string batchId, List<TransferTicket> tickets, List<PeerInfo> peers, List<TransferSource> sources, string summary)
    {
        if (recoverableSends.Count + tickets.Count > 128) throw new InvalidDataException("可恢复任务已达上限，请取消不需要的传输记录。");
        for (int i = 0; i < tickets.Count; i++)
            recoverableSends[tickets[i].Id] = new RecoverableSend { Id = tickets[i].Id, BatchId = batchId,
                Peer = Clone(peers[i]), Sources = Clone(sources), Summary = summary };
        try { SaveRecoverableSends(); }
        catch { foreach (var ticket in tickets) recoverableSends.TryRemove(ticket.Id, out _); throw; }
    }
    private void ForgetSend(string id)
    {
        pausedSends.TryRemove(id, out _);
        if (recoverableSends.TryRemove(id, out _))
        {
            try { SaveRecoverableSends(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ReportDiagnostic("无法保存恢复记录：" + ex.Message); }
        }
    }
    private TransferProgress RecoveryProgress(TransferProgress progress)
    {
        if (progress.Direction != "send") return progress with { CanDiscard = progress.CanDiscard || progress.CanResume, CanResume = false };
        if (!recoverableSends.ContainsKey(progress.Id)) return progress with { CanResume = false };
        if (progress.State is "completed" or "rejected") { ForgetSend(progress.Id); return progress with { CanResume = false }; }
        if (progress.State == "cancelled" && (pausedSends.ContainsKey(progress.Id) || stopped.IsCancellationRequested))
            progress = progress with { State = "paused", Message = "已暂停，可继续传输。" };
        if (progress.State is "paused" or "failed")
        {
            if (transfers.TryGetValue(progress.Id, out var previous) && progress.BytesTransferred == 0)
                progress = progress with { BytesTransferred = previous.BytesTransferred, TotalBytes = Math.Max(progress.TotalBytes, previous.TotalBytes),
                    ReusedBytes = previous.ReusedBytes, PeerBytes = previous.PeerBytes };
            return progress with { CanResume = true };
        }
        if (progress.State == "cancelled") ForgetSend(progress.Id);
        return progress with { CanResume = false };
    }
    private IpcResponse PauseSend(string id)
    {
        lock (outgoingGate)
        {
            if (!recoverableSends.ContainsKey(id) || !outgoing.TryGetValue(id, out var cancellation))
                throw new InvalidDataException("该传输当前不能暂停。");
            pausedSends[id] = true;
            cancellation.Cancel();
        }
        return new() { Snapshot = Snapshot() };
    }
    private IpcResponse ResumeSend(string id)
    {
        RecoverableSend saved;
        CancellationTokenSource cancellation;
        lock (outgoingGate)
        {
            if (!Settings().LanOnline || updateInstalling) throw new InvalidDataException("请先连接局域网，再继续传输。");
            if (!recoverableSends.TryGetValue(id, out saved!)) throw new InvalidDataException("没有可恢复的发送任务，请重新选择内容发送。");
            if (outgoing.ContainsKey(id)) throw new InvalidDataException("传输仍在结束中，请稍后继续。");
            if (outgoing.Count >= 128) throw new InvalidDataException("发送队列已满。");
            var currentPeer = discovery.Peers.FirstOrDefault(p => p.Fingerprint.Equals(saved.Peer.Fingerprint, StringComparison.OrdinalIgnoreCase));
            if (currentPeer != null) saved = saved with { Peer = currentPeer };
            saved = saved with { SavedAt = DateTimeOffset.UtcNow };
            recoverableSends[id] = saved;
            SaveRecoverableSends();
            pausedSends.TryRemove(id, out _);
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
            outgoing[id] = cancellation;
            OnProgress(new() { Id = id, Direction = "send", PeerName = saved.Peer.Name, State = "preparing", Message = "正在检查可复用的数据…" });
        }
        var ticket = new TransferTicket { Id = id, PeerFingerprint = saved.Peer.Fingerprint, PeerName = saved.Peer.Name };
        // Register the completion before scheduling work, so shutdown never misses an in-flight resume.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sendingTasks[id] = finished.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                var plan = ManifestBuilder.BuildPlan(saved.Sources, Settings(), cancellation.Token);
                plan.Offer.Summary = saved.Summary; plan.Offer.BatchId = saved.BatchId;
                await SendQueuedAsync(plan, ticket, saved.Peer, cancellation.Token);
            }
            catch (Exception ex)
            {
                OnProgress(new() { Id = id, Direction = "send", PeerName = saved.Peer.Name,
                    State = cancellation.IsCancellationRequested ? "cancelled" : "failed", Message = ex.Message });
            }
            finally
            {
                lock (outgoingGate) { outgoing.TryRemove(id, out _); cancellation.Dispose(); }
                finished.TrySetResult(); sendingTasks.TryRemove(id, out _);
            }
        });
        return new() { TransferId = id, Transfers = [ticket], Snapshot = Snapshot() };
    }
}