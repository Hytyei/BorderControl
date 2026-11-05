using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace BorderControl;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private MonitorEngine? _engine;
    private TrayManager? _tray;
    private MainWindow? _window;
    private UpdateService? _updates;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, "BorderControl.SingleInstance", out bool created);
        if (!created && e.Args.Contains("--after-update"))
        {
            // Started by the previous version during an update: wait for it to finish closing.
            try { created = _mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { created = true; }
        }
        _ownsMutex = created;
        if (!created)
        {
            Shutdown();
            return;
        }

        _engine = new MonitorEngine();
        _updates = new UpdateService();
        _updates.Log += m => _engine.Note(m);
        _window = new MainWindow(_engine, _updates, RestartForUpdate);
        _tray = new TrayManager(_engine, _window, ExitApp, _updates, RestartForUpdate);

        SessionEnding += (_, _) => _engine?.RestoreOnExit();

        // "BorderControl.exe --tray" starts minimized to the system tray.
        if (!e.Args.Contains("--tray")) _window.Show();

        if (e.Args.Contains("--after-update")) _engine.Note($"Updated to {UpdateService.Current}");
        _ = _engine.StartAsync();
        _updates.Start();
    }

    private void ExitApp()
    {
        _engine?.RestoreOnExit();
        Close();
    }

    /// <summary>
    /// Installs the downloaded update and hands over to it. Nothing is restored in between:
    /// the new version reads the same saved state and carries on (time zone, location, keyboard).
    /// </summary>
    private void RestartForUpdate()
    {
        if (_updates == null || !_updates.InstallAndLaunch(showWindow: _window?.IsVisible == true)) return;
        Close();
    }

    private void Close()
    {
        _tray?.Dispose();
        _updates?.Dispose();
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
