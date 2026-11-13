using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Plan 3 build 6, Screens and options: pause settings, Ironman saves and the rival's advisor.</summary>
public class ScreensOptionsTests
{
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    // ---- Pause settings -----------------------------------------------------------------------------

    [Fact]
    public void Standard_pause_settings_match_the_old_behaviour()
    {
        var rules = new PauseRules();
        Assert.Equal(PauseRules.Standard, rules.Preset);
        Assert.Equal(PauseResponse.Pause, rules[PauseEvent.EnemySpotted]);
        Assert.Equal(PauseResponse.Pause, rules[PauseEvent.BattleStarts]);
        Assert.Equal(PauseResponse.Pause, rules[PauseEvent.ShipyardAttacked]);
        Assert.Equal(PauseResponse.Notice, rules[PauseEvent.ResearchDone]);
        Assert.Equal(PauseResponse.Notice, rules[PauseEvent.EnemyClosing]);
    }

    [Fact]
    public void Pause_settings_presets_differ_and_survive_the_settings_file()
    {
        var rules = new PauseRules();
        rules.Apply(PauseRules.HandsOff);
        Assert.Equal(PauseRules.HandsOff, rules.Preset);
        Assert.Equal(PauseResponse.Slow, rules[PauseEvent.EnemySpotted]);
        Assert.Equal(PauseResponse.Pause, rules[PauseEvent.BattleStarts]);
        rules[PauseEvent.ResearchDone] = PauseResponse.Pause;
        Assert.Null(rules.Preset); // changed by hand

        var back = PauseRules.Parse(rules.ToText());
        foreach (var e in Enum.GetValues<PauseEvent>()) Assert.Equal(rules[e], back[e]);

        // Broken or unknown lines are skipped and keep the Standard response.
        var odd = PauseRules.Parse("EnemySpotted=Ignore\nNoSuchThing=Pause\nBattleStarts=Sideways\n");
        Assert.Equal(PauseResponse.Ignore, odd[PauseEvent.EnemySpotted]);
        Assert.Equal(PauseResponse.Pause, odd[PauseEvent.BattleStarts]);

        var watchful = new PauseRules();
        watchful.Apply(PauseRules.Watchful);
        Assert.True(Enum.GetValues<PauseEvent>().Count(e => watchful[e] == PauseResponse.Pause)
                    > Enum.GetValues<PauseEvent>().Count(e => rules[e] == PauseResponse.Pause && e != PauseEvent.ResearchDone));
        foreach (var e in Enum.GetValues<PauseEvent>()) Assert.False(string.IsNullOrEmpty(PauseRules.Name(e)));
    }

    // ---- Ironman ------------------------------------------------------------------------------------

    [Fact]
    public void Ironman_keeps_one_save_and_writes_no_others()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path).EnsureCreated();
        var sim = Simulation.NewGame(Scenarios.TestSystem, 31, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal) with { Ironman = true });
        Assert.True(sim.World.Rules.Ironman);
        var a = SaveSlots.WriteAutosave(paths, sim);
        Days(sim, 1);
        var b = SaveSlots.WriteManual(paths, sim);
        var c = SaveSlots.WriteQuick(paths, sim);
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(SaveSlots.IronmanPath(paths, sim.World), a);
        var entry = Assert.Single(SaveSlots.List(paths));
        Assert.Equal("Ironman", entry.Kind);
        // The one save is the latest state, and loads back as an Ironman game.
        var loaded = Simulation.FromSave(SaveGame.Load(a), GameLog.Memory());
        Assert.True(loaded.World.Rules.Ironman);
        Assert.Equal(sim.World.Tick, loaded.World.Tick);
    }

    [Fact]
    public void Ordinary_games_keep_their_usual_saves()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path).EnsureCreated();
        var sim = Simulation.NewGame(Scenarios.TestSystem, 32, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal));
        Assert.False(sim.World.Rules.Ironman);
        SaveSlots.WriteQuick(paths, sim);
        SaveSlots.WriteAutosave(paths, sim);
        Assert.Equal(2, SaveSlots.List(paths).Count);
        Assert.DoesNotContain(SaveSlots.List(paths), e => e.Kind == "Ironman");
    }

    // ---- The rival's advisor --------------------------------------------------------------------------

    [Theory]
    [InlineData("FOCUS: raid | WHY: their freighters run unescorted", "raid", "their freighters run unescorted")]
    [InlineData("**FOCUS:** Expand | WHY: lots of empty worlds", "expand", "lots of empty worlds")]
    [InlineData("<think>maybe hunt? or raid</think>\nFOCUS: defend | WHY: we are outnumbered", "defend", "we are outnumbered")]
    [InlineData("I would research now, and then take colonies.", "research", "")]
    public void Advisor_reads_the_models_answer(string reply, string focus, string why)
    {
        var parsed = Advisor.ParseReply(reply);
        Assert.NotNull(parsed);
        Assert.Equal(focus, parsed!.Value.Focus);
        Assert.Equal(why, parsed.Value.Why);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I cannot help with that.")]
    [InlineData("<think>raid</think> no idea")]
    public void Advisor_answers_without_a_focus_give_no_advice(string? reply) => Assert.Null(Advisor.ParseReply(reply));

    [Fact]
    public void Advisor_brief_holds_only_what_the_rival_knows()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 33, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal));
        Days(sim, 2);
        var w = sim.World;
        var brief = Advisor.Brief(w, Side.Rival);
        foreach (var f in Advisor.Focuses) Assert.Contains(f, brief);
        // Its own fleets are named; the player's fleets it has not seen are not.
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Rival)) Assert.Contains(f.Name, brief);
        var seen = Sensors.Known(w, seat: true).Where(s => s.SeenBy == Side.Rival).Select(s => s.FleetId).ToHashSet();
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && !seen.Contains(f.Id))) Assert.DoesNotContain(f.Name, brief);
    }

    [Fact]
    public void Advice_leans_the_rival_for_a_while_and_replays_exactly()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 34, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        Days(sim, 3);
        var m = Mind.Of(sim.World, Side.Rival)!;
        Assert.True(Advisor.Due(sim.World, Side.Rival));
        sim.Issue(new AdviseCommand(Side.Rival, "Raid", "freighters out alone\nsecond line dropped") { Source = "advisor" });
        sim.Step();
        Assert.Equal("raid", m.Advice);
        Assert.Equal("freighters out alone", m.AdviceWhy);
        Assert.False(Advisor.Due(sim.World, Side.Rival));
        var base0 = Mind.ProfileOf(m.Personality);
        var leaned = Advisor.Apply(base0, m, sim.World.Hour);
        Assert.True(leaned.Raiding > base0.Raiding || base0.Raiding >= 0.9);
        Assert.Equal(Math.Round(base0.Raiding + (0.9 - base0.Raiding) * Advisor.Weight, 3), leaned.Raiding);
        Assert.Contains(m.Thoughts, t => t.Text.StartsWith("Advisor: raid"));

        // Too soon for another, an unknown focus, and the player's side (not computer-run) are all turned down.
        sim.Issue(new AdviseCommand(Side.Rival, "hunt", ""));
        sim.Issue(new AdviseCommand(Side.Player, "hunt", ""));
        sim.Step();
        Assert.Equal("raid", m.Advice);
        Assert.Equal(1, m.AdviceCount);

        // It wears off.
        Days(sim, Advisor.NudgeDays + 0.5);
        Assert.Equal(base0, Advisor.Apply(base0, m, sim.World.Hour));
        Days(sim, Advisor.GapDays - Advisor.NudgeDays);
        Assert.True(Advisor.Due(sim.World, Side.Rival));
        sim.Issue(new AdviseCommand(Side.Rival, "nonsense", ""));
        sim.Step();
        Assert.Equal(1, m.AdviceCount);

        var replay = ReplayRunner.Run(ReplayFile.FromJson(sim.Replay.ToJson()), GameLog.Memory());
        Assert.True(replay.Exact, replay.Message);
    }

    [Fact]
    public void Every_idea_has_its_own_picture_or_a_shared_one()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Lightlag.sln"))) root = Path.GetDirectoryName(root)!;
        var ideas = Path.Combine(root, "game", "Art", "Ideas");
        var own = 0;
        foreach (var r in GameData.Economy.Research)
        {
            var has = File.Exists(Path.Combine(ideas, r.Id + ".svg"));
            if (has) own++;
            Assert.True(has || File.Exists(Path.Combine(ideas, (string.IsNullOrEmpty(r.Picture) ? "engine" : r.Picture) + ".svg")), $"{r.Id} has no picture");
        }
        Assert.True(own >= GameData.Economy.Research.Count - 6, $"only {own} ideas have their own picture");
    }
}
