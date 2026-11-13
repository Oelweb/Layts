using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Layts.Core;
using Layts.Games.Ets2;
using Layts.ViewModels;

namespace Layts.Games.Thy;

/// <summary>A hat or skill with a state picked from a small list.</summary>
public sealed class ThyStateItem : ObservableObject
{
    private int _state;

    public ThyStateItem(string label, IReadOnlyList<string> options)
    {
        Label = label;
        Options = options;
    }

    public string Label { get; }
    public IReadOnlyList<string> Options { get; }

    public int State
    {
        get => _state;
        set => Set(ref _state, Math.Clamp(value, 0, Options.Count - 1));
    }
}

public sealed class ThyViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private ThySave? _save;
    private int _loadToken;

    private ThySlot? _selectedSlot;
    private bool _isLoaded, _isBusy, _isGameRunning, _justSaved;
    private string _statusText = "";
    private StatusKind _statusKind;
    private int _levelIndex, _currentHatIndex;
    private string _checkpointText = "", _coinText = "", _acornText = "", _eyeText = "";
    private bool _kaizo, _enoughAcorns, _challengeUnlocked;
    private string _levelStat = "–", _hatStat = "–", _skillStat = "–", _coinStat = "–", _axeStat = "–", _gateStat = "–";

    public ThyViewModel(MainViewModel main)
    {
        _main = main;

        BackCommand = new RelayCommand(() => _main.ShowLibrary());
        RefreshCommand = new RelayCommand(LoadSlots, () => !IsBusy);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => IsLoaded && !IsBusy);
        FindAllHatsCommand = new RelayCommand(() => { foreach (var h in Hats) h.State = 1; });
        ResetHatsCommand = new RelayCommand(() => { foreach (var h in Hats) h.State = 0; });
        UnlockAllSkillsCommand = new RelayCommand(() => { foreach (var s in Skills) s.State = Math.Max(s.State, 1); });
        ActivateAllSkillsCommand = new RelayCommand(() => { foreach (var s in Skills) s.State = 2; });
        LastLevelCommand = new RelayCommand(() => LevelIndex = LevelOptions.Count - 1);
        AddCoinsCommand = new RelayCommand(p => CoinText = AddTo(CoinText, p));
        AddAcornsCommand = new RelayCommand(p => AcornText = AddTo(AcornText, p));
        SetEyeCommand = new RelayCommand(p =>
        {
            if (p is string s) EyeText = s;
        });
        OpenSaveFolderCommand = new RelayCommand(() => OpenFolder(ThyLocator.DataDirectory));
        OpenBackupFolderCommand = new RelayCommand(() => OpenFolder(ThyBackups.Root));
        RestoreBackupCommand = new RelayCommand(async p =>
        {
            if (p is BackupEntry b) await RestoreAsync(b);
        }, _ => !IsBusy);

        _isGameRunning = ThyLocator.IsGameRunning();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => IsGameRunning = ThyLocator.IsGameRunning();
        _timer.Start();

        LoadSlots();
    }

    // ---------- Images ----------

    public ImageSource? HeroImage { get; } = SteamArt.Load(ThyLocator.SteamAppId, SteamArt.Hero, 1920)
                                             ?? SteamArt.Load(ThyLocator.SteamAppId, "library_header.jpg", 1920);
    public ImageSource? LogoImage { get; } = SteamArt.Load(ThyLocator.SteamAppId, SteamArt.Logo, 640);

    // ---------- Collections & commands ----------

    public ObservableCollection<ThySlot> Slots { get; } = new();
    public ObservableCollection<ThyStateItem> Hats { get; } = new();
    public ObservableCollection<ThyStateItem> Skills { get; } = new();
    public ObservableCollection<string> LevelOptions { get; } = new();
    public ObservableCollection<string> HatOptions { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();

    public IReadOnlyList<string> EyePresets { get; } = new[]
    {
        "#2770D6", "#3FA34D", "#7A4A24", "#3B3B3B", "#8E44AD", "#D63031", "#F1C40F", "#00CEC9",
    };

    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand FindAllHatsCommand { get; }
    public ICommand ResetHatsCommand { get; }
    public ICommand UnlockAllSkillsCommand { get; }
    public ICommand ActivateAllSkillsCommand { get; }
    public ICommand LastLevelCommand { get; }
    public ICommand AddCoinsCommand { get; }
    public ICommand AddAcornsCommand { get; }
    public ICommand SetEyeCommand { get; }
    public ICommand OpenSaveFolderCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand RestoreBackupCommand { get; }

    // ---------- State ----------

    public bool HasSlots => Slots.Count > 0;

    public ThySlot? SelectedSlot
    {
        get => _selectedSlot;
        set
        {
            if (Set(ref _selectedSlot, value)) _ = LoadSaveAsync();
        }
    }

    public bool IsLoaded { get => _isLoaded; private set => Set(ref _isLoaded, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsGameRunning { get => _isGameRunning; private set => Set(ref _isGameRunning, value); }
    public bool JustSaved { get => _justSaved; private set => Set(ref _justSaved, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    // ---------- Progress ----------

    public int LevelIndex { get => _levelIndex; set => Set(ref _levelIndex, value); }
    public string CheckpointText { get => _checkpointText; set => Set(ref _checkpointText, value); }
    public bool Kaizo { get => _kaizo; set => Set(ref _kaizo, value); }
    public bool ChallengeUnlocked { get => _challengeUnlocked; set => Set(ref _challengeUnlocked, value); }
    public string CoinText { get => _coinText; set => Set(ref _coinText, value); }
    public string AcornText { get => _acornText; set => Set(ref _acornText, value); }
    public bool EnoughAcorns { get => _enoughAcorns; set => Set(ref _enoughAcorns, value); }

    // ---------- Looks ----------

    public int CurrentHatIndex { get => _currentHatIndex; set => Set(ref _currentHatIndex, value); }

    public string EyeText
    {
        get => _eyeText;
        set
        {
            if (!Set(ref _eyeText, value)) return;
            OnPropertyChanged(nameof(EyeBrush));
            OnPropertyChanged(nameof(EyeError));
        }
    }

    public bool HasEyeColor { get; private set; }

    public Brush EyeBrush => ThySave.TryParseHex(EyeText ?? "", out var r, out var g, out var b)
        ? new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b))
        : Brushes.Transparent;

    public string EyeError => !HasEyeColor || ThySave.TryParseHex(EyeText ?? "", out _, out _, out _) ? "" : Loc.Get("Thy_Err_Eye");

    // ---------- Overview ----------

    public string LevelStat { get => _levelStat; private set => Set(ref _levelStat, value); }
    public string HatStat { get => _hatStat; private set => Set(ref _hatStat, value); }
    public string SkillStat { get => _skillStat; private set => Set(ref _skillStat, value); }
    public string CoinStat { get => _coinStat; private set => Set(ref _coinStat, value); }
    public string AxeStat { get => _axeStat; private set => Set(ref _axeStat, value); }
    public string GateStat { get => _gateStat; private set => Set(ref _gateStat, value); }

    // ---------- Loading ----------

    private void LoadSlots()
    {
        var keep = SelectedSlot?.FilePath;
        Slots.Clear();
        foreach (var s in ThyLocator.FindSlots()) Slots.Add(s);
        OnPropertyChanged(nameof(HasSlots));

        var target = Slots.FirstOrDefault(s => s.FilePath == keep)
                     ?? Slots.OrderByDescending(s => s.Modified).FirstOrDefault();
        if (ReferenceEquals(target, _selectedSlot)) _ = LoadSaveAsync();
        else SelectedSlot = target;

        if (!HasSlots) ShowStatus(StatusKind.Info, Loc.Get("Thy_Err_NoSaves"));
    }

    private async Task LoadSaveAsync()
    {
        var slot = SelectedSlot;
        int token = ++_loadToken;
        _save = null;
        IsLoaded = false;
        if (slot == null) return;

        IsBusy = true;
        ClearStatus();
        try
        {
            var (save, challenge) = await Task.Run(() => (ThySave.Load(slot.FilePath), ThySave.ReadChallengeUnlocked()));
            if (token != _loadToken) return;
            _save = save;
            Apply(save, challenge ?? false);
            LoadBackups();
            IsLoaded = true;
        }
        catch (Exception ex)
        {
            if (token != _loadToken) return;
            ShowStatus(StatusKind.Error, Loc.F("Err_Load", ex.Message));
        }
        finally
        {
            if (token == _loadToken) IsBusy = false;
        }
    }

    private void Apply(ThySave save, bool challenge)
    {
        int level = save.GetInt("levelReached");
        LevelOptions.Clear();
        for (int i = 0; i < Math.Max(ThyLevels.Names.Length, level + 1); i++) LevelOptions.Add(ThyLevels.Name(i));
        LevelIndex = Math.Max(0, level);
        CheckpointText = save.GetInt("checkpoint").ToString(CultureInfo.InvariantCulture);
        Kaizo = save.GetBool("kaizo");
        ChallengeUnlocked = challenge;
        CoinText = save.GetInt("coinCount").ToString(CultureInfo.InvariantCulture);
        AcornText = save.GetInt("acornCount").ToString(CultureInfo.InvariantCulture);
        EnoughAcorns = save.GetBool("enough_acorns_found");

        string[] hatOptions = { Loc.Get("Thy_Hat_NotFound"), Loc.Get("Thy_Hat_Found"), Loc.Get("Thy_Hat_Lost") };
        Hats.Clear();
        var hatStates = save.GetIntList("hatStates");
        for (int i = 0; i < hatStates.Count; i++)
            Hats.Add(new ThyStateItem(Loc.F("Thy_HatX", i + 1), hatOptions) { State = hatStates[i] });

        string[] skillOptions = { Loc.Get("Thy_Skill_Locked"), Loc.Get("Thy_Skill_Unlocked"), Loc.Get("Thy_Skill_Active") };
        Skills.Clear();
        var skillStates = save.GetIntList("skillStates");
        for (int i = 0; i < skillStates.Count; i++)
            Skills.Add(new ThyStateItem(Loc.F("Thy_SkillX", i + 1), skillOptions) { State = skillStates[i] });

        HatOptions.Clear();
        HatOptions.Add(Loc.Get("Thy_NoHat"));
        for (int i = 0; i < hatStates.Count; i++) HatOptions.Add(Loc.F("Thy_HatX", i + 1));
        CurrentHatIndex = Math.Clamp(save.GetInt("currentHat", -1) + 1, 0, HatOptions.Count - 1);

        var eye = save.EyeColor;
        HasEyeColor = eye != null;
        OnPropertyChanged(nameof(HasEyeColor));
        EyeText = eye ?? "";

        LevelStat = ThyLevels.Name(level);
        HatStat = $"{hatStates.Count(s => s == 1)} / {hatStates.Count}";
        SkillStat = $"{skillStates.Count(s => s > 0)} / {skillStates.Count}";
        CoinStat = save.Count("coinsCollected").ToString(Loc.Culture);
        AxeStat = save.Count("axesBroken").ToString(Loc.Culture);
        GateStat = save.Count("gatesOpened").ToString(Loc.Culture);
    }

    private void LoadBackups()
    {
        Backups.Clear();
        foreach (var b in ThyBackups.List()) Backups.Add(b);
    }

    // ---------- Saving ----------

    private async Task SaveAsync()
    {
        var save = _save;
        if (save == null) return;

        if (!int.TryParse(CheckpointText.Trim(), out var checkpoint) || checkpoint < 0 || checkpoint > 999)
        {
            ShowStatus(StatusKind.Error, Loc.Get("Thy_Err_Checkpoint"));
            return;
        }
        if (!int.TryParse(CoinText.Trim(), out var coins) || coins < 0 || !int.TryParse(AcornText.Trim(), out var acorns) || acorns < 0)
        {
            ShowStatus(StatusKind.Error, Loc.Get("Thy_Err_Count"));
            return;
        }
        if (EyeError.Length > 0)
        {
            ShowStatus(StatusKind.Error, EyeError);
            return;
        }

        if (ThyLocator.IsGameRunning())
        {
            var answer = MessageBox.Show(Loc.Get("Thy_Msg_GameRunning"), Loc.Get("Msg_GameRunningTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        int level = Math.Max(0, LevelIndex);
        bool levelChanged = level != save.GetInt("levelReached");
        bool kaizo = Kaizo, enough = EnoughAcorns, challenge = ChallengeUnlocked;
        var hats = Hats.Select(h => h.State).ToList();
        var skills = Skills.Select(s => s.State).ToList();
        int currentHat = CurrentHatIndex - 1;
        string? eye = HasEyeColor ? EyeText : null;
        bool? oldChallenge = ThySave.ReadChallengeUnlocked();
        bool backup = ShouldBackupNow();

        IsBusy = true;
        ClearStatus();
        try
        {
            await Task.Run(() =>
            {
                if (backup) ThyBackups.Create();
                save.SetInt("levelReached", level);
                // A checkpoint of another level would be meaningless – start the new level at its beginning.
                save.SetInt("checkpoint", levelChanged ? 0 : checkpoint);
                save.SetBool("kaizo", kaizo);
                save.SetInt("coinCount", coins);
                save.SetInt("acornCount", acorns);
                save.SetBool("enough_acorns_found", enough);
                save.SetIntList("hatStates", hats);
                save.SetIntList("skillStates", skills);
                // Wearing a hat you haven't found makes no sense – fall back to none.
                save.SetInt("currentHat", currentHat >= 0 && currentHat < hats.Count && hats[currentHat] == 1 ? currentHat : -1);
                if (eye != null) save.EyeColor = eye;
                save.Write();

                if (oldChallenge != challenge) ThySave.WriteChallengeUnlocked(challenge);
            });

            await ReloadKeepingSlotAsync();
            ShowStatus(StatusKind.Success, Loc.F(backup ? "Msg_Saved" : "Msg_SavedNoBackup", ""));
            _ = FlashSavedAsync();
        }
        catch (Exception ex)
        {
            await ReloadKeepingSlotAsync();
            ShowStatus(StatusKind.Error, Loc.F("Err_Save", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadKeepingSlotAsync()
    {
        var keep = SelectedSlot?.FilePath;
        Slots.Clear();
        foreach (var s in ThyLocator.FindSlots()) Slots.Add(s);
        _selectedSlot = Slots.FirstOrDefault(s => s.FilePath == keep) ?? Slots.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedSlot));
        await LoadSaveAsync();
    }

    private async Task RestoreAsync(BackupEntry backup)
    {
        var answer = MessageBox.Show(Loc.F("Msg_Restore", backup.Display), Loc.Get("Msg_RestoreTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => ThyBackups.Restore(backup));
            await ReloadKeepingSlotAsync();
            ShowStatus(StatusKind.Success, Loc.F("Msg_Restored", backup.Display));
            _ = FlashSavedAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(StatusKind.Error, Loc.F("Err_Restore", ex.Message));
        }
    }

    // ---------- Helpers ----------

    private static string AddTo(string text, object? parameter)
    {
        int.TryParse(text?.Trim(), out var current);
        int.TryParse(parameter as string, out var add);
        return Math.Clamp((long)current + add, 0, int.MaxValue).ToString(CultureInfo.InvariantCulture);
    }

    private async Task FlashSavedAsync()
    {
        JustSaved = true;
        await Task.Delay(2600);
        JustSaved = false;
    }

    private void ShowStatus(StatusKind kind, string text)
    {
        StatusKind = kind;
        StatusText = text;
    }

    private void ClearStatus() => ShowStatus(StatusKind.None, "");

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    // ---------- Backup options ----------

    private const string BackupGameId = "thy";

    /// <summary>Automatic backups for this game (the global switch is in Settings).</summary>
    public bool AutoBackupThisGame
    {
        get => BackupPolicy.IsGameEnabled(BackupGameId);
        set
        {
            if (value == AutoBackupThisGame) return;
            BackupPolicy.SetGameEnabled(BackupGameId, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackupHint));
        }
    }

    public bool IsGlobalBackupOff => !AppSettings.Current.AutoBackup;
    public string BackupHint => BackupPolicy.Hint(BackupGameId);

    private ICommand? _backupNowCommand;
    public ICommand BackupNowCommand => _backupNowCommand ??= new RelayCommand(async () =>
    {
        try
        {
            await Task.Run(() => ThyBackups.Create());
            LoadBackups();
            ShowStatus(StatusKind.Success, Loc.Get("Bk_Created"));
        }
        catch (Exception ex)
        {
            ShowStatus(StatusKind.Error, Loc.F("Err_Save", ex.Message));
        }
    }, () => !IsBusy);

    private bool ShouldBackupNow() => BackupPolicy.ShouldBackup(BackupGameId, ThyBackups.List().FirstOrDefault()?.Created);

    public void Dispose() => _timer.Stop();
}
