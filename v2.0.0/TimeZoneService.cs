using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace VpnGuard;

/// <summary>Reads and changes the Windows system time zone (needs admin rights).</summary>
public static class TimeZoneService
{
    private const string AutoTzKey = @"SYSTEM\CurrentControlSet\Services\tzautoupdate";

    public static string CurrentWindowsId()
    {
        TimeZoneInfo.ClearCachedData();
        return TimeZoneInfo.Local.Id;
    }

    /// <summary>Converts an IANA id such as "Europe/Berlin" to a Windows id such as "W. Europe Standard Time".</summary>
    public static string? ToWindowsId(string iana)
    {
        if (string.IsNullOrWhiteSpace(iana)) return null;
        return TimeZoneInfo.TryConvertIanaIdToWindowsId(iana, out var win) ? win : null;
    }

    public static bool Set(string windowsId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(windowsId); // validates the id before we hand it to tzutil
            var ok = Run("tzutil.exe", "/s", windowsId);
            TimeZoneInfo.ClearCachedData();
            return ok;
        }
        catch { return false; }
    }

    /// <summary>Start value of the "Set time zone automatically" service (3 = manual, 4 = disabled).</summary>
    public static int? GetAutoTzStart() =>
        Registry.LocalMachine.OpenSubKey(AutoTzKey)?.GetValue("Start") is int i ? i : null;

    public static void SetAutoTzStart(int value)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(AutoTzKey, writable: true);
            k?.SetValue("Start", value, RegistryValueKind.DWord);
            if (value == 4) Run("sc.exe", "stop", "tzautoupdate");
        }
        catch { /* not elevated or key missing */ }
    }

    private static bool Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.WaitForExit(10_000);
        return p.HasExited && p.ExitCode == 0;
    }
}
