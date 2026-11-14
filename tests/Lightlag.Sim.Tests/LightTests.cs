using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

public class LightTests
{
    private static Simulation NewGame(double speedFactor = 1.0, ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(),
            options: new GameOptions(Light: new LightSetup(true, speedFactor)));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    /// <summary>Splits one frigate off the player's home fleet and sends it to Tamber, then lets it arrive.</summary>
    private static Fleet FrigateAtTamber(Simulation sim)
    {
        var w = sim.World;
        var home = w.Fleets.First(f => f.Side == Side.Player);
        var frigate = w.ShipsOf(home).First(s => s.ClassId == "gun-frigate");
        sim.Issue(new SplitFleetCommand(home.Id, new List<int> { frigate.Id }));
        sim.Step();
        var lone = w.FleetOf(frigate)!;
        var tamber = BodyNamed(sim, "tamber");
        sim.Issue(new PlanFlightCommand(lone.Id, new FlightRequest(tamber.Id, tamber.Position, 1, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        var end = w.Tick + 600 * GameTime.TicksPerDay;
        sim.Step();
        while (lone.InFlight && w.Tick < end) sim.Step();
        Assert.False(lone.InFlight);
        return lone;
    }

    [Fact]
    public void Each_side_starts_with_a_destroyer_as_flagship_at_home()
    {
        var sim = NewGame();
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var flag = Light.Flagship(sim.World, side);
            Assert.NotNull(flag);
            Assert.Equal("missile-destroyer", flag!.ClassId);
            Assert.Equal(0, Light.DelayHours(sim.World, side, sim.World.FleetOf(flag)!.Position));
        }
    }

    [Fact]
    public void An_order_to_a_far_fleet_arrives_after_its_light_travel_time()
    {
        var sim = NewGame();
        var w = sim.World;
        var lone = FrigateAtTamber(sim);
        var delay = Light.DelayHours(w, Side.Player, lone.Position);
        Assert.InRange(delay, 0.5, 1.0); // Tamber is 35 to 52 light-minutes from Verdant

        var verdant = BodyNamed(sim, "verdant");
        sim.Issue(new PlanFlightCommand(lone.Id, new FlightRequest(verdant.Id, verdant.Position, 0.8, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        sim.Step();
        Assert.Single(w.Light.InTransit);
        Assert.False(lone.InFlight);
        var arrive = w.Light.InTransit[0].ArriveHour;
        while (w.Hour < arrive - 1e-9) { sim.Step(); Assert.False(lone.InFlight); }
        sim.Step();
        Assert.Empty(w.Light.InTransit);
        Assert.True(lone.InFlight);
    }

    [Fact]
    public void Slower_light_makes_orders_take_ten_times_as_long()
    {
        var real = NewGame(1.0);
        var slow = NewGame(0.1);
        var at = BodyNamed(real, "tamber").Position;
        Assert.Equal(10 * Light.DelayHours(real.World, Side.Player, at), Light.DelayHours(slow.World, Side.Player, at), 6);
    }

    [Fact]
    public void Light_lag_off_gives_no_delay()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        Assert.False(sim.World.Light.On);
        Assert.Equal(0, Light.DelayHours(sim.World, Side.Player, BodyNamed(sim, "tamber").Position));
    }

    [Fact]
    public void Orders_in_transit_survive_a_save_and_load()
    {
        var sim = NewGame();
        var w = sim.World;
        var lone = FrigateAtTamber(sim);
        sim.Issue(new SetHoldCommand(lone.Id, HoldStyle.Drift));
        sim.Step();
        Assert.Single(w.Light.InTransit);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Single(loaded.World.Light.InTransit);
        Assert.IsType<SetHoldCommand>(loaded.World.Light.InTransit[0].Command);
        for (var i = 0; i < 3; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void Command_hears_of_a_far_fleet_only_when_its_light_arrives()
    {
        var sim = NewGame();
        var w = sim.World;
        var lone = FrigateAtTamber(sim);
        var report = Light.Known(w, Side.Player, lone.Id);
        Assert.NotNull(report);
        Assert.True(report!.Own);
        var delay = Light.DelayHours(w, Side.Player, lone.Position);
        // The newest news of it is about as old as its light takes to come home.
        Assert.InRange(Light.AgeHours(w, Side.Player, lone.Id), delay - 0.15, delay + 1.05);
        // A fleet next to the flagship is live.
        var home = w.Fleets.First(f => f.Side == Side.Player && f.Id != lone.Id);
        Assert.InRange(Light.AgeHours(w, Side.Player, home.Id), 0, 1e-9);
    }

    [Fact]
    public void A_game_with_light_lag_plays_by_the_rules_and_replays_exactly()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 21, GameLog.Memory(),
            options: new GameOptions(GameOptions.AiVsAi().Ai, new LightSetup(true, 0.1)));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.StepMany(120 * GameTime.TicksPerDay);
        Assert.NotEmpty(sim.World.Picture);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        for (var i = 0; i < 48; i++) { sim.Step(); loaded.Step(); }
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void A_body_between_flagship_and_fleet_blocks_its_news_until_it_clears()
    {
        var sim = NewGame();
        var w = sim.World;
        var lone = FrigateAtTamber(sim);
        var tamber = BodyNamed(sim, "tamber");
        // Step on until the frigate, orbiting Tamber, is on the far side of it from the flagship.
        var end = w.Tick + 60 * GameTime.TicksPerDay;
        while (Light.SignalClear(w, Side.Player, lone.Position) && w.Tick < end) sim.Step();
        Assert.False(Light.SignalClear(w, Side.Player, lone.Position));
        var before = Light.Known(w, Side.Player, lone.Id)!.Hour;
        sim.Step();
        sim.Step();
        Assert.Equal(before, Light.Known(w, Side.Player, lone.Id)!.Hour, 6);
        // An order to it waits too.
        sim.Issue(new SetHoldCommand(lone.Id, HoldStyle.Drift));
        sim.Step();
        while (!Light.SignalClear(w, Side.Player, lone.Position) && w.Tick < end) { sim.Step(); Assert.Equal(HoldStyle.Orbit, lone.Hold); }
        for (var i = 0; i < 3; i++) sim.Step();
        Assert.Equal(HoldStyle.Drift, lone.Hold);
        Assert.True(Light.Known(w, Side.Player, lone.Id)!.Hour > before);
    }

    [Fact]
    public void A_relay_gets_orders_round_a_planet_in_the_way()
    {
        var sim = NewGame();
        var w = sim.World;
        var lone = FrigateAtTamber(sim);
        var tamber = BodyNamed(sim, "tamber");
        // Wait until Tamber blocks the line and one of its moons is in sight of both the flagship and the frigate.
        Body? Corner()
        {
            var seat = Light.Seat(w, Side.Player);
            return w.Bodies.Where(b => b.ParentId == tamber.Id)
                .FirstOrDefault(b => Sensors.LineOfSight(w, seat, b.Position, b.Id) && Sensors.LineOfSight(w, b.Position, lone.Position, b.Id));
        }
        var end = w.Tick + 90 * GameTime.TicksPerDay;
        while ((Light.SignalClear(w, Side.Player, lone.Position) || Corner() == null) && w.Tick < end) sim.Step();
        Assert.False(Light.SignalClear(w, Side.Player, lone.Position));
        var direct = Light.DelayHours(w, Side.Player, lone.Position);
        var moon = Corner();
        Assert.NotNull(moon);
        moon!.Owner = Side.Player;
        var relay = new Station { Id = w.NextId++, Side = Side.Player, Kind = Economy.Relay, BodyId = moon.Id, ReadyHour = w.Hour + 1 };
        w.Stations.Add(relay);
        Assert.False(Light.SignalClear(w, Side.Player, lone.Position)); // not built yet
        relay.ReadyHour = w.Hour;
        Assert.True(Light.SignalClear(w, Side.Player, lone.Position));
        Assert.True(Light.DelayHours(w, Side.Player, lone.Position) > direct);
    }

    [Fact]
    public void Command_research_widens_the_command_range()
    {
        var sim = NewGame();
        var w = sim.World;
        var basic = Light.CommandRangeMm(w, Side.Player);
        Economy.Of(w, Side.Player).Researched.Add(Tech.CommandUplink);
        Assert.Equal(basic * 1.5, Light.CommandRangeMm(w, Side.Player), 6);
        Economy.Of(w, Side.Player).Researched.Add(Tech.TightBeam);
        Assert.InRange(Light.CommandRangeMm(w, Side.Player) / basic, 1.95, 2.05);
    }

    [Fact]
    public void A_sensor_station_lets_a_held_moon_see_as_far_as_home()
    {
        var sim = NewGame();
        var w = sim.World;
        w.Rules.Budget = false; // one pool: the station is paid from all of it
        var pale = BodyNamed(sim, "pale");
        pale.Owner = Side.Player;
        var bare = Sensors.PlanetSensorOf(w, Side.Player, pale);
        var before = Economy.Of(w, Side.Player).Industry;
        // A sensor station is now a watch bay on the station line.
        Assert.Contains("watch bay", Economy.CanBuildStation(w, Side.Player, pale.Id, Economy.SensorStation));
        sim.Issue(new BuildStarbaseCommand(Side.Player, pale.Id, "station-platform"));
        sim.Step();
        var st = Assert.Single(w.Starbases);
        st.Fit[2] = "watch-bay";
        Assert.True(Economy.Of(w, Side.Player).Industry < before);
        Assert.Equal(bare, Sensors.PlanetSensorOf(w, Side.Player, pale));
        sim.StepMany(6 * GameTime.TicksPerDay);
        Assert.Equal(GameData.Sensors.PlanetSensorMm, Sensors.PlanetSensorOf(w, Side.Player, pale), 6);
        Assert.True(Sensors.PlanetSensorOf(w, Side.Player, pale) > bare);
        // Lost with the moon.
        pale.Owner = Side.Rival;
        sim.StepMany(1);
        Assert.Empty(w.Starbases);
    }

    [Fact]
    public void The_rival_lives_with_the_same_lag()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 21, GameLog.Memory(),
            options: new GameOptions(GameOptions.AiVsAi().Ai, new LightSetup(true, 0.1)));
        var sent = false;
        var end = 120 * GameTime.TicksPerDay;
        while (!sent && sim.World.Tick < end)
        {
            sim.Step();
            sent = sim.World.Light.InTransit.Any(t => t.Side == Side.Rival);
        }
        Assert.True(sent);
    }

    private static Simulation Decap(bool on)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(),
            options: new GameOptions(Light: new LightSetup(true, 1.0), Decapitation: on));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    [Fact]
    public void Losing_the_flagship_with_no_destroyer_left_loses_the_game()
    {
        var sim = Decap(true);
        Combat.Destroy(sim, Light.Flagship(sim.World, Side.Player)!);
        Assert.Equal(Side.Rival, sim.World.Winner);
        Assert.Contains("decapitation", sim.World.WinReason);
        sim.Step();
    }

    [Fact]
    public void Without_decapitation_a_frigate_takes_command()
    {
        var sim = Decap(false);
        Combat.Destroy(sim, Light.Flagship(sim.World, Side.Player)!);
        Assert.Null(sim.World.Winner);
        Assert.Equal("gun-frigate", Light.Flagship(sim.World, Side.Player)!.ClassId);
        sim.Step();
    }

    [Fact]
    public void A_destroyer_nearby_takes_command_at_once_and_a_far_one_after_a_gap()
    {
        var sim = Decap(true);
        var w = sim.World;
        var flag = Light.Flagship(w, Side.Player)!;
        var home = w.FleetOf(flag)!;
        // A second destroyer in the same fleet: instant handover.
        var spare = Economy.NewShip(w, Side.Player, "missile-destroyer");
        spare.FleetId = home.Id;
        spare.Position = home.Position;
        home.ShipIds.Add(spare.Id);
        Combat.Destroy(sim, flag);
        Assert.Equal(spare.Id, Light.Flagship(w, Side.Player)!.Id);
        Assert.False(Light.InCommandGap(w, Side.Player));
        // Now the only destroyer flies far off and the new flagship is lost at home... it has no successor in range.
        var lone = FrigateAtTamber(sim);
        var far = Economy.NewShip(w, Side.Player, "missile-destroyer");
        far.FleetId = lone.Id;
        far.Position = lone.Position;
        lone.ShipIds.Add(far.Id);
        Combat.Destroy(sim, Light.Flagship(w, Side.Player)!);
        Assert.Equal(far.Id, Light.Flagship(w, Side.Player)!.Id);
        Assert.True(Light.InCommandGap(w, Side.Player));
        Assert.Null(w.Winner);
    }

    [Fact]
    public void A_drone_carries_orders_round_a_planet_in_the_way()
    {
        var sim = NewGame();
        var w = sim.World;
        var lone = FrigateAtTamber(sim);
        var end = w.Tick + 60 * GameTime.TicksPerDay;
        while (Light.SignalClear(w, Side.Player, lone.Position) && w.Tick < end) sim.Step();
        Assert.False(Light.SignalClear(w, Side.Player, lone.Position));
        sim.Issue(new SetHoldCommand(lone.Id, HoldStyle.Drift));
        sim.Step();
        Assert.Null(Drones.CanSend(w, Side.Player, lone.Id));
        var industry = Economy.Of(w, Side.Player).Industry;
        sim.Issue(new SendDroneCommand(Side.Player, lone.Id));
        sim.Step();
        Assert.Single(w.Drones);
        Assert.Empty(Drones.Pending(w, Side.Player, lone.Id));
        Assert.True(Economy.Of(w, Side.Player).Industry < industry + 1);
        // Tamber is 4 to 6 AU out: at 20,000 Mm/h the drone takes one to two days.
        for (var i = 0; i < 4 * 24 && w.Drones.Count > 0; i++) sim.Step();
        Assert.Empty(w.Drones);
        Assert.Equal(HoldStyle.Drift, lone.Hold);
    }
}
