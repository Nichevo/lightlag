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
        // Three warships and a supply tender in the war fleet, and a second tender at home as the transport.
        Assert.Equal(5, player.Count);
        Assert.All(player, s => Assert.True(Vec2.Distance(s.Position, home.Position) < 3000));
        Assert.Contains(player, s => s.ClassId == "missile-destroyer");
        Assert.Equal(2, player.Count(s => s.ClassId == "supply-tender"));
        Assert.Equal(2, w.Fleets.Count(f => f.Side == Side.Player));
        Assert.Contains(w.Fleets, f => f.Side == Side.Player && f.ShipIds.Count == 1 && w.ShipsOf(f).All(Colonies.IsHauler));
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
