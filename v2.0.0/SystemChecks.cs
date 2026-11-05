using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VpnGuard;

/// <summary>Local signals an app can read to guess where you are, besides your IP.</summary>
public static class SystemChecks
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultGeoName(StringBuilder geoName, int geoNameCount);

    /// <summary>True when the Windows location service is allowed for the machine.</summary>
    public static bool LocationServiceOn()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location");
            return (k?.GetValue("Value") as string) == "Allow";
        }
        catch { return false; }
    }

    public static (Level Level, string Detail) LocationService()
    {
        return LocationServiceOn()
            ? (Level.Warn, "Windows location service is ON – apps can read your real position. Settings → Privacy → Location.")
            : (Level.Ok, "Windows location service is off.");
    }

    public static string WindowsRegion()
    {
        try
        {
            var sb = new StringBuilder(16);
            if (GetUserDefaultGeoName(sb, sb.Capacity) > 0) return sb.ToString();
        }
        catch { /* older Windows */ }

        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName; }
        catch { return "??"; }
    }

    public static (Level Level, string Detail) Region(GeoInfo? vpn)
    {
        var region = WindowsRegion();
        var ui = CultureInfo.CurrentUICulture.Name;
        string keyboards;
        try
        {
            keyboards = string.Join(", ", System.Windows.Forms.InputLanguage.InstalledInputLanguages
                .Cast<System.Windows.Forms.InputLanguage>()
                .Select(l => l.Culture.TwoLetterISOLanguageName)
                .Distinct());
        }
        catch { keyboards = "?"; }

        var info = $"Windows region {region} · UI {ui} · keyboards {keyboards}";
        if (vpn == null || string.IsNullOrEmpty(vpn.CountryCode)) return (Level.Unknown, info);

        return string.Equals(region, vpn.CountryCode, StringComparison.OrdinalIgnoreCase)
            ? (Level.Ok, info)
            : (Level.Warn, $"{info} – differs from VPN country ({vpn.CountryCode}).");
    }
}
