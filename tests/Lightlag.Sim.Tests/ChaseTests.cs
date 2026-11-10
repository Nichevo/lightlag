using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class ChaseTests
{
    private static (Simulation Sim, Fleet Mine, Fleet Theirs) Setup(int myFrigates, int myDestroyers, Vec2 theirVelocity, double distance = 1500)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(myFrigates, myDestroyers, 1, 0, 1, 1, Stance.Hold, Stance.Hold, distance));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        var mine = sim.World.Fleets.First(f => f.Side == Side.Player);
        var theirs = sim.World.Fleets.First(f => f.Side == Side.Rival);
        theirs.Velocity = theirVelocity;
        Sensors.Reveal(sim.World, Side.Player, theirs);
        return (sim, mine, theirs);
    }

    private static FlightRequest Chase(Fleet target, ChaseMode mode, double speed = 1.0) =>
        new(null, target.Position, speed, ArrivalStyle.Brake, HoldStyle.Drift, 0, TargetFleetId: target.Id, Chase: mode);

    [Fact]
    public void Chase_settles_at_missile_range_moving_with_the_enemy_even_out_of_sight()
    {
        var (sim, mine, theirs) = Setup(2, 0, new Vec2(0, 120));
        sim.Issue(new PlanFlightCommand(mine.Id, Chase(theirs, ChaseMode.MissileRange)));
        sim.StepMany(1);
        Assert.NotNull(mine.Chase);
        sim.StepMany(60);
        var gap = Vec2.Distance(mine.Position, theirs.Position);
        var want = FlightPlanner.ChaseDistance(ChaseMode.MissileRange)!.Value;
        Assert.InRange(gap, want * 0.8, want * 1.2);
        Assert.True((mine.Velocity - theirs.Velocity).Length < 5, $"relative speed {(mine.Velocity - theirs.Velocity).Length}");
        Assert.NotNull(mine.Chase);
    }

    [Fact]
    public void Chase_reaims_when_the_enemy_is_seen_to_change_course()
    {
        var (sim, mine, theirs) = Setup(2, 0, new Vec2(0, 120));
        sim.Issue(new PlanFlightCommand(mine.Id, Chase(theirs, ChaseMode.MissileRange)));
        sim.StepMany(4);
        // The enemy turns; the player is told (as if a burn was seen).
        theirs.Velocity = new Vec2(0, -150);
        Sensors.Reveal(sim.World, Side.Player, theirs);
        sim.Log.Info("test", "turn", new { });
        sim.StepMany(1);
        var c = mine.Chase!;
        Assert.Equal(new Vec2(0, -150), c.TargetVelocity);
        sim.StepMany(60);
        Assert.True(Math.Abs(Vec2.Distance(mine.Position, theirs.Position) - FlightPlanner.ChaseDistance(ChaseMode.MissileRange)!.Value) < 10,
            $"gap {Vec2.Distance(mine.Position, theirs.Position)}\n" + string.Join("\n", sim.Log.Tail().Where(l => l.Contains("fleet") || l.Contains("combat"))));
    }

    [Fact]
    public void Too_slow_to_chase_says_which_ship_holds_it_back()
    {
        var (sim, mine, theirs) = Setup(1, 1, new Vec2(500, 0));
        var plan = FlightPlanner.Plan(sim.World, mine, Chase(theirs, ChaseMode.GunRange));
        Assert.False(plan.Ok);
        Assert.Contains("slowest ship", plan.Problem);
        Assert.Contains("500 Mm/h", plan.Problem);
    }

    [Fact]
    public void Overtake_passes_the_enemy_and_ends_the_chase()
    {
        var (sim, mine, theirs) = Setup(2, 0, new Vec2(0, 80), 600);
        sim.Issue(new PlanFlightCommand(mine.Id, Chase(theirs, ChaseMode.Overtake, 0.5)));
        sim.StepMany(1);
        Assert.Equal(ChaseMode.Overtake, mine.Flight!.Chase);
        sim.StepMany(48);
        Assert.Null(mine.Chase);
    }
}

public class HeadingForTests
{
    [Fact]
    public void An_enemy_coming_straight_at_a_fleet_is_flagged_and_one_going_past_is_not()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(1, 0, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 1000));
        var mine = sim.World.Fleets.First(f => f.Side == Side.Player);
        var theirs = sim.World.Fleets.First(f => f.Side == Side.Rival);
        theirs.Velocity = (mine.Position - theirs.Position).Normalized() * 100;
        Sensors.Reveal(sim.World, Side.Player, theirs);
        var eta = Sensors.HeadingFor(Sensors.Find(sim.World, Side.Player, theirs.Id)!, mine, sim.World.Hour);
        Assert.NotNull(eta);
        Assert.InRange(eta!.Value, 9, 11);

        theirs.Velocity = new Vec2(0, 100);
        Sensors.Reveal(sim.World, Side.Player, theirs);
        Assert.Null(Sensors.HeadingFor(Sensors.Find(sim.World, Side.Player, theirs.Id)!, mine, sim.World.Hour));
    }
}
