using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Layts.Core;
using Layts.Games.Ets2;
using Layts.ViewModels;

namespace Layts.Games.Swyf;

public sealed class SwyfProductViewModel : ObservableObject
{
    private bool _isOwned;

    public SwyfProductViewModel(SwyfProduct product) => Product = product;

    public SwyfProduct Product { get; }
    public string Title => Product.Title;
    public string Description => Product.Description;
    public bool IsPermanent => Product.IsPermanent;
    public string PriceText => Product.Price > 0 ? "$" + Product.Price.ToString("N0", Loc.Culture) : Loc.Get("Sw_Free");
    public string UnlockText => Loc.F("Sw_FromDayX", Product.UnlockDay);
    public string KindText => Product.IsPermanent ? Loc.Get("Sw_Permanent") : Loc.Get("Sw_ThisRun");

    public bool IsOwned
    {
        get => _isOwned;
        set => Set(ref _isOwned, value);
    }
}

public sealed class SwyfEmoteViewModel : ObservableObject
{
    private bool _isOn;

    public SwyfEmoteViewModel(SwyfEmote emote) => Emote = emote;

    public SwyfEmote Emote { get; }
    public string Name => Emote.Name;

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }
}

public sealed class SwyfPlayerViewModel : ObservableObject
{
    private string _moneyText = "";

    public SwyfPlayerViewModel(long steamId, string label, bool isYou)
    {
        SteamId = steamId;
        Label = label;
        IsYou = isYou;
        AddCommand = new RelayCommand(p =>
        {
            if (!long.TryParse(p as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount)) return;
            SwyfViewModel.TryParse(MoneyText, out var current);
            MoneyText = SwyfViewModel.Format(Math.Clamp(current + amount, 0, 1_000_000_000_000));
        });
    }

    public long SteamId { get; }
    public string Label { get; }
    public bool IsYou { get; }
    public string SteamIdText => "SteamID " + SteamId.ToString(CultureInfo.InvariantCulture);
    public ICommand AddCommand { get; }

    public string MoneyText
    {
        get => _moneyText;
        set
        {
            if (Set(ref _moneyText, value)) OnPropertyChanged(nameof(MoneyError));
        }
    }

    public string MoneyError => SwyfViewModel.TryParse(MoneyText, out var v) && v >= 0 ? "" : Loc.Get("Err_Money");
}

public sealed class SwyfViewModel : ObservableObject, IDisposable
{
    // Setting keys in local.json
    private const string KFov = "Camera FOV", KFps = "Max FPS", KSens = "Mouse Sensitivity";
    private const string KMaster = "Master Volume", KMusic = "Music Volume", KBoom = "Explosions Volume";
    private const string KSprint = "Toggle Sprint", KHideUi = "Disable Game UI", KHideKeys = "Disable Keys UI";

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private readonly List<SwyfProduct> _catalog;
    private readonly long? _ownSteamId;
    private SwyfSave? _save;
    private SwyfMetaFile? _settings;
    private int _loadToken;

    private SwyfSlot? _selectedSlot;
    private bool _isLoaded, _isBusy, _isGameRunning, _justSaved, _hasRun, _hasSettings, _curry;
    private string _statusText = "";
    private StatusKind _statusKind;
    private string _dayText = "", _startQuotaText = "", _extraQuotaText = "", _quotaIncreaseText = "", _dayLengthText = "";
    private double _fov = 69, _master = 100, _music = 100, _boom = 100;
    private string _fpsText = "", _sensText = "";
    private bool _sprint, _hideUi, _hideKeys;
    private string _dayStat = "–", _playerStat = "–", _scamStat = "–", _appStat = "–", _emoteStat = "–", _playStat = "–", _achStat = "–";

    public SwyfViewModel(MainViewModel main)
    {
        _main = main;
        _catalog = SwyfCatalog.Load();
        _ownSteamId = SwyfLocator.FindOwnSteamId();

        foreach (var p in _catalog)
        {
            var vm = new SwyfProductViewModel(p);
            if (p.IsScam) Scams.Add(vm);
            else Apps.Add(vm);
        }

        BackCommand = new RelayCommand(() => _main.ShowLibrary());
        RefreshCommand = new RelayCommand(LoadSlots, () => !IsBusy);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => IsLoaded && !IsBusy);
        UnlockAllScamsCommand = new RelayCommand(() => SetAll(Scams, true));
        LockAllScamsCommand = new RelayCommand(() => SetAll(Scams, false));
        UnlockAllAppsCommand = new RelayCommand(() => SetAll(Apps, true));
        LockAllAppsCommand = new RelayCommand(() => SetAll(Apps, false));
        UnlockAllEmotesCommand = new RelayCommand(() =>
        {
            foreach (var e in Emotes) e.IsOn = true;
        });
        EasyQuotaCommand = new RelayCommand(() =>
        {
            StartQuotaText = Format(100);
            ExtraQuotaText = "0";
            QuotaIncreaseText = "0";
        });
        DefaultQuotaCommand = new RelayCommand(() =>
        {
            StartQuotaText = Format(600);
            ExtraQuotaText = Format(360);
            QuotaIncreaseText = "15";
        });
        SetDayLengthCommand = new RelayCommand(p =>
        {
            if (p is string s) DayLengthText = s;
        });
        SetFpsCommand = new RelayCommand(p =>
        {
            if (p is string s) FpsText = s;
        });
        OpenSaveFolderCommand = new RelayCommand(() => OpenFolder(SwyfLocator.MetaSaveDirectory));
        OpenBackupFolderCommand = new RelayCommand(() => OpenFolder(SwyfBackups.Root));
        RestoreBackupCommand = new RelayCommand(async p =>
        {
            if (p is BackupEntry b) await RestoreAsync(b);
        }, _ => !IsBusy);

        _isGameRunning = SwyfLocator.IsGameRunning();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => IsGameRunning = SwyfLocator.IsGameRunning();
        _timer.Start();

        LoadSlots();
    }

    // ---------- Images ----------

    public System.Windows.Media.ImageSource? HeroImage { get; } = SteamArt.Load(SwyfLocator.SteamAppId, SteamArt.Hero, 1920);
    public System.Windows.Media.ImageSource? LogoImage { get; } = SteamArt.Load(SwyfLocator.SteamAppId, SteamArt.Logo, 640);

    // ---------- Collections & commands ----------

    public ObservableCollection<SwyfSlot> Slots { get; } = new();
    public ObservableCollection<SwyfPlayerViewModel> Players { get; } = new();
    public ObservableCollection<SwyfProductViewModel> Scams { get; } = new();
    public ObservableCollection<SwyfProductViewModel> Apps { get; } = new();
    public ObservableCollection<SwyfEmoteViewModel> Emotes { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();

    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand UnlockAllScamsCommand { get; }
    public ICommand LockAllScamsCommand { get; }
    public ICommand UnlockAllAppsCommand { get; }
    public ICommand LockAllAppsCommand { get; }
    public ICommand UnlockAllEmotesCommand { get; }
    public ICommand EasyQuotaCommand { get; }
    public ICommand DefaultQuotaCommand { get; }
    public ICommand SetDayLengthCommand { get; }
    public ICommand SetFpsCommand { get; }
    public ICommand OpenSaveFolderCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand RestoreBackupCommand { get; }

    // ---------- State ----------

    public bool HasSlots => Slots.Count > 0;
    public string CatalogSource => SwyfCatalog.LoadedFromGame ? Loc.Get("Sw_CatalogGame") : Loc.Get("Sw_CatalogBuiltIn");

    public SwyfSlot? SelectedSlot
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
    public bool HasRun { get => _hasRun; private set => Set(ref _hasRun, value); }
    public bool HasSettings { get => _hasSettings; private set => Set(ref _hasSettings, value); }
    public bool HasEmotes => Emotes.Count > 0;
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    // ---------- Run ----------

    public string DayText { get => _dayText; set => Set(ref _dayText, value); }
    public string StartQuotaText { get => _startQuotaText; set => Set(ref _startQuotaText, value); }
    public string ExtraQuotaText { get => _extraQuotaText; set => Set(ref _extraQuotaText, value); }
    public string QuotaIncreaseText { get => _quotaIncreaseText; set => Set(ref _quotaIncreaseText, value); }
    public string DayLengthText { get => _dayLengthText; set => Set(ref _dayLengthText, value); }

    // ---------- Cosmetics ----------

    public bool CurrySuit { get => _curry; set => Set(ref _curry, value); }

    // ---------- Game settings ----------

    public double Fov { get => _fov; set => Set(ref _fov, Math.Round(value)); }
    public string FpsText { get => _fpsText; set => Set(ref _fpsText, value); }
    public string SensitivityText { get => _sensText; set => Set(ref _sensText, value); }
    public double MasterVolume { get => _master; set => Set(ref _master, Math.Round(value)); }
    public double MusicVolume { get => _music; set => Set(ref _music, Math.Round(value)); }
    public double ExplosionsVolume { get => _boom; set => Set(ref _boom, Math.Round(value)); }
    public bool ToggleSprint { get => _sprint; set => Set(ref _sprint, value); }
    public bool HideGameUi { get => _hideUi; set => Set(ref _hideUi, value); }
    public bool HideKeysUi { get => _hideKeys; set => Set(ref _hideKeys, value); }

    // ---------- Overview ----------

    public string DayStat { get => _dayStat; private set => Set(ref _dayStat, value); }
    public string PlayerStat { get => _playerStat; private set => Set(ref _playerStat, value); }
    public string ScamStat { get => _scamStat; private set => Set(ref _scamStat, value); }
    public string AppStat { get => _appStat; private set => Set(ref _appStat, value); }
    public string EmoteStat { get => _emoteStat; private set => Set(ref _emoteStat, value); }
    public string PlayStat { get => _playStat; private set => Set(ref _playStat, value); }
    public string AchievementStat { get => _achStat; private set => Set(ref _achStat, value); }

    // ---------- Loading ----------

    private void LoadSlots()
    {
        var keep = SelectedSlot?.FilePath;
        Slots.Clear();
        foreach (var s in SwyfLocator.FindSlots()) Slots.Add(s);
        OnPropertyChanged(nameof(HasSlots));

        var target = Slots.FirstOrDefault(s => s.FilePath == keep)
                     ?? Slots.Where(s => !s.IsEmpty).OrderByDescending(s => s.Updated).FirstOrDefault()
                     ?? Slots.FirstOrDefault();
        if (ReferenceEquals(target, _selectedSlot)) _ = LoadSaveAsync();
        else SelectedSlot = target;

        if (!HasSlots) ShowStatus(StatusKind.Info, Loc.Get("Sw_Err_NoSaves"));
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
            var (save, settings, achievements) = await Task.Run(() =>
            {
                var s = SwyfSave.Load(slot.FilePath);
                SwyfMetaFile? local = File.Exists(SwyfLocator.LocalSettingsPath) ? SwyfMetaFile.Load(SwyfLocator.LocalSettingsPath) : null;
                int ach = CountAchievements();
                return (s, local, ach);
            });
            if (token != _loadToken) return;

            _save = save;
            _settings = settings;
            Apply(save, settings, achievements);
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

    private void Apply(SwyfSave save, SwyfMetaFile? settings, int achievements)
    {
        // Run
        var run = save.Run;
        HasRun = run != null;
        Players.Clear();
        var owned = new HashSet<string>();
        if (run != null)
        {
            DayText = run.ResumeDay.ToString(CultureInfo.InvariantCulture);
            StartQuotaText = Format((long)(run.GetDayValue("StartingQuota") ?? 600));
            ExtraQuotaText = Format((long)(run.GetDayValue("ExtraPlayerQuota") ?? 360));
            QuotaIncreaseText = (run.GetDayValue("QuotaIncreasePercent") ?? 15).ToString("0.##", Loc.Culture);
            DayLengthText = ((run.GetDayValue("DayLengthSeconds") ?? 900) / 60).ToString("0.##", Loc.Culture);

            int n = 1;
            foreach (var p in run.Players)
            {
                bool you = _ownSteamId == p.SteamId || (_ownSteamId == null && n == 1);
                var label = you ? Loc.Get("Sw_You") : Loc.F("Sw_PlayerX", n);
                Players.Add(new SwyfPlayerViewModel(p.SteamId, label, you) { MoneyText = Format(p.Money) });
                owned.UnionWith(p.Entitlements);
                n++;
            }
        }

        foreach (var p in Scams.Concat(Apps))
            p.IsOwned = p.IsPermanent ? save.OwnsPermanent(p.Product.Id) : owned.Contains(p.Product.Id);

        // Cosmetics
        Emotes.Clear();
        foreach (var e in save.Emotes) Emotes.Add(new SwyfEmoteViewModel(e) { IsOn = save.IsOn(e.Key) });
        OnPropertyChanged(nameof(HasEmotes));
        CurrySuit = save.IsOn(SwyfSave.CurryKey);

        // Settings
        HasSettings = settings != null;
        if (settings != null)
        {
            Fov = settings.GetDouble(KFov) ?? 69;
            FpsText = (settings.GetLong(KFps) ?? -1).ToString(CultureInfo.InvariantCulture);
            SensitivityText = (settings.GetDouble(KSens) ?? 100).ToString("0.##", Loc.Culture);
            MasterVolume = settings.GetDouble(KMaster) ?? 100;
            MusicVolume = settings.GetDouble(KMusic) ?? 100;
            ExplosionsVolume = settings.GetDouble(KBoom) ?? 100;
            ToggleSprint = settings.GetString(KSprint) == "On";
            HideGameUi = settings.GetString(KHideUi) == "On";
            HideKeysUi = settings.GetString(KHideKeys) == "On";
        }

        // Overview
        DayStat = run != null ? Loc.F("Sw_DayX", run.ResumeDay) : Loc.Get("Sw_NoRun");
        PlayerStat = Players.Count.ToString(Loc.Culture);
        ScamStat = $"{Scams.Count(s => s.IsOwned)} / {Scams.Count}";
        AppStat = $"{Apps.Count(s => s.IsOwned)} / {Apps.Count}";
        EmoteStat = $"{Emotes.Count(e => e.IsOn)} / {Emotes.Count}";
        PlayStat = Loc.F("Sw_Hours", (save.PlaytimeSeconds / 3600.0).ToString("0.0", Loc.Culture));
        AchievementStat = achievements.ToString(Loc.Culture);
    }

    private static int CountAchievements()
    {
        try
        {
            if (!File.Exists(SwyfLocator.PersistentPath)) return 0;
            var f = SwyfMetaFile.Load(SwyfLocator.PersistentPath);
            return f.Values.Count(kv => Regex.IsMatch(kv.Key, @"^SteamAchievements\.\d+\.earned\.") && SwyfJson.Long(kv.Value) == 1);
        }
        catch
        {
            return 0;
        }
    }

    private void LoadBackups()
    {
        Backups.Clear();
        foreach (var b in SwyfBackups.List()) Backups.Add(b);
    }

    // ---------- Saving ----------

    private async Task SaveAsync()
    {
        var save = _save;
        if (save == null) return;

        // Validate run values
        int day = 0;
        long startQuota = 0, extraQuota = 0;
        double increase = 0, dayMinutes = 0;
        var money = new Dictionary<long, long>();
        if (save.Run != null)
        {
            if (!int.TryParse(DayText.Trim(), out day) || day < 1 || day > 9999)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sw_Err_Day"));
                return;
            }
            if (!TryParse(StartQuotaText, out startQuota) || startQuota < 0 || !TryParse(ExtraQuotaText, out extraQuota) || extraQuota < 0)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sw_Err_Quota"));
                return;
            }
            if (!TryParseDouble(QuotaIncreaseText, out increase) || increase < 0 || increase > 1000)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sw_Err_Increase"));
                return;
            }
            if (!TryParseDouble(DayLengthText, out dayMinutes) || dayMinutes < 1 || dayMinutes > 600)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sw_Err_DayLength"));
                return;
            }
            foreach (var p in Players)
            {
                if (!TryParse(p.MoneyText, out var m) || m < 0)
                {
                    ShowStatus(StatusKind.Error, Loc.Get("Err_MoneyInvalid"));
                    return;
                }
                money[p.SteamId] = m;
            }
        }

        long fps = 0;
        double sens = 0;
        if (HasSettings)
        {
            if (!long.TryParse(FpsText.Trim(), out fps) || fps < -1 || fps > 1000 || fps == 0)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sw_Err_Fps"));
                return;
            }
            if (!TryParseDouble(SensitivityText, out sens) || sens <= 0 || sens > 1000)
            {
                ShowStatus(StatusKind.Error, Loc.Get("Sw_Err_Sens"));
                return;
            }
        }

        if (SwyfLocator.IsGameRunning())
        {
            var answer = MessageBox.Show(Loc.Get("Sw_Msg_GameRunning"), Loc.Get("Msg_GameRunningTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var runAdd = Scams.Concat(Apps).Where(p => !p.IsPermanent && p.IsOwned).Select(p => p.Product.Id).ToList();
        var runRemove = Scams.Concat(Apps).Where(p => !p.IsPermanent && !p.IsOwned).Select(p => p.Product.Id).ToList();
        var permanent = Scams.Concat(Apps).Where(p => p.IsPermanent).ToDictionary(p => p.Product.Id, p => p.IsOwned);
        var emotes = Emotes.ToDictionary(e => e.Emote.Key, e => e.IsOn);
        bool curry = CurrySuit, hadCurry = save.File.Has(SwyfSave.CurryKey);
        var settings = _settings;
        double fov = Fov, master = MasterVolume, music = MusicVolume, boom = ExplosionsVolume;
        bool sprint = ToggleSprint, hideUi = HideGameUi, hideKeys = HideKeysUi;

        IsBusy = true;
        ClearStatus();
        try
        {
            var summary = await Task.Run(() =>
            {
                SwyfBackups.Create();
                var lines = new List<string>();

                var run = save.Run;
                if (run != null)
                {
                    foreach (var p in run.Players)
                        if (money.TryGetValue(p.SteamId, out var m)) p.Money = m;
                    foreach (var p in run.Players) p.SetEntitlements(runAdd, runRemove);

                    run.SetDayValue("StartingQuota", JsonValue.Create(startQuota));
                    run.SetDayValue("ExtraPlayerQuota", JsonValue.Create(extraQuota));
                    run.SetDayValue("QuotaIncreasePercent", JsonValue.Create(increase));
                    run.SetDayValue("DayLengthSeconds", JsonValue.Create(Math.Round(dayMinutes * 60)));
                    if (day != run.ResumeDay)
                    {
                        run.SetDay(day);
                        lines.Add(Loc.F("Sw_Sum_Day", day));
                    }
                }

                foreach (var (id, owned) in permanent) save.SetPermanent(id, owned);
                foreach (var (key, on) in emotes) save.SetOn(key, on);
                if (curry || hadCurry) save.SetOn(SwyfSave.CurryKey, curry);
                save.Write();

                if (settings != null)
                {
                    bool changed = false;
                    void SetNum(string key, double value)
                    {
                        if (settings.GetDouble(key) is { } old && Math.Abs(old - value) < 0.0001) return;
                        if (value == Math.Floor(value)) settings.Set(key, (long)value);
                        else settings.Set(key, value);
                        changed = true;
                    }
                    void SetSwitch(string key, bool on)
                    {
                        var text = on ? "On" : "Off";
                        if (settings.GetString(key) == text) return;
                        settings.Set(key, text);
                        changed = true;
                    }

                    SetNum(KFov, fov);
                    SetNum(KFps, fps);
                    SetNum(KSens, sens);
                    SetNum(KMaster, master);
                    SetNum(KMusic, music);
                    SetNum(KBoom, boom);
                    SetSwitch(KSprint, sprint);
                    SetSwitch(KHideUi, hideUi);
                    SetSwitch(KHideKeys, hideKeys);
                    if (changed)
                    {
                        settings.Write();
                        lines.Add(Loc.Get("Sw_Sum_Settings"));
                    }
                }
                return lines;
            });

            await ReloadKeepingSlotAsync();
            var details = summary.Count > 0 ? " " + string.Join(" · ", summary) + "." : "";
            ShowStatus(StatusKind.Success, Loc.F("Msg_Saved", details));
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
        foreach (var s in SwyfLocator.FindSlots()) Slots.Add(s);
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
            await Task.Run(() => SwyfBackups.Restore(backup));
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

    private static void SetAll(IEnumerable<SwyfProductViewModel> items, bool on)
    {
        foreach (var i in items) i.IsOwned = on;
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

    internal static string Format(long value) => value.ToString("N0", Loc.Culture);

    internal static bool TryParse(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = new string(text.Where(c => char.IsDigit(c) || c == '-').ToArray());
        return long.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

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

    public void Dispose() => _timer.Stop();
}
