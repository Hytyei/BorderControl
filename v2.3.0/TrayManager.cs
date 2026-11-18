using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BorderControl;

/// <summary>System-tray icon (Border Control shield that changes colour/glyph with status), menu and balloon/toast notifications.</summary>
public sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MonitorEngine _engine;
    private readonly MainWindow _window;
    private readonly ToolStripMenuItem _statusItem;
    private readonly System.Collections.Generic.Dictionary<Level, Icon> _icons = new();

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly TrafficMonitor _traffic;
    private readonly NotifyIcon _speedIcon;
    private Icon? _speedIconObj;
    private IntPtr _speedHandle;
    private string _speedKey = "";

    public TrayManager(MonitorEngine engine, MainWindow window, Action exit, UpdateService updates, Action restartForUpdate,
        TrafficMonitor traffic)
    {
        _engine = engine;
        _window = window;
        _traffic = traffic;

        var menu = new ContextMenuStrip();
        _statusItem = new ToolStripMenuItem("Starting…") { Enabled = false };
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Border Control", null, (_, _) => ShowWindow());
        menu.Items.Add("Re-check now", null, async (_, _) => await _engine.RefreshAsync(true));

        menu.Items.Add("Settings…", null, (_, _) => { ShowWindow(); _window.OpenSettings(); });

        menu.Items.Add("Restore everything now", null, (_, _) => _engine.RestoreNow());
        menu.Items.Add(new ToolStripSeparator());

        // Hidden until a verified update has been downloaded.
        var updateItem = new ToolStripMenuItem("Restart to update") { Visible = false, Font = new Font(menu.Font, FontStyle.Bold) };
        updateItem.Click += (_, _) => restartForUpdate();
        menu.Items.Add(updateItem);
        menu.Items.Add("Check for updates", null, async (_, _) => await updates.CheckAsync());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = IconFor(Level.Unknown),
            Text = "Border Control",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowWindow();

        // Second icon: live download speed (the status shield above stays as it is).
        _speedIcon = new NotifyIcon { Text = "Border Control", ContextMenuStrip = menu, Visible = false };
        _speedIcon.DoubleClick += (_, _) => { ShowWindow(); _window.OpenStats(); };
        traffic.Sampled += _ => OnUi(UpdateLive);

        updates.Ready += info => OnUi(() =>
        {
            updateItem.Text = $"Restart to update to {info.Version}";
            updateItem.Visible = true;
            if (_engine.State.Allows(Notice.Updates))
                _icon.ShowBalloonTip(8000, $"Border Control {info.Version} is ready",
                    "Downloaded and verified. Click here or use the tray menu to restart and update.", ToolTipIcon.Info);
        });
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (updates.Pending != null) ShowWindow();
        };

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
        _statusItem.Text = _engine.Headline;
        UpdateLive();
    }

    /// <summary>Every second: hover text on both icons, and the speed icon when it is switched on.</summary>
    private void UpdateLive()
    {
        var tip = HoverText();
        _icon.Text = tip;

        bool show = _engine.State.TraySpeed;
        if (_speedIcon.Visible != show) _speedIcon.Visible = show;
        if (!show) return;
        _speedIcon.Text = tip;

        var (value, unit) = TrafficMonitor.SpeedParts(_traffic.Latest.Down);
        var level = _engine.VpnConnected ? _engine.Overall : Level.Unknown;
        int size = SystemInformation.SmallIconSize.Width;
        bool light = LightTaskbar();
        var key = $"{value}|{unit}|{level}|{size}|{light}";
        if (key == _speedKey) return;   // nothing changed: keep the current icon
        _speedKey = key;

        using var bmp = Branding.RenderSpeed(size, value, unit, Branding.For(level), light);
        var handle = bmp.GetHicon();
        var icon = Icon.FromHandle(handle);
        _speedIcon.Icon = icon;
        _speedIconObj?.Dispose();
        if (_speedHandle != IntPtr.Zero) DestroyIcon(_speedHandle);
        _speedIconObj = icon;
        _speedHandle = handle;
    }

    private bool _light;
    private DateTime _lightChecked;

    /// <summary>Taskbar theme (Settings → Personalisation → Colours), re-read every 10 seconds.</summary>
    private bool LightTaskbar()
    {
        if ((DateTime.UtcNow - _lightChecked).TotalSeconds < 10) return _light;
        _lightChecked = DateTime.UtcNow;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _light = k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { _light = false; }
        return _light;
    }

    /// <summary>
    /// Time (your real clock), overall status, then the lines picked in Settings → Tray.
    /// Windows allows 127 characters.
    /// </summary>
    private string HoverText()
    {
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, _engine.RealTimeZone);
        var lines = new List<string> { $"Border Control · {now:HH:mm}", _engine.Headline };
        foreach (var extra in _engine.State.HoverExtras)
        {
            var line = extra switch
            {
                "Leaks" => LeakLine(),
                "Speed" => $"↓ {TrafficMonitor.Speed(_traffic.Latest.Down)}   ↑ {TrafficMonitor.Speed(_traffic.Latest.Up)}",
                "Traffic" => TodayLine(),
                "Location" => _engine.VpnGeo is { } g ? $"Exit: {g.City}, {g.CountryCode}" : "Exit: no VPN",
                "IP" => $"IP: {(_engine.VpnGeo ?? _engine.RealGeo)?.Ip ?? "unknown"}",
                _ => null,
            };
            if (line != null) lines.Add(line);
        }
        var text = string.Join("\n", lines);
        return text.Length <= 127 ? text : text[..126] + "…";
    }

    private string LeakLine()
    {
        if (!_engine.VpnConnected) return "Leaks: no VPN connected";
        var checks = new[] { (_engine.Ip, "IP"), (_engine.Dns, "DNS"), (_engine.Ipv6, "IPv6"), (_engine.Udp, "WebRTC") };
        var bad = checks.Where(c => c.Item1.Level == Level.Bad).Select(c => c.Item2).ToList();
        if (bad.Count > 0) return "LEAK: " + string.Join(", ", bad);
        if (checks.Any(c => c.Item1.Level == Level.Unknown)) return "Leaks: checking…";
        var warn = checks.Where(c => c.Item1.Level == Level.Warn).Select(c => c.Item2).ToList();
        return warn.Count > 0 ? $"Leaks: none (check {string.Join(", ", warn)})" : "Leaks: none found";
    }

    private string TodayLine()
    {
        var d = _traffic.Day(_traffic.RealToday);
        return $"Today: ↓ {TrafficMonitor.Size(d.Down)}  ↑ {TrafficMonitor.Size(d.Up)}";
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
        _speedIcon.Visible = false;
        _speedIcon.Dispose();
        _speedIconObj?.Dispose();
        if (_speedHandle != IntPtr.Zero) DestroyIcon(_speedHandle);
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }
}
