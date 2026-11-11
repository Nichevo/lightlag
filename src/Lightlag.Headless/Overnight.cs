using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Lightlag.Sim.Core;

namespace Lightlag.Headless;

/// <summary>
/// The overnight "what matters" report: headline numbers checked against the spec targets, how battles
/// ended (with pull-back presets, Hold on, formations and fleet sizes), warships per side over the game,
/// the what-matters table, and tunes the runner suggests from all of it. Written into summary.md and,
/// as a page to look at, summary.html. One hour of play is taken as about one game year.
/// </summary>
public static class Overnight
{
    public const double DaysPerPlayHour = 365;
    public const double BattlesLow = 2, BattlesHigh = 4, WipeTarget = 1 / 3.0, WarshipsLow = 6, WarshipsHigh = 12, StallHigh = 0.25;

    /// <summary>One side's part in one fought battle (from the choice log).</summary>
    public sealed record Fight(string Outcome, int Ships, int Lost, string Ending, string[] Rules, string[] Formations,
        string[] Missions, bool HeldOn, int Blocked, int Caught, bool BeforeShots);

    /// <summary>A freighter caught on a lane, and how many ships the raider had.</summary>
    public sealed record Raid(int Ships);

    public static readonly List<Fight> Fights = new();
    public static readonly List<Raid> Raids = new();

    private static readonly Regex Field = new(@"(?<k>[a-z ]+?) (?<v>[^;]*)(;|$)", RegexOptions.Compiled);

    /// <summary>Reads a finished game's battles and raids from its choice log.</summary>
    public static void Collect(World w)
    {
        foreach (var c in w.Choices)
        {
            if (c.Kind == "raid") Raids.Add(new Raid(Int(Get(c.Detail, "ships"), 1)));
            if (c.Kind != "battle") continue;
            var d = c.Detail;
            var ships = Get(d, "ships").Split(" v ");
            var lost = Get(d, "lost").Split(" v ");
            Fights.Add(new Fight(c.What, Int(ships[0], 0), Int(lost[0], 0), Get(d, "ending"), Words(Get(d, "rules")),
                Words(Get(d, "formation")), Words(Get(d, "missions")), Get(d, "held") == "1", Int(Get(d, "blocked"), 0),
                Int(Get(d, "caught"), 0), Get(d, "before shots") == "1"));
        }
    }

    /// <summary>Warships per side (average of the two) at each 10th of the game, and at mid-game.</summary>
    public static (double Mid, double[] Curve) Warships(World w, int days)
    {
        var curve = new double[10];
        double At(int day)
        {
            var rows = w.History.Where(r => r.Day <= day).GroupBy(r => r.Side).Select(g => g.Last().Warships).ToList();
            return rows.Count == 0 ? 0 : rows.Average();
        }
        for (var i = 0; i < 10; i++) curve[i] = Math.Round(At((i + 1) * days / 10), 2);
        return (Math.Round(At(days / 2), 2), curve);
    }

    private static string Get(string detail, string key)
    {
        foreach (var part in detail.Split(';'))
        {
            var p = part.Trim();
            if (p.StartsWith(key + " ", StringComparison.Ordinal)) return p[(key.Length + 1)..].Trim();
        }
        return "";
    }

    private static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static int Int(string s, int fallback) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // ---- The report ---------------------------------------------------------------------------

    public sealed record Tile(string Name, string Value, string Target, bool? Good, string Note = "");

    public sealed record Report(List<Tile> Tiles, (string Name, double Share, string Css)[] Endings, int Battles, string Presets, string HoldOn,
        List<string[]> Formations, List<string[]> Sizes, string RaidNote, double[] Curve, List<WhatMatters.Line> Matters, List<string> Dead, List<string> Tunes);

    public static Report Build(List<GameResult> ai, Dictionary<string, (int Games, int Wins)> personalities)
    {
        double Avg(Func<SideEnd, double> f) => ai.Average(r => (f(r.Player!) + f(r.Rival!)) / 2);
        var days = ai.Average(r => r.Ticks / (double)GameTime.TicksPerDay);
        var years = Math.Max(1e-9, days / DaysPerPlayHour);
        var battles = Avg(e => e.Battles);
        var perYear = battles / years;
        var wiped = Avg(e => e.Wiped);
        var one = Avg(e => e.OneWithdrew);
        var both = Avg(e => e.BothWithdrew);
        var drifted = Math.Max(0, battles - wiped - one - both);
        var wipeShare = battles > 0 ? wiped / battles : 0;
        var mids = ai.Where(r => r.WarshipsMid != null).Select(r => r.WarshipsMid!.Value).ToList();
        var mid = mids.Count > 0 ? mids.Average() : Avg(e => e.Warships);
        var contacts = Avg(e => e.Contacts);
        var noShot = contacts > 0 ? Math.Max(0, contacts - battles) / contacts : 0;
        var pulledEarly = Avg(e => e.PulledBeforeShots);
        var stall = Avg(e => e.MaterialStallHours) / 24 / Math.Max(1, days);
        var won = ai.Where(r => r.Winner != null).ToList();

        var tiles = new List<Tile>
        {
            new("Games", ai.Count.ToString(CultureInfo.InvariantCulture), "", null, $"{days:0} game days each"),
            new("Battles per game year", $"{perYear:0.0}", "2 to 4", perYear >= BattlesLow && perYear <= BattlesHigh, $"{battles:0.0} a game"),
            new("Battles ending in a wipe", $"{100 * wipeShare:0} %", "under a third", battles > 0 && wipeShare < WipeTarget),
            new("Warships per side, mid-game", $"{mid:0.0}", "6 to 12", mid >= WarshipsLow && mid <= WarshipsHigh),
            new("Fights left before shots", $"{100 * noShot:0} %", "", null, $"of contacts; {pulledEarly:0.0} pull-backs a side before a shot"),
            new("Shipyard days stalled on materials", $"{100 * stall:0} %", "", stall <= StallHigh, $"{Avg(e => e.MaterialStallHours) / 24:0} days a side"),
            new("Games won", $"{won.Count} of {ai.Count}", "a 1v1 in about 10 game years", null,
                won.Count > 0 ? $"average win on day {won.Average(r => r.WinDay ?? 0):0}" : "nobody won in time"),
        };

        var endings = new (string, double, string)[]
        {
            ("One side wiped out", battles > 0 ? wiped / battles : 0, "wipe"),
            ("One side withdrew", battles > 0 ? one / battles : 0, "one"),
            ("Both withdrew", battles > 0 ? both / battles : 0, "both"),
            ("Drifted apart", battles > 0 ? drifted / battles : 0, "drift"),
        };

        var fights = Fights;
        var rules = fights.SelectMany(f => f.Rules).ToList();
        var presets = rules.Count == 0 ? "no pull-back presets recorded" : "Pull-back presets used: " + string.Join(", ",
            rules.GroupBy(r => r).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{Preset(g.Key)} {100.0 * g.Count() / rules.Count:0} %"));
        var held = fights.Where(f => f.HeldOn).ToList();
        var holdOn = fights.Count == 0 ? "" : held.Count == 0 ? "Hold on was never pressed (the computer doesn't use it; the test pilot does)."
            : $"Hold on was pressed in {100.0 * held.Count / fights.Count:0} % of fights and won {100.0 * held.Count(f => f.Outcome == "won") / held.Count:0} % of those.";

        var formations = fights.Where(f => f.Formations.Length > 0).GroupBy(f => f.Formations[0]).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new[] { g.Key, g.Count().ToString(CultureInfo.InvariantCulture), Pct(g.Count(), fights.Count), Pct(g.Count(f => f.Outcome == "won"), g.Count()),
                $"{g.Average(f => f.Blocked):0.0}", $"{g.Average(f => f.Lost / (double)Math.Max(1, f.Ships)):0.00}" }).ToList();

        string Bucket(int n) => n <= 1 ? "1 ship" : n == 2 ? "2 ships" : n <= 5 ? "3-5 ships" : "6 or more";
        var sizes = fights.GroupBy(f => Bucket(f.Ships)).OrderBy(g => g.Min(f => f.Ships))
            .Select(g => new[] { g.Key, g.Count().ToString(CultureInfo.InvariantCulture), Pct(g.Count(f => f.Outcome == "won"), g.Count()),
                $"{g.Average(f => f.Lost / (double)Math.Max(1, f.Ships)):0.00}", g.Sum(f => f.Caught).ToString(CultureInfo.InvariantCulture),
                Pct(g.Count(f => f.Missions.Any(m => m is "Hunt" or "Ambush" or "Raid")), g.Count()) }).ToList();
        var raidNote = Raids.Count == 0 ? "No freighters were caught on lanes."
            : $"Freighters caught on lanes: {Raids.Count}; by raiders of " + string.Join(", ",
                Raids.GroupBy(r => Bucket(r.Ships)).OrderBy(g => g.Min(r => r.Ships)).Select(g => $"{g.Key} {g.Count()}")) + ".";

        var curve = new double[10];
        var curves = ai.Where(r => r.WarshipCurve is { Length: 10 }).ToList();
        if (curves.Count > 0)
            for (var i = 0; i < 10; i++) curve[i] = curves.Average(r => r.WarshipCurve![i]);

        var lines = WhatMatters.Lines();
        var dead = WhatMatters.DeadChoices();
        var tunes = Tunes(perYear, wipeShare, mid, stall, won.Count, ai.Count, lines, dead, fights, personalities, noShot);
        return new Report(tiles, endings, (int)Math.Round(battles * ai.Count), presets, holdOn, formations, sizes, raidNote, curve, lines, dead, tunes);
    }

    private static string Preset(string p) => p switch { "FightOn" => "Fight on", "ToTheLast" => "To the last", _ => p };

    private static string Pct(int n, int of) => of == 0 ? "-" : $"{100.0 * n / of:0} %";

    private static List<string> Tunes(double perYear, double wipes, double mid, double stall, int won, int games, List<WhatMatters.Line> lines,
        List<string> dead, List<Fight> fights, Dictionary<string, (int Games, int Wins)> personalities, double noShot)
    {
        var t = new List<string>();
        if (perYear < BattlesLow)
            t.Add($"Too few battles ({perYear:0.0} a game year against 2 to 4): lower the odds wanted for a campaign (restlessMax, campaignRelief in ai.json) or send more raiding parties.");
        if (perYear > BattlesHigh)
            t.Add($"Too many battles ({perYear:0.0} a game year): raise the odds wanted (fightRatio) or slow ship building.");
        if (wipes >= WipeTarget)
            t.Add($"{100 * wipes:0} % of battles end in a wipe: pull back earlier (Standard's strength lost) or let small groups slip away more.");
        if (mid < WarshipsLow) t.Add($"Only {mid:0.0} warships a side mid-game: cheaper alloys or electronics per hull, or faster smelters.");
        if (mid > WarshipsHigh) t.Add($"{mid:0.0} warships a side mid-game is above 12: dearer hulls in materials, or a lower alloy rate.");
        if (stall > StallHigh) t.Add($"Shipyards wait for materials {100 * stall:0} % of days: build material plants sooner, or queue fewer ships.");
        if (noShot > 0.5) t.Add($"{100 * noShot:0} % of contacts end before a shot: fleets turn back too readily (turnBack ratio, risk).");
        if (games >= 4 && won == 0) t.Add("Nobody won: campaigns don't press home; check the campaign notes in an ai-trace.");
        foreach (var l in lines.Where(l => !l.Thin && l.Gap >= WhatMatters.BigGap).OrderByDescending(l => l.Gap).Take(3))
            t.Add($"{l.Choice} ({l.Kind}) is a big gap ({(l.WinWith - l.WinWithout) * 100:+0;-0} points): {(l.WinWith > l.WinWithout ? "the AI should value it more, or it is too cheap" : "it costs more than it gives")}.");
        foreach (var l in lines.Where(l => l.Thin && l.With >= 4 && l.Gap >= 0.25).OrderByDescending(l => l.Gap).Take(2))
            t.Add($"{l.Choice} ({l.Kind}) looks {(l.WinWith > l.WinWithout ? "strong" : "weak")} ({l.WinWith:P0} with) but only {l.With} games: keep it in tonight's mix.");
        if (dead.Count > 0)
            t.Add($"Never picked: {string.Join(", ", dead.Take(5))}{(dead.Count > 5 ? $" and {dead.Count - 5} more" : "")}: too dear, or the AI never scores them.");
        var byShape = fights.Where(f => f.Formations.Length > 0).GroupBy(f => f.Formations[0]).Where(g => g.Count() >= 10).ToList();
        foreach (var g in byShape)
        {
            var rate = g.Count(f => f.Outcome == "won") / (double)g.Count();
            if (rate >= 0.65 || rate <= 0.35) t.Add($"{g.Key} formation wins {rate:P0} of {g.Count()} fights: check blocking and cover numbers (formations in ship-classes.json).");
        }
        var solo = fights.Where(f => f.Ships <= 2).ToList();
        if (solo.Count >= 10 && solo.Count(f => f.Outcome == "won") < 0.25 * solo.Count && solo.Sum(f => f.Caught) == 0)
            t.Add("Groups of one or two ships rarely win and catch nothing: make lone raiders quieter (soloCold) or give them better lanes.");
        foreach (var (name, (g, w)) in personalities.Where(p => p.Value.Games >= 10).OrderBy(p => p.Key, StringComparer.Ordinal))
            if (w >= 0.7 * g || w <= 0.1 * g) t.Add($"{name} wins {100.0 * w / g:0} % of its {g} games: retune its profile in ai.json.");
        if (t.Count == 0) t.Add("Everything is on target tonight: nothing to tune.");
        return t;
    }

    // ---- Markdown -----------------------------------------------------------------------------

    public static void Append(StringBuilder md, Report r)
    {
        md.AppendLine();
        md.AppendLine("## What matters tonight");
        md.AppendLine();
        md.AppendLine("Targets assume one hour of play is about one game year.");
        md.AppendLine();
        md.AppendLine("| | Tonight | Target | |");
        md.AppendLine("|---|---|---|---|");
        foreach (var t in r.Tiles)
            md.AppendLine($"| {t.Name} | **{t.Value}** | {t.Target} | {(t.Good is bool g ? g ? "on target" : "**off target**" : "")}{(t.Note != "" ? $" {t.Note}" : "")} |");
        md.AppendLine();
        md.AppendLine($"### How battles ended ({r.Battles} battles)");
        md.AppendLine();
        var marks = new[] { '█', '▓', '▒', '░' };
        var bar = new StringBuilder();
        for (var i = 0; i < r.Endings.Length; i++) bar.Append(marks[i], (int)Math.Round(r.Endings[i].Share * 40));
        md.AppendLine($"`{bar}`");
        md.AppendLine();
        md.AppendLine(string.Join(" · ", r.Endings.Select((e, i) => $"{marks[i]} {e.Name} {100 * e.Share:0} %")));
        md.AppendLine();
        md.AppendLine($"- {r.Presets}.");
        if (r.HoldOn != "") md.AppendLine($"- {r.HoldOn}");
        md.AppendLine();
        if (r.Formations.Count > 0)
        {
            md.AppendLine("| Formation | Fights | Share | Won | Volleys blocked per fight | Ships lost per ship |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var f in r.Formations) md.AppendLine($"| {string.Join(" | ", f)} |");
            md.AppendLine();
        }
        if (r.Sizes.Count > 0)
        {
            md.AppendLine("| Fleet size | Fights | Won | Ships lost per ship | Freighters caught | On a raid or hunt |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var s in r.Sizes) md.AppendLine($"| {string.Join(" | ", s)} |");
            md.AppendLine();
        }
        md.AppendLine(r.RaidNote);
        md.AppendLine();
        md.AppendLine("### Warships per side over the game");
        md.AppendLine();
        md.AppendLine("```");
        var top = Math.Max(1, r.Curve.Max());
        for (var i = 0; i < r.Curve.Length; i++)
            md.AppendLine($"{(i + 1) * 10,3} %  {new string('█', (int)Math.Round(r.Curve[i] / top * 30)),-30} {r.Curve[i]:0.0}");
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("### Suggested next tunes");
        md.AppendLine();
        foreach (var t in r.Tunes) md.AppendLine($"- {t}");
    }

    // ---- HTML ---------------------------------------------------------------------------------

    public static string Html(Report r, string title, string personalitiesTable)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var h = new StringBuilder();
        h.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        h.AppendLine($"<title>{E(title)}</title><style>");
        h.AppendLine(":root{--bg:#0b0f15;--card:#111821;--line:#1f2733;--text:#d6dde8;--dim:#93a0b2;--good:#9be3bd;--bad:#f0a08a;--wipe:#f26b4f;--one:#f0d08a;--both:#9be3bd;--drift:#5fb3d9}");
        h.AppendLine("body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,sans-serif}main{max-width:1200px;margin:0 auto;padding:16px}");
        h.AppendLine("h1{font-size:22px;margin:4px 0 12px}h2{font-size:17px;margin:0 0 8px}.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:10px}");
        h.AppendLine(".card{background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 14px;margin-top:14px;overflow-x:auto}.tile .v{font-size:22px;font-weight:600}");
        h.AppendLine(".tile .n{font-size:11px;color:var(--dim);text-transform:uppercase}.good{color:var(--good)}.bad{color:var(--bad)}.dim{color:var(--dim)}");
        h.AppendLine("table{border-collapse:collapse;width:100%;font-size:13px}th,td{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line)}th{color:var(--dim);font-weight:500}");
        h.AppendLine(".bar{display:flex;height:20px;border-radius:4px;overflow:hidden}.meter{display:inline-block;width:80px;height:8px;background:#1c2531;border-radius:3px;vertical-align:middle;margin-right:6px}");
        h.AppendLine(".meter i{display:block;height:8px;border-radius:3px;background:#5fb3d9}.flag{padding:2px 8px;border-radius:10px;font-size:11px;white-space:nowrap}");
        h.AppendLine(".matters{background:#45371a;color:#f0d08a}.thin{background:#222a35;color:#b9c3d0}.dead{background:#4a2418;color:#f8b49a}");
        h.AppendLine(".cols{display:grid;grid-template-columns:repeat(auto-fit,minmax(320px,1fr));gap:14px}svg{width:100%;height:140px}</style></head><body><main>");
        h.AppendLine($"<h1>{E(title)}</h1><div class=\"tiles\">");
        foreach (var t in r.Tiles)
            h.AppendLine($"<div class=\"card tile\" style=\"margin:0\"><div class=\"n\">{E(t.Name)}</div><div class=\"v\">{E(t.Value)}</div>"
                         + $"<div class=\"{(t.Good is bool g ? g ? "good" : "bad" : "dim")}\">{E(t.Target != "" ? "target " + t.Target : t.Note)}</div>"
                         + (t.Target != "" && t.Note != "" ? $"<div class=\"dim\">{E(t.Note)}</div>" : "") + "</div>");
        h.AppendLine("</div>");

        h.AppendLine("<div class=\"cols\"><div class=\"card\"><h2>How battles ended</h2><div class=\"bar\">");
        foreach (var e in r.Endings) h.Append($"<div style=\"width:{100 * e.Share:0.#}%;background:var(--{e.Css})\" title=\"{E(e.Name)}\"></div>");
        h.AppendLine("</div><p>" + string.Join(" · ", r.Endings.Select(e => $"<span style=\"color:var(--{e.Css})\">{E(e.Name)} {100 * e.Share:0} %</span>")) + "</p>");
        h.AppendLine($"<p class=\"dim\">{E(r.Presets)}. {E(r.HoldOn)}</p>");
        Table(h, new[] { "Formation", "Fights", "Share", "Won", "Blocked volleys", "Lost per ship" }, r.Formations);
        Table(h, new[] { "Fleet size", "Fights", "Won", "Lost per ship", "Freighters caught", "Raid or hunt" }, r.Sizes);
        h.AppendLine($"<p class=\"dim\">{E(r.RaidNote)}</p></div>");

        h.AppendLine("<div class=\"card\"><h2>Warships per side over the game</h2><svg viewBox=\"0 0 240 70\" role=\"img\" aria-label=\"Warships per side by tenth of the game\">");
        var top = Math.Max(1, r.Curve.Max());
        for (var i = 0; i < r.Curve.Length; i++)
        {
            var bh = r.Curve[i] / top * 56;
            h.Append(CultureInfo.InvariantCulture, $"<rect x=\"{i * 24 + 2}\" y=\"{60 - bh:0.#}\" width=\"20\" height=\"{bh:0.#}\" fill=\"#5fb3d9\"><title>{(i + 1) * 10} %: {r.Curve[i]:0.0}</title></rect>");
            h.Append(CultureInfo.InvariantCulture, $"<text x=\"{i * 24 + 12}\" y=\"68\" font-size=\"6\" fill=\"#93a0b2\" text-anchor=\"middle\">{r.Curve[i]:0.#}</text>");
        }
        h.AppendLine("<rect x=\"0\" y=\"" + (60 - 6 / top * 56).ToString("0.#", CultureInfo.InvariantCulture) + "\" width=\"240\" height=\""
                     + Math.Max(0, (Math.Min(12, top) - 6) / top * 56).ToString("0.#", CultureInfo.InvariantCulture) + "\" fill=\"#9be3bd\" opacity=\".12\"/></svg>");
        h.AppendLine("<p class=\"dim\">Average of both sides at each tenth of the game. The green band is the 6 to 12 target.</p>");
        h.AppendLine("<h2>Suggested next tunes</h2><ul>" + string.Join("", r.Tunes.Select(t => $"<li>{E(t)}</li>")) + "</ul></div></div>");

        h.AppendLine("<div class=\"card\"><h2>What matters</h2><table><thead><tr><th>Kind</th><th>Choice</th><th>Games</th><th>Win rate with</th><th>Win rate without</th><th>Gap</th><th>Flag</th></tr></thead><tbody>");
        foreach (var l in r.Matters.OrderBy(l => l.Thin).ThenByDescending(l => l.Gap).ThenBy(l => l.Key, StringComparer.Ordinal).Take(WhatMatters.MaxRows))
            h.AppendLine($"<tr><td class=\"dim\">{E(l.Kind)}</td><td>{E(l.Choice)}</td><td>{l.With}</td><td>{Meter(l.WinWith)}</td><td>{Meter(l.WinWithout)}</td>"
                         + $"<td>{E(WhatMatters.GapSize(l))}</td><td>{Flag(l)}</td></tr>");
        foreach (var d in r.Dead.Take(20))
            h.AppendLine($"<tr><td class=\"dim\">{E(d.Split(' ')[0])}</td><td>{E(d[(d.IndexOf(' ') + 1)..])}</td><td>0</td><td>-</td><td>-</td><td>-</td><td><span class=\"flag dead\">Dead: never picked</span></td></tr>");
        h.AppendLine("</tbody></table></div>");
        if (personalitiesTable != "") h.AppendLine($"<div class=\"card\"><h2>Personalities</h2>{personalitiesTable}</div>");
        h.AppendLine("</main></body></html>");
        return h.ToString();

        static string Meter(double v) => $"<span class=\"meter\"><i style=\"width:{100 * v:0}%\"></i></span>{100 * v:0} %";
        static string Flag(WhatMatters.Line l) => l.Thin ? "<span class=\"flag thin\">Thin: under 30 games</span>"
            : l.Gap >= WhatMatters.BigGap ? "<span class=\"flag matters\">Choice that matters</span>" : "";
    }

    private static void Table(StringBuilder h, string[] head, List<string[]> rows)
    {
        if (rows.Count == 0) return;
        h.Append("<table><thead><tr>").Append(string.Join("", head.Select(x => $"<th>{WebUtility.HtmlEncode(x)}</th>"))).Append("</tr></thead><tbody>");
        foreach (var r in rows) h.Append("<tr>").Append(string.Join("", r.Select(x => $"<td>{WebUtility.HtmlEncode(x)}</td>"))).Append("</tr>");
        h.AppendLine("</tbody></table>");
    }
}
