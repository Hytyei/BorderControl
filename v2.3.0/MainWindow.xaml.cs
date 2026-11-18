using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace BorderControl;

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
    private string? _notesText;
    private DispatcherTimer? _copyReset;

    private readonly UpdateService _updates;
    private readonly Action _restartForUpdate;
    private bool _bannerDismissed;
    private bool _bannerShown;

    private readonly TrafficMonitor _traffic;
    private bool _statsOpen;

    public MainWindow(MonitorEngine engine, UpdateService updates, Action restartForUpdate, TrafficMonitor traffic)
    {
        // Animated brush shared by the ring, shield and glyph (DynamicResource keeps it unfrozen).
        Resources["Status"] = _status;
        InitializeComponent();
        _engine = engine;
        _updates = updates;
        _restartForUpdate = restartForUpdate;
        _traffic = traffic;
        // Live speed: only drawn while the window is visible.
        traffic.Sampled += sample => Dispatcher.InvokeAsync(() => { if (IsVisible) OnSample(sample); });
        Loaded += (_, _) => { BuildRing(); OnSample(_traffic.Latest); };
        updates.Ready += _ => Dispatcher.InvokeAsync(() =>
        {
            _bannerDismissed = false;
            if (IsVisible) ShowUpdateBanner();
        });

        StatusList.ItemsSource = engine.Items;
        LogList.ItemsSource = _log;
        _log.CollectionChanged += (_, _) => LogEmpty.Visibility = _log.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        engine.Changed += () => Dispatcher.InvokeAsync(() =>
        {
            RefreshHeader();
            if (_sheetOpen) ShowDetail();
            UpdateRingSegments();
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
            if ((bool)e.NewValue)
            {
                PlayEntrance();
                StartAmbient();
                if (_updates.Pending != null && !_bannerDismissed) ShowUpdateBanner();
            }
            else StopAmbient(); // nothing animates while the window is hidden in the tray
        };
        KeyDown += OnKeyDown;

        ApplyTextSmoothing(engine.State.TextSmoothing);
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
        FrameworkElement[] sections = { Hero, Clocks, SpeedCard, Checks, Footer };
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
        string? flag = null;
        if (_engine.VpnConnected && _engine.VpnGeo is { } g)
        {
            sub = $"{Join(g.City, g.Country)} · {g.Ip}";
            flag = g.CountryCode;
        }
        else if (_engine.VpnConnected)
            sub = "Finding your VPN exit…";
        else if (_engine.RealGeo is { } r)
        {
            sub = $"Websites see your real IP {r.Ip}";
            flag = r.CountryCode;
        }
        else
            sub = "Connect a VPN to hide your location";
        HeroFlag.Source = Flags.For(flag);

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

        SyncSettingsSwitches();
        UpdateRingMotion();
    }

    // ───────────────────────── status ring (one segment per check) ─────────────────────────

    private readonly List<SolidColorBrush> _segmentBrushes = new();

    private static readonly Color DownColor = Color.FromRgb(0x8E, 0xE6, 0xB8);
    private static readonly Color UpColor = Color.FromRgb(0x8E, 0xC5, 0xFF);

    /// <summary>Draws the ring as arcs, one per check, so the whole result reads at a glance.</summary>
    private void BuildRing()
    {
        Ring.Children.Clear();
        _segmentBrushes.Clear();
        var items = _engine.Items;
        int n = items.Count;
        double cx = 70, cy = 70, r = 60, gap = 7;
        double span = 360.0 / n;
        for (int i = 0; i < n; i++)
        {
            double a0 = -90 + i * span + gap / 2, a1 = -90 + (i + 1) * span - gap / 2;
            var fig = new PathFigure { StartPoint = Polar(cx, cy, r, a0), IsClosed = false };
            fig.Segments.Add(new ArcSegment(Polar(cx, cy, r, a1), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
            var brush = new SolidColorBrush(SegmentColor(items[i].Level));
            _segmentBrushes.Add(brush);
            var path = new System.Windows.Shapes.Path
            {
                Data = new PathGeometry(new[] { fig }),
                Stroke = brush,
                StrokeThickness = 3.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                ToolTip = items[i].Title,
            };
            Ring.Children.Add(path);
        }
    }

    private void UpdateRingSegments()
    {
        var items = _engine.Items;
        if (_segmentBrushes.Count != items.Count) { BuildRing(); return; }
        for (int i = 0; i < items.Count; i++)
        {
            var target = SegmentColor(items[i].Level);
            if (_segmentBrushes[i].Color != target) Motion.Color(_segmentBrushes[i], target, 450);
        }
    }

    private Color SegmentColor(Level level) => !_engine.VpnConnected && level == Level.Unknown
        ? Color.FromArgb(0x40, 0xA9, 0xB0, 0xC2)
        : level switch
        {
            Level.Ok => Color.FromRgb(0x8E, 0xE6, 0xB8),
            Level.Warn => Color.FromRgb(0xFF, 0xD5, 0x8A),
            Level.Bad => Color.FromRgb(0xFF, 0x9C, 0x9C),
            _ => Color.FromArgb(0x59, 0xA9, 0xB0, 0xC2),
        };

    private static Point Polar(double cx, double cy, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    // ───────────────────────── live speed ─────────────────────────

    private double _sparkMax = 64 * 1024, _bigMax = 64 * 1024;

    private void OnSample(SpeedSample s)
    {
        var (v, u) = TrafficMonitor.SpeedParts(s.Down);
        DownValue.Text = v;
        DownUnit.Text = $" {u}/s";
        UpText.Text = " " + TrafficMonitor.Speed(s.Up);
        TodayText.Text = TrafficMonitor.Size(Total(_traffic.Day(_traffic.RealToday)));

        var history = _traffic.History();
        DrawChart(history, 60, Spark, SparkShift, SparkDownFill, SparkDown, SparkUp, ref _sparkMax);
        if (_statsOpen) RefreshStatsLive(history);
    }

    private static long Total(DayTraffic d) => d.Down + d.Up;

    /// <summary>
    /// Area (download) and line (upload) of the last <paramref name="count"/> seconds. The newest point starts one step
    /// past the right edge and slides in over the next second (linear: constant motion), so the chart scrolls smoothly.
    /// </summary>
    private static void DrawChart(IReadOnlyList<SpeedSample> all, int count, Canvas host, TranslateTransform shift,
        System.Windows.Shapes.Path fill, System.Windows.Shapes.Path down, System.Windows.Shapes.Path up, ref double scale)
    {
        double w = host.ActualWidth, h = host.ActualHeight;
        if (w < 10 || h < 10) return;
        var data = all.Skip(Math.Max(0, all.Count - count)).ToList();
        if (data.Count < 2) return;

        double peak = data.Max(d => Math.Max(d.Down, d.Up));
        double target = Math.Max(32 * 1024, peak * 1.15);
        // Scale rises at once but falls slowly, so the chart doesn't jump around.
        scale = target > scale ? target : scale + (target - scale) * 0.12;

        double step = w / (count - 1);
        bool animate = !Motion.Reduced;
        double xEnd = animate ? w + step : w;
        double sc = scale;
        double Y(double v) => h - 1.5 - Math.Min(1, v / sc) * (h - 4);
        double X(int i) => xEnd - (data.Count - 1 - i) * step;

        var downGeo = new StreamGeometry();
        using (var ctx = downGeo.Open())
        {
            ctx.BeginFigure(new Point(X(0), Y(data[0].Down)), false, false);
            for (int i = 1; i < data.Count; i++) ctx.LineTo(new Point(X(i), Y(data[i].Down)), true, true);
        }
        var fillGeo = new StreamGeometry();
        using (var ctx = fillGeo.Open())
        {
            ctx.BeginFigure(new Point(X(0), h), true, true);
            for (int i = 0; i < data.Count; i++) ctx.LineTo(new Point(X(i), Y(data[i].Down)), true, true);
            ctx.LineTo(new Point(X(data.Count - 1), h), true, true);
        }
        var upGeo = new StreamGeometry();
        using (var ctx = upGeo.Open())
        {
            ctx.BeginFigure(new Point(X(0), Y(data[0].Up)), false, false);
            for (int i = 1; i < data.Count; i++) ctx.LineTo(new Point(X(i), Y(data[i].Up)), true, true);
        }
        downGeo.Freeze(); fillGeo.Freeze(); upGeo.Freeze();
        down.Data = downGeo;
        fill.Data = fillGeo;
        up.Data = upGeo;

        if (animate)
            shift.BeginAnimation(TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, -step, TimeSpan.FromMilliseconds(1000)));
    }

    // ───────────────────────── traffic stats sheet ─────────────────────────

    /// <summary>The app is in English, so dates use English names and the Gregorian calendar whatever the Windows culture.</summary>
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");

    private void SpeedCard_Click(object sender, MouseButtonEventArgs e) => OpenStats();

    /// <summary>Opens the traffic stats sheet (also from the tray speed icon).</summary>
    public void OpenStats()
    {
        _detailItem = null;
        _notesText = null;
        SheetTitle.Text = "Speed & traffic";
        SheetStatus.Text = _engine.VpnConnected ? $"Measured on {_traffic.AdapterName}" : "No VPN – totals count VPN traffic only";
        SheetStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x92, 0xA5));
        SheetStatus.Visibility = Visibility.Visible;
        LogPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Visible;
        CopyButton.Visibility = Visibility.Collapsed;
        Sheet.Height = Math.Min(660, ActualHeight - 60);
        _statsOpen = true;

        BuildTotals();
        StatsPanel.ScrollToTop();
        OpenSheet();
        // Bars need the sheet's final width: build them once layout has run.
        Dispatcher.InvokeAsync(() => { BuildBars(); RefreshStatsLive(_traffic.History()); },
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RefreshStatsLive(IReadOnlyList<SpeedSample> history)
    {
        var last = _traffic.Latest;
        BigDown.Text = TrafficMonitor.Speed(last.Down);
        BigUp.Text = TrafficMonitor.Speed(last.Up);
        DrawChart(history, 120, BigChart, BigShift, BigDownFill, BigDownLine, BigUpLine, ref _bigMax);
        BigScale.Text = TrafficMonitor.Speed(_bigMax);
        if (history.Count > 0)
            PeakText.Text = $"peak ↓ {TrafficMonitor.Speed(history.Max(h => h.Down))}  ↑ {TrafficMonitor.Speed(history.Max(h => h.Up))}";

        if (_engine.VpnConnected && _traffic.SessionStart is { } start)
        {
            SessDown.Text = TrafficMonitor.Size(_traffic.SessionDown);
            SessUp.Text = TrafficMonitor.Size(_traffic.SessionUp);
            SessTime.Text = TrafficMonitor.Duration((long)(DateTime.UtcNow - start).TotalSeconds);
        }
        else
        {
            SessDown.Text = SessUp.Text = SessTime.Text = "–";
        }
    }

    private void BuildTotals()
    {
        TotalsGrid.Children.Clear();
        foreach (var (label, days) in new[] { ("Today", 1), ("Last 7 days", 7), ("Last 30 days", 30), ("All time", 0) })
        {
            var t = _traffic.Total(days);
            var card = new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(4), Padding = new Thickness(12, 9, 12, 10) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = (Brush)FindResource("Muted") });
            var big = new TextBlock { FontSize = 16, Margin = new Thickness(0, 2, 0, 0) };
            big.Inlines.Add(new System.Windows.Documents.Run("↓ ") { Foreground = new SolidColorBrush(DownColor), FontSize = 12 });
            big.Inlines.Add(new System.Windows.Documents.Run(TrafficMonitor.Size(t.Down)));
            sp.Children.Add(big);
            var small = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("Faint"), Margin = new Thickness(0, 1, 0, 0) };
            small.Inlines.Add(new System.Windows.Documents.Run("↑ ") { Foreground = new SolidColorBrush(UpColor) });
            small.Inlines.Add(new System.Windows.Documents.Run($"{TrafficMonitor.Size(t.Up)} · {TrafficMonitor.Duration(t.Seconds)}"));
            sp.Children.Add(small);
            card.Child = sp;
            TotalsGrid.Children.Add(card);
        }
        var since = _traffic.Store.Since?.ToLocalTime() ?? DateTime.Now;
        StatsSince.Text = $"Counting VPN traffic since {since.ToString("d MMM yyyy", En)} · {_traffic.Store.Sessions} sessions";
    }

    /// <summary>Stacked bars per day (download below, upload on top). They grow up from the baseline when the sheet opens.</summary>
    private void BuildBars()
    {
        Bars.Children.Clear(); Bars.ColumnDefinitions.Clear();
        BarLabels.Children.Clear(); BarLabels.ColumnDefinitions.Clear();
        var today = _traffic.RealToday;
        var days = Enumerable.Range(0, 14).Select(i => today.AddDays(i - 13)).ToList();
        var data = days.Select(d => _traffic.Day(d)).ToList();
        double max = Math.Max(1, data.Max(d => (double)d.Down + d.Up));
        double h = Bars.Height - 2;

        for (int i = 0; i < days.Count; i++)
        {
            Bars.ColumnDefinitions.Add(new ColumnDefinition());
            BarLabels.ColumnDefinitions.Add(new ColumnDefinition());
            var d = data[i];
            double downH = d.Down / max * h, upH = d.Up / max * h;

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(3, 0, 3, 0) };
            bool any = d.Down + d.Up > 0;
            stack.Children.Add(new Border
            {
                Height = Math.Max(any ? 1.5 : 0, upH),
                Background = new SolidColorBrush(UpColor),
                CornerRadius = new CornerRadius(3, 3, 0, 0),
                Opacity = 0.9,
            });
            stack.Children.Add(new Border
            {
                Height = any ? Math.Max(2, downH) : 2,
                Background = any ? new SolidColorBrush(DownColor) : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                CornerRadius = upH < 1 ? new CornerRadius(3, 3, 0, 0) : new CornerRadius(0),
            });
            stack.ToolTip = $"{days[i].ToString("ddd d MMM", En)}\n↓ {TrafficMonitor.Size(d.Down)}   ↑ {TrafficMonitor.Size(d.Up)}\nConnected {TrafficMonitor.Duration(d.Seconds)}";
            stack.Background = Brushes.Transparent;
            Grid.SetColumn(stack, i);
            Bars.Children.Add(stack);

            var scale = new ScaleTransform(1, Motion.Reduced ? 1 : 0.05);
            stack.RenderTransformOrigin = new Point(0.5, 1);
            stack.RenderTransform = scale;
            if (!Motion.Reduced)
                Motion.To(scale, ScaleTransform.ScaleYProperty, 1, 460, Motion.EaseOut, from: 0.05, delayMs: 120 + i * 25);

            var label = new TextBlock
            {
                Text = days[i].ToString("ddd", En)[..2],
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = i == days.Count - 1 ? (Brush)FindResource("Ink") : (Brush)FindResource("Faint"),
            };
            Grid.SetColumn(label, i);
            BarLabels.Children.Add(label);
        }
    }

    private void StatsReset_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "Clear all saved traffic totals? This can't be undone.", "Reset stats",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        _traffic.Reset();
        _engine.Note("Traffic stats reset");
        BuildTotals();
        BuildBars();
    }

    // ───────────────────────── notification & tray settings ─────────────────────────

    private void Notify_Click(object sender, RoutedEventArgs e) => _engine.SaveSettings(st =>
    {
        st.NotifyConnection = NotifyConnToggle.IsChecked == true;
        st.NotifyLeaks = NotifyLeakToggle.IsChecked == true;
        st.NotifyChanges = NotifyChangeToggle.IsChecked == true;
        st.NotifyKillSwitch = NotifyKillToggle.IsChecked == true;
        st.NotifyUpdates = NotifyUpdateToggle.IsChecked == true;
    });

    private void TraySpeed_Click(object sender, RoutedEventArgs e) =>
        _engine.SaveSettings(st => st.TraySpeed = TraySpeedToggle.IsChecked == true);

    private void Hover_Click(object sender, RoutedEventArgs e) => _engine.SaveSettings(st =>
        st.HoverExtras = new[] { HoverLeaks, HoverSpeed, HoverTraffic, HoverLocation, HoverIp }
            .Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList());

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
        var culture = En;

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

    // ───────────────────────── settings ─────────────────────────

    private sealed record KillAppView(string Name, string Path, ImageSource? Icon);

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>Opens the settings sheet (also used by the tray menu).</summary>
    public void OpenSettings()
    {
        _detailItem = null;
        _notesText = null;
        SheetTitle.Text = "Settings";
        SheetStatus.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        StatsPanel.Visibility = Visibility.Collapsed;
        _statsOpen = false;
        CopyButton.Visibility = Visibility.Collapsed;
        Sheet.Height = Math.Min(660, ActualHeight - 60);

        SyncSettingsSwitches();
        StartupToggle.IsChecked = StartupTask.IsEnabled();
        UpdateToggle.IsChecked = _engine.State.AutoUpdate;
        VersionText.Text = $"Version {UpdateService.Current}";
        UpdateStatus.Text = _updates.Pending is { } p ? $"Version {p.Version} is ready – restart from the popup." : "";
        RunningPicker.Visibility = Visibility.Collapsed;
        RefreshKillList();
        SettingsPanel.ScrollToTop();
        OpenSheet();
    }

    private void SyncSettingsSwitches()
    {
        var st = _engine.State;
        AutoToggle.IsChecked = st.AutoSync;
        LocationToggle.IsChecked = st.AutoLocation;
        KeyboardToggle.IsChecked = st.AutoKeyboard;
        RegionToggle.IsChecked = st.AutoRegion;
        FlushToggle.IsChecked = st.FlushDns;
        Ipv6Toggle.IsChecked = st.BlockIpv6;
        KillToggle.IsChecked = st.KillSwitch;
        KeepClosedToggle.IsChecked = st.KeepClosed;
        KeepClosedToggle.IsEnabled = st.KillSwitch;
        SmoothClear.IsChecked = st.TextSmoothing != "Grayscale";
        SmoothGray.IsChecked = st.TextSmoothing == "Grayscale";
        NotifyConnToggle.IsChecked = st.NotifyConnection;
        NotifyLeakToggle.IsChecked = st.NotifyLeaks;
        NotifyChangeToggle.IsChecked = st.NotifyChanges;
        NotifyKillToggle.IsChecked = st.NotifyKillSwitch;
        NotifyUpdateToggle.IsChecked = st.NotifyUpdates;
        TraySpeedToggle.IsChecked = st.TraySpeed;
        foreach (var chip in new[] { HoverLeaks, HoverSpeed, HoverTraffic, HoverLocation, HoverIp })
            chip.IsChecked = st.HoverExtras.Contains((string)chip.Tag);
    }

    private void RefreshKillList()
    {
        var apps = _engine.State.KillApps.Select(a => new KillAppView(a.Name, a.Path, KillSwitch.IconFor(a.Path))).ToList();
        KillList.ItemsSource = apps;
        KillEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddKillApp(string path)
    {
        if (!System.IO.File.Exists(path)) return;
        if (_engine.State.KillApps.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        var app = new KillApp(KillSwitch.NameFor(path), path);
        _engine.SaveSettings(s => s.KillApps = s.KillApps.Append(app).ToList());
        _engine.Note($"Kill switch: added {app.Name}");
        RefreshKillList();
    }

    private void KillRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        _engine.SaveSettings(s => s.KillApps = s.KillApps.Where(a => !a.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList());
        RefreshKillList();
    }

    private void KillPickRunning_Click(object sender, RoutedEventArgs e)
    {
        if (RunningPicker.Visibility == Visibility.Visible)
        {
            RunningPicker.Visibility = Visibility.Collapsed;
            return;
        }
        var chosen = _engine.State.KillApps.Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        RunningList.ItemsSource = KillSwitch.RunningApps()
            .Where(a => !chosen.Contains(a.Path))
            .Select(a => new KillAppView(a.Name, a.Path, KillSwitch.IconFor(a.Path)))
            .ToList();
        RunningPicker.Visibility = Visibility.Visible;
        Motion.To(RunningPicker, OpacityProperty, 1, 180, Motion.EaseOut, from: 0);
    }

    private void KillAddRunning_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        AddKillApp(path);
        RunningPicker.Visibility = Visibility.Collapsed;
    }

    private void KillBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose an app to close when the VPN drops",
            Filter = "Programs (*.exe)|*.exe",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dlg.ShowDialog(this) == true) AddKillApp(dlg.FileName);
    }

    private void KillToggle_Click(object sender, RoutedEventArgs e)
    {
        _engine.SetKillSwitch(KillToggle.IsChecked == true);
        KeepClosedToggle.IsEnabled = KillToggle.IsChecked == true;
    }

    private void KeepClosedToggle_Click(object sender, RoutedEventArgs e) =>
        _engine.SaveSettings(s => s.KeepClosed = KeepClosedToggle.IsChecked == true);

    private async void RegionToggle_Click(object sender, RoutedEventArgs e) =>
        await _engine.SetAutoRegionAsync(RegionToggle.IsChecked == true);

    private void FlushToggle_Click(object sender, RoutedEventArgs e) =>
        _engine.SaveSettings(s => s.FlushDns = FlushToggle.IsChecked == true);

    private async void Ipv6Toggle_Click(object sender, RoutedEventArgs e) =>
        await _engine.SetBlockIpv6Async(Ipv6Toggle.IsChecked == true);

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupToggle.IsChecked == true;
        bool ok = want ? StartupTask.Enable() : StartupTask.Disable();
        if (!ok) StartupToggle.IsChecked = StartupTask.IsEnabled();
        _engine.Note(want ? "Start with Windows turned on" : "Start with Windows turned off");
    }

    private void UpdateToggle_Click(object sender, RoutedEventArgs e)
    {
        bool on = UpdateToggle.IsChecked == true;
        _engine.SaveSettings(s => s.AutoUpdate = on);
        _updates.SetAutomatic(on);
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        await _updates.CheckAsync();
        UpdateStatus.Text = _updates.Pending is { } p
            ? $"Version {p.Version} is ready – restart from the popup."
            : _updates.LastError is { } err ? $"Couldn't check: {err}" : $"You're up to date.";
        CheckNowButton.IsEnabled = true;
    }

    private void Smoothing_Click(object sender, RoutedEventArgs e)
    {
        var mode = SmoothGray.IsChecked == true ? "Grayscale" : "ClearType";
        _engine.SaveSettings(s => s.TextSmoothing = mode);
        ApplyTextSmoothing(mode);
    }

    /// <summary>
    /// ClearType (sharp, colour sub-pixels) or greyscale anti-aliasing (smooth). ClearTypeHint lets WPF keep
    /// ClearType on the semi-transparent Mica window, where it would otherwise fall back to greyscale.
    /// </summary>
    private void ApplyTextSmoothing(string mode)
    {
        var rendering = mode == "Grayscale" ? TextRenderingMode.Grayscale : TextRenderingMode.ClearType;
        foreach (var el in new FrameworkElement[] { Root, Sheet, UpdateBanner })
        {
            TextOptions.SetTextRenderingMode(el, rendering);
            RenderOptions.SetClearTypeHint(el, ClearTypeHint.Enabled);
        }
    }

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
        CopyButton.Visibility = Visibility.Visible;
        _detailItem = null;
        _notesText = null;
        SheetTitle.Text = "Activity";
        SheetStatus.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Collapsed;
        _statsOpen = false;
        Sheet.Height = 500;
        CopyLabel.Text = "Copy all";
        ResetCopyFeedback();
        OpenSheet();
    }

    /// <summary>Clicking a check opens its full, selectable message.</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        CopyButton.Visibility = Visibility.Visible;
        if ((sender as FrameworkElement)?.DataContext is not StatusItem item) return;
        _detailItem = item;
        _notesText = null;
        LogPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Collapsed;
        _statsOpen = false;
        Sheet.Height = 500;
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

        FactList.ItemsSource = item.Facts;
        FactsHost.Visibility = item.Facts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MeaningText.Text = item.Meaning;
        MeaningHost.Visibility = item.HasMeaning ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Shows free text (e.g. release notes) in the detail layout.</summary>
    private void ShowNotes(string title, string status, string text)
    {
        CopyButton.Visibility = Visibility.Visible;
        _detailItem = null;
        _notesText = text;
        SheetTitle.Text = title;
        SheetStatus.Text = status;
        SheetStatus.Foreground = new SolidColorBrush(PastelFor(Level.Ok));
        SheetStatus.Visibility = Visibility.Visible;
        DetailText.Text = text;
        FactsHost.Visibility = Visibility.Collapsed;
        MeaningHost.Visibility = Visibility.Collapsed;
        LogPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;
        StatsPanel.Visibility = Visibility.Collapsed;
        _statsOpen = false;
        Sheet.Height = 500;
        CopyLabel.Text = "Copy";
        DetailPanel.ScrollToTop();
        OpenSheet();
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
        string text = _detailItem is { } item ? item.ToPlainText(SheetStatus.Text)
            : _notesText ?? string.Join(Environment.NewLine, _log);
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
        CopyLabel.Text = _detailItem == null && _notesText == null ? "Copy all" : "Copy";
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

    // ───────────────────────── update popup ─────────────────────────

    /// <summary>Drops in below the title bar – occasional, so it may animate (ease-out in, faster out).</summary>
    private void ShowUpdateBanner()
    {
        if (_updates.Pending is not { } info) return;
        UpdateTitle.Text = $"Border Control {info.Version} is ready";
        if (_bannerShown) return;
        _bannerShown = true;
        UpdateBanner.Visibility = Visibility.Visible;
        if (Motion.Reduced)
        {
            BannerShift.Y = 0;
            Motion.To(UpdateBanner, OpacityProperty, 1, 160, Motion.Ease, from: 0);
            return;
        }
        Motion.To(UpdateBanner, OpacityProperty, 1, 260, Motion.EaseOut, from: 0, delayMs: 350);
        Motion.To(BannerShift, TranslateTransform.YProperty, 0, 380, Motion.EaseOut, from: -14, delayMs: 350);
    }

    private void HideUpdateBanner()
    {
        if (!_bannerShown) return;
        _bannerShown = false;
        Motion.To(BannerShift, TranslateTransform.YProperty, -10, 180, Motion.EaseOut);
        Motion.To(UpdateBanner, OpacityProperty, 0, 160, Motion.EaseOut,
            done: () => { if (!_bannerShown) UpdateBanner.Visibility = Visibility.Collapsed; });
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _bannerDismissed = true;
        HideUpdateBanner();
    }

    private void UpdateRestart_Click(object sender, RoutedEventArgs e) => _restartForUpdate();

    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_updates.Pending is not { } info) return;
        ShowNotes($"What's new in {info.Version}", $"You have {UpdateService.Current}",
            info.Notes.Length > 0 ? info.Notes : "No release notes were published for this version.");
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
