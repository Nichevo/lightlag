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

/// <summary>
/// A star system. SizeScale multiplies every body's radius (1 = true size); moons that would end up too
/// close to their bigger planet are moved out (see Scenarios.ClosestMoonOrbit), keeping their order.
/// </summary>
public sealed record SystemData(string Name, List<BodyData> Bodies, List<BeltData>? Belts, List<StartShipsData>? Ships,
    double SizeScale = 1)
{
    /// <summary>A moon orbits at least this many planet radii out, even where its Roche limit is closer.</summary>
    public const double MoonClearance = 2;
}

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
    int Missiles, double Cost, string Role, double SensorMm, double SignatureSize,
    double HeatCapacity = 100, int HeatSinks = 2, double Shield = 0,
    string Needs = "", double LengthM = 100, double Power = 0, double HeatBudget = 0, double Cargo = 0, List<SlotData>? Slots = null,
    double FoodDays = 60, string CargoKind = "", Dictionary<string, double>? Materials = null)
{
    /// <summary>The class's slots (none for old data).</summary>
    public IReadOnlyList<SlotData> SlotList => Slots ?? (IReadOnlyList<SlotData>)Array.Empty<SlotData>();

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
    double Brain2DodgeFactor, double ModuleHitChance, double RetreatBelowArmour, double RepairPerDay,
    double DodgeFactor = 1, double DodgeDeltaV = 0, double DodgeStopsBelow = 0.3, SpreadData? Spread = null, FlagBridgeData? FlagBridge = null,
    WoundsData? Wounds = null);

/// <summary>Damage bars, crippled ships, wrecks and repairs. See ship-classes.json ("wounds").</summary>
public sealed record WoundsData(double BarShare = 1.5, double CrippleHold = 0.4, double CrippledSpeed = 0.25, double KnockedSpeed = 0.5,
    double MendBarsTo = 0.6, double BarRepairPerDay = 0.5, double RepairAlloys = 0.4, double RepairElectronics = 0.3, double NoMaterialsPace = 0.25,
    double WreckShare = 0.4, double WreckDays = 90, double SalvageRangeMm = 100, double CrewMendPerDay = 0.1);

/// <summary>The flagship's fleet fights better (light lag only): faster reloads, linked point defence, better aimed railguns.</summary>
public sealed record FlagBridgeData(double ReloadFactor = 1, double PointDefenceFactor = 1, double AccuracyFactor = 1);

/// <summary>Spread railgun fire and hard-burn dodging. See ship-classes.json.</summary>
public sealed record SpreadData(int SlugsPerGun, double Accuracy, double Cooldown, double FanMm, double FocusFanMm,
    double SidestepVsSpread, double HardVsFocused, double HardVsSpread, double HardCost,
    double SlugSpeedMmPerHour = 180, double FullDodgeSeconds = 80);

/// <summary>Detection numbers (passive sensors). See ship-classes.json for what each means.</summary>
public sealed record SensorData(double Cold, double Orbit, double Burn, double ExtraShip, double PlanetSensorMm, double ForgetAfterHours,
    double Warm = 1, double WarmHours = 0, double IdentifyMm = 0,
    double CuedFactor = 1, double CueRadiusMm = 0, double CueGrowthMmPerHour = 0, double PlanetBareSensorMm = 0, double SoloCold = 1);

/// <summary>A hull slot: W weapon, D defence, U utility, E engine. A pair is two mirrored mounts with one module type. Fit is the standard module ("" empty).</summary>
public sealed record SlotData(string Type, bool Pair = false, string Fit = "")
{
    public int Mounts => Pair ? 2 : 1;
}

/// <summary>A module for a slot. Its numbers are added to the ship per mount (see Fitting).</summary>
public sealed record ModuleData(string Id, string Name, string Slot, double Power, double Heat, double Cost,
    int Railguns = 0, int PointDefence = 0, int Missiles = 0, double Shield = 0, double Armour = 0, double SensorMm = 0,
    double Supplies = 0, int HeatSinks = 0, double HeatCapacity = 0, double AccelFactor = 1, double BurnHeatFactor = 1, string Needs = "");

/// <summary>
/// Slingshot tuning (game physics: real flybys barely touch a fast torch ship, so giants hit harder).
/// A planet's weight for a swing is the square root of (its mass / GiantEarthMasses), at most 1: about 1 for
/// Tamber, 0.55 for Coldwell, 0.2 for Azure and 0.06 for an Earth-sized planet. AssistStrength
/// multiplies a planet's pull for the bend, scaled by that weight, and a swing adds up to GiantKickKmS
/// of speed, also scaled by it. DepartShares are the burn levels tried for the stretch to the planet:
/// a gentler burn there leaves the swing to add the speed.
/// </summary>
public sealed record SlingshotData(double AssistStrength = 40, double MinTimeGain = 0.02, double MinFuelGain = 0.06,
    double FuelHourWeight = 0.5, List<double>? DepartShares = null, List<double>? FuelSaverShares = null,
    double GiantEarthMasses = 318, double GiantKickKmS = 30)
{
    /// <summary>How much a planet counts for a swing (0..1), by its mass.</summary>
    public double Weight(double gm) => Math.Min(1, Math.Sqrt(Math.Max(0, gm / Physics.GmEarth / GiantEarthMasses)));

    /// <summary>Speed settings tried for a slingshot, as shares of the one asked for (lower ones save fuel).</summary>
    public IReadOnlyList<double> FuelSaverSpeeds => FuelSaverShares is { Count: > 0 } f ? f : new[] { 1.0, 0.7 };

    public IReadOnlyList<double> Departs => DepartShares is { Count: > 0 } d ? d : new[] { 1.0, 0.75, 0.55 };
}

public sealed record ShipClassFile(List<ShipClass> Classes, CombatData Combat, SensorData Sensors, LightData? Light = null, FlagshipData? Flagships = null, DroneData? Drones = null, BattleRecordData? Recording = null, HeatData? Heat = null, List<ModuleData>? Modules = null, SlingshotData? Slingshots = null, FormationData? Formations = null);

/// <summary>
/// Reads the game data files (Data/*.json, built into the simulation) and checks them, so a typo in
/// the data fails loudly at start-up with a clear message instead of misbehaving later.
/// </summary>
public static class GameData
{
    private static readonly Lazy<ShipClassFile> _file = new(LoadShipClassFile);
    private static readonly Lazy<IReadOnlyDictionary<string, ShipClass>> _classes = new(LoadShipClasses);
    private static readonly Lazy<EconomyData> _economy = new(LoadEconomy);
    private static readonly Lazy<AiData> _ai = new(LoadAi);
    private static readonly Lazy<IReadOnlyDictionary<string, ResearchData>> _research =
        new(() => _economy.Value.Research.ToDictionary(r => r.Id));

    public static IReadOnlyDictionary<string, ShipClass> ShipClasses => _classes.Value;
    private static readonly Lazy<IReadOnlyDictionary<string, ModuleData>> _modules =
        new(() => (_file.Value.Modules ?? new List<ModuleData>()).ToDictionary(m => m.Id));
    public static IReadOnlyDictionary<string, ModuleData> Modules => _modules.Value;
    public static ModuleData? Module(string id) => id.Length > 0 && Modules.TryGetValue(id, out var m) ? m : null;
    public static CombatData Combat => _file.Value.Combat;
    public static SensorData Sensors => _file.Value.Sensors;
    public static LightData Light => _file.Value.Light ?? new LightData();
    public static FlagshipData Flagships => _file.Value.Flagships ?? new FlagshipData();
    public static DroneData Drones => _file.Value.Drones ?? new DroneData();
    public static SlingshotData Slingshots => _file.Value.Slingshots ?? new SlingshotData();
    public static HeatData Heat => _file.Value.Heat ?? new HeatData();
    public static BattleRecordData Recording => _file.Value.Recording ?? new BattleRecordData();
    public static FormationData Formations => _file.Value.Formations ?? new FormationData();
    public static EconomyData Economy => _economy.Value;
    public static AiData Ai => _ai.Value;
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
            if (Array.IndexOf(Core.Research.Fields, r.Field) < 0) throw new InvalidDataException($"economy.json: research '{r.Id}' has unknown field '{r.Field}'");
        }
        foreach (var r in e.Research)
            foreach (var need in r.Needs ?? new List<string>())
                if (!ids.Contains(need) || need == r.Id) throw new InvalidDataException($"economy.json: research '{r.Id}' needs unknown '{need}'");
        if (e.Teams is { } t && !(t.Slots >= 1 && t.StartScientists > 0 && t.BaseScientists > 0 && t.MaxSkill >= 1 && t.SurveyCut is >= 0 and < 1))
            throw new InvalidDataException("economy.json: teams needs slots of at least 1, scientists above 0, maxSkill of at least 1 and surveyCut in [0, 1)");
        return e;
    }

    private static AiData LoadAi()
    {
        var a = Json.Deserialize<AiData>(ReadText("ai.json"));
        if (a.ThinkHours == null || a.Easy == null || a.AttackRatio == null || a.FirstProbeDay == null || a.Personalities == null)
            throw new InvalidDataException("ai.json: thinkHours, easy, attackRatio, firstProbeDay or personalities is missing");
        if (!(a.ThinkHours.Easy >= 1 && a.ThinkHours.Normal >= 1)) throw new InvalidDataException("ai.json: thinkHours must be at least 1");
        if (!(a.AttackRatio.Min >= 1 && a.AttackRatio.Max >= a.AttackRatio.Min)) throw new InvalidDataException("ai.json: attackRatio needs 1 <= min <= max");
        foreach (var p in Enum.GetValues<Personality>())
        {
            if (!a.Personalities.TryGetValue(p.ToString().ToLowerInvariant(), out var d))
                throw new InvalidDataException($"ai.json: personality '{p.ToString().ToLowerInvariant()}' is missing");
            if (d.ClaimGroups < 1) throw new InvalidDataException($"ai.json: {d.Name} needs claimGroups of at least 1");
            foreach (var r in d.Research)
                if (!ResearchById.ContainsKey(r)) throw new InvalidDataException($"ai.json: {d.Name} lists unknown research '{r}'");
        }
        return a;
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
            var modules = (file.Modules ?? new List<ModuleData>()).ToDictionary(m => m.Id);
            foreach (var slot in c.SlotList)
            {
                if (slot.Type is not ("W" or "D" or "U" or "E")) throw new InvalidDataException($"ship-classes.json: class '{c.Id}' has a slot of unknown type '{slot.Type}'");
                if (slot.Fit.Length > 0 && (!modules.TryGetValue(slot.Fit, out var m) || m.Slot != slot.Type))
                    throw new InvalidDataException($"ship-classes.json: class '{c.Id}' fits '{slot.Fit}' in a {slot.Type} slot");
            }
            if (c.SlotList.Count > 0)
            {
                var std = c.SlotList.Select(x => x.Fit).ToList();
                if (Fitting.PowerUsed(c, std) > c.Power || Fitting.HeatUsed(c, std) > c.HeatBudget)
                    throw new InvalidDataException($"ship-classes.json: class '{c.Id}' standard fit is over its power or heat budget");
            }
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
