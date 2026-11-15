using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Layts.Core;
using Layts.Games.Ets2;
using Layts.Games.Swyf;

namespace Layts.Games.Bus;

public sealed class BusProfile
{
    public required string FolderPath { get; init; }
    public string Name => Path.GetFileName(FolderPath);
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string SettingsPath => Path.Combine(FolderPath, "Settings", "Settings.cfg");
    public string AggroPath => Path.Combine(FolderPath, "Settings", "AggroConstants.cfg");
    public override string ToString() => Name;
}

/// <summary>A career (TBE) save file.</summary>
public sealed class BusCareerSlot
{
    public required string FilePath { get; init; }
    public string Map { get; init; } = "";
    public DateTime Modified { get; init; }
    public string Name => $"{Map} · {Path.GetFileNameWithoutExtension(FilePath)}";
    public string DetailText => Modified.ToString("g", Loc.Culture);
    public override string ToString() => Name;
}

public static class BusLocator
{
    public const string SteamAppId = "491540";
    private static readonly string[] ProcessNames = { "TheBus", "TheBus-Win64-Shipping" };

    public static string SavedDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TheBus", "Saved");

    public static string ProfilesDirectory => Path.Combine(SavedDirectory, "Profiles");
    public static string GameUserSettingsPath => Path.Combine(SavedDirectory, "Config", "Windows", "GameUserSettings.ini");

    public static List<BusProfile> FindProfiles()
    {
        if (!Directory.Exists(ProfilesDirectory)) return new List<BusProfile>();
        return Directory.GetDirectories(ProfilesDirectory)
            .Where(d => Directory.Exists(Path.Combine(d, "Settings")) || Directory.Exists(Path.Combine(d, "SaveGames")))
            .Select(d => new BusProfile { FolderPath = d })
            .OrderByDescending(p => Directory.GetLastWriteTime(p.FolderPath))
            .ToList();
    }

    /// <summary>Career saves live in SaveGames\&lt;Map&gt;\TBE.</summary>
    public static List<BusCareerSlot> FindCareerSaves(BusProfile profile)
    {
        var root = Path.Combine(profile.FolderPath, "SaveGames");
        if (!Directory.Exists(root)) return new List<BusCareerSlot>();
        return Directory.GetDirectories(root)
            .Select(map => (map, tbe: Path.Combine(map, "TBE")))
            .Where(x => Directory.Exists(x.tbe))
            .SelectMany(x => Directory.GetFiles(x.tbe, "*.sav").Select(f => new BusCareerSlot
            {
                FilePath = f,
                Map = Path.GetFileName(x.map),
                Modified = File.GetLastWriteTime(f),
            }))
            .OrderByDescending(s => s.Modified)
            .ToList();
    }

    public static bool IsGameRunning()
    {
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (running) return true;
        }
        return false;
    }
}

public sealed class BusEmployee
{
    public required string Name { get; init; }
    public GvasTag? Experience { get; init; }
    public GvasTag? Satisfaction { get; init; }
    public bool IsPlayer { get; init; }
}

/// <summary>The career save: budget, reputation, employees and vehicle condition.</summary>
public sealed class BusCareer
{
    private BusCareer(string path, BusGvas gvas)
    {
        FilePath = path;
        Gvas = gvas;
        Employees = ReadEmployees();
    }

    public string FilePath { get; }
    public BusGvas Gvas { get; }
    public List<BusEmployee> Employees { get; }

    public static BusCareer Load(string path) => new(path, BusGvas.Load(path));

    private List<GvasTag> BudgetTags => Gvas.Find("CurrentBudget", "DoubleProperty").ToList();
    private GvasTag? ReputationTag => Gvas.Find("Reputation", "FloatProperty").FirstOrDefault();
    private List<GvasTag> ConditionTags => Gvas.Find("CurrentState", "FloatProperty").ToList();
    private List<GvasTag> CleanlinessTags => Gvas.Find("CurrentCleanliness", "FloatProperty").ToList();

    public bool HasBudget => BudgetTags.Count > 0;
    public double Budget => BudgetTags.Count > 0 ? Gvas.ReadDouble(BudgetTags[0]) : 0;

    public void SetBudget(double value)
    {
        foreach (var t in BudgetTags) Gvas.WriteDouble(t, value);
    }

    public bool HasReputation => ReputationTag != null;
    public float Reputation => ReputationTag is { } t ? Gvas.ReadFloat(t) : 0;

    public void SetReputation(float value)
    {
        if (ReputationTag is { } t) Gvas.WriteFloat(t, value);
    }

    public int VehicleCount => CleanlinessTags.Count;

    /// <summary>Average technical condition 0–1 over all parts of all buses.</summary>
    public double AverageCondition => ConditionTags.Count == 0 ? 1 : ConditionTags.Average(t => Gvas.ReadFloat(t));
    public double AverageCleanliness => CleanlinessTags.Count == 0 ? 1 : CleanlinessTags.Average(t => Gvas.ReadFloat(t));

    public int RepairAll()
    {
        int n = 0;
        foreach (var t in ConditionTags)
            if (Gvas.ReadFloat(t) < 1f && Gvas.WriteFloat(t, 1f)) n++;
        return n;
    }

    public int CleanAll()
    {
        int n = 0;
        foreach (var t in CleanlinessTags)
            if (Gvas.ReadFloat(t) < 1f && Gvas.WriteFloat(t, 1f)) n++;
        return n;
    }

    /// <summary>
    /// Employees array: each entry has EmployeeID, Satisfaction, …, FirstName, LastName, …, Experience.
    /// </summary>
    private List<BusEmployee> ReadEmployees()
    {
        var region = Gvas.ArrayRegion("Employees");
        var list = new List<BusEmployee>();
        var starts = region.Select((t, i) => (t, i)).Where(x => x.t.Name == "EmployeeID" && x.t.Type == "IntProperty").Select(x => x.i).ToList();
        for (int k = 0; k < starts.Count; k++)
        {
            int from = starts[k], to = k + 1 < starts.Count ? starts[k + 1] : region.Count;
            var part = region.GetRange(from, to - from);
            string first = part.FirstOrDefault(t => t.Name == "FirstName" && t.Type == "NameProperty") is { } f ? Gvas.ReadName(f) : "";
            string last = part.FirstOrDefault(t => t.Name == "LastName" && t.Type == "NameProperty") is { } l ? Gvas.ReadName(l) : "";
            var exp = part.FirstOrDefault(t => t.Name == "Experience" && t.Type == "FloatProperty");
            var sat = part.FirstOrDefault(t => t.Name == "Satisfaction" && t.Type == "FloatProperty");
            if (exp == null && sat == null) continue;
            var name = $"{first} {last}".Trim();
            list.Add(new BusEmployee
            {
                Name = name.Length > 0 ? name : "#" + (list.Count + 1),
                Experience = exp,
                Satisfaction = sat,
                // The player's own entry has no experience/satisfaction values and is skipped above.
                IsPlayer = false,
            });
        }
        return list;
    }

    public void Save() => Gvas.Save(FilePath);
}

/// <summary>An Unreal JSON settings file (tab indented, CRLF) edited without touching unknown keys.</summary>
public sealed class BusJsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        IndentCharacter = '\t',
        IndentSize = 1,
        NewLine = "\r\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private BusJsonFile(string path, JsonObject root)
    {
        FilePath = path;
        Root = root;
    }

    public string FilePath { get; }
    public JsonObject Root { get; }

    public static BusJsonFile? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject root ? new BusJsonFile(path, root) : null;
        }
        catch
        {
            return null;
        }
    }

    public bool Has(string key) => Root.ContainsKey(key);
    public double? GetDouble(string key) => SwyfJson.Double(Root[key]);
    public bool? GetBool(string key) => Root[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    /// <summary>Sets a value only when the key exists and the value really changes.</summary>
    public bool SetDouble(string key, double value)
    {
        if (!Has(key) || GetDouble(key) is { } old && Math.Abs(old - value) < 1e-6) return false;
        Root[key] = value == Math.Floor(value) && Math.Abs(value) < 1e9 ? JsonValue.Create((long)value) : JsonValue.Create(value);
        return true;
    }

    public bool SetBool(string key, bool value)
    {
        if (!Has(key) || GetBool(key) == value) return false;
        Root[key] = JsonValue.Create(value);
        return true;
    }

    public void Save()
    {
        var sb = new System.Text.StringBuilder();
        WriteObject(sb, Root, 0);
        var temp = FilePath + ".layts-tmp";
        File.WriteAllText(temp, sb.ToString());
        File.Move(temp, FilePath, overwrite: true);
    }

    // Unreal's pretty JSON: tabs, CRLF, an object value starts on its own line ("key":⏎{),
    // arrays stay on the key's line ("key": [), empty arrays are "[]", no newline at the end.
    private const string NL = "\r\n";

    private static void WriteObject(System.Text.StringBuilder sb, JsonObject obj, int depth)
    {
        sb.Append('{');
        int i = 0;
        foreach (var (key, value) in obj)
        {
            sb.Append(i++ == 0 ? NL : "," + NL);
            sb.Append('\t', depth + 1).Append(JsonValue.Create(key)!.ToJsonString(Options)).Append(':');
            if (value is JsonObject child)
            {
                sb.Append(NL).Append('\t', depth + 1);
                WriteObject(sb, child, depth + 1);
            }
            else
            {
                sb.Append(' ');
                WriteValue(sb, value, depth + 1);
            }
        }
        sb.Append(NL).Append('\t', depth).Append('}');
    }

    private static void WriteValue(System.Text.StringBuilder sb, JsonNode? value, int depth)
    {
        switch (value)
        {
            case JsonObject o:
                WriteObject(sb, o, depth);
                break;
            case JsonArray a when a.Count == 0:
                sb.Append("[]");
                break;
            case JsonArray a:
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    sb.Append(i == 0 ? NL : "," + NL).Append('\t', depth + 1);
                    WriteValue(sb, a[i], depth + 1);
                }
                sb.Append(NL).Append('\t', depth).Append(']');
                break;
            case null:
                sb.Append("null");
                break;
            default:
                sb.Append(value.ToJsonString(Options));
                break;
        }
    }
}

public static class BusIni
{
    /// <summary>Updates FieldOfView in GameUserSettings.ini (the game keeps it there as well).</summary>
    public static void SetFieldOfView(double fov)
    {
        var path = BusLocator.GameUserSettingsPath;
        if (!File.Exists(path)) return;
        var text = File.ReadAllText(path);
        var updated = Regex.Replace(text, @"(?m)^FieldOfView=.*$",
            "FieldOfView=" + fov.ToString("0.000000", CultureInfo.InvariantCulture));
        if (updated != text) File.WriteAllText(path, updated);
    }
}

/// <summary>Backups of a profile's settings and career saves plus GameUserSettings.ini.</summary>
public static class BusBackups
{
    public static string Root => Path.Combine(AppSettings.DataDirectory, "Backups", "TheBus");

    public static string DirectoryFor(BusProfile profile) => Path.Combine(Root, profile.Name);

    public static void Create(BusProfile profile)
    {
        var dir = Path.Combine(DirectoryFor(profile), DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
        Directory.CreateDirectory(dir);

        void Copy(string source, string relative)
        {
            if (!File.Exists(source)) return;
            var target = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        foreach (var f in Directory.GetFiles(Path.Combine(profile.FolderPath, "Settings"), "*.cfg"))
            Copy(f, Path.Combine("Settings", Path.GetFileName(f)));
        foreach (var slot in BusLocator.FindCareerSaves(profile))
            Copy(slot.FilePath, Path.Combine("SaveGames", slot.Map, "TBE", Path.GetFileName(slot.FilePath)));
        Copy(BusLocator.GameUserSettingsPath, "GameUserSettings.ini");

        BackupPolicy.Prune(DirectoryFor(profile));
    }

    public static List<BackupEntry> List(BusProfile profile)
    {
        var root = DirectoryFor(profile);
        if (!Directory.Exists(root)) return new List<BackupEntry>();
        return Directory.GetDirectories(root)
            .Select(d => new BackupEntry
            {
                FolderPath = d,
                Created = Directory.GetCreationTime(d),
                Size = Directory.GetFiles(d, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length),
            })
            .Where(b => b.Size > 0)
            .OrderByDescending(b => Path.GetFileName(b.FolderPath), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Restores a backup. The current state is backed up first.</summary>
    public static void Restore(BackupEntry backup, BusProfile profile)
    {
        Create(profile);
        foreach (var file in Directory.GetFiles(backup.FolderPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(backup.FolderPath, file);
            var target = relative == "GameUserSettings.ini"
                ? BusLocator.GameUserSettingsPath
                : Path.Combine(profile.FolderPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
