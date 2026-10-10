using System.Text.Json;
namespace Boardcast.Core;

public sealed class SettingsStore
{
    private const int MaximumSettingsBytes = 4 * 1024 * 1024;
    public string DataDirectory { get; }
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public SettingsStore(string? dataDirectory = null)
    {
        DataDirectory = Path.GetFullPath(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Boardcast"));
        Directory.CreateDirectory(DataDirectory);
    }

    public AppSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new AppSettings();
        if (new FileInfo(SettingsPath).Length > MaximumSettingsBytes) throw new InvalidDataException("设置文件超过 4 MiB。");
        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), BoardcastJson.Options)
                ?? throw new InvalidDataException("设置文件为空。");
        }
        catch (JsonException ex) { throw new InvalidDataException("设置文件不是有效的 Boardcast JSON 配置。", ex); }
        Validate(settings);
        return settings;
    }

    public void Save(AppSettings settings)
    {
        Validate(settings);
        var json = JsonSerializer.Serialize(settings, BoardcastJson.Options);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumSettingsBytes) throw new InvalidDataException("设置文件超过 4 MiB。");
        var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, SettingsPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Validate(AppSettings settings)
    {
        if (settings is null) throw new InvalidDataException("设置不能为空。");
        settings.AutoDownloadUpdates = false;
        settings.DiscoverySubnets = DiscoverySubnets.Normalize(settings.DiscoverySubnets);
        if (settings.Version != 1) throw new InvalidDataException("不支持此配置版本；当前版本为 1。");
        if (!ValidText(settings.DeviceName, 80, required: true)) throw new InvalidDataException("设备名需要 1–80 个字符，且不能包含控制字符。");
        if (!DeviceGroups.IsValidGroupName(settings.GroupName)) throw new InvalidDataException("分组名不能为 null、超过 80 个字符或包含控制字符。");
        if (!Enum.IsDefined(settings.PeerGroupFilter)) throw new InvalidDataException("附近设备分组筛选方式无效。");
        if (settings.VisibleGroups is null || settings.VisibleGroups.Count > DeviceGroups.MaximumVisibleGroups ||
            settings.VisibleGroups.Any(group => !DeviceGroups.IsValidGroupName(group, DeviceGroups.MaximumVisibleGroupNameLength) || string.IsNullOrWhiteSpace(group)))
            throw new InvalidDataException("筛选分组最多 128 个，每个名称需要 1–256 个字符，且不能包含控制字符。");
        settings.GroupName = settings.GroupName.Trim();
        settings.VisibleGroups = settings.VisibleGroups.Select(group => group.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (settings.Port is < 1024 or > 65535) throw new InvalidDataException("端口必须在 1024–65535 之间。");
        if (settings.MaxTransferBytes <= 0 || settings.MaxEntries is < 1 or > 100000)
            throw new InvalidDataException("传输大小和文件数量限制无效。");
        if (settings.Devices is null || settings.Rules is null || settings.Integrations is null)
            throw new InvalidDataException("设备、规则和扩展列表不能为 null。");
        if (settings.Devices.Count > 1000 || settings.Rules.Count > 200 || settings.Integrations.Count > 50)
            throw new InvalidDataException("设置条目过多。");
        ValidateRoot(settings.ReceiveDirectory);
        var fingerprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in settings.Devices)
        {
            if (device is null || !ValidFingerprint(device.Fingerprint) || !fingerprints.Add(device.Fingerprint))
                throw new InvalidDataException("已知设备证书指纹无效或重复。");
            if (!ValidText(device.Name, 256) || !ValidText(device.Alias, 80) || !ValidText(device.LastAddress, 256) || device.Port is < 1024 or > 65535)
                throw new InvalidDataException("已知设备名称、备注、地址或端口无效。");
            if (!DeviceGroups.IsValidGroupName(device.GroupName)) throw new InvalidDataException("设备分组名不能为 null、超过 80 个字符或包含控制字符。");
            device.GroupName = device.GroupName.Trim();
        }
        var ruleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in settings.Rules)
        {
            if (rule is null || !ValidText(rule.Id, 128, required: true) || !ruleIds.Add(rule.Id) || !ValidText(rule.Name, 80, required: true))
                throw new InvalidDataException("规则名称或标识无效，规则标识不能重复。");
            if (rule.DeviceFingerprint is null || (rule.DeviceFingerprint.Length > 0 && !ValidFingerprint(rule.DeviceFingerprint)))
                throw new InvalidDataException("规则的设备指纹无效。");
            if (!Enum.IsDefined(rule.Scope) || rule.MaxBytes < 0 || rule.Extensions is null || rule.Extensions.Count > 128)
                throw new InvalidDataException("规则类型、大小限制或扩展名列表无效。");
            if (!ValidText(rule.FolderNamePattern, 256) || rule.FolderNamePattern.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new InvalidDataException("文件夹匹配模式只匹配目录名，不能包含路径。");
            foreach (var extension in rule.Extensions)
            {
                if (string.IsNullOrWhiteSpace(extension) || extension.Length > 64)
                    throw new InvalidDataException("扩展名不能为空或超过 64 个字符。");
                var suffix = extension.Trim().TrimStart('.');
                if (suffix.Length == 0 || suffix.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '.' or '/' or '\\' or ':' or '*' or '?' or '<' or '>' or '"' or '|'))
                    throw new InvalidDataException("规则扩展名必须是最终文件后缀，不能含路径或通配符。");
            }
            if (rule.Destination is null) throw new InvalidDataException("规则接收目录不能为 null。");
            if (rule.Destination.Length > 0) ValidateRoot(rule.Destination);
        }
        var integrationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var integration in settings.Integrations)
        {
            if (integration is null || !ValidText(integration.Id, 128, required: true) || !integrationIds.Add(integration.Id) || !ValidText(integration.Name, 80, required: true))
                throw new InvalidDataException("扩展名称或标识无效，扩展标识不能重复。");
            if (integration.Executable is null || integration.Executable.Length > 2048 || integration.Executable.Contains('\0') ||
                (integration.Enabled && string.IsNullOrWhiteSpace(integration.Executable)) ||
                (integration.Executable.Length > 0 && (!Path.IsPathFullyQualified(integration.Executable) || integration.Executable.StartsWith("\\\\", StringComparison.Ordinal) || integration.Executable.StartsWith("//", StringComparison.Ordinal))))
                throw new InvalidDataException("扩展可执行文件必须使用本机绝对路径。");
            if (integration.Arguments is null || integration.Arguments.Count > 128 || integration.Arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')) ||
                integration.TimeoutSeconds is < 1 or > 300)
                throw new InvalidDataException("扩展参数或超时无效。");
        }
    }

    private static bool ValidText(string? text, int maximum, bool required = false) =>
        text is not null && text.Length <= maximum && !text.Any(char.IsControl) && (!required || !string.IsNullOrWhiteSpace(text));

    private static bool ValidFingerprint(string? fingerprint) => fingerprint is { Length: 64 } && fingerprint.All(Uri.IsHexDigit);

    private static void ValidateRoot(string root)
    {
        try { _ = PathSafety.ValidateReceiveRoot(root); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("接收目录不可用：" + ex.Message, ex); }
    }
}
