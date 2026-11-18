using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BorderControl;

/// <summary>Closes chosen apps when the VPN drops, and helps pick them.</summary>
public static class KillSwitch
{
    /// <summary>
    /// Ends every running instance of the given apps immediately (a polite close could take seconds,
    /// during which the app would keep talking without the VPN). Returns the names that were closed.
    /// </summary>
    public static List<string> Close(IEnumerable<KillApp> apps)
    {
        var closed = new List<string>();
        int self = Environment.ProcessId;
        foreach (var app in apps)
        {
            var procName = Path.GetFileNameWithoutExtension(app.Path);
            Process[] procs;
            try { procs = Process.GetProcessesByName(procName); } catch { continue; }
            foreach (var p in procs)
            {
                try
                {
                    if (p.Id == self) continue;
                    // Only the exact program: another exe that happens to share the name is left alone.
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* protected process */ }
                    if (path != null && !path.Equals(app.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(entireProcessTree: true);
                    if (!closed.Contains(app.Name)) closed.Add(app.Name);
                }
                catch { /* already gone or not allowed */ }
                finally { p.Dispose(); }
            }
        }
        return closed;
    }

    /// <summary>Running programs that have a window, one entry per exe, sorted by name.</summary>
    public static List<KillApp> RunningApps()
    {
        var result = new Dictionary<string, KillApp>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                var path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path) || result.ContainsKey(path)) continue;
                result[path] = new KillApp(NameFor(path), path);
            }
            catch { /* no access */ }
            finally { p.Dispose(); }
        }
        return result.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Friendly name from the exe's version info ("Telegram Desktop"), else its file name.</summary>
    public static string NameFor(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch { /* fall back */ }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The exe's own icon for the settings list.</summary>
    public static ImageSource? IconFor(string path)
    {
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? img = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                img = src;
            }
        }
        catch { /* no icon */ }
        IconCache[path] = img;
        return img;
    }
}
