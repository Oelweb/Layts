using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;

namespace Layts.Core;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute == null ? null : _ => canExecute())
    {
    }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}

public sealed class AppSettings
{
    public List<string> Favorites { get; set; } = new();

    /// <summary>„en“ (Standard) oder „de“.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Beim Start automatisch nach Updates suchen.</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Back up a save automatically before layts writes it.</summary>
    public bool AutoBackup { get; set; } = true;

    /// <summary>At most one automatic backup per this many minutes (0 = before every save).</summary>
    public int BackupIntervalMinutes { get; set; } = 15;

    /// <summary>How many backups are kept per save (older ones are deleted).</summary>
    public int BackupKeep { get; set; } = 10;

    /// <summary>Game ids with automatic backups turned off.</summary>
    public List<string> BackupDisabledGames { get; set; } = new();

    /// <summary>Turn steering wheel buttons into keyboard keys while layts is open.</summary>
    public bool WheelMapperEnabled { get; set; }

    public List<WheelBinding> WheelBindings { get; set; } = new();

    /// <summary>The settings instance the app uses (set by <see cref="Load"/>).</summary>
    public static AppSettings Current { get; private set; } = new();

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Layts");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // Defekte Einstellungen ignorieren – es wird mit Standardwerten gestartet.
        }
        return Current = new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Nicht kritisch.
        }
    }
}

public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dunkle Titelleiste (Windows 10 20H1+ / Windows 11).</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).EnsureHandle();
            int on = 1;
            DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
            int caption = 0x0010100E; // #0E0E10 als BGR
            DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));

            // Windows 11: abgerundete Fensterecken (DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND)
            int round = 2;
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));

            // Dezenter Fensterrand in der Linienfarbe (#2A2A2F als BGR)
            int border = 0x002F2A2A;
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
        catch
        {
            // Ältere Windows-Versionen: Standard-Titelleiste.
        }
    }
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sichtbar, wenn der Wert (z. B. ein Enum) als Text dem ConverterParameter entspricht.</summary>
public sealed class StateIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class TextToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
