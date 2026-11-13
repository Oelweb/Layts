using System.Text.RegularExpressions;

namespace Layts.Core;

/// <summary>
/// One place that decides when the editors make automatic backups and how many are kept.
/// Backups are folders named yyyy-MM-dd_HH-mm-ss-fff inside a per-game (or per-save) folder.
/// </summary>
public static class BackupPolicy
{
    private static readonly Regex StampFolder = new(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}-\d{3}$");

    /// <summary>Choices on the settings page.</summary>
    public static readonly int[] IntervalChoices = { 0, 5, 15, 60 };
    public static readonly int[] KeepChoices = { 3, 5, 10, 25 };

    private static AppSettings S => AppSettings.Current;

    public static string Root => Path.Combine(AppSettings.DataDirectory, "Backups");

    public static int Keep => Math.Clamp(S.BackupKeep, 1, 100);

    public static bool IsGameEnabled(string gameId) => !S.BackupDisabledGames.Contains(gameId);

    public static void SetGameEnabled(string gameId, bool enabled)
    {
        S.BackupDisabledGames.Remove(gameId);
        if (!enabled) S.BackupDisabledGames.Add(gameId);
        S.Save();
    }

    /// <summary>Automatic backups are on for this game (globally and per game).</summary>
    public static bool IsActive(string gameId) => S.AutoBackup && IsGameEnabled(gameId);

    /// <summary>Should an automatic backup be made now, given the newest existing one?</summary>
    public static bool ShouldBackup(string gameId, DateTime? newest)
    {
        if (!IsActive(gameId)) return false;
        if (S.BackupIntervalMinutes <= 0 || newest == null) return true;
        return DateTime.Now - newest.Value >= TimeSpan.FromMinutes(S.BackupIntervalMinutes);
    }

    /// <summary>Short text under a save button that says what will happen.</summary>
    public static string Hint(string gameId)
    {
        if (!S.AutoBackup) return Loc.Get("Bk_HintOffGlobal");
        if (!IsGameEnabled(gameId)) return Loc.Get("Bk_HintOffGame");
        return S.BackupIntervalMinutes <= 0 ? Loc.Get("BackupFirst") : Loc.F("Bk_HintInterval", S.BackupIntervalMinutes);
    }

    /// <summary>Deletes all but the newest <see cref="Keep"/> backups in one folder.</summary>
    public static void Prune(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var old = Directory.GetDirectories(folder)
            .Where(d => StampFolder.IsMatch(Path.GetFileName(d)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .Skip(Keep);
        foreach (var dir in old)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* not critical */ }
        }
    }

    /// <summary>Applies the keep limit to every backup folder (after the user lowered it).</summary>
    public static void PruneAll()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var dir in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories)
                     .Where(d => !StampFolder.IsMatch(Path.GetFileName(d)))
                     .Append(Root))
            Prune(dir);
    }

    public static long TotalSize()
    {
        try
        {
            return Directory.Exists(Root)
                ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    public static int Count()
    {
        try
        {
            return Directory.Exists(Root)
                ? Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).Count(d => StampFolder.IsMatch(Path.GetFileName(d)))
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Deletes every backup layts has made.</summary>
    public static void DeleteAll()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* skip locked folders */ }
        }
    }
}
