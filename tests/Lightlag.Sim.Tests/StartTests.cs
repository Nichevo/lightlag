using Lightlag.Sim;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class StartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_new_game_starts_the_player_researching_and_with_spare_power_at_home(bool seeded)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory(),
            options: GameOptions.VsRival(Difficulty.Normal) with { Map = seeded ? new MapSetup(MapType.SunLike, MapSize.Standard, 5) : null, StartResearch = true });
        sim.Step();
        Assert.Contains(sim.World.Orders, o => o.Side == Side.Player && o.Kind == OrderKind.Research);
        Assert.Equal(1, sim.World.Orders.Count(o => o.Side == Side.Player && o.Kind == OrderKind.Research));
        var home = sim.World.Bodies.Single(b => b.Owner == Side.Player && b.Home);
        Assert.True(home.PowerMade - home.PowerWanted >= 6, $"{home.Name}: made {home.PowerMade}, wanted {home.PowerWanted}");
    }
}
