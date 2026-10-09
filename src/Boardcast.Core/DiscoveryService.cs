using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Encodings.Web;
namespace Boardcast.Core;

public sealed class DiscoveryService : IDisposable
{
    public const int DiscoveryPort = 45831;
    private const int MaximumPacketBytes = 2048;
    // Discovery is a UTF-8 datagram, never HTML. Avoid expanding Chinese names into six-byte escapes.
    private static readonly JsonSerializerOptions DiscoveryJson = new(BoardcastJson.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Func<PeerInfo> local;
    private readonly Action<string>? report;
    private readonly ConcurrentDictionary<string, PeerInfo> peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly UdpClient socket;
    private readonly int discoveryPort;
    private readonly CancellationTokenSource lifetime = new();
    private DateTimeOffset lastProbeResponse;
    private long lastNetworkEvent;
    public DiscoveryService(Func<PeerInfo> local, Action<string>? report = null, int discoveryPort = DiscoveryPort)
    {
        if (discoveryPort is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(discoveryPort));
        this.local = local; this.report = report; this.discoveryPort = discoveryPort;
        socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, discoveryPort));
        socket.EnableBroadcast = true; socket.Ttl = 1;
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
    }
    public List<PeerInfo> Peers => !local().LanOnline ? [] : peers.Values.Where(p => DateTimeOffset.UtcNow - p.LastSeen < TimeSpan.FromSeconds(150)).OrderBy(p => p.Name).Select(p => p with { }).ToList();
    public void Remember(PeerInfo peer)
    {
        if (!local().LanOnline || !IPAddress.TryParse(peer.Address, out var address) || !LanAddress.IsAllowed(address)) return;
        if (peer.Fingerprint is not { Length: 64 } || !peer.Fingerprint.All(Uri.IsHexDigit) ||
            peer.Port is < 1024 or > 65535 || peer.Name is null || peer.Name.Length > 256 ||
            !DeviceGroups.IsValidGroupName(peer.GroupName) ||
            peer.Platform is null || peer.Platform.Length > 128 || peer.AppVersion is null || peer.AppVersion.Length > 64 ||
            peer.Runtime is null || peer.Runtime.Length > 64 || peer.UpdateVersion is null || peer.UpdateVersion.Length > 64 ||
            peer.UpdatePort != 0 && peer.UpdatePort is < 1024 or > 65535) return;
        if (peer.Fingerprint.Equals(local().Fingerprint, StringComparison.OrdinalIgnoreCase)) return;
        if (peers.Count >= 512 && !peers.ContainsKey(peer.Fingerprint)) return;
        peers[peer.Fingerprint.ToLowerInvariant()] = peer with { Fingerprint = peer.Fingerprint.ToLowerInvariant(), GroupName = peer.GroupName.Trim(), DepartureHint = false, LastSeen = DateTimeOffset.UtcNow };
    }
    public Task ProbeAsync(CancellationToken ct = default)
    {
        if (!local().LanOnline) throw new InvalidOperationException("请先连接局域网，再刷新附近设备。");
        return BroadcastAsync("probe", ct);
    }
    public async Task StateChangedAsync(bool wasOnline, CancellationToken ct = default)
    {
        if (!local().LanOnline)
        {
            if (wasOnline) await BroadcastAsync("goodbye", ct, allowOffline: true).ConfigureAwait(false);
            peers.Clear();
            return;
        }
        // Publish changed receiving/name/group/version metadata immediately, then solicit peers on reconnect.
        await BroadcastAsync("announce", ct).ConfigureAwait(false);
        if (!wasOnline) await BroadcastAsync("probe", ct).ConfigureAwait(false);
    }
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        try
        {
            if (local().LanOnline)
            {
                await BroadcastAsync("announce", linked.Token).ConfigureAwait(false);
                await BroadcastAsync("probe", linked.Token).ConfigureAwait(false);
            }
            await Task.WhenAll(ReadLoop(linked.Token), AnnounceLoop(linked.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            if (!lifetime.IsCancellationRequested && local().LanOnline)
            {
                using var departure = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await BroadcastAsync("goodbye", departure.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }
    }
    private async Task AnnounceLoop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            foreach (var entry in peers)
                if (DateTimeOffset.UtcNow - entry.Value.LastSeen > TimeSpan.FromSeconds(150)) peers.TryRemove(entry.Key, out _);
            await BroadcastAsync("announce", ct).ConfigureAwait(false);
        }
    }
    private async Task ReadLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await socket.ReceiveAsync(ct).ConfigureAwait(false); }
            catch (SocketException ex)
            {
                report?.Invoke("设备发现暂不可用：" + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }
            if (!local().LanOnline || received.Buffer.Length > MaximumPacketBytes || !LanAddress.IsAllowed(received.RemoteEndPoint.Address)) continue;
            try
            {
                var message = JsonSerializer.Deserialize<DiscoveryMessage>(received.Buffer, BoardcastJson.Options);
                if (message is null || message.ProtocolVersion != 1 || message.Type is not ("probe" or "announce" or "goodbye") ||
                    message.Fingerprint is not { Length: 64 } || !message.Fingerprint.All(Uri.IsHexDigit) ||
                    message.Name is null || message.Platform is null || !DeviceGroups.IsValidGroupName(message.GroupName)) continue;
                if (message.Fingerprint.Equals(local().Fingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                if (message.Type == "goodbye")
                {
                    // UDP is not authenticated: never delete a certificate identity on a departure claim.
                    // Only mark an existing matching endpoint as a hint, without extending its lifetime.
                    if (received.RemoteEndPoint.Port == discoveryPort && peers.TryGetValue(message.Fingerprint, out var current) &&
                        current.Address == received.RemoteEndPoint.Address.ToString() && current.Port == message.Port)
                        peers.TryUpdate(message.Fingerprint, current with { DepartureHint = true }, current);
                    continue;
                }
                Remember(new PeerInfo { Fingerprint = message.Fingerprint, Name = message.Name, GroupName = message.GroupName, Platform = message.Platform, Port = message.Port,
                    ReceivingEnabled = message.ReceivingEnabled, LanOnline = message.LanOnline, AppVersion = message.AppVersion, Runtime = message.Runtime,
                    UpdatePort = message.UpdatePort, UpdateVersion = message.UpdateVersion, Address = received.RemoteEndPoint.Address.ToString() });
                if (message.Type == "probe" && DateTimeOffset.UtcNow - lastProbeResponse > TimeSpan.FromSeconds(1))
                {
                    lastProbeResponse = DateTimeOffset.UtcNow;
                    await BroadcastAsync("announce", ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { }
        }
    }
    private async Task BroadcastAsync(string type, CancellationToken ct, bool allowOffline = false)
    {
        var peer = local();
        if (!peer.LanOnline && !allowOffline) return;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryMessage { Type = type, Fingerprint = peer.Fingerprint, Name = peer.Name, GroupName = peer.GroupName,
            Platform = peer.Platform, Port = peer.Port, ReceivingEnabled = peer.ReceivingEnabled, LanOnline = type != "goodbye" && peer.LanOnline,
            AppVersion = peer.AppVersion, Runtime = peer.Runtime, UpdatePort = peer.UpdatePort, UpdateVersion = peer.UpdateVersion }, DiscoveryJson);
        if (bytes.Length > MaximumPacketBytes)
        {
            report?.Invoke("广播未发出：设备发现信息超过 2048 字节，请缩短设备名或分组名。可使用 IP 连接。");
            return;
        }
        var endpoints = new HashSet<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || !LanAddress.IsAllowed(address.Address)) continue;
                    var ip = address.Address.GetAddressBytes(); var mask = address.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4]; for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    endpoints.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException ex) { report?.Invoke("网络接口信息暂不可用：" + ex.Message); }
        if (endpoints.Count == 0) endpoints.Add(IPAddress.Broadcast);
        foreach (var endpoint in endpoints)
        {
            try { await socket.SendAsync(bytes, new IPEndPoint(endpoint, discoveryPort), ct).ConfigureAwait(false); }
            catch (SocketException ex) { report?.Invoke("广播未发出，可使用 IP 连接：" + ex.Message); }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
        }
    }
    private void NetworkChanged(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64;
        if (!local().LanOnline || now - Interlocked.Exchange(ref lastNetworkEvent, now) < 2000 || lifetime.IsCancellationRequested) return;
        _ = RefreshNetworkAsync();
    }
    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) { if (e.IsAvailable) NetworkChanged(sender, EventArgs.Empty); }
    private async Task RefreshNetworkAsync()
    {
        try
        {
            await BroadcastAsync("announce", lifetime.Token).ConfigureAwait(false);
            await BroadcastAsync("probe", lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        lifetime.Cancel(); socket.Dispose();
    }
    private sealed record DiscoveryMessage
    {
        public int ProtocolVersion { get; set; } = 1;
        public string Type { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string Name { get; set; } = "";
        public string GroupName { get; set; } = "";
        public string Platform { get; set; } = "";
        public int Port { get; set; }
        public bool ReceivingEnabled { get; set; } = true;
        public bool LanOnline { get; set; } = true;
        public string AppVersion { get; set; } = "";
        public string Runtime { get; set; } = "";
        public int UpdatePort { get; set; }
        public string UpdateVersion { get; set; } = "";
    }
}
