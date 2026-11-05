using System;
using System.IO;
using System.Text.Json;

namespace VpnGuard;

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

            // Carry over state from the old "VpnGuard" name – it may hold the real time zone to restore.
            var legacy = Path.Combine(appData, "VpnGuard", "state.json");
            if (!File.Exists(file) && File.Exists(legacy))
            {
                try { File.Copy(legacy, file); } catch { /* start fresh */ }
            }
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
