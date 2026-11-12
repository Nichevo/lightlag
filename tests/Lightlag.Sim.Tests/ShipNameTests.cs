using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Ship designation codes and names (ShipNames.cs, names.json).</summary>
public class ShipNameTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: new GameOptions { Events = EventRate.Off });
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        return sim;
    }

    [Fact]
    public void Every_ship_has_a_code_and_a_unique_name()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.All(w.Ships, s =>
        {
            Assert.Matches(@"^[A-Z]{2}[1-3][A-Z]-\d{2,}$", s.Code);
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
        });
        Assert.Equal(w.Ships.Count, w.Ships.Select(s => s.Name.ToLowerInvariant()).Distinct().Count());
        var start = w.Ships.First(s => s.Side == Side.Player);
        Assert.Contains(start.Name, ShipNames.Data.Races["human"]["home"]);
    }

    [Fact]
    public void Many_new_ships_never_share_a_name()
    {
        var sim = NewGame();
        var w = sim.World;
        for (var i = 0; i < 250; i++) Economy.NewShip(w, Side.Player, "gun-frigate");
        Assert.Equal(w.Ships.Count, w.Ships.Select(s => s.Name.ToLowerInvariant()).Distinct().Count());
    }

    [Fact]
    public void The_yard_world_picks_the_theme()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.Equal("home", ShipNames.Theme(w, null));
        foreach (var b in w.Bodies.Where(b => !b.Shipyard))
        {
            var theme = ShipNames.Theme(w, b);
            Assert.Contains(theme, ShipNames.Data.Races["human"].Keys);
            if (b.Type is "ocean" or "terran") Assert.Equal("water", theme);
        }
        var ocean = w.Bodies.FirstOrDefault(b => !b.Shipyard && b.Type is "ocean" or "terran");
        if (ocean != null)
        {
            var s = Economy.NewShip(w, Side.Player, "gun-frigate", ocean);
            Assert.Contains(s.Name, ShipNames.Data.Races["human"]["water"]);
        }
    }

    [Fact]
    public void Armour_research_raises_the_hull_mark_and_a_new_fit_is_a_new_variant()
    {
        var sim = NewGame();
        var w = sim.World;
        var a = Economy.NewShip(w, Side.Player, "gun-frigate");
        Assert.StartsWith("FF1A-", a.Code);
        Economy.Of(w, Side.Player).Researched.Add("heavy-armour");
        var b = Economy.NewShip(w, Side.Player, "gun-frigate");
        Assert.StartsWith("FF2A-", b.Code);
        var name = a.Name;
        var number = a.HullNumber;
        var fit = Fitting.FitOf(a).ToList();
        fit[2] = GameData.Modules.Values.First(m => m.Slot == "U").Id;
        Fitting.Apply(w, a, fit);
        Assert.Equal($"FF2B-{number:00}", a.Code);
        Assert.Equal(name, a.Name);
    }

    [Fact]
    public void A_ship_can_be_renamed_even_while_paused_but_not_to_a_taken_name()
    {
        var sim = NewGame();
        var w = sim.World;
        var mine = w.Ships.Where(s => s.Side == Side.Player).Take(2).ToList();
        new RenameShipCommand(Side.Player, mine[0].Id, "  Steadfast Jeanne  ").Apply(w, sim);
        Assert.Equal("Steadfast Jeanne", mine[0].Name);
        new RenameShipCommand(Side.Player, mine[1].Id, "steadfast jeanne").Apply(w, sim);
        Assert.NotEqual("steadfast jeanne", mine[1].Name);
        Assert.NotNull(ShipNames.Problem(w, mine[1], ""));
        Assert.NotNull(ShipNames.Problem(w, mine[1], new string('x', ShipNames.MaxLength + 1)));
        var rival = w.Ships.First(s => s.Side == Side.Rival);
        var old = rival.Name;
        new RenameShipCommand(Side.Player, rival.Id, "Mine Now").Apply(w, sim);
        Assert.Equal(old, rival.Name);
    }

    [Fact]
    public void Names_are_the_same_for_the_same_seed_and_survive_save_and_load()
    {
        var a = NewGame(21);
        var b = NewGame(21);
        Assert.Equal(a.World.Ships.Select(s => s.Name + s.Code), b.World.Ships.Select(s => s.Name + s.Code));
        var loaded = Simulation.FromSave(SaveGame.FromJson(a.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(a.World.Ships.Select(s => s.Name + s.Code + s.HullNumber), loaded.World.Ships.Select(s => s.Name + s.Code + s.HullNumber));
    }
}
