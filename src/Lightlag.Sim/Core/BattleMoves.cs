namespace Lightlag.Sim.Core;

/// <summary>What ships split off in one click do next: take their own orders, run for a yard, or hold back at range.</summary>
public enum SplitThen { OwnOrders, RunHome, HoldBack }

/// <summary>Which ship a fleet protects: the flagship only (as before), its most damaged ship (Auto), one ship picked by hand, or none.</summary>
public enum ProtectPick { Flagship, Worst, Ship, None }

/// <summary>
/// Battle moves built on top of the fleet orders: split off and run (or hold back) in one click, the "when done,
/// rejoin" rule for split-off groups, which ship a fleet protects, and who point defence fires for. The rival
/// gives the same commands (see Mind.BattleMoves). How to add another: docs/BATTLE-MECHANICS.md.
/// </summary>
public static class BattleMoves
{
    /// <summary>Ships just split off from <paramref name="from"/> as <paramref name="g"/>: set them going.</summary>
    public static void AfterSplit(Simulation sim, Fleet from, Fleet g, SplitThen then)
    {
        var w = sim.World;
        var fighting = Heat.InBattle(w, from) || Combat.NearestEnemyFleet(w, from, GameData.Combat.ContactRangeMm) != null;
        g.RejoinFleetId = from.Id;
        switch (then)
        {
            case SplitThen.RunHome:
                g.RejoinWhen = RejoinWhen.Mended;
                g.Chase = null;
                g.JoinFleetId = null;
                BattleRules.PullBack(sim, g, $"split off from {from.Name} to run", toYard: true, alone: true, byHand: true);
                break;
            case SplitThen.HoldBack:
                g.RejoinWhen = RejoinWhen.Done;
                HoldBack(sim, from, g);
                break;
            default:
                g.RejoinWhen = fighting ? RejoinWhen.Done : RejoinWhen.Never;
                if (!fighting) g.RejoinFleetId = null;
                break;
        }
        sim.Log.Info("fleet", "Ships split off", new { fleetId = from.Id, newFleetId = g.Id, then = then.ToString(), rejoin = g.RejoinWhen.ToString(), fighting });
    }

    /// <summary>The enemy a fleet is fighting or chasing: its chase quarry, else the nearest enemy fleet it can see in contact range.</summary>
    public static Fleet? Foe(World w, Fleet f) =>
        f.Chase is { Mode: not ChaseMode.Join } c && w.FindFleet(c.TargetFleetId) is Fleet q && q.Side != f.Side ? q
        : Combat.NearestEnemyFleet(w, f, GameData.Combat.ContactRangeMm);

    /// <summary>The range a group holding back keeps: just inside missile range when it carries missiles, else just outside their railguns.</summary>
    public static ChaseMode HoldBackMode(World w, Fleet g) =>
        w.ShipsOf(g).Any(s => s.Missiles > 0 && !s.Out(Module.Missiles)) ? ChaseMode.MissileRange : ChaseMode.ShadowGuns;

    private static void HoldBack(Simulation sim, Fleet from, Fleet g)
    {
        var w = sim.World;
        g.Stance = Stance.Hold; // it keeps its distance: no closing in to gun range
        g.JoinFleetId = null;
        if (Foe(w, from) is Fleet foe && Sensors.Find(w, g.Side, foe.Id) is Sighting s)
        {
            var mode = HoldBackMode(w, g);
            var req = new FlightRequest(null, s.Guess(w.Hour), 1.0, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick, TargetFleetId: foe.Id, Chase: mode);
            new PlanFlightCommand(g.Id, req) { Source = "split" }.Apply(w, sim);
            if (g.Chase != null) return;
        }
        // Nothing to hold off from (or no way to): stop where it is and wait.
        g.Chase = null;
        g.Flight = null;
        g.Maneuvering = true; // brakes to a stop (Combat.Steer)
        Fleets.EnterDrift(g);
    }

    /// <summary>
    /// Has a split-off group finished its task? Out of the fight, not pulling back or under way, and either with no
    /// chase left or only holding back at range while the fleet it came from is out of the fight too.
    /// </summary>
    public static bool Done(World w, Fleet g, Fleet from)
    {
        if (Heat.InBattle(w, g) || g.Retreating || g.Maneuvering || (g.Flight != null && g.Chase == null)) return false;
        if (g.Chase == null) return true;
        var holding = g.Chase.Mode is ChaseMode.MissileRange or ChaseMode.ShadowGuns or ChaseMode.ShadowMissiles or ChaseMode.RailRange;
        return holding && !Heat.InBattle(w, from) && !from.Maneuvering && from.Chase?.TargetFleetId != g.Chase.TargetFleetId;
    }

    /// <summary>Hourly (full games): split-off groups set to rejoin when done fly back to their fleet once done.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        foreach (var g in w.Fleets.Where(g => g.RejoinWhen == RejoinWhen.Done && g.RejoinFleetId != null && !g.HeldBack && g.ShipIds.Count > 0)
                     .OrderBy(g => g.Id).ToList())
        {
            var from = w.FindFleet(g.RejoinFleetId!.Value);
            if (from == null || from.Side != g.Side || from.ShipIds.Count == 0)
            {
                g.RejoinFleetId = null;
                g.RejoinWhen = RejoinWhen.Never;
                continue;
            }
            if (!Done(w, g, from)) continue;
            g.Chase = null;
            g.RejoinWhen = RejoinWhen.Never;
            if (Fleets.FlyToJoin(w, g, from, 1.0) is string problem)
            {
                Autonomy.Report(sim, g, $"{g.Name} is done but can't rejoin {from.Name}: {problem}. It waits here.", NoteKind.StandingOrder);
                sim.Log.Info("fleet", "Split-off group can't rejoin", new { fleetId = g.Id, target = from.Id, problem });
                continue;
            }
            g.JoinFleetId = from.Id;
            g.Stance = from.Stance;
            sim.Log.Info("fleet", "Split-off group is done and flies to rejoin", new { fleetId = g.Id, target = from.Id });
        }
    }

    /// <summary>What the fleet protects now, as the battle view's Formation card shows it.</summary>
    public static (ProtectPick Pick, int? ShipId) Protecting(World w, Fleet f)
    {
        var ships = w.ShipsOf(f).ToList();
        if (ships.Find(s => s.Protect == true) is Ship picked) return (ProtectPick.Ship, picked.Id);
        if (f.ProtectWorst) return (ProtectPick.Worst, null);
        if (ships.Count > 0 && ships.All(s => s.Protect == false)) return (ProtectPick.None, null);
        return (ProtectPick.Flagship, null);
    }

    /// <summary>The ship the fleet protects now, in words (for the card and the fleet panel).</summary>
    public static string ProtectText(World w, Fleet f)
    {
        var (pick, _) = Protecting(w, f);
        var guarded = Formations.Guarded(w, f);
        var ship = guarded != 0 ? w.FindShip(guarded) : null;
        var who = ship == null ? "nobody" : ship.Name;
        return pick switch
        {
            ProtectPick.Worst => ship == null ? "the most damaged ship (none hurt yet)" : $"{who} (most damaged)",
            ProtectPick.None => "nobody",
            ProtectPick.Ship => who,
            _ => Light.Flagship(w, f.Side) is Ship flag && flag.FleetId == f.Id ? $"{flag.Name} (flagship; keeps its place)" : "the flagship (not in this fleet)",
        };
    }
}

/// <summary>Who the fleet's point defence fires for: every ship of the fleet close by, or each mount only for its own ship (harder, less heat).</summary>
public sealed record SetPdGuardCommand(int FleetId, PdGuard Guard) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Point defence order") is not Fleet fleet) return;
        fleet.PdGuard = Guard;
        sim.Log.Info("fleet", "Point defence guard set", new { FleetId, Guard = Guard.ToString(), Source });
    }
}

/// <summary>
/// Which ship the fleet protects (it takes the safest slot in the formation, and point defence puts it first). Picking
/// a ship or the most damaged one puts a fleet in Loose cover into Screen, so other ships take some of the fire meant for it.
/// </summary>
public sealed record ProtectShipCommand(int FleetId, ProtectPick Pick, int? ShipId = null) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Protect order") is not Fleet fleet) return;
        if (Pick == ProtectPick.Ship && (ShipId is not int id || !fleet.ShipIds.Contains(id)))
        {
            Refuse(world, sim, fleet, "that ship is not in this fleet", new { FleetId, ShipId, Source });
            return;
        }
        foreach (var s in world.ShipsOf(fleet))
            s.Protect = Pick switch
            {
                ProtectPick.Ship => s.Id == ShipId ? true : false,
                ProtectPick.None => false,
                _ => null,
            };
        fleet.ProtectWorst = Pick == ProtectPick.Worst;
        if (Pick is ProtectPick.Ship or ProtectPick.Worst && fleet.Cover == CoverStyle.Loose) fleet.Cover = CoverStyle.Screen;
        sim.Log.Info("fleet", "Protect set", new { FleetId, Pick = Pick.ToString(), ShipId, cover = fleet.Cover.ToString(), Source });
    }
}

/// <summary>Split ships off and send them on in one order: own orders, run for the nearest yard, or hold back at range.</summary>
public sealed record SplitOffCommand(int FleetId, List<int> ShipIds, SplitThen Then) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Split order") is not Fleet fleet) return;
        var ids = ShipIds.Distinct().Where(fleet.ShipIds.Contains).ToList();
        if (ids.Count == 0 || ids.Count == fleet.ShipIds.Count)
        {
            Refuse(world, sim, fleet, "a split needs some but not all of its ships", new { fleetId = fleet.Id, ShipIds, Source });
            return;
        }
        new SplitFleetCommand(FleetId, ids) { Source = Source }.Apply(world, sim);
        if (world.FindShip(ids[0]) is not Ship first || world.FleetOf(first) is not Fleet g || g.Id == fleet.Id) return;
        BattleMoves.AfterSplit(sim, fleet, g, Then);
    }
}

/// <summary>When a split-off group flies back to the fleet it came from: once its task is done, once mended, or only by order.</summary>
public sealed record SetRejoinWhenCommand(int FleetId, RejoinWhen When) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Rejoin order") is not Fleet fleet) return;
        if (fleet.RejoinFleetId == null)
        {
            Refuse(world, sim, fleet, "it did not split off from another fleet", new { FleetId, Source });
            return;
        }
        fleet.RejoinWhen = When;
        sim.Log.Info("fleet", "Rejoin rule set", new { FleetId, When = When.ToString(), Source });
    }
}
