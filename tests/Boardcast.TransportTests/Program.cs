using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Boardcast.Core;
using Boardcast.Daemon;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
string root = Path.Combine(Path.GetTempPath(), "Boardcast.TransportTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var serverStop = new CancellationTokenSource();
try
{
    if (args.Contains("--subnets-only"))
    {
        checks += await SubnetDiscoveryScenarios.RunAsync(root);
        Console.WriteLine($"All {checks} subnet discovery checks passed.");
        return;
    }
    if (args.Contains("--receive-storage-only"))
    {
        checks += await ReceiveStorageScenarios.RunAsync(root);
        Console.WriteLine($"All {checks} receive storage checks passed.");
        return;
    }
    if (args.Contains("--chunks-only"))
    {
        checks += await ChunkTransportScenarios.RunAsync(root);
        Console.WriteLine($"All {checks} chunk transport checks passed.");
        return;
    }
    foreach (var address in new[] { "127.0.0.1", "10.5.4.3", "172.16.0.1", "172.31.255.254", "192.168.1.2", "169.254.1.2", "::1", "fd00::1", "fe80::1", "::ffff:192.168.1.2" })
        Check(LanAddress.IsAllowed(IPAddress.Parse(address)), "LAN allow " + address);
    foreach (var address in new[] { "8.8.8.8", "1.1.1.1", "172.32.0.1", "172.15.1.1", "192.0.2.1", "100.64.0.1", "0.0.0.0", "224.0.0.1", "2606:4700:4700::1111" })
        Check(!LanAddress.IsAllowed(IPAddress.Parse(address)), "LAN deny " + address);
    try { LanAddress.Parse("example.com"); throw new Exception("Host accepted"); } catch (InvalidDataException) { Check(true, "No external DNS resolution"); }

    using var senderIdentity = new IdentityStore(Path.Combine(root, "a"));
    using var receiverIdentity = new IdentityStore(Path.Combine(root, "b"));
    using (var loaded = new IdentityStore(Path.Combine(root, "a")))
        Check(loaded.Fingerprint == senderIdentity.Fingerprint, "Device identity persists");
    Check(senderIdentity.Fingerprint.Length == 64 && senderIdentity.Fingerprint == senderIdentity.Fingerprint.ToLowerInvariant(), "Fingerprint is lowercase SHA256 DER");
    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start(); int port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
    var receiverSettings = new AppSettings { DeviceName = "Receiver", Port = port, ReceiveDirectory = Path.Combine(root, "received") };
    var senderSettings = new AppSettings { DeviceName = "Sender", ReceiveDirectory = Path.Combine(root, "unused") };
    var receiverPeer = new PeerInfo { Fingerprint = receiverIdentity.Fingerprint, Name = "Receiver", Address = "127.0.0.1", Port = port, Platform = "windows" };
    var senderPeer = new PeerInfo { Fingerprint = senderIdentity.Fingerprint, Name = "Sender", Address = "127.0.0.1", Port = 45831, Platform = "windows" };
    var receiveProgress = new ConcurrentDictionary<string, TransferProgress>();
    var sendProgress = new ConcurrentDictionary<string, TransferProgress>();
    int decisions = 0;
    var receiver = new TransferService(receiverIdentity, () => receiverSettings, () => receiverPeer,
        (_, _, _) => { Interlocked.Increment(ref decisions); return Task.FromResult(new IncomingDecision(true, receiverSettings.ReceiveDirectory, "Test accepted")); },
        p => receiveProgress[p.Id] = p, _ => { });
    var sender = new TransferService(senderIdentity, () => senderSettings, () => senderPeer,
        (_, _, _) => Task.FromResult(new IncomingDecision(false, "", "Not receiving")), p => sendProgress[p.Id] = p, _ => { });
    receiver.Diagnostic += message => Console.WriteLine("Server diagnostic: " + message);
    Task listener = receiver.RunAsync(serverStop.Token);
    await Task.Delay(100);
    var peer = await sender.ConnectAsync("127.0.0.1", port, receiverIdentity.Fingerprint, CancellationToken.None);
    Check(peer.Fingerprint == receiverIdentity.Fingerprint, "Mutual TLS hello and pinned identity");
    string source = Path.Combine(root, "source");
    Directory.CreateDirectory(Path.Combine(source, "empty"));
    Directory.CreateDirectory(Path.Combine(source, "中文"));
    byte[] payload = RandomNumberGenerator.GetBytes(16 * 1024 * 1024 + 19);
    await File.WriteAllBytesAsync(Path.Combine(source, "中文", "data.bin"), payload);
    await File.WriteAllTextAsync(Path.Combine(source, "note.txt"), "Boardcast TLS test");
    await File.WriteAllBytesAsync(Path.Combine(source, "zero.dat"), []);
    var plan = ManifestBuilder.BuildPlan([source], senderSettings);
    var timer = Stopwatch.StartNew();
    await sender.SendAsync(plan, receiverPeer, CancellationToken.None);
    timer.Stop();
    Check(sendProgress[plan.Offer.Id].State == "completed", "Streaming folder transfer completed");
    Check(decisions == 1, "Receiver approval requested exactly once");
    var received = receiveProgress[plan.Offer.Id];
    Check(received.State == "completed", "Receiver confirmed committed transfer");
    byte[] actual = await File.ReadAllBytesAsync(Path.Combine(received.Destination, "source", "中文", "data.bin"));
    Check(payload.SequenceEqual(actual), "16 MiB binary content exactly preserved");
    Check(Directory.Exists(Path.Combine(received.Destination, "source", "empty")), "Empty directories preserved");
    Check(new FileInfo(Path.Combine(received.Destination, "source", "zero.dat")).Length == 0, "Empty files preserved");
    Check(received.Destination == Path.GetFullPath(receiverSettings.ReceiveDirectory), "Confirmed receive directory is the final destination");
    Check(!Directory.EnumerateFileSystemEntries(receiverSettings.ReceiveDirectory, "Boardcast_*").Any(), "Completed transfer has no extra task directory");
    Check(!Directory.EnumerateFiles(receiverSettings.ReceiveDirectory, ".boardcast-*.json", SearchOption.AllDirectories).Any(), "Completed transfer leaves no checksum sidecars in the receive directory");
    Check(!Directory.EnumerateDirectories(receiverSettings.ReceiveDirectory, ".boardcast-*").Any(), "No staging directories after commit");
    Console.WriteLine($"Streaming loopback: {payload.Length / 1048576d / timer.Elapsed.TotalSeconds:F1} MiB/s, {timer.Elapsed.TotalSeconds:F3}s (includes TLS and filesystem).");

    var selective = Directory.CreateDirectory(Path.Combine(root, "selective")).FullName;
    Directory.CreateDirectory(Path.Combine(selective, ".git", "objects"));
    Directory.CreateDirectory(Path.Combine(selective, "docs"));
    await File.WriteAllTextAsync(Path.Combine(selective, ".git", "objects", "secret"), "excluded repository data");
    await File.WriteAllTextAsync(Path.Combine(selective, "private.txt"), "excluded root file");
    await File.WriteAllTextAsync(Path.Combine(selective, "docs", "private.txt"), "excluded nested file");
    await File.WriteAllTextAsync(Path.Combine(selective, "docs", "public.txt"), "included document");
    var selectivePlan = ManifestBuilder.BuildPlan([new TransferSource { Path = selective, ExcludedPaths = [".git", "private.txt", @"docs\private.txt"] }], senderSettings);
    await sender.SendAsync(selectivePlan, receiverPeer, CancellationToken.None);
    var selectiveDestination = Path.Combine(receiveProgress[selectivePlan.Offer.Id].Destination, "selective");
    Check(sendProgress[selectivePlan.Offer.Id].State == "completed" && receiveProgress[selectivePlan.Offer.Id].State == "completed", "Selective folder transfer completed");
    Check(await File.ReadAllTextAsync(Path.Combine(selectiveDestination, "docs", "public.txt")) == "included document", "Included descendant is received unchanged");
    Check(!Directory.Exists(Path.Combine(selectiveDestination, ".git")) && !File.Exists(Path.Combine(selectiveDestination, "private.txt")) && !File.Exists(Path.Combine(selectiveDestination, "docs", "private.txt")), "Excluded files and directory subtree never reach the recipient");
    Check(selectivePlan.LocalFiles.Count == 1 && selectivePlan.Offer.Entries.Count == 3 && selectivePlan.Offer.TotalBytes == new FileInfo(Path.Combine(selectiveDestination, "docs", "public.txt")).Length, "Offer and byte totals describe only included items");

    var wrongPin = receiverPeer with { Fingerprint = new string('a', 64) };
    var pinPlan = ManifestBuilder.BuildPlan([Path.Combine(source, "note.txt")], senderSettings);
    await sender.SendAsync(pinPlan, wrongPin, CancellationToken.None);
    Check(sendProgress[pinPlan.Offer.Id].State == "failed", "Wrong recipient certificate pin rejected");

    var approvalsBeforePause = decisions;
    receiverSettings.ReceivingEnabled = false;
    var paused = ManifestBuilder.BuildPlan([Path.Combine(source, "note.txt")], senderSettings);
    await sender.SendAsync(paused, receiverPeer, CancellationToken.None);
    Check(sendProgress[paused.Offer.Id].State == "rejected", "Paused receiver rejects before approval or writes");
    Check(decisions == approvalsBeforePause, "Paused receiver creates no pending approval");
    receiverSettings.ReceivingEnabled = true;

    receiverSettings.LanOnline = false;
    var disconnectedReceiver = ManifestBuilder.BuildPlan([Path.Combine(source, "note.txt")], senderSettings);
    await sender.SendAsync(disconnectedReceiver, receiverPeer, CancellationToken.None);
    Check(sendProgress[disconnectedReceiver.Offer.Id].State == "failed" && decisions == approvalsBeforePause, "Offline receiver cannot be bypassed by direct IP sending");
    receiverSettings.LanOnline = true;
    senderSettings.LanOnline = false;
    var disconnectedSender = ManifestBuilder.BuildPlan([Path.Combine(source, "note.txt")], senderSettings);
    await sender.SendAsync(disconnectedSender, receiverPeer, CancellationToken.None);
    Check(sendProgress[disconnectedSender.Offer.Id].State == "failed", "Offline sender cannot open a new transfer");
    bool offlineConnectRejected = false;
    try { await sender.ConnectAsync("127.0.0.1", port, receiverIdentity.Fingerprint, CancellationToken.None); }
    catch (InvalidOperationException) { offlineConnectRejected = true; }
    Check(offlineConnectRejected, "Offline direct-IP connection is refused before TLS");
    senderSettings.LanOnline = true;
    Check((await sender.ConnectAsync("127.0.0.1", port, receiverIdentity.Fingerprint, CancellationToken.None)).Fingerprint == receiverIdentity.Fingerprint, "Reconnect restores direct peer connection");
    var legacySettings = JsonSerializer.Deserialize<AppSettings>("{\"receivingEnabled\":false}", BoardcastJson.Options)!;
    Check(legacySettings.LanOnline && !legacySettings.ReceivingEnabled, "Existing settings default LAN online independently of receiving pause");

    var changed = ManifestBuilder.BuildPlan([Path.Combine(source, "note.txt")], senderSettings);
    await File.AppendAllTextAsync(Path.Combine(source, "note.txt"), "changed");
    await sender.SendAsync(changed, receiverPeer, CancellationToken.None);
    Check(sendProgress[changed.Offer.Id].State == "failed", "Changed source size fails safely");

    async Task<WireMessage> RawOffer(TransferOffer offer, bool badHash)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null && IdentityStore.GetFingerprint(cert) == receiverIdentity.Fingerprint);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Boardcast", ClientCertificates = new X509CertificateCollection { senderIdentity.Certificate }, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
        await WireProtocol.WriteAsync(tls, new WireMessage { Type = "hello", Peer = senderPeer });
        await WireProtocol.ReadAsync<WireMessage>(tls);
        await WireProtocol.WriteAsync(tls, new WireMessage { Type = "offer", Offer = offer });
        WireMessage result;
        do { result = await WireProtocol.ReadAsync<WireMessage>(tls); } while (result.Type == "pending");
        if (badHash && result.Type == "accept")
        {
            await tls.WriteAsync(new byte[] { 42 });
            await tls.WriteAsync(new byte[32]);
            result = await WireProtocol.ReadAsync<WireMessage>(tls);
        }
        return result;
    }
    var malicious = new TransferOffer { SenderName = "Sender", SenderFingerprint = senderIdentity.Fingerprint, Entries = [new() { RelativePath = "../escape.txt", Size = 0 }] };
    Check((await RawOffer(malicious, false)).Type == "reject", "Authenticated traversal manifest rejected");
    var spoof = new TransferOffer { SenderName = "Sender", SenderFingerprint = new string('b', 64), Entries = [new() { RelativePath = "safe.txt", Size = 0 }] };
    Check((await RawOffer(spoof, false)).Type == "reject", "Offer identity must match client TLS certificate");
    var tamper = new TransferOffer { SenderName = "Sender", SenderFingerprint = senderIdentity.Fingerprint, Entries = [new() { RelativePath = "tamper.bin", Size = 1 }] };
    Check((await RawOffer(tamper, true)).Type == "reject", "Wrong streamed SHA256 rejected");
    await Task.Delay(100);
    Check(receiveProgress[tamper.Id].State == "failed", "Tampered file is never committed");
    Check(!Directory.EnumerateDirectories(receiverSettings.ReceiveDirectory, ".boardcast-*").Any(), "Failed transfer staging cleaned");

    bool noCertificateRejected = false;
    try
    {
        using var noCertificateTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var noCertificateClient = new TcpClient();
        await noCertificateClient.ConnectAsync(IPAddress.Loopback, port, noCertificateTimeout.Token);
        using var noCertificateTls = new SslStream(noCertificateClient.GetStream(), false, (_, _, _, _) => true);
        await noCertificateTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Boardcast", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 }, noCertificateTimeout.Token);
        await WireProtocol.WriteAsync(noCertificateTls, new WireMessage { Type = "hello", Peer = senderPeer }, noCertificateTimeout.Token);
        await WireProtocol.ReadAsync<WireMessage>(noCertificateTls, noCertificateTimeout.Token);
    }
    catch (Exception ex) when (ex is IOException or AuthenticationException) { noCertificateRejected = true; }
    Check(noCertificateRejected, "Client certificate is mandatory");

    if (Socket.OSSupportsIPv6)
    {
        var ipv6Peer = await sender.ConnectAsync("::1", port, receiverIdentity.Fingerprint, CancellationToken.None);
        Check(ipv6Peer.Address == "::1", "IPv6 loopback retains correct authenticated address");
    }

    bool publicTargetRejected = false;
    try { await sender.ConnectAsync("8.8.8.8", port, receiverIdentity.Fingerprint, CancellationToken.None); }
    catch (InvalidDataException) { publicTargetRejected = true; }
    Check(publicTargetRejected, "Public target rejected before socket connection");

    await using (var invalidFrame = new MemoryStream(new byte[] { 0x7f, 0xff, 0xff, 0xff }))
    {
        bool rejected = false;
        try { await WireProtocol.ReadAsync<WireMessage>(invalidFrame); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Oversized frame refused before payload allocation");
    }

    var pendingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelProbe = new TcpListener(IPAddress.Loopback, 0);
    cancelProbe.Start(); int cancelPort = ((IPEndPoint)cancelProbe.LocalEndpoint).Port; cancelProbe.Stop();
    var cancelSettings = receiverSettings with { Port = cancelPort };
    var cancelPeer = receiverPeer with { Port = cancelPort };
    using var cancelServerStop = new CancellationTokenSource();
    var cancelReceiver = new TransferService(receiverIdentity, () => cancelSettings, () => cancelPeer,
        async (_, _, token) => { pendingGate.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(false, "", ""); },
        p => receiveProgress[p.Id] = p, _ => { });
    var cancelListener = cancelReceiver.RunAsync(cancelServerStop.Token);
    var cancelPlan = ManifestBuilder.BuildPlan([Path.Combine(source, "note.txt")], senderSettings);
    using var cancelSend = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var pendingSend = sender.SendAsync(cancelPlan, cancelPeer, cancelSend.Token);
    await pendingGate.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Check(cancelReceiver.Cancel(cancelPlan.Offer.Id), "Receiver can cancel pending transfer");
    await pendingSend.WaitAsync(TimeSpan.FromSeconds(3));
    Check(receiveProgress[cancelPlan.Offer.Id].State == "cancelled", "Pending cancellation updates receiver state");
    cancelServerStop.Cancel();
    try { await cancelListener; } catch (OperationCanceledException) { }

    checks += await SubnetDiscoveryScenarios.RunAsync(root);
    await ExerciseDiscoveryAsync(Check);
    await ExerciseMultiSendAsync(root, Path.Combine(source, "note.txt"), Check);
    checks += await ChunkTransportScenarios.RunAsync(root);
    checks += await ReceiveStorageScenarios.RunAsync(root);
    checks += await GroupTransportScenarios.RunAsync(root);

    serverStop.Cancel();
    try { await listener; } catch (OperationCanceledException) { }
    Console.WriteLine($"All {checks} transport checks passed.");
}
finally
{
    serverStop.Cancel();
    // Only this freshly generated test directory is eligible for recursive cleanup.
    var normalized = Path.GetFullPath(root);
    var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (normalized.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(normalized).StartsWith("Boardcast.TransportTests-", StringComparison.Ordinal))
        try { Directory.Delete(normalized, true); } catch { }
}



static async Task WaitForAsync(Func<bool> predicate, string reason)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!predicate()) { try { await Task.Delay(20, timeout.Token); } catch (OperationCanceledException) { throw new Exception("Timed out: " + reason); } }
}
static async Task ExerciseDiscoveryAsync(Action<bool, string> check)
{
    var local = new PeerInfo { Fingerprint = new string('a', 64), Name = "Discovery test", Port = 45999 };
    using var udpProbe = new UdpClient(0);
    int testPort = ((IPEndPoint)udpProbe.Client.LocalEndPoint!).Port; udpProbe.Dispose();
    using var discovery = new DiscoveryService(() => local, discoveryPort: testPort);
    using var stop = new CancellationTokenSource();
    var running = discovery.RunAsync(stop.Token);
    var peer = new PeerInfo { Fingerprint = new string('b', 64), Name = "Existing peer", Address = "127.0.0.2", Port = 45998 };
    discovery.Remember(peer);
    PeerInfo? Observed() => discovery.Peers.FirstOrDefault(p => p.Fingerprint == peer.Fingerprint);
    using var wrongEndpoint = new UdpClient(AddressFamily.InterNetwork);
    wrongEndpoint.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    wrongEndpoint.Client.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.3"), testPort));
    byte[] goodbye = JsonSerializer.SerializeToUtf8Bytes(new { protocolVersion = 1, type = "goodbye", fingerprint = peer.Fingerprint, name = peer.Name, platform = "windows", port = peer.Port, lanOnline = false }, BoardcastJson.Options);
    await wrongEndpoint.SendAsync(goodbye, new IPEndPoint(IPAddress.Loopback, testPort));
    await Task.Delay(100);
    check(Observed() is { DepartureHint: false }, "Spoofed goodbye from a different endpoint cannot mark or remove a peer");
    using var sameEndpoint = new UdpClient(AddressFamily.InterNetwork);
    sameEndpoint.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    sameEndpoint.Client.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), testPort));
    await sameEndpoint.SendAsync(goodbye, new IPEndPoint(IPAddress.Loopback, testPort));
    await WaitForAsync(() => Observed() is { DepartureHint: true }, "matching departure hint");
    check(Observed() is not null, "Even an endpoint-matched UDP goodbye cannot delete a certificate identity");
    discovery.Remember(peer);
    check(Observed() is { DepartureHint: false }, "Fresh peer observation clears a departure hint");
    local = local with { LanOnline = false };
    await discovery.StateChangedAsync(wasOnline: true);
    check(discovery.Peers.Count == 0, "Offline discovery clears displayed nearby devices");
    bool rejected = false;
    try { await discovery.ProbeAsync(); } catch (InvalidOperationException) { rejected = true; }
    check(rejected, "Offline manual discovery does not broadcast a probe");
    local = local with { LanOnline = true };
    await discovery.StateChangedAsync(wasOnline: false);
    discovery.Remember(peer);
    check(Observed() is not null, "Reconnected discovery accepts peer observations again");
    stop.Cancel(); await running;
}
static int UnusedPort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
static async Task ExerciseMultiSendAsync(string root, string source, Action<bool, string> check)
{
    using var stop = new CancellationTokenSource();
    var identities = new List<IdentityStore>();
    var listeners = new List<Task>();
    var receivers = new List<TransferService>();
    var peers = new List<PeerInfo>();
    var approval = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    try
    {
        for (int i = 0; i < 6; i++)
        {
            var identity = new IdentityStore(Path.Combine(root, "multi-identity-" + i)); identities.Add(identity);
            var settings = new AppSettings { DeviceName = "Receiver " + i, Port = UnusedPort(), ReceiveDirectory = Path.Combine(root, "multi-received-" + i) };
            var peer = new PeerInfo { Name = settings.DeviceName, Fingerprint = identity.Fingerprint, Address = "127.0.0.1", Port = settings.Port };
            peers.Add(peer);
            var receiver = new TransferService(identity, () => settings, () => peer,
                async (_, _, ct) => { await approval.Task.WaitAsync(ct); return new(true, settings.ReceiveDirectory, "Accepted"); }, _ => { }, _ => { });
            receivers.Add(receiver); listeners.Add(receiver.RunAsync(stop.Token));
        }
        using var daemon = new DaemonHost(Path.Combine(root, "multi-daemon"), UnusedPort(), noUi: true);
        var running = daemon.RunAsync(stop.Token);
        var response = await daemon.HandleAsync(new() { Command = "send-many", Peers = peers, Paths = [source] });
        check(response.Success && response.Transfers.Count == 6 && response.Transfers.Select(t => t.Id).Distinct().Count() == 6, "Multi-send returns distinct per-recipient transfer tickets immediately");
        DaemonSnapshot latest = new();
        async Task ReadStatus() => latest = (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!;
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            do { await ReadStatus(); await Task.Delay(20, timeout.Token); } while (latest.Transfers.Count(t => t.State == "waiting") != 4);
        var blockedInstall = await daemon.HandleAsync(new() { Command = "update-install", ProcessId = Environment.ProcessId });
        check(!blockedInstall.Success && (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!.Settings.LanOnline, "Pending or active transfers block update installation before taking LAN offline");
        check(latest.Transfers.Count(t => t.State == "queued") == 2, "Multi-send bounds simultaneous TLS transmissions to four and queues remaining peers");
        check(latest.Transfers.All(t => t.TotalBytes == new FileInfo(source).Length), "Every recipient uses the same enumerated manifest and byte total");
        await daemon.HandleAsync(new() { Command = "cancel", TransferId = response.Transfers[0].Id });
        await daemon.HandleAsync(new() { Command = "cancel", TransferId = response.Transfers[5].Id });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            do { await ReadStatus(); await Task.Delay(20, timeout.Token); } while (latest.Transfers.Count(t => t.State == "cancelled") != 2 || latest.Transfers.Count(t => t.State == "waiting") != 4);
        check(latest.Transfers.Count(t => t.State == "cancelled") == 2 && latest.Transfers.Count(t => t.State == "waiting") == 4, "Cancelling an active or queued recipient leaves other sends running");
        receivers[0].Cancel(response.Transfers[0].Id);
        approval.TrySetResult();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            do { await ReadStatus(); await Task.Delay(20, timeout.Token); } while (latest.Transfers.Count(t => t.State == "completed") != 4);
        check(latest.Transfers.Count(t => t.State == "completed") == 4 && latest.Transfers.Count(t => t.State == "cancelled") == 2, "Each multi-send recipient finishes with its own result");
        var wrongPin = peers[1] with { Fingerprint = new string('f', 64), Name = "Wrong identity" };
        var mixed = await daemon.HandleAsync(new() { Command = "send-many", Peers = [peers[0], wrongPin], Paths = [source] });
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            do { await ReadStatus(); await Task.Delay(20, timeout.Token); } while (mixed.Transfers.Any(ticket => !latest.Transfers.Any(t => t.Id == ticket.Id && t.State is "completed" or "failed")));
        check(latest.Transfers.Single(t => t.Id == mixed.Transfers[0].Id).State == "completed" && latest.Transfers.Single(t => t.Id == mixed.Transfers[1].Id).State == "failed", "A failed recipient pin does not fail another recipient's send");
        var unavailableInstall = await daemon.HandleAsync(new() { Command = "update-install", ProcessId = Environment.ProcessId });
        var restored = (await daemon.HandleAsync(new() { Command = "status" })).Snapshot!;
        check(!unavailableInstall.Success && restored.Settings.LanOnline, "Unavailable update installation restores the original LAN state");
        check(!restored.Update.CanUpdate && restored.AppVersion == AppRelease.CurrentVersion, "Unsigned development builds retain disabled update capability after an install rejection");
        var disconnected = await daemon.HandleAsync(new() { Command = "set-online", LanOnline = false });
        check(disconnected.Success && !disconnected.Snapshot!.Settings.LanOnline && disconnected.Snapshot.Settings.ReceivingEnabled, "LAN disconnect preserves the separate receiving-enabled preference");
        check(!(await daemon.HandleAsync(new() { Command = "connect", Address = "127.0.0.1", Port = peers[0].Port })).Success, "IPC direct IP cannot bypass disconnected LAN state");
        check(!(await daemon.HandleAsync(new() { Command = "send-many", Peers = peers, Paths = [source] })).Success, "Disconnected daemon refuses a new multi-send batch");
        check((await daemon.HandleAsync(new() { Command = "set-online", LanOnline = true })).Snapshot!.Settings.LanOnline, "IPC reconnect enables LAN activity again");
        stop.Cancel(); await running;
    }
    finally
    {
        approval.TrySetResult(); stop.Cancel();
        foreach (var listener in listeners) try { await listener; } catch (OperationCanceledException) { }
        foreach (var identity in identities) identity.Dispose();
    }
}
