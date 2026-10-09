using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Boardcast.Core;

namespace Boardcast.Windows;
public partial class MainWindow
{
    private bool _hasBackend, _rebindingPeers;
    private string _settingsSection = "general";
    private readonly HashSet<string> _selectedPeerIds = new(StringComparer.OrdinalIgnoreCase);
    private List<PeerInfo> SelectedRecipients() => _snapshot.Peers.Where(p => _selectedPeerIds.Contains(p.Fingerprint) && DeviceGroups.IsVisible(_snapshot.Settings, p)).ToList();
    private string PeerName(PeerInfo peer) => _snapshot.Settings.Devices.FirstOrDefault(d => d.Fingerprint == peer.Fingerprint)?.DisplayName ?? peer.Name;

    private void RenderPeerList()
    {
        var query = PeerSearch.Text.Trim();
        var recommended = RecommendedUpdatePeer();
        var peers = _snapshot.Peers
            .Where(IsPeerVisible)
            .OrderByDescending(p => p.Fingerprint == recommended?.Fingerprint)
            .ThenByDescending(p => _snapshot.Settings.Devices.Any(d => d.Fingerprint == p.Fingerprint && d.Trusted))
            .ThenBy(p => PeerName(p), StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PeerView(p, _snapshot.Settings.Devices.FirstOrDefault(d => d.Fingerprint == p.Fingerprint),
                IsUpdateSource(p) ? UpdateRecommendationLabel(p) : "", DeviceGroups.GetPeerGroup(_snapshot.Settings, p))).ToList();
        _selectedPeerIds.IntersectWith(_snapshot.Peers.Where(p => DeviceGroups.IsVisible(_snapshot.Settings, p)).Select(p => p.Fingerprint));
        var signature = System.Text.Json.JsonSerializer.Serialize(new
        {
            Query = query,
            _snapshot.Settings.PeerGroupFilter,
            _snapshot.Settings.VisibleGroups,
            LocalGroup = DeviceGroups.GetLocalGroup(_snapshot.Settings),
            Peers = peers.Select(p => new { p.Peer.Fingerprint, p.Peer.Address, p.Peer.Port, p.Peer.UpdatePort,
                p.Peer.Runtime, p.Peer.AppVersion, p.Peer.LanOnline, p.Peer.DepartureHint, p.Peer.ReceivingEnabled, p.Peer.UpdateVersion, p.DisplayName, p.Description, p.UpdateLabel, p.GroupName })
        });
        if (signature != _peerSignature)
        {
            _peerSignature = signature;
            _rebindingPeers = true;
            try
            {
                PeerList.ItemsSource = peers;
                foreach (var item in peers.Where(p => _selectedPeerIds.Contains(p.Peer.Fingerprint))) PeerList.SelectedItems.Add(item);
            }
            finally { _rebindingPeers = false; }
        }
        PeerSearchRow.Visibility = _snapshot.Peers.Count >= 6 || query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPeers.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var filtered = _snapshot.Settings.PeerGroupFilter != DeviceGroupFilter.All || query.Length > 0;
        EmptyPeersTitle.Text = !_snapshot.Settings.LanOnline ? "已断开局域网" : filtered ? "当前筛选下没有设备" : "等待设备上线";
        EmptyPeersHint.Text = !_snapshot.Settings.LanOnline ? "点击左下角的连接状态，上线后会自动查找设备。" : filtered ? "可调整分组或搜索条件，也可以显示全部设备。" : "在同一局域网的另一台设备上打开邻间，上线后会自动出现。";
        ShowAllPeersButton.Visibility = filtered && _snapshot.Settings.LanOnline ? Visibility.Visible : Visibility.Collapsed;
        PeerCount.Text = $"{peers.Count:N0} / {_snapshot.Peers.Count:N0} 台";
        PeerCount.ToolTip = $"当前显示 {peers.Count:N0} 台，共发现 {_snapshot.Peers.Count:N0} 台设备";
        UpdateGroupFilterUi();
        ManagePeerButton.IsEnabled = SelectedPeer != null;
    }

    private bool IsPeerVisible(PeerInfo peer)
    {
        if (!DeviceGroups.IsVisible(_snapshot.Settings, peer)) return false;
        var query = PeerSearch.Text.Trim();
        return query.Length == 0 || (PeerName(peer) + peer.Name + peer.Address + DeviceGroups.GetPeerGroup(_snapshot.Settings, peer))
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateConnectionUi()
    {
        if (ConnectionButton == null) return;
        var online = _snapshot.Settings.LanOnline && _hasBackend;
        ConnectionButton.IsEnabled = _hasBackend && !_exitInProgress;
        ConnectionState.Text = _sidebarCollapsed ? (online ? "●" : "○") : !_hasBackend ? "○  正在连接" : online ? "●  局域网在线" : "○  连接";
        ConnectionState.FontSize = _sidebarCollapsed ? 17 : 12;
        ConnectionButton.Background = Brushes.Transparent;
        ConnectionButton.Foreground = Brush(online ? "#21724F" : "#657080");
        System.Windows.Automation.AutomationProperties.SetName(ConnectionButton, online ? "局域网在线，点击断开连接" : "连接局域网");
        ConnectionButton.ToolTip = online ? "点击断开邻间的局域网连接；已接受的传输会继续完成。" : "点击连接局域网，向附近设备广播上线并自动查找设备。";
        RefreshPeersButton.IsEnabled = online;
        ConnectPeerButton.IsEnabled = online;
        ReceiveFolderButton.ToolTip = _snapshot.Settings.ReceiveDirectory;
    }
    private async void ConnectionClick(object sender, RoutedEventArgs e)
    {
        if (!_hasBackend) return;
        ConnectionButton.IsEnabled = false;
        var online = !_snapshot.Settings.LanOnline;
        try
        {
            if (await RequestAsync(new IpcRequest { Command = "set-online", LanOnline = online }) != null)
            {
                await RefreshAsync();
                SetStatus(online ? "已连接，正在自动查找附近设备。" : "已广播下线；已接受的传输会继续完成。");
            }
        }
        finally { UpdateConnectionUi(); }
    }

    private void FileSearchChanged(object sender, TextChangedEventArgs e) { if (FileList != null && FileCount != null) RenderPaths(); }
    private void PeerSearchChanged(object sender, TextChangedEventArgs e) { if (PeerList != null && RecipientHint != null) { RenderPeerList(); UpdateSend(); } }
    private void ClearFilesClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ClearSelectedPaths(); SummaryBox.Clear(); FileSearch.Clear(); RenderPaths();
    }
    private void ReceiveFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var directory = _snapshot.Settings.ReceiveDirectory;
            PathSafety.ValidateReceiveRoot(directory);
            Directory.CreateDirectory(directory);
            OpenFolder(directory);
        }
        catch (Exception ex) { SetStatus("无法打开接收文件夹：" + ex.Message); }
    }

    private void SettingsSectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section } && section != _settingsSection)
        {
            _settingsSection = section; RenderSettingsSection(); SettingsPage.ScrollToTop(); RulesPage.ScrollToTop();
        }
    }
    private void RenderSettingsSection()
    {
        foreach (var tab in new[] { GeneralSettingsTab, RulesSettingsTab, DevicesSettingsTab, ExtensionsSettingsTab, UpdatesSettingsTab })
        {
            tab.Background = Brushes.Transparent;
            tab.BorderBrush = (string)tab.Tag == _settingsSection ? (Brush)FindResource("Accent") : Brushes.Transparent;
            tab.FontWeight = (string)tab.Tag == _settingsSection ? FontWeights.SemiBold : FontWeights.Normal;
            tab.Foreground = (string)tab.Tag == _settingsSection ? (Brush)FindResource("Accent") : (Brush)FindResource("Muted");
        }
        RulesPage.Visibility = _settingsSection == "rules" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = _settingsSection == "rules" ? Visibility.Collapsed : Visibility.Visible;
        if (_settingsSection == "rules") RenderRules(); else RenderSettings();
    }

    private void ExpandFilesClick(object sender, RoutedEventArgs e)
    {
        var window = CreateExpandedFilesWindow();
        window.Owner = this;
        window.ShowDialog();
    }
    private Window CreateExpandedFilesWindow()
    {
        var window = new Window { Title = "待发送内容", Width = 860, Height = 680, MinWidth = 520, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Style = (Style)FindResource(typeof(Window)) };
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = $"待发送 · {_paths.Count:N0} 项", FontSize = 22, FontWeight = FontWeights.SemiBold };
        var searchRow = new DockPanel { Margin = new Thickness(0, 14, 0, 12) };
        searchRow.Children.Add(new TextBlock { Text = "筛选文件", Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        var search = new TextBox { Margin = new Thickness(0), ToolTip = "按名称或路径筛选" };
        System.Windows.Automation.AutomationProperties.SetName(search, "筛选完整待发送清单");
        searchRow.Children.Add(search);
        var list = new ListBox { ItemsSource = _paths.ToArray(), ItemTemplate = (DataTemplate)FindResource("FileItemTemplate"), SelectionMode = SelectionMode.Extended };
        System.Windows.Automation.AutomationProperties.SetName(list, "完整待发送清单");
        search.TextChanged += (_, _) =>
        {
            var matches = _paths.Where(p => p.Path.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
            list.ItemsSource = matches; heading.Text = $"待发送 · {matches.Length:N0} / {_paths.Count:N0} 项";
        };
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var close = new Button { Content = "返回", IsCancel = true, Margin = new Thickness(0) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        footer.Children.Add(new TextBlock { Text = "滚动查看全部内容 · 悬停查看完整路径", Foreground = (Brush)FindResource("Muted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(heading); Grid.SetRow(searchRow, 1); grid.Children.Add(searchRow);
        Grid.SetRow(list, 2); grid.Children.Add(list); Grid.SetRow(footer, 3); grid.Children.Add(footer);
        window.Content = grid; return window;
    }
}





