using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Boardcast.Core;

/// <summary>Event-driven activation of the window associated with one local data directory.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsUiActivation
{
    public static readonly uint Message = RegisterWindowMessage("Boardcast.Activate.v1");
    private static string Property(string? dataDirectory) => "Boardcast.UI." + IpcClient.GetPipeName(dataDirectory);

    public static void Register(IntPtr window, string? dataDirectory)
    {
        if (Message == 0 || !SetProp(window, Property(dataDirectory), new IntPtr(1)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static void Unregister(IntPtr window, string? dataDirectory) => RemoveProp(window, Property(dataDirectory));

    public static bool TryActivate(string? dataDirectory, bool incoming)
    {
        IntPtr found = IntPtr.Zero;
        string property = Property(dataDirectory);
        EnumWindows((window, _) =>
        {
            if (GetProp(window, property) != new IntPtr(1)) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        GetWindowThreadProcessId(found, out uint process);
        AllowSetForegroundWindow(process);
        return PostMessage(found, Message, incoming ? new IntPtr(1) : IntPtr.Zero, IntPtr.Zero);
    }

    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
