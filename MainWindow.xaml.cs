using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace VpnGuard;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private const int GWL_STYLE = -16;
    private const long WS_SYSMENU = 0x00080000;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private static readonly Geometry GlyphOk = Geometry.Parse("M4.5,10.5 L8.5,14.5 L15.5,6");
    private static readonly Geometry GlyphWarn = Geometry.Parse("M10,4.5 L10,11.2 M10,15.3 L10,15.4");
    private static readonly Geometry GlyphBad = Geometry.Parse("M6,6 L14,14 M14,6 L6,14");
    private static readonly Geometry GlyphNone = Geometry.Parse("M5.5,10 L14.5,10");

    private readonly MonitorEngine _engine;
    private readonly ObservableCollection<string> _log = new();
    private readonly DispatcherTimer _timer;
    private readonly SolidColorBrush _status = new(Palette.Unknown);

    private bool _allowClose;
    private bool _busy;
    private bool _sheetOpen;
    private bool _spinning;
    private Level? _shownLevel;
    private string _shownHeadline = "";
    private StatusItem? _detailItem;
    private DispatcherTimer? _copyReset;

    public MainWindow(MonitorEngine engine)
    {
        // Animated brush shared by the ring, shield and glyph (DynamicResource keeps it unfrozen).
        Resources["Status"] = _status;
        InitializeComponent();
        _engine = engine;

        StatusList.ItemsSource = engine.Items;
        LogList.ItemsSource = _log;
        _log.CollectionChanged += (_, _) => LogEmpty.Visibility = _log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        engine.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            RefreshHeader();
            if (_sheetOpen) ShowDetail();
        });
        engine.LogLine += line => Dispatcher.InvokeAsync(() =>
        {
            _log.Insert(0, line);
            while (_log.Count > 80) _log.RemoveAt(_log.Count - 1);
        });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => TickClocks();
        _timer.Start();

        SourceInitialized += (_, _) => ApplyBackdrop();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { PlayEntrance(); StartAmbient(); }
            else StopAmbient(); // nothing animates while the window is hidden in the tray
        };
        KeyDown += OnKeyDown;

        RefreshHeader();
        TickClocks();
    }

    // ───────────────────────── window chrome ─────────────────────────

    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Without this, DWM paints faint native caption buttons through the glass behind ours.
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));

        int on = 1, round = 2, mica = 2;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // Mica needs Windows 11 22H2+. Elsewhere fall back to a solid background.
        bool micaOk = Environment.OSVersion.Version.Build >= 22621
                      && DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref mica, sizeof(int)) == 0;
        if (micaOk && HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = Colors.Transparent;
        else
            Root.Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    public void ForceClose()
    {
        _allowClose = true;
        _timer.Stop();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing only hides the window; Border Control keeps watching from the tray.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    // ───────────────────────── motion ─────────────────────────

    /// <summary>Sections rise in one after another when the window opens (occasional, so it may animate).</summary>
    private void PlayEntrance()
    {
        FrameworkElement[] sections = { Hero, Clocks, Checks, Footer };
        for (int i = 0; i < sections.Length; i++)
        {
            var el = sections[i];
            if (Motion.Reduced)
            {
                Motion.To(el, OpacityProperty, 1, 160, Motion.Ease, from: 0);
                continue;
            }
            int delay = i * 55;
            Motion.To(el, OpacityProperty, 1, 340, Motion.EaseOut, from: 0, delayMs: delay);
            Motion.To(el.RenderTransform, TranslateTransform.YProperty, 0, 420, Motion.EaseOut, from: 14, delayMs: delay);
        }
    }

    private void StartAmbient()
    {
        Motion.Drift(Blob1T, TranslateTransform.XProperty, -30, 40, 13);
        Motion.Drift(Blob1T, TranslateTransform.YProperty, -12, 22, 17);
        Motion.Drift(Blob2T, TranslateTransform.XProperty, 30, -40, 15);
        Motion.Drift(Blob2T, TranslateTransform.YProperty, 18, -14, 11);
        UpdateRingMotion();
    }

    private void StopAmbient()
    {
        Motion.Stop(Blob1T, TranslateTransform.XProperty);
        Motion.Stop(Blob1T, TranslateTransform.YProperty);
        Motion.Stop(Blob2T, TranslateTransform.XProperty);
        Motion.Stop(Blob2T, TranslateTransform.YProperty);
        Motion.Stop(RingGlow, DropShadowEffect.OpacityProperty);
        Motion.Stop(ArcSpin, RotateTransform.AngleProperty);
        _spinning = false;
    }

    private bool Checking =>
        _busy || (_engine.VpnConnected && _engine.Items.Any(i => i != _engine.Vpn && i.Level == Level.Unknown));

    /// <summary>Fast spinner while checks run; a slow breathing glow once everything is fine.</summary>
    private void UpdateRingMotion()
    {
        if (!IsVisible) return;
        bool checking = Checking;

        if (checking)
        {
            Motion.To(Arc, OpacityProperty, 0.9, 180, Motion.EaseOut);
            Motion.To(Ring, OpacityProperty, 0.25, 180, Motion.EaseOut);
            if (!_spinning && !Motion.Reduced)
            {
                _spinning = true;
                ArcSpin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        else
        {
            Motion.To(Arc, OpacityProperty, 0, 220, Motion.EaseOut, done: () =>
            {
                if (Checking) return;
                ArcSpin.BeginAnimation(RotateTransform.AngleProperty, null);
                _spinning = false;
            });
            Motion.To(Ring, OpacityProperty, 0.9, 260, Motion.EaseOut);
        }

        var level = DisplayLevel;
        if (!checking && level == Level.Ok && !Motion.Reduced)
            Motion.Drift(RingGlow, DropShadowEffect.OpacityProperty, 0.35, 0.8, 2.6);
        else
            Motion.To(RingGlow, DropShadowEffect.OpacityProperty, level == Level.Bad ? 0.85 : 0.5, 300, Motion.Ease);
    }

    private Level DisplayLevel => _engine.VpnConnected ? _engine.Overall : Level.Unknown;

    // ───────────────────────── state → view ─────────────────────────

    private void RefreshHeader()
    {
        var level = DisplayLevel;
        bool checking = Checking;

        string headline = !_engine.VpnConnected ? "Unprotected"
            : checking && level == Level.Unknown ? "Checking…"
            : level switch
            {
                Level.Bad => "Leak detected",
                Level.Warn => "Protected, with warnings",
                Level.Ok => "Protected",
                _ => "Checking…",
            };

        string sub;
        if (_engine.VpnConnected && _engine.VpnGeo is { } g)
            sub = $"{g.CountryCode} · {Join(g.City, g.Country)} · {g.Ip}";
        else if (_engine.VpnConnected)
            sub = "Finding your VPN exit…";
        else if (_engine.RealGeo is { } r)
            sub = $"Websites see your real IP {r.Ip} · {r.Country}";
        else
            sub = "Connect a VPN to hide your location";

        if (headline != _shownHeadline)
        {
            _shownHeadline = headline;
            HeadlineText.Text = headline;
            SubHeadline.Text = sub;
            Crossfade(HeadlineHost, HeadlineBlur, HeadlineShift, 4);
        }
        else
        {
            SubHeadline.Text = sub;
        }

        if (_shownLevel != level)
        {
            bool first = _shownLevel == null;
            _shownLevel = level;
            SetStatusColor(Palette.For(level), instant: first);
            Glyph.Data = level switch
            {
                Level.Ok => GlyphOk,
                Level.Warn => GlyphWarn,
                Level.Bad => GlyphBad,
                _ => GlyphNone,
            };
            if (!first) PopGlyph();
        }

        AutoToggle.IsChecked = _engine.State.AutoSync;
        LocationToggle.IsChecked = _engine.State.AutoLocation;
        KeyboardToggle.IsChecked = _engine.State.AutoKeyboard;
        UpdateRingMotion();
    }

    private void SetStatusColor(Color c, bool instant)
    {
        int ms = instant ? 1 : 520;
        Motion.Color(_status, c, ms);
        Motion.Color(ShieldFill, WithAlpha(c, 0x26), ms);
        RingGlow.BeginAnimation(DropShadowEffect.ColorProperty,
            new ColorAnimation(c, TimeSpan.FromMilliseconds(Motion.Reduced ? 1 : ms)) { EasingFunction = Motion.Ease });

        if (SysLabel.Text.EndsWith("synced"))
            Motion.Color(SysTileStroke, WithAlpha(c, 0x66), ms);

        var core = CoreBrush.GradientStops;
        Motion.Color(core[0], WithAlpha(c, 0x2C), ms);
        Motion.Color(core[1], WithAlpha(c, 0x00), ms);

        // Ambient light takes a little longer than the ring so the change feels like it spreads outwards.
        var b1 = ((RadialGradientBrush)Blob1.Fill).GradientStops;
        var b2 = ((RadialGradientBrush)Blob2.Fill).GradientStops;
        int slow = instant ? 1 : 900;
        Motion.Color(b1[0], WithAlpha(c, 0x48), slow);
        Motion.Color(b1[1], WithAlpha(c, 0x00), slow);
        Motion.Color(b2[0], WithAlpha(c, 0x30), slow);
        Motion.Color(b2[1], WithAlpha(c, 0x00), slow);
    }

    /// <summary>New glyph sharpens into place: blur masks the swap so it reads as one shape changing.</summary>
    private void PopGlyph()
    {
        if (Motion.Reduced)
        {
            Motion.To(Glyph, OpacityProperty, 1, 160, Motion.Ease, from: 0);
            return;
        }
        Motion.To(Glyph, OpacityProperty, 1, 200, Motion.EaseOut, from: 0.2);
        Motion.To(GlyphBlur, BlurEffect.RadiusProperty, 0, 260, Motion.EaseOut, from: 6);
        Motion.To(GlyphScale, ScaleTransform.ScaleXProperty, 1, 300, Motion.EaseOut, from: 0.8);
        Motion.To(GlyphScale, ScaleTransform.ScaleYProperty, 1, 300, Motion.EaseOut, from: 0.8);
    }

    private static void Crossfade(UIElement host, BlurEffect blur, TranslateTransform shift, double dy)
    {
        if (Motion.Reduced)
        {
            Motion.To(host, OpacityProperty, 1, 160, Motion.Ease, from: 0);
            return;
        }
        Motion.To(host, OpacityProperty, 1, 220, Motion.EaseOut, from: 0);
        Motion.To(blur, BlurEffect.RadiusProperty, 0, 240, Motion.EaseOut, from: 5);
        Motion.To(shift, TranslateTransform.YProperty, 0, 260, Motion.EaseOut, from: dy);
    }

    private void TickClocks()
    {
        var utc = DateTime.UtcNow;
        var culture = CultureInfo.CurrentCulture;

        var real = TimeZoneInfo.ConvertTime(utc, _engine.RealTimeZone);
        RealHm.Text = real.ToString("HH:mm", culture);
        RealSec.Text = real.ToString(":ss", culture);
        RealZone.Text = ShortZone(_engine.RealTimeZone);
        RealZone.ToolTip = _engine.RealTimeZone.DisplayName;

        var local = TimeZoneInfo.Local;
        var sys = TimeZoneInfo.ConvertTime(utc, local);
        SysHm.Text = sys.ToString("HH:mm", culture);
        SysSec.Text = sys.ToString(":ss", culture);
        SysZone.Text = ShortZone(local);
        SysZone.ToolTip = local.DisplayName;

        bool synced = local.Id != _engine.RealTimeZone.Id;
        string label = synced ? "Apps see · synced" : "Apps see";
        if (SysLabel.Text != label)
        {
            SysLabel.Text = label;
            Motion.Color(SysTileStroke, synced ? WithAlpha(_status.Color, 0x66) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 400);
        }
    }

    private static string ShortZone(TimeZoneInfo tz)
    {
        var off = tz.GetUtcOffset(DateTime.UtcNow);
        string sign = off < TimeSpan.Zero ? "−" : "+";
        string offset = off.Minutes == 0
            ? $"UTC{sign}{Math.Abs(off.Hours)}"
            : $"UTC{sign}{Math.Abs(off.Hours)}:{Math.Abs(off.Minutes):00}";
        return $"{tz.Id.Replace(" Standard Time", "")} · {offset}";
    }

    // ───────────────────────── actions ─────────────────────────

    private async void AutoToggle_Click(object sender, RoutedEventArgs e) =>
        await _engine.SetAutoSyncAsync(AutoToggle.IsChecked == true);

    private async void LocationToggle_Click(object sender, RoutedEventArgs e) =>
        await _engine.SetAutoLocationAsync(LocationToggle.IsChecked == true);

    private async void KeyboardToggle_Click(object sender, RoutedEventArgs e) =>
        await _engine.SetAutoKeyboardAsync(KeyboardToggle.IsChecked == true);

    private async void Recheck_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        RecheckButton.IsEnabled = false;
        if (!Motion.Reduced)
            RecheckSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.8)) { RepeatBehavior = RepeatBehavior.Forever });
        RefreshHeader();
        try
        {
            await _engine.RefreshAsync(force: true);
        }
        finally
        {
            _busy = false;
            RecheckButton.IsEnabled = true;
            // finish the current turn instead of snapping back
            RecheckSpin.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(360, TimeSpan.FromMilliseconds(Motion.Reduced ? 1 : 320)) { EasingFunction = Motion.EaseOut });
            RefreshHeader();
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        _engine.RestoreNow();
        RefreshHeader();
        TickClocks();
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        _detailItem = null;
        SheetTitle.Text = "Activity";
        SheetStatus.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        CopyLabel.Text = "Copy all";
        ResetCopyFeedback();
        OpenSheet();
    }

    /// <summary>Clicking a check opens its full, selectable message.</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StatusItem item) return;
        _detailItem = item;
        LogPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        CopyLabel.Text = "Copy";
        ResetCopyFeedback();
        ShowDetail();
        DetailPanel.ScrollToTop();
        OpenSheet();
    }

    private void ShowDetail()
    {
        if (_detailItem is not { } item) return;
        SheetTitle.Text = item.Title;
        SheetStatus.Text = item.Level switch
        {
            Level.Ok => "OK",
            Level.Warn => "Warning",
            Level.Bad => "Leak / problem",
            _ => "Not checked yet",
        };
        SheetStatus.Foreground = new SolidColorBrush(PastelFor(item.Level));
        SheetStatus.Visibility = Visibility.Visible;
        if (DetailText.Text != item.Detail) DetailText.Text = item.Detail;
    }

    private static Color PastelFor(Level level) => level switch
    {
        Level.Ok => Color.FromRgb(0x8E, 0xE6, 0xB8),
        Level.Warn => Color.FromRgb(0xFF, 0xD5, 0x8A),
        Level.Bad => Color.FromRgb(0xFF, 0x9C, 0x9C),
        _ => Color.FromRgb(0xA9, 0xB0, 0xC2),
    };

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        string text = _detailItem is { } item
            ? $"{item.Title} – {SheetStatus.Text}{Environment.NewLine}{item.Detail}"
            : string.Join(Environment.NewLine, _log);
        if (text.Length == 0) return;

        try { Clipboard.SetText(text); }
        catch { return; } // clipboard busy – nothing copied, so no "Copied" feedback

        // Brief confirmation, then back to the normal label.
        CopyIcon.Text = "";
        CopyLabel.Text = "Copied";
        _copyReset?.Stop();
        _copyReset = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        _copyReset.Tick += (_, _) => ResetCopyFeedback();
        _copyReset.Start();
    }

    private void ResetCopyFeedback()
    {
        _copyReset?.Stop();
        CopyIcon.Text = "";
        CopyLabel.Text = _detailItem == null ? "Copy all" : "Copy";
    }
    private void SheetClose_Click(object sender, RoutedEventArgs e) => CloseSheet(animate: true);
    private void Scrim_MouseDown(object sender, MouseButtonEventArgs e) => CloseSheet(animate: true);

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Keyboard actions are instant – no animation.
        if (e.Key == Key.Escape && _sheetOpen) CloseSheet(animate: false);
    }

    private void OpenSheet()
    {
        if (_sheetOpen) return;
        _sheetOpen = true;
        Sheet.Visibility = Visibility.Visible;
        Scrim.IsHitTestVisible = true;

        if (Motion.Reduced)
        {
            SheetShift.BeginAnimation(TranslateTransform.YProperty, null);
            SheetShift.Y = 0;
            Motion.To(Sheet, OpacityProperty, 1, 160, Motion.Ease, from: 0);
            Motion.To(Scrim, OpacityProperty, 0.5, 160, Motion.Ease);
            return;
        }
        Sheet.Opacity = 1;
        Motion.To(SheetShift, TranslateTransform.YProperty, 0, 420, Motion.Drawer);
        Motion.To(Scrim, OpacityProperty, 0.5, 300, Motion.Ease);
    }

    private void CloseSheet(bool animate)
    {
        if (!_sheetOpen) return;
        _sheetOpen = false;
        Scrim.IsHitTestVisible = false;

        if (!animate || Motion.Reduced)
        {
            Motion.Stop(SheetShift, TranslateTransform.YProperty);
            Motion.Stop(Scrim, OpacityProperty);
            SheetShift.Y = Sheet.ActualHeight + 20;
            Scrim.Opacity = 0;
            Sheet.Visibility = Visibility.Collapsed;
            return;
        }
        // Exit is quicker than the entrance.
        Motion.To(Scrim, OpacityProperty, 0, 200, Motion.Ease);
        Motion.To(SheetShift, TranslateTransform.YProperty, Sheet.ActualHeight + 20, 240, Motion.EaseOut,
            done: () => { if (!_sheetOpen) Sheet.Visibility = Visibility.Collapsed; });
    }

    // ───────────────────────── helpers ─────────────────────────

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static string Join(params string[] parts) =>
        string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));
}

/// <summary>Status colours shared by the window, list and tray icons.</summary>
public static class Palette
{
    public static readonly Color Ok = Color.FromRgb(0x3D, 0xDC, 0x97);
    public static readonly Color Warn = Color.FromRgb(0xFF, 0xB0, 0x20);
    public static readonly Color Bad = Color.FromRgb(0xFF, 0x5C, 0x5C);
    public static readonly Color Unknown = Color.FromRgb(0x7C, 0x83, 0x96);

    public static Color For(Level level) => level switch
    {
        Level.Ok => Ok,
        Level.Warn => Warn,
        Level.Bad => Bad,
        _ => Unknown,
    };
}
