using System.Globalization;
using System.Text;

namespace Lightlag.Sim.Core;

/// <summary>
/// One choice a side made, for the choice log: a building laid down, a research idea picked, a ship
/// built (with its fittings), a leaning rolled, a pull-back preset taken into battle, a zone drawn, or
/// how a battle went for the side (Kind "battle", What won / lost / even).
/// </summary>
public sealed record Choice(Side Side, double Hour, string Kind, string What, string Where = "", string Detail = "");

/// <summary>
/// The choice log: what each side chose through a full game, written next to the per-day stats (batch
/// runs and F8 reports), so overnight runs can show which choices go with winning.
/// </summary>
public static class Choices
{
    /// <summary>A cap, so a very long game can't grow the save without end.</summary>
    public const int Max = 6000;

    public static void Note(World w, Side side, string kind, string what, string where = "", string detail = "")
    {
        if (!w.Loop || w.Choices.Count >= Max) return;
        w.Choices.Add(new Choice(side, Math.Round(w.Hour, 2), kind, what, where, detail));
    }

    /// <summary>A side's part in a battle that just ended: odds going in, losses, how it ended, and the fleets' makeup and rules.</summary>
    public static void OnBattleOver(World w, Battle b)
    {
        if (!w.Loop) return;
        foreach (var side in Sides.InPlay(w))
        {
            var mine = b.For(side);
            var theirs = b.Foes(side);
            if (mine.Fleets.Count == 0 || theirs.Fleets.Count == 0) continue;
            var odds = theirs.PowerIn > 0 ? mine.PowerIn / theirs.PowerIn : 9.99;
            Note(w, side, "battle", Outcome(mine, theirs), "", string.Create(CultureInfo.InvariantCulture,
                $"odds {odds:0.00}; ships {mine.ShipsIn} v {theirs.ShipsIn}; lost {mine.ShipsLost.Count} v {theirs.ShipsLost.Count}; "
                + $"ending {b.Ending}; makeup {string.Join(" ", mine.Makeup.OrderBy(m => m, StringComparer.Ordinal))}; rules {string.Join(" ", mine.Rules.Distinct().OrderBy(r => r, StringComparer.Ordinal))}; "
                + $"formation {string.Join(" ", mine.Formations.Distinct().OrderBy(r => r, StringComparer.Ordinal))}; blocked {mine.BlockedVolleys}; "
                + $"missions {string.Join(" ", mine.Missions.Distinct().OrderBy(r => r, StringComparer.Ordinal))}; held {(mine.HeldOn ? 1 : 0)}; caught {mine.CargoCaught}; "
                + $"before shots {(mine.WithdrewBeforeShots ? 1 : 0)}"));
        }
    }

    /// <summary>Won, lost or even for one side: who held the field (the other withdrew or was wiped out), else who lost less.</summary>
    public static string Outcome(BattleSide mine, BattleSide theirs)
    {
        var wipedMe = mine.ShipsIn > 0 && mine.ShipsLost.Count >= mine.ShipsIn;
        var wipedThem = theirs.ShipsIn > 0 && theirs.ShipsLost.Count >= theirs.ShipsIn;
        if (wipedThem && !wipedMe || theirs.Withdrew && !mine.Withdrew) return "won";
        if (wipedMe && !wipedThem || mine.Withdrew && !theirs.Withdrew) return "lost";
        var mineLost = (double)mine.ShipsLost.Count / Math.Max(1, mine.ShipsIn);
        var theirLost = (double)theirs.ShipsLost.Count / Math.Max(1, theirs.ShipsIn);
        if (Math.Abs(mineLost - theirLost) > 1e-9) return mineLost < theirLost ? "won" : "lost";
        if (Math.Abs(mine.DamageDealt - theirs.DamageDealt) > 1) return mine.DamageDealt > theirs.DamageDealt ? "won" : "lost";
        return "even";
    }

    /// <summary>The choice log as CSV: side, day, kind, what, where, detail.</summary>
    public static string ToCsv(World w)
    {
        var sb = new StringBuilder("side,day,kind,what,where,detail\n");
        foreach (var c in w.Choices)
            sb.Append(c.Side).Append(',')
              .Append((c.Hour / GameTime.TicksPerDay).ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
              .Append(Field(c.Kind)).Append(',').Append(Field(c.What)).Append(',').Append(Field(c.Where)).Append(',').Append(Field(c.Detail)).Append('\n');
        return sb.ToString();
    }

    private static string Field(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    /// <summary>The keys the "what matters" table compares: one per kind and choice (battles are results, not choices).</summary>
    public static IEnumerable<string> Keys(World w, Side side) =>
        w.Choices.Where(c => c.Side == side && c.Kind != "battle").Select(c => $"{c.Kind}: {c.What}").Distinct();

    /// <summary>
    /// Picks one of the options scoring close to the best (within spread of it), weighted by score, so a
    /// computer side plays well but not the same way every game. Scores must be positive; ties keep order.
    /// </summary>
    public static T Band<T>(IReadOnlyList<(T Item, double Score)> options, SimRandom rng, double spread)
    {
        if (options.Count == 1 || spread <= 0) return options[0].Item;
        var best = options.Max(o => o.Score);
        var close = options.Where(o => o.Score >= best * (1 - spread)).ToList();
        if (close.Count == 1) return close[0].Item;
        var roll = rng.NextDouble() * close.Sum(o => o.Score);
        foreach (var (item, score) in close)
        {
            roll -= score;
            if (roll <= 0) return item;
        }
        return close[^1].Item;
    }
}
