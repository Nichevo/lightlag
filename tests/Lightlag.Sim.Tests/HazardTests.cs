using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Space hazards (rocks, debris, radiation, the sun) and events.</summary>
public class HazardTests
{
    private static Simulation NewGame(ulong seed = 8, EventRate events = EventRate.Off)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: new GameOptions { Events = events });
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);
    private static Belt BeltNamed(Simulation sim, string key) => sim.World.Belts.Single(b => b.Key == key);

    private static Fleet NewFleet(Simulation sim, Side side, Vec2 at, params string[] classes) =>
        Fleets.Create(sim.World, side, classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList(), at);

    /// <summary>A fleet drifting through the Scatter at this speed, far from anyone.</summary>
    private static Fleet Drifting(Simulation sim, double speed, params string[] classes)
    {
        var sun = BodyNamed(sim, "hesper").Position;
        var f = NewFleet(sim, Side.Player, sun + new Vec2(0, -2.7 * Physics.MmPerAu), classes);
        Fleets.EnterDrift(f);
        f.Velocity = new Vec2(speed, 0);
        return f;
    }

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    [Fact]
    public void The_map_has_the_scatter_rings_and_radiation_belts()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.Equal(BeltKind.Belt, BeltNamed(sim, "scatter").Kind);
        Assert.Equal(BeltKind.Ring, BeltNamed(sim, "tamber-ring").Kind);
        Assert.Equal(BeltKind.Radiation, BeltNamed(sim, "tamber-radiation").Kind);
        // Rings and radiation belts are sized in the planet's radii (after the size scale).
        var tamber = BodyNamed(sim, "tamber");
        Assert.Equal(1.25 * tamber.Radius, BeltNamed(sim, "tamber-ring").InnerRadius, 6);
        // Brim sits in Tamber's radiation belt; Verdant is clear of every hazard.
        Assert.True(Hazards.RadiationAt(w, BodyNamed(sim, "brim").Position) > 0);
        Assert.Equal((0.0, 0.0), Hazards.RocksAt(w, BodyNamed(sim, "verdant").Position));
        Assert.Equal(0, Hazards.RadiationAt(w, BodyNamed(sim, "verdant").Position));
    }

    [Fact]
    public void Rocks_hit_big_ships_more_and_fast_ships_harder()
    {
        var sim = NewGame();
        var w = sim.World;
        var escort = Economy.NewShip(w, Side.Player, "escort");
        var cruiser = Economy.NewShip(w, Side.Player, "cruiser");
        var capital = Economy.NewShip(w, Side.Player, "capital");
        Assert.Equal(0, Hazards.SizeOf(escort));
        Assert.Equal(2, Hazards.SizeOf(cruiser));
        Assert.Equal(3, Hazards.SizeOf(capital));
        // Hits scale with cross-section: a huge ship takes eight times a small one's.
        var small = Hazards.HitsPerHour(w, escort, 300, 1, 0, pd: false);
        var huge = Hazards.HitsPerHour(w, capital, 300, 1, 0, pd: false);
        Assert.Equal(8, huge / small, 6);
        // Twice the speed: twice the hits an hour (the same per crossing) and each hit hurts twice as much.
        Assert.Equal(2, Hazards.HitsPerHour(w, capital, 600, 1, 0, false) / huge, 6);
        Assert.Equal(2, Hazards.HitShare(w, Side.Player, 600) / Hazards.HitShare(w, Side.Player, 300), 6);
        // Point defence clears some; debris hurts sizes much alike.
        Assert.True(Hazards.HitsPerHour(w, capital, 300, 1, 0, pd: true) < huge);
        Assert.True(Hazards.HitsPerHour(w, capital, 300, 0, 1, false) / Hazards.HitsPerHour(w, escort, 300, 0, 1, false) < 4);
        // Not moving: no hits.
        Assert.Equal(0, Hazards.HitsPerHour(w, capital, 0, 1, 0, false));
    }

    [Fact]
    public void A_fast_cruiser_in_the_belt_is_worn_down_but_never_below_the_floor()
    {
        var sim = NewGame();
        var w = sim.World;
        BeltNamed(sim, "scatter").Density = 40; // a thick clump, so the test sees many hits
        var f = Drifting(sim, 600, "cruiser", "escort");
        var cruiser = w.ShipsOf(f).First(s => s.ClassId == "cruiser");
        var escort = w.ShipsOf(f).First(s => s.ClassId == "escort");
        var max = Tech.MaxArmour(w, cruiser);
        sim.StepMany(100);
        Assert.True(cruiser.Armour < max * 0.9, $"cruiser hull {cruiser.Armour / max:P0}");
        Assert.True(cruiser.Armour >= max * Hazards.D.HullFloor - 1e-6);
        Assert.False(cruiser.Crippled);
        Assert.True(Economy.Of(w, Side.Player).Totals.RockHits > 0);
        // The escort takes far less (a quarter of the cruiser's cross-section).
        Assert.True(escort.Armour / Tech.MaxArmour(w, escort) > cruiser.Armour / max);
    }

    [Fact]
    public void A_fleet_holding_orbit_in_the_belt_takes_no_hits()
    {
        var sim = NewGame();
        var w = sim.World;
        BeltNamed(sim, "scatter").Density = 40;
        var kesh = BodyNamed(sim, "kesh");
        Assert.True(Hazards.RocksAt(w, kesh.Position).Belts > 0);
        var f = NewFleet(sim, Side.Player, kesh.Position, "cruiser");
        PlaceInOrbit(sim, f, kesh);
        sim.StepMany(100);
        Assert.Equal(0, Economy.Of(w, Side.Player).Totals.RockHits);
    }

    [Fact]
    public void Debris_shielding_and_rock_mapping_cut_the_harm()
    {
        var sim = NewGame();
        var w = sim.World;
        var capital = Economy.NewShip(w, Side.Player, "capital");
        var hits = Hazards.HitsPerHour(w, capital, 300, 1, 0, false);
        var share = Hazards.HitShare(w, Side.Player, 300);
        Economy.Of(w, Side.Player).Researched.Add(Hazards.RockMapping);
        Economy.Of(w, Side.Player).Researched.Add(Hazards.DebrisShielding);
        Assert.Equal(0.6, Hazards.HitsPerHour(w, capital, 300, 1, 0, false) / hits, 6);
        Assert.Equal(0.5, Hazards.HitShare(w, Side.Player, 300) / share, 6);
    }

    [Fact]
    public void The_route_estimate_warns_of_rocks_and_is_lower_when_slower()
    {
        var sim = NewGame();
        var w = sim.World;
        var verdant = BodyNamed(sim, "verdant");
        var tamber = BodyNamed(sim, "tamber");
        var f = NewFleet(sim, Side.Player, verdant.Position, "capital");
        PlaceInOrbit(sim, f, verdant);
        var fast = FlightPlanner.Plan(w, f, new FlightRequest(tamber.Id, tamber.Position, 1.0, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        var slow = FlightPlanner.Plan(w, f, new FlightRequest(tamber.Id, tamber.Position, 0.35, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        Assert.True(fast.Ok && slow.Ok);
        var a = Hazards.Estimate(w, f, fast.Flight!);
        var b = Hazards.Estimate(w, f, slow.Flight!);
        Assert.True(a.RockHours > 0, "the route to Tamber crosses the Scatter");
        Assert.True(b.HullShare < a.HullShare, $"slow {b.HullShare} v fast {a.HullShare}");
        Assert.Contains("rocks", Hazards.RouteWarning(w, f, fast.Flight!));
    }

    [Fact]
    public void Radiation_strains_crews_but_not_below_the_floor()
    {
        var sim = NewGame();
        var w = sim.World;
        var brim = BodyNamed(sim, "brim");
        var f = NewFleet(sim, Side.Player, brim.Position, "gun-frigate");
        PlaceInOrbit(sim, f, brim);
        var s = w.ShipsOf(f).Single();
        sim.StepMany(5 * 24);
        Assert.True(s.Crew < 0.97, $"crew {s.Crew}");
        Assert.True(s.Defence < 1);
        sim.StepMany(200 * 24);
        Assert.True(s.Crew >= Hazards.D.RadCrewFloor - 1e-9);
        Assert.True(s.Defence >= Hazards.D.RadWearFloor - 1e-9);
        Assert.True(Economy.Of(w, Side.Player).Totals.RadiationShipHours > 0);
    }

    [Fact]
    public void Ships_close_to_the_sun_run_hot()
    {
        var sim = NewGame();
        var w = sim.World;
        var sun = BodyNamed(sim, "hesper").Position;
        Assert.Equal(0, Hazards.SunHeat(w, BodyNamed(sim, "verdant").Position));
        Assert.True(Hazards.SunHeat(w, BodyNamed(sim, "cinder").Position) > 0);
        Assert.True(Hazards.SunHeat(w, sun + new Vec2(0.2 * Physics.MmPerAu, 0)) > Hazards.SunHeat(w, BodyNamed(sim, "cinder").Position));
        // Sensors looking straight at the sun are dazzled; looking away they are not.
        var from = sun + new Vec2(Physics.MmPerAu, 0);
        Assert.Equal(Hazards.D.SunDazzleFactor, Hazards.SensorFactor(w, Side.Player, from, sun + new Vec2(-0.5 * Physics.MmPerAu, 0)), 6);
        Assert.Equal(1, Hazards.SensorFactor(w, Side.Player, from, from + new Vec2(0, Physics.MmPerAu)), 6);
    }

    [Fact]
    public void A_big_battle_leaves_debris_that_thins_out()
    {
        var sim = NewGame();
        var w = sim.World;
        var where = BodyNamed(sim, "hesper").Position + new Vec2(0, 1.6 * Physics.MmPerAu);
        var b = new Battle { Id = 99, Where = where };
        b.For(Side.Player).ShipsLost.Add("X-1");
        b.For(Side.Rival).Crippled.Add("Y-1");
        b.For(Side.Rival).ShipsLost.Add("Y-2");
        Hazards.OnBattleOver(sim, b);
        var d = Assert.Single(w.Debris);
        Assert.True(Hazards.RocksAt(w, where).Debris > 0);
        sim.StepMany((int)(Hazards.D.DebrisDays * 24) + 2);
        Assert.Empty(w.Debris);
        // A small fight leaves none.
        var small = new Battle { Id = 100, Where = where };
        small.For(Side.Player).ShipsLost.Add("X-2");
        Hazards.OnBattleOver(sim, small);
        Assert.Empty(w.Debris);
        Assert.Null(d.BodyId); // fought in open space: the cloud stays put
    }

    [Fact]
    public void Hazards_can_be_switched_off()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Hazards = false, Events = EventRate.Off });
        var w = sim.World;
        Assert.False(Hazards.On(w));
        Assert.Equal(0, Hazards.SunHeat(w, BodyNamed(sim, "cinder").Position));
    }

    // ---- Events ---------------------------------------------------------------------------------

    [Fact]
    public void An_event_waits_for_the_player_then_auto_picks_at_the_deadline()
    {
        var sim = NewGame();
        var w = sim.World;
        var e = Events.Spawn(sim, Side.Player, "comet")!;
        Assert.NotNull(e);
        Assert.True(e.Call.Open);
        Assert.True(e.Call.DueHour > w.Hour);
        sim.StepMany(Stewards.EveryTicks);
        Assert.Contains(Economy.Of(w, Side.Player).Needs, n => n.Key == $"event:{e.Id}" && n.Act is PickEventCommand);
        sim.StepMany((int)Math.Ceiling(e.Call.DueHour - w.Hour) + 1);
        Assert.False(e.Call.Open);
        Assert.True(e.Call.ByAuto);
    }

    [Fact]
    public void The_rival_picks_at_once()
    {
        var sim = NewGame();
        var w = sim.World;
        Mind.Setup(w, GameOptions.VsRival(Difficulty.Normal), 8);
        var e = Events.Spawn(sim, Side.Rival, "comet")!;
        Assert.False(e.Call.Open);
    }

    [Fact]
    public void A_comet_mined_fills_the_store_with_water()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Industry = 100;
        var e = Events.Spawn(sim, Side.Player, "comet")!;
        sim.Issue(new PickEventCommand(Side.Player, e.Id, 0));
        sim.Step();
        Assert.Equal(0, e.Call.Picked);
        var b = w.FindBody(e.BodyId!.Value)!;
        sim.StepMany((int)Math.Ceiling(e.HitsHour - w.Hour) + 1);
        Assert.True(e.Hit);
        var water = Colonies.Held(b, "water");
        sim.StepMany(48);
        Assert.True(Colonies.Held(b, "water") > water);
    }

    [Fact]
    public void A_reactor_fault_cuts_power_until_fixed()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.ShipyardOf(w, Side.Player)!;
        if (!Colonies.Built(home).Any(p => Planets.BuildingKind(p.Kind) is { Power: < 0 }))
        {
            var data = Planets.BuildingKind("solar-field")!;
            var (x, y, turned) = Colonies.BestSpot(home, data)!.Value;
            Colonies.AddPiece(w, home, data, x, y, turned, built: true);
            Colonies.SyncBuildings(home);
        }
        sim.StepMany(2);
        var e = Events.Spawn(sim, Side.Player, "reactor-fault")!;
        var body = w.FindBody(e.BodyId!.Value)!;
        Assert.True(Events.PowerCut(w, body) > 0);
        body.Stock[Colonies.Spares] = 50;
        sim.Issue(new PickEventCommand(Side.Player, e.Id, 0));
        sim.Step();
        Assert.Equal(30, Colonies.Held(body, Colonies.Spares), 0);
        sim.StepMany((int)(Hazards.D.Events.FaultFastDays * 24) + 2);
        Assert.Equal(0, Events.PowerCut(w, body));
        Assert.True(e.Done);
    }

    [Fact]
    public void A_rock_strike_left_to_hit_destroys_a_building_and_evacuating_saves_the_people()
    {
        var sim = Starts.Classic(NewGame());
        var w = sim.World;
        var e = Events.Spawn(sim, Side.Player, "rock-strike")!;
        var body = w.FindBody(e.BodyId!.Value)!;
        var pieces = Colonies.Built(body).Count();
        var people = body.PopK;
        sim.Issue(new PickEventCommand(Side.Player, e.Id, 1));
        sim.StepMany((int)Math.Ceiling(e.HitsHour - w.Hour) + 1);
        Assert.True(e.Hit);
        Assert.Equal(pieces - 1, Colonies.Built(body).Count());
        Assert.True(body.PopK >= people * 0.95, "evacuated: nobody lost to the rock");
        if (body.Shipyard) Assert.Contains(Colonies.Built(body), p => p.Kind == Colonies.Shipyard);
    }

    [Fact]
    public void People_over_the_cap_after_a_lost_building_move_to_another_world()
    {
        var sim = Starts.Classic(NewGame());
        var w = sim.World;
        var home = w.Bodies.First(b => b.Owner == Side.Player && b.Home);
        var other = w.Bodies.First(b => b.Owner == null && b.Kind == BodyKind.Planet && Planets.Cap(b) > 2);
        other.Owner = Side.Player;
        other.PopK = 1;
        home.PopK = Planets.Cap(home) + 0.5;
        var (moved, lost) = Planets.Overflow(w, home);
        Assert.Equal(Planets.Cap(home), home.PopK, 9);
        Assert.Equal(0.5, moved, 9);
        Assert.Equal(0, lost, 9);
        Assert.Equal(1.5, other.PopK, 9);
    }

    [Fact]
    public void Pushing_a_rock_costs_industry_and_saves_the_colony()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Industry = 100;
        var e = Events.Spawn(sim, Side.Player, "rock-strike")!;
        var body = w.FindBody(e.BodyId!.Value)!;
        var pieces = Colonies.Built(body).Count();
        sim.Issue(new PickEventCommand(Side.Player, e.Id, 0));
        sim.Step();
        Assert.True(Economy.Of(w, Side.Player).Industry < 100);
        sim.StepMany((int)Math.Ceiling(e.HitsHour - w.Hour) + 1);
        Assert.Equal(pieces, Colonies.Built(body).Count());
    }

    [Fact]
    public void A_flare_reaches_both_sides_and_strains_crews_out_in_space()
    {
        var sim = NewGame();
        var w = sim.World;
        var f = Drifting(sim, 0, "gun-frigate");
        var s = w.ShipsOf(f).Single();
        var e = Events.Spawn(sim, Side.Player, "flare")!;
        Assert.Contains(w.Events, x => x.Kind == "flare" && x.Side == Side.Rival);
        sim.Issue(new PickEventCommand(Side.Player, e.Id, 1)); // ride it out
        sim.StepMany((int)Math.Ceiling(e.HitsHour - w.Hour) + 1);
        Assert.True(s.Crew < 1);
        Assert.True(w.Hour < w.FlareUntilHour);
        Assert.True(Events.BlindFactor(w, Side.Rival) < 1, "the flare dazzles everyone's sensors");
        sim.StepMany((int)Hazards.D.Events.FlareHours + 2);
        Assert.Equal(1, Events.BlindFactor(w, Side.Player));
    }

    [Fact]
    public void A_defector_raises_the_intel_level()
    {
        var sim = NewGame();
        var w = sim.World;
        var file = Intel.Of(w, Side.Player);
        file.LastContactHour = w.Hour + 1;
        var before = Intel.Level(file);
        var e = Events.Spawn(sim, Side.Player, "defector")!;
        sim.Issue(new PickEventCommand(Side.Player, e.Id, 0));
        sim.Step();
        Assert.True(Intel.Level(file) > before);
    }

    [Fact]
    public void A_salvaged_derelict_brings_materials_and_a_spark()
    {
        var sim = NewGame();
        var w = sim.World;
        Drifting(sim, 0, "gun-frigate");
        var e = Events.Spawn(sim, Side.Player, "derelict")!;
        var home = Economy.ShipyardOf(w, Side.Player)!;
        var alloys = Colonies.Held(home, "alloys");
        var sparked = Economy.Of(w, Side.Player).Sparked.Count;
        sim.Issue(new PickEventCommand(Side.Player, e.Id, 0));
        sim.StepMany((int)Math.Ceiling(e.HitsHour - w.Hour) + 2);
        Assert.True(e.Hit);
        Assert.True(Colonies.Held(home, "alloys") > alloys);
        Assert.True(Economy.Of(w, Side.Player).Sparked.Count > sparked);
    }

    [Fact]
    public void Events_come_at_the_rate_set_and_none_when_off()
    {
        var off = NewGame(5, EventRate.Off);
        off.StepMany(120 * 24);
        Assert.Empty(off.World.Events);
        var lively = NewGame(5, EventRate.Lively);
        lively.StepMany(120 * 24);
        Assert.True(Economy.Of(lively.World, Side.Player).Totals.Events >= 3);
        Assert.True(Economy.Of(lively.World, Side.Rival).Totals.Events >= 3);
    }

    [Fact]
    public void A_game_with_hazards_and_events_survives_a_save_and_replays_the_same()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 21, GameLog.Memory(), options: GameOptions.AiVsAi() with { Events = EventRate.Lively });
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.StepMany(90 * 24);
        Assert.NotEmpty(sim.World.Events);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        for (var i = 0; i < 72; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }
}
