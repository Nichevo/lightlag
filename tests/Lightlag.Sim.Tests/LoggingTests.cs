using Lightlag.Sim;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Xunit;

namespace Lightlag.Sim.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lightlag-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class LoggingTests
{
    [Fact]
    public void Log_lines_carry_build_seed_and_game_time()
    {
        using var dir = new TempDir();
        var file = System.IO.Path.Combine(dir.Path, "log.jsonl");
        using (var log = new GameLog(new BuildInfo("0.0.7", "abc1234", ""), file, LogLevel.Debug))
        {
            var sim = Simulation.NewGame(Scenarios.TestSystem, 123, log);
            sim.StepMany(GameTime.TicksPerDay * 3 + 5);
            log.Warn("ui", "Something odd", new { panel = "fleet", count = 2 });
        }
        var last = GameLog.Parse(File.ReadLines(file).Last())!;
        Assert.Equal(LogLevel.Warn, last.Level);
        Assert.Equal("0.0.7", last.Build);
        Assert.Equal(123UL, last.Seed);
        Assert.Equal(3 * 24 + 5, last.Tick);
        Assert.Equal("ui", last.Area);
        Assert.Equal("fleet", last.Data!["panel"]!.GetValue<string>());
    }

    [Fact]
    public void Crash_report_holds_save_replay_and_log()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path).EnsureCreated();
        var log = GameLog.Memory();
        var sim = Simulation.NewGame(Scenarios.TestSystem, 77, log);
        sim.StepMany(30);
        sim.Issue(new DebugCrashCommand("test"));
        var ex = Assert.Throws<InvalidOperationException>(() => sim.Step());

        var folder = Reports.WriteCrash(paths, log, sim, ex, "unit test");

        foreach (var f in new[] { "README.md", "error.txt", "save.json", "replay.json", "log-tail.jsonl" })
            Assert.True(File.Exists(System.IO.Path.Combine(folder, f)), $"missing {f}");
        var readme = File.ReadAllText(System.IO.Path.Combine(folder, "README.md"));
        Assert.Contains("Seed: 77", readme);
        Assert.Contains("Test crash on purpose", readme);

        // The replay in the report reproduces the crash.
        var replay = ReplayFile.Load(System.IO.Path.Combine(folder, "replay.json"));
        var result = ReplayRunner.Run(replay, GameLog.Memory());
        Assert.True(result.Crashed, result.Message);
        Assert.Equal(30, result.ReachedTick);
    }

    [Fact]
    public void Player_report_writes_note()
    {
        using var dir = new TempDir();
        var paths = new DataPaths(dir.Path).EnsureCreated();
        var log = GameLog.Memory();
        var sim = Simulation.NewGame(Scenarios.TestSystem, 1, log);
        var folder = Reports.WritePlayerReport(paths, log, sim, "Frigate flew through the sun", "game");
        Assert.Contains("Frigate flew through the sun", File.ReadAllText(System.IO.Path.Combine(folder, "README.md")));
        Assert.True(File.Exists(System.IO.Path.Combine(folder, "save.json")));
    }

    [Fact]
    public void Random_is_stable_across_versions()
    {
        // These are the published PCG32 reference values for seed 42, stream 54.
        // If this fails, every old replay and save breaks. Only change it on purpose.
        var rng = new SimRandom(42);
        Assert.Equal(new uint[] { 0xa15c02b7, 0x7b47f409, 0xba1d3330 },
                     new[] { rng.NextUInt(), rng.NextUInt(), rng.NextUInt() });
    }
}
