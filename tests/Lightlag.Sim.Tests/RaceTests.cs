using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Races (Plan 3 build 3): the race picker's options, and what makes the Hive play differently.</summary>
public class RaceTests
{
    private static Simulation NewGame(string player = Races.Hive, string rival = Races.Human, ulong seed = 8, bool minds = false)
    {
        var options = (minds ? GameOptions.AiVsAi() : new GameOptions()) with { PlayerRace = player, RivalRace = rival };
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        if (!minds) sim.World.Minds.Clear();
        return sim;
    }

    private static Body Home(Simulation sim, Side side) => Economy.ShipyardOf(sim.World, side)!;

    private static Fleet NewFleet(Simulation sim, Side side, Vec2 at, params string[] classes) =>
        Fleets.Create(sim.World, side, classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList(), at);

    private static Vec2 FarAway(Simulation sim) =>
        sim.World.Bodies.Single(b => b.Kind == BodyKind.Star).Position + new Vec2(0, 60000);

    [Fact]
    public void Both_sides_are_human_unless_asked()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory());
        Assert.All(sim.World.Sides, s => Assert.Equal(Races.Human, s.Race));
        Assert.All(sim.World.Bodies.Where(b => b.Owner != null), b => Assert.Equal(Races.Human, b.PeopleRace));
    }

    [Fact]
    public void Race_data_loads_with_humans_and_the_hive()
    {
        Assert.True(Races.Known(Races.Human));
        Assert.True(Races.Known(Races.Hive));
        Assert.False(Races.Known(Races.Random));
        var hive = Races.Get(Races.Hive);
        Assert.True(hive.Grown);
        Assert.False(hive.CrewRanks);
        Assert.NotEmpty(hive.Pros!);
        Assert.NotEmpty(hive.Cons!);
        // Humans are the baseline: every factor is 1.
        var human = Races.Get(Races.Human);
        Assert.Equal(1, human.FoodNeed);
        Assert.False(human.Grown);
        Assert.True(human.CrewRanks);
        Assert.Equal(new[] { Races.Human, Races.Hive, Races.Random }, Races.Choices().Select(c => c.Id).Take(3));
    }

    [Fact]
    public void Unknown_race_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Simulation.NewGame(Scenarios.TestSystem, 1, GameLog.Memory(), options: new GameOptions { PlayerRace = "kesh" }));
    }

    [Fact]
    public void Random_race_comes_from_the_seed_and_leaves_the_map_alone()
    {
        var a = Simulation.NewGame(Scenarios.TestSystem, 21, GameLog.Memory(), options: new GameOptions { PlayerRace = Races.Random, RivalRace = Races.Random });
        var b = Simulation.NewGame(Scenarios.TestSystem, 21, GameLog.Memory(), options: new GameOptions { PlayerRace = Races.Random, RivalRace = Races.Random });
        var plain = Simulation.NewGame(Scenarios.TestSystem, 21, GameLog.Memory());
        Assert.Equal(a.World.Sides.Select(s => s.Race), b.World.Sides.Select(s => s.Race));
        Assert.Equal(plain.World.Bodies.Select(x => x.Angle), a.World.Bodies.Select(x => x.Angle));
        // Over many seeds both races come up.
        var seen = Enumerable.Range(1, 30).Select(seed => Simulation.NewGame(Scenarios.TestSystem, (ulong)seed, GameLog.Memory(),
            options: new GameOptions { PlayerRace = Races.Random }).World.Sides[0].Race).Distinct().ToList();
        Assert.Contains(Races.Human, seen);
        Assert.Contains(Races.Hive, seen);
    }

    [Fact]
    public void Hive_home_is_hive_people_with_an_extra_farm_and_hive_ship_names()
    {
        var sim = NewGame();
        var w = sim.World;
        var hive = Home(sim, Side.Player);
        var human = Home(sim, Side.Rival);
        Assert.Equal(Races.Hive, hive.PeopleRace);
        Assert.Equal(Races.Human, human.PeopleRace);
        Assert.Equal(Planets.Count(human, Planets.Farm) + 1, Planets.Count(hive, Planets.Farm));
        var names = ShipNames.Data.Races[Races.Hive].Values.SelectMany(v => v).ToHashSet();
        var ship = Economy.NewShip(w, Side.Player, "gun-frigate", hive);
        Assert.Contains(ShipNames.Bare(ship.Name), names);
    }

    [Fact]
    public void Hive_people_eat_more_and_grow_faster()
    {
        var sim = NewGame();
        var hive = Home(sim, Side.Player);
        var human = Home(sim, Side.Rival);
        hive.PopK = human.PopK = 50;
        Assert.Equal(Planets.FoodNeedPerDay(human) * Races.Get(Races.Hive).FoodNeed, Planets.FoodNeedPerDay(hive), 9);
        sim.StepMany(3 * GameTime.TicksPerDay); // (before Auto's first settlers leave)
        Assert.True(hive.PopK - 50 > (human.PopK - 50) * 1.2, $"hive grew {hive.PopK - 50:0.###}k, humans {human.PopK - 50:0.###}k");
    }

    [Fact]
    public void Hive_ships_take_biomass_and_fewer_alloys()
    {
        var sim = NewGame();
        var w = sim.World;
        var yard = Home(sim, Side.Player);
        Economy.Of(w, Side.Player).Industry = 10_000;
        yard.Stock["alloys"] = 200;
        yard.Stock["electronics"] = 50;
        var foodBefore = Races.BiomassAt(w, yard);
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.StepMany(2);
        var order = w.Orders.Single(o => o.Side == Side.Player && o.Kind == OrderKind.Ship);
        Assert.True(order.Paid);
        var cost = Economy.CostOf(order);
        var alloys = Economy.MaterialsOf(order)!["alloys"];
        Assert.Equal(200 - alloys * Races.Get(Races.Hive).ShipMaterials, yard.Stock["alloys"], 6);
        // Food moves each hour too, so check the drop is at least about the biomass.
        Assert.True(foodBefore - Races.BiomassAt(w, yard) > Races.BiomassFor(w, Side.Player, cost) * 0.8,
            $"food {foodBefore:0.###} -> {Races.BiomassAt(w, yard):0.###}, biomass {Races.BiomassFor(w, Side.Player, cost)}");
    }

    [Fact]
    public void Hive_yard_waits_for_biomass_and_says_so()
    {
        var sim = NewGame();
        var w = sim.World;
        var yard = Home(sim, Side.Player);
        Economy.Of(w, Side.Player).Industry = 10_000;
        yard.Stock["alloys"] = 500;
        yard.Stock["electronics"] = 50;
        foreach (var b in w.Bodies.Where(b => b.Owner == Side.Player)) b.FoodStock = 0;
        yard.Pieces.RemoveAll(p => Planets.BuildingKind(p.Kind)?.Food > 0); // no harvest either
        Colonies.SyncBuildings(yard);
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, "gun-frigate"));
        sim.StepMany(2);
        var order = w.Orders.Single(o => o.Side == Side.Player && o.Kind == OrderKind.Ship);
        Assert.False(order.Paid);
        Assert.NotNull(order.Waiting);
        Assert.True(order.Waiting!.Contains("biomass") || order.Waiting.Contains("starving"), order.Waiting);
    }

    [Fact]
    public void Starving_hive_pauses_a_ship_already_growing()
    {
        var sim = NewGame();
        var w = sim.World;
        var yard = Home(sim, Side.Player);
        var order = new WorkOrder { Id = w.NextId++, Side = Side.Player, Kind = OrderKind.Ship, Key = "gun-frigate", Paid = true, TotalHours = 100, HoursLeft = 50 };
        w.Orders.Add(order);
        yard.Hungry = true;
        Assert.Contains("starving", Races.GrowStatus(w, Side.Player, yard, 0, started: true)!);
        // Humans don't grow ships: never held up this way.
        Home(sim, Side.Rival).Hungry = true;
        Assert.Null(Races.GrowStatus(w, Side.Rival, Home(sim, Side.Rival), 0, started: true));
    }

    [Fact]
    public void Grown_hulls_heal_by_themselves()
    {
        var sim = NewGame();
        var w = sim.World;
        var hive = NewFleet(sim, Side.Player, FarAway(sim), "gun-frigate");
        var human = NewFleet(sim, Side.Rival, FarAway(sim) + new Vec2(40000, 0), "gun-frigate");
        Fleets.EnterDrift(hive);
        Fleets.EnterDrift(human);
        var a = w.ShipsOf(hive).Single();
        var b = w.ShipsOf(human).Single();
        a.Armour = Tech.MaxArmour(w, a) * 0.5;
        b.Armour = Tech.MaxArmour(w, b) * 0.5;
        sim.StepMany(GameTime.TicksPerDay);
        Assert.True(a.Armour / Tech.MaxArmour(w, a) > 0.52, $"hive hull {a.Armour / Tech.MaxArmour(w, a):P1}");
        Assert.Equal(0.5, b.Armour / Tech.MaxArmour(w, b), 6);
        Assert.True(Races.Feeds(w, a));
        a.Food = 0;
        Assert.False(Races.Feeds(w, a));
    }

    [Fact]
    public void Drone_crews_never_rank_up_and_take_fewer_people()
    {
        var sim = NewGame();
        var w = sim.World;
        var ship = Economy.NewShip(w, Side.Player, "gun-frigate");
        var human = Economy.NewShip(w, Side.Rival, "gun-frigate");
        Assert.True(Crews.Crewed(ship));
        Assert.False(Crews.Learns(w, ship));
        Assert.True(Crews.Learns(w, human));
        Economy.Of(w, Side.Player).ReserveCrews.Add(5);
        Crews.Man(w, ship, Home(sim, Side.Player));
        Assert.Equal(Crews.DroneXp, ship.Xp); // the rescued veteran crew stays in the reserve
        Assert.Single(Economy.Of(w, Side.Player).ReserveCrews);
        Assert.Equal(Crews.CrewPeopleK(w, Side.Rival, "gun-frigate") * Races.Get(Races.Hive).CrewPop, Crews.CrewPeopleK(w, Side.Player, "gun-frigate"), 9);
        // Strain from a dodge is lighter for drones.
        Crews.Strain(w, ship, false, 1);
        Crews.Strain(w, human, false, 1);
        Assert.True(ship.Strain < human.Strain);
        w.Ships.Remove(ship);
        w.Ships.Remove(human);
    }

    [Fact]
    public void Swarm_boarding_fights_harder()
    {
        var sim = NewGame();
        var w = sim.World;
        var target = NewFleet(sim, Side.Rival, FarAway(sim), "gun-frigate");
        var targetShip = w.ShipsOf(target).Single();
        Boarding Party(Side side, BoardApproach how) => new() { Side = side, TargetShipId = targetShip.Id, Marines = 1, Defenders = 1, Approach = how };
        var swarm = Boarders.Strength(w, Party(Side.Player, BoardApproach.Breach)).Att;
        Economy.Of(w, Side.Player).Race = Races.Human;
        var plain = Boarders.Strength(w, Party(Side.Player, BoardApproach.Breach)).Att;
        Assert.Equal(plain * Races.Get(Races.Hive).Boarding, swarm, 9);
        // A cyber attack is no swarm: the same for both races.
        var cyber = Boarders.Strength(w, Party(Side.Player, BoardApproach.Cyber)).Att;
        Economy.Of(w, Side.Player).Race = Races.Hive;
        Assert.Equal(cyber, Boarders.Strength(w, Party(Side.Player, BoardApproach.Cyber)).Att, 9);
        Assert.True(Planets.SlipwayTroopsK(w, Side.Player) < Planets.SlipwayTroopsK(w, Side.Rival));
    }

    [Fact]
    public void Hive_sensors_see_less_far_and_signals_research_is_slower()
    {
        var sim = NewGame();
        var w = sim.World;
        var hive = NewFleet(sim, Side.Player, FarAway(sim), "gun-frigate");
        var human = NewFleet(sim, Side.Rival, FarAway(sim) + new Vec2(40000, 0), "gun-frigate");
        Assert.Equal(Sensors.SensorOf(w, human) * Races.Get(Races.Hive).Sensors, Sensors.SensorOf(w, hive), 6);
        Assert.True(Sensors.PlanetSensorOf(w, Side.Player, Home(sim, Side.Player)) < Sensors.PlanetSensorOf(w, Side.Rival, Home(sim, Side.Rival)));
        Assert.True(Research.BaseHours(w, Side.Player, "signals-analysis") > Research.BaseHours(w, Side.Rival, "signals-analysis"));
        Assert.True(Research.BaseHours(w, Side.Player, "cruiser-hull") < Research.BaseHours(w, Side.Rival, "cruiser-hull"));
        Assert.Equal(Research.BaseHours(w, Side.Rival, "automation"), Research.BaseHours(w, Side.Player, "automation"), 6);
    }

    [Fact]
    public void Hive_buildings_are_dug_in()
    {
        var sim = NewGame();
        var w = sim.World;
        var hive = Home(sim, Side.Player);
        var human = Home(sim, Side.Rival);
        var farm = Planets.BuildingKind(Planets.Farm)!;
        Assert.Equal(Races.BuildHours(w, human, farm) * Races.Get(Races.Hive).DigDays, Races.BuildHours(w, hive, farm), 6);
        Assert.Equal(Planets.BuildingArmourOf(w, human) * Races.Get(Races.Hive).BombArmour, Planets.BuildingArmourOf(w, hive), 6);
        // A building queued on a Hive world takes the slower pace, and the queued-building rule accepts it.
        var spot = Colonies.BestSpot(hive, farm);
        if (spot is var (x, y, turned))
        {
            var piece = Colonies.AddPiece(w, hive, farm, x, y, turned, built: false);
            Assert.Equal(Races.BuildHours(w, hive, farm), piece.HoursLeft, 6);
            Assert.Empty(RuleBook.Default().Check(w).Where(v => v.Rule == "queued-buildings-unstarted"));
        }
    }

    [Fact]
    public void Hive_rival_plays_a_long_game_within_the_rules()
    {
        var sim = NewGame(Races.Human, Races.Hive, seed: 5, minds: true);
        sim.StepMany(150 * GameTime.TicksPerDay);
        Assert.Equal(0, sim.RuleViolations);
        Assert.True(Economy.Of(sim.World, Side.Rival).Totals.ShipsBuilt > 0, "the Hive grew no ships");
        Assert.All(sim.World.Ships.Where(s => s.Side == Side.Rival && Crews.Crewed(s)), s => Assert.Equal(Crews.DroneXp, s.Xp));
    }

    [Fact]
    public void Races_survive_saves_and_replays()
    {
        var sim = NewGame(Races.Random, Races.Hive, seed: 9, minds: true);
        sim.StepMany(20 * GameTime.TicksPerDay);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(sim.World.Sides.Select(s => s.Race), loaded.World.Sides.Select(s => s.Race));
        Assert.Equal(sim.StateHash(), loaded.StateHash());
        var result = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(result.Exact, result.Message);
    }
}
