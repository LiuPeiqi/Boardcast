using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Boardcast.Core;
using Microsoft.Win32;
namespace Boardcast.Windows;
public partial class MainWindow
{
    private void RenderRules() => RenderRulesContent();
    private async Task EditRuleAsync(ReceiveRule? existing)
    {
        var rule = existing == null ? new ReceiveRule() : JsonSerializer.Deserialize<ReceiveRule>(JsonSerializer.Serialize(existing, BoardcastJson.Options), BoardcastJson.Options)!;
        var dialog = new EditorDialog(this, existing == null ? "新建接收规则" : "编辑接收规则");
        var name = dialog.AddText("规则名称", rule.Name); var enabled = dialog.AddCheck("启用", rule.Enabled);
        var deviceChoices = new List<(string Label, string Fingerprint)> { ("任何设备（自动接收仍要求信任）", "") };
        deviceChoices.AddRange(_snapshot.Settings.Devices.Select(d => (d.DisplayName, d.Fingerprint)));
        if (rule.DeviceFingerprint.Length > 0 && !deviceChoices.Any(d => d.Fingerprint == rule.DeviceFingerprint))
            deviceChoices.Add(("已移除设备 · " + rule.DeviceFingerprint[..Math.Min(12, rule.DeviceFingerprint.Length)], rule.DeviceFingerprint));
        var device = dialog.AddChoice("设备", deviceChoices.Select(d => d.Label).ToArray(), Math.Max(0, deviceChoices.FindIndex(d => d.Fingerprint == rule.DeviceFingerprint)));
        var scope = dialog.AddChoice("内容类型", ["文件和文件夹", "仅散文件", "仅文件夹"], (int)rule.Scope);
        var extensions = dialog.AddText("允许的后缀，用逗号分隔；留空表示全部", string.Join(", ", rule.Extensions));
        var pattern = dialog.AddText("顶层文件夹名称模式，如 Design-*；留空不限", rule.FolderNamePattern);
        var maxSize = dialog.AddText("总大小上限（MiB）；0 表示使用全局上限", (rule.MaxBytes / 1048576).ToString(CultureInfo.InvariantCulture));
        var destination = dialog.AddFolder("接收目录；留空使用默认目录", rule.Destination);
        var auto = dialog.AddCheck("满足规则时自动接收（仍须设备信任和许可）", rule.AutoAccept);
        if (dialog.ShowDialog() != true) return;
        if (!long.TryParse(maxSize.Text, out var size) || size < 0 || size > 100000000) { SetStatus("大小上限无效。"); return; }
        rule.Name = name.Text.Trim(); rule.Enabled = enabled.IsChecked == true;
        rule.DeviceFingerprint = deviceChoices[device.SelectedIndex].Fingerprint;
        rule.Scope = (EntryScope)scope.SelectedIndex;
        rule.Extensions = extensions.Text.Split([',', '，', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant()).ToList();
        rule.FolderNamePattern = pattern.Text.Trim(); rule.MaxBytes = size * 1048576;
        rule.Destination = destination.Text.Trim(); rule.AutoAccept = auto.IsChecked == true;
        var settings = CloneSettings(); var index = settings.Rules.FindIndex(r => r.Id == rule.Id);
        if (index >= 0) settings.Rules[index] = rule; else settings.Rules.Add(rule);
        if (await SaveSettingsAsync(settings)) RenderRules();
    }
    private void RenderSettings()
    {
        SettingsContent.Children.Clear();
        if (_settingsSection == "updates") { RenderUpdateSettings(); return; }
        if (_settingsSection == "known") { RenderKnownSettings(); return; }
        if (_settingsSection == "extensions") { RenderExtensionSettings(); return; }

        var name = SettingInput("设备名称", _snapshot.Settings.DeviceName);
        var groupName = SettingInput("我的分组", _snapshot.Settings.GroupName);
        groupName.MaxLength = DeviceGroups.MaximumGroupNameLength;
        groupName.ToolTip = "留空按设备名称前缀自动分组；点击“保存更改”后生效。";
        var directory = SettingInput("接收文件夹", _snapshot.Settings.ReceiveDirectory);
        directory.ToolTip = directory.Text;
        directory.TextChanged += (_, _) => directory.ToolTip = directory.Text;
        var browse = SettingAction("浏览…", () =>
        {
            var picker = new OpenFolderDialog();
            if (picker.ShowDialog(this) == true) directory.Text = picker.FolderName;
            return Task.CompletedTask;
        });
        System.Windows.Automation.AutomationProperties.SetName(browse, "选择接收文件夹");
        var receiving = SettingSwitch("允许接收新请求", _snapshot.Settings.ReceivingEnabled);
        var save = SettingAction("保存更改", async () =>
        {
            var settings = CloneSettings();
            settings.DeviceName = name.Text.Trim(); settings.GroupName = groupName.Text.Trim(); settings.ReceiveDirectory = directory.Text.Trim();
            settings.ReceivingEnabled = receiving.IsChecked == true;
            await SaveSettingsAsync(settings);
        }, primary: true);
        var localTitle = SettingText("本机与接收"); localTitle.FontWeight = FontWeights.SemiBold;
        var localHeader = SettingRow(localTitle, save); localHeader.Margin = new Thickness(2, 0, 2, 8);
        SettingsContent.Children.Add(localHeader);
        receiving.ToolTip = "保存更改后生效";
        SettingsContent.Children.Add(SettingGroup("",
            SettingField("设备名称", name),
            SettingField("接收文件夹", SettingRow(directory, browse)),
            SettingRow(SettingDescription("允许接收新请求"), receiving)));

        AddGroupSettings(name, groupName);

        var startup = SettingSwitch("登录 Windows 时启动到托盘", !_previewMode && StartupRegistration.IsEnabled());
        startup.Click += (_, _) =>
        {
            try { StartupRegistration.SetEnabled(startup.IsChecked == true); SetStatus("登录启动已更新。"); }
            catch (Exception ex) { startup.IsChecked = StartupRegistration.IsEnabled(); SetStatus(ex.Message); }
        };
        SettingsContent.Children.Add(SettingGroup("后台运行",
            SettingRow(SettingDescription("登录时启动", "自动启动到系统托盘"), startup),
            SettingRow(SettingDescription("关闭窗口后继续传输", "退出程序会停止后台"), SettingAction("退出程序", ExitApplicationAsync, quiet: true))));

        var fingerprint = SettingText(_snapshot.Fingerprint, true);
        fingerprint.FontSize = 11; fingerprint.FontFamily = new System.Windows.Media.FontFamily("Consolas");
        fingerprint.Margin = new Thickness(0, 4, 0, 0);
        var identity = SettingDescription("本机指纹"); identity.Children.Add(fingerprint);
        var copy = SettingAction("复制", () => { Clipboard.SetText(_snapshot.Fingerprint); SetStatus("指纹已复制。"); return Task.CompletedTask; }, quiet: true);
        System.Windows.Automation.AutomationProperties.SetName(copy, "复制完整本机指纹");
        var diagnosticContent = new StackPanel();
        diagnosticContent.Children.Add(SettingRow(identity, copy));
        var port = SettingText($"TCP / UDP 端口  {_snapshot.Settings.Port}", true); port.Margin = new Thickness(0, 12, 0, 0); diagnosticContent.Children.Add(port);
        foreach (var diagnostic in _snapshot.Diagnostics.TakeLast(8))
        {
            var line = SettingText(diagnostic, true); line.FontSize = 12; line.Margin = new Thickness(0, 6, 0, 0); diagnosticContent.Children.Add(line);
        }
        var diagnostics = new Expander
        {
            Header = "设备信息与网络诊断", FontSize = 13, Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
            Margin = new Thickness(2, 0, 2, 12), Content = diagnosticContent
        };
        diagnosticContent.Margin = new Thickness(12, 12, 0, 0);
        SettingsContent.Children.Add(diagnostics);
    }

    private void RenderKnownSettings()
    {
        var intro = SettingText("核对指纹后再信任设备；自动接收权限可在编辑中单独设置。", true);
        intro.Margin = new Thickness(2, 0, 0, 12); SettingsContent.Children.Add(intro);
        var rows = new List<UIElement>();
        foreach (var device in _snapshot.Settings.Devices)
        {
            var peer = _snapshot.Peers.FirstOrDefault(p => p.Fingerprint == device.Fingerprint)
                ?? new PeerInfo { Fingerprint = device.Fingerprint, Name = device.Name, Address = device.LastAddress, Port = device.Port };
            var detail = "分组：" + DeviceGroups.GetPeerGroup(_snapshot.Settings, peer) + " · " + (device.Trusted ? "已信任" : "每次确认") + (device.LastAddress.Length > 0 ? " · " + device.LastAddress : "");
            var description = SettingDescription(device.DisplayName, detail);
            var title = (TextBlock)description.Children[0]; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = device.DisplayName;
            var edit = SettingAction("编辑", async () =>
            {
                await EditDeviceAsync(new PeerInfo { Fingerprint = device.Fingerprint, Name = device.Name, Address = device.LastAddress, Port = device.Port }); RenderSettings();
            }, quiet: true);
            var forget = SettingAction("忘记", async () =>
            {
                var settings = CloneSettings(); settings.Devices.RemoveAll(d => d.Fingerprint == device.Fingerprint);
                if (await SaveSettingsAsync(settings)) RenderSettings();
            }, quiet: true);
            System.Windows.Automation.AutomationProperties.SetName(edit, "编辑设备 " + device.DisplayName);
            System.Windows.Automation.AutomationProperties.SetName(forget, "忘记设备 " + device.DisplayName);
            rows.Add(SettingRow(description, SettingActions(edit, forget)));
        }
        if (rows.Count == 0) rows.Add(SettingDescription("暂无已保存的设备", "在发送页选择设备，打开“设备详情”即可保存。"));
        SettingsContent.Children.Add(SettingGroup($"已保存设备 · {_snapshot.Settings.Devices.Count}", rows.ToArray()));
    }

    private void RenderExtensionSettings()
    {
        var add = SettingAction("添加工具", async () => await EditIntegrationAsync(null));
        var header = SettingRow(SettingDescription("内容摘要", "使用本机命令行工具生成发送说明，确认后再发送。"), add);
        header.Margin = new Thickness(2, 0, 2, 14); SettingsContent.Children.Add(header);
        var rows = new List<UIElement>();
        foreach (var integration in _snapshot.Settings.Integrations)
        {
            var description = SettingDescription(integration.Name, integration.Enabled ? "已启用" : "已停用");
            var title = (TextBlock)description.Children[0]; title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = integration.Name;
            var path = SettingText(integration.Executable, true); path.FontSize = 12; path.TextWrapping = TextWrapping.NoWrap;
            path.TextTrimming = TextTrimming.CharacterEllipsis; path.ToolTip = integration.Executable; path.Margin = new Thickness(0, 4, 0, 0); description.Children.Add(path);
            var edit = SettingAction("编辑", async () => await EditIntegrationAsync(integration), quiet: true);
            var remove = SettingAction("删除", async () =>
            {
                var settings = CloneSettings(); settings.Integrations.RemoveAll(p => p.Id == integration.Id);
                if (await SaveSettingsAsync(settings)) RenderSettings();
            }, quiet: true);
            System.Windows.Automation.AutomationProperties.SetName(edit, "编辑工具 " + integration.Name);
            System.Windows.Automation.AutomationProperties.SetName(remove, "删除工具 " + integration.Name);
            rows.Add(SettingRow(description, SettingActions(edit, remove)));
        }
        if (rows.Count == 0) rows.Add(SettingDescription("尚未添加工具", "添加已安装的 CLI 工具后，可在发送时手动生成摘要。"));
        SettingsContent.Children.Add(SettingGroup("", rows.ToArray()));
    }
    private async Task EditIntegrationAsync(IntegrationProfile? existing)
    {
        var profile = existing == null ? new IntegrationProfile() : JsonSerializer.Deserialize<IntegrationProfile>(JsonSerializer.Serialize(existing, BoardcastJson.Options), BoardcastJson.Options)!;
        var dialog = new EditorDialog(this, "配置 CLI");
        dialog.AddNote("手动运行，输出作为发送说明。");
        var name = dialog.AddText("名称", profile.Name);
        var executable = dialog.AddText("可执行文件的绝对路径", profile.Executable);
        var arguments = dialog.AddText("参数（每行一个）", string.Join("\n", profile.Arguments), true);
        var seconds = dialog.AddText("超时秒数（1–120）", profile.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        var localPaths = dialog.AddCheck("允许此 CLI 获取本机路径并读取所选文件正文", profile.IncludeLocalPaths);
        var enabled = dialog.AddCheck("启用此 CLI", profile.Enabled);
        if (dialog.ShowDialog() != true) return;
        if (!int.TryParse(seconds.Text, out var timeout) || timeout is < 1 or > 120) { SetStatus("超时须为 1–120 秒。"); return; }
        if (!Path.IsPathFullyQualified(executable.Text.Trim()) || !File.Exists(executable.Text.Trim())) { SetStatus("请选择存在的可执行文件绝对路径。"); return; }
        profile.Name = name.Text.Trim(); profile.Executable = executable.Text.Trim(); profile.Arguments = arguments.Text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(); profile.TimeoutSeconds = timeout; profile.Enabled = enabled.IsChecked == true; profile.IncludeLocalPaths = localPaths.IsChecked == true;
        var settings = CloneSettings(); var index = settings.Integrations.FindIndex(p => p.Id == profile.Id);
        if (index < 0) settings.Integrations.Add(profile); else settings.Integrations[index] = profile;
        if (await SaveSettingsAsync(settings)) RenderSettings();
    }
    private AppSettings CloneSettings() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_snapshot.Settings, BoardcastJson.Options), BoardcastJson.Options)!;
    private async Task<bool> SaveSettingsAsync(AppSettings settings)
    {
        try { SettingsStore.Validate(settings); } catch (Exception ex) { SetStatus(ex.Message); return false; }
        if (_savingSettings) { SetStatus("正在保存，请稍后再修改。"); return false; }
        _savingSettings = true;
        _settingsRevision++;
        UpdateGroupFilterUi();
        try
        {
            if (!_previewMode && await RequestAsync(new IpcRequest { Command = "settings", Settings = settings }) == null) return false;
            _snapshot.Settings = settings;
            LocalName.Text = settings.DeviceName;
            RenderPeerList();
            UpdateSend();
        }
        finally
        {
            _savingSettings = false;
            UpdateGroupFilterUi();
        }
        if (!_previewMode) await RefreshAsync();
        SetStatus("已保存。");
        return true;
    }
}

