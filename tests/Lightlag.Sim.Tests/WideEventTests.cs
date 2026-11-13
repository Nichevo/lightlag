using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class WideEventTests
{
    [Fact]
    public void A_flare_shows_as_a_wide_event_while_coming_and_while_it_lasts()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Events = EventRate.Off });
        sim.World.Minds.Clear();
        var w = sim.World;
        Assert.Empty(Events.Wide(w, Side.Player));

        var e = Events.Spawn(sim, Side.Player, "flare");
        Assert.NotNull(e);
        var coming = Assert.Single(Events.Wide(w, Side.Player));
        Assert.True(coming.Coming);
        Assert.Equal(e!.HitsHour, coming.UntilHour);

        sim.Issue(new PickEventCommand(Side.Player, e.Id, 1));
        while (!e.Hit && w.Hour < e.HitsHour + 2) sim.Step();
        var now = Assert.Single(Events.Wide(w, Side.Player));
        Assert.False(now.Coming);
        Assert.Contains("sensors", now.Effect);

        while (w.Hour < w.FlareUntilHour + 2) sim.Step();
        Assert.Empty(Events.Wide(w, Side.Player));
    }
}
