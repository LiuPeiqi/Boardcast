using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Boardcast.Windows;

internal sealed class TrayIcon : IDisposable
{
    internal const int CallbackMessage = 0x8001;
    internal const uint IconId = 1;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetFocus = 3, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifShowTip = 0x80;
    private const int WmContextMenu = 0x7B, WmLeftButtonDoubleClick = 0x203, NinSelect = 0x400, NinKeySelect = 0x401;
    private const uint MenuOpen = 1, MenuExit = 2, MfGrayed = 1, MfSeparator = 0x800;
    private const uint TpmRightButton = 2, TpmNoNotify = 0x80, TpmReturnCommand = 0x100;
    private readonly Window _window;
    private readonly Action _open, _exit;
    private readonly IntPtr _handle;
    private readonly HwndSource? _source;
    private readonly HwndSourceHook _hook;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _icon;
    private bool _available, _exiting, _disposed;
    public bool IsAvailable => !_disposed && _available;

    public TrayIcon(Window window, Action open, Action exit)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(exit);
        _window = window; _open = open; _exit = exit;
        _handle = new WindowInteropHelper(window).Handle;
        _hook = WindowProcedure;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_handle == IntPtr.Zero) return;
        _source = HwndSource.FromHwnd(_handle);
        if (_source is null) return;
        try { _icon = LoadResourceIcon(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return; }
        if (_icon == IntPtr.Zero) return;
        _source.AddHook(_hook);
        TryInstall();
    }

    public void SetExiting(bool exiting)
    {
        if (_disposed) return;
        _exiting = exiting;
        if (!_available) return;
        var data = CreateData();
        data.Flags = NifTip | NifShowTip;
        ShellNotifyIcon(NimModify, ref data);
    }

    private bool TryInstall()
    {
        if (_disposed || _icon == IntPtr.Zero || _handle == IntPtr.Zero) return false;
        var data = CreateData();
        if (!ShellNotifyIcon(NimAdd, ref data)) { _available = false; return false; }
        data.Version = 4;
        if (!ShellNotifyIcon(NimSetVersion, ref data))
        {
            ShellNotifyIcon(NimDelete, ref data);
            _available = false; return false;
        }
        _available = true; return true;
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _handle, Id = IconId,
        Flags = NifMessage | NifIcon | NifTip | NifShowTip, CallbackMessage = CallbackMessage,
        Icon = _icon, ToolTip = _exiting ? "Boardcast · 正在退出…" : "Boardcast",
        State = 0, StateMask = 0, Info = string.Empty, Version = 4,
        InfoTitle = string.Empty, InfoFlags = 0, GuidItem = Guid.Empty, BalloonIcon = IntPtr.Zero
    };

    private IntPtr WindowProcedure(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreatedMessage != 0 && unchecked((uint)message) == _taskbarCreatedMessage)
        {
            // Restore after Explorer restarts; keep the app reachable if installation fails.
            _available = false;
            if (!TryInstall()) QueueOpen();
            return IntPtr.Zero;
        }
        if (message != CallbackMessage) return IntPtr.Zero;
        var packed = unchecked((uint)lParam.ToInt64());
        if ((packed >> 16) != IconId) return IntPtr.Zero;
        switch ((int)(packed & 0xFFFF))
        {
            case WmContextMenu: handled = true; ShowContextMenu(wParam); break;
            case WmLeftButtonDoubleClick:
            case NinSelect:
            case NinKeySelect: handled = true; QueueOpen(); break;
        }
        return IntPtr.Zero;
    }

    private void QueueOpen()
    {
        if (_disposed || _window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _open(); }));
    }

    private void ShowContextMenu(IntPtr packedPosition)
    {
        if (_disposed) return;
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        uint selected = 0;
        try
        {
            if (!AppendMenu(menu, 0, new UIntPtr(MenuOpen), "打开 Boardcast") ||
                !AppendMenu(menu, MfSeparator, UIntPtr.Zero, null) ||
                !AppendMenu(menu, _exiting ? MfGrayed : 0, new UIntPtr(MenuExit), _exiting ? "正在退出…" : "退出")) return;
            var packed = unchecked((uint)packedPosition.ToInt64());
            var point = new NativePoint { X = unchecked((short)(packed & 0xFFFF)), Y = unchecked((short)(packed >> 16)) };
            if (point.X == -1 && point.Y == -1)
            {
                var identifier = new NotifyIconIdentifier { Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _handle, Id = IconId, GuidItem = Guid.Empty };
                if (ShellNotifyIconGetRect(ref identifier, out var rectangle) == 0)
                {
                    point.X = rectangle.Left + (rectangle.Right - rectangle.Left) / 2;
                    point.Y = rectangle.Bottom;
                }
                else GetCursorPos(out point);
            }
            SetForegroundWindow(_handle);
            selected = TrackPopupMenuEx(menu, TpmReturnCommand | TpmNoNotify | TpmRightButton, point.X, point.Y, _handle, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
            PostMessage(_handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (_available) { var data = CreateData(); ShellNotifyIcon(NimSetFocus, ref data); }
        }
        // Dispatch after destroying the menu; exit can destroy the window and this object.
        if (_disposed) return;
        if (selected == MenuOpen) _open();
        else if (selected == MenuExit && !_exiting) _exit();
    }

    private static IntPtr LoadResourceIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/boardcast.ico", UriKind.Absolute));
        if (resource is null) return IntPtr.Zero;
        using var stream = resource.Stream;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)) != 1) throw new InvalidDataException("Invalid embedded ICO header.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        if (count == 0 || 6L + count * 16L > bytes.Length) throw new InvalidDataException("Invalid embedded ICO directory.");
        var width = Math.Max(16, GetSystemMetrics(49));
        var height = Math.Max(16, GetSystemMetrics(50));
        var bestEntry = -1; var bestScore = int.MaxValue; var bestDepth = -1;
        for (var index = 0; index < count; index++)
        {
            var p = 6 + index * 16;
            var w = bytes[p] == 0 ? 256 : bytes[p];
            var h = bytes[p + 1] == 0 ? 256 : bytes[p + 1];
            var depth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 6, 2));
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 8, 4));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 12, 4));
            if (length == 0 || offset > bytes.Length || (ulong)offset + length > (ulong)bytes.Length) continue;
            var score = (w >= width ? w - width : 1000 + width - w) + (h >= height ? h - height : 1000 + height - h);
            if (score < bestScore || (score == bestScore && depth > bestDepth)) { bestEntry = p; bestScore = score; bestDepth = depth; }
        }
        if (bestEntry < 0) throw new InvalidDataException("Embedded ICO has no valid image.");
        var selectedLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bestEntry + 8, 4));
        var selectedOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bestEntry + 12, 4));
        var image = bytes.AsSpan(checked((int)selectedOffset), checked((int)selectedLength)).ToArray();
        return CreateIconFromResourceEx(image, (uint)image.Length, true, 0x30000, width, height, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_available) { var data = CreateData(); ShellNotifyIcon(NimDelete, ref data); _available = false; }
        if (_source is not null)
        {
            try { _source.RemoveHook(_hook); }
            catch (InvalidOperationException) { }
        }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string ToolTip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid GuidItem; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rectangle);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint resourceSize, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int desiredWidth, int desiredHeight, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr itemId, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
