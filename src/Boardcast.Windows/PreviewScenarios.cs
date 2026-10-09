using System.IO;
using System.Windows;
using System.Windows.Controls;
using Boardcast.Core;

namespace Boardcast.Windows;
public partial class MainWindow
{
    private void ConfigurePreview(string[] args)
    {
        string Option(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
        var scenario = Option("--preview-scenario", "empty");
        if (int.TryParse(Option("--preview-width", "1040"), out var width)) Width = Math.Max(MinWidth, width);
        if (int.TryParse(Option("--preview-height", "580"), out var height)) Height = Math.Max(MinHeight, height);
        SetSidebarCollapsed(!args.Contains("--preview-expanded-sidebar"));
        _hasBackend = true;
        _snapshot.Update = new PeerUpdateStatus { State = "disabled", Message = "此开发版本未配置受信任的发行更新包。" };
        _snapshot.Settings.DeviceName = "我的电脑";
        _snapshot.Settings.LanOnline = scenario != "offline";
        _snapshot.Fingerprint = new string('a', 64);
        _snapshot.Peers = scenario is "empty" or "offline" ? [] :
        [
            new() { Fingerprint = new string('b', 64), Name = "设计工作站", Address = "192.168.1.18", Platform = "windows", AppVersion = "0.3.0", Runtime = AppRelease.RuntimeIdentifier, UpdateVersion = "0.3.0", UpdatePort = 45832 },
            new() { Fingerprint = new string('c', 64), Name = "客厅 MacBook", Address = "192.168.1.26", Platform = "macOS", AppVersion = "0.2.0", Runtime = "osx-arm64" },
            new() { Fingerprint = new string('d', 64), Name = "书房电脑", Address = "192.168.1.32", Platform = "windows", AppVersion = "0.2.0", Runtime = AppRelease.RuntimeIdentifier }
        ];
        _snapshot.Settings.Devices = [new() { Fingerprint = new string('b', 64), Name = "设计工作站", Trusted = true }];
        if (scenario is "few" or "many" or "updates")
        {
            _paths.Add(new SelectedPath(@"C:\Design\品牌方案", true, "124.6 MiB · 48 个文件", _lifetime.Token));
            _paths.Add(new SelectedPath(@"C:\Design\使用说明.pdf", false, "2.4 MiB", _lifetime.Token));
            if (scenario == "many")
                for (var i = 1; i <= 1000; i++) _paths.Add(new SelectedPath($@"C:\Design\素材\设计素材-{i:0000}.png", false, $"{i * 7} KiB", _lifetime.Token));
            _selectedPeerIds.Add(new string('b', 64));
            if (scenario == "many") _selectedPeerIds.Add(new string('d', 64));
        }
        LocalName.Text = _snapshot.Settings.DeviceName;
        UpdateConnectionUi(); RenderPeerList(); RenderPaths();
        if (args.Contains("--preview-note")) { NoteExpander.IsExpanded = true; SummaryBox.Text = "这里是本次发送的补充说明。"; }
        if (scenario == "updates") { _settingsSection = "updates"; ShowPage("settings"); }
    }
}




