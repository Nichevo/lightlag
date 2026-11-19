using System.Globalization;
using System.Text;

namespace Lightlag.Sim.Core;

/// <summary>
/// The game's story, kept for the after-game screen (Plan 4, "Lift the fog at the end"): where every fleet really
/// was and where each side believed the others' fleets were, every FrameHours; the moments that moved the game
/// (planets changing hands, costly battles, the lead changing, terms, the end); and each computer-run side's goal
/// and reasons day by day. Plain data in World, so saves keep it and replays rebuild it exactly.
/// </summary>
public sealed class ChronicleState
{
    /// <summary>The record has begun (games from before it begin at the first hour after loading).</summary>
    public bool Started { get; set; }

    /// <summary>The first hour the record covers (0 for games played with it from the start).</summary>
    public double FromHour { get; set; }

    public List<ChronicleFrame> Frames { get; set; } = new();
    public List<Moment> Moments { get; set; } = new();
    public List<MindDay> MindDays { get; set; } = new();

    /// <summary>Each claimable body's holder at the last look (body id to side number, -1 for nobody).</summary>
    public Dictionary<int, int> Owners { get; set; } = new();

    /// <summary>The last battle already looked at, the side in the lead at the last daily look (-1: none yet), sides that have tipped the game.</summary>
    public int LastBattleId { get; set; }
    public int Leader { get; set; } = -1;
    public List<Side> Tipped { get; set; } = new();
    public double LastTermsHour { get; set; } = -1;

    /// <summary>When the computer-run sides' thoughts were last copied into MindDays.</summary>
    public double MindsUpToHour { get; set; } = -1;
}

/// <summary>
/// One look at the map. Fleets: "id,side,ships,warships,x,y" for every fleet, joined by ';' (positions in units of PositionUnitMm).
/// Beliefs: "by,of,fleetId,ships,x,y,seen,ageHours" for what each side's command believed about the others' fleets.
/// Owners: "bodyId,side" for each held claimable body. Packed as text to keep saves small.
/// </summary>
public sealed record ChronicleFrame(double Hour, string Fleets, string Beliefs, string Owners);

/// <summary>Something that moved the game. Weight ranks it among the turning points; Side is whom it was good for.</summary>
public sealed record Moment(double Hour, string Kind, string Text, Side? Side, double Weight, int? BodyId = null);

/// <summary>
/// A computer-run side's mind at the end of one day: its goal and why, its focus, research lean and fleet jobs, what it
/// thought that day, and its guess of the enemy's fighting power against the truth. As stored, a null text is the same as
/// the day before and null thoughts are none (saves stay small); Chronicle.MindDaysOf fills them in.
/// </summary>
public sealed record MindDay(int Day, Side Side, string? Goal, string? Why, string? Focus, string? Research, string? Jobs,
    double EnemyPowerGuess, double EnemyPowerTrue, double OwnPower, List<string>? Thoughts);

/// <summary>A fleet where it really was in a frame.</summary>
public readonly record struct FleetMark(int Id, Side Side, int Ships, int Warships, Vec2 Position);

/// <summary>Where one side's command believed one enemy fleet was in a frame, whether it was in sight then, and how old the report was.</summary>
public readonly record struct BeliefMark(Side By, Side Of, int FleetId, int Ships, Vec2 Position, bool Seen, double AgeHours);

public static class Chronicle
{
    /// <summary>Hours between frames of the replay.</summary>
    public const int FrameHours = 12;

    /// <summary>Reports older than this are not kept as beliefs (the rival plans on the last 20 days, see Mind.Believe; ten keeps saves small).</summary>
    public const double BeliefMaxAgeHours = 10 * 24;

    /// <summary>A control lead must be at least this much before it counts as changing hands (stops a flicker at the start).</summary>
    public const double LeadMargin = 0.02;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Called when a full game begins: the first frame and the starting holders, with no moments for them.</summary>
    public static void Start(World w)
    {
        if (!w.Loop || w.Chronicle.Started) return;
        var c = w.Chronicle;
        c.Started = true;
        c.FromHour = w.Hour;
        foreach (var b in Economy.Claimable(w)) c.Owners[b.Id] = b.Owner is Side s ? (int)s : -1;
        c.LastBattleId = w.Battles.Count > 0 ? w.Battles.Max(b => b.Id) : 0;
        c.MindsUpToHour = w.Hour;
        c.Tipped = Sides.InPlay(w).Where(s => Planets.Tipped(w, s)).ToList();
        c.LastTermsHour = w.Endings.TermsTo != null ? w.Endings.TermsHour : -1;
        c.Frames.Add(Frame(w));
    }

    /// <summary>Each game hour: planets changing hands, battles that ended, terms; a frame every FrameHours; leads and minds each day.</summary>
    public static void Hourly(World w)
    {
        if (!w.Loop) return;
        var c = w.Chronicle;
        if (!c.Started) { Start(w); return; }
        if (w.Winner != null) return;
        Holders(w, c);
        BattlesEnded(w, c);
        Terms(w, c);
        if (w.Tick % GameTime.TicksPerDay == 0)
        {
            Lead(w, c);
            Minds(w, c);
        }
        if (w.Tick % FrameHours == 0 && (c.Frames.Count == 0 || c.Frames[^1].Hour < w.Hour)) c.Frames.Add(Frame(w));
    }

    /// <summary>The game is over: the last moments, the closing frame and the minds' last day.</summary>
    public static void End(World w)
    {
        if (!w.Loop || w.Winner is not Side winner) return;
        var c = w.Chronicle;
        if (!c.Started) Start(w);
        if (c.Moments.Any(m => m.Kind == "end")) return;
        Holders(w, c);
        BattlesEnded(w, c);
        c.Moments.Add(new Moment(w.Hour, "end", $"{Who(w, winner)} {(winner == Side.Player ? "win" : "wins")}: {w.WinReason}", winner, 10));
        Minds(w, c);
        if (c.Frames.Count == 0 || c.Frames[^1].Hour < w.Hour) c.Frames.Add(Frame(w));
    }

    // ---- What happened ----------------------------------------------------------------------------

    private static void Holders(World w, ChronicleState c)
    {
        var claimable = Economy.Claimable(w).ToList();
        var top = Math.Max(1e-9, claimable.Select(b => b.Industry).DefaultIfEmpty(1).Max());
        foreach (var b in claimable)
        {
            var now = b.Owner is Side s ? (int)s : -1;
            if (!c.Owners.TryGetValue(b.Id, out var was)) { c.Owners[b.Id] = now; continue; }
            if (was == now) continue;
            c.Owners[b.Id] = now;
            var weight = 1 + 2 * b.Industry / top;
            if (now < 0)
                c.Moments.Add(new Moment(w.Hour, "planet", $"{Who(w, (Side)was)} lost {b.Name}", null, weight, b.Id));
            else if (was < 0)
                c.Moments.Add(new Moment(w.Hour, "planet", $"{Who(w, (Side)now)} claimed {b.Name}", (Side)now, weight, b.Id));
            else
                c.Moments.Add(new Moment(w.Hour, "planet", $"{Who(w, (Side)now)} took {b.Name} from {Whom(w, (Side)was)}", (Side)now, weight + 1, b.Id));
        }
    }

    private static void BattlesEnded(World w, ChronicleState c)
    {
        foreach (var b in w.Battles)
        {
            if (b.Id <= c.LastBattleId) continue;
            if (b.EndHour == null) break; // battles are listed in order: wait for this one to end before looking past it
            c.LastBattleId = b.Id;
            var sides = Sides.InPlay(w).Where(s => b.For(s).Fleets.Count > 0).ToList();
            var lost = sides.Sum(s => b.For(s).ShipsLost.Count);
            if (!b.Fought || lost == 0 || sides.Count < 2) continue;
            var parts = sides.Select(s => $"{Who(w, s).ToLowerInvariant()} lost {b.For(s).ShipsLost.Count}");
            var best = sides.OrderBy(s => b.For(s).ShipsLost.Count).ThenBy(s => (int)s).First();
            var even = sides.Select(s => b.For(s).ShipsLost.Count).Distinct().Count() == 1;
            c.Moments.Add(new Moment(b.EndHour.Value, "battle", $"Battle near {NearestName(w, b.Where)}: {string.Join(", ", parts)}",
                even ? null : best, Math.Min(6, lost)));
        }
    }

    private static void Terms(World w, ChronicleState c)
    {
        if (w.Endings.TermsTo is Side to && w.Endings.TermsHour != c.LastTermsHour)
        {
            c.LastTermsHour = w.Endings.TermsHour;
            c.Moments.Add(new Moment(w.Hour, "terms", $"Terms offered to {Whom(w, to)} at {w.Endings.TermsShare.ToString("P0", Inv)} control", to, 3));
        }
    }

    private static void Lead(World w, ChronicleState c)
    {
        var day = w.History.Where(r => r.Day == w.Day).ToList();
        if (day.Count >= 2)
        {
            var order = day.OrderByDescending(r => r.Control).ThenBy(r => (int)r.Side).ToList();
            var lead = order[0].Control - order[1].Control >= LeadMargin ? (int)order[0].Side : c.Leader;
            if (c.Leader < 0) c.Leader = lead;
            else if (lead != c.Leader)
            {
                c.Leader = lead;
                var s = (Side)lead;
                c.Moments.Add(new Moment(w.Hour, "lead", $"{Who(w, s)} {(s == Side.Player ? "take" : "takes")} the lead ({order[0].Control.ToString("P0", Inv)} control)", s, 5));
            }
        }
        foreach (var s in Sides.InPlay(w))
        {
            var tipped = Planets.Tipped(w, s);
            if (tipped && !c.Tipped.Contains(s))
            {
                c.Tipped.Add(s);
                c.Moments.Add(new Moment(w.Hour, "tip", $"{Who(w, s)} {(s == Side.Player ? "tip" : "tips")} the game ({Planets.D.TipShare.ToString("P0", Inv)} control)", s, 4));
            }
            else if (!tipped) c.Tipped.Remove(s);
        }
    }

    private static void Minds(World w, ChronicleState c)
    {
        foreach (var m in w.Minds)
        {
            if (!Sides.InPlay(w).Contains(m.Side)) continue;
            var goal = m.Goal;
            var cut = goal.IndexOf(": ", StringComparison.Ordinal);
            var why = cut >= 0 ? goal[(cut + 2)..] : "";
            if (cut >= 0) goal = goal[..cut];
            var thoughts = m.Thoughts.Where(t => t.Hour > c.MindsUpToHour && t.Hour <= w.Hour).Select(t => t.Text).ToList();
            var jobs = string.Join(", ", m.Jobs.GroupBy(j => j.Kind).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.ToString(), StringComparer.Ordinal)
                .Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));
            var truth = Sides.Enemies(w, m.Side).Sum(e => w.Ships.Where(s => s.Side == e && Planner.Warship(s)).Sum(s => Stats.ShipPower(w, s)));
            var research = m.ResearchFocus != "" ? m.ResearchFocus + (m.ResearchFocusWhy != "" ? $" ({m.ResearchFocusWhy})" : "") : "";
            var day = w.Day;
            c.MindDays.RemoveAll(d => d.Day == day && d.Side == m.Side); // the end of a game can fall on a day already looked at
            var before = MindDaysOf(w, m.Side).LastOrDefault();
            string? Changed(string now, string? was) => before != null && now == was ? null : now;
            c.MindDays.Add(new MindDay(day, m.Side, Changed(goal, before?.Goal), Changed(why, before?.Why), Changed(m.Focus, before?.Focus),
                Changed(research, before?.Research), Changed(jobs, before?.Jobs),
                Math.Round(m.EnemyPowerGuess, 1), Math.Round(truth, 1), Math.Round(m.OwnPower, 1), thoughts.Count > 0 ? thoughts : null));
        }
        c.MindsUpToHour = w.Hour;
    }

    // ---- Frames -----------------------------------------------------------------------------------

    private static string N(double v) => Math.Round(v).ToString("0", Inv);

    /// <summary>Positions are kept to the nearest this many Mm (a tenth of a pixel on a whole-system map), to keep saves small.</summary>
    public const double PositionUnitMm = 100;

    private static string P(double v) => N(v / PositionUnitMm);

    private static ChronicleFrame Frame(World w)
    {
        var fleets = new StringBuilder();
        foreach (var f in w.Fleets)
        {
            if (f.ShipIds.Count == 0) continue;
            var war = w.ShipsOf(f).Count(Planner.Warship);
            if (fleets.Length > 0) fleets.Append(';');
            fleets.Append(f.Id).Append(',').Append((int)f.Side).Append(',').Append(f.ShipIds.Count).Append(',').Append(war)
                .Append(',').Append(P(f.Position.X)).Append(',').Append(P(f.Position.Y));
        }
        var beliefs = new StringBuilder();
        foreach (var s in Sensors.Known(w, seat: true))
        {
            if (s.Own || s.SeenBy == s.Of || w.Hour - s.Hour > BeliefMaxAgeHours) continue;
            var at = Sensors.Predict(w, s.SeenBy, s.FleetId, w.Hour, seat: true) ?? s.Position;
            if (!at.IsFinite) at = s.Position;
            if (beliefs.Length > 0) beliefs.Append(';');
            beliefs.Append((int)s.SeenBy).Append(',').Append((int)s.Of).Append(',').Append(s.FleetId).Append(',').Append(s.Ships)
                .Append(',').Append(P(at.X)).Append(',').Append(P(at.Y)).Append(',').Append(s.Visible ? 1 : 0)
                .Append(',').Append(N(Math.Max(0, w.Hour - s.Hour)));
        }
        var owners = string.Join(";", Economy.Claimable(w).Where(b => b.Owner != null).Select(b => $"{b.Id},{(int)b.Owner!.Value}"));
        return new ChronicleFrame(w.Hour, fleets.ToString(), beliefs.ToString(), owners);
    }

    // ---- Reading it back (the after-game screen) --------------------------------------------------

    private static IEnumerable<string[]> Rows(string packed) =>
        packed.Length == 0 ? Enumerable.Empty<string[]>() : packed.Split(';').Select(r => r.Split(','));

    private static int I(string s) => int.Parse(s, Inv);
    private static double D(string s) => double.Parse(s, Inv);

    public static List<FleetMark> FleetsIn(ChronicleFrame f) =>
        Rows(f.Fleets).Where(r => r.Length >= 6).Select(r => new FleetMark(I(r[0]), (Side)I(r[1]), I(r[2]), I(r[3]), new Vec2(D(r[4]) * PositionUnitMm, D(r[5]) * PositionUnitMm))).ToList();

    public static List<BeliefMark> BeliefsIn(ChronicleFrame f) =>
        Rows(f.Beliefs).Where(r => r.Length >= 8)
            .Select(r => new BeliefMark((Side)I(r[0]), (Side)I(r[1]), I(r[2]), I(r[3]), new Vec2(D(r[4]) * PositionUnitMm, D(r[5]) * PositionUnitMm), r[6] == "1", D(r[7]))).ToList();

    public static Dictionary<int, Side> OwnersIn(ChronicleFrame f) =>
        Rows(f.Owners).Where(r => r.Length >= 2).ToDictionary(r => I(r[0]), r => (Side)I(r[1]));

    /// <summary>The frame at or just before an hour (the first frame for earlier hours); null with no frames.</summary>
    public static int FrameAt(World w, double hour)
    {
        var frames = w.Chronicle.Frames;
        if (frames.Count == 0) return -1;
        int lo = 0, hi = frames.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (frames[mid].Hour <= hour) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// The turning points: the weightiest moments (the end always among them), at most this many, in time order.
    /// Ties go to the earlier moment, so a long run of small claims at the start does not crowd out the war.
    /// </summary>
    public static List<Moment> TurningPoints(World w, int max = 8)
    {
        var all = w.Chronicle.Moments;
        var picked = all.Where(m => m.Kind == "end").Take(1).ToList();
        picked.AddRange(all.Where(m => m.Kind != "end").OrderByDescending(m => m.Weight).ThenBy(m => m.Hour).Take(Math.Max(0, max - picked.Count)));
        return picked.OrderBy(m => m.Hour).ToList();
    }

    /// <summary>A side's mind on a day: that day's record, or the last one before it.</summary>
    public static MindDay? MindOn(World w, Side side, int day)
    {
        var days = MindDaysOf(w, side);
        return days.LastOrDefault(d => d.Day <= day) ?? days.FirstOrDefault();
    }

    /// <summary>A side's mind day by day, oldest first, with every text filled in (stored days leave out what didn't change).</summary>
    public static List<MindDay> MindDaysOf(World w, Side side)
    {
        var list = new List<MindDay>();
        MindDay? prev = null;
        foreach (var d in w.Chronicle.MindDays.Where(d => d.Side == side).OrderBy(d => d.Day))
        {
            var full = d with
            {
                Goal = d.Goal ?? prev?.Goal ?? "", Why = d.Why ?? prev?.Why ?? "", Focus = d.Focus ?? prev?.Focus ?? "",
                Research = d.Research ?? prev?.Research ?? "", Jobs = d.Jobs ?? prev?.Jobs ?? "", Thoughts = d.Thoughts ?? new List<string>(),
            };
            list.Add(full);
            prev = full;
        }
        return list;
    }

    // ---- Words ------------------------------------------------------------------------------------

    /// <summary>A side as the subject of a sentence: "You", "The rival", or with two rivals the race's name.</summary>
    public static string Who(World w, Side s) => s == Side.Player ? "You" : Cap(Sides.Them(w, s));

    private static string Whom(World w, Side s) => s == Side.Player ? "you" : Sides.Them(w, s);

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The planet or moon nearest a point (by name), for saying where a battle was.</summary>
    public static string NearestName(World w, Vec2 at)
    {
        Body? best = null;
        var bestD = double.MaxValue;
        foreach (var b in w.Bodies)
        {
            if (b.ParentId == null) continue;
            var d = (b.Position - at).Length;
            if (d < bestD) { bestD = d; best = b; }
        }
        return best?.Name ?? "the star";
    }
}
