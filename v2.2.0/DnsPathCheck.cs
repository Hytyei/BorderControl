using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BorderControl;

/// <param name="Server">DNS server Windows is configured to use.</param>
/// <param name="Adapter">Adapter its queries leave through.</param>
/// <param name="ViaTunnel">True when that adapter is the VPN tunnel.</param>
/// <param name="Answers">Outside the tunnel only: did the server answer a probe (true = open leak path)?</param>
public sealed record DnsPath(string Server, string Adapter, bool ViaTunnel, bool? Answers);

/// <summary>
/// Local, location-database-free DNS leak check: for every DNS server Windows uses, find the adapter its
/// queries would leave through. A server reached outside the tunnel that still answers is a real leak path.
/// </summary>
public static class DnsPathCheck
{
    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterfaceEx(byte[] destAddr, out uint bestIfIndex);

    public static async Task<List<DnsPath>> RunAsync()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        var servers = new List<IPAddress>();
        foreach (var n in up)
        {
            IPAddressCollection dns;
            try { dns = n.GetIPProperties().DnsAddresses; } catch { continue; }
            foreach (var a in dns)
            {
                // fec0:0:0:ffff::1-3 are Windows' unused IPv6 placeholders, not real servers.
                if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6SiteLocal) continue;
                if (!servers.Contains(a)) servers.Add(a);
            }
        }

        var results = new List<DnsPath>();
        var probes = new List<Task>();
        foreach (var server in servers)
        {
            var nic = Egress(up, server);
            string name = nic?.Name ?? "unknown adapter";
            if (nic == null || VpnDetector.IsVirtual(nic))
            {
                results.Add(new DnsPath(server.ToString(), name, nic != null, null));
                continue;
            }

            int slot = results.Count;
            results.Add(new DnsPath(server.ToString(), name, false, null));
            probes.Add(Task.Run(async () =>
            {
                bool answers = await AnswersAsync(server);
                lock (results) results[slot] = results[slot] with { Answers = answers };
            }));
        }
        await Task.WhenAll(probes);
        return results;
    }

    /// <summary>Turns the paths into a report. Returns null when nothing was found to judge.</summary>
    public static Report? Verdict(List<DnsPath> paths)
    {
        if (paths.Count == 0) return null;

        var facts = new List<Fact> { Fact.Group("Route check (this PC)") };
        facts.AddRange(paths.Select(p => new Fact(p.Server, Describe(p),
            p.ViaTunnel || p.Answers == false ? Level.Ok : p.Answers == true ? Level.Bad : Level.Unknown)));

        var open = paths.Where(p => !p.ViaTunnel && p.Answers == true).ToList();
        if (open.Count > 0)
        {
            var first = open[0];
            return new Report(Level.Bad, $"LEAK – Windows can send DNS to {first.Server} via {first.Adapter}")
            {
                Facts = facts,
                Meaning =
                    $"Windows is set to also use {string.Join(", ", open.Select(p => p.Server))}, reached through " +
                    $"{string.Join(", ", open.Select(p => p.Adapter).Distinct())} – outside the VPN – and it answered. " +
                    "That's usually your router, which passes lookups to your internet provider. Windows asks several DNS " +
                    "servers at once, so your provider can see the names of sites you visit.\n\n" +
                    "This check uses your PC's own routing, not location databases, so it is reliable.\n\n" +
                    "Fix: turn on DNS leak protection or the kill switch in your VPN app, or set that adapter's DNS to the VPN's.",
            };
        }

        bool blocked = paths.Any(p => !p.ViaTunnel && p.Answers == false);
        return new Report(Level.Ok, "Windows DNS stays inside the VPN")
        {
            Facts = facts,
            Meaning = (blocked
                    ? "Windows still knows other DNS servers (usually your router), but the VPN blocks them – they didn't answer. "
                    : "Every DNS server Windows uses is reached through the VPN tunnel. ") +
                "This check uses your PC's own routing, not location databases, so it is reliable.",
        };
    }

    private static string Describe(DnsPath p) =>
        p.ViaTunnel ? $"via {p.Adapter} (VPN tunnel)"
        : p.Answers == true ? $"via {p.Adapter} – outside the VPN, answered"
        : p.Answers == false ? $"via {p.Adapter} – outside the VPN, blocked"
        : $"via {p.Adapter}";

    private static NetworkInterface? Egress(List<NetworkInterface> up, IPAddress dest)
    {
        byte[] sa;
        if (dest.AddressFamily == AddressFamily.InterNetwork)
        {
            sa = new byte[16];
            BitConverter.GetBytes((ushort)2).CopyTo(sa, 0);               // AF_INET
            dest.GetAddressBytes().CopyTo(sa, 4);
        }
        else
        {
            sa = new byte[28];
            BitConverter.GetBytes((ushort)23).CopyTo(sa, 0);              // AF_INET6
            dest.GetAddressBytes().CopyTo(sa, 8);
            BitConverter.GetBytes((uint)dest.ScopeId).CopyTo(sa, 24);
        }
        if (GetBestInterfaceEx(sa, out uint index) != 0) return null;

        foreach (var n in up)
        {
            try
            {
                var p = n.GetIPProperties();
                if (p.GetIPv4Properties()?.Index == index) return n;
            }
            catch (NetworkInformationException) { /* no IPv4 */ }
            try
            {
                if (n.GetIPProperties().GetIPv6Properties()?.Index == index) return n;
            }
            catch (NetworkInformationException) { /* no IPv6 */ }
        }
        return null;
    }

    /// <summary>
    /// Sends one DNS query for a random name under the reserved ".invalid" domain (it can never resolve and
    /// carries nothing about you) and reports whether any reply came back within 1.5 s.
    /// </summary>
    private static async Task<bool> AnswersAsync(IPAddress server)
    {
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            var query = BuildQuery($"bc{Guid.NewGuid():N}".Substring(0, 20) + ".invalid");
            await udp.SendAsync(query, query.Length, new IPEndPoint(server, 53));
            var receive = udp.ReceiveAsync();
            var done = await Task.WhenAny(receive, Task.Delay(1500));
            return done == receive && receive.Result.Buffer.Length >= 12;
        }
        catch { return false; }
    }

    private static byte[] BuildQuery(string name)
    {
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // end of name, type A, class IN
        return bytes.ToArray();
    }
}
