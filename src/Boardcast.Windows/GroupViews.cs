using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Boardcast.Core;

namespace Boardcast.Windows;

public partial class MainWindow
{
    private bool _savingSettings;
    private int _settingsRevision;

    private static string GroupFilterLabel(AppSettings settings) => settings.PeerGroupFilter switch
    {
        DeviceGroupFilter.SameGroup => "与我同组",
        DeviceGroupFilter.SelectedGroups => $"指定分组（{settings.VisibleGroups.Count}）",
        _ => "全部分组"
    };

    private void UpdateGroupFilterUi()
    {
        GroupFilterLabelText.Text = GroupFilterLabel(_snapshot.Settings) + "  ▾";
        GroupFilterButton.ToolTip = GroupFilterDescription();
        GroupFilterButton.IsEnabled = !_savingSettings && (_hasBackend || _previewMode);
        ShowAllPeersButton.IsEnabled = !_savingSettings && (_hasBackend || _previewMode);
        AutomationProperties.SetName(GroupFilterButton, "附近设备分组：" + GroupFilterLabel(_snapshot.Settings));
    }

    private string GroupFilterDescription() => _snapshot.Settings.PeerGroupFilter switch
    {
        DeviceGroupFilter.SameGroup => "只显示与我的分组相同的设备：" + DeviceGroups.GetLocalGroup(_snapshot.Settings),
        DeviceGroupFilter.SelectedGroups => _snapshot.Settings.VisibleGroups.Count == 0
            ? "尚未选择分组，当前不显示设备。点击选择分组或恢复全部。"
            : "只显示这些分组：" + string.Join("、", _snapshot.Settings.VisibleGroups),
        _ => "显示所有分组。此选择会保存，并在下次打开时沿用。"
    };

    private void GroupFilterClick(object sender, RoutedEventArgs e) => ShowGroupFilterMenu(GroupFilterButton);

    private void ShowGroupFilterMenu(FrameworkElement anchor, Action? changed = null)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var all = new MenuItem { Header = "全部分组", IsCheckable = true, IsChecked = _snapshot.Settings.PeerGroupFilter == DeviceGroupFilter.All };
        var same = new MenuItem
        {
            Header = "与我同组", IsCheckable = true, IsChecked = _snapshot.Settings.PeerGroupFilter == DeviceGroupFilter.SameGroup,
            ToolTip = "我的分组：" + DeviceGroups.GetLocalGroup(_snapshot.Settings)
        };
        var selected = new MenuItem { Header = "指定分组…", IsCheckable = true, IsChecked = _snapshot.Settings.PeerGroupFilter == DeviceGroupFilter.SelectedGroups };
        all.Click += async (_, _) => { if (await SetGroupFilterAsync(DeviceGroupFilter.All)) changed?.Invoke(); };
        same.Click += async (_, _) => { if (await SetGroupFilterAsync(DeviceGroupFilter.SameGroup)) changed?.Invoke(); };
        selected.Click += async (_, _) => { if (await ChooseVisibleGroupsAsync()) changed?.Invoke(); };
        menu.Items.Add(all); menu.Items.Add(same); menu.Items.Add(selected);
        menu.IsOpen = true;
    }

    private async Task<bool> SetGroupFilterAsync(DeviceGroupFilter filter, List<string>? groups = null)
    {
        var settings = CloneSettings();
        settings.PeerGroupFilter = filter;
        if (groups != null) settings.VisibleGroups = groups;
        return await SaveSettingsAsync(settings);
    }

    private async Task<bool> ChooseVisibleGroupsAsync()
    {
        var groups = _snapshot.Peers.Select(p => DeviceGroups.GetPeerGroup(_snapshot.Settings, p))
            .Concat(_snapshot.Settings.Devices.Select(d => DeviceGroups.Resolve(d.Name, d.GroupName)))
            .Concat(_snapshot.Settings.VisibleGroups)
            .Append(DeviceGroups.GetLocalGroup(_snapshot.Settings))
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var dialog = new EditorDialog(this, "选择显示的分组") { Height = 530 };
        dialog.AddNote("可同时勾选多个分组，选择会保存。未勾选任何分组时不显示设备；可随时切回“全部分组”。");
        if (groups.Length == 0) dialog.AddNote("暂无可选分组。可以先在常规设置或设备详情中设置分组。");
        var choices = groups.Select(g => (Group: g, Box: dialog.AddCheck(g,
            _snapshot.Settings.VisibleGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))).ToArray();
        if (dialog.ShowDialog() != true) return false;
        return await SetGroupFilterAsync(DeviceGroupFilter.SelectedGroups,
            choices.Where(choice => choice.Box.IsChecked == true).Select(choice => choice.Group).ToList());
    }

    private async void ShowAllPeersClick(object sender, RoutedEventArgs e)
    {
        if (await SetGroupFilterAsync(DeviceGroupFilter.All))
        {
            PeerSearch.Clear();
            RenderPeerList();
            UpdateSend();
        }
    }

    private void AddGroupSettings(TextBox deviceName, TextBox groupName)
    {
        var effective = SettingText("", true);
        effective.FontSize = 12;
        effective.Margin = new Thickness(0, 7, 0, 0);
        void UpdateEffectiveGroup() => effective.Text = "当前分组：" + DeviceGroups.Resolve(deviceName.Text, groupName.Text);
        deviceName.TextChanged += (_, _) => UpdateEffectiveGroup();
        groupName.TextChanged += (_, _) => UpdateEffectiveGroup();
        UpdateEffectiveGroup();
        var input = new StackPanel();
        input.Children.Add(groupName); input.Children.Add(effective);
        var rule = SettingText("留空时按设备名称自动分组：以连字符（-）、下划线（_）、句点（.）或空白分隔，取第一个非空段；没有分隔符时使用完整名称。例如 Design-PC 和 Design-Mac 都属于 Design。自定义组名会向附近设备公开。", true);
        rule.FontSize = 12;
        var filter = new Button { Content = GroupFilterLabel(_snapshot.Settings) + "  ▾", Style = (Style)FindResource("SettingButton") };
        AutomationProperties.SetName(filter, "附近设备默认显示分组");
        filter.Click += (_, _) => ShowGroupFilterMenu(filter, () => filter.Content = GroupFilterLabel(_snapshot.Settings) + "  ▾");
        SettingsContent.Children.Add(SettingGroup("分组与设备显示",
            SettingField("我的分组", input),
            rule,
            SettingRow(SettingDescription("默认显示", "立即保存；每次打开沿用此选择，也可在附近设备中切换。"), filter),
            SettingDescription("仅影响本机设备列表", "给对方设备设置自己的分组：打开设备详情，填写“我的分组备注”。分组不会改变信任或自动接收权限。")));
    }
}