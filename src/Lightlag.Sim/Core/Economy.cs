using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (Data/economy.json) -----------------------------------------------------------

public sealed record ShipyardData(double Armour, double RepairPerDay, double RailDamagePerHour, double RailSuppliesPerHour,
    double MissileDamage, double MissileEveryHours);

/// <summary>
/// One research idea. What Factor does depends on the idea (see Tech). Field is the team skill it uses
/// and grows; Tier (1-5) places it in the research web; Needs lists ideas that must be researched (or
/// queued) first; Unlocks groups ideas for the research screen's filters (hulls, engines, weapons, defence,
/// supply, sensors, industry, colonies); Picture names the drawing on its card. Materials are taken from
/// the home planet's store when its team starts it (T3 and up). Spark names an event that brings it into
/// view early (or speeds it up, see Sparks). Later ideas sit in the web but can't be researched until
/// the system they belong to is built.
/// </summary>
public sealed record ResearchData(string Id, string Name, string Effect, double Cost, double Days, double Factor,
    string Field = "drives", List<string>? Needs = null, string Unlocks = "", string Picture = "", string Description = "",
    int Tier = 1, Dictionary<string, double>? Materials = null, string? Spark = null, bool Later = false);

public sealed record EconomyData(double StartIndustry, double BuildIndustryPerDay, double ClaimDays, double HoldToWinDays,
    ShipyardData Shipyard, List<ResearchData> Research, double ClaimHeldDays = 0, List<StationData>? Stations = null, double RelayDelayHours = 0.1,
    TeamsData? Teams = null, PlanetsData? Planets = null, ColoniesData? Colonies = null, WarData? War = null);

/// <summary>A station that can be built in orbit of a body you hold (economy.json, "stations").</summary>
public sealed record StationData(string Id, string Name, string Effect, double Cost, double Days, string? Needs = null,
    List<string>? OnTypes = null, Dictionary<string, double>? Makes = null);

/// <summary>
/// A station in orbit of a body: a relay passes signals round planets that block them; a sensor
/// station lets the body see as far as the home planet. It works from ReadyHour, and is lost with the body.
/// </summary>
public sealed class Station
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public string Kind { get; set; } = "";
    public int BodyId { get; set; }
    public double ReadyHour { get; set; }
}

// ---- State --------------------------------------------------------------------------------

/// <summary>What one side has: its Industry stock, what it has researched, and how long it has held most planets.</summary>
public sealed class SideState
{
    public Side Side { get; set; }
    public double Industry { get; set; }

    /// <summary>Free freighters carry goods where planets ask for them (see Colonies), and freighters lost with cargo.</summary>
    public bool AutoTransport { get; set; } = true;
    public int CargoLost { get; set; }

    /// <summary>Route settings, recent deliveries and losses for the supply chain screen (see SupplyChain).</summary>
    public SupplyBook Supply { get; set; } = new();
    public List<string> Researched { get; set; } = new();

    /// <summary>Research teams (see Research). The first is made when the game starts.</summary>
    public List<ResearchTeam> Teams { get; set; } = new();
    public int TeamsFounded { get; set; }

    /// <summary>Ideas a spark brought into view early (see Sparks), and the news of each spark.</summary>
    public List<string> Sparked { get; set; } = new();
    public List<SparkNote> SparkNotes { get; set; } = new();

    /// <summary>Ideas a survey find has sped up, and the finds themselves (for the discovery web).</summary>
    public List<string> Cuts { get; set; } = new();
    public List<SurveyFind> Finds { get; set; } = new();

    /// <summary>Variants of each class (see Designer): Auto ones and your own. The Standard (id 0) is not stored.</summary>
    public List<Design> Designs { get; set; } = new();
    public int NextDesignId { get; set; } = 1;

    /// <summary>The variant yards build per class when no other is asked for (missing: the Standard).</summary>
    public Dictionary<string, int> DefaultDesign { get; set; } = new();

    /// <summary>Each class's Standard as last kept (live on Auto; frozen with Advise or Manual until updated).</summary>
    public Dictionary<string, List<string>> StandardFits { get; set; } = new();

    /// <summary>Classes whose new ships are AI-run (AI cores): no crew, no food, no strain, no ranks.</summary>
    public List<string> AiRun { get; set; } = new();

    /// <summary>Where the other side's Q-ships gave themselves away: this side's raiders stay wary there for a while.</summary>
    public List<QShipWarning> QShipWarnings { get; set; } = new();

    /// <summary>Lifeboats this side lost (air ran out), in k of crew.</summary>
    public double CrewsLostK { get; set; }

    /// <summary>The ship command sits on (see Light); null once none is left.</summary>
    public int? FlagshipId { get; set; }

    /// <summary>The autonomy new fleets start with.</summary>
    public AutonomyLevel DefaultAutonomy { get; set; } = AutonomyLevel.Sensible;

    /// <summary>No orders leave the flagship (they are held), and its signal traffic can't be followed.</summary>
    public bool RadioSilence { get; set; }

    /// <summary>After the flagship is lost far from its successor: no orders go out until this hour (fleets act on their own).</summary>
    public double CommandGapUntil { get; set; } = -1e9;

    /// <summary>When this side reached the tip share of control (null while it hasn't; see Planets.Control).</summary>
    public double? MajoritySinceHour { get; set; }

    /// <summary>Running totals for the game statistics (see Stats).</summary>
    public SideTotals Totals { get; set; } = new();

    // ---- Automation (see Stewards) ----

    /// <summary>Each area's level (missing: the area's default), and levels for single planets or fleets.</summary>
    public Dictionary<AutoArea, AutoLevel> Auto { get; set; } = new();
    public List<AutoOverride> AutoOverrides { get; set; } = new();

    /// <summary>The Needs you list, and items dismissed until an hour.</summary>
    public List<NeedItem> Needs { get; set; } = new();
    public Dictionary<string, double> Dismissed { get; set; } = new();

    /// <summary>What automation did lately, and how much (all game, and today).</summary>
    public List<AutoNote> AutoNotes { get; set; } = new();
    public int AutoHandled { get; set; }
    public int AutoHandledToday { get; set; }
    public int AutoDay { get; set; }
    public double NextAutoBuildHour { get; set; }

    /// <summary>Warships lost lately (offered for rebuilding; never rebuilt by themselves).</summary>
    public List<LostShip> Lost { get; set; } = new();

    /// <summary>Learning (see Stewards.Learn): auto actions waiting to be judged, how each area's turned out, and the player's habits.</summary>
    public List<AutoCheck> AutoChecks { get; set; } = new();
    public Dictionary<AutoArea, AutoScore> AutoScores { get; set; } = new();
    public Dictionary<string, int> Habits { get; set; } = new();

    /// <summary>Pull-back presets the player made Auto's default for a situation (see BattleRules.AutoPick).</summary>
    public Dictionary<string, PullPreset> PullDefaults { get; set; } = new();

    /// <summary>Crews rescued from pods, waiting at home to man new ships (their experience; see Crews).</summary>
    public List<double> ReserveCrews { get; set; } = new();

    /// <summary>What this side has learned of the other from prisoners, captures and wrecks (see Spoils).</summary>
    public SpoilsState Spoils { get; set; } = new();

    /// <summary>Ship names (see ShipNames): the side's race, and per class the fits it has built (index = variant letter).</summary>
    public string Race { get; set; } = ShipNames.DefaultRace;
    public Dictionary<string, List<string>> Variants { get; set; } = new();

    /// <summary>Weapons this side keeps secret (see Secrecy); null: Auto, its two newest.</summary>
    public List<string>? Advanced { get; set; }

    /// <summary>Rival ideas being learned from what was seen of them, 0 to 1 (see RivalTech): Seen, Studied, then Unlocked.</summary>
    public Dictionary<string, double> RivalIdeas { get; set; } = new();

    /// <summary>What this side has learned about its rival (see Intel).</summary>
    public IntelFile Intel { get; set; } = new();

    /// <summary>Events (see Events): when the next one comes (0: not rolled yet); a flare sheltered from (sensors blind) or used to hide, until.</summary>
    public double NextEventHour { get; set; }
    public double BlindUntilHour { get; set; } = -1e9;
    public double HiddenUntilHour { get; set; } = -1e9;
}

public enum OrderKind { Ship, Research }

/// <summary>A ship being built at the shipyard, or an upgrade being researched. Paid for when it starts.</summary>
public sealed class WorkOrder
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public OrderKind Kind { get; set; }

    /// <summary>The ship class id or research id.</summary>
    public string Key { get; set; } = "";
    public bool Paid { get; set; }

    /// <summary>Ship orders: waiting because an enemy fleet in orbit blockades the shipyard.</summary>
    public bool Blockaded { get; set; }

    /// <summary>Ship orders: the dry dock planet building it (null: the shipyard).</summary>
    public int? YardBodyId { get; set; }

    /// <summary>Why it can't start yet (Industry or materials short), for the queue; null once started or nothing stops it.</summary>
    public string? Waiting { get; set; }

    /// <summary>Placed by an Auto manager rather than by hand (hand orders stay pinned: Auto never moves or cancels them).</summary>
    public bool ByAuto { get; set; }

    /// <summary>Ship orders: the fleet the new ship flies out to join (null: the fleet at the shipyard).</summary>
    public int? JoinFleetId { get; set; }

    /// <summary>Ship orders: the variant to build (null: the class default when it starts; 0: the Standard), and its price when queued (null: the class's).</summary>
    public int? DesignId { get; set; }
    public double? FitCost { get; set; }
    public Dictionary<string, double>? FitMaterials { get; set; }

    /// <summary>Settle orders: the planet the settler ship carries people to, the fleet that escorts it, and the planet's name.</summary>
    public int? SettleBodyId { get; set; }
    public int? EscortFleetId { get; set; }
    public string? SettleName { get; set; }

    /// <summary>Settle orders placed by Auto top-up rather than by hand.</summary>
    public bool Auto { get; set; }

    /// <summary>Research orders: the team working on it. HoursLeft counts base hours; a team gets through Research.Speed of them an hour.</summary>
    public int? TeamId { get; set; }
    public double HoursLeft { get; set; }
    public double TotalHours { get; set; }
}

// ---- Research effects ---------------------------------------------------------------------

/// <summary>Where research changes the numbers. Every place that reads an upgradable number asks here.</summary>
public static class Tech
{
    public const string MissileBrain2 = "missile-brain-2";
    public const string PointDefence2 = "point-defence-2";
    public const string HeavyArmour = "heavy-armour";
    public const string EfficientDrives = "efficient-drives";
    public const string LongSensors = "long-sensors";
    public const string QuietDrives = "quiet-drives";
    public const string FastRailguns = "fast-railguns";
    public const string Automation = "automation";
    public const string CommandUplink = "command-uplink";
    public const string TightBeam = "tight-beam";
    public const string DeflectorGrid = "deflector-grid";

    /// <summary>Research that only matters with light lag on.</summary>
    public static bool LagOnly(string id) => id is CommandUplink or TightBeam;

    public static bool Has(World w, Side side, string id) => w.Sides[(int)side].Researched.Contains(id);

    /// <summary>The upgrade's factor once researched, otherwise 1.</summary>
    public static double Factor(World w, Side side, string id) => Has(w, side, id) ? GameData.Research(id).Factor : 1;

    public const string ArmourMk3 = "armour-mk3";
    public const string MissileBrain3 = "missile-brain-3";
    public const string MissileBrain4 = "missile-brain-4";
    public const string PointDefence3 = "point-defence-3";
    public const string CounterMissiles = "counter-missiles";
    public const string Spoofing = "spoofing";
    public const string RailgunMk3 = "railgun-mk3";
    public const string ShieldsMk3 = "shields-mk3";
    public const string FastShipyard = "fast-shipyard";
    public const string SignalsAnalysis = "signals-analysis";
    public const string LargeTanks = "large-tanks";
    public const string EngineMk2 = "engine-mk2";
    public const string EngineMk3 = "engine-mk3";
    public const string EscortHull = "escort-hull";
    public const string CruiserHull = "cruiser-hull";
    public const string TankerHull = "tanker-hull";
    public const string FreighterHull = "freighter-hull";
    public const string FoodRecyclers = "food-recyclers";
    public const string EfficientUpkeep = "efficient-upkeep";

    public const string MissileBrain5 = "missile-brain-5";

    /// <summary>Missile guidance level from research: 1, or the highest brain researched.</summary>
    public static int MissileBrain(World w, Side side) =>
        Has(w, side, MissileBrain5) ? 5 : Has(w, side, MissileBrain4) ? 4 : Has(w, side, MissileBrain3) ? 3 : Has(w, side, MissileBrain2) ? 2 : 1;

    /// <summary>What finishing an idea changes at once.</summary>
    public static void OnLearn(Simulation sim, Side side, string id)
    {
        var w = sim.World;
        if (id is MissileBrain2 or MissileBrain3 or MissileBrain4 or MissileBrain5)
            w.MissileBrain[(int)side] = Math.Max(w.MissileBrain[(int)side], MissileBrain(w, side));
        // Assays: every world held shows one more deposit.
        if (id == "rare-element-assays")
            foreach (var b in w.Bodies.Where(b => b.Owner == side && b.Board != null)) Colonies.ExtraDeposit(w, b);
        Refresh(w);
    }

    /// <summary>Hourly (and when research lands): the per-ship numbers research changes that are read where there is no world at hand.</summary>
    public static void Refresh(World w)
    {
        foreach (var s in w.Ships)
        {
            s.TechHeat = Factor(w, s.Side, "heat-sink-tanks") * Factor(w, s.Side, "heat-sink-armour");
            s.TechSinks = Has(w, s.Side, "ejectable-heat-sinks") ? 1 : 0;
            s.TechPeople = Has(w, s.Side, "cryo-sleep") ? GameData.Research("cryo-sleep").Factor : 1;
        }
        foreach (var b in w.Bodies)
        {
            b.BunkerFactor = b.Owner is Side side && Has(w, side, "hardened-bunkers") ? 1.5 : 1;
            // Low-gravity medicine: asteroids and small moons hold half again as many people.
            b.CapFactor = b.Owner is Side holder && Has(w, holder, "low-g-medicine") && b.Kind is BodyKind.Asteroid or BodyKind.Moon
                          && Planets.TypeOf(b).Cap <= 30 ? GameData.Research("low-g-medicine").Factor : 1;
        }
    }

    public static double MaxArmour(World w, Ship s) => Fitting.Spec(s).Armour * Factor(w, s.Side, HeavyArmour) * Factor(w, s.Side, ArmourMk3);

    /// <summary>Fuel a burn takes: Efficient drives, and the fusion drive (Engine Mk IV and V) burns 15 % less.</summary>
    public static double SuppliesPerDeltaV(World w, Ship s) =>
        Fitting.Spec(s).SuppliesPerDeltaV * Factor(w, s.Side, EfficientDrives) * (FusionEngine(s) ? 0.85 : 1) / Fuels.Reach(w, s);

    private static bool FusionEngine(Ship s) => s.Fit.Any(m => m is "engine-mk4" or "engine-mk5" or "engine-mk6");

    /// <summary>A big hull (cruiser and up): Siege rail works only on these.</summary>
    public static bool BigHull(Ship s) => s.ClassId is "cruiser" or "siege-monitor" or "capital";

    /// <summary>A railgun slug's damage: Railgun Mk III, and Siege rail on big hulls.</summary>
    /// <summary>Slug damage: Railgun Mk III needs Mk III slugs loaded (see Munitions); Siege rail on big hulls.</summary>
    public static double RailDamage(World w, Ship s) =>
        GameData.Combat.Railgun.Damage * (s.RailGrade >= 2 ? Factor(w, s.Side, RailgunMk3) : 1) * (BigHull(s) ? Factor(w, s.Side, "railgun-mk5") : 1);

    /// <summary>Railgun reach: Coil-boosted rail adds 20 %.</summary>
    public static double RailRange(World w, Side side) => GameData.Combat.Railgun.RangeMm * Factor(w, side, "railgun-mk4");

    /// <summary>Railgun reload time factor: Fast railguns and Capacitor banks.</summary>
    public static double ReloadFactor(World w, Side side) => Factor(w, side, FastRailguns) * Factor(w, side, "capacitor-banks");

    /// <summary>A missile's damage: Heavy warheads add 30 %.</summary>
    public static double MissileDamage(World w, Side side) => GameData.Combat.Missile.Damage * Factor(w, side, "heavy-warheads");

    /// <summary>Missiles a ship's racks hold: Heavy warheads hold 20 % fewer.</summary>
    public static int Racks(World w, Ship s) =>
        Has(w, s.Side, "heavy-warheads") ? (int)Math.Round(Fitting.Spec(s).Missiles * 0.8) : Fitting.Spec(s).Missiles;

    /// <summary>
    /// Point defence kill chance factor for a side against a missile of this brain: Mk II and III, counter-missiles,
    /// fleet data links, predictive fire control (brain 3 and up), capacitor banks (faster recharge) and free-electron lasers.
    /// </summary>
    public static double PointDefence(World w, Side side, int brain) =>
        Factor(w, side, PointDefence2) * Factor(w, side, PointDefence3) * Factor(w, side, CounterMissiles) * Factor(w, side, "fleet-data-links")
        * (brain >= 3 ? Factor(w, side, "predictive-fire-control") : 1) * (Has(w, side, "capacitor-banks") ? 1.1 : 1) * Factor(w, side, "free-electron-lasers");

    /// <summary>Signature factor from research for a fleet seen by the enemy: Jamming (seen from 30 % closer) and Stealth coatings when cold.</summary>
    public static double SignatureFactor(World w, Side side, bool burning)
    {
        if (!w.Loop) return 1;
        var range = Factor(w, side, "jamming") * (burning ? 1 : Factor(w, side, "stealth-coatings"));
        return range * range; // range goes with the square root of the signature
    }
}

// ---- The loop -----------------------------------------------------------------------------

/// <summary>
/// The game loop around the battles: Industry income from held planets, the shipyard build queue,
/// research, planet claims, shipyard bombardment and the win check. Runs once per game hour, only in
/// full games (not the battle sandbox).
/// </summary>
public static class Economy
{
    private static EconomyData E => GameData.Economy;

    public static SideState Of(World w, Side side) => w.Sides[(int)side];

    /// <summary>The side's shipyard planet, if it still has one.</summary>
    public static Body? ShipyardOf(World w, Side side) => w.Bodies.Find(b => b.Shipyard && b.Owner == side);

    /// <summary>Where a side's fleets go to refill and repair: the shipyard, or else any planet it holds.</summary>
    public static Body? HomeOf(World w, Side side) => ShipyardOf(w, side) ?? w.Bodies.Find(b => b.Owner == side);

    /// <summary>Bodies worth holding (they make Industry). Holding more than half of them for a while wins.</summary>
    public static IEnumerable<Body> Claimable(World w) => w.Bodies.Where(b => b.Industry > 0);

    public static double IncomePerDay(World w, Side side) =>
        w.Bodies.Where(b => b.Owner == side).Sum(b => Planets.IndustryPerDay(w, b)) * Tech.Factor(w, side, Tech.Automation);

    /// <summary>A ship order's variant and its price (the bill of its fit, and an AI core if the class is AI-run), set when it is queued.</summary>
    public static void PriceShip(World w, WorkOrder o, int? designId)
    {
        var cls = GameData.ShipClass(o.Key);
        o.DesignId = Designer.ForBuild(w, o.Side, o.Key, designId);
        var fit = Designer.FitFor(w, o.Side, o.Key, o.DesignId.Value);
        var ai = Designer.AiRun(w, o.Side, o.Key);
        o.FitCost = Math.Round(Designer.Industry(cls, fit, ai));
        o.FitMaterials = Designer.Materials(cls, fit, ai);
    }

    /// <summary>Industry an order takes when it starts. Research costs none: only scientist time (and a T3+ idea's material).</summary>
    public static double CostOf(WorkOrder o) => o.Kind == OrderKind.Ship ? o.FitCost ?? GameData.ShipClass(o.Key).Cost : 0;

    /// <summary>The materials an order takes when it starts: a ship's alloys and electronics, or a T3+ idea's material.</summary>
    public static Dictionary<string, double>? MaterialsOf(WorkOrder o) => o.Kind == OrderKind.Ship
        ? o.FitMaterials ?? GameData.ShipClass(o.Key).Materials
        : GameData.Research(o.Key).Materials;

    /// <summary>Where a ship order is built: its dry dock, or the shipyard.</summary>
    public static Body? YardFor(World w, WorkOrder o) =>
        o.Kind != OrderKind.Ship ? null : o.YardBodyId is int id ? w.FindBody(id) : ShipyardOf(w, o.Side);

    /// <summary>The first material short at this store for these needs, as a line ("needs 12 more alloys at Verdant"), or null.</summary>
    public static string? MaterialShort(Body? store, Dictionary<string, double>? mats)
    {
        if (mats == null || mats.Count == 0) return null;
        if (store == null) return "No store to take materials from";
        foreach (var (good, amount) in mats.OrderBy(m => m.Key, StringComparer.Ordinal))
            if (Colonies.Held(store, good) < amount - 1e-9)
                return $"needs {amount - Colonies.Held(store, good):0} more {Colonies.GoodName(good).ToLowerInvariant()} at {store.Name}";
        return null;
    }

    /// <summary>Takes the materials out of the store (call after MaterialShort said there are enough).</summary>
    public static void TakeMaterials(World w, Body store, Dictionary<string, double>? mats, string why)
    {
        if (mats == null) return;
        foreach (var (good, amount) in mats)
        {
            Ledger.Out(w, store.Owner, good, Math.Min(amount, Colonies.Held(store, good)), why);
            store.Stock[good] = Math.Max(0, Colonies.Held(store, good) - amount);
        }
    }

    public static double HoursFor(World w, Side side, OrderKind kind, string key) => kind == OrderKind.Ship
        ? GameData.ShipClass(key).Cost / (E.BuildIndustryPerDay * Tech.Factor(w, side, Tech.FastShipyard)) * 24
        : Research.BaseHours(w, side, key);

    public static string NameOf(WorkOrder o) => o.SettleBodyId != null ? $"Settlers for {o.SettleName}" : o.Kind == OrderKind.Ship ? GameData.ShipClass(o.Key).Name : GameData.Research(o.Key).Name;

    /// <summary>A fleet that is holding orbit at this body (not flying off).</summary>
    public static bool InOrbitAt(Fleet f, Body b) =>
        f.Hold == HoldStyle.Orbit && f.OrbitBodyId == b.Id && f.Flight is not { Started: true } && f.ShipIds.Count > 0;

    /// <summary>Sets up a new full game: start Industry, and each start planet gets a shipyard.</summary>
    public static void Start(World w)
    {
        w.Loop = true;
        foreach (var s in w.Sides) s.Industry = E.StartIndustry;
    }

    public static void AddShipyard(Body b, Side side)
    {
        b.Owner = side;
        b.Shipyard = true;
        b.ShipyardArmour = GameData.Economy.Shipyard.Armour;
    }

    public const string Relay = "relay";
    public const string SensorStation = "sensor";

    public static StationData? StationKind(string id) => E.Stations?.Find(x => x.Id == id);

    /// <summary>The side's working stations of this kind (built, and on a body it still holds).</summary>
    public static IEnumerable<(Station Station, Body Body)> Stations(World w, Side side, string kind)
    {
        foreach (var st in w.Stations)
            if (st.Side == side && st.Kind == kind && w.Hour >= st.ReadyHour && w.FindBody(st.BodyId) is Body b && b.Owner == side)
                yield return (st, b);
    }

    public static bool HasStation(World w, Side side, int bodyId, string kind) =>
        w.Stations.Any(st => st.Side == side && st.Kind == kind && st.BodyId == bodyId);

    /// <summary>Why this station can't be built here now, or null if it can.</summary>
    public static string? CanBuildStation(World w, Side side, int bodyId, string kind)
    {
        if (!w.Loop) return "There is no economy in the battle sandbox";
        if (w.Winner != null) return "The game is over";
        if (StationKind(kind) is not StationData data) return $"Unknown station '{kind}'";
        if (w.FindBody(bodyId) is not Body b || b.Owner != side) return "Only on a body you hold";
        if (HasStation(w, side, bodyId, kind)) return $"{b.Name} already has a {data.Name.ToLowerInvariant()}";
        if (data.Needs is string needs && !Tech.Has(w, side, needs)) return $"Research {GameData.Research(needs).Name} first";
        if (data.OnTypes is { Count: > 0 } types && !types.Contains(b.Type)) return $"Only over a gas giant";
        if (Of(w, side).Industry < data.Cost) return $"Needs {data.Cost:0} Industry";
        return null;
    }

    /// <summary>Stations on bodies the side no longer holds are lost.</summary>
    private static void LoseStations(Simulation sim)
    {
        var w = sim.World;
        foreach (var st in w.Stations.Where(st => w.FindBody(st.BodyId)?.Owner != st.Side).ToList())
        {
            w.Stations.Remove(st);
            sim.Log.Info("economy", $"{st.Side} loses a station", new { side = st.Side.ToString(), st.Kind, st.BodyId });
        }
    }

    /// <summary>One game hour of the loop. Called at each whole hour.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Winner != null) return;
        Research.EnsureTeams(w);
        using (SectionTimer.Time("planets")) Planets.Update(sim);
        if (w.Winner != null) return;
        Works.Update(sim);
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var earned = IncomePerDay(w, side) / 24;
            Of(w, side).Industry += earned;
            Of(w, side).Totals.IndustryEarned += earned;
            using (SectionTimer.Time("yards")) Work(sim, side);
            using (SectionTimer.Time("research")) Research.Work(sim, side);
            using (SectionTimer.Time("upkeep")) SupplyLines.Upkeep(sim, side);
        }
        Research.Surveys(sim);
        Sparks.Check(sim);
        Claims(sim);
        LoseStations(sim);
        Bombard(sim);
        CheckWin(sim);
    }

    /// <summary>Each yard (the shipyard, and dry docks once Colony shipyards is researched) works on the first ship order for it.</summary>
    private static void Work(Simulation sim, Side side)
    {
        var w = sim.World;
        if (ShipyardOf(w, side) is Body main) Work(sim, side, main, w.Orders.Find(o => o.Side == side && o.Kind == OrderKind.Ship && o.YardBodyId == null));
        foreach (var dock in w.Bodies.Where(b => b.Owner == side && Colonies.DockBuilds(w, b, side)).ToList())
            Work(sim, side, dock, w.Orders.Find(o => o.Side == side && o.Kind == OrderKind.Ship && o.YardBodyId == dock.Id));
        // Orders for a dock that is gone go back to the shipyard.
        foreach (var o in w.Orders.Where(o => o.Side == side && o.YardBodyId is int id && !(w.FindBody(id) is Body b && Colonies.DockBuilds(w, b, side))))
            o.YardBodyId = null;
    }

    private static void Work(Simulation sim, Side side, Body yard, WorkOrder? order)
    {
        var w = sim.World;
        if (order == null) return;
        // An enemy fleet in orbit over the shipyard blockades it: nothing is launched, the build waits.
        if (Blockaded(w, yard))
        {
            if (order.Paid && !order.Blockaded) sim.Log.Info("economy", $"{side} shipyard is blockaded: building paused", new { side = side.ToString(), order.Id });
            order.Blockaded = true;
            return;
        }
        order.Blockaded = false;
        var state = Of(w, side);
        // Grown ships (the Hive): a starving brood yard grows nothing, and a new ship takes biomass (food) from the store.
        var biomass = order.Paid ? 0 : Races.BiomassFor(w, side, CostOf(order));
        if (order.Paid && Races.GrowStatus(w, side, yard, 0, started: true) is string starving)
        {
            if (order.Waiting == null) sim.Log.Info("economy", "Brood yard starving: growth paused", new { side = side.ToString(), order.Id, order.Key });
            order.Waiting = starving;
            return;
        }
        if (!order.Paid)
        {
            var cost = CostOf(order);
            var mats = Races.Materials(w, side, MaterialsOf(order));
            var shortOf = MaterialShort(yard, mats) ?? Races.GrowStatus(w, side, yard, biomass, started: false);
            var wait = state.Industry < cost ? $"needs {cost - state.Industry:0} more Industry" : shortOf;
            if (wait != null)
            {
                if (order.Waiting == null && shortOf != null)
                    sim.Log.Info("economy", $"{side} shipyard waits for materials", new { side = side.ToString(), order.Id, order.Key, why = shortOf });
                if (shortOf != null) state.Totals.MaterialStallHours += GameTime.HoursPerTick;
                if (state.Totals.MaterialStallHours >= 24 * 10) Sparks.Fire(sim, side, "yard-waiting");
                order.Waiting = wait;
                return;
            }
            order.Waiting = null;
            state.Industry -= cost;
            state.Totals.IndustrySpent += cost;
            TakeMaterials(w, yard, mats, "ships");
            if (biomass > 0)
            {
                Races.TakeBiomass(w, yard, biomass);
                Ledger.Out(w, side, "food", biomass, "ships grown");
            }
            order.Paid = true;
            order.HoursLeft = order.TotalHours;
            sim.Log.Info("economy", $"{side} starts {NameOf(order)}", new { side = side.ToString(), order.Id, order.Key, cost, materials = mats });
        }
        order.Waiting = null;
        order.HoursLeft -= GameTime.HoursPerTick * Colonies.ShipyardRate(w, yard);
        if (order.HoursLeft > 1e-9) return;
        w.Orders.Remove(order);
        Launch(sim, side, order.Key, order.JoinFleetId, order, yard);
    }

    /// <summary>
    /// A new ship leaves the shipyard. With no fleet named it joins a fleet waiting there, or forms a new one.
    /// With a fleet named it joins it at once if it is at the shipyard, or forms its own fleet and flies out to it.
    /// </summary>
    private static void Launch(Simulation sim, Side side, string classId, int? joinFleetId = null, WorkOrder? order = null, Body? from = null)
    {
        var w = sim.World;
        var yard = from ?? ShipyardOf(w, side)!;
        var ship = NewShip(w, side, classId, yard, order != null ? Designer.ForOrder(w, order) : null);
        ship.BuiltHour = w.Hour;
        ship.BuiltAtBodyId = yard.Id;
        Crews.Man(w, ship, yard);
        Of(w, side).Totals.ShipsBuilt++;
        Choices.Note(w, side, "ship", classId, yard.Name, string.Join(" ", ship.Fit));
        // The crew comes from the shipyard planet's people.
        if (yard.PopK > 0 && Crews.Crewed(ship)) yard.PopK = Math.Max(1, yard.PopK - Crews.CrewPeopleK(w, side, classId));
        // Troop ships leave with their soldiers aboard, raised from the same people.
        if (Planets.IsTroopShip(ship))
        {
            var troops = Math.Min(Planets.TroopHold(ship), Math.Max(0, yard.PopK - Planets.D.KeepAtSourceK));
            ship.CargoTroops = troops;
            yard.PopK -= troops;
        }
        var target = joinFleetId is int j && w.FindFleet(j) is Fleet t && t.Side == side ? t : null;
        Fleet? waiting;
        if (target != null)
            waiting = InOrbitAt(target, yard) && target.Flight == null && !target.Retreating ? target : null;
        else
        {
            // Colony ships keep to themselves (they have their own errands); warships don't wait on them.
            var colony = Planets.IsColonyShip(ship);
            waiting = order?.SettleBodyId != null ? null : w.Fleets.FirstOrDefault(f => f.Side == side && f.Flight == null && f.JoinFleetId == null && !f.Retreating
                                                   && f.PeopleRun == null && InOrbitAt(f, yard) && w.ShipsOf(f).All(Planets.IsColonyShip) == colony);
        }
        if (waiting != null)
        {
            waiting.ShipIds.Add(ship.Id);
            ship.FleetId = waiting.Id;
            Fleets.Arrange(w, waiting);
        }
        else
        {
            waiting = Fleets.Create(w, side, new[] { ship }, yard.Position);
            var angle = sim.Rng.Range(0, Math.PI * 2);
            waiting.Hold = HoldStyle.Orbit;
            waiting.OrbitBodyId = yard.Id;
            waiting.OrbitRadius = Fleets.OrbitRadiusFor(yard);
            waiting.OrbitAngle = angle;
            waiting.Position = yard.Position + new Vec2(DetMath.Cos(angle), DetMath.Sin(angle)) * waiting.OrbitRadius;
        }
        foreach (var s in w.ShipsOf(waiting)) s.Position = waiting.Position + s.FormationOffset;
        sim.Log.Info("economy", $"{ship.Name} is launched at {yard.Name} and joins {waiting.Name}",
            new { side = side.ToString(), shipId = ship.Id, classId, fleetId = waiting.Id });
        if (order?.SettleBodyId is int settle) Planets.SendSettlers(sim, waiting, yard, settle, order.EscortFleetId, order.Auto);
        if (target != null && waiting != target)
        {
            waiting.JoinFleetId = target.Id;
            if (Fleets.FlyToJoin(w, waiting, target, ReinforceSpeed) is string problem)
                sim.Log.Info("economy", $"{waiting.Name} waits at {yard.Name} to join {target.Name}", new { fleetId = waiting.Id, target = target.Id, problem });
            else
                sim.Log.Info("economy", $"{waiting.Name} flies out to join {target.Name}",
                    new { fleetId = waiting.Id, target = target.Id, arrive = waiting.Flight!.ArriveHour });
        }
    }

    /// <summary>Speed (share of top speed) new ships fly at to reach the fleet they were built for.</summary>
    public const double ReinforceSpeed = 0.7;

    /// <summary>Makes a ship with full supplies and armour, numbered after the side's earlier ships of its class.</summary>
    public static Ship NewShip(World w, Side side, string classId, Body? yard = null, int? designId = null)
    {
        var cls = GameData.ShipClass(classId);
        var key = $"{side}:{cls.Id}";
        var design = designId ?? Designer.DefaultFor(w, side, classId);
        var fit = Designer.FitFor(w, side, classId, design);
        var number = w.ShipNumbers[key] = w.ShipNumbers.GetValueOrDefault(key) + 1;
        var ship = new Ship
        {
            Id = w.NextId++, Side = side, ClassId = cls.Id,
            MaxSpeed = cls.MaxSpeedMmPerHour, Accel = cls.AccelMmPerHour2, Supplies = cls.Supplies, Missiles = cls.Missiles, Fit = fit,
            DesignId = design, AiRun = Designer.AiRun(w, side, classId),
        };
        var spec = Fitting.Spec(ship);
        ship.MaxSpeed = spec.MaxSpeedMmPerHour;
        ship.Accel = spec.AccelMmPerHour2;
        ship.Fuel = Fuels.ForNewShip(w, ship, ShipyardOf(w, side));
        ship.Supplies = Fitting.MaxSupplies(w, ship);
        ship.Missiles = Tech.Racks(w, ship);
        ship.Ammo = SupplyLines.MaxAmmo(ship);
        ship.Food = SupplyLines.MaxFood(ship);
        // Loaded with what the side's munitions plants make (outside the game loop, e.g. the battle sandbox: no limit).
        (ship.MissileGrade, ship.RailGrade) = w.Loop ? Munitions.Made(w, side) : (5, 2);
        SupplyLines.FillCargo(ship);
        (ship.CargoMissileGrade, ship.CargoRailGrade) = (ship.MissileGrade, ship.RailGrade);
        ship.Armour = Tech.MaxArmour(w, ship);
        Heat.Fit(w, ship);
        ShipNames.Christen(w, ship, number, yard);
        w.Ships.Add(ship);
        return ship;
    }

    /// <summary>Hours of holding orbit to claim a body: longer to take one the other side holds than an empty one.</summary>
    public static double ClaimHoursFor(Body b) =>
        (b.Owner != null && E.ClaimHeldDays > 0 ? E.ClaimHeldDays : E.ClaimDays) * 24;

    /// <summary>
    /// A side that holds orbit at a body with no enemy fleet in orbit there claims it after ClaimDays
    /// (ClaimHeldDays for a body the other side holds).
    /// Leaving, or an enemy arriving, resets the count. Shipyard planets can't be claimed, only destroyed.
    /// </summary>
    private static void Claims(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in Claimable(w))
        {
            var need = ClaimHoursFor(b);
            // Shipyards can't be claimed, only destroyed; a peopled planet held by a side must surrender or be taken by troops.
            if (b.Shipyard || b.Owner != null && b.PopK > 0) { b.ClaimSide = null; b.ClaimHours = 0; continue; }
            var sides = w.Fleets.Where(f => InOrbitAt(f, b)).Select(f => f.Side).Distinct().ToList();
            if (sides.Count != 1 || b.Owner == sides[0])
            {
                b.ClaimSide = null;
                b.ClaimHours = 0;
                continue;
            }
            var side = sides[0];
            if (b.ClaimSide != side)
            {
                b.ClaimSide = side;
                b.ClaimHours = 0;
                sim.Log.Info("economy", $"{side} starts claiming {b.Name}", new { side = side.ToString(), bodyId = b.Id });
            }
            b.ClaimHours += GameTime.HoursPerTick;
            if (b.ClaimHours < need) continue;
            var before = b.Owner;
            b.Owner = side;
            b.AutoTopUp = false;
            b.Pieces.RemoveAll(p => !p.Built);
            Of(w, side).Totals.PlanetsClaimed++;
            if (before is Side loser) Of(w, loser).Totals.PlanetsLost++;
            b.ClaimSide = null;
            b.ClaimHours = 0;
            sim.Log.Info("economy", $"{side} claims {b.Name}", new { side = side.ToString(), bodyId = b.Id, from = before?.ToString() });
        }
    }

    /// <summary>
    /// An armed fleet in orbit at an enemy shipyard with no defending fleet in contact range wears the
    /// shipyard down: railguns every hour, a missile from each destroyer every few hours. Shipyards mend
    /// slowly when left alone.
    /// </summary>
    /// <summary>
    /// Why a side's fleets are or are not wearing down this enemy shipyard, in a line for the player:
    /// the damage a day and days left, or what stops it (no armed fleet in orbit, or defenders near).
    /// </summary>
    public static string? BombardStatus(World w, Body b, Side side)
    {
        if (!b.Shipyard || b.Owner is not Side owner || owner == side) return null;
        var y = E.Shipyard;
        var range = GameData.Combat.ContactRangeMm;
        var attackers = w.Fleets.Where(f => f.Side == side && InOrbitAt(f, b)).ToList();
        if (attackers.Count == 0)
        {
            var near = w.Fleets.FirstOrDefault(f => f.Side == side && f.LeftOrbitBodyId == b.Id);
            return near != null
                ? $"Not under attack: {near.Name} left orbit to fight. Shipyards are only worn down from orbit: select it and press O"
                : "Not under attack: park an armed fleet in orbit here (O) with no defenders within " + $"{range * 1000:#,0} km";
        }
        if (w.Fleets.Any(f => f.Side == owner && Vec2.Distance(f.Position, b.Position) <= range))
            return $"Not under attack: defended. A rival fleet is within {range * 1000:#,0} km; beat it or draw it off first";
        double perDay = 0;
        foreach (var s in attackers.SelectMany(w.ShipsOf))
        {
            var cls = Fitting.Spec(s);
            if (cls.Railguns > 0 && !s.Out(Module.Railguns) && s.Ammo >= cls.Railguns * y.RailSuppliesPerHour) perDay += cls.Railguns * y.RailDamagePerHour * 24;
            if (s.Missiles > 0 && !s.Out(Module.Missiles)) perDay += y.MissileDamage * 24 / Math.Max(1, y.MissileEveryHours);
        }
        if (perDay <= 0) return "Not under attack: the fleet in orbit has no working weapons, ammunition or supplies left";
        return $"Under attack and blockaded (it can't build): about {perDay / y.Armour:P0} of its armour a day, {b.ShipyardArmour / perDay:0.#} days to go";
    }

    /// <summary>An armed enemy fleet holds orbit over this body: a shipyard can't launch ships, and a planet is cut off (see Planets).</summary>
    public static bool Blockaded(World w, Body yard) =>
        yard.Owner is Side owner && w.Fleets.Any(f => f.Side != owner && InOrbitAt(f, yard) && Combat.Armed(w, f));

    /// <summary>
    /// Damage a ship does in an hour of bombardment from orbit: its railguns (using ammunition) every hour,
    /// a missile every few hours. With consume, the ammunition and missiles are used up.
    /// </summary>
    public static double HourlyDamage(World w, Ship s, bool consume)
    {
        var y = E.Shipyard;
        var cls = Fitting.Spec(s);
        double damage = 0;
        if (cls.Railguns > 0 && !s.Out(Module.Railguns) && s.Ammo >= cls.Railguns * y.RailSuppliesPerHour)
        {
            if (consume) s.Ammo -= cls.Railguns * y.RailSuppliesPerHour;
            damage += cls.Railguns * y.RailDamagePerHour;
        }
        if (s.Missiles > 0 && !s.Out(Module.Missiles) && w.Tick % (long)Math.Max(1, y.MissileEveryHours) == 0)
        {
            if (consume) s.Missiles--;
            damage += y.MissileDamage;
        }
        return damage;
    }

    private static void Bombard(Simulation sim)
    {
        var w = sim.World;
        var y = E.Shipyard;
        foreach (var b in w.Bodies.Where(b => b.Shipyard && b.Owner != null).ToList())
        {
            var owner = b.Owner!.Value;
            var attackers = w.Fleets.Where(f => f.Side != owner && InOrbitAt(f, b)).ToList();
            var defended = w.Fleets.Any(f => f.Side == owner && Vec2.Distance(f.Position, b.Position) <= GameData.Combat.ContactRangeMm);
            double damage = 0;
            if (attackers.Count > 0 && !defended)
            {
                foreach (var s in attackers.SelectMany(w.ShipsOf)) damage += HourlyDamage(w, s, consume: true);
            }
            if (damage > 0)
            {
                if (b.LastBombardedHour == null || w.Hour - b.LastBombardedHour > 24)
                    sim.Log.Info("economy", $"{b.Name} shipyard is under attack", new { bodyId = b.Id, attacker = attackers[0].Side.ToString() });
                b.LastBombardedHour = w.Hour;
                b.ShipyardArmour -= damage;
                Of(w, attackers[0].Side).Totals.ShipyardDamage += damage;
                if (b.ShipyardArmour <= 0)
                {
                    b.ShipyardArmour = 0;
                    b.Shipyard = false;
                    w.Orders.RemoveAll(o => o.Side == owner && o.Kind == OrderKind.Ship);
                    sim.Log.Info("economy", $"{b.Name} shipyard is destroyed", new { bodyId = b.Id, side = owner.ToString() });
                }
            }
            else if (b.LastBombardedHour == null || w.Hour - b.LastBombardedHour > 24)
            {
                b.ShipyardArmour = Math.Min(y.Armour, b.ShipyardArmour + y.Armour * y.RepairPerDay / 24);
            }
        }
    }

    private static void CheckWin(Simulation sim)
    {
        var w = sim.World;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            if (ShipyardOf(w, side) != null) continue;
            var other = side == Side.Player ? Side.Rival : Side.Player;
            Win(sim, other, other == Side.Player ? "destroyed the rival shipyard" : "destroyed your shipyard");
            return;
        }
        // Control: a side with tipShare of control has tipped the game (planets it blockades give up sooner);
        // it wins at winShare, or after holding tipShare for winAfterDays.
        var d = Planets.D;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var state = Of(w, side);
            var share = Planets.Control(w, side).Share;
            if (share >= d.WinShare)
            {
                Win(sim, side, $"holds {share:P0} of the system");
                return;
            }
            if (share < d.TipShare)
            {
                if (state.MajoritySinceHour != null) sim.Log.Info("economy", $"{side} no longer leads", new { side = side.ToString(), share });
                state.MajoritySinceHour = null;
                continue;
            }
            if (state.MajoritySinceHour == null)
            {
                state.MajoritySinceHour = w.Hour;
                sim.Log.Info("economy", $"{side} has tipped the game", new { side = side.ToString(), share });
            }
            if (w.Hour - state.MajoritySinceHour >= d.WinAfterDays * 24)
            {
                Win(sim, side, $"held {d.TipShare:P0} of the system for {d.WinAfterDays:0} days");
                return;
            }
        }
    }

    internal static void Win(Simulation sim, Side side, string reason)
    {
        var w = sim.World;
        w.Winner = side;
        w.WinReason = reason;
        w.WinHour = w.Hour;
        sim.Log.Info("game", $"{side} wins: {reason}", new { winner = side.ToString(), reason, day = w.Day });
    }

    /// <summary>Why this order can't be queued, or null if it can.</summary>
    public static string? CanQueue(World w, Side side, OrderKind kind, string key, int? teamId = null)
    {
        if (!w.Loop) return "There is no economy in the battle sandbox";
        if (w.Winner != null) return "The game is over";
        if (kind == OrderKind.Ship)
        {
            if (!GameData.ShipClasses.TryGetValue(key, out var cls)) return $"Unknown ship class '{key}'";
            if (cls.Needs.Length > 0 && !Tech.Has(w, side, cls.Needs)) return $"Needs {GameData.Research(cls.Needs).Name}";
            if (ShipyardOf(w, side) == null) return "No shipyard";
            return null;
        }
        if (!GameData.ResearchById.TryGetValue(key, out var idea)) return $"Unknown research '{key}'";
        if (Of(w, side).Researched.Contains(key)) return "Already researched";
        if (idea.Later) return "Comes with a later build";
        if (!Research.Seen(w, side, key)) return "Not discovered yet: research one of the ideas it needs first";
        if (w.Orders.Any(o => o.Side == side && o.Kind == OrderKind.Research && o.Key == key)) return "Already in the queue";
        if (Research.Missing(w, side, key) is string missing) return missing;
        if (teamId is int t && Research.Team(w, side, t) == null) return "No such team";
        return null;
    }
}

// ---- Commands -------------------------------------------------------------------------------

/// <summary>Add a ship to the shipyard queue, or an upgrade to the research queue.</summary>
public sealed record QueueOrderCommand(Side Side, OrderKind Kind, string Key, int? JoinFleetId = null, int? TeamId = null, int? DesignId = null) : GameCommand
{
    /// <summary>Home orders take effect at once (no light lag), so they work while paused.</summary>
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (Kind == OrderKind.Research) Research.EnsureTeams(world);
        if ((Kind == OrderKind.Ship && Key == Planets.SettlerClass ? "Settler ships are ordered with Settle on the planet to settle" : Economy.CanQueue(world, Side, Kind, Key, TeamId)) is string problem)
        {
            sim.Log.Warn("economy", $"Can't queue {Key}: {problem}", new { side = Side.ToString(), Kind, Key, Source });
            return;
        }
        var order = new WorkOrder { Id = world.NextId++, Side = Side, Kind = Kind, Key = Key, TotalHours = Economy.HoursFor(world, Side, Kind, Key) };
        if (Kind == OrderKind.Ship && JoinFleetId is int j && world.FindFleet(j)?.Side == Side) order.JoinFleetId = j;
        if (Kind == OrderKind.Ship) Economy.PriceShip(world, order, DesignId);
        if (Kind == OrderKind.Research) order.TeamId = TeamId ?? Research.BestTeamFor(world, Side, Key)?.Id;
        order.HoursLeft = order.TotalHours;
        world.Orders.Add(order);
        if (Kind == OrderKind.Research) Choices.Note(world, Side, "research", Key);
        sim.Log.Info("economy", $"{Side} queues {Economy.NameOf(order)}", new { side = Side.ToString(), order.Id, Kind, Key, Source });
    }
}

/// <summary>Build a station in orbit of a body the side holds: paid now, working after its build time.</summary>
public sealed record BuildStationCommand(Side Side, int BodyId, string Kind) : GameCommand
{
    /// <summary>The order goes to the body where the station is built.</summary>
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (Economy.CanBuildStation(world, Side, BodyId, Kind) is string problem)
        {
            sim.Log.Warn("economy", $"Can't build a station: {problem}", new { side = Side.ToString(), BodyId, Kind, Source });
            return;
        }
        var data = Economy.StationKind(Kind)!;
        Economy.Of(world, Side).Industry -= data.Cost;
        world.Stations.Add(new Station { Id = world.NextId++, Side = Side, Kind = Kind, BodyId = BodyId, ReadyHour = world.Hour + data.Days * 24 });
        sim.Log.Info("economy", $"{Side} builds a {data.Name.ToLowerInvariant()} at {world.FindBody(BodyId)!.Name}", new { side = Side.ToString(), BodyId, Kind, days = data.Days, Source });
    }
}

/// <summary>Choose the fleet a ship being built will join (null: the fleet at the shipyard).</summary>
public sealed record SetOrderFleetCommand(Side Side, int OrderId, int? JoinFleetId) : GameCommand
{
    /// <summary>Home orders take effect at once (no light lag), so they work while paused.</summary>
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var order = world.Orders.Find(o => o.Id == OrderId && o.Side == Side && o.Kind == OrderKind.Ship);
        if (order == null)
        {
            sim.Log.Warn("economy", "Fleet choice for an order that does not exist", new { side = Side.ToString(), OrderId, Source });
            return;
        }
        if (JoinFleetId is int j && world.FindFleet(j)?.Side != Side)
        {
            sim.Log.Warn("economy", "Fleet choice for a fleet that is not ours", new { side = Side.ToString(), OrderId, JoinFleetId, Source });
            return;
        }
        order.JoinFleetId = JoinFleetId;
        sim.Log.Info("economy", "New ship will join a fleet", new { side = Side.ToString(), OrderId, JoinFleetId, Source });
    }
}

/// <summary>Take an order off its queue. If it was already started, its Industry comes back.</summary>
public sealed record CancelOrderCommand(Side Side, int OrderId) : GameCommand
{
    /// <summary>Home orders take effect at once (no light lag), so they work while paused.</summary>
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var order = world.Orders.Find(o => o.Id == OrderId && o.Side == Side);
        if (order == null)
        {
            sim.Log.Warn("economy", "Cancel for an order that does not exist", new { side = Side.ToString(), OrderId, Source });
            return;
        }
        world.Orders.Remove(order);
        if (order.Paid)
        {
            Economy.Of(world, Side).Industry += Economy.CostOf(order);
            Economy.Of(world, Side).Totals.IndustrySpent -= Economy.CostOf(order);
        }
        sim.Log.Info("economy", $"{Side} cancels {Economy.NameOf(order)}", new { side = Side.ToString(), OrderId, refunded = order.Paid, Source });
    }
}
