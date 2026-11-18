using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;

namespace BorderControl;

/// <summary>One speed reading, in bytes per second.</summary>
public readonly record struct SpeedSample(DateTime At, double Down, double Up, bool Vpn);

/// <summary>Traffic for one day (real local date), counted only while a VPN was connected.</summary>
public sealed class DayTraffic
{
    public long Down { get; set; }
    public long Up { get; set; }
    public long Seconds { get; set; }
}

/// <summary>Saved totals: %AppData%\BorderControl\traffic.json.</summary>
public sealed class TrafficStore
{
    public Dictionary<string, DayTraffic> Days { get; set; } = new();
    public long Sessions { get; set; }
    public DateTime? Since { get; set; }
}

/// <summary>
/// Live speed and VPN traffic totals, read from Windows' own per-adapter byte counters once a second.
/// Nothing is downloaded or uploaded to measure – it only watches what already flows.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    private const int HistorySize = 120;   // two minutes of one-second samples
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly MonitorEngine _engine;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly List<SpeedSample> _history = new();

    private string? _adapterId;
    private long _lastRx, _lastTx;
    private DateTime _lastAt;
    private bool _wasVpn;
    private bool _dirty;
    private DateTime _lastSave = DateTime.UtcNow;

    public TrafficStore Store { get; }

    /// <summary>Raised every second on a background thread.</summary>
    public event Action<SpeedSample>? Sampled;

    public SpeedSample Latest { get; private set; }
    public string AdapterName { get; private set; } = "";

    /// <summary>This VPN session so far.</summary>
    public long SessionDown { get; private set; }
    public long SessionUp { get; private set; }
    public DateTime? SessionStart { get; private set; }

    public TrafficMonitor(MonitorEngine engine)
    {
        _engine = engine;
        Store = Load();
        Store.Since ??= DateTime.UtcNow;
        _timer = new Timer(_ => Sample(), null, TimeSpan.FromSeconds(1), Tick);
    }

    public IReadOnlyList<SpeedSample> History()
    {
        lock (_lock) return _history.ToList();
    }

    /// <summary>Today's key in your real time zone (the system clock may be following the VPN).</summary>
    private string Today => TimeZoneInfo.ConvertTime(DateTime.UtcNow, _engine.RealTimeZone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public DayTraffic Day(DateTime realDate)
    {
        lock (_lock)
            return Store.Days.TryGetValue(realDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), out var d)
                ? new DayTraffic { Down = d.Down, Up = d.Up, Seconds = d.Seconds }
                : new DayTraffic();
    }

    public DateTime RealToday => TimeZoneInfo.ConvertTime(DateTime.UtcNow, _engine.RealTimeZone).Date;

    /// <summary>Totals for the last <paramref name="days"/> days including today (0 = all time).</summary>
    public DayTraffic Total(int days)
    {
        lock (_lock)
        {
            var from = days > 0 ? RealToday.AddDays(-(days - 1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";
            var t = new DayTraffic();
            foreach (var (k, d) in Store.Days)
                if (days == 0 || string.CompareOrdinal(k, from) >= 0) { t.Down += d.Down; t.Up += d.Up; t.Seconds += d.Seconds; }
            return t;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Store.Days.Clear();
            Store.Sessions = _engine.VpnConnected ? 1 : 0;
            Store.Since = DateTime.UtcNow;
            SessionDown = SessionUp = 0;
            _dirty = true;
        }
        Save();
    }

    private void Sample()
    {
        try
        {
            bool vpn = _engine.VpnConnected;
            var nic = PickAdapter(vpn);
            var now = DateTime.UtcNow;
            double down = 0, up = 0;

            if (nic != null)
            {
                var st = nic.GetIPStatistics();
                long rx = st.BytesReceived, tx = st.BytesSent;
                if (nic.Id == _adapterId)
                {
                    long dRx = rx - _lastRx, dTx = tx - _lastTx;
                    double secs = Math.Max(0.2, (now - _lastAt).TotalSeconds);
                    // Counters restart when an adapter is re-created: skip that one reading.
                    if (dRx >= 0 && dTx >= 0)
                    {
                        down = dRx / secs;
                        up = dTx / secs;
                        if (vpn) Count(dRx, dTx, secs);
                    }
                }
                _adapterId = nic.Id;
                AdapterName = nic.Name;
                _lastRx = rx;
                _lastTx = tx;
                _lastAt = now;
            }

            if (vpn && !_wasVpn)
            {
                lock (_lock) Store.Sessions++;
                SessionStart = DateTime.UtcNow;
                SessionDown = SessionUp = 0;
                _dirty = true;
            }
            if (!vpn && _wasVpn) Save();
            _wasVpn = vpn;

            var sample = new SpeedSample(now, down, up, vpn);
            lock (_lock)
            {
                _history.Add(sample);
                if (_history.Count > HistorySize) _history.RemoveAt(0);
            }
            Latest = sample;
            Sampled?.Invoke(sample);

            if (_dirty && (now - _lastSave).TotalSeconds >= 30) Save();
        }
        catch { /* next tick */ }
    }

    private void Count(long down, long up, double secs)
    {
        lock (_lock)
        {
            var key = Today;
            if (!Store.Days.TryGetValue(key, out var d)) Store.Days[key] = d = new DayTraffic();
            d.Down += down;
            d.Up += up;
            d.Seconds += (long)Math.Round(secs);
            SessionDown += down;
            SessionUp += up;
            _dirty = true;
        }
    }

    /// <summary>The VPN tunnel while connected (proxy mode: the adapter carrying traffic), else the internet adapter.</summary>
    private NetworkInterface? PickAdapter(bool vpn)
    {
        var nic = VpnDetector.InternetAdapter();
        if (nic != null || !vpn) return nic;
        // A tunnel that isn't the best route for the probe addresses: fall back to any up virtual adapter.
        return NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up && VpnDetector.IsVirtual(n));
    }

    // ───────── persistence ─────────

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BorderControl");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "traffic.json");
        }
    }

    private static TrafficStore Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<TrafficStore>(File.ReadAllText(FilePath)) ?? new TrafficStore()
                : new TrafficStore();
        }
        catch { return new TrafficStore(); }
    }

    public void Save()
    {
        try
        {
            string json;
            lock (_lock)
            {
                // Keep a year of days at most.
                if (Store.Days.Count > 400)
                    foreach (var k in Store.Days.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(Store.Days.Count - 400).ToList())
                        Store.Days.Remove(k);
                json = JsonSerializer.Serialize(Store);
                _dirty = false;
            }
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
            _lastSave = DateTime.UtcNow;
        }
        catch { /* try again later */ }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Save();
    }

    // ───────── formatting ─────────

    /// <summary>"2.4 MB/s", "850 KB/s", "0 KB/s".</summary>
    public static string Speed(double bytesPerSecond)
    {
        var (v, u) = SpeedParts(bytesPerSecond);
        return $"{v} {u}/s";
    }

    /// <summary>Value and unit for compact displays ("2.4", "MB").</summary>
    public static (string Value, string Unit) SpeedParts(double bps)
    {
        if (bps >= 1024d * 1024 * 1024) return (Short(bps / (1024d * 1024 * 1024)), "GB");
        if (bps >= 1024d * 1024) return (Short(bps / (1024d * 1024)), "MB");
        return (Math.Round(bps / 1024d).ToString("0"), "KB");
    }

    /// <summary>"1.24 GB", "380 MB", "12 KB".</summary>
    public static string Size(long bytes)
    {
        double b = bytes;
        if (b >= 1024d * 1024 * 1024 * 1024) return $"{b / (1024d * 1024 * 1024 * 1024):0.00} TB";
        if (b >= 1024d * 1024 * 1024) return $"{b / (1024d * 1024 * 1024):0.00} GB";
        if (b >= 1024d * 1024) return $"{b / (1024d * 1024):0} MB";
        return $"{b / 1024d:0} KB";
    }

    /// <summary>"3 h 12 min", "45 min".</summary>
    public static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : $"{t.Minutes} min";
    }

    private static string Short(double v) => v >= 100 ? v.ToString("0") : v >= 10 ? v.ToString("0") : v.ToString("0.0");
}
