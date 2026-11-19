using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Seeded maps (MapMaker): the same seed gives the same map, every map is sane, and starts are fair.</summary>
public class MapTests
{
    private static Simulation NewGame(MapSetup map, ulong seed = 8, string? third = null)
    {
        var options = GameOptions.AiVsAi() with { Map = map, ThirdRace = third };
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body HomeOf(Simulation sim, Side side) => sim.World.Bodies.Single(b => b.Home && b.Owner == side);

    [Fact]
    public void The_same_setup_gives_the_same_map()
    {
        var a = MapMaker.Make(new MapSetup(MapType.SunLike, MapSize.Standard, 48213907), 2);
        var b = MapMaker.Make(new MapSetup(MapType.SunLike, MapSize.Standard, 48213907), 2);
        Assert.Equal(Json.Serialize(a.Data), Json.Serialize(b.Data));
        Assert.Equal(a.Homes, b.Homes);
        var c = MapMaker.Make(new MapSetup(MapType.SunLike, MapSize.Standard, 48213908), 2);
        Assert.NotEqual(Json.Serialize(a.Data), Json.Serialize(c.Data));
    }

    [Theory]
    [InlineData(MapType.SunLike, MapSize.Small)]
    [InlineData(MapType.SunLike, MapSize.Standard)]
    [InlineData(MapType.SunLike, MapSize.Large)]
    [InlineData(MapType.RedDwarf, MapSize.Small)]
    [InlineData(MapType.RedDwarf, MapSize.Standard)]
    [InlineData(MapType.RedDwarf, MapSize.Large)]
    public void Every_two_race_map_is_fair_and_the_right_size(MapType type, MapSize size)
    {
        var (min, max) = MapMaker.PlanetRange(type, size);
        var (worthMin, worthMax) = MapMaker.WorthRange(size);
        for (ulong i = 0; i < 150; i++)
        {
            var setup = new MapSetup(type, size, MapCodes.Fit(i * 7919));
            var m = MapMaker.Make(setup, 2);
            Assert.True(m.Fair, $"{setup.Code}: home scores {string.Join(", ", m.Scores)}");
            Assert.InRange(m.Planets, min, max);
            Assert.InRange(m.WorthClaiming, worthMin - 3, worthMax);
            Assert.Equal(2, m.Homes.Distinct().Count());
            foreach (var h in m.Homes)
            {
                var home = m.Data.Bodies.Single(b => b.Id == h);
                Assert.Contains(home.Type, MapMaker.HomeTypes);
                Assert.Contains(m.Data.Bodies, b => b.Parent == h && b.Kind == BodyKind.Moon);
            }
            Assert.Contains(m.Data.Bodies, b => b.Type == "gas" && m.Data.Bodies.Any(x => x.Parent == b.Id));
            GameData.Validate(m.Data with { Ships = new() }, setup.Code);
        }
    }

    [Fact]
    public void Map_codes_read_back()
    {
        var m = new MapSetup(MapType.RedDwarf, MapSize.Large, 48213907);
        Assert.Equal("RED-LRG-4821-3907", m.Code);
        Assert.Equal(m, MapCodes.Parse(m.Code, new MapSetup()));
        Assert.Equal(m with { Type = MapType.SunLike, Size = MapSize.Small },
            MapCodes.Parse("4821-3907", new MapSetup(MapType.SunLike, MapSize.Small)));
        Assert.Equal(MapType.Hesper, MapCodes.Parse("hesper", new MapSetup())!.Type);
        Assert.Null(MapCodes.Parse("not a map", new MapSetup()));
        Assert.Equal("HESPER", new MapSetup(MapType.Hesper).Code);
    }

    [Fact]
    public void A_seeded_game_plays_with_rule_checks_and_replays_exactly()
    {
        var sim = NewGame(new MapSetup(MapType.SunLike, MapSize.Standard, 31415926));
        Assert.True(sim.World.Map.Seeded);
        Assert.Equal(MapMaker.Name, sim.Replay.Scenario);
        Assert.NotEqual(HomeOf(sim, Side.Player).Id, HomeOf(sim, Side.Rival).Id);
        Assert.True(Vec2.Distance(HomeOf(sim, Side.Player).Position, HomeOf(sim, Side.Rival).Position) >= MapMaker.MinHomeDays(MapType.SunLike) * MapMaker.CruiseMmPerDay);
        sim.StepMany(GameTime.TicksPerDay * 20);
        Assert.Equal(0, sim.RuleViolations);
        var result = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(result.Exact, result.Message);

        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(sim.World.Map.Code, loaded.World.Map.Code);
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void Red_dwarf_and_three_races_play()
    {
        var sim = NewGame(new MapSetup(MapType.RedDwarf, MapSize.Large, 27182818), third: Races.Human);
        Assert.Equal(MapType.RedDwarf, sim.World.Map.Type);
        Assert.Equal(2, sim.World.Map.FlareFactor);
        Assert.True(Hazards.HotMm(sim.World) < 0.3 * Hazards.D.SunHotAu * Physics.MmPerAu);
        Assert.Equal(3, sim.World.Bodies.Count(b => b.Home));
        sim.StepMany(GameTime.TicksPerDay * 10);
        Assert.Equal(0, sim.RuleViolations);
    }

    [Fact]
    public void Swap_sides_swaps_the_homes_and_nothing_else()
    {
        var map = new MapSetup(MapType.SunLike, MapSize.Standard, 55555555);
        var a = NewGame(map, seed: 4);
        var b = NewGame(map with { SwapHomes = true }, seed: 4);
        Assert.Equal(HomeOf(a, Side.Player).Key, HomeOf(b, Side.Rival).Key);
        Assert.Equal(HomeOf(a, Side.Rival).Key, HomeOf(b, Side.Player).Key);
        Assert.Equal(a.World.Bodies.Select(x => x.Key), b.World.Bodies.Select(x => x.Key));
    }

    [Fact]
    public void Play_again_keeps_the_map_and_reshuffles_the_start()
    {
        var map = new MapSetup(MapType.SunLike, MapSize.Small, 12121212);
        var a = NewGame(map, seed: 1);
        var b = NewGame(map, seed: 2);
        Assert.Equal(a.World.Bodies.Select(x => x.Key), b.World.Bodies.Select(x => x.Key));
        Assert.NotEqual(a.World.Bodies.Select(x => x.Angle), b.World.Bodies.Select(x => x.Angle));
    }

    [Fact]
    public void Surprise_turns_into_a_real_kind_and_keeps_its_choice()
    {
        var sim = NewGame(new MapSetup(MapType.Surprise, MapSize.Small, 24681357));
        Assert.Contains(sim.World.Map.Type, new[] { MapType.SunLike, MapType.RedDwarf });
        Assert.Equal(MapType.Surprise, sim.World.Map.Chosen);
        Assert.StartsWith("ANY-SML-", sim.World.Map.Code);
    }

    [Fact]
    public void Hesper_is_unchanged_without_a_map()
    {
        var plain = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        var hesper = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions(Map: new MapSetup(MapType.Hesper)));
        Assert.False(plain.World.Map.Seeded);
        Assert.Equal(plain.StateHash(), hesper.StateHash());
        Assert.Equal("HESPER", plain.World.Map.Code);
    }
}
