using Boardcast.Daemon;
string? directory = null;
int? port = null;
bool noUi = false, service = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-dir": directory = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--no-ui": noUi = true; break;
        case "--service": service = true; noUi = true; break;
        default: throw new ArgumentException("Unknown argument: " + args[i]);
    }
}
try
{
    if (service && OperatingSystem.IsWindows())
        WindowsServiceHost.Run(async ct => { using var daemon = new DaemonHost(directory, port, true); await daemon.RunAsync(ct); });
    else
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var daemon = new DaemonHost(directory, port, noUi);
        await daemon.RunAsync(stop.Token);
    }
}
catch (Exception ex)
{
    try
    {
        var store = new Boardcast.Core.SettingsStore(directory);
        File.WriteAllText(Path.Combine(store.DataDirectory, "last-error.log"), DateTimeOffset.UtcNow + " " + ex);
    }
    catch { }
    Environment.ExitCode = 1;
}
