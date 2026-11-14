using System.Text;
using Lightlag.Sim.Core;

namespace Lightlag.Headless;

/// <summary>
/// The "Industry budget" section: for each personality, the shares it started with, the shares it ended
/// with, and how its Industry was actually spent across the pots. A personality whose spend does not look like
/// its shares is not playing to type (or its pots are blocked by something other than Industry).
/// </summary>
public static class BudgetReport
{
    /// <summary>One side in one computer-vs-computer game.</summary>
    public sealed record Row(string Personality, double[] Start, double[] End, double[] Spent, double Overflowed, double UrgentDrawn)
    {
        public static Row? Of(World w, Side side)
        {
            if (!Budget.On(w) || Mind.Of(w, side) is not MindState m) return null;
            var bs = Budget.Of(w, side);
            return new Row(m.Personality.ToString(), bs.BaseShares.ToArray(), bs.Pots.Select(p => p.Share).ToArray(),
                bs.Pots.Select(p => p.Spent).ToArray(), bs.Overflowed, bs.UrgentDrawn);
        }
    }

    public static readonly List<Row> Rows = new();

    public static void Append(StringBuilder md)
    {
        if (Rows.Count == 0) return;
        md.AppendLine();
        md.AppendLine("## Industry budget");
        md.AppendLine();
        md.AppendLine("Shares at the start → at the end, and the share of Industry actually spent from each pot (Ships / Buildings / Stations / Refits / Transport / Reserve).");
        md.AppendLine();
        md.AppendLine("| Personality | Sides | Start shares | End shares | Spent share | Overflowed a game | Urgent Reserve draws a game |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var g in Rows.GroupBy(r => r.Personality).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string Shares(Func<Row, double[]> f) => string.Join(" / ", Enumerable.Range(0, Budget.Count).Select(i => $"{g.Average(r => f(r)[i]):0}"));
            var spent = Enumerable.Range(0, Budget.Count).Select(i => g.Average(r => r.Spent[i] / Math.Max(1e-9, r.Spent.Sum()) * 100)).ToArray();
            md.AppendLine($"| {g.Key} | {g.Count()} | {Shares(r => r.Start)} | {Shares(r => r.End)} | {string.Join(" / ", spent.Select(s => $"{s:0}"))} | "
                          + $"{g.Average(r => r.Overflowed):0} | {g.Average(r => r.UrgentDrawn):0} |");
        }
    }
}
