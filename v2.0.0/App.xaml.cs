using System.Linq;
using System.Threading;
using System.Windows;

namespace VpnGuard;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private MonitorEngine? _engine;
    private TrayManager? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "BorderControl.SingleInstance", out bool created);
        _ownsMutex = created;
        if (!created)
        {
            Shutdown();
            return;
        }

        _engine = new MonitorEngine();
        _window = new MainWindow(_engine);
        _tray = new TrayManager(_engine, _window, ExitApp);

        SessionEnding += (_, _) => _engine?.RestoreOnExit();

        // "BorderControl.exe --tray" starts minimized to the system tray.
        if (!e.Args.Contains("--tray")) _window.Show();

        _ = _engine.StartAsync();
    }

    private void ExitApp()
    {
        _engine?.RestoreOnExit();
        _tray?.Dispose();
        _engine?.Dispose();
        _window?.ForceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
