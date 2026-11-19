namespace Lightlag.Sim.Core;

/// <summary>
/// Known kit: a side that has studied a rival weapon (intel level KnownKitLevel or more, and that weapon seen at the
/// level now firing at it) counters it slightly: its point defence kills a little more of those missiles, and those
/// railguns hit it a little less. The same for every side. Battle reports name what it applied to.
/// </summary>
public static class KnownKit
{
    private static RivalTechData D => GameData.Weapons.R;

    public static double Bonus => D.KnownKitBonus;

    /// <summary>Does the defender know this part of the shooter's kit, at this level or better?</summary>
    public static bool Knows(World w, Side defender, Side shooter, string part, int level)
    {
        if (!w.Loop || defender == shooter || Bonus <= 0 || !Sides.InGame(w, shooter) || !Sides.InGame(w, defender)) return false;
        var file = Intel.Of(w, defender, shooter);
        return Intel.Level(file) >= D.KnownKitLevel && file.Tech.Find(t => t.Part == part) is { } fact && fact.Level >= level;
    }

    /// <summary>Kill chance factor for the defender's point defence against this missile.</summary>
    public static double PointDefence(World w, Battle? battle, Side defender, Missile m)
    {
        if (!Knows(w, defender, m.Side, "missiles", m.Brain)) return 1;
        Note(battle, defender, $"brain {m.Brain} missiles: point defence {Bonus:P0} better");
        return 1 + Bonus;
    }

    /// <summary>Hit chance factor for the shooter's railguns against a defender that knows them.</summary>
    public static double Railguns(World w, Battle? battle, Side shooter, Side defender)
    {
        var level = Intel.TrueLevel(w, shooter, "railguns");
        if (!Knows(w, defender, shooter, "railguns", level)) return 1;
        Note(battle, defender, $"Mk {Roman(level)} railguns: {Bonus:P0} fewer hits taken");
        return 1 - Bonus;
    }

    /// <summary>One line of a matchup: their part as seen ("?" if not), yours, who has the edge (null: unknown) and any known-kit edge.</summary>
    public sealed record MatchRow(string Part, string Theirs, string Yours, int? Edge, string? Known);

    public static readonly string[] MatchParts = { "railguns", "missiles", "point-defence", "shields", "engines", "armour" };

    /// <summary>Their kit as this side has seen it against its own, part by part (the engagement screen and the Intelligence screen).</summary>
    public static List<MatchRow> Matchup(World w, Side side, Side rival)
    {
        var file = Intel.Of(w, side, rival);
        var studied = Intel.Level(file) >= D.KnownKitLevel && Bonus > 0;
        var rows = new List<MatchRow>();
        foreach (var part in MatchParts)
        {
            var fact = file.Tech.Find(t => t.Part == part);
            var mine = Intel.TrueLevel(w, side, part);
            string? known = !studied || fact == null ? null : part switch
            {
                "missiles" => $"studied: your point defence {Bonus:P0} better against brain {fact.Level} and below",
                "railguns" => $"studied: {Bonus:P0} fewer hits from Mk {Roman(fact.Level)} railguns and below",
                _ => null,
            };
            rows.Add(new MatchRow(part, fact != null ? Intel.LevelName(part, fact.Level) : "?", Intel.LevelName(part, mine),
                fact != null ? Math.Sign(mine - fact.Level) : null, known));
        }
        return rows;
    }

    private static void Note(Battle? battle, Side defender, string text)
    {
        if (battle == null) return;
        var list = battle.For(defender).KnownKit;
        if (!list.Contains(text)) list.Add(text);
    }

    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };
}
