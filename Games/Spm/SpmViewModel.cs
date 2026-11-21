using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Layts.Core;
using Layts.Games.Bus;
using Layts.Games.Ets2;
using Layts.ViewModels;

namespace Layts.Games.Spm;

public sealed class SpmTokenRow : ObservableObject
{
    private string _text;

    public SpmTokenRow(string label, int offset, uint value, bool known)
    {
        Label = label;
        Offset = offset;
        Known = known;
        _text = value.ToString(CultureInfo.InvariantCulture);
        Original = value;
    }

    public string Label { get; }
    public int Offset { get; }
    public bool Known { get; }
    public uint Original { get; }
    public string Text { get => _text; set => Set(ref _text, value); }
}

public sealed class SpmViewModel : ObservableObject, IDisposable
{
    private const string BackupGameId = "spiderman";

    /// <summary>Registry switches: (sub key, value name, string key).</summary>
    private static readonly (string Sub, string Name, string Key)[] RegistryToggles =
    {
        ("Input", "EnableMouseSmoothing", "Sm_R_MouseSmooth"),
        ("", "englishVO", "Sm_R_EnglishVo"),
        ("Graphics", "Vignette", "Sm_R_Vignette"),
        ("Graphics", "Bloom", "Sm_R_Bloom"),
        ("Graphics", "LensFlares", "Sm_R_LensFlares"),
        ("Graphics", "ChromaticAberration", "Sm_R_ChromAb"),
    };

    /// <summary>Registry values stored as 0–1000 → shown as 0–100 %.</summary>
    private static readonly (string Sub, string Name, string Key)[] RegistrySliders =
    {
        ("Graphics", "FilmGrainStrength", "Sm_R_FilmGrain"),
        ("Graphics", "MotionBlurScale", "Sm_R_MotionBlur"),
        ("Graphics", "Sharpness", "Sm_R_Sharpness"),
    };

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private SpmSave? _save;
    private SpmUserPrefs? _prefs;
    private int _loadToken;

    private SpmProfile? _selectedProfile;
    private SpmSlot? _selectedSlot;
    private bool _isLoaded, _isBusy, _isGameRunning, _justSaved;
    private string _statusText = "";
    private StatusKind _statusKind;
    private bool _newGamePlus, _activateTowers, _showLandmarks, _discoverLandmarks, _completeChallenges, _completeActivities;
    private string _xpText = "";
    private string _percentStat = "–", _playStat = "–", _towerStat = "–", _landmarkStat = "–", _challengeStat = "–";

    public SpmViewModel(MainViewModel main)
    {
        _main = main;

        BackCommand = new RelayCommand(() => _main.ShowLibrary());
        RefreshCommand = new RelayCommand(LoadProfiles, () => !IsBusy);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => IsLoaded && !IsBusy);
        AllHelpsCommand = new RelayCommand(() =>
        {
            foreach (var t in SlotOptions.Where(t => t.Key is "SkipQTEs" or "ButtonMashUseHold" or "SkipTowerPuzzle" or "CombatCameraRotationAssistEnabled"))
                t.IsOn = true;
        });
        AddXpCommand = new RelayCommand(p =>
        {
            uint.TryParse(XpText?.Trim(), out var current);
            long.TryParse(p as string, out var add);
            XpText = Math.Clamp(current + add, 0, 9_999_999).ToString(CultureInfo.InvariantCulture);
        });
        TokensCommand = new RelayCommand(p =>
        {
            foreach (var t in Tokens.Where(t => t.Known)) t.Text = p as string ?? "0";
        });
        ResetMouseCommand = new RelayCommand(() =>
        {
            foreach (var s in MouseSliders) s.Value = 50;
        });
        OpenSaveFolderCommand = new RelayCommand(() => OpenFolder(SelectedProfile?.FolderPath ?? SpmLocator.RootDirectory));
        OpenBackupFolderCommand = new RelayCommand(() => OpenFolder(SelectedProfile == null ? SpmBackups.Root : SpmBackups.DirectoryFor(SelectedProfile)));
        RestoreBackupCommand = new RelayCommand(async p =>
        {
            if (p is BackupEntry b) await RestoreAsync(b);
        }, _ => !IsBusy);
        BackupNowCommand = new RelayCommand(async () =>
        {
            if (SelectedProfile is not { } profile) return;
            try
            {
                await Task.Run(() => SpmBackups.Create(profile));
                LoadBackups();
                ShowStatus(StatusKind.Success, Loc.Get("Bk_Created"));
            }
            catch (Exception ex)
            {
                ShowStatus(StatusKind.Error, Loc.F("Err_Save", ex.Message));
            }
        }, () => SelectedProfile != null && !IsBusy);

        _isGameRunning = SpmLocator.IsGameRunning();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => IsGameRunning = SpmLocator.IsGameRunning();
        _timer.Start();

        LoadRegistry();
        LoadProfiles();
    }

    // ---------- Images ----------

    public ImageSource? HeroImage { get; } = SteamArt.Load(SpmLocator.SteamAppId, SteamArt.Hero, 1920)
                                             ?? SteamArt.Load(SpmLocator.SteamAppId, "library_header.jpg", 1920);
    public ImageSource? LogoImage { get; } = SteamArt.Load(SpmLocator.SteamAppId, SteamArt.Logo, 640);

    // ---------- Collections & commands ----------

    public ObservableCollection<SpmProfile> Profiles { get; } = new();
    public ObservableCollection<SpmSlot> Slots { get; } = new();
    public ObservableCollection<BusToggle> SlotOptions { get; } = new();
    public ObservableCollection<BusToggle> MouseToggles { get; } = new();
    public ObservableCollection<BusSlider> MouseSliders { get; } = new();
    public ObservableCollection<BusToggle> PcToggles { get; } = new();
    public ObservableCollection<BusSlider> PcSliders { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();
    public ObservableCollection<SpmTokenRow> Tokens { get; } = new();
    public ObservableCollection<SpmTokenRow> OtherTokens { get; } = new();

    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AllHelpsCommand { get; }
    public ICommand ResetMouseCommand { get; }
    public ICommand AddXpCommand { get; }
    public ICommand TokensCommand { get; }
    public ICommand OpenSaveFolderCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand BackupNowCommand { get; }

    // ---------- State ----------

    public bool HasProfiles => Profiles.Count > 0;
    public bool HasSlots => Slots.Count > 0;
    public bool HasPrefs => _prefs?.Root != null;
    public bool HasPcSettings => PcToggles.Count + PcSliders.Count > 0;

    public SpmProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            LoadSlots();
            LoadBackups();
        }
    }

    public SpmSlot? SelectedSlot
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

    // ---------- Progress & map ----------

    public bool NewGamePlus { get => _newGamePlus; set => Set(ref _newGamePlus, value); }
    public bool HasNewGamePlus { get; private set; }
    public bool ActivateTowers { get => _activateTowers; set => Set(ref _activateTowers, value); }
    public bool ShowLandmarks { get => _showLandmarks; set => Set(ref _showLandmarks, value); }

    public bool DiscoverLandmarks
    {
        get => _discoverLandmarks;
        set
        {
            if (Set(ref _discoverLandmarks, value) && value) ShowLandmarks = true;
        }
    }

    public bool CompleteChallenges { get => _completeChallenges; set => Set(ref _completeChallenges, value); }
    public bool CompleteActivities { get => _completeActivities; set => Set(ref _completeActivities, value); }
    public bool HasActivities { get; private set; }
    public string ActivityInfo { get; private set; } = "";
    public bool HasChallenges { get; private set; }
    public string ChallengeInfo { get; private set; } = "";

    public bool HasProgress { get; private set; }
    public string XpText { get => _xpText; set => Set(ref _xpText, value); }
    public string LevelInfo { get; private set; } = "";
    public bool HasTokens => Tokens.Count > 0;
    public bool HasOtherTokens => OtherTokens.Count > 0;

    public bool HasTowers { get; private set; }
    public bool HasLandmarks { get; private set; }
    public string TowerInfo { get; private set; } = "";
    public string LandmarkInfo { get; private set; } = "";

    // ---------- Overview ----------

    public string PercentStat { get => _percentStat; private set => Set(ref _percentStat, value); }
    public string PlayStat { get => _playStat; private set => Set(ref _playStat, value); }
    public string TowerStat { get => _towerStat; private set => Set(ref _towerStat, value); }
    public string LandmarkStat { get => _landmarkStat; private set => Set(ref _landmarkStat, value); }
    public string ChallengeStat { get => _challengeStat; private set => Set(ref _challengeStat, value); }

    // ---------- Loading ----------

    private void LoadProfiles()
    {
        var keep = SelectedProfile?.FolderPath;
        Profiles.Clear();
        foreach (var p in SpmLocator.FindProfiles()) Profiles.Add(p);
        OnPropertyChanged(nameof(HasProfiles));

        var target = Profiles.FirstOrDefault(p => p.FolderPath == keep) ?? Profiles.FirstOrDefault();
        if (ReferenceEquals(target, _selectedProfile))
        {
            LoadSlots();
            LoadBackups();
        }
        else
        {
            SelectedProfile = target;
        }

        if (!HasProfiles) ShowStatus(StatusKind.Info, Loc.Get("Sm_Err_NoSaves"));
    }

    private void LoadSlots()
    {
        var keep = SelectedSlot?.FilePath;
        Slots.Clear();
        if (SelectedProfile is { } profile)
            foreach (var s in SpmLocator.FindSlots(profile)) Slots.Add(s);
        OnPropertyChanged(nameof(HasSlots));

        var target = Slots.FirstOrDefault(s => s.FilePath == keep) ?? Slots.OrderByDescending(s => s.Modified).FirstOrDefault();
        if (ReferenceEquals(target, _selectedSlot)) _ = LoadSaveAsync();
        else SelectedSlot = target;
    }

    private async Task LoadSaveAsync()
    {
        var slot = SelectedSlot;
        var profile = SelectedProfile;
        int token = ++_loadToken;
        _save = null;
        IsLoaded = false;
        if (slot == null || profile == null) return;

        IsBusy = true;
        ClearStatus();
        try
        {
            var (save, prefs) = await Task.Run(() => (SpmSave.Load(slot.FilePath), SpmUserPrefs.TryLoad(profile.UserPrefsPath)));
            if (token != _loadToken) return;
            if (!save.HasGame) throw new InvalidDataException(Loc.Get("Sm_Err_Format"));
            _save = save;
            _prefs = prefs;
            Apply(save, prefs);
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

    private void Apply(SpmSave save, SpmUserPrefs? prefs)
    {
        SlotOptions.Clear();
        foreach (var (field, key) in SpmSave.Options)
        {
            if (save.GetOption(field) is not { } on) continue;
            SlotOptions.Add(new BusToggle(field, Loc.Get(key), Loc.Get(key + "Desc")) { IsOn = on });
        }

        var ngp = save.GetOption("NewGamePlusUnlocked");
        HasNewGamePlus = ngp != null;
        NewGamePlus = ngp == true || prefs?.GetBool(prefs.Root, "NewGamePlusUnlocked") == true;
        OnPropertyChanged(nameof(HasNewGamePlus));

        HasProgress = save.HasProgress;
        XpText = save.TotalXp.ToString(CultureInfo.InvariantCulture);
        LevelInfo = Loc.F("Sm_LevelInfo", save.Level);
        OnPropertyChanged(nameof(HasProgress));
        OnPropertyChanged(nameof(LevelInfo));

        Tokens.Clear();
        OtherTokens.Clear();
        int other = 0;
        foreach (var (id, offset) in save.Tokens())
        {
            var known = SpmSave.KnownTokens.FirstOrDefault(k => k.Id == id);
            if (known.Key != null) Tokens.Add(new SpmTokenRow(Loc.Get(known.Key), offset, save.GetCount(offset), true));
            else OtherTokens.Add(new SpmTokenRow(Loc.F("Sm_T_Other", ++other), offset, save.GetCount(offset), false));
        }
        OnPropertyChanged(nameof(HasTokens));
        OnPropertyChanged(nameof(HasOtherTokens));

        CompleteActivities = false;
        HasActivities = save.HasActivities;
        ActivityInfo = Loc.F("Sm_ActivityInfo", save.ActivitiesDone, save.ActivitiesTotal);
        OnPropertyChanged(nameof(HasActivities));
        OnPropertyChanged(nameof(ActivityInfo));

        CompleteChallenges = false;
        HasChallenges = save.Challenges.Count > 0 || save.HasActivities;
        ChallengeInfo = Loc.F("Sm_ChallengeInfo", save.ChallengesDone, save.Challenges.Count);
        OnPropertyChanged(nameof(HasChallenges));
        OnPropertyChanged(nameof(ChallengeInfo));

        ActivateTowers = false;
        ShowLandmarks = false;
        DiscoverLandmarks = false;
        HasTowers = save.Towers.Count > 0;
        HasLandmarks = save.Landmarks.Count > 0;
        TowerInfo = Loc.F("Sm_TowerInfo", save.TowersActive, save.Towers.Count);
        LandmarkInfo = Loc.F("Sm_LandmarkInfo", save.LandmarksOnMap, save.LandmarksDiscovered, save.Landmarks.Count);
        OnPropertyChanged(nameof(HasTowers));
        OnPropertyChanged(nameof(HasLandmarks));
        OnPropertyChanged(nameof(TowerInfo));
        OnPropertyChanged(nameof(LandmarkInfo));

        // Mouse (account-wide)
        MouseSliders.Clear();
        MouseToggles.Clear();
        if (prefs?.Root != null)
        {
            foreach (var aiming in new[] { false, true })
            {
                var o = prefs.Mouse(aiming);
                string prefix = aiming ? "Sm_M_Aim" : "Sm_M_Normal";
                foreach (var axis in new[] { "X", "Y" })
                {
                    if (prefs.GetFloat(o, "SensitivityFactor" + axis) is { } v)
                        MouseSliders.Add(new BusSlider($"{(aiming ? "A" : "N")}:SensitivityFactor{axis}", Loc.F(prefix, axis), 0, 100, 100, " %") { Value = v * 100 });
                }
                foreach (var axis in new[] { "X", "Y" })
                {
                    if (prefs.GetBool(o, "Invert" + axis) is { } inv)
                        MouseToggles.Add(new BusToggle($"{(aiming ? "A" : "N")}:Invert{axis}", Loc.F(aiming ? "Sm_M_InvertAim" : "Sm_M_Invert", axis), "") { IsOn = inv });
                }
            }
        }
        OnPropertyChanged(nameof(HasPrefs));

        PercentStat = save.Percent.ToString("0.0", Loc.Culture) + " %";
        var t = TimeSpan.FromSeconds(Math.Max(0, save.PlaySeconds));
        PlayStat = save.PlaySeconds > 0 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : "–";
        TowerStat = $"{save.TowersActive} / {save.Towers.Count}";
        LandmarkStat = $"{save.LandmarksDiscovered} / {save.Landmarks.Count}";
        ChallengeStat = $"{save.ChallengesDone} / {save.Challenges.Count}";
    }

    private void LoadRegistry()
    {
        PcToggles.Clear();
        PcSliders.Clear();
        foreach (var (sub, name, key) in RegistryToggles)
            if (SpmRegistry.Get(sub, name) is { } v)
                PcToggles.Add(new BusToggle(sub + "|" + name, Loc.Get(key), Loc.Get(key + "Desc")) { IsOn = v != 0 });
        foreach (var (sub, name, key) in RegistrySliders)
            if (SpmRegistry.Get(sub, name) is { } v)
                PcSliders.Add(new BusSlider(sub + "|" + name, Loc.Get(key), 0, 100, 10, " %") { Value = v / 10.0 });
        OnPropertyChanged(nameof(HasPcSettings));
    }

    private void LoadBackups()
    {
        Backups.Clear();
        if (SelectedProfile is { } p)
            foreach (var b in SpmBackups.List(p)) Backups.Add(b);
    }

    // ---------- Saving ----------

    private async Task SaveAsync()
    {
        var save = _save;
        var prefs = _prefs;
        var profile = SelectedProfile;
        if (save == null || profile == null) return;

        if (SpmLocator.IsGameRunning())
        {
            var answer = MessageBox.Show(Loc.Get("Sm_Msg_GameRunning"), Loc.Get("Msg_GameRunningTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        uint? xp = null;
        if (HasProgress)
        {
            if (!uint.TryParse(XpText.Trim(), out var v) || v > 9_999_999)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sm_Err_Xp"));
                return;
            }
            xp = v;
        }
        var tokens = new List<(int Offset, uint Value)>();
        foreach (var t in Tokens.Concat(OtherTokens))
        {
            if (!uint.TryParse(t.Text.Trim(), out var v) || v > 9999)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sm_Err_Tokens"));
                return;
            }
            if (v != t.Original) tokens.Add((t.Offset, v));
        }
        bool challenges = CompleteChallenges, activities = CompleteActivities;

        var options = SlotOptions.Select(o => (o.Key, o.IsOn)).ToList();
        bool ngp = NewGamePlus, towers = ActivateTowers, show = ShowLandmarks, discover = DiscoverLandmarks;
        var mouse = MouseSliders.Select(s => (s.Key, (float)(s.Value / 100.0))).ToList();
        var mouseToggles = MouseToggles.Select(t => (t.Key, t.IsOn)).ToList();
        var regToggles = PcToggles.Select(t => (t.Key, t.IsOn)).ToList();
        var regSliders = PcSliders.Select(s => (s.Key, (int)Math.Round(s.Value * 10))).ToList();
        bool backup = ShouldBackupNow();

        IsBusy = true;
        ClearStatus();
        try
        {
            await Task.Run(() =>
            {
                if (backup) SpmBackups.Create(profile);

                foreach (var (field, on) in options) save.SetOption(field, on);
                if (HasNewGamePlus) save.SetOption("NewGamePlusUnlocked", ngp);
                if (towers) save.ActivateTowers();
                if (show || discover) save.RevealLandmarks(discover);
                if (challenges) save.CompleteChallenges();
                if (activities) save.CompleteActivities();
                if (xp is { } x && x != save.TotalXp) save.TotalXp = x;
                foreach (var (offset, value) in tokens) save.SetCount(offset, value);
                save.Save();

                if (prefs?.Root != null)
                {
                    var before = (byte[])prefs.Dat.Data.Clone();
                    foreach (var (key, value) in mouse)
                        prefs.SetFloat(prefs.Mouse(key.StartsWith("A:")), key[2..], value);
                    foreach (var (key, on) in mouseToggles)
                        prefs.SetBool(prefs.Mouse(key.StartsWith("A:")), key[2..], on);
                    if (ngp) prefs.SetBool(prefs.Root, "NewGamePlusUnlocked", true);
                    if (!before.AsSpan().SequenceEqual(prefs.Dat.Data)) prefs.Save();
                }

                foreach (var (key, on) in regToggles) WriteRegistry(key, on ? 1 : 0);
                foreach (var (key, value) in regSliders) WriteRegistry(key, value);
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

    private static void WriteRegistry(string key, int value)
    {
        var parts = key.Split('|');
        if (SpmRegistry.Get(parts[0], parts[1]) != value) SpmRegistry.Set(parts[0], parts[1], value);
    }

    private async Task ReloadKeepingSlotAsync()
    {
        LoadRegistry();
        var keep = SelectedSlot?.FilePath;
        Slots.Clear();
        if (SelectedProfile is { } profile)
            foreach (var s in SpmLocator.FindSlots(profile)) Slots.Add(s);
        _selectedSlot = Slots.FirstOrDefault(s => s.FilePath == keep) ?? Slots.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedSlot));
        LoadBackups();
        await LoadSaveAsync();
    }

    private async Task RestoreAsync(BackupEntry backup)
    {
        if (SelectedProfile is not { } profile) return;
        var answer = MessageBox.Show(Loc.F("Msg_Restore", backup.Display), Loc.Get("Msg_RestoreTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => SpmBackups.Restore(backup, profile));
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

    public string BackupHint => BackupPolicy.Hint(BackupGameId);

    private bool ShouldBackupNow() =>
        SelectedProfile is { } p && BackupPolicy.ShouldBackup(BackupGameId, SpmBackups.List(p).FirstOrDefault()?.Created);

    public void Dispose() => _timer.Stop();
}
