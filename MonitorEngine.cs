using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace VpnGuard;

/// <summary>
/// Watches network changes, detects VPN connect/disconnect, syncs the system time zone to the VPN
/// location (and restores it afterwards) and runs the leak checks.
/// </summary>
public sealed class MonitorEngine : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _debounce;
    private Level _lastOverall = Level.Unknown;
    private bool _started;
    private Timer? _poll;
    private string _lastSignature = "";

    public MonitorEngine()
    {
        Items = new ObservableCollection<StatusItem>(new[] { Vpn, Ip, Dns, Ipv6, Tz, Region, Location });
    }

    public AppState State { get; } = AppState.Load();

    public StatusItem Vpn { get; } = new("vpn", "VPN connection");
    public StatusItem Ip { get; } = new("ip", "Public IP & location");
    public StatusItem Dns { get; } = new("dns", "DNS leak test");
    public StatusItem Ipv6 { get; } = new("ipv6", "IPv6 leak test");
    public StatusItem Tz { get; } = new("tz", "Time zone");
    public StatusItem Region { get; } = new("region", "Region & language");
    public StatusItem Location { get; } = new("loc", "Windows location service");
    public ObservableCollection<StatusItem> Items { get; }

    public bool VpnConnected { get; private set; }
    public Level Overall { get; private set; } = Level.Unknown;
    public string Headline { get; private set; } = "Starting…";
    public GeoInfo? VpnGeo { get; private set; }
    public GeoInfo? RealGeo => State.RealGeo;
    public TimeZoneInfo RealTimeZone { get; private set; } = TimeZoneInfo.Local;

    /// <summary>Raised whenever status, headline or settings change (may fire on a background thread).</summary>
    public event Action? Changed;

    /// <summary>title, message, isWarning</summary>
    public event Action<string, string, bool>? Notified;

    public event Action<string>? LogLine;

    // ───────────────────────── lifecycle ─────────────────────────

    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        // Crash/reboot recovery: a saved original zone means we were still synced to a VPN zone.
        if (State.OriginalTzId != null)
        {
            try { RealTimeZone = TimeZoneInfo.FindSystemTimeZoneById(State.OriginalTzId); }
            catch { State.OriginalTzId = null; State.Save(); }
        }
        else if (State.RealTzId != null)
        {
            try { RealTimeZone = TimeZoneInfo.FindSystemTimeZoneById(State.RealTzId); }
            catch { State.RealTzId = null; State.Save(); }
        }

        // Detector v1 missed TUN clients (Happ, Clash, sing-box…) and could save the VPN exit IP as the
        // "real" IP. Forget anything it learned; the next VPN-off check stores the right one.
        if (State.DetectorVersion < AppState.CurrentDetectorVersion)
        {
            State.RealGeo = null;
            State.DetectorVersion = AppState.CurrentDetectorVersion;
            State.Save();
        }

        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

        // Proxy on/off and some route-only changes raise no network event, so poll the (cheap, local) detector.
        _poll = new Timer(_ =>
        {
            try
            {
                if (Signature(VpnDetector.Detect()) != _lastSignature) ScheduleRefresh();
            }
            catch { /* next tick */ }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        await RefreshAsync(force: true);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _poll?.Dispose();
        _debounce?.Cancel();
    }

    private void OnNetworkAddressChanged(object? s, EventArgs e) => ScheduleRefresh();
    private void OnNetworkAvailabilityChanged(object? s, NetworkAvailabilityEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh() => ScheduleRefresh(TimeSpan.FromMilliseconds(2500)); // adapters flap while a tunnel comes up

    private void ScheduleRefresh(TimeSpan delay)
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
                await RefreshAsync(force: false);
            }
            catch (OperationCanceledException) { }
        });
    }

    // ───────────────────────── public actions ─────────────────────────

    public async Task RefreshAsync(bool force)
    {
        await _gate.WaitAsync();
        try
        {
            var status = VpnDetector.Detect();
            _lastSignature = Signature(status);
            bool now = status.Connected;
            bool was = VpnConnected;
            VpnConnected = now;

            if (now)
            {
                string what = status.Kind == VpnKind.Proxy
                    ? "System proxy " + status.Description
                    : status.Description;
                if (status.Kind == VpnKind.Proxy)
                {
                    Vpn.Level = Level.Warn;
                    Vpn.Detail = $"Proxy mode: {status.Description}. Apps that ignore the system proxy (games, " +
                                 "some CLI tools, UDP) bypass it – use TUN mode for full protection.";
                }
                else
                {
                    Vpn.Level = Level.Ok;
                    Vpn.Detail = "Connected – traffic routed through " + status.Description;
                }
                if (status.Note.Length > 0) Vpn.Detail += " " + status.Note;

                if (!was)
                {
                    AddLog("VPN connected – " + what);
                    Push("VPN connected", what, false);
                }
                await HandleConnectedAsync(newConnection: !was);
            }
            else
            {
                Vpn.Level = Level.Warn;
                Vpn.Detail = status.Note.Length > 0
                    ? "No VPN is carrying traffic. " + status.Note
                    : "No VPN is carrying traffic.";
                if (was)
                {
                    AddLog("VPN disconnected");
                    Push("VPN disconnected", "Your real IP is exposed.", true);
                }
                await HandleDisconnectedAsync(was, force);
            }

            ComputeOverall();
        }
        catch (Exception ex)
        {
            AddLog("Refresh failed: " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAutoSyncAsync(bool value)
    {
        State.AutoSync = value;
        State.Save();
        if (!value && State.OriginalTzId != null) RestoreReal(notify: true);
        await RefreshAsync(force: true);
    }

    /// <summary>Restores the real time zone immediately; turns auto-sync off if a VPN is still up.</summary>
    public void RestoreNow()
    {
        if (VpnConnected && (State.AutoSync || State.AutoLocation || State.AutoKeyboard))
        {
            State.AutoSync = false;
            State.AutoLocation = false;
            State.AutoKeyboard = false;
            State.Save();
            AddLog("Automatic changes turned off");
        }
        RestoreAll(notify: true);
        UpdateLocationService();
        TimeZoneService.CurrentWindowsId();
        RealTimeZone = TimeZoneInfo.Local;
        ComputeOverall();
    }

    /// <summary>Called on app exit / sign-out so the PC is never left in a VPN time zone with nobody watching.</summary>
    public void RestoreOnExit() => RestoreAll(notify: false);

    public async Task SetAutoLocationAsync(bool value)
    {
        State.AutoLocation = value;
        State.Save();
        if (!value) RestoreLocation(notify: true);
        await RefreshAsync(force: true);
    }

    public async Task SetAutoKeyboardAsync(bool value)
    {
        State.AutoKeyboard = value;
        State.Save();
        if (!value) RestoreKeyboard(notify: true);
        await RefreshAsync(force: true);
    }

    // ───────────────────────── connected / disconnected ─────────────────────────

    private async Task HandleConnectedAsync(bool newConnection)
    {
        Ip.Level = Level.Unknown;
        Ip.Detail = "Looking up public IP…";

        // A fresh tunnel can take several seconds before traffic really flows through it,
        // so keep asking for up to ~20 s while the lookup still returns your real IP.
        GeoInfo? geo = null;
        for (int i = 0; i < 12; i++)
        {
            geo = await GeoService.LookupAsync();
            if (geo != null && (RealGeo == null || geo.Ip != RealGeo.Ip)) break;
            await Task.Delay(1700);
        }

        bool stillReal = geo != null && RealGeo != null && geo.Ip == RealGeo.Ip;
        if (stillReal)
        {
            // Your real location is not a VPN exit: don't sync the time zone, keyboard or DNS check to it.
            ScheduleRefresh(TimeSpan.FromSeconds(20));
            geo = null;
        }
        VpnGeo = geo;

        if (stillReal)
        {
            Ip.Level = Level.Bad;
            Ip.Detail = $"Public IP is still your real IP ({RealGeo!.Ip}) – the VPN is not carrying traffic yet. Checking again shortly.";
        }
        else if (geo == null)
        {
            Ip.Level = Level.Warn;
            Ip.Detail = "Could not determine the public IP (offline or lookup blocked).";
        }
        else
        {
            Ip.Level = Level.Ok;
            Ip.Detail = $"{geo.Ip} · {Place(geo)} · {geo.Isp}";
            AddLog($"VPN exit: {geo.Ip} ({Place(geo)})");
        }

        ApplyTimezone(geo);
        ApplyLocationOff();
        ApplyKeyboard(geo);

        var reg = SystemChecks.Region(geo);
        Region.Level = reg.Level;
        Region.Detail = reg.Detail;
        UpdateLocationService();
        ComputeOverall();

        Dns.Level = Level.Unknown;
        Dns.Detail = "Testing…";
        Ipv6.Level = Level.Unknown;
        Ipv6.Detail = "Testing…";

        var dnsTask = DnsLeakService.RunAsync(geo, RealGeo);
        var v6Task = CheckIpv6Async(geo);
        await Task.WhenAll(dnsTask, v6Task);

        var d = await dnsTask;
        Dns.Level = d.Level;
        Dns.Detail = d.Summary;
        var v6 = await v6Task;
        Ipv6.Level = v6.Level;
        Ipv6.Detail = v6.Summary;
        AddLog($"DNS: {d.Level} – {d.Summary}");
        AddLog($"IPv6: {v6.Level} – {v6.Summary}");
    }

    private async Task HandleDisconnectedAsync(bool was, bool force)
    {
        VpnGeo = null;
        RestoreAll(notify: was);

        // Same for the location switch: with nothing of ours pending, the current value is yours.
        if (State.OriginalLocation == null && LocationControl.Get() is { } loc && State.RealLocation != loc)
        {
            State.RealLocation = loc;
            State.Save();
        }

        // With nothing of ours applied, the current zone is your real one – unless it is a zone we set.
        var current = TimeZoneService.CurrentWindowsId();
        if (State.OriginalTzId == null && current != State.AppliedTzId && State.RealTzId != current)
        {
            State.RealTzId = current;
            State.Save();
        }
        RealTimeZone = TimeZoneInfo.Local;

        if (was || force || RealGeo == null)
        {
            Ip.Level = Level.Unknown;
            Ip.Detail = "Looking up your real IP…";
            var g = await GeoService.LookupAsync();
            if (g != null)
            {
                State.RealGeo = g;
                State.Save();
            }
        }

        Ip.Level = Level.Warn;
        Ip.Detail = RealGeo != null
            ? $"No VPN – websites see {RealGeo.Ip} · {Place(RealGeo)} · {RealGeo.Isp}"
            : "No VPN – could not look up your public IP.";

        Dns.Level = Level.Unknown;
        Dns.Detail = "Connect a VPN to run this test.";
        Ipv6.Level = Level.Unknown;
        Ipv6.Detail = "Connect a VPN to run this test.";

        Tz.Level = Level.Unknown;
        Tz.Detail = $"Real time zone: {TimeZoneInfo.Local.Id}. Syncs to the VPN location when connected.";

        var reg = SystemChecks.Region(null);
        Region.Level = reg.Level;
        Region.Detail = reg.Detail;
        UpdateLocationService();
    }

    // ───────────────────────── checks ─────────────────────────

    private void ApplyTimezone(GeoInfo? geo)
    {
        var localId = TimeZoneService.CurrentWindowsId();

        if (geo == null || string.IsNullOrEmpty(geo.TimeZone))
        {
            Tz.Level = Level.Warn;
            Tz.Detail = "VPN time zone is unknown.";
            return;
        }

        var win = TimeZoneService.ToWindowsId(geo.TimeZone);
        if (win == null)
        {
            Tz.Level = Level.Warn;
            Tz.Detail = $"Cannot map {geo.TimeZone} to a Windows time zone.";
            return;
        }

        if (State.AutoSync)
        {
            if (State.OriginalTzId == null)
            {
                // Remember the real zone before touching anything. Prefer the one learned while the VPN was off;
                // never adopt a zone Border Control set itself (that is how a VPN zone could become "real").
                var original = State.RealTzId ?? (localId != State.AppliedTzId ? localId : null);
                if (original == null)
                {
                    Tz.Level = Level.Warn;
                    Tz.Detail = "Your real time zone is unknown – disconnect the VPN once so Border Control can learn it.";
                    return;
                }
                State.OriginalTzId = original;
                State.OriginalAutoTzStart = TimeZoneService.GetAutoTzStart();
                State.Save();
                try { RealTimeZone = TimeZoneInfo.FindSystemTimeZoneById(original); } catch { /* keep current */ }
            }

            TimeZoneService.SetAutoTzStart(4); // stop Windows from fighting us

            if (localId != win)
            {
                if (TimeZoneService.Set(win))
                {
                    localId = win;
                    State.AppliedTzId = win;
                    State.Save();
                    AddLog($"Time zone set to {win} ({geo.TimeZone})");
                    Push("Time zone synced", $"{win} – matches your VPN in {Place(geo)}", false);
                }
                else
                {
                    Tz.Level = Level.Bad;
                    Tz.Detail = "Could not change the time zone – run Border Control as administrator.";
                    return;
                }
            }
        }
        else if (State.OriginalTzId == null)
        {
            RealTimeZone = TimeZoneInfo.Local;
        }

        bool match = localId == win;
        Tz.Level = match ? Level.Ok : Level.Warn;
        Tz.Detail = match
            ? $"System time zone matches the VPN location ({geo.TimeZone})."
            : $"Mismatch – system is {localId}, VPN location is {geo.TimeZone}. Turn on auto-sync.";
    }

    private async Task<DnsResult> CheckIpv6Async(GeoInfo? vpn)
    {
        var v6 = await GeoService.PublicIpv6Async();
        if (v6 == null)
            return new DnsResult(Level.Ok, "No IPv6 connectivity – nothing can leak over IPv6.");

        var g6 = await GeoService.LookupAsync(v6);
        if (g6 == null)
            return new DnsResult(Level.Warn, $"IPv6 address {v6} is reachable but could not be located.");

        bool realNetwork = RealGeo != null &&
            ((RealGeo.Country == g6.Country && (vpn == null || vpn.Country != g6.Country))
             || (RealGeo.Isp.Length > 0 && g6.Isp == RealGeo.Isp));

        if (realNetwork)
            return new DnsResult(Level.Bad, $"IPv6 LEAK – {v6} belongs to your real network ({Place(g6)}). Disable IPv6 on your physical adapter.");

        if (vpn != null && g6.Country != vpn.Country)
            return new DnsResult(Level.Warn, $"IPv6 {v6} is located in {g6.Country}, VPN exit is {vpn.Country}.");

        return new DnsResult(Level.Ok, $"IPv6 {v6} is routed through the VPN ({Place(g6)}).");
    }

    private void UpdateLocationService()
    {
        var loc = SystemChecks.LocationService();
        Location.Level = loc.Level;
        Location.Detail = loc.Detail;
    }

    // ───────────────────────── helpers ─────────────────────────

    // ───────────────────────── location & keyboard ─────────────────────────

    private void ApplyLocationOff()
    {
        if (!State.AutoLocation) return;
        var now = LocationControl.Get();
        // Already off: if you normally have it on, make sure we still switch it back on later.
        if (now == "Deny" && State.OriginalLocation == null && State.RealLocation == "Allow")
        {
            State.OriginalLocation = "Allow";
            State.Save();
        }
        if (now != "Allow") return;

        if (State.OriginalLocation == null)
        {
            State.OriginalLocation = "Allow"; // remember before touching it
            State.Save();
        }
        if (LocationControl.Set("Deny"))
        {
            AddLog("Windows location service turned off");
            Push("Location turned off", "Windows location is off while the VPN is connected.", false);
        }
        else
        {
            State.OriginalLocation = null;
            State.Save();
            AddLog("Could not turn off Windows location – run as administrator.");
        }
    }

    private void RestoreLocation(bool notify)
    {
        if (State.OriginalLocation is not { } original) return;
        if (LocationControl.Set(original))
        {
            State.OriginalLocation = null;
            State.Save();
            AddLog("Windows location service restored");
            if (notify) Push("Location restored", "Windows location is back on.", false);
        }
    }

    private void ApplyKeyboard(GeoInfo? geo)
    {
        if (!State.AutoKeyboard || geo == null) return;
        var culture = KeyboardControl.CultureFor(geo.CountryCode);
        if (culture == null) return;
        var profile = KeyboardControl.ProfileFor(culture);

        if (State.AddedKeyboard == profile) return;           // already added for this country
        if (State.AddedKeyboard != null) RestoreKeyboard(notify: false); // VPN moved to another country
        if (KeyboardControl.HasLanguage(culture)) return;      // you already type in this language

        State.AddedKeyboard = profile; // record first so a crash still cleans up
        State.AddedKeyboardName = culture.DisplayName;
        State.Save();
        if (KeyboardControl.Add(profile))
        {
            AddLog($"Keyboard added: {culture.DisplayName}");
        }
        else
        {
            State.AddedKeyboard = null;
            State.AddedKeyboardName = null;
            State.Save();
            AddLog($"Could not add the {culture.DisplayName} keyboard");
        }
    }

    private void RestoreKeyboard(bool notify)
    {
        if (State.AddedKeyboard is not { } profile) return;
        var name = State.AddedKeyboardName ?? profile;
        KeyboardControl.Remove(profile);
        State.AddedKeyboard = null;
        State.AddedKeyboardName = null;
        State.Save();
        AddLog($"Keyboard removed: {name}");
        if (notify) Push("Keyboard removed", name, false);
    }

    /// <summary>Puts back everything Border Control changed: time zone, location service, keyboard.</summary>
    private void RestoreAll(bool notify)
    {
        if (State.OriginalTzId != null) RestoreReal(notify);
        RestoreLocation(notify);
        RestoreKeyboard(notify);
    }

    private void RestoreReal(bool notify)
    {
        var id = State.OriginalTzId;
        if (id == null) return;

        if (TimeZoneService.Set(id))
        {
            if (State.OriginalAutoTzStart is int start) TimeZoneService.SetAutoTzStart(start);
            State.OriginalTzId = null;
            State.OriginalAutoTzStart = null;
            State.AppliedTzId = null;
            State.RealTzId = id;
            State.Save();
            AddLog($"Real time zone restored ({id})");
            if (notify) Push("Real time zone restored", id, false);
        }
        else
        {
            AddLog("Could not restore the real time zone – run as administrator.");
        }
    }

    private void ComputeOverall()
    {
        Level overall;
        string worstDetail = "";

        if (!VpnConnected)
        {
            overall = Level.Unknown;
            Headline = "VPN not connected – real location visible";
        }
        else
        {
            var worst = new[] { Vpn, Ip, Dns, Ipv6, Tz, Region, Location }
                .Where(i => i.Level != Level.Unknown)
                .OrderByDescending(i => (int)i.Level)
                .FirstOrDefault();

            overall = worst?.Level ?? Level.Unknown;
            Headline = overall switch
            {
                Level.Bad => "Leak detected",
                Level.Warn => "Protected – check warnings",
                Level.Ok => "Protected – no leaks found",
                _ => "Checking…",
            };
            if (overall == Level.Bad && worst != null) worstDetail = $"{worst.Title}: {worst.Detail}";
        }

        bool changed = overall != _lastOverall;
        _lastOverall = overall;
        Overall = overall;

        if (changed && overall == Level.Bad) Push("Leak detected", worstDetail, true);
        Changed?.Invoke();
    }

    private static string Signature(VpnStatus s) => $"{s.Kind}|{s.Description}";

    private static string Place(GeoInfo g) =>
        string.Join(", ", new[] { g.City, g.Country }.Where(s => !string.IsNullOrEmpty(s)));

    private void Push(string title, string message, bool warning) => Notified?.Invoke(title, message, warning);

    private static readonly object LogFileLock = new();

    private void AddLog(string message)
    {
        // Stamp with your real local time (the system clock may be following the VPN), Gregorian digits.
        var now = TimeZoneInfo.ConvertTime(DateTime.UtcNow, RealTimeZone);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        LogLine?.Invoke($"{now.ToString("HH:mm:ss", inv)}  {message}");

        // Also keep a small on-disk log (%AppData%\BorderControlctivity.log) for troubleshooting.
        try
        {
            lock (LogFileLock)
            {
                var file = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BorderControl", "activity.log");
                var info = new System.IO.FileInfo(file);
                if (info.Exists && info.Length > 512 * 1024)
                    System.IO.File.Move(file, file + ".1", overwrite: true);
                if (!System.IO.File.Exists(file))
                    System.IO.File.WriteAllText(file, "", new System.Text.UTF8Encoding(true)); // BOM so every editor reads UTF-8
                System.IO.File.AppendAllText(file, $"{now.ToString("yyyy-MM-dd HH:mm:ss", inv)}  {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break monitoring */ }
    }
}
