using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Layts.Core;
using Layts.Games.Ets2;
using Layts.Games.Swyf;

namespace Layts.Games.Thy;

/// <summary>One save file (save_0.json, save_1.json, …).</summary>
public sealed class ThySlot
{
    public required string FilePath { get; init; }
    public int Number { get; init; }
    public int Level { get; init; }
    public bool Kaizo { get; init; }
    public DateTime Modified { get; init; }

    public string Name => Loc.F("Thy_SlotX", Number + 1);
    public string Glyph => Kaizo ? "" : "";

    public string DetailText =>
        $"{ThyLevels.Name(Level)}{(Kaizo ? " · " + Loc.Get("Thy_Kaizo") : "")} · {Modified.ToString("g", Loc.Culture)}";

    public override string ToString() => Name;
}

public static class ThyLevels
{
    /// <summary>Levels of the demo (Assets/Scenes in build order).</summary>
    public static readonly string[] Names = { "1-1 Picnic", "1-2 Road", "1-3 Afterlife" };

    public static string Name(int index) =>
        index >= 0 && index < Names.Length ? Names[index] : Loc.F("Thy_LevelX", index + 1);
}

public static class ThyLocator
{
    public const string SteamAppId = "4307190";
    public const string ProcessName = "Trees Hate You";

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "tykenn games", "Trees Hate You");

    public static string SharedDataPath => Path.Combine(DataDirectory, "sharedData.json");

    public static List<ThySlot> FindSlots()
    {
        var list = new List<ThySlot>();
        if (!Directory.Exists(DataDirectory)) return list;

        foreach (var file in Directory.GetFiles(DataDirectory, "save_*.json"))
        {
            var m = Regex.Match(Path.GetFileName(file), @"^save_(\d+)\.json$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            int level = 0;
            bool kaizo = false;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is JsonObject root)
                {
                    level = (int)(SwyfJson.Long(root["levelReached"]) ?? 0);
                    kaizo = root["kaizo"] is JsonValue k && k.TryGetValue<bool>(out var b) && b;
                }
            }
            catch
            {
                // still list it
            }
            list.Add(new ThySlot
            {
                FilePath = file,
                Number = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Level = level,
                Kaizo = kaizo,
                Modified = File.GetLastWriteTime(file),
            });
        }
        return list.OrderBy(s => s.Number).ToList();
    }

    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        bool running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        return running;
    }
}

/// <summary>
/// A Unity JsonUtility save. Only touched keys change; everything else (times, appearance meshes …)
/// is written back unchanged.
/// </summary>
public sealed class ThySave
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private ThySave(string path, JsonObject root)
    {
        FilePath = path;
        Root = root;
    }

    public string FilePath { get; }
    public JsonObject Root { get; }

    public static ThySave Load(string path) =>
        new(path, JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("Not a save file."));

    public int GetInt(string key, int fallback = 0) => (int)(SwyfJson.Long(Root[key]) ?? fallback);
    public void SetInt(string key, int value) => Root[key] = JsonValue.Create(value);

    public bool GetBool(string key) => Root[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public void SetBool(string key, bool value) => Root[key] = JsonValue.Create(value);

    public int Count(string key) => (Root[key] as JsonArray)?.Count ?? 0;

    public List<int> GetIntList(string key) =>
        (Root[key] as JsonArray)?.Select(n => (int)(SwyfJson.Long(n) ?? 0)).ToList() ?? new List<int>();

    public void SetIntList(string key, IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (var v in values) array.Add(JsonValue.Create(v));
        Root[key] = array;
    }

    /// <summary>Eye colour as #RRGGBB (alpha is kept).</summary>
    public string? EyeColor
    {
        get
        {
            if (Root["appearance"]?["eyeColor"] is not JsonObject c) return null;
            int R(string k) => (int)Math.Round(Math.Clamp(SwyfJson.Double(c[k]) ?? 0, 0, 1) * 255);
            return $"#{R("r"):X2}{R("g"):X2}{R("b"):X2}";
        }
        set
        {
            if (value == null || Root["appearance"]?["eyeColor"] is not JsonObject c) return;
            if (!TryParseHex(value, out var r, out var g, out var b)) return;
            c["r"] = JsonValue.Create(r / 255.0);
            c["g"] = JsonValue.Create(g / 255.0);
            c["b"] = JsonValue.Create(b / 255.0);
        }
    }

    public static bool TryParseHex(string text, out int r, out int g, out int b)
    {
        r = g = b = 0;
        var t = text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        r = (rgb >> 16) & 0xFF;
        g = (rgb >> 8) & 0xFF;
        b = rgb & 0xFF;
        return true;
    }

    public void Write() => WriteJson(FilePath, Root);

    public static void WriteJson(string path, JsonObject root)
    {
        var temp = path + ".layts-tmp";
        File.WriteAllText(temp, root.ToJsonString(Options));
        File.Move(temp, path, overwrite: true);
    }

    // ----- sharedData.json (applies to all slots) -----

    public static bool? ReadChallengeUnlocked()
    {
        try
        {
            if (!File.Exists(ThyLocator.SharedDataPath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(ThyLocator.SharedDataPath)) as JsonObject;
            return root?["kaizoModeUnlocked"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        }
        catch
        {
            return null;
        }
    }

    public static void WriteChallengeUnlocked(bool unlocked)
    {
        JsonObject root;
        try
        {
            root = File.Exists(ThyLocator.SharedDataPath)
                ? JsonNode.Parse(File.ReadAllText(ThyLocator.SharedDataPath)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }
        root["kaizoModeUnlocked"] = JsonValue.Create(unlocked);
        WriteJson(ThyLocator.SharedDataPath, root);
    }
}

/// <summary>Backups of all save files under %LOCALAPPDATA%\Layts\Backups\TreesHateYou.</summary>
public static class ThyBackups
{
    private const int MaxBackups = 25;

    public static string Root => Path.Combine(AppSettings.DataDirectory, "Backups", "TreesHateYou");

    public static void Create()
    {
        var dir = Path.Combine(Root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(ThyLocator.DataDirectory, "*.json"))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        foreach (var old in List().Skip(MaxBackups))
        {
            try { Directory.Delete(old.FolderPath, recursive: true); }
            catch { /* not critical */ }
        }
    }

    public static List<BackupEntry> List()
    {
        if (!Directory.Exists(Root)) return new List<BackupEntry>();
        return Directory.GetDirectories(Root)
            .Select(d => new BackupEntry
            {
                FolderPath = d,
                Created = Directory.GetCreationTime(d),
                Size = Directory.GetFiles(d, "*.json").Sum(f => new FileInfo(f).Length),
            })
            .Where(b => b.Size > 0)
            .OrderByDescending(b => Path.GetFileName(b.FolderPath), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Restores a backup. The current state is backed up first.</summary>
    public static void Restore(BackupEntry backup)
    {
        Create();
        foreach (var file in Directory.GetFiles(backup.FolderPath, "*.json"))
            File.Copy(file, Path.Combine(ThyLocator.DataDirectory, Path.GetFileName(file)), overwrite: true);
    }
}
