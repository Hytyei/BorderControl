using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

namespace BorderControl;

/// <summary>
/// WebRTC / UDP leak test: browsers use STUN over UDP to find their public address for calls and
/// WebRTC, and sites can read that address. If UDP skips the VPN (common in proxy mode or with
/// TCP-only VPNs), STUN reveals your real IP even though websites see the VPN.
/// </summary>
public static class StunCheck
{
    private static readonly (string Host, int Port)[] Servers =
    {
        ("stun.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    };

    public static async Task<Report> RunAsync(GeoInfo? vpn, GeoInfo? real)
    {
        string? mapped = null, server = null;
        foreach (var (host, port) in Servers)
        {
            mapped = await MappedAddressAsync(host, port);
            if (mapped != null) { server = host; break; }
        }

        var facts = new List<Fact>();
        if (mapped == null)
            return new Report(Level.Ok, "UDP to STUN servers is blocked – nothing to leak")
            {
                Facts = new[] { new Fact("STUN result", "no answer", Level.Ok) },
                Meaning = "No STUN server answered over UDP, so browsers can't use WebRTC to find your address either. " +
                          "Video calls in the browser may not work, but nothing can leak this way.",
            };

        var g = await GeoService.LookupAsync(mapped);
        bool isReal = real != null && (mapped == real.Ip
                      || (g != null && real.Isp.Length > 0 && g.Isp == real.Isp)
                      || (g != null && g.CountryCode == real.CountryCode && vpn != null && vpn.CountryCode != real.CountryCode));
        facts.Add(new Fact("Address seen over UDP", mapped, isReal ? Level.Bad : Level.Ok));
        if (g != null)
        {
            facts.Add(new Fact("Location", string.Join(", ", new[] { g.City, g.Country }.Where(s => s.Length > 0))) { CountryCode = g.CountryCode });
            facts.Add(new Fact("Network", g.Isp.Length > 0 ? g.Isp : "unknown"));
        }
        facts.Add(new Fact("STUN server", server!));

        if (isReal)
            return new Report(Level.Bad, "LEAK – UDP shows your real IP")
            {
                Facts = facts,
                Meaning = "UDP traffic leaves your PC outside the VPN. Any website can use WebRTC (built into every browser) " +
                          "to read this address – your real one – without asking.\n\n" +
                          "Fix: switch your VPN app to its full VPN / TUN mode, or turn off WebRTC in your browser " +
                          "(e.g. uBlock Origin → \"Prevent WebRTC from leaking local IP addresses\", or Firefox " +
                          "about:config → media.peerconnection.enabled = false).",
            };

        if (vpn != null && g != null && g.CountryCode != vpn.CountryCode)
            return new Report(Level.Warn, $"UDP exits in {g.Country}, not {vpn.Country}")
            {
                Facts = facts,
                Meaning = "UDP goes through a VPN-like address, but in another country than your web traffic. It isn't your " +
                          "real address, but a site using WebRTC may see two locations. Often it's just location databases " +
                          "disagreeing about the same VPN server.",
            };

        return new Report(Level.Ok, "No WebRTC leak – UDP goes through the VPN")
        {
            Facts = facts,
            Meaning = "Browsers' WebRTC sees the VPN's address too, so it can't reveal your real IP.",
        };
    }

    /// <summary>Sends one STUN Binding Request (RFC 5389) and returns the public IPv4 the server saw.</summary>
    private static async Task<string?> MappedAddressAsync(string host, int port)
    {
        try
        {
            var addr = (await Dns.GetHostAddressesAsync(host)).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (addr == null) return null;

            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var req = new byte[20];
            req[1] = 0x01;                                                // Binding Request
            new byte[] { 0x21, 0x12, 0xA4, 0x42 }.CopyTo(req, 4);         // magic cookie
            Random.Shared.NextBytes(req.AsSpan(8, 12));                   // transaction id
            await udp.SendAsync(req, req.Length, new IPEndPoint(addr, port));

            var receive = udp.ReceiveAsync();
            if (await Task.WhenAny(receive, Task.Delay(2500)) != receive) return null;
            var res = receive.Result.Buffer;
            if (res.Length < 20 || res[0] != 0x01 || res[1] != 0x01) return null;

            int i = 20;
            while (i + 4 <= res.Length)
            {
                int type = (res[i] << 8) | res[i + 1];
                int len = (res[i + 2] << 8) | res[i + 3];
                int v = i + 4;
                if (v + len > res.Length) break;
                if ((type == 0x0020 || type == 0x0001) && len >= 8 && res[v + 1] == 0x01)
                {
                    var ip = new byte[4];
                    for (int k = 0; k < 4; k++)
                        ip[k] = type == 0x0020 ? (byte)(res[v + 4 + k] ^ req[4 + k]) : res[v + 4 + k];
                    return new IPAddress(ip).ToString();
                }
                i = v + ((len + 3) & ~3);
            }
            return null;
        }
        catch { return null; }
    }
}

/// <summary>
/// How recognisable the VPN exit is: IP-reputation lists mark VPN, proxy and hosting (data-centre)
/// addresses, and many sites check them. Nothing on your PC can change this – it's informational.
/// Uses proxycheck.io (free, no key, HTTPS) for the IPv4 exit and, when present, the IPv6 exit.
/// </summary>
public static class VisibilityCheck
{
    private static readonly HttpClient Http = CreateClient();

    private sealed record Rep(string Ip, bool Proxy, string Type, string Provider, string Operator, string Cc, string Place);

    public static async Task<Report> RunAsync()
    {
        try
        {
            var v4 = await TryGet("https://api.ipify.org");
            var v6 = await GeoService.PublicIpv6Async();
            var reps = new List<Rep>();
            foreach (var ip in new[] { v4, v6 }.Where(x => !string.IsNullOrEmpty(x)))
                if (await LookupAsync(ip!) is { } r) reps.Add(r);
            if (reps.Count == 0) throw new InvalidOperationException("no answer from the reputation service");

            var facts = new List<Fact>();
            foreach (var r in reps)
            {
                if (reps.Count > 1) facts.Add(Fact.Group(r.Ip.Contains(':') ? "IPv6 exit" : "IPv4 exit"));
                facts.Add(new Fact("Exit address", r.Ip) { CountryCode = r.Cc });
                facts.Add(new Fact("Listed location", r.Place) { CountryCode = r.Cc });
                facts.Add(new Fact("Network", r.Provider.Length > 0 ? r.Provider : "unknown"));
                facts.Add(new Fact("Listed as", Label(r), Flagged(r) ? Level.Warn : Level.Ok));
                if (r.Operator.Length > 0) facts.Add(new Fact("Attributed to", r.Operator, Level.Warn));
            }

            var worst = reps.FirstOrDefault(Flagged);
            if (worst != null)
                return new Report(Level.Warn, $"Sites can tell this is a {Lower(Label(worst))} address")
                {
                    Facts = facts,
                    Meaning =
                        $"Reputation lists mark your exit address as {Lower(Label(worst))}" +
                        (worst.Operator.Length > 0 ? $" (attributed to {worst.Operator})" : "") +
                        ". Services that check IP reputation – streaming, banks, some shops, Google captchas – can see " +
                        "you're using a VPN, but not who you are or where you really are.\n\n" +
                        "No setting on your PC can change this; it's how the VPN's addresses are known. Only VPNs with " +
                        "residential or mobile addresses look like ordinary home connections.",
                };

            return new Report(Level.Ok, "Exit address looks like an ordinary connection")
            {
                Facts = facts,
                Meaning = "Reputation lists don't mark this address as a VPN, proxy or data centre, so most sites see an ordinary connection.",
            };
        }
        catch (Exception ex)
        {
            return new Report(Level.Unknown, "Visibility check unavailable")
            {
                Facts = new[] { new Fact("Error", ex.Message, Level.Warn) },
                Meaning = "The reputation service (proxycheck.io) didn't answer. It's only informational – press Check later.",
            };
        }
    }

    /// <summary>"Data centre" → "data centre", but acronyms like "VPN" stay as they are.</summary>
    private static string Lower(string s) => s.Length > 1 && s == s.ToUpperInvariant() ? s : s.ToLowerInvariant();

    private static bool Flagged(Rep r) => r.Proxy || r.Type.Equals("Hosting", StringComparison.OrdinalIgnoreCase);

    private static string Label(Rep r) =>
        r.Proxy ? (r.Type.Length > 0 ? r.Type : "Proxy")
        : r.Type.Equals("Hosting", StringComparison.OrdinalIgnoreCase) ? "Data centre"
        : r.Type.Length > 0 ? r.Type : "Ordinary";

    private static async Task<Rep?> LookupAsync(string ip)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://proxycheck.io/v2/{ip}?vpn=1&asn=1"));
            if (!doc.RootElement.TryGetProperty(ip, out var e)) return null;
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string op = e.TryGetProperty("operator", out var o) && o.TryGetProperty("name", out var on) ? on.GetString() ?? "" : "";
            var place = string.Join(", ", new[] { S("city"), S("country") }.Where(x => x.Length > 0));
            return new Rep(ip, S("proxy") == "yes", S("type"), S("provider"), op, S("isocode"), place);
        }
        catch { return null; }
    }

    private static async Task<string?> TryGet(string url)
    {
        try { return (await Http.GetStringAsync(url)).Trim(); } catch { return null; }
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BorderControl/2");
        return c;
    }
}
