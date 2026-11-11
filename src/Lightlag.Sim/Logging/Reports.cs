using System.Text;
using Lightlag.Sim.Core;

namespace Lightlag.Sim.Logging;

/// <summary>
/// Writes crash reports and player (F8) reports. Each report is one folder holding everything needed
/// to understand and reproduce the problem:
///   README.md      what happened, build, seed, game time, and how to replay it
///   save.json      the game at that moment (load it to continue from there)
///   replay.json    the whole game so far, to watch it happen again exactly
///   log-tail.jsonl the last few hundred log lines
///   stats.csv      each side at the end of each game day (full games only)
///   choices.csv    what each side chose (buildings, research, ships, leanings, battles), for the "what matters" table
///   rival-mind.md  what the computer rival is doing and why (full games only)
///   error.txt      the full exception (crash reports only)
///   screenshot.png (F8 reports from the game; the game adds it)
/// Writing a report must never throw: every part is tried separately and failures are noted inside.
/// </summary>
public static class Reports
{
    public static string WriteCrash(DataPaths paths, GameLog log, Simulation? sim, Exception error, string where)
    {
        var dir = MakeFolder(paths.Crashes, "crash");
        var problems = new List<string>();
        Try(problems, "error.txt", () => File.WriteAllText(Path.Combine(dir, "error.txt"), error.ToString()));
        WriteGameFiles(dir, sim, problems);
        Try(problems, "log-tail.jsonl", () => File.WriteAllLines(Path.Combine(dir, "log-tail.jsonl"), log.Tail()));

        var md = new StringBuilder();
        md.AppendLine("# Lightlag crash report");
        md.AppendLine();
        AppendFacts(md, log, sim);
        md.AppendLine($"- Where: {where}");
        md.AppendLine($"- Error: `{error.GetType().Name}`: {Escape(error.Message)}");
        if (error is RuleBrokenException rb) md.AppendLine($"- Broken rule: `{rb.Violation.Rule}`");
        AppendHowTo(md, sim);
        AppendProblems(md, problems);
        Try(problems, "README.md", () => File.WriteAllText(Path.Combine(dir, "README.md"), md.ToString()));

        log.Fatal("crash", $"Crash report written: {error.GetType().Name}: {error.Message}", new { folder = dir, where });
        return dir;
    }

    /// <summary>An F8 report: the player's note plus the same game files. Returns the folder, so the game can add a screenshot.</summary>
    public static string WritePlayerReport(DataPaths paths, GameLog log, Simulation? sim, string note, string? screen = null)
    {
        var dir = MakeFolder(paths.Reports, "report");
        var problems = new List<string>();
        WriteGameFiles(dir, sim, problems);
        Try(problems, "log-tail.jsonl", () => File.WriteAllLines(Path.Combine(dir, "log-tail.jsonl"), log.Tail()));

        var md = new StringBuilder();
        md.AppendLine("# Lightlag player report (F8)");
        md.AppendLine();
        md.AppendLine("## Note");
        md.AppendLine();
        md.AppendLine(string.IsNullOrWhiteSpace(note) ? "(no note)" : note.Trim());
        md.AppendLine();
        md.AppendLine("## Facts");
        md.AppendLine();
        AppendFacts(md, log, sim);
        if (screen != null) md.AppendLine($"- Screen: {screen}");
        AppendHowTo(md, sim);
        AppendProblems(md, problems);
        Try(problems, "README.md", () => File.WriteAllText(Path.Combine(dir, "README.md"), md.ToString()));

        log.Info("report", "Player report written", new { folder = dir, note });
        return dir;
    }

    private static void WriteGameFiles(string dir, Simulation? sim, List<string> problems)
    {
        if (sim == null) return;
        Try(problems, "save.json", () => sim.ToSave().WriteTo(Path.Combine(dir, "save.json")));
        Try(problems, "replay.json", () => sim.Replay.WriteTo(Path.Combine(dir, "replay.json")));
        if (!sim.World.Loop) return;
        Try(problems, "stats.csv", () => File.WriteAllText(Path.Combine(dir, "stats.csv"), Stats.ToCsv(sim.World)));
        Try(problems, "choices.csv", () => File.WriteAllText(Path.Combine(dir, "choices.csv"), Choices.ToCsv(sim.World)));
        if (sim.World.Minds.Count > 0)
            Try(problems, "rival-mind.md", () => File.WriteAllText(Path.Combine(dir, "rival-mind.md"), Mind.Describe(sim.World)));
    }

    private static void AppendFacts(StringBuilder md, GameLog log, Simulation? sim)
    {
        md.AppendLine($"- Time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        md.AppendLine($"- Build: {log.Build}");
        if (sim != null)
        {
            md.AppendLine($"- Scenario: {sim.World.Scenario}");
            md.AppendLine($"- Seed: {sim.World.Seed}");
            md.AppendLine($"- Game time: {GameTime.Format(sim.World.Tick)} (tick {sim.World.Tick})");
            md.AppendLine($"- Rule failures this game: {sim.RuleViolations}");
            foreach (var m in sim.World.Minds)
                md.AppendLine($"- {m.Side} run by the computer: {m.Difficulty}, {m.Personality}, attack ratio {m.Ratio:0.00}");
            if (sim.World.Loop) md.AppendLine($"- So far: {Stats.Summary(sim.World).Replace(Environment.NewLine, " ")}");
        }
        else
        {
            md.AppendLine("- No game was running.");
        }
        md.AppendLine($"- Log errors this session: {log.Count(LogLevel.Error)}, warnings: {log.Count(LogLevel.Warn)}");
        if (log.FilePath != null) md.AppendLine($"- Full log: `{log.FilePath}`");
    }

    private static void AppendHowTo(StringBuilder md, Simulation? sim)
    {
        if (sim == null) return;
        md.AppendLine();
        md.AppendLine("## Reproduce");
        md.AppendLine();
        md.AppendLine("Replay the game exactly up to this moment:");
        md.AppendLine();
        md.AppendLine("```");
        md.AppendLine("Lightlag.Headless replay replay.json");
        md.AppendLine("```");
    }

    private static void AppendProblems(StringBuilder md, List<string> problems)
    {
        if (problems.Count == 0) return;
        md.AppendLine();
        md.AppendLine("## Parts that could not be written");
        md.AppendLine();
        foreach (var p in problems) md.AppendLine($"- {p}");
    }

    private static string MakeFolder(string parent, string suffix)
    {
        var name = DataPaths.StampedName(DateTime.UtcNow, suffix);
        var dir = Path.Combine(parent, name);
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(parent, $"{name}{n}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Try(List<string> problems, string what, Action action)
    {
        try { action(); }
        catch (Exception ex) { problems.Add($"{what}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static string Escape(string s) => s.Replace("\r", " ").Replace("\n", " ");
}
