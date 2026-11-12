using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class RefusedOrderTests
{
    [Fact]
    public void A_refused_player_order_marks_the_fleet_and_tells_the_screen()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(0, 2, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 1500));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        mine.Velocity = new Vec2(0, 30);
        var heard = new List<string>();
        sim.OrderRefused += (_, why) => heard.Add(why);

        sim.Issue(new SetHoldCommand(mine.Id, HoldStyle.Orbit));
        sim.ApplyPending();

        Assert.Equal("it is still moving; stop it first", mine.Refused);
        Assert.Single(heard);
        Assert.Contains("still moving", heard[0]);
    }

    [Fact]
    public void Orders_from_automation_do_not_reach_the_screen()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(0, 2, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 1500));
        var mine = sim.World.Fleets.First(f => f.Side == Side.Player);
        var heard = 0;
        sim.OrderRefused += (_, _) => heard++;

        sim.Issue(new SplitFleetCommand(mine.Id, new List<int>()) { Source = "auto" });
        sim.ApplyPending();

        Assert.Equal(0, heard);
        Assert.NotNull(mine.Refused);
    }
}
