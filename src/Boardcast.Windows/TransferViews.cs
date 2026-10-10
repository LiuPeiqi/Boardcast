using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boardcast.Core;
using Microsoft.Win32;
namespace Boardcast.Windows;
public partial class MainWindow
{
    private void RenderTransfers()
    {
        TransferContent.Children.Clear();
        RenderPendingSubnets();
        foreach (var pending in _snapshot.Pending)
        {
            var content = new StackPanel();
            var alias = _snapshot.Settings.Devices.FirstOrDefault(d => d.Fingerprint == pending.Offer.SenderFingerprint)?.DisplayName ?? pending.Offer.SenderName;
            content.Children.Add(Heading($"来自 {alias} 的接收请求", 19));
            content.Children.Add(Note($"{pending.Offer.Entries.Count} 项 · {Size(pending.Offer.TotalBytes)}\n{pending.Reason}\n保存到：{pending.Destination}"));
            content.Children.Add(Note("指纹：" + pending.Offer.SenderFingerprint));
            if (pending.Offer.BatchId.Length > 0)
                content.Children.Add(Note("本次接收可由同批设备协助传输，也可能向同批已获授权的接收设备提供已校验的数据块。"));
            foreach (var entry in pending.Offer.Entries.Take(8))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                row.Children.Add(new Image { Source = FileItemIcon.Get(entry.RelativePath, entry.IsDirectory), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) });
                row.Children.Add(new TextBlock { Text = entry.RelativePath, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(row);
            }
            if (pending.Offer.Entries.Count > 8) content.Children.Add(Note($"另有 {pending.Offer.Entries.Count - 8} 项"));
            if (!string.IsNullOrWhiteSpace(pending.Offer.Summary)) content.Children.Add(Note("对方说明：\n" + pending.Offer.Summary));
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(Action("接收", async () => await DecideAsync(pending, true, pending.Destination), true));
            actions.Children.Add(Action("选择位置并接收", async () =>
            {
                var picker = new OpenFolderDialog { Title = "选择此次接收的位置" };
                if (picker.ShowDialog(this) == true) await DecideAsync(pending, true, picker.FolderName);
            }));
            actions.Children.Add(Action("拒绝", async () => await DecideAsync(pending, false, "")));
            content.Children.Add(actions); TransferContent.Children.Add(Card(content, "#FFF9EC"));
        }
        foreach (var transfer in _snapshot.Transfers.OrderByDescending(t => t.UpdatedAt).Take(60))
        {
            if (transfer.Kind == "subnets") { RenderSubnetTransfer(transfer); continue; }
            var content = new StackPanel();
            content.Children.Add(Heading($"{(transfer.Direction == "receive" ? "接收自" : "发送至")} {transfer.PeerName}", 18));
            content.Children.Add(Note($"{StateLabel(transfer.State)} · {Size(transfer.BytesTransferred)} / {Size(transfer.TotalBytes)}"));
            if (transfer.Direction == "receive" && transfer.CompletedFiles > 0)
                content.Children.Add(Note($"已保存 {transfer.CompletedFiles} 个文件"));
            content.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, transfer.TotalBytes), Value = transfer.BytesTransferred, Height = 5, Margin = new Thickness(0, 7, 0, 10), Foreground = Brush("#2F68D2") });
            if (transfer.CurrentFile.Length > 0) content.Children.Add(Note(transfer.CurrentFile));
            if (transfer.Message.Length > 0) content.Children.Add(Note(transfer.Message));
            if (transfer.ReusedBytes > 0 || transfer.PeerBytes > 0)
                content.Children.Add(Note($"已复用 {Size(transfer.ReusedBytes)} · P2P 接收 {Size(transfer.PeerBytes)}"));
            var actions = new WrapPanel();
            bool active = !new[] { "completed", "failed", "cancelled", "rejected", "paused" }.Contains(transfer.State);
            if (active && transfer.Direction == "send")
                actions.Children.Add(Action("暂停", async () => { await RequestAsync(new IpcRequest { Command = "pause", TransferId = transfer.Id }); await RefreshAsync(); }));
            if (transfer.Direction == "send" && transfer.CanResume)
                actions.Children.Add(Action("继续传输", async () => { await RequestAsync(new IpcRequest { Command = "resume", TransferId = transfer.Id }); await RefreshAsync(); }, true));
            if (active || transfer.Direction == "send" && transfer.CanResume || transfer.CanDiscard)
                actions.Children.Add(Action(transfer.CanDiscard && !active ? "清理未完成内容" : "取消", async () => { await RequestAsync(new IpcRequest { Command = "cancel", TransferId = transfer.Id }); await RefreshAsync(); }));
            if (transfer.Direction == "receive" && (transfer.State == "completed" || transfer.CompletedFiles > 0) && Directory.Exists(transfer.Destination))
                actions.Children.Add(Action("打开接收目录", () => { OpenFolder(transfer.Destination); return Task.CompletedTask; }));
            content.Children.Add(actions); TransferContent.Children.Add(Card(content));
        }
        if (_snapshot.Pending.Count == 0 && _snapshot.PendingSubnets.Count == 0 && _snapshot.Transfers.Count == 0)
        {
            TransferContent.Children.Add(Heading("暂无传输", 22));
        }
    }
    private async Task DecideAsync(PendingTransfer pending, bool accept, string destination)
    {
        await RequestAsync(new IpcRequest { Command = "decide", TransferId = pending.Offer.Id, Accept = accept, Destination = destination }); await RefreshAsync();
    }
    private System.Windows.Threading.DispatcherTimer? _statusNoticeTimer;
    private void SetStatus(string message)
    {
        if (_statusNoticeTimer == null)
        {
            _statusNoticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _statusNoticeTimer.Tick += (_, _) => HideStatusNotice();
            Closed += (_, _) => _statusNoticeTimer.Stop();
        }
        _statusNoticeTimer.Stop();
        StatusText.Text = message;
        StatusText.ToolTip = message;
        StatusNotice.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        if (StatusNotice.Visibility == Visibility.Visible) _statusNoticeTimer.Start();
    }
    private void HideStatusNotice()
    {
        _statusNoticeTimer?.Stop();
        StatusNotice.Visibility = Visibility.Collapsed;
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock Heading(string text, double size = 20) => new() { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 10) };
    private static TextBlock Note(string text) => new() { Text = text, Foreground = Brush("#626D7D"), Margin = new Thickness(0, 5, 0, 13), TextWrapping = TextWrapping.Wrap };
    private Border Card(UIElement child, string color = "#FFFFFF") => new() { Child = child, Padding = new Thickness(20, 4, 20, 18), Background = Brush(color), BorderBrush = Brush("#E2E6EC"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 12, 0, 0) };
    private Button Action(string label, Func<Task> callback, bool primary = false)
    {
        var button = new Button { Content = label }; if (primary) button.Style = (Style)FindResource("Primary");
        button.Click += async (_, _) => { button.IsEnabled = false; try { await callback(); } catch (Exception ex) { SetStatus(ex.Message); } finally { button.IsEnabled = true; } }; return button;
    }
    private static TextBox Field(Panel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 0) });
        var box = new TextBox { Text = value, MinWidth = 380 }; panel.Children.Add(box); return box;
    }
    private TextBox FolderField(Panel panel, string label, string value)
    {
        var box = Field(panel, label, value);
        panel.Children.Add(Action("选择文件夹", () => { var picker = new OpenFolderDialog(); if (picker.ShowDialog(this) == true) box.Text = picker.FolderName; return Task.CompletedTask; })); return box;
    }
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { path } });
    public static string Size(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.##} GiB" : bytes >= 1048576 ? $"{bytes / 1048576d:0.##} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KiB" : $"{bytes} B";
    private static string ScopeLabel(EntryScope scope) => scope switch { EntryScope.Files => "散文件", EntryScope.Folders => "文件夹", _ => "所有内容" };
    private static string StateLabel(string state) => state switch { "preparing" => "准备中", "queued" => "排队中", "waiting" or "pending" => "等待对方确认", "sending" => "正在发送", "receiving" => "正在接收", "paused" => "已暂停", "completed" => "已完成", "cancelled" => "已取消", "rejected" => "已拒绝", "failed" => "未完成", _ => state };
    private void RenderPreview(string path)
    {
        UpdateLayout();
        var visual = (FrameworkElement)Content;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path); encoder.Save(stream); Application.Current.Shutdown();
    }
}
