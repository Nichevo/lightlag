namespace Lightlag.Sim.Core;

/// <summary>
/// The sides in play. A game has you and one rival, or in a three-race game you and two rivals (the Third
/// side). Every side is hostile to every other: there are no alliances, only one-way contact offers (see
/// Contact). w.Sides holds one SideState per side in play, indexed by Side, so loops over the sides use
/// InPlay rather than naming Player and Rival. Per-side arrays elsewhere (missile brains, surveys, known
/// minefields, battle sides) are always Max long; old saves are padded on load (see Pad).
/// </summary>
public static class Sides
{
    /// <summary>The most sides a game can have.</summary>
    public const int Max = 3;

    /// <summary>The sides in this game, in order.</summary>
    public static IEnumerable<Side> InPlay(World w)
    {
        foreach (var s in w.Sides) yield return s.Side;
    }

    /// <summary>A three-race game (two rivals).</summary>
    public static bool Three(World w) => w.Sides.Length > 2;

    public static bool InGame(World w, Side s) => (int)s < w.Sides.Length;

    /// <summary>The side has lost its last shipyard in a three-race game: it is out of the running (its fleets and planets stay).</summary>
    public static bool Out(World w, Side s) => InGame(w, s) && w.Sides[(int)s].OutHour != null;

    /// <summary>The sides still in the running.</summary>
    public static IEnumerable<Side> Standing(World w) => InPlay(w).Where(s => !Out(w, s));

    /// <summary>Every other side in play (all are enemies).</summary>
    public static IEnumerable<Side> Enemies(World w, Side s)
    {
        foreach (var x in w.Sides) if (x.Side != s) yield return x.Side;
    }

    /// <summary>
    /// The enemy a side mostly thinks about: in a two-side game the other side; with two rivals the one its
    /// computer mind has picked as its target, else the first still standing.
    /// </summary>
    public static Side MainEnemy(World w, Side s)
    {
        if (w.Sides.Length <= 2) return s == Side.Player ? Side.Rival : Side.Player;
        if (Mind.Of(w, s) is { Target: Side t } && t != s && InGame(w, t) && !Out(w, t)) return t;
        foreach (var x in w.Sides) if (x.Side != s && x.OutHour == null) return x.Side;
        foreach (var x in w.Sides) if (x.Side != s) return x.Side;
        return s == Side.Player ? Side.Rival : Side.Player;
    }

    /// <summary>The other side in a two-side contest (a battle, a missile's flight): the side of whoever it is aimed at, else the main enemy.</summary>
    public static Side Opponent(World w, Side s) => MainEnemy(w, s);

    /// <summary>A short name for a side: You, or its race ("the Hive") when there are two rivals, else Rival.</summary>
    public static string Label(World w, Side s)
    {
        if (s == Side.Player) return "You";
        if (!Three(w) || !InGame(w, s)) return "Rival";
        var race = Races.Of(w, s);
        return race.Name;
    }

    /// <summary>"the rival" or the race's name for text.</summary>
    public static string Them(World w, Side s) => Three(w) && InGame(w, s) ? Races.Of(w, s).Name : "the rival";

    /// <summary>Old saves and data made before the third side: per-side arrays grow to Max (missing entries are false or 0).</summary>
    public static void Pad(World w)
    {
        static T[] Grow<T>(T[] a, T fill) => a.Length >= Max ? a : a.Concat(Enumerable.Repeat(fill, Max - a.Length)).ToArray();
        w.MissileBrain = Grow(w.MissileBrain, 1);
        w.FleetNumbers = Grow(w.FleetNumbers, 0);
        foreach (var b in w.Bodies)
        {
            b.SurveyHours = Grow(b.SurveyHours, 0.0);
            b.Surveyed = Grow(b.Surveyed, false);
        }
        foreach (var m in w.Minefields) m.Known = Grow(m.Known, false);
        // Sightings from before they said whose fleet: the other side (there were only two).
        foreach (var s in w.Sightings.Concat(w.Picture))
            if (!s.Own && s.Of == s.SeenBy) s.Of = s.SeenBy == Side.Player ? Side.Rival : Side.Player;
        foreach (var b in w.Battles)
            if (b.Sides.Length < Max) b.Sides = b.Sides.Concat(Enumerable.Range(0, Max - b.Sides.Length).Select(_ => new BattleSide())).ToArray();
    }
}
