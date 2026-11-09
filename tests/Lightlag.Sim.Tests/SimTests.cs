using Lightlag.Sim;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

public class SimTests
{
    private static Simulation Play(ulong seed, int days, GameLog? log = null)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, log ?? GameLog.Memory());
        var pilot = new TestPilot(seed);
        pilot.PlayUntil(sim, days * GameTime.TicksPerDay);
        return sim;
    }

    [Fact]
    public void Same_seed_gives_the_same_game()
    {
        Assert.Equal(Play(42, 60).StateHash(), Play(42, 60).StateHash());
    }

    [Fact]
    public void Different_seeds_give_different_starts()
    {
        var a = Simulation.NewGame(Scenarios.TestSystem, 1, GameLog.Memory());
        var b = Simulation.NewGame(Scenarios.TestSystem, 2, GameLog.Memory());
        Assert.NotEqual(a.StateHash(), b.StateHash());
    }

    [Fact]
    public void Replay_reproduces_the_game_exactly()
    {
        var sim = Play(7, 90);
        Assert.NotEmpty(sim.Replay.Commands);
        var copy = ReplayFile.FromJson(sim.Replay.ToJson());
        var result = ReplayRunner.Run(copy, GameLog.Memory());
        Assert.True(result.Exact, result.Message);
        Assert.False(result.Crashed);
        Assert.Equal(sim.World.Tick, result.ReachedTick);
    }

    [Fact]
    public void Replay_notices_when_the_game_drifts()
    {
        var sim = Play(7, 10);
        sim.Replay.Checkpoints[5] = sim.Replay.Checkpoints[5] with { Hash = "0000000000000000" };
        var result = ReplayRunner.Run(sim.Replay, GameLog.Memory());
        Assert.False(result.Exact);
        Assert.Equal(sim.Replay.Checkpoints[5].Tick, result.ReachedTick);
    }

    [Fact]
    public void Loading_a_save_continues_the_same_game()
    {
        var straight = Play(11, 40);

        var first = Play(11, 20);
        var save = SaveGame.FromJson(first.ToSave().ToJson());
        var loaded = Simulation.FromSave(save, GameLog.Memory());
        var pilot = new TestPilot(11);
        // Bring the pilot's own dice to the same point by replaying its first 20 days of decisions.
        var warmup = Simulation.NewGame(Scenarios.TestSystem, 11, GameLog.Memory());
        pilot.PlayUntil(warmup, 20 * GameTime.TicksPerDay);
        pilot.PlayUntil(loaded, 40 * GameTime.TicksPerDay);

        Assert.Equal(straight.StateHash(), loaded.StateHash());
    }

    [Fact]
    public void Rule_checks_catch_a_broken_state()
    {
        var log = GameLog.Memory();
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, log);
        sim.RuleCheckEveryTicks = 1;
        sim.Issue(new DebugBreakRuleCommand());
        sim.Step();
        Assert.True(sim.RuleViolations > 0);
        Assert.Contains(log.Tail(), l => l.Contains("\"area\":\"rule\"") && l.Contains("positions-finite"));
    }

    [Fact]
    public void Strict_rules_throw()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.Issue(new DebugBreakRuleCommand());
        var ex = Assert.Throws<RuleBrokenException>(() => sim.Step());
        Assert.Equal("positions-finite", ex.Violation.Rule);
    }

    [Fact]
    public void Busy_rules_do_not_flood_the_log()
    {
        var log = GameLog.Memory();
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, log);
        sim.RuleCheckEveryTicks = 1;
        sim.Issue(new DebugBreakRuleCommand());
        sim.StepMany(50);
        Assert.True(sim.RuleHits["positions-finite"] >= 50);
        Assert.True(log.Count(LogLevel.Error) <= 6);
    }

    [Fact]
    public void Healthy_games_break_no_rules()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        var pilot = new TestPilot(5);
        pilot.PlayUntil(sim, 400 * GameTime.TicksPerDay);
        Assert.Equal(0, sim.RuleViolations);
    }

    [Fact]
    public void Planets_move_along_their_orbits()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 9, GameLog.Memory());
        var verdant = sim.World.Bodies.Single(b => b.Name == "Verdant");
        var start = verdant.Position;
        sim.StepMany(365 / 4 * GameTime.TicksPerDay);
        var moved = Vec2.Distance(start, verdant.Position);
        Assert.InRange(moved, 150_000 * 1.3, 150_000 * 1.5); // about a quarter turn: r * sqrt(2)
    }
}
