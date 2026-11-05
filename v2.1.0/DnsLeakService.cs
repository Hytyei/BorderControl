using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BorderControl;

/// <summary>
/// External DNS test: resolves unique sub-domains of a leak-test service, then asks the service
/// which resolver IPs actually reached its name server, and compares them with the VPN exit and your real ISP.
/// </summary>
public static class DnsLeakService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private sealed record Resolver(string Ip, string Code, string Name, string Asn);

    public static async Task<Report> RunAsync(GeoInfo? vpn, GeoInfo? real, CancellationToken ct = default)
    {
        try
        {
            var id = (await Http.GetStringAsync("https://bash.ws/id", ct)).Trim();
            if (id.Length == 0 || id.Length > 64 || id.Any(ch => !char.IsLetterOrDigit(ch)))
                return Unavailable("The test service returned an unexpected answer.");

            // Fire several unique lookups so every resolver in use shows up.
            await Task.WhenAll(Enumerable.Range(1, 8).Select(i => ResolveQuietly($"{i}.{id}.bash.ws")));

            var json = await Http.GetStringAsync($"https://bash.ws/dnsleak/test/{id}?json", ct);
            using var doc = JsonDocument.Parse(json);

            var resolvers = new List<Resolver>();
            var exitCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(vpn?.CountryCode)) exitCodes.Add(vpn!.CountryCode);

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var type = Str(e, "type");
                // The service also reports where *it* thinks the VPN exit is. Location databases often
                // disagree about VPN servers (e.g. US vs DE), so either answer counts as the exit country.
                if (type == "ip" && Str(e, "country").Length == 2) exitCodes.Add(Str(e, "country"));
                if (type == "dns")
                    resolvers.Add(new Resolver(Str(e, "ip"), Str(e, "country").ToUpperInvariant(), Str(e, "country_name"), Str(e, "asn")));
            }

            if (resolvers.Count == 0)
                return Unavailable("No DNS servers were reported by the test.");

            var realCode = real?.CountryCode ?? "";
            var realIsp = real?.Isp ?? "";
            string exitText = exitCodes.Count == 0 ? "unknown" : string.Join(" / ", exitCodes.Select(c => c.ToUpperInvariant()));

            bool RealNetwork(Resolver r) =>
                (!string.IsNullOrEmpty(realIsp) && r.Asn.Contains(realIsp, StringComparison.OrdinalIgnoreCase))
                || (realCode.Length == 2 && !exitCodes.Contains(realCode) && r.Code.Equals(realCode, StringComparison.OrdinalIgnoreCase));
            bool Foreign(Resolver r) => exitCodes.Count > 0 && r.Code.Length == 2 && !exitCodes.Contains(r.Code);

            var facts = new List<Fact>
            {
                Fact.Group("Resolver test (bash.ws)"),
                new("VPN exit country", exitText),
            };
            facts.AddRange(resolvers.Select(r => new Fact(
                r.Ip,
                $"{r.Name} · {Company(r.Asn)}",
                RealNetwork(r) || Foreign(r) ? Level.Bad : exitCodes.Count == 0 ? Level.Unknown : Level.Ok)));

            var companies = string.Join(", ", resolvers.Select(r => Company(r.Asn)).Distinct());

            var inReal = resolvers.Where(RealNetwork).ToList();
            if (inReal.Count > 0)
                return new Report(Level.Bad, $"LEAK – {Count(inReal.Count, resolvers.Count)} in your real network")
                {
                    Facts = facts,
                    Meaning =
                        "Some site-name lookups are answered by servers of your own internet provider or home country. " +
                        "Your provider can log every site you visit, even though the pages themselves go through the VPN.\n\n" +
                        "Fix: turn on DNS leak protection in your VPN app, or switch it to its full VPN / TUN mode.",
                };

            if (exitCodes.Count == 0)
                return new Report(Level.Warn, "VPN country unknown – resolvers can't be judged")
                {
                    Facts = facts,
                    Meaning = "Border Control couldn't find out where the VPN exit is, so it can't tell whether these DNS servers are in the right place. Press Re-check in a moment.",
                };

            var foreign = resolvers.Where(Foreign).ToList();
            if (foreign.Count > 0)
            {
                var where = string.Join(", ", foreign.Select(r => r.Name).Distinct());
                return new Report(Level.Bad, $"LEAK – {Count(foreign.Count, resolvers.Count)} outside the VPN country")
                {
                    Facts = facts,
                    Meaning =
                        $"Lookups are answered from {where}, which isn't the VPN's country ({exitText}). Public DNS services like Google and " +
                        "Cloudflare answer from the data centre nearest to where a lookup enters the internet, so this usually means " +
                        "the lookups leave your PC outside the tunnel – often because the VPN app resolves names itself (\"direct\" DNS " +
                        "in v2ray/xray apps). Your provider can see these lookups.\n\n" +
                        "Possible false alarm: location databases sometimes put Google or Cloudflare servers in the wrong country. " +
                        "If the route check is fine and the country is next to your VPN server, it is probably harmless.\n\n" +
                        "Fix: in your VPN app, send DNS through the proxy (\"remote DNS\" / \"FakeDNS\"), or turn on DNS leak protection.",
                };
            }

            return new Report(Level.Ok, $"No leak – {resolvers.Count} resolvers in the VPN country ({exitText})")
            {
                Facts = facts,
                Meaning =
                    $"Every site name you look up is answered by {companies} servers in the VPN's country, so your internet provider " +
                    "can't see which sites you visit. It's normal for VPNs to use big public DNS services like these.",
            };
        }
        catch (Exception ex)
        {
            return Unavailable(ex.Message);
        }
    }

    private static Report Unavailable(string why) => new(Level.Warn, "Resolver test unavailable")
    {
        Facts = new[] { Fact.Group("Resolver test (bash.ws)"), new Fact("Error", why, Level.Warn) },
        Meaning = "The online test service didn't answer, so this part couldn't run. It's usually temporary – press Re-check later.",
    };

    private static async Task ResolveQuietly(string host)
    {
        try { await Dns.GetHostAddressesAsync(host); } catch { /* expected: names only exist to be observed */ }
    }

    /// <summary>"AS15169 Google LLC" → "Google LLC".</summary>
    private static string Company(string asn)
    {
        var parts = asn.Split(' ', 2);
        return parts.Length == 2 && parts[0].StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? parts[1] : asn;
    }

    private static string Count(int bad, int total) =>
        total == 1 ? "the resolver is" : bad == 1 ? $"1 of {total} resolvers is" : $"{bad} of {total} resolvers are";

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
