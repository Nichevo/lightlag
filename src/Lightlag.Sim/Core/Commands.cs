using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

/// <summary>
/// Everything a player (or AI) does to the game goes through a command. Commands are recorded with
/// the tick they took effect, which is what a replay is: the seed plus this list.
/// Never change the world from the UI directly; add a command instead.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PlanFlightCommand), "planFlight")]
[JsonDerivedType(typeof(StopFleetCommand), "stopFleet")]
[JsonDerivedType(typeof(SetHoldCommand), "setHold")]
[JsonDerivedType(typeof(SplitFleetCommand), "splitFleet")]
[JsonDerivedType(typeof(MergeFleetsCommand), "mergeFleets")]
[JsonDerivedType(typeof(SetStanceCommand), "setStance")]
[JsonDerivedType(typeof(SendAheadCommand), "sendAhead")]
[JsonDerivedType(typeof(GatherFleetsCommand), "gatherFleets")]
[JsonDerivedType(typeof(QueueOrderCommand), "queueOrder")]
[JsonDerivedType(typeof(CancelOrderCommand), "cancelOrder")]
[JsonDerivedType(typeof(DebugCrashCommand), "debugCrash")]
[JsonDerivedType(typeof(DebugBreakRuleCommand), "debugBreakRule")]
public abstract record GameCommand
{
    /// <summary>Who issued it, e.g. "player", "ai:rival", "headless".</summary>
    public string Source { get; init; } = "player";

    public abstract void Apply(World world, Simulation sim);

    protected Fleet? GetFleet(World world, Simulation sim, int fleetId, string what)
    {
        var fleet = world.FindFleet(fleetId);
        if (fleet == null) sim.Log.Warn("fleet", $"{what} for a fleet that does not exist", new { fleetId, Source });
        return fleet;
    }
}

/// <summary>Fly a fleet to a body (it ends in orbit there, or drifting) or to a point in space.</summary>
public sealed record PlanFlightCommand(int FleetId, FlightRequest Request) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Flight order") is not Fleet fleet) return;
        var plan = FlightPlanner.Plan(world, fleet, Request);
        if (!plan.Ok || plan.Flight == null)
        {
            sim.Log.Warn("fleet", $"{fleet.Name} cannot fly that: {plan.Problem}", new { fleetId = fleet.Id, Request, Source });
            return;
        }
        fleet.Flight = plan.Flight;
        fleet.JoinFleetId = null;
        var target = Request.TargetBodyId is int b ? world.FindBody(b)?.Name : plan.Flight.Target.ToString();
        sim.Log.Info("fleet", $"{fleet.Name} plans a flight to {target}", new
        {
            fleetId = fleet.Id, Request, plan.Flight.DepartHour, plan.Flight.ArriveHour, plan.DeltaV, Source,
        });
    }
}

/// <summary>Cancel the flight. A moving fleet brakes to a stop if it has the fuel, otherwise it drifts on.</summary>
public sealed record StopFleetCommand(int FleetId) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Stop order") is not Fleet fleet) return;
        fleet.Flight = null;
        fleet.JoinFleetId = null;
        if (fleet.Velocity.Length < 1e-9) return;
        if (FlightPlanner.BrakeToStop(world, fleet) is Flight brake)
        {
            fleet.Flight = brake;
            sim.Log.Info("fleet", $"{fleet.Name} brakes to a stop", new { fleetId = fleet.Id, Source });
        }
        else
        {
            sim.Log.Warn("fleet", $"{fleet.Name} has no fuel to brake and drifts on", new { fleetId = fleet.Id, Source });
        }
    }
}

/// <summary>Orbit the nearest body, or drift with engines cold. The fleet must be stopped.</summary>
public sealed record SetHoldCommand(int FleetId, HoldStyle Hold) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Hold order") is not Fleet fleet) return;
        if (fleet.InFlight && fleet.Flight!.Started)
        {
            sim.Log.Warn("fleet", $"{fleet.Name} is flying; stop it first", new { fleetId = fleet.Id, Hold, Source });
            return;
        }
        if (fleet.InFlight) fleet.Flight!.HoldAfter = Hold;
        if (Hold == HoldStyle.Drift)
        {
            Fleets.EnterDrift(fleet);
            fleet.Velocity = Vec2.Zero;
            return;
        }
        if (fleet.Velocity.Length > 1e-9)
        {
            sim.Log.Warn("fleet", $"{fleet.Name} is still moving; stop it first", new { fleetId = fleet.Id, Source });
            return;
        }
        if (Fleets.OrbitableNear(world, fleet.Position) is Body body) Fleets.EnterOrbit(fleet, body);
        else sim.Log.Warn("fleet", $"Nothing near {fleet.Name} to orbit", new { fleetId = fleet.Id, Source });
    }
}

/// <summary>Take some ships out of a fleet into a new fleet that carries on doing the same thing.</summary>
public sealed record SplitFleetCommand(int FleetId, List<int> ShipIds) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Split order") is not Fleet fleet) return;
        var leaving = ShipIds.Distinct().Where(fleet.ShipIds.Contains).Select(id => world.FindShip(id)!).ToList();
        if (leaving.Count == 0 || leaving.Count == fleet.ShipIds.Count)
        {
            sim.Log.Warn("fleet", $"Split of {fleet.Name} needs some but not all of its ships", new { fleetId = fleet.Id, ShipIds, Source });
            return;
        }
        fleet.ShipIds.RemoveAll(id => leaving.Any(s => s.Id == id));
        var created = Fleets.Create(world, fleet.Side, leaving, fleet.Position);
        created.Velocity = fleet.Velocity;
        created.Hold = fleet.Hold;
        created.OrbitBodyId = fleet.OrbitBodyId;
        created.OrbitRadius = fleet.OrbitRadius;
        created.OrbitAngle = fleet.OrbitAngle;
        created.Flight = fleet.Flight?.Copy();
        created.Stance = fleet.Stance;
        created.Facing = fleet.Facing;
        created.RetreatWhenDamaged = fleet.RetreatWhenDamaged;
        Fleets.Arrange(world, fleet);
        Fleets.SetApart(world, created);
        sim.Log.Info("fleet", $"{created.Name} splits from {fleet.Name}", new { fleetId = fleet.Id, newFleetId = created.Id, ShipIds, Source });
    }
}

/// <summary>Join another fleet into this one. Both must be stopped and close together.</summary>
public sealed record MergeFleetsCommand(int FleetId, int OtherFleetId) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Merge order") is not Fleet fleet) return;
        if (GetFleet(world, sim, OtherFleetId, "Merge order") is not Fleet other) return;
        if (Fleets.CanMerge(fleet, other) is string problem)
        {
            sim.Log.Warn("fleet", $"Cannot merge {other.Name} into {fleet.Name}: {problem}", new { FleetId, OtherFleetId, Source });
            return;
        }
        Fleets.Absorb(world, fleet, other);
        sim.Log.Info("fleet", $"{other.Name} merges into {fleet.Name}", new { FleetId, OtherFleetId, Source });
    }
}

/// <summary>
/// "Fast ships go ahead": the ships faster than the fleet's slowest split off and fly the same order at
/// their own pace, then wait at the destination and join the slower ships when they arrive.
/// </summary>
public sealed record SendAheadCommand(int FleetId, FlightRequest Request) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Send-ahead order") is not Fleet fleet) return;
        if (Request.Arrival != ArrivalStyle.Brake)
        {
            sim.Log.Warn("fleet", "Fast ships can only go ahead on a flight that brakes at the end", new { FleetId, Source });
            return;
        }
        var ships = world.ShipsOf(fleet).ToList();
        var slowest = ships.Count == 0 ? 0 : ships.Min(Fleets.ShipSpeed);
        var fast = ships.Where(s => Fleets.ShipSpeed(s) > slowest * 1.0001).ToList();
        if (fast.Count == 0 || fast.Count == ships.Count)
        {
            new PlanFlightCommand(FleetId, Request) { Source = Source }.Apply(world, sim);
            return;
        }

        fleet.ShipIds.RemoveAll(id => fast.Any(s => s.Id == id));
        var ahead = Fleets.Create(world, fleet.Side, fast, fleet.Position);
        ahead.Velocity = fleet.Velocity;
        ahead.Hold = fleet.Hold;
        ahead.OrbitBodyId = fleet.OrbitBodyId;
        ahead.OrbitRadius = fleet.OrbitRadius;
        ahead.OrbitAngle = fleet.OrbitAngle;
        ahead.Stance = fleet.Stance;
        ahead.Facing = fleet.Facing;
        ahead.RetreatWhenDamaged = fleet.RetreatWhenDamaged;
        Fleets.Arrange(world, fleet);

        var slowPlan = FlightPlanner.Plan(world, fleet, Request);
        var fastPlan = FlightPlanner.Plan(world, ahead, Request);
        if (!slowPlan.Ok || slowPlan.Flight == null || !fastPlan.Ok || fastPlan.Flight == null)
        {
            // Put things back as they were.
            Fleets.Absorb(world, fleet, ahead);
            world.FleetNumbers[(int)fleet.Side]--;
            sim.Log.Warn("fleet", $"{fleet.Name} cannot send its fast ships ahead: {slowPlan.Problem ?? fastPlan.Problem}", new { FleetId, Request, Source });
            return;
        }
        fleet.Flight = slowPlan.Flight;
        fleet.JoinFleetId = null;
        ahead.Flight = fastPlan.Flight;
        ahead.JoinFleetId = fleet.Id;
        sim.Log.Info("fleet", $"{ahead.Name} goes ahead of {fleet.Name}", new
        {
            FleetId, aheadFleetId = ahead.Id, Request, slowArrive = slowPlan.Flight.ArriveHour, fastArrive = fastPlan.Flight.ArriveHour, Source,
        });
    }
}

/// <summary>
/// Bring fleets together into one: those already close join now; the others fly to it (to its
/// destination if it is flying) and join when they get there.
/// </summary>
public sealed record GatherFleetsCommand(int FleetId, List<int> OtherFleetIds, double SpeedFraction) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Gather order") is not Fleet into) return;
        foreach (var otherId in OtherFleetIds.Distinct())
        {
            if (world.FindFleet(otherId) is not Fleet other || other.Id == into.Id || other.Side != into.Side) continue;
            if (Fleets.CanJoin(other, into))
            {
                Fleets.Absorb(world, into, other);
                sim.Log.Info("fleet", $"{other.Name} merges into {into.Name}", new { FleetId, otherFleetId = otherId, Source });
                continue;
            }
            if (Meeting(world, into) is not FlightRequest request)
            {
                sim.Log.Warn("fleet", $"{into.Name} is drifting on; stop it before gathering", new { FleetId, Source });
                return;
            }
            var plan = FlightPlanner.Plan(world, other, request);
            if (!plan.Ok || plan.Flight == null)
            {
                sim.Log.Warn("fleet", $"{other.Name} cannot reach {into.Name}: {plan.Problem}", new { FleetId, otherFleetId = otherId, Source });
                continue;
            }
            other.Flight = plan.Flight;
            other.JoinFleetId = into.Id;
            sim.Log.Info("fleet", $"{other.Name} flies to join {into.Name}", new { FleetId, otherFleetId = otherId, plan.Flight.ArriveHour, Source });
        }
    }

    /// <summary>Where to meet the fleet: where it is flying to, the body it circles, or the point where it waits.</summary>
    private FlightRequest? Meeting(World world, Fleet into)
    {
        if (into.Flight is Flight f && f.TargetFleetId == null && f.Arrival == ArrivalStyle.Brake)
            return new FlightRequest(f.TargetBodyId, f.Target, SpeedFraction, ArrivalStyle.Brake, f.HoldAfter, world.Tick);
        if (into.Flight != null || into.Velocity.Length > 1e-9) return null;
        if (into.Hold == HoldStyle.Orbit && into.OrbitBodyId is int body && world.FindBody(body) is Body b)
            return new FlightRequest(b.Id, b.Position, SpeedFraction, ArrivalStyle.Brake, HoldStyle.Orbit, world.Tick);
        return new FlightRequest(null, into.Position, SpeedFraction, ArrivalStyle.Brake, HoldStyle.Drift, world.Tick);
    }
}

/// <summary>Set a fleet's standing orders: engage or hold, and whether to retreat when badly damaged.</summary>
public sealed record SetStanceCommand(int FleetId, Stance Stance, bool RetreatWhenDamaged) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Standing orders") is not Fleet fleet) return;
        fleet.Stance = Stance;
        fleet.RetreatWhenDamaged = RetreatWhenDamaged;
        sim.Log.Info("fleet", $"{fleet.Name} standing orders: {Stance}" + (RetreatWhenDamaged ? ", retreat when damaged" : ""), new { FleetId, Stance, RetreatWhenDamaged, Source });
    }
}

/// <summary>Throws inside the simulation on purpose, to test that crash reports and replays work.</summary>
public sealed record DebugCrashCommand(string Reason) : GameCommand
{
    public override void Apply(World world, Simulation sim) =>
        throw new InvalidOperationException($"Test crash on purpose: {Reason}");
}

/// <summary>Breaks the game state on purpose, to test that rule checks catch it.</summary>
public sealed record DebugBreakRuleCommand : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (world.Fleets.Count == 0) return;
        var f = world.Fleets[0];
        f.Flight = null;
        Fleets.EnterDrift(f);
        f.Velocity = Vec2.Zero;
        f.Position = new Vec2(double.NaN, 0);
    }
}
