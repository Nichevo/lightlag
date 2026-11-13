using System.Text;
using Lightlag.Sim.Core;

namespace Lightlag.Headless;

/// <summary>
/// The research table in the overnight summary (the research review): for each idea, how many computer sides
/// queued it, on what day on average, and the win rate of sides that had it by the end of year 1 against
/// those that didn't; ideas no side picked; the share of picks per field and tier; how alike two sides'
/// research was; and the directions sides turned to and why. "Won" means won, or ahead on control at the end.
/// </summary>
public static class ResearchReport
{
    public sealed record Row(string Personality, string Race, bool Won, List<(double Day, string Id)> Picks, List<string> Turns);

    public static readonly List<Row> Rows = new();

    public static Row Of(World w, Side side)
    {
        var won = w.Winner is Side winner ? winner == side
            : Sides.Enemies(w, side).All(o => Planets.Control(w, side).Share > Planets.Control(w, o).Share);
        var picks = w.Choices.Where(c => c.Side == side && c.Kind == "research").Select(c => (c.Hour / 24, c.What)).ToList();
        var turns = w.Choices.Where(c => c.Side == side && c.Kind == "research-focus").Select(c => $"{c.What} ({c.Detail})").ToList();
        return new Row(Mind.Of(w, side)?.Personality.ToString() ?? "", Economy.Of(w, side).Race, won, picks, turns);
    }

    public static void Append(StringBuilder md)
    {
        if (Rows.Count == 0) return;
        var n = Rows.Count;
        var all = Rows.SelectMany(r => r.Picks).ToList();
        md.AppendLine("## Research");
        md.AppendLine();
        md.AppendLine($"{n} computer sides, {all.Count / (double)n:0.0} ideas queued each on average. "
                      + $"Alike: two sides share {Alike(Rows) * 100:0} % of their ideas on average ({AlikeSame() * 100:0} % with the same personality).");
        md.AppendLine();
        md.AppendLine("| Field | Share of picks |");
        md.AppendLine("|---|---|");
        foreach (var g in all.GroupBy(p => GameData.Research(p.Id).Field).OrderByDescending(g => g.Count()))
            md.AppendLine($"| {Research.FieldName(g.Key)} | {100.0 * g.Count() / Math.Max(1, all.Count):0} % |");
        md.AppendLine();
        md.AppendLine("Tiers: " + string.Join(", ", all.GroupBy(p => GameData.Research(p.Id).Tier).OrderBy(g => g.Key).Select(g => $"tier {g.Key} {g.Count()}")));
        md.AppendLine();
        var turns = Rows.SelectMany(r => r.Turns).ToList();
        if (turns.Count > 0)
        {
            md.AppendLine($"Directions turned to ({turns.Count / (double)n:0.0} a side): "
                          + string.Join(", ", turns.GroupBy(t => t).OrderByDescending(g => g.Count()).Take(10).Select(g => $"{g.Key} x{g.Count()}")));
            md.AppendLine();
        }
        md.AppendLine("| Idea | Field | Tier | Sides | First day | Win % with (year 1) | Win % without |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in GameData.Economy.Research.Where(r => !r.Later)
                     .Select(r => (r, Sides: Rows.Count(x => x.Picks.Any(p => p.Id == r.Id))))
                     .OrderByDescending(x => x.Sides).ThenBy(x => x.r.Id, StringComparer.Ordinal))
        {
            if (r.Sides == 0) continue;
            var days = Rows.SelectMany(x => x.Picks.Where(p => p.Id == r.r.Id).Take(1)).Select(p => p.Day).ToList();
            var with = Rows.Where(x => x.Picks.Any(p => p.Id == r.r.Id && p.Day <= 365)).ToList();
            var without = Rows.Except(with).ToList();
            md.AppendLine($"| {r.r.Name} | {Research.FieldName(r.r.Field)} | {r.r.Tier} | {100.0 * r.Sides / n:0} % | {days.Average():0} | "
                          + $"{Rate(with)} | {Rate(without)} |");
        }
        md.AppendLine();
        var never = GameData.Economy.Research.Where(r => !r.Later && !Rows.Any(x => x.Picks.Any(p => p.Id == r.Id))).Select(r => r.Name).ToList();
        md.AppendLine(never.Count == 0 ? "Every idea was picked by some side." : $"Never picked ({never.Count}): {string.Join(", ", never)}.");
        md.AppendLine();
    }

    private static string Rate(List<Row> rows) => rows.Count < 5 ? "thin" : $"{100.0 * rows.Count(r => r.Won) / rows.Count:0} %";

    private static double Alike(List<Row> rows)
    {
        double sum = 0;
        var pairs = 0;
        for (var i = 0; i < rows.Count; i++)
            for (var j = i + 1; j < rows.Count; j++)
            {
                var a = rows[i].Picks.Select(p => p.Id).ToHashSet();
                var b = rows[j].Picks.Select(p => p.Id).ToHashSet();
                if (a.Count + b.Count == 0) continue;
                sum += a.Intersect(b).Count() / (double)a.Union(b).Count();
                pairs++;
            }
        return pairs > 0 ? sum / pairs : 0;
    }

    private static double AlikeSame()
    {
        var groups = Rows.GroupBy(r => r.Personality).Where(g => g.Count() > 1).ToList();
        return groups.Count == 0 ? 0 : groups.Average(g => Alike(g.ToList()));
    }
}
