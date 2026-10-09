using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Boardcast.Core;

public sealed partial class TransferService
{
    private const string ReceiveCancellationCapability = "receive-cancel-v1";
    // Registration and inactive cleanup share this gate so a resumed receiver cannot open a cache while it is discarded.
    private readonly object receiveCancellationGate = new();

    /// <summary>Best-effort authenticated discard of this sender's unfinished receive task on the recipient.</summary>
    public async Task<bool> CancelRemoteReceiveAsync(PeerInfo peer, string transferId, CancellationToken ct)
    {
        if (!Guid.TryParse(transferId, out _)) throw new ArgumentException("Invalid transfer ID.", nameof(transferId));
        try
        {
            EnsureOnline();
            if (peer.Fingerprint.Length != 64 || !peer.Fingerprint.All(Uri.IsHexDigit))
                throw new InvalidDataException("Recipient fingerprint is required.");
            using var client = new TcpClient(LanAddress.Parse(peer.Address).AddressFamily);
            Configure(client);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(LanAddress.Parse(peer.Address), peer.Port, timeout.Token);
            await using var tls = await AuthenticateClient(client, peer.Fingerprint, timeout.Token);
            WireMessage? remoteHello = null;
            var authenticated = await ExchangeHello(tls, peer.Address, timeout.Token, value => remoteHello = value);
            EnsureOnline();
            if (!authenticated.LanOnline) throw new InvalidOperationException("对方已断开局域网连接。");
            remember(authenticated);
            if (!remoteHello!.Capabilities.Contains(ReceiveCancellationCapability))
            {
                await WireProtocol.WriteAsync(tls, new WireMessage { Type = "bye" }, timeout.Token);
                Diagnostic?.Invoke("接收端不支持远端取消，请在接收端手动丢弃未完成任务。");
                return false;
            }
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "transfer-cancel", TransferId = transferId }, timeout.Token);
            var response = await WireProtocol.ReadAsync<WireMessage>(tls, timeout.Token);
            if (response.Type == "transfer-cancelled" && response.TransferId == transferId) return true;
            Diagnostic?.Invoke(response.Message.Length > 0 ? response.Message : "接收端未确认取消任务。");
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Diagnostic?.Invoke("远端取消请求超时，请在接收端检查未完成任务。");
            return false;
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or InvalidOperationException or ArgumentException)
        {
            Diagnostic?.Invoke("远端取消未确认：" + ex.Message);
            return false;
        }
    }

    private async Task HandleReceiveCancellationAsync(SslStream tls, WireMessage message, string authenticatedFingerprint, CancellationToken ct)
    {
        var transferId = message.TransferId;
        if (!Guid.TryParse(transferId, out _))
        {
            await WireProtocol.WriteAsync(tls, new WireMessage { Type = "reject", Message = "取消请求的任务编号无效。" }, ct);
            return;
        }
        IncomingTransfer? active = null;
        var handled = false;
        var cleaned = true;
        lock (receiveCancellationGate)
        {
            if (incoming.TryGetValue(transferId, out active))
            {
                lock (active.Gate)
                {
                    // The certificate, rather than the advertised peer or a guessed task ID, identifies the sender.
                    if (StringComparer.OrdinalIgnoreCase.Equals(active.SenderFingerprint, authenticatedFingerprint) &&
                        !active.Finished.Task.IsCompleted && (active.Storage is null || active.Storage.MarkCancelled()))
                    {
                        active.CancelRequested = true;
                        active.Stop.Cancel();
                        handled = true;
                    }
                }
            }
            else
            {
                foreach (var storage in ReceiveStorage.FindStored(identity.DataDirectory, transferId))
                {
                    if (!StringComparer.OrdinalIgnoreCase.Equals(storage.SenderFingerprint, authenticatedFingerprint) ||
                        !storage.MarkCancelled()) continue;
                    var clean = storage.CleanupCancelled();
                    cleaned &= clean;
                    progress(new TransferProgress { Id = transferId, Direction = "receive", State = "cancelled", Destination = storage.Destination,
                        CompletedFiles = storage.CompletedFiles, BytesTransferred = storage.CompletedBytes, TotalBytes = storage.TotalBytes,
                        CanDiscard = !clean, Message = CancellationMessage(storage.CompletedFiles, clean) });
                    handled = true;
                }
            }
        }
        // The acknowledgement follows stream closure and cleanup; completed files remain owned by the recipient.
        if (handled && active is not null)
        {
            await active.Finished.Task.WaitAsync(ct).ConfigureAwait(false);
            cleaned = active.CleanupSucceeded;
        }
        await WireProtocol.WriteAsync(tls, new WireMessage
        {
            Type = handled && cleaned ? "transfer-cancelled" : "reject", TransferId = transferId,
            Message = handled ? (cleaned ? "接收任务已取消。" : "接收任务已取消，部分临时内容待清理，将自动重试。") : "没有可供该发送方取消的未完成接收任务。"
        }, ct);
    }
}
