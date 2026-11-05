using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace VpnGuard;

public enum VpnKind { None, Tunnel, Proxy }

/// <param name="Kind">How traffic is protected.</param>
/// <param name="Description">Adapter name or proxy address.</param>
/// <param name="Note">Extra info, e.g. a VPN adapter that is up but not carrying traffic.</param>
public sealed record VpnStatus(VpnKind Kind, string Description, string Note)
{
    public bool Connected => Kind != VpnKind.None;
}

/// <summary>
/// Decides whether internet traffic is going through a VPN.
///
/// A tunnel counts as connected when the adapter Windows would use to reach the public internet
/// is a virtual one (WireGuard/Wintun, TAP, PPP/IKEv2, or a TUN adapter from v2ray/xray/sing-box
/// clients such as Happ, Hiddify, Clash or v2rayN). Checking the route instead of only the adapter
/// name catches clients we have never heard of and ignores idle VPN adapters that carry nothing.
/// If no tunnel carries the traffic, an enabled system proxy counts as "proxy mode".
/// </summary>
public static class VpnDetector
{
    // IF_TYPE_PROP_VIRTUAL: what Wintun, WireGuard and most TUN drivers report. .NET has no enum member for it.
    private const NetworkInterfaceType PropVirtual = (NetworkInterfaceType)53;

    private static readonly string[] Keywords =
    {
        "wireguard", "wintun", "tap-windows", "tap0901", "openvpn", "nordlynx", "nordvpn", "proton",
        "expressvpn", "mullvad", "surfshark", "cyberghost", "private internet access", "windscribe",
        "anyconnect", "fortinet", "globalprotect", "pangp", "ivpn", "hotspot shield", "tunnelbear",
        "pritunl", "outline", "happ", "hiddify", "v2ray", "xray", "sing-box", "singbox", "clash",
        "mihomo", "nekoray", "sockstunnel", "tun", "vpn",
    };

    private static readonly string[] Ignore =
    {
        "teredo", "isatap", "6to4", "loopback", "hyper-v", "vethernet", "vmware", "virtualbox",
        "bluetooth", "wi-fi direct", "wan miniport",
    };

    // Public addresses used only for a local routing-table lookup; nothing is sent to them.
    private static readonly IPAddress[] Probes = { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8") };

    public static VpnStatus Detect()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        // 1. Which adapter actually carries internet traffic?
        var routing = new List<NetworkInterface>();
        foreach (var probe in Probes)
        {
            var ni = BestInterface(up, probe);
            if (ni != null && !routing.Contains(ni)) routing.Add(ni);
        }

        var tunnels = routing.Where(IsVirtual).ToList();
        if (tunnels.Count > 0)
            return new VpnStatus(VpnKind.Tunnel, string.Join(", ", tunnels.Select(t => t.Name)), "");

        // VPN-looking adapters that are up but not routing traffic (split tunnel, idle, Tailscale without exit node…)
        var idle = up.Where(n => IsVirtual(n) && !Ignored(n)).Select(n => n.Name).ToList();
        string note = idle.Count > 0
            ? $"{string.Join(", ", idle)} is up, but internet traffic does not go through it."
            : "";

        // 2. No tunnel: is a system proxy set (v2rayN/Happ/Clash "system proxy" mode)?
        var proxy = SystemProxy();
        if (proxy != null) return new VpnStatus(VpnKind.Proxy, proxy, note);

        return new VpnStatus(VpnKind.None, "", note);
    }

    private static bool Ignored(NetworkInterface n)
    {
        var text = (n.Name + " " + n.Description).ToLowerInvariant();
        return Ignore.Any(text.Contains);
    }

    private static bool IsVirtual(NetworkInterface n)
    {
        if (Ignored(n)) return false;
        if (n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel or PropVirtual)
            return true;
        var text = (n.Name + " " + n.Description).ToLowerInvariant();
        return Keywords.Any(text.Contains);
    }

    private static NetworkInterface? BestInterface(List<NetworkInterface> up, IPAddress dest)
    {
        uint addr = BitConverter.ToUInt32(dest.GetAddressBytes(), 0);
        if (GetBestInterface(addr, out uint index) != 0) return null;

        foreach (var n in up)
        {
            try
            {
                if (n.GetIPProperties().GetIPv4Properties()?.Index == index) return n;
            }
            catch (NetworkInformationException) { /* adapter has no IPv4 */ }
        }
        return null;
    }

    /// <summary>Returns the current user's system proxy (e.g. "127.0.0.1:10809" or a PAC URL), or null.</summary>
    private static string? SystemProxy()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (k == null) return null;
            if (k.GetValue("ProxyEnable") is int on && on != 0 && k.GetValue("ProxyServer") is string s && s.Length > 0)
                return s;
            if (k.GetValue("AutoConfigURL") is string pac && pac.Length > 0)
                return "PAC " + pac;
        }
        catch { /* ignore */ }
        return null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
}
