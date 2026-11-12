using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class LostContactTests
{
    [Fact]
    public void A_chase_after_a_quarry_long_out_of_sight_closes_in_to_look_and_reports_it_missing()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(0, 2, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 1500));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        theirs.Velocity = new Vec2(0, 60);
        Sensors.Reveal(w, Side.Player, theirs);
        sim.Issue(new PlanFlightCommand(mine.Id, new FlightRequest(null, theirs.Position, 1.0, ArrivalStyle.Brake, HoldStyle.Drift, 0,
            TargetFleetId: theirs.Id, Chase: ChaseMode.MissileRange)));
        sim.StepMany(2);
        Assert.NotNull(mine.Chase);
        // The quarry slips away somewhere else entirely; the player keeps its old track.
        theirs.Position = new Vec2(-theirs.Position.X, -theirs.Position.Y - 20000);
        theirs.Velocity = Vec2.Zero;

        while (mine.Chase != null && w.Hour < 30 * 24) sim.Step();
        Assert.Null(mine.Chase);
        Assert.NotNull(mine.LostContact);
        Assert.Contains(sim.Log.Tail(), l => l.Contains("closes in to look for"));
        Assert.Contains(w.Light.Notes, n => n.FleetId == mine.Id && n.Text.Contains("is not where expected"));
        Assert.Equal(theirs.Id, mine.LostContact!.FleetId);
    }

    [Fact]
    public void Missiles_from_a_fast_fleet_still_hit_a_slow_target()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory(),
            sandbox: new SandboxSetup(0, 3, 1, 0, 1, 1, Stance.Engage, Stance.Engage, 55));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        foreach (var s in w.ShipsOf(theirs)) { s.Ammo = 0; s.Missiles = 0; }
        mine.Velocity = new Vec2(0, 190);
        theirs.Velocity = new Vec2(0, 20);
        Sensors.Reveal(w, Side.Player, theirs);
        var start = w.Hour;
        while (w.Hour < start + 3) sim.Step();
        var stats = (w.Battles.LastOrDefault() ?? w.ActiveBattle)!.For(Side.Player);
        Assert.True(stats.MissilesFired > 0);
        Assert.True(stats.MissileHits > 0, $"fired {stats.MissilesFired}, hits {stats.MissileHits}, dry {stats.RanDry}");
    }

    [Fact]
    public void A_fleet_shows_its_tanker_on_the_way_and_a_tanker_shows_its_run()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 1, GameLog.Memory());
        Planets.EnsureStarted(sim.World);
        var w = sim.World;
        var mine = w.Fleets.Where(f => f.Side == Side.Player).ToList();
        var transport = mine.First(f => f.ShipIds.Count == 1 && w.ShipsOf(f).All(SupplyLines.IsSupplyShip));
        var war = mine.First(f => f.Id != transport.Id && f.ShipIds.Count > 1);
        Assert.Null(SupplyLines.RunStatus(w, war));
        sim.Issue(new SetSupplyRunCommand(Side.Player, transport.Id, war.Id));
        sim.StepMany(1);
        var status = SupplyLines.RunStatus(w, war);
        Assert.NotNull(status);
        Assert.StartsWith($"Tanker {transport.Name}", status);
    }
}
