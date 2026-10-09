using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Boardcast.Core;
using Boardcast.Daemon;

/// <summary>Sender IPC cancellation must explicitly discard remote intermediates; pause keeps recovery.</summary>
public static class SendCancellationRecoveryScenarios
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
        string scenarios = Directory.CreateDirectory(Path.Combine(root, "send-cancellation")).FullName;
        foreach (bool pauseBeforeCancel in new[] { false, true })
        {
            string name = pauseBeforeCancel ? "paused-batch" : "active-batch";
            string scenario = Directory.CreateDirectory(Path.Combine(scenarios, name)).FullName;
            string source = Directory.CreateDirectory(Path.Combine(scenario, name)).FullName;
            byte[] completedBytes = RandomNumberGenerator.GetBytes(256 * 1024 + 17);
            await File.WriteAllBytesAsync(Path.Combine(source, "a-first.bin"), completedBytes);
            await File.WriteAllBytesAsync(Path.Combine(source, "z-partial.bin"), RandomNumberGenerator.GetBytes(32 * MiB + 23));
            await using var receiver = new Receiver(scenario);
            receiver.Start();
            string senderData = Path.Combine(scenario, "sender-data");
            using var daemon = new DaemonHost(senderData, UnusedPort(), noUi: true, discoveryPort: UnusedPort());
            var send = await daemon.HandleAsync(new() { Command = "send", Peer = receiver.Peer, Paths = [source] });
            if (!send.Success) throw new Exception("Cancellation scenario could not start send: " + send.Error);
            string id = send.TransferId;
            var receiving = await receiver.StageReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(receiving.Id == id && receiving.CompletedFiles == 1, $"{name}: cancellation starts with a verified published file and an incomplete sibling");
            Task<IpcResponse>? cancel = null;
            try
            {
                if (pauseBeforeCancel)
                {
                    var pause = await daemon.HandleAsync(new() { Command = "pause", TransferId = id });
                    Check(pause.Success, "IPC pause stops sending while preserving receiver recovery data");
                    await WaitSenderAsync(daemon, id, "paused");
                }
                else
                {
                    cancel = daemon.HandleAsync(new() { Command = "cancel", TransferId = id });
                    // The outgoing token stops before IPC awaits the peer's close-and-cleanup acknowledgement.
                    await WaitSenderAsync(daemon, id, "cancelled");
                }
            }
            finally { receiver.Release(); }
            if (pauseBeforeCancel)
            {
                var pausedReceive = await receiver.TerminalAsync(id, "paused", "failed");
                Check(pausedReceive.CanResume && receiver.HasIncompleteBytes, "IPC pause leaves verified unfinished remote chunks available for continuation");
                cancel = daemon.HandleAsync(new() { Command = "cancel", TransferId = id });
            }
            var cancelled = await cancel!.WaitAsync(TimeSpan.FromSeconds(15));
            Check(cancelled.Success, $"{name}: sender IPC cancellation receives remote cleanup acknowledgement");
            await WaitSenderAsync(daemon, id, "cancelled");
            var cancelledReceive = await receiver.TerminalAsync(id, "cancelled");
            Check(cancelledReceive.CompletedFiles == 1 && !cancelledReceive.CanResume, $"{name}: sender cancellation marks the peer cancelled and preserves one completed file");
            string receivedRoot = Path.Combine(receiver.ReceiveDirectory, name);
            Check(Enumerable.SequenceEqual(completedBytes, await File.ReadAllBytesAsync(Path.Combine(receivedRoot, "a-first.bin"))) &&
                !File.Exists(Path.Combine(receivedRoot, "z-partial.bin")), $"{name}: remote cancellation retains exact completed bytes without publishing its unfinished sibling");
            Check(!receiver.HasIncompleteBytes && !SavedIds(senderData).Contains(id), $"{name}: cancellation removes remote incomplete chunks and local durable continuation");
            Check(!((await StatusAsync(daemon)).Transfers.Single(value => value.Id == id).CanResume), $"{name}: sender cancellation disables the continue action");
        }
        return checks;
    }
    private static async Task<DaemonSnapshot> StatusAsync(DaemonHost daemon) => (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!;
    private static async Task WaitSenderAsync(DaemonHost daemon, string id, string expected)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            var progress = (await StatusAsync(daemon)).Transfers.SingleOrDefault(value => value.Id == id);
            if (progress?.State == expected) return;
            if (progress?.State is "completed" or "failed" or "rejected" && progress.State != expected)
                throw new Exception($"Sender cancellation expected {expected}, got {progress.State}: {progress.Message}");
            await Task.Delay(10);
        }
        throw new TimeoutException("Sender did not reach " + expected);
    }
    private static List<string> SavedIds(string directory)
    {
        string path = Path.Combine(directory, "recoverable-sends.json");
        if (!File.Exists(path)) return [];
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        return json.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToList();
    }
    private static int UnusedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
    private sealed class Receiver : IAsyncDisposable
    {
        private readonly IdentityStore identity;
        private readonly CancellationTokenSource stopped = new();
        private readonly ManualResetEventSlim release = new();
        private readonly TransferService service;
        private readonly ConcurrentDictionary<string, TransferProgress> progress = new();
        private Task listener = Task.CompletedTask;
        private int held;
        public TaskCompletionSource<TransferProgress> StageReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ReceiveDirectory { get; }
        private string DataDirectory { get; }
        public PeerInfo Peer { get; }
        public bool HasIncompleteBytes
        {
            get
            {
                string tasks = Path.Combine(DataDirectory, "receive-tasks");
                return Directory.Exists(tasks) && Directory.EnumerateFiles(tasks, "z-partial.bin", SearchOption.AllDirectories).Any();
            }
        }
        public Receiver(string root)
        {
            DataDirectory = Path.Combine(root, "receiver-data");
            identity = new IdentityStore(DataDirectory);
            ReceiveDirectory = Path.Combine(root, "received");
            var settings = new AppSettings { DeviceName = "Cancellation receiver", Port = UnusedPort(), ReceiveDirectory = ReceiveDirectory };
            Peer = new PeerInfo { Name = settings.DeviceName, Fingerprint = identity.Fingerprint, Port = settings.Port, Address = "127.0.0.1", Platform = "windows" };
            service = new TransferService(identity, () => settings, () => Peer,
                (_, _, _) => Task.FromResult(new IncomingDecision(true, ReceiveDirectory, "Test accepted")), value =>
                {
                    progress[value.Id] = value;
                    if (value.State != "receiving" || value.CompletedFiles != 1 || value.BytesTransferred < MiB || value.BytesTransferred >= value.TotalBytes) return;
                    if (Interlocked.CompareExchange(ref held, 1, 0) == 0) StageReady.TrySetResult(value);
                    if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Sender cancellation receive gate was not released");
                }, _ => { });
        }
        public void Start() => listener = service.RunAsync(stopped.Token);
        public void Release() => release.Set();
        public async Task<TransferProgress> TerminalAsync(string id, params string[] states)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (progress.TryGetValue(id, out var value) && states.Contains(value.State)) return value;
                await Task.Delay(10);
            }
            throw new TimeoutException("Remote receiver did not reach " + string.Join("/", states));
        }
        public async ValueTask DisposeAsync()
        {
            release.Set(); stopped.Cancel();
            try { await listener.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
            finally { identity.Dispose(); stopped.Dispose(); release.Dispose(); }
        }
    }
}