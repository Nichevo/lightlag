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
        Fleets.Arrange(world, fleet);
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
        foreach (var s in world.ShipsOf(other).ToList())
        {
            fleet.ShipIds.Add(s.Id);
            s.FleetId = fleet.Id;
        }
        world.Fleets.Remove(other);
        Fleets.Arrange(world, fleet);
        sim.Log.Info("fleet", $"{other.Name} merges into {fleet.Name}", new { FleetId, OtherFleetId, Source });
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
