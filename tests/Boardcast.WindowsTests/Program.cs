using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Boardcast.Core;
using Microsoft.Win32.SafeHandles;

namespace Boardcast.WindowsTests;

// Explicitly opt-in: this process-level check opens its own windows on an interactive desktop.
// It uses only fresh data directories and never enumerates/stops unrelated Boardcast processes.
internal static partial class Program
{
    private static int checks;
    private static readonly List<Process> OwnedProcesses = [];
    private static string dataDirectory = "";
    private static string? fixtureRoot;
    private static readonly List<string> CompletedChecks = [];

    [STAThread]
    private static int Main(string[] args)
    {
        int index = Array.IndexOf(args, "--binaries-directory");
        if (index < 0 || index + 1 >= args.Length)
        {
            Console.WriteLine("Usage: Boardcast.WindowsTests --binaries-directory <published Windows folder> [--scenario tray|picker|picker-size]");
            Console.WriteLine("Requires an unlocked, interactive Windows desktop; starts isolated UI/daemon instances.");
            return 2;
        }
        try
        {
            int scenarioIndex = Array.IndexOf(args, "--scenario");
            string scenario = scenarioIndex < 0 ? "tray" : args[scenarioIndex + 1];
            if (scenario == "picker") RunPickerAsync(Path.GetFullPath(args[index + 1])).GetAwaiter().GetResult();
            else if (scenario == "picker-size") RunPickerAsync(Path.GetFullPath(args[index + 1]), verifySizes: true).GetAwaiter().GetResult();
            else if (scenario == "tray") RunAsync(Path.GetFullPath(args[index + 1])).GetAwaiter().GetResult();
            else throw new ArgumentException("Supported scenarios: tray, picker, picker-size.");
            Console.WriteLine($"PASS {checks} Windows {scenario} checks");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            return 1;
        }
        finally { CleanupAsync().GetAwaiter().GetResult(); }
    }

    private static async Task RunAsync(string binariesDirectory)
    {
        string uiPath = Path.Combine(binariesDirectory, "Boardcast.exe");
        if (!File.Exists(uiPath) || !File.Exists(Path.Combine(binariesDirectory, "Boardcast.Daemon.exe")))
            throw new FileNotFoundException("Publish/copy the UI and daemon to the same directory before testing.");
        fixtureRoot = Path.Combine(Path.GetTempPath(), "Boardcast.WindowsTests-" + Guid.NewGuid().ToString("N"));
        dataDirectory = Path.Combine(fixtureRoot, "receiver");
        Directory.CreateDirectory(dataDirectory);
        var settings = new AppSettings
        {
            DeviceName = "Tray lifecycle test",
            ReceiveDirectory = Path.Combine(fixtureRoot, "received"),
            Port = FreePort()
        };
        new SettingsStore(dataDirectory).Save(settings);
        Process ui = StartUi(uiPath, dataDirectory);
        nint window = await WaitForWindowAsync(ui.Id);
        await WaitUntilAsync(() => Native.IsWindowVisible(window), "initial main window is visible");
        DaemonSnapshot snapshot = await WaitForSnapshotAsync();
        int daemonPid = await GetDaemonPidAsync();
        Process daemon = Process.GetProcessById(daemonPid);
        OwnedProcesses.Add(daemon);
        Check(daemonPid != ui.Id, "the network service runs in its own process");

        Native.PostMessage(window, Native.WmClose, 0, 0);
        await WaitUntilAsync(() => !Native.IsWindowVisible(window), "close hides the main window");
        await Task.Delay(300);
        Check(!ui.HasExited, "closing the window keeps the UI/tray process alive");
        Check((await StatusAsync()).Success && !daemon.HasExited, "closing the window keeps the daemon and IPC alive");

        using (Process second = StartUi(uiPath, dataDirectory + Path.DirectorySeparatorChar + "."))
        {
            await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(second.ExitCode == 0, "relaunching the same data directory exits the second UI process");
        }
        await WaitUntilAsync(() => Native.IsWindowVisible(window), "relaunch restores the original hidden window");

        Native.PostMessage(window, Native.WmClose, 0, 0);
        await WaitUntilAsync(() => !Native.IsWindowVisible(window), "window can return to the tray again");
        using var identity = new IdentityStore(Path.Combine(fixtureRoot, "sender"));
        var senderSettings = settings with { DeviceName = "Incoming lifecycle fixture" };
        var senderPeer = new PeerInfo { Fingerprint = identity.Fingerprint, Name = senderSettings.DeviceName, Address = "127.0.0.1", Port = FreePort(), Platform = "windows" };
        var sender = new TransferService(identity, () => senderSettings, () => senderPeer,
            (_, _, _) => Task.FromResult(new IncomingDecision(false, "", "Test sender only")), _ => { }, _ => { });
        string sourcePath = Path.Combine(fixtureRoot, "incoming.txt");
        await File.WriteAllTextAsync(sourcePath, "The tray must still receive a manual acceptance request.");
        var plan = ManifestBuilder.BuildPlan([sourcePath], senderSettings);
        using var transferDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Task sending = sender.SendAsync(plan, new PeerInfo { Fingerprint = snapshot.Fingerprint, Name = settings.DeviceName, Address = "127.0.0.1", Port = settings.Port, Platform = "windows" }, transferDeadline.Token);
        await WaitUntilAsync(async () => (await StatusAsync()).Snapshot?.Pending.Any(p => p.Offer.Id == plan.Offer.Id) == true, "a real TLS offer reaches the hidden receiver");
        await WaitUntilAsync(() => Native.IsWindowVisible(window), "a transfer needing confirmation restores the hidden receiver window");

        Native.PostMessage(window, Native.WmClose, 0, 0);
        await WaitUntilAsync(() => !Native.IsWindowVisible(window), "receiver hides after the incoming request");
        await OpenTrayMenuAndExitAsync(ui.Id, window, sending);
        await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(ui.ExitCode == 0, "tray Exit closes the UI process cleanly");
        Check(daemon.HasExited, "tray Exit stops the network daemon before the UI exits");
        await sending.WaitAsync(TimeSpan.FromSeconds(10));
        Check(sending.IsCompleted, "tray Exit terminates the pending TLS session");
        using var pipeDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        try
        {
            await IpcClient.SendAsync(new IpcRequest(), dataDirectory, pipeDeadline.Token);
            throw new InvalidOperationException("Daemon pipe remained reachable after tray exit.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        { Check(true, "daemon pipe is unavailable after tray Exit"); }
        await VerifyBackgroundStartupAsync(uiPath, settings);
        Console.WriteLine("Fixture directory: " + fixtureRoot);
        await File.WriteAllTextAsync(Path.Combine(fixtureRoot, "result.json"),
            System.Text.Json.JsonSerializer.Serialize(new { success = true, checks, completedChecks = CompletedChecks, uiPid = ui.Id, daemonPid, binariesDirectory, measuredAt = DateTimeOffset.Now }, BoardcastJson.Options));
    }

    private static Process StartUi(string path, string directory, bool background = false)
    {
        var start = new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)!, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(directory);
        if (background) start.ArgumentList.Add("--background");
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the test UI.");
        OwnedProcesses.Add(process);
        return process;
    }

    private static async Task VerifyBackgroundStartupAsync(string uiPath, AppSettings initialSettings)
    {
        dataDirectory = Path.Combine(fixtureRoot!, "background");
        new SettingsStore(dataDirectory).Save(initialSettings with { Port = FreePort(), DeviceName = "Background lifecycle test" });
        Process ui = StartUi(uiPath, dataDirectory, background: true);
        nint window = await WaitForWindowAsync(ui.Id);
        _ = await WaitForSnapshotAsync();
        Process daemon = Process.GetProcessById(await GetDaemonPidAsync());
        OwnedProcesses.Add(daemon);
        await WaitUntilAsync(() => !Native.IsWindowVisible(window), "--background starts with its main window hidden");
        Check(!ui.HasExited && !daemon.HasExited, "--background keeps the tray and daemon alive");
        await ExitFromTrayAsync(ui.Id, window);
        await ui.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(ui.ExitCode == 0 && daemon.HasExited, "--background tray Exit stops both processes cleanly");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<IpcResponse> StatusAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        return await IpcClient.SendAsync(new IpcRequest(), dataDirectory, deadline.Token);
    }

    private static async Task<DaemonSnapshot> WaitForSnapshotAsync()
    {
        DaemonSnapshot? snapshot = null;
        await WaitUntilAsync(async () =>
        {
            try { snapshot = (await StatusAsync()).Snapshot; return snapshot is not null; }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException) { return false; }
        }, "the UI starts the isolated daemon");
        return snapshot!;
    }

    private static async Task<int> GetDaemonPidAsync()
    {
        using var pipe = new NamedPipeClientStream(".", IpcClient.GetPipeName(dataDirectory), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(deadline.Token);
        if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        await WireProtocol.WriteAsync(pipe, new IpcRequest(), deadline.Token);
        _ = await WireProtocol.ReadAsync<IpcResponse>(pipe, deadline.Token);
        return checked((int)processId);
    }

    private static async Task<nint> WaitForWindowAsync(int processId)
    {
        nint result = 0;
        await WaitUntilAsync(() =>
        {
            Native.EnumWindows((window, _) =>
            {
                Native.GetWindowThreadProcessId(window, out uint owner);
                if (owner == processId && Native.Title(window) == "Boardcast · 邻间") { result = window; return false; }
                return true;
            }, 0);
            return result != 0;
        }, "the isolated main window was created");
        return result;
    }

    private static Task WaitUntilAsync(Func<bool> predicate, string label) => WaitUntilAsync(() => Task.FromResult(predicate()), label);
    private static async Task WaitUntilAsync(Func<Task<bool>> predicate, string label)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (await predicate()) { Check(true, label); return; }
            await Task.Delay(100);
        }
        throw new TimeoutException(label);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        checks++; CompletedChecks.Add(label);
        Console.WriteLine("PASS " + label);
    }

    private static async Task CleanupAsync()
    {
        if (dataDirectory.Length > 0)
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await IpcClient.SendAsync(new IpcRequest { Command = "stop" }, dataDirectory, deadline.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException) { }
        }
        foreach (var process in OwnedProcesses)
        {
            try
            {
                if (!process.HasExited)
                {
                    if (!process.WaitForExit(2000)) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException) { }
            finally { process.Dispose(); }
        }
    }

    private static async Task OpenTrayMenuAndExitAsync(int processId, nint window, Task pendingTransfer)
    {
        var open = await OpenTrayMenuAsync(processId, window, "打开 Boardcast");
        _ = ClickMenuItem(open, processId, window);
        await WaitUntilAsync(() => Native.IsWindowVisible(window), "tray Open restores the hidden main window");
        Native.PostMessage(window, Native.WmClose, 0, 0);
        await WaitUntilAsync(() => !Native.IsWindowVisible(window), "tray Open can be followed by another close");

        await ExitFromTrayAsync(processId, window, pendingTransfer);
    }

    private static async Task ExitFromTrayAsync(int processId, nint window, Task? pendingTransfer = null)
    {
        var exit = await OpenTrayMenuAsync(processId, window, "退出");
        if (pendingTransfer is not null) Check(!pendingTransfer.IsCompleted, "the TLS request is still pending when tray Exit is selected");
        nint menu = ClickMenuItem(exit, processId, window);
        try
        {
            await WaitUntilAsync(() => (!Native.IsWindow(menu) || !Native.IsWindowVisible(menu)) &&
                (!Native.IsWindow(window) || !Native.IsWindowEnabled(window)),
                "tray Exit dismisses its menu and begins application shutdown");
        }
        finally { TraceTrayState("after-exit-wait", window, menu); }
    }

    private static nint ClickMenuItem(AutomationElement item, int processId, nint window)
    {
        // Native TrackPopupMenu with TPM_RETURNCMD does not consistently dispatch UIA Invoke.
        // Use UIA to locate the real menu item, then click only after checking the target HWND owner.
        var rectangle = item.Current.BoundingRectangle;
        if (rectangle.IsEmpty || item.Current.IsOffscreen) throw new InvalidOperationException("Menu item is not visible.");
        var point = new Native.Point { X = (int)(rectangle.Left + rectangle.Width / 2), Y = (int)(rectangle.Top + rectangle.Height / 2) };
        nint menu = Native.WindowFromPoint(point);
        Native.GetWindowThreadProcessId(menu, out uint owner);
        TraceTrayState("before-click", window, menu);
        if (owner != processId) throw new InvalidOperationException("Menu was obscured by another process; no input was sent.");
        Native.GetCursorPos(out var previous);
        try
        {
            if (!Native.SetCursorPos(point.X, point.Y)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Native.GetWindowThreadProcessId(Native.WindowFromPoint(point), out owner);
            if (owner != processId) throw new InvalidOperationException("Menu changed before click; no input was sent.");
            var input = new[]
            {
                new Native.Input { Type = 0, Data = new Native.InputData { Mouse = new Native.MouseInput { Flags = 0x0002 } } },
                new Native.Input { Type = 0, Data = new Native.InputData { Mouse = new Native.MouseInput { Flags = 0x0004 } } }
            };
            if (Native.SendInput((uint)input.Length, input, Marshal.SizeOf<Native.Input>()) != input.Length)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            Console.WriteLine("TRACE tray accepted mouse input; menu selection remains unconfirmed");
            Thread.Sleep(80);
        }
        finally
        {
            if (Native.GetCursorPos(out var current) && current.X == point.X && current.Y == point.Y)
                Native.SetCursorPos(previous.X, previous.Y);
            TraceTrayState("after-click", window, menu);
        }
        return menu;
    }

    private static void TraceTrayState(string stage, nint window, nint menu)
    {
        nint foreground = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(foreground, out uint foregroundPid);
        Console.WriteLine($"TRACE tray {stage}: owner=0x{window:X}, ownerExists={Native.IsWindow(window)}, ownerEnabled={Native.IsWindowEnabled(window)}, ownerIconic={Native.IsIconic(window)}, foreground=0x{foreground:X}, foregroundPid={foregroundPid}, menu=0x{menu:X}, menuVisible={Native.IsWindowVisible(menu)}");
    }

    private static async Task<AutomationElement> OpenTrayMenuAsync(int processId, nint window, string itemName)
    {
        // Shell notification protocol v4 packs event into LOWORD and icon id into HIWORD.
        // This is the ordinary production callback, not a test-only product command.
        TraceTrayState("before-open", window, 0);
        if (!Native.PostMessage(window, 0x8001, (100 << 16) | 100, (1 << 16) | 0x007B))
            throw new InvalidOperationException("Could not post the tray notification callback.");
        AutomationElement? item = null;
        await WaitUntilAsync(() =>
        {
            try
            {
                var windows = new List<nint>();
                Native.EnumWindows((candidate, _) =>
                {
                    Native.GetWindowThreadProcessId(candidate, out uint owner);
                    if (owner == processId && Native.IsWindowVisible(candidate)) windows.Add(candidate);
                    return true;
                }, 0);
                foreach (nint candidate in windows)
                {
                    item = AutomationElement.FromHandle(candidate).FindFirst(TreeScope.Subtree,
                        new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                            new PropertyCondition(AutomationElement.NameProperty, itemName)));
                    if (item is not null && item.Current.IsEnabled) return true;
                }
                return false;
            }
            catch (ElementNotAvailableException) { return false; }
        }, "tray right-click exposes enabled menu item: " + itemName);
        return item!;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { internal int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { internal int X, Y; internal uint MouseData, Flags, Time; internal UIntPtr ExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] internal struct InputData { [FieldOffset(0)] internal MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal InputData Data; }
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] input, int size);
        [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowEnabled(nint window);
        internal const uint WmClose = 0x0010;
        internal delegate bool WindowVisitor(nint window, nint parameter);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(WindowVisitor visitor, nint parameter);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maximum);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
        internal static string Title(nint window) { var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity); return text.ToString(); }
    }
}
