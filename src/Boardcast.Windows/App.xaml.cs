using System.Windows;
using Boardcast.Core;

namespace Boardcast.Windows;
public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    internal bool IsShuttingDown { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!e.Args.Contains("--render-preview"))
        {
            var index = Array.IndexOf(e.Args, "--data-dir");
            var directory = index >= 0 && e.Args.Length > index + 1 ? e.Args[index + 1] : null;
            _instance = new Mutex(true, @"Local\Boardcast.UI." + IpcClient.GetPipeName(directory), out _ownsInstance);
            if (!_ownsInstance)
            {
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (WindowsUiActivation.TryActivate(directory, e.Args.Contains("--incoming"))) break;
                    Thread.Sleep(50);
                }
                IsShuttingDown = true;
                Shutdown(); return;
            }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Boardcast", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsShuttingDown = true;
        if (MainWindow is MainWindow window) window.PrepareForSessionEnding();
        base.OnSessionEnding(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose(); base.OnExit(e);
    }
}
