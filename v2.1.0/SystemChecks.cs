using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace BorderControl;

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

    /// <param name="managed">True when Border Control switched it off and will switch it back on.</param>
    public static Report LocationService(bool managed, bool vpnOn)
    {
        if (LocationServiceOn())
            return new Report(Level.Warn, "Windows location is on")
            {
                Facts = new[] { new Fact("Location services", "On", Level.Warn) },
                Meaning =
                    "Apps with location permission (Maps, Weather, and browsers if you allowed a site) can get your real " +
                    "position from nearby Wi-Fi networks – a VPN can't hide that." +
                    (vpnOn ? "\n\nTurn on \"Turn off Windows location\" to switch it off automatically while the VPN is connected." : ""),
            };

        return new Report(Level.Ok, "Windows location is off")
        {
            Facts = new[]
            {
                new Fact("Location services", "Off", Level.Ok),
                new Fact("Changed by Border Control", managed ? "Yes – switched back on after the VPN" : "No"),
            },
            Meaning = "Apps can't get your position from Windows, and websites asking the browser get nothing to share." +
                      (managed ? " Border Control turned it off and will switch it back on when the VPN disconnects." : ""),
        };
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

    public static Report Region(GeoInfo? vpn)
    {
        var region = WindowsRegion();
        var ui = CultureInfo.CurrentUICulture;
        string keyboards;
        try
        {
            keyboards = string.Join(", ", System.Windows.Forms.InputLanguage.InstalledInputLanguages
                .Cast<System.Windows.Forms.InputLanguage>()
                .Select(l => l.Culture.EnglishName)
                .Distinct());
        }
        catch { keyboards = "?"; }

        bool hasVpn = vpn != null && !string.IsNullOrEmpty(vpn.CountryCode);
        bool match = hasVpn && string.Equals(region, vpn!.CountryCode, StringComparison.OrdinalIgnoreCase);

        var facts = new List<Fact>
        {
            new("Windows region", region, hasVpn ? (match ? Level.Ok : Level.Warn) : Level.Unknown),
            new("Display language", $"{ui.EnglishName} ({ui.Name})"),
            new("Keyboards", keyboards),
        };
        if (hasVpn) facts.Add(new Fact("VPN country", $"{vpn!.Country} ({vpn.CountryCode})"));

        // English is used worldwide; most other display languages point at a few countries.
        string langNote = ui.TwoLetterISOLanguageName == "en"
            ? $"Your browser's language ({ui.Name}) is visible to every site, but English is used everywhere, so it says little about where you are."
            : $"Your browser's language ({ui.EnglishName}) is visible to every site and hints at where you're from. If that matters, set your browser's preferred language to one that fits the VPN country.";

        if (!hasVpn)
            return new Report(Level.Unknown, $"Windows region {region} · {ui.Name}")
            {
                Facts = facts,
                Meaning = "Compared with the VPN country once a VPN is connected.",
            };

        if (match)
            return new Report(Level.Ok, $"Windows region matches the VPN country ({region})")
            {
                Facts = facts,
                Meaning = "Apps that read the Windows region see the same country as your IP. " + langNote,
            };

        return new Report(Level.Warn, $"Windows region {region} differs from VPN country {vpn!.CountryCode}")
        {
            Facts = facts,
            Meaning =
                $"Websites can't read the Windows region, so this is a weak signal. Desktop apps and Microsoft services " +
                $"(Store, Bing, News) can, and may notice {region} while your IP is in {vpn.CountryCode}.\n\n" + langNote +
                "\n\nTo clear this, set Settings → Time & language → Language & region → Country or region to the VPN country " +
                "(date and number formats may change with it).",
        };
    }
}
