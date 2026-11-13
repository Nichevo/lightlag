using System.Text;
using Lightlag.Sim.Core;

namespace Lightlag.Headless;

/// <summary>
/// The "Blockade or invasion" section: planets taken each way (days, troops, buildings and happiness
/// after), boardings and captures, prisoners and rescued crews, and crew ranks at the end. Target from
/// the plan: both ways get used, a blockade takes 30-60 days, an invasion 3-8.
/// </summary>
public static class WarReport
{
    /// <summary>One side in one computer-vs-computer game.</summary>
    public sealed record Row(SideTotals T, double AvgRank, int Veterans)
    {
        public static Row Of(World w, Side side)
        {
            var crewed = w.Ships.Where(s => s.Side == side && Crews.Crewed(s)).ToList();
            return new Row(Stats.Of(w, side), crewed.Count == 0 ? 0 : crewed.Average(s => (int)Crews.RankOf(s)), crewed.Count(s => Crews.RankOf(s) >= Rank.Veteran));
        }
    }

    public static readonly List<Row> Rows = new();

    /// <summary>The ways a planet is taken, in the order the report lists them.</summary>
    private static readonly string[] Methods = { "Blockade", "Blitz", "Methodical", "Starve" };

    public static void Append(StringBuilder md)
    {
        if (Rows.Count == 0) return;
        double Avg(Func<Row, double> f) => Rows.Average(f);
        double Sum(Func<Row, double> f) => Rows.Sum(f);
        var invWins = Sum(r => r.T.InvasionWins);
        var blkWins = Sum(r => r.T.BlockadeWins);
        md.AppendLine();
        md.AppendLine("## Blockade or invasion");
        md.AppendLine();
        md.AppendLine("| Way | Planets taken per side per game | Days each (target) | Happiness after | Cost |");
        md.AppendLine("|---|---|---|---|---|");
        md.AppendLine($"| Invasion | {Avg(r => r.T.InvasionWins):0.00} (of {Avg(r => r.T.Invasions):0.00} landings) | "
                      + $"{(invWins > 0 ? Sum(r => r.T.InvasionDays) / invWins : 0):0.0} (3-8) | {(invWins > 0 ? Sum(r => r.T.InvasionHappiness) / invWins : 0):0} | "
                      + $"{Avg(r => r.T.TroopsLostK) * 1000:0} troops, {Avg(r => r.T.InvasionBuildingsLost):0.0} buildings lost |");
        md.AppendLine($"| Blockade | {Avg(r => r.T.BlockadeWins):0.00} | {(blkWins > 0 ? Sum(r => r.T.BlockadeDays) / blkWins : 0):0.0} (30-60) | "
                      + $"{(blkWins > 0 ? Sum(r => r.T.BlockadeHappiness) / blkWins : 0):0} | fleet time only |");
        md.AppendLine();
        md.AppendLine("Value on capture, by method (averages per planet taken):");
        md.AppendLine();
        md.AppendLine("| Method | Taken per side per game | Days to take (of it fighting) | People | Buildings intact (damaged, wrecked) | Happiness after |");
        md.AppendLine("|---|---|---|---|---|---|");
        foreach (var how in Methods)
        {
            var all = Rows.Select(r => r.T.Captures.GetValueOrDefault(how)).OfType<CaptureTally>().ToList();
            var n = all.Sum(c => c.Count);
            if (n == 0) { md.AppendLine($"| {how} | 0.00 | - | - | - | - |"); continue; }
            double Per(Func<CaptureTally, double> f) => all.Sum(f) / n;
            md.AppendLine($"| {how} | {(double)n / Rows.Count:0.00} | {Per(c => c.Days):0.0} ({Per(c => c.FightDays):0.0}) | {Per(c => c.PeopleK):0.0}k | "
                          + $"{Per(c => c.BuildingsKept):0.0} ({Per(c => c.BuildingsDamaged):0.0}, {Per(c => c.BuildingsWrecked):0.0}) | {Per(c => c.Happiness):0} |");
        }
        var started = Sum(r => r.T.BlockadesStarted);
        if (started > 0)
        {
            var broken = Sum(r => r.T.BlockadesBroken);
            md.AppendLine();
            md.AppendLine($"Blockades (a day or more): {started / Rows.Count:0.00} per side per game; {broken / started:P0} broken by a relief fleet (target 50 %), "
                          + $"{Sum(r => r.T.BlockadesLeft) / started:P0} left by the blockaders, {blkWins / started:P0} ended in surrender, the rest in an invasion or still going.");
        }
        var sieges = Sum(r => r.T.Sieges);
        if (sieges > 0)
        {
            // Sieges: enemy warships in orbit a day or more (contested or blockaded), from the holder's side.
            var sent = Sum(r => r.T.SiegesRelieved + r.T.SiegesLeftHelpSent + r.T.SiegesLostHelpLate + r.T.SiegesLostHelpThere);
            var late = Sum(r => r.T.SiegesLostHelpLate);
            md.AppendLine();
            md.AppendLine($"Sieges (enemy warships in orbit a day or more, contested or blockaded): {sieges / Rows.Count:0.00} per side per game.");
            md.AppendLine();
            md.AppendLine("| How it ended | Share |");
            md.AppendLine("|---|---|");
            void Line(string what, double n) => md.AppendLine($"| {what} | {n / sieges:P0} |");
            Line("Help reached it", Sum(r => r.T.SiegesRelieved));
            Line("Attackers left, help on its way", Sum(r => r.T.SiegesLeftHelpSent));
            Line("Attackers left, no help sent", Sum(r => r.T.SiegesLeftNoHelp));
            Line("Fell, no help sent", Sum(r => r.T.SiegesLostNoHelp));
            Line("Fell before help came", late);
            Line("Fell with help there", Sum(r => r.T.SiegesLostHelpThere));
            if (sent > 0)
                md.AppendLine($"\nHelp was sent in {sent / sieges:P0} of sieges, {Sum(r => r.T.SiegeHelpSentAfterDays) / sent:0.0} days in, from {Sum(r => r.T.SiegeHelpDays) / sent:0.0} days away"
                              + (late > 0 ? $"; where the planet fell first, help was still {Sum(r => r.T.SiegeHelpLateDays) / late:0.0} days out." : "."));
            var why = Rows.SelectMany(r => r.T.ReliefWhyNot).GroupBy(kv => kv.Key).Select(g => (g.Key, N: g.Sum(kv => kv.Value))).OrderByDescending(x => x.N).ThenBy(x => x.Key).ToList();
            if (why.Count > 0)
                md.AppendLine($"\nWhy the computer sent no relief (sieges, where most of its power was): " + string.Join(", ", why.Select(x => $"{x.Key} {x.N}")) + ".");
        }
        if (invWins + blkWins > 0 && (invWins == 0 || blkWins == 0))
            md.AppendLine($"\n**Only {(invWins == 0 ? "blockades" : "invasions")} took planets: both ways should be worth using.**");
        md.AppendLine();
        md.AppendLine($"- Boarding per side per game: {Avg(r => r.T.Boardings):0.00} tried, {Avg(r => r.T.ShipsCaptured):0.00} ships captured, "
                      + $"{Avg(r => r.T.BoardingsFailed):0.00} failed, {Avg(r => r.T.Scuttled):0.00} own ships scuttled");
        md.AppendLine($"- Crews per side per game: {Avg(r => r.T.PrisonersTaken) * 1000:0} prisoners taken, {Avg(r => r.T.CrewsRescued) * 1000:0} own crew rescued; "
                      + $"average rank at the end {Avg(r => r.AvgRank):0.0} (0 Green to 4 Elite), {Avg(r => r.Veterans):0.0} Veteran or better ships");
    }
}
