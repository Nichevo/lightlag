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

    [Fact]
    public void A_slow_ship_holding_the_fleets_burn_back_is_named()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = w.Fleets.Where(x => x.Side == Side.Player && x.ShipIds.Count >= 2).OrderBy(x => x.Id).First();
        var ships = w.ShipsOf(f).ToList();
        foreach (var s in ships) s.Accel = 15;
        Assert.Null(FleetPerformance.HeldBackBy(w, f));
        ships[^1].Accel = 8;
        var held = FleetPerformance.HeldBackBy(w, f);
        Assert.NotNull(held);
        Assert.Equal(ships[^1].Name, held!.Value.Ship);
        Assert.Null(Chases.PullingAway(w, f)); // no chase: nothing to say
    }
}
