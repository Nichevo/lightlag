using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

public enum RefitStage { Flying, Waiting, Refitting }

/// <summary>A fleet sent for refit: the yard, where it goes back to afterwards, and how it is getting on.</summary>
public sealed class RefitPlan
{
    public int YardBodyId { get; set; }
    public RefitStage Stage { get; set; }
    public int? ReturnBodyId { get; set; }
    public Vec2? ReturnPoint { get; set; }
    public double StartHour { get; set; }
    public string? Waiting { get; set; }
}

/// <summary>
/// Refits: a fleet sent for refit flies to the nearest yard (the shipyard or a dry dock), and each ship not
/// on its class's current design is refitted to it in a bay, for Industry and materials (a share of the
/// class's cost); then the fleet flies back on station. Yards have a few bays: ships being built and
/// ships being repaired come first, refits take the bays left. No refits in battle. The Refits automation
/// area sends idle and home-guard fleets when a bay is free and the refit is worth it.
/// </summary>
public static class Refits
{
    public const double HoursPerShip = 48;
    public const double IndustryShare = 0.25;
    public const double MaterialShare = 0.3;

    /// <summary>The fit a ship of this class gets now (the saved design, with the best engines).</summary>
    public static List<string> Target(World w, Ship s) => Fitting.AutoFit(w, s.Side, GameData.ShipClass(s.ClassId));

    public static bool Outdated(World w, Ship s) => !Fitting.FitOf(s).SequenceEqual(Target(w, s));

    public static double HoursFor(World w, Ship s, Body yard) =>
        HoursPerShip * Math.Clamp(GameData.ShipClass(s.ClassId).Cost / 100, 0.5, 3) / Math.Max(0.1, Colonies.ShipyardRate(w, yard));

    public static double CostOf(Ship s) => Math.Round(GameData.ShipClass(s.ClassId).Cost * IndustryShare);

    public static Dictionary<string, double> MaterialsOf(Ship s) =>
        (GameData.ShipClass(s.ClassId).Materials ?? new()).ToDictionary(m => m.Key, m => Math.Ceiling(m.Value * MaterialShare));

    /// <summary>Bays at a yard: the shipyard has two (three with Fast shipyard), each dry dock one.</summary>
    public static int Bays(World w, Body b, Side side)
    {
        if (b.Owner != side) return 0;
        var bays = Colonies.Built(b).Count(p => p.Kind == Colonies.DryDock && p.Powered);
        if (b.Shipyard) bays += 2 + (Tech.Has(w, side, Tech.FastShipyard) ? 1 : 0);
        return bays;
    }

    /// <summary>Bays in use: a ship being built here, ships being repaired here, ships in a refit here.</summary>
    public static int BaysBusy(World w, Body b, Side side)
    {
        var building = w.Orders.Any(o => o.Side == side && o.Kind == OrderKind.Ship && o.Paid
                                        && (o.YardBodyId == b.Id || o.YardBodyId == null && b.Shipyard)) ? 1 : 0;
        var here = w.Fleets.Where(f => f.Side == side && Economy.InOrbitAt(f, b)).SelectMany(f => w.ShipsOf(f)).ToList();
        return building + here.Count(s => s.RefitHoursLeft > 0) + here.Count(s => s.RefitHoursLeft <= 0 && (s.Crippled || Wounds.Worst(w, s) < 0.95));
    }

    public static int FreeBays(World w, Body b, Side side) => Math.Max(0, Bays(w, b, side) - BaysBusy(w, b, side));

    /// <summary>The nearest yard with bays (shipyard or dry dock).</summary>
    public static Body? NearestYard(World w, Side side, Vec2 from) =>
        w.Bodies.Where(b => Bays(w, b, side) > 0).OrderBy(b => Vec2.Distance(b.Position, from)).ThenBy(b => b.Id).FirstOrDefault();

    public static string? CanSend(World w, Fleet f)
    {
        if (!w.Loop) return "Refits need a shipyard (not in the battle sandbox)";
        if (Heat.InBattle(w, f) || f.FightPower > 0) return "No refits in battle";
        if (f.Refit != null) return "Already going for refit";
        if (!w.ShipsOf(f).Any(s => Outdated(w, s))) return "Every ship is on its current design";
        if (NearestYard(w, f.Side, f.Position) == null) return "No yard with bays";
        return null;
    }

    /// <summary>For the fleet panel: wait for a bay, time in the bays, and when it should be back on station (hours from now).</summary>
    public static (double Wait, double InBay, double Back)? Estimate(World w, Fleet f)
    {
        var yard = f.Refit is { } plan ? w.FindBody(plan.YardBodyId) : NearestYard(w, f.Side, f.Position);
        if (yard == null) return null;
        var ships = w.ShipsOf(f).Where(s => Outdated(w, s) || s.RefitHoursLeft > 0).ToList();
        var bays = Math.Max(1, Bays(w, yard, f.Side));
        var free = FreeBays(w, yard, f.Side);
        var speed = Math.Max(1, FleetPerformance.Of(w, f).MaxSpeed * 0.5);
        var fly = Economy.InOrbitAt(f, yard) ? 0 : Vec2.Distance(f.Position, yard.Position) / speed;
        var wait = free > 0 ? 0 : HoursPerShip;
        var each = ships.Count == 0 ? 0 : ships.Max(s => s.RefitHoursLeft > 0 ? s.RefitHoursLeft : HoursFor(w, s, yard));
        var inBay = each * Math.Ceiling(ships.Count / (double)Math.Max(1, Math.Min(bays, Math.Max(free, 1))));
        var back = f.Refit?.ReturnBodyId is int r && w.FindBody(r) is Body rb ? Vec2.Distance(yard.Position, rb.Position) / speed : fly;
        return (fly + wait, inBay, fly + wait + inBay + back);
    }

    /// <summary>One line for the fleet panel about refit, repair and supply, or null with nothing going on.</summary>
    public static string? Status(World w, Fleet f)
    {
        if (f.Refit is { } plan && w.FindBody(plan.YardBodyId) is Body yard)
        {
            var e = Estimate(w, f);
            var back = e is { } x ? $", back on station in about {x.Back / 24:0.#} days" : "";
            return plan.Stage switch
            {
                RefitStage.Flying => $"Going to {yard.Name} for refit{back}",
                RefitStage.Waiting => $"Waiting for a refit bay at {yard.Name}{(plan.Waiting != null ? $" ({plan.Waiting})" : "")}{back}",
                _ => $"Refitting at {yard.Name}: {w.ShipsOf(f).Count(s => s.RefitHoursLeft > 0)} in the bays{back}",
            };
        }
        if (w.ShipsOf(f).Any(s => s.Crippled || Wounds.Worst(w, s) < 0.95))
        {
            var at = w.Bodies.FirstOrDefault(b => Economy.InOrbitAt(f, b) && Colonies.RepairsHere(w, b, f.Side));
            if (at != null) return $"Repairing at {at.Name}";
            if (f.Retreating) return $"Heading for repairs{(f.PullReason != null ? $" ({f.PullReason})" : "")}";
        }
        if (w.Fleets.FirstOrDefault(o => o.SupplyRunFor == f.Id) is Fleet tanker) return $"Supplies coming: {tanker.Name}{(tanker.AutoRun ? " (Auto)" : "")}";
        if (f.RejoinFleetId is int id && w.FindFleet(id) is Fleet home) return $"Will rejoin {home.Name} once mended";
        return null;
    }

    /// <summary>Hourly: fleets on a refit fly in, take bays as they come free, pay, refit, and fly back.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Refit != null).OrderBy(f => f.Id).ToList())
        {
            var plan = f.Refit!;
            if (w.FindBody(plan.YardBodyId) is not Body yard || yard.Owner != f.Side)
            {
                End(sim, f, "its yard is lost");
                continue;
            }
            if (!Economy.InOrbitAt(f, yard))
            {
                plan.Stage = RefitStage.Flying;
                if (f.Flight == null && !f.Maneuvering && Fly(w, f, yard.Id, yard.Position) == null)
                    End(sim, f, "it can't fly to the yard");
                continue;
            }
            if (Heat.InBattle(w, f) || Economy.Blockaded(w, yard)) { plan.Waiting = "the yard is under attack"; continue; }
            plan.Waiting = null;
            var state = Economy.Of(w, f.Side);
            foreach (var s in w.ShipsOf(f).Where(s => s.RefitHoursLeft > 0).ToList())
            {
                s.RefitHoursLeft -= GameTime.HoursPerTick;
                if (s.RefitHoursLeft > 1e-9) continue;
                s.RefitHoursLeft = 0;
                Fitting.Apply(w, s, Target(w, s));
                sim.Log.Info("fitting", $"{s.Name} is refitted to the current design", new { side = f.Side.ToString(), shipId = s.Id, yard = yard.Name });
            }
            foreach (var s in w.ShipsOf(f).Where(s => s.RefitHoursLeft <= 0 && Outdated(w, s)).OrderBy(s => s.Id).ToList())
            {
                if (FreeBays(w, yard, f.Side) <= 0) { plan.Waiting ??= "all bays busy"; break; }
                var cost = CostOf(s);
                var mats = MaterialsOf(s);
                var shortOf = state.Industry < cost ? $"needs {cost - state.Industry:0} more Industry" : Economy.MaterialShort(yard, mats);
                if (shortOf != null) { plan.Waiting = shortOf; break; }
                state.Industry -= cost;
                state.Totals.IndustrySpent += cost;
                Economy.TakeMaterials(yard, mats);
                s.RefitHoursLeft = HoursFor(w, s, yard);
                plan.Stage = RefitStage.Refitting;
                sim.Log.Info("fitting", $"{s.Name} goes into a refit bay at {yard.Name}", new { side = f.Side.ToString(), shipId = s.Id, cost, materials = mats, hours = s.RefitHoursLeft });
            }
            if (plan.Stage != RefitStage.Refitting && w.ShipsOf(f).Any(s => Outdated(w, s))) plan.Stage = RefitStage.Waiting;
            if (w.ShipsOf(f).All(s => s.RefitHoursLeft <= 0 && !Outdated(w, s)))
            {
                var back = plan.ReturnBodyId is int r && r != yard.Id ? w.FindBody(r) : null;
                End(sim, f, null);
                if (back != null) Fly(w, f, back.Id, back.Position);
                else if (plan.ReturnPoint is Vec2 p) Fly(w, f, null, p);
                sim.Log.Info("fitting", $"{f.Name} is refitted and goes back on station", new { fleetId = f.Id, to = back?.Name });
            }
        }
    }

    private static Flight? Fly(World w, Fleet f, int? bodyId, Vec2 at)
    {
        var req = new FlightRequest(bodyId, at, 0.6, ArrivalStyle.Brake, bodyId != null ? HoldStyle.Orbit : HoldStyle.Drift, w.Tick);
        var plan = FlightPlanner.Plan(w, f, req);
        if (!plan.Ok) plan = FlightPlanner.Plan(w, f, req with { SpeedFraction = 0.3 });
        if (plan is { Ok: true, Flight: Flight flight }) f.Flight = flight;
        return f.Flight;
    }

    private static void End(Simulation sim, Fleet f, string? why)
    {
        f.Refit = null;
        foreach (var s in sim.World.ShipsOf(f)) s.RefitHoursLeft = 0;
        if (why != null) sim.Log.Info("fitting", $"{f.Name}'s refit is called off: {why}", new { fleetId = f.Id });
    }

    /// <summary>Send a fleet (or the most outdated half of it) for refit; returns the fleet that goes.</summary>
    public static Fleet? Send(Simulation sim, Fleet f, bool half)
    {
        var w = sim.World;
        if (CanSend(w, f) is string problem)
        {
            sim.Log.Warn("fitting", $"Can't send {f.Name} for refit: {problem}", new { fleetId = f.Id });
            return null;
        }
        var go = f;
        if (half && f.ShipIds.Count > 1)
        {
            var ships = w.ShipsOf(f).OrderByDescending(s => Outdated(w, s)).ThenBy(s => s.Id).Take((f.ShipIds.Count + 1) / 2).ToList();
            new SplitFleetCommand(f.Id, ships.Select(s => s.Id).ToList()) { Source = "refit" }.Apply(w, sim);
            go = w.FleetOf(ships[0]) ?? f;
        }
        var yard = NearestYard(w, go.Side, go.Position)!;
        var station = go.OrbitBodyId is int b && go.Flight == null && go.Hold == HoldStyle.Orbit ? b : (int?)null;
        go.Refit = new RefitPlan
        {
            YardBodyId = yard.Id, StartHour = w.Hour, ReturnBodyId = station,
            ReturnPoint = station == null ? go.Position : null, Stage = Economy.InOrbitAt(go, yard) ? RefitStage.Waiting : RefitStage.Flying,
        };
        go.Mission = Mission.None;
        if (!Economy.InOrbitAt(go, yard)) Fly(w, go, yard.Id, yard.Position);
        sim.Log.Info("fitting", $"{go.Name} goes to {yard.Name} for refit", new { fleetId = go.Id, yard = yard.Name, half });
        return go;
    }

    /// <summary>Worth a refit: something outdated, a bay free at the nearest yard, Industry to spare for it.</summary>
    public static bool WorthIt(World w, Fleet f, double reserve)
    {
        var outdated = w.ShipsOf(f).Where(s => Outdated(w, s)).ToList();
        if (outdated.Count == 0 || CanSend(w, f) != null) return false;
        var yard = NearestYard(w, f.Side, f.Position);
        return yard != null && FreeBays(w, yard, f.Side) > 0 && Economy.Of(w, f.Side).Industry - reserve >= outdated.Sum(CostOf);
    }

    /// <summary>The Refits area: idle fleets at home or holding orbit over their own planets, when worth it.</summary>
    public static void Steward(Simulation sim, Side side, OfferFn offer)
    {
        var w = sim.World;
        var reserve = w.Orders.FirstOrDefault(o => o.Side == side && o.Kind == OrderKind.Ship && !o.Paid) is WorkOrder next ? Economy.CostOf(next) : 0;
        foreach (var f in w.Fleets.Where(f => f.Side == side && Planner.Warfleet(w, f) && f.Refit == null && f.Flight == null && !f.Retreating
                                              && f.Mission is Mission.None or Mission.Hold
                                              && f.OrbitBodyId is int b && w.FindBody(b)?.Owner == side).OrderBy(f => f.Id).ToList())
        {
            if (!WorthIt(w, f, reserve)) continue;
            var n = w.ShipsOf(f).Count(s => Outdated(w, s));
            var yard = NearestYard(w, side, f.Position)!;
            if (offer(AutoArea.Refits, $"refit:{f.Id}", $"Send {f.Name} to {yard.Name} to refit {n} ship{(n == 1 ? "" : "s")} to the current design",
                    new SendForRefitCommand(side, f.Id), "Send for refit", fleetId: f.Id))
                reserve += w.ShipsOf(f).Where(s => Outdated(w, s)).Sum(CostOf);
        }
    }
}

/// <summary>"Send for refit": the fleet (or half of it) flies to the nearest yard, refits to the current designs, and comes back.</summary>
public sealed record SendForRefitCommand(Side Side, int FleetId, bool Half = false) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Refit order") is not Fleet f || f.Side != Side) return;
        Choices.Note(world, Side, "refit", Half ? "half at a time" : "whole fleet", "", Source);
        Refits.Send(sim, f, Half);
    }
}

/// <summary>Call off a fleet's refit (ships already in a bay finish theirs first is not kept: the work is lost).</summary>
public sealed record CancelRefitCommand(Side Side, int FleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Refit order") is not Fleet f || f.Side != Side || f.Refit == null) return;
        f.Refit = null;
        foreach (var s in world.ShipsOf(f)) s.RefitHoursLeft = 0;
        sim.Log.Info("fitting", $"{f.Name}'s refit is called off", new { FleetId, Source });
    }
}
