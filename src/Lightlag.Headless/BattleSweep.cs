using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lightlag.Sim;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;

namespace Lightlag.Headless;

/// <summary>
/// Battle sweeps (overnight job from M3): every fleet mix against every other, with every approach and
/// missile brain, several times each. Answers "does each ship counter the other, and is any mix always best?"
/// Writes battles.csv and a short summary to <data>/nightly/<time>_battles/ and latest-battles.md.
/// </summary>
public static class BattleSweep
{
    /// <summary>Fleet mixes of equal cost (a destroyer costs two frigates).</summary>
    public static readonly (string Name, int Frigates, int Destroyers)[] Mixes =
    {
        ("4 FF", 4, 0),
        ("2 FF + 1 DD", 2, 1),
        ("2 DD", 0, 2),
    };

    public static int Run(Options opts)
    {
        var data = Cli.DataFolder(opts);
        var build = Cli.LoadBuild();
        var repeats = (int)(opts.GetLong("repeats") ?? 3);
        var seed = opts.GetULong("seed") ?? 1;
        var started = DateTime.Now;
        var runDir = Path.Combine(data.Nightly, DataPaths.StampedName(started, "battles"));
        Directory.CreateDirectory(runDir);
        var timer = Stopwatch.StartNew();

        var rows = new List<Row>();
        var crashes = 0;
        foreach (var pm in Mixes)
        foreach (var rm in Mixes)
        foreach (var approach in Enum.GetValues<Approach>())
        foreach (var pb in new[] { 1, 2 })
        foreach (var rb in new[] { 1, 2 })
        for (var i = 0; i < repeats; i++)
        {
            var s = seed + (ulong)rows.Count + (ulong)crashes;
            var setup = new SandboxSetup(pm.Frigates, pm.Destroyers, rm.Frigates, rm.Destroyers, pb, rb);
            using var log = GameLog.Memory(build);
            log.MinLevel = LogLevel.Warn;
            try
            {
                var o = BattleRunner.Run(setup, approach, s, log);
                rows.Add(new Row(pm.Name, rm.Name, approach, pb, rb, s, o));
            }
            catch (Exception ex)
            {
                crashes++;
                Console.WriteLine($"Battle crashed: {pm.Name} vs {rm.Name}, {approach}, seed {s}: {ex.Message}");
            }
        }

        var csv = new StringBuilder("player,rival,approach,playerBrain,rivalBrain,seed,winner,playerLost,rivalLost,playerDamage,rivalDamage,hours,playerMissiles,rivalMissiles,shotDown,railHits\n");
        foreach (var r in rows)
            csv.AppendLine(string.Join(",", r.Player, r.Rival, r.Approach, r.PlayerBrain, r.RivalBrain, r.Seed, r.O.Winner,
                r.O.PlayerLost, r.O.RivalLost, F(r.O.PlayerDamage), F(r.O.RivalDamage), F(r.O.Hours),
                r.O.PlayerMissiles, r.O.RivalMissiles, r.O.ShotDown, r.O.RailHits));
        File.WriteAllText(Path.Combine(runDir, "battles.csv"), csv.ToString());

        var md = Summarise(build, rows, crashes, timer.Elapsed);
        File.WriteAllText(Path.Combine(runDir, "summary.md"), md);
        File.WriteAllText(Path.Combine(data.Nightly, "latest-battles.md"), md);
        Console.WriteLine(md);
        Console.WriteLine($"Folder: {runDir}");
        return crashes == 0 ? 0 : 1;
    }

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private sealed record Row(string Player, string Rival, Approach Approach, int PlayerBrain, int RivalBrain, ulong Seed, BattleOutcome O);

    private static string Rate(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return "-";
        var win = list.Count(r => r.O.Winner == "player") * 100 / list.Count;
        var lose = list.Count(r => r.O.Winner == "rival") * 100 / list.Count;
        return $"{win}% / {lose}%";
    }

    private static string Summarise(BuildInfo build, List<Row> rows, int crashes, TimeSpan took)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Battle sweep, {DateTime.Now:ddd d MMM yyyy HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"- Build: {build}");
        sb.AppendLine($"- Battles: {rows.Count} in {took:hh\\:mm\\:ss}; crashed: {crashes}");
        sb.AppendLine($"- Each cell: player wins / rival wins (the rest are draws). Fleets cost the same.");
        sb.AppendLine();
        sb.AppendLine("## Player mix (rows) against rival mix (columns), all approaches");
        sb.AppendLine();
        sb.AppendLine("| | " + string.Join(" | ", Mixes.Select(m => m.Name)) + " |");
        sb.AppendLine("|---|" + string.Concat(Mixes.Select(_ => "---|")));
        foreach (var pm in Mixes)
            sb.AppendLine($"| **{pm.Name}** | " + string.Join(" | ", Mixes.Select(rm => Rate(rows.Where(r => r.Player == pm.Name && r.Rival == rm.Name)))) + " |");
        sb.AppendLine();
        sb.AppendLine("## By approach");
        sb.AppendLine();
        foreach (var a in Enum.GetValues<Approach>())
        {
            var list = rows.Where(r => r.Approach == a).ToList();
            var hours = list.Count > 0 ? list.Average(r => r.O.Hours) : 0;
            sb.AppendLine($"- {a}: {Rate(list)}, battles last {hours:0.0} hours on average");
        }
        sb.AppendLine();
        sb.AppendLine("## Missile brains");
        sb.AppendLine();
        foreach (var pb in new[] { 1, 2 })
            foreach (var rb in new[] { 1, 2 })
                sb.AppendLine($"- Player brain {pb} vs rival brain {rb}: {Rate(rows.Where(r => r.PlayerBrain == pb && r.RivalBrain == rb))}");
        sb.AppendLine();
        var missiles = rows.Sum(r => r.O.PlayerMissiles + r.O.RivalMissiles);
        sb.AppendLine($"Missiles fired: {missiles}; shot down by point defence: {rows.Sum(r => r.O.ShotDown)}; railgun hits: {rows.Sum(r => r.O.RailHits)}.");
        return sb.ToString();
    }
}
