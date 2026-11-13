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
    private string _message = "";

    public Updater()
    {
        CheckCommand = new RelayCommand(async () => await CheckAsync(silent: false), () => !IsBusy);
        UpdateCommand = new RelayCommand(async () => await DownloadAndRestartAsync(), () => State == UpdateState.Available);
        OpenReleasesCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true }); }
            catch { /* kein Browser */ }
        });
    }

    public ICommand CheckCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand OpenReleasesCommand { get; }

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
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Restarting;
    public bool HasUpdate => State is UpdateState.Available or UpdateState.Downloading or UpdateState.Restarting;

    public string AvailableVersion => _update?.TargetFullRelease.Version.ToString() ?? "";

    public int Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    /// <summary>Prüft auf Updates. <paramref name="silent"/> = beim Programmstart, ohne Meldungen bei „aktuell“ oder Fehlern.</summary>
    public async Task CheckAsync(bool silent)
    {
        if (IsBusy) return;

        if (!IsInstalled)
        {
            if (!silent)
            {
                State = UpdateState.NotInstalled;
                Message = "Automatische Updates gibt es in der installierten Version.";
            }
            return;
        }

        State = UpdateState.Checking;
        Message = "Suche nach Updates …";
        try
        {
            _update = await _manager.CheckForUpdatesAsync();
            OnPropertyChanged(nameof(AvailableVersion));

            if (_update != null)
            {
                State = UpdateState.Available;
                Message = $"Version {AvailableVersion} ist verfügbar.";
            }
            else if (silent)
            {
                State = UpdateState.Idle;
                Message = "";
            }
            else
            {
                State = UpdateState.UpToDate;
                Message = "Du hast die neueste Version.";
                await Task.Delay(4000);
                if (State == UpdateState.UpToDate)
                {
                    State = UpdateState.Idle;
                    Message = "";
                }
            }
        }
        catch (Exception ex)
        {
            if (silent)
            {
                State = UpdateState.Idle;
                Message = "";
            }
            else
            {
                State = UpdateState.Error;
                Message = "Update-Suche fehlgeschlagen: " + ex.Message;
            }
        }
    }

    private async Task DownloadAndRestartAsync()
    {
        if (_update == null) return;

        State = UpdateState.Downloading;
        Progress = 0;
        Message = $"Lade Version {AvailableVersion} …";
        try
        {
            await _manager.DownloadUpdatesAsync(_update, p =>
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress = p));

            State = UpdateState.Restarting;
            Message = "Wird installiert, Layts startet gleich neu …";
            await Task.Delay(600);
            _manager.ApplyUpdatesAndRestart(_update);
        }
        catch (Exception ex)
        {
            State = UpdateState.Error;
            Message = "Update fehlgeschlagen: " + ex.Message;
        }
    }
}
