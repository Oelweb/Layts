using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Layts.Core;

namespace Layts.ViewModels;

/// <summary>Per-game switch for automatic backups on the settings page.</summary>
public sealed class GameBackupToggle : ObservableObject
{
    public GameBackupToggle(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }
    public string Name { get; }

    public bool IsOn
    {
        get => BackupPolicy.IsGameEnabled(Id);
        set
        {
            if (value == IsOn) return;
            BackupPolicy.SetGameEnabled(Id, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the value (it can also be changed inside a game's editor).</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsOn));
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        OpenBackupFolderCommand = new RelayCommand(() =>
        {
            try
            {
                Directory.CreateDirectory(BackupFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BackupFolder}\"") { UseShellExecute = true });
            }
            catch
            {
                // Explorer nicht verfügbar
            }
        });

        foreach (var g in Layts.Games.GameCatalog.All) GameBackups.Add(new GameBackupToggle(g.Id, g.Name));

        DeleteAllBackupsCommand = new RelayCommand(() =>
        {
            var answer = MessageBox.Show(Loc.F("Bk_DeleteAllAsk", BackupPolicy.Count()), Loc.Get("Bk_DeleteAll"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            BackupPolicy.DeleteAll();
            RefreshBackupStats();
        });
        RefreshBackupStats();
    }

    public Updater Updater => _main.Updater;
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand DeleteAllBackupsCommand { get; }

    public string BackupFolder => BackupPolicy.Root;

    // ---------- Backups ----------

    public ObservableCollection<GameBackupToggle> GameBackups { get; } = new();

    public bool AutoBackup
    {
        get => _main.Settings.AutoBackup;
        set
        {
            if (_main.Settings.AutoBackup == value) return;
            _main.Settings.AutoBackup = value;
            _main.Settings.Save();
            OnPropertyChanged();
        }
    }

    public IEnumerable<string> IntervalOptions => BackupPolicy.IntervalChoices.Select(m => m == 0
        ? Loc.Get("Bk_EverySave")
        : m >= 60 ? Loc.F("Bk_EveryHours", m / 60) : Loc.F("Bk_EveryMinutes", m));

    public int IntervalIndex
    {
        get => Math.Max(0, Array.IndexOf(BackupPolicy.IntervalChoices, _main.Settings.BackupIntervalMinutes));
        set
        {
            if (value < 0 || value >= BackupPolicy.IntervalChoices.Length) return;
            _main.Settings.BackupIntervalMinutes = BackupPolicy.IntervalChoices[value];
            _main.Settings.Save();
            OnPropertyChanged();
        }
    }

    public IEnumerable<string> KeepOptions => BackupPolicy.KeepChoices.Select(k => Loc.F("Bk_KeepX", k));

    public int KeepIndex
    {
        get => Math.Max(0, Array.IndexOf(BackupPolicy.KeepChoices, _main.Settings.BackupKeep));
        set
        {
            if (value < 0 || value >= BackupPolicy.KeepChoices.Length) return;
            var keep = BackupPolicy.KeepChoices[value];
            if (keep == _main.Settings.BackupKeep) return;
            _main.Settings.BackupKeep = keep;
            _main.Settings.Save();
            BackupPolicy.PruneAll();
            RefreshBackupStats();
            OnPropertyChanged();
        }
    }

    private string _backupStats = "";
    public string BackupStats
    {
        get => _backupStats;
        private set => Set(ref _backupStats, value);
    }

    public void RefreshBackupStats() =>
        BackupStats = Loc.F("Bk_Stats", BackupPolicy.Count(), (BackupPolicy.TotalSize() / 1024.0 / 1024.0).ToString("0.0", Loc.Culture));

    public bool IsEnglish
    {
        get => Loc.Language == "en";
        set
        {
            if (value) SetLanguage("en");
        }
    }

    public bool IsGerman
    {
        get => Loc.Language == "de";
        set
        {
            if (value) SetLanguage("de");
        }
    }

    public bool AutoUpdate
    {
        get => _main.Settings.AutoUpdate;
        set
        {
            if (_main.Settings.AutoUpdate == value) return;
            _main.Settings.AutoUpdate = value;
            _main.Settings.Save();
            OnPropertyChanged();
        }
    }

    private void SetLanguage(string lang)
    {
        if (Loc.Language == lang) return;
        Loc.SetLanguage(lang);
        _main.Settings.Language = lang;
        _main.Settings.Save();
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(IsGerman));
        OnPropertyChanged(nameof(IntervalOptions));
        OnPropertyChanged(nameof(KeepOptions));
        OnPropertyChanged(nameof(IntervalIndex));
        OnPropertyChanged(nameof(KeepIndex));
        RefreshBackupStats();
    }
}
