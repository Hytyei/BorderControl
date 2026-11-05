using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VpnGuard;

/// <summary>
/// DNS leak test: resolves unique sub-domains of a leak-test service, then asks the service
/// which resolver IPs actually reached its name server, and compares them with the VPN exit and your real ISP.
/// </summary>
public static class DnsLeakService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<DnsResult> RunAsync(GeoInfo? vpn, GeoInfo? real, CancellationToken ct = default)
    {
        try
        {
            var id = (await Http.GetStringAsync("https://bash.ws/id", ct)).Trim();
            if (id.Length == 0 || id.Length > 64 || id.Any(ch => !char.IsLetterOrDigit(ch)))
                return new DnsResult(Level.Warn, "Leak-test service returned an unexpected id.");

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
                return new DnsResult(Level.Warn, "No DNS resolvers were reported by the test.");

            var realCode = real?.CountryCode ?? "";
            var realIsp = real?.Isp ?? "";
            string exitText = exitCodes.Count == 0 ? "" : string.Join("/", exitCodes.Select(c => c.ToUpperInvariant()));

            bool RealNetwork(Resolver r) =>
                (!string.IsNullOrEmpty(realIsp) && r.Asn.Contains(realIsp, StringComparison.OrdinalIgnoreCase))
                || (realCode.Length == 2 && !exitCodes.Contains(realCode) && r.Code.Equals(realCode, StringComparison.OrdinalIgnoreCase));

            var inReal = resolvers.Where(RealNetwork).ToList();
            if (inReal.Count > 0)
                return new DnsResult(Level.Bad,
                    $"LEAK – {Count(inReal.Count, resolvers.Count)} in your real network: {Show(inReal)}. " +
                    "Route DNS through the VPN in your VPN app.");

            if (exitCodes.Count == 0)
                return new DnsResult(Level.Warn, $"VPN country unknown, cannot judge resolvers: {Show(resolvers)}");

            // A resolver in a third country means some lookups leave the PC outside the tunnel
            // (e.g. an anycast resolver answering from a city near you).
            var foreign = resolvers.Where(r => r.Code.Length == 2 && !exitCodes.Contains(r.Code)).ToList();
            if (foreign.Count > 0)
                return new DnsResult(Level.Bad,
                    $"LEAK – {Count(foreign.Count, resolvers.Count)} outside the VPN country ({exitText}): {Show(foreign)}. " +
                    "Some DNS lookups bypass the tunnel – route DNS through the VPN in your VPN app.");

            return new DnsResult(Level.Ok, $"No leak – all {resolvers.Count} resolvers are in the VPN country ({exitText}): {Show(resolvers)}");
        }
        catch (Exception ex)
        {
            return new DnsResult(Level.Warn, "DNS leak test unavailable: " + ex.Message);
        }
    }

    private static async Task ResolveQuietly(string host)
    {
        try { await Dns.GetHostAddressesAsync(host); } catch { /* expected: names only exist to be observed */ }
    }

    private sealed record Resolver(string Ip, string Code, string Name, string Asn);

    private static string Show(IEnumerable<Resolver> list) =>
        string.Join("; ", list.Select(r => $"{r.Ip} ({r.Name}, {r.Asn})"));

    private static string Count(int bad, int total) =>
        total == 1 ? "the resolver is" : bad == 1 ? $"1 of {total} resolvers is" : $"{bad} of {total} resolvers are";


    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
