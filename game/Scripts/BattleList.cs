using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>What became of one ship in a battle, as the battle list and the report show it.</summary>
public enum ShipFate { Fine, Hurt, Crippled, Lost }

/// <summary>One ship's line in a battle summary: its name and class, and what became of it.</summary>
public sealed record FateRow(string Name, string ClassId, ShipFate Fate, double HurtShare)
{
    public string Text => Fate switch
    {
        ShipFate.Lost => "lost",
        ShipFate.Crippled => "crippled",
        ShipFate.Hurt => $"hurt {HurtShare * 100:0}%",
        _ => "fine",
    };

    public Color Color => BattleList.FateColor(Fate);
}

/// <summary>One side of a battle as the player sees it: who, how many ships, and each ship's fate (estimates only for fights merely seen).</summary>
public sealed record SideSummary(string Label, Side Side, int Ships, int Lost, int Crippled, List<FateRow> Rows, bool Estimate);

/// <summary>A battle as a row of the Battles list: where, when, who, how it ended and the ships lost on each side.</summary>
public sealed record BattleSummary(Battle Battle, string Place, string When, string Who, string Result, Color ResultColor,
    bool Live, bool Seen, bool FatesKnown, SideSummary Us, SideSummary Them, string Ending)
{
    public int Id => Battle.Id;
}

/// <summary>
/// The battles the player knows of: every fight of theirs (once its light has arrived) and fights between others
/// their sensors saw. Builds the summaries the Battles tab, the full report and the running score read.
/// </summary>
public static class BattleList
{
    /// <summary>Battles further than this in light time show no fates until their end is seen.</summary>
    private const double FarLagHours = 5.0 / 60;

    public static Color FateColor(ShipFate f) => f switch
    {
        ShipFate.Lost => Ui.Danger,
        ShipFate.Crippled => new Color("f0c040"),
        ShipFate.Hurt => new Color("c9a46a"),
        _ => new Color("7fd67f"),
    };

    public static readonly Color Won = new("7fd67f");

    private static double Lag(World w, Battle b) => w.Light.On ? Light.DelayHours(w, Side.Player, b.Where) : 0;
    private static bool Heard(World w, Battle b, double hour) => w.Hour - hour >= Lag(w, b) - 1e-9;

    /// <summary>One of your fleets fought in it.</summary>
    public static bool Mine(Battle b) => b.For(Side.Player).Fleets.Count > 0;

    /// <summary>The battles to list, oldest first: yours and those you saw, with shots fired, once their light has arrived.</summary>
    public static List<Battle> Known(World w) =>
        w.Battles.Where(b => b.Fought && (Mine(b) || b.SeenBy.Contains(Side.Player)) && Heard(w, b, b.StartHour)).ToList();

    public static BattleSummary Summarise(World w, Battle b)
    {
        var mine = Mine(b);
        var ended = b.EndHour is double end && Heard(w, b, end);
        var far = Lag(w, b) > FarLagHours;
        var fatesKnown = ended || !far;
        var place = NearestName(w, b.Where);
        var length = (ended ? b.EndHour!.Value : w.Hour - Lag(w, b)) - b.StartHour;
        var when = $"{GameTime.Format(b.StartHour)} · {Format.Hours(Math.Max(0, length))}" + (ended ? "" : ", still going");

        SideSummary us, them;
        string result, who, ending;
        Color color;
        if (mine)
        {
            us = SideOf(w, b, new[] { Side.Player }, "You", fatesKnown, false);
            var foes = Enumerable.Range(0, b.Sides.Length).Select(i => (Side)i).Where(s => s != Side.Player && b.For(s).Fleets.Count > 0).ToArray();
            them = SideOf(w, b, foes, foes.Length == 1 ? SideName(w, foes[0]) : "Them", fatesKnown, false);
            who = $"{string.Join(", ", b.For(Side.Player).Fleets)} v {string.Join(", ", b.Foes(Side.Player).Fleets)}";
            if (!ended) { result = "Fighting"; color = Ui.Rival; }
            else
            {
                var o = Choices.Outcome(b.For(Side.Player), b.Foes(Side.Player));
                result = o == "won" ? "Won" : o == "lost" ? "Lost" : "Even";
                color = o == "won" ? Won : o == "lost" ? Ui.Danger : Ui.Text;
            }
            ending = ended ? Ending(b, b.For(Side.Player), b.Foes(Side.Player)) : far ? "Still going: the light of it is on its way" : "Still going";
        }
        else
        {
            // A fight between others: only what the light showed, so counts are estimates and names unknown.
            var sides = Enumerable.Range(0, b.Sides.Length).Select(i => (Side)i).Where(s => b.For(s).Fleets.Count > 0)
                .OrderByDescending(s => b.For(s).ShipsIn).ThenBy(s => (int)s).ToList();
            var a = sides.ElementAtOrDefault(0);
            var c = sides.Count > 1 ? sides[1] : a;
            us = SideOf(w, b, new[] { a }, SideName(w, a), fatesKnown, true);
            them = SideOf(w, b, sides.Skip(1).ToArray(), sides.Count > 1 ? SideName(w, c) : "?", fatesKnown, true);
            who = string.Join(" v ", sides.Select(s => string.Join(", ", b.For(s).Fleets)));
            result = ended ? "Seen" : "Fighting";
            color = ended ? Ui.Dim : Ui.Rival;
            ending = "A fight between others that your sensors saw: the numbers are estimates from what the light showed.";
        }
        return new BattleSummary(b, place, when, who, result, color, !ended, !mine, fatesKnown, us, them, ending);
    }

    private static string SideName(World w, Side s) => s switch
    {
        Side.Player => "You",
        _ when Sides.Three(w) => Races.Of(w, s).Name,
        _ => "Rival",
    };

    private static SideSummary SideOf(World w, Battle b, Side[] sides, string label, bool fatesKnown, bool estimate)
    {
        var rows = new List<FateRow>();
        var lost = 0;
        var crippled = 0;
        var shipsIn = 0;
        foreach (var side in sides)
        {
            var s = b.For(side);
            shipsIn += s.ShipsIn;
            var named = new HashSet<string>();
            foreach (var r in b.Roster.Where(r => r.Side == side))
            {
                named.Add(r.Name);
                var armour = b.EndArmour.TryGetValue(r.Id, out var a) ? a : r.MaxArmour;
                var hurt = r.MaxArmour > 0 ? Math.Clamp(1 - armour / r.MaxArmour, 0, 1) : 0;
                var fate = !fatesKnown ? ShipFate.Fine
                    : s.ShipsLost.Contains(r.Name) ? ShipFate.Lost
                    : s.Crippled.Contains(r.Name) ? ShipFate.Crippled
                    : hurt >= 0.05 ? ShipFate.Hurt : ShipFate.Fine;
                rows.Add(new FateRow(r.Name, r.ClassId, fate, hurt));
            }
            // Lost before the recording caught them: still on the list.
            if (fatesKnown)
                foreach (var name in s.ShipsLost.Where(n => !named.Contains(n)).Distinct())
                    rows.Add(new FateRow(name, "", ShipFate.Lost, 1));
            if (fatesKnown)
            {
                lost += s.ShipsLost.Distinct().Count();
                crippled += s.Crippled.Distinct().Count(n => !s.ShipsLost.Contains(n));
            }
        }
        rows = rows.OrderBy(r => r.Fate).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new SideSummary(label, sides.FirstOrDefault(), Math.Max(shipsIn, rows.Count), lost, crippled, rows, estimate);
    }

    /// <summary>How it ended, in a sentence, for one side against everyone else.</summary>
    private static string Ending(Battle b, BattleSide us, BattleSide them)
    {
        var text = b.Ending switch
        {
            BattleEnding.Wipe when them.ShipsIn > 0 && them.ShipsLost.Count >= them.ShipsIn => "Their side was wiped out.",
            BattleEnding.Wipe => "Your side was wiped out.",
            BattleEnding.OneWithdrew when us.Withdrew => us.WithdrewBeforeShots ? "You pulled back before firing a shot." : "You pulled back.",
            BattleEnding.OneWithdrew => them.WithdrewBeforeShots ? "They pulled back before firing a shot." : "They pulled back.",
            BattleEnding.BothWithdrew => "Both sides pulled back.",
            _ => "The fleets drew apart.",
        };
        if (us.CargoCaught > 0) text += $" You caught {us.CargoCaught} freighter{(us.CargoCaught == 1 ? "" : "s")}.";
        if (them.CargoCaught > 0) text += $" They caught {them.CargoCaught} of your freighters.";
        return text;
    }

    public static string NearestName(World w, Vec2 at) =>
        w.Bodies.Where(x => x.Kind != BodyKind.Star).OrderBy(x => Vec2.Distance(x.Position, at)).FirstOrDefault() is Body body ? $"Near {body.Name}" : "Open space";

    /// <summary>A side's count line: "You 5 · lost 1 · crippled 2", with "~" for estimates.</summary>
    public static string CountLine(SideSummary s, bool fatesKnown)
    {
        var est = s.Estimate ? "~" : "";
        var line = $"{s.Label} {est}{s.Ships}";
        if (!fatesKnown) return line;
        if (s.Lost > 0) line += $" · lost {est}{s.Lost}";
        if (s.Crippled > 0) line += $" · crippled {est}{s.Crippled}";
        if (s.Lost == 0 && s.Crippled == 0) line += " · none lost";
        return line;
    }

    /// <summary>The running score of your finished battles: won, lost, even, and ships lost and crippled on each side.</summary>
    public static (int Won, int Lost, int Even, int MyLost, int MyCrippled, int TheirLost, int TheirCrippled) Score(World w)
    {
        int won = 0, lost = 0, even = 0, ml = 0, mc = 0, tl = 0, tc = 0;
        foreach (var b in Known(w).Where(Mine))
        {
            if (b.EndHour is not double end || !Heard(w, b, end)) continue;
            var us = b.For(Side.Player);
            var them = b.Foes(Side.Player);
            switch (Choices.Outcome(us, them))
            {
                case "won": won++; break;
                case "lost": lost++; break;
                default: even++; break;
            }
            ml += us.ShipsLost.Distinct().Count();
            mc += us.Crippled.Distinct().Count(n => !us.ShipsLost.Contains(n));
            tl += them.ShipsLost.Distinct().Count();
            tc += them.Crippled.Distinct().Count(n => !them.ShipsLost.Contains(n));
        }
        return (won, lost, even, ml, mc, tl, tc);
    }
}

/// <summary>A side's ships as a row of small squares: solid = came out fine (or hurt), half = crippled, crossed = lost.</summary>
public partial class ShipDots : Control
{
    private const float Size = 10, Gap = 2;
    private readonly List<ShipFate> _fates;
    private readonly Color _color;
    private readonly bool _estimate;

    public ShipDots() : this(new List<ShipFate>(), Ui.Player, false) { }

    public ShipDots(List<ShipFate> fates, Color color, bool estimate)
    {
        _fates = fates;
        _color = color;
        _estimate = estimate;
        MouseFilter = MouseFilterEnum.Ignore;
        var shown = Math.Min(fates.Count, 16);
        CustomMinimumSize = new Vector2(shown * (Size + Gap) + (fates.Count > 16 ? 24 : 0), Size + 2);
    }

    public override void _Draw()
    {
        var shown = Math.Min(_fates.Count, 16);
        for (var i = 0; i < shown; i++)
        {
            var r = new Rect2(i * (Size + Gap), 1, Size, Size);
            var fate = _fates[i];
            var alpha = _estimate ? 0.55f : 1f;
            switch (fate)
            {
                case ShipFate.Fine or ShipFate.Hurt:
                    DrawRect(r, new Color(_color, alpha));
                    if (fate == ShipFate.Hurt) DrawRect(new Rect2(r.Position + new Vector2(0, Size * 0.65f), new Vector2(Size, Size * 0.35f)), new Color(BattleList.FateColor(ShipFate.Hurt), alpha));
                    break;
                case ShipFate.Crippled:
                    var c = new Color(BattleList.FateColor(fate), alpha);
                    DrawRect(new Rect2(r.Position, new Vector2(Size / 2, Size)), c);
                    DrawRect(r, c, false, 1);
                    break;
                default:
                    var l = new Color(BattleList.FateColor(fate), alpha);
                    DrawRect(r, l, false, 1);
                    DrawLine(r.Position + new Vector2(2, 2), r.End - new Vector2(2, 2), l, 1.5f, true);
                    DrawLine(new Vector2(r.End.X - 2, r.Position.Y + 2), new Vector2(r.Position.X + 2, r.End.Y - 2), l, 1.5f, true);
                    break;
            }
        }
        if (_fates.Count > shown)
            DrawString(ThemeDB.FallbackFont, new Vector2(shown * (Size + Gap) + 2, Size), $"+{_fates.Count - shown}", HorizontalAlignment.Left, -1, 10, Ui.Dim);
    }
}
