using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class PlaytestScreenTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Events = EventRate.Off });
        sim.World.Minds.Clear();
        return sim;
    }

    [Fact]
    public void A_contact_with_no_shots_is_not_a_fight()
    {
        var b = new Battle();
        Assert.False(b.Fought);
        b.For(Side.Rival).RailShots = 2;
        Assert.True(b.Fought);
    }

    [Fact]
    public void A_queued_building_short_of_Industry_shows_as_held_up_with_why()
    {
        var sim = NewGame();
        var verdant = sim.World.Bodies.First(b => b.Key == "verdant");
        Economy.Of(sim.World, Side.Player).Industry = 0;
        sim.Issue(new BuildOnPlanetCommand(Side.Player, verdant.Id, Planets.Farm, Queue: true));
        sim.ApplyPending();
        var held = Colonies.HeldUp(sim.World, verdant);
        Assert.Contains(held, h => h.Why.StartsWith("waiting for", StringComparison.Ordinal) && h.Why.Contains("Industry"));
    }
}
