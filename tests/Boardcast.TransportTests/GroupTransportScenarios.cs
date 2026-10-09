using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Boardcast.Core;

internal static class GroupTransportScenarios
{
    public static async Task<int> RunAsync(string root)
    {
        int checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new Exception(label);
            checks++; Console.WriteLine("PASS " + label);
        }
        await TlsAsync(root, Check);
        await DiscoveryAsync(Check);
        return checks;
    }

    private static async Task TlsAsync(string root, Action<bool, string> check)
    {
        using var receiverIdentity = new IdentityStore(Path.Combine(root, "group-receiver"));
        using var senderIdentity = new IdentityStore(Path.Combine(root, "group-sender"));
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var receiverSettings = new AppSettings { DeviceName = "Ops-Receiver", GroupName = "接收工作组", Port = port, ReceiveDirectory = Path.Combine(root, "group-received") };
        var senderSettings = new AppSettings { DeviceName = "Ops-Sender", GroupName = "发送工作组", ReceiveDirectory = Path.Combine(root, "group-unused") };
        var receiverPeer = new PeerInfo { Name = receiverSettings.DeviceName, GroupName = receiverSettings.GroupName, Fingerprint = receiverIdentity.Fingerprint, Port = port };
        var senderPeer = new PeerInfo { Name = senderSettings.DeviceName, GroupName = senderSettings.GroupName, Fingerprint = senderIdentity.Fingerprint };
        var remembered = new ConcurrentQueue<PeerInfo>();
        var receiver = new TransferService(receiverIdentity, () => receiverSettings, () => receiverPeer,
            (_, _, _) => Task.FromResult(new IncomingDecision(false, "", "")), _ => { }, peer => remembered.Enqueue(peer));
        var sender = new TransferService(senderIdentity, () => senderSettings, () => senderPeer,
            (_, _, _) => Task.FromResult(new IncomingDecision(false, "", "")), _ => { }, _ => { });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = receiver.RunAsync(stop.Token);
        try
        {
            var observed = await sender.ConnectAsync("127.0.0.1", port, receiverIdentity.Fingerprint, stop.Token);
            check(observed.GroupName == receiverSettings.GroupName, "TLS hello carries the recipient's explicit Unicode group");
            check(remembered.TryDequeue(out var remote) && remote.GroupName == senderSettings.GroupName, "TLS hello carries the sender's explicit Unicode group");

            async Task<WireMessage> RawHello(JsonObject peer)
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, stop.Token);
                await using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null && IdentityStore.GetFingerprint(cert) == receiverIdentity.Fingerprint);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "Boardcast", ClientCertificates = new X509CertificateCollection { senderIdentity.Certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, stop.Token);
                await WireProtocol.WriteAsync(tls, new JsonObject { ["type"] = "hello", ["version"] = 1, ["peer"] = peer }, stop.Token);
                var response = await WireProtocol.ReadAsync<WireMessage>(tls, stop.Token);
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "bye" }, stop.Token);
                return response;
            }
            var legacy = JsonSerializer.SerializeToNode(senderPeer, BoardcastJson.Options)!.AsObject();
            legacy.Remove("groupName");
            var legacyReply = await RawHello(legacy);
            check(legacyReply.Type == "hello" && remembered.TryDequeue(out var legacyPeer) && legacyPeer.GroupName == "" &&
                DeviceGroups.GetPeerGroup(senderSettings, legacyPeer) == "Ops", "TLS accepts legacy hello without groupName and infers its name prefix");

            foreach (var invalid in new[] { null, "bad\nname", new string('x', 81) })
            {
                var malformed = JsonSerializer.SerializeToNode(senderPeer, BoardcastJson.Options)!.AsObject();
                malformed["groupName"] = invalid;
                bool rejected = false;
                try { await RawHello(malformed); }
                catch (Exception ex) when (ex is IOException or AuthenticationException) { rejected = true; }
                check(rejected && remembered.IsEmpty, "TLS rejects invalid group metadata before remembering a peer");
            }
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    private static async Task DiscoveryAsync(Action<bool, string> check)
    {
        var local = new PeerInfo { Fingerprint = new string('a', 64), Name = "Local-PC" };
        using var probe = new UdpClient(0);
        int port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port; probe.Dispose();
        using var discovery = new DiscoveryService(() => local, discoveryPort: port);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var running = discovery.RunAsync(stop.Token);
        using var publisher = new UdpClient(AddressFamily.InterNetwork);
        publisher.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var peerId = new string('b', 64);
        JsonObject Packet(string id, string name) => new()
        {
            ["protocolVersion"] = 1, ["type"] = "announce", ["fingerprint"] = id,
            ["name"] = name, ["platform"] = "macos", ["port"] = 45831
        };
        async Task Publish(JsonObject packet) => await publisher.SendAsync(JsonSerializer.SerializeToUtf8Bytes(packet, BoardcastJson.Options), new IPEndPoint(IPAddress.Loopback, port), stop.Token);
        PeerInfo? Observed() => discovery.Peers.FirstOrDefault(peer => peer.Fingerprint == peerId);
        async Task Wait(Func<bool> predicate)
        {
            while (!predicate()) await Task.Delay(20, stop.Token);
        }
        try
        {
            await Publish(Packet(peerId, "Ops-Legacy"));
            await Wait(() => Observed()?.Name == "Ops-Legacy");
            check(Observed()!.GroupName == "" && DeviceGroups.GetPeerGroup(new(), Observed()!) == "Ops", "UDP accepts old announcements without groupName and infers their prefix");

            var modern = Packet(peerId, "Ops-Modern"); modern["groupName"] = " 设计工作组 ";
            await Publish(modern);
            await Wait(() => Observed()?.Name == "Ops-Modern");
            check(Observed()!.GroupName == "设计工作组", "UDP discovery receives and normalizes an explicit Unicode group");

            int marker = 0;
            foreach (var invalid in new[] { null, "bad\nname", new string('x', 81) })
            {
                var malformed = Packet(peerId, "Invalid-ShouldNotReplace"); malformed["groupName"] = invalid;
                await Publish(malformed);
                var markerName = "Marker-" + ++marker;
                await Publish(Packet(new string('c', 64), markerName));
                await Wait(() => discovery.Peers.Any(peer => peer.Name == markerName));
                check(Observed()!.Name == "Ops-Modern" && Observed()!.GroupName == "设计工作组", "Malformed UDP group cannot replace an existing peer observation");
            }
            var changed = Packet(peerId, "Ops-Changed"); changed["groupName"] = "支持组";
            await Publish(changed);
            await Wait(() => Observed()?.Name == "Ops-Changed");
            check(Observed()!.GroupName == "支持组", "Fresh announcements replace a peer's changed group");
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
    }
}
