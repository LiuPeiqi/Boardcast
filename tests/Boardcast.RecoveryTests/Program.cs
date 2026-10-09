using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Boardcast.Core;
using Boardcast.Daemon;

int checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    checks++;
    Console.WriteLine("PASS " + label);
}
string root = Path.Combine(Path.GetTempPath(), "Boardcast.RecoveryTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string source = Path.Combine(root, "payload.bin");
    byte[] payload = RandomNumberGenerator.GetBytes(2 * 1024 * 1024 + 73);
    await File.WriteAllBytesAsync(source, payload);

    // The sender is exercised through its public IPC handler without starting discovery or the user daemon.
    // The receiver uses a fresh identity and temporary storage; daemon discovery remains unstarted on a separate ephemeral port.
    await using var receiver = new ReceiverFixture(Path.Combine(root, "receiver"));
    receiver.Start();
    string pausedData = Path.Combine(root, "paused-sender");
    string pausedId;
    string validJournal;
    receiver.HoldApprovals = true;
    using (var daemon = NewDaemon(pausedData))
    {
        pausedId = await Send(daemon, receiver.Peer, source, "Keep this summary through restart");
        await receiver.WaitForOffer(pausedId);
        Check(SavedIds(pausedData).SequenceEqual([pausedId]), "An active send is durably recorded before transmission");
        Check((await daemon.HandleAsync(new() { Command = "pause", TransferId = pausedId })).Success, "IPC pause accepts an active send");
        var paused = await WaitState(daemon, pausedId, "paused");
        Check(paused.CanResume, "Paused send exposes the continue action");
        receiver.Release(pausedId, accept: false);
        await receiver.WaitForTerminal(pausedId);
        Check(SavedIds(pausedData).Contains(pausedId), "Pause retains the durable recovery record");
        validJournal = await File.ReadAllTextAsync(Path.Combine(pausedData, "recoverable-sends.json"));
    }
    receiver.HoldApprovals = false;
    using (var restarted = NewDaemon(pausedData))
    {
        var initial = await Snapshot(restarted);
        Check(initial.Transfers.Single().Id == pausedId && initial.Transfers.Single().State == "paused" && initial.Transfers.Single().CanResume,
            "A newly constructed daemon restores the same paused task");
        Check(receiver.OfferCount(pausedId) == 1, "Loading recovery records does not automatically resend");
        var resumed = await Resume(restarted, pausedId);
        Check(resumed.TransferId == pausedId && resumed.Transfers.Single().Id == pausedId, "Continue preserves the original transfer identity");
        var completed = await WaitState(restarted, pausedId, "completed");
        Check(!completed.CanResume && completed.BytesTransferred == payload.Length, "Successful continued transfer reports completed bytes and removes continue");
        var offers = receiver.Offers[pausedId].ToArray();
        Check(offers.Length == 2 && offers[0].BatchId == offers[1].BatchId && offers[1].Summary == "Keep this summary through restart",
            "Restart preserves batch identity and transfer summary");
        Check(Enumerable.SequenceEqual(payload, await receiver.ReadReceived(pausedId, Path.GetFileName(source))), "Continued transfer arrives intact over authenticated transport");
        Check(SavedIds(pausedData).Count == 0, "Successful completion removes the durable recovery record");
    }
    using (var afterSuccess = NewDaemon(pausedData))
        Check((await Snapshot(afterSuccess)).Transfers.Count == 0, "Completed sends do not reappear after another restart");

    string failedData = Path.Combine(root, "failed-sender");
    await using (var delayedReceiver = new ReceiverFixture(Path.Combine(root, "delayed-receiver")))
    using (var daemon = NewDaemon(failedData))
    {
        string id = await Send(daemon, delayedReceiver.Peer, source);
        var failed = await WaitState(daemon, id, "failed");
        Check(failed.CanResume && SavedIds(failedData).Contains(id), "Connection failure remains recoverable and durable");
        delayedReceiver.Start();
        Check((await Resume(daemon, id)).Success, "A failed send can continue when its recipient returns");
        await WaitState(daemon, id, "completed");
        Check(Enumerable.SequenceEqual(payload, await delayedReceiver.ReadReceived(id, Path.GetFileName(source))), "Retry after connection failure delivers the original bytes");
        Check(SavedIds(failedData).Count == 0, "Successful retry clears its failure recovery record");
    }

    string missingData = Path.Combine(root, "missing-source-sender");
    string missingSource = Path.Combine(root, "temporarily-missing.bin");
    await File.WriteAllBytesAsync(missingSource, payload);
    receiver.HoldApprovals = true;
    string missingId;
    using (var daemon = NewDaemon(missingData))
    {
        missingId = await Send(daemon, receiver.Peer, missingSource);
        await receiver.WaitForOffer(missingId);
        Check((await daemon.HandleAsync(new() { Command = "pause", TransferId = missingId })).Success, "Source-loss setup pauses a pending transfer");
        await WaitState(daemon, missingId, "paused");
        receiver.Release(missingId, accept: false);
        await receiver.WaitForTerminal(missingId);
    }
    File.Delete(missingSource);
    receiver.HoldApprovals = false;
    using (var restarted = NewDaemon(missingData))
    {
        await Resume(restarted, missingId);
        var missing = await WaitState(restarted, missingId, "failed");
        Check(missing.CanResume && missing.Message.Length > 0 && SavedIds(missingData).Contains(missingId),
            "A missing source produces a visible failure without silently losing the recovery task");
        Check(receiver.OfferCount(missingId) == 1, "A missing source is rejected before contacting its recipient");
        await File.WriteAllBytesAsync(missingSource, payload);
        await Resume(restarted, missingId);
        await WaitState(restarted, missingId, "completed");
        Check(Enumerable.SequenceEqual(payload, await receiver.ReadReceived(missingId, Path.GetFileName(missingSource))), "Restoring the source allows the same task to finish");
    }

    string cancelledData = Path.Combine(root, "cancelled-sender");
    receiver.HoldApprovals = true;
    string cancelledId;
    using (var daemon = NewDaemon(cancelledData))
    {
        cancelledId = await Send(daemon, receiver.Peer, source);
        await receiver.WaitForOffer(cancelledId);
        Check((await daemon.HandleAsync(new() { Command = "cancel", TransferId = cancelledId })).Success, "IPC cancellation accepts an active transfer");
        receiver.Release(cancelledId, accept: false);
        await receiver.WaitForTerminal(cancelledId);
        var cancelled = await WaitState(daemon, cancelledId, "cancelled");
        Check(!cancelled.CanResume && SavedIds(cancelledData).Count == 0, "Explicit cancellation removes both continuation and persisted recovery");
        Check(!(await daemon.HandleAsync(new() { Command = "resume", TransferId = cancelledId })).Success, "Cancelled tasks cannot be resumed through IPC");
    }
    using (var restarted = NewDaemon(cancelledData))
        Check((await Snapshot(restarted)).Transfers.Count == 0, "Cancelled tasks stay absent after restart");

    string rejectedData = Path.Combine(root, "rejected-sender");
    using (var daemon = NewDaemon(rejectedData))
    {
        string id = await Send(daemon, receiver.Peer, source);
        await receiver.WaitForOffer(id);
        receiver.Release(id, accept: false);
        var rejected = await WaitState(daemon, id, "rejected");
        Check(!rejected.CanResume && SavedIds(rejectedData).Count == 0, "Recipient rejection clears continuation and persistent recovery");
        Check(!(await daemon.HandleAsync(new() { Command = "resume", TransferId = id })).Success, "Rejected transfers require a new send instead of continuation");
    }

    string pausedCancelData = Path.Combine(root, "paused-cancel-sender");
    string pausedCancelId;
    using (var daemon = NewDaemon(pausedCancelData))
    {
        pausedCancelId = await Send(daemon, receiver.Peer, source);
        await receiver.WaitForOffer(pausedCancelId);
        await daemon.HandleAsync(new() { Command = "pause", TransferId = pausedCancelId });
        await WaitState(daemon, pausedCancelId, "paused");
        receiver.Release(pausedCancelId, accept: false);
        await receiver.WaitForTerminal(pausedCancelId);
    }
    using (var restarted = NewDaemon(pausedCancelData))
    {
        Check((await restarted.HandleAsync(new() { Command = "cancel", TransferId = pausedCancelId })).Success, "A restored paused task can be explicitly discarded");
        var discarded = await WaitState(restarted, pausedCancelId, "cancelled");
        Check(!discarded.CanResume && SavedIds(pausedCancelData).Count == 0, "Discarding a restored task persists the removal");
    }

    string settingsData = Path.Combine(root, "legacy-settings-sender");
    var settingsStore = new SettingsStore(settingsData);
    var legacy = new AppSettings { DeviceName = "Legacy device", ReceiveDirectory = Path.Combine(settingsData, "received"), AutoDownloadUpdates = true, LanOnline = false };
    await File.WriteAllTextAsync(settingsStore.SettingsPath, JsonSerializer.Serialize(legacy, BoardcastJson.Options));
    using (var daemon = NewDaemon(settingsData))
    {
        var initial = await Snapshot(daemon);
        Check(!initial.Settings.AutoDownloadUpdates && initial.Settings.DeviceName == legacy.DeviceName,
            "Daemon loading old enabled settings preserves identity settings and disables automatic update downloads");
        var oldClientSettings = initial.Settings with { AutoDownloadUpdates = true, LanOnline = false };
        var applied = await daemon.HandleAsync(new() { Command = "settings", Settings = oldClientSettings });
        Check(applied.Success && !applied.Snapshot!.Settings.AutoDownloadUpdates && !settingsStore.Load().AutoDownloadUpdates,
            "Old IPC clients cannot turn automatic update downloads back on");
        Check((await Snapshot(daemon)).Update.State is not "downloading" and not "restoring", "Reading status and changing settings does not launch an update download");
        Check(!(await daemon.HandleAsync(new() { Command = "resume", TransferId = Guid.NewGuid().ToString("N") })).Success,
            "An unknown recovery identity is rejected without creating a task");
    }
    string historyData = Path.Combine(root, "history-sender");
    string retainedId;
    using (var daemon = NewDaemon(historyData))
    {
        string absentSource = Path.Combine(root, "never-created.bin");
        retainedId = await Send(daemon, receiver.Peer, absentSource);
        await WaitState(daemon, retainedId, "failed");
        for (int index = 0; index < 205; index++)
        {
            string id = await Send(daemon, receiver.Peer, absentSource);
            await WaitState(daemon, id, "failed");
            var cancelled = await daemon.HandleAsync(new() { Command = "cancel", TransferId = id });
            if (!cancelled.Success) throw new Exception("Unable to clear history fixture task: " + cancelled.Error);
        }
        var retained = (await Snapshot(daemon)).Transfers.SingleOrDefault(item => item.Id == retainedId);
        Check(retained is { State: "failed", CanResume: true } && SavedIds(historyData).SequenceEqual([retainedId]),
            "Trimming more than 200 historical tasks preserves an actionable failed transfer");
    }
    using (var daemon = NewDaemon(historyData))
        Check((await Snapshot(daemon)).Transfers.Single() is { State: "paused", CanResume: true } restored && restored.Id == retainedId,
            "A retained failed transfer remains recoverable after history trimming and restart");
    string mixedJournalData = Path.Combine(root, "mixed-journal-sender");
    Directory.CreateDirectory(mixedJournalData);
    var validRecord = JsonNode.Parse(validJournal)!.AsArray().Single()!;
    var invalidAddress = validRecord.DeepClone();
    invalidAddress["id"] = Guid.NewGuid().ToString("N");
    invalidAddress["peer"]!["address"] = "8.8.8.8";
    var staleRecord = validRecord.DeepClone();
    staleRecord["id"] = Guid.NewGuid().ToString("N");
    staleRecord["savedAt"] = DateTimeOffset.UtcNow.AddDays(-8);
    var malformedSource = validRecord.DeepClone();
    malformedSource["id"] = Guid.NewGuid().ToString("N");
    malformedSource["sources"] = new JsonArray((JsonNode?)null);
    var mixedJournal = new JsonArray(null, invalidAddress, staleRecord, malformedSource, validRecord.DeepClone());
    await File.WriteAllTextAsync(Path.Combine(mixedJournalData, "recoverable-sends.json"), mixedJournal.ToJsonString());
    using (var daemon = NewDaemon(mixedJournalData))
    {
        var snapshot = await Snapshot(daemon);
        Check(snapshot.Transfers.Count == 1 && snapshot.Transfers[0].Id == pausedId,
            "Null, public-address, expired and malformed-source records do not hide a following valid task");
    }
    foreach (var entry in new[] { (Name: "truncated", Json: "[{"), (Name: "oversized", Json: new string(' ', 4 * 1024 * 1024 + 1)) })
    {
        string directory = Path.Combine(root, entry.Name + "-journal-sender");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "recoverable-sends.json"), entry.Json);
        using var daemon = NewDaemon(directory);
        var snapshot = await Snapshot(daemon);
        Check(snapshot.Transfers.Count == 0 && snapshot.Diagnostics.Count > 0,
            $"A {entry.Name} recovery journal leaves the daemon usable and reports a diagnostic");
    }
    checks += await SendCancellationRecoveryScenarios.RunAsync(root);
    Console.WriteLine($"All {checks} daemon recovery checks passed.");
}
finally
{
    string normalized = Path.GetFullPath(root);
    string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    // Cleanup is confined to the unique temporary tree created by this test process.
    if (normalized.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(normalized).StartsWith("Boardcast.RecoveryTests-", StringComparison.Ordinal))
        try { Directory.Delete(normalized, recursive: true); } catch (IOException) { }
}

static DaemonHost NewDaemon(string directory) => new(directory, ReceiverFixture.UnusedPort(), noUi: true, discoveryPort: ReceiverFixture.UnusedPort());
static async Task<DaemonSnapshot> Snapshot(DaemonHost daemon) => (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!;
static async Task<string> Send(DaemonHost daemon, PeerInfo peer, string source, string summary = "")
{
    var response = await daemon.HandleAsync(new() { Command = "send", Peer = peer, Paths = [source], Summary = summary });
    if (!response.Success) throw new Exception("Send failed: " + response.Error);
    return response.TransferId;
}
static async Task<IpcResponse> Resume(DaemonHost daemon, string id)
{
    var timer = Stopwatch.StartNew();
    while (true)
    {
        var response = await daemon.HandleAsync(new() { Command = "resume", TransferId = id });
        if (response.Success) return response;
        if (!response.Error.Contains("仍在结束中", StringComparison.Ordinal) || timer.Elapsed > TimeSpan.FromSeconds(5))
            throw new Exception("Resume failed: " + response.Error);
        await Task.Delay(20);
    }
}
static async Task<TransferProgress> WaitState(DaemonHost daemon, string id, string expected)
{
    var timer = Stopwatch.StartNew();
    TransferProgress? latest = null;
    while (timer.Elapsed < TimeSpan.FromSeconds(20))
    {
        latest = (await Snapshot(daemon)).Transfers.FirstOrDefault(p => p.Id == id);
        if (latest?.State == expected) return latest;
        if (latest?.State is "completed" or "rejected" or "failed" or "cancelled" && expected != latest.State)
            throw new Exception($"Expected {expected}, got {latest.State}: {latest.Message}");
        await Task.Delay(20);
    }
    throw new TimeoutException($"Expected {expected} for {id}, last state {latest?.State}: {latest?.Message}");
}
static List<string> SavedIds(string directory)
{
    string path = Path.Combine(directory, "recoverable-sends.json");
    if (!File.Exists(path)) return [];
    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
    return document.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList();
}

sealed class ReceiverFixture : IAsyncDisposable
{
    private readonly IdentityStore identity;
    private readonly CancellationTokenSource stopped = new();
    private readonly TransferService service;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IncomingDecision>> approvals = new();
    private readonly ConcurrentDictionary<string, TransferProgress> progress = new();
    private Task? listening;
    private readonly AppSettings settings;
    public PeerInfo Peer { get; }
    public bool HoldApprovals { get; set; }
    public ConcurrentDictionary<string, ConcurrentQueue<TransferOffer>> Offers { get; } = new();
    public ReceiverFixture(string directory)
    {
        identity = new IdentityStore(Path.Combine(directory, "identity"));
        settings = new() { DeviceName = "Recovery test receiver", Port = UnusedPort(), ReceiveDirectory = Path.Combine(directory, "received") };
        Peer = new() { Fingerprint = identity.Fingerprint, Name = settings.DeviceName, Port = settings.Port, Address = "127.0.0.1", Platform = "windows" };
        service = new(identity, () => settings, () => Peer, async (offer, _, ct) =>
        {
            var approval = new TaskCompletionSource<IncomingDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (HoldApprovals) approvals[offer.Id] = approval;
            Offers.GetOrAdd(offer.Id, _ => new()).Enqueue(offer);
            return HoldApprovals ? await approval.Task.WaitAsync(ct) : new(true, settings.ReceiveDirectory, "Test accepted");
        }, item => progress[item.Id] = item, _ => { });
    }
    public void Start() => listening ??= service.RunAsync(stopped.Token);
    public int OfferCount(string id) => Offers.TryGetValue(id, out var offers) ? offers.Count : 0;
    public void Release(string id, bool accept)
    {
        if (!approvals.TryRemove(id, out var approval)) throw new Exception("No pending approval for " + id);
        approval.TrySetResult(new(accept, settings.ReceiveDirectory, accept ? "Accepted" : "Declined"));
    }
    public Task WaitForOffer(string id) => WaitUntil(() => OfferCount(id) > 0, "receiver offer " + id);
    public async Task WaitForTerminal(string id)
    {
        // Receiver terminal progress is reported after writes close and cancellation cleanup has finished.
        await WaitUntil(() => progress.TryGetValue(id, out var current) && current.State is "completed" or "failed" or "rejected" or "cancelled" or "paused", "receiver cleanup " + id);
    }
    public async Task<byte[]> ReadReceived(string id, string fileName)
    {
        await WaitUntil(() => progress.TryGetValue(id, out var current) && current.State == "completed", "receiver completion " + id);
        return await File.ReadAllBytesAsync(Path.Combine(progress[id].Destination, fileName));
    }
    private static async Task WaitUntil(Func<bool> condition, string label)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(label);
            await Task.Delay(20);
        }
    }
    public static int UnusedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
    public async ValueTask DisposeAsync()
    {
        stopped.Cancel();
        if (listening is not null)
            try { await listening.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        identity.Dispose();
        stopped.Dispose();
    }
}





