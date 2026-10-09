using System.Globalization;
using System.Text.Json;
using Boardcast.Core;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Boardcast.GroupTests-" + Guid.NewGuid().ToString("N"));
    private static readonly string Fingerprint = new('a', 64);
    private static int passed;
    private static int failed;

    public static int Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            Check("Name prefixes support separators, Unicode, and empty segments", () =>
            {
                foreach (var name in new[] { "Design-PC", "Design_Mac", "Design.example", "Design Laptop", "  Design-PC  ", "__Design--PC", "Design\tTablet", "Design\u3000Tablet" })
                    Equal("Design", DeviceGroups.Resolve(name, ""), name);
                Equal("设计", DeviceGroups.Resolve("设计-电脑", ""));
                Equal("Design", DeviceGroups.Resolve("Design", ""));
                Equal("Sales/West", DeviceGroups.Resolve("Sales/West-PC", ""));
                Equal("未分组", DeviceGroups.Resolve("", ""));
                Equal("未分组", DeviceGroups.Resolve("  -_.  ", ""));
            });
            Check("Explicit groups override prefixes and keep interior spaces and separators", () =>
            {
                Equal("Product Design_1.0-East", DeviceGroups.Resolve("Engineering-PC", " Product Design_1.0-East "));
                Equal("Engineering", DeviceGroups.Resolve("Engineering-PC", "   "));
            });
            Check("Old JSON defaults to automatic grouping and all-device visibility", () =>
            {
                var settings = JsonSerializer.Deserialize<AppSettings>("""{"deviceName":"Studio-PC","devices":[{"name":"Studio-Mac"}]}""", BoardcastJson.Options)!;
                settings.ReceiveDirectory = Path.Combine(Root, "legacy");
                settings.Devices[0].Fingerprint = Fingerprint;
                SettingsStore.Validate(settings);
                var peer = JsonSerializer.Deserialize<PeerInfo>("""{"name":"Studio-Mac"}""", BoardcastJson.Options)!;
                peer.Fingerprint = Fingerprint;
                Equal("", settings.GroupName);
                Equal("", settings.Devices[0].GroupName);
                Equal("", peer.GroupName);
                Assert(settings.PeerGroupFilter == DeviceGroupFilter.All && settings.VisibleGroups.Count == 0);
                Equal("Studio", DeviceGroups.GetLocalGroup(settings));
                Equal("Studio", DeviceGroups.GetPeerGroup(settings, peer));
                Assert(DeviceGroups.IsVisible(settings, peer with { Name = "Other-PC" }));
            });
            Check("Private override beats advertised group; aliases never change inferred groups", () =>
            {
                var settings = Settings();
                var peer = Peer("Original-PC", " Remote Team ");
                settings.Devices.Add(new() { Fingerprint = Fingerprint.ToUpperInvariant(), Name = "Stale-PC", Alias = "Alias-Device", GroupName = " My Team " });
                Equal("My Team", DeviceGroups.GetPeerGroup(settings, peer));
                settings.Devices[0].GroupName = "";
                Equal("Remote Team", DeviceGroups.GetPeerGroup(settings, peer));
                peer.GroupName = "";
                Equal("Original", DeviceGroups.GetPeerGroup(settings, peer));
                settings.Devices[0].Fingerprint = new string('b', 64);
                settings.Devices[0].GroupName = "Private";
                Equal("Original", DeviceGroups.GetPeerGroup(settings, peer));
            });
            Check("All, same-group, multi-select and empty selection have distinct results", () =>
            {
                var settings = Settings();
                var design = Peer("Design-PC"); var support = Peer("Support-PC"); var sales = Peer("Sales-PC");
                Assert(DeviceGroups.IsVisible(settings, design) && DeviceGroups.IsVisible(settings, support));
                settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
                Assert(DeviceGroups.IsVisible(settings, design) && !DeviceGroups.IsVisible(settings, support));
                settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups;
                settings.VisibleGroups = [" Design ", "support"];
                Assert(DeviceGroups.IsVisible(settings, design) && DeviceGroups.IsVisible(settings, support) && !DeviceGroups.IsVisible(settings, sales));
                settings.VisibleGroups.Clear();
                Assert(!DeviceGroups.IsVisible(settings, design) && !DeviceGroups.IsVisible(settings, support));
            });
            Check("Same-group filtering follows local rename, manual group, and private override changes", () =>
            {
                var settings = Settings(); settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
                var design = Peer("Design-Mac"); var sales = Peer("Sales-Mac");
                Assert(DeviceGroups.IsVisible(settings, design));
                settings.DeviceName = "Sales-PC";
                Assert(!DeviceGroups.IsVisible(settings, design) && DeviceGroups.IsVisible(settings, sales));
                settings.GroupName = " Design ";
                Assert(DeviceGroups.IsVisible(settings, design) && !DeviceGroups.IsVisible(settings, sales));
                settings.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = "Sales" });
                Assert(!DeviceGroups.IsVisible(settings, design));
            });
            Check("Group equality remains ordinal and case-insensitive under Turkish culture", () =>
            {
                var previous = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                    var settings = Settings(); settings.GroupName = " FILE "; settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
                    Assert(DeviceGroups.IsVisible(settings, Peer("Other", "file")));
                    Assert(!DeviceGroups.IsVisible(settings, Peer("Other", "fıle")));
                    settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups; settings.VisibleGroups = [" FILE "];
                    Assert(DeviceGroups.IsVisible(settings, Peer("Other", " file ")));
                }
                finally { CultureInfo.CurrentCulture = previous; }
            });
            Check("Ungrouped devices can be selected explicitly", () =>
            {
                var settings = Settings(); settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups; settings.VisibleGroups = ["未分组"];
                Assert(DeviceGroups.IsVisible(settings, Peer("---")));
                Assert(!DeviceGroups.IsVisible(settings, Peer("Design-PC")));
            });
            Check("User-specific stores and overrides remain independent of each other and wire metadata", () =>
            {
                var alice = Settings(); var bob = Settings(); var peer = Peer("Design-PC", "Shared");
                alice.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = "Alice" });
                bob.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = "Bob" });
                Equal("Alice", DeviceGroups.GetPeerGroup(alice, peer)); Equal("Bob", DeviceGroups.GetPeerGroup(bob, peer));
                Equal("Shared", peer.GroupName);
                var aliceStore = new SettingsStore(Path.Combine(Root, "alice")); var bobStore = new SettingsStore(Path.Combine(Root, "bob"));
                aliceStore.Save(alice); bobStore.Save(bob);
                alice.Devices[0].GroupName = "Changed"; aliceStore.Save(alice);
                Equal("Changed", aliceStore.Load().Devices[0].GroupName); Equal("Bob", bobStore.Load().Devices[0].GroupName);
            });
            Check("Groups persist with normalized whitespace and deduplicated selections", () =>
            {
                var settings = Settings(); settings.GroupName = " Local Team "; settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups;
                settings.VisibleGroups = [" Design ", "design", " Support ", "SUPPORT"];
                settings.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = " Personal ", Trusted = true, AutoAccept = true, Alias = "My desktop" });
                var store = new SettingsStore(Path.Combine(Root, "round-trip")); store.Save(settings);
                var loaded = store.Load();
                Equal("Local Team", loaded.GroupName); Equal("Personal", loaded.Devices[0].GroupName);
                Assert(loaded.PeerGroupFilter == DeviceGroupFilter.SelectedGroups);
                Assert(loaded.VisibleGroups.SequenceEqual(new[] { "Design", "Support" }));
                Assert(loaded.Devices[0].Trusted && loaded.Devices[0].AutoAccept && loaded.Devices[0].Alias == "My desktop");
                Assert(Directory.GetFiles(store.DataDirectory, "*.tmp").Length == 0);
            });
            Check("Same-group membership grants neither trust nor automatic receive permission", () =>
            {
                var settings = Settings(); settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
                settings.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = "Design", AutoAccept = true });
                settings.Rules.Add(new() { AutoAccept = true });
                Assert(DeviceGroups.IsVisible(settings, Peer("Design-PC")));
                Assert(!ReceivePolicy.Evaluate(settings, Offer(), Fingerprint).AutoAccept);
                settings.Devices[0].Trusted = true; settings.Devices[0].AutoAccept = false;
                Assert(!ReceivePolicy.Evaluate(settings, Offer(), Fingerprint).AutoAccept);
            });
            Check("Hiding another group preserves receive rules and certificate identity checks", () =>
            {
                var settings = Settings(); settings.PeerGroupFilter = DeviceGroupFilter.SameGroup;
                settings.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = "Other", Trusted = true, AutoAccept = true });
                settings.Rules.Add(new() { AutoAccept = true });
                Assert(!DeviceGroups.IsVisible(settings, Peer("Other-PC")));
                Assert(ReceivePolicy.Evaluate(settings, Offer(), Fingerprint).AutoAccept);
                Throws(() => ReceivePolicy.Evaluate(settings, Offer(), new string('b', 64)));
                Assert(settings.Devices[0].Trusted && settings.Devices[0].AutoAccept);
            });
            Check("Explicit group values reject null, controls, and names over 80 characters", () =>
            {
                foreach (var invalid in new[] { null, "Bad\nGroup", "Bad\0Group", "Bad\u007fGroup", new string('a', 81) })
                {
                    var settings = Settings(); settings.GroupName = invalid!; Throws(() => SettingsStore.Validate(settings));
                    settings = Settings(); settings.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = invalid! }); Throws(() => SettingsStore.Validate(settings));
                }
            });
            Check("Selected groups reject null, controls, blank values, and names over 256 characters", () =>
            {
                foreach (var invalid in new[] { null, "Bad\nGroup", "Bad\0Group", "Bad\u007fGroup", "   ", "", new string('a', 257) })
                {
                    var settings = Settings(); settings.VisibleGroups = [invalid!]; Throws(() => SettingsStore.Validate(settings));
                }
            });
            Check("Null settings, null selections, invalid enum and too many selections fail validation", () =>
            {
                Throws(() => SettingsStore.Validate(null!));
                var settings = Settings(); settings.PeerGroupFilter = (DeviceGroupFilter)999; Throws(() => SettingsStore.Validate(settings));
                settings = Settings(); settings.VisibleGroups = null!; Throws(() => SettingsStore.Validate(settings));
                settings = Settings(); settings.VisibleGroups = Enumerable.Repeat("Same", 129).ToList(); Throws(() => SettingsStore.Validate(settings));
            });
            Check("Legal maximum group lengths and 128 selections are accepted", () =>
            {
                var settings = Settings(); settings.GroupName = new string('a', 80);
                settings.Devices.Add(new() { Fingerprint = Fingerprint, GroupName = new string('b', 80) });
                settings.VisibleGroups = Enumerable.Range(0, 127).Select(i => "Group" + i).Append(new string('c', 256)).ToList();
                SettingsStore.Validate(settings); Assert(settings.VisibleGroups.Count == 128);
                settings.PeerGroupFilter = DeviceGroupFilter.SelectedGroups;
                Assert(DeviceGroups.IsVisible(settings, Peer(new string('c', 256)) with { Fingerprint = new string('b', 64) }));
            });
            Check("Invalid saves leave the last valid configuration intact; invalid JSON fields fail on load", () =>
            {
                var store = new SettingsStore(Path.Combine(Root, "invalid-save")); var settings = Settings(); settings.GroupName = "Safe"; store.Save(settings);
                var original = File.ReadAllText(store.SettingsPath); settings.GroupName = "Unsafe\nGroup";
                Throws(() => store.Save(settings)); Equal(original, File.ReadAllText(store.SettingsPath));
                var json = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(original)!; json["groupName"] = null;
                File.WriteAllText(store.SettingsPath, json.ToJsonString()); Throws(() => store.Load());
            });
        }
        finally
        {
            var normalized = Path.GetFullPath(Root);
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (normalized.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(normalized).StartsWith("Boardcast.GroupTests-", StringComparison.Ordinal))
                Directory.Delete(normalized, recursive: true);
        }
        Console.WriteLine($"Group checks: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static AppSettings Settings() => new() { DeviceName = "Design-Local", ReceiveDirectory = Path.Combine(Root, "received") };
    private static PeerInfo Peer(string name, string group = "") => new() { Fingerprint = Fingerprint, Name = name, GroupName = group };
    private static TransferOffer Offer() => new() { SenderFingerprint = Fingerprint, SenderName = "Design-PC", Entries = [new() { RelativePath = "report.pdf", Size = 1 }] };
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal(string expected, string actual, string? context = null)
    { if (expected != actual) throw new Exception($"{context}: expected '{expected}', got '{actual}'."); }
    private static void Throws(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected InvalidDataException."); }
    private static void Check(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
}
