using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class SensorTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);

    [Fact]
    public void Cold_fleets_are_seen_only_close_up()
    {
        // One cold frigate each: a frigate sees another cold frigate out to its sensor range (30 Mm).
        var far = Sandbox(new SandboxSetup(1, 0, 1, 0, RivalStance: Stance.Hold, PlayerStance: Stance.Hold, DistanceMm: 40));
        Assert.False(Sensors.Sees(far.World, Side.Player, FleetOf(far, Side.Rival)));
        var near = Sandbox(new SandboxSetup(1, 0, 1, 0, RivalStance: Stance.Hold, PlayerStance: Stance.Hold, DistanceMm: 20));
        Assert.True(Sensors.Sees(near.World, Side.Player, FleetOf(near, Side.Rival)));
    }

    [Fact]
    public void A_burning_fleet_lights_up_from_far_away()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 1, 0, RivalStance: Stance.Hold, PlayerStance: Stance.Hold, DistanceMm: 5000));
        var player = FleetOf(sim, Side.Player);
        Assert.False(Sensors.Sees(sim.World, Side.Rival, player));
        sim.Issue(new PlanFlightCommand(player.Id, new FlightRequest(null, player.Position + new Vec2(0, 20000), 1, ArrivalStyle.Brake, HoldStyle.Drift, 0)));
        sim.Step();
        Assert.True(player.Thrusting);
        Assert.Equal(SignatureState.Burn, Sensors.StateOf(player));
        Assert.True(Sensors.Sees(sim.World, Side.Rival, player));
    }

    [Fact]
    public void A_lost_contact_leaves_a_last_seen_mark()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 1, 0, RivalStance: Stance.Hold, PlayerStance: Stance.Hold, DistanceMm: 5000));
        var player = FleetOf(sim, Side.Player);
        sim.Issue(new PlanFlightCommand(player.Id, new FlightRequest(null, player.Position + new Vec2(0, 20000), 1, ArrivalStyle.Brake, HoldStyle.Drift, 0)));
        // Burn, then coast: the rival keeps the sighting from the burn but loses sight once the engines stop.
        sim.Step();
        var burnEnd = player.Flight!.Legs[0].End;
        while (sim.World.Hour < burnEnd + 2) sim.Step();
        Assert.False(player.Thrusting);
        var sighting = Sensors.Find(sim.World, Side.Rival, player.Id);
        Assert.NotNull(sighting);
        Assert.False(sighting!.Visible);
        // The guess carries on along the last seen course.
        var guess = sighting.Guess(sim.World.Hour);
        Assert.True(Vec2.Distance(guess, player.Position) < Vec2.Distance(sighting.Position, player.Position));
    }

    [Fact]
    public void Planets_block_the_line_of_sight()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 1, GameLog.Memory());
        var verdant = sim.World.Bodies.Single(b => b.Key == "verdant");
        var a = verdant.Position + new Vec2(-50, 0);
        var b = verdant.Position + new Vec2(50, 0);
        Assert.False(Sensors.LineOfSight(sim.World, a, b));
        Assert.True(Sensors.LineOfSight(sim.World, a, b + new Vec2(0, 40)));
    }

    [Fact]
    public void Destroyers_hold_fire_at_frigates_they_cannot_see()
    {
        // 58 Mm is inside missile range, but cold frigates only show up at about 49 Mm to a destroyer.
        var sim = Sandbox(new SandboxSetup(3, 0, 0, 1, RivalStance: Stance.Hold, PlayerStance: Stance.Hold, DistanceMm: 58));
        sim.StepMany(6);
        Assert.Empty(sim.World.Missiles);
        Assert.True(sim.World.Battles.Count == 0);
    }

    [Fact]
    public void You_cannot_plan_an_attack_on_a_fleet_never_seen()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 1, 0, DistanceMm: 400, RivalKnowsWhere: false));
        var rival = FleetOf(sim, Side.Rival);
        var player = FleetOf(sim, Side.Player);
        var req = new FlightRequest(null, player.Position, 1, ArrivalStyle.Brake, HoldStyle.Drift, 0, TargetFleetId: player.Id);
        var plan = FlightPlanner.Plan(sim.World, rival, req);
        Assert.False(plan.Ok);
        // The player was told where the rival is, so the player can.
        Assert.True(FlightPlanner.Plan(sim.World, player, req with { TargetFleetId = rival.Id }).Ok);
    }
}
