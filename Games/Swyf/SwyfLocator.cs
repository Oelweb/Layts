using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Layts.Core;

namespace Layts.Games.Swyf;

/// <summary>One MetaSave slot (save-1.json, save-2.json, …).</summary>
public sealed class SwyfSlot
{
    public required string FilePath { get; init; }
    public int Number { get; init; }
    public bool IsEmpty { get; init; }
    public int Day { get; init; }
    public DateTime? Updated { get; init; }

    public string Name => Loc.F("Sw_SlotX", Number);
    public string Glyph => IsEmpty ? "\uE8B7" : "\uE7FC";

    public string DetailText
    {
        get
        {
            if (IsEmpty) return Loc.Get("Sw_SlotEmpty");
            var day = Day > 0 ? Loc.F("Sw_DayX", Day) : Loc.Get("Sw_NoRun");
            return Updated is { } u ? $"{day} · {u.ToLocalTime().ToString("g", Loc.Culture)}" : day;
        }
    }

    public override string ToString() => Name;
}

/// <summary>Finds the save folder, the game install and tells whether the game is running.</summary>
public static class SwyfLocator
{
    public const string SteamAppId = "4954910";
    public const string ProcessName = "Scam With Your Friends";
    private const string InstallFolder = "Scam With Your Friends";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Jatater Worldwide", "Scam With Your Friends");

    public static string MetaSaveDirectory => Path.Combine(DataDirectory, "MetaSave");
    public static string LocalSettingsPath => Path.Combine(MetaSaveDirectory, "local.json");
    public static string PersistentPath => Path.Combine(MetaSaveDirectory, "persistent.json");

    public static bool IsInstalledOrPlayed => Directory.Exists(MetaSaveDirectory);

    public static List<SwyfSlot> FindSlots()
    {
        var list = new List<SwyfSlot>();
        if (!Directory.Exists(MetaSaveDirectory)) return list;

        foreach (var file in Directory.GetFiles(MetaSaveDirectory, "save-*.json"))
        {
            var m = Regex.Match(Path.GetFileName(file), @"^save-(\d+)\.json$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;

            int number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            bool empty = true;
            int day = 0;
            DateTime? updated = null;
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                var values = root?["values"] as JsonObject;
                empty = values == null || values.Count == 0;
                if (root?["updatedUtc"] is JsonValue t && t.TryGetValue<long>(out var ticks) && ticks > 0)
                    updated = new DateTime(ticks, DateTimeKind.Utc);
                if (values?["Scammer.Run"] is JsonValue rv && rv.TryGetValue<string>(out var runText)
                    && JsonNode.Parse(runText) is JsonObject run)
                    day = (int)(SwyfJson.Long(run["ResumeDay"]) ?? 0);
                else if (values != null)
                    day = (int)(SwyfJson.Long(values["Scammer.ResumeDay"]) ?? 0);
            }
            catch
            {
                // Unreadable – still list it so the user sees it.
            }

            list.Add(new SwyfSlot { FilePath = file, Number = number, IsEmpty = empty, Day = day, Updated = updated });
        }
        return list.OrderBy(s => s.Number).ToList();
    }

    /// <summary>Your own SteamID64, taken from the achievement keys the game keeps in persistent.json.</summary>
    public static long? FindOwnSteamId()
    {
        try
        {
            if (!File.Exists(PersistentPath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(PersistentPath)) as JsonObject;
            if (root?["values"] is not JsonObject values) return null;
            foreach (var (key, _) in values)
            {
                var m = Regex.Match(key, @"^SteamAchievements\.(\d{17})\.");
                if (m.Success) return long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    /// <summary>The game's install folder in any Steam library, or null.</summary>
    public static string? FindInstallDirectory()
    {
        foreach (var library in SteamLibraries())
        {
            var dir = Path.Combine(library, "steamapps", "common", InstallFolder);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var steam in Steam.Directories)
        {
            if (seen.Add(steam)) yield return steam;

            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            string text;
            try { text = File.ReadAllText(vdf); }
            catch { continue; }

            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path) && seen.Add(path)) yield return path;
            }
        }
    }

    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        bool running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        return running;
    }
}
