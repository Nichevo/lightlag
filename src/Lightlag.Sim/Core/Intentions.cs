namespace Lightlag.Sim.Core;

/// <summary>What a heard plan is about.</summary>
public enum PlanKind { Attack, Claim, Relief, Raid, Hunt, Scout, Massing }

/// <summary>
/// One of a rival's plans as overheard in its order traffic: late (the signal travels at light speed, then on to
/// command) and partial (the target, the timing or the fleet's size may be missing). A spoofed one is false, which
/// only shows after the game. Kept in the listener's IntelFile, so saves keep it.
/// </summary>
public sealed class Intercept
{
    public int Id { get; set; }
    public PlanKind Kind { get; set; }

    /// <summary>The plan as the source knows it ("fleet:kind:body"), so the same plan is not heard twice.</summary>
    public string Key { get; set; } = "";
    public int FleetId { get; set; }
    public string FleetName { get; set; } = "";

    /// <summary>Ships in the fleet, as heard (0: not caught).</summary>
    public int Ships { get; set; }

    /// <summary>The target as heard (null: the target was not caught).</summary>
    public int? BodyId { get; set; }
    public string BodyName { get; set; } = "";

    /// <summary>When it should arrive, as a range of game hours (null: the timing was not caught).</summary>
    public double? EtaLow { get; set; }
    public double? EtaHigh { get; set; }

    /// <summary>How sure the reading was when heard (0-1); it fades with age (see Intentions.SureNow).</summary>
    public double Sure { get; set; }

    /// <summary>When the signal left the rival, and when it reached this side's command.</summary>
    public double SentHour { get; set; }
    public double HeardHour { get; set; }

    /// <summary>When it was first heard (HeardHour moves on each time it is heard again).</summary>
    public double FirstHeardHour { get; set; }

    /// <summary>How many times it was heard (each catch after the first sharpens it).</summary>
    public int Times { get; set; } = 1;

    // ---- The truth, kept for the after-game screen only (never shown while playing) ----

    /// <summary>A spoofed plan: there was never such a plan.</summary>
    public bool False { get; set; }

    /// <summary>The real target (also when the target was not caught), and what became of the plan.</summary>
    public int? TrueBodyId { get; set; }
    public PlanOutcome Outcome { get; set; }
    public double OutcomeHour { get; set; }
}

public enum PlanOutcome { Pending, CameTrue, CalledOff, Spoofed }

/// <summary>A plan in motion at its source: a fleet going somewhere for a reason, or a side massing at home.</summary>
public sealed record LivePlan(string Key, PlanKind Kind, Fleet Fleet, Body? Body, double? ArriveHour, Vec2 LeakAt);

/// <summary>
/// Reading the rival's intentions. Every six hours each side may overhear the order traffic of its enemies' plans:
/// the chance rises with its intel level and Signals analysis, and with how close its fleets and planets are to the
/// rival fleet doing the plan or to the rival's command. What is heard is partial (better at higher levels) and
/// arrives late by light lag. A side with False order traffic sometimes leaks plans it does not have.
/// Plans come from the same thing for everyone: fleets on their way to a planet (the computer's fleet jobs give
/// them a name), and a computer side massing at home for a strike. No cheating: what a side reads comes only
/// from what it overheard.
/// </summary>
public static class Intentions
{
    public const int WindowHours = 6;
    public const double ListenFullAu = 0.4, ListenMaxAu = 1.6;
    public const double SeatShare = 0.7;
    public const double BaseChance = 0.08, ChancePerLevel = 0.07, MaxChance = 0.85;
    public const double SpoofChance = 0.05;
    public const double RehearHours = 48, DecodeBaseHours = 14;
    public const double FadeDays = 10;
    public const double ArriveAu = 0.05;
    public const int MaxKept = 80;
    public const string FalseTraffic = "false-traffic";

    /// <summary>The plans a side has in motion now (warship fleets only; supply and settler runs are not plans).</summary>
    public static List<LivePlan> PlansOf(World w, Side side)
    {
        var list = new List<LivePlan>();
        var mind = Mind.Of(w, side);
        var seat = Light.Seat(w, side);
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0 && f.Position.IsFinite).OrderBy(f => f.Id))
        {
            var job = mind?.JobOf(f.Id);
            var body = (job?.BodyId ?? f.Flight?.TargetBodyId) is int id ? w.FindBody(id) : null;
            PlanKind? kind = job?.Kind switch
            {
                JobKind.Strike => PlanKind.Attack,
                JobKind.Claim => body?.Owner is Side o && o != side ? PlanKind.Attack : PlanKind.Claim,
                JobKind.Break => PlanKind.Relief,
                JobKind.Strafe or JobKind.Ambush => PlanKind.Raid,
                JobKind.Hunt => PlanKind.Hunt,
                JobKind.Probe => PlanKind.Scout,
                JobKind.Guard or JobKind.Refit or JobKind.Escort => null,
                _ => body == null || f.Flight == null ? null
                    : body.Owner is Side owner && owner != side ? PlanKind.Attack
                    : body.Owner == null && body.Industry > 0 ? PlanKind.Claim
                    : body.Owner == side && body.ClaimSide is Side c && c != side ? PlanKind.Relief
                    : null,
            };
            if (kind == null || !Combat.Armed(w, f)) continue;
            // A raid or hunt with no planet: name the planet nearest where it is going.
            if (body == null && job != null && job.Point.IsFinite && kind is PlanKind.Raid or PlanKind.Hunt or PlanKind.Scout)
                body = Nearest(w, job.Point);
            var arrive = f.Flight is Flight fl && (body == null || fl.TargetBodyId == body.Id || job != null) && fl.ArriveHour > w.Hour ? fl.ArriveHour : (double?)null;
            list.Add(new LivePlan($"{f.Id}:{kind}:{body?.Id}", kind.Value, f, body, arrive, f.Position));
        }
        // Massing at home for a strike (the computer only says so in its orders when it calls fleets in).
        if (mind != null && mind.GatherUntilHour > w.Hour && Economy.ShipyardOf(w, side) is Body home)
        {
            var biggest = w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0).OrderByDescending(f => f.ShipIds.Count).ThenBy(f => f.Id).FirstOrDefault();
            if (biggest != null)
                list.Add(new LivePlan($"mass:{side}:{(int)(mind.GatherUntilHour / 24)}", PlanKind.Massing, biggest, home, mind.GatherUntilHour, seat));
        }
        return list;
    }

    private static Body? Nearest(World w, Vec2 at) =>
        w.Bodies.Where(b => b.Position.IsFinite && b.Industry > 0).OrderBy(b => Vec2.Distance(b.Position, at)).ThenBy(b => b.Id).FirstOrDefault();

    /// <summary>The listener's fleets and planets: where signals can be picked up.</summary>
    public static List<Vec2> Ears(World w, Side side) =>
        w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0 && f.Position.IsFinite).OrderBy(f => f.Id).Select(f => f.Position)
            .Concat(w.Bodies.Where(b => b.Owner == side && b.Position.IsFinite).OrderBy(b => b.Id).Select(b => b.Position)).ToList();

    /// <summary>How well a point is covered by the ears (1 within ListenFullAu, falling to 0 at ListenMaxAu), and the nearest ear.</summary>
    public static (double Cover, Vec2 Ear) Cover(List<Vec2> ears, Vec2 at)
    {
        if (ears.Count == 0 || !at.IsFinite) return (0, at);
        var ear = ears.OrderBy(e => Vec2.Distance(e, at)).First();
        var au = Vec2.Distance(ear, at) / Physics.MmPerAu;
        return (Math.Clamp((ListenMaxAu - au) / (ListenMaxAu - ListenFullAu), 0, 1), ear);
    }

    /// <summary>The chance per six-hour window of overhearing a plan, before cover.</summary>
    public static double Chance(World w, Side listener, int level) =>
        Math.Min(MaxChance, (BaseChance + ChancePerLevel * level) * Tech.Factor(w, listener, Tech.SignalsAnalysis));

    /// <summary>Every six game hours: overhear plans, leak false ones, and note what became of the plans heard.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Sides.Length < 2 || w.Tick % WindowHours != 0 || w.WinHour != null) return;
        var window = (ulong)(w.Tick / WindowHours);
        var plans = new Dictionary<Side, List<LivePlan>>();
        List<LivePlan> Plans(Side s) => plans.TryGetValue(s, out var p) ? p : plans[s] = PlansOf(w, s);
        foreach (var listener in Sides.InPlay(w).Where(s => !Sides.Out(w, s)))
        {
            var ears = Ears(w, listener);
            foreach (var file in Intel.Files(w, listener))
            {
                var source = file.About;
                if (Sides.Out(w, source)) continue;
                var level = Intel.Level(file);
                var chance = Chance(w, listener, level);
                var live = Plans(source);
                var seatCover = Cover(ears, Light.Seat(w, source));
                foreach (var plan in live)
                {
                    // A plan already heard is listened for again after a while: the next catch sharpens it (closer, so surer and narrower).
                    var known = file.Intercepts.LastOrDefault(i => i.Key == plan.Key && !i.False && i.Outcome == PlanOutcome.Pending);
                    if (known != null && w.Hour - known.SentHour < RehearHours) continue;
                    var fleetCover = Cover(ears, plan.LeakAt);
                    var (cover, ear, from) = fleetCover.Cover >= seatCover.Cover * SeatShare
                        ? (fleetCover.Cover, fleetCover.Ear, plan.LeakAt)
                        : (seatCover.Cover * SeatShare, seatCover.Ear, Light.Seat(w, source));
                    if (cover <= 0 || Roll(w, window, listener, plan.Key, 1) >= chance * cover) continue;
                    var heard = Hear(w, file, plan, level, cover, window, listener);
                    heard.HeardHour = w.Hour + Lag(w, listener, from, ear) + DecodeHours(w, listener, level);
                    if (known != null && known.HeardHour <= w.Hour) Sharpen(known, heard);
                    else if (known == null) Add(file, heard);
                    else continue;
                    sim.Log.Info("intel", known != null ? "Plan heard again" : "Plan overheard",
                        new { listener = listener.ToString(), about = source.ToString(), kind = plan.Kind.ToString(), fleet = plan.Fleet.Id, body = plan.Body?.Id, heard.HeardHour });
                }
                // False order traffic: now and then a plan that isn't there, aimed at one of the listener's planets.
                if (Tech.Has(w, source, FalseTraffic) && seatCover.Cover > 0 && Roll(w, window, listener, "spoof", 2) < SpoofChance * Math.Max(seatCover.Cover, 0.5)
                    && Spoof(w, file, source, listener, level, Math.Max(seatCover.Cover, 0.5), window) is Intercept fake)
                {
                    fake.HeardHour = w.Hour + Lag(w, listener, Light.Seat(w, source), seatCover.Ear) + DecodeHours(w, listener, level);
                    Add(file, fake);
                    sim.Log.Info("intel", "False plan leaked", new { listener = listener.ToString(), about = source.ToString(), body = fake.BodyId, fake.HeardHour });
                }
                Settle(w, file, live);
            }
        }
    }

    /// <summary>A plan heard again: the newer catch fills in what was missing and replaces the timing; it is as sure as the surer of the two.</summary>
    private static void Sharpen(Intercept known, Intercept again)
    {
        if (again.BodyId != null) (known.BodyId, known.BodyName) = (again.BodyId, again.BodyName);
        if (again.Ships > 0) known.Ships = again.Ships;
        if (again.EtaLow != null) (known.EtaLow, known.EtaHigh) = (again.EtaLow, again.EtaHigh);
        known.Sure = Math.Round(Math.Max(again.Sure, Math.Min(0.95, known.Sure + 0.1)), 2);
        known.FleetName = again.FleetName;
        (known.SentHour, known.HeardHour) = (again.SentHour, again.HeardHour);
        known.Times++;
    }

    /// <summary>Hours to make sense of what was caught: long at low intel levels, shorter with Signals analysis.</summary>
    public static double DecodeHours(World w, Side listener, int level) =>
        Math.Round(DecodeBaseHours * (1 - 0.18 * level) / Tech.Factor(w, listener, Tech.SignalsAnalysis), 1);

    private static void Add(IntelFile file, Intercept i)
    {
        i.FirstHeardHour = i.HeardHour;
        i.Id = ++file.LastInterceptId;
        file.Intercepts.Add(i);
        // Keep the newest; settled ones go first.
        while (file.Intercepts.Count > MaxKept)
        {
            var old = file.Intercepts.FirstOrDefault(x => x.Outcome != PlanOutcome.Pending) ?? file.Intercepts[0];
            file.Intercepts.Remove(old);
        }
    }

    /// <summary>Light time from where the signal leaked to the ear that caught it, then on to the listener's command.</summary>
    private static double Lag(World w, Side listener, Vec2 from, Vec2 ear) =>
        !w.Light.On ? 0 : Vec2.Distance(from, ear) / Light.MmPerHour(w) + Light.DelayHours(w, listener, ear);

    /// <summary>What was caught of a plan: more of it, and surer, at higher intel levels and better cover.</summary>
    private static Intercept Hear(World w, IntelFile file, LivePlan plan, int level, double cover, ulong window, Side listener)
    {
        var i = new Intercept
        {
            Kind = plan.Kind,
            Key = plan.Key,
            FleetId = plan.Fleet.Id,
            FleetName = plan.Fleet.Name,
            TrueBodyId = plan.Body?.Id,
            SentHour = w.Hour,
            Sure = Math.Round(Math.Clamp((0.35 + 0.12 * level) * (0.5 + 0.5 * cover), 0.1, 0.9), 2),
        };
        if (plan.Body != null && Roll(w, window, listener, plan.Key, 3) < 0.35 + 0.15 * level)
        {
            i.BodyId = plan.Body.Id;
            i.BodyName = plan.Body.Name;
        }
        if (Roll(w, window, listener, plan.Key, 4) < 0.25 + 0.15 * level) i.Ships = plan.Fleet.ShipIds.Count;
        if (plan.ArriveHour is double at && Roll(w, window, listener, plan.Key, 5) < 0.3 + 0.15 * level)
            (i.EtaLow, i.EtaHigh) = Spread(w, at, level, Roll(w, window, listener, plan.Key, 6));
        return i;
    }

    /// <summary>An arrival time widened to a range: wide at low levels, narrower when the rival is well read; not centred.</summary>
    private static (double Low, double High) Spread(World w, double at, int level, double skew)
    {
        var left = Math.Max(1, at - w.Hour);
        var width = Math.Max(0.15, 0.6 - 0.1 * level);
        var low = w.Hour + left * (1 - width * (0.5 + skew));
        var high = w.Hour + left * (1 + width * (1.5 - skew));
        return (Math.Round(Math.Max(w.Hour, low), 1), Math.Round(high, 1));
    }

    private static Intercept? Spoof(World w, IntelFile file, Side source, Side listener, int level, double cover, ulong window)
    {
        var targets = w.Bodies.Where(b => b.Owner == listener && b.Industry > 0).OrderBy(b => b.Id).ToList();
        var fleets = w.Fleets.Where(f => f.Side == source && f.ShipIds.Count > 0 && Combat.Armed(w, f)).OrderBy(f => f.Id).ToList();
        if (targets.Count == 0 || fleets.Count == 0) return null;
        var body = targets[(int)(Roll(w, window, listener, "spoof", 3) * targets.Count) % targets.Count];
        var fleet = fleets[(int)(Roll(w, window, listener, "spoof", 4) * fleets.Count) % fleets.Count];
        if (file.Intercepts.Any(i => i.FleetId == fleet.Id && i.BodyId == body.Id && i.Outcome == PlanOutcome.Pending)) return null;
        var days = 5 + 15 * Roll(w, window, listener, "spoof", 5);
        var i = new Intercept
        {
            Kind = body.Shipyard || Roll(w, window, listener, "spoof", 6) < 0.6 ? PlanKind.Attack : PlanKind.Raid,
            Key = $"spoof:{window}",
            FleetId = fleet.Id,
            FleetName = fleet.Name,
            BodyId = body.Id,
            BodyName = body.Name,
            TrueBodyId = body.Id,
            SentHour = w.Hour,
            Sure = Math.Round(Math.Clamp((0.35 + 0.12 * level) * (0.5 + 0.5 * cover), 0.1, 0.9), 2),
            False = true,
        };
        if (Roll(w, window, listener, "spoof", 7) < 0.5) i.Ships = fleet.ShipIds.Count;
        (i.EtaLow, i.EtaHigh) = Spread(w, w.Hour + days * 24, level, Roll(w, window, listener, "spoof", 8));
        return i;
    }

    /// <summary>What became of each plan heard (for the after-game screen): it got there, it was dropped, or it was never real.</summary>
    private static void Settle(World w, IntelFile file, List<LivePlan> live)
    {
        foreach (var i in file.Intercepts.Where(i => i.Outcome == PlanOutcome.Pending))
        {
            if (i.False)
            {
                if (w.Hour > (i.EtaHigh ?? i.SentHour + 20 * 24) + 48) (i.Outcome, i.OutcomeHour) = (PlanOutcome.Spoofed, w.Hour);
                continue;
            }
            var fleet = w.FindFleet(i.FleetId);
            var body = i.TrueBodyId is int b ? w.FindBody(b) : null;
            if (i.Kind == PlanKind.Massing)
            {
                if (live.All(p => p.Key != i.Key)) (i.Outcome, i.OutcomeHour) = (PlanOutcome.CameTrue, w.Hour);
                continue;
            }
            if (fleet != null && body != null && Vec2.Distance(fleet.Position, body.Position) <= ArriveAu * Physics.MmPerAu)
                (i.Outcome, i.OutcomeHour) = (PlanOutcome.CameTrue, w.Hour);
            else if (live.All(p => p.Key != i.Key))
                (i.Outcome, i.OutcomeHour) = (PlanOutcome.CalledOff, w.Hour);
        }
    }

    /// <summary>A roll in [0, 1) from the game seed and what is being rolled for: the same in every replay, and no other stream moves.</summary>
    private static double Roll(World w, ulong window, Side listener, string key, int salt)
    {
        var h = Mix(w.Seed ^ 0x1A7E57A11UL);
        h = Mix(h ^ window);
        h = Mix(h ^ ((ulong)(int)listener * 256UL + (ulong)(uint)salt));
        foreach (var ch in key) h = Mix(h ^ ch);
        return (h >> 11) * (1.0 / (1UL << 53));
    }

    private static ulong Mix(ulong z)
    {
        z = unchecked(z + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        return z ^ (z >> 31);
    }

    // ---- Reading what was heard ------------------------------------------------------------------

    /// <summary>How sure a heard plan is now: its sureness, fading over FadeDays after it was heard (and once its window has passed).</summary>
    public static double SureNow(World w, Intercept i)
    {
        var age = Math.Max(0, w.Hour - i.HeardHour);
        var late = i.EtaHigh is double h && w.Hour > h ? (w.Hour - h) / 48 : 0;
        return Math.Clamp(i.Sure * (1 - age / (FadeDays * 24)) - 0.3 * late, 0, 1);
    }

    /// <summary>The plans the side has heard of and that still count, surest first (none still on their way at light speed).</summary>
    public static List<Intercept> Heard(World w, Side side, Side? about = null, bool old = false) =>
        Intel.Of(w, side, about).Intercepts.Where(i => i.HeardHour <= w.Hour && (old || SureNow(w, i) > 0))
            .OrderByDescending(i => old ? i.HeardHour : SureNow(w, i)).ThenByDescending(i => i.Id).ToList();

    /// <summary>The single likeliest next move, if any is heard and still counts.</summary>
    public static Intercept? Likeliest(World w, Side side, Side? about = null) =>
        Heard(w, side, about).Where(i => i.BodyId != null || i.Kind == PlanKind.Massing).FirstOrDefault() ?? Heard(w, side, about).FirstOrDefault();

    /// <summary>Plans heard against one of the side's planets, sure at least minSure, with timing caught and due within the hours.</summary>
    public static IEnumerable<Intercept> Against(World w, Side side, int bodyId, double withinHours, double minSure)
    {
        foreach (var about in Sides.Enemies(w, side))
        foreach (var i in Heard(w, side, about))
            if (i.BodyId == bodyId && i.Kind is PlanKind.Attack or PlanKind.Raid && SureNow(w, i) >= minSure
                && i.EtaLow is double low && low - w.Hour <= withinHours && i.EtaHigh >= w.Hour)
                yield return i;
    }

    public static string KindText(PlanKind k) => k switch
    {
        PlanKind.Attack => "Attack",
        PlanKind.Claim => "Claim",
        PlanKind.Relief => "Relief",
        PlanKind.Raid => "Raid",
        PlanKind.Hunt => "Hunt",
        PlanKind.Scout => "Scouting",
        PlanKind.Massing => "Massing fleets",
        _ => k.ToString(),
    };

    /// <summary>"Attack on Verdant", "Massing fleets at Tamber", "Claim (target not caught)".</summary>
    public static string What(Intercept i) => i.Kind switch
    {
        PlanKind.Massing => i.BodyName.Length > 0 ? $"Massing fleets at {i.BodyName}" : "Massing fleets",
        _ when i.BodyName.Length == 0 => $"{KindText(i.Kind)} (target not caught)",
        PlanKind.Claim => $"Claim {i.BodyName}",
        PlanKind.Scout => $"Scouting near {i.BodyName}",
        PlanKind.Hunt => $"Hunt near {i.BodyName}",
        PlanKind.Raid => $"Raid near {i.BodyName}",
        PlanKind.Relief => $"Relief of {i.BodyName}",
        _ => $"{KindText(i.Kind)} on {i.BodyName}",
    };

    /// <summary>"3 to 6 days", "any time now to 2 days", "timing not caught", or for massing, when a strike could follow.</summary>
    public static string When(World w, Intercept i)
    {
        if (i.EtaLow is not double low || i.EtaHigh is not double high) return "timing not caught";
        if (w.Hour > high) return "expected by now";
        var a = Math.Max(0, low - w.Hour) / 24;
        var b = Math.Max(0, high - w.Hour) / 24;
        string D(double d) => d < 1 ? $"{Math.Max(1, Math.Round(d * 24)):0} h" : $"{Math.Round(d):0} day{(Math.Round(d) == 1 ? "" : "s")}";
        var range = a < 0.25 ? $"any time now to {D(b)}" : Math.Round(a) == Math.Round(b) && a >= 1 ? $"about {D(a)}" : $"{D(a).Replace(" days", "").Replace(" day", "")} to {D(b)}";
        return i.Kind == PlanKind.Massing ? $"strike could follow in {range}" : range;
    }

    /// <summary>One line for lists: "Likely next move: Attack on Verdant, 3 to 6 days · 4 ships · 55% sure".</summary>
    public static string Line(World w, Intercept i) =>
        $"{What(i)}, {When(w, i)}" + (i.Ships > 0 ? $" · {i.Ships} ship{(i.Ships == 1 ? "" : "s")}" : "") + $" · {SureNow(w, i):P0} sure";

    public static string OutcomeText(PlanOutcome o) => o switch
    {
        PlanOutcome.CameTrue => "came true",
        PlanOutcome.CalledOff => "called off or changed",
        PlanOutcome.Spoofed => "false: planted by its False order traffic",
        _ => "still under way at the end",
    };

    /// <summary>Report text: plans heard against the truth, both ways.</summary>
    public static string Describe(World w) =>
        "# Plans overheard\n\n" + string.Join("\n\n", Sides.InPlay(w).SelectMany(s => Intel.Files(w, s).Select(f =>
            $"{s} on {f.About}: {f.Intercepts.Count} heard, {f.Intercepts.Count(i => i.Outcome == PlanOutcome.CameTrue)} came true, "
            + $"{f.Intercepts.Count(i => i.Outcome == PlanOutcome.CalledOff)} called off, {f.Intercepts.Count(i => i.False)} false")));
}
