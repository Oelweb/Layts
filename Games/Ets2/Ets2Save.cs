using Layts.Core.Sii;

namespace Layts.Games.Ets2;

/// <summary>Ein geladener ETS2-Spielstand (game.sii) mit allen Bearbeitungsfunktionen.</summary>
public sealed class Ets2Save
{
    /// <summary>Garagen-Status: 0 = nicht gekauft, 1 = winzig, 2 = klein (3 Plätze), 3 = groß (5 Plätze).</summary>
    public const int GarageLarge = 3;
    public const int GarageLargeSlots = 5;

    private readonly ISiiDocument _doc;

    private Ets2Save(string path, ISiiDocument doc, bool wasEncrypted)
    {
        GamePath = path;
        _doc = doc;
        WasEncrypted = wasEncrypted;
    }

    public string GamePath { get; }
    public bool WasEncrypted { get; }
    public string FormatLabel => (WasEncrypted ? "Verschlüsselt · " : "") + _doc.FormatName;

    public static Ets2Save Load(string path)
    {
        var doc = SiiLoader.Load(File.ReadAllBytes(path), out var encrypted);
        var save = new Ets2Save(path, doc, encrypted);
        _ = save.Bank;
        _ = save.Economy;
        save.HasCoachesDlc = save.DetectCoaches();
        return save;
    }

    // ---------- Coaches-DLC ----------

    public const string CoachesDlcId = "eut2_coaches";

    /// <summary>
    /// True, wenn der Spielstand mit dem Coaches-DLC gespeichert wurde (Eintrag in info.sii)
    /// oder bereits Bus-Daten enthält (eigener Bus, Bus-XP, gefahrene Linien).
    /// </summary>
    public bool HasCoachesDlc { get; private set; }

    private bool DetectCoaches()
    {
        if (!Economy.HasField("bus_experience_points")) return false;
        if (InfoDependencies().Any(d => d.Contains(CoachesDlcId, StringComparison.OrdinalIgnoreCase))) return true;
        return BusExperience > 0 || BusCount > 0 || BusTripCount > 0;
    }

    private IEnumerable<string> InfoDependencies()
    {
        try
        {
            var info = Path.Combine(Path.GetDirectoryName(GamePath)!, "info.sii");
            if (!File.Exists(info)) return Array.Empty<string>();
            var unit = SiiLoader.Load(File.ReadAllBytes(info), out _).First("save_container");
            return unit != null && unit.HasField("dependencies") ? unit.GetStringArray("dependencies") : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public long BusExperience
    {
        get => Economy.HasField("bus_experience_points") ? Economy.GetInteger("bus_experience_points") : 0;
        set
        {
            if (Economy.HasField("bus_experience_points")) Economy.SetInteger("bus_experience_points", value);
        }
    }

    public int BusCount => Player is { } p && p.HasField("buses") ? p.GetArrayCount("buses") : 0;

    public int BusTripCount =>
        _doc.Units("bus_job_log").FirstOrDefault() is { } log && log.HasField("entries") ? log.GetArrayCount("entries") : 0;

    public int BusStopCount => _doc.Units("bus_stop").Count();

    public int DiscoveredBusStopCount =>
        _doc.Units("bus_stop").Count(s => s.HasField("discovered") && s.GetInteger("discovered") != 0);

    /// <summary>Markiert alle Bushaltestellen und Busbahnhöfe auf der Karte als entdeckt.</summary>
    public int DiscoverAllBusStops()
    {
        int count = 0;
        foreach (var stop in _doc.Units("bus_stop"))
        {
            if (!stop.HasField("discovered") || stop.GetInteger("discovered") != 0) continue;
            stop.SetInteger("discovered", 1);
            count++;
        }
        return count;
    }

    private ISiiUnit Bank => _doc.First("bank")
        ?? throw new InvalidDataException(Layts.Core.Loc.Get("Err_NoBank"));

    private ISiiUnit Economy => _doc.First("economy")
        ?? throw new InvalidDataException(Layts.Core.Loc.Get("Err_NoEconomy"));

    private ISiiUnit? Player => _doc.First("player");

    // ---------- Werte ----------

    public long Money
    {
        get => Bank.GetInteger("money_account");
        set => Bank.SetInteger("money_account", value);
    }

    public long Experience
    {
        get => Economy.GetInteger("experience_points");
        set => Economy.SetInteger("experience_points", value);
    }

    public bool HasSkill(string key) => Economy.HasField(key);

    public int GetSkill(string key) => HasSkill(key) ? (int)Economy.GetInteger(key) : 0;

    public void SetSkill(string key, int value)
    {
        if (HasSkill(key)) Economy.SetInteger(key, value);
    }

    // ---------- Statistik ----------

    public int TruckCount => CountNonNull("garage", "vehicles");
    public int DriverCount => CountNonNull("garage", "drivers");

    public int TrailerCount =>
        Player is { } p && p.HasField("trailers") ? p.GetArrayCount("trailers") : 0;

    public int OwnedGarageCount => _doc.Units("garage").Count(g => g.HasField("status") && g.GetInteger("status") > 0);
    public int GarageCount => _doc.Units("garage").Count();

    public int VisitedCityCount =>
        Economy.HasField("visited_cities") ? Economy.GetArrayCount("visited_cities") : 0;

    public int KnownCityCount => KnownCities().Count;

    private int CountNonNull(string unitType, string field) =>
        _doc.Units(unitType)
            .Where(u => u.HasField(field))
            .Sum(u => u.GetIdArray(field).Count(id => id != "null"));

    /// <summary>Alle Städte der Karte – ermittelt aus den Firmen- und Garagen-Einträgen im Spielstand.</summary>
    public SortedSet<string> KnownCities()
    {
        var cities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var company in _doc.Units("company"))
        {
            var city = company.Id.Split('.').Last();
            if (SiiToken.IsValid(city)) cities.Add(city);
        }
        foreach (var garage in _doc.Units("garage"))
        {
            if (!garage.Id.StartsWith("garage.", StringComparison.Ordinal)) continue;
            var city = garage.Id["garage.".Length..];
            if (SiiToken.IsValid(city)) cities.Add(city);
        }
        return cities;
    }

    // ---------- Aktionen ----------

    /// <summary>Setzt jeden Verschleiß (Motor, Getriebe, Kabine, Fahrgestell, Räder, Aufbau) aller LKW, Busse und Anhänger auf 0.</summary>
    public int RepairAllVehicles()
    {
        int repaired = 0;
        foreach (var unit in _doc.Units("vehicle").Concat(_doc.Units("trailer")))
        {
            bool changed = false;
            foreach (var field in unit.FieldNames.Where(IsWearField).ToList())
            {
                if (unit.IsFloatArray(field))
                {
                    unit.SetFloatArrayAll(field, 0f);
                    changed = true;
                }
                else if (unit.IsFloat(field))
                {
                    unit.SetFloat(field, 0f);
                    changed = true;
                }
            }
            if (changed) repaired++;
        }
        return repaired;
    }

    private static bool IsWearField(string name) =>
        name.EndsWith("_wear", StringComparison.Ordinal) || name.EndsWith("_wear_unfixable", StringComparison.Ordinal);

    public int RefuelAllTrucks()
    {
        int count = 0;
        foreach (var vehicle in _doc.Units("vehicle"))
        {
            if (!vehicle.IsFloat("fuel_relative")) continue;
            vehicle.SetFloat("fuel_relative", 1f);
            count++;
        }
        return count;
    }

    /// <summary>Kauft Garagen und/oder baut sie auf „groß“ (5 Plätze) aus. Bestehende LKW/Fahrer bleiben erhalten.</summary>
    public (int Bought, int Upgraded) UpgradeGarages(bool buyAll, bool upgradeOwned)
    {
        int bought = 0, upgraded = 0;
        foreach (var garage in _doc.Units("garage"))
        {
            if (!garage.HasField("status")) continue;
            long status = garage.GetInteger("status");
            if (status >= GarageLarge) continue;

            bool isOwned = status > 0;
            if (!isOwned && !buyAll) continue;
            if (isOwned && !upgradeOwned && !buyAll) continue;

            garage.SetInteger("status", GarageLarge);
            if (garage.HasField("vehicles")) garage.GrowIdArray("vehicles", GarageLargeSlots);
            if (garage.HasField("drivers")) garage.GrowIdArray("drivers", GarageLargeSlots);

            if (isOwned) upgraded++;
            else bought++;
        }
        return (bought, upgraded);
    }

    /// <summary>Markiert alle bekannten Städte als besucht (wichtig z. B. für den Garagenkauf).</summary>
    public int VisitAllCities()
    {
        var economy = Economy;
        if (!economy.HasField("visited_cities") || !economy.HasField("visited_cities_count")) return 0;

        var visited = economy.GetTokenArray("visited_cities");
        var counts = economy.GetIntegerArray("visited_cities_count");
        while (counts.Count < visited.Count) counts.Add(1);
        if (counts.Count > visited.Count) counts = counts.Take(visited.Count).ToList();

        var known = new HashSet<string>(visited, StringComparer.Ordinal);
        int added = 0;
        foreach (var city in KnownCities())
        {
            if (!known.Add(city)) continue;
            visited.Add(city);
            counts.Add(1);
            added++;
        }

        if (added > 0)
        {
            economy.SetTokenArray("visited_cities", visited);
            economy.SetIntegerArray("visited_cities_count", counts);
        }
        return added;
    }

    /// <summary>Schreibt den Spielstand (unverschlüsselt – das Spiel liest das problemlos).</summary>
    public void Write()
    {
        var bytes = _doc.Serialize();
        var temp = GamePath + ".layts-tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, GamePath, overwrite: true);
    }
}

/// <summary>
/// Level ↔ XP nach der Tabelle aus den Spieldaten (def.scs: economy.data.xp_levels und
/// bus_economy.data.xp_levels – beide identisch). Ab Level 30 gilt der letzte Wert für jedes weitere Level.
/// </summary>
public static class Ets2Levels
{
    /// <summary>XP, die von Level i zu Level i+1 nötig sind.</summary>
    private static readonly long[] XpToNext =
    {
        200, 500, 700, 900, 1000, 1100, 1300, 1600, 1700, 2100,
        2300, 2600, 2700, 2900, 3000, 3100, 3400, 3700, 4000, 4300,
        4600, 4700, 4900, 5200, 5700, 5900, 6000, 6200, 6600, 6800,
    };

    private static readonly long TableTotal = XpToNext.Sum();

    public static long XpForLevel(int level)
    {
        if (level <= 0) return 0;
        if (level <= XpToNext.Length) return XpToNext.Take(level).Sum();
        return TableTotal + (long)(level - XpToNext.Length) * XpToNext[^1];
    }

    public static int LevelForXp(long xp)
    {
        if (xp <= 0) return 0;
        if (xp >= TableTotal)
            return XpToNext.Length + (int)Math.Min(int.MaxValue / 2, (xp - TableTotal) / XpToNext[^1]);

        int level = 0;
        long sum = 0;
        while (level < XpToNext.Length && sum + XpToNext[level] <= xp)
        {
            sum += XpToNext[level];
            level++;
        }
        return level;
    }
}
