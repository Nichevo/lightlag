using Lightlag.Sim.Logging;
using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

// ---- Data (Data/stations.json) ------------------------------------------------------------

/// <summary>A tier of the station line: its hull (a station class), build days, the troops that hold it, the research it needs.</summary>
public sealed record StationTier(string ClassId, int Tier, double Days, double GarrisonK, List<string>? Needs = null, double LiftCut = 0,
    string Stage = "", string Blurb = "");

/// <summary>A yard size: the longest hull it can lay down.</summary>
public sealed record YardData(string Id, string Name, double MaxLengthM);

/// <summary>The station line's numbers (see the comments in stations.json).</summary>
public sealed record StationsData(List<StationTier> Tiers, List<ShipClass> Classes, List<YardData> Yards,
    double RailReachMm = 40, double SiloReachMm = 60, double UmbrellaMm = 12,
    double RailDamagePerHour = 3, double MissileDamagePerHour = 2, double RailAmmoPerHour = 0.02, double MissileAmmoPerHour = 0.05,
    double EmptyGunShare = 0.5, double BaseStore = 20, double StoreFillPerDay = 10, double MendPerDay = 0.05,
    double UmbrellaShare = 1, double UndockHours = 6, double HighOrbitBombShare = 0.4, double LandingFactor = 2, int LandingFactorTier = 4,
    double MineDriftPerDay = 0.01, double MineUpkeepPer10 = 0.05);

// ---- State ---------------------------------------------------------------------------------

/// <summary>What a station's guns fire at: both orbits, low orbit only (hides how far they reach), or nothing.</summary>
public enum StationFire { Both, LowOnly, Hold }

/// <summary>
/// The station over a body: one per body, a hull of the station line (platform up to orbital ring) fitted with
/// bays and guns like a ship. It works from ReadyHour. Its store holds ammunition (its guns and the fleets it
/// rearms) and fuel (for the fleets it refuels). A station can be boarded and taken: then it stays over a
/// planet it does not hold (Captured) until its new side takes the planet.
/// </summary>
public sealed class Starbase
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int BodyId { get; set; }
    public string ClassId { get; set; } = "";
    public int DesignId { get; set; }
    public List<string> Fit { get; set; } = new();
    public string Name { get; set; } = "";
    public double ReadyHour { get; set; }
    public double Armour { get; set; }
    public double? LastHitHour { get; set; }

    /// <summary>Goods in its store: ammunition and fuel.</summary>
    public double Ammo { get; set; }
    public double Fuel { get; set; }

    /// <summary>Its side held the body last hour (a station is lost with the planet; a captured one is not).</summary>
    public bool HeldBody { get; set; } = true;

    /// <summary>Taken by boarding: its guns fire at half until its new side's own ammunition reaches it.</summary>
    public bool ForeignAmmo { get; set; }

    /// <summary>A tier upgrade or a bay swap under way (the station keeps working; swapped bays are out meanwhile).</summary>
    public StationRefit? Refit { get; set; }

    /// <summary>The defence plan: what its guns fire at, and whether it is blown up rather than lost to boarders.</summary>
    public StationFire Fire { get; set; }
    public bool ScuttleIfTaken { get; set; }

    [JsonIgnore] public ShipClass? SpecCache { get; set; }
    [JsonIgnore] public string? SpecKey { get; set; }
}

/// <summary>A refit under way: to this class (a tier up) and fit, done when HoursLeft runs out.</summary>
public sealed class StationRefit
{
    public string ClassId { get; set; } = "";
    public List<string> Fit { get; set; } = new();
    public int DesignId { get; set; }
    public double HoursLeft { get; set; }
    public double TotalHours { get; set; }
}

/// <summary>
/// The station line (Starbases build): orbital platform, defence station, starbase, fortress, orbital ring.
/// A station guards its planet (guns into low and high orbit, a ring into far orbit too), serves the
/// friendly fleets holding low orbit under it (point defence umbrella, rearming, refuelling, repairs, spotting),
/// and its slipway lets the body build longer hulls (see Yards). Enemy warships in orbit wear it down before
/// they can blockade; troops can board and take it.
/// </summary>
public static class Starbases
{
    public static StationsData D => GameData.Stations;

    // ---- Tiers and specs ---------------------------------------------------------------------

    public static StationTier TierOf(string classId) => D.Tiers.Find(t => t.ClassId == classId) ?? D.Tiers[0];
    public static StationTier? TierAt(int tier) => tier >= 1 && tier <= D.Tiers.Count ? D.Tiers[tier - 1] : null;
    public static int Tier(Starbase st) => TierOf(st.ClassId).Tier;
    public static ShipClass ClassOf(Starbase st) => GameData.ShipClass(st.ClassId);

    /// <summary>The fit working now: during a bay swap, the bays being swapped are out.</summary>
    public static List<string> WorkingFit(Starbase st)
    {
        var fit = st.Fit.Count == ClassOf(st).SlotList.Count ? st.Fit.ToList() : Fitting.StandardFit(ClassOf(st));
        if (st.Refit is { } r && r.ClassId == st.ClassId && r.Fit.Count == fit.Count)
            for (var i = 0; i < fit.Count; i++)
                if (fit[i] != r.Fit[i]) fit[i] = "";
        return fit;
    }

    /// <summary>The station's working numbers.</summary>
    public static ShipClass Spec(Starbase st)
    {
        var fit = WorkingFit(st);
        var key = st.ClassId + ":" + string.Join("|", fit);
        if (st.SpecCache != null && st.SpecKey == key) return st.SpecCache;
        st.SpecCache = Fitting.Compute(ClassOf(st), fit);
        st.SpecKey = key;
        return st.SpecCache;
    }

    public static double MaxArmour(Starbase st) => Spec(st).Armour;

    public static IEnumerable<ModuleData> Bays(Starbase st) =>
        WorkingFit(st).Select(GameData.Module).Where(m => m is { Slot: "B" }).Select(m => m!);

    public static bool HasBay(Starbase st, string bay) => Bays(st).Any(m => m.Bay == bay);

    public static bool Working(World w, Starbase st) => w.Hour >= st.ReadyHour && st.Armour > 0;

    public static double AmmoCap(Starbase st) => D.BaseStore + Bays(st).Where(m => m.Bay == "magazine").Sum(m => m.Store);
    public static double FuelCap(Starbase st) => Bays(st).Where(m => m.Bay == "fuel-depot").Sum(m => m.Store);

    /// <summary>The station of this side over the body (built or being built), if any.</summary>
    public static Starbase? Of(World w, int bodyId, Side side) => w.Starbases.Find(st => st.BodyId == bodyId && st.Side == side);

    /// <summary>The station over the body, whoever has it (one per body).</summary>
    public static Starbase? At(World w, int bodyId) => w.Starbases.Find(st => st.BodyId == bodyId);

    /// <summary>This side's working station over the body.</summary>
    public static Starbase? WorkingAt(World w, int bodyId, Side side) => Of(w, bodyId, side) is Starbase st && Working(w, st) ? st : null;

    /// <summary>The working station that guards a planet for its holder (the old "defence station" test: one with guns).</summary>
    public static Starbase? Fort(World w, Body b) =>
        b.Owner is Side owner && WorkingAt(w, b.Id, owner) is Starbase st && Guns(st) > 0 ? st : null;

    /// <summary>Gun damage an hour at full stores (railguns and missile racks).</summary>
    public static double Guns(Starbase st)
    {
        var s = Spec(st);
        return s.Railguns * D.RailDamagePerHour + s.Missiles / 8.0 * D.MissileDamagePerHour;
    }

    public static double AmmoPerHour(Starbase st)
    {
        var s = Spec(st);
        return s.Railguns * D.RailAmmoPerHour + s.Missiles / 8.0 * D.MissileAmmoPerHour;
    }

    /// <summary>How hard its guns hit now: at a share with an empty store or captured ammunition.</summary>
    public static double GunShare(Starbase st) => st.Ammo < AmmoPerHour(st) - 1e-9 || st.ForeignAmmo ? D.EmptyGunShare : 1;

    /// <summary>The longest hull its slipway or dock bay lays down (a dock bay builds only with Colony shipyards).</summary>
    public static double YardM(World w, Starbase st) =>
        Bays(st).Where(m => m.YardM > 0 && (m.Bay != "dock" || Tech.Has(w, st.Side, "colony-shipyards"))).Select(m => m.YardM).DefaultIfEmpty(0).Max();

    /// <summary>The share of the lift to orbit still paid at a body with this station (an orbital ring is a space elevator).</summary>
    public static double LiftCut(Starbase st) => TierOf(st.ClassId).LiftCut;

    /// <summary>Troops holding the station against boarders (thinner as its armour goes).</summary>
    public static double GarrisonK(World w, Starbase st) =>
        TierOf(st.ClassId).GarrisonK * (0.25 + 0.75 * Math.Clamp(st.Armour / Math.Max(1, MaxArmour(st)), 0, 1)) * Races.Of(w, st.Side).Boarding;

    public static string DisplayName(World w, Starbase st) =>
        $"{w.FindBody(st.BodyId)?.Name ?? "?"} {ClassOf(st).Name.ToLowerInvariant()}";

    // ---- Building, upgrading, refitting -------------------------------------------------------

    /// <summary>Why this side can't have a station of this class (tier research), or null.</summary>
    public static string? Researched(World w, Side side, string classId)
    {
        if (!GameData.StationClasses.ContainsKey(classId)) return $"Unknown station '{classId}'";
        foreach (var t in D.Tiers)
        {
            foreach (var n in t.Needs ?? new())
                if (!Tech.Has(w, side, n)) return $"Research {GameData.Research(n).Name} first";
            if (t.ClassId == classId) break;
        }
        return null;
    }

    /// <summary>The highest tier this side has researched.</summary>
    public static int TopTier(World w, Side side) => D.Tiers.LastOrDefault(t => Researched(w, side, t.ClassId) == null)?.Tier ?? 0;

    /// <summary>Why this side can't build a station of this class over this body now, or null.</summary>
    public static string? CanBuild(World w, Side side, int bodyId, string classId, bool queue = false)
    {
        if (!w.Loop) return "There is no economy in the battle sandbox";
        if (w.Winner != null) return "The game is over";
        if (w.FindBody(bodyId) is not Body b || b.Owner != side) return "Only over a body you hold";
        if (b.Kind == BodyKind.Star) return "Not round a star";
        if (At(w, bodyId) is Starbase there) return there.Side == side ? $"{b.Name} already has a {ClassOf(there).Name.ToLowerInvariant()}: upgrade it instead" : "An enemy station holds this orbit";
        if (Researched(w, side, classId) is string missing) return missing;
        var cls = GameData.ShipClass(classId);
        if (Queued(w, side, bodyId) is QueuedStarbase q) return $"A {GameData.ShipClass(q.ClassId).Name.ToLowerInvariant()} is already queued over {b.Name}";
        if (queue) return null; // queued: it waits for its Industry (and for a blockade to lift)
        if (Budget.Problem(w, side, Pot.Stations, cls.Cost, Key(bodyId, classId)) is string tooLow) return tooLow;
        if (Economy.Blockaded(w, b)) return $"{b.Name} is blockaded";
        return null;
    }

    /// <summary>The budget key of a station build (the same whether paid at once or queued first).</summary>
    public static string Key(int bodyId, string classId) => $"starbase:{bodyId}:{classId}";

    /// <summary>The station queued over this body, if any.</summary>
    public static QueuedStarbase? Queued(World w, Side side, int bodyId) => w.StarbaseQueue.Find(q => q.Side == side && q.BodyId == bodyId);

    /// <summary>Why a queued station has not started yet, for the screens.</summary>
    public static string QueuedWhy(World w, QueuedStarbase q)
    {
        var b = w.FindBody(q.BodyId);
        if (b != null && Economy.Blockaded(w, b)) return $"waiting: {b.Name} is blockaded";
        return Budget.ShortOf(w, q.Side, Pot.Stations, GameData.ShipClass(q.ClassId).Cost, Key(q.BodyId, q.ClassId)) is string why
            ? (why.StartsWith("needs ", StringComparison.Ordinal) ? "waiting for " + why[6..] : "waiting: " + why) : "starts next hour";
    }

    /// <summary>
    /// Hourly: queued stations start, in queue order, once their pot can pay; ones that can no longer be built
    /// there (the planet lost, a station already over it) are dropped and the log says why.
    /// </summary>
    public static void StartQueued(Simulation sim)
    {
        var w = sim.World;
        foreach (var q in w.StarbaseQueue.ToList())
        {
            var b = w.FindBody(q.BodyId);
            if (b == null || b.Owner != q.Side || At(w, q.BodyId) != null)
            {
                w.StarbaseQueue.Remove(q);
                sim.Log.Info("stations", "Queued station dropped", new { side = q.Side.ToString(), q.BodyId, q.ClassId, why = b?.Owner != q.Side ? "planet lost" : "orbit taken" });
                continue;
            }
            if (Economy.Blockaded(w, b)) continue;
            if (Budget.ShortOf(w, q.Side, Pot.Stations, GameData.ShipClass(q.ClassId).Cost, Key(q.BodyId, q.ClassId)) != null) continue;
            w.StarbaseQueue.Remove(q);
            new BuildStarbaseCommand(q.Side, q.BodyId, q.ClassId, q.DesignId) { Source = "queue" }.Apply(w, sim);
        }
    }

    /// <summary>The next tier up from this station, if there is one.</summary>
    public static StationTier? NextTier(Starbase st) => TierAt(Tier(st) + 1);

    /// <summary>Industry an upgrade to this tier costs: the difference.</summary>
    public static double UpgradeCost(Starbase st, string toClass) => Math.Max(0, GameData.ShipClass(toClass).Cost - ClassOf(st).Cost);

    /// <summary>Why this station can't be upgraded (or refitted) to this class and variant now, or null.</summary>
    public static string? CanRefit(World w, Side side, Starbase? st, string toClass, int designId)
    {
        if (!w.Loop) return "Only in a full game";
        if (st == null || st.Side != side) return "Not your station";
        if (w.Hour < st.ReadyHour) return "Still being built";
        if (st.Refit != null) return "A refit is already under way";
        if (w.FindBody(st.BodyId)?.Owner != side) return "Only over a planet you hold";
        if (!GameData.StationClasses.ContainsKey(toClass)) return $"Unknown station '{toClass}'";
        var from = Tier(st);
        var to = TierOf(toClass).Tier;
        if (to < from) return "Stations are only upgraded, never cut down";
        if (to > from + 1) return "Upgrade one tier at a time";
        if (Researched(w, side, toClass) is string missing) return missing;
        var fit = FitFor(w, side, toClass, designId);
        if (to == from && fit.SequenceEqual(st.Fit)) return "It is already fitted that way";
        var cost = to > from ? UpgradeCost(st, toClass) : RefitCost(st, fit);
        if (Budget.Problem(w, side, Pot.Stations, cost, $"upgrade:{st.Id}:{toClass}") is string tooLow) return tooLow;
        return null;
    }

    /// <summary>Industry a bay swap costs: the full price of each part put in.</summary>
    public static double RefitCost(Starbase st, IReadOnlyList<string> fit) =>
        Enumerable.Range(0, Math.Min(fit.Count, st.Fit.Count)).Where(i => fit[i] != st.Fit[i])
            .Sum(i => (GameData.Module(fit[i])?.Cost ?? 0) * ClassOf(st).SlotList[i].Mounts);

    /// <summary>The fit a variant of a station class gets for this side (the Standard if the variant is not of that class).</summary>
    public static List<string> FitFor(World w, Side side, string classId, int designId) => Designer.FitFor(w, side, classId, designId);

    /// <summary>The variant of the next tier with the same name as this one (so a Watchtower stays a Watchtower), else its default.</summary>
    public static int SameVariant(World w, Side side, Starbase st, string toClass)
    {
        var name = Designer.NameOf(w, side, st.DesignId);
        return Designer.Of(w, side, toClass).Find(d => d.Name == name)?.Id ?? Designer.DefaultFor(w, side, toClass);
    }

    // ---- Yards ---------------------------------------------------------------------------------

    public static YardData YardSize(string id) => D.Yards.Find(y => y.Id == id) ?? D.Yards[0];

    /// <summary>The longest hull this side can lay down at this body: its shipyard (Yard), a dry dock (Dock bay), or its station's slipway.</summary>
    public static double YardLength(World w, Side side, Body b)
    {
        double m = 0;
        if (b.Owner == side && b.Shipyard) m = YardSize("yard").MaxLengthM;
        if (Colonies.DockBuilds(w, b, side)) m = Math.Max(m, YardSize("dock-bay").MaxLengthM);
        if (b.Owner == side && WorkingAt(w, b.Id, side) is Starbase st) m = Math.Max(m, YardM(w, st));
        return m;
    }

    /// <summary>A body other than the shipyard where this side can build ships: a dry dock with Colony shipyards, or a station slipway.</summary>
    public static bool Builds(World w, Body b, Side side) =>
        b.Owner == side && !b.Shipyard && YardLength(w, side, b) > 0;

    /// <summary>Settler ships are made by a settle order, so a Yard can still make one however long it is.</summary>
    public static bool Fits(World w, Side side, Body b, string classId) =>
        classId == Planets.SettlerClass || !GameData.ShipClasses.TryGetValue(classId, out var cls) || cls.LengthM <= YardLength(w, side, b) + 1e-9;

    /// <summary>Where a new ship of this class is built: the shipyard if it fits, else the nearest yard that takes it (null: none).</summary>
    public static Body? YardFor(World w, Side side, string classId)
    {
        var main = Economy.ShipyardOf(w, side);
        if (main != null && Fits(w, side, main, classId)) return main;
        var from = main?.Position ?? Light.Seat(w, side);
        return w.Bodies.Where(b => Builds(w, b, side) && Fits(w, side, b, classId))
            .OrderBy(b => Vec2.Distance(b.Position, from)).ThenBy(b => b.Id).FirstOrDefault();
    }

    /// <summary>The yard size a hull needs.</summary>
    public static YardData YardNeeded(ShipClass cls) => D.Yards.FirstOrDefault(y => cls.LengthM <= y.MaxLengthM + 1e-9) ?? D.Yards[^1];

    /// <summary>"A Cruiser (260 m) needs a Great yard (260 m): a starbase's slipway".</summary>
    public static string NeedsYard(ShipClass cls)
    {
        var y = YardNeeded(cls);
        var where = y.Id switch
        {
            "dock-bay" => "a dry dock",
            "yard" => "a shipyard",
            "great-yard" => "a starbase or fortress with a great slipway",
            _ => "an orbital ring's capital slipway",
        };
        return $"A {cls.Name.ToLowerInvariant()} ({cls.LengthM:0} m) needs a {y.Name.ToLowerInvariant()} ({y.MaxLengthM:0} m): {where}";
    }

    /// <summary>The yard size name for a length ("Great yard").</summary>
    public static string YardName(double lengthM) =>
        lengthM <= 0 ? "None" : D.Yards.LastOrDefault(y => y.MaxLengthM <= lengthM + 1e-9)?.Name ?? D.Yards[0].Name;

    // ---- Orbits --------------------------------------------------------------------------------

    /// <summary>A fleet sitting in an orbit where it can besiege: low or high (far orbit does nothing).</summary>
    public static bool Besieging(Fleet f, Body b) => Economy.InOrbitAt(f, b) && f.OrbitHeight != OrbitHeight.Far;

    /// <summary>The moons of a planet within this reach (Mm) of it: under its guns.</summary>
    public static IEnumerable<Body> MoonsWithin(World w, Body planet, double reachMm) =>
        w.Bodies.Where(m => m.ParentId == planet.Id && m.Kind == BodyKind.Moon && m.OrbitRadius <= reachMm + 1e-9);

    /// <summary>The planet whose guns cover this moon (its holder is another side and its rails or silos reach it), if any.</summary>
    public static Body? CoveredBy(World w, Body moon, Side side)
    {
        if (moon.Kind != BodyKind.Moon || moon.ParentId is not int pid || w.FindBody(pid) is not Body planet) return null;
        if (planet.Owner is not Side owner || owner == side) return null;
        var reach = Math.Max(Colonies.Sum(w, planet, k => k.GunDamage) > 0 ? (Colonies.Sum(w, planet, k => k.HighOrbit ? k.GunDamage : 0) > 0 ? D.SiloReachMm : D.RailReachMm) : 0,
            Fort(w, planet) is Starbase st ? (Spec(st).Missiles > 0 ? D.SiloReachMm : D.RailReachMm) : 0);
        return reach > 0 && moon.OrbitRadius <= reach + 1e-9 ? planet : null;
    }

    /// <summary>Why this side can't take this moon while the planet above it still shoots, or null.</summary>
    public static string? CoverProblem(World w, Body moon, Side side) =>
        CoveredBy(w, moon, side) is Body p ? $"{moon.Name} is under {p.Name}'s guns: take or silence {p.Name} first" : null;

    // ---- Each hour -----------------------------------------------------------------------------

    /// <summary>Hourly in full games: old defence stations join the line, building and refits, stores, mending, losses.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        Migrate(sim);
        foreach (var st in w.Starbases.ToList())
        {
            var b = w.FindBody(st.BodyId);
            if (b == null) { w.Starbases.Remove(st); continue; }
            // A station is lost with its planet (a captured one is not: it was never over a planet its side held).
            if (st.HeldBody && b.Owner != st.Side)
            {
                w.Starbases.Remove(st);
                sim.Log.Info("stations", $"{st.Side} loses a station with its planet", new { side = st.Side.ToString(), st.Id, bodyId = b.Id, st.ClassId });
                continue;
            }
            st.HeldBody = b.Owner == st.Side;
            if (w.Hour < st.ReadyHour) continue;
            Refit(sim, st, b);
            Clamp(st);
            Stores(w, st, b);
            if (st.LastHitHour == null || w.Hour - st.LastHitHour > 24)
                st.Armour = Math.Min(MaxArmour(st), st.Armour + MaxArmour(st) * D.MendPerDay / 24);
        }
        foreach (var b in w.Bodies)
            b.LandingFactor = b.Owner is Side o && WorkingAt(w, b.Id, o) is Starbase f && Tier(f) >= D.LandingFactorTier ? D.LandingFactor : 1;
        MineUpkeep(sim);
    }

    /// <summary>Defence stations from before the line become tier 2 stations.</summary>
    private static void Migrate(Simulation sim)
    {
        var w = sim.World;
        foreach (var old in w.Stations.Where(s => s.Kind == "defence-station").ToList())
        {
            w.Stations.Remove(old);
            if (At(w, old.BodyId) != null) continue;
            var cls = GameData.ShipClass("station-defence");
            var st = new Starbase
            {
                Id = w.NextId++, Side = old.Side, BodyId = old.BodyId, ClassId = cls.Id, Fit = Fitting.StandardFit(cls), ReadyHour = old.ReadyHour,
                Armour = Math.Clamp(old.Armour, 1, cls.Armour), LastHitHour = old.LastHitHour, Ammo = D.BaseStore,
            };
            st.Name = DisplayName(w, st);
            w.Starbases.Add(st);
            sim.Log.Info("stations", "An old defence station joins the station line", new { side = st.Side.ToString(), st.Id, st.BodyId });
        }
    }

    private static void Refit(Simulation sim, Starbase st, Body b)
    {
        if (st.Refit is not { } r) return;
        var w = sim.World;
        r.HoursLeft -= GameTime.HoursPerTick;
        if (r.HoursLeft > 1e-9) return;
        var share = st.Armour / Math.Max(1, MaxArmour(st));
        var up = r.ClassId != st.ClassId;
        st.ClassId = r.ClassId;
        st.Fit = r.Fit.ToList();
        st.DesignId = r.DesignId;
        st.Refit = null;
        st.SpecCache = null;
        st.Name = DisplayName(w, st);
        // A new tier comes with its new armour whole; a bay swap keeps the share it had.
        st.Armour = up ? MaxArmour(st) : Math.Max(1, share * MaxArmour(st));
        st.Ammo = Math.Min(st.Ammo, AmmoCap(st));
        st.Fuel = Math.Min(st.Fuel, FuelCap(st));
        sim.Log.Info("stations", up ? $"{b.Name}'s station is now a {ClassOf(st).Name.ToLowerInvariant()}" : $"{b.Name}'s station refit is done",
            new { side = st.Side.ToString(), st.Id, st.ClassId, fit = string.Join(",", st.Fit) });
    }

    /// <summary>Armour and stores never above what the working fit holds (a bay swap takes its bays out meanwhile).</summary>
    public static void Clamp(Starbase st)
    {
        st.Armour = Math.Min(st.Armour, MaxArmour(st));
        st.Ammo = Math.Clamp(st.Ammo, 0, AmmoCap(st));
        st.Fuel = Math.Clamp(st.Fuel, 0, FuelCap(st));
    }

    /// <summary>Over a planet its side holds, the station fills its store from the planet's ammunition and fuels.</summary>
    private static void Stores(World w, Starbase st, Body b)
    {
        if (b.Owner != st.Side) return;
        var pace = D.StoreFillPerDay / 24;
        var ammo = Math.Min(pace, Math.Min(AmmoCap(st) - st.Ammo, Colonies.Held(b, "ammunition")));
        // An outpost with no store at all (no ground) keeps it topped up slowly from nothing, as it restocks ships.
        if (b.Board == null && b.Stock.Count == 0) ammo = Math.Min(pace * 0.25, AmmoCap(st) - st.Ammo);
        else if (ammo > 1e-9)
        {
            b.Stock["ammunition"] = Colonies.Held(b, "ammunition") - ammo;
            Ledger.Out(w, st.Side, "ammunition", ammo, "station store");
        }
        if (ammo > 1e-9) { st.Ammo += ammo; st.ForeignAmmo = false; }
        var room = FuelCap(st) - st.Fuel;
        if (room <= 1e-9) return;
        foreach (var f in GameData.Fuels.Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal))
        {
            var take = Math.Min(Math.Min(pace, room), Colonies.Held(b, f));
            if (take <= 1e-9) continue;
            b.Stock[f] = Colonies.Held(b, f) - take;
            Ledger.Out(w, st.Side, f, take, "station store");
            st.Fuel += take;
            room -= take;
            if (room <= 1e-9) break;
        }
    }

    /// <summary>Orbital minefields drift thin and cost upkeep (fields round a planet; loose ones already spread).</summary>
    private static void MineUpkeep(Simulation sim)
    {
        var w = sim.World;
        foreach (var m in w.Minefields.Where(m => m.BodyId != null && w.Hour - m.LaidHour >= 24).ToList())
        {
            m.Mines = Math.Max(0, m.Mines - m.Mines * D.MineDriftPerDay / 24);
            if (w.Loop) Budget.Drain(w, m.Side, Pot.Stations, m.Mines / 10 * D.MineUpkeepPer10 / 24, m.BodyId);
            if (m.Mines >= 0.5) continue;
            w.Minefields.Remove(m);
            sim.Log.Info("combat", "An orbital minefield has drifted away", new { fieldId = m.Id, side = m.Side.ToString(), m.BodyId });
        }
    }

    // ---- Fighting ------------------------------------------------------------------------------

    /// <summary>
    /// Hourly, from Planets.PlanetGuns: enemy warships in orbit shoot the station first (it is the biggest gun over the
    /// planet); the station's guns fire at enemy fleets in its orbits (and over moons in reach), as its defence plan says.
    /// At no armour it is wrecked.
    /// </summary>
    public static void Fight(Simulation sim)
    {
        var w = sim.World;
        foreach (var st in w.Starbases.Where(st => Working(w, st)).OrderBy(st => st.Id).ToList())
        {
            if (w.FindBody(st.BodyId) is not Body b) continue;
            var attackers = w.Fleets.Where(f => f.Side != st.Side && Besieging(f, b) && Combat.Armed(w, f)).OrderBy(f => f.Id).ToList();
            var damage = attackers.SelectMany(w.ShipsOf).Sum(s => Economy.HourlyDamage(w, s, consume: true));
            if (damage > 0)
            {
                if (st.LastHitHour == null || w.Hour - st.LastHitHour > 24)
                    sim.Log.Info("stations", $"{b.Name}'s station is under fire", new { bodyId = b.Id, side = st.Side.ToString(), attacker = attackers[0].Side.ToString() });
                st.LastHitHour = w.Hour;
                st.Armour -= damage;
                if (st.Armour <= 0)
                {
                    Wreck(sim, st, b, attackers[0].Side);
                    continue;
                }
            }
            Shoot(sim, st, b);
        }
    }

    /// <summary>The station's guns fire at enemy fleets in its orbits and over the moons in their reach.</summary>
    private static void Shoot(Simulation sim, Starbase st, Body b)
    {
        var w = sim.World;
        if (st.Fire == StationFire.Hold || Guns(st) <= 0) return;
        var ring = TierOf(st.ClassId).LiftCut > 0;
        var targets = w.Fleets.Where(f => f.Side != st.Side && f.ShipIds.Count > 0 && Economy.InOrbitAt(f, b)
                                          && (f.OrbitHeight == OrbitHeight.Low || f.OrbitHeight == OrbitHeight.High && st.Fire == StationFire.Both || f.OrbitHeight == OrbitHeight.Far && ring))
            .ToList();
        if (st.Fire == StationFire.Both)
        {
            var reach = Spec(st).Missiles > 0 ? D.SiloReachMm : D.RailReachMm;
            foreach (var moon in MoonsWithin(w, b, reach))
                targets.AddRange(w.Fleets.Where(f => f.Side != st.Side && f.ShipIds.Count > 0 && Besieging(f, moon)));
        }
        if (targets.Count == 0) return;
        var share = GunShare(st);
        st.Ammo = Math.Max(0, st.Ammo - AmmoPerHour(st));
        var pool = Guns(st) * share;
        foreach (var f in targets.OrderBy(f => f.OrbitHeight).ThenBy(f => f.Id))
        {
            if (pool <= 1e-9) break;
            var ship = w.ShipsOf(f).OrderBy(s => s.Armour).ThenBy(s => s.Id).FirstOrDefault();
            if (ship == null) continue;
            var hit = Math.Min(pool, ship.Armour);
            pool -= hit;
            ship.Armour -= hit;
            if (ship.Armour > 1e-9) continue;
            Economy.Of(w, ship.Side).Totals.ShipsLost++;
            Economy.Of(w, st.Side).Totals.ShipsKilled++;
            sim.Log.Info("stations", $"{b.Name}'s station destroys {ship.Name}", new { bodyId = b.Id, shipId = ship.Id, side = ship.Side.ToString() });
            Combat.Destroy(sim, ship);
        }
    }

    /// <summary>Ground guns of the planet's holder hit an enemy station over it (a captured one): what is left of the low-orbit fire.</summary>
    public static void TakeGroundFire(Simulation sim, Body b, double damage)
    {
        var w = sim.World;
        if (damage <= 1e-9 || b.Owner is not Side owner || At(w, b.Id) is not Starbase st || st.Side == owner || !Working(w, st)) return;
        st.Armour -= damage;
        st.LastHitHour = w.Hour;
        if (st.Armour <= 0) Wreck(sim, st, b, owner);
    }

    private static void Wreck(Simulation sim, Starbase st, Body b, Side by)
    {
        var w = sim.World;
        w.Starbases.Remove(st);
        Economy.Of(w, by).Totals.FortsWrecked++;
        sim.Log.Info("stations", $"{b.Name}'s station is wrecked", new { bodyId = b.Id, side = st.Side.ToString(), by = by.ToString(), st.ClassId });
    }

    /// <summary>Point defence mounts a station lends a friendly fleet within its umbrella: in low or high orbit under it, or within umbrellaMm of its orbit.</summary>
    public static double Umbrella(World w, Fleet f)
    {
        if (w.Starbases.Count == 0) return 0;
        double mounts = 0;
        foreach (var st in w.Starbases)
            if (st.Side == f.Side && Working(w, st) && w.FindBody(st.BodyId) is Body b
                && (Besieging(f, b) || Vec2.Distance(b.Position, f.Position) <= D.UmbrellaMm + Fleets.OrbitRadiusFor(b)))
                mounts += Spec(st).PointDefence * D.UmbrellaShare;
        return mounts;
    }

    // ---- Serving friendly fleets --------------------------------------------------------------

    /// <summary>The body whose station this fleet holds low orbit under (its own side's working station), if any.</summary>
    public static Body? StationedAt(World w, Fleet f)
    {
        if (f.OrbitBodyId is not int id || f.OrbitHeight != OrbitHeight.Low || w.FindBody(id) is not Body b || !Economy.InOrbitAt(f, b)) return null;
        return WorkingAt(w, id, f.Side) != null ? b : null;
    }

    /// <summary>Does this side's station over the body mend ships (a repair hangar or a dock bay)?</summary>
    public static bool Repairs(World w, Body b, Side side) =>
        WorkingAt(w, b.Id, side) is Starbase st && Bays(st).Any(m => m.Bay is "hangar" or "dock");

    /// <summary>
    /// Hourly for a fleet holding orbit over a body where its side has a station (from Simulation): a magazine
    /// reloads missiles and ammunition from the station's store, a fuel depot refuels (over a planet it does not
    /// hold, all supplies come from the store). Mending is RepairAtHome's; a fleet that was mended is docked.
    /// </summary>
    public static void Serve(World w, Fleet f, double hours, Body b)
    {
        f.Docked = false;
        if (StationedAt(w, f) is null || WorkingAt(w, b.Id, f.Side) is not Starbase st) return;
        var magazine = HasBay(st, "magazine");
        var depot = HasBay(st, "fuel-depot");
        var held = b.Owner == f.Side;
        foreach (var s in w.ShipsOf(f))
        {
            if (s.Armour < Tech.MaxArmour(w, s) - 1e-6 && Repairs(w, b, f.Side)) f.Docked = true;
            if (magazine)
            {
                var want = SupplyLines.MaxAmmo(s) - s.Ammo;
                var goods = Math.Min(st.Ammo, Math.Min(want, SupplyLines.MaxAmmo(s) * SupplyLines.RestockPerHour * hours) / SupplyLines.AmmoPerGood);
                if (goods > 1e-9) { s.Ammo += goods * SupplyLines.AmmoPerGood; st.Ammo -= goods; }
                if (s.Missiles < Tech.Racks(w, s) && st.Ammo >= 1 && w.Hour % 6 < hours)
                {
                    s.Missiles++;
                    st.Ammo -= 1;
                }
                if (w.Loop && !st.ForeignAmmo) (s.MissileGrade, s.RailGrade) = Munitions.Made(w, s.Side);
            }
            if (depot || !held)
            {
                var max = Fitting.MaxSupplies(w, s);
                var want = Math.Min(max - s.Supplies, max * 0.25 * hours / 24);
                var fuel = Math.Min(st.Fuel, want / 4);
                if (fuel > 1e-9) { s.Supplies += fuel * 4; st.Fuel -= fuel; }
            }
        }
        // Supply ships of the station's side top up its store over a planet it does not hold (its own ammunition at last).
        if (!held)
            foreach (var s in w.ShipsOf(f).Where(s => s.CargoAmmo > 1e-9))
            {
                var give = Math.Min(s.CargoAmmo / SupplyLines.AmmoPerGood, AmmoCap(st) - st.Ammo);
                if (give <= 1e-9) continue;
                s.CargoAmmo -= give * SupplyLines.AmmoPerGood;
                st.Ammo += give;
                st.ForeignAmmo = false;
            }
    }

    // ---- Boarding ------------------------------------------------------------------------------

    /// <summary>Why this fleet can't board the enemy station over the body it orbits, or null.</summary>
    public static string? CanBoard(World w, Fleet f, Starbase? st)
    {
        if (!w.Loop) return "Only in a full game";
        if (st == null || st.Side == f.Side) return "Pick an enemy station";
        if (w.FindBody(st.BodyId) is not Body b || !Economy.InOrbitAt(f, b)) return "Hold orbit at its planet first";
        if (f.OrbitHeight != OrbitHeight.Low) return "Boarders cross from low orbit";
        if (!Working(w, st)) return "It is not finished yet: there is nothing to take";
        if (w.Boardings.Any(x => x.Outcome == null && x.StationId == st.Id)) return "Already being boarded";
        if (w.Boardings.Any(x => x.Outcome == null && x.FleetId == f.Id)) return "This fleet is already boarding";
        if (w.Fleets.Any(o => o.Side == st.Side && Combat.Armed(w, o) && Economy.InOrbitAt(o, b) && o.OrbitHeight != OrbitHeight.Far))
            return "Its own warships still cover it: beat them first";
        var (troops, parties) = Boarders.Available(w, f);
        if (troops + parties.Sum(p => p.CrewK) < 0.01) return "No troops aboard and no crew to spare for a boarding party";
        return null;
    }

    /// <summary>
    /// The station changes hands: hull, stores and bays go to the boarders' side (its guns fire at half until their
    /// own ammunition reaches it), a hull on its slipway is towed off as a prize, and the planet below is not taken.
    /// </summary>
    public static void Capture(Simulation sim, Starbase st, Fleet captor)
    {
        var w = sim.World;
        var loser = st.Side;
        var b = w.FindBody(st.BodyId)!;
        // The hull on the slipway (the first started order there) is taken with it.
        var slip = w.Orders.FirstOrDefault(o => o.Side == loser && o.Kind == OrderKind.Ship && o.Paid && Economy.YardFor(w, o) == b
                                                && GameData.ShipClasses.TryGetValue(o.Key, out var c) && c.LengthM > YardSize("yard").MaxLengthM);
        st.Side = captor.Side;
        st.HeldBody = b.Owner == captor.Side;
        st.ForeignAmmo = true;
        st.Refit = null;
        st.Fire = StationFire.Both;
        st.ScuttleIfTaken = false;
        st.DesignId = Designer.StandardId;
        st.SpecCache = null;
        Economy.Of(w, captor.Side).Totals.FortsWrecked++;
        sim.Log.Info("stations", $"{captor.Name} takes {b.Name}'s station", new { side = captor.Side.ToString(), from = loser.ToString(), st.Id, st.ClassId, bodyId = b.Id });
        if (slip != null)
        {
            w.Orders.Remove(slip);
            var share = Math.Clamp(1 - slip.HoursLeft / Math.Max(1, slip.TotalHours), 0.1, 1);
            var prize = Economy.NewShip(w, captor.Side, slip.Key, b, null);
            prize.Armour = Math.Max(1, Tech.MaxArmour(w, prize) * share);
            prize.Crew = 0.2;
            var fleet = Fleets.Create(w, captor.Side, new[] { prize }, captor.Position + new Vec2(2, 2));
            fleet.Hold = captor.Hold;
            fleet.OrbitBodyId = captor.OrbitBodyId;
            fleet.OrbitRadius = captor.OrbitRadius;
            fleet.OrbitAngle = captor.OrbitAngle;
            sim.Log.Info("stations", "A hull on the captured slipway is towed off", new { side = captor.Side.ToString(), shipId = prize.Id, prize.ClassId, share = Math.Round(share, 2), fleetId = fleet.Id });
        }
    }

    // ---- Help ----------------------------------------------------------------------------------

    /// <summary>"Starbase (tier 3) · 620 of 750 armour · guns 12 an hour · store 40 of 80".</summary>
    public static string Summary(World w, Starbase st)
    {
        var bits = new List<string> { $"{ClassOf(st).Name} (tier {Tier(st)})" };
        if (w.Hour < st.ReadyHour) bits.Add($"being built, {Math.Max(0, st.ReadyHour - w.Hour) / 24:0.#} days to go");
        else
        {
            bits.Add($"{Math.Max(0, st.Armour):0} of {MaxArmour(st):0} armour");
            if (Guns(st) > 0) bits.Add($"guns {Guns(st) * GunShare(st):0.#} an hour{(st.Fire == StationFire.Hold ? " (holding fire)" : st.Fire == StationFire.LowOnly ? " (low orbit only)" : "")}");
            bits.Add($"store {st.Ammo:0} of {AmmoCap(st):0} ammunition{(FuelCap(st) > 0 ? $", {st.Fuel:0} of {FuelCap(st):0} fuel" : "")}");
            if (st.ForeignAmmo) bits.Add("captured: guns at half until your ammunition reaches it");
        }
        if (st.Refit is { } r) bits.Add($"{(r.ClassId != st.ClassId ? "upgrading to " + GameData.ShipClass(r.ClassId).Name.ToLowerInvariant() : "refitting bays")}, {r.HoursLeft / 24:0.#} days left");
        return string.Join(" · ", bits);
    }

    /// <summary>What the station's bays do, one short phrase each.</summary>
    public static List<string> BayNotes(World w, Starbase st)
    {
        var notes = new List<string>();
        foreach (var m in Bays(st))
            notes.Add(m.Bay switch
            {
                "hangar" => "repairs fleets holding low orbit",
                "dock" => Tech.Has(w, st.Side, "colony-shipyards") ? $"repairs, builds hulls up to {m.YardM:0} m" : "repairs fleets (builds with Colony shipyards)",
                "slipway" => $"builds hulls up to {m.YardM:0} m",
                "magazine" => "rearms fleets in orbit",
                "fuel-depot" => "refuels fleets in orbit",
                "watch" => "sees as far as home, spots for the fleet",
                "relay" => "passes orders round the planet",
                "laser" => "pushes your sail ships",
                _ => m.Name,
            });
        return notes;
    }
}

// ---- Commands ------------------------------------------------------------------------------

/// <summary>Build a station of the line over a body you hold: paid now, working after its build days.</summary>
/// <summary>A station waiting in line for its Industry (BuildStarbaseCommand with Queue).</summary>
public sealed class QueuedStarbase
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int BodyId { get; set; }
    public string ClassId { get; set; } = "";
    public int? DesignId { get; set; }
}

/// <summary>Take a queued station off the queue.</summary>
public sealed record CancelQueuedStarbaseCommand(Side Side, int QueuedId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var q = world.StarbaseQueue.Find(x => x.Id == QueuedId && x.Side == Side);
        if (q == null) return;
        world.StarbaseQueue.Remove(q);
        sim.Log.Info("stations", "Queued station cancelled", new { side = Side.ToString(), q.BodyId, q.ClassId, Source });
    }
}

public sealed record BuildStarbaseCommand(Side Side, int BodyId, string ClassId, int? DesignId = null, bool Queue = false) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (Queue)
        {
            if (Starbases.CanBuild(world, Side, BodyId, ClassId, queue: true) is string why)
            {
                sim.Log.Warn("stations", $"Can't queue a station: {why}", new { side = Side.ToString(), BodyId, ClassId, Source });
                return;
            }
            world.StarbaseQueue.Add(new QueuedStarbase { Id = world.NextId++, Side = Side, BodyId = BodyId, ClassId = ClassId, DesignId = DesignId });
            sim.Log.Info("stations", "Station queued until its Industry is there", new { side = Side.ToString(), BodyId, ClassId, Source });
            return;
        }
        if (Starbases.CanBuild(world, Side, BodyId, ClassId) is string problem)
        {
            sim.Log.Warn("stations", $"Can't build a station: {problem}", new { side = Side.ToString(), BodyId, ClassId, Source });
            return;
        }
        var cls = GameData.ShipClass(ClassId);
        var design = DesignId is int d && Designer.Find(world, Side, d)?.ClassId == ClassId ? d : Designer.DefaultFor(world, Side, ClassId);
        Budget.Pay(world, Side, Pot.Stations, cls.Cost, Starbases.Key(BodyId, ClassId), BodyId, log: sim.Log);
        Economy.Of(world, Side).Totals.IndustrySpent += cls.Cost;
        var st = new Starbase
        {
            Id = world.NextId++, Side = Side, BodyId = BodyId, ClassId = ClassId, DesignId = design, Fit = Starbases.FitFor(world, Side, ClassId, design),
            ReadyHour = world.Hour + Starbases.TierOf(ClassId).Days * 24, Ammo = Starbases.D.BaseStore,
        };
        st.Armour = Starbases.MaxArmour(st);
        st.Name = Starbases.DisplayName(world, st);
        st.ScuttleIfTaken = Mind.Of(world, Side) != null && Starbases.Tier(st) >= 3;
        world.Starbases.Add(st);
        sim.Log.Info("stations", $"{Side} builds a {cls.Name.ToLowerInvariant()} at {world.FindBody(BodyId)!.Name}",
            new { side = Side.ToString(), st.Id, BodyId, ClassId, design, days = Starbases.TierOf(ClassId).Days, Source });
    }
}

/// <summary>
/// Upgrade a station a tier (costs the difference, takes half the new tier's days) or refit its bays to a variant
/// (each new part at full price, half a day a part): the station keeps working meanwhile, less the bays being swapped.
/// </summary>
public sealed record RefitStarbaseCommand(Side Side, int StationId, string ClassId, int DesignId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) =>
        (Side, world.Starbases.Find(s => s.Id == StationId) is Starbase st && world.FindBody(st.BodyId) is Body b ? b.Position : Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        var st = world.Starbases.Find(s => s.Id == StationId);
        if (Starbases.CanRefit(world, Side, st, ClassId, DesignId) is string problem)
        {
            sim.Log.Warn("stations", $"Can't refit a station: {problem}", new { side = Side.ToString(), StationId, ClassId, DesignId, Source });
            return;
        }
        var fit = Starbases.FitFor(world, Side, ClassId, DesignId);
        var up = ClassId != st!.ClassId;
        var cost = up ? Starbases.UpgradeCost(st, ClassId) : Starbases.RefitCost(st, fit);
        var swaps = up ? 0 : Enumerable.Range(0, fit.Count).Count(i => fit[i] != st.Fit[i]);
        var hours = up ? Starbases.TierOf(ClassId).Days * 12 : Math.Max(12, swaps * 12);
        Budget.Pay(world, Side, Pot.Stations, cost, $"upgrade:{StationId}:{ClassId}", st.BodyId, log: sim.Log);
        Economy.Of(world, Side).Totals.IndustrySpent += cost;
        st.Refit = new StationRefit { ClassId = ClassId, Fit = fit, DesignId = DesignId, HoursLeft = hours, TotalHours = hours };
        st.SpecCache = null;
        Starbases.Clamp(st);
        sim.Log.Info("stations", up ? $"{Side} upgrades a station" : $"{Side} refits a station's bays",
            new { side = Side.ToString(), StationId, ClassId, DesignId, cost, days = hours / 24, Source });
    }
}

/// <summary>The station's defence plan: what its guns fire at, and whether it is scuttled rather than taken.</summary>
public sealed record SetStationPlanCommand(Side Side, int StationId, StationFire Fire, bool ScuttleIfTaken) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) =>
        (Side, world.Starbases.Find(s => s.Id == StationId) is Starbase st && world.FindBody(st.BodyId) is Body b ? b.Position : Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (world.Starbases.Find(s => s.Id == StationId) is not Starbase st || st.Side != Side)
        {
            sim.Log.Warn("stations", "Plan for a station that is not ours", new { side = Side.ToString(), StationId, Source });
            return;
        }
        st.Fire = Fire;
        st.ScuttleIfTaken = ScuttleIfTaken;
        sim.Log.Info("stations", "Station defence plan set", new { side = Side.ToString(), StationId, fire = Fire.ToString(), ScuttleIfTaken, Source });
    }
}

/// <summary>Blow up your own station: its stores are lost, but nobody can take it.</summary>
public sealed record ScuttleStarbaseCommand(Side Side, int StationId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) =>
        (Side, world.Starbases.Find(s => s.Id == StationId) is Starbase st && world.FindBody(st.BodyId) is Body b ? b.Position : Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (world.Starbases.Find(s => s.Id == StationId) is not Starbase st || st.Side != Side)
        {
            sim.Log.Warn("stations", "Scuttle for a station that is not ours", new { side = Side.ToString(), StationId, Source });
            return;
        }
        world.Starbases.Remove(st);
        foreach (var b in world.Boardings.Where(b => b.Outcome == null && b.StationId == StationId))
        {
            b.Outcome = "scuttled";
            b.EndHour = world.Hour;
            b.Notes.Add("The station is blown up before the boarders can take it");
        }
        sim.Log.Info("stations", $"{Side} scuttles a station", new { side = Side.ToString(), StationId, st.ClassId, st.BodyId, Source });
    }
}

/// <summary>Send troops and boarding parties across to the enemy station over the planet the fleet holds low orbit at.</summary>
public sealed record BoardStationCommand(int FleetId, int StationId, BoardApproach Approach = BoardApproach.Dock) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Boarding") is not Fleet f) return;
        var st = world.Starbases.Find(s => s.Id == StationId);
        if (Starbases.CanBoard(world, f, st) is string problem)
        {
            sim.Log.Warn("boarding", $"Can't board the station: {problem}", new { FleetId, StationId, Source });
            return;
        }
        Boarders.StartOnStation(sim, f, st!, Approach == BoardApproach.Cyber ? BoardApproach.Dock : Approach);
    }
}
