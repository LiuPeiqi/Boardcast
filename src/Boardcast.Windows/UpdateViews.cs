using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Boardcast.Core;

namespace Boardcast.Windows;
public partial class MainWindow
{
    private string? _pinnedUpdatePeer, _updateSignature, _lastUpdateOutcome;
    private PeerView? _contextPeer;
    private PeerInfo? RecommendedUpdatePeer() => PeerUpdateCatalog.Recommend(_snapshot.Peers, AppRelease.CurrentVersion, AppRelease.RuntimeIdentifier, _pinnedUpdatePeer);
    private string UpdateRecommendationLabel(PeerInfo peer) => "v" + peer.UpdateVersion;
    private bool IsUpdateSource(PeerInfo peer) => peer.LanOnline && !peer.DepartureHint &&
        PeerUpdateCatalog.IsNewerCompatible(peer, AppRelease.CurrentVersion, AppRelease.RuntimeIdentifier);

    private void PeerContextMouseDown(object sender, MouseButtonEventArgs e) => _contextPeer = (ItemsControl.ContainerFromElement(PeerList, e.OriginalSource as DependencyObject) as ListBoxItem)?.DataContext as PeerView;
    private void PeerContextKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))
            _contextPeer = (ItemsControl.ContainerFromElement(PeerList, Keyboard.FocusedElement as DependencyObject) as ListBoxItem)?.DataContext as PeerView ?? SelectedPeer;
    }
    private void PeerContextOpening(object sender, ContextMenuEventArgs e)
    {
        var view = _contextPeer;
        if (view == null) { e.Handled = true; return; }
        var peer = view.Peer;
        var menu = PeerList.ContextMenu;
        menu.Items.Clear();
        var details = new MenuItem { Header = "设备详情" };
        details.Click += async (_, _) => await EditDeviceAsync(peer);
        menu.Items.Add(details);
        var copy = new MenuItem { Header = "复制 IP 地址" };
        copy.Click += (_, _) => { Clipboard.SetText(peer.Address); SetStatus("地址已复制。"); };
        menu.Items.Add(copy);
        menu.Items.Add(new Separator());
        if (IsUpdateSource(peer))
        {
            var pin = new MenuItem { Header = _pinnedUpdatePeer == peer.Fingerprint ? "取消置顶版本" : $"置顶 v{peer.UpdateVersion} 更新" };
            pin.Click += (_, _) => { _pinnedUpdatePeer = _pinnedUpdatePeer == peer.Fingerprint ? null : peer.Fingerprint; _peerSignature = "-"; RenderPeerList(); RefreshUpdateUi(); };
            menu.Items.Add(pin);
            var update = new MenuItem { Header = $"更新到 v{peer.UpdateVersion}", IsEnabled = CanDownloadUpdate(), ToolTip = _snapshot.Update.Message };
            update.Click += async (_, _) => await DownloadUpdateAsync(peer);
            menu.Items.Add(update);
        }
        else
        {
            var text = !peer.LanOnline || peer.DepartureHint ? "此设备已离线或可能离开" : peer.Runtime.Length > 0 && peer.Runtime != AppRelease.RuntimeIdentifier ? "此设备的版本不适用于当前系统" :
                peer.UpdateVersion.Length == 0 ? "此设备未提供可验证的更新包" : "此设备版本无需更新";
            menu.Items.Add(new MenuItem { Header = text, IsEnabled = false });
        }
        _contextPeer = null;
    }
    private bool CanDownloadUpdate() => _hasBackend && _snapshot.Settings.LanOnline && _snapshot.Update.CanUpdate && _snapshot.Update.State is not ("downloading" or "restoring" or "installing");
    private void PeerUpdateClick(object sender, RoutedEventArgs e)
    {
        _settingsSection = "updates";
        ShowPage("settings");
        e.Handled = true;
    }
    private async Task DownloadUpdateAsync(PeerInfo peer)
    {
        var source = _snapshot.Peers.FirstOrDefault(p => p.Fingerprint == peer.Fingerprint);
        if (source == null || !IsUpdateSource(source)) { SetStatus("此更新来源当前不可用，请选择在线设备。"); return; }
        peer = source;
        _settingsSection = "updates"; ShowPage("settings");
        if (!CanDownloadUpdate()) { SetStatus(_snapshot.Update.Message); return; }
        var dialog = new EditorDialog(this, $"更新到 v{peer.UpdateVersion}") { Height = 390 };
        dialog.AddNote($"从 {PeerName(peer)} 下载更新包。下载后将检查发布签名和文件完整性；你可以在传输结束后选择重启并更新。");
        dialog.AddNote($"当前 v{AppRelease.CurrentVersion} → v{peer.UpdateVersion}");
        if (dialog.ShowDialog() != true) return;
        if (await RequestAsync(new IpcRequest { Command = "update-download", Peer = peer }) != null)
        { await RefreshAsync(); SetStatus("正在下载并验证更新…"); RenderUpdateSettings(); }
    }
    private void RefreshUpdateUi()
    {
        var update = _snapshot.Update;
        if (update.State == "idle" && update.Message != _lastUpdateOutcome &&
            (update.Message.StartsWith("已更新到 v", StringComparison.Ordinal) || update.Message.StartsWith("上次更新", StringComparison.Ordinal)))
        {
            _lastUpdateOutcome = update.Message;
            SetStatus(update.Message);
        }
        var recommended = RecommendedUpdatePeer();
        var signature = update.State + update.Version + update.BytesReceived + update.TotalBytes + update.Message + update.CanUpdate + HasActiveTransfers() + _snapshot.Settings.LanOnline + _hasBackend + recommended?.Fingerprint + recommended?.UpdateVersion + recommended?.UpdatePort + string.Join("|", _snapshot.Peers.Select(p => p.Fingerprint + p.UpdateVersion + p.UpdatePort + p.LanOnline + p.DepartureHint));
        if (signature == _updateSignature) return;
        _updateSignature = signature;
        if (_page == "settings" && _settingsSection == "updates") RenderUpdateSettings();
    }
    private Button UpdateAction(string label, Func<Task> action, bool primary = false, bool quiet = false) =>
        SettingAction(label, action, primary, quiet);
    private void RenderUpdateSettings()
    {
        SettingsContent.Children.Clear();
        SettingsContent.Children.Add(SettingGroup("当前版本",
            SettingRow(SettingDescription("邻间", $"v{AppRelease.CurrentVersion} · {AppRelease.RuntimeIdentifier}"))));
        var update = _snapshot.Update;
        var statusRows = new List<UIElement>();
        if (update.State == "downloading")
        {
            var progress = SettingDescription($"正在下载 v{update.Version}",
                $"{Size(update.BytesReceived)} / {Size(update.TotalBytes)}" +
                (update.SourceCount > 1 ? $" · {update.SourceCount} 台设备提供" : $" · {update.PeerName}"));
            var phase = SettingText(update.Message, true); phase.FontSize = 12; phase.Margin = new Thickness(0, 4, 0, 0);
            progress.Children.Add(phase);
            progress.ToolTip = update.Message +
                (update.ReusedBytes > 0 ? $" · 已续传 {Size(update.ReusedBytes)}" : "") +
                (update.PeerBytes > 0 ? $" · 协作下载 {Size(update.PeerBytes)}" : "");
            progress.Children.Add(new ProgressBar { Height = 4, Minimum = 0, Maximum = Math.Max(1, update.TotalBytes), Value = update.BytesReceived, IsIndeterminate = update.TotalBytes == 0, Foreground = (Brush)FindResource("Accent"), Margin = new Thickness(0, 10, 0, 0) });
            statusRows.Add(SettingRow(progress, UpdateAction("暂停下载", async () => { await RequestAsync(new IpcRequest { Command = "update-cancel" }); await RefreshAsync(); })));
        }
        else if (update.State == "ready")
        {
            var install = UpdateAction("重启并更新", InstallUpdateAsync, true);
            install.IsEnabled = !HasActiveTransfers();
            var ready = SettingDescription($"v{update.Version} 已就绪", string.IsNullOrWhiteSpace(update.Message)
                ? "更新包已验证，可以重启安装。" : update.Message);
            if (HasActiveTransfers())
            {
                var waiting = SettingText("请先完成当前传输或接收请求。", true);
                waiting.FontSize = 12; waiting.Margin = new Thickness(0, 4, 0, 0); ready.Children.Add(waiting);
            }
            statusRows.Add(SettingRow(ready, install));
        }
        else
            statusRows.Add(SettingRow(SettingDescription(update.State switch
            {
                "restoring" => "正在恢复下载",
                "installing" => "正在准备安装",
                "paused" => "下载已暂停",
                "failed" => "更新未完成",
                "disabled" => "更新暂不可用",
                _ => "自动发现附近版本"
            }, update.Message)));
        SettingsContent.Children.Add(SettingGroup("局域网更新", statusRows.ToArray()));

        if (update.State is not ("downloading" or "restoring" or "installing"))
        {
            var versions = PeerUpdateCatalog.AvailableVersions(_snapshot.Peers, AppRelease.CurrentVersion, AppRelease.RuntimeIdentifier);
            var recommended = versions.FirstOrDefault(version => version.Sources.Any(source => source.Fingerprint == _pinnedUpdatePeer)) ?? versions.FirstOrDefault();
            var versionRows = new List<UIElement>();
            if (versions.Count == 0)
                versionRows.Add(SettingRow(SettingText("附近设备暂无可用的新版本。", true)));
            foreach (var version in versions)
            {
                var peer = version.Sources.FirstOrDefault(source => source.Fingerprint == _pinnedUpdatePeer) ?? version.Sources[0];
                var pinned = version.Sources.Any(source => source.Fingerprint == _pinnedUpdatePeer);
                var description = SettingDescription($"v{version.Version}", $"{version.Sources.Count} 台设备可提供" +
                    (pinned ? " · 已置顶" : version == recommended ? " · 推荐" : ""));
                if (update.State == "ready" && update.Version == version.Version)
                    versionRows.Add(SettingRow(description, SettingText("已就绪", true)));
                else
                {
                    var download = UpdateAction(update.CanResume && update.Version == version.Version ? "继续下载" : "下载", async () => await DownloadUpdateAsync(peer),
                        update.State != "ready" && version == recommended);
                    download.IsEnabled = CanDownloadUpdate();
                    versionRows.Add(SettingRow(description, download));
                }
            }
            SettingsContent.Children.Add(SettingGroup("可用版本", versionRows.ToArray()));
        }
        if (_pinnedUpdatePeer != null)
            SettingsContent.Children.Add(UpdateAction("恢复推荐最新版本", () => { _pinnedUpdatePeer = null; _peerSignature = "-"; RenderPeerList(); RenderUpdateSettings(); return Task.CompletedTask; }, quiet: true));
    }
    private bool HasActiveTransfers() => _snapshot.Pending.Count > 0 || _snapshot.Transfers.Any(t => t.State is not ("completed" or "failed" or "cancelled" or "rejected" or "paused"));
    private async Task InstallUpdateAsync()
    {
        if (_busy || HasActiveTransfers()) { SetStatus("请等待当前传输结束后再更新。"); return; }
        var reply = await RequestAsync(new IpcRequest { Command = "update-install", ProcessId = Environment.ProcessId });
        if (reply == null) return;
        _timer.Stop(); _exitInProgress = true; _allowClose = true;
        _lifetime.Cancel();
        await _summaryTask;
        await Task.WhenAll(_sizeTasks.ToArray());
        _tray?.Dispose(); _tray = null;
        Application.Current.Shutdown();
    }
}






