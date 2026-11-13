using Layts.Games.Ets2;
using Layts.ViewModels;

namespace Layts.Games;

public sealed class GameDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Short { get; init; }
    public required string Description { get; init; }

    /// <summary>Steam-AppId – damit werden Cover, Titelbild und Logo aus dem lokalen Steam-Cache geladen.</summary>
    public string? SteamAppId { get; init; }

    /// <summary>Stichpunkte für die Bibliothek („Geld“, „Level“ …).</summary>
    public IReadOnlyList<string> Features { get; init; } = Array.Empty<string>();

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
            Description = "Spielstand-Editor für Geld, Level, Skills, Garagen und das Coaches-DLC.",
            SteamAppId = Ets2Locator.SteamAppId,
            Features = new[] { "Geld", "Level & XP", "Skills", "Garagen", "Fahrzeuge", "Coaches-DLC", "Backups" },
            CreateViewModel = main => new Ets2ViewModel(main),
        },
    };
}
