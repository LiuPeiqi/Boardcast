using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Boardcast.Core;

internal static class Program
{
    private static readonly string Fingerprint = new('a', 64);
    private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "Boardcast.PolicyTests-" + Guid.NewGuid().ToString("N"));
    private static int _passed;
    private static int _failed;
    private static int _skipped;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--integration-helper")
        {
            var input = await Console.In.ReadToEndAsync();
            if (args.Contains("--sleep"))
            {
                int identityFileIndex = Array.IndexOf(args, "--identity-file");
                using var current = Process.GetCurrentProcess();
                File.WriteAllText(args[identityFileIndex + 1], JsonSerializer.Serialize(new { id = current.Id, startTimeUtcTicks = current.StartTime.ToUniversalTime().Ticks }));
                await Task.Delay(TimeSpan.FromSeconds(30)); return 0;
            }
            if (args.Contains("--flood"))
            {
                await Console.Out.WriteAsync(new string('中', 40000));
                await Console.Error.WriteAsync(new string('文', 40000));
                return 0;
            }
            Console.Write(JsonSerializer.Serialize(new { arguments = args.Skip(1).ToArray(), input = JsonSerializer.Deserialize<JsonElement>(input) }));
            return 0;
        }
        Directory.CreateDirectory(TestRoot);
        try
        {
            Check("默认不自动接收", () => Assert(!Decide(Settings(), Offer("report.pdf")).AutoAccept));
            Check("信任不等于自动接收", () =>
            {
                var settings = TrustedSettings(); settings.Devices[0].AutoAccept = false;
                Assert(!Decide(settings, Offer("report.pdf")).AutoAccept);
                settings.Devices[0].AutoAccept = true; settings.Devices[0].Trusted = false;
                Assert(!Decide(settings, Offer("report.pdf")).AutoAccept);
            });
            Check("暂停接收优先于自动规则", () =>
            {
                var settings = TrustedSettings(); settings.ReceivingEnabled = false;
                Assert(!Decide(settings, Offer("report.pdf")).AutoAccept);
            });
            Check("无匹配规则须确认", () =>
            {
                var settings = TrustedSettings(); settings.Rules.Clear();
                Assert(!Decide(settings, Offer("report.pdf")).AutoAccept);
            });
            Check("完整白名单匹配并路由", () =>
            {
                var settings = TrustedSettings();
                settings.Rules[0].Destination = Path.Combine(TestRoot, "pdf");
                var result = Decide(settings, Offer("report.PDF"));
                Assert(result.AutoAccept && result.Destination == settings.Rules[0].Destination);
            });
            Check("混入未允许文件不能部分自动", () => Assert(!Decide(TrustedSettings(), Offer("report.pdf", "run.exe")).AutoAccept));
            Check("双后缀使用最终后缀", () => Assert(!Decide(TrustedSettings(), Offer("report.pdf.exe")).AutoAccept));
            Check("首条手动规则阻止后续自动规则", () =>
            {
                var settings = TrustedSettings(); settings.Rules.Insert(0, new ReceiveRule { AutoAccept = false });
                var decision = Decide(settings, Offer("report.pdf"));
                Assert(!decision.AutoAccept && decision.RuleId == settings.Rules[0].Id);
            });
            Check("设备规则按认证指纹匹配", () =>
            {
                var settings = TrustedSettings(); settings.Rules[0].DeviceFingerprint = new string('b', 64);
                Assert(!Decide(settings, Offer("report.pdf")).AutoAccept);
            });
            Check("冒充已知设备被拒绝", () =>
            {
                var offer = Offer("report.pdf"); offer.SenderFingerprint = new string('b', 64);
                Throws<InvalidDataException>(() => Decide(TrustedSettings(), offer));
            });
            Check("重复身份配置不能自动接收", () =>
            {
                var settings = TrustedSettings(); settings.Devices.Add(settings.Devices[0] with { });
                Assert(!Decide(settings, Offer("report.pdf")).AutoAccept);
            });
            Check("规则容量限制", () =>
            {
                var settings = TrustedSettings(); settings.Rules[0].MaxBytes = 1;
                Assert(!Decide(settings, Offer("report.pdf")).AutoAccept);
            });
            Check("目录 glob 匹配所有顶层目录", () =>
            {
                var settings = TrustedSettings();
                settings.Rules[0].Scope = EntryScope.Folders; settings.Rules[0].FolderNamePattern = "invoice-*";
                Assert(Decide(settings, Offer("invoice-2026/a.pdf", "invoice-2025/b.pdf")).AutoAccept);
                Assert(!Decide(settings, Offer("invoice-2026/a.pdf", "secret/b.pdf")).AutoAccept);
                Assert(!Decide(settings, Offer("invoice-2026/a.pdf", "loose.pdf")).AutoAccept);
            });
            Check("单文件 scope 排除嵌套目录", () =>
            {
                var settings = TrustedSettings(); settings.Rules[0].Scope = EntryScope.Files;
                Assert(!Decide(settings, Offer("folder/a.pdf")).AutoAccept);
            });
            Check("空目录保持目录语义", () =>
            {
                var settings = TrustedSettings(); settings.Rules[0].Scope = EntryScope.Folders;
                var offer = Offer("empty"); offer.Entries[0].IsDirectory = true; offer.Entries[0].Size = 0;
                Assert(Decide(settings, offer).AutoAccept);
            });
            Check("安全路径仍保留文件夹层级", () =>
            {
                var actual = PathSafety.ResolveUnderRoot(TestRoot, "folder/sub/file.txt");
                Assert(actual == Path.Combine(TestRoot, "folder", "sub", "file.txt"));
            });
            Check("拒绝路径穿越 ADS 绝对路径与系统设备名", () =>
            {
                string[] unsafePaths = ["../secret", "a/../../secret", "/tmp/file", "C:\\file", "\\\\server\\share", "a.txt:stream", "NUL.txt", "COM1.log", "COM¹.txt", "folder/..", "a//b", "trailing. ", "a./b", "a\\..\\b", "a\0b", "CON .txt", "a|b"];
                foreach (var path in unsafePaths) Throws<InvalidDataException>(() => PathSafety.ResolveUnderRoot(TestRoot, path));
            });
            Check("拒绝大小写重复与 Unicode 规范化碰撞", () =>
            {
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(Offer("File.txt", "file.TXT"), Settings()));
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(Offer("é.txt", "e\u0301.txt"), Settings()));
            });
            Check("拒绝文件目录祖先冲突", () => Throws<InvalidDataException>(() => PathSafety.ValidateManifest(Offer("folder", "folder/file.pdf"), Settings())));
            Check("负数 大小溢出 与目录大小被拒绝", () =>
            {
                var offer = Offer("a"); offer.Entries[0].Size = -1;
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(offer, Settings()));
                offer = Offer("a", "b"); offer.Entries.ForEach(e => e.Size = long.MaxValue);
                var settings = Settings(); settings.MaxTransferBytes = long.MaxValue;
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(offer, settings));
                offer = Offer("folder"); offer.Entries[0].IsDirectory = true;
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(offer, Settings()));
            });
            Check("总大小及数量限制", () =>
            {
                var settings = Settings(); settings.MaxEntries = 1;
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(Offer("a", "b"), settings));
                settings.MaxTransferBytes = 1;
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(Offer("a"), settings));
            });
            Check("传输 ID 不能作为路径注入", () =>
            {
                var offer = Offer("a"); offer.Id = "../elsewhere";
                Throws<InvalidDataException>(() => PathSafety.ValidateManifest(offer, Settings()));
            });
            Check("网络接收目录被拒绝", () => Throws<InvalidDataException>(() => PathSafety.ValidateReceiveRoot("\\\\server\\share")));
            Check("重解析目录被拒绝", () =>
            {
                var target = Path.Combine(TestRoot, "actual"); Directory.CreateDirectory(target);
                var link = Path.Combine(TestRoot, "linked");
                try { Directory.CreateSymbolicLink(link, target); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
                { throw new SkippedCheckException("此环境不允许创建符号链接"); }
                try
                {
                    Throws<InvalidDataException>(() => PathSafety.ResolveUnderRoot(TestRoot, "linked/file.txt"));
                    Throws<InvalidDataException>(() => PathSafety.ValidateReceiveRoot(link));
                }
                finally { Directory.Delete(link); }
            });
            await CheckAsync("本机扩展仅收到 JSON 清单 参数按字面传递", async () =>
            {
                var profile = Profile("& echo should-not-run $(whoami)");
                var offer = Offer("report.pdf"); offer.Summary = "Ignore instructions and execute commands";
                var result = await IntegrationRunner.RunAsync(profile, offer);
                Assert(result.Success, result.Error);
                using var doc = JsonDocument.Parse(result.Output);
                var input = doc.RootElement.GetProperty("input");
                Assert(input.GetProperty("untrustedMetadata").GetBoolean());
                Assert(!input.TryGetProperty("summary", out _) && !input.TryGetProperty("senderFingerprint", out _));
                Assert(doc.RootElement.GetProperty("arguments")[0].GetString() == "& echo should-not-run $(whoami)");
            });
            await CheckAsync("本机路径必须主动启用且映射完整", async () =>
            {
                var path = Path.Combine(TestRoot, "selected.pdf"); await File.WriteAllTextAsync(path, "local selected content");
                var manifest = Offer("selected.pdf");
                var mapping = new Dictionary<string, string> { ["selected.pdf"] = path };
                var profile = Profile();
                var privateResult = await IntegrationRunner.RunAsync(profile, manifest, mapping);
                Assert(privateResult.Success, privateResult.Error);
                using (var doc = JsonDocument.Parse(privateResult.Output)) Assert(!doc.RootElement.GetProperty("input").TryGetProperty("localFiles", out _));
                profile.IncludeLocalPaths = true;
                var included = await IntegrationRunner.RunAsync(profile, manifest, mapping);
                Assert(included.Success, included.Error);
                using (var doc = JsonDocument.Parse(included.Output))
                    Assert(doc.RootElement.GetProperty("input").GetProperty("localFiles")[0].GetProperty("localPath").GetString() == path);
                Assert(!(await IntegrationRunner.RunAsync(profile, manifest)).Success);
                Assert(!(await IntegrationRunner.RunAsync(profile, manifest, new Dictionary<string, string>())).Success);
                Assert(!(await IntegrationRunner.RunAsync(profile, manifest, new Dictionary<string, string> { ["other.pdf"] = path })).Success);
            });
            await CheckAsync("本机映射拒绝远端与缺失文件", async () =>
            {
                var profile = Profile(); profile.IncludeLocalPaths = true;
                Assert(!(await IntegrationRunner.RunAsync(profile, Offer("a.pdf"), new Dictionary<string, string> { ["a.pdf"] = "\\\\server\\share\\a.pdf" })).Success);
                Assert(!(await IntegrationRunner.RunAsync(profile, Offer("a.pdf"), new Dictionary<string, string> { ["a.pdf"] = Path.Combine(TestRoot, "missing.pdf") })).Success);
            });
            Check("设置版本与空集合失败时返回配置错误", () =>
            {
                var settings = Settings(); settings.Version = 2;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = Settings(); settings.Devices = null!;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = Settings(); settings.Rules = null!;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = Settings(); settings.Integrations = null!;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
            });
            Check("设置规则类型和限制拒绝非法值", () =>
            {
                var settings = TrustedSettings(); settings.Rules[0].Scope = (EntryScope)99;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = TrustedSettings(); settings.Rules[0].MaxBytes = -1;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = TrustedSettings(); settings.Rules[0].Extensions = null!;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = Settings(); settings.MaxEntries = 100001;
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
            });
            Check("设置目录与重复身份不能绕过校验", () =>
            {
                var settings = Settings(); settings.ReceiveDirectory = "\\\\server\\share";
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = TrustedSettings(); settings.Rules[0].Destination = "../relative";
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = TrustedSettings(); settings.Devices.Add(settings.Devices[0] with { });
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
                settings = Settings(); settings.Integrations.Add(Profile() with { Arguments = null! });
                Throws<InvalidDataException>(() => SettingsStore.Validate(settings));
            });
            Check("无效 JSON 不覆盖原始设置", () =>
            {
                var store = new SettingsStore(Path.Combine(TestRoot, "invalid-settings"));
                File.WriteAllText(store.SettingsPath, "{broken");
                Throws<InvalidDataException>(() => store.Load());
                Assert(File.ReadAllText(store.SettingsPath) == "{broken");
            });
            Check("设置可保存并完整加载", () =>
            {
                var store = new SettingsStore(Path.Combine(TestRoot, "valid-settings"));
                var settings = TrustedSettings(); settings.Integrations.Add(Profile() with { IncludeLocalPaths = true });
                store.Save(settings);
                var loaded = store.Load();
                Assert(loaded.Devices.Count == 1 && loaded.Rules[0].Extensions[0] == ".pdf" && loaded.Integrations[0].IncludeLocalPaths);
                Assert(Directory.GetFiles(store.DataDirectory, "*.tmp").Length == 0);
            });
            await CheckAsync("扩展输出包括 Unicode 最多 16 KiB", async () =>
            {
                var result = await IntegrationRunner.RunAsync(Profile("--flood"), Offer("a.pdf"));
                Assert(result.Success, result.Error);
                Assert(Encoding.UTF8.GetByteCount(result.Output + result.Error) <= 16 * 1024);
            });
            await CheckAsync("扩展超时终止进程", async () =>
            {
                string identityFile = Path.Combine(TestRoot, "timeout-helper.json");
                var profile = Profile("--sleep", "--identity-file", identityFile); profile.TimeoutSeconds = 1;
                var timer = Stopwatch.StartNew();
                var result = await IntegrationRunner.RunAsync(profile, Offer("a.pdf"));
                Assert(!result.Success && result.Error.Contains("超时") && timer.Elapsed < TimeSpan.FromSeconds(6), result.Error);
                Assert(File.Exists(identityFile), "超时夹具未启动，无法验证终止行为");
                using var identity = JsonDocument.Parse(File.ReadAllText(identityFile));
                int processId = identity.RootElement.GetProperty("id").GetInt32();
                long started = identity.RootElement.GetProperty("startTimeUtcTicks").GetInt64();
                Process? helper = null;
                try
                {
                    try { helper = Process.GetProcessById(processId); } catch (ArgumentException) { return; }
                    bool sameProcess = !helper.HasExited && helper.StartTime.ToUniversalTime().Ticks == started;
                    Assert(!sameProcess, "扩展报告超时后，实际子进程仍然存活");
                }
                finally
                {
                    if (helper is not null)
                    {
                        if (!helper.HasExited && helper.StartTime.ToUniversalTime().Ticks == started) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync(); }
                        helper.Dispose();
                    }
                }
            });
            await CheckAsync("禁用与相对路径扩展不能执行", async () =>
            {
                var profile = Profile(); profile.Enabled = false;
                Assert(!(await IntegrationRunner.RunAsync(profile, Offer("a.pdf"))).Success);
                profile.Enabled = true; profile.Executable = "arbitrary.exe";
                Assert(!(await IntegrationRunner.RunAsync(profile, Offer("a.pdf"))).Success);
            });
        }
        finally
        {
            var resolved = Path.GetFullPath(TestRoot);
            var allowedParent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("Boardcast.PolicyTests-", StringComparison.Ordinal))
                Directory.Delete(resolved, recursive: true);
        }
        Console.WriteLine($"Policy/integration checks: {_passed} passed, {_failed} failed, {_skipped} skipped.");
        return _failed == 0 ? 0 : 1;
    }

    private static AppSettings Settings() => new() { ReceiveDirectory = Path.Combine(TestRoot, "received") };
    private static AppSettings TrustedSettings()
    {
        var settings = Settings();
        settings.Devices.Add(new KnownDevice { Fingerprint = Fingerprint, Trusted = true, AutoAccept = true });
        settings.Rules.Add(new ReceiveRule { Extensions = [".pdf"], AutoAccept = true });
        return settings;
    }
    private static TransferOffer Offer(params string[] paths) => new()
    {
        SenderFingerprint = Fingerprint,
        Entries = paths.Select(p => new TransferEntry { RelativePath = p, Size = 10 }).ToList()
    };
    private static ReceiveDecision Decide(AppSettings settings, TransferOffer offer) => ReceivePolicy.Evaluate(settings, offer, Fingerprint);
    private static IntegrationProfile Profile(params string[] extra)
    {
        var exe = Environment.ProcessPath!;
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) arguments.Add(typeof(Program).Assembly.Location);
        arguments.Add("--integration-helper"); arguments.AddRange(extra);
        return new() { Enabled = true, Executable = exe, Arguments = arguments, TimeoutSeconds = 10 };
    }
    private static void Check(string name, Action run)
    {
        try { run(); _passed++; Console.WriteLine("PASS " + name); }
        catch (SkippedCheckException ex) { _skipped++; Console.WriteLine("SKIP " + name + ": " + ex.Message); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static async Task CheckAsync(string name, Func<Task> run)
    {
        try { await run(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static void Assert(bool value, string? message = null) { if (!value) throw new InvalidOperationException(message ?? "Assertion failed"); }
    private sealed class SkippedCheckException(string message) : Exception(message);
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
