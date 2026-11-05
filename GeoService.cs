using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VpnGuard;

/// <summary>Public-IP geolocation (HTTPS only). Primary ipwho.is, fallback ipapi.co.</summary>
public static class GeoService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BorderControl/1.0");
        return c;
    }

    /// <summary>Looks up the caller's public IP, or the given IP when supplied.</summary>
    public static async Task<GeoInfo?> LookupAsync(string? ip = null, CancellationToken ct = default)
        => await TryIpWho(ip, ct) ?? await TryIpApiCo(ip, ct);

    /// <summary>Returns the machine's public IPv6 address, or null when it has no IPv6 route.</summary>
    public static async Task<string?> PublicIpv6Async(CancellationToken ct = default)
    {
        // Some VPN DNS servers can't resolve one service or another, so try several and accept the
        // first answer that is actually an IPv6 address (dual-stack services return IPv4 when v6 is absent).
        string[] urls =
        {
            "https://api6.ipify.org",
            "https://v6.ident.me",
            "https://api64.ipify.org",
            "https://ipv6.icanhazip.com",
        };
        foreach (var url in urls)
        {
            try
            {
                var s = (await Http.GetStringAsync(url, ct)).Trim();
                if (s.Contains(':') && System.Net.IPAddress.TryParse(s, out _)) return s;
            }
            catch { /* try the next one */ }
        }
        return null;
    }

    private static async Task<GeoInfo?> TryIpWho(string? ip, CancellationToken ct)
    {
        try
        {
            var json = await Http.GetStringAsync("https://ipwho.is/" + (ip ?? ""), ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False) return null;

            var tz = r.TryGetProperty("timezone", out var t) ? Str(t, "id") : "";
            var isp = r.TryGetProperty("connection", out var c) ? Str(c, "isp") : "";
            return new GeoInfo(Str(r, "ip"), Str(r, "country"), Str(r, "country_code"), Str(r, "city"), tz, isp);
        }
        catch { return null; }
    }

    private static async Task<GeoInfo?> TryIpApiCo(string? ip, CancellationToken ct)
    {
        try
        {
            var url = string.IsNullOrEmpty(ip) ? "https://ipapi.co/json/" : $"https://ipapi.co/{ip}/json/";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out _)) return null;
            return new GeoInfo(Str(r, "ip"), Str(r, "country_name"), Str(r, "country_code"),
                Str(r, "city"), Str(r, "timezone"), Str(r, "org"));
        }
        catch { return null; }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
