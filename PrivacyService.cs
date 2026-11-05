using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VpnGuard;

/// <summary>Turns the Windows location service off (and back on) – needs admin rights.</summary>
public static class LocationControl
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location";

    /// <summary>Machine-wide "Location services" switch: "Allow" or "Deny" (null if unreadable).</summary>
    public static string? Get()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(Key);
            return k?.GetValue("Value") as string;
        }
        catch { return null; }
    }

    public static bool Set(string value)
    {
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(Key, writable: true);
            k.SetValue("Value", value, RegistryValueKind.String);
            return Get() == value;
        }
        catch { return false; }
    }
}

/// <summary>Adds / removes a keyboard for the VPN country so the input-language list doesn't give you away.</summary>
public static class KeyboardControl
{
    [DllImport("input.dll", CharSet = CharSet.Unicode)]
    private static extern bool InstallLayoutOrTip(string profile, uint flags);

    private const uint ILOT_UNINSTALL = 0x1;

    // Countries where the first culture .NET lists isn't the language most people type in.
    private static readonly Dictionary<string, string> Preferred = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "en-US", ["GB"] = "en-GB", ["CA"] = "en-CA", ["AU"] = "en-AU", ["NZ"] = "en-NZ",
        ["IE"] = "en-IE", ["IN"] = "en-IN", ["SG"] = "en-SG", ["ZA"] = "en-ZA", ["PH"] = "en-PH",
        ["CH"] = "de-CH", ["AT"] = "de-AT", ["BE"] = "nl-BE", ["LU"] = "fr-LU", ["ES"] = "es-ES",
        ["MX"] = "es-MX", ["AR"] = "es-AR", ["BR"] = "pt-BR", ["PT"] = "pt-PT", ["JP"] = "ja-JP",
        ["KR"] = "ko-KR", ["CN"] = "zh-CN", ["TW"] = "zh-TW", ["HK"] = "zh-HK", ["SE"] = "sv-SE",
        ["DK"] = "da-DK", ["NO"] = "nb-NO", ["UA"] = "uk-UA", ["GR"] = "el-GR", ["CZ"] = "cs-CZ",
        ["IL"] = "he-IL", ["AE"] = "ar-AE", ["SA"] = "ar-SA", ["EG"] = "ar-EG", ["EE"] = "et-EE",
        ["RS"] = "sr-Latn-RS", ["VN"] = "vi-VN", ["MY"] = "ms-MY", ["KZ"] = "kk-KZ", ["GE"] = "ka-GE",
        ["AM"] = "hy-AM", ["SI"] = "sl-SI", ["AL"] = "sq-AL", ["IR"] = "fa-IR",
    };

    /// <summary>Main typing language of a country, e.g. "DE" → de-DE.</summary>
    public static CultureInfo? CultureFor(string countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return null;
        if (Preferred.TryGetValue(countryCode, out var name))
        {
            try { return CultureInfo.GetCultureInfo(name); } catch { /* fall through */ }
        }

        var candidates = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Where(c =>
            {
                try { return new RegionInfo(c.Name).TwoLetterISORegionName.Equals(countryCode, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            })
            .ToList();

        // de-DE, fr-FR, it-IT, pl-PL… – the language code usually matches the country code.
        return candidates.FirstOrDefault(c => c.TwoLetterISOLanguageName.Equals(countryCode, StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    /// <summary>"0407:00000407" style profile for the culture's default keyboard.</summary>
    public static string ProfileFor(CultureInfo culture) =>
        $"{culture.LCID:X4}:{culture.KeyboardLayoutId:X8}";

    /// <summary>True when the user already has a keyboard for this language.</summary>
    public static bool HasLanguage(CultureInfo culture)
    {
        try
        {
            return System.Windows.Forms.InputLanguage.InstalledInputLanguages
                .Cast<System.Windows.Forms.InputLanguage>()
                .Any(l => l.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);
        }
        catch { return false; }
    }

    public static bool Add(string profile)
    {
        try { return InstallLayoutOrTip(profile, 0); }
        catch { return false; }
    }

    public static bool Remove(string profile)
    {
        try { return InstallLayoutOrTip(profile, ILOT_UNINSTALL); }
        catch { return false; }
    }
}
