using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace BorderControl;

/// <summary>Windows "Country or region" (the GeoID apps and Microsoft services read).</summary>
public static class RegionControl
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultGeoName(StringBuilder geoName, int geoNameCount);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetUserGeoName(string geoName);

    public static string? Get()
    {
        try
        {
            var sb = new StringBuilder(16);
            return GetUserDefaultGeoName(sb, sb.Capacity) > 0 ? sb.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>Sets the region by two-letter code. Date and number formats are separate and stay as they are.</summary>
    public static bool Set(string code)
    {
        try { return SetUserGeoName(code.ToUpperInvariant()) && string.Equals(Get(), code, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}

/// <summary>Clears the Windows DNS client cache (same as "ipconfig /flushdns").</summary>
public static class DnsCache
{
    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern uint DnsFlushResolverCache();

    public static bool Flush()
    {
        try { return DnsFlushResolverCache() != 0; }
        catch { return false; }
    }
}

/// <summary>Turns IPv6 off / on for physical adapters (the "Internet Protocol Version 6" tick box).</summary>
public static class Ipv6Control
{
    /// <summary>Physical adapters that are up right now (never the VPN tunnel).</summary>
    public static List<string> PhysicalAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && !VpnDetector.IsVirtual(n))
            .Select(n => n.Name)
            .ToList();

    /// <summary>Disables IPv6 on the given adapters where it is on; returns the ones actually changed.</summary>
    public static List<string> Disable(IEnumerable<string> adapters)
    {
        var names = adapters.ToList();
        if (names.Count == 0) return new();
        var list = string.Join(",", names.Select(Quote));
        var output = PowerShell(
            $"Get-NetAdapterBinding -Name {list} -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue | " +
            "Where-Object Enabled | ForEach-Object { Disable-NetAdapterBinding -Name $_.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue; $_.Name }");
        return output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    public static void Enable(IEnumerable<string> adapters)
    {
        var names = adapters.ToList();
        if (names.Count == 0) return;
        PowerShell($"Enable-NetAdapterBinding -Name {string.Join(",", names.Select(Quote))} -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue");
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static string PowerShell(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(20_000);
            return output;
        }
        catch { return ""; }
    }
}

/// <summary>
/// "Start with Windows": a scheduled task that runs at sign-in with highest privileges,
/// so there is no admin prompt every time. A Run-key entry would trigger UAC.
/// </summary>
public static class StartupTask
{
    private const string Name = "Border Control";

    public static bool IsEnabled() => Schtasks("/Query", "/TN", Name) == 0;

    public static bool Enable()
    {
        if (Environment.ProcessPath is not { } exe) return false;
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        return Schtasks("/Create", "/TN", Name, "/TR", $"\"{exe}\" --tray", "/SC", "ONLOGON",
                   "/RU", user, "/IT", "/RL", "HIGHEST", "/F") == 0;
    }

    public static bool Disable() => Schtasks("/Delete", "/TN", Name, "/F") == 0;

    private static int Schtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(15_000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
