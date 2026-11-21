using Layts.Games.Bus;
using Layts.Games.Ets2;
using Layts.Games.Spm;
using Layts.Games.Swyf;
using Layts.Games.Thy;
using Layts.ViewModels;

namespace Layts.Games;

public sealed class GameDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Short { get; init; }

    /// <summary>Übersetzungsschlüssel der Beschreibung (siehe Core/Strings.cs).</summary>
    public required string DescriptionKey { get; init; }

    /// <summary>Steam-AppId – damit werden Cover, Titelbild und Logo aus dem lokalen Steam-Cache geladen.</summary>
    public string? SteamAppId { get; init; }

    /// <summary>Übersetzungsschlüssel der Stichpunkte für die Bibliothek.</summary>
    public IReadOnlyList<string> FeatureKeys { get; init; } = Array.Empty<string>();

    /// <summary>Erzeugt die Editor-Ansicht. <c>null</c> = noch nicht verfügbar.</summary>
    public Func<MainViewModel, object>? CreateViewModel { get; init; }
}

/// <summary>
/// Alle Spiele der Bibliothek. Ein neues Spiel = ein neuer Eintrag hier + eigener Ordner unter Games/.
/// </summary>
public static class GameCatalog
{
    public static IReadOnlyList<GameDefinition> All { get; } = new[]
    {
        new GameDefinition
        {
            Id = "ets2",
            Name = "Euro Truck Simulator 2",
            Short = "ETS2",
            DescriptionKey = "Ets2Desc",
            SteamAppId = Ets2Locator.SteamAppId,
            FeatureKeys = new[] { "F_Money", "F_Level", "F_Skills", "F_Garages", "F_Vehicles", "F_Coaches", "F_Backups" },
            CreateViewModel = main => new Ets2ViewModel(main),
        },
        new GameDefinition
        {
            Id = "swyf",
            Name = "Scam With Your Friends",
            Short = "SWYF",
            DescriptionKey = "SwyfDesc",
            SteamAppId = SwyfLocator.SteamAppId,
            FeatureKeys = new[] { "F_Money", "Sw_F_Day", "Sw_F_Quota", "Sw_F_Apps", "Sw_F_Emotes", "Sw_F_Settings", "F_Backups" },
            CreateViewModel = main => new SwyfViewModel(main),
        },
        new GameDefinition
        {
            Id = "thy",
            Name = "Trees Hate You",
            Short = "THY",
            DescriptionKey = "ThyDesc",
            SteamAppId = ThyLocator.SteamAppId,
            FeatureKeys = new[] { "Thy_F_Levels", "Thy_F_Hats", "Thy_F_Skills", "Thy_F_Challenge", "F_Backups" },
            CreateViewModel = main => new ThyViewModel(main),
        },
        new GameDefinition
        {
            Id = "thebus",
            Name = "The Bus",
            Short = "BUS",
            DescriptionKey = "BusDesc",
            SteamAppId = BusLocator.SteamAppId,
            FeatureKeys = new[] { "Bus_F_Budget", "Bus_F_Reputation", "Bus_F_Staff", "Bus_F_Repair", "Bus_F_Settings", "F_Backups" },
            CreateViewModel = main => new BusViewModel(main),
        },
        new GameDefinition
        {
            Id = "spiderman",
            Name = "Marvel's Spider-Man Remastered",
            Short = "MSM",
            DescriptionKey = "SmDesc",
            SteamAppId = SpmLocator.SteamAppId,
            FeatureKeys = new[] { "Sm_F_Xp", "Sm_F_Activities", "Sm_F_Map", "Sm_F_Landmarks", "Sm_F_NewGamePlus", "Sm_F_Options", "Sm_F_Mouse", "F_Backups" },
            CreateViewModel = main => new SpmViewModel(main),
        },
    };
}
