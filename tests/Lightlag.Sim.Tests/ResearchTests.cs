using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class ResearchTests
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
    private static void Hour(Simulation sim) => sim.StepMany((int)Math.Round(GameTime.TicksPerDay / 24.0));

    [Fact]
    public void A_game_starts_with_one_general_team_and_empty_slots()
    {
        var sim = NewGame();
        Hour(sim);
        var teams = Player(sim).Teams;
        Assert.Single(teams);
        Assert.Equal(Research.T.StartScientists, teams[0].Scientists);
        Assert.All(teams[0].Skill, k => Assert.Equal(1, k));
        Assert.True(Research.T.Slots > teams.Count);
    }

    [Fact]
    public void Research_costs_scientist_time_not_Industry()
    {
        var sim = NewGame();
        var idea = GameData.ResearchById.Values.OrderBy(r => r.Id, StringComparer.Ordinal).First(r => !r.Later && Economy.CanQueue(sim.World, Side.Player, OrderKind.Research, r.Id) == null && r.Needs is not { Count: > 0 } && r.Materials is not { Count: > 0 });
        Economy.Of(sim.World, Side.Player).Industry = 0;
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, idea.Id));
        Hour(sim);
        Hour(sim);
        var order = sim.World.Orders.Single(o => o.Key == idea.Id);
        Assert.True(order.Paid);
        Assert.Null(order.Waiting);
        Assert.True(order.HoursLeft < order.TotalHours);
    }

    [Fact]
    public void Twice_the_scientists_is_about_one_and_two_thirds_the_speed()
    {
        Assert.Equal(1, Research.SizeFactor(Research.T.BaseScientists), 9);
        var two = Research.SizeFactor(Research.T.BaseScientists * 2);
        Assert.InRange(two, 1.6, 1.75);
    }

    [Fact]
    public void Finishing_an_idea_raises_the_team_skill_in_its_field_and_others_fade()
    {
        var sim = NewGame();
        Hour(sim);
        Player(sim).Industry = 10000;
        var team = Player(sim).Teams[0];
        team.Skill[Research.FieldIndex("weapons")] = 1.5;           // learned earlier
        sim.Issue(new SetTeamKeptCommand(Side.Player, team.Id, new List<string> { "drives" }));
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, Tech.EfficientDrives));
        Days(sim, Research.BaseHours(sim.World, Side.Player, Tech.EfficientDrives) / 24 + 1);
        Assert.Contains(Tech.EfficientDrives, Player(sim).Researched);
        Assert.Equal(1 + Research.T.SkillPerIdea, team.Skill[Research.FieldIndex("drives")], 6);
        Assert.True(team.Skill[Research.FieldIndex("weapons")] < 1.5, "a field the team doesn't keep fades");
        Assert.Equal(1, team.Ideas);
    }

    [Fact]
    public void A_skilled_team_researches_faster()
    {
        var sim = NewGame();
        Hour(sim);
        Player(sim).Industry = 10000;
        var team = Player(sim).Teams[0];
        team.Skill[Research.FieldIndex("drives")] = 2;
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, Tech.EfficientDrives));
        Days(sim, Research.BaseHours(sim.World, Side.Player, Tech.EfficientDrives) / 24 / 2 + 0.5);
        Assert.Contains(Tech.EfficientDrives, Player(sim).Researched);
    }

    [Fact]
    public void Ideas_wait_for_the_ideas_they_need()
    {
        var sim = NewGame();
        Hour(sim);
        Assert.NotNull(Economy.CanQueue(sim.World, Side.Player, OrderKind.Research, Tech.EngineMk3));
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Research, Tech.EngineMk2));
        Hour(sim);
        Assert.Null(Economy.CanQueue(sim.World, Side.Player, OrderKind.Research, Tech.EngineMk3));
    }

    [Fact]
    public void Founding_a_team_costs_industry_and_takes_time_and_it_starts_skilled()
    {
        var sim = NewGame();
        Hour(sim);
        Player(sim).Industry = 1000;
        sim.Issue(new FoundTeamCommand(Side.Player, "weapons"));
        Hour(sim);
        var teams = Player(sim).Teams;
        Assert.Equal(2, teams.Count);
        var team = teams[1];
        Assert.Equal("Second team", team.Name);
        Assert.Equal(Research.T.FoundSkill, team.Skill[Research.FieldIndex("weapons")]);
        Assert.False(Research.Ready(sim.World, team));
        Assert.True(Player(sim).Industry < 1000 - Research.T.FoundCost + 50);
        Days(sim, Research.T.FoundDays + 0.1);
        Assert.True(Research.Ready(sim.World, team));
    }

    [Fact]
    public void Splitting_a_team_moves_half_the_scientists_and_half_the_skill()
    {
        var sim = NewGame();
        Hour(sim);
        var first = Player(sim).Teams[0];
        first.Skill[Research.FieldIndex("drives")] = 2;
        sim.Issue(new SplitTeamCommand(Side.Player, first.Id, "sensors"));
        Hour(sim);
        var teams = Player(sim).Teams;
        Assert.Equal(2, teams.Count);
        var second = teams[1];
        Assert.Equal(Research.T.StartScientists, first.Scientists + second.Scientists);
        Assert.Equal(1 + 1 * Research.T.SplitShare, second.Skill[Research.FieldIndex("drives")], 2);
        Assert.Equal(new[] { "sensors" }, second.Kept);
    }

    [Fact]
    public void Training_adds_scientists_after_the_training_time()
    {
        var sim = NewGame();
        Hour(sim);
        Player(sim).Industry = 1000;
        var team = Player(sim).Teams[0];
        sim.Issue(new TrainTeamCommand(Side.Player, team.Id));
        Days(sim, Research.T.TrainDays + 0.2);
        Assert.Equal(Research.T.StartScientists + Research.T.TrainScientists, team.Scientists);
    }

    [Fact]
    public void All_slots_full_refuses_a_new_team()
    {
        var sim = NewGame();
        Hour(sim);
        Player(sim).Industry = 10000;
        for (var i = 0; i < Research.T.Slots + 1; i++) sim.Issue(new FoundTeamCommand(Side.Player, "drives"));
        Hour(sim);
        Assert.Equal(Research.T.Slots, Player(sim).Teams.Count);
        Assert.NotNull(Research.CanFound(sim.World, Side.Player, "drives"));
    }

    [Fact]
    public void Holding_orbit_at_a_body_surveys_it_and_speeds_up_an_idea()
    {
        var sim = NewGame();
        Days(sim, 2);
        var home = Economy.ShipyardOf(sim.World, Side.Player)!;
        Assert.True(home.Surveyed[(int)Side.Player], "the start fleet sits at home, so home gets surveyed");
        var find = Assert.Single(Player(sim).Finds, f => f.BodyId == home.Id);
        Assert.Contains(find.Idea, Player(sim).Cuts);
        Assert.Equal(GameData.Research(find.Idea).Days * Research.Pace(GameData.Research(find.Idea).Tier) * 24 * (1 - Research.T.SurveyCut), Research.BaseHours(sim.World, Side.Player, find.Idea), 6);
    }

    [Fact]
    public void Every_idea_has_a_known_field_and_needs()
    {
        // The research web: 121 ideas in six fields, 14 of them "later" nodes (transport and orbit built solar and laser sails and the
        // space elevator, and added satellites and the skyhook; the Lattice build brought in Decode alien signals for contact).
        Assert.Equal(121, GameData.Economy.Research.Count);
        Assert.Equal(14, GameData.Economy.Research.Count(r => r.Later));
        Assert.All(Research.Fields, f => Assert.Contains(GameData.Economy.Research, r => r.Field == f));
        foreach (var r in GameData.Economy.Research)
        {
            Assert.True(Research.FieldIndex(r.Field) >= 0, r.Id);
            Assert.InRange(r.Tier, 1, 5);
            // Only "later" ideas may need a "later" idea (otherwise it could never be researched).
            if (!r.Later) Assert.All(r.Needs ?? new List<string>(), n => Assert.False(GameData.Research(n).Later, $"{r.Id} needs later {n}"));
            foreach (var (good, _) in r.Materials ?? new()) Assert.Contains(Colonies.Goods, g => g.Id == good);
            foreach (var n in r.Needs ?? new List<string>()) Assert.True(GameData.ResearchById.ContainsKey(n), $"{r.Id} needs {n}");
            Assert.False(string.IsNullOrEmpty(r.Unlocks), r.Id);
        }
    }
}
