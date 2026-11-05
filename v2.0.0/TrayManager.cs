using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VpnGuard;

/// <summary>System-tray icon (Border Control shield that changes colour/glyph with status), menu and balloon/toast notifications.</summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MonitorEngine _engine;
    private readonly MainWindow _window;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _locItem;
    private readonly ToolStripMenuItem _kbItem;
    private readonly System.Collections.Generic.Dictionary<Level, Icon> _icons = new();

    public TrayManager(MonitorEngine engine, MainWindow window, Action exit)
    {
        _engine = engine;
        _window = window;

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Border Control", null, (_, _) => ShowWindow());
        menu.Items.Add("Re-check now", null, async (_, _) => await _engine.RefreshAsync(true));

        _autoItem = new ToolStripMenuItem("Sync time zone to VPN") { Checked = engine.State.AutoSync };
        _autoItem.Click += async (_, _) => await _engine.SetAutoSyncAsync(!_engine.State.AutoSync);
        menu.Items.Add(_autoItem);

        _locItem = new ToolStripMenuItem("Turn off Windows location") { Checked = engine.State.AutoLocation };
        _locItem.Click += async (_, _) => await _engine.SetAutoLocationAsync(!_engine.State.AutoLocation);
        menu.Items.Add(_locItem);

        _kbItem = new ToolStripMenuItem("Add VPN country keyboard") { Checked = engine.State.AutoKeyboard };
        _kbItem.Click += async (_, _) => await _engine.SetAutoKeyboardAsync(!_engine.State.AutoKeyboard);
        menu.Items.Add(_kbItem);

        menu.Items.Add("Restore everything now", null, (_, _) => _engine.RestoreNow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = IconFor(Level.Unknown),
            Text = "Border Control",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        engine.Changed += () => OnUi(UpdateIcon);
        engine.Notified += (title, msg, warn) => OnUi(() => Toast(title, msg, warn));
        UpdateIcon();
    }

    private void ShowWindow()
    {
        _window.Show();
        _window.WindowState = System.Windows.WindowState.Normal;
        _window.Activate();
    }

    private static void OnUi(Action action) =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(action);

    private void UpdateIcon()
    {
        _icon.Icon = IconFor(_engine.VpnConnected ? _engine.Overall : Level.Unknown);

        var tip = "Border Control – " + _engine.Headline;
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
        _statusItem.Text = _engine.Headline;
        _autoItem.Checked = _engine.State.AutoSync;
        _locItem.Checked = _engine.State.AutoLocation;
        _kbItem.Checked = _engine.State.AutoKeyboard;
    }

    private void Toast(string title, string message, bool warning)
    {
        if (message.Length > 250) message = message[..250] + "…";
        _icon.ShowBalloonTip(6000, title, message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    /// <summary>Status shields are drawn once per level, at every size Windows may ask for.</summary>
    private Icon IconFor(Level level)
    {
        if (!_icons.TryGetValue(level, out var icon))
        {
            icon = Branding.TrayIcon(level, SystemInformation.SmallIconSize);
            _icons[level] = icon;
        }
        return icon;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }
}
