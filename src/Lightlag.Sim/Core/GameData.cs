using System.Reflection;
using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

/// <summary>Physical constants in game units: megametres (Mm) and hours.</summary>
public static class Physics
{
    public const double MmPerAu = 149_597.870700;

    /// <summary>Gravitational parameter G*M of one Sun, in Mm^3 per hour^2.</summary>
    public const double GmSun = 1.32712440018e11 / 1e9 * 3600 * 3600;

    /// <summary>Gravitational parameter G*M of one Earth, in Mm^3 per hour^2.</summary>
    public const double GmEarth = 3.986004418e5 / 1e9 * 3600 * 3600;

    /// <summary>Circular orbit period in hours (Kepler's third law).</summary>
    public static double OrbitPeriodHours(double radiusMm, double parentGm) =>
        parentGm > 0 ? 2 * Math.PI * Math.Sqrt(radiusMm * radiusMm * radiusMm / parentGm) : 0;
}

// ---- JSON shapes of the data files ------------------------------------------------------

public sealed record SystemData(string Name, List<BodyData> Bodies, List<BeltData>? Belts, List<StartShipsData>? Ships);

public sealed record BodyData(
    string Id, string Name, BodyKind Kind, string Type, string? Parent,
    double? OrbitAu, double? OrbitMm, double? MassSun, double? MassEarth,
    double RadiusMm, string Color, string? Atmosphere, double? Industry = null)
{
    public double OrbitRadiusMm => OrbitMm ?? (OrbitAu ?? 0) * Physics.MmPerAu;
    public double Gm => (MassSun ?? 0) * Physics.GmSun + (MassEarth ?? 0) * Physics.GmEarth;
}

public sealed record BeltData(string Id, string Name, string Parent, double InnerAu, double OuterAu, string Color);

public sealed record StartShipsData([property: JsonPropertyName("class")] string Class, Side Side, string Near, int Count);

/// <summary>
/// A ship class. Speed in Mm per hour, acceleration in Mm per hour per hour. Supplies are the one stock
/// of fuel and ammunition; every 1 Mm/h of speed gained or lost burns suppliesPerDeltaV of it.
/// </summary>
public sealed record ShipClass(
    string Id, string Name, string Short, string Icon, double MaxSpeedMmPerHour, double AccelMmPerHour2,
    double Supplies, double SuppliesPerDeltaV, double Armour, double Evasion, int Railguns, int PointDefence,
    int Missiles, double Cost, string Role, double SensorMm, double SignatureSize)
{
    /// <summary>Total speed change a full load of supplies buys.</summary>
    public double FullDeltaV => Supplies / SuppliesPerDeltaV;
}

public sealed record RailgunData(double RangeMm, double ReloadSeconds, double Accuracy, double Damage, double SuppliesPerShot);

public sealed record MissileData(double LaunchRangeMm, int Salvo, double SalvoReloadSeconds, double AccelMmPerHour2,
    double DeltaV, double Damage, double LifetimeHours, double HitRadiusMm);

public sealed record PointDefenceData(double RangeMm, double ShotSeconds, double KillChance, double Brain2KillFactor);

/// <summary>Combat numbers shared by every ship. See ship-classes.json for units.</summary>
public sealed record CombatData(
    double ContactRangeMm, double EngageRangeMm, RailgunData Railgun, MissileData Missile, PointDefenceData PointDefence,
    double Brain2DodgeFactor, double ModuleHitChance, double RetreatBelowArmour, double RepairPerDay);

/// <summary>Detection numbers (passive sensors). See ship-classes.json for what each means.</summary>
public sealed record SensorData(double Cold, double Orbit, double Burn, double ExtraShip, double PlanetSensorMm, double ForgetAfterHours);

public sealed record ShipClassFile(List<ShipClass> Classes, CombatData Combat, SensorData Sensors);

/// <summary>
/// Reads the game data files (Data/*.json, built into the simulation) and checks them, so a typo in
/// the data fails loudly at start-up with a clear message instead of misbehaving later.
/// </summary>
public static class GameData
{
    private static readonly Lazy<ShipClassFile> _file = new(LoadShipClassFile);
    private static readonly Lazy<IReadOnlyDictionary<string, ShipClass>> _classes = new(LoadShipClasses);
    private static readonly Lazy<EconomyData> _economy = new(LoadEconomy);
    private static readonly Lazy<IReadOnlyDictionary<string, ResearchData>> _research =
        new(() => _economy.Value.Research.ToDictionary(r => r.Id));

    public static IReadOnlyDictionary<string, ShipClass> ShipClasses => _classes.Value;
    public static CombatData Combat => _file.Value.Combat;
    public static SensorData Sensors => _file.Value.Sensors;
    public static EconomyData Economy => _economy.Value;
    public static IReadOnlyDictionary<string, ResearchData> ResearchById => _research.Value;

    public static ResearchData Research(string id) =>
        ResearchById.TryGetValue(id, out var r) ? r : throw new InvalidDataException($"Unknown research '{id}'");

    public static ShipClass ShipClass(string id) =>
        ShipClasses.TryGetValue(id, out var c) ? c : throw new InvalidDataException($"Unknown ship class '{id}'");

    public static string ReadText(string fileName)
    {
        var asm = typeof(GameData).Assembly;
        using var stream = asm.GetManifestResourceStream("Lightlag.Data." + fileName)
            ?? throw new FileNotFoundException($"Game data file '{fileName}' is not built into {asm.GetName().Name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static SystemData LoadSystem(string fileName)
    {
        var data = Json.Deserialize<SystemData>(ReadText(fileName));
        Validate(data, fileName);
        return data;
    }

    private static ShipClassFile LoadShipClassFile()
    {
        var file = Json.Deserialize<ShipClassFile>(ReadText("ship-classes.json"));
        if (file.Combat == null) throw new InvalidDataException("ship-classes.json: the combat section is missing");
        if (file.Sensors == null) throw new InvalidDataException("ship-classes.json: the sensors section is missing");
        return file;
    }

    private static EconomyData LoadEconomy()
    {
        var e = Json.Deserialize<EconomyData>(ReadText("economy.json"));
        if (e.Shipyard == null || e.Research == null) throw new InvalidDataException("economy.json: the shipyard or research section is missing");
        if (!(e.BuildIndustryPerDay > 0 && e.ClaimDays > 0 && e.HoldToWinDays > 0 && e.StartIndustry >= 0))
            throw new InvalidDataException("economy.json: buildIndustryPerDay, claimDays and holdToWinDays must be above 0");
        if (!(e.Shipyard.Armour > 0 && e.Shipyard.MissileEveryHours >= 1))
            throw new InvalidDataException("economy.json: shipyard armour must be above 0 and missileEveryHours at least 1");
        var ids = new HashSet<string>();
        foreach (var r in e.Research)
        {
            if (!ids.Add(r.Id)) throw new InvalidDataException($"economy.json: research '{r.Id}' is listed twice");
            if (!(r.Cost > 0 && r.Days > 0 && r.Factor > 0)) throw new InvalidDataException($"economy.json: research '{r.Id}' needs cost, days and factor above 0");
        }
        return e;
    }

    private static IReadOnlyDictionary<string, ShipClass> LoadShipClasses()
    {
        var file = _file.Value;
        var dict = new Dictionary<string, ShipClass>();
        foreach (var c in file.Classes)
        {
            if (!dict.TryAdd(c.Id, c)) throw new InvalidDataException($"ship-classes.json: class '{c.Id}' is listed twice");
            if (!(c.MaxSpeedMmPerHour > 0)) throw new InvalidDataException($"ship-classes.json: class '{c.Id}' needs a speed above 0");
            if (!(c.AccelMmPerHour2 > 0)) throw new InvalidDataException($"ship-classes.json: class '{c.Id}' needs an acceleration above 0");
            if (!(c.Supplies > 0 && c.SuppliesPerDeltaV > 0)) throw new InvalidDataException($"ship-classes.json: class '{c.Id}' needs supplies and suppliesPerDeltaV above 0");
            if (!(c.Armour > 0) || c.Evasion is < 0 or >= 1) throw new InvalidDataException($"ship-classes.json: class '{c.Id}' needs armour above 0 and evasion from 0 to below 1");
            if (!(c.SensorMm > 0 && c.SignatureSize > 0)) throw new InvalidDataException($"ship-classes.json: class '{c.Id}' needs sensorMm and signatureSize above 0");
        }
        return dict;
    }

    public static void Validate(SystemData data, string file)
    {
        var seen = new HashSet<string>();
        foreach (var b in data.Bodies)
        {
            if (!seen.Add(b.Id)) throw new InvalidDataException($"{file}: body '{b.Id}' is listed twice");
            if (b.Parent != null)
            {
                if (!seen.Contains(b.Parent))
                    throw new InvalidDataException($"{file}: '{b.Id}' orbits '{b.Parent}', which must be listed before it");
                if (!(b.OrbitRadiusMm > 0)) throw new InvalidDataException($"{file}: '{b.Id}' needs orbitAu or orbitMm above 0");
                var parent = data.Bodies.First(p => p.Id == b.Parent);
                if (!(parent.Gm > 0)) throw new InvalidDataException($"{file}: '{b.Parent}' needs a mass for '{b.Id}' to orbit it");
            }
            if (b.Industry is < 0) throw new InvalidDataException($"{file}: '{b.Id}' has negative industry");
            if (!(b.RadiusMm > 0)) throw new InvalidDataException($"{file}: '{b.Id}' needs radiusMm above 0");
        }
        foreach (var belt in data.Belts ?? new())
            if (!seen.Contains(belt.Parent) || !(belt.OuterAu > belt.InnerAu))
                throw new InvalidDataException($"{file}: belt '{belt.Id}' has a bad parent or radii");
        foreach (var s in data.Ships ?? new())
        {
            ShipClass(s.Class);
            if (!seen.Contains(s.Near)) throw new InvalidDataException($"{file}: ships start near unknown body '{s.Near}'");
        }
    }
}
