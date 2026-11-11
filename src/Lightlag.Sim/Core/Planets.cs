using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (Data/economy.json, "planets") ---------------------------------------------------

/// <summary>What a type of world can hold: people (thousands), how well farms do there, and building plots.</summary>
public sealed record PlanetTypeData(double Cap = 0, double Farm = 0, int Plots = 2);

/// <summary>The pulls on a planet's happiness (see Planets.TargetHappiness).</summary>
public sealed record HappinessData(double Base = 55, double Fed = 10, double Hungry = -30, double Blockaded = -20, double Bombed = -30,
    double Struck = -10, double Foreign = -30, double Guarded = 5, double BombedDays = 10, double StruckDays = 5);

/// <summary>A building on a planet: paid when ordered, standing after Days, and needing StaffK people to run fully.</summary>
public sealed record BuildingData(string Id, string Name, string Effect, double Cost, double Days, double StaffK, double Industry = 0);

public sealed record PlanetsData(
    Dictionary<string, PlanetTypeData>? Types = null, double MoonCapShare = 0.3, double HomeCap = 200, double HomeStartK = 100,
    List<string>? HomeBuildings = null, double SpreadStartK = 6, double GrowthPerDay = 0.02, double StarvePerDay = 0.03,
    double FoodPerKPerDay = 0.01, double FarmFoodPerDay = 1, double FoodStockDays = 20, double BaseStaffK = 5, double UnsettledShare = 0.5,
    double LeaveAfterHungryDays = 20, double CrewKPerCost = 0.0025, double KeepAtSourceK = 5, double HappinessStart = 60,
    double HappinessPerDay = 2, HappinessData? Happiness = null, double SurrenderBelow = 25, double SurrenderBelowTipped = 50,
    double HungryDaysToSurrender = 5, double DecayAfterDays = 30, double DecayEveryDays = 10, double GarrisonPerK = 0.02,
    double GroundLossPerHour = 0.05, double BoardTroopsK = 0.5, double BuildingArmour = 40, double PeopleKPerDamage = 0.02,
    double GunDamagePerHour = 8, double GunGarrisonK = 0.5, double TipShare = 0.8, double WinShare = 0.95, double WinAfterDays = 30,
    List<BuildingData>? Buildings = null);

/// <summary>
/// Planets and people. Each planet holds one number of people (thousands). People eat food from farms
/// (shared between your planets unless a blockade cuts one off), grow while fed, staff the planet's
/// industry and buildings, and have a happiness that moves with food, blockades and bombing. A blockaded
/// planet that grows unhappy enough gives itself up to the blockader, unless the blockader bombed its
/// people. Colony ships carry people to empty worlds and between your planets (taken ones too); troop
/// ships land soldiers to take a planet by force. Runs once per game hour in full games.
/// </summary>
public static class Planets
{
    public static PlanetsData D => GameData.Economy.Planets ?? new PlanetsData();
    private static HappinessData H => D.Happiness ?? new HappinessData();

    public const string Farm = "farm", Mine = "mine", Guns = "guns";

    public static BuildingData? BuildingKind(string id) => D.Buildings?.Find(b => b.Id == id);

    public static PlanetTypeData TypeOf(Body b) =>
        D.Types != null && D.Types.TryGetValue(b.Type, out var t) ? t : new PlanetTypeData();

    /// <summary>The most people (k) this body can hold: 0 for gas giants and the star.</summary>
    public static double Cap(Body b)
    {
        if (b.Kind == BodyKind.Star) return 0;
        var cap = TypeOf(b).Cap * (b.Kind == BodyKind.Moon ? D.MoonCapShare : 1);
        return b.Home ? Math.Max(cap, D.HomeCap) : cap;
    }

    public static bool Settleable(Body b) => Cap(b) > 0;

    public static int Plots(Body b) => b.Home ? Math.Max(TypeOf(b).Plots, 8) : TypeOf(b).Plots;

    /// <summary>Food a farm makes here per day, as a share of a terran farm (homes are always well farmed).</summary>
    public static double FarmFactor(Body b) => b.Home ? Math.Max(1, TypeOf(b).Farm) : TypeOf(b).Farm;

    public static int Count(Body b, string kind) => b.Buildings.Count(x => x == kind);

    /// <summary>People needed to run the planet fully: its base industry and every building.</summary>
    public static double StaffNeeded(Body b) =>
        (b.Industry > 0 ? D.BaseStaffK : 0) + b.Buildings.Sum(x => BuildingKind(x)?.StaffK ?? 0);

    /// <summary>Share of the work that gets done: 1 with enough people, less with too few.</summary>
    public static double Staffing(Body b)
    {
        var need = StaffNeeded(b);
        return need <= 0 ? 1 : Math.Clamp(b.PopK / need, 0, 1);
    }

    /// <summary>Happy planets work a little harder: 0.7 at happiness 0, 1 at 60, 1.2 at 100.</summary>
    public static double HappyFactor(World w, Body b) => w.Rules.Happiness ? 0.7 + 0.5 * b.Happiness / 100 : 1;

    public static double FoodMadePerDay(Body b) => Count(b, Farm) * D.FarmFoodPerDay * FarmFactor(b) * Staffing(b);
    public static double FoodNeedPerDay(Body b) => b.PopK * D.FoodPerKPerDay;

    /// <summary>The armed enemy side holding orbit here, if any (it cuts the planet off: see Economy.Blockaded).</summary>
    public static Side? Blockader(World w, Body b) =>
        b.Owner is Side owner && Economy.Blockaded(w, b) ? Combat.Other(owner) : null;

    /// <summary>Industry a day this body makes for its holder (before Automated industry).</summary>
    public static double IndustryPerDay(World w, Body b)
    {
        if (b.Owner == null || b.Kind == BodyKind.Star) return 0;
        if (Economy.Blockaded(w, b)) return 0;
        if (b.PopK <= 0) return b.Industry * D.UnsettledShare;
        var mines = b.Buildings.Sum(x => BuildingKind(x)?.Industry ?? 0);
        return (b.Industry + mines) * Staffing(b) * HappyFactor(w, b);
    }

    public static double PopulationOf(World w, Side side) => w.Bodies.Where(b => b.Owner == side).Sum(b => b.PopK);

    public static bool IsColonyShip(Ship s) => Fitting.Spec(s).CargoKind == "people";
    public static bool IsTroopShip(Ship s) => Fitting.Spec(s).CargoKind == "troops";
    public static double PeopleHold(Ship s) => IsColonyShip(s) ? Fitting.Spec(s).Cargo : 0;
    public static double TroopHold(Ship s) => IsTroopShip(s) ? Fitting.Spec(s).Cargo : 0;

    public static double PeopleAboard(World w, Fleet f) => w.ShipsOf(f).Sum(s => s.CargoPeople);
    public static double TroopsAboard(World w, Fleet f) => w.ShipsOf(f).Sum(s => s.CargoTroops);

    // ---- Control (the win) -------------------------------------------------------------------

    /// <summary>
    /// A side's share of control, 0 to 1: the average of its shares of people, Industry a day, bodies
    /// worth holding and fleet power. Each part counts half to each side while neither has any.
    /// </summary>
    public static (double Share, double People, double Industry, double Bodies, double Fleets) Control(World w, Side side)
    {
        var other = Combat.Other(side);
        static double Part(double mine, double theirs) => mine + theirs <= 1e-9 ? 0.5 : mine / (mine + theirs);
        var people = Part(PopulationOf(w, side), PopulationOf(w, other));
        var industry = Part(Economy.IncomePerDay(w, side), Economy.IncomePerDay(w, other));
        var bodies = Part(Economy.Claimable(w).Count(b => b.Owner == side), Economy.Claimable(w).Count(b => b.Owner == other));
        var fleets = Part(Stats.Power(w, side), Stats.Power(w, other));
        return ((people + industry + bodies + fleets) / 4, people, industry, bodies, fleets);
    }

    /// <summary>The side has tipped the game: planets it blockades give up sooner (see economy.json tipShare).</summary>
    public static bool Tipped(World w, Side side) => Economy.Of(w, side).MajoritySinceHour != null;

    // ---- Start ----------------------------------------------------------------------------------

    /// <summary>Gives the homes their people and buildings (and, with that option, a few people everywhere).</summary>
    public static void EnsureStarted(World w)
    {
        if (!w.Loop || w.PeopleStarted) return;
        w.PeopleStarted = true;
        foreach (var b in w.Bodies.Where(b => b.Shipyard && b.Owner != null))
        {
            b.Home = true;
            b.PopK = Math.Min(Cap(b), D.HomeStartK);
            b.Buildings = new List<string>(D.HomeBuildings ?? new List<string>());
            b.Happiness = D.HappinessStart;
            b.OwnShare = 1;
            b.FoodStock = FoodNeedPerDay(b) * D.FoodStockDays;
        }
        if (!w.Rules.StartPopulation) return;
        foreach (var b in w.Bodies.Where(b => !b.Home && Settleable(b) && b.Industry > 0))
        {
            b.PopK = Math.Min(Cap(b), D.SpreadStartK);
            b.Happiness = D.HappinessStart;
            b.OwnShare = b.Owner != null ? 1 : 0.5;
        }
    }

    // ---- The hour ---------------------------------------------------------------------------

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        EnsureStarted(w);
        Construction(sim);
        Food(w);
        Grow(sim);
        Moods(w);
        Decay(sim);
        Bomb(sim);
        PlanetGuns(sim);
        Ground(sim);
        Surrenders(sim);
        Runs(sim);
        TopUps(sim);
        LoadTroops(w);
    }

    private static void Construction(Simulation sim)
    {
        foreach (var b in sim.World.Bodies.Where(b => b.Building != null))
        {
            if (b.Owner == null) { b.Building = null; continue; }
            if (Economy.Blockaded(sim.World, b)) continue;
            b.BuildingHoursLeft -= GameTime.HoursPerTick;
            if (b.BuildingHoursLeft > 1e-9) continue;
            b.Buildings.Add(b.Building!);
            sim.Log.Info("planets", $"{BuildingKind(b.Building!)?.Name ?? b.Building} built at {b.Name}", new { bodyId = b.Id, side = b.Owner.ToString(), kind = b.Building });
            b.Building = null;
            b.BuildingHoursLeft = 0;
        }
    }

    /// <summary>
    /// Food: your planets that are not blockaded share one pool (their stores are filled and drawn down
    /// together); a blockaded planet lives off its own farms and store. Neutral planets feed themselves.
    /// </summary>
    private static void Food(World w)
    {
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var mine = w.Bodies.Where(b => b.Owner == side).ToList();
            var linked = mine.Where(b => !Economy.Blockaded(w, b)).ToList();
            double made = 0, need = 0, stock = 0, room = 0;
            foreach (var b in linked)
            {
                made += FoodMadePerDay(b);
                need += FoodNeedPerDay(b);
                stock += b.FoodStock;
                room += FoodNeedPerDay(b) * D.FoodStockDays;
            }
            var after = stock + (made - need) / 24;
            var hungry = after < 0 && need > 0;
            after = Math.Clamp(after, 0, room);
            foreach (var b in linked)
            {
                var share = room > 0 ? FoodNeedPerDay(b) * D.FoodStockDays / room : 0;
                b.FoodStock = after * share;
                SetHungry(b, hungry && b.PopK > 0);
            }
            foreach (var b in mine.Where(b => Economy.Blockaded(w, b)))
            {
                var left = b.FoodStock + (FoodMadePerDay(b) - FoodNeedPerDay(b)) / 24;
                SetHungry(b, left < 0 && b.PopK > 0);
                b.FoodStock = Math.Clamp(left, 0, FoodNeedPerDay(b) * D.FoodStockDays);
            }
        }
        foreach (var b in w.Bodies.Where(b => b.Owner == null)) SetHungry(b, false);
    }

    private static void SetHungry(Body b, bool hungry)
    {
        b.Hungry = hungry;
        b.HungryHours = hungry ? b.HungryHours + GameTime.HoursPerTick : 0;
    }

    /// <summary>People grow towards the planet's cap while fed and dwindle while hungry; long hunger empties a planet.</summary>
    private static void Grow(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies.Where(b => b.PopK > 0).ToList())
        {
            var cap = Cap(b);
            if (b.Hungry) b.PopK -= D.StarvePerDay * b.PopK / 24;
            else if (cap > 0) b.PopK += D.GrowthPerDay * b.PopK * (1 - b.PopK / cap) / 24;
            if (cap <= 0 || b.PopK > cap) b.PopK = Math.Max(0, Math.Min(b.PopK, cap));
            if (b.PopK < 0.05) { b.PopK = 0; continue; }
            if (b.Owner is Side side && b.HungryHours >= D.LeaveAfterHungryDays * 24) Leave(sim, b, side);
        }
    }

    /// <summary>The hungry people of a planet give up on it and move to the nearest of their side's planets that can feed them.</summary>
    private static void Leave(Simulation sim, Body b, Side side)
    {
        var w = sim.World;
        var to = w.Bodies.Where(o => o.Owner == side && o.Id != b.Id && o.PopK > 0 && !Economy.Blockaded(w, o) && Cap(o) > o.PopK)
            .OrderBy(o => Vec2.Distance(o.Position, b.Position)).ThenBy(o => o.Id).FirstOrDefault();
        var people = b.PopK;
        if (to != null)
        {
            var moved = Math.Min(people, Cap(to) - to.PopK);
            to.OwnShare = (to.OwnShare * to.PopK + b.OwnShare * moved) / Math.Max(1e-9, to.PopK + moved);
            to.PopK += moved;
            to.Happiness = Math.Max(0, to.Happiness - 5);
        }
        b.PopK = 0;
        b.HungryHours = 0;
        b.Hungry = false;
        sim.Log.Info("planets", $"The hungry people of {b.Name} abandon it", new { bodyId = b.Id, side = side.ToString(), people, to = to?.Name });
    }

    /// <summary>What a planet's happiness is moving towards, and the reasons, for the planet screen.</summary>
    public static (double Target, List<(string Why, double Amount)> Parts) TargetHappiness(World w, Body b)
    {
        var parts = new List<(string, double)> { ("Base", H.Base) };
        if (b.Hungry) parts.Add(("Hungry", H.Hungry));
        else parts.Add(("Fed", H.Fed));
        if (Economy.Blockaded(w, b)) parts.Add(("Blockaded", H.Blockaded));
        if (b.LastBombedHour is double bombed && w.Hour - bombed < H.BombedDays * 24) parts.Add(("People bombed", H.Bombed));
        if (b.LastStruckHour is double struck && w.Hour - struck < H.StruckDays * 24) parts.Add(("Buildings struck", H.Struck));
        if (b.OwnShare < 0.995) parts.Add(("Not your people", H.Foreign * (1 - b.OwnShare)));
        if (b.Owner is Side owner && w.Fleets.Any(f => f.Side == owner && Economy.InOrbitAt(f, b) && Combat.Armed(w, f))) parts.Add(("Guarded by a fleet", H.Guarded));
        return (Math.Clamp(parts.Sum(p => p.Item2), 0, 100), parts);
    }

    private static void Moods(World w)
    {
        foreach (var b in w.Bodies.Where(b => b.PopK > 0 && b.Owner != null))
        {
            if (!w.Rules.Happiness) { b.Happiness = D.HappinessStart; continue; }
            var (target, _) = TargetHappiness(w, b);
            var step = D.HappinessPerDay / 24;
            b.Happiness = b.Happiness < target ? Math.Min(target, b.Happiness + step) : Math.Max(target, b.Happiness - step);
            // Settled in and content, newcomers slowly come to see the holder as their own.
            if (b.Happiness >= 50 && b.OwnShare < 1) b.OwnShare = Math.Min(1, b.OwnShare + 0.01 / 24);
        }
    }

    /// <summary>Under a long blockade, buildings fail one by one for want of parts and upkeep.</summary>
    private static void Decay(Simulation sim)
    {
        foreach (var b in sim.World.Bodies.Where(b => b.Owner != null))
        {
            if (!Economy.Blockaded(sim.World, b)) { b.BlockadeHours = 0; continue; }
            b.BlockadeHours += GameTime.HoursPerTick;
            var over = b.BlockadeHours - D.DecayAfterDays * 24;
            if (over <= 0 || b.Buildings.Count == 0 || Math.Abs(over % (D.DecayEveryDays * 24)) > 1e-6) continue;
            var lost = b.Buildings[^1];
            b.Buildings.RemoveAt(b.Buildings.Count - 1);
            sim.Log.Info("planets", $"A {BuildingKind(lost)?.Name.ToLowerInvariant() ?? lost} on blockaded {b.Name} fails", new { bodyId = b.Id, kind = lost });
        }
    }

    public static string BuildingFor(BombTarget t) => t switch { BombTarget.Farms => Farm, BombTarget.Mines => Mine, BombTarget.Guns => Guns, _ => "" };

    /// <summary>Is any of the planet's holders' fleets close enough to stop bombing (as for the shipyard)?</summary>
    public static bool Defended(World w, Body b) =>
        b.Owner is Side owner && w.Fleets.Any(f => f.Side == owner && Vec2.Distance(f.Position, b.Position) <= GameData.Combat.ContactRangeMm && Combat.Armed(w, f));

    /// <summary>Why this fleet is or isn't bombing, in a line for the player (null when it isn't asked to).</summary>
    public static string? BombStatus(World w, Fleet f)
    {
        if (f.Bombing == BombTarget.None) return null;
        if (f.OrbitBodyId is not int id || w.FindBody(id) is not Body b || !Economy.InOrbitAt(f, b)) return "Bombing: waiting to reach orbit";
        if (b.Owner == null || b.Owner == f.Side) return "Bombing: only enemy planets";
        if (f.OrbitHeight != OrbitHeight.Low) return "Bombing: needs low orbit";
        if (Defended(w, b)) return "Bombing: held off, a defending fleet is near";
        return $"Bombing {f.Bombing.ToString().ToLowerInvariant()} at {b.Name}";
    }

    /// <summary>Fleets told to bomb hit the people or a kind of building of the enemy planet they hold low orbit over.</summary>
    private static void Bomb(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Bombing != BombTarget.None).ToList())
        {
            if (f.OrbitBodyId is not int id || w.FindBody(id) is not Body b || !Economy.InOrbitAt(f, b)) continue;
            if (b.Owner is not Side owner || owner == f.Side || f.OrbitHeight != OrbitHeight.Low || Defended(w, b)) continue;
            var kind = BuildingFor(f.Bombing);
            if (f.Bombing == BombTarget.People ? b.PopK <= 0 : Count(b, kind) == 0)
            {
                Autonomy.Report(sim, f, $"{f.Name} stops bombing {b.Name}: nothing left to hit", NoteKind.OwnDecision);
                f.Bombing = BombTarget.None;
                continue;
            }
            var damage = w.ShipsOf(f).Sum(s => Economy.HourlyDamage(w, s, consume: true));
            if (damage <= 0) continue;
            if (f.Bombing == BombTarget.People)
            {
                var first = b.Grudge != f.Side;
                b.PopK = Math.Max(0, b.PopK - damage * D.PeopleKPerDamage);
                b.LastBombedHour = w.Hour;
                b.Grudge = f.Side;
                if (first) sim.Log.Info("planets", $"{f.Side} bombs the people of {b.Name}: they will never give up to it", new { bodyId = b.Id, side = f.Side.ToString() });
                continue;
            }
            b.LastStruckHour = w.Hour;
            b.StrikeDamage += damage;
            if (b.StrikeDamage < D.BuildingArmour) continue;
            b.StrikeDamage = 0;
            b.Buildings.RemoveAt(b.Buildings.LastIndexOf(kind));
            sim.Log.Info("planets", $"{f.Side} knocks out a {BuildingKind(kind)?.Name.ToLowerInvariant() ?? kind} on {b.Name}", new { bodyId = b.Id, side = f.Side.ToString(), kind });
        }
    }

    /// <summary>Planet guns fire on enemy ships holding low orbit.</summary>
    private static void PlanetGuns(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies.Where(b => b.Owner != null && Count(b, Guns) > 0).ToList())
        {
            var damage = Count(b, Guns) * D.GunDamagePerHour * Staffing(b);
            foreach (var f in w.Fleets.Where(f => f.Side != b.Owner && Economy.InOrbitAt(f, b) && f.OrbitHeight == OrbitHeight.Low).ToList())
            {
                if (damage <= 0) break;
                var ship = w.ShipsOf(f).OrderBy(s => s.Armour).ThenBy(s => s.Id).First();
                var hit = Math.Min(damage, ship.Armour);
                damage -= hit;
                ship.Armour -= hit;
                if (ship.Armour > 1e-9) continue;
                Economy.Of(w, ship.Side).Totals.ShipsLost++;
                Economy.Of(w, b.Owner!.Value).Totals.ShipsKilled++;
                sim.Log.Info("planets", $"{b.Name}'s guns destroy {ship.Name}", new { bodyId = b.Id, shipId = ship.Id, side = ship.Side.ToString() });
                Combat.Destroy(sim, ship);
            }
        }
    }

    /// <summary>The planet's defenders against a landing: a share of its people (more if they are happy) and its guns.</summary>
    public static double GarrisonOf(Body b) =>
        b.PopK * D.GarrisonPerK * (0.5 + b.Happiness / 100) + Count(b, Guns) * D.GunGarrisonK;

    /// <summary>Ground fights: each side wears the other down by a share of its own strength each hour.</summary>
    private static void Ground(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies.Where(b => b.LandingSide != null).ToList())
        {
            var side = b.LandingSide!.Value;
            if (b.Owner == side) { b.LandingSide = null; b.LandingTroops = 0; continue; }
            var troops = b.LandingTroops - b.Garrison * D.GroundLossPerHour;
            var garrison = b.Garrison - b.LandingTroops * D.GroundLossPerHour;
            b.LandingTroops = Math.Max(0, troops);
            b.Garrison = Math.Max(0, garrison);
            if (b.Garrison <= 1e-6)
            {
                b.PopK *= 0.95;
                Take(sim, b, side, "troops took it");
            }
            else if (b.LandingTroops <= 1e-6)
            {
                b.LandingSide = null;
                sim.Log.Info("planets", $"The landing on {b.Name} is beaten off", new { bodyId = b.Id, side = side.ToString() });
            }
        }
    }

    /// <summary>The line below which a blockaded planet gives up to this side.</summary>
    public static double SurrenderLine(World w, Side to) => Tipped(w, to) ? D.SurrenderBelowTipped : D.SurrenderBelow;

    /// <summary>A blockaded, unhappy planet (or, with happiness off, a starving one) gives itself up to the blockader, unless it holds a grudge against it.</summary>
    private static void Surrenders(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies.Where(b => b.Owner != null && b.PopK > 0).ToList())
        {
            if (Blockader(w, b) is not Side by || b.Grudge == by) continue;
            var gives = w.Rules.Happiness ? b.Happiness < SurrenderLine(w, by) : b.HungryHours >= D.HungryDaysToSurrender * 24;
            if (gives) Take(sim, b, by, "surrendered to the blockade");
        }
    }

    /// <summary>A planet changes hands with its people: they are not the new holder's own, and a shipyard there is scuttled.</summary>
    public static void Take(Simulation sim, Body b, Side side, string how)
    {
        var w = sim.World;
        var before = b.Owner;
        b.Owner = side;
        b.OwnShare = b.PopK > 0 ? 0 : 1;
        b.Happiness = Math.Min(b.Happiness, 35);
        b.ClaimSide = null;
        b.ClaimHours = 0;
        b.LandingSide = null;
        b.LandingTroops = 0;
        b.Garrison = 0;
        b.Building = null;
        b.BuildingHoursLeft = 0;
        b.AutoTopUp = false;
        if (b.Shipyard && before is Side loser)
        {
            b.Shipyard = false;
            b.ShipyardArmour = 0;
            w.Orders.RemoveAll(o => o.Side == loser && o.Kind == OrderKind.Ship);
        }
        Economy.Of(w, side).Totals.PlanetsClaimed++;
        if (before is Side lost) Economy.Of(w, lost).Totals.PlanetsLost++;
        sim.Log.Info("planets", $"{side} takes {b.Name}: {how}", new { bodyId = b.Id, side = side.ToString(), from = before?.ToString(), people = Math.Round(b.PopK, 1) });
    }

    // ---- Colony ships -----------------------------------------------------------------------

    /// <summary>Why a colony fleet can't carry people between these two bodies, or null if it can.</summary>
    public static string? CanSendPeople(World w, Side side, Fleet f, int fromId, int toId)
    {
        if (f.Side != side) return "Not your fleet";
        if (!w.ShipsOf(f).Any(IsColonyShip)) return "Needs a colony ship";
        if (w.FindBody(fromId) is not Body from || w.FindBody(toId) is not Body to) return "Unknown planet";
        if (fromId == toId) return "Pick two different planets";
        if (from.Owner != side || from.PopK <= D.KeepAtSourceK) return $"{from.Name} has no people to spare";
        if (!Settleable(to)) return $"Nobody can live on {to.Name}";
        if (to.Owner is Side o && o != side) return $"{to.Name} is held by the rival";
        return null;
    }

    /// <summary>Colony fleets on a run load at the source, fly, unload at the destination (settling it if empty) and, if repeating, go back.</summary>
    private static void Runs(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.PeopleRun != null).ToList())
        {
            var run = f.PeopleRun!;
            var ships = w.ShipsOf(f).Where(IsColonyShip).ToList();
            var from = w.FindBody(run.FromBodyId);
            var to = w.FindBody(run.ToBodyId);
            if (ships.Count == 0 || from == null || to == null) { f.PeopleRun = null; continue; }
            if (to.Owner is Side o && o != f.Side)
            {
                f.PeopleRun = null;
                Autonomy.Report(sim, f, $"{f.Name} turns back: {to.Name} is now the rival's", NoteKind.Supply);
                continue;
            }
            var hold = ships.Sum(PeopleHold);
            var aboard = ships.Sum(s => s.CargoPeople);
            if (Economy.InOrbitAt(f, to) && aboard > 1e-6)
            {
                var room = Cap(to) - to.PopK;
                if (room <= 0.01)
                {
                    f.PeopleRun = null;
                    Autonomy.Report(sim, f, $"{f.Name} can't land its settlers: {to.Name} is full", NoteKind.Supply);
                    continue;
                }
                var amount = Math.Min(Math.Min(aboard, hold * SupplyLines.TransferPerHour), room);
                Unload(sim, f, to, ships, amount);
                if (ships.Sum(s => s.CargoPeople) > 1e-6) continue;
                if (run.Repeat && from.Owner == f.Side) { Fly(sim, f, from); continue; }
                f.PeopleRun = null;
                Autonomy.Report(sim, f, $"{f.Name} has landed its settlers on {to.Name}", NoteKind.Supply);
                continue;
            }
            if (Economy.InOrbitAt(f, from) && aboard < hold - 1e-6)
            {
                var spare = from.Owner == f.Side ? Math.Max(0, from.PopK - Math.Max(D.KeepAtSourceK, StaffNeeded(from))) : 0;
                var take = Math.Min(Math.Min(hold * SupplyLines.LoadPerHour * 4, hold - aboard), spare);
                if (take > 1e-6)
                {
                    from.PopK -= take;
                    foreach (var s in ships)
                    {
                        var put = Math.Min(take, PeopleHold(s) - s.CargoPeople);
                        s.CargoPeople += put;
                        take -= put;
                    }
                    continue;
                }
                if (aboard <= 1e-6) continue; // waiting for people to spare
            }
            if (f.Flight != null) continue;
            Fly(sim, f, ships.Sum(s => s.CargoPeople) > 1e-6 ? to : from);
        }
    }

    private static void Unload(Simulation sim, Fleet f, Body to, List<Ship> ships, double amount)
    {
        var w = sim.World;
        if (to.Owner == null)
        {
            if (w.Fleets.Any(o => o.Side != f.Side && Economy.InOrbitAt(o, to) && Combat.Armed(w, o))) return; // can't land under enemy guns
            to.Owner = f.Side;
            to.Happiness = to.PopK > 0 ? to.Happiness : D.HappinessStart;
            to.OwnShare = to.PopK > 0 ? to.OwnShare : 1;
            to.ClaimSide = null;
            to.ClaimHours = 0;
            to.FoodStock = 0;
            Economy.Of(w, f.Side).Totals.PlanetsClaimed++;
            sim.Log.Info("planets", $"{f.Side} settles {to.Name}", new { bodyId = to.Id, side = f.Side.ToString(), fleetId = f.Id });
        }
        var left = amount;
        foreach (var s in ships)
        {
            var off = Math.Min(left, s.CargoPeople);
            s.CargoPeople -= off;
            left -= off;
        }
        var landed = amount - left;
        // Settlers are the holder's own people: they raise the share who are loyal.
        to.OwnShare = (to.OwnShare * to.PopK + landed) / Math.Max(1e-9, to.PopK + landed);
        to.PopK += landed;
    }

    private static void Fly(Simulation sim, Fleet f, Body body)
    {
        var w = sim.World;
        if (Economy.InOrbitAt(f, body)) return;
        if (f.Flight is { TargetBodyId: int t } && t == body.Id) return;
        var plan = FlightPlanner.Plan(w, f, new FlightRequest(body.Id, body.Position, Economy.ReinforceSpeed, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            f.Chase = null;
        }
        else if (w.Tick % GameTime.TicksPerDay == 0)
            sim.Log.Info("planets", $"{f.Name} can't reach {body.Name}", new { fleetId = f.Id, bodyId = body.Id, plan.Problem });
    }

    /// <summary>Idle colony fleets fill up planets marked for Auto top-up from the planet with the most people to spare.</summary>
    private static void TopUps(Simulation sim)
    {
        var w = sim.World;
        if (w.Tick % 6 != 0) return;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var wanting = w.Bodies.Where(b => b.Owner == side && b.AutoTopUp && b.PopK < Cap(b) * 0.9
                                              && !w.Fleets.Any(f => f.PeopleRun?.ToBodyId == b.Id)).OrderBy(b => b.PopK / Math.Max(1e-9, Cap(b))).ThenBy(b => b.Id).ToList();
            if (wanting.Count == 0) continue;
            var idle = w.Fleets.Where(f => f.Side == side && f.PeopleRun == null && f.Flight == null && f.ShipIds.Count > 0
                                           && w.ShipsOf(f).All(IsColonyShip)).OrderBy(f => f.Id).ToList();
            foreach (var to in wanting)
            {
                if (idle.Count == 0) break;
                var source = w.Bodies.Where(b => b.Owner == side && b.Id != to.Id && b.PopK - Math.Max(D.KeepAtSourceK, StaffNeeded(b)) > 2 && !b.AutoTopUp)
                    .OrderByDescending(b => b.PopK - StaffNeeded(b)).ThenBy(b => b.Id).FirstOrDefault();
                if (source == null) break;
                var f = idle.OrderBy(x => Vec2.Distance(x.Position, source.Position)).ThenBy(x => x.Id).First();
                idle.Remove(f);
                f.PeopleRun = new PeopleRun { FromBodyId = source.Id, ToBodyId = to.Id, Auto = true };
                Autonomy.Report(sim, f, $"{f.Name} sets out to top up {to.Name} with people from {source.Name} (Auto top-up)", NoteKind.Supply);
            }
        }
    }

    /// <summary>Troop ships in orbit at one of your peopled planets take on soldiers from its people.</summary>
    private static void LoadTroops(World w)
    {
        foreach (var f in w.Fleets)
        {
            if (f.OrbitBodyId is not int id || w.FindBody(id) is not Body b || b.Owner != f.Side || !Economy.InOrbitAt(f, b)) continue;
            foreach (var s in w.ShipsOf(f).Where(IsTroopShip))
            {
                var spare = Math.Max(0, b.PopK - Math.Max(D.KeepAtSourceK, StaffNeeded(b)));
                var take = Math.Min(Math.Min(TroopHold(s) * SupplyLines.LoadPerHour * 4, TroopHold(s) - s.CargoTroops), spare);
                if (take <= 0) continue;
                s.CargoTroops += take;
                b.PopK -= take;
            }
        }
    }

    /// <summary>Why this fleet can't land troops on this body now, or null if it can.</summary>
    public static string? CanLand(World w, Fleet f, Body b)
    {
        if (TroopsAboard(w, f) <= 1e-6) return "No troops aboard";
        if (!Economy.InOrbitAt(f, b)) return $"Hold orbit at {b.Name} first";
        if (f.OrbitHeight != OrbitHeight.Low) return "Troops land from low orbit";
        if (b.Owner == f.Side) return "It is already yours";
        if (b.PopK <= 0 && b.Owner == null) return "Nobody there to fight: claim or settle it instead";
        if (w.Fleets.Any(o => o.Side != f.Side && Economy.InOrbitAt(o, b) && Combat.Armed(w, o))) return "Enemy warships hold orbit here";
        return null;
    }

    /// <summary>Why this fleet can't board a ship on the enemy's slipway here, or null if it can.</summary>
    public static string? CanBoard(World w, Fleet f, Body b)
    {
        if (!b.Shipyard || b.Owner is not Side owner || owner == f.Side) return "Only at an enemy shipyard";
        if (!Economy.InOrbitAt(f, b)) return $"Hold orbit at {b.Name} first";
        if (!Economy.Blockaded(w, b)) return "Blockade the shipyard first (armed ships in orbit)";
        if (TroopsAboard(w, f) < D.BoardTroopsK - 1e-9) return $"Needs {D.BoardTroopsK:0.#}k troops aboard";
        if (!w.Orders.Any(o => o.Side == owner && o.Kind == OrderKind.Ship && o.Paid)) return "No ship on the slipway";
        return null;
    }

    /// <summary>Takes troops off a fleet's troop ships (as many as there are, up to amount); returns how many.</summary>
    public static double TakeTroops(World w, Fleet f, double amount)
    {
        var left = amount;
        foreach (var s in w.ShipsOf(f).Where(IsTroopShip))
        {
            var off = Math.Min(left, s.CargoTroops);
            s.CargoTroops -= off;
            left -= off;
        }
        return amount - left;
    }

    /// <summary>Why this building can't be ordered here, or null if it can.</summary>
    public static string? CanBuild(World w, Side side, int bodyId, string kind)
    {
        if (!w.Loop) return "There is no economy in the battle sandbox";
        if (w.Winner != null) return "The game is over";
        if (BuildingKind(kind) is not BuildingData data) return $"Unknown building '{kind}'";
        if (w.FindBody(bodyId) is not Body b || b.Owner != side) return "Only on a planet you hold";
        if (b.PopK <= 0) return "Settle it first: buildings need people";
        if (b.Building != null) return $"Already building a {BuildingKind(b.Building)?.Name.ToLowerInvariant()}";
        if (b.Buildings.Count >= Plots(b)) return $"No room: {b.Name} has {Plots(b)} plots";
        if (kind == Farm && FarmFactor(b) <= 0) return "Nothing grows here";
        if (Economy.Of(w, side).Industry < data.Cost) return $"Needs {data.Cost:0} Industry";
        return null;
    }
}

// ---- Commands -------------------------------------------------------------------------------

/// <summary>Start a building on a planet you hold: paid now, standing after its build time.</summary>
public sealed record BuildOnPlanetCommand(Side Side, int BodyId, string Kind) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (Planets.CanBuild(world, Side, BodyId, Kind) is string problem)
        {
            sim.Log.Warn("planets", $"Can't build: {problem}", new { side = Side.ToString(), BodyId, Kind, Source });
            return;
        }
        var data = Planets.BuildingKind(Kind)!;
        var b = world.FindBody(BodyId)!;
        var state = Economy.Of(world, Side);
        state.Industry -= data.Cost;
        state.Totals.IndustrySpent += data.Cost;
        b.Building = Kind;
        b.BuildingHoursLeft = data.Days * 24;
        sim.Log.Info("planets", $"{Side} starts a {data.Name.ToLowerInvariant()} at {b.Name}", new { side = Side.ToString(), BodyId, Kind, Source });
    }
}

/// <summary>Take down a building (to free its plot and its people).</summary>
public sealed record DemolishCommand(Side Side, int BodyId, string Kind) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(BodyId) is not Body b || b.Owner != Side || !b.Buildings.Remove(Kind))
        {
            sim.Log.Warn("planets", "Bad demolish order", new { side = Side.ToString(), BodyId, Kind, Source });
            return;
        }
        sim.Log.Info("planets", $"{Side} takes down a {Kind} at {b.Name}", new { side = Side.ToString(), BodyId, Kind, Source });
    }
}

/// <summary>Send a colony fleet to carry people from one planet to another (Repeat: keep going back); null To ends the run.</summary>
public sealed record SendPeopleCommand(Side Side, int FleetId, int FromBodyId, int? ToBodyId, bool Repeat = false) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "People run") is not Fleet f) return;
        if (ToBodyId is not int to)
        {
            f.PeopleRun = null;
            sim.Log.Info("planets", $"{f.Name} ends its people run", new { FleetId, Source });
            return;
        }
        if (Planets.CanSendPeople(world, Side, f, FromBodyId, to) is string problem)
        {
            sim.Log.Warn("planets", $"Can't send people: {problem}", new { side = Side.ToString(), FleetId, FromBodyId, ToBodyId, Source });
            return;
        }
        f.PeopleRun = new PeopleRun { FromBodyId = FromBodyId, ToBodyId = to, Repeat = Repeat };
        f.SupplyRunFor = null;
        sim.Log.Info("planets", $"{f.Name} will carry people from {world.FindBody(FromBodyId)!.Name} to {world.FindBody(to)!.Name}",
            new { FleetId, FromBodyId, ToBodyId, Repeat, Source });
    }
}

/// <summary>Mark a planet for Auto top-up: idle colony ships keep bringing it people.</summary>
public sealed record SetAutoTopUpCommand(Side Side, int BodyId, bool On) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, Economy.ShipyardOf(world, Side)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(BodyId) is not Body b || b.Owner != Side || !Planets.Settleable(b))
        {
            sim.Log.Warn("planets", "Bad Auto top-up order", new { side = Side.ToString(), BodyId, On, Source });
            return;
        }
        b.AutoTopUp = On;
        sim.Log.Info("planets", $"Auto top-up {(On ? "on" : "off")} at {b.Name}", new { side = Side.ToString(), BodyId, On, Source });
    }
}

/// <summary>Hold low orbit (can bomb and land troops, but planet guns can reach it) or high orbit (out of their reach).</summary>
public sealed record SetOrbitHeightCommand(int FleetId, OrbitHeight Height) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Orbit height") is not Fleet f) return;
        f.OrbitHeight = Height;
        sim.Log.Info("fleet", $"{f.Name} will hold {Height.ToString().ToLowerInvariant()} orbit", new { FleetId, Height, Source });
    }
}

/// <summary>What to bomb from low orbit over an enemy planet (None: only blockade).</summary>
public sealed record SetBombingCommand(int FleetId, BombTarget Target) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Bombing order") is not Fleet f) return;
        f.Bombing = Target;
        sim.Log.Info("fleet", $"{f.Name} bombing: {Target}", new { FleetId, Target, Source });
    }
}

/// <summary>Land the fleet's troops on the planet it holds low orbit over.</summary>
public sealed record LandTroopsCommand(int FleetId, int BodyId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Landing") is not Fleet f || world.FindBody(BodyId) is not Body b) return;
        if (Planets.CanLand(world, f, b) is string problem)
        {
            sim.Log.Warn("planets", $"Can't land troops: {problem}", new { FleetId, BodyId, Source });
            return;
        }
        var troops = Planets.TakeTroops(world, f, double.MaxValue);
        if (b.LandingSide == f.Side) b.LandingTroops += troops;
        else
        {
            b.LandingSide = f.Side;
            b.LandingTroops = troops;
            b.Garrison = Planets.GarrisonOf(b);
        }
        sim.Log.Info("planets", $"{f.Side} lands troops on {b.Name}", new { FleetId, BodyId, troops = Math.Round(troops, 2), garrison = Math.Round(b.Garrison, 2), Source });
    }
}

/// <summary>Board the ship on the enemy's slipway and fly it away as your own.</summary>
public sealed record BoardSlipwayCommand(int FleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Boarding") is not Fleet f) return;
        if (f.OrbitBodyId is not int id || world.FindBody(id) is not Body b || Planets.CanBoard(world, f, b) is string problem)
        {
            sim.Log.Warn("planets", "Can't board the slipway", new { FleetId, Source });
            return;
        }
        var owner = b.Owner!.Value;
        var order = world.Orders.First(o => o.Side == owner && o.Kind == OrderKind.Ship && o.Paid);
        world.Orders.Remove(order);
        Planets.TakeTroops(world, f, Planets.D.BoardTroopsK);
        var ship = Economy.NewShip(world, f.Side, order.Key);
        ship.Armour = Math.Max(1, ship.Armour * (1 - order.HoursLeft / Math.Max(1, order.TotalHours)) * 0.5 + ship.Armour * 0.25);
        ship.FleetId = f.Id;
        f.ShipIds.Add(ship.Id);
        Fleets.Arrange(world, f);
        ship.Position = f.Position + ship.FormationOffset;
        Economy.Of(world, f.Side).Totals.ShipsKilled++;
        Economy.Of(world, owner).Totals.ShipsLost++;
        sim.Log.Info("planets", $"{f.Side} boards and steals {ship.Name} from the slipway at {b.Name}", new { FleetId, bodyId = b.Id, classId = order.Key, Source });
    }
}
