namespace Boardcast.Core;

public enum DeviceGroupFilter { All, SameGroup, SelectedGroups }

public static class DeviceGroups
{
    public const int MaximumGroupNameLength = 80;
    public const int MaximumVisibleGroupNameLength = 256;
    public const int MaximumVisibleGroups = 128;
    public const string Ungrouped = "未分组";

    public static string Resolve(string name, string? groupName)
    {
        if (!string.IsNullOrWhiteSpace(groupName)) return groupName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return Ungrouped;
        var start = 0;
        while (start < name.Length && IsSeparator(name[start])) start++;
        if (start == name.Length) return Ungrouped;
        var end = start;
        while (end < name.Length && !IsSeparator(name[end])) end++;
        return name[start..end];
    }

    public static string GetLocalGroup(AppSettings settings) => Resolve(settings.DeviceName, settings.GroupName);

    public static string GetPeerGroup(AppSettings settings, PeerInfo peer)
    {
        var known = settings.Devices.FirstOrDefault(device =>
            string.Equals(device.Fingerprint, peer.Fingerprint, StringComparison.OrdinalIgnoreCase));
        return Resolve(peer.Name, !string.IsNullOrWhiteSpace(known?.GroupName) ? known.GroupName : peer.GroupName);
    }

    public static bool IsVisible(AppSettings settings, PeerInfo peer) => settings.PeerGroupFilter switch
    {
        DeviceGroupFilter.All => true,
        DeviceGroupFilter.SameGroup => string.Equals(GetLocalGroup(settings), GetPeerGroup(settings, peer), StringComparison.OrdinalIgnoreCase),
        DeviceGroupFilter.SelectedGroups => settings.VisibleGroups.Any(group =>
            string.Equals(group.Trim(), GetPeerGroup(settings, peer), StringComparison.OrdinalIgnoreCase)),
        _ => false
    };

    internal static bool IsValidGroupName(string? groupName, int maximum = MaximumGroupNameLength) =>
        groupName is not null && groupName.Length <= maximum && !groupName.Any(char.IsControl);

    private static bool IsSeparator(char value) => value is '-' or '_' or '.' || char.IsWhiteSpace(value);
}
