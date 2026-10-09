using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Boardcast.Daemon;

/// <summary>Optional SCM host. The default deployment remains a per-user daemon.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsServiceHost
{
    private const string Name = "Boardcast";
    private static readonly MainCallback MainDelegate = ServiceMain;
    private static readonly HandlerCallback HandlerDelegate = Control;
    private static readonly CancellationTokenSource Stopping = new();
    private static Func<CancellationToken, Task>? _run;
    private static IntPtr _handle;
    private static ServiceStatus _status;
    private static readonly object Gate = new();

    public static void Run(Func<CancellationToken, Task> run)
    {
        _run = run;
        var entries = new[]
        {
            new ServiceEntry { Name = Name, Main = Marshal.GetFunctionPointerForDelegate(MainDelegate) },
            new ServiceEntry()
        };
        if (!StartServiceCtrlDispatcher(entries))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SCM 启动失败。--service 仅供 Windows 服务控制管理器使用。");
        GC.KeepAlive(MainDelegate); GC.KeepAlive(HandlerDelegate);
    }
    private static void ServiceMain(int count, IntPtr arguments)
    {
        _handle = RegisterServiceCtrlHandlerEx(Name, HandlerDelegate, IntPtr.Zero);
        if (_handle == IntPtr.Zero) return;
        _status = new ServiceStatus { ServiceType = 0x10, CurrentState = 2, WaitHint = 10000 };
        Publish(2, 0);
        try
        {
            Publish(4, 0); // RUNNING: socket startup errors transition to STOPPED.
            _run!(Stopping.Token).GetAwaiter().GetResult();
            Publish(1, 0);
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested) { Publish(1, 0); }
        catch (Exception) { Publish(1, 1064); }
    }
    private static int Control(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5)
        {
            Publish(3, 0);
            Stopping.Cancel();
        }
        else if (control == 4) { lock (Gate) SetServiceStatus(_handle, ref _status); }
        return 0;
    }
    private static void Publish(int state, int error)
    {
        lock (Gate)
        {
            _status.CurrentState = state;
            _status.ControlsAccepted = state == 4 ? 0x1 | 0x4 : 0;
            _status.Win32ExitCode = error;
            _status.WaitHint = state is 2 or 3 ? 10000 : 0;
            _status.CheckPoint = state is 2 or 3 ? 1 : 0;
            SetServiceStatus(_handle, ref _status);
        }
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void MainCallback(int count, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int HandlerCallback(int control, int eventType, IntPtr eventData, IntPtr context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public IntPtr Main;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceCtrlDispatcher([In] ServiceEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterServiceCtrlHandlerEx(string name, HandlerCallback callback, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}
