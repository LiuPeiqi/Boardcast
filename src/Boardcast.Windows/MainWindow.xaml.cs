using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Boardcast.Core;
namespace Boardcast.Windows;
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly List<SelectedPath> _paths = [];
    private DaemonSnapshot _snapshot = new();
    private bool _refreshing, _busy, _loaded;
    private string _page = "devices";
    private string _peerSignature = "-";
    private string? _dataDirectory;
    private PeerView? SelectedPeer => PeerList.SelectedItems.Count == 1 ? PeerList.SelectedItems[0] as PeerView : null;
    public MainWindow()
    {
        InitializeComponent();
        var args = Environment.GetCommandLineArgs();
        var dataIndex = Array.IndexOf(args, "--data-dir");
        if (dataIndex >= 0 && args.Length > dataIndex + 1) _dataDirectory = args[dataIndex + 1];
        InitializeLifecycle(args.Contains("--render-preview"), args.Contains("--background"));
        SetSidebarCollapsed(!_previewMode ? UiPreferences.Load(_dataDirectory).SidebarCollapsed : true);
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            if (args.Contains("--render-preview"))
            {
                ConfigurePreview(args);

                await Dispatcher.InvokeAsync(() => RenderPreview(args[Array.IndexOf(args, "--render-preview") + 1]), DispatcherPriority.ApplicationIdle);
                return;
            }
            if (args.Contains("--background")) HideToTray();
            _initializationTask = InitializeBackgroundAsync(args.Contains("--incoming"));
            await _initializationTask;
        };
        _timer.Tick += async (_, _) => { if (IsVisible && WindowState != WindowState.Minimized) await RefreshAsync(); };
        Closed += (_, _) => _timer.Stop();

        ShowPage("devices");
    }
    private async Task InitializeBackgroundAsync(bool incoming)
    {
        try
        {
            await StartDaemonAsync();
            if (_exitInProgress) return;
            await RefreshAsync();
            if (incoming) ShowPage("transfers");
            if (IsVisible) _timer.Start();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!_exitInProgress) SetStatus("后台启动失败：" + ex.Message); }
    }
    private async Task StartDaemonAsync()
    {
        _lifetime.Token.ThrowIfCancellationRequested();
        try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); cts.CancelAfter(500); await IpcClient.SendAsync(new IpcRequest(), _dataDirectory, cts.Token); return; }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
        _lifetime.Token.ThrowIfCancellationRequested();
        var executable = Path.Combine(AppContext.BaseDirectory, "Boardcast.Daemon.exe");
        if (!File.Exists(executable))
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props"))) root = root.Parent;
            if (root != null) executable = Path.Combine(root.FullName, "src", "Boardcast.Daemon", "bin", "Debug", "net10.0", "Boardcast.Daemon.exe");
        }
        if (!File.Exists(executable)) { SetStatus("找不到网络后台，请从完整发行包启动。"); return; }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (_dataDirectory != null) { start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(_dataDirectory); }
        _lifetime.Token.ThrowIfCancellationRequested();
        _launchedBackend = Process.Start(start);
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(200, _lifetime.Token);
            try { using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); cts.CancelAfter(300); await IpcClient.SendAsync(new IpcRequest(), _dataDirectory, cts.Token); return; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { }
        }
    }
    private async Task<IpcResponse?> RequestAsync(IpcRequest request)
    {
        if (_exitInProgress) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var reply = await IpcClient.SendAsync(request, _dataDirectory, timeout.Token);
            if (!reply.Success) { SetStatus(reply.Error); return null; }
            return reply;
        }
        catch (OperationCanceledException) when (_exitInProgress) { return null; }
        catch (Exception ex) { SetStatus("后台连接失败：" + ex.Message); _hasBackend = false; UpdateConnectionUi(); if (!_sidebarCollapsed) ConnectionState.Text = "○  后台未连接"; return null; }
    }
    private async Task RefreshAsync()
    {
        if (_refreshing || _savingSettings) return;
        _refreshing = true;
        var settingsRevision = _settingsRevision;
        try
        {
            var reply = await RequestAsync(new IpcRequest());
            if (reply?.Snapshot == null || _savingSettings || settingsRevision != _settingsRevision) return;
            _snapshot = reply.Snapshot;
            _hasBackend = true;
            LocalName.Text = _snapshot.Settings.DeviceName;
            LocalName.ToolTip = LocalName.Text; CompactLocalDevice.ToolTip = "此设备 · " + LocalName.Text;
            UpdateConnectionUi();
            RenderPeerList();
            RefreshUpdateUi();
            if (_page == "transfers") RenderTransfers();
            TransfersNavLabel.Text = _snapshot.Pending.Count > 0 ? $"传输记录 · {_snapshot.Pending.Count}" : "传输记录";
            PendingNavBadge.Visibility = _snapshot.Pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateSend();
        }
        finally { _refreshing = false; }
    }
    private void ShowPage(string page)
    {
        if (page == "rules") { _settingsSection = "rules"; page = "settings"; }
        _page = page;
        DevicesPage.Visibility = page == "devices" ? Visibility.Visible : Visibility.Collapsed;
        TransfersPage.Visibility = page == "transfers" ? Visibility.Visible : Visibility.Collapsed;
        SettingsHost.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Text = page switch { "transfers" => "传输记录", "settings" => "设置", _ => "发送" };
        PageSubtitle.Text = page switch { "transfers" => "查看发送、接收与待确认请求", "settings" => "按你的习惯使用邻间", _ => "选择内容，再选择接收设备" };
        foreach (var item in new[] { (DevicesNav, "devices"), (TransfersNav, "transfers"), (SettingsNav, "settings") })
        {
            item.Item1.Background = item.Item2 == page ? Brush("#E2E8F2") : Brushes.Transparent;
            item.Item1.FontWeight = item.Item2 == page ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (page == "transfers") RenderTransfers();
        if (page == "settings") RenderSettingsSection();
    }
    private void DevicesClick(object sender, RoutedEventArgs e) => ShowPage("devices");
    private void TransfersClick(object sender, RoutedEventArgs e) => ShowPage("transfers");
    private void SettingsClick(object sender, RoutedEventArgs e) => ShowPage("settings");
    private async void DiscoverClick(object sender, RoutedEventArgs e)
    {
        RefreshPeersButton.IsEnabled = false;
        try
        {
            if (await RequestAsync(new IpcRequest { Command = "discover" }) != null)
            { await RefreshAsync(); SetStatus("正在查找设备；上线后会自动出现。"); }
        }
        finally { UpdateConnectionUi(); }
    }
    private void PeerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_rebindingPeers)
        {
            foreach (PeerView item in e.RemovedItems) _selectedPeerIds.Remove(item.Peer.Fingerprint);
            foreach (PeerView item in e.AddedItems) _selectedPeerIds.Add(item.Peer.Fingerprint);
        }
        ManagePeerButton.IsEnabled = SelectedPeer != null;
        UpdateSend();
    }
    private string? _lastAddDirectory;
    private void AddItemsClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var menu = new ContextMenu { PlacementTarget = sender as FrameworkElement ?? AddItemsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var files = new MenuItem { Header = "文件…" };
        var folders = new MenuItem { Header = "文件夹…" };
        System.Windows.Automation.AutomationProperties.SetAutomationId(files, "AddFilesMenuItem");
        System.Windows.Automation.AutomationProperties.SetAutomationId(folders, "AddFoldersMenuItem");
        files.Click += (_, _) => AddPickedPaths(NativeItemPicker.ShowFiles(this, _lastAddDirectory));
        folders.Click += (_, _) => AddPickedPaths(NativeItemPicker.ShowFolders(this, _lastAddDirectory));
        menu.Items.Add(files); menu.Items.Add(folders); menu.IsOpen = true;
    }
    private void EmptyFilesActionClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_paths.Count == 0) AddItemsClick(sender, e);
        else FileSearch.Clear();
    }
    private void AddPickedPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        _lastAddDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(paths[0]));
        AddPaths(paths);
    }
    private void FileSelected(object sender, SelectionChangedEventArgs e) => UpdateSend();
    private void ExcludeClick(object sender, RoutedEventArgs e)
    {
        if (_busy || FileList.SelectedItem is not SelectedPath { IsDirectory: true } item) return;
        var dialog = new ExclusionsDialog(this, item.Path, item.ExcludedPaths);
        if (dialog.ShowDialog() != true || item.ExcludedPaths.SequenceEqual(dialog.ExcludedPaths)) return;
        item.SetExclusions(dialog.ExcludedPaths);
        SummaryBox.Clear(); StartFolderSize(item);
    }
    private void FilesDropped(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) { AddPaths(paths); ShowPage("devices"); }
    }
    private void AddPaths(IEnumerable<string> paths)
    {
        if (_busy) { SetStatus("操作进行中，请等待后再更改文件。"); return; }
        bool added = false;
        foreach (var path in paths)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.StartsWith("//", StringComparison.Ordinal))
                    throw new InvalidDataException("请选择本机文件或文件夹。");
                string root = Path.GetPathRoot(full)!;
                if (new DriveInfo(root).DriveType == DriveType.Network) throw new InvalidDataException("不支持映射的网络磁盘。");
                if (Path.TrimEndingDirectorySeparator(full).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("请选择磁盘内的文件或文件夹。");
                PathSafety.ValidateReceiveRoot(Path.GetDirectoryName(full)!);
                if (_paths.Any(p => p.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) continue;
                var attributes = File.GetAttributes(full);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("不支持链接或重解析点。");
                bool directory = (attributes & FileAttributes.Directory) != 0;
                var item = new SelectedPath(full, directory, directory ? "正在统计…" : Size(new FileInfo(full).Length), _lifetime.Token);
                _paths.Add(item); added = true;
                if (directory) StartFolderSize(item);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { SetStatus("未添加：" + ex.Message); }
        }
        if (added) { SummaryBox.Clear(); RenderPaths(); }
    }
    private void RemoveFileClick(object sender, RoutedEventArgs e)
    {
        if (_busy) { SetStatus("操作进行中，请等待后再更改文件。"); return; }
        foreach (var selected in FileList.SelectedItems.Cast<SelectedPath>().ToArray())
        { _paths.Remove(selected); selected.Dispose(); }
        SummaryBox.Clear(); RenderPaths();
    }
    private void RenderPaths()
    {
        if (FileList == null) return;
        foreach (var item in _paths) item.IsCompact = _paths.Count > 8;
        var selected = FileList.SelectedItems.Cast<SelectedPath>().ToArray();
        var query = FileSearch.Text.Trim();
        var visible = _paths.Where(p => query.Length == 0 || p.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        FileList.ItemsSource = visible;
        foreach (var item in selected.Where(visible.Contains)) FileList.SelectedItems.Add(item);
        FileSearchRow.Visibility = _paths.Count >= 8 || query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyFiles.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyFilesTitle.Text = _paths.Count == 0 ? "拖入文件或文件夹" : "没有匹配的项目";
        EmptyFilesHint.Text = _paths.Count == 0 ? "也可以点击此处添加" : "点击此处清除筛选，查看全部";
        EmptyFilesIcon.Text = _paths.Count == 0 ? "↗" : "↺";
        var emptyAction = _paths.Count == 0 ? "添加文件或文件夹" : "清除文件筛选";
        EmptyFilesActionButton.ToolTip = emptyAction;
        System.Windows.Automation.AutomationProperties.SetName(EmptyFilesActionButton, emptyAction);
        FileCount.Text = query.Length > 0 ? $"{visible.Count:N0} / {_paths.Count:N0} 项" : $"{_paths.Count:N0} 项";

        FileSelectionInfo.Text = _paths.Count == 0 ? "支持文件与文件夹混合发送" : $"{_paths.Count(p => !p.IsDirectory):N0} 个文件 · {_paths.Count(p => p.IsDirectory):N0} 个文件夹";
        UpdateSend();
    }
    private void UpdateSend()
    {
        if (SendButton == null) return;
        AddItemsButton.IsEnabled = !_busy;
        AddItemsButton.Visibility = _paths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyFilesActionButton.IsEnabled = !_busy;
        RemoveItemsButton.IsEnabled = !_busy && FileList.SelectedItems.Count > 0;
        ClearItemsButton.IsEnabled = !_busy && _paths.Count > 0;
        ExpandFilesButton.IsEnabled = _paths.Count > 0;
        ExpandFilesButton.Visibility = _paths.Count > 6 ? Visibility.Visible : Visibility.Collapsed;
        RemoveItemsButton.Visibility = FileList.SelectedItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearItemsButton.Visibility = _paths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoteExpander.Visibility = _paths.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummarizeButton.IsEnabled = !_busy && _paths.Count > 0;
        ExcludeItemsButton.Visibility = FileList.SelectedItems.Count == 1 && FileList.SelectedItem is SelectedPath { IsDirectory: true } ? Visibility.Visible : Visibility.Collapsed;
        ExcludeItemsButton.IsEnabled = !_busy && FileList.SelectedItems.Count == 1 && FileList.SelectedItem is SelectedPath { IsDirectory: true };
        FileToolsRow.Visibility = RemoveItemsButton.Visibility == Visibility.Visible || ExcludeItemsButton.Visibility == Visibility.Visible || ExpandFilesButton.Visibility == Visibility.Visible || FileSearchRow.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        SummarizeButton.Visibility = _snapshot.Settings.Integrations.Any(p => p.Enabled) ? Visibility.Visible : Visibility.Collapsed;
        var peers = SelectedRecipients();
        SendButton.IsEnabled = !_busy && _hasBackend && _snapshot.Settings.LanOnline && _paths.Count > 0 && peers.Count > 0 && peers.All(p => p.ReceivingEnabled && p.LanOnline);
        SendButton.Content = peers.Count switch { 0 => "选择设备后发送", 1 => "发送至 " + PeerName(peers[0]), _ => $"发送至 {peers.Count} 台设备" };
        var hidden = peers.Count(p => PeerList.Items.Cast<PeerView>().All(view => view.Peer.Fingerprint != p.Fingerprint));
        RecipientHint.Text = peers.Any(p => !p.ReceivingEnabled) ? "有设备已暂停接收" : hidden > 0 ? $"已选 {peers.Count} 台 · {hidden} 台已隐藏" : "";
        RecipientHint.Visibility = RecipientHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecipientNames.Text = string.Join("、", peers.Select(PeerName));
        RecipientNames.ToolTip = RecipientNames.Text;
        RecipientNames.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearPeersButton.Visibility = peers.Count > 1 || hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void SendClick(object sender, RoutedEventArgs e)
    {
        var peers = SelectedRecipients();
        if (_busy || peers.Count == 0 || _paths.Count == 0) return;
        _busy = true; UpdateSend();
        try
        {
            var reply = await RequestAsync(new IpcRequest { Command = peers.Count == 1 ? "send" : "send-many", Peer = peers.Count == 1 ? peers[0] : null, Peers = peers.Count > 1 ? peers : [], Sources = _paths.Select(p => p.ToSource()).ToList(), Summary = SummaryBox.Text.Trim() });
            if (reply != null)
            {
                ClearSelectedPaths(); SummaryBox.Clear(); FileSearch.Clear(); RenderPaths();
                await RefreshAsync(); ShowPage("transfers"); SetStatus(peers.Count == 1 ? "发送请求已提交。" : $"已为 {peers.Count} 台设备创建发送任务。");
            }
        }
        finally { _busy = false; UpdateSend(); }
    }
    private async void SummarizeClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_paths.Count == 0) { SetStatus("请先选择文件或文件夹。"); return; }
        var profiles = _snapshot.Settings.Integrations.Where(p => p.Enabled).ToList();
        if (profiles.Count == 0) { SetStatus("请在“设置 → 扩展”中启用 CLI，也可以直接填写发送说明。"); return; }
        var chooser = new EditorDialog(this, "生成内容摘要");
        chooser.AddNote("向 CLI 提供发送清单；已授权的 CLI 可读取文件。");
        var selection = chooser.AddChoice("使用 CLI", profiles.Select(p => p.Name).ToArray(), 0);
        if (chooser.ShowDialog() != true) return;
        var selectedSources = _paths.Select(p => p.ToSource()).ToArray();
        _busy = true; UpdateSend(); SetStatus("正在运行本机 CLI…");
        _summaryTask = GenerateSummaryAsync(profiles[selection.SelectedIndex], selectedSources);
        await _summaryTask;
    }
    private async Task GenerateSummaryAsync(IntegrationProfile profile, TransferSource[] selectedSources)
    {
        try
        {
            var manifest = await Task.Run(() => BuildSummaryManifest(selectedSources), _lifetime.Token);
            var result = await IntegrationRunner.RunAsync(profile, manifest.Offer, manifest.LocalPaths, _lifetime.Token);
            if (result.Success) { SummaryBox.Text = result.Output; NoteExpander.IsExpanded = true; SetStatus("摘要已生成，请检查后发送。"); } else SetStatus("CLI 未完成：" + result.Error);
        }
        catch (Exception ex) { SetStatus(ex.Message); }
        finally { _busy = false; UpdateSend(); }
    }
    private (TransferOffer Offer, Dictionary<string, string> LocalPaths) BuildSummaryManifest(TransferSource[] sources)
    {
        var plan = ManifestBuilder.BuildPlan(sources, _snapshot.Settings, _lifetime.Token);
        plan.Offer.SenderFingerprint = _snapshot.Fingerprint;
        return (plan.Offer, plan.LocalFiles.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
    }
    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        var dialog = new EditorDialog(this, "通过 IP 连接");
        dialog.AddNote("连接后请核对设备指纹。");
        var address = dialog.AddText("对方 IP 地址", ""); var port = dialog.AddText("端口", "45831");
        if (dialog.ShowDialog() != true) return;
        if (!int.TryParse(port.Text, out var number) || number is < 1024 or > 65535) { SetStatus("请输入有效端口。"); return; }
        if (await RequestAsync(new IpcRequest { Command = "connect", Address = address.Text.Trim(), Port = number }) != null) await RefreshAsync();
    }
    private async void ManagePeerClick(object sender, RoutedEventArgs e) { if (SelectedPeer != null) await EditDeviceAsync(SelectedPeer.Peer); }
    private async Task EditDeviceAsync(PeerInfo peer)
    {
        peer = _snapshot.Peers.FirstOrDefault(p => p.Fingerprint == peer.Fingerprint) ?? peer;
        var known = _snapshot.Settings.Devices.FirstOrDefault(d => d.Fingerprint == peer.Fingerprint)
            ?? new KnownDevice { Fingerprint = peer.Fingerprint, Name = peer.Name, LastAddress = peer.Address, Port = peer.Port };
        var dialog = new EditorDialog(this, "设备、分组与信任");
        dialog.AddNote($"对方名称：{peer.Name}\n地址：{peer.Address}\n\n指纹：{peer.Fingerprint}\n\n首次信任前，请在另一台设备的设置页核对完整指纹。");
        dialog.AddNote($"系统：{peer.Platform} · 版本：{(peer.AppVersion.Length > 0 ? "v" + peer.AppVersion : "未报告")}");
        var availability = new PeerView(peer, known).Availability;
        if (availability.Length > 0) dialog.AddNote(availability);
        var alias = dialog.AddText("本机备注", known.Alias);
        var group = dialog.AddText("我的分组备注（仅本机可见，留空自动）", known.GroupName);
        group.MaxLength = DeviceGroups.MaximumGroupNameLength;
        dialog.AddNote($"当前分组：{DeviceGroups.GetPeerGroup(_snapshot.Settings, peer)}。留空时采用对方设置的组名；对方未设置时，按其设备名称前缀分组。此设置只影响你的设备列表。");
        var trusted = dialog.AddCheck("已核对指纹，信任此设备", known.Trusted);
        var auto = dialog.AddCheck("允许此设备按匹配规则自动接收", known.AutoAccept);
        if (dialog.ShowDialog() != true) return;
        var settings = CloneSettings();
        known = settings.Devices.FirstOrDefault(d => d.Fingerprint == peer.Fingerprint);
        if (known == null)
        {
            known = new KnownDevice { Fingerprint = peer.Fingerprint, Name = peer.Name, LastAddress = peer.Address, Port = peer.Port };
            settings.Devices.Add(known);
        }
        known.Name = peer.Name; known.Alias = alias.Text.Trim(); known.GroupName = group.Text.Trim();
        known.Trusted = trusted.IsChecked == true; known.AutoAccept = known.Trusted && auto.IsChecked == true;
        await SaveSettingsAsync(settings);
    }
    private sealed record PeerView(PeerInfo Peer, KnownDevice? Known, string UpdateLabel = "", string GroupName = "")
    {
        public string AutomationLabel => DisplayName + " · 分组：" + GroupName + " · " + Description + (Peer.AppVersion.Length > 0 ? " · v" + Peer.AppVersion : "") + (UpdateLabel.Length > 0 ? " · " + UpdateLabel : "");
        public string UpdateHint => $"可更新至 v{Peer.UpdateVersion}，点击查看；当前运行 v{Peer.AppVersion}。";
        public string DisplayName => Known is { } known && !string.IsNullOrWhiteSpace(known.Alias) ? known.Alias : Peer.Name;
        public string PlatformLabel => Peer.Platform.Equals("windows", StringComparison.OrdinalIgnoreCase) ? "Windows" : Peer.Platform;
        public string Availability => Peer.DepartureHint ? "可能已离开" : !Peer.LanOnline ? "已离线" : !Peer.ReceivingEnabled ? "已暂停接收" : "";
        public bool CanOpenUpdate => UpdateLabel.Length > 0;
        public string VersionLabel => CanOpenUpdate ? UpdateLabel : Peer.AppVersion.Length > 0 ? "v" + Peer.AppVersion : "";
        public string PlatformIcon => Peer.Platform.ToLowerInvariant() switch
        {
            "windows" => "M0,0 H6 V6 H0 Z M8,0 H14 V6 H8 Z M0,8 H6 V14 H0 Z M8,8 H14 V14 H8 Z",
            "macos" or "mac" or "osx" => "M3,1 H13 V10 H3 Z M1,11 H15 L16,13 H0 Z",
            _ => "M1,1 H15 V11 H1 Z M6,12 H10 V14 H6 Z M3,4 L6,6 L3,8 L4,8 L7,6 L4,4 Z M8,8 H12 V9 H8 Z"
        };
        public string Description => string.Join(" · ", new[] { Availability, PlatformLabel, Known?.Trusted == true ? "已信任" : "" }.Where(value => value.Length > 0));
        public string Details => DisplayName + "\n分组：" + GroupName + "\n" + Peer.Address + "\n" + Description +
            (Peer.AppVersion.Length > 0 ? "\n版本：v" + Peer.AppVersion : "");
    }
}







