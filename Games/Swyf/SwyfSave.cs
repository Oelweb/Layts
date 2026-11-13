using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Layts.Core;
using Layts.Games.Ets2;

namespace Layts.Games.Swyf;

internal static class SwyfJson
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static long? Long(JsonNode? node)
    {
        var d = Double(node);
        if (d == null) return null;
        // Big ids (SteamID64) must not go through double – parse the raw text exactly.
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var exact))
            return exact;
        return (long)Math.Round(d.Value);
    }

    /// <summary>Reads any number (int, long, double – parsed or created in code) or numeric string.</summary>
    public static double? Double(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            case JsonValueKind.String:
                return double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
            case JsonValueKind.True:
                return 1;
            case JsonValueKind.False:
                return 0;
            default:
                return null;
        }
    }

    public static string? String(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// One MetaSave file ({ version, updatedUtc, importLegacy, values }).
/// Only touched keys change – everything else is written back as it was.
/// </summary>
public sealed class SwyfMetaFile
{
    private SwyfMetaFile(string path, JsonObject root, JsonObject values)
    {
        FilePath = path;
        Root = root;
        Values = values;
    }

    public string FilePath { get; }
    public JsonObject Root { get; }
    public JsonObject Values { get; }

    public static SwyfMetaFile Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException("Not a MetaSave file.");
        if (root["values"] is not JsonObject values)
        {
            values = new JsonObject();
            root["values"] = values;
        }
        return new SwyfMetaFile(path, root, values);
    }

    public bool Has(string key) => Values.ContainsKey(key);
    public long? GetLong(string key) => SwyfJson.Long(Values[key]);
    public double? GetDouble(string key) => SwyfJson.Double(Values[key]);
    public string? GetString(string key) => SwyfJson.String(Values[key]);

    public void Set(string key, long value) => Values[key] = JsonValue.Create(value);
    public void Set(string key, double value) => Values[key] = JsonValue.Create(value);
    public void Set(string key, string value) => Values[key] = JsonValue.Create(value);

    /// <summary>Writes atomically. The previous file becomes .bak (like the game does it).</summary>
    public void Write()
    {
        Root["updatedUtc"] = JsonValue.Create(DateTime.UtcNow.Ticks);
        var json = Root.ToJsonString(SwyfJson.Indented);

        var temp = FilePath + ".layts-tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
        File.Move(temp, FilePath, overwrite: true);
    }
}

public sealed class SwyfPlayer
{
    private readonly JsonObject _node;

    public SwyfPlayer(JsonObject node) => _node = node;

    public long SteamId => SwyfJson.Long(_node["SteamId"]) ?? 0;

    public long Money
    {
        get => SwyfJson.Long(_node["Money"]) ?? 0;
        set => _node["Money"] = JsonValue.Create(value);
    }

    public HashSet<string> Entitlements =>
        (_node["Entitlements"] as JsonArray)?.Select(SwyfJson.String).OfType<string>().ToHashSet() ?? new();

    /// <summary>Adds/removes known ids; ids the editor doesn't know stay untouched.</summary>
    public void SetEntitlements(IEnumerable<string> add, IEnumerable<string> remove)
    {
        if (_node["Entitlements"] is not JsonArray array)
        {
            array = new JsonArray();
            _node["Entitlements"] = array;
        }
        var removeSet = remove.ToHashSet();
        for (int i = array.Count - 1; i >= 0; i--)
            if (SwyfJson.String(array[i]) is { } id && removeSet.Contains(id)) array.RemoveAt(i);

        var have = array.Select(SwyfJson.String).OfType<string>().ToHashSet();
        foreach (var id in add)
            if (have.Add(id)) array.Add(JsonValue.Create(id));
    }
}

/// <summary>The current run ("Scammer.Run" – stored as a JSON string inside the slot file).</summary>
public sealed class SwyfRun
{
    private readonly JsonObject _run;

    public SwyfRun(JsonObject run) => _run = run;

    private JsonArray Days => _run["Days"] as JsonArray ?? new JsonArray();

    public int ResumeDay => (int)(SwyfJson.Long(_run["ResumeDay"]) ?? 1);
    public int CompletedDay => (int)(SwyfJson.Long(_run["CompletedDay"]) ?? 0);

    /// <summary>The day snapshot the game continues from.</summary>
    public JsonObject? CurrentDay
    {
        get
        {
            var days = Days.OfType<JsonObject>().ToList();
            return days.FirstOrDefault(d => SwyfJson.Long(d["Day"]) == ResumeDay)
                   ?? days.OrderByDescending(d => SwyfJson.Long(d["Day"]) ?? 0).FirstOrDefault();
        }
    }

    public List<SwyfPlayer> Players =>
        (CurrentDay?["Players"] as JsonArray)?.OfType<JsonObject>().Select(p => new SwyfPlayer(p)).ToList() ?? new();

    public double? GetDayValue(string key) => SwyfJson.Double(CurrentDay?[key]);

    /// <summary>Sets a rule value (quota, day length …) on every stored day so it sticks.</summary>
    public void SetDayValue(string key, JsonNode value)
    {
        foreach (var day in Days.OfType<JsonObject>())
            day[key] = value.DeepClone();
    }

    /// <summary>Continue the run from <paramref name="day"/>.</summary>
    public void SetDay(int day)
    {
        var current = CurrentDay;
        if (current == null || day == ResumeDay) return;

        var days = Days;
        for (int i = days.Count - 1; i >= 0; i--)
            if (days[i] is JsonObject d && !ReferenceEquals(d, current) && SwyfJson.Long(d["Day"]) >= day)
                days.RemoveAt(i);

        current["Day"] = JsonValue.Create(day);
        _run["ResumeDay"] = JsonValue.Create(day);
        _run["CompletedDay"] = JsonValue.Create(Math.Max(0, day - 1));
    }

    public string ToJson()
    {
        _run["UpdatedUtc"] = JsonValue.Create(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return _run.ToJsonString(SwyfJson.Compact);
    }
}

public sealed record SwyfEmote(string Key, string Name);

/// <summary>A save slot with its run, owned apps and cosmetics.</summary>
public sealed class SwyfSave
{
    private const string RunKey = "Scammer.Run";
    private const string OwnedPrefix = "KolkataOS.ApplicationOwned.";
    private const string NotifiedPrefix = "KolkataOS.ApplicationAffordabilityNotified.";
    public const string CurryKey = "Kolkata.MrCurrySuit.Selected";

    private SwyfSave(SwyfMetaFile file) => File = file;

    public SwyfMetaFile File { get; }
    public SwyfRun? Run { get; private set; }

    public static SwyfSave Load(string path)
    {
        var save = new SwyfSave(SwyfMetaFile.Load(path));
        var text = save.File.GetString(RunKey);
        if (!string.IsNullOrWhiteSpace(text) && JsonNode.Parse(text) is JsonObject run)
            save.Run = new SwyfRun(run);
        return save;
    }

    public long PlaytimeSeconds => File.GetLong("TotalPlaytimeInSeconds") ?? 0;

    public bool OwnsPermanent(string id) => File.GetLong(OwnedPrefix + id) == 1;

    public void SetPermanent(string id, bool owned)
    {
        if (owned)
        {
            File.Set(OwnedPrefix + id, 1);
            File.Set(NotifiedPrefix + id, 1);
        }
        else if (File.Has(OwnedPrefix + id))
        {
            File.Set(OwnedPrefix + id, 0);
        }
    }

    public List<SwyfEmote> Emotes =>
        File.Values
            .Select(kv => (kv.Key, Match: Regex.Match(kv.Key, @"^EmoteUnlocked_(\d+)_(.+)$")))
            .Where(x => x.Match.Success)
            .OrderBy(x => int.Parse(x.Match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(x => new SwyfEmote(x.Key, x.Match.Groups[2].Value))
            .ToList();

    public bool IsOn(string key) => (File.GetLong(key) ?? 0) != 0;
    public void SetOn(string key, bool on) => File.Set(key, on ? 1 : 0);

    public void Write()
    {
        if (Run != null)
        {
            File.Set(RunKey, Run.ToJson());
            File.Set("Scammer.ResumeDay", Run.ResumeDay);
        }
        File.Write();
    }
}

/// <summary>Backups of the whole MetaSave folder under %LOCALAPPDATA%\Layts\Backups\SWYF.</summary>
public static class SwyfBackups
{
    private const int MaxBackups = 25;

    public static string Root => Path.Combine(AppSettings.DataDirectory, "Backups", "SWYF");

    public static BackupEntry Create()
    {
        var dir = Path.Combine(Root, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
        Directory.CreateDirectory(dir);
        long size = 0;
        foreach (var file in Directory.GetFiles(SwyfLocator.MetaSaveDirectory, "*.json"))
        {
            System.IO.File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            size += new FileInfo(file).Length;
        }
        Prune();
        return new BackupEntry { FolderPath = dir, Created = DateTime.Now, Size = size };
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
            System.IO.File.Copy(file, Path.Combine(SwyfLocator.MetaSaveDirectory, Path.GetFileName(file)), overwrite: true);
    }

    private static void Prune()
    {
        foreach (var old in List().Skip(MaxBackups))
        {
            try { Directory.Delete(old.FolderPath, recursive: true); }
            catch { /* not critical */ }
        }
    }
}
