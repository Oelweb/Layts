using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Layts.Core;
using Layts.Games.Ets2;
using Layts.ViewModels;

namespace Layts.Games.Bus;

/// <summary>A numeric setting shown as a slider. <see cref="Scale"/> converts the stored value to what is shown (0–1 → 0–100 %).</summary>
public sealed class BusSlider : ObservableObject
{
    private double _value;

    public BusSlider(string key, string label, double min, double max, double scale, string unit)
    {
        Key = key;
        Label = label;
        Min = min;
        Max = max;
        Scale = scale;
        Unit = unit;
    }

    public string Key { get; }
    public string Label { get; }
    public double Min { get; }
    public double Max { get; }
    public double Scale { get; }
    public string Unit { get; }
    public string ValueText => Math.Round(Value).ToString(CultureInfo.InvariantCulture) + Unit;

    public double Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, Math.Clamp(Math.Round(value), Min, Max))) OnPropertyChanged(nameof(ValueText));
        }
    }
}

public sealed class BusToggle : ObservableObject
{
    private bool _isOn;

    public BusToggle(string key, string label, string description)
    {
        Key = key;
        Label = label;
        Description = description;
    }

    public string Key { get; }
    public string Label { get; }
    public string Description { get; }

    public bool IsOn
    {
        get => _isOn;
        set => Set(ref _isOn, value);
    }
}

public sealed class BusEmployeeViewModel : ObservableObject
{
    private double _experience, _satisfaction;

    public BusEmployeeViewModel(BusEmployee employee) => Employee = employee;

    public BusEmployee Employee { get; }
    public string Name => Employee.IsPlayer ? $"{Employee.Name} ({Loc.Get("Sw_You")})" : Employee.Name;
    public bool HasExperience => Employee.Experience != null;
    public bool HasSatisfaction => Employee.Satisfaction != null;

    public double Experience
    {
        get => _experience;
        set => Set(ref _experience, Math.Clamp(Math.Round(value), 0, 100));
    }

    public double Satisfaction
    {
        get => _satisfaction;
        set => Set(ref _satisfaction, Math.Clamp(Math.Round(value), 0, 100));
    }
}

public sealed class BusPlate : ObservableObject
{
    private string _plate;

    public BusPlate(int index, string vehicle, string plate)
    {
        Index = index;
        Vehicle = vehicle.Replace('_', ' ');
        _plate = plate;
    }

    public int Index { get; }
    public string Vehicle { get; }

    public string Plate
    {
        get => _plate;
        set => Set(ref _plate, value ?? "");
    }
}

public sealed class BusViewModel : ObservableObject, IDisposable
{
    private const string BackupGameId = "thebus";

    /// <summary>Default intervals in AggroConstants.cfg (seconds between passenger comments of one kind).</summary>
    private static readonly Dictionary<string, double> CommentDefaults = new()
    {
        ["temperature Comment Intervale"] = 300,
        ["accident Comment Intervale"] = 20,
        ["offroad Comment Intervale"] = 45,
        ["radar Control Comment Intervale"] = 10,
        ["resting Comment Intervale"] = 600,
        ["feature Comment Intervale"] = 45,
        ["speed Comment Intervale"] = 30,
        ["curbstone Comment Intervale"] = 20,
        ["steering Comment Intervale"] = 20,
    };

    private static readonly double[] CommentFactors = { 1, 3, 10, 1000 };

    private readonly MainViewModel _main;
    private readonly DispatcherTimer _timer;
    private BusCareer? _career;
    private BusJsonFile? _settings, _aggro;
    private int _loadToken, _initialCommentIndex;
    private double _initialFov;

    private BusProfile? _selectedProfile;
    private BusCareerSlot? _selectedCareer;
    private bool _isLoaded, _isBusy, _isGameRunning, _justSaved, _hasCareer, _hasSettings, _hasComments;
    private bool _repairBuses, _cleanBuses;
    private string _statusText = "", _budgetText = "", _autosaveText = "";
    private StatusKind _statusKind;
    private double _reputation, _fov = 80;
    private int _commentIndex;
    private string _budgetStat = "–", _repStat = "–", _staffStat = "–", _busStat = "–", _conditionStat = "–", _cleanStat = "–";

    public BusViewModel(MainViewModel main)
    {
        _main = main;

        BackCommand = new RelayCommand(() => _main.ShowLibrary());
        RefreshCommand = new RelayCommand(LoadProfiles, () => !IsBusy);
        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => IsLoaded && !IsBusy);
        AddBudgetCommand = new RelayCommand(p =>
        {
            if (!long.TryParse(p as string, out var add)) return;
            TryParse(BudgetText, out var current);
            BudgetText = Format(Math.Clamp(current + add, -1_000_000_000_000, 1_000_000_000_000));
        });
        MaxReputationCommand = new RelayCommand(() => Reputation = 100);
        MaxExperienceCommand = new RelayCommand(() => { foreach (var e in Employees.Where(e => e.HasExperience)) e.Experience = 100; });
        AllSatisfiedCommand = new RelayCommand(() => { foreach (var e in Employees.Where(e => e.HasSatisfaction)) e.Satisfaction = 100; });
        AllSimpleCommand = new RelayCommand(() => { foreach (var t in Simplifications) t.IsOn = true; });
        NoSimpleCommand = new RelayCommand(() => { foreach (var t in Simplifications) t.IsOn = false; });
        SetWheelAngleCommand = new RelayCommand(p =>
        {
            if (p is string s) WheelAngleText = s;
        });
        OpenSaveFolderCommand = new RelayCommand(() => OpenFolder(SelectedProfile?.FolderPath ?? BusLocator.ProfilesDirectory));
        OpenBackupFolderCommand = new RelayCommand(() => OpenFolder(SelectedProfile == null ? BusBackups.Root : BusBackups.DirectoryFor(SelectedProfile)));
        RestoreBackupCommand = new RelayCommand(async p =>
        {
            if (p is BackupEntry b) await RestoreAsync(b);
        }, _ => !IsBusy);
        BackupNowCommand = new RelayCommand(async () =>
        {
            if (SelectedProfile is not { } profile) return;
            try
            {
                await Task.Run(() => BusBackups.Create(profile));
                LoadBackups();
                ShowStatus(StatusKind.Success, Loc.Get("Bk_Created"));
            }
            catch (Exception ex)
            {
                ShowStatus(StatusKind.Error, Loc.F("Err_Save", ex.Message));
            }
        }, () => SelectedProfile != null && !IsBusy);

        _isGameRunning = BusLocator.IsGameRunning();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => IsGameRunning = BusLocator.IsGameRunning();
        _timer.Start();

        LoadProfiles();
    }

    // ---------- Images ----------

    public ImageSource? HeroImage { get; } = SteamArt.Load(BusLocator.SteamAppId, SteamArt.Hero, 1920);
    public ImageSource? LogoImage { get; } = SteamArt.Load(BusLocator.SteamAppId, SteamArt.Logo, 640);

    // ---------- Collections & commands ----------

    public ObservableCollection<BusProfile> Profiles { get; } = new();
    public ObservableCollection<BusCareerSlot> CareerSaves { get; } = new();
    public ObservableCollection<BusEmployeeViewModel> Employees { get; } = new();
    public ObservableCollection<BusSlider> TrafficSliders { get; } = new();
    public ObservableCollection<BusSlider> AudioSliders { get; } = new();
    public ObservableCollection<BusSlider> CameraSliders { get; } = new();
    public ObservableCollection<BusToggle> Simplifications { get; } = new();
    public ObservableCollection<BusToggle> GameplayToggles { get; } = new();
    public ObservableCollection<BusToggle> WheelToggles { get; } = new();
    public ObservableCollection<BusSlider> WheelSliders { get; } = new();
    public ObservableCollection<BusPlate> Plates { get; } = new();
    public ObservableCollection<BackupEntry> Backups { get; } = new();

    public ICommand BackCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand AddBudgetCommand { get; }
    public ICommand MaxReputationCommand { get; }
    public ICommand MaxExperienceCommand { get; }
    public ICommand AllSatisfiedCommand { get; }
    public ICommand AllSimpleCommand { get; }
    public ICommand NoSimpleCommand { get; }
    public ICommand SetWheelAngleCommand { get; }
    public ICommand OpenSaveFolderCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand BackupNowCommand { get; }

    // ---------- Selection & state ----------

    public bool HasProfiles => Profiles.Count > 0;
    public bool HasCareerSaves => CareerSaves.Count > 0;

    public BusProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!Set(ref _selectedProfile, value)) return;
            CareerSaves.Clear();
            if (value != null)
                foreach (var s in BusLocator.FindCareerSaves(value)) CareerSaves.Add(s);
            OnPropertyChanged(nameof(HasCareerSaves));
            _selectedCareer = CareerSaves.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedCareer));
            _ = LoadAsync();
        }
    }

    public BusCareerSlot? SelectedCareer
    {
        get => _selectedCareer;
        set
        {
            if (Set(ref _selectedCareer, value)) _ = LoadAsync();
        }
    }

    public bool IsLoaded { get => _isLoaded; private set => Set(ref _isLoaded, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public bool IsGameRunning { get => _isGameRunning; private set => Set(ref _isGameRunning, value); }
    public bool JustSaved { get => _justSaved; private set => Set(ref _justSaved, value); }
    public bool HasCareer { get => _hasCareer; private set => Set(ref _hasCareer, value); }
    public bool HasSettings { get => _hasSettings; private set => Set(ref _hasSettings, value); }
    public bool HasComments { get => _hasComments; private set => Set(ref _hasComments, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public StatusKind StatusKind { get => _statusKind; private set => Set(ref _statusKind, value); }

    // ---------- Career ----------

    public string BudgetText { get => _budgetText; set => Set(ref _budgetText, value); }

    public double Reputation
    {
        get => _reputation;
        set => Set(ref _reputation, Math.Clamp(Math.Round(value), 0, 100));
    }

    public bool RepairBuses { get => _repairBuses; set => Set(ref _repairBuses, value); }
    public bool CleanBuses { get => _cleanBuses; set => Set(ref _cleanBuses, value); }

    // ---------- Settings ----------

    public double Fov { get => _fov; set => Set(ref _fov, Math.Clamp(Math.Round(value), 50, 120)); }
    public string AutosaveText { get => _autosaveText; set => Set(ref _autosaveText, value); }

    private string _wheelAngleText = "";
    private bool _hasWheelAngle;

    /// <summary>Rotation range of the real steering wheel in degrees (0 = the game decides).</summary>
    public string WheelAngleText { get => _wheelAngleText; set => Set(ref _wheelAngleText, value); }
    public bool HasWheelAngle { get => _hasWheelAngle; private set => Set(ref _hasWheelAngle, value); }

    public IReadOnlyList<string> CommentOptions => new[]
    {
        Loc.Get("Bus_Comments_Normal"), Loc.Get("Bus_Comments_Less"), Loc.Get("Bus_Comments_Rare"), Loc.Get("Bus_Comments_Never"),
    };

    public int CommentIndex { get => _commentIndex; set => Set(ref _commentIndex, Math.Clamp(value, 0, CommentFactors.Length - 1)); }

    // ---------- Backups ----------

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

    // ---------- Overview ----------

    public string BudgetStat { get => _budgetStat; private set => Set(ref _budgetStat, value); }
    public string ReputationStat { get => _repStat; private set => Set(ref _repStat, value); }
    public string StaffStat { get => _staffStat; private set => Set(ref _staffStat, value); }
    public string BusStat { get => _busStat; private set => Set(ref _busStat, value); }
    public string ConditionStat { get => _conditionStat; private set => Set(ref _conditionStat, value); }
    public string CleanStat { get => _cleanStat; private set => Set(ref _cleanStat, value); }

    // ---------- Loading ----------

    private void LoadProfiles()
    {
        var keep = SelectedProfile?.FolderPath;
        Profiles.Clear();
        foreach (var p in BusLocator.FindProfiles()) Profiles.Add(p);
        OnPropertyChanged(nameof(HasProfiles));
        _selectedProfile = null;
        SelectedProfile = Profiles.FirstOrDefault(p => p.FolderPath == keep) ?? Profiles.FirstOrDefault();
        if (!HasProfiles) ShowStatus(StatusKind.Info, Loc.Get("Bus_Err_NoProfiles"));
    }

    private async Task LoadAsync()
    {
        var profile = SelectedProfile;
        var careerSlot = SelectedCareer;
        int token = ++_loadToken;
        IsLoaded = false;
        _career = null;
        if (profile == null) return;

        IsBusy = true;
        ClearStatus();
        try
        {
            var (career, settings, aggro) = await Task.Run(() =>
            {
                var c = careerSlot != null ? BusCareer.Load(careerSlot.FilePath) : null;
                return (c, BusJsonFile.TryLoad(profile.SettingsPath), BusJsonFile.TryLoad(profile.AggroPath));
            });
            if (token != _loadToken) return;
            _career = career;
            _settings = settings;
            _aggro = aggro;
            Apply();
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

    private void Apply()
    {
        // Career
        var c = _career;
        HasCareer = c != null && c.HasBudget;
        Employees.Clear();
        RepairBuses = CleanBuses = false;
        if (c != null)
        {
            BudgetText = Format((long)Math.Round(c.Budget));
            Reputation = c.Reputation;
            foreach (var e in c.Employees)
                Employees.Add(new BusEmployeeViewModel(e)
                {
                    Experience = e.Experience != null ? c.Gvas.ReadFloat(e.Experience) : 0,
                    Satisfaction = e.Satisfaction != null ? c.Gvas.ReadFloat(e.Satisfaction) : 0,
                });
            BudgetStat = Format((long)Math.Round(c.Budget)) + " €";
            ReputationStat = Math.Round(c.Reputation).ToString(Loc.Culture) + " / 100";
            StaffStat = Math.Max(0, c.Employees.Count - 1).ToString(Loc.Culture);
            BusStat = c.VehicleCount.ToString(Loc.Culture);
            ConditionStat = Math.Round(c.AverageCondition * 100) + " %";
            CleanStat = Math.Round(c.AverageCleanliness * 100) + " %";
        }
        else
        {
            BudgetStat = ReputationStat = StaffStat = BusStat = ConditionStat = CleanStat = "–";
        }

        // Settings.cfg
        var s = _settings;
        HasSettings = s != null;
        TrafficSliders.Clear();
        AudioSliders.Clear();
        CameraSliders.Clear();
        Simplifications.Clear();
        GameplayToggles.Clear();
        WheelToggles.Clear();
        WheelSliders.Clear();
        Plates.Clear();
        if (s != null)
        {
            void Slider(ObservableCollection<BusSlider> target, string key, string labelKey, double min, double max, double scale, string unit)
            {
                if (s.GetDouble(key) is not { } v) return;
                target.Add(new BusSlider(key, Loc.Get(labelKey), min, max, scale, unit) { Value = v * scale });
            }
            void Toggle(ObservableCollection<BusToggle> target, string key, string labelKey)
            {
                if (s.GetBool(key) is not { } v) return;
                target.Add(new BusToggle(key, Loc.Get(labelKey), Loc.Get(labelKey + "_d")) { IsOn = v });
            }

            Slider(TrafficSliders, "traffic Density", "Bus_Traffic", 0, 100, 100, " %");
            Slider(TrafficSliders, "pedestrian Density", "Bus_Pedestrians", 0, 100, 100, " %");
            Slider(TrafficSliders, "passengerDensity", "Bus_Passengers", 0, 100, 100, " %");
            Slider(TrafficSliders, "passenger Ticket Buy Chance", "Bus_TicketChance", 0, 100, 100, " %");

            Slider(AudioSliders, "masterAudioVolume", "Bus_VolMaster", 0, 100, 100, " %");
            Slider(AudioSliders, "volumeMusicMaster", "Bus_VolMusic", 0, 100, 100, " %");
            Slider(AudioSliders, "volumeRadioMaster", "Bus_VolRadio", 0, 100, 100, " %");
            Slider(AudioSliders, "volumeVoiceMaster", "Bus_VolVoice", 0, 100, 100, " %");
            Slider(AudioSliders, "volumeVehicleMaster", "Bus_VolVehicle", 0, 100, 100, " %");
            Slider(AudioSliders, "volumeAmbientMaster", "Bus_VolAmbient", 0, 100, 100, " %");
            Slider(AudioSliders, "volumeTrafficAI", "Bus_VolTraffic", 0, 100, 100, " %");

            Slider(CameraSliders, "lookHorizontalSensitivity", "Bus_LookH", 1, 100, 1, "");
            Slider(CameraSliders, "lookVerticalSensitivity", "Bus_LookV", 1, 100, 1, "");
            Slider(CameraSliders, "headSwingingIntensity", "Bus_HeadSwing", 0, 100, 100, " %");
            Slider(CameraSliders, "vehicle Tilt", "Bus_Tilt", 0, 100, 100, " %");

            foreach (var key in new[] { "Motor Start Stop", "Driving", "Lights", "Doors", "Air Condition", "Clearance", "Wipers", "Resting" })
                Toggle(Simplifications, "simple " + key, "Bus_Simple_" + key.Replace(" ", ""));

            Toggle(GameplayToggles, "start From Depot", "Bus_StartDepot");
            Toggle(GameplayToggles, "play Passenger Comments In Bus", "Bus_PassengerComments");
            Toggle(GameplayToggles, "show Passenger States", "Bus_PassengerStates");
            Toggle(GameplayToggles, "enableBusAI", "Bus_BusAI");
            Toggle(GameplayToggles, "enableLiveEvents", "Bus_LiveEvents");
            Toggle(GameplayToggles, "show Tutorial", "Bus_Tutorial");
            Toggle(GameplayToggles, "show Ingame Messages", "Bus_Messages");
            Toggle(GameplayToggles, "highlight Interactables", "Bus_Highlight");
            Toggle(GameplayToggles, "enable Head Shaking", "Bus_HeadShake");
            Toggle(GameplayToggles, "hide Steering Wheel", "Bus_HideWheel");
            Toggle(GameplayToggles, "use System Time", "Bus_SystemTime");
            Toggle(GameplayToggles, "use System Date", "Bus_SystemDate");

            Toggle(WheelToggles, "speedDependentSteering", "Bus_SpeedSteer");
            Toggle(WheelToggles, "autoCenterSteeringWheel", "Bus_AutoCenter");
            Toggle(WheelToggles, "forceFeedbackEnabled", "Bus_FF");
            Toggle(WheelToggles, "wheelDependentIndicator", "Bus_WheelIndicator");
            Toggle(WheelToggles, "keepBrake", "Bus_KeepBrake");
            Slider(WheelSliders, "steering Camera", "Bus_SteerCam", 0, 100, 100, " %");
            Slider(WheelSliders, "genericEffectMultiplier", "Bus_FfGeneric", 0, 100, 100, " %");
            Slider(WheelSliders, "springForceMultiplier", "Bus_FfSpring", 0, 100, 100, " %");
            Slider(WheelSliders, "damperForceMultiplier", "Bus_FfDamper", 0, 100, 100, " %");
            Slider(WheelSliders, "roadEffectMultiplier", "Bus_FfRoad", 0, 100, 100, " %");
            Slider(WheelSliders, "collisionEffectMultiplier", "Bus_FfCollision", 0, 100, 100, " %");
            HasWheelAngle = s.GetDouble("wheelMaxAngle") is not null;
            WheelAngleText = ((long)(s.GetDouble("wheelMaxAngle") ?? 0)).ToString(CultureInfo.InvariantCulture);

            Fov = s.GetDouble("field Of View FOV") ?? 80;
            _initialFov = Fov;
            AutosaveText = ((long)(s.GetDouble("autosave Intervale") ?? 15)).ToString(CultureInfo.InvariantCulture);

            if (s.Root["vehicle Settings"] is JsonArray vehicles)
                for (int i = 0; i < vehicles.Count; i++)
                    if (vehicles[i] is JsonObject v && v["license Plate"] is JsonValue)
                        Plates.Add(new BusPlate(i, (string?)v["vehicle Filename"] ?? "?", (string?)v["license Plate"] ?? ""));
        }

        // AggroConstants.cfg
        HasComments = _aggro != null && _aggro.Has("speed Comment Intervale");
        if (HasComments)
        {
            double factor = (_aggro!.GetDouble("speed Comment Intervale") ?? 30) / 30.0;
            int best = 0;
            for (int i = 0; i < CommentFactors.Length; i++)
                if (Math.Abs(Math.Log(CommentFactors[i]) - Math.Log(Math.Max(factor, 0.01))) <
                    Math.Abs(Math.Log(CommentFactors[best]) - Math.Log(Math.Max(factor, 0.01)))) best = i;
            CommentIndex = _initialCommentIndex = best;
        }
    }

    private void LoadBackups()
    {
        Backups.Clear();
        if (SelectedProfile != null)
            foreach (var b in BusBackups.List(SelectedProfile)) Backups.Add(b);
    }

    // ---------- Saving ----------

    private async Task SaveAsync()
    {
        var profile = SelectedProfile;
        if (profile == null) return;

        long budget = 0;
        if (HasCareer && !TryParse(BudgetText, out budget))
        {
            ShowStatus(StatusKind.Error, Loc.Get("Bus_Err_Budget"));
            return;
        }

        int autosave = 15;
        if (HasSettings && (!int.TryParse(AutosaveText.Trim(), out autosave) || autosave < 1 || autosave > 240))
        {
            ShowStatus(StatusKind.Error, Loc.Get("Bus_Err_Autosave"));
            return;
        }

        int wheelAngle = 0;
        if (HasSettings && HasWheelAngle && (!int.TryParse(WheelAngleText.Trim(), out wheelAngle) || wheelAngle < 0 || wheelAngle > 2520))
        {
            ShowStatus(StatusKind.Error, Loc.Get("Bus_Err_WheelAngle"));
            return;
        }

        if (BusLocator.IsGameRunning())
        {
            var answer = MessageBox.Show(Loc.Get("Bus_Msg_GameRunning"), Loc.Get("Msg_GameRunningTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var career = _career;
        var settings = _settings;
        var aggro = _aggro;
        bool hasCareer = HasCareer;
        float reputation = (float)Reputation;
        var staff = Employees.Select(e => (e.Employee, (float)e.Experience, (float)e.Satisfaction)).ToList();
        bool repair = RepairBuses, clean = CleanBuses;
        var sliders = TrafficSliders.Concat(AudioSliders).Concat(CameraSliders).Concat(WheelSliders).Select(x => (x.Key, x.Value / x.Scale)).ToList();
        var toggles = Simplifications.Concat(GameplayToggles).Concat(WheelToggles).Select(x => (x.Key, x.IsOn)).ToList();
        var plates = Plates.Select(p => (p.Index, p.Plate)).ToList();
        double fov = Fov;
        bool fovChanged = Math.Abs(fov - _initialFov) > 0.01;
        int commentIndex = CommentIndex;
        bool commentsChanged = HasComments && commentIndex != _initialCommentIndex;
        bool backup = BackupPolicy.ShouldBackup(BackupGameId, BusBackups.List(profile).FirstOrDefault()?.Created);

        IsBusy = true;
        ClearStatus();
        try
        {
            var lines = await Task.Run(() =>
            {
                if (backup) BusBackups.Create(profile);
                var summary = new List<string>();

                if (career != null && hasCareer)
                {
                    career.SetBudget(budget);
                    career.SetReputation(reputation);
                    foreach (var (employee, exp, sat) in staff)
                    {
                        if (employee.Experience != null && Math.Abs(career.Gvas.ReadFloat(employee.Experience) - exp) >= 0.5f)
                            career.Gvas.WriteFloat(employee.Experience, exp);
                        if (employee.Satisfaction != null && Math.Abs(career.Gvas.ReadFloat(employee.Satisfaction) - sat) >= 0.5f)
                            career.Gvas.WriteFloat(employee.Satisfaction, sat);
                    }
                    if (repair) summary.Add(Loc.F("Bus_Sum_Repaired", career.RepairAll()));
                    if (clean) summary.Add(Loc.F("Bus_Sum_Cleaned", career.CleanAll()));
                    career.Save();
                }

                if (settings != null)
                {
                    bool changed = false;
                    foreach (var (key, value) in sliders) changed |= settings.SetDouble(key, value);
                    foreach (var (key, on) in toggles) changed |= settings.SetBool(key, on);
                    changed |= settings.SetDouble("field Of View FOV", fov);
                    changed |= settings.SetDouble("autosave Intervale", autosave);
                    changed |= settings.SetDouble("wheelMaxAngle", wheelAngle);
                    if (settings.Root["vehicle Settings"] is JsonArray vehicles)
                        foreach (var (index, plate) in plates)
                            if (vehicles[index] is JsonObject v && (string?)v["license Plate"] != plate)
                            {
                                v["license Plate"] = plate;
                                changed = true;
                            }
                    if (changed)
                    {
                        settings.Save();
                        summary.Add(Loc.Get("Sw_Sum_Settings"));
                    }
                    if (fovChanged) BusIni.SetFieldOfView(fov);
                }

                if (aggro != null && commentsChanged)
                {
                    foreach (var (key, value) in CommentDefaults)
                        aggro.SetDouble(key, value * CommentFactors[commentIndex]);
                    aggro.Save();
                }
                return summary;
            });

            await LoadAsync();
            var details = lines.Count > 0 ? " " + string.Join(" · ", lines) + "." : "";
            ShowStatus(StatusKind.Success, Loc.F(backup ? "Msg_Saved" : "Msg_SavedNoBackup", details));
            _ = FlashSavedAsync();
        }
        catch (Exception ex)
        {
            await LoadAsync();
            ShowStatus(StatusKind.Error, Loc.F("Err_Save", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAsync(BackupEntry backup)
    {
        if (SelectedProfile is not { } profile) return;
        var answer = MessageBox.Show(Loc.F("Msg_Restore", backup.Display), Loc.Get("Msg_RestoreTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            await Task.Run(() => BusBackups.Restore(backup, profile));
            await LoadAsync();
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

    private static string Format(long value) => value.ToString("N0", Loc.Culture);

    private static bool TryParse(string? text, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = new string(text.Where(c => char.IsDigit(c) || c == '-').ToArray());
        return long.TryParse(cleaned, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
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
