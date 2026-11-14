using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class PlaytestScreenTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Events = EventRate.Off, Budget = false }); // one pool: these tests predate the Industry budget (see BudgetTests)
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

    [Fact]
    public void Support_ships_stay_behind_when_the_fleet_goes_in_and_rejoin_after()
    {
        var sim = NewGame();
        var w = sim.World;
        var at = w.Bodies.Single(b => b.Key == "verdant").Position * 1.3;
        var ships = new[] { Economy.NewShip(w, Side.Player, "gun-frigate"), Economy.NewShip(w, Side.Player, "gun-frigate"), Economy.NewShip(w, Side.Player, "supply-tender") }.ToList();
        var f = Fleets.Create(w, Side.Player, ships, at);
        foreach (var s in ships) s.Position = f.Position + s.FormationOffset;
        var foes = new[] { Economy.NewShip(w, Side.Rival, "gun-frigate") }.ToList();
        var enemy = Fleets.Create(w, Side.Rival, foes, at + new Vec2(2, 0));
        foreach (var s in foes) s.Position = enemy.Position + s.FormationOffset;
        Assert.True(Fleets.Unarmed(ships[2]));

        sim.Issue(new SetLeaveSupportCommand(f.Id, true));
        sim.Issue(new PlanFlightCommand(f.Id, new FlightRequest(null, enemy.Position, 1.0, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick, TargetFleetId: enemy.Id, Chase: ChaseMode.GunRange)));
        sim.ApplyPending();
        Assert.Equal(2, f.ShipIds.Count);
        var held = w.Fleets.Single(x => x.ShipIds.Contains(ships[2].Id));
        Assert.True(held.HeldBack);
        Assert.Equal(f.Id, held.RejoinFleetId);

        // The fight is over (the enemy is gone and the chase ended): the support ships set off to rejoin.
        w.Fleets.Remove(enemy);
        foreach (var s in foes) w.Ships.Remove(s);
        f.Chase = null;
        f.Flight = null;
        BattleRules.Hourly(sim);
        Assert.False(held.HeldBack);
        Assert.Equal(f.Id, held.JoinFleetId);
    }

    private static (Fleet Mine, Fleet Enemy, List<Ship> Ships) TwoFleets(Simulation sim, string[] mine, double apartMm)
    {
        var w = sim.World;
        var at = w.Bodies.Single(b => b.Key == "verdant").Position * 1.3;
        var ships = mine.Select(c => Economy.NewShip(w, Side.Player, c)).ToList();
        var f = Fleets.Create(w, Side.Player, ships, at);
        foreach (var s in ships) s.Position = f.Position + s.FormationOffset;
        var foes = new[] { Economy.NewShip(w, Side.Rival, "gun-frigate") }.ToList();
        var enemy = Fleets.Create(w, Side.Rival, foes, at + new Vec2(apartMm, 0));
        foreach (var s in foes) s.Position = enemy.Position + s.FormationOffset;
        return (f, enemy, ships);
    }

    [Fact]
    public void The_missile_budget_caps_missiles_in_flight_at_one_ship()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory(), sandbox: new SandboxSetup(PlayerFrigates: 0, PlayerDestroyers: 3, RivalFrigates: 1, RivalDestroyers: 0,
            RivalStance: Stance.Hold, DistanceMm: 45));
        var w = sim.World;
        var f = w.Fleets.First(x => x.Side == Side.Player);
        sim.Issue(new SetMissileBudgetCommand(f.Id, 2));
        sim.ApplyPending();
        Assert.Equal(2, f.MissilesPerTarget);
        var most = 0;
        var fired = false;
        var end = w.Tick + 48;
        while (w.Tick < end && (w.Battles.Count == 0 || w.ActiveBattle != null))
        {
            sim.Step();
            var counts = w.Missiles.Where(m => m.Side == Side.Player).GroupBy(m => m.TargetShipId).Select(g => g.Count()).ToList();
            if (counts.Count > 0) { fired = true; most = Math.Max(most, counts.Max()); }
        }
        Assert.True(fired, "no missiles were fired");
        Assert.True(most <= 2, $"{most} missiles flew at one ship");
    }

    [Fact]
    public void Sending_one_ship_home_splits_it_off_to_retreat_and_rejoin_later()
    {
        var sim = NewGame();
        var w = sim.World;
        var (f, _, ships) = TwoFleets(sim, new[] { "gun-frigate", "gun-frigate" }, 500);
        sim.Issue(new SendShipHomeCommand(f.Id, ships[1].Id));
        sim.ApplyPending();
        var alone = w.Fleets.Single(x => x.ShipIds.Contains(ships[1].Id));
        Assert.NotEqual(f.Id, alone.Id);
        Assert.True(alone.Retreating);
        Assert.Equal(f.Id, alone.RejoinFleetId);
        Assert.Single(f.ShipIds);
    }

    [Fact]
    public void Chasing_offers_let_them_go_and_trip_times_without_the_tender()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory(), sandbox: new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 150));
        var w = sim.World;
        var f = w.Fleets.First(x => x.Side == Side.Player);
        var enemy = w.Fleets.First(x => x.Side == Side.Rival);
        var tender = Economy.NewShip(w, Side.Player, "supply-tender");
        var tf = Fleets.Create(w, Side.Player, new List<Ship> { tender }, f.Position);
        tender.Position = f.Position;
        Fleets.Absorb(w, f, tf);
        sim.Step();
        Assert.True(Sensors.Find(w, Side.Player, enemy.Id) != null, "the enemy was not seen");
        var before = Engagement.Of(w, f, enemy.Id)!;
        Assert.DoesNotContain(before.Options, o => o.Key == Engagement.LetGoKey);
        var engage = before.Options.Single(o => o.Key == "engage");
        sim.Issue(new PlanFlightCommand(f.Id, engage.Request! with { DepartTick = w.Tick }));
        sim.ApplyPending();
        var chasing = Engagement.Of(w, f, enemy.Id)!;
        Assert.Contains(chasing.Options, o => o.Key == Engagement.LetGoKey);

        f.LeaveSupportBehind = true;
        var lean = Engagement.Of(w, f, enemy.Id)!.Options.Single(o => o.Key == "engage");
        Assert.Contains("warships only", lean.Summary);
        Assert.Equal(3, f.ShipIds.Count); // planning leaves the fleet as it was
    }
}
