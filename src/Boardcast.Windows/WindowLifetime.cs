using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Boardcast.Core;

namespace Boardcast.Windows;

public partial class MainWindow
{
    private TrayIcon? _tray;
    private System.Diagnostics.Process? _launchedBackend;
    private HwndSource? _activationSource;
    private IntPtr _windowHandle;
    private bool _previewMode, _allowClose, _exitInProgress;
    private bool _lifecycleDisposed;
    private WindowState _restoreState = WindowState.Normal;
    private CancellationTokenSource _lifetime = new();
    private Task _initializationTask = Task.CompletedTask;
    private Task _summaryTask = Task.CompletedTask;

    private void InitializeLifecycle(bool preview, bool background)
    {
        _previewMode = preview;
        if (background && !preview) { WindowState = WindowState.Minimized; ShowInTaskbar = false; }
        SourceInitialized += (_, _) =>
        {
            if (_previewMode) return;
            _windowHandle = new WindowInteropHelper(this).Handle;
            _activationSource = HwndSource.FromHwnd(_windowHandle);
            WindowsUiActivation.Register(_windowHandle, _dataDirectory);
            _activationSource?.AddHook(ActivationMessage);
            try { _tray = new TrayIcon(this, () => _ = ShowFromTrayAsync(), () => _ = ExitApplicationAsync()); }
            catch (Exception ex) { SetStatus("无法创建托盘图标：" + ex.Message); }
        };
        Closing += WindowClosing;
        Closed += (_, _) => ReleaseLifecycle();
        IsVisibleChanged += (_, _) => { if (!IsVisible) _timer.Stop(); };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        };
    }

    private IntPtr ActivationMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == WindowsUiActivation.Message)
        {
            handled = true;
            if (!_exitInProgress) _ = ShowFromTrayAsync(wParam != IntPtr.Zero);
        }
        return IntPtr.Zero;
    }

    private void WindowClosing(object? sender, CancelEventArgs args)
    {
        if (_previewMode || _allowClose || Application.Current is App { IsShuttingDown: true }) return;
        args.Cancel = true;
        if (_exitInProgress) return;
        if (_tray?.IsAvailable != true)
        {
            SetStatus("托盘图标不可用，窗口保持打开。可在设置中退出程序。");
            return;
        }
        _timer.Stop();
        Hide();
    }

    private void HideToTray()
    {
        if (_tray?.IsAvailable != true)
        {
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            SetStatus("托盘图标不可用，窗口保持打开。可在设置中退出程序。");
            return;
        }
        _timer.Stop();
        Hide();
    }

    private async Task ShowFromTrayAsync(bool incoming = false)
    {
        if (_exitInProgress) return;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
        Activate();
        if (incoming) ShowPage("transfers");
        if (_initializationTask.IsCompleted)
        {
            _timer.Start();
            await RefreshAsync();
        }
    }

    internal async Task ExitApplicationAsync()
    {
        if (_exitInProgress) return;
        _exitInProgress = true;
        _timer.Stop();
        _tray?.SetExiting(true);
        IsEnabled = false;
        SetStatus("正在停止传输与后台…");
        _lifetime.Cancel();
        try
        {
            // Startup must settle before stop, or a late launch could leave a new daemon behind.
            await _initializationTask;
            await _summaryTask;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Task.WhenAll(_sizeTasks.ToArray()).WaitAsync(timeout.Token);
            await BackendLifetime.StopAsync(_dataDirectory, timeout.Token, _launchedBackend);
            _allowClose = true;
            _tray?.Dispose();
            _tray = null;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _exitInProgress = false;
            IsEnabled = true;
            _tray?.SetExiting(false);
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
            await ShowFromTrayAsync();
            SetStatus(ex is OperationCanceledException ? "后台尚未结束，退出未完成；请稍后重试。" : "退出未完成：" + ex.Message);
        }
    }

    internal void PrepareForSessionEnding()
    {
        _allowClose = true;
        _lifetime.Cancel();
        _timer.Stop();
        _tray?.Dispose();
        _tray = null;
    }

    private void ReleaseLifecycle()
    {
        if (_lifecycleDisposed) return;
        _lifecycleDisposed = true;
        _timer.Stop();
        _lifetime.Cancel();
        _tray?.Dispose();
        _tray = null;
        _activationSource?.RemoveHook(ActivationMessage);
        if (_windowHandle != IntPtr.Zero) WindowsUiActivation.Unregister(_windowHandle, _dataDirectory);
        ClearSelectedPaths();
        _folderSizes.Dispose();
        _lifetime.Dispose();
        _launchedBackend?.Dispose();
    }
}
