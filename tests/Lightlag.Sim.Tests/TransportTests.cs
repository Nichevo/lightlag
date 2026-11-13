using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Transport and orbit (Plan 3 build 4): sails and laser stations, satellites, and the lift to orbit.</summary>
public class TransportTests
{
    private static Simulation NewGame(ulong seed = 8, LightSetup? light = null, List<AiSetup>? ai = null)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: new GameOptions(Ai: ai, Light: light));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        Planets.EnsureStarted(sim.World);
        return sim;
    }

    private static SideState Player(Simulation sim) => Economy.Of(sim.World, Side.Player);
    private static Body Named(Simulation sim, string key) => sim.World.Bodies.First(b => b.Key == key);
    private static void Hours(Simulation sim, double hours) => sim.StepMany((int)Math.Round(hours));
    private static void Days(Simulation sim, double days) => Hours(sim, days * 24);

    private static Fleet FleetOf(Simulation sim, Side side, string cls, Vec2 at, Body? orbit = null)
    {
        var ship = Economy.NewShip(sim.World, side, cls);
        var f = Fleets.Create(sim.World, side, new List<Ship> { ship }, at);
        if (orbit != null) Fleets.EnterOrbit(f, orbit);
        ship.Position = f.Position + ship.FormationOffset;
        return f;
    }

    private static Station Station(Simulation sim, Side side, string kind, Body b)
    {
        var st = new Station { Id = sim.World.NextId++, Side = side, Kind = kind, BodyId = b.Id, ReadyHour = sim.World.Hour };
        sim.World.Stations.Add(st);
        return st;
    }

    // ---- Sails ----

    [Fact]
    public void Sail_classes_need_solar_sails_and_the_ladder_ideas_are_open()
    {
        foreach (var id in new[] { "sail-courier", "sail-freighter" })
        {
            var c = GameData.ShipClass(id);
            Assert.True(c.Sail);
            Assert.Equal("solar-sails", c.Needs);
        }
        foreach (var id in new[] { "solar-sails", "laser-sails", "satellites", "skyhook", "space-elevator" })
            Assert.False(GameData.Research(id).Later, id);
        Assert.NotNull(Economy.StationKind(Sails.LaserStation));
        Assert.Equal(0.3, Economy.StationKind("skyhook")!.LiftCut, 6);
        Assert.Equal(0.1, Economy.StationKind("space-elevator")!.LiftCut, 6);
        Assert.Equal(2, Satellites.Kinds.Count);
    }

    [Fact]
    public void Sunlight_falls_with_the_square_of_distance_and_is_gone_in_a_shadow()
    {
        var sim = NewGame();
        var w = sim.World;
        var au = Physics.MmPerAu;
        Assert.Equal(1.0, Sails.Sunlight(w, new Vec2(0, au * 1.0) + new Vec2(au * 0.0001, 0)), 2);
        var far = Sails.Sunlight(w, new Vec2(0, -2 * au));
        Assert.Equal(0.25, far, 2);
        // Close in, it is capped.
        Assert.Equal(Sails.D.MaxLight, Sails.Sunlight(w, new Vec2(0.1 * au, 0)), 6);
        // Right behind a planet (on the far side from the star) there is no sunlight.
        var tamber = Named(sim, "tamber");
        var behind = tamber.Position + tamber.Position.Normalized() * (tamber.Radius * 3);
        Assert.Equal(0, Sails.Sunlight(w, behind));
        // ...unless that planet is the one left out (a ship in its orbit is always round the corner).
        Assert.True(Sails.Sunlight(w, behind, tamber.Id) > 0);
    }

    [Fact]
    public void A_sail_ship_accelerates_with_the_light_burns_no_fuel_and_makes_no_burn_heat()
    {
        var sim = NewGame();
        var w = sim.World;
        Player(sim).Researched.Add("solar-sails");
        var au = Physics.MmPerAu;
        var near = FleetOf(sim, Side.Player, "sail-freighter", new Vec2(0, -1.0 * au));
        var far = FleetOf(sim, Side.Player, "sail-freighter", new Vec2(0, 2.0 * au));
        var ship = w.ShipsOf(near).Single();
        var a1 = FleetPerformance.Of(w, near).Accel;
        var a2 = FleetPerformance.Of(w, far).Accel;
        Assert.Equal(ship.Accel * Sails.PushOn(w, near), a1, 6);
        Assert.Equal(a1 / 4, a2, 2);
        Assert.Equal(0, Fuels.BurnHeat(w, ship));
        Assert.Equal(1, Fuels.Thrust(w, ship));
        // A long trip barely touches its tanks.
        var before = ship.Supplies;
        sim.BurnFuel(near, 300);
        Assert.True(before - ship.Supplies < 0.2);
    }

    [Fact]
    public void A_fleet_under_sail_shows_like_a_cold_one()
    {
        var sim = NewGame();
        var w = sim.World;
        Player(sim).Researched.Add("solar-sails");
        var f = FleetOf(sim, Side.Player, "sail-courier", new Vec2(0, -Physics.MmPerAu));
        var cold = Sensors.Signature(w, f);
        f.Thrusting = true;
        Assert.Equal(cold, Sensors.Signature(w, f), 6);
        // A frigate burning beside it shows far brighter.
        var frigate = FleetOf(sim, Side.Player, "gun-frigate", new Vec2(0, -Physics.MmPerAu));
        var frigateCold = Sensors.Signature(w, frigate);
        frigate.Thrusting = true;
        Assert.True(Sensors.Signature(w, frigate) > frigateCold * 5);
    }

    [Fact]
    public void Sails_fly_in_the_inner_system_but_need_a_laser_station_among_the_giants()
    {
        var sim = NewGame();
        var w = sim.World;
        Player(sim).Researched.Add("solar-sails");
        var verdant = Named(sim, "verdant");
        var rust = Named(sim, "rust");
        var tamber = Named(sim, "tamber");
        var inner = FleetOf(sim, Side.Player, "sail-freighter", verdant.Position + new Vec2(Fleets.OrbitRadiusFor(verdant), 0), verdant);
        var ok = FlightPlanner.Plan(w, inner, new FlightRequest(rust.Id, Vec2.Zero, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        Assert.True(ok.Ok, ok.Problem);
        // Slower than a freighter on the same trip.
        var freighter = FleetOf(sim, Side.Player, "freighter", verdant.Position + new Vec2(Fleets.OrbitRadiusFor(verdant), 0), verdant);
        var fast = FlightPlanner.Plan(w, freighter, new FlightRequest(rust.Id, Vec2.Zero, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        Assert.True(ok.Flight!.ArriveHour > fast.Flight!.ArriveHour);
        // No slingshots under sail.
        var sling = FlightPlanner.Plan(w, inner, new FlightRequest(rust.Id, Vec2.Zero, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick, RouteKind.Slingshot));
        Assert.False(sling.Ok);
        Assert.Contains("slingshot", sling.Problem);

        // Out at Tamber the sun is too weak for a trip out to Coldwell...
        var outer = FleetOf(sim, Side.Player, "sail-freighter", tamber.Position + new Vec2(Fleets.OrbitRadiusFor(tamber), 0), tamber);
        var coldwell = Named(sim, "coldwell");
        var dark = FlightPlanner.Plan(w, outer, new FlightRequest(coldwell.Id, Vec2.Zero, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        Assert.False(dark.Ok);
        Assert.Contains("Laser sails", dark.Problem);
        // ...until a laser station over one of Tamber's moons pushes it.
        Player(sim).Researched.Add("laser-sails");
        var brim = Named(sim, "brim");
        brim.Owner = Side.Player;
        Station(sim, Side.Player, Sails.LaserStation, brim);
        Assert.True(Sails.PushOn(w, outer) > Sails.D.LaserPush * 0.99);
        var lit = FlightPlanner.Plan(w, outer, new FlightRequest(coldwell.Id, Vec2.Zero, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        Assert.True(lit.Ok, lit.Problem);
        Assert.Contains("laser from Brim", Sails.Status(w, outer));
    }

    [Fact]
    public void A_sail_freighter_carries_goods_between_the_inner_worlds()
    {
        var sim = NewGame();
        var w = sim.World;
        Player(sim).Researched.Add("solar-sails");
        var verdant = Named(sim, "verdant");
        var pale = Named(sim, "pale");
        Assert.Equal(Side.Player, pale.Owner);
        pale.Stock["iron"] = 60;
        var f = FleetOf(sim, Side.Player, "sail-freighter", pale.Position + new Vec2(Fleets.OrbitRadiusFor(pale), 0), pale);
        Player(sim).AutoTransport = false;
        sim.Issue(new FixedRunCommand(Side.Player, pale.Id, verdant.Id, "iron", 30));
        var start = Colonies.Held(verdant, "iron");
        for (var day = 0; day < 60 && Colonies.Held(verdant, "iron") < start + 1; day++) Days(sim, 1);
        Assert.True(Colonies.Held(verdant, "iron") >= start + 1);
        Assert.True(w.ShipsOf(f).Single().Supplies > 10);
    }

    // ---- Satellites ----

    [Fact]
    public void Satellites_need_their_research_and_pay_their_lift()
    {
        var sim = NewGame();
        var w = sim.World;
        var verdant = Named(sim, "verdant");
        Assert.Contains("Research", Satellites.CanLaunch(w, Side.Player, "sensor-sat", verdant.Id, SatSpot.Leading));
        Player(sim).Researched.Add("satellites");
        Assert.Null(Satellites.CanLaunch(w, Side.Player, "sensor-sat", verdant.Id, SatSpot.Leading));
        Assert.Contains("light lag", Satellites.CanLaunch(w, Side.Player, "relay-sat", verdant.Id, SatSpot.Orbit));
        Assert.Contains("leading", Satellites.CanLaunch(w, Side.Player, "sensor-sat", Sails.Star(w)!.Id, SatSpot.Leading));
        var industry = Player(sim).Industry = 500;
        var kind = Satellites.KindOf("sensor-sat")!;
        var from = Satellites.LaunchSite(w, Side.Player, verdant.Position)!;
        var cost = Satellites.CostOf(w, kind, from);
        Assert.True(cost > kind.Cost);
        sim.Issue(new LaunchSatelliteCommand(Side.Player, "sensor-sat", verdant.Id, SatSpot.Leading));
        sim.ApplyPending();
        Assert.Equal(industry - cost, Player(sim).Industry, 6);
        Assert.Equal(cost - kind.Cost, Player(sim).Totals.LiftSpent, 6);
        var sat = Assert.Single(w.Satellites);
        Assert.False(sat.Arrived);
        // A second one to the same spot is turned down.
        Assert.Contains("already", Satellites.CanLaunch(w, Side.Player, "sensor-sat", verdant.Id, SatSpot.Leading));
    }

    [Fact]
    public void A_satellite_flies_to_a_planets_leading_point_and_stays_sixty_degrees_ahead()
    {
        var sim = NewGame();
        var w = sim.World;
        Player(sim).Researched.Add("satellites");
        Player(sim).Industry = 500;
        var verdant = Named(sim, "verdant");
        sim.Issue(new LaunchSatelliteCommand(Side.Player, "sensor-sat", verdant.Id, SatSpot.Leading));
        sim.ApplyPending();
        Assert.Single(w.Satellites);
        for (var day = 0; day < 60 && !w.Satellites[0].Arrived; day++) Days(sim, 1);
        var sat = w.Satellites.Single();
        Assert.True(sat.Arrived);
        for (var i = 0; i < 2; i++)
        {
            var p = Satellites.PositionOf(w, sat);
            var star = Sails.Star(w)!.Position;
            var a = verdant.Position - star;
            var b = p - star;
            // Same distance from the star, 60 degrees ahead (cos 0.5, and the cross product positive: anticlockwise).
            Assert.Equal(a.Length, b.Length, 3);
            Assert.Equal(0.5, Vec2.Dot(a, b) / (a.Length * b.Length), 6);
            Assert.True(a.X * b.Y - a.Y * b.X > 0);
            Days(sim, 20);
        }
    }

    [Fact]
    public void A_sensor_satellite_sees_an_enemy_nothing_else_sees()
    {
        var sim = NewGame();
        var w = sim.World;
        Player(sim).Researched.Add("satellites");
        Player(sim).Industry = 500;
        var tamber = Named(sim, "tamber");
        Assert.NotEqual(Side.Player, tamber.Owner);
        var enemy = FleetOf(sim, Side.Rival, "gun-frigate", tamber.Position + new Vec2(Fleets.OrbitRadiusFor(tamber) * 6, 0));
        enemy.Thrusting = true; // burning: easy to see, but nothing of ours is near
        Sensors.Update(sim);
        Assert.False(Sensors.Sees(w, Side.Player, enemy));
        var sat = new Satellite { Id = w.NextId++, Side = Side.Player, Kind = "sensor-sat", BodyId = tamber.Id, Spot = SatSpot.Orbit, Arrived = true, Name = "Sensor sat 1" };
        w.Satellites.Add(sat);
        sat.Position = Satellites.PositionOf(w, sat);
        Sensors.Update(sim);
        Assert.True(Sensors.Sees(w, Side.Player, enemy));
    }

    [Fact]
    public void An_armed_enemy_fleet_close_by_shoots_a_satellite_down_and_the_owner_hears_why()
    {
        var sim = NewGame();
        var w = sim.World;
        var rust = Named(sim, "rust");
        var sat = new Satellite { Id = w.NextId++, Side = Side.Player, Kind = "sensor-sat", BodyId = rust.Id, Spot = SatSpot.Orbit, Arrived = true, Name = "Sensor sat 1" };
        w.Satellites.Add(sat);
        sat.Position = Satellites.PositionOf(w, sat);
        var enemy = FleetOf(sim, Side.Rival, "gun-frigate", sat.Position + new Vec2(5, 0));
        for (var h = 0; h < 72 && sat.LostHour == null; h++)
        {
            enemy.Position = Satellites.PositionOf(w, sat) + new Vec2(5, 0);
            foreach (var s in w.ShipsOf(enemy)) s.Position = enemy.Position;
            Hours(sim, 1);
        }
        Assert.NotNull(sat.LostHour);
        Assert.StartsWith("Rival fleet", sat.LostTo);
        Assert.Contains("LOST", Satellites.Describe(w, sat));
        Assert.Empty(Satellites.Working(w, Side.Player));
        // Forgotten after a while.
        Days(sim, Satellites.D.KeepLostDays + 1);
        Assert.Empty(w.Satellites);
    }

    [Fact]
    public void A_relay_satellite_carries_orders_round_a_planet_in_the_way()
    {
        var sim = NewGame(light: new LightSetup(true, 1));
        var w = sim.World;
        var tamber = Named(sim, "tamber");
        var seat = Light.Seat(w, Side.Player);
        // A point straight behind Tamber as seen from the flagship: the planet blocks the signal.
        var behind = tamber.Position + (tamber.Position - seat).Normalized() * (tamber.Radius * 4);
        Assert.False(Light.SignalClear(w, Side.Player, behind));
        // A relay satellite at Tamber's leading point sees both ends.
        var sat = new Satellite { Id = w.NextId++, Side = Side.Player, Kind = "relay-sat", BodyId = tamber.Id, Spot = SatSpot.Leading, Arrived = true, Name = "Relay sat 1" };
        w.Satellites.Add(sat);
        sat.Position = Satellites.PositionOf(w, sat);
        Assert.True(Light.SignalClear(w, Side.Player, behind));
    }

    // ---- Lift to orbit ----

    [Fact]
    public void The_lift_follows_the_gravity_well()
    {
        var sim = NewGame();
        var verdant = Named(sim, "verdant");
        var pale = Named(sim, "pale");
        var kesh = Named(sim, "kesh");
        Assert.Equal(1.0, Lift.Well(verdant), 1);
        Assert.InRange(Lift.Well(pale), 0.02, 0.1);
        Assert.True(Lift.Well(kesh) < 0.01);
        Assert.True(Lift.CostPerGood(sim.World, verdant) > 10 * Lift.CostPerGood(sim.World, pale));
        Assert.Contains("Lift to orbit", Lift.Describe(sim.World, verdant));
    }

    [Fact]
    public void Loading_a_freighter_pays_the_lift_and_the_ladder_cuts_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var verdant = Named(sim, "verdant");
        Player(sim).Industry = 1000;
        var full = Lift.CostPerGood(w, verdant);
        Assert.Equal(Lift.D.IndustryPerGood * Lift.Well(verdant), full, 9);
        var paid = Lift.Pay(w, Side.Player, verdant, 100);
        Assert.Equal(full * 100, paid, 6);
        Assert.Equal(1000 - paid, Player(sim).Industry, 6);
        Assert.Equal(paid, Player(sim).Totals.LiftSpent, 6);

        // A skyhook over it cuts the lift by 70 %, a space elevator by 90 %; the best counts.
        Station(sim, Side.Player, "skyhook", verdant);
        Assert.Equal(full * 0.3, Lift.CostPerGood(w, verdant), 9);
        Station(sim, Side.Player, "space-elevator", verdant);
        Assert.Equal(full * 0.1, Lift.CostPerGood(w, verdant), 9);
        Assert.Contains("space elevator", Lift.Describe(w, verdant));

        // Never more than the side has.
        Player(sim).Industry = 0.001;
        Lift.Pay(w, Side.Player, verdant, 1e6);
        Assert.True(Player(sim).Industry >= 0);
    }

    [Fact]
    public void Ladder_stations_need_ground_and_research()
    {
        var sim = NewGame();
        var w = sim.World;
        Player(sim).Industry = 1000;
        var verdant = Named(sim, "verdant");
        Assert.Contains("Research", Economy.CanBuildStation(w, Side.Player, verdant.Id, "skyhook"));
        Player(sim).Researched.Add("skyhook");
        Assert.Null(Economy.CanBuildStation(w, Side.Player, verdant.Id, "skyhook"));
        var tamber = Named(sim, "tamber");
        tamber.Owner = Side.Player;
        Assert.Contains("ground", Economy.CanBuildStation(w, Side.Player, tamber.Id, "skyhook"));
    }

    // ---- The rival ----

    [Fact]
    public void A_rival_with_the_research_launches_satellites_and_builds_its_ladder()
    {
        var sim = NewGame(seed: 3, ai: new List<AiSetup> { new(Side.Rival, Difficulty.Normal) });
        var rival = Economy.Of(sim.World, Side.Rival);
        rival.Researched.AddRange(new[] { "long-sensors", "satellites", "mass-driver", "skyhook" });
        rival.Industry = 3000;
        Days(sim, 20);
        Assert.Contains(sim.World.Satellites, s => s.Side == Side.Rival);
        Assert.Contains(sim.World.Stations, st => st.Side == Side.Rival && st.Kind == "skyhook");
    }
}
