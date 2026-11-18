using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BorderControl;

/// <summary>Small persisted state so the real time zone is restored even after a crash or reboot.</summary>
public sealed class AppState
{
    public bool AutoSync { get; set; } = true;
    public string? OriginalTzId { get; set; }

    /// <summary>Your real time zone, learned only while no VPN is connected and never from a zone we set.</summary>
    public string? RealTzId { get; set; }

    /// <summary>The zone Border Control last applied – so it is never mistaken for your real one.</summary>
    public string? AppliedTzId { get; set; }
    public int? OriginalAutoTzStart { get; set; }

    /// <summary>Turn the Windows location service off while a VPN is connected.</summary>
    public bool AutoLocation { get; set; } = true;

    /// <summary>Location setting ("Allow") to put back; null when we haven't changed it.</summary>
    public string? OriginalLocation { get; set; }

    /// <summary>Your own location setting, learned while no VPN is connected.</summary>
    public string? RealLocation { get; set; }

    /// <summary>Add the VPN country's keyboard while connected.</summary>
    public bool AutoKeyboard { get; set; } = true;

    /// <summary>Keyboard profile we added (e.g. "0407:00000407"); removed again on disconnect.</summary>
    public string? AddedKeyboard { get; set; }
    public string? AddedKeyboardName { get; set; }

    // ───────── stealth (2.2.0) ─────────

    /// <summary>Set the Windows "Country or region" to the VPN country while connected.</summary>
    public bool AutoRegion { get; set; } = true;
    /// <summary>Region code to put back (e.g. "US"); null when we haven't changed it.</summary>
    public string? OriginalGeoName { get; set; }
    /// <summary>Your own region, learned while no VPN is connected.</summary>
    public string? RealGeoName { get; set; }

    /// <summary>Clear the Windows DNS cache when the VPN connects and disconnects.</summary>
    public bool FlushDns { get; set; } = true;

    /// <summary>Unbind IPv6 from physical adapters while connected (off by default: breaks VPNs that use IPv6).</summary>
    public bool BlockIpv6 { get; set; }
    /// <summary>Adapters whose IPv6 we turned off; turned back on at disconnect.</summary>
    public List<string> DisabledIpv6Adapters { get; set; } = new();

    // ───────── kill switch (2.2.0) ─────────

    /// <summary>Close the chosen apps the moment the VPN connection is lost.</summary>
    public bool KillSwitch { get; set; }
    /// <summary>Keep closing them if they're reopened, until the VPN is back.</summary>
    public bool KeepClosed { get; set; } = true;
    public List<KillApp> KillApps { get; set; } = new();

    // ───────── notifications & tray (2.3.0) ─────────

    public bool NotifyConnection { get; set; } = true;
    public bool NotifyLeaks { get; set; } = true;
    public bool NotifyChanges { get; set; } = true;
    public bool NotifyKillSwitch { get; set; } = true;
    public bool NotifyUpdates { get; set; } = true;

    public bool Allows(Notice kind) => kind switch
    {
        Notice.Connection => NotifyConnection,
        Notice.Leaks => NotifyLeaks,
        Notice.Changes => NotifyChanges,
        Notice.KillSwitch => NotifyKillSwitch,
        Notice.Updates => NotifyUpdates,
        _ => true,
    };

    /// <summary>Show a second tray icon with the live download speed.</summary>
    public bool TraySpeed { get; set; } = true;

    /// <summary>Extra lines in the tray hover text, besides time and overall status: Leaks, Speed, Traffic, Location, IP.</summary>
    public List<string> HoverExtras { get; set; } = new() { "Leaks", "Speed" };

    // ───────── app (2.2.0) ─────────

    public bool AutoUpdate { get; set; } = true;
    /// <summary>"ClearType" (sharp) or "Grayscale" (smooth).</summary>
    public string TextSmoothing { get; set; } = "ClearType";

    /// <summary>Location of your real (non-VPN) public IP, remembered from the last time no VPN was active.</summary>
    public GeoInfo? RealGeo { get; set; }

    public const int CurrentDetectorVersion = 2;

    /// <summary>Detector version that learned <see cref="RealGeo"/>; older values are discarded.</summary>
    public int DetectorVersion { get; set; }

    private static string PathFile
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "BorderControl");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "state.json");

            return file;
        }
    }

    public static AppState Load()
    {
        var file = PathFile;
        if (!File.Exists(file)) return new AppState();
        try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(file)) ?? new AppState(); }
        catch
        {
            // Keep the damaged file for inspection instead of silently losing the saved real time zone.
            try { File.Copy(file, file + ".bad", overwrite: true); } catch { /* ignore */ }
            return new AppState();
        }
    }

    public void Save()
    {
        try
        {
            // Write-then-swap so a crash mid-save can never leave a half-written file.
            var file = PathFile;
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, file, overwrite: true);
        }
        catch { /* non-fatal */ }
    }
}

/// <summary>An app the kill switch closes: display name and full exe path.</summary>
public sealed record KillApp(string Name, string Path);

/// <summary>Notification categories the user can switch on or off.</summary>
public enum Notice { Connection, Leaks, Changes, KillSwitch, Updates }
