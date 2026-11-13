using System.Diagnostics;
using System.Windows.Input;
using Layts.Core;
using Layts.Games.Ets2;

namespace Layts.ViewModels;

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
    }

    public Updater Updater => _main.Updater;
    public ICommand OpenBackupFolderCommand { get; }

    public string BackupFolder => Path.GetDirectoryName(Ets2Backups.Root) ?? Ets2Backups.Root;

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
    }
}
