using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The research review: ideas scored by aims, situation and race; split ideas; small % ideas; the computer's research.</summary>
public class ResearchValueTests
{
    private static Simulation NewGame(ulong seed = 8, GameOptions? options = null) =>
        Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: (options ?? new GameOptions()) with { Budget = false }); // one pool: these tests predate the Industry budget (see BudgetTests)

    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    [Fact]
    public void Every_idea_has_some_value_for_every_personality_and_race()
    {
        var sim = NewGame();
        foreach (var p in Enum.GetValues<Personality>())
            foreach (var race in Races.All)
            {
                var outlook = new ResearchValue.Outlook(ResearchValue.Weights(Mind.ProfileOf(p)), new(), race);
                foreach (var r in GameData.Economy.Research.Where(r => !r.Later))
                    Assert.True(ResearchValue.Base(sim.World, Side.Player, r, outlook) > 0.05, $"{r.Id} is worth nothing to a {p} {race.Id}");
            }
    }

    [Fact]
    public void Hunger_raises_food_ideas()
    {
        var sim = NewGame();
        var w = sim.World;
        var calm = ResearchValue.For(w, Side.Player) with { Situation = new() };
        var hungry = calm with { Situation = new() { ["food"] = 2.0 } };
        var hydroponics = GameData.Research(Tech.Hydroponics);
        Assert.True(ResearchValue.Base(w, Side.Player, hydroponics, hungry) > 1.5 * ResearchValue.Base(w, Side.Player, hydroponics, calm));
    }

    [Fact]
    public void Seen_enemy_missiles_raise_point_defence()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.DoesNotContain(ResearchValue.Read(w, Side.Player), n => n.Aim == "anti-missile");
        Intel.Of(w, Side.Player).Tech.Add(new TechFact { Part = "missiles", Level = 3 });
        var needs = ResearchValue.Read(w, Side.Player);
        Assert.Contains(needs, n => n.Aim == "anti-missile" && n.Factor > 1.3);
        Assert.Equal("defence", ResearchValue.Pressing(needs)?.Aim);
    }

    [Fact]
    public void A_machine_race_cares_little_for_crew_and_food_ideas()
    {
        var sim = NewGame();
        var w = sim.World;
        var weights = ResearchValue.Weights(null);
        var human = new ResearchValue.Outlook(weights, new(), Races.Get(Races.Human));
        var lattice = new ResearchValue.Outlook(weights, new(), Races.Get("lattice"));
        foreach (var id in new[] { Tech.CrewDrills, Tech.Hydroponics, Tech.FoodRecyclers })
            Assert.True(ResearchValue.Base(w, Side.Player, GameData.Research(id), lattice) < 0.5 * ResearchValue.Base(w, Side.Player, GameData.Research(id), human), id);
    }

    [Fact]
    public void Split_ideas_add_up_to_about_the_old_big_ones()
    {
        var sim = NewGame();
        var w = sim.World;
        var done = Economy.Of(w, Side.Player).Researched;
        done.AddRange(new[] { Tech.Automation, Tech.LongSensors, Tech.DeflectorGrid, Tech.PointDefence2 });
        Assert.Equal(1.1, Tech.IndustryFactor(w, Side.Player), 6);
        Assert.Equal(1.5, Tech.SensorReach(w, Side.Player), 6);
        done.AddRange(new[] { Tech.Automation2, Tech.Automation3, Tech.LongSensors2, Tech.ShieldHarmonics, Tech.TrackingRadars });
        Assert.InRange(Tech.IndustryFactor(w, Side.Player), 1.3, 1.34);
        Assert.InRange(Tech.SensorReach(w, Side.Player), 2.0, 2.1);
        Assert.InRange(Tech.ShieldFactor(w, Side.Player), 1.49, 1.51);
        Assert.InRange(Tech.PointDefence(w, Side.Player, 2), 1.29, 1.31);
    }

    [Fact]
    public void Small_ideas_change_food_mines_and_happiness()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = w.Bodies.First(b => b.Owner == Side.Player && b.Home);
        var food = Planets.FoodMadePerDay(home);
        var eat = Planets.FoodNeedPerDay(home);
        var (happy, _) = Planets.TargetHappiness(w, home);
        Economy.Of(w, Side.Player).Researched.AddRange(new[] { Tech.Hydroponics, Tech.NutritionScience, Tech.CivicPlanning });
        Tech.Refresh(w);
        Assert.Equal(food * 1.1, Planets.FoodMadePerDay(home), 6);
        Assert.Equal(eat * 0.9, Planets.FoodNeedPerDay(home), 6);
        Assert.True(Planets.TargetHappiness(w, home).Target >= Math.Min(100, happy + 5) - 1e-6);
    }

    [Fact]
    public void Player_research_automation_picks_a_counter_to_what_the_enemy_fields()
    {
        var sim = NewGame();
        var w = sim.World;
        var calm = Planner.NextIdea(w, Side.Player, null);
        Assert.NotNull(calm);
        Intel.Of(w, Side.Player).Tech.Add(new TechFact { Part = "missiles", Level = 3 });
        var next = Planner.NextIdea(w, Side.Player, null)!;
        Assert.Contains("anti-missile", GameData.Research(next).Aims!);
    }

    [Fact]
    public void The_computer_spreads_its_research_and_builds_up_its_teams()
    {
        var sim = NewGame(11, GameOptions.AiVsAi());
        Days(sim, 200);
        var w = sim.World;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var state = Economy.Of(w, side);
            Assert.True(state.Teams.Count >= 2, $"{side} has {state.Teams.Count} teams");
            Assert.True(state.Teams.Sum(t => t.Scientists + t.TrainingScientists) > Research.T.StartScientists + Research.T.FoundScientists,
                $"{side} trained no scientists");
        }
        // The two sides do not research the same ideas in the same order.
        var mine = w.Choices.Where(c => c.Side == Side.Player && c.Kind == "research").Select(c => c.What).ToList();
        var theirs = w.Choices.Where(c => c.Side == Side.Rival && c.Kind == "research").Select(c => c.What).ToList();
        Assert.NotEqual(mine, theirs);
    }
}
