using System.ComponentModel;
using System.Windows.Media;

namespace VpnGuard;

public enum Level { Unknown, Ok, Warn, Bad }

public sealed record GeoInfo(string Ip, string Country, string CountryCode, string City, string TimeZone, string Isp);

public sealed record DnsResult(Level Level, string Summary);

/// <summary>One row on the dashboard (VPN, DNS, IPv6, timezone ...).</summary>
public sealed class StatusItem : INotifyPropertyChanged
{
    private Level _level = Level.Unknown;
    private string _detail = "Checking…";

    public StatusItem(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }

    public Level Level
    {
        get => _level;
        set { _level = value; Raise(nameof(Level)); Raise(nameof(Brush)); }
    }

    public string Detail
    {
        get => _detail;
        set { _detail = value; Raise(nameof(Detail)); }
    }

    public Brush Brush => BrushFor(_level);

    public static Brush BrushFor(Level level) => level switch
    {
        Level.Ok => Frozen("#3DDC97"),
        Level.Warn => Frozen("#FFB020"),
        Level.Bad => Frozen("#FF5C5C"),
        _ => Frozen("#6B7280"),
    };

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
