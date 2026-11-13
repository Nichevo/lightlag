using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The Lattice (Plan 3 build 7) and three-race games: the machine race's traits, and a second rival.</summary>
public class LatticeTests
{
    private static Simulation NewGame(string player = Races.Lattice, string rival = Races.Human, string? third = null, ulong seed = 8, bool minds = false)
    {
        var options = (minds ? GameOptions.AiVsAi() : new GameOptions()) with { PlayerRace = player, RivalRace = rival, ThirdRace = third };
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        if (!minds) sim.World.Minds.Clear();
        return sim;
    }

    private static Body Home(Simulation sim, Side side) => Economy.ShipyardOf(sim.World, side)!;

    [Fact]
    public void Two_sides_unless_a_third_race_is_asked()
    {
        var two = NewGame();
        Assert.Equal(2, two.World.Sides.Length);
        Assert.DoesNotContain(two.World.Ships, s => s.Side == Side.Third);
        var three = NewGame(third: Races.Hive);
        Assert.Equal(3, three.World.Sides.Length);
        Assert.Equal(Races.Hive, three.World.Sides[(int)Side.Third].Race);
        Assert.Equal(Races.Lattice, three.World.Sides[(int)Side.Player].Race);
        Assert.NotNull(Economy.ShipyardOf(three.World, Side.Third));
        Assert.Contains(three.World.Fleets, f => f.Side == Side.Third && f.Name.StartsWith("Hive fleet"));
        Assert.Equal(3, Sides.InPlay(three.World).Count());
        Assert.Equal(new[] { Side.Rival, Side.Third }, Sides.Enemies(three.World, Side.Player));
        // Control is shared three ways: each side starts near a third.
        Assert.All(Sides.InPlay(three.World), s => Assert.InRange(Planets.Control(three.World, s).Share, 0.2, 0.5));
        var none = NewGame(third: Races.None);
        Assert.Equal(2, none.World.Sides.Length);
    }

    [Fact]
    public void Lattice_data_loads()
    {
        var l = Races.Get(Races.Lattice);
        Assert.Equal("The Lattice", l.Name);
        Assert.True(l.AiOnly);
        Assert.False(l.Eats);
        Assert.True(l.NoCaptures);
        Assert.True(l.Precision > 1);
        Assert.True(l.Cyber > 1);
        Assert.Contains(Races.Choices(), c => c.Id == Races.Lattice);
    }

    [Fact]
    public void Lattice_ships_are_all_ai_run_with_no_food()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.All(w.Ships.Where(s => s.Side == Side.Player), s => { Assert.True(s.AiRun); Assert.False(Crews.Crewed(s)); });
        Assert.True(Designer.AiRun(w, Side.Player, "gun-frigate"));
        Assert.True(Designer.AiRun(w, Side.Player, "colony-ship"));
        Assert.Null(Designer.CanRunOnAi(w, Side.Player, "troop-ship"));
        Assert.False(Designer.AiRun(w, Side.Rival, "gun-frigate"));
        var ship = Economy.NewShip(w, Side.Player, "missile-destroyer");
        Assert.True(ship.AiRun);
    }

    [Fact]
    public void Lattice_people_eat_nothing_and_never_go_hungry()
    {
        var sim = NewGame();
        var home = Home(sim, Side.Player);
        Assert.Equal(0, Planets.FoodNeedPerDay(home));
        home.FoodStock = 0;
        for (var i = 0; i < 48; i++) sim.Step();
        Assert.False(home.Hungry);
        Assert.Equal(0, Races.FoodWanted(sim.World, Side.Player, new[] { home }));
    }

    [Fact]
    public void Lattice_buildings_draw_more_power()
    {
        var lattice = NewGame();
        var human = NewGame(player: Races.Human);
        lattice.Step();
        human.Step();
        var lw = Home(lattice, Side.Player).PowerWanted;
        var hw = Home(human, Side.Player).PowerWanted;
        Assert.True(hw > 0);
        // The Lattice home has an extra power plant; compare the draw of the same buildings.
        Assert.True(lw > hw * 1.3, $"lattice wants {lw}, humans {hw}");
    }

    [Fact]
    public void Airless_rock_holds_more_lattice_people()
    {
        var sim = NewGame();
        var w = sim.World;
        var flint = w.Bodies.Single(b => b.Key == "flint");
        flint.Owner = Side.Rival;
        flint.PeopleRace = Races.Human;
        var humanCap = Planets.Cap(flint);
        flint.Owner = Side.Player;
        flint.PeopleRace = Races.Lattice;
        Assert.Equal(humanCap * 2, Planets.Cap(flint), 6);
        var verdant = w.Bodies.Single(b => b.Key == "verdant");
        Assert.False(Races.Airless(verdant));
    }

    [Fact]
    public void Lattice_missiles_are_easier_to_hijack()
    {
        var sim = NewGame(player: Races.Human, rival: Races.Lattice);
        var w = sim.World;
        Economy.Of(w, Side.Player).Researched.Add(Countermeasures.Hijack);
        Economy.Of(w, Side.Rival).Researched.Add(Countermeasures.Hijack);
        var onLattice = Countermeasures.HijackChance(w, Side.Rival, 2, Side.Player);
        var onHuman = Countermeasures.HijackChance(w, Side.Player, 2, Side.Rival);
        Assert.True(onLattice > onHuman * 1.5, $"{onLattice} vs {onHuman}");
    }

    [Fact]
    public void A_planet_the_lattice_takes_loses_its_people()
    {
        var sim = NewGame(player: Races.Human, rival: Races.Lattice);
        var w = sim.World;
        var home = Home(sim, Side.Player);
        var pale = w.Bodies.Single(b => b.Key == "pale");
        Assert.Equal(Side.Player, pale.Owner);
        var before = home.PopK;
        pale.PopK = 3;
        Planets.Take(sim, pale, Side.Rival, "test");
        Assert.Equal(0, pale.PopK);
        Assert.Equal(Races.Lattice, pale.PeopleRace);
        Assert.True(home.PopK > before, "people fled home");
        // And it takes no prisoners.
        Spoils.TakePrisoners(sim, Side.Rival, 5, "test");
        Assert.Equal(0, Economy.Of(w, Side.Rival).Spoils.PrisonersK);
    }

    [Fact]
    public void Precise_fire_control_raises_hit_chance_in_full_games_only()
    {
        Assert.Equal(1.15, Races.Get(Races.Lattice).Precision, 6);
        Assert.Equal(1, Races.Get(Races.Human).Precision);
    }

    [Fact]
    public void Three_race_game_runs_with_rules_and_replays()
    {
        var sim = NewGame(player: Races.Human, rival: Races.Hive, third: Races.Lattice, seed: 21, minds: true);
        for (var day = 0; day < 120 && sim.World.Winner == null; day++)
            for (var h = 0; h < 24; h++) sim.Step();
        Assert.Equal(0, sim.RuleViolations);
        Assert.Equal(3, sim.World.Minds.Count);
        Assert.Contains(sim.World.Minds, m => m.Side == Side.Third);
        Assert.All(sim.World.Minds, m => Assert.NotNull(m.Target));
        // Every side keeps a file on each rival.
        Assert.Equal(2, Intel.Files(sim.World, Side.Player).Count());
        // A save round trip and the replay give the same game.
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(sim.StateHash(), loaded.StateHash());
        var result = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(result.Exact, result.Message);
    }

    [Fact]
    public void Losing_the_shipyard_in_a_three_race_game_puts_a_rival_out()
    {
        var sim = NewGame(player: Races.Human, rival: Races.Hive, third: Races.Lattice);
        var w = sim.World;
        var yard = Home(sim, Side.Third);
        yard.Shipyard = false;
        sim.Step();
        Assert.True(Sides.Out(w, Side.Third));
        Assert.Null(w.Winner);
        Assert.Equal(new[] { Side.Player, Side.Rival }, Sides.Standing(w));
        Home(sim, Side.Rival).Shipyard = false;
        sim.Step();
        Assert.Equal(Side.Player, w.Winner);
    }

    [Fact]
    public void Old_two_side_saves_are_padded()
    {
        var w = new World { MissileBrain = new[] { 1, 2 }, FleetNumbers = new[] { 3, 4 } };
        w.Bodies.Add(new Body { SurveyHours = new double[] { 1, 2 }, Surveyed = new[] { true, false } });
        w.Minefields.Add(new Minefield { Known = new[] { true, true } });
        w.Battles.Add(new Battle { Sides = new[] { new BattleSide(), new BattleSide() } });
        Sides.Pad(w);
        Assert.Equal(Sides.Max, w.MissileBrain.Length);
        Assert.Equal(2, w.MissileBrain[1]);
        Assert.Equal(Sides.Max, w.Bodies[0].Surveyed.Length);
        Assert.Equal(Sides.Max, w.Minefields[0].Known.Length);
        Assert.Equal(Sides.Max, w.Battles[0].Sides.Length);
    }
}
