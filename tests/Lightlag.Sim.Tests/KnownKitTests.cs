using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Known kit: a slight edge against rival weapons a side has studied through intel.</summary>
public class KnownKitTests
{
    private static World Game()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 300, GameLog.Memory(), options: GameOptions.AiVsAi(Difficulty.Normal));
        Starts.Classic(sim);
        return sim.World;
    }

    [Fact]
    public void Studied_missiles_are_shot_down_slightly_more_and_the_report_says_so()
    {
        var w = Game();
        var battle = new Battle();
        var missile = new Missile { Side = Side.Rival, Brain = 2 };
        var file = Intel.Of(w, Side.Player, Side.Rival);

        // Unknown kit: no edge.
        Assert.Equal(1, KnownKit.PointDefence(w, battle, Side.Player, missile));

        // Seen, but intel level too low: still none.
        file.Tech.Add(new TechFact { Part = "missiles", Level = 2 });
        file.Points = 0;
        Assert.Equal(1, KnownKit.PointDefence(w, battle, Side.Player, missile));

        // Studied: a slight edge, named in the battle report.
        file.Points = 100;
        var f = KnownKit.PointDefence(w, battle, Side.Player, missile);
        Assert.InRange(f, 1.01, 1.1);
        Assert.Single(battle.For(Side.Player).KnownKit);

        // A better brain than the one studied: no edge.
        Assert.Equal(1, KnownKit.PointDefence(w, battle, Side.Player, new Missile { Side = Side.Rival, Brain = 3 }));
    }

    [Fact]
    public void Matchup_shows_unknown_parts_as_a_question_mark_and_names_the_studied_edge()
    {
        var w = Game();
        var file = Intel.Of(w, Side.Player, Side.Rival);
        var rows = KnownKit.Matchup(w, Side.Player, Side.Rival);
        Assert.All(rows, r => { Assert.Equal("?", r.Theirs); Assert.Null(r.Edge); Assert.Null(r.Known); });
        file.Tech.Add(new TechFact { Part = "railguns", Level = 2 });
        file.Points = 100;
        var rail = KnownKit.Matchup(w, Side.Player, Side.Rival).Single(r => r.Part == "railguns");
        Assert.Equal("Mk II", rail.Theirs);
        Assert.Equal(-1, rail.Edge);
        Assert.NotNull(rail.Known);
    }

    [Fact]
    public void Guard_picks_a_free_armed_fleet_or_says_why_not()
    {
        var w = Game();
        var home = Economy.ShipyardOf(w, Side.Player)!;
        var pick = Intentions.GuardPick(w, Side.Player, home.Id);
        Assert.False(string.IsNullOrEmpty(pick.Text));
        if (pick.Fleet != null) Assert.Equal(Side.Player, pick.Fleet.Side);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player)) f.ShipIds.Clear();
        var none = Intentions.GuardPick(w, Side.Player, home.Id);
        Assert.Null(none.Fleet);
        Assert.StartsWith("No free warships", none.Text);
    }

    [Fact]
    public void Research_is_overheard_only_at_level_four()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 301, GameLog.Memory(), options: GameOptions.AiVsAi(Difficulty.Normal));
        Starts.Classic(sim);
        var w = sim.World;
        sim.StepMany((int)(40 * GameTime.TicksPerDay));
        foreach (var s in Sides.InPlay(w))
            foreach (var f in Intel.Files(w, s))
                Assert.All(f.Intercepts.Where(i => i.Kind == PlanKind.Research), i => Assert.False(string.IsNullOrEmpty(i.Idea)));
        // Put the player's file at level 4 and its ears on the rival's command: research is heard.
        var file = Intel.Of(w, Side.Player, Side.Rival);
        var heardBefore = file.Intercepts.Count(i => i.Kind == PlanKind.Research);
        for (var day = 0; day < 30; day++)
        {
            file.Points = 250;
            file.LastContactHour = w.Hour;
            sim.StepMany((int)GameTime.TicksPerDay);
        }
        Assert.True(file.Intercepts.Count(i => i.Kind == PlanKind.Research) >= heardBefore);
        Assert.All(file.Intercepts.Where(i => i.Kind == PlanKind.Research), i => Assert.True(i.HeardHour >= i.SentHour));
        Assert.Null(Intentions.Likeliest(w, Side.Player, Side.Rival) is { Kind: PlanKind.Research } r ? r : null);
    }
}

