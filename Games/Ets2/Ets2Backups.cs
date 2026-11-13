using System.Security.Cryptography;
using System.Text;
using Layts.Core;

namespace Layts.Games.Ets2;

public sealed class BackupEntry
{
    public required string FolderPath { get; init; }
    public DateTime Created { get; init; }
    public long Size { get; init; }

    public string Display => Created.ToString("dd.MM.yyyy  ·  HH:mm:ss");
    public string SizeText => $"{Size / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>Sicherungen liegen unter %LOCALAPPDATA%\Layts\Backups\ETS2 – nie im Spielordner.</summary>
public static class Ets2Backups
{
    private const int MaxBackupsPerSave = 25;
    private static readonly string[] SaveFiles = { "game.sii", "info.sii" };

    public static string Root => Path.Combine(AppSettings.DataDirectory, "Backups", "ETS2");

    public static string DirectoryFor(Ets2SaveSlot slot)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(slot.FolderPath.ToLowerInvariant())))[..8];
        var profile = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(slot.FolderPath))) ?? "profil";
        return Path.Combine(Root, $"{profile}_{Path.GetFileName(slot.FolderPath)}_{hash}");
    }

    public static BackupEntry Create(Ets2SaveSlot slot)
    {
        var dir = Path.Combine(DirectoryFor(slot), DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
        Directory.CreateDirectory(dir);

        long size = 0;
        foreach (var name in SaveFiles)
        {
            var source = Path.Combine(slot.FolderPath, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(dir, name), overwrite: true);
            size += new FileInfo(source).Length;
        }
        File.WriteAllText(Path.Combine(dir, "quelle.txt"), slot.FolderPath);

        Prune(slot);
        return new BackupEntry { FolderPath = dir, Created = DateTime.Now, Size = size };
    }

    public static List<BackupEntry> List(Ets2SaveSlot slot)
    {
        var root = DirectoryFor(slot);
        if (!Directory.Exists(root)) return new List<BackupEntry>();

        return Directory.GetDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, "game.sii")))
            .Select(d => new BackupEntry
            {
                FolderPath = d,
                Created = Directory.GetCreationTime(d),
                Size = new FileInfo(Path.Combine(d, "game.sii")).Length,
            })
            .OrderByDescending(b => Path.GetFileName(b.FolderPath), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Stellt ein Backup wieder her. Der aktuelle Stand wird vorher selbst gesichert.</summary>
    public static void Restore(BackupEntry backup, Ets2SaveSlot slot)
    {
        Create(slot);
        foreach (var name in SaveFiles)
        {
            var source = Path.Combine(backup.FolderPath, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(slot.FolderPath, name), overwrite: true);
        }
    }

    private static void Prune(Ets2SaveSlot slot)
    {
        foreach (var old in List(slot).Skip(MaxBackupsPerSave))
        {
            try { Directory.Delete(old.FolderPath, recursive: true); }
            catch { /* nicht kritisch */ }
        }
    }
}
