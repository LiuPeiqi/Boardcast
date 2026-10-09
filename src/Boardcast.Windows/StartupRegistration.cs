using System.IO;
using Microsoft.Win32;
namespace Boardcast.Windows;
internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled() { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("Boardcast") is string; }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (!enabled) { key.DeleteValue("Boardcast", false); return; }
        var daemon = Path.Combine(AppContext.BaseDirectory, "Boardcast.exe");
        if (!File.Exists(daemon)) throw new FileNotFoundException("请从发行包启用登录启动。", daemon);
        key.SetValue("Boardcast", "\"" + daemon + "\" --background");
    }
}