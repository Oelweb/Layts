using System.Diagnostics;
using System.Text;
using Layts.Core;
using Layts.Core.Sii;

namespace Layts.Games.Ets2;

public sealed class Ets2Profile
{
    public required string Name { get; init; }
    public required string FolderPath { get; init; }
    public required string Source { get; init; }
    public DateTime LastPlayed { get; init; }

    /// <summary>Herkunft: „local“, „steam“ oder „cloud“.</summary>
    public bool IsSteamCloud => Source == "cloud";

    public string SourceLabel => Source switch
    {
        "cloud" => Loc.Get("Src_SteamCloud"),
        "steam" => Loc.Get("Src_Steam"),
        _ => Loc.Get("Src_Local"),
    };
    public string SaveRoot => Path.Combine(FolderPath, "save");

    /// <summary>Anfangsbuchstabe für den Avatar im Auswahlmenü.</summary>
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();

    /// <summary>Wolke für Steam Cloud, sonst PC-Symbol.</summary>
    public string SourceGlyph => IsSteamCloud ? "" : "";

    public string SourceText => Loc.F("LastPlayed", SourceLabel, LastPlayed);
    public string Display => $"{Name}   ·   {SourceLabel}";
    public override string ToString() => Display;
}

public sealed class Ets2SaveSlot
{
    public required string Name { get; init; }
    public required string FolderPath { get; init; }
    public DateTime LastWrite { get; init; }

    public string GamePath => Path.Combine(FolderPath, "game.sii");

    public bool IsAutosave => Path.GetFileName(FolderPath).StartsWith("autosave", StringComparison.OrdinalIgnoreCase);

    /// <summary>Uhr für automatische Speicherstände, Diskette für eigene.</summary>
    public string Glyph => IsAutosave ? "" : "";

    public string DateText => LastWrite.Date == DateTime.Today
        ? Loc.F("Date_Today", LastWrite)
        : LastWrite.Date == DateTime.Today.AddDays(-1)
            ? Loc.F("Date_Yesterday", LastWrite)
            : Loc.F("Date_Other", LastWrite);
    public string Display => $"{Name}   ·   {DateText}";
    public override string ToString() => Display;
}

public static class Ets2Locator
{
    public const string ProcessName = "eurotrucks2";
    public const string SteamAppId = "227300";

    public static string DocumentsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Euro Truck Simulator 2");

    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    public static List<Ets2Profile> FindProfiles()
    {
        var roots = new List<(string Path, string Source)>
        {
            (Path.Combine(DocumentsDirectory, "profiles"), "local"),
            (Path.Combine(DocumentsDirectory, "steam_profiles"), "steam"),
        };
        foreach (var userDir in SteamUserDirectories())
            roots.Add((Path.Combine(userDir, "227300", "remote", "profiles"), "cloud"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Ets2Profile>();

        foreach (var (root, source) in roots)
        {
            foreach (var dir in SafeDirectories(root))
            {
                if (!seen.Add(Path.GetFullPath(dir))) continue;

                var games = SafeDirectories(Path.Combine(dir, "save"))
                    .Select(d => Path.Combine(d, "game.sii"))
                    .Where(File.Exists)
                    .ToList();
                if (games.Count == 0) continue;

                result.Add(new Ets2Profile
                {
                    Name = DecodeProfileName(Path.GetFileName(dir)),
                    FolderPath = dir,
                    Source = source,
                    LastPlayed = games.Max(File.GetLastWriteTime),
                });
            }
        }

        return result.OrderByDescending(p => p.LastPlayed).ToList();
    }

    public static List<Ets2SaveSlot> FindSaves(Ets2Profile profile)
    {
        var list = new List<Ets2SaveSlot>();
        foreach (var dir in SafeDirectories(profile.SaveRoot))
        {
            var game = Path.Combine(dir, "game.sii");
            if (!File.Exists(game)) continue;

            var folder = Path.GetFileName(dir);
            list.Add(new Ets2SaveSlot
            {
                Name = ReadSaveName(dir) ?? FriendlySlotName(folder),
                FolderPath = dir,
                LastWrite = File.GetLastWriteTime(game),
            });
        }
        return list.OrderByDescending(s => s.LastWrite).ToList();
    }

    private static string FriendlySlotName(string folder)
    {
        if (folder == "autosave") return Loc.Get("Slot_Autosave");
        if (folder == "quicksave") return Loc.Get("Slot_Quicksave");
        if (folder == "multiplayer_backup") return Loc.Get("Slot_MpBackup");
        if (folder.StartsWith("autosave_drive", StringComparison.Ordinal))
            return Loc.F("Slot_Drive", folder["autosave_drive".Length..].Replace('_', ' '));
        if (folder.StartsWith("autosave_job", StringComparison.Ordinal)) return Loc.Get("Slot_Job");
        if (int.TryParse(folder, out var n)) return Loc.F("Slot_N", n);
        return folder;
    }

    private static string? ReadSaveName(string dir)
    {
        try
        {
            var info = Path.Combine(dir, "info.sii");
            if (!File.Exists(info)) return null;
            var doc = SiiLoader.Load(File.ReadAllBytes(info), out _);
            var unit = doc.First("save_container");
            if (unit == null || !unit.HasField("name")) return null;
            var name = unit.GetString("name");
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Profilordner heißen wie der Profilname in Hex-Schreibweise (z. B. 4D6178 = „Max“).</summary>
    private static string DecodeProfileName(string folder)
    {
        try
        {
            if (folder.Length % 2 == 0 && folder.All(Uri.IsHexDigit))
                return Encoding.UTF8.GetString(Convert.FromHexString(folder));
        }
        catch
        {
            // Fallback unten.
        }
        return folder;
    }

    private static IEnumerable<string> SteamUserDirectories()
    {
        foreach (var steamDir in Layts.Core.Steam.Directories)
            foreach (var user in SafeDirectories(Path.Combine(steamDir, "userdata")))
                yield return user;
    }

    private static string[] SafeDirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
