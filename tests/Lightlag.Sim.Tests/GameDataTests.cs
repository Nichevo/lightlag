using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class GameDataTests
{
    [Fact]
    public void Test_system_loads_and_checks_out()
    {
        var data = GameData.LoadSystem("test-system.json");
        Assert.Contains(data.Bodies, b => b.Kind == BodyKind.Star);
        Assert.True(data.Bodies.Count(b => b.Kind == BodyKind.Moon) >= 5);
        Assert.NotEmpty(data.Belts!);
    }

    [Fact]
    public void Ship_classes_include_the_two_warships()
    {
        Assert.Equal("Gun frigate", GameData.ShipClass("gun-frigate").Name);
        Assert.Equal("Missile destroyer", GameData.ShipClass("missile-destroyer").Name);
    }

    [Theory]
    [InlineData("verdant", 365.25, 1.0)]   // one Earth-like year at 1 AU
    [InlineData("pale", 27.3, 0.3)]        // the Moon-like moon
    [InlineData("tamber", 4333, 20)]       // Jupiter-like
    public void Orbit_periods_come_from_gravity(string key, double expectedDays, double tolerance)
    {
        var w = Simulation.NewGame(Scenarios.TestSystem, 1, GameLog.Memory()).World;
        var body = w.Bodies.Single(b => b.Key == key);
        Assert.InRange(body.OrbitPeriodDays, expectedDays - tolerance, expectedDays + tolerance);
    }

    [Fact]
    public void Moons_stay_with_their_planet()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 4, GameLog.Memory());
        sim.StepMany(200 * GameTime.TicksPerDay);
        var w = sim.World;
        foreach (var moon in w.Bodies.Where(b => b.Kind == BodyKind.Moon))
        {
            var planet = w.FindBody(moon.ParentId!.Value)!;
            Assert.InRange(Vec2.Distance(moon.Position, planet.Position), moon.OrbitRadius * 0.999, moon.OrbitRadius * 1.001);
        }
    }

    [Fact]
    public void Ships_start_near_their_home_with_their_class()
    {
        var w = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory()).World;
        var home = w.Bodies.Single(b => b.Key == "verdant");
        var player = w.Ships.Where(s => s.Side == Side.Player).ToList();
        Assert.Equal(3, player.Count);
        Assert.All(player, s => Assert.True(Vec2.Distance(s.Position, home.Position) < 3000));
        Assert.Contains(player, s => s.ClassId == "missile-destroyer");
    }

    [Fact]
    public void Idle_ships_keep_station_with_their_planet()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        var home = sim.World.Bodies.Single(b => b.Key == "verdant");
        var ship = sim.World.Ships.First(s => s.Side == Side.Player);
        var offset = ship.Position - home.Position;
        sim.StepMany(30 * GameTime.TicksPerDay);   // Verdant moves about 77 million km
        Assert.True(Vec2.Distance(ship.Position - home.Position, offset) < 1e-6);
    }

    [Fact]
    public void Ships_that_arrive_near_a_body_keep_station_with_it()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        var rust = sim.World.Bodies.Single(b => b.Key == "rust");
        var ship = sim.World.Ships.First(s => s.Side == Side.Rival);
        var target = rust.Position + new Vec2(1500, 0);
        sim.Issue(new MoveShipCommand(ship.Id, target.X, target.Y));
        sim.StepMany(10);
        Assert.Equal(rust.Id, ship.HoldBodyId);
        var offset = ship.Position - rust.Position;
        sim.StepMany(48);
        Assert.True(Vec2.Distance(ship.Position - rust.Position, offset) < 1e-6);
    }

    [Fact]
    public void Ships_in_open_space_stay_put()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        var ship = sim.World.Ships.First();
        var spot = new Vec2(0, 2.7 * Physics.MmPerAu * 0.5);
        ship.Position = spot + new Vec2(100, 0);
        sim.Issue(new MoveShipCommand(ship.Id, spot.X, spot.Y));
        sim.StepMany(5);
        Assert.Null(ship.HoldBodyId);
        Assert.Equal(spot, ship.Position);
    }

    [Fact]
    public void Bad_data_fails_with_a_clear_message()
    {
        var bad = new SystemData("bad", new()
        {
            new BodyData("moon", "Moon", BodyKind.Moon, "rocky", "planet", null, 100, null, 0.01, 1, "fff", null),
        }, null, null);
        var ex = Assert.Throws<InvalidDataException>(() => GameData.Validate(bad, "bad.json"));
        Assert.Contains("'moon' orbits 'planet'", ex.Message);
    }
}
