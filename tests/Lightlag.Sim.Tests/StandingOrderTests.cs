using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class StandingOrderTests
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
    public void Engage_if_stronger_holds_back_against_a_bigger_fleet()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 0, 3, PlayerStance: Stance.EngageIfStronger, DistanceMm: 80));
        sim.Step();
        var mine = FleetOf(sim, Side.Player);
        Assert.False(Combat.Stronger(sim.World, mine));
        Assert.False(Combat.WouldEngage(sim.World, mine, FleetOf(sim, Side.Rival)));
        Assert.False(mine.Maneuvering);
    }

    [Fact]
    public void Engage_if_stronger_closes_in_on_a_weaker_fleet()
    {
        var sim = Sandbox(new SandboxSetup(6, 2, 1, 0, PlayerStance: Stance.EngageIfStronger, DistanceMm: 80));
        sim.Step();
        var mine = FleetOf(sim, Side.Player);
        Assert.True(Combat.Stronger(sim.World, mine));
        Assert.True(mine.Maneuvering);
    }

    [Fact]
    public void Hold_position_brakes_to_a_stop_on_contact()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 2, 0, PlayerStance: Stance.HoldPosition, DistanceMm: 90));
        var mine = FleetOf(sim, Side.Player);
        mine.Velocity = new Vec2(30, 0); // drifting towards the rival
        for (var i = 0; i < 2 * GameTime.SubSteps && mine.Velocity.Length > 1e-6; i++) sim.Step();
        Assert.True(mine.Velocity.Length < 1e-6);
        Assert.False(mine.Maneuvering);
        Assert.False(mine.Retreating);
    }

    [Fact]
    public void Return_home_breaks_off_on_contact()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 2, 0, PlayerStance: Stance.ReturnHome, DistanceMm: 80));
        sim.Step();
        var mine = FleetOf(sim, Side.Player);
        var home = Economy.HomeOf(sim.World, Side.Player)!;
        Assert.True(mine.Retreating);
        Assert.Equal(home.Id, mine.Flight?.TargetBodyId);
    }

    [Fact]
    public void Avoid_heads_home_when_an_enemy_comes_for_it()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 2, 0, PlayerStance: Stance.Avoid, DistanceMm: 400));
        var mine = FleetOf(sim, Side.Player);
        sim.Step();
        Assert.False(mine.Retreating); // nobody coming yet
        // The rival has just burned straight at it (warm engines show up far off), about 7 hours out.
        var rival = FleetOf(sim, Side.Rival);
        rival.Velocity = new Vec2(-60, 0);
        rival.LastBurnHour = sim.World.Hour;
        for (var i = 0; i < 3 && !mine.Retreating; i++) sim.Step();
        Assert.True(mine.Retreating);
        Assert.Equal(Economy.HomeOf(sim.World, Side.Player)!.Id, mine.Flight?.TargetBodyId);
    }

    [Fact]
    public void A_fleet_at_home_does_not_keep_fleeing()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 2, 0, PlayerStance: Stance.ReturnHome, DistanceMm: 80));
        sim.Step();
        var mine = FleetOf(sim, Side.Player);
        Assert.True(Combat.GoingHome(sim.World, mine));
    }
}
