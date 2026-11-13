using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Layts.Core;

public static class Steam
{
    /// <summary>Alle möglichen Steam-Installationsordner (Registry + Standardpfad).</summary>
    public static IReadOnlyList<string> Directories { get; } = FindDirectories();

    private static List<string> FindDirectories()
    {
        var candidates = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam
                && !string.IsNullOrWhiteSpace(steam))
                candidates.Add(Path.GetFullPath(steam.Replace('/', '\\')));
        }
        catch
        {
            // Registry nicht lesbar.
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }
}

/// <summary>
/// Lädt die Bibliotheksbilder, die Steam ohnehin lokal zwischenspeichert
/// (appcache\librarycache\&lt;AppId&gt;). Es wird nichts heruntergeladen.
/// </summary>
public static class SteamArt
{
    public const string Cover = "library_600x900.jpg";
    public const string Hero = "library_hero.jpg";
    public const string Logo = "logo.png";

    private static readonly Dictionary<string, ImageSource?> Cache = new();

    public static ImageSource? Load(string? appId, string file, int decodeWidth = 0)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        var key = $"{appId}/{file}/{decodeWidth}";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            var path = Find(appId, file);
            if (path != null)
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
            }
        }
        catch
        {
            image = null;
        }

        Cache[key] = image;
        return image;
    }

    /// <summary>
    /// Das kleine App-Icon (32×32), das Steam unter einem Hash-Namen ablegt (40 Hex-Zeichen + .jpg).
    /// </summary>
    public static ImageSource? LoadIcon(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        var key = $"{appId}/icon";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        ImageSource? image = null;
        try
        {
            foreach (var steam in Steam.Directories)
            {
                var dir = Path.Combine(steam, "appcache", "librarycache", appId);
                if (!Directory.Exists(dir)) continue;
                var icon = Directory.EnumerateFiles(dir, "*.jpg", SearchOption.AllDirectories)
                    .FirstOrDefault(f =>
                    {
                        var name = Path.GetFileNameWithoutExtension(f);
                        return name.Length == 40 && name.All(Uri.IsHexDigit);
                    });
                if (icon == null) continue;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(icon);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                image = bmp;
                break;
            }
        }
        catch
        {
            image = null;
        }

        Cache[key] = image;
        return image;
    }

    private static string? Find(string appId, string file)
    {
        foreach (var steam in Steam.Directories)
        {
            var dir = Path.Combine(steam, "appcache", "librarycache", appId);
            if (!Directory.Exists(dir)) continue;

            var direct = Path.Combine(dir, file);
            if (File.Exists(direct)) return direct;

            var nested = Directory.EnumerateFiles(dir, file, SearchOption.AllDirectories).FirstOrDefault();
            if (nested != null) return nested;
        }
        return null;
    }
}
