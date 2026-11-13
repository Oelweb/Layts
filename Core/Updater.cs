using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using Velopack;
using Velopack.Sources;

namespace Layts.Core;

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Restarting,
    NotInstalled,
    Error,
}

/// <summary>
/// Sucht Updates in den GitHub-Releases (https://github.com/Oelweb/Layts),
/// lädt sie herunter und startet das Programm danach neu.
/// Funktioniert nur in der installierten Version (über Setup.exe).
/// </summary>
public sealed class Updater : ObservableObject
{
    public const string RepoUrl = "https://github.com/Oelweb/Layts";
    public const string ReleasesUrl = RepoUrl + "/releases";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _update;
    private UpdateState _state = UpdateState.Idle;
    private int _progress;
    private string _error = "";

    public Updater()
    {
        CheckCommand = new RelayCommand(async () => await CheckAsync(silent: false), () => !IsBusy);
        UpdateCommand = new RelayCommand(async () => await DownloadAndRestartAsync(), () => State == UpdateState.Available);
        OpenReleasesCommand = new RelayCommand(() => OpenUrl(ReleasesUrl));
        OpenRepoCommand = new RelayCommand(() => OpenUrl(RepoUrl));

        // Texte bei Sprachwechsel neu berechnen
        Loc.Changed += () =>
        {
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(AvailableText));
        };
    }

    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand OpenReleasesCommand { get; }
    public ICommand OpenRepoCommand { get; }

    /// <summary>Aktuelle Programmversion, z. B. „1.1.0“.</summary>
    public string CurrentVersion
    {
        get
        {
            try
            {
                if (_manager.IsInstalled && _manager.CurrentVersion is { } v) return v.ToString();
            }
            catch
            {
                // nicht installiert
            }
            var asm = Assembly.GetExecutingAssembly().GetName().Version;
            return asm == null ? "?" : $"{asm.Major}.{asm.Minor}.{asm.Build}";
        }
    }

    public bool IsInstalled
    {
        get
        {
            try { return _manager.IsInstalled; }
            catch { return false; }
        }
    }

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(HasUpdate));
            OnPropertyChanged(nameof(Message));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Restarting;
    public bool HasUpdate => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Restarting;

    public string AvailableVersion => _update?.TargetFullRelease.Version.ToString() ?? "";
    public string AvailableText => Loc.F("UpdateVersionIs", AvailableVersion);

    public int Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>Statusmeldung in der aktuellen Sprache.</summary>
    public string Message => State switch
    {
        UpdateState.Checking => Loc.Get("Upd_Checking"),
        UpdateState.UpToDate => Loc.Get("Upd_UpToDate"),
        UpdateState.Available => Loc.F("Upd_Available", AvailableVersion),
        UpdateState.Downloading => Loc.F("Upd_Downloading", AvailableVersion),
        UpdateState.Restarting => Loc.Get("Upd_Restarting"),
        UpdateState.NotInstalled => Loc.Get("Upd_NotInstalled"),
        UpdateState.Error => Loc.F("Upd_Error", _error),
        _ => "",
    };

    /// <summary>Prüft auf Updates. <paramref name="silent"/> = beim Programmstart, ohne Meldungen bei „aktuell“ oder Fehlern.</summary>
    public async Task CheckAsync(bool silent)
    {
        if (IsBusy) return;

        if (!IsInstalled)
        {
            if (!silent) State = UpdateState.NotInstalled;
            return;
        }

        State = UpdateState.Checking;
        try
        {
            _update = await _manager.CheckForUpdatesAsync();
            OnPropertyChanged(nameof(AvailableVersion));
            OnPropertyChanged(nameof(AvailableText));

            if (_update != null)
            {
                State = UpdateState.Available;
            }
            else if (silent)
            {
                State = UpdateState.Idle;
            }
            else
            {
                State = UpdateState.UpToDate;
                await Task.Delay(4000);
                if (State == UpdateState.UpToDate) State = UpdateState.Idle;
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            State = silent ? UpdateState.Idle : UpdateState.Error;
        }
    }

    private async Task DownloadAndRestartAsync()
    {
        if (_update == null) return;

        State = UpdateState.Downloading;
        Progress = 0;
        try
        {
            await _manager.DownloadUpdatesAsync(_update, p =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress = p));

            State = UpdateState.Restarting;
            await Task.Delay(600);
            _manager.ApplyUpdatesAndRestart(_update);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            State = UpdateState.Error;
        }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* kein Browser */ }
    }
}
