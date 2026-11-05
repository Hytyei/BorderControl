using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Windows.Media;

namespace BorderControl;

public enum Level { Unknown, Ok, Warn, Bad }

public sealed record GeoInfo(string Ip, string Country, string CountryCode, string City, string TimeZone, string Isp);

/// <summary>One result row in a check's detail view. Header rows start a group.</summary>
public sealed record Fact(string Label, string Value, Level Level = Level.Unknown, bool Header = false)
{
    public static Fact Group(string title) => new(title, "", Level.Unknown, true);
}

/// <summary>
/// The outcome of one check: a level, a one-line summary for the list, result rows,
/// and a plain-language note on what it means and what to do.
/// </summary>
public sealed record Report(Level Level, string Summary)
{
    public IReadOnlyList<Fact> Facts { get; init; } = Array.Empty<Fact>();
    public string Meaning { get; init; } = "";
}

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

    private IReadOnlyList<Fact> _facts = Array.Empty<Fact>();
    private string _meaning = "";

    public IReadOnlyList<Fact> Facts => _facts;
    public string Meaning => _meaning;
    public bool HasMeaning => _meaning.Length > 0;

    /// <summary>Publishes a whole report at once.</summary>
    public void Apply(Report r)
    {
        _facts = r.Facts;
        _meaning = r.Meaning;
        Level = r.Level;
        Detail = r.Summary;
        Raise(nameof(Facts));
        Raise(nameof(Meaning));
        Raise(nameof(HasMeaning));
    }

    /// <summary>Plain-text version for the clipboard.</summary>
    public string ToPlainText(string status)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Title} – {status}");
        sb.AppendLine(Detail);
        if (_facts.Count > 0)
        {
            sb.AppendLine();
            foreach (var f in _facts)
            {
                if (f.Header) sb.AppendLine().AppendLine(f.Label);
                else sb.AppendLine($"  {f.Label}: {f.Value}");
            }
        }
        if (_meaning.Length > 0) sb.AppendLine().AppendLine("What this means:").AppendLine(_meaning);
        return sb.ToString().TrimEnd();
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
