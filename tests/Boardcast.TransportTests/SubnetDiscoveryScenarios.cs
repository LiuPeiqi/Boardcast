using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Boardcast.Core;
using Boardcast.Daemon;

internal static class SubnetDiscoveryScenarios
{
    public static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool condition, string description) { if (!condition) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
        var candidate = new DiscoverySubnet { Cidr = "192.168.41.19/23", Port = 45831 };
        var normalized = DiscoverySubnets.Normalize([candidate, candidate]);
        Check(normalized.Count == 1 && normalized[0].Cidr == "192.168.40.0/23", "Subnet canonicalization preserves an actual /23 and deduplicates endpoints");
        var peer = new PeerInfo { Address = "192.168.41.19", Port = 45831, DiscoverySubnets = [new() { Cidr = "192.168.40.0/23" }, new() { Cidr = "192.168.0.0/16" }] };
        var learned = DiscoverySubnets.FromConnectedPeer(peer);
        Check(learned.Single().Cidr == "192.168.40.0/23" && DiscoverySubnets.Parse(learned[0].Cidr).Contains(IPAddress.Parse("192.168.40.20")), "An authenticated peer teaches its most specific real subnet, including the adjacent /24");
        Check(DiscoverySubnets.FromConnectedPeer(peer with { DiscoverySubnets = [] }).Single().Cidr == "192.168.41.0/24", "Old hello without subnet metadata falls back to the connected IP /24");
        Check(DiscoverySubnets.FromConnectedPeer(peer with { Address = "172.16.40.19", DiscoverySubnets = [new() { Cidr = "172.16.0.0/15" }] }).Single().Cidr == "172.16.40.0/24", "An overly broad advertised subnet falls back to the seed /24");
        Check(DiscoverySubnets.FromConnectedPeer(peer with { Address = "::ffff:192.168.41.19", DiscoverySubnets = [] }).Single().Cidr == "192.168.41.0/24", "IPv4-mapped addresses teach the normalized IPv4 subnet");
        foreach (string address in new[] { "8.8.8.8", "127.0.0.1", "169.254.1.2", "fd00::1" })
            Check(DiscoverySubnets.FromConnectedPeer(peer with { Address = address }).Count == 0, "No remembered scan range for " + address);
        foreach (string cidr in new[] { "8.8.8.0/24", "127.0.0.0/24", "169.254.0.0/16", "172.16.0.0/15", "10.0.0.0/8", "192.168.1.0/33", "fd00::/64" })
        {
            bool rejected = false; try { DiscoverySubnets.Parse(cidr); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Scanning rejects unapproved/public/oversized format " + cidr);
        }
        Check(DiscoverySubnets.Parse("10.2.3.4/31").Hosts().Select(ip => ip.ToString()).SequenceEqual(new[] { "10.2.3.4", "10.2.3.5" }), "/31 scans both point-to-point addresses");
        Check(DiscoverySubnets.Parse("10.2.3.4/32").Hosts().Single().ToString() == "10.2.3.4", "/32 scans the exact permitted address");
        Check(DiscoverySubnets.Parse("10.2.0.0/23").Hosts().Any(ip => ip.ToString() == "10.2.0.255") && DiscoverySubnets.Parse("10.2.0.0/23").Hosts().Any(ip => ip.ToString() == "10.2.1.0"), "/23 host enumeration retains valid .255 and .0 hosts inside the subnet");
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}", BoardcastJson.Options)!;
        Check(legacy.DiscoverySubnets.Count == 0, "Legacy settings begin with no remotely scanned ranges");
        var settingsStore = new SettingsStore(Path.Combine(root, "subnet-settings"));
        legacy.ReceiveDirectory = Path.Combine(root, "subnet-downloads"); legacy.DiscoverySubnets = normalized;
        settingsStore.Save(legacy);
        Check(settingsStore.Load().DiscoverySubnets.Single().Cidr == "192.168.40.0/23", "Approved subnet settings survive a restart");

        await ScannerAsync(Check);
        await DaemonApprovalAsync(root, Check);
        await TransportAsync(root, Check);
        await MultiShareAsync(root, Check);
        await UnicastReplyAsync(Check);
        return checks;
    }
    private static async Task ScannerAsync(Action<bool, string> check)
    {
        var current = new AppSettings { DiscoverySubnets = [new() { Cidr = "10.19.0.0/23" }] };
        int udp = 0, tcp = 0, active = 0, maximum = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new ConcurrentBag<CancellationToken>();
        using var stop = new CancellationTokenSource();
        using var scanner = new SubnetDiscoveryService(() => current, (_, token) =>
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref udp); return Task.CompletedTask;
        }, async (_, _, token) =>
        {
            Interlocked.Increment(ref tcp); int count = Interlocked.Increment(ref active); maximum = Math.Max(maximum, count);
            blocked.Add(token); entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } finally { Interlocked.Decrement(ref active); }
        });
        scanner.Refresh(); await Task.Delay(30);
        check(udp == 0 && tcp == 0, "An unstarted scanner never probes merely because settings were changed");
        var running = scanner.RunAsync(stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(80);
        check(maximum <= 8 && udp > 0, "Injected scan streams UDP and bounds concurrent TCP probes");
        current = current with { DiscoverySubnets = [] }; scanner.Refresh();
        await WaitForAsync(() => active == 0);
        int forgottenUdp = udp, forgottenTcp = tcp; await Task.Delay(120);
        check(blocked.All(token => token.IsCancellationRequested) && udp == forgottenUdp && tcp == forgottenTcp, "Forgetting a subnet cancels in-flight TCP and stops further UDP/TCP work");
        current = current with { DiscoverySubnets = [new() { Cidr = "10.19.0.4/32" }] }; scanner.Refresh();
        await WaitForAsync(() => tcp > forgottenTcp);
        current = current with { LanOnline = false }; scanner.Refresh(); await WaitForAsync(() => active == 0);
        int offlineUdp = udp, offlineTcp = tcp; await Task.Delay(100);
        check(udp == offlineUdp && tcp == offlineTcp, "Taking LAN offline cancels the remaining subnet worker");
        stop.Cancel(); await running;

        current = new AppSettings { DiscoverySubnets = [new() { Cidr = "10.19.0.4/32" }] };
        var touched = new ConcurrentBag<string>();
        using var knownStop = new CancellationTokenSource();
        using var knownScanner = new SubnetDiscoveryService(() => current, (ip, _) => { touched.Add("udp:" + ip); return Task.CompletedTask; },
            (ip, _, _) => { touched.Add("tcp:" + ip); return Task.CompletedTask; }, () => [new() { Address = "10.19.0.4", Port = 45831 }, new() { Address = "10.20.0.4", Port = 45831 }]);
        var knownRunning = knownScanner.RunAsync(knownStop.Token); await WaitForAsync(() => touched.Count >= 2);
        touched.Clear(); await knownScanner.RefreshKnownPeersAsync();
        check(touched.Count == 2 && touched.All(value => value.EndsWith("10.19.0.4")), "Large-range keepalive refreshes known peers only inside currently permitted ranges");
        current = current with { DiscoverySubnets = [] }; knownScanner.Refresh(); touched.Clear(); await knownScanner.RefreshKnownPeersAsync();
        check(touched.IsEmpty, "Forgotten ranges are excluded from known-peer keepalive");
        knownStop.Cancel(); await knownRunning;
    }
    private static Task<IncomingDecision> Incoming(DaemonHost host, PendingSubnetOffer offer, CancellationToken ct) =>
        (Task<IncomingDecision>)typeof(DaemonHost).GetMethod("DecideSubnetsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host, [offer, ct])!;
    private static async Task DaemonApprovalAsync(string root, Action<bool, string> check)
    {
        string data = Path.Combine(root, "subnet-daemon");
        using var udp = new UdpClient(0); int discoveryPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port; udp.Dispose();
        using var host = new DaemonHost(data, UnusedPort(), noUi: true, discoveryPort: discoveryPort);
        var configuration = (await host.HandleAsync(new() { Command = "status" })).Snapshot!.Settings;
        configuration.Devices.Add(new() { Fingerprint = new string('a', 64), Name = "Trusted", Trusted = true, AutoAccept = true });
        configuration.Rules.Add(new() { Name = "Auto accept files", AutoAccept = true });
        check((await host.HandleAsync(new() { Command = "settings", Settings = configuration })).Success, "Subnet approval fixture enables file auto-accept");
        var offer = new PendingSubnetOffer { Id = "subnet-receive-" + new string('a', 64) + "-" + Guid.NewGuid().ToString("N"), SenderName = "Trusted", SenderFingerprint = new string('a', 64), Address = "10.50.0.4", Subnets = [new() { Cidr = "10.50.0.0/23" }] };
        using var stop = new CancellationTokenSource();
        var decision = Incoming(host, offer, stop.Token);
        var pending = (await host.HandleAsync(new() { Command = "status" })).Snapshot!;
        check(!decision.IsCompleted && pending.PendingSubnets.Count == 1 && pending.Settings.DiscoverySubnets.Count == 0, "File trust and auto-accept rules cannot silently approve shared subnet data");
        check((await host.HandleAsync(new() { Command = "subnet-decide", TransferId = offer.Id, Accept = true })).Success && (await decision).Accept, "Explicit acceptance commits the pending subnet request");
        check(new SettingsStore(data).Load().DiscoverySubnets.Single().Cidr == "10.50.0.0/23", "Subnet acceptance persists settings before the callback returns success");
        configuration.DiscoverySubnets = [];
        await host.HandleAsync(new() { Command = "settings", Settings = configuration });
        check(new SettingsStore(data).Load().DiscoverySubnets.Single().Cidr == "10.50.0.0/23", "A stale general settings editor cannot clear newly approved subnet permissions");
        check((await host.HandleAsync(new() { Command = "subnet-forget", Subnets = offer.Subnets })).Snapshot!.Settings.DiscoverySubnets.Count == 0 && new SettingsStore(data).Load().DiscoverySubnets.Count == 0, "Forget removes only the saved subnet and persists the removal");
        configuration.DiscoverySubnets = offer.Subnets;
        await host.HandleAsync(new() { Command = "settings", Settings = configuration });
        check(new SettingsStore(data).Load().DiscoverySubnets.Count == 0, "A stale general settings editor cannot restore a forgotten subnet");
        var rejectedOffer = offer with { Id = "reject-" + Guid.NewGuid().ToString("N") };
        var rejection = Incoming(host, rejectedOffer, stop.Token);
        await host.HandleAsync(new() { Command = "subnet-decide", TransferId = rejectedOffer.Id, Accept = false });
        check(!(await rejection).Accept && new SettingsStore(data).Load().DiscoverySubnets.Count == 0, "Declining a share never imports its subnet");
        var blockedOffer = offer with { Id = "storage-" + Guid.NewGuid().ToString("N") };
        var blocked = Incoming(host, blockedOffer, stop.Token);
        string settingsPath = Path.Combine(data, "settings.json");
        File.Delete(settingsPath); Directory.CreateDirectory(settingsPath);
        try
        {
            var failure = await host.HandleAsync(new() { Command = "subnet-decide", TransferId = blockedOffer.Id, Accept = true });
            check(!failure.Success && !blocked.IsCompleted && (await host.HandleAsync(new() { Command = "status" })).Snapshot!.Settings.DiscoverySubnets.Count == 0, "A failed settings write never acknowledges acceptance or mutates active scan ranges");
        }
        finally { Directory.Delete(settingsPath); }
        await host.HandleAsync(new() { Command = "subnet-decide", TransferId = blockedOffer.Id, Accept = false }); await blocked;
        configuration = (await host.HandleAsync(new() { Command = "status" })).Snapshot!.Settings; configuration.ReceivingEnabled = false;
        await host.HandleAsync(new() { Command = "settings", Settings = configuration });
        check(!(await Incoming(host, offer with { Id = "paused" }, stop.Token)).Accept, "Paused receiving rejects a new subnet offer");
        var invalidShare = await host.HandleAsync(new() { Command = "subnet-share", Peers = [new() { Address = "127.0.0.1", Fingerprint = new string('b', 64), Name = "Unchosen" }], Subnets = [new() { Cidr = "10.199.0.0/24" }] });
        check(!invalidShare.Success && invalidShare.Error.Contains("许可"), "IPC cannot share arbitrary ranges outside local interfaces and saved permissions");
    }
    private static async Task TransportAsync(string root, Action<bool, string> check)
    {
        using var stop = new CancellationTokenSource();
        using var senderIdentity = new IdentityStore(Path.Combine(root, "subnet-sender"));
        using var receiverIdentity = new IdentityStore(Path.Combine(root, "subnet-receiver"));
        var sendSettings = new AppSettings { DeviceName = "Subnet sender", ReceiveDirectory = Path.Combine(root, "subnet-sender-downloads") };
        var receiveSettings = new AppSettings { DeviceName = "Subnet receiver", Port = UnusedPort(), ReceiveDirectory = Path.Combine(root, "subnet-receiver-downloads") };
        var senderPeer = new PeerInfo { Fingerprint = senderIdentity.Fingerprint, Name = sendSettings.DeviceName, Address = "127.0.0.1" };
        var receiverPeer = new PeerInfo { Fingerprint = receiverIdentity.Fingerprint, Name = receiveSettings.DeviceName, Address = "127.0.0.1", Port = receiveSettings.Port };
        var sending = new ConcurrentDictionary<string, TransferProgress>();
        var receiving = new ConcurrentDictionary<string, TransferProgress>();
        var sender = new TransferService(senderIdentity, () => sendSettings, () => senderPeer, (_, _, _) => Task.FromResult(new IncomingDecision(false, "", "unused")), p => sending[p.Id] = p, _ => { });
        var receiver = new TransferService(receiverIdentity, () => receiveSettings, () => receiverPeer, (_, _, _) => Task.FromResult(new IncomingDecision(false, "", "unused")), p => receiving[p.Id] = p, _ => { });
        var choice = new TaskCompletionSource<IncomingDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingSubnetOffer? pending = null;
        receiver.DecideSubnets = async (offer, token) => { pending = offer; return await choice.Task.WaitAsync(token); };
        var listening = receiver.RunAsync(stop.Token);
        try
        {
            string id = Guid.NewGuid().ToString("N");
            var send = sender.ShareSubnetsAsync(id, receiverPeer, [new() { Cidr = "10.50.0.0/23" }], stop.Token);
            await WaitForAsync(() => pending is not null && sending.TryGetValue(id, out var progress) && progress.State == "waiting");
            check(pending!.SenderFingerprint == senderIdentity.Fingerprint && pending.Id == "subnet-receive-" + senderIdentity.Fingerprint + "-" + id && !send.IsCompleted, "mTLS subnet offers bind the sender and keep the incoming local ID separate from the wire ID");
            choice.TrySetResult(new(true, "", "Persisted")); await send;
            check(sending[id].State == "completed" && sending[id].Kind == "subnets" && receiving[pending.Id].State == "completed", "Accepted subnet transfer reports independent subnet progress on both devices");
            receiver.DecideSubnets = (_, _) => Task.FromResult(new IncomingDecision(false, "", "Declined"));
            string rejectedId = Guid.NewGuid().ToString("N");
            await sender.ShareSubnetsAsync(rejectedId, receiverPeer, [new() { Cidr = "10.50.0.0/23" }], stop.Token);
            check(sending[rejectedId].State == "rejected", "The subnet sender sees an explicit recipient rejection");
            receiver.DecideSubnets = (_, _) => throw new IOException("Test persist failed");
            string failedId = Guid.NewGuid().ToString("N");
            await sender.ShareSubnetsAsync(failedId, receiverPeer, [new() { Cidr = "10.50.0.0/23" }], stop.Token);
            check(sending[failedId].State == "rejected" && sending[failedId].Message.Contains("persist failed"), "Receiver persistence failure is returned as subnet rejection");
            receiveSettings.ReceivingEnabled = false;
            string pausedId = Guid.NewGuid().ToString("N");
            await sender.ShareSubnetsAsync(pausedId, receiverPeer, [new() { Cidr = "10.50.0.0/23" }], stop.Token);
            check(sending[pausedId].State == "rejected", "A paused receiver rejects subnet data before prompting");
        }
        finally { stop.Cancel(); try { await listening; } catch (OperationCanceledException) { } }
    }
    private static async Task MultiShareAsync(string root, Action<bool, string> check)
    {
        using var stop = new CancellationTokenSource();
        var identities = new List<IdentityStore>(); var listeners = new List<Task>(); var peers = new List<PeerInfo>();
        var receiverServices = new List<TransferService>();
        try
        {
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                var identity = new IdentityStore(Path.Combine(root, "subnet-multi-" + i)); identities.Add(identity);
                var configuration = new AppSettings { Port = UnusedPort(), DeviceName = "Subnet target " + i, ReceiveDirectory = Path.Combine(root, "subnet-multi-download-" + i) };
                var peer = new PeerInfo { Fingerprint = identity.Fingerprint, Name = configuration.DeviceName, Port = configuration.Port, Address = "127.0.0.1" }; peers.Add(peer);
                var receiver = new TransferService(identity, () => configuration, () => peer, (_, _, _) => Task.FromResult(new IncomingDecision(false, "", "unused")), _ => { }, _ => { });
                receiver.DecideSubnets = (_, _) => Task.FromResult(new IncomingDecision(index == 0, "", index == 0 ? "Accepted" : "Declined"));
                receiverServices.Add(receiver); listeners.Add(receiver.RunAsync(stop.Token));
            }
            string data = Path.Combine(root, "subnet-multi-daemon");
            new SettingsStore(data).Save(new AppSettings { ReceiveDirectory = Path.Combine(root, "subnet-multi-daemon-downloads"), DiscoverySubnets = [new() { Cidr = "10.71.0.0/23" }] });
            using var udp = new UdpClient(0); int discoveryPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port; udp.Dispose();
            using var daemon = new DaemonHost(data, UnusedPort(), noUi: true, discoveryPort: discoveryPort);
            var response = await daemon.HandleAsync(new() { Command = "subnet-share", Peers = peers, Subnets = [new() { Cidr = "10.71.0.0/23" }] });
            check(response.Success && response.Transfers.Count == 2 && response.Transfers.Select(ticket => ticket.Id).Distinct().Count() == 2, "Multiple subnet recipients receive independent immediately returned tickets");
            DaemonSnapshot latest = new();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                do { latest = (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!; await Task.Delay(15, timeout.Token); }
                while (response.Transfers.Any(ticket => !latest.Transfers.Any(item => item.Id == ticket.Id && item.State is "completed" or "rejected")));
            check(latest.Transfers.Single(item => item.Id == response.Transfers[0].Id).State == "completed" && latest.Transfers.Single(item => item.Id == response.Transfers[1].Id).State == "rejected", "One subnet recipient can decline while another independently accepts");
            receiverServices[0].DecideSubnets = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new(false, "", "Cancelled"); };
            var cancellation = await daemon.HandleAsync(new() { Command = "subnet-share", Peers = [peers[0]], Subnets = [new() { Cidr = "10.71.0.0/23" }] });
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                do { latest = (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!; await Task.Delay(15, timeout.Token); }
                while (!latest.Transfers.Any(item => item.Id == cancellation.TransferId && item.State == "waiting"));
            await daemon.HandleAsync(new() { Command = "cancel", TransferId = cancellation.TransferId });
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                do { latest = (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!; await Task.Delay(15, timeout.Token); }
                while (!latest.Transfers.Any(item => item.Id == cancellation.TransferId && item.State == "cancelled"));
            check(latest.Transfers.Single(item => item.Id == cancellation.TransferId).Kind == "subnets", "Standard IPC cancel stops a pending outgoing subnet share without file pause/resume");
        }
        finally
        {
            stop.Cancel();
            foreach (var listener in listeners) try { await listener; } catch (OperationCanceledException) { }
            foreach (var identity in identities) identity.Dispose();
        }
    }
    private static async Task UnicastReplyAsync(Action<bool, string> check)
    {
        using var candidate = new UdpClient(0); int port = ((IPEndPoint)candidate.Client.LocalEndPoint!).Port; candidate.Dispose();
        var local = new PeerInfo { Fingerprint = new string('b', 64), Name = "Direct reply", Port = 45831 };
        using var discovery = new DiscoveryService(() => local, discoveryPort: port);
        using var stop = new CancellationTokenSource(); var running = discovery.RunAsync(stop.Token);
        using var sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        byte[] request = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, type = "probe", fingerprint = new string('a', 64), name = "Routed sender", platform = "macos", port = 45831 }, BoardcastJson.Options);
        await sender.SendAsync(request, new IPEndPoint(IPAddress.Loopback, port));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var reply = await sender.ReceiveAsync(timeout.Token);
        using var parsed = JsonDocument.Parse(reply.Buffer);
        check(parsed.RootElement.GetProperty("type").GetString() == "announce" && parsed.RootElement.GetProperty("capabilities").EnumerateArray().Any(value => value.GetString() == TransferService.SubnetSharingCapability), "UDP probe responses go directly to the requesting endpoint and advertise subnet sharing");
        stop.Cancel(); await running;
    }
    private static int UnusedPort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(15, timer.Token);
    }
}
