using System.Diagnostics;
using System.Text.Json;
using Boardcast.Core;

string dataDirectory = args.SkipWhile(a => a != "--data-dir").Skip(1).First();
dataDirectory = PathSafety.ValidateReceiveRoot(dataDirectory); Directory.CreateDirectory(dataDirectory);
string version = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture-version.txt"))).Trim();
string behavior = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fixture-behavior.txt"))).Trim();
string stopUi = Path.Combine(dataDirectory, "fixture-ui-stop");
if (args.Contains("--backend"))
{
    using var daemonLock = new FileStream(Path.Combine(dataDirectory, "daemon.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await File.WriteAllTextAsync(Path.Combine(dataDirectory, "fixture-backend-pid.txt"), Environment.ProcessId.ToString());
    using var identity = new IdentityStore(dataDirectory);
    using var updates = new PeerUpdateService(identity, AppContext.BaseDirectory, dataDirectory, version);
    using var stopped = new CancellationTokenSource();
    async Task<IpcResponse> Handle(IpcRequest request)
    {
        switch (request.Command)
        {
            case "status": return new() { Snapshot = new() { AppVersion = behavior == "wrong-backend-version-and-ui-exit" ? "0.0.0" : version, Update = updates.Status } };
            case "update-download": await updates.DownloadAsync(request.Peer!, stopped.Token); return new() { Snapshot = new() { AppVersion = version, Update = updates.Status } };
            case "update-install":
                updates.SetOnline(false);
                await updates.LaunchInstallerAsync(request.ProcessId, [Environment.ProcessId, request.ProcessId], ["--data-dir", dataDirectory], () => false, stopped.Token);
                await File.WriteAllTextAsync(stopUi, "stop"); stopped.CancelAfter(500);
                return new() { Snapshot = new() { AppVersion = version, Update = updates.Status } };
            case "stop": await File.WriteAllTextAsync(stopUi, "stop"); stopped.CancelAfter(250); return new();
            default: return new() { Success = false, Error = "Unknown fixture command." };
        }
    }
    try { await IpcServer.RunAsync(dataDirectory, Handle, stopped.Token); }
    catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
}
else
{
    if (File.Exists(stopUi)) File.Delete(stopUi);
    await File.AppendAllTextAsync(Path.Combine(dataDirectory, "fixture-ui-launches.jsonl"), JsonSerializer.Serialize(new { version, id = Environment.ProcessId, started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks }, BoardcastJson.Options) + "\n");
    if (behavior == "exit-ui") { Environment.ExitCode = 2; return; }
    var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Boardcast.Daemon.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
    start.ArgumentList.Add("--backend"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(dataDirectory);
    using var backend = Process.Start(start) ?? throw new IOException("Fixture backend did not start.");
    if (behavior == "wrong-backend-version-and-ui-exit") { await Task.Delay(2000); Environment.ExitCode = 3; return; }
    while (!File.Exists(stopUi) && !backend.HasExited) await Task.Delay(25);
}
