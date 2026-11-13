namespace Lightlag.Sim.Core;

// ---- Data (Data/races.json) -----------------------------------------------------------------

/// <summary>A race a side can play: its picker text and its factors on the usual rules (1: as humans). See races.json.</summary>
public sealed record RaceData(string Id, string Name, string Short = "", string Blurb = "", string Style = "",
    List<string>? Pros = null, List<string>? Cons = null,
    double FoodNeed = 1, double Growth = 1, double BiomassPerIndustry = 0, double ShipMaterials = 1, double HullHealPerDay = 0,
    double CrewPop = 1, bool CrewRanks = true, double Strain = 1, double Boarding = 1, double Sensors = 1,
    Dictionary<string, double>? ResearchDays = null, double DigDays = 1, double BombArmour = 1, double GroundDefence = 1,
    List<string>? StartBuildings = null, Dictionary<string, double>? Personalities = null,
    bool AiOnly = false, double PowerUse = 1, double Precision = 1, double MineralIndustry = 1, double AirlessCap = 1,
    double Cyber = 1, bool NoCaptures = false, bool NeedsPowerToGrow = false, bool ListenOnly = true, string Colour = "",
    string? Drawings = null, Dictionary<string, double>? ResearchLean = null)
{
    /// <summary>The race's lean on a research aim (see ResearchValue): 1 when it has none.</summary>
    public double Lean(string aim) => ResearchLean != null && ResearchLean.TryGetValue(aim, out var f) ? f : 1;

    /// <summary>Machines: no people to feed (see FoodNeed 0).</summary>
    public bool Eats => FoodNeed > 0;

    /// <summary>Ships are grown from biomass (food from the yard's store).</summary>
    public bool Grown => BiomassPerIndustry > 0;

    /// <summary>The factor on an idea's days for its research group.</summary>
    public double ResearchFactor(string unlocks) => ResearchDays != null && ResearchDays.TryGetValue(unlocks, out var f) ? f : 1;

    /// <summary>The weight on the rival's roll of this personality.</summary>
    public double PersonalityWeight(Personality p) =>
        Personalities != null && Personalities.TryGetValue(p.ToString().ToLowerInvariant(), out var f) ? f : 1;
}

public sealed record RacesData(List<RaceData> Races);

/// <summary>
/// Races. Each side plays one, picked when the game starts (or at random, from the seed). Humans are the
/// baseline; the Hive is an insect collective: many people who eat a lot, ships grown from biomass with
/// hulls that heal, drone crews that never rank up, swarm boarding, buildings dug in against bombing and
/// invasion, and weak sensors. The race is kept on the side (SideState.Race) so saves and replays keep it.
/// </summary>
public static class Races
{
    public const string Human = "human", Hive = "hive", Lattice = "lattice", Random = "random", None = "none";

    private static readonly Lazy<RacesData> _data = new(Load);
    public static IReadOnlyList<RaceData> All => _data.Value.Races;

    public static RaceData Get(string? id) => All.FirstOrDefault(r => r.Id == id) ?? All.First(r => r.Id == Human);

    public static bool Known(string? id) => All.Any(r => r.Id == id);

    public static RaceData Of(World w, Side side) => Get(w.Sides[(int)side].Race);

    /// <summary>The race of a planet's people: its holder's (neutral planets live like humans).</summary>
    public static RaceData OfBody(World w, Body b) => b.Owner is Side s ? Of(w, s) : Get(Human);

    public static bool IsHive(World w, Side side) => Of(w, side).Id == Hive;

    public static bool IsLattice(World w, Side side) => Of(w, side).Id == Lattice;

    /// <summary>A body with no air (rock, ice, asteroid): the Lattice settles these without habitats (see AirlessCap).</summary>
    public static bool Airless(Body b) => string.IsNullOrEmpty(b.Atmosphere) && b.Kind != BodyKind.Star && b.Type is not ("gas" or "ice-giant");

    /// <summary>The picker's choices: each race, then Random.</summary>
    public static IEnumerable<(string Id, string Name)> Choices() => All.Select(r => (r.Id, r.Name)).Append((Random, "Random"));

    /// <summary>
    /// Sets each side's race from the game options (missing: human). "random" is rolled from the seed on a
    /// stream of its own, so the map and the rival's other rolls stay as they were.
    /// </summary>
    public static void Setup(World w, GameOptions? options, ulong seed)
    {
        foreach (var side in Sides.InPlay(w))
        {
            var asked = side switch { Side.Player => options?.PlayerRace, Side.Rival => options?.RivalRace, _ => options?.ThirdRace };
            var id = asked ?? Human;
            if (id == Random)
            {
                var rng = new SimRandom(seed, stream: 31 + (ulong)side);
                id = All[rng.Range(0, All.Count)].Id;
            }
            if (!Known(id)) throw new ArgumentException($"Unknown race '{id}'. Known: {string.Join(", ", All.Select(r => r.Id))}, random");
            w.Sides[(int)side].Race = id;
            // Drone crews (no ranks) are bred trained: the start ships' crews too.
            if (!Get(id).CrewRanks)
                foreach (var s in w.Ships.Where(s => s.Side == side && Crews.Crewed(s))) s.Xp = Crews.DroneXp;
            // Machine races run every ship on AI cores: the start ships too.
            if (Get(id).AiOnly)
                foreach (var s in w.Ships.Where(s => s.Side == side)) { s.AiRun = true; s.Xp = 0; s.Food = 0; }
            if (side == Side.Third)
                foreach (var f in w.Fleets.Where(f => f.Side == side && f.Name.Contains(" fleet ")))
                    f.Name = $"{Get(id).Short} fleet {f.Name[(f.Name.LastIndexOf(' ') + 1)..]}";
        }
    }

    /// <summary>Biomass (food) a ship order of this price takes from the yard's store, for this side.</summary>
    public static double BiomassFor(World w, Side side, double industry) => Math.Round(industry * Of(w, side).BiomassPerIndustry, 2);

    /// <summary>The planets whose food stores a yard can draw on: its side's planets that share food (none blockaded), or only itself when cut off.</summary>
    private static IEnumerable<Body> Pool(World w, Body yard) =>
        yard.Owner is Side side && !Economy.Blockaded(w, yard)
            ? w.Bodies.Where(b => b.Owner == side && !Economy.Blockaded(w, b))
            : new[] { yard };

    /// <summary>Food the yard can draw on for growing ships: the shared store of its side's linked planets.</summary>
    public static double BiomassAt(World w, Body yard) => Pool(w, yard).Sum(b => b.FoodStock);

    /// <summary>Takes biomass out of the shared store (each planet gives its share), after BiomassAt said there is enough.</summary>
    public static void TakeBiomass(World w, Body yard, double amount)
    {
        var pool = Pool(w, yard).ToList();
        var total = pool.Sum(b => b.FoodStock);
        if (total <= 0 || amount <= 0) return;
        var keep = Math.Max(0, 1 - amount / total);
        foreach (var b in pool) b.FoodStock *= keep;
    }

    /// <summary>
    /// Food a day a side's farm planner aims for: what its people eat with a margin, and for a race that grows
    /// its ships, biomass for about half its Industry going into ships.
    /// </summary>
    public static double FoodWanted(World w, Side side, IEnumerable<Body> linked) =>
        !Of(w, side).Eats && !Of(w, side).Grown ? 0
            : linked.Sum(Planets.FoodNeedPerDay) * 1.15 + 0.1 + BiomassFor(w, side, Economy.IncomePerDay(w, side) * 0.5);

    /// <summary>A grown hull has food aboard to heal with (always, with supply lines off or on a ship that carries none).</summary>
    public static bool Feeds(World w, Ship s) => !w.Rules.SupplyLines || SupplyLines.MaxFood(s) <= 0 || s.Food > 1e-9;

    /// <summary>Hours to put up a building on this body (the Hive digs in, so it takes longer).</summary>
    public static double BuildHours(World w, Body b, BuildingData data) => data.Days * 24 * OfBody(w, b).DigDays;

    /// <summary>The materials of a fit for this side's race (grown hulls need fewer).</summary>
    public static Dictionary<string, double>? Materials(World w, Side side, Dictionary<string, double>? mats)
    {
        var f = Of(w, side).ShipMaterials;
        if (mats == null || Math.Abs(f - 1) < 1e-9) return mats;
        return mats.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value * f, 2));
    }

    /// <summary>Why a grown ship order can't start or go on at this yard (no biomass, or a starving Hive), or null.</summary>
    public static string? GrowStatus(World w, Side side, Body yard, double biomass, bool started)
    {
        if (!Of(w, side).Grown) return null;
        if (yard.Hungry) return "Brood yard starving: no ships grow until the planets are fed";
        if (!started && biomass > 0 && BiomassAt(w, yard) < biomass - 1e-9)
            return $"needs {biomass - BiomassAt(w, yard):0.#} more biomass (food) at {yard.Name}";
        return null;
    }

    private static RacesData Load()
    {
        var d = Json.Deserialize<RacesData>(GameData.ReadText("races.json"));
        if (d.Races == null || d.Races.Count == 0) throw new InvalidDataException("races.json: no races");
        if (d.Races.All(r => r.Id != Human)) throw new InvalidDataException("races.json: the human race is missing");
        var ids = new HashSet<string>();
        foreach (var r in d.Races)
        {
            if (!ids.Add(r.Id) || r.Id == Random) throw new InvalidDataException($"races.json: race '{r.Id}' is listed twice or uses a reserved id");
            if (!(r.FoodNeed >= 0 && r.Growth > 0 && r.PowerUse > 0 && r.Precision > 0 && r.MineralIndustry > 0 && r.AirlessCap > 0 && r.Cyber > 0 && r.BiomassPerIndustry >= 0 && r.ShipMaterials >= 0 && r.HullHealPerDay >= 0 && r.CrewPop >= 0
                  && r.Strain >= 0 && r.Boarding > 0 && r.Sensors > 0 && r.DigDays > 0 && r.BombArmour > 0 && r.GroundDefence > 0))
                throw new InvalidDataException($"races.json: race '{r.Id}' has a factor out of range");
            foreach (var p in r.Personalities?.Keys ?? Enumerable.Empty<string>())
                if (!Enum.GetValues<Personality>().Any(x => x.ToString().ToLowerInvariant() == p))
                    throw new InvalidDataException($"races.json: race '{r.Id}' weights unknown personality '{p}'");
        }
        return d;
    }
}
