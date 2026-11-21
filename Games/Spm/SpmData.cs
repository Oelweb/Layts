using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Layts.Core;
using Layts.Games.Ets2;
using Microsoft.Win32;

namespace Layts.Games.Spm;

/// <summary>One Steam account folder under Documents\Marvel's Spider-Man Remastered.</summary>
public sealed class SpmProfile
{
    public required string FolderPath { get; init; }
    public string Name => Path.GetFileName(FolderPath);
    public string Initial => "S";
    public string UserPrefsPath => Path.Combine(FolderPath, "-userprefs.save");
    public override string ToString() => Name;
}

/// <summary>A save file: slot0-s.save (autosave of slot 1), slot0-s-manual-0.save, …</summary>
public sealed class SpmSlot
{
    public required string FilePath { get; init; }
    public int Slot { get; init; }
    public int Manual { get; init; } = -1;
    public double Percent { get; init; } = -1;
    public DateTime Modified { get; init; }

    public string Name => Manual < 0 ? Loc.F("Sm_SlotX", Slot + 1) : Loc.F("Sm_SlotManual", Slot + 1, Manual + 1);

    public string DetailText => (Percent >= 0 ? Percent.ToString("0.#", Loc.Culture) + " % · " : "") + Modified.ToString("g", Loc.Culture);

    public override string ToString() => Name;
}

public static class SpmLocator
{
    public const string SteamAppId = "1817070";
    public const string ProcessName = "Spider-Man";

    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Marvel's Spider-Man Remastered");

    public static List<SpmProfile> FindProfiles()
    {
        if (!Directory.Exists(RootDirectory)) return new List<SpmProfile>();
        return Directory.GetDirectories(RootDirectory)
            .Where(d => Directory.GetFiles(d, "*.save").Length > 0)
            .Select(d => new SpmProfile { FolderPath = d })
            .OrderByDescending(p => Directory.GetLastWriteTime(p.FolderPath))
            .ToList();
    }

    public static List<SpmSlot> FindSlots(SpmProfile profile)
    {
        var list = new List<SpmSlot>();
        if (!Directory.Exists(profile.FolderPath)) return list;
        foreach (var file in Directory.GetFiles(profile.FolderPath, "slot*.save"))
        {
            var m = Regex.Match(Path.GetFileName(file), @"^slot(\d+)-s(?:-manual-(\d+))?\.save$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            double percent = -1;
            try
            {
                var dat = SpmDat1.Load(file);
                if (dat.FindObjectWith("GamePercent")?["GamePercent"] is { } gp) percent = dat.GetFloat(gp) * 100;
            }
            catch
            {
                // still list it
            }
            list.Add(new SpmSlot
            {
                FilePath = file,
                Slot = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Manual = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : -1,
                Percent = percent,
                Modified = File.GetLastWriteTime(file),
            });
        }
        return list.OrderBy(s => s.Slot).ThenBy(s => s.Manual).ToList();
    }

    public static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        bool running = processes.Length > 0;
        foreach (var p in processes) p.Dispose();
        return running;
    }
}

/// <summary>A slot save: progress, map and per-save options. Values are changed in place only.</summary>
public sealed class SpmSave
{
    /// <summary>Per-save options that are plain switches (field name → string key).</summary>
    public static readonly (string Field, string Key)[] Options =
    {
        ("SkipQTEs", "Sm_O_SkipQte"),
        ("ButtonMashUseHold", "Sm_O_MashHold"),
        ("SkipTowerPuzzle", "Sm_O_SkipTower"),
        ("CombatCameraRotationAssistEnabled", "Sm_O_CombatCam"),
        ("CameraFollowingEnable", "Sm_O_CamFollow"),
        ("ShowMinimap", "Sm_O_Minimap"),
        ("ShowControlHints", "Sm_O_Hints"),
        ("ShowHudBG", "Sm_O_HudBg"),
        ("HudParallaxOn", "Sm_O_Parallax"),
        ("SubtitleBig", "Sm_O_SubBig"),
        ("SubtitleBg", "Sm_O_SubBg"),
        ("JJJDialogEnabled", "Sm_O_Jjj"),
    };

    private SpmSave(SpmDat1 dat)
    {
        Dat = dat;
        Game = dat.FindObjectWith("SlotDifficulty");
        Towers = dat.FindObjectWith("TowerData")?["TowerData"]?.Objects ?? new List<SpmObject>();
        Landmarks = dat.FindObjectWith("LandmarkData")?["LandmarkData"]?.Objects ?? new List<SpmObject>();
        Challenges = dat.FindObjectWith("Challenges")?["Challenges"]?.Objects ?? new List<SpmObject>();
    }

    public SpmDat1 Dat { get; }
    public SpmObject? Game { get; }
    public List<SpmObject> Towers { get; }
    public List<SpmObject> Landmarks { get; }
    public List<SpmObject> Challenges { get; }

    public static SpmSave Load(string path) => new(SpmDat1.Load(path));

    public bool HasGame => Game != null;

    public double Percent => Game?["GamePercent"] is { } f ? Dat.GetFloat(f) * 100 : 0;

    /// <summary>Play time in seconds from the "Playthrough" segment.</summary>
    public double PlaySeconds
    {
        get
        {
            var segments = Dat.FindObjectWith("Segments")?["Segments"]?.Object;
            if (segments == null) return 0;
            foreach (var s in segments.Fields.Values)
            {
                var o = s.Object;
                if (o?["SegmentName"] is { } n && Dat.GetString(n) == "Playthrough" && o["Duration"] is { } d)
                    return Dat.GetFloat(d);
            }
            return 0;
        }
    }

    public bool? GetOption(string field) => Game?[field] is { Type: SpmDat1.TBool } f ? Dat.GetBool(f) : null;

    public void SetOption(string field, bool value)
    {
        if (Game?[field] is { Type: SpmDat1.TBool } f) Dat.SetBool(f, value);
    }

    // ----- Map -----

    public int TowersActive => Towers.Count(t => TowerState(t) == "kActivated");

    private string TowerState(SpmObject t) => t["State"] is { } s ? Dat.GetString(s) : "";

    /// <summary>Activates every tower that is available (same as repairing it in the game).</summary>
    public int ActivateTowers()
    {
        int n = 0;
        foreach (var t in Towers)
            if (t["State"] is { } s && Dat.GetString(s) == "kAvailable" && Dat.SetStringSameLength(s, "kActivated")) n++;
        return n;
    }

    public int LandmarksOnMap => Landmarks.Count(l => l["OnMap"] is { } f && Dat.GetBool(f));
    public int LandmarksDiscovered => Landmarks.Count(l => l["Discovered"] is { } f && Dat.GetBool(f));

    public void RevealLandmarks(bool discover)
    {
        foreach (var l in Landmarks)
        {
            if (l["OnMap"] is { Type: SpmDat1.TBool } m) Dat.SetBool(m, true);
            if (discover && l["Discovered"] is { Type: SpmDat1.TBool } d) Dat.SetBool(d, true);
        }
    }

    public int ChallengesDone => Challenges.Count(c => c["CompletedOnce"] is { } f && Dat.GetBool(f));

    /// <summary>Ticks every Taskmaster challenge off (no medals or times are changed).</summary>
    public void CompleteChallenges()
    {
        foreach (var c in Challenges)
            if (c["CompletedOnce"] is { Type: SpmDat1.TBool } f) Dat.SetBool(f, true);
    }

    // ----- Activities (XFSM state machines: collectibles, side activities …) -----

    private const uint ActivitySection = 0xEB364589;
    private const uint XfsmTag = 0x4D534658; // "XFSM"

    /// <summary>(count, table 1 start, table 2 start) or null if the layout is unknown.</summary>
    private (int Count, int T1, int T2)? Activities
    {
        get
        {
            if (Dat.FindSection(ActivitySection) is not { } s || s.Size < 32 || Dat.ReadU32(s.Offset) != XfsmTag) return null;
            int n = (int)Dat.ReadU32(s.Offset + 9);
            int t1 = s.Offset + 13;
            int t2 = t1 + n * 14 + 16;
            if (n <= 0 || n > 4000 || t2 + 4 + n * 32 > s.Offset + s.Size || Dat.ReadU32(t2) != (uint)n) return null;
            for (int i = 0; i < n; i++)
                if (Dat.ReadU32(t1 + i * 14) != Dat.ReadU32(t2 + 4 + i * 32)) return null;
            return (n, t1, t2 + 4);
        }
    }

    public bool HasActivities => Activities != null;

    public int ActivitiesDone => Activities is { } a ? Enumerable.Range(0, a.Count).Count(i => Dat.Data[a.T1 + i * 14 + 4] == 4) : 0;
    public int ActivitiesTotal => Activities?.Count ?? 0;

    /// <summary>
    /// Puts every known activity that isn't finished yet into its finished state (as in a 100 % save).
    /// Story missions are a separate list and stay untouched. Returns how many were changed.
    /// </summary>
    public int CompleteActivities()
    {
        if (Activities is not { } a) return 0;
        var data = Convert.FromBase64String(SpmCompletedActivities.Data);
        var done = new Dictionary<uint, int>();
        for (int p = 0; p + 42 <= data.Length; p += 42) done[BitConverter.ToUInt32(data, p)] = p;

        int changed = 0;
        for (int i = 0; i < a.Count; i++)
        {
            int r1 = a.T1 + i * 14, r2 = a.T2 + i * 32;
            if (Dat.Data[r1 + 4] == 4 || !done.TryGetValue(Dat.ReadU32(r1), out var p)) continue;
            Array.Copy(data, p + 4, Dat.Data, r1 + 4, 10);
            Array.Copy(data, p + 14, Dat.Data, r2 + 4, 28);
            changed++;
        }
        return changed;
    }

    // ----- Progression (XP / level) -----

    private const uint ProgressSection = 0x586E01A8;
    private const uint ProgressTag = 0xF2D60B24;
    private int? _progress;
    private bool _progressSearched;

    /// <summary>Start of the progression block, or null if the layout is unknown.</summary>
    private int? Progress
    {
        get
        {
            if (_progressSearched) return _progress;
            _progressSearched = true;
            if (Dat.FindSection(ProgressSection) is { } s && s.Size > 0x21 &&
                Dat.Data[s.Offset] == 9 && Dat.ReadU32(s.Offset + 5) == ProgressTag &&
                Dat.ReadU32(s.Offset + 0x19) <= 60 && Dat.ReadU32(s.Offset + 0x1D) < 2000)
                _progress = s.Offset;
            return _progress;
        }
    }

    public bool HasProgress => Progress != null;

    /// <summary>Total XP collected so far.</summary>
    public uint TotalXp
    {
        get => Progress is { } p ? Dat.ReadU32(p + 9) : 0;
        set
        {
            if (Progress is { } p) Dat.WriteU32(p + 9, value);
        }
    }

    /// <summary>Level as shown in the game (stored 0-based).</summary>
    public int Level => Progress is { } p ? (int)Dat.ReadU32(p + 0x19) + 1 : 0;

    // ----- Tokens / resources -----

    private const uint InventorySection = 0x00D0A9DF;
    private const uint InventoryTag = 0x494E5654; // "TVNI"
    private const uint CurrencyType = 0xC69D8B53;

    public static readonly (ulong Id, string Key)[] KnownTokens =
    {
        (0xA130AADD4519CEBAul, "Sm_T_Crime"),
        (0x2FFBB94870F48CB7ul, "Sm_T_Base"),
        (0x1D8599E39CF4577Bul, "Sm_T_Research"),
        (0x7845373EFE686086ul, "Sm_T_Backpack"),
    };

    /// <summary>Currency entries of the inventory: item id and absolute offset of the count.</summary>
    public List<(ulong Id, int CountOffset)> Tokens()
    {
        var list = new List<(ulong, int)>();
        if (Dat.FindSection(InventorySection) is not { } s || s.Size < 16 || Dat.ReadU32(s.Offset + 8) != InventoryTag) return list;
        int end = s.Offset + s.Size;
        int count = (int)Dat.ReadU32(s.Offset + 12);
        int p = s.Offset + 16;
        for (int i = 0; i < count; i++)
        {
            if (p + 8 > end) return new List<(ulong, int)>();
            uint type = Dat.ReadU32(p);
            int len = (int)Dat.ReadU32(p + 4);
            if (len < 0 || len > 512 || p + 8 + len > end) return new List<(ulong, int)>();
            if (type == CurrencyType && len >= 21) list.Add((Dat.ReadU64(p + 9), p + 8 + 17));
            p += 8 + len;
        }
        return list;
    }

    public uint GetCount(int countOffset) => Dat.ReadU32(countOffset);
    public void SetCount(int countOffset, uint value) => Dat.WriteU32(countOffset, value);

    public void Save() => Dat.Save();
}

/// <summary>-userprefs.save: account-wide options (mouse, New Game+ …).</summary>
public sealed class SpmUserPrefs
{
    private SpmUserPrefs(SpmDat1 dat)
    {
        Dat = dat;
        Root = dat.FindObjectWith("MouseControlNormal");
    }

    public SpmDat1 Dat { get; }
    public SpmObject? Root { get; }

    public static SpmUserPrefs? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? new SpmUserPrefs(SpmDat1.Load(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public SpmObject? Mouse(bool aiming) => Root?[aiming ? "MouseControlAiming" : "MouseControlNormal"]?.Object;

    public float? GetFloat(SpmObject? o, string field) => o?[field] is { Type: SpmDat1.TFloat } f ? Dat.GetFloat(f) : null;

    public void SetFloat(SpmObject? o, string field, float value)
    {
        if (o?[field] is { Type: SpmDat1.TFloat } f) Dat.SetFloat(f, value);
    }

    public bool? GetBool(SpmObject? o, string field) => o?[field] is { Type: SpmDat1.TBool } f ? Dat.GetBool(f) : null;

    public void SetBool(SpmObject? o, string field, bool value)
    {
        if (o?[field] is { Type: SpmDat1.TBool } f) Dat.SetBool(f, value);
    }

    public void Save() => Dat.Save();
}

/// <summary>PC settings the game keeps in the registry (HKCU\Software\Insomniac Games\…).</summary>
public static class SpmRegistry
{
    public const string BaseKey = @"Software\Insomniac Games\Marvel's Spider-Man Remastered";

    public static int? Get(string sub, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(sub.Length == 0 ? BaseKey : BaseKey + "\\" + sub);
            return key?.GetValue(name) is int v ? v : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Set(string sub, string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(sub.Length == 0 ? BaseKey : BaseKey + "\\" + sub);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}

/// <summary>Backups of all .save files of a profile under %LOCALAPPDATA%\Layts\Backups\SpiderMan\&lt;id&gt;.</summary>
public static class SpmBackups
{
    public static string Root => Path.Combine(AppSettings.DataDirectory, "Backups", "SpiderMan");

    public static string DirectoryFor(SpmProfile profile) => Path.Combine(Root, profile.Name);

    public static void Create(SpmProfile profile)
    {
        var dir = Path.Combine(DirectoryFor(profile), DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
        Directory.CreateDirectory(dir);
        foreach (var file in Directory.GetFiles(profile.FolderPath, "*.save"))
            File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);

        BackupPolicy.Prune(DirectoryFor(profile));
    }

    public static List<BackupEntry> List(SpmProfile profile)
    {
        var root = DirectoryFor(profile);
        if (!Directory.Exists(root)) return new List<BackupEntry>();
        return Directory.GetDirectories(root)
            .Select(d => new BackupEntry
            {
                FolderPath = d,
                Created = Directory.GetCreationTime(d),
                Size = Directory.GetFiles(d, "*.save").Sum(f => new FileInfo(f).Length),
            })
            .Where(b => b.Size > 0)
            .OrderByDescending(b => Path.GetFileName(b.FolderPath), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Restores a backup. The current state is backed up first.</summary>
    public static void Restore(BackupEntry backup, SpmProfile profile)
    {
        Create(profile);
        foreach (var file in Directory.GetFiles(backup.FolderPath, "*.save"))
            File.Copy(file, Path.Combine(profile.FolderPath, Path.GetFileName(file)), overwrite: true);
    }
}
