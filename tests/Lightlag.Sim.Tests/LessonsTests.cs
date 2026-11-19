using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The rival learning from past games: the memory, what it learns, and how a new game uses it.</summary>
public class LessonsTests
{
    private static Simulation NewGame(ulong seed, GameOptions options)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static readonly string Now = GameData.BalanceStamp;

    private static Dictionary<string, string> Style(string missiles = "even guns", string odds = "usual odds") => new()
    {
        ["missiles"] = missiles, ["field"] = "drives research first", ["escorts"] = "usual escorts", ["bigShips"] = "usual big ships",
        ["odds"] = odds, ["pull"] = "pull back Standard",
    };

    /// <summary>An overnight game: the winner a given personality and style, the loser another.</summary>
    private static GameLesson Overnight(int i, string winner, string loser, Dictionary<string, string>? winStyle = null, Dictionary<string, string>? loseStyle = null,
        string balance = "", List<BattleLesson>? battles = null) =>
        new($"g{i}", "test", balance == "" ? Now : balance, GameLesson.Overnight, 200, "Player", "test",
            new List<SideLesson>
            {
                new(Side.Player, true, "human", winner, winStyle ?? Style(), 1, 0.8, battles ?? new List<BattleLesson>()),
                new(Side.Rival, true, "human", loser, loseStyle ?? Style(), 0, 0.2, new List<BattleLesson>()),
            }, null);

    private static GameLesson Played(int i, PlayerHabits habits) =>
        new($"p{i}", "test", Now, GameLesson.Played, 200, "Player", "test",
            new List<SideLesson>
            {
                new(Side.Player, false, "human", null, null, 1, 0.8, new List<BattleLesson>()),
                new(Side.Rival, true, "human", "Turtle", Style(), 0, 0.2, new List<BattleLesson>()),
            }, habits);

    [Fact]
    public void The_balance_stamp_is_a_steady_fingerprint()
    {
        Assert.Matches("^[0-9a-f]{16}$", GameData.BalanceStamp);
        Assert.Equal(GameData.BalanceStamp, GameData.BalanceStamp);
    }

    [Fact]
    public void Nothing_is_learned_with_learning_off_or_an_empty_memory()
    {
        var mem = new RivalMemory();
        Assert.Null(Lessons.Learn(mem, LearnMode.All, Now));
        mem.Add(Overnight(1, "Raider", "Turtle"));
        Assert.Null(Lessons.Learn(mem, LearnMode.Off, Now));
        Assert.NotNull(Lessons.Learn(mem, LearnMode.All, Now));
    }

    [Fact]
    public void The_memory_keeps_each_game_once_and_drops_the_oldest()
    {
        var mem = new RivalMemory();
        Assert.True(mem.Add(Overnight(1, "Raider", "Turtle")));
        Assert.False(mem.Add(Overnight(1, "Raider", "Turtle")));
        for (var i = 2; i <= RivalMemory.MaxGames + 5; i++) mem.Add(Overnight(i, "Raider", "Turtle"));
        Assert.Equal(RivalMemory.MaxGames, mem.Games.Count);
        Assert.Equal("g6", mem.Games[0].Id);
    }

    [Fact]
    public void The_memory_saves_loads_and_forgets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lightlag-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Empty(RivalMemory.Load(dir).Games);
            Assert.True(RivalMemory.Remember(dir, Overnight(1, "Raider", "Turtle")));
            Assert.False(RivalMemory.Remember(dir, Overnight(1, "Raider", "Turtle")));
            var back = RivalMemory.Load(dir);
            Assert.Equal("Raider", Assert.Single(back.Games).Sides[0].Personality);
            File.WriteAllText(RivalMemory.PathIn(dir), "not json");
            Assert.Empty(RivalMemory.Load(dir).Games);
            RivalMemory.Forget(dir);
            Assert.False(File.Exists(RivalMemory.PathIn(dir)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Winning_personalities_and_bands_are_favoured()
    {
        var mem = new RivalMemory();
        for (var i = 0; i < 10; i++)
            mem.Add(Overnight(i, "Expander", "Raider", Style(missiles: "more missiles"), Style(missiles: "more railguns")));
        var learned = Lessons.Learn(mem, LearnMode.Overnight, Now)!;
        var weights = learned.Personalities!["human"];
        Assert.True(weights["Expander"] > 1.3);
        Assert.True(weights["Raider"] < 0.7);
        Assert.Equal("more missiles", learned.Style!["missiles"]);
        // Bands every game shared teach nothing.
        Assert.False(learned.Style.ContainsKey("field"));
        Assert.Contains(learned.Notes!, n => n.Contains("Expander"));
    }

    [Fact]
    public void Games_from_an_older_balance_count_for_less_and_then_not_at_all()
    {
        var mem = new RivalMemory();
        // Old balance: Raider always won. This balance: Expander always wins, in fewer games.
        for (var i = 0; i < 12; i++) mem.Add(Overnight(i, "Raider", "Expander", balance: "old"));
        for (var i = 12; i < 18; i++) mem.Add(Overnight(i, "Expander", "Raider"));
        var w = Lessons.Learn(mem, LearnMode.Overnight, Now)!.Personalities!["human"];
        Assert.True(w["Expander"] > w["Raider"]);
        // Once enough games are on this balance, the old ones are left out altogether.
        for (var i = 18; i < 18 + Lessons.FreshEnough; i++) mem.Add(Overnight(i, "Turtle", "Hunter"));
        var fresh = Lessons.Learn(mem, LearnMode.Overnight, Now)!;
        Assert.Equal(Lessons.FreshEnough + 6, fresh.Games);
    }

    [Fact]
    public void Fights_lost_at_good_odds_make_it_more_careful()
    {
        var mem = new RivalMemory();
        var lost = Enumerable.Range(0, 12).Select(_ => new BattleLesson(1.2, "lost")).ToList();
        for (var i = 0; i < 2; i++) mem.Add(Overnight(i, "Raider", "Turtle", battles: lost));
        var learned = Lessons.Learn(mem, LearnMode.Overnight, Now)!;
        Assert.True(learned.Odds > 1.05);
        Assert.InRange(learned.Odds!.Value, 0.85, 1.15);
    }

    [Fact]
    public void Overnight_only_ignores_the_persons_games()
    {
        var mem = new RivalMemory();
        for (var i = 0; i < 4; i++) mem.Add(Played(i, new PlayerHabits(20, 10, 0.8, 10, 3, 2, 60)));
        Assert.Null(Lessons.Learn(mem, LearnMode.Overnight, Now));
        var all = Lessons.Learn(mem, LearnMode.All, Now)!;
        Assert.NotNull(all.Counters);
    }

    [Fact]
    public void The_persons_habits_get_counters()
    {
        var mem = new RivalMemory();
        for (var i = 0; i < 3; i++) mem.Add(Played(i, new PlayerHabits(20, 10, 0.8, 10, 3, 2, 60)));
        var c = Lessons.Learn(mem, LearnMode.All, Now)!.Counters!;
        Assert.True(c.Defence > 0 && c.Defence <= 0.2);
        Assert.True(c.Escorts > 0);
        Assert.Equal("defence", c.ResearchAim);
        // One game is not enough to read a habit.
        var one = new RivalMemory();
        one.Add(Played(0, new PlayerHabits(20, 10, 0.8, 10, 3, 2, 60)));
        Assert.Null(Lessons.Learn(one, LearnMode.All, Now)?.Counters);
    }

    [Fact]
    public void A_new_game_takes_the_counters_against_a_person_and_stays_in_range()
    {
        var learned = new Learned(5, 5, Counters: new Counters(Defence: 0.2, Escorts: 0.5, ResearchAim: "defence"),
            Notes: new List<string>(), CounterNotes: new List<string> { "guards its colonies sooner" });
        var sim = NewGame(11, GameOptions.VsRival(Difficulty.Normal) with { Learned = learned });
        var m = Assert.Single(sim.World.Minds);
        Assert.NotNull(m.Counter);
        Assert.InRange(m.Lean.Escorts, 0.4, 1.6);
        Assert.Equal(1.3, m.ResearchTaste[ResearchValue.BroadIndex("defence")]);
        Assert.Equal(Math.Min(1, Mind.ProfileOf(m.Personality).Defence + 0.2), Mind.BaseProfile(m).Defence, 6);
        Assert.Contains("guards its colonies sooner", Lessons.Summary(m));
        Assert.Contains(sim.World.Choices, c => c.Kind == "learned");
        Assert.Equal(6, m.Style.Count);

        // Computer against computer: no person to counter.
        var ai = NewGame(11, GameOptions.AiVsAi() with { Learned = learned });
        Assert.All(ai.World.Minds, x => Assert.Null(x.Counter));
        Assert.All(ai.World.Minds, x => Assert.DoesNotContain("guards its colonies sooner", x.LearnedNotes));
    }

    [Fact]
    public void Learned_styles_keep_leanings_inside_their_rolled_ranges()
    {
        var learned = new Learned(20, 0, Style: Style(missiles: "more missiles", odds: "careful odds"),
            Odds: 1.15, Notes: new List<string>());
        var picked = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var m = NewGame(seed, GameOptions.VsRival(Difficulty.Normal) with { Learned = learned }).World.Minds[0];
            Assert.InRange(m.Lean.Missiles, -0.2, 0.2);
            Assert.InRange(m.Lean.Odds, 0.85, 1.15);
            Assert.InRange(m.Lean.Escorts, 0.4, 1.6);
            if (m.Style["missiles"] == "more missiles") picked++;
        }
        // Two in three take the learned band (some random rolls land there too); the rest still explore.
        Assert.InRange(picked, 17, 29);
    }

    [Fact]
    public void A_game_with_lessons_replays_exactly_and_saves()
    {
        var learned = new Learned(5, 2, new Dictionary<string, Dictionary<string, double>> { ["*"] = new() { ["Expander"] = 1.5 } },
            Style(missiles: "more missiles"), 1.1, new Counters(Defence: 0.1), new List<string> { "prefers more missiles" }, new List<string> { "x" });
        var sim = NewGame(21, GameOptions.VsRival(Difficulty.Normal) with { Learned = learned });
        sim.StepMany(3 * GameTime.TicksPerDay);
        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(sim.World.Minds[0].Counter, loaded.World.Minds[0].Counter);
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void A_finished_game_is_written_down_without_the_map()
    {
        var sim = NewGame(3, GameOptions.VsRival(Difficulty.Normal));
        sim.StepMany(5 * GameTime.TicksPerDay);
        var g = Lessons.Of(sim, GameLesson.Played, "test");
        Assert.Equal(Now, g.Balance);
        Assert.Equal("test", g.Build);
        Assert.Equal(2, g.Sides.Count);
        Assert.False(g.Sides.Single(s => s.Side == Side.Player).Computer);
        var rival = g.Sides.Single(s => s.Side == Side.Rival);
        Assert.True(rival.Computer);
        Assert.Equal(6, rival.Style!.Count);
        Assert.NotNull(g.Player);
        // Nothing names a planet, a place or the seed.
        var json = Json.Serialize(g);
        foreach (var b in sim.World.Bodies) Assert.DoesNotContain($"\"{b.Name}\"", json);
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);

        var ai = NewGame(3, GameOptions.AiVsAi());
        ai.StepMany(GameTime.TicksPerDay);
        Assert.Null(Lessons.Of(ai, GameLesson.Overnight, "test").Player);
    }
}
