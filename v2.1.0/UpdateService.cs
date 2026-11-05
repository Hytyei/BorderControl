using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BorderControl;

public sealed record UpdateInfo(Version Version, string Notes, string ExeUrl, string ShaUrl);

/// <summary>
/// Checks GitHub for a newer release, downloads it in the background, verifies its SHA-256
/// against the published checksum, and swaps the exe on restart.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string Repo = "Hytyei/BorderControl";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private static readonly HttpClient Http = CreateClient();
    private Timer? _timer;
    private int _busy;

    /// <summary>Raised (on a background thread) once a verified update is ready to install.</summary>
    public event Action<UpdateInfo>? Ready;
    public event Action<string>? Log;

    public UpdateInfo? Pending { get; private set; }
    private string? _downloaded;

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    private static string UpdateDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BorderControl", "updates");

    /// <summary>True when running as the published BorderControl.exe (not via "dotnet run"), so the exe can be swapped.</summary>
    public static bool CanInstall =>
        Environment.ProcessPath is { } p && Path.GetFileName(p).Equals("BorderControl.exe", StringComparison.OrdinalIgnoreCase);

    public void Start()
    {
        CleanUpOld();
        _timer = new Timer(_ => _ = CheckAsync(), null, FirstCheck, Interval);
    }

    public async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var r = doc.RootElement;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return;
            latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            if (latest <= Current || Pending?.Version == latest) return;

            string? exeUrl = null, shaUrl = null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (name.Equals("BorderControl.exe", StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                if (name.Equals("BorderControl.exe.sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
            }
            // Never install something we can't verify.
            if (exeUrl == null || shaUrl == null)
            {
                Log?.Invoke($"Update {latest} found, but it has no checksum – skipped");
                return;
            }

            var notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var info = new UpdateInfo(latest, CleanNotes(notes), exeUrl, shaUrl);
            Log?.Invoke($"Update {latest} found – downloading");

            var path = await DownloadAndVerifyAsync(info);
            if (path == null) return;

            _downloaded = path;
            Pending = info;
            Log?.Invoke($"Update {latest} downloaded and verified");
            Ready?.Invoke(info);
        }
        catch (Exception ex)
        {
            Log?.Invoke("Update check failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<string?> DownloadAndVerifyAsync(UpdateInfo info)
    {
        Directory.CreateDirectory(UpdateDir);
        var final = Path.Combine(UpdateDir, $"BorderControl-{info.Version}.exe");
        var part = final + ".part";

        var expected = (await Http.GetStringAsync(info.ShaUrl)).Trim().Split(' ', '\t')[0].ToLowerInvariant();
        if (expected.Length != 64)
        {
            Log?.Invoke("Update checksum file is malformed – skipped");
            return null;
        }

        if (File.Exists(final) && Sha256(final) == expected) return final;

        using (var response = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(part);
            await src.CopyToAsync(dst);
        }

        if (Sha256(part) != expected)
        {
            File.Delete(part);
            Log?.Invoke("Downloaded update failed its checksum – deleted");
            return null;
        }
        File.Move(part, final, overwrite: true);
        return final;
    }

    /// <summary>
    /// Puts the new exe in place of the running one (a running exe can be renamed, not overwritten)
    /// and starts it. Returns false if nothing was installed.
    /// </summary>
    public bool InstallAndLaunch(bool showWindow)
    {
        if (_downloaded == null || !File.Exists(_downloaded) || !CanInstall) return false;
        var exe = Environment.ProcessPath!;
        var old = exe + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            try
            {
                File.Copy(_downloaded, exe);
            }
            catch
            {
                File.Move(old, exe); // put the working version back
                throw;
            }

            var args = showWindow ? "--after-update" : "--after-update --tray";
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke("Update install failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Removes the previous exe and old downloads left by an earlier update.</summary>
    private void CleanUpOld()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
        }
        catch { /* the old process may still be closing; next start cleans it */ }

        try
        {
            if (!Directory.Exists(UpdateDir)) return;
            foreach (var f in Directory.GetFiles(UpdateDir, "BorderControl-*.exe*"))
            {
                var name = Path.GetFileNameWithoutExtension(f.Replace(".part", ""));
                if (Version.TryParse(name.Replace("BorderControl-", ""), out var v) && v <= Current) File.Delete(f);
            }
        }
        catch { /* best effort */ }
    }

    /// <summary>Drops the trailing attribution line and Markdown heading marks so notes read cleanly.</summary>
    private static string CleanNotes(string md) =>
        string.Join("\n", md.Replace("\r", "").Split('\n')
                .Where(l => !l.Contains("Generated with [Claude Code]"))
                .Select(l => l.TrimStart('#', ' ').Replace("**", "").Replace("`", "")))
            .Trim();

    private static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"BorderControl/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public void Dispose() => _timer?.Dispose();
}
