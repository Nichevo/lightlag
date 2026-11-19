using System.Text.RegularExpressions;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Research that is different each game: leads, hard problems and lost ideas (see ResearchRolls).</summary>
public class ResearchRollTests
{
    private static Simulation NewGame(ulong seed = 8, bool variety = true)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: new GameOptions(ResearchVariety: variety));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static void Hour(Simulation sim) => sim.StepMany((int)Math.Round(GameTime.TicksPerDay / 24.0));

    [Fact]
    public void Each_side_rolls_leads_hard_problems_and_lost_ideas()
    {
        var sim = NewGame();
        foreach (var side in Sides.InPlay(sim.World))
        {
            var roll = ResearchRolls.Of(sim.World, side);
            Assert.InRange(roll.Leads.Count, 4, 5);
            Assert.InRange(roll.Hard.Count, 2, 3);
            Assert.Equal(ResearchRolls.D.LostCount, roll.Lost.Count);
            Assert.Empty(roll.Leads.Intersect(roll.Hard));
            Assert.Empty(roll.Lost.Intersect(roll.Leads.Concat(roll.Hard)));
            Assert.All(roll.Leads.Concat(roll.Hard), id => Assert.True(roll.Why.ContainsKey(id)));
            Assert.All(roll.Lost, id => Assert.Contains(id, ResearchRolls.D.LostPool!));
        }
    }

    [Fact]
    public void The_same_seed_rolls_the_same_and_other_seeds_differ()
    {
        string Key(Simulation s) => string.Join(",", ResearchRolls.Of(s.World, Side.Player).Leads) + "|" + string.Join(",", ResearchRolls.Of(s.World, Side.Player).Lost);
        Assert.Equal(Key(NewGame(11)), Key(NewGame(11)));
        var keys = Enumerable.Range(20, 6).Select(s => Key(NewGame((ulong)s))).Distinct().Count();
        Assert.True(keys > 1, "six seeds all rolled the same research");
        // Player and rival roll separately.
        var sim = NewGame(11);
        Assert.NotEqual(ResearchRolls.Of(sim.World, Side.Player).Leads, ResearchRolls.Of(sim.World, Side.Rival).Leads);
    }

    [Fact]
    public void Switched_off_every_game_has_the_same_web()
    {
        var sim = NewGame(variety: false);
        Assert.False(sim.World.Rules.ResearchVariety);
        foreach (var side in Sides.InPlay(sim.World))
        {
            var roll = ResearchRolls.Of(sim.World, side);
            Assert.Empty(roll.Leads);
            Assert.Empty(roll.Hard);
            Assert.Empty(roll.Lost);
        }
        Assert.True(NewGame().World.Rules.ResearchVariety);
    }

    [Fact]
    public void Leads_are_quicker_and_in_view_hard_problems_slower()
    {
        var on = NewGame();
        var off = NewGame(variety: false);
        var roll = ResearchRolls.Of(on.World, Side.Player);
        foreach (var id in roll.Leads)
        {
            Assert.Equal(Research.BaseHours(off.World, Side.Player, id) * (1 - ResearchRolls.D.LeadCut), Research.BaseHours(on.World, Side.Player, id), 6);
            Assert.True(Research.Seen(on.World, Side.Player, id));
        }
        foreach (var id in roll.Hard)
            Assert.Equal(Research.BaseHours(off.World, Side.Player, id) * (1 + ResearchRolls.D.HardExtra), Research.BaseHours(on.World, Side.Player, id), 6);
    }

    [Fact]
    public void A_lost_idea_cannot_be_seen_or_queued_until_found()
    {
        var sim = NewGame();
        var w = sim.World;
        // A lost idea whose needs are all done would otherwise be open to queue.
        var id = ResearchRolls.Of(w, Side.Player).Lost.First();
        foreach (var need in GameData.Research(id).Needs ?? new()) Economy.Of(w, Side.Player).Researched.Add(need);
        Assert.True(ResearchRolls.Hidden(w, Side.Player, id));
        Assert.False(Research.Seen(w, Side.Player, id));
        Assert.False(Research.Open(w, Side.Player, id));
        Assert.NotNull(Economy.CanQueue(w, Side.Player, OrderKind.Research, id));
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, id));
        Hour(sim);
        Assert.DoesNotContain(w.Orders, o => o.Key == id);

        ResearchRolls.Reveal(sim, Side.Player, id, "a test");
        Assert.False(ResearchRolls.Hidden(w, Side.Player, id));
        Assert.True(ResearchRolls.FoundLost(w, Side.Player, id));
        Assert.Equal("a test", ResearchRolls.HowFound(w, Side.Player, id));
        Assert.Null(Economy.CanQueue(w, Side.Player, OrderKind.Research, id));
        Assert.Contains(Economy.Of(w, Side.Player).SparkNotes, n => n.Idea == id);
    }

    [Fact]
    public void A_spark_or_a_capture_turns_up_lost_ideas()
    {
        var sim = NewGame();
        var w = sim.World;
        var roll = ResearchRolls.Of(w, Side.Player);
        // A spark that names a lost idea brings it into view like any other.
        if (roll.Lost.FirstOrDefault(id => GameData.Research(id).Spark != null) is string sparked)
        {
            Sparks.Fire(sim, Side.Player, GameData.Research(sparked).Spark!);
            Assert.False(ResearchRolls.Hidden(w, Side.Player, sparked));
        }
        var hidden = roll.Lost.Count(id => ResearchRolls.Hidden(w, Side.Player, id));
        Spoils.Scrap(sim, Side.Player, "a test capture", IntelSource.Capture, from: Side.Rival);
        Assert.Equal(hidden - 1, roll.Lost.Count(id => ResearchRolls.Hidden(w, Side.Player, id)));
    }

    [Fact]
    public void Founding_trait_seeds_and_the_map_shape_the_roll()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Traits.Add("belters");
        var roll = ResearchRolls.Roll(w, Side.Player, new SimRandom(5, 61));
        Assert.Contains("rock-mapping", roll.Leads);
        Assert.Contains("mineral-surveys", roll.Leads);
        Assert.Equal("Founding trait: Belters", roll.Why["rock-mapping"]);
        Assert.DoesNotContain("rock-mapping", roll.Lost);

        w.Map.StarLight = 0.2;
        Assert.True(ResearchRolls.MapHas(w, "dim-star"));
        Assert.Contains("solar-sails", ResearchRolls.Roll(w, Side.Player, new SimRandom(5, 61)).Hard);
    }

    [Fact]
    public void Every_founding_trait_has_research_seeds()
    {
        var traits = new[] { "belters", "torch-born", "patient", "listeners", "silent", "archivists", "shipwrights", "salvagers",
                             "gardeners", "hard-livers", "sun-dwellers", "free-captains" };
        Assert.All(traits, t => Assert.True(ResearchRolls.D.TraitSeeds!.TryGetValue(t, out var seed) && seed.Leads is { Count: > 0 }, t));
    }

    [Fact]
    public void Nothing_in_the_game_data_needs_a_lost_pool_idea()
    {
        // Lost ideas are minor: no idea, ship class, station or fuel may need one, so no line is locked away.
        var needed = new HashSet<string>(GameData.Economy.Research.SelectMany(r => r.Needs ?? new()));
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Lightlag.sln"))) root = Path.GetDirectoryName(root)!;
        foreach (var file in Directory.GetFiles(Path.Combine(root, "src", "Lightlag.Sim", "Data"), "*.json"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"needs\"\\s*:\\s*(\"[^\"]*\"|\\[[^\\]]*\\])"))
                foreach (Match id in Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\""))
                    needed.Add(id.Groups[1].Value);
        Assert.Empty(ResearchRolls.D.LostPool!.Where(needed.Contains));
        Assert.True(ResearchRolls.D.LostPool!.Count >= ResearchRolls.D.LostCount);
    }

    [Fact]
    public void The_rivals_leads_are_known_from_intel_level_3()
    {
        var sim = NewGame();
        var w = sim.World;
        var file = Intel.Of(w, Side.Player, Side.Rival);
        file.Points = Intel.LevelPoints[ResearchRolls.KnownAtLevel] - 1;
        Assert.False(ResearchRolls.KnowsRivalRoll(w, Side.Player, Side.Rival));
        file.Points = Intel.LevelPoints[ResearchRolls.KnownAtLevel];
        Assert.True(ResearchRolls.KnowsRivalRoll(w, Side.Player, Side.Rival));
        Assert.StartsWith("Leads: ", ResearchRolls.Describe(w, Side.Rival));
    }

    [Fact]
    public void Auto_values_a_lead_above_the_same_idea_without_it()
    {
        var on = NewGame();
        var off = NewGame(variety: false);
        var lead = ResearchRolls.Of(on.World, Side.Player).Leads.FirstOrDefault(id => Economy.CanQueue(on.World, Side.Player, OrderKind.Research, id) == null
                                                                                      && Economy.CanQueue(off.World, Side.Player, OrderKind.Research, id) == null);
        if (lead == null) return; // every lead this seed still has unmet needs
        var o = ResearchValue.For(on.World, Side.Player);
        var withLead = ResearchValue.Rank(on.World, Side.Player, o, new[] { lead }).Single().Score;
        var without = ResearchValue.Rank(off.World, Side.Player, ResearchValue.For(off.World, Side.Player), new[] { lead }).Single().Score;
        Assert.True(withLead > without);
    }
}
