using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Fixes from the first M8 + M9 playtest: blockades, leaving orbit to fight, supply reserves, who decided.</summary>
public class PlaytestFixTests
{
    private static Simulation NewGame(ulong seed = 8, GameOptions? options = null)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);
    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    private static void SendAway(Fleet f)
    {
        f.Flight = null;
        Fleets.EnterDrift(f);
        f.Position = new Vec2(0, 30 * Physics.MmPerAu);
    }

    [Fact]
    public void A_fleet_in_orbit_over_an_enemy_shipyard_blockades_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var rust = BodyNamed(sim, "rust");
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Rival).ToList()) SendAway(f);
        w.Minds.Clear(); // no rival plans: just its build queue
        Economy.Of(w, Side.Rival).Industry = 1000;
        sim.Issue(new QueueOrderCommand(Side.Rival, OrderKind.Ship, "gun-frigate"));
        PlaceInOrbit(sim, FleetOf(sim, Side.Player), rust);
        var ships = w.Ships.Count(s => s.Side == Side.Rival);
        sim.StepMany(3 * GameTime.TicksPerDay);
        Assert.True(Economy.Blockaded(w, rust));
        Assert.Equal(ships, w.Ships.Count(s => s.Side == Side.Rival));
        Assert.Contains(w.Orders, o => o.Side == Side.Rival && o.Blockaded);
        Assert.Contains("blockaded", Economy.BombardStatus(w, rust, Side.Player));
    }

    [Fact]
    public void Bombard_status_says_when_defenders_stop_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var rust = BodyNamed(sim, "rust");
        Assert.StartsWith("Not under attack: park", Economy.BombardStatus(w, rust, Side.Player));
        PlaceInOrbit(sim, FleetOf(sim, Side.Player), rust);
        var defender = FleetOf(sim, Side.Rival);
        defender.Position = rust.Position + new Vec2(30, 0);
        Assert.Contains("defended", Economy.BombardStatus(w, rust, Side.Player));
        Assert.Null(Economy.BombardStatus(w, BodyNamed(sim, "verdant"), Side.Player));
    }

    [Fact]
    public void A_fleet_in_orbit_fights_from_orbit_and_keeps_its_orbit()
    {
        var sim = NewGame();
        var w = sim.World;
        var rust = BodyNamed(sim, "rust");
        var mine = FleetOf(sim, Side.Player);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Rival).ToList()) SendAway(f);
        PlaceInOrbit(sim, mine, rust);
        var enemy = FleetOf(sim, Side.Rival);
        enemy.Position = rust.Position + new Vec2(60, 0);
        enemy.Warm = true;
        enemy.LastBurnHour = w.Hour;
        for (var i = 0; i < 360; i++) sim.Step();
        Assert.Equal(HoldStyle.Orbit, mine.Hold);
        Assert.Equal(rust.Id, mine.OrbitBodyId);
        Assert.Null(mine.LeftOrbitBodyId);
    }

    [Fact]
    public void A_fleet_that_left_orbit_to_fight_goes_back_once_the_fight_is_over()
    {
        var sim = NewGame();
        var w = sim.World;
        var rust = BodyNamed(sim, "rust");
        var mine = FleetOf(sim, Side.Player);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Rival).ToList()) SendAway(f);
        PlaceInOrbit(sim, mine, rust);
        // As if it had broken orbit for a fight that is now over.
        mine.LeftOrbitBodyId = rust.Id;
        var moving = Sensors.MotionOf(w, mine);
        Fleets.EnterDrift(mine);
        mine.Velocity = moving;
        mine.Position += new Vec2(20, 0);
        Assert.Contains("left orbit", Economy.BombardStatus(w, rust, Side.Player));
        sim.Step();
        Assert.Equal(HoldStyle.Orbit, mine.Hold); // right by the planet: straight back into orbit
        Assert.Equal(rust.Id, mine.OrbitBodyId);
        Assert.Null(mine.LeftOrbitBodyId);
    }

    [Fact]
    public void A_fleet_down_to_its_fuel_reserve_breaks_off_and_coasts_to_refill()
    {
        var sim = NewGame();
        var w = sim.World;
        var mine = FleetOf(sim, Side.Player);
        mine.Flight = null;
        Fleets.EnterDrift(mine);
        mine.Position = BodyNamed(sim, "verdant").Position + new Vec2(0.3 * Physics.MmPerAu, 0);
        foreach (var s in w.ShipsOf(mine)) s.Position = mine.Position;
        sim.Step();
        Assert.True(mine.ReserveDeltaV > 0);
        // Burn the fuel down to just under the reserve.
        var perf = FleetPerformance.Of(w, mine);
        sim.BurnFuel(mine, perf.DeltaVLeft - mine.ReserveDeltaV * 1.1);
        sim.Step();
        Assert.True(mine.Resupplying);
        Assert.NotNull(mine.Flight);
        Assert.Equal(Side.Player, w.FindBody(mine.Flight!.TargetBodyId!.Value)!.Owner);
        Assert.Contains(w.Light.Notes, n => n.FleetId == mine.Id && n.Kind == NoteKind.Supply);
        Assert.False(Combat.Dodging(w, mine));
    }

    [Fact]
    public void Far_fleets_say_when_they_act_on_a_standing_order_rather_than_on_their_own()
    {
        var sim = NewGame(options: new GameOptions(Light: new LightSetup(true, 1.0)));
        var w = sim.World;
        var mine = FleetOf(sim, Side.Player);
        var flag = Light.Flagship(w, Side.Player)!;
        // A far fleet (not the flagship's) set to Return home meets an enemy.
        var far = mine.ShipIds.Count > 1 ? mine : mine;
        sim.Issue(new SplitFleetCommand(mine.Id, w.ShipsOf(mine).Where(s => s.Id != flag.Id).Select(s => s.Id).Take(1).ToList()));
        sim.Step();
        var lone = w.Fleets.First(f => f.Side == Side.Player && !f.ShipIds.Contains(flag.Id));
        lone.Flight = null;
        Fleets.EnterDrift(lone);
        lone.Position = BodyNamed(sim, "rust").Position + new Vec2(200, 0);
        foreach (var s in w.ShipsOf(lone)) s.Position = lone.Position;
        lone.Stance = Stance.ReturnHome;
        lone.Autonomy = AutonomyLevel.Strict;
        var enemy = FleetOf(sim, Side.Rival);
        enemy.Flight = null;
        Fleets.EnterDrift(enemy);
        enemy.Position = lone.Position + new Vec2(50, 0);
        enemy.Warm = true;
        enemy.LastBurnHour = w.Hour;
        for (var i = 0; i < 50 && !w.Light.Notes.Any(n => n.FleetId == lone.Id); i++) sim.Step();
        var note = Assert.Single(w.Light.Notes, n => n.FleetId == lone.Id);
        Assert.Equal(NoteKind.StandingOrder, note.Kind);
        Assert.True(note.ArriveHour > note.Hour);
        _ = far;
    }
}
