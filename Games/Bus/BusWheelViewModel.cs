using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Layts.Core;
using Layts.ViewModels;

namespace Layts.Games.Bus;

public sealed class WheelBindingViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _label;
    private int _key;

    public WheelBindingViewModel(WheelBinding binding, string deviceName, Action changed, Action<WheelBindingViewModel> reassign, Action<WheelBindingViewModel> remove)
    {
        Binding = binding;
        DeviceName = deviceName;
        _label = binding.Label;
        _key = binding.Key;
        _changed = changed;
        ReassignCommand = new RelayCommand(() => reassign(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public WheelBinding Binding { get; }
    public string DeviceName { get; }
    public string InputText => WheelMapper.Describe(Binding.Input);
    public bool HasNoKey => Key <= 0;
    public ICommand ReassignCommand { get; }
    public ICommand RemoveCommand { get; }

    public string Label
    {
        get => _label;
        set
        {
            if (!Set(ref _label, value ?? "")) return;
            Binding.Label = _label;
            _changed();
        }
    }

    public int Key
    {
        get => _key;
        set
        {
            if (!Set(ref _key, value)) return;
            Binding.Key = value;
            OnPropertyChanged(nameof(HasNoKey));
            _changed();
        }
    }

    public void InputChanged() => OnPropertyChanged(nameof(InputText));

    /// <summary>Picking a suggestion fills in the name.</summary>
    public string? PickedSuggestion
    {
        get => null;
        set
        {
            if (!string.IsNullOrEmpty(value)) Label = value;
            OnPropertyChanged();
        }
    }
}

/// <summary>
/// "Wheel buttons → keyboard keys": works without the game's own binding file, for any wheel and any game,
/// as long as layts is open. Changes are saved right away.
/// </summary>
public sealed class BusWheelViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private string _search = "", _live = "", _hint = "";
    private bool _isCapturing;
    private WheelBindingViewModel? _reassigning;

    public BusWheelViewModel(MainViewModel main)
    {
        _main = main;
        var devices = WheelMapper.Devices();
        DevicesText = devices.Count == 0
            ? Loc.Get("Wh_NoDevices")
            : string.Join(" · ", devices.Select(d => d.Name));
        HasDevices = devices.Count > 0;

        foreach (var b in _main.Settings.WheelBindings) Bindings.Add(Wrap(b, devices));
        View = CollectionViewSource.GetDefaultView(Bindings);
        View.Filter = Matches;

        AddCommand = new RelayCommand(() => StartCapture(null));
        CancelCaptureCommand = new RelayCommand(StopCapture);
        RefreshDevicesCommand = new RelayCommand(() =>
        {
            var list = WheelMapper.Devices();
            HasDevices = list.Count > 0;
            DevicesText = list.Count == 0 ? Loc.Get("Wh_NoDevices") : string.Join(" · ", list.Select(d => d.Name));
            OnPropertyChanged(nameof(DevicesText));
            OnPropertyChanged(nameof(HasDevices));
        });

        WheelMapper.Instance.LiveInput += OnLive;
        WheelMapper.Instance.Capture(null); // wakes the reader so the live display works
    }

    public ObservableCollection<WheelBindingViewModel> Bindings { get; } = new();
    public ICollectionView View { get; }
    public string DevicesText { get; private set; }
    public bool HasDevices { get; private set; }
    public ICommand AddCommand { get; }
    public ICommand CancelCaptureCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    /// <summary>Suggestions for the action name (free text is allowed too).</summary>
    public IReadOnlyList<string> LabelSuggestions { get; } = new[]
    {
        "Wh_L_Door1", "Wh_L_Door2", "Wh_L_Door3", "Wh_L_AllDoors", "Wh_L_StopBrake", "Wh_L_ParkingBrake", "Wh_L_Kneeling",
        "Wh_L_IndicatorL", "Wh_L_IndicatorR", "Wh_L_Hazard", "Wh_L_Lights", "Wh_L_HighBeam", "Wh_L_Horn", "Wh_L_Wipers",
        "Wh_L_AirCon", "Wh_L_Engine", "Wh_L_Ignition", "Wh_L_Retarder", "Wh_L_Ramp", "Wh_L_Ticket", "Wh_L_Announcement",
        "Wh_L_Destination", "Wh_L_Camera", "Wh_L_LookLeft", "Wh_L_LookRight", "Wh_L_Map", "Wh_L_Pause",
    }.Select(Loc.Get).ToList();

    public bool Enabled
    {
        get => _main.Settings.WheelMapperEnabled;
        set
        {
            if (_main.Settings.WheelMapperEnabled == value) return;
            _main.Settings.WheelMapperEnabled = value;
            Save();
            OnPropertyChanged();
        }
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? "")) View.Refresh();
        }
    }

    public string LiveText { get => _live; private set => Set(ref _live, value); }
    public bool IsCapturing { get => _isCapturing; private set => Set(ref _isCapturing, value); }
    public string Hint { get => _hint; private set => Set(ref _hint, value); }
    public bool IsEmpty => Bindings.Count == 0;

    private WheelBindingViewModel Wrap(WheelBinding b, List<WheelDevice>? devices = null)
    {
        devices ??= WheelMapper.Devices();
        var name = devices.FirstOrDefault(d => d.Id == b.Device)?.Name ?? Loc.Get("Wh_Disconnected");
        return new WheelBindingViewModel(b, name, Save, r => StartCapture(r), Remove);
    }

    private bool Matches(object o)
    {
        if (o is not WheelBindingViewModel b) return false;
        var q = _search.Trim();
        return q.Length == 0
               || b.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
               || b.InputText.Contains(q, StringComparison.OrdinalIgnoreCase)
               || WheelMapper.KeyName(b.Key).Contains(q, StringComparison.OrdinalIgnoreCase)
               || b.DeviceName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void StartCapture(WheelBindingViewModel? reassign)
    {
        _reassigning = reassign;
        IsCapturing = true;
        Hint = Loc.Get("Wh_PressWheel");
        WheelMapper.Instance.Capture(captured =>
            Application.Current?.Dispatcher.BeginInvoke(() => OnCaptured(captured)));
    }

    private void StopCapture()
    {
        WheelMapper.Instance.Capture(null);
        IsCapturing = false;
        Hint = "";
        _reassigning = null;
    }

    private void OnCaptured(WheelBinding captured)
    {
        IsCapturing = false;
        if (_reassigning is { } r)
        {
            r.Binding.Device = captured.Device;
            r.Binding.Input = captured.Input;
            r.Binding.AxisRest = captured.AxisRest;
            r.InputChanged();
            _reassigning = null;
        }
        else
        {
            var vm = Wrap(new WheelBinding { Device = captured.Device, Input = captured.Input, AxisRest = captured.AxisRest });
            Bindings.Insert(0, vm);
            OnPropertyChanged(nameof(IsEmpty));
        }
        Hint = Loc.F("Wh_Captured", WheelMapper.Describe(captured.Input));
        Save();
    }

    private void Remove(WheelBindingViewModel b)
    {
        Bindings.Remove(b);
        OnPropertyChanged(nameof(IsEmpty));
        Save();
    }

    private void Save()
    {
        _main.Settings.WheelBindings = Bindings.Select(b => b.Binding).ToList();
        _main.Settings.Save();
        WheelMapper.Instance.Configure(_main.Settings.WheelMapperEnabled, _main.Settings.WheelBindings);
    }

    private void OnLive(string text) =>
        Application.Current?.Dispatcher.BeginInvoke(() => LiveText = text);

    public void Dispose()
    {
        WheelMapper.Instance.LiveInput -= OnLive;
        if (IsCapturing) WheelMapper.Instance.Capture(null);
    }
}
