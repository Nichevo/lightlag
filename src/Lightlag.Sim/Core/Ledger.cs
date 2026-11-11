namespace Lightlag.Sim.Core;

/// <summary>
/// The goods ledger: how much of each good a side made or gained, and used or lost, and why. Kept in
/// SideTotals (so saves keep it) for the resources numbers and the overnight balance audit. Only counts in a
/// full game; it never changes the world.
/// </summary>
public static class Ledger
{
    /// <summary>Reasons used on both sides of the ledger.</summary>
    public const string Mined = "mined", Made = "made", Start = "start", StoreFull = "store full";

    public static void In(World w, Side? side, string good, double amount, string why) => Note(w, side, good, amount, why, true);

    public static void Out(World w, Side? side, string good, double amount, string why) => Note(w, side, good, amount, why, false);

    private static void Note(World w, Side? side, string good, double amount, string why, bool gained)
    {
        if (!w.Loop || side is not Side s || !(amount > 1e-12)) return;
        var t = Economy.Of(w, s).Totals;
        var book = gained ? t.GoodsIn : t.GoodsOut;
        if (!book.TryGetValue(good, out var reasons)) book[good] = reasons = new Dictionary<string, double>();
        reasons[why] = reasons.GetValueOrDefault(why) + amount;
    }

    public static double Total(Dictionary<string, Dictionary<string, double>> book, string good, string? why = null) =>
        !book.TryGetValue(good, out var reasons) ? 0 : why == null ? reasons.Values.Sum() : reasons.GetValueOrDefault(why);
}
