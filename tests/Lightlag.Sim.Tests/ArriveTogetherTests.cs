using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class ArriveTogetherTests
{
    [Fact]
    public void Two_fleets_at_different_distances_are_timed_to_arrive_together()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(3, 0, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 400));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        Sensors.Reveal(w, Side.Player, theirs);
        sim.Issue(new SplitFleetCommand(mine.Id, new List<int> { mine.ShipIds[^1] }));
        sim.ApplyPending();
        var other = w.Fleets.Single(f => f.Side == Side.Player && f.Id != mine.Id);
        // Put the second fleet much closer, so on its own it would arrive well before the first.
        other.Position = mine.Position + (theirs.Position - mine.Position) * 0.6;

        var alone = FlightPlanner.Plan(w, other, new FlightRequest(null, theirs.Position, 1.0, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick,
            TargetFleetId: theirs.Id, Chase: ChaseMode.GunRange));
        var plan = Engagement.Together(w, mine, other, theirs.Id);
        Assert.Null(plan.Problem);
        var a = FlightPlanner.Plan(w, mine, plan.First!);
        var b = FlightPlanner.Plan(w, other, plan.Second!);
        Assert.True(alone.Flight!.ArriveHour < a.Flight!.ArriveHour - 1, "the near fleet alone should be well ahead");
        Assert.True(Math.Abs(a.Flight.ArriveHour - b.Flight!.ArriveHour) < 1.0,
            $"arrivals {a.Flight.ArriveHour:0.00} and {b.Flight.ArriveHour:0.00} should be within an hour");
        Assert.True(plan.Second!.SpeedFraction < 1.0);
    }
}
