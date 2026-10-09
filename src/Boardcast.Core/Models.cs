using System.Text.Json;
using System.Text.Json.Serialization;
namespace Boardcast.Core;

public static class BoardcastJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { WriteIndented = false, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
}
public sealed record AppSettings
{
    public int Version { get; set; } = 1;
    public string DeviceName { get; set; } = Environment.MachineName;
    // Empty means infer the group from the device name.
    public string GroupName { get; set; } = "";
    public DeviceGroupFilter PeerGroupFilter { get; set; } = DeviceGroupFilter.All;
    public List<string> VisibleGroups { get; set; } = [];
    public string ReceiveDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Boardcast");
    public int Port { get; set; } = 45831;
    public bool ReceivingEnabled { get; set; } = true;
    public bool LanOnline { get; set; } = true;
    // Retained for old configuration readers; downloads require an explicit version choice.
    public bool AutoDownloadUpdates { get; set; }
    public long MaxTransferBytes { get; set; } = 100L * 1024 * 1024 * 1024;
    public int MaxEntries { get; set; } = 50000;
    public List<KnownDevice> Devices { get; set; } = [];
    public List<ReceiveRule> Rules { get; set; } = [];
    public List<IntegrationProfile> Integrations { get; set; } = [];
}
public sealed record KnownDevice
{
    public string Fingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public string Alias { get; set; } = "";
    // A private override on this computer; it is never advertised on the network.
    public string GroupName { get; set; } = "";
    public bool Trusted { get; set; }
    public bool AutoAccept { get; set; }
    public string LastAddress { get; set; } = "";
    public int Port { get; set; } = 45831;
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias;
}
public sealed record PeerInfo
{
    public string Fingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 45831;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    public bool ReceivingEnabled { get; set; } = true;
    public bool LanOnline { get; set; } = true;
    public bool DepartureHint { get; set; }
    public string AppVersion { get; set; } = "";
    public string Runtime { get; set; } = "";
    public int UpdatePort { get; set; }
    public string UpdateVersion { get; set; } = "";
}
public sealed record TransferSource
{
    public string Path { get; set; } = "";
    public List<string> ExcludedPaths { get; set; } = [];
}
public sealed record TransferEntry
{
    public string RelativePath { get; set; } = "";
    public long Size { get; set; }
    public bool IsDirectory { get; set; }
}
public sealed record TransferOffer
{
    public string BatchId { get; set; } = "";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SenderName { get; set; } = "";
    public string SenderFingerprint { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<TransferEntry> Entries { get; set; } = [];
    public long TotalBytes => Entries.Sum(e => e.Size);
}
public enum EntryScope { Any, Files, Folders }
public sealed record ReceiveRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新规则";
    public bool Enabled { get; set; } = true;
    public string DeviceFingerprint { get; set; } = "";
    public EntryScope Scope { get; set; } = EntryScope.Any;
    public List<string> Extensions { get; set; } = [];
    public string FolderNamePattern { get; set; } = "";
    public long MaxBytes { get; set; }
    public string Destination { get; set; } = "";
    public bool AutoAccept { get; set; }
}
public sealed record ReceiveDecision(bool AutoAccept, string Destination, string Reason, string? RuleId = null);
public sealed record IntegrationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "内容摘要";
    public bool Enabled { get; set; }
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 30;
    public bool IncludeLocalPaths { get; set; }
}
public sealed record IntegrationResult(bool Success, string Output, string Error);
public sealed record PendingTransfer
{
    public TransferOffer Offer { get; set; } = new();
    public string Address { get; set; } = "";
    public string Destination { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record TransferProgress
{
    public string Id { get; set; } = "";
    public string Direction { get; set; } = "send";
    public string PeerName { get; set; } = "";
    public string State { get; set; } = "preparing";
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public string CurrentFile { get; set; } = "";
    public string Message { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool CanResume { get; set; }
    public bool CanDiscard { get; set; }
    public int CompletedFiles { get; set; }
    public long ReusedBytes { get; set; }
    public long PeerBytes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record DaemonSnapshot
{
    public string AppVersion { get; set; } = AppRelease.CurrentVersion;
    public PeerUpdateStatus Update { get; set; } = new();
    public string Fingerprint { get; set; } = "";
    public AppSettings Settings { get; set; } = new();
    public List<PeerInfo> Peers { get; set; } = [];
    public List<PendingTransfer> Pending { get; set; } = [];
    public List<TransferProgress> Transfers { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
}
public sealed record IpcRequest
{
    public string Command { get; set; } = "status";
    public AppSettings? Settings { get; set; }
    public PeerInfo? Peer { get; set; }
    public List<PeerInfo> Peers { get; set; } = [];
    public bool LanOnline { get; set; } = true;
    public int ProcessId { get; set; }
    public List<string> Paths { get; set; } = [];
    public List<TransferSource> Sources { get; set; } = [];
    public string TransferId { get; set; } = "";
    public bool Accept { get; set; }
    public string Destination { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; } = 45831;
}
public sealed record IpcResponse
{
    public bool Success { get; set; } = true;
    public string Error { get; set; } = "";
    public string TransferId { get; set; } = "";
    public List<TransferTicket> Transfers { get; set; } = [];
    public DaemonSnapshot? Snapshot { get; set; }
}


public sealed record TransferTicket
{
    public string Id { get; set; } = "";
    public string PeerFingerprint { get; set; } = "";
    public string PeerName { get; set; } = "";
}
