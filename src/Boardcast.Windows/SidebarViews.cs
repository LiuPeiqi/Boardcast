using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Boardcast.Core;

namespace Boardcast.Windows;
public partial class MainWindow
{
    private bool _sidebarCollapsed;
    private void ToggleSidebarClick(object sender, RoutedEventArgs e)
    {
        SetSidebarCollapsed(!_sidebarCollapsed);
        if (!_previewMode)
            try { UiPreferences.Save(_dataDirectory, new UiPreferences { SidebarCollapsed = _sidebarCollapsed }); }
            catch (Exception ex) { SetStatus("导航偏好未保存：" + ex.Message); }
    }
    private void SetSidebarCollapsed(bool collapsed)
    {
        _sidebarCollapsed = collapsed;
        SidebarColumn.Width = new GridLength(collapsed ? 64 : 176);
        foreach (var label in new[] { SendNavLabel, TransfersNavLabel, SettingsNavLabel })
            label.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { DevicesNav, TransfersNav, SettingsNav, ToggleSidebarButton })
        {
            button.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            button.Padding = collapsed ? new Thickness(12) : new Thickness(14, 11, 12, 11);
        }
        ExpandedLocalDevice.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CompactLocalDevice.Visibility = Visibility.Collapsed;
        ConnectionButton.HorizontalContentAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        UpdateConnectionUi();
        ToggleSidebarButton.ToolTip = collapsed ? "展开导航" : "收起导航";
        System.Windows.Automation.AutomationProperties.SetName(ToggleSidebarButton, collapsed ? "展开导航" : "收起导航");
    }
    private void ClearPeersClick(object sender, RoutedEventArgs e)
    {
        _selectedPeerIds.Clear(); _rebindingPeers = true;
        try { PeerList.UnselectAll(); } finally { _rebindingPeers = false; }
        UpdateSend();
    }
}
internal sealed record UiPreferences
{
    public bool SidebarCollapsed { get; init; } = true;
    private static string DirectoryPath(string? directory) => directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boardcast");
    public static UiPreferences Load(string? directory)
    {
        try
        {
            var path = PathSafety.ResolveUnderRoot(DirectoryPath(directory), "ui-preferences.json");
            return File.Exists(path) && new FileInfo(path).Length < 4096 ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllBytes(path), BoardcastJson.Options) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }
    public static void Save(string? directory, UiPreferences preferences)
    {
        var root = PathSafety.ValidateReceiveRoot(DirectoryPath(directory));
        Directory.CreateDirectory(root);
        var path = PathSafety.ResolveUnderRoot(root, "ui-preferences.json");
        var temporary = PathSafety.ResolveUnderRoot(root, "ui-preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = PrivateFile.Create(temporary)) file.Write(JsonSerializer.SerializeToUtf8Bytes(preferences, BoardcastJson.Options));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}


