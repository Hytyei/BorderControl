using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BorderControl;

/// <summary>
/// Country flags bundled into the exe (Windows can't draw flag emoji – it shows two letters instead).
/// Look-ups are cached; unknown codes return null so the UI simply shows no flag.
/// </summary>
public static class Flags
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? For(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(countryCode, out var cached)) return cached;
            ImageSource? img = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/BorderControl;component/Assets/Flags/{countryCode.ToLowerInvariant()}.png");
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { /* no flag for this code */ }
            Cache[countryCode] = img;
            return img;
        }
    }
}

/// <summary>XAML: Source="{Binding CountryCode, Converter={StaticResource Flag}}".</summary>
public sealed class FlagConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) => Flags.For(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
