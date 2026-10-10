using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Boardcast.Core;

namespace Boardcast.Windows;

public partial class MainWindow
{
    private Border? _subnetSettingsHost;
    private string? _subnetSettingsSignature;
    private bool _subnetSharing;

    private List<DiscoverySubnet> ShareableSubnets() => _snapshot.LocalDiscoverySubnets
        .Concat(_snapshot.Settings.DiscoverySubnets)
        .DistinctBy(subnet => (subnet.Cidr, subnet.Port))
        .OrderBy(subnet => subnet.Cidr, StringComparer.Ordinal)
        .ThenBy(subnet => subnet.Port).ToList();

    private bool CanShareSubnets() => !_subnetSharing && !_savingSettings && _hasBackend && _snapshot.Settings.LanOnline &&
        ShareableSubnets().Count > 0 && _snapshot.Peers.Any(peer => peer.LanOnline && peer.ReceivingEnabled && !peer.DepartureHint);

    private static string SubnetLabel(DiscoverySubnet subnet) => $"{subnet.Cidr} · 端口 {subnet.Port}";

    private void AddSubnetSettings()
    {
        _subnetSettingsHost = new Border();
        _subnetSettingsSignature = null;
        SettingsContent.Children.Add(_subnetSettingsHost);
        RefreshSubnetUi();
    }

    private void RefreshSubnetUi()
    {
        if (ShareSubnetsButton != null)
        {
            ShareSubnetsButton.IsEnabled = CanShareSubnets();
            ShareSubnetsButton.ToolTip = "将可发现的网段分享给一台或多台设备，由对方确认是否接受。";
        }
        if (_subnetSettingsHost == null || _page != "settings" || _settingsSection != "general") return;
        var signature = JsonSerializer.Serialize(new
        {
            _snapshot.LocalDiscoverySubnets, _snapshot.Settings.DiscoverySubnets,
            CanShare = CanShareSubnets(), CanForget = !_savingSettings && (_hasBackend || _previewMode)
        });
        if (signature == _subnetSettingsSignature) return;
        _subnetSettingsSignature = signature;
        var share = SettingAction("分享网段…", () => OpenSubnetShareAsync());
        share.IsEnabled = CanShareSubnets();
        AutomationProperties.SetAutomationId(share, "SettingsShareSubnets");
        var rows = new List<UIElement>
        {
            SettingRow(SettingDescription("可发现的网段", "通过 IP 连接成功或接受网段分享后，自动记住网段并查找其中的邻间设备。"), share)
        };
        foreach (var subnet in _snapshot.LocalDiscoverySubnets)
            rows.Add(SettingDescription(SubnetLabel(subnet), "本机当前网络 · 可选择分享给其他设备"));
        foreach (var subnet in _snapshot.Settings.DiscoverySubnets)
        {
            var forget = SettingAction("遗忘", () => ForgetSubnetAsync(subnet), quiet: true);
            forget.IsEnabled = !_savingSettings && (_hasBackend || _previewMode);
            AutomationProperties.SetName(forget, "遗忘网段 " + SubnetLabel(subnet));
            AutomationProperties.SetAutomationId(forget, "ForgetSubnet:" + subnet.Cidr + ":" + subnet.Port);
            rows.Add(SettingRow(SettingDescription(SubnetLabel(subnet), "已保存 · 自动查找此网段中的设备"), forget));
        }
        if (_snapshot.Settings.DiscoverySubnets.Count == 0)
            rows.Add(SettingDescription("暂无已保存的网段", "可以先通过 IP 连接其他网段的一台设备，或接受其他设备发来的网段。"));
        rows.Add(SettingDescription("遗忘后停止主动查找该网段", "本机当前网络仍可直接发现设备；再次通过 IP 连接或接受分享可以重新添加网段。"));
        _subnetSettingsHost.Child = SettingGroup("网段发现", rows.ToArray());
    }

    private async Task ForgetSubnetAsync(DiscoverySubnet subnet)
    {
        if (_savingSettings) { SetStatus("正在保存，请稍后再遗忘网段。"); return; }
        int mutationRevision = ++_settingsRevision;
        if (_previewMode)
            _snapshot.Settings.DiscoverySubnets.RemoveAll(saved => saved.Cidr == subnet.Cidr && saved.Port == subnet.Port);
        else
        {
            var reply = await RequestAsync(new IpcRequest { Command = "subnet-forget", Subnets = [subnet] });
            if (reply == null) return;
            ApplySubnetMutationSnapshot(reply, mutationRevision);
            await RefreshAsync();
        }
        RefreshSubnetUi();
        SetStatus("已遗忘网段 " + SubnetLabel(subnet) + "。");
    }

    private async void ShareSubnetsClick(object sender, RoutedEventArgs e) => await OpenSubnetShareAsync();

    private SubnetShareDialog CreateSubnetShareDialog(Window owner, List<PeerInfo> initialRecipients) => new(owner,
        ShareableSubnets(),
        _snapshot.Peers.Where(peer => peer.LanOnline && !peer.DepartureHint)
            .OrderBy(PeerName, StringComparer.CurrentCultureIgnoreCase)
            .Select(peer => (Peer: peer, Name: PeerName(peer))).ToList(),
        initialRecipients.Select(peer => peer.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase));

    private async Task OpenSubnetShareAsync(List<PeerInfo>? initialRecipients = null)
    {
        if (!CanShareSubnets()) return;
        var dialog = CreateSubnetShareDialog(this, initialRecipients ?? SelectedRecipients());
        if (dialog.ShowDialog() != true) return;
        await SubmitSubnetShareAsync(dialog.SelectedPeers, dialog.SelectedSubnets);
    }

    private async Task SubmitSubnetShareAsync(List<PeerInfo> peers, List<DiscoverySubnet> subnets)
    {
        if (_subnetSharing || !_snapshot.Settings.LanOnline || subnets.Count is < 1 or > 32 || peers.Count is < 1 or > 32) return;
        var recipients = peers.Select(peer => _snapshot.Peers.FirstOrDefault(current => current.Fingerprint == peer.Fingerprint))
            .Where(peer => peer is { LanOnline: true, ReceivingEnabled: true, DepartureHint: false }).Cast<PeerInfo>().ToList();
        if (recipients.Count != peers.Count) { SetStatus("所选设备状态已变化，请重新选择在线且允许接收的设备。"); return; }
        _settingsRevision++;
        _subnetSharing = true;
        RefreshSubnetUi();
        try
        {
            IpcResponse? reply;
            if (_previewMode)
                reply = new IpcResponse { Transfers = recipients.Select(peer => new TransferTicket
                    { Id = Guid.NewGuid().ToString("N"), PeerFingerprint = peer.Fingerprint, PeerName = PeerName(peer) }).ToList() };
            else
                reply = await RequestAsync(new IpcRequest { Command = "subnet-share", Peers = recipients, Subnets = subnets });
            if (reply == null) return;
            foreach (var ticket in reply.Transfers)
                _snapshot.Transfers.Add(new TransferProgress
                {
                    Id = ticket.Id, Kind = "subnets", Direction = "send", State = "waiting", PeerName = ticket.PeerName,
                    Message = $"已分享 {subnets.Count} 个网段，等待对方确认。"
                });
            if (!_previewMode) await RefreshAsync();
            ShowPage("transfers");
            SetStatus($"已向 {recipients.Count} 台设备发起网段分享，等待对方确认。");
        }
        finally { _subnetSharing = false; RefreshSubnetUi(); }
    }

    private void RenderPendingSubnets()
    {
        foreach (var pending in _snapshot.PendingSubnets)
        {
            var alias = _snapshot.Settings.Devices.FirstOrDefault(device => device.Fingerprint == pending.SenderFingerprint)?.DisplayName ?? pending.SenderName;
            var content = new StackPanel();
            content.Children.Add(Heading($"来自 {alias} 的网段分享", 19));
            content.Children.Add(Note($"来源地址：{pending.Address}\n指纹：{pending.SenderFingerprint}"));
            content.Children.Add(Note($"{pending.Subnets.Count} 个网段 · 接受后会保存并自动查找其中的邻间设备。"));
            foreach (var subnet in pending.Subnets)
                content.Children.Add(Note(SubnetLabel(subnet)));
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var accept = Action("接受网段", () => DecideSubnetsAsync(pending, true), true);
            accept.IsEnabled = _snapshot.Settings.LanOnline && _snapshot.Settings.ReceivingEnabled && (_hasBackend || _previewMode);
            accept.ToolTip = !_snapshot.Settings.LanOnline ? "连接局域网后才能接受网段。" : !_snapshot.Settings.ReceivingEnabled ? "本机已暂停接收。" : "保存这些网段并自动查找设备";
            var reject = Action("拒绝", () => DecideSubnetsAsync(pending, false));
            AutomationProperties.SetName(accept, "接受来自 " + alias + " 的网段分享");
            AutomationProperties.SetName(reject, "拒绝来自 " + alias + " 的网段分享");
            actions.Children.Add(accept); actions.Children.Add(reject);
            content.Children.Add(actions);
            TransferContent.Children.Add(Card(content, "#FFF9EC"));
        }
    }

    private async Task DecideSubnetsAsync(PendingSubnetOffer pending, bool accept)
    {
        int mutationRevision = ++_settingsRevision;
        if (_previewMode)
        {
            if (accept)
                _snapshot.Settings.DiscoverySubnets = _snapshot.Settings.DiscoverySubnets.Concat(pending.Subnets)
                    .DistinctBy(subnet => (subnet.Cidr, subnet.Port)).ToList();
            _snapshot.PendingSubnets.RemoveAll(offer => offer.Id == pending.Id);
        }
        else
        {
            var reply = await RequestAsync(new IpcRequest { Command = "subnet-decide", TransferId = pending.Id, Accept = accept });
            if (reply == null) return;
            ApplySubnetMutationSnapshot(reply, mutationRevision);
            await RefreshAsync();
        }
        RenderTransfers();
        RefreshSubnetUi();
        UpdatePendingNavigation();
        SetStatus(accept ? "已接受网段，正在自动查找设备。" : "已拒绝此次网段分享。");
    }

    private void RenderSubnetTransfer(TransferProgress transfer)
    {
        var content = new StackPanel();
        content.Children.Add(Heading($"网段分享 · {(transfer.Direction == "receive" ? "接收自" : "发送至")} {transfer.PeerName}", 18));
        content.Children.Add(Note(StateLabel(transfer.State)));
        if (transfer.Message.Length > 0) content.Children.Add(Note(transfer.Message));
        if (transfer.Direction == "send" && transfer.State is not ("completed" or "rejected" or "failed" or "cancelled"))
        {
            var cancel = Action("取消", () => CancelSubnetShareAsync(transfer));
            AutomationProperties.SetName(cancel, "取消发送至 " + transfer.PeerName + " 的网段分享");
            var actions = new WrapPanel();
            actions.Children.Add(cancel);
            content.Children.Add(actions);
        }
        TransferContent.Children.Add(Card(content));
    }

    private async Task CancelSubnetShareAsync(TransferProgress transfer)
    {
        int mutationRevision = ++_settingsRevision;
        if (_previewMode)
        {
            transfer.State = "cancelled";
            transfer.Message = "已取消网段分享。";
        }
        else
        {
            var reply = await RequestAsync(new IpcRequest { Command = "cancel", TransferId = transfer.Id });
            if (reply == null) return;
            ApplySubnetMutationSnapshot(reply, mutationRevision);
            await RefreshAsync();
        }
        RenderTransfers();
        SetStatus("已取消网段分享。");
    }

    // A mutation reply keeps the view current even when the periodic status request
    // is still in flight. RefreshAsync discards older replies via _settingsRevision.
    private void ApplySubnetMutationSnapshot(IpcResponse reply, int mutationRevision)
    {
        if (reply.Snapshot == null || _savingSettings || mutationRevision != _settingsRevision) return;
        _snapshot = reply.Snapshot;
        UpdateConnectionUi();
        RenderPeerList();
        RefreshUpdateUi();
        RefreshSubnetUi();
        UpdatePendingNavigation();
        UpdateSend();
    }

    private void UpdatePendingNavigation()
    {
        int pending = _snapshot.Pending.Count + _snapshot.PendingSubnets.Count;
        TransfersNavLabel.Text = pending > 0 ? $"传输记录 · {pending}" : "传输记录";
        PendingNavBadge.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
