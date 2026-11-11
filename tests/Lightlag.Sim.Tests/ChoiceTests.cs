using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The choice log, the rival's leanings and the score band it picks from.</summary>
public class ChoiceTests
{
    private static Simulation AiGame(ulong seed = 8, double? spread = null)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: GameOptions.AiVsAi() with { ChoiceSpread = spread });
        sim.RuleCheckEveryTicks = 24;
        sim.StrictRules = true;
        return sim;
    }

    [Fact]
    public void Computer_sides_roll_leanings_and_log_what_they_choose()
    {
        var sim = AiGame();
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var leans = sim.World.Choices.Where(c => c.Side == side && c.Kind == "lean").ToList();
            Assert.Contains(leans, c => c.What.StartsWith("personality "));
            Assert.Contains(leans, c => c.What.StartsWith("pull back "));
            Assert.Contains(leans, c => c.What.EndsWith(" research first"));
        }
        sim.StepMany(40 * GameTime.TicksPerDay);
        Assert.Contains(sim.World.Choices, c => c.Kind == "research");
        Assert.Contains(sim.World.Choices, c => c.Kind == "building");
        Assert.Contains(sim.World.Choices, c => c.Kind == "ship" && c.Where != "");
        var csv = Choices.ToCsv(sim.World);
        Assert.StartsWith("side,day,kind,what,where,detail", csv);
        Assert.Equal(sim.World.Choices.Count + 1, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.DoesNotContain("battle: ", Choices.Keys(sim.World, Side.Player));
    }

    [Fact]
    public void Leanings_differ_between_games_and_drift_a_little()
    {
        var a = Mind.Of(AiGame(1).World, Side.Rival)!.Lean;
        var b = Mind.Of(AiGame(2).World, Side.Rival)!.Lean;
        Assert.True(a.Missiles != b.Missiles || a.Odds != b.Odds || a.Field != b.Field);
        var sim = AiGame(3);
        var lean = Mind.Of(sim.World, Side.Rival)!.Lean;
        var start = (lean.Missiles, lean.Odds);
        sim.StepMany(5 * GameTime.TicksPerDay);
        Assert.NotEqual(start, (lean.Missiles, lean.Odds));
        Assert.InRange(lean.Missiles, -0.2, 0.2);
        Assert.InRange(lean.Odds, 0.85, 1.15);
    }

    [Fact]
    public void The_band_picks_only_options_close_to_the_best()
    {
        var options = new List<(string, double)> { ("a", 1.0), ("b", 0.95), ("c", 0.7) };
        var rng = new SimRandom(5);
        var picks = Enumerable.Range(0, 400).Select(_ => Choices.Band(options, rng, 0.1)).ToList();
        Assert.Contains("a", picks);
        Assert.Contains("b", picks);
        Assert.DoesNotContain("c", picks);
        Assert.All(Enumerable.Range(0, 20), _ => Assert.Equal("a", Choices.Band(options, rng, 0)));
    }

    [Fact]
    public void Headless_spread_is_wider_than_a_normal_game()
    {
        Assert.Equal(Mind.NormalSpread, Mind.Of(AiGame().World, Side.Rival)!.Spread);
        Assert.Equal(0.1, Mind.Of(AiGame(spread: 0.1).World, Side.Rival)!.Spread);
    }

    [Fact]
    public void Battle_outcomes_go_to_whoever_held_the_field()
    {
        BattleSide Side(int shipsIn, int lost, bool withdrew = false) =>
            new() { ShipsIn = shipsIn, ShipsLost = Enumerable.Range(0, lost).Select(i => $"S{i}").ToList(), Withdrew = withdrew };
        Assert.Equal("won", Choices.Outcome(Side(4, 1), Side(3, 3)));
        Assert.Equal("lost", Choices.Outcome(Side(4, 0, withdrew: true), Side(3, 1)));
        Assert.Equal("won", Choices.Outcome(Side(4, 1), Side(4, 2)));
        Assert.Equal("even", Choices.Outcome(Side(4, 0), Side(4, 0)));
    }

    [Fact]
    public void A_battle_is_logged_for_both_sides_with_its_odds()
    {
        var sim = AiGame();
        var w = sim.World;
        var battle = new Battle { Id = 1 };
        battle.For(Side.Player).Fleets.Add("A");
        battle.For(Side.Player).ShipsIn = 4;
        battle.For(Side.Player).PowerIn = 40;
        battle.For(Side.Rival).Fleets.Add("B");
        battle.For(Side.Rival).ShipsIn = 2;
        battle.For(Side.Rival).PowerIn = 20;
        battle.For(Side.Rival).Withdrew = true;
        Choices.OnBattleOver(w, battle);
        var mine = Assert.Single(w.Choices, c => c.Kind == "battle" && c.Side == Lightlag.Sim.Core.Side.Player);
        Assert.Equal("won", mine.What);
        Assert.Contains("odds 2.00", mine.Detail);
        Assert.Equal("lost", Assert.Single(w.Choices, c => c.Kind == "battle" && c.Side == Lightlag.Sim.Core.Side.Rival).What);
    }
}
