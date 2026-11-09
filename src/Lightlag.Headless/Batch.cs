using System.Diagnostics;
using System.Text;
using Lightlag.Sim;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;

namespace Lightlag.Headless;

public sealed record GameResult(
    int Index, ulong Seed, string Outcome, long Ticks, double Seconds, int RuleFailures,
    int Errors, int Warnings, string? Error, string? ReportFolder);

/// <summary>Runs many games back to back and writes the morning summary.</summary>
public static class Batch
{
    public static int Run(Options opts, bool nightly)
    {
        var build = Cli.LoadBuild();
        var data = Cli.DataFolder(opts);
        var games = (int)(opts.GetLong("games") ?? (nightly ? 200 : 10));
        var days = (int)(opts.GetLong("days") ?? (nightly ? 365 : 120));
        var minutes = opts.GetLong("minutes") ?? (nightly ? 360 : 60);
        var scenario = opts.Get("scenario") ?? Scenarios.TestSystem;
        var strict = !opts.Has("lenient");
        var firstSeed = opts.GetULong("seed") ?? (ulong)DateTime.UtcNow.Ticks % 1_000_000_000UL;

        var started = DateTime.UtcNow;
        var runDir = Path.Combine(data.Nightly, DataPaths.StampedName(started, nightly ? "nightly" : "batch"));
        var runPaths = new DataPaths(runDir).EnsureCreated();
        var logPath = Path.Combine(runDir, "log.jsonl");
        Console.WriteLine($"Lightlag {build}: {games} games of {days} days, {scenario}, first seed {firstSeed}, writing to {runDir}");

        var results = new List<GameResult>();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < games; i++)
        {
            if (clock.Elapsed.TotalMinutes >= minutes)
            {
                Console.WriteLine($"Time budget of {minutes} minutes used; stopping after {i} games.");
                break;
            }
            var seed = firstSeed + (ulong)i;
            var r = PlayOne(i + 1, seed, scenario, days, strict, build, logPath, runPaths);
            results.Add(r);
            Console.WriteLine($"  game {r.Index,4}  seed {r.Seed,-12} {r.Outcome,-9} {r.Seconds,7:0.00}s  rules {r.RuleFailures}  errors {r.Errors}"
                              + (r.Error != null ? $"  {r.Error}" : ""));
        }

        var summary = Summarise(build, scenario, days, strict, started, clock.Elapsed, games, results, runDir);
        File.WriteAllText(Path.Combine(runDir, "summary.md"), summary);
        File.WriteAllText(Path.Combine(runDir, "summary.json"), Json.Serialize(new
        {
            build = build.ToString(), scenario, days, strict, started = started.ToString("o"),
            seconds = clock.Elapsed.TotalSeconds, requested = games, results,
        }, indented: true));
        File.WriteAllText(Path.Combine(data.Nightly, "latest-summary.md"), summary);
        File.WriteAllText(Path.Combine(data.Nightly, "latest-summary.json"), Json.Serialize(new
        {
            folder = runDir, build = build.ToString(), finished = DateTime.UtcNow.ToString("o"),
            games = results.Count,
            crashed = results.Count(r => r.Outcome != "ok"),
            ruleFailures = results.Sum(r => r.RuleFailures),
            averageSeconds = results.Count > 0 ? results.Average(r => r.Seconds) : 0,
        }, indented: true));

        Console.WriteLine();
        Console.WriteLine(summary);
        return results.Any(r => r.Outcome != "ok") ? 1 : 0;
    }

    private static GameResult PlayOne(int index, ulong seed, string scenario, int days, bool strict,
                                      BuildInfo build, string logPath, DataPaths runPaths)
    {
        using var log = new GameLog(build, logPath);
        var timer = Stopwatch.StartNew();
        Simulation? sim = null;
        try
        {
            sim = Simulation.NewGame(scenario, seed, log, build);
            sim.StrictRules = strict;
            sim.RuleCheckEveryTicks = 1;
            var pilot = new TestPilot(seed);
            var end = (long)days * GameTime.TicksPerDay;
            while (sim.World.Tick < end)
            {
                pilot.Act(sim);
                sim.Step();
            }
            log.Info("game", $"Game finished at {GameTime.Format(sim.World.Tick)}", new { hash = sim.StateHash() });
            return new GameResult(index, seed, "ok", sim.World.Tick, timer.Elapsed.TotalSeconds, sim.RuleViolations,
                log.Count(LogLevel.Error), log.Count(LogLevel.Warn), null, null);
        }
        catch (Exception ex)
        {
            var folder = Reports.WriteCrash(runPaths, log, sim, ex, $"headless game {index}");
            var outcome = ex is RuleBrokenException ? "rule" : "crash";
            return new GameResult(index, seed, outcome, sim?.World.Tick ?? 0, timer.Elapsed.TotalSeconds,
                sim?.RuleViolations ?? 0, log.Count(LogLevel.Error), log.Count(LogLevel.Warn),
                $"{ex.GetType().Name}: {ex.Message}", folder);
        }
    }

    public static string Summarise(BuildInfo build, string scenario, int days, bool strict, DateTime started,
                                   TimeSpan elapsed, int requested, List<GameResult> results, string runDir)
    {
        var bad = results.Where(r => r.Outcome != "ok").ToList();
        var md = new StringBuilder();
        md.AppendLine($"# Lightlag overnight summary, {started.ToLocalTime():ddd d MMM yyyy HH:mm}");
        md.AppendLine();
        md.AppendLine(bad.Count == 0
            ? $"**All {results.Count} games finished cleanly.**"
            : $"**{bad.Count} of {results.Count} games failed.** Details below.");
        md.AppendLine();
        md.AppendLine($"- Build: {build}");
        md.AppendLine($"- Scenario: {scenario}, {days} game days each, rule checks every tick ({(strict ? "strict" : "lenient")})");
        md.AppendLine($"- Games: {results.Count} played of {requested} asked for, in {elapsed:hh\\:mm\\:ss}");
        if (results.Count > 0)
        {
            var slowest = results.MaxBy(r => r.Seconds)!;
            md.AppendLine($"- Time per game: average {results.Average(r => r.Seconds):0.00}s, slowest {slowest.Seconds:0.00}s (seed {slowest.Seed}). Target is under 60s.");
            md.AppendLine($"- Rule failures: {results.Sum(r => r.RuleFailures)}; log errors: {results.Sum(r => r.Errors)}; warnings: {results.Sum(r => r.Warnings)}");
        }
        md.AppendLine($"- Folder: `{runDir}`");
        if (bad.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Failed games");
            md.AppendLine();
            md.AppendLine("| Game | Seed | What | Game time | Error | Report |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var r in bad)
                md.AppendLine($"| {r.Index} | {r.Seed} | {r.Outcome} | {GameTime.Format(r.Ticks)} | {r.Error?.Replace("|", "/")} | `{Path.GetFileName(r.ReportFolder)}` |");
            md.AppendLine();
            md.AppendLine("Each report folder has a README, save.json and replay.json. Replay one with `Lightlag.Headless replay <folder>/replay.json`.");
        }
        return md.ToString();
    }
}
