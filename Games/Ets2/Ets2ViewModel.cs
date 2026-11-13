using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Layts.Core;
using Layts.ViewModels;

namespace Layts.Games.Ets2;

public sealed class SkillViewModel : ObservableObject
{
    private int _value;

    public SkillViewModel(string key, string label, string description, int max = 6)
    {
        Key = key;
        Label = label;
        Description = description;
        Max = max;
    }

    public string Key { get; }
    public string Label { get; }
    public string Description { get; }

    /// <summary>Höchste Stufe laut Spieldaten (LKW-Skills 6, Bus-Skills 3).</summary>
    public int Max { get; }
    public string MaxText => $" / {Max}";
    public bool IsAvailable { get; set; } = true;

    public int Value
    {
        get => _value;
        set => Set(ref _value, Math.Clamp(value, 0, Max));
    }
}

public sealed class AdrClassViewModel : ObservableObject
{
    private bool _isOn;

    public AdrClassViewModel(int bit, string label)
    {
        Bit = bit;
        Label = label;
    }

    public int Bit { get; }
    public string Label { get; }

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }
}

public enum StatusKind { None, Info, Success, Error }

public sealed class Ets2ViewModel : ObservableObject, IDisposable
{
    private static CultureInfo German => Loc.Culture;

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private Ets2Save? _save;
    private int _loadToken;
    private string? _preferredSave;

    private Ets2Profile? _selectedProfile;
    private Ets2SaveSlot? _selectedSave;
    private bool _isLoaded, _isBusy, _isGameRunning;
    private string _statusText = "";
    private StatusKind _statusKind;
    private string _moneyText = "", _xpText = "", _levelInput = "";
    private bool _repairVehicles, _refuelTrucks, _visitCities, _upgradeGarages, _buyAllGarages;
    private string _formatLabel = "";
    private string _truckStat = "–", _trailerStat = "–", _driverStat = "–", _garageStat = "–", _cityStat = "–";

    public Ets2ViewModel(MainViewModel main)
    {
        _main = main;

        // LKW-Skills: Feldname im Spielstand → Übersetzung „Sk_<feld>“ (Name) und „Sk_<feld>_d“ (Beschreibung)
        foreach (var key in new[] { "long_dist", "heavy", "fragile", "urgent", "mechanical" })
            Skills.Add(new SkillViewModel(key, Loc.Get("Sk_" + key), Loc.Get("Sk_" + key + "_d")));

        // Coaches-DLC: laut def.scs (skills.data) haben alle Bus-Skills max_rank 3.
        // Das Bus-Öko-Fahren liegt im Spielstand im Feld „ecodriving“ (das LKW-Öko-Fahren in „mechanical“).
        foreach (var key in new[] { "long_duty", "tight_schedule", "satisfaction_decrease", "low_acceleration", "satisfaction_increase", "ecodriving" })
            BusSkills.Add(new SkillViewModel(key, Loc.Get("Bs_" + key), Loc.Get("Bs_" + key + "_d"), 3));

        string[] adr =
        {
            Loc.Get("Adr1"), Loc.Get("Adr2"), Loc.Get("Adr3"),
            Loc.Get("Adr4"), Loc.Get("Adr6"), Loc.Get("Adr8"),
        };
        for (int i = 0; i < adr.Length; i++) AdrClasses.Add(new AdrClassViewModel(i, adr[i]));

        BackCommand = new RelayCommand(() => _main.ShowLibrary());
        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => IsLoaded && !IsBusy);
        AddMoneyCommand = new RelayCommand(p => MoneyText = Add(MoneyText, p, long.MaxValue));
        AddXpCommand = new RelayCommand(p => XpText = Add(XpText, p, uint.MaxValue));
        AddBusXpCommand = new RelayCommand(p => BusXpText = Add(BusXpText, p, uint.MaxValue));
        SetLevelCommand = new RelayCommand(SetLevel);
        MaxSkillsCommand = new RelayCommand(MaxSkills);
        MaxBusSkillsCommand = new RelayCommand(() =>
        {
            foreach (var s in BusSkills) s.Value = s.Max;
        });
        SetBusLevelCommand = new RelayCommand(SetBusLevel);
        OpenSaveFolderCommand = new RelayCommand(() => OpenFolder(SelectedSave?.FolderPath), () => SelectedSave != null);
        OpenBackupFolderCommand = new RelayCommand(
            () => OpenFolder(SelectedSave == null ? Ets2Backups.Root : Ets2Backups.DirectoryFor(SelectedSave)));
        RestoreBackupCommand = new RelayCommand(async p =>
        {
            if (p is BackupEntry b) await RestoreAsync(b);
        }, _ => !IsBusy);

        _isGameRunning = Ets2Locator.IsGameRunning();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => IsGameRunning = Ets2Locator.IsGameRunning();
        _timer.Start();

        LoadProfiles(null, null);
    }

    // ---------- Bilder (aus dem lokalen Steam-Cache) ----------

    public System.Windows.Media.ImageSource? HeroImage { get; } =
        SteamArt.Load(Ets2Locator.SteamAppId, SteamArt.Hero, 1920);

    public System.Windows.Media.ImageSource? LogoImage { get; } =
        SteamArt.Load(Ets2Locator.SteamAppId, SteamArt.Logo, 640);

    // ---------- Sammlungen & Befehle ----------

    public ObservableCollection<Ets2Profile> Profiles { get; } = new();
    public ObservableCollection<Ets2SaveSlot> Saves { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();
    public ObservableCollection<SkillViewModel> Skills { get; } = new();
    public ObservableCollection<AdrClassViewModel> AdrClasses { get; } = new();
    public ObservableCollection<SkillViewModel> BusSkills { get; } = new();

    public ICommand AddBusXpCommand { get; }
    public ICommand MaxBusSkillsCommand { get; }
    public ICommand SetBusLevelCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddMoneyCommand { get; }
    public ICommand AddXpCommand { get; }
    public ICommand SetLevelCommand { get; }
    public ICommand MaxSkillsCommand { get; }
    public ICommand OpenSaveFolderCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand RestoreBackupCommand { get; }

    // ---------- Auswahl ----------

    public bool HasProfiles => Profiles.Count > 0;

    public Ets2Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            OnPropertyChanged(nameof(IsCloudProfile));
            LoadSaves();
        }
    }

    public bool IsCloudProfile => SelectedProfile?.IsSteamCloud == true;

    public Ets2SaveSlot? SelectedSave
    {
        get => _selectedSave;
        set
        {
            if (Set(ref _selectedSave, value)) _ = LoadSaveAsync();
        }
    }

    // ---------- Zustand ----------

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set => Set(ref _isGameRunning, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public StatusKind StatusKind
    {
        get => _statusKind;
        private set => Set(ref _statusKind, value);
    }

    private bool _justSaved;

    /// <summary>Kurz nach erfolgreichem Speichern true – für das grüne Häkchen am Button.</summary>
    public bool JustSaved
    {
        get => _justSaved;
        private set => Set(ref _justSaved, value);
    }

    private async Task FlashSavedAsync()
    {
        JustSaved = true;
        await Task.Delay(2600);
        JustSaved = false;
    }

    public string FormatLabel
    {
        get => _formatLabel;
        private set => Set(ref _formatLabel, value);
    }

    // ---------- Finanzen & Erfahrung ----------

    public string MoneyText
    {
        get => _moneyText;
        set
        {
            if (Set(ref _moneyText, value)) OnPropertyChanged(nameof(MoneyError));
        }
    }

    public string MoneyError => TryParseNumber(MoneyText, out _) ? "" : Loc.Get("Err_Money");

    public string XpText
    {
        get => _xpText;
        set
        {
            if (!Set(ref _xpText, value)) return;
            OnPropertyChanged(nameof(XpError));
            OnPropertyChanged(nameof(LevelDisplay));
        }
    }

    public string XpError => TryParseNumber(XpText, out var xp) && xp >= 0 && xp <= uint.MaxValue
        ? ""
        : Loc.Get("Err_Xp");

    public string LevelDisplay => TryParseNumber(XpText, out var xp) && xp >= 0
        ? Loc.F("LevelX", Ets2Levels.LevelForXp(xp))
        : Loc.Get("LevelNone");

    public string LevelInput
    {
        get => _levelInput;
        set => Set(ref _levelInput, value);
    }

    // ---------- Optionen ----------

    public bool RepairVehicles { get => _repairVehicles; set => Set(ref _repairVehicles, value); }
    public bool RefuelTrucks { get => _refuelTrucks; set => Set(ref _refuelTrucks, value); }
    public bool VisitCities { get => _visitCities; set => Set(ref _visitCities, value); }
    public bool UpgradeGarages { get => _upgradeGarages; set => Set(ref _upgradeGarages, value); }
    public bool BuyAllGarages { get => _buyAllGarages; set => Set(ref _buyAllGarages, value); }

    public string TruckStat { get => _truckStat; private set => Set(ref _truckStat, value); }
    public string TrailerStat { get => _trailerStat; private set => Set(ref _trailerStat, value); }
    public string DriverStat { get => _driverStat; private set => Set(ref _driverStat, value); }
    public string GarageStat { get => _garageStat; private set => Set(ref _garageStat, value); }
    public string CityStat { get => _cityStat; private set => Set(ref _cityStat, value); }

    // ---------- Coaches-DLC ----------

    private bool _showCoaches, _discoverBusStops;
    private string _busXpText = "", _busLevelInput = "", _busStat = "–", _busTripStat = "–", _busStopStat = "–";

    public string BusLevelInput
    {
        get => _busLevelInput;
        set => Set(ref _busLevelInput, value);
    }

    public string BusLevelDisplay => TryParseNumber(BusXpText, out var xp) && xp >= 0
        ? Loc.F("BusLevelX", Ets2Levels.LevelForXp(xp))
        : Loc.Get("BusLevelNone");

    /// <summary>Bus-Bereich nur zeigen, wenn der Spielstand das Coaches-DLC nutzt.</summary>
    public bool ShowCoaches { get => _showCoaches; private set => Set(ref _showCoaches, value); }

    public string BusXpText
    {
        get => _busXpText;
        set
        {
            if (!Set(ref _busXpText, value)) return;
            OnPropertyChanged(nameof(BusXpError));
            OnPropertyChanged(nameof(BusLevelDisplay));
        }
    }

    public string BusXpError => !ShowCoaches || (TryParseNumber(BusXpText, out var xp) && xp >= 0 && xp <= uint.MaxValue)
        ? ""
        : Loc.Get("Err_Xp");

    public bool DiscoverBusStops { get => _discoverBusStops; set => Set(ref _discoverBusStops, value); }
    public string BusStat { get => _busStat; private set => Set(ref _busStat, value); }
    public string BusTripStat { get => _busTripStat; private set => Set(ref _busTripStat, value); }
    public string BusStopStat { get => _busStopStat; private set => Set(ref _busStopStat, value); }

    // ---------- Laden ----------

    private void LoadProfiles(string? keepProfile, string? keepSave)
    {
        _preferredSave = keepSave;
        Profiles.Clear();
        foreach (var p in Ets2Locator.FindProfiles()) Profiles.Add(p);
        OnPropertyChanged(nameof(HasProfiles));

        var target = Profiles.FirstOrDefault(p => p.FolderPath == keepProfile) ?? Profiles.FirstOrDefault();
        if (ReferenceEquals(target, _selectedProfile)) LoadSaves();
        else SelectedProfile = target;

        if (!HasProfiles)
            ShowStatus(StatusKind.Info,
                Loc.Get("Err_NoProfiles"));
    }

    private void LoadSaves()
    {
        var keep = _preferredSave;
        _preferredSave = null;

        Saves.Clear();
        if (SelectedProfile != null)
            foreach (var s in Ets2Locator.FindSaves(SelectedProfile)) Saves.Add(s);

        var target = Saves.FirstOrDefault(s => s.FolderPath == keep) ?? Saves.FirstOrDefault();
        if (ReferenceEquals(target, _selectedSave)) _ = LoadSaveAsync();
        else SelectedSave = target;
    }

    private void Refresh() => LoadProfiles(SelectedProfile?.FolderPath, SelectedSave?.FolderPath);

    private async Task LoadSaveAsync()
    {
        var slot = SelectedSave;
        int token = ++_loadToken;

        _save = null;
        IsLoaded = false;
        Backups.Clear();
        if (slot == null) return;

        IsBusy = true;
        ClearStatus();
        try
        {
            var save = await Task.Run(() => Ets2Save.Load(slot.GamePath));
            if (token != _loadToken) return;

            _save = save;
            ApplyFromSave(save);
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

    private void ApplyFromSave(Ets2Save save)
    {
        MoneyText = Format(save.Money);
        XpText = Format(save.Experience);
        LevelInput = Ets2Levels.LevelForXp(save.Experience).ToString(CultureInfo.InvariantCulture);

        foreach (var skill in Skills)
        {
            skill.IsAvailable = save.HasSkill(skill.Key);
            skill.Value = save.GetSkill(skill.Key);
        }
        int adr = save.GetSkill("adr");
        foreach (var a in AdrClasses) a.IsOn = (adr & (1 << a.Bit)) != 0;

        RepairVehicles = RefuelTrucks = VisitCities = UpgradeGarages = BuyAllGarages = false;

        TruckStat = save.TruckCount.ToString(German);
        TrailerStat = save.TrailerCount.ToString(German);
        DriverStat = save.DriverCount.ToString(German);
        GarageStat = $"{save.OwnedGarageCount} / {save.GarageCount}";
        CityStat = $"{save.VisitedCityCount} / {save.KnownCityCount}";
        FormatLabel = save.FormatLabel;

        ShowCoaches = save.HasCoachesDlc;
        DiscoverBusStops = false;
        if (save.HasCoachesDlc)
        {
            BusXpText = Format(save.BusExperience);
            BusLevelInput = Ets2Levels.LevelForXp(save.BusExperience).ToString(CultureInfo.InvariantCulture);
            foreach (var skill in BusSkills)
            {
                skill.IsAvailable = save.HasSkill(skill.Key);
                skill.Value = save.GetSkill(skill.Key);
            }
            BusStat = save.BusCount.ToString(German);
            BusTripStat = save.BusTripCount.ToString(German);
            BusStopStat = $"{save.DiscoveredBusStopCount} / {save.BusStopCount}";
        }
        else
        {
            BusXpText = "";
        }
        OnPropertyChanged(nameof(BusXpError));
    }

    private void LoadBackups()
    {
        Backups.Clear();
        if (SelectedSave == null) return;
        foreach (var b in Ets2Backups.List(SelectedSave)) Backups.Add(b);
    }

    // ---------- Bearbeiten ----------

    /// <summary>Addiert den Button-Betrag (CommandParameter) zum Feldwert und begrenzt auf <paramref name="max"/>.</summary>
    private static string Add(string currentText, object? parameter, long max)
    {
        if (!long.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var amount))
            return currentText;

        TryParseNumber(currentText, out var current);
        long result;
        try { result = checked(current + amount); }
        catch (OverflowException) { result = long.MaxValue; }

        return Format(Math.Min(result, max));
    }

    private void SetLevel()
    {
        if (!int.TryParse(LevelInput.Trim(), out var level) || level < 0 || level > 100_000)
        {
            ShowStatus(StatusKind.Error, Loc.Get("Err_Level"));
            return;
        }
        XpText = Format(Math.Min(Ets2Levels.XpForLevel(level), uint.MaxValue));
        ClearStatus();
    }

    private void SetBusLevel()
    {
        if (!int.TryParse(BusLevelInput.Trim(), out var level) || level < 0 || level > 100_000)
        {
            ShowStatus(StatusKind.Error, Loc.Get("Err_BusLevel"));
            return;
        }
        BusXpText = Format(Math.Min(Ets2Levels.XpForLevel(level), uint.MaxValue));
        ClearStatus();
    }

    private void MaxSkills()
    {
        foreach (var s in Skills) s.Value = 6;
        foreach (var a in AdrClasses) a.IsOn = true;
    }

    private async Task SaveAsync()
    {
        var save = _save;
        var slot = SelectedSave;
        if (save == null || slot == null) return;

        if (!TryParseNumber(MoneyText, out var money))
        {
            ShowStatus(StatusKind.Error, Loc.Get("Err_MoneyInvalid"));
            return;
        }
        if (!TryParseNumber(XpText, out var xp) || xp < 0 || xp > uint.MaxValue)
        {
            ShowStatus(StatusKind.Error, Loc.Get("Err_XpInvalid"));
            return;
        }

        long busXp = 0;
        bool coaches = ShowCoaches;
        if (coaches && (!TryParseNumber(BusXpText, out busXp) || busXp < 0 || busXp > uint.MaxValue))
        {
            ShowStatus(StatusKind.Error, Loc.Get("Err_BusXpInvalid"));
            return;
        }

        if (Ets2Locator.IsGameRunning())
        {
            var answer = MessageBox.Show(
                Loc.Get("Msg_GameRunning"), Loc.Get("Msg_GameRunningTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var skills = Skills.Where(s => s.IsAvailable).ToDictionary(s => s.Key, s => s.Value);
        int adr = AdrClasses.Where(a => a.IsOn).Sum(a => 1 << a.Bit);
        bool repair = RepairVehicles, refuel = RefuelTrucks, visit = VisitCities;
        bool upgrade = UpgradeGarages, buyAll = BuyAllGarages;
        var busSkills = BusSkills.Where(s => s.IsAvailable).ToDictionary(s => s.Key, s => s.Value);
        bool discoverStops = coaches && DiscoverBusStops;

        IsBusy = true;
        ClearStatus();
        try
        {
            var summary = await Task.Run(() =>
            {
                Ets2Backups.Create(slot);

                var lines = new List<string>();
                save.Money = money;
                save.Experience = xp;
                foreach (var (key, value) in skills) save.SetSkill(key, value);
                save.SetSkill("adr", adr);

                if (coaches)
                {
                    save.BusExperience = busXp;
                    foreach (var (key, value) in busSkills) save.SetSkill(key, value);
                    if (discoverStops) lines.Add(Loc.F("Sum_Stops", save.DiscoverAllBusStops()));
                }

                if (repair) lines.Add(Loc.F("Sum_Repaired", save.RepairAllVehicles()));
                if (refuel) lines.Add(Loc.F("Sum_Refueled", save.RefuelAllTrucks()));
                if (visit) lines.Add(Loc.F("Sum_Cities", save.VisitAllCities()));
                if (upgrade || buyAll)
                {
                    var (bought, upgraded) = save.UpgradeGarages(buyAll, upgrade);
                    lines.Add(Loc.F("Sum_Garages", bought, upgraded));
                }

                save.Write();
                return lines;
            });

            await LoadSaveAsync();
            var details = summary.Count > 0 ? " " + string.Join(" · ", summary) + "." : "";
            ShowStatus(StatusKind.Success,
                Loc.F("Msg_Saved", details));
            _ = FlashSavedAsync();
        }
        catch (Exception ex)
        {
            await LoadSaveAsync();
            ShowStatus(StatusKind.Error, Loc.F("Err_Save", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync(BackupEntry backup)
    {
        var slot = SelectedSave;
        if (slot == null) return;

        var answer = MessageBox.Show(
            Loc.F("Msg_Restore", backup.Display), Loc.Get("Msg_RestoreTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            await Task.Run(() => Ets2Backups.Restore(backup, slot));
            await LoadSaveAsync();
            ShowStatus(StatusKind.Success, Loc.F("Msg_Restored", backup.Display));
            _ = FlashSavedAsync();
        }
        catch (Exception ex)
        {
            ShowStatus(StatusKind.Error, Loc.F("Err_Restore", ex.Message));
        }
    }

    // ---------- Hilfen ----------

    private void ShowStatus(StatusKind kind, string text)
    {
        StatusKind = kind;
        StatusText = text;
    }

    private void ClearStatus() => ShowStatus(StatusKind.None, "");

    private static string Format(long value) => value.ToString("N0", German);

    private static bool TryParseNumber(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = new string(text.Where(c => char.IsDigit(c) || c == '-').ToArray());
        return long.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // Explorer nicht verfügbar – ignorieren.
        }
    }

    public void Dispose() => _timer.Stop();
}
