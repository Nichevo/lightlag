using System.Text;
using Lightlag.Sim.Core;

namespace Lightlag.Headless;

/// <summary>
/// The "what matters" table: for every choice seen in the choice logs (building, research idea, ship
/// class, leaning, pull-back preset, zone), how often it was picked, the win rate with and without it,
/// and the battle win rate and ships lost per battle with and without it. Big gaps are flagged as
/// choices that matter; choices never picked are listed as dead; figures from under 30 games are thin.
/// </summary>
public static class WhatMatters
{
    public const int ThinGames = 30;
    public const double BigGap = 0.15;
    public const int MaxRows = 40;

    /// <summary>One side in one computer-vs-computer game.</summary>
    public sealed record Row(HashSet<string> Keys, bool Won, int Battles, int BattlesWon, int ShipsLost)
    {
        /// <summary>Won: won the game, or with no winner, ahead on control at the end.</summary>
        public static Row Of(World w, Side side)
        {
            var won = w.Winner is Side winner ? winner == side
                : Planets.Control(w, side).Share > Planets.Control(w, Combat.Other(side)).Share;
            var battles = w.Choices.Where(c => c.Side == side && c.Kind == "battle").ToList();
            return new Row(Choices.Keys(w, side).ToHashSet(), won, battles.Count, battles.Count(c => c.What == "won"), Stats.Of(w, side).ShipsLost);
        }
    }

    public static readonly List<Row> Rows = new();

    private sealed record Line(string Key, int With, int Without, double WinWith, double WinWithout,
        double? BattleWith, double? BattleWithout, double? LossWith, double? LossWithout)
    {
        public double Gap => Math.Max(Math.Abs(WinWith - WinWithout), BattleWith is double a && BattleWithout is double b ? Math.Abs(a - b) : 0);
        public bool Thin => With < ThinGames || Without < ThinGames;
    }

    public static void Append(StringBuilder md)
    {
        if (Rows.Count == 0) return;
        var keys = Rows.SelectMany(r => r.Keys).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
        var lines = keys.Select(k =>
        {
            var with = Rows.Where(r => r.Keys.Contains(k)).ToList();
            var without = Rows.Where(r => !r.Keys.Contains(k)).ToList();
            return new Line(k, with.Count, without.Count, WinRate(with), WinRate(without),
                BattleRate(with), BattleRate(without), LossRate(with), LossRate(without));
        }).ToList();

        md.AppendLine();
        md.AppendLine("## What matters");
        md.AppendLine();
        md.AppendLine($"From the choice logs of {Rows.Count} sides in {Rows.Count / 2} computer-vs-computer games. \"Won\" is a win, or being ahead on control "
                      + $"when time ran out. Rows from fewer than {ThinGames} games with or without the choice are thin; gaps of {BigGap:P0} or more "
                      + "are flagged. The full logs are the `-choices.csv` files in the `stats` folder.");
        md.AppendLine();
        var matter = lines.Where(l => !l.Thin && l.Gap >= BigGap).OrderByDescending(l => l.Gap).ToList();
        md.AppendLine(matter.Count == 0
            ? "- Choices that matter: none yet with enough games behind them."
            : "- **Choices that matter:** " + string.Join("; ", matter.Take(10).Select(l => $"{l.Key} (wins {l.WinWith:P0} with, {l.WinWithout:P0} without)")));
        var dead = Dead(keys).ToList();
        md.AppendLine(dead.Count == 0 ? "- Dead choices (never picked): none." : $"- Dead choices (never picked): {string.Join(", ", dead)}.");
        md.AppendLine();
        md.AppendLine("| Choice | Picked (games) | Wins with | Wins without | Battles won with | without | Ships lost per battle with | without | Note |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var l in lines.OrderBy(l => l.Thin).ThenByDescending(l => l.Gap).ThenBy(l => l.Key, StringComparer.Ordinal).Take(MaxRows))
            md.AppendLine($"| {l.Key} | {l.With} of {l.With + l.Without} | {l.WinWith:P0} | {l.WinWithout:P0} | {P(l.BattleWith)} | {P(l.BattleWithout)} | "
                          + $"{N(l.LossWith)} | {N(l.LossWithout)} | {(l.Thin ? "thin" : l.Gap >= BigGap ? "**matters**" : "")} |");
        if (lines.Count > MaxRows) md.AppendLine($"\n({lines.Count - MaxRows} more rows with smaller gaps are left out.)");
    }

    private static double WinRate(List<Row> rows) => rows.Count == 0 ? 0 : rows.Count(r => r.Won) / (double)rows.Count;

    private static double? BattleRate(List<Row> rows) => rows.Sum(r => r.Battles) is var n and > 0 ? rows.Sum(r => r.BattlesWon) / (double)n : null;

    private static double? LossRate(List<Row> rows) => rows.Sum(r => r.Battles) is var n and > 0 ? rows.Sum(r => r.ShipsLost) / (double)n : null;

    private static string P(double? v) => v is double d ? d.ToString("P0") : "-";

    private static string N(double? v) => v is double d ? d.ToString("0.00") : "-";

    /// <summary>Buildings, research ideas and ship classes in the data that no side picked in any game.</summary>
    private static IEnumerable<string> Dead(List<string> seen)
    {
        var picked = seen.ToHashSet();
        foreach (var b in (Planets.D.Buildings ?? new()).Select(b => b.Id).OrderBy(i => i, StringComparer.Ordinal))
            if (!picked.Contains($"building: {b}")) yield return $"building {b}";
        foreach (var r in GameData.Economy.Research.Select(r => r.Id).OrderBy(i => i, StringComparer.Ordinal))
            if (!picked.Contains($"research: {r}")) yield return $"research {r}";
        foreach (var c in GameData.ShipClasses.Keys.Where(Planner.WarshipClass).OrderBy(i => i, StringComparer.Ordinal))
            if (!picked.Contains($"ship: {c}")) yield return $"ship {c}";
    }
}
