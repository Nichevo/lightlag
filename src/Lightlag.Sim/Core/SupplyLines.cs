using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// Supply lines. Ships carry three stocks: fuel (Ship.Supplies, used by burns), railgun ammunition
/// (Ammo) and food (Food, days for the crew). Any planet the side holds restocks all three for a fleet in
/// orbit. Away from home, crews eat a day's food a day; a hungry crew (no food) aims worse, reloads
/// slower and dodges less. Tankers carry fuel and ammunition, freighters food, as cargo: supply ships
/// in a fleet top up the rest of it every hour, and a supply fleet on an Auto supply run flies out to
/// its fleet (Join-style intercepts), tops it up while they are together, goes home to load again when
/// its cargo runs low, and repeats. Every ship and station costs upkeep: a share of its cost in
/// Industry a day.
/// </summary>
public static class SupplyLines
{
    /// <summary>Ammunition per railgun (supply units; a slug uses the railgun's suppliesPerShot).</summary>
    public const double AmmoPerRailgun = 8;

    /// <summary>Share of a full load restocked per hour in orbit at a held planet (food and ammunition).</summary>
    public const double RestockPerHour = 0.1 / 24 * 4;

    /// <summary>Cargo a supply ship loads per hour at a held planet, as a share of its hold.</summary>
    public const double LoadPerHour = 1.0 / 24;

    /// <summary>Cargo passed per hour to the ships it supplies, as a share of the supply ship's hold.</summary>
    public const double TransferPerHour = 0.25;

    /// <summary>A supply run goes home to load again below this share of cargo.</summary>
    public const double ReloadBelow = 0.15;

    /// <summary>Hungry crews: hit chances, reload times and dodges are scaled by these.</summary>
    public const double HungryAccuracy = 0.8, HungryReload = 1.25, HungryEvasion = 0.8;

    /// <summary>Upkeep per day as a share of each ship's and station's cost.</summary>
    public const double UpkeepShare = 0.004;

    public static double MaxAmmo(Ship s) => Fitting.Spec(s).Railguns * AmmoPerRailgun;
    public static double MaxFood(Ship s) => Fitting.Spec(s).FoodDays;

    public static bool IsSupplyShip(Ship s) => Fitting.Spec(s) is { Cargo: > 0, CargoKind: "fuel" or "food" };

    /// <summary>The ship's hold for each kind of cargo: tankers carry fuel (80 %) and ammunition (20 %), freighters food.</summary>
    public static (double Fuel, double Ammo, double Food) Hold(Ship s)
    {
        var spec = Fitting.Spec(s);
        return spec.CargoKind switch
        {
            "fuel" => (spec.Cargo * 0.8, spec.Cargo * 0.2, 0),
            "food" => (0, 0, spec.Cargo),
            _ => (0, 0, 0),
        };
    }

    public static void FillCargo(Ship s)
    {
        var (fuel, ammo, food) = Hold(s);
        s.CargoFuel = fuel;
        s.CargoAmmo = ammo;
        s.CargoFood = food;
    }

    /// <summary>How full the ship's hold is (1 for a ship with no hold).</summary>
    public static double CargoShare(Ship s)
    {
        var (fuel, ammo, food) = Hold(s);
        var total = fuel + ammo + food;
        return total <= 0 ? 1 : (s.CargoFuel + s.CargoAmmo + s.CargoFood) / total;
    }

    public static double CargoShare(World w, Fleet f)
    {
        var ships = w.ShipsOf(f).Where(IsSupplyShip).ToList();
        return ships.Count == 0 ? 0 : ships.Average(CargoShare);
    }

    public static bool Hungry(Ship s) => s.Food <= 0 && MaxFood(s) > 0;

    /// <summary>Railgun ammunition (supply units) one unit of the ammunition good restocks.</summary>
    public const double AmmoPerGood = 4;

    /// <summary>
    /// A held planet restocks food and ammunition (fuel is refilled by Simulation.Refill), and loads supply
    /// ships. Ammunition comes out of the planet's store of the ammunition good; with none it comes slowly
    /// (a third of the pace) from local stores. A planet with no store at all (an outpost) restocks freely.
    /// </summary>
    public static void Restock(Ship s, double hours, Body? at = null)
    {
        var (fuel, ammo, food) = Hold(s);
        var ammoWant = Math.Min(MaxAmmo(s) - s.Ammo, MaxAmmo(s) * RestockPerHour * hours)
                       + Math.Min(ammo - s.CargoAmmo, ammo * LoadPerHour * hours);
        var ammoRate = 1.0;
        if (at != null && ammoWant > 1e-9 && (at.Board != null || at.Stock.Count > 0))
        {
            var goods = ammoWant / AmmoPerGood;
            var have = Colonies.Held(at, "ammunition");
            ammoRate = have >= goods ? 1 : Math.Max(1.0 / 3, have / goods);
            at.Stock["ammunition"] = Math.Max(0, have - goods * ammoRate);
        }
        s.Ammo = Math.Min(MaxAmmo(s), s.Ammo + MaxAmmo(s) * RestockPerHour * hours * ammoRate);
        s.Food = Math.Min(MaxFood(s), s.Food + MaxFood(s) * RestockPerHour * hours);
        s.CargoFuel = Math.Min(fuel, s.CargoFuel + fuel * LoadPerHour * hours);
        s.CargoAmmo = Math.Min(ammo, s.CargoAmmo + ammo * LoadPerHour * hours * ammoRate);
        s.CargoFood = Math.Min(food, s.CargoFood + food * LoadPerHour * hours);
    }

    /// <summary>Hourly: crews eat, supply ships top up their fleets, and supply runs move on.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Rules.SupplyLines)
        {
            // Supply lines switched off: ships keep full ammunition and food wherever they are.
            foreach (var s in w.Ships) { s.Ammo = MaxAmmo(s); s.Food = MaxFood(s); }
        }
        foreach (var f in w.Fleets.ToList())
        {
            var home = f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int b && w.FindBody(b)?.Owner == f.Side;
            foreach (var s in w.ShipsOf(f))
            {
                if (home || MaxFood(s) <= 0 || !w.Rules.SupplyLines) continue;
                var before = s.Food;
                s.Food = Math.Max(0, s.Food - GameTime.HoursPerTick / 24 * Tech.Factor(w, f.Side, Tech.FoodRecyclers) * Tech.Factor(w, f.Side, "closed-loop"));
                if (before > 0 && s.Food <= 0)
                    Autonomy.Report(sim, f, $"{s.Name} has run out of food: the crew is hungry (worse aim, slower reloads)", NoteKind.Supply);
            }
            var suppliers = w.ShipsOf(f).Where(IsSupplyShip).ToList();
            if (suppliers.Count > 0 && f.ShipIds.Count > suppliers.Count) Deliver(w, suppliers, w.ShipsOf(f).Where(s => !IsSupplyShip(s)).ToList());
        }
        if (w.Loop) Runs(sim);
    }

    /// <summary>Supply ships pass cargo to ships short of fuel, ammunition or food.</summary>
    private static void Deliver(World w, List<Ship> from, List<Ship> to)
    {
        foreach (var src in from)
        {
            var (hf, ha, hfood) = Hold(src);
            // Underway replenishment: hoses rigged for transfer on the move pass cargo twice as fast.
            var rate = TransferPerHour * (w.Loop ? Tech.Factor(w, src.Side, "underway-replenishment") : 1);
            var fuel = Math.Min(src.CargoFuel, hf * rate);
            var ammo = Math.Min(src.CargoAmmo, ha * rate);
            var food = Math.Min(src.CargoFood, hfood * rate);
            foreach (var s in to.OrderBy(s => s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w, s))))
            {
                var take = Math.Min(fuel, Fitting.MaxSupplies(w, s) - s.Supplies);
                if (take > 0) { s.Supplies += take; src.CargoFuel -= take; fuel -= take; }
                take = Math.Min(ammo, MaxAmmo(s) - s.Ammo);
                if (take > 0) { s.Ammo += take; src.CargoAmmo -= take; ammo -= take; }
                take = Math.Min(food, MaxFood(s) - s.Food);
                if (take > 0) { s.Food += take; src.CargoFood -= take; food -= take; }
            }
        }
    }

    /// <summary>Do the ships of this fleet still need anything a supply ship carries?</summary>
    private static bool Needs(World w, Fleet f) => w.ShipsOf(f).Any(s => !IsSupplyShip(s)
        && (s.Supplies < Fitting.MaxSupplies(w, s) * 0.95 || s.Ammo < MaxAmmo(s) * 0.95 || s.Food < MaxFood(s) * 0.95));

    private static bool Together(World w, Fleet a, Fleet b) =>
        a.Hold == HoldStyle.Orbit && b.Hold == HoldStyle.Orbit && a.OrbitBodyId != null && a.OrbitBodyId == b.OrbitBodyId
        || Vec2.Distance(a.Position, b.Position) <= TogetherMm && (a.Flight is not { Started: true } || Tech.Has(w, a.Side, "underway-replenishment"));

    /// <summary>Supply ships hand cargo over to a fleet within this distance (or in orbit at the same body).</summary>
    public const double TogetherMm = 50;

    /// <summary>Auto supply runs: load at home, fly out, top up while together, go home when low.</summary>
    private static void Runs(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.SupplyRunFor != null).ToList())
        {
            var target = w.FindFleet(f.SupplyRunFor!.Value);
            if (target == null || target.Side != f.Side || !w.ShipsOf(f).Any(IsSupplyShip))
            {
                f.SupplyRunFor = null;
                Autonomy.Report(sim, f, $"{f.Name} ends its supply run: the fleet it supplied is gone", NoteKind.Supply);
                continue;
            }
            var cargo = CargoShare(w, f);
            var home = f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int b && w.FindBody(b)?.Owner == f.Side;
            if (Together(w, f, target))
            {
                Deliver(w, w.ShipsOf(f).Where(IsSupplyShip).ToList(), w.ShipsOf(target).Where(s => !IsSupplyShip(s)).ToList());
                if (CargoShare(w, f) < ReloadBelow) GoLoad(sim, f, target);
                continue;
            }
            if (f.Flight is { } fl && (fl.TargetFleetId == target.Id || f.Chase != null)) continue; // on its way out
            if (home && cargo < 0.95) continue;                                                     // loading
            if (f.Flight != null && !home) continue;                                                // on its way home
            if (cargo < ReloadBelow) { GoLoad(sim, f, target); continue; }
            if (!Needs(w, target) && home) continue;                                                // nothing wanted yet
            if (Fleets.FlyToJoin(w, f, target, Economy.ReinforceSpeed) is string problem)
            {
                if (w.Tick % (GameTime.TicksPerDay) == 0)
                    sim.Log.Info("supply", $"{f.Name} can't reach {target.Name} yet", new { fleetId = f.Id, target = target.Id, problem });
            }
            else
            {
                if (f.Flight != null) f.Flight.TargetFleetId ??= target.Id;
                Autonomy.Report(sim, f, $"{f.Name} sets out with supplies for {target.Name}", NoteKind.Supply);
            }
        }
    }

    private static void GoLoad(Simulation sim, Fleet f, Fleet target)
    {
        var w = sim.World;
        if (Supply.Nearest(w, f) is not (Body body, _)) return;
        var plan = FlightPlanner.Plan(w, f, new FlightRequest(body.Id, body.Position, Economy.ReinforceSpeed, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            f.Chase = null;
            Autonomy.Report(sim, f, $"{f.Name} heads to {body.Name} to load more supplies for {target.Name}", NoteKind.Supply);
        }
    }

    /// <summary>Hourly upkeep: a share of the cost of every ship and station, paid in Industry (it can't go below 0).</summary>
    public static void Upkeep(Simulation sim, Side side)
    {
        var w = sim.World;
        var perDay = UpkeepPerDay(w, side);
        var state = Economy.Of(w, side);
        var pay = Math.Min(state.Industry, perDay / 24);
        state.Industry -= pay;
        state.Totals.IndustrySpent += pay;
        if (pay < perDay / 24 - 1e-9 && w.Tick % GameTime.TicksPerDay == 0)
            sim.Log.Info("supply", $"{side} can't pay full upkeep", new { side = side.ToString(), perDay });
    }

    public static double UpkeepPerDay(World w, Side side) =>
        (w.Ships.Where(s => s.Side == side).Sum(s => GameData.ShipClass(s.ClassId).Cost)
         + w.Stations.Where(st => st.Side == side).Sum(st => Economy.StationKind(st.Kind)?.Cost ?? 0))
        * UpkeepShare * Tech.Factor(w, side, Tech.EfficientUpkeep);
}

/// <summary>Send a supply fleet on an Auto supply run for another fleet (null: end the run).</summary>
public sealed record SetSupplyRunCommand(Side Side, int FleetId, int? TargetFleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        var f = world.FindFleet(FleetId);
        if (f == null || f.Side != Side || !world.ShipsOf(f).Any(SupplyLines.IsSupplyShip)
            || TargetFleetId is int t && (t == FleetId || world.FindFleet(t)?.Side != Side))
        {
            sim.Log.Warn("supply", "Bad supply run order", new { side = Side.ToString(), FleetId, TargetFleetId, Source });
            return;
        }
        f.SupplyRunFor = TargetFleetId;
        f.AutoRun = TargetFleetId != null && Source == "auto";
        sim.Log.Info("supply", TargetFleetId == null ? $"{f.Name} ends its supply run" : $"{f.Name} starts a supply run", new { FleetId, TargetFleetId, Source });
    }
}
