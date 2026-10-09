using System.Text.Json;
using Boardcast.Core;

int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
const string current = "0.2.0", runtime = "win-x64";
Check(!new AppSettings().AutoDownloadUpdates, "Fresh settings require an explicit update selection");
Check(!JsonSerializer.Deserialize<AppSettings>("{\"version\":1}", BoardcastJson.Options)!.AutoDownloadUpdates,
    "Legacy settings without an automatic-download field remain disabled");
string root = Path.Combine(Path.GetTempPath(), "Boardcast.AutomaticUpdateTests-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new SettingsStore(root);
    Check(!store.Load().AutoDownloadUpdates, "Missing settings do not enable automatic downloads");
    var legacy = new AppSettings { AutoDownloadUpdates = true, ReceiveDirectory = Path.Combine(root, "received"), DeviceName = "Configured device", ReceivingEnabled = false };
    File.WriteAllText(store.SettingsPath, JsonSerializer.Serialize(legacy, BoardcastJson.Options));
    var loaded = store.Load();
    Check(!loaded.AutoDownloadUpdates, "Previously enabled automatic download is disabled when loading legacy settings");
    Check(loaded.DeviceName == "Configured device" && !loaded.ReceivingEnabled && loaded.LanOnline,
        "Disabling legacy automatic downloads preserves other user settings");
    store.Save(legacy);
    Check(!store.Load().AutoDownloadUpdates && !JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(store.SettingsPath), BoardcastJson.Options)!.AutoDownloadUpdates,
        "Saving old clients' enabled flag cannot re-enable automatic downloads");

    PeerInfo Peer(char identity, string name, string version) => new()
    {
        Fingerprint = new string(identity, 64), Name = name, Runtime = runtime, UpdateVersion = version,
        UpdatePort = 12345, Address = "192.168.1.10", LanOnline = true
    };
    var lower = Peer('a', "Older compatible", "0.3.0");
    var latest = Peer('b', "A latest", "0.4.0");
    var sameLatest = Peer('c', "B latest", "0.4.0");
    List<PeerInfo> peers = [lower, latest, sameLatest, Peer('d', "Preview", "9.0.0-beta"), Peer('e', "Other processor", "9.0.0") with { Runtime = "win-arm64" }];
    var versions = PeerUpdateCatalog.AvailableVersions(peers, current, runtime);
    Check(versions.Select(v => v.Version).SequenceEqual(["0.4.0", "0.3.0"]),
        "Discovery lists every compatible stable upgrade in descending order");
    Check(versions[0].Sources.Select(p => p.Fingerprint).SequenceEqual([latest.Fingerprint, sameLatest.Fingerprint]),
        "Devices offering the same version are grouped as transfer sources");
    Check(versions[1].Sources.Single().Fingerprint == lower.Fingerprint,
        "A lower compatible upgrade remains available for explicit selection");
    Check(PeerUpdateCatalog.Recommend(peers, current, runtime, lower.Fingerprint)?.UpdateVersion == "0.3.0",
        "Explicit selection preserves the chosen version instead of forcing the newest");
    Check(PeerUpdateCatalog.AvailableVersions(peers.AsEnumerable().Reverse(), current, runtime).SelectMany(v => v.Sources).Select(p => p.Fingerprint)
        .SequenceEqual(versions.SelectMany(v => v.Sources).Select(p => p.Fingerprint)),
        "Version and source ordering is independent of discovery order");
    Check(PeerUpdateCatalog.AvailableVersions(peers, "0.4.0", runtime).Count == 0,
        "Current, older, incompatible and preview releases cannot become stable update choices");
    Check(PeerUpdateCatalog.AvailableVersions([
        latest with { LanOnline = false }, latest with { DepartureHint = true }, latest with { UpdatePort = 0 },
        latest with { UpdatePort = 65536 }, latest with { UpdateVersion = "invalid" }], current, runtime).Count == 0,
        "Offline, departing and malformed advertisements do not become update choices");
    foreach (var unavailable in new[] { latest with { LanOnline = false }, latest with { DepartureHint = true } })
    {
        Check(PeerUpdateCatalog.Recommend([unavailable], current, runtime) is null,
            "Offline and departing sources do not become homepage recommendations");
        Check(PeerUpdateCatalog.Recommend([lower, unavailable], current, runtime, unavailable.Fingerprint)?.Fingerprint == lower.Fingerprint,
            "Pinning an offline or departing source falls back to an available recommendation");
    }
    versions[0].Sources[0].Address = "192.168.1.99";
    Check(latest.Address == "192.168.1.10", "Listed version sources are snapshots and do not mutate live discovery metadata");
    Check(PeerUpdateCatalog.AvailableVersions([Peer('f', "Preview upgrade", "0.5.0-beta")], "0.5.0-alpha", runtime).Single().Version == "0.5.0-beta",
        "Preview installations can explicitly choose a newer preview");
    Console.WriteLine($"All {checks} explicit-update discovery and settings checks passed.");
}
finally
{
    string normalized = Path.GetFullPath(root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (normalized.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(normalized).StartsWith("Boardcast.AutomaticUpdateTests-", StringComparison.Ordinal) && Directory.Exists(normalized)) Directory.Delete(normalized, true);
}
