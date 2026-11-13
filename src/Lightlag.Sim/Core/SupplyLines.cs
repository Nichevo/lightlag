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
    public static double MaxFood(Ship s) => Crews.Crewed(s) ? Fitting.Spec(s).FoodDays : 0;

    /// <summary>A hauler that carries goods between planets (freighters of every kind, sail freighters too).</summary>
    public static bool IsFreighter(Ship s) => s.ClassId is "freighter" or "bulk-freighter" or "sail-freighter";

    /// <summary>
    /// The freighter to build next: bulk once researched, else the plain one; once sails are researched every
    /// other one is a sail freighter (cheap and fuel-free, but slow and in need of light).
    /// </summary>
    public static string HaulerClass(World w, Side side)
    {
        var normal = Economy.CanQueue(w, side, OrderKind.Ship, "bulk-freighter") == null ? "bulk-freighter" : "freighter";
        if (Economy.CanQueue(w, side, OrderKind.Ship, "sail-freighter") != null) return normal;
        var classes = w.Ships.Where(s => s.Side == side).Select(s => s.ClassId)
            .Concat(w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Ship).Select(o => o.Key)).ToList();
        var sails = classes.Count(c => c == "sail-freighter");
        var others = classes.Count(c => c is "freighter" or "bulk-freighter");
        return sails < others ? "sail-freighter" : normal;
    }

    public static bool IsSupplyShip(Ship s) => Fitting.Spec(s) is { Cargo: > 0, CargoKind: "fuel" or "food" or "mixed" };

    /// <summary>
    /// The ship's hold for each kind of cargo: tankers carry fuel (80 %) and ammunition (20 %), freighters
    /// food, and the small supply tender a little of each (fuel 50 %, ammunition 20 %, food 30 %).
    /// </summary>
    public static (double Fuel, double Ammo, double Food) Hold(Ship s)
    {
        var spec = Fitting.Spec(s);
        return spec.CargoKind switch
        {
            "fuel" => (spec.Cargo * 0.8, spec.Cargo * 0.2, 0),
            "food" => (0, 0, spec.Cargo),
            "mixed" => (spec.Cargo * 0.5, spec.Cargo * 0.2, spec.Cargo * 0.3),
            _ => (0, 0, 0),
        };
    }

    public static void FillCargo(Ship s)
    {
        var (fuel, ammo, food) = Hold(s);
        s.CargoFuels.Clear();
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
    public static void Restock(World w, Ship s, double hours, Body? at = null)
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
            Ledger.Out(w, at.Owner, "ammunition", have - Colonies.Held(at, "ammunition"), "ship ammo");
            // Topped up from the planet's store: slugs (and the hold) are what the side's plants make now.
            if (w.Loop && at.Owner == s.Side && have > 0)
            {
                var (missile, rail) = Munitions.Made(w, s.Side);
                if (MaxAmmo(s) - s.Ammo > 1e-9) s.RailGrade = rail;
                if (ammo - s.CargoAmmo > 1e-9) (s.CargoMissileGrade, s.CargoRailGrade) = (missile, rail);
            }
        }
        s.Ammo = Math.Min(MaxAmmo(s), s.Ammo + MaxAmmo(s) * RestockPerHour * hours * ammoRate);
        s.Food = Math.Min(MaxFood(s), s.Food + MaxFood(s) * RestockPerHour * hours);
        // Fuel comes out of the planet's store, each fuel the fleet it supplies burns (see Fuels).
        Fuels.Load(w, s, Math.Min(fuel - s.CargoFuel, fuel * LoadPerHour * hours), at);
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
            foreach (var s in suppliers) RefuelSelf(w, s);
            if (suppliers.Count > 0 && f.ShipIds.Count > suppliers.Count) Deliver(w, suppliers, w.ShipsOf(f).Where(s => !IsSupplyShip(s)).ToList());
        }
        if (w.Loop) Runs(sim);
    }

    /// <summary>
    /// A supply ship carrying the fuel its own engines burn tops its own tanks up from its hold once they fall below
    /// half, to SelfFuelTo: enough to carry on and get home, leaving the rest of the cargo for the fleet.
    /// </summary>
    public static void RefuelSelf(World w, Ship s)
    {
        var max = Fitting.MaxSupplies(w, s);
        if (s.Supplies >= max * 0.5) return;
        s.Supplies += Fuels.TakeCargo(s, Fuels.IdOf(s), max * SelfFuelTo - s.Supplies);
    }

    /// <summary>Share of its own tanks a supply ship refuels to from its cargo.</summary>
    public const double SelfFuelTo = 0.6;

    /// <summary>A supply run whose own tanks fall to this share (and can't refill from its hold) goes home to refuel.</summary>
    public const double RefuelBelow = 0.25;

    /// <summary>Supply ships pass cargo to ships short of fuel, ammunition, missiles or food.</summary>
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
            foreach (var s in to.OrderBy(s => s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w, s))).ThenBy(s => s.Id))
            {
                // Each ship takes only the fuel it burns; a ship nearly dry may switch to another fuel its engine takes.
                if (Fuels.On(w) && Fuels.CargoOf(src, Fuels.IdOf(s)) <= 1e-9 && s.Supplies < Fitting.MaxSupplies(w, s) * 0.05
                    && Fuels.Burns(w, s).FirstOrDefault(x => Fuels.CargoOf(src, x) > 1e-9) is string other)
                    Fuels.Switch(w, s, other, null);
                var take = Math.Min(Math.Min(fuel, Fuels.CargoOf(src, Fuels.IdOf(s))), Fitting.MaxSupplies(w, s) - s.Supplies);
                if (take > 0) { s.Supplies += take; Fuels.TakeCargo(src, Fuels.IdOf(s), take); fuel -= take; }
                take = Math.Min(ammo, MaxAmmo(s) - s.Ammo);
                if (take > 0)
                {
                    // Mixed magazines fire as the worse of the two; an empty one takes what comes.
                    s.RailGrade = s.Ammo < 1 ? src.CargoRailGrade : Math.Min(s.RailGrade, src.CargoRailGrade);
                    s.Ammo += take; src.CargoAmmo -= take; ammo -= take;
                }
                // Missiles: one reload an hour per ship, each taking one unit of ammunition from the hold.
                if (s.Missiles < Tech.Racks(w, s) && src.CargoAmmo >= 1)
                {
                    s.MissileGrade = s.Missiles == 0 ? src.CargoMissileGrade : Math.Min(s.MissileGrade, src.CargoMissileGrade);
                    s.Missiles++; src.CargoAmmo -= 1;
                    ammo = Math.Min(ammo, src.CargoAmmo); // the next ship can't take ammunition the reload used
                }
            }
            // Food goes round: the hungriest first, each taking no more than an even share of what is left this hour.
            var eaters = to.Where(s => MaxFood(s) - s.Food > 1e-9).OrderBy(s => s.Food / MaxFood(s)).ThenBy(s => s.Id).ToList();
            for (var i = 0; i < eaters.Count && food > 1e-9; i++)
            {
                var s = eaters[i];
                var take = Math.Min(food / (eaters.Count - i), MaxFood(s) - s.Food);
                s.Food += take; src.CargoFood -= take; food -= take;
            }
        }
    }

    /// <summary>Do the ships of this fleet still need anything a supply ship carries?</summary>
    public static bool Needs(World w, Fleet f) => w.ShipsOf(f).Any(s => !IsSupplyShip(s)
        && (s.Supplies < Fitting.MaxSupplies(w, s) * 0.95 || s.Ammo < MaxAmmo(s) * 0.95 || s.Food < MaxFood(s) * 0.95 || s.Missiles < Tech.Racks(w, s)));

    /// <summary>Running short enough to send a tanker for: some ship below half its fuel, ammunition or food.</summary>
    public static bool Short(World w, Fleet f) => w.ShipsOf(f).Any(s => !IsSupplyShip(s)
        && (s.Supplies < Fitting.MaxSupplies(w, s) * 0.5 || s.Ammo < MaxAmmo(s) * 0.5 || s.Food < MaxFood(s) * 0.5));

    private static bool Together(World w, Fleet a, Fleet b) =>
        a.Hold == HoldStyle.Orbit && b.Hold == HoldStyle.Orbit && a.OrbitBodyId != null && a.OrbitBodyId == b.OrbitBodyId
        || Vec2.Distance(a.Position, b.Position) <= TogetherMm && (a.Flight is not { Started: true } || Tech.Has(w, a.Side, "underway-replenishment"));

    /// <summary>Supply ships hand cargo over to a fleet within this distance (or in orbit at the same body).</summary>
    public const double TogetherMm = 50;

    /// <summary>
    /// Did the supply fleet come within TogetherMm of its fleet during the last hour? Checked along the straight
    /// relative track, since a fast tanker can pass through the handover distance between two hourly looks.
    /// </summary>
    public static bool PassedClose(Fleet f, Fleet target)
    {
        var rel = f.Position - target.Position;
        var rv = f.Velocity - target.Velocity;
        var before = rel - rv * GameTime.HoursPerTick;
        var seg = rel - before;
        var len2 = seg.X * seg.X + seg.Y * seg.Y;
        var t = len2 < 1e-12 ? 1 : Math.Clamp(-(before.X * seg.X + before.Y * seg.Y) / len2, 0, 1);
        return (before + seg * t).Length <= TogetherMm;
    }

    /// <summary>
    /// Close to a planet or moon (taking up or leaving orbit): stopping alongside there could put the tanker inside the
    /// body, so the usual flight to the planet's orbit does the meeting instead.
    /// </summary>
    private static bool NearBody(World w, Fleet target) =>
        w.Bodies.Any(b => Vec2.Distance(target.Position, b.Position) < Fleets.OrbitRadiusFor(b) * 3 + TogetherMm);

    /// <summary>Ends the supply fleet's flight beside its fleet, matching its speed (paid in fuel).</summary>
    private static void ComeAlongside(Simulation sim, Fleet f, Fleet target)
    {
        var w = sim.World;
        sim.BurnFuel(f, (f.Velocity - target.Velocity).Length);
        f.Flight = null;
        f.Chase = null;
        Fleets.EnterDrift(f);
        f.Position = target.Position + (f.Position - target.Position) * Math.Min(1, TogetherMm * 0.5 / Math.Max(1e-9, Vec2.Distance(f.Position, target.Position)));
        f.Velocity = target.Velocity;
        foreach (var s in w.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        sim.Log.Info("supply", $"{f.Name} comes alongside {target.Name}", new { fleetId = f.Id, target = target.Id });
    }

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
                f.SupplyRunByRole = false;
                Autonomy.Report(sim, f, $"{f.Name} ends its supply run: the fleet it supplied is gone", NoteKind.Supply);
                continue;
            }
            var cargo = CargoShare(w, f);
            var filling = cargo > f.SupplyRunLastCargo + 1e-6;
            f.SupplyRunLastCargo = cargo;
            var home = f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int b && w.FindBody(b)?.Owner == f.Side;
            // A planned meeting with nothing to fly (it is already where the fleet will stop): wait here, not "on its way".
            if (f.Flight is { Started: false, Legs.Count: 0 }) f.Flight = null;
            // Out to its fleet and passing close by it: stop alongside (match its speed) rather than fly on past.
            var outbound = f.Flight is { } way && (way.TargetFleetId == target.Id || f.Chase?.TargetFleetId == target.Id);
            if ((outbound || f.Flight == null && !home) && target.Hold != HoldStyle.Orbit && !f.Maneuvering
                && !NearBody(w, target) && !Together(w, f, target) && PassedClose(f, target))
                ComeAlongside(sim, f, target);
            if (Together(w, f, target))
            {
                // Drifting together: keep matching its speed so the two stay side by side while cargo goes over.
                if (f.Flight == null && f.Hold != HoldStyle.Orbit && !NearBody(w, target) && (f.Velocity - target.Velocity).Length > 1e-6)
                {
                    sim.BurnFuel(f, (f.Velocity - target.Velocity).Length);
                    f.Velocity = target.Velocity;
                }
                var before = CargoShare(w, f);
                Deliver(w, w.ShipsOf(f).Where(IsSupplyShip).ToList(), w.ShipsOf(target).Where(s => !IsSupplyShip(s)).ToList());
                // Empty, or carrying nothing the fleet still wants (say food is gone but ammunition is left): reload.
                var after = CargoShare(w, f);
                if (!home && (after < ReloadBelow || after >= before - 1e-9 && Short(w, target))) GoLoad(sim, f, target);
                continue;
            }
            // Low on its own fuel (a freighter carries none for itself): go home to refuel before it is stranded.
            if (!home && !f.Resupplying && Combat.SupplyShare(w, f) < RefuelBelow 
                && (f.Flight is not { TargetBodyId: int tb } || w.FindBody(tb)?.Owner != f.Side))
            {
                if (GoLoad(sim, f, target, refuel: true)) continue;
            }
            if (f.Flight is { } fl && (fl.TargetFleetId == target.Id || f.Chase != null)) continue; // on its way out
            // Loading and refuelling. It sets out once full, or once the hold stops filling (the planet is out of
            // something), or, for a fleet already running short, once the hold is nearly half full.
            var loaded = cargo >= 0.95 || cargo >= ReloadBelow * 2 && (!filling || Short(w, target) && cargo >= 0.45);
            if (home && (!loaded || Combat.SupplyShare(w, f) < Supply.RefilledAt)) continue;
            if (f.Flight != null && !home) continue;                                                // on its way home
            if (cargo < ReloadBelow) { GoLoad(sim, f, target); continue; }
            if (!Needs(w, target) && home) continue;                                                // nothing wanted yet
            f.SupplyRunProblem = null;
            if (Fleets.FlyToJoin(w, f, target, Economy.ReinforceSpeed) is string problem)
            {
                f.SupplyRunProblem = problem;
                if (w.Tick % (GameTime.TicksPerDay) == 0)
                    sim.Log.Info("supply", $"{f.Name} can't reach {target.Name} yet", new { fleetId = f.Id, target = target.Id, problem });
            }
            else if (f.Flight is { Legs.Count: 0 })
            {
                // Already where the fleet will stop: wait there for it.
                f.Flight = null;
                f.Chase = null;
            }
            else
            {
                if (f.Flight != null) f.Flight.TargetFleetId ??= target.Id;
                Autonomy.Report(sim, f, $"{f.Name} sets out with supplies for {target.Name}", NoteKind.Supply);
            }
        }
    }

    /// <summary>
    /// What a fleet's supplies are doing, for its panel and the fleets list: the tanker on a run for it and its
    /// stage (loading, on its way, alongside, can't reach), or a Send waiting in Needs you. Null: nothing planned.
    /// </summary>
    public static string? RunStatus(World w, Fleet target)
    {
        var f = w.Fleets.Where(x => x.SupplyRunFor == target.Id && x.Side == target.Side).OrderBy(x => x.Id).FirstOrDefault();
        if (f == null)
            return Economy.Of(w, target.Side).Needs.Any(n => n.FleetId == target.Id && n.Act is SetSupplyRunCommand)
                ? "No tanker yet: Send is waiting in Needs you" : null;
        var name = $"Tanker {f.Name}";
        if (Together(w, f, target)) return $"{name}: alongside, topping up";
        if (f.SupplyRunProblem != null && f.Flight == null) return $"{name} can't reach it: {f.SupplyRunProblem}";
        if (f.Flight is { } fl && (fl.TargetFleetId == target.Id || f.Chase != null))
            return $"{name}: on its way, arrives in {Math.Max(0, fl.ArriveHour - w.Hour) / 24:0.#} d";
        var at = f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int b ? w.FindBody(b) : null;
        if (at?.Owner == f.Side && f.Flight == null)
            return CargoShare(w, f) < 0.95 ? $"{name}: loading at {at.Name} ({CargoShare(w, f):P0})" : $"{name}: loaded at {at.Name}, sets out when needed";
        if (f.Resupplying && f.Flight != null) return $"{name}: low on fuel, going to refuel";
        if (f.Flight != null) return $"{name}: going to load";
        return $"{name}: waiting where {target.Name} will stop";
    }

    private static bool GoLoad(Simulation sim, Fleet f, Fleet target, bool refuel = false)
    {
        var w = sim.World;
        if (Supply.Nearest(w, f) is not (Body body, _)) return false;
        var plan = FlightPlanner.Plan(w, f, new FlightRequest(body.Id, body.Position, Economy.ReinforceSpeed, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        // Short of fuel for the usual pace: a slow coast home.
        if (plan is not { Ok: true } || plan.DeltaV > FleetPerformance.Of(w, f).DeltaVLeft)
            plan = FlightPlanner.Plan(w, f, new FlightRequest(body.Id, body.Position, FlightPlanner.MinSpeedFraction, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            f.Chase = null;
            if (refuel) f.Resupplying = true;
            Autonomy.Report(sim, f, refuel
                ? $"{f.Name} is low on fuel: heads to {body.Name} to refuel and load more supplies for {target.Name}"
                : $"{f.Name} heads to {body.Name} to load more supplies for {target.Name}", NoteKind.Supply);
            sim.Log.Info("supply", refuel ? "Supply run goes home to refuel" : "Supply run goes home to load", new { fleetId = f.Id, target = target.Id, bodyId = body.Id });
            return true;
        }
        return false;
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
         + w.Stations.Where(st => st.Side == side).Sum(st => Economy.StationKind(st.Kind)?.Cost ?? 0)
         + w.Starbases.Where(st => st.Side == side).Sum(st => Starbases.ClassOf(st).Cost))
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
            if (f != null && f.Side == Side)
            {
                f.Refused = !world.ShipsOf(f).Any(SupplyLines.IsSupplyShip) ? "no tanker or supply ship in this fleet" : "that fleet can't be supplied";
                f.RefusedHour = world.Hour;
            }
            return;
        }
        f.SupplyRunFor = TargetFleetId;
        f.SupplyRunByRole = false;
        f.AutoRun = TargetFleetId != null && Source == "auto";
        sim.Log.Info("supply", TargetFleetId == null ? $"{f.Name} ends its supply run" : $"{f.Name} starts a supply run", new { FleetId, TargetFleetId, Source });
    }
}
