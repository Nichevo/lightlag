using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

public class StewardTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static SideState Player(Simulation sim) => Economy.Of(sim.World, Side.Player);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    [Fact]
    public void Areas_start_at_their_defaults()
    {
        var sim = NewGame();
        Assert.Equal(AutoLevel.Advise, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Building));
        Assert.Equal(AutoLevel.Auto, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Transport));
        Assert.Equal(AutoLevel.Advise, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Research));
        Assert.Equal(AutoLevel.Manual, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Shipyard));
        Assert.Equal(AutoLevel.Auto, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Battle));
        Assert.Equal(Stewards.Areas.Length, GameData.Ai.Automation!.Defaults.Count);
    }

    [Fact]
    public void Advise_suggests_research_and_accepting_queues_it_even_while_paused()
    {
        var sim = NewGame();
        Days(sim, 0.5);
        var need = Assert.Single(Player(sim).Needs, n => n.Key.StartsWith("research:"));
        Assert.NotNull(need.Act);
        Assert.Empty(sim.World.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research));
        sim.Issue(new AcceptNeedCommand(Side.Player, need.Key));
        sim.ApplyPending();
        Assert.Single(sim.World.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research));
        Assert.DoesNotContain(Player(sim).Needs, n => n.Key == need.Key);
    }

    [Fact]
    public void Auto_research_queues_by_itself_and_counts_as_handled()
    {
        var sim = NewGame();
        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Research, AutoLevel.Auto));
        Days(sim, 0.5);
        Assert.Single(sim.World.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research));
        Assert.True(Player(sim).AutoHandled > 0);
        Assert.Contains(Player(sim).AutoNotes, n => n.Area == AutoArea.Research);
    }

    [Fact]
    public void Manual_leaves_an_area_alone()
    {
        var sim = NewGame();
        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Research, AutoLevel.Manual));
        Days(sim, 1);
        Assert.DoesNotContain(Player(sim).Needs, n => n.Key.StartsWith("research:"));
        Assert.Contains(Player(sim).Needs, n => n.Key.StartsWith("idle-team:"));
    }

    [Fact]
    public void Shipyard_auto_never_queues_warships()
    {
        var sim = NewGame();
        foreach (var area in Stewards.Areas) sim.Issue(new SetAutoLevelCommand(Side.Player, area, AutoLevel.Auto));
        Player(sim).Industry = 5000;
        var warships = sim.World.Ships.Count(s => s.Side == Side.Player && Planner.Warship(s));
        Days(sim, 60);
        Assert.DoesNotContain(sim.World.Orders, o => o.Side == Side.Player && o.Kind == OrderKind.Ship && Planner.WarshipClass(o.Key));
        Assert.True(sim.World.Ships.Count(s => s.Side == Side.Player && Planner.Warship(s)) <= warships);
    }

    [Fact]
    public void A_lost_warship_is_offered_for_rebuilding_not_rebuilt()
    {
        var sim = NewGame();
        Days(sim, 0.2);
        var ship = sim.World.Ships.First(s => s.Side == Side.Player && Planner.Warship(s));
        Combat.Destroy(sim, ship);
        Days(sim, 0.5);
        var need = Assert.Single(Player(sim).Needs, n => n.Key == "lost:" + ship.Name);
        Assert.Equal("Build it", need.ActLabel);
        Assert.DoesNotContain(sim.World.Orders, o => o.Side == Side.Player && o.Key == ship.ClassId);
    }

    [Fact]
    public void The_same_hand_change_again_and_again_suggests_a_new_default()
    {
        var sim = NewGame();
        var bodies = sim.World.Bodies.Take(GameData.Ai.Automation!.HabitChanges).ToList();
        foreach (var b in bodies) sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Building, AutoLevel.Auto, b.Id));
        Days(sim, 0.5);
        var need = Assert.Single(Player(sim).Needs, n => n.Key == "learn-habit:Building:Auto");
        sim.Issue(new AcceptNeedCommand(Side.Player, need.Key));
        sim.ApplyPending();
        Assert.Equal(AutoLevel.Auto, Player(sim).Auto[AutoArea.Building]);
        Days(sim, 0.5);
        Assert.DoesNotContain(Player(sim).Needs, n => n.Key.StartsWith("learn-habit:Building"));
    }

    [Fact]
    public void A_planet_override_wins_over_the_area()
    {
        var sim = NewGame();
        var home = Economy.ShipyardOf(sim.World, Side.Player)!;
        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Building, AutoLevel.Manual, home.Id));
        sim.ApplyPending();
        Assert.Equal(AutoLevel.Manual, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Building, home.Id));
        Assert.Equal(AutoLevel.Advise, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Building));
        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Building, null, home.Id));
        sim.ApplyPending();
        Assert.Equal(AutoLevel.Advise, Stewards.LevelFor(sim.World, Side.Player, AutoArea.Building, home.Id));
    }

    [Fact]
    public void Needs_and_automation_survive_a_save()
    {
        var sim = NewGame();
        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Building, AutoLevel.Auto));
        Days(sim, 3);
        Assert.NotEmpty(Player(sim).Needs);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(sim.StateHash(), loaded.StateHash());
        var p = Economy.Of(loaded.World, Side.Player);
        Assert.Equal(AutoLevel.Auto, p.Auto[AutoArea.Building]);
        Assert.Equal(Player(sim).Needs.Count, p.Needs.Count);
        Assert.Equal(Player(sim).Needs.Select(n => n.Act?.GetType()), p.Needs.Select(n => n.Act?.GetType()));
        Days(sim, 5);
        loaded.StepMany(5 * GameTime.TicksPerDay);
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }
}
