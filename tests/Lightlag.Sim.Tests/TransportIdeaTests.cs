using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class TransportIdeaTests
{
    [Fact]
    public void Ideas_bring_a_short_good_from_a_planet_with_spare_and_send_it_the_other_way()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 1, GameLog.Memory());
        Planets.EnsureStarted(sim.World);
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var moon = w.Bodies.First(m => m.ParentId == home.Id && m.Kind == BodyKind.Moon && m.Owner == Side.Player);
        Assert.Equal(Side.Player, moon.Owner);
        Economy.Of(w, Side.Player).AutoTransport = false;

        // The moon runs dry of spare parts; home holds plenty.
        moon.Stock[Colonies.Spares] = 0;
        home.Stock[Colonies.Spares] = 5000;
        Assert.True(Colonies.NeedOf(moon, Colonies.Spares) >= 1);
        Assert.True(Colonies.SpareOf(home, Colonies.Spares) >= 1);

        var atMoon = Colonies.Ideas(w, Side.Player, moon);
        var bring = Assert.Single(atMoon, i => i.Bring && i.Good == Colonies.Spares);
        Assert.Equal(home.Id, bring.OtherId);
        Assert.True(bring.Amount >= 1 && bring.TripDays >= 0);
        Assert.False(bring.AutoDoing);

        var atHome = Colonies.Ideas(w, Side.Player, home);
        var send = Assert.Single(atHome, i => !i.Bring && i.Good == Colonies.Spares);
        Assert.Equal(moon.Id, send.OtherId);

        // With Auto transport on, the same load is marked as Auto's.
        Economy.Of(w, Side.Player).AutoTransport = true;
        Assert.True(Colonies.Ideas(w, Side.Player, moon).Single(i => i.Bring && i.Good == Colonies.Spares).AutoDoing);
    }
}
