using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Boardcast.Core;

/// <summary>Streams bounded scans; forgetting a range or taking LAN offline cancels its in-flight work.</summary>
public sealed class SubnetDiscoveryService : IDisposable
{
    private readonly Func<AppSettings> settings;
    private readonly Func<IPAddress, CancellationToken, Task> probe;
    private readonly Func<string, int, CancellationToken, Task> connect;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly Dictionary<string, (CancellationTokenSource Stop, Task Task)> workers = [];
    private readonly List<(CancellationTokenSource Stop, Task Task)> retired = [];
    private readonly SemaphoreSlim rateGate = new(1);
    private readonly SemaphoreSlim tcpSlots = new(32);
    private long nextDatagram;
    private bool running;
    private readonly Func<IReadOnlyList<PeerInfo>> knownPeers;
    public SubnetDiscoveryService(Func<AppSettings> settings, Func<IPAddress, CancellationToken, Task> probe,
        Func<string, int, CancellationToken, Task> connect, Func<IReadOnlyList<PeerInfo>>? knownPeers = null)
    { this.settings = settings; this.probe = probe; this.connect = connect; this.knownPeers = knownPeers ?? (() => []); }
    public void Refresh()
    {
        lock (gate)
        {
            if (lifetime.IsCancellationRequested || !running) return;
            foreach (var item in workers.Where(pair => pair.Value.Task.IsCompleted).ToList())
            {
                workers.Remove(item.Key); retired.Add(item.Value);
            }
            foreach (var item in retired.Where(item => item.Task.IsCompleted).ToList())
            {
                _ = item.Task.Exception; item.Stop.Dispose(); retired.Remove(item);
            }
            var current = settings();
            var selected = current.LanOnline ? current.DiscoverySubnets.ToDictionary(DiscoverySubnets.Key) : [];
            foreach (var item in workers.ToList())
                if (!selected.ContainsKey(item.Key)) { item.Value.Stop.Cancel(); workers.Remove(item.Key); retired.Add(item.Value); }
            foreach (var item in selected)
            {
                if (workers.ContainsKey(item.Key)) continue;
                var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var task = Task.Run(() => ScanAsync(item.Value, stop.Token));
                workers.Add(item.Key, (stop, task));

            }
        }
    }
    public async Task RunAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() => lifetime.Cancel());
        Task keepalive = Task.CompletedTask;
        try
        {
            lock (gate) running = true;
            Refresh();
            keepalive = KeepaliveLoopAsync(lifetime.Token);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token)) Refresh();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            (CancellationTokenSource Stop, Task Task)[] ending;
            lock (gate)
            {
                foreach (var worker in workers.Values) worker.Stop.Cancel();
                running = false; ending = workers.Values.Concat(retired).ToArray(); workers.Clear(); retired.Clear();
            }
            try { await Task.WhenAll(ending.Select(item => item.Task).Append(keepalive)); }
            finally { foreach (var item in ending) item.Stop.Dispose(); }
        }
    }
    private async Task ScanAsync(DiscoverySubnet subnet, CancellationToken ct)
    {
        var range = DiscoverySubnets.Parse(subnet.Cidr);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // UDP is the fast route. TCP hello also finds old versions which only answer broadcast probes.
                await Task.WhenAll(ProbeRangeAsync(range, ct), TcpRangeAsync(range, subnet.Port, ct));
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RefreshKnownPeersAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task RefreshKnownPeersAsync(CancellationToken ct = default)
    {
                await Parallel.ForEachAsync(knownPeers().Take(512), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (peer, token) =>
                {
                    CancellationTokenSource? scoped = null;
                    lock (gate)
                    {
                        if (!IPAddress.TryParse(peer.Address, out var address)) return;
                        var worker = workers.Values.FirstOrDefault(item => !item.Stop.IsCancellationRequested &&
                            settings().DiscoverySubnets.Any(subnet => subnet.Port == peer.Port && workers.TryGetValue(DiscoverySubnets.Key(subnet), out var owner) && owner.Stop == item.Stop && DiscoverySubnets.Parse(subnet.Cidr).Contains(address)));
                        if (worker.Stop is not null) scoped = CancellationTokenSource.CreateLinkedTokenSource(token, worker.Stop.Token);
                    }
                    if (scoped is null) return;
                    using (scoped)
                    {
                        try { await ProbeOneAsync(IPAddress.Parse(peer.Address), scoped.Token); await ConnectOneAsync(peer.Address, peer.Port, scoped.Token); }
                        catch (OperationCanceledException) when (scoped.IsCancellationRequested) { }
                    }
                });
    }
    private async Task ProbeOneAsync(IPAddress address, CancellationToken ct)
    {
        await rateGate.WaitAsync(ct);
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (nextDatagram > now) await Task.Delay(TimeSpan.FromSeconds((nextDatagram - now) / (double)Stopwatch.Frequency), ct);
            ct.ThrowIfCancellationRequested();
            nextDatagram = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 128;
            try { await probe(address, ct); } catch (SocketException) { }
        }
        finally { rateGate.Release(); }
    }
    private async Task ConnectOneAsync(string address, int port, CancellationToken ct)
    {
        await tcpSlots.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(750));
            try { await connect(address, port, timeout.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or InvalidOperationException or System.Security.Authentication.AuthenticationException) { }
        }
        finally { tcpSlots.Release(); }
    }
    private async Task ProbeRangeAsync(PrivateIpv4Range range, CancellationToken ct)
    {
        foreach (var address in range.Hosts()) await ProbeOneAsync(address, ct);
    }
    private async Task TcpRangeAsync(PrivateIpv4Range range, int port, CancellationToken ct)
    {
        await Parallel.ForEachAsync(range.Hosts(), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (address, token) =>
        {
            await ConnectOneAsync(address.ToString(), port, token);
        });
    }
    public void Dispose() { lifetime.Cancel(); }
}
