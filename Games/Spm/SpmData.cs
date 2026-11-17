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
