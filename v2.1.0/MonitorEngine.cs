using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace BorderControl;

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
    private VpnKind _kind;
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
            _kind = status.Kind;
            _lastSignature = Signature(status);
            bool now = status.Connected;
            bool was = VpnConnected;
            VpnConnected = now;

            if (now)
            {
                string what = status.Kind == VpnKind.Proxy
                    ? "System proxy " + status.Description
                    : status.Description;
                Vpn.Apply(VpnReport(status));

                if (!was)
                {
                    AddLog("VPN connected – " + what);
                    Push("VPN connected", what, false);
                }
                await HandleConnectedAsync(newConnection: !was);
            }
            else
            {
                Vpn.Apply(VpnReport(status));
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
        Ip.Apply(Pending("Looking up public IP…"));

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

        Ip.Apply(IpReport(geo, stillReal));
        if (geo != null) AddLog($"VPN exit: {geo.Ip} ({Place(geo)})");

        ApplyTimezone(geo);
        ApplyLocationOff();
        ApplyKeyboard(geo);

        Region.Apply(SystemChecks.Region(geo));
        UpdateLocationService();
        ComputeOverall();

        Dns.Apply(Pending("Testing…"));
        Ipv6.Apply(Pending("Testing…"));

        var dnsTask = DnsLeakService.RunAsync(geo, RealGeo);
        var v6Task = CheckIpv6Async(geo);
        // In proxy mode nothing is supposed to go through a tunnel, so the route check has nothing to judge.
        var pathTask = _kind == VpnKind.Tunnel ? DnsPathCheck.RunAsync() : Task.FromResult(new System.Collections.Generic.List<DnsPath>());
        await Task.WhenAll(dnsTask, v6Task, pathTask);

        var d = CombineDns(DnsPathCheck.Verdict(await pathTask), await dnsTask);
        Dns.Apply(d);
        var v6 = await v6Task;
        Ipv6.Apply(v6);
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
            Ip.Apply(Pending("Looking up your real IP…"));
            var g = await GeoService.LookupAsync();
            if (g != null)
            {
                State.RealGeo = g;
                State.Save();
            }
        }

        Ip.Apply(RealGeo != null
            ? new Report(Level.Warn, $"No VPN – websites see {RealGeo.Ip}")
            {
                Facts = GeoFacts("Public IP", RealGeo, Level.Warn),
                Meaning = "This is your real address. Every website and app can see it and roughly where you are.",
            }
            : new Report(Level.Warn, "No VPN – public IP lookup failed")
            {
                Meaning = "The lookup services didn't answer (offline, blocked or rate-limited). Without a VPN, sites see your real address.",
            });

        var idle = new Report(Level.Unknown, "Connect a VPN to run this test")
        {
            Meaning = "This check only makes sense while a VPN is connected.",
        };
        Dns.Apply(idle);
        Ipv6.Apply(idle);

        Tz.Apply(new Report(Level.Unknown, $"Real time zone: {ZoneName(TimeZoneInfo.Local)}")
        {
            Facts = new[] { new Fact("System time zone", TimeZoneInfo.Local.DisplayName) },
            Meaning = State.AutoSync
                ? "Nothing to hide while no VPN is connected. When one connects, the clock follows its location and comes back afterwards."
                : "Time zone sync is off, so the clock stays as it is when a VPN connects.",
        });

        Region.Apply(SystemChecks.Region(null));
        UpdateLocationService();
    }

    // ───────────────────────── checks ─────────────────────────

    private void ApplyTimezone(GeoInfo? geo)
    {
        var localId = TimeZoneService.CurrentWindowsId();

        if (geo == null || string.IsNullOrEmpty(geo.TimeZone))
        {
            Tz.Apply(new Report(Level.Warn, "VPN time zone unknown – clock left alone")
            {
                Facts = new[] { new Fact("System time zone", ZoneName(TimeZoneInfo.Local)) },
                Meaning = "Border Control couldn't locate the VPN exit yet, so it didn't touch your clock. It tries again on the next check.",
            });
            return;
        }

        var win = TimeZoneService.ToWindowsId(geo.TimeZone);
        if (win == null)
        {
            Tz.Apply(new Report(Level.Warn, $"No Windows zone for {geo.TimeZone}")
            {
                Facts = new[] { new Fact("VPN location zone", geo.TimeZone, Level.Warn) },
                Meaning = "Windows has no time zone matching the VPN location, so the clock was left alone. Sites may notice the mismatch.",
            });
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
                    Tz.Apply(new Report(Level.Warn, "Real time zone unknown – sync paused")
                    {
                        Meaning = "To be able to put your clock back later, Border Control must first see your real time zone. " +
                                  "Disconnect the VPN once, then reconnect.",
                    });
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
                    Tz.Apply(new Report(Level.Bad, "Couldn't change the time zone")
                    {
                        Meaning = "Changing the Windows time zone needs administrator rights. Start Border Control as administrator.",
                    });
                    return;
                }
            }
        }
        else if (State.OriginalTzId == null)
        {
            RealTimeZone = TimeZoneInfo.Local;
        }

        bool match = localId == win;
        var facts = new[]
        {
            new Fact("Apps see", ZoneName(TimeZoneInfo.Local), match ? Level.Ok : Level.Warn),
            new Fact("VPN location", $"{geo.TimeZone} ({Place(geo)})"),
            new Fact("Your real zone", ZoneName(RealTimeZone)),
            new Fact("Time zone sync", State.AutoSync ? "On" : "Off"),
        };
        Tz.Apply(match
            ? new Report(Level.Ok, $"Matches the VPN location ({geo.TimeZone})")
            {
                Facts = facts,
                Meaning = "Websites can read your clock's time zone with JavaScript and compare it with where your IP is. " +
                          "They match, so this doesn't give you away." +
                          (State.OriginalTzId != null ? $" Your real zone comes back when the VPN disconnects." : ""),
            }
            : new Report(Level.Warn, $"Clock doesn't match the VPN location")
            {
                Facts = facts,
                Meaning = $"A site can see your clock is set to {ZoneName(TimeZoneInfo.Local)} while your IP is in {geo.Country}. " +
                          "That's a common sign of a VPN user and hints at where you really are.\n\n" +
                          "Turn on \"Match time zone to VPN\" to fix it automatically.",
            });
    }

    private async Task<Report> CheckIpv6Async(GeoInfo? vpn)
    {
        var v6 = await GeoService.PublicIpv6Async();
        if (v6 == null)
            return new Report(Level.Ok, "No IPv6 connection – nothing can leak over IPv6")
            {
                Facts = new[] { new Fact("Public IPv6", "none", Level.Ok) },
                Meaning = "Your PC has no working IPv6 internet right now, so no traffic can slip past the VPN that way.",
            };

        var g6 = await GeoService.LookupAsync(v6);
        if (g6 == null)
            return new Report(Level.Warn, "IPv6 works but couldn't be located")
            {
                Facts = new[] { new Fact("Public IPv6", v6, Level.Warn) },
                Meaning = "Sites that support IPv6 see this address, but the lookup service couldn't say where it is. Press Re-check later.",
            };

        bool realNetwork = RealGeo != null &&
            ((RealGeo.CountryCode == g6.CountryCode && (vpn == null || vpn.CountryCode != g6.CountryCode))
             || (RealGeo.Isp.Length > 0 && g6.Isp == RealGeo.Isp));
        var facts = GeoFacts("Public IPv6", g6, realNetwork ? Level.Bad : Level.Ok);

        if (realNetwork)
            return new Report(Level.Bad, "IPv6 LEAK – your real network is visible")
            {
                Facts = facts,
                Meaning = "Your IPv6 traffic skips the VPN. Many big sites (Google, Facebook, Cloudflare-hosted sites) prefer IPv6, " +
                          "so they see your real network and location.\n\n" +
                          "Fix: turn on IPv6 leak protection in your VPN app, or turn off IPv6 on your Wi-Fi / Ethernet adapter.",
            };

        if (vpn != null && g6.CountryCode != vpn.CountryCode)
            return new Report(Level.Warn, $"IPv6 exits in {g6.Country}, IPv4 in {vpn.Country}")
            {
                Facts = facts,
                Meaning = "IPv6 and IPv4 leave from different places. It isn't your real network, but sites using both may see two locations. " +
                          "Often it's just location databases disagreeing about the same VPN server.",
            };

        return new Report(Level.Ok, $"IPv6 goes through the VPN ({Place(g6)})")
        {
            Facts = facts,
            Meaning = "Sites that use IPv6 also see the VPN, not you.",
        };
    }

    private void UpdateLocationService()
    {
        Location.Apply(SystemChecks.LocationService(managed: State.OriginalLocation != null, vpnOn: VpnConnected));
    }

    /// <summary>
    /// Local route check first (certain, no location data), then the external resolver test
    /// (catches VPN apps that resolve names themselves outside the tunnel). The worse verdict wins.
    /// </summary>
    private static Report CombineDns(Report? local, Report external)
    {
        if (local == null) return external;
        var level = (Level)Math.Max((int)local.Level, (int)external.Level);
        var worse = local.Level >= external.Level ? local : external;
        var other = ReferenceEquals(worse, local) ? external : local;

        string summary = level == Level.Ok ? $"No leak – {external.Summary.Replace("No leak – ", "")}" : worse.Summary;
        string meaning = worse.Meaning;
        if (other.Level != worse.Level && other.Meaning.Length > 0)
            meaning += $"\n\n{(ReferenceEquals(other, local) ? "Route check" : "Resolver test")}: {other.Meaning}";
        else if (level == Level.Ok)
            meaning = $"{local.Meaning}\n\n{external.Meaning}";

        return new Report(level, summary)
        {
            Facts = local.Facts.Concat(external.Facts).ToList(),
            Meaning = meaning,
        };
    }

    private static Report Pending(string what) => new(Level.Unknown, what) { Meaning = "Checking now…" };

    private static Fact[] GeoFacts(string ipLabel, GeoInfo g, Level level) => new[]
    {
        new Fact(ipLabel, g.Ip, level),
        new Fact("Location", Place(g)),
        new Fact("Network", g.Isp.Length > 0 ? g.Isp : "unknown"),
    };

    private static string ZoneName(TimeZoneInfo tz)
    {
        var off = tz.GetUtcOffset(DateTime.UtcNow);
        var sign = off < TimeSpan.Zero ? "−" : "+";
        var o = off.Minutes == 0 ? $"{Math.Abs(off.Hours)}" : $"{Math.Abs(off.Hours)}:{Math.Abs(off.Minutes):00}";
        return $"{tz.Id} (UTC{sign}{o})";
    }

    private Report VpnReport(VpnStatus status)
    {
        var facts = new System.Collections.Generic.List<Fact>();
        if (status.Kind == VpnKind.Tunnel)
        {
            facts.Add(new Fact("Mode", "VPN tunnel – all apps", Level.Ok));
            facts.Add(new Fact("Adapter", status.Description));
        }
        else if (status.Kind == VpnKind.Proxy)
        {
            facts.Add(new Fact("Mode", "System proxy – some apps", Level.Warn));
            facts.Add(new Fact("Proxy", status.Description));
        }
        else
        {
            facts.Add(new Fact("Mode", "No VPN", Level.Warn));
        }
        if (status.Note.Length > 0) facts.Add(new Fact("Idle VPN adapters", status.Note));

        return status.Kind switch
        {
            VpnKind.Tunnel => new Report(Level.Ok, $"Connected through {status.Description}")
            {
                Facts = facts,
                Meaning = "Windows sends the internet traffic of every app through the VPN adapter. That's the strongest mode.",
            },
            VpnKind.Proxy => new Report(Level.Warn, $"Proxy mode ({status.Description})")
            {
                Facts = facts,
                Meaning = "Only apps that use the Windows proxy setting (browsers and most apps) go through the VPN. Games, many " +
                          "command-line tools and anything using UDP connect directly with your real address.\n\n" +
                          "Switch your VPN app to its TUN / VPN mode to cover everything.",
            },
            _ => new Report(Level.Warn, "No VPN is carrying traffic")
            {
                Facts = facts,
                Meaning = "Websites and apps see your real address and location." +
                          (status.Note.Length > 0
                              ? " A VPN adapter is up but traffic doesn't go through it – the VPN may be in split-tunnel mode or still connecting."
                              : ""),
            },
        };
    }

    private Report IpReport(GeoInfo? geo, bool stillReal)
    {
        if (stillReal)
            return new Report(Level.Bad, $"Sites still see your real IP ({RealGeo!.Ip})")
            {
                Facts = GeoFacts("Public IP", RealGeo, Level.Bad),
                Meaning = "The VPN is connected, but your traffic doesn't go through it yet. A new tunnel often needs a few " +
                          "seconds – Border Control checks again in 20 s.\n\n" +
                          "If this stays red, the VPN app may be bypassing this app (split tunnel) or its connection is failing.",
            };

        if (geo == null)
            return new Report(Level.Warn, "Couldn't look up the public IP")
            {
                Meaning = "The lookup services didn't answer (offline, blocked or rate-limited). The other checks still run. Press Re-check later.",
            };

        var facts = GeoFacts("Public IP", geo, Level.Ok).ToList();
        if (RealGeo != null) facts.Add(new Fact("Your real IP", $"{RealGeo.Ip} · {RealGeo.Country}"));
        return new Report(Level.Ok, $"{geo.Ip} · {Place(geo)}")
        {
            Facts = facts,
            Meaning = $"Websites see the VPN server in {Place(geo)}, not you. Its network ({(geo.Isp.Length > 0 ? geo.Isp : "a hosting company")}) " +
                      "is a data centre, so some sites can tell you're on a VPN – but not who you are or where you really are.",
        };
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

    /// <summary>Adds a line to the activity log from outside the engine (e.g. the updater).</summary>
    public void Note(string message) => AddLog(message);

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
