using System.Net.Security;
using System.Net.Sockets;

namespace Boardcast.Core;

public sealed partial class TransferService
{
    public const string SubnetSharingCapability = "subnet-sharing-v1";
    public Func<PendingSubnetOffer, CancellationToken, Task<IncomingDecision>>? DecideSubnets { get; set; }
    public async Task ShareSubnetsAsync(string id, PeerInfo recipient, List<DiscoverySubnet> subnets, CancellationToken ct)
    {
        var normalized = DiscoverySubnets.Normalize(subnets, allowEmpty: false);
        ValidateSubnetId(id);
        var state = new TransferProgress { Id = id, Kind = "subnets", Direction = "send", PeerName = recipient.Name, CurrentFile = "网段分享" };
        void Report(string value, string message = "") { state = state with { State = value, Message = message, UpdatedAt = DateTimeOffset.UtcNow }; progress(state); }
        Report("preparing");
        try
        {
            EnsureOnline();
            if (recipient.Fingerprint is not { Length: 64 } || !recipient.Fingerprint.All(Uri.IsHexDigit)) throw new InvalidDataException("接收设备证书指纹无效。");
            using var client = new TcpClient(LanAddress.Parse(recipient.Address).AddressFamily);
            Configure(client);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(LanAddress.Parse(recipient.Address), recipient.Port, timer.Token);
            await using var tls = await AuthenticateClient(client, recipient.Fingerprint, timer.Token);
            WireMessage? hello = null;
            var authenticated = await ExchangeHello(tls, recipient.Address, timer.Token, message => hello = message);
            EnsureOnline();
            if (!authenticated.LanOnline) throw new InvalidOperationException("对方已断开局域网连接。");
            remember(authenticated);
            if (!hello!.Capabilities.Contains(SubnetSharingCapability))
            {
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "bye" }, timer.Token);
                throw new InvalidDataException("对方版本暂不支持网段分享，请先更新对方设备。");
            }
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "subnet-offer", TransferId = id, Subnets = normalized }, timer.Token);
            Report("waiting", "等待对方确认接收网段。");
            timer.CancelAfter(TimeSpan.FromMinutes(2));
            WireMessage response;
            do
            {
                response = await WireProtocol.ReadAsync<WireMessage>(tls, timer.Token);
                if (response.Version != 1 || response.TransferId != id || response.Message is null || response.Message.Length > 4096)
                    throw new InvalidDataException("网段分享确认无效。");
            } while (response.Type == "subnet-pending");
            if (response.Type == "subnet-reject") { Report("rejected", response.Message); return; }
            if (response.Type != "subnet-accept") throw new InvalidDataException("对方未确认网段分享。");
            Report("completed", "对方已接收网段并开始发现设备。");
        }
        catch (OperationCanceledException) { Report(ct.IsCancellationRequested ? "cancelled" : "failed", "网段分享已取消或超时。"); }
        catch (Exception ex) { Report("failed", ex.Message); }
    }
    private async Task ReceiveSubnetsAsync(SslStream tls, WireMessage message, PeerInfo sender, CancellationTokenSource timer)
    {
        ValidateSubnetId(message.TransferId);
        if (message.Version != 1) throw new InvalidDataException("网段分享协议版本无效。");
        var subnets = DiscoverySubnets.Normalize(message.Subnets, allowEmpty: false);
        string localId = "subnet-receive-" + sender.Fingerprint + "-" + message.TransferId;
        var state = new TransferProgress { Id = localId, Kind = "subnets", Direction = "receive", PeerName = sender.Name, State = "pending", CurrentFile = "网段分享" };
        void Report(string value, string reason = "") { state = state with { State = value, Message = reason, UpdatedAt = DateTimeOffset.UtcNow }; progress(state); }
        Report("pending", "请确认是否接收对方分享的网段。");
        timer.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            EnsureOnline();
            if (!settings().ReceivingEnabled) throw new InvalidDataException("接收已暂停。");
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "subnet-pending", TransferId = message.TransferId }, timer.Token);
            var offer = new PendingSubnetOffer { Id = localId, SenderName = sender.Name, SenderFingerprint = sender.Fingerprint, Address = sender.Address, Subnets = subnets };
            // This callback always asks explicitly and commits settings before returning an acceptance.
            var choice = DecideSubnets is null ? new IncomingDecision(false, "", "此设备暂不支持接收网段。") : await DecideSubnets(offer, timer.Token);
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = choice.Accept ? "subnet-accept" : "subnet-reject", TransferId = message.TransferId, Message = choice.Reason }, timer.Token);
            Report(choice.Accept ? "completed" : "rejected", choice.Reason);
        }
        catch (OperationCanceledException) { Report("cancelled", "网段分享已取消或超时。"); throw; }
        catch (Exception ex)
        {
            Report("failed", ex.Message);
            try { await WireProtocol.WriteAsync(tls, new WireMessage { Type = "subnet-reject", TransferId = message.TransferId, Message = ex.Message }, timer.Token); } catch { }
        }
    }
    private static void ValidateSubnetId(string? id)
    {
        if (id is null || id.Length != 32 || id.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException("网段分享标识无效。");
    }
}
