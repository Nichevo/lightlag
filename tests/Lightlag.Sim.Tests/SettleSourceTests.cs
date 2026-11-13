using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class SettleSourceTests
{
    [Fact]
    public void Settlers_without_an_escort_are_queued_and_the_source_is_the_shipyard()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Events = EventRate.Off });
        sim.World.Minds.Clear();
        var w = sim.World;
        var yard = Economy.ShipyardOf(w, Side.Player)!;
        var to = w.Bodies.Where(b => b.Owner == null && b.PopK <= 0 && Planets.Settleable(b) && Planets.CanSettle(w, Side.Player, b.Id) == null)
            .OrderBy(b => b.Id).First();

        var src = Planets.SettleSource(w, Side.Player, to.Id);
        Assert.NotNull(src);
        Assert.Equal(yard.Id, src!.From.Id);
        Assert.True(src.PeopleK > 0);

        sim.Issue(new SettleCommand(Side.Player, to.Id));
        sim.ApplyPending();
        Assert.Contains(w.Orders, o => o.SettleBodyId == to.Id && o.EscortFleetId == null);
    }

    [Fact]
    public void A_rival_fleet_seen_on_the_way_raises_the_trip_risk_and_an_escort_lowers_it()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Events = EventRate.Off });
        sim.World.Minds.Clear();
        var w = sim.World;
        var to = w.Bodies.Where(b => b.Owner == null && b.PopK <= 0 && Planets.Settleable(b) && Planets.CanSettle(w, Side.Player, b.Id) == null)
            .OrderBy(b => b.Id).First();
        var before = Planets.SettleSource(w, Side.Player, to.Id)!;
        Assert.Equal(RouteRisk.Low, before.Risk);

        var mid = (before.From.Position + to.Position) * 0.5;
        Intel.Of(w, Side.Player).Fleets.Add(new FleetFact { FleetId = 9001, Name = "Raider", Ships = 3, Position = mid, Hour = w.Hour, FirstHour = w.Hour });
        var after = Planets.SettleSource(w, Side.Player, to.Id)!;
        Assert.True(after.Risk > RouteRisk.Low);
        Assert.Contains("rival fleet", after.RiskWhy);
        Assert.True(after.EscortedRisk < after.Risk);
    }
}
