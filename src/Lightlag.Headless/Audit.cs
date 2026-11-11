using System.Text;
using Lightlag.Sim.Core;

namespace Lightlag.Headless;

/// <summary>
/// The balance audit in the batch summary: each good made against used (and why), what is left at the end,
/// and heat: how full the hulls' heat budgets are with the fits in use, and when ships overheat.
/// </summary>
public static class Audit
{
    private sealed class GoodRow
    {
        public readonly Dictionary<string, double> In = new(), Out = new();
        public double End;
    }

    private static readonly Dictionary<string, GoodRow> Goods = new();
    private static readonly Dictionary<string, (int Ships, double Heat, double HeatBudget, double Power, double PowerBudget)> Fits = new();
    private static double _overheats, _inBattle, _burning, _cold, _sides;

    public static void Clear()
    {
        Goods.Clear();
        Fits.Clear();
        _overheats = _inBattle = _burning = _cold = _sides = 0;
    }

    public static void Collect(World w)
    {
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            _sides++;
            var t = Stats.Of(w, side);
            void Add(Dictionary<string, Dictionary<string, double>> book, bool gained)
            {
                foreach (var (good, reasons) in book)
                {
                    var row = Row(good);
                    foreach (var (why, amount) in reasons)
                        (gained ? row.In : row.Out)[why] = (gained ? row.In : row.Out).GetValueOrDefault(why) + amount;
                }
            }
            Add(t.GoodsIn, true);
            Add(t.GoodsOut, false);
            foreach (var b in w.Bodies.Where(b => b.Owner == side))
                foreach (var (good, amount) in b.Stock) Row(good).End += amount;
            _overheats += t.Overheats;
            _inBattle += t.OverheatsInBattle;
            _burning += t.OverheatsBurning;
            _cold += t.OverheatsCold;
        }
        foreach (var s in w.Ships)
        {
            var cls = GameData.ShipClass(s.ClassId);
            var fit = Fitting.FitOf(s);
            var f = Fits.GetValueOrDefault(cls.Name);
            Fits[cls.Name] = (f.Ships + 1, f.Heat + Fitting.HeatUsed(cls, fit), f.HeatBudget + cls.HeatBudget,
                f.Power + Fitting.PowerUsed(cls, fit), f.PowerBudget + cls.Power);
        }
    }

    private static GoodRow Row(string good) => Goods.TryGetValue(good, out var r) ? r : Goods[good] = new GoodRow();

    public static void Append(StringBuilder md)
    {
        if (_sides == 0) return;
        var n = _sides;
        md.AppendLine();
        md.AppendLine("## Balance audit: goods");
        md.AppendLine();
        md.AppendLine("Per side per game. Made counts what came out of mines and plants (and captures, salvage, skimmers); lost to full stores is output with no room left.");
        md.AppendLine();
        md.AppendLine("| Good | Made | Used | Lost to full stores | Left at end | Made from | Used for |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var (good, row) in Goods.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var made = row.In.Values.Sum() / n;
            var full = row.Out.GetValueOrDefault(Ledger.StoreFull) / n;
            var used = row.Out.Where(kv => kv.Key != Ledger.StoreFull).Sum(kv => kv.Value) / n;
            string Split(Dictionary<string, double> d) => string.Join(", ", d.Where(kv => kv.Key != Ledger.StoreFull && kv.Value / n >= 0.5)
                .OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value / n:0}"));
            md.AppendLine($"| {good} | {made:0} | {used:0} | {full:0} | {row.End / n:0} | {Split(row.In)} | {Split(row.Out)} |");
        }
        md.AppendLine();
        md.AppendLine("## Balance audit: heat");
        md.AppendLine();
        md.AppendLine($"- Overheats per side per game: {_overheats / n:0.0}; in battle {_inBattle / n:0.0}, with engines burning {_burning / n:0.0}, running cold {_cold / n:0.0}");
        md.AppendLine();
        md.AppendLine("| Class | Ships at end (per game) | Heat used of budget | Power used of budget |");
        md.AppendLine("|---|---|---|---|");
        foreach (var (name, f) in Fits.OrderBy(f => f.Key, StringComparer.Ordinal))
            md.AppendLine($"| {name} | {f.Ships / (n / 2):0.0} | {100 * f.Heat / Math.Max(1e-9, f.HeatBudget):0} % | {100 * f.Power / Math.Max(1e-9, f.PowerBudget):0} % |");
    }
}
