using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Home orders (shipyard queue, research teams) take effect at once, so they work while paused.</summary>
public class PausedOrderTests
{
    [Fact]
    public void Home_orders_apply_without_a_step_even_with_the_flagship_far_away()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions(Light: new LightSetup(true, 1.0)));
        sim.Step(); // the first team forms on the first tick
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player)) f.Position = new Vec2(f.Position.X + 3e6, f.Position.Y); // light minutes away
        var yard = Economy.ShipyardOf(w, Side.Player)!;
        Assert.True(Light.DelayHours(w, Side.Player, yard.Position) > 0);

        var teams = Economy.Of(w, Side.Player).Teams;
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.Issue(new SplitTeamCommand(Side.Player, teams[0].Id, "sensors"));
        sim.ApplyPending();
        Assert.Single(w.Orders);
        Assert.Equal(2, teams.Count);
        Assert.Empty(w.Light.InTransit);
    }
}
