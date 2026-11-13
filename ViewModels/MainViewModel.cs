using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Layts.Core;
using Layts.Games;

namespace Layts.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly AppSettings _settings = AppSettings.Load();
    private object _currentView;
    private GameCardViewModel? _activeGame;

    public MainViewModel()
    {
        foreach (var def in GameCatalog.All)
        {
            var card = new GameCardViewModel(this, def);
            card.InitFavorite(_settings.Favorites.Contains(def.Id));
            Games.Add(card);
        }
        RebuildFavorites();

        Library = new LibraryViewModel(this);
        _currentView = Library;
        ShowLibraryCommand = new RelayCommand(ShowLibrary);

        // Beim Start leise nach Updates suchen (nur installierte Version).
        _ = Task.Run(async () => await Task.Delay(2500))
            .ContinueWith(_ => Updater.CheckAsync(silent: true), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public Updater Updater { get; } = new();

    public ObservableCollection<GameCardViewModel> Games { get; } = new();
    public ObservableCollection<GameCardViewModel> Favorites { get; } = new();
    public LibraryViewModel Library { get; }
    public ICommand ShowLibraryCommand { get; }

    public bool HasFavorites => Favorites.Count > 0;
    public bool IsLibraryActive => ReferenceEquals(CurrentView, Library);

    public object CurrentView
    {
        get => _currentView;
        private set
        {
            if (Set(ref _currentView, value)) OnPropertyChanged(nameof(IsLibraryActive));
        }
    }

    public void Open(GameCardViewModel card)
    {
        if (!card.IsAvailable || card.Definition.CreateViewModel == null) return;
        if (CurrentView is IDisposable d) d.Dispose();
        SetActive(card);
        CurrentView = card.Definition.CreateViewModel(this);
    }

    public void ShowLibrary()
    {
        if (IsLibraryActive) return;
        if (CurrentView is IDisposable d) d.Dispose();
        SetActive(null);
        CurrentView = Library;
    }

    private void SetActive(GameCardViewModel? card)
    {
        if (_activeGame != null) _activeGame.IsActive = false;
        _activeGame = card;
        if (card != null) card.IsActive = true;
    }

    internal void OnFavoriteChanged()
    {
        _settings.Favorites = Games.Where(g => g.IsFavorite).Select(g => g.Definition.Id).ToList();
        _settings.Save();
        RebuildFavorites();
        Library.Refresh();
    }

    private void RebuildFavorites()
    {
        Favorites.Clear();
        foreach (var g in Games.Where(g => g.IsFavorite).OrderBy(g => g.Name)) Favorites.Add(g);
        OnPropertyChanged(nameof(HasFavorites));
    }
}

public sealed class GameCardViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _isFavorite, _isActive;

    public GameCardViewModel(MainViewModel main, GameDefinition definition)
    {
        _main = main;
        Definition = definition;

        Cover = SteamArt.Load(definition.SteamAppId, SteamArt.Cover, 400);
        Hero = SteamArt.Load(definition.SteamAppId, SteamArt.Hero, 1920);
        Logo = SteamArt.Load(definition.SteamAppId, SteamArt.Logo, 640);
        Icon = SteamArt.LoadIcon(definition.SteamAppId) ?? Cover;

        OpenCommand = new RelayCommand(() => _main.Open(this), () => IsAvailable);
        ToggleFavoriteCommand = new RelayCommand(() => IsFavorite = !IsFavorite);
    }

    public GameDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Short => Definition.Short;
    public string Description => Definition.Description;
    public IReadOnlyList<string> Features => Definition.Features;
    public bool IsAvailable => Definition.CreateViewModel != null;

    public ImageSource? Cover { get; }
    public ImageSource? Hero { get; }
    public ImageSource? Logo { get; }

    /// <summary>Kleines App-Icon für die Seitenleiste (sonst das Cover).</summary>
    public ImageSource? Icon { get; }

    public ICommand OpenCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (Set(ref _isFavorite, value)) _main.OnFavoriteChanged();
        }
    }

    /// <summary>Gerade geöffnet (für die Markierung in der Seitenleiste).</summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    internal void InitFavorite(bool value) => _isFavorite = value;
}

public sealed class LibraryViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private string _searchText = "";
    private bool _onlyFavorites;

    public LibraryViewModel(MainViewModel main)
    {
        _main = main;
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        GamesView = new ListCollectionView(main.Games) { Filter = Matches };
        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.IsFavorite), ListSortDirection.Descending));
        GamesView.SortDescriptions.Add(new SortDescription(nameof(GameCardViewModel.Name), ListSortDirection.Ascending));
    }

    public ListCollectionView GamesView { get; }
    public ICommand ClearSearchCommand { get; }
    public bool IsEmpty => GamesView.IsEmpty;
    public string CountText => GamesView.Count == 1 ? "1 Spiel" : $"{GamesView.Count} Spiele";

    /// <summary>Großes Banner oben: erster Favorit, sonst das erste Spiel.</summary>
    public GameCardViewModel? Featured =>
        _main.Games.OrderByDescending(g => g.IsFavorite).ThenBy(g => g.Name).FirstOrDefault();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? "")) Refresh();
        }
    }

    public bool OnlyFavorites
    {
        get => _onlyFavorites;
        set
        {
            if (Set(ref _onlyFavorites, value)) Refresh();
        }
    }

    public void Refresh()
    {
        GamesView.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(Featured));
    }

    private bool Matches(object item)
    {
        if (item is not GameCardViewModel g) return false;
        if (_onlyFavorites && !g.IsFavorite) return false;
        var q = _searchText.Trim();
        return q.Length == 0
            || g.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || g.Short.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
