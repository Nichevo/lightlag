namespace Lightlag.Sim.Core;

// ---- Data --------------------------------------------------------------------------------------

/// <summary>
/// One stop on a patrol loop: a body's orbit, a point on the straight run between two bodies (it slides
/// with them), or a fixed point in space; and how long the fleet waits there before the next stop.
/// </summary>
public sealed class LoopStop
{
    public int? BodyId { get; set; }

    /// <summary>On a run: the two bodies and how far along from the first (0..1).</summary>
    public int? FromBodyId { get; set; }
    public int? ToBodyId { get; set; }
    public double T { get; set; } = 0.5;

    /// <summary>A fixed point (used when there is no body and no run).</summary>
    public Vec2 Point { get; set; }

    public double WaitHours { get; set; } = 24;

    public LoopStop Copy() => (LoopStop)MemberwiseClone();
}

/// <summary>Several fleets, one arrival: together, as soon as each can, or in waves.</summary>
public enum StrikeArrival { Together, AsSoonAsEach, Waves }

/// <summary>A strike plan saved to be sent (again) later; arrival times are worked out afresh each time it is sent.</summary>
public sealed class StrikePlan
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public string Name { get; set; } = "";
    public List<int> FleetIds { get; set; } = new();
    public int? TargetBodyId { get; set; }
    public int? TargetFleetId { get; set; }
    public StrikeArrival Arrival { get; set; }
    public double WaveGapHours { get; set; } = 12;
    public double Burn { get; set; } = 0.8;
    public double SavedHour { get; set; }
    public int TimesSent { get; set; }
}

/// <summary>A fleet sent on a strike: what it is part of and when it should get there (for its status line).</summary>
public sealed class StrikeOrder
{
    public int? PlanId { get; set; }
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public double SentHour { get; set; }
    public double ArriveHour { get; set; }
    public double? ArrivedHour { get; set; }
}

/// <summary>What a coverage row is about: a planet (with any moons of it we hold), a group of moons, or a cargo run.</summary>
public enum PlaceKind { Planet, Moons, Run }

/// <summary>Covered: enough help is there or close; thin: someone would answer but too slowly or too weakly; open: nobody would.</summary>
public enum CoverState { Covered, Thin, Open }

public sealed record CoverPlace(string Key, PlaceKind Kind, string Name, List<int> BodyIds, int? FromBodyId, int? ToBodyId, string Worth)
{
    public int MainBodyId => BodyIds.Count > 0 ? BodyIds[0] : FromBodyId ?? -1;
}

/// <summary>A fleet that covers a place: how many hours until it is there (0: there now), its strength, and how ("holds orbit here").</summary>
public sealed record Coverer(int FleetId, string Name, double Hours, double Power, bool OnSpot, string How);

public sealed record CoverJudgement(CoverPlace Place, CoverState State, string Why, List<Coverer> Who, double Threat, string ThreatNote, double Guns);

/// <summary>A fleet that could cover a place: how long it would take, its strength, why not (null: it can), the order and what its leaving uncovers.</summary>
public sealed record CoverOffer(int FleetId, string Name, double? Hours, double Power, string? Problem, string Leaves);

/// <summary>One fleet's part in a strike: the order, its burn, when it arrives, and why it can't go (null: it can).</summary>
public sealed record StrikeLeg(int FleetId, string Name, FlightRequest? Request, double Burn, double ArriveHour, string? Problem, bool Supply);

/// <summary>A strike worked out: each fleet's leg, the odds as a guess against what was last seen there, what leaving uncovers.</summary>
public sealed record StrikePreview(string TargetName, List<StrikeLeg> Legs, double Power, double EnemyPower, string EnemyNote, string Uncovers, string? SupplyNote, string? Problem)
{
    public double? Odds => EnemyPower > 0 ? Power / EnemyPower : null;
}

// ---- Coverage ----------------------------------------------------------------------------------

/// <summary>
/// Who covers what. Every planet we hold (with its moons), every group of moons we hold round a planet that is
/// not ours, and every cargo run is judged against the strongest enemy seen near it lately (or, with nothing
/// seen, a raid of two ships): Covered when the fleets on the spot and those that would answer within a day,
/// with the planet's guns, are at least as strong; Thin when someone would answer but too slowly or too weakly;
/// Open when nobody would. Read-only: a view over roles, missions, zones, calls and sightings.
/// </summary>
public static class Coverage
{
    /// <summary>Help that gets there within this many hours counts as cover.</summary>
    public const double CoverHours = 24;

    /// <summary>Enemies seen this close (AU) in the last ThreatDays set the threat a place is judged against.</summary>
    public const double ThreatAu = 1.5;
    public const double ThreatDays = 10;

    /// <summary>With nothing seen near: judged against a raid of this many ships.</summary>
    public const int BaselineShips = 2;

    /// <summary>A fleet within this (AU) of a cargo run counts as on the run.</summary>
    public const double OnRunAu = 0.15;

    /// <summary>A working fort over a planet counts as this many enemy-ship values of defence.</summary>
    public const double FortShips = 1.5;

    /// <summary>Role fleets answer calls at this share of top speed (see HelpCalls.Aim).</summary>
    public const double AnswerBurn = 0.8;

    private static double Au => Physics.MmPerAu;

    public static string StateName(CoverState s) => s switch { CoverState.Covered => "covered", CoverState.Thin => "thin", _ => "open" };

    // ---- Places --------------------------------------------------------------------------------

    /// <summary>Every place the side should cover, planets first (by id), then moon groups, then cargo runs.</summary>
    public static List<CoverPlace> Places(World w, Side side)
    {
        var list = new List<CoverPlace>();
        var mine = w.Bodies.Where(b => b.Owner == side && b.Kind != BodyKind.Star).OrderBy(b => b.Id).ToList();
        var roots = mine.Select(b => Root(w, b)).DistinctBy(b => b.Id).OrderBy(b => b.Id).ToList();
        foreach (var root in roots)
        {
            var moons = mine.Where(b => b.Id != root.Id && Root(w, b).Id == root.Id).OrderBy(b => b.Id).ToList();
            if (root.Owner == side)
            {
                var ids = new List<int> { root.Id };
                ids.AddRange(moons.Select(m => m.Id));
                var name = moons.Count == 0 ? root.Name : $"{root.Name} and {(moons.Count == 1 ? moons[0].Name : $"{moons.Count} moons")}";
                list.Add(new CoverPlace($"planet:{root.Id}", PlaceKind.Planet, name, ids, null, null, Worth(w, side, root, moons)));
            }
            else
            {
                var name = moons.Count == 1 ? moons[0].Name : $"{root.Name} moons";
                list.Add(new CoverPlace($"moons:{root.Id}", PlaceKind.Moons, name, moons.Select(m => m.Id).ToList(), null, null,
                    moons.Count == 1 ? Worth(w, side, moons[0], new()) : $"{moons.Count} moons"));
            }
        }
        var runs = new List<(int A, int B, string What)>();
        foreach (var r in w.FixedRuns.Where(r => r.Side == side).OrderBy(r => r.Id))
            runs.Add((r.FromBodyId, r.ToBodyId, $"{Colonies.GoodName(r.Good).ToLowerInvariant()} every {r.EveryDays:0} d"));
        foreach (var s in w.Shipments.Where(s => s.Side == side).OrderBy(s => s.Id))
            runs.Add((s.FromBodyId, s.ToBodyId, "cargo under way"));
        foreach (var g in runs.GroupBy(r => (Math.Min(r.A, r.B), Math.Max(r.A, r.B))).OrderBy(g => g.Key.Item1).ThenBy(g => g.Key.Item2))
        {
            var first = g.First();
            if (first.A == first.B || w.FindBody(first.A) is not Body a || w.FindBody(first.B) is not Body b) continue;
            // Moons of one planet are one place already: a run between them is covered with it.
            if (Root(w, a).Id == Root(w, b).Id) continue;
            var what = string.Join(", ", g.Select(x => x.What).Distinct().Take(2));
            list.Add(new CoverPlace($"run:{g.Key.Item1}:{g.Key.Item2}", PlaceKind.Run, $"{a.Name} to {b.Name}", new(), first.A, first.B, $"cargo run · {what}"));
        }
        return list;
    }

    private static Body Root(World w, Body b) => b.Kind == BodyKind.Moon && b.ParentId is int p && w.FindBody(p) is Body parent ? parent : b;

    private static string Worth(World w, Side side, Body b, List<Body> moons)
    {
        var bits = new List<string>();
        if (b == Economy.HomeOf(w, side)) bits.Add("home");
        if (b.Shipyard) bits.Add("the yard");
        if (b.PopK > 0) bits.Add($"{b.PopK:0}k people");
        else bits.Add("outpost");
        if (moons.Count > 0) bits.Add($"{moons.Count} moon{(moons.Count == 1 ? "" : "s")}");
        return string.Join(" · ", bits);
    }

    public static CoverPlace? Find(World w, Side side, string key) => Places(w, side).Find(p => p.Key == key);

    /// <summary>Where a place is now: a planet's position, or the middle of a run.</summary>
    public static Vec2 Where(World w, CoverPlace p)
    {
        if (p.Kind == PlaceKind.Run && w.FindBody(p.FromBodyId ?? -1) is Body a && w.FindBody(p.ToBodyId ?? -1) is Body b) return (a.Position + b.Position) * 0.5;
        var root = w.FindBody(p.MainBodyId);
        return root == null ? Vec2.Zero : Root(w, root).Position;
    }

    // ---- The threat ------------------------------------------------------------------------------

    /// <summary>The strength a place is judged against, and where that figure comes from (a guess, from what was seen).</summary>
    public static (double Power, string Note) Threat(World w, Side side, CoverPlace p)
    {
        var at = Where(w, p);
        var near = w.Sightings.Where(s => s.SeenBy == side && !s.Own && !Lanes.CargoSighting(s) && w.Hour - s.Hour <= ThreatDays * 24
                                          && Vec2.Distance(s.Position, at) <= ThreatAu * Au)
            .OrderByDescending(HelpCalls.EnemyPower).ThenBy(s => s.FleetId).FirstOrDefault();
        var baseline = BaselineShips * GameData.Ai.EnemyShipValue;
        if (near == null || HelpCalls.EnemyPower(near) < baseline) return (baseline, near == null ? "a raid of two ships (nothing seen near lately)" : $"a raid of two ships ({near.Name} seen near was smaller)");
        return (HelpCalls.EnemyPower(near), $"{near.Name} ({near.Ships} ship{(near.Ships == 1 ? "" : "s")}), seen {Age(w.Hour - near.Hour)} ago");
    }

    public static string Age(double hours) => hours < 1 ? "under an hour" : hours < 48 ? $"{hours:0} h" : $"{hours / 24:0.#} d";

    /// <summary>The planet guns over a place: working forts, each worth FortShips ships.</summary>
    public static double Guns(World w, CoverPlace p) =>
        p.BodyIds.Select(w.FindBody).OfType<Body>().Count(b => Starbases.Fort(w, b) != null) * FortShips * GameData.Ai.EnemyShipValue;

    // ---- Who covers it -------------------------------------------------------------------------

    /// <summary>A fighting fleet: armed, and not only supply or cargo ships.</summary>
    public static bool Fighting(World w, Fleet f) => f.ShipIds.Count > 0 && Combat.Armed(w, f) && !HelpCalls.SupplyOnly(w, f) && !Lanes.CargoOnly(w, f);

    /// <summary>Does this fleet sit on the place now (in orbit there, holding its zone there, or on the run)?</summary>
    public static bool OnSpot(World w, Fleet f, CoverPlace p)
    {
        if (f.Stance is Stance.Avoid or Stance.ReturnHome || f.Retreating) return false;
        if (p.Kind == PlaceKind.Run)
        {
            if (w.FindBody(p.FromBodyId ?? -1) is not Body a || w.FindBody(p.ToBodyId ?? -1) is not Body b) return false;
            return (f.Flight == null || f.Mission == Mission.Patrol) && Lanes.DistToSegment(f.Position, a.Position, b.Position) <= OnRunAu * Au;
        }
        if (f.Flight != null) return false;
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int ob && p.BodyIds.Contains(ob)) return true;
        return f.ZoneId is int z && w.Zones.Find(o => o.Id == z) is Zone zone && p.BodyIds.Select(w.FindBody).OfType<Body>()
            .Any(b => Vec2.Distance(Lanes.CenterOf(w, zone), b.Position) <= HelpCalls.NearAu * Au);
    }

    /// <summary>Is this fleet's job to be at the place (a hold or patrol there, a loop stop there, Cover it, or flying there)?</summary>
    public static bool Posted(World w, Fleet f, CoverPlace p)
    {
        if (f.CoverKey == p.Key) return true;
        if (f.Mission is Mission.Hold or Mission.Patrol && f.Loop.Count == 0 && f.MissionBodyId is int mb && p.BodyIds.Contains(mb)) return true;
        if (f.Mission == Mission.Patrol && f.Loop.Count > 0)
            return f.Loop.Any(s => s.BodyId is int sb && p.BodyIds.Contains(sb)
                                   || p.Kind == PlaceKind.Run && s.FromBodyId is int x && s.ToBodyId is int y
                                   && (x == p.FromBodyId && y == p.ToBodyId || x == p.ToBodyId && y == p.FromBodyId));
        return f.Flight?.TargetBodyId is int tb && p.BodyIds.Contains(tb) && f.Chase == null && !f.Retreating;
    }

    /// <summary>The call a place would make, for asking a fleet whether its role answers it.</summary>
    private static IEnumerable<HelpCall> Probes(World w, Side side, CoverPlace p)
    {
        var at = Where(w, p);
        if (p.Kind == PlaceKind.Run)
        {
            yield return new HelpCall { Side = side, Reasons = CallReason.Raiding, FromBodyId = p.FromBodyId, ToBodyId = p.ToBodyId, Position = at, RaisedHour = double.NegativeInfinity };
            yield break;
        }
        foreach (var id in p.BodyIds)
            yield return new HelpCall { Side = side, Reasons = CallReason.EnemyNear, BodyId = id, Position = w.FindBody(id)?.Position ?? at, RaisedHour = double.NegativeInfinity };
    }

    /// <summary>Why this fleet's role would not answer here (null: it would, as far as role, range and wear go).</summary>
    public static string? RoleWhyNot(World w, Fleet f, CoverPlace p)
    {
        string? why = null;
        foreach (var c in Probes(w, f.Side, p))
        {
            var no = HelpCalls.WhyNot(w, f, c, checkFree: false);
            if (no == null) return null;
            why ??= no;
        }
        return why ?? "it has no role";
    }

    /// <summary>
    /// Rough hours for a fleet to fly to a point at a share of its top speed (burn, then coast, then brake), the
    /// speed change it costs, and the share of top speed it can afford on the fuel it has. Null when it can't fly
    /// or has too little fuel for even a slow trip. An estimate: the planner aims at where a planet will be.
    /// </summary>
    public static (double Hours, double DeltaV, double Burn)? Trip(World w, Fleet f, Vec2 to, double burn)
    {
        var perf = FleetPerformance.Of(w, f);
        if (perf.MaxSpeed <= 0 || perf.Accel <= 0) return null;
        var d = Vec2.Distance(f.Position, to);
        var extra = f.Docked ? Starbases.D.UndockHours : 0;
        if (d < 1) return (extra, 0, burn);
        var v = burn * perf.MaxSpeed;
        // Too little fuel for this burn: ease off to what it can afford, down to a slow drift.
        if (2 * v > perf.DeltaVLeft) v = perf.DeltaVLeft / 2 * 0.95;
        if (v < perf.MaxSpeed * FlightPlanner.MinSpeedFraction * 0.5) return null;
        var a = perf.Accel;
        double hours, dv;
        if (v * v / a >= d) { hours = 2 * Math.Sqrt(d / a); dv = 2 * Math.Sqrt(d * a); }
        else { hours = 2 * v / a + (d - v * v / a) / v; dv = 2 * v; }
        return (hours + extra, dv, v / perf.MaxSpeed);
    }

    /// <summary>Hours for a call from this point to reach the fleet (light lag), 0 with light lag off.</summary>
    public static double HearHours(World w, Fleet f, Vec2 at) => w.Light.On ? Vec2.Distance(at, f.Position) / Light.MmPerHour(w) : 0;

    /// <summary>
    /// The fleets that cover a place now, soonest first: those on the spot, those posted there (on their way),
    /// and free role fleets that would answer a call from it (call's light, then the trip).
    /// </summary>
    public static List<Coverer> Who(World w, Side side, CoverPlace p, ISet<int>? without = null)
    {
        var at = Where(w, p);
        var list = new List<Coverer>();
        foreach (var f in w.Fleets.Where(f => f.Side == side && (without == null || !without.Contains(f.Id))).OrderBy(f => f.Id))
        {
            if (!Fighting(w, f)) continue;
            var power = Stats.Power(w, f);
            if (OnSpot(w, f, p)) { list.Add(new Coverer(f.Id, f.Name, 0, power, true, p.Kind == PlaceKind.Run ? "on the run" : "here")); continue; }
            if (Posted(w, f, p) && !f.Retreating)
            {
                var eta = f.Flight != null && f.Flight.TargetBodyId is int tb && p.BodyIds.Contains(tb) ? Math.Max(0, f.Flight.ArriveHour - w.Hour)
                    : Trip(w, f, at, 0.5)?.Hours;
                if (eta is double e) list.Add(new Coverer(f.Id, f.Name, e, power, false, f.Mission == Mission.Patrol ? "patrols it" : "posted here"));
                continue;
            }
            if (f.Role == FleetRole.None || RoleWhyNot(w, f, p) != null) continue;
            // Busy with an order: it would not answer now (one answering another call comes back to it).
            if (!HelpCalls.Free(w, f) && f.AnsweringCallId == null) continue;
            if (Trip(w, f, at, AnswerBurn) is not var (hours, _, _)) continue;
            list.Add(new Coverer(f.Id, f.Name, HearHours(w, f, at) + hours, power, false, HelpCalls.RoleName(f.Role)));
        }
        return list.OrderBy(c => c.Hours).ThenBy(c => c.FleetId).ToList();
    }

    /// <summary>Judge one place: covered, thin or open, and why.</summary>
    public static CoverJudgement Judge(World w, Side side, CoverPlace p, ISet<int>? without = null)
    {
        var (threat, note) = Threat(w, side, p);
        var guns = p.Kind == PlaceKind.Run ? 0 : Guns(w, p);
        var who = Who(w, side, p, without);
        var soon = who.Where(c => c.Hours <= CoverHours).ToList();
        var soonPower = soon.Sum(c => c.Power) + guns;
        var allPower = who.Sum(c => c.Power) + guns;
        string Names(IEnumerable<Coverer> cs) => string.Join(", ", cs.Take(3).Select(c => c.OnSpot ? $"{c.Name} ({c.How})" : $"{c.Name} ({Age(c.Hours)} away)"));
        var gunsText = guns > 0 ? "planet guns" : null;
        if (soonPower >= threat && soonPower > 0)
        {
            var parts = new List<string>();
            if (soon.Count > 0) parts.Add(Names(soon));
            if (gunsText != null) parts.Add(gunsText);
            return new(p, CoverState.Covered, $"{string.Join(", ", parts)}: {soonPower / threat:0.#} to 1 against {note}", who, threat, note, guns);
        }
        if (who.Count > 0 || guns > 0)
        {
            string why;
            if (allPower >= threat && who.Count > 0)
                why = $"help is slow: {Names(who)}; enough strength takes {Age(HoursToEnough(who, guns, threat))}";
            else
                why = $"too weak: {(who.Count > 0 ? Names(who) : "")}{(who.Count > 0 && gunsText != null ? " and " : "")}{gunsText ?? ""} make {allPower / threat:0.#} to 1 against {note}";
            return new(p, CoverState.Thin, why, who, threat, note, guns);
        }
        return new(p, CoverState.Open, OpenWhy(w, side, p, without), who, threat, note, guns);
    }

    private static double HoursToEnough(List<Coverer> who, double guns, double threat)
    {
        var sum = guns;
        foreach (var c in who.OrderBy(c => c.Hours))
        {
            sum += c.Power;
            if (sum >= threat) return c.Hours;
        }
        return who.Max(c => c.Hours);
    }

    /// <summary>Why nobody covers a place: the nearest fighting fleet and what holds it back.</summary>
    private static string OpenWhy(World w, Side side, CoverPlace p, ISet<int>? without)
    {
        var at = Where(w, p);
        var near = w.Fleets.Where(f => f.Side == side && Fighting(w, f) && (without == null || !without.Contains(f.Id)))
            .OrderBy(f => Vec2.Distance(f.Position, at)).ThenBy(f => f.Id).FirstOrDefault();
        if (near == null) return "nothing: you have no fighting fleet free";
        var why = near.Role == FleetRole.None ? "it has no role" : RoleWhyNot(w, near, p) ?? (HelpCalls.Free(w, near) ? "it can't get there" : "it is busy");
        return $"nothing: the nearest, {near.Name}, {Vec2.Distance(near.Position, at) / Au:0.##} AU away, would not come ({why})";
    }

    /// <summary>Every place judged.</summary>
    public static List<CoverJudgement> JudgeAll(World w, Side side, ISet<int>? without = null) =>
        Places(w, side).Select(p => Judge(w, side, p, without)).ToList();

    /// <summary>The places a fleet covers now (it is one of the fleets counted for them).</summary>
    public static List<CoverJudgement> CoverGiven(World w, Fleet f) =>
        JudgeAll(w, f.Side).Where(j => j.Who.Any(c => c.FleetId == f.Id)).ToList();

    /// <summary>Places that would be worse off without these fleets: (judgement now, judgement without them).</summary>
    public static List<(CoverJudgement Now, CoverJudgement Without)> Uncovered(World w, Side side, IReadOnlyCollection<int> fleetIds)
    {
        var without = fleetIds.ToHashSet();
        var list = new List<(CoverJudgement, CoverJudgement)>();
        foreach (var p in Places(w, side))
        {
            var now = Judge(w, side, p);
            if (!now.Who.Any(c => without.Contains(c.FleetId))) continue;
            var then = Judge(w, side, p, without);
            if (then.State > now.State) list.Add((now, then));
        }
        return list;
    }

    /// <summary>"Rust (open), Home to Verdant (thin)", or "nothing" when no place is worse off.</summary>
    public static string UncoveredText(World w, Side side, IReadOnlyCollection<int> fleetIds)
    {
        var worse = Uncovered(w, side, fleetIds);
        return worse.Count == 0 ? "nothing" : string.Join(", ", worse.Select(x => $"{x.Now.Place.Name} ({StateName(x.Without.State)})"));
    }

    /// <summary>
    /// The fleets that could cover a place, quickest first: every fighting fleet of the side, how long it would take
    /// at a steady burn, its strength, and why it can't (null: it can), with what its leaving would uncover.
    /// </summary>
    public static List<CoverOffer> Offers(World w, Side side, CoverPlace p)
    {
        var at = Where(w, p);
        var list = new List<CoverOffer>();
        foreach (var f in w.Fleets.Where(f => f.Side == side && Fighting(w, f)).OrderBy(f => f.Id))
        {
            if (OnSpot(w, f, p) || f.CoverKey == p.Key) continue;
            var trip = Trip(w, f, at, 0.6);
            string? problem = trip == null ? "too little fuel to get there" : f.Retreating ? "it is pulling back" : null;
            var leaves = UncoveredText(w, side, new[] { f.Id });
            list.Add(new CoverOffer(f.Id, f.Name, trip?.Hours, Stats.Power(w, f), problem, leaves));
        }
        return list.OrderBy(o => o.Problem != null).ThenBy(o => o.Hours ?? double.MaxValue).ThenBy(o => o.FleetId).ToList();
    }

    /// <summary>The fleet that best covers an open place: the quickest that can, preferring one whose leaving uncovers nothing.</summary>
    public static CoverOffer? Best(World w, Side side, CoverPlace p) =>
        Offers(w, side, p).Where(o => o.Problem == null).OrderBy(o => o.Leaves == "nothing" ? 0 : 1).ThenBy(o => o.Hours ?? double.MaxValue).ThenBy(o => o.FleetId).FirstOrDefault();

    /// <summary>
    /// Needs you: an open verdict on a planet or moons the side holds is an Advice item, with Cover it by the best
    /// fleet as the act (thin places are not listed).
    /// </summary>
    public static void Needs(World w, Side side, Action<string, string, int?, int?, GameCommand?, string?> need)
    {
        foreach (var j in JudgeAll(w, side).Where(j => j.State == CoverState.Open && j.Place.Kind != PlaceKind.Run))
        {
            var best = Best(w, side, j.Place);
            var text = $"{j.Place.Name} has nothing covering it: {j.Why}";
            need($"open:{j.Place.Key}", text, j.Place.MainBodyId, best?.FleetId,
                best == null ? null : new CoverPlaceCommand(best.FleetId, j.Place.Key),
                best == null ? null : $"Cover it with {best.Name}" + (best.Hours is double h ? $" ({Age(h)})" : ""));
        }
    }
}

// ---- Fleet operations: loops, cover orders, strikes ----------------------------------------------

/// <summary>
/// Fleet operations: patrol loops (stops with waits; a call for help in range wins and the fleet comes back to
/// the stop it left), Cover it orders, and strikes (several fleets, one arrival). Status lines for each, shown
/// against the fleet.
/// </summary>
public static class Operations
{
    /// <summary>At a point stop: this close (Mm) counts as there.</summary>
    public const double AtPointMm = 3000;

    /// <summary>Patrol loops fly between stops at this share of top speed.</summary>
    public const double LoopBurn = 0.5;

    /// <summary>A strike's "arrived" line stays on the fleet this long (hours).</summary>
    public const double ArrivedShowHours = 12;

    public const int MaxStops = 12;
    public const double MaxWaitHours = 30 * 24;

    // ---- Patrol loops --------------------------------------------------------------------------

    /// <summary>Where a loop stop is now (null when its body or run is gone).</summary>
    public static Vec2? StopAt(World w, LoopStop s)
    {
        if (s.BodyId is int b) return w.FindBody(b)?.Position;
        if (s.FromBodyId != null && s.ToBodyId != null) return Lanes.PointOn(w, s.FromBodyId, s.ToBodyId, s.T);
        return s.Point;
    }

    public static string StopName(World w, LoopStop s)
    {
        if (s.BodyId is int b) return $"{w.FindBody(b)?.Name ?? "?"} orbit";
        if (s.FromBodyId is int a && s.ToBodyId is int z)
            return $"{w.FindBody(a)?.Name ?? "?"}–{w.FindBody(z)?.Name ?? "?"} run, {(Math.Abs(s.T - 0.5) < 0.01 ? "halfway" : $"{s.T:P0} along")}";
        return "a point in space";
    }

    private static bool AtStop(World w, Fleet f, LoopStop s)
    {
        if (s.BodyId is int b) return w.FindBody(b) is Body body && Economy.InOrbitAt(f, body);
        return f.Flight == null && StopAt(w, s) is Vec2 at && Vec2.Distance(f.Position, at) <= AtPointMm;
    }

    private static bool FlyTo(Simulation sim, Fleet f, LoopStop s)
    {
        var w = sim.World;
        if (StopAt(w, s) is not Vec2 at) return false;
        FlightRequest Req(double burn) => s.BodyId is int b
            ? new FlightRequest(b, at, burn, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)
            : new FlightRequest(null, at, burn, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick);
        Do(sim, new PlanFlightCommand(f.Id, Req(LoopBurn)));
        if (f.Flight != null) return true;
        Do(sim, new PlanFlightCommand(f.Id, Req(0.3)));
        return f.Flight != null;
    }

    /// <summary>A loop's rounds (called each hour by Autonomy for a free patrolling fleet with a loop): go to the stop, wait there, move on.</summary>
    public static void LoopRounds(Simulation sim, Fleet f)
    {
        var w = sim.World;
        if (f.Loop.Count == 0) return;
        var leg = ((f.MissionLeg % f.Loop.Count) + f.Loop.Count) % f.Loop.Count;
        var stop = f.Loop[leg];
        if (!AtStop(w, f, stop))
        {
            f.MissionUntil = 0;
            if (FlyTo(sim, f, stop)) return;
            f.MissionLeg++; // can't get there from here: try the next one
            return;
        }
        if (f.MissionUntil <= 0) { f.MissionUntil = w.Hour + Math.Max(1, stop.WaitHours); return; }
        if (w.Hour < f.MissionUntil) return;
        f.MissionLeg++;
        f.MissionUntil = 0;
        if (f.Loop.Count > 1) FlyTo(sim, f, f.Loop[f.MissionLeg % f.Loop.Count]);
    }

    /// <summary>One lap: hours flying between stops (at the loop's burn) plus the waits, and the speed change it costs. Null if a stop is gone or it can't fly.</summary>
    public static (double Hours, double DeltaV)? Lap(World w, Fleet f, IReadOnlyList<LoopStop> stops)
    {
        if (stops.Count == 0) return null;
        var perf = FleetPerformance.Of(w, f);
        if (perf.MaxSpeed <= 0 || perf.Accel <= 0) return null;
        double hours = 0, dv = 0;
        for (var i = 0; i < stops.Count; i++)
        {
            if (StopAt(w, stops[i]) is not Vec2 a || StopAt(w, stops[(i + 1) % stops.Count]) is not Vec2 b) return null;
            hours += stops[i].WaitHours;
            if (stops.Count == 1) break;
            var d = Vec2.Distance(a, b);
            var v = LoopBurn * perf.MaxSpeed;
            if (v * v / perf.Accel >= d) { hours += 2 * Math.Sqrt(d / perf.Accel); dv += 2 * Math.Sqrt(d * perf.Accel); }
            else { hours += 2 * v / perf.Accel + (d - v * v / perf.Accel) / v; dv += 2 * v; }
        }
        return (hours, dv);
    }

    /// <summary>"one lap 11 d · fuel for 3 laps".</summary>
    public static string LapText(World w, Fleet f, IReadOnlyList<LoopStop> stops)
    {
        if (Lap(w, f, stops) is not var (hours, dv)) return "can't work out a lap";
        var left = FleetPerformance.Of(w, f).DeltaVLeft;
        var laps = dv <= 1e-6 ? "fuel is no limit" : $"fuel for {Math.Floor(left / dv):0} lap{(Math.Floor(left / dv) == 1 ? "" : "s")}";
        return $"one lap {Coverage.Age(hours)} · {laps}";
    }

    // ---- Reach ---------------------------------------------------------------------------------

    /// <summary>
    /// How far the fleet reaches on its fuel: one-way times to planets at full burn (or what it can afford), and
    /// how many full-burn trips (burn and brake) its fuel allows before a tanker is needed.
    /// </summary>
    public static (List<(Body Body, double Hours)> Trips, int Burns, double DeltaVLeft) Reach(World w, Fleet f, int count = 4)
    {
        var perf = FleetPerformance.Of(w, f);
        var trips = new List<(Body, double)>();
        foreach (var b in w.Bodies.Where(b => b.Kind == BodyKind.Planet && !(f.Hold == HoldStyle.Orbit && f.OrbitBodyId == b.Id && f.Flight == null))
                     .OrderBy(b => b.Owner == f.Side ? 0 : 1).ThenBy(b => Vec2.Distance(b.Position, f.Position)).ThenBy(b => b.Id).Take(count))
            if (Coverage.Trip(w, f, b.Position, 1.0) is var (h, _, _)) trips.Add((b, h));
        var burns = perf.MaxSpeed > 0 ? (int)Math.Floor(perf.DeltaVLeft / (2 * perf.MaxSpeed)) : 0;
        return (trips, burns, perf.DeltaVLeft);
    }

    // ---- Status lines --------------------------------------------------------------------------

    /// <summary>Where this fleet's operations stand: its strike, its Cover it order, its loop stop. Shown against the fleet.</summary>
    public static List<string> Status(World w, Fleet f)
    {
        var list = new List<string>();
        if (f.Strike is { } s)
        {
            if (s.ArrivedHour != null) list.Add($"{s.Name}: arrived at {s.Target}");
            else if (f.Flight is { } fl && !fl.Started && fl.DepartHour > w.Hour)
                list.Add($"{s.Name}: leaves in {Coverage.Age(fl.DepartHour - w.Hour)}, arrives {GameTime.Format(fl.ArriveHour)}");
            else if (f.Flight != null || f.Chase != null)
                list.Add($"{s.Name}: on its way to {s.Target}, arrives {GameTime.Format(f.Flight?.ArriveHour ?? s.ArriveHour)}");
            else list.Add($"{s.Name}: at {s.Target}");
        }
        if (f.CoverKey != null)
        {
            var going = f.Flight != null && !f.Retreating;
            list.Add(going ? $"Cover {f.CoverName}: on its way, arrives {GameTime.Format(f.Flight!.ArriveHour)}" : $"Covering {f.CoverName}");
        }
        var offLoop = f.AnsweringCallId != null && f.PostMission == Mission.Patrol;
        if (f.Loop.Count > 0 && (f.Mission == Mission.Patrol || offLoop))
        {
            var leg = (offLoop ? f.PostMissionLeg : f.MissionLeg) % f.Loop.Count;
            var stop = f.Loop[leg];
            var where = $"loop stop {leg + 1} of {f.Loop.Count} ({StopName(w, stop)})";
            if (offLoop) list.Add($"Off its loop to answer a call; comes back to {where}");
            else if (f.MissionUntil > w.Hour) list.Add($"Waiting at {where}, {Coverage.Age(f.MissionUntil - w.Hour)} more");
            else list.Add($"Heading to {where}");
        }
        return list;
    }

    /// <summary>Hourly: strikes that got there are marked arrived (and dropped a while later); Cover it orders that ended are dropped.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Strike != null || f.CoverKey != null).OrderBy(f => f.Id))
        {
            if (f.Strike is { } s)
            {
                if (s.ArrivedHour is double at) { if (w.Hour - at > ArrivedShowHours) f.Strike = null; }
                else if (f.Flight == null && f.Chase == null && !f.Maneuvering)
                {
                    s.ArrivedHour = w.Hour;
                    sim.Log.Info("operations", "Strike fleet arrived", new { fleetId = f.Id, fleet = f.Name, strike = s.Name, target = s.Target, planned = s.ArriveHour });
                }
                else if (f.Retreating)
                {
                    sim.Log.Info("operations", "Strike fleet pulled back", new { fleetId = f.Id, fleet = f.Name, strike = s.Name });
                    f.Strike = null;
                }
            }
            if (f.CoverKey != null && f.Mission is not (Mission.Hold or Mission.Patrol) && f.AnsweringCallId == null)
            {
                sim.Log.Info("operations", "Cover order over", new { fleetId = f.Id, fleet = f.Name, place = f.CoverKey });
                f.CoverKey = null;
                f.CoverName = null;
            }
        }
    }

    /// <summary>A direct move from command: the fleet is no longer on its strike or Cover it order.</summary>
    public static void Heard(Fleet f, GameCommand command)
    {
        if (command is PlanFlightCommand or StopFleetCommand or SetHoldCommand or GatherFleetsCommand or SendAheadCommand or SetMissionCommand
            or SetZoneFleetCommand or HuntCommand or SetPatrolLoopCommand)
        {
            f.Strike = null;
            f.CoverKey = null;
            f.CoverName = null;
            if (f.Mission != Mission.Patrol) f.Loop = new();
        }
    }

    internal static void Do(Simulation sim, GameCommand command) => (command with { Source = "auto" }).Apply(sim.World, sim);

    /// <summary>Take a fleet off whatever it was doing by itself (a call, a mission, a zone, a guard post) before an operation's order.</summary>
    internal static void Clear(Fleet f)
    {
        HelpCalls.Dropped(f);
        f.Mission = Mission.None;
        f.MissionBodyId = null;
        f.MissionFleetId = null;
        f.ZoneId = null;
        f.GuardPosting = false;
        f.Loop = new();
        f.Strike = null;
        f.CoverKey = null;
        f.CoverName = null;
    }
}

// ---- Strikes -------------------------------------------------------------------------------------

/// <summary>
/// The strike planner: several fleets, one arrival. Together: the fleets that would get there first ease their
/// burn so all arrive about when the slowest does; as soon as each can: every fleet at the plan's burn; in waves:
/// each wave the gap after the one before. Odds are a guess against what was last seen at the target. Read-only.
/// </summary>
public static class Strikes
{
    /// <summary>Enemies seen this close (AU) to a planet target count against the strike.</summary>
    public const double TargetAu = 0.3;

    public static string ArrivalName(StrikeArrival a) => a switch
    {
        StrikeArrival.Together => "Arrive together",
        StrikeArrival.AsSoonAsEach => "As soon as each can",
        _ => "In waves",
    };

    public static string TargetName(World w, Side side, int? bodyId, int? fleetId) =>
        bodyId is int b ? w.FindBody(b)?.Name ?? "?" : fleetId is int e ? HelpCalls.SightingOf(w, side, e)?.Name ?? "a lost contact" : "nothing";

    /// <summary>The flight order for one fleet at a burn: into orbit at a planet, or after an enemy fleet (gun range, missile range for missile ships).</summary>
    public static FlightRequest? Request(World w, Fleet f, int? bodyId, int? enemyId, double burn)
    {
        if (bodyId is int b && w.FindBody(b) is Body body)
            return new FlightRequest(b, body.Position, burn, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick);
        if (enemyId is int e && HelpCalls.SightingOf(w, f.Side, e) is Sighting s)
        {
            if (!Coverage.Fighting(w, f)) // supply ships follow to where it was, not into the fight
                return new FlightRequest(null, s.Guess(w.Hour), burn, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick);
            var rails = w.ShipsOf(f).Any(x => Fitting.Spec(x).Railguns > 0 && !x.Out(Module.Railguns));
            return new FlightRequest(null, s.Guess(w.Hour), burn, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick, TargetFleetId: e,
                Chase: rails ? ChaseMode.GunRange : ChaseMode.MissileRange);
        }
        return null;
    }

    /// <summary>Lowest burn the planner eases a fleet to before it holds the fleet back and leaves later instead.</summary>
    public const double MinEase = 0.3;

    /// <summary>
    /// The order that gets the fleet there closest to the hour wanted: first easing its burn (down to MinEase, by
    /// halving), then, if it would still be early, leaving later at that burn (corrected a few times, as targets move).
    /// </summary>
    private static (FlightRequest Req, double Arrive, double Burn)? EaseTo(World w, Fleet f, int? bodyId, int? enemyId, double burn, double wanted)
    {
        if (Request(w, f, bodyId, enemyId, burn) is not FlightRequest top) return null;
        var plan = FlightPlanner.Plan(w, f, top);
        if (plan.Flight == null) return null;
        if (plan.Flight.ArriveHour >= wanted - 0.5) return (top, plan.Flight.ArriveHour, burn);
        double lo = Math.Min(MinEase, burn), hi = burn, best = burn, bestArrive = plan.Flight.ArriveHour;
        var bestReq = top;
        for (var i = 0; i < 10 && Math.Abs(bestArrive - wanted) > 0.5; i++)
        {
            var mid = i == 0 ? lo : (lo + hi) / 2;
            if (Request(w, f, bodyId, enemyId, mid) is not FlightRequest r) break;
            var p = FlightPlanner.Plan(w, f, r);
            if (p.Flight == null) { lo = mid; continue; }
            if (Math.Abs(p.Flight.ArriveHour - wanted) < Math.Abs(bestArrive - wanted)) { best = mid; bestArrive = p.Flight.ArriveHour; bestReq = r; }
            if (i == 0 && p.Flight.ArriveHour < wanted) break; // even the slowest burn is early: leave later instead
            if (p.Flight.ArriveHour < wanted) hi = mid; else lo = mid;
        }
        // Still early at the slowest burn: hold it back and leave later.
        for (var i = 0; i < 4 && bestArrive < wanted - 0.5; i++)
        {
            var later = bestReq with { DepartTick = bestReq.DepartTick + Math.Max(1, (long)Math.Round(wanted - bestArrive)) };
            var p = FlightPlanner.Plan(w, f, later);
            if (p.Flight == null) break;
            bestReq = later;
            bestArrive = p.Flight.ArriveHour;
        }
        return (bestReq, bestArrive, best);
    }

    /// <summary>Work a strike out: each fleet's order and arrival, the odds (a guess), what leaving uncovers.</summary>
    public static StrikePreview Preview(World w, Side side, IReadOnlyList<int> fleetIds, int? bodyId, int? enemyId, StrikeArrival arrival, double gapHours, double burn)
    {
        var target = TargetName(w, side, bodyId, enemyId);
        var fleets = fleetIds.Distinct().Select(w.FindFleet).OfType<Fleet>().Where(f => f.Side == side && f.ShipIds.Count > 0).ToList();
        if (bodyId == null && enemyId == null) return new(target, new(), 0, 0, "", "nothing", null, "Pick a target");
        if (enemyId is int e && HelpCalls.SightingOf(w, side, e) == null) return new(target, new(), 0, 0, "", "nothing", null, "That enemy fleet is no longer on record");
        if (fleets.Count == 0) return new(target, new(), 0, 0, "", "nothing", null, "Tick the fleets to send");

        // Each on its own at the plan's burn.
        var solo = new List<(Fleet F, FlightRequest? Req, double Arrive, string? Problem)>();
        foreach (var f in fleets)
        {
            var req = Request(w, f, bodyId, enemyId, burn);
            var plan = req == null ? null : FlightPlanner.Plan(w, f, req);
            solo.Add((f, req, plan?.Flight?.ArriveHour ?? 0, plan == null ? "can't aim at that" : plan.Flight == null ? plan.Problem ?? "can't get there" : null));
        }
        var ok = solo.Where(s => s.Problem == null).ToList();
        var legs = new List<StrikeLeg>();
        var wanted = new Dictionary<int, double>();
        if (ok.Count > 0)
        {
            switch (arrival)
            {
                case StrikeArrival.Together:
                {
                    // Supply ships are not held to the warships' hour: they follow as soon as they can.
                    var war = ok.Where(s => Coverage.Fighting(w, s.F)).ToList();
                    var late = (war.Count > 0 ? war : ok).Max(s => s.Arrive);
                    foreach (var s in ok) wanted[s.F.Id] = Coverage.Fighting(w, s.F) || war.Count == 0 ? late : s.Arrive;
                    break;
                }
                case StrikeArrival.Waves:
                {
                    var prev = double.NegativeInfinity;
                    foreach (var s in ok.OrderBy(s => s.Arrive).ThenBy(s => s.F.Id))
                    {
                        var at = Math.Max(s.Arrive, prev + Math.Max(0, gapHours));
                        wanted[s.F.Id] = at;
                        prev = at;
                    }
                    break;
                }
                default:
                    foreach (var s in ok) wanted[s.F.Id] = s.Arrive;
                    break;
            }
        }
        foreach (var s in solo)
        {
            var supply = !Coverage.Fighting(w, s.F);
            if (s.Problem != null) { legs.Add(new StrikeLeg(s.F.Id, s.F.Name, null, burn, 0, s.Problem, supply)); continue; }
            var want = wanted[s.F.Id];
            if (want <= s.Arrive + 0.5) { legs.Add(new StrikeLeg(s.F.Id, s.F.Name, s.Req, burn, s.Arrive, null, supply)); continue; }
            var eased = EaseTo(w, s.F, bodyId, enemyId, burn, want);
            legs.Add(eased is var (r, at, b) ? new StrikeLeg(s.F.Id, s.F.Name, r, b, at, null, supply) : new StrikeLeg(s.F.Id, s.F.Name, s.Req, burn, s.Arrive, null, supply));
        }

        var going = legs.Where(l => l.Problem == null).ToList();
        var power = going.Sum(l => w.FindFleet(l.FleetId) is Fleet f ? Stats.Power(w, f) : 0);
        var (enemy, note) = EnemyAt(w, side, bodyId, enemyId);
        var uncovers = Coverage.UncoveredText(w, side, going.Select(l => l.FleetId).ToList());
        if (uncovers != "nothing" && going.Count > 0)
        {
            var away = (going.Max(l => l.ArriveHour) - w.Hour) * 2;
            uncovers += $" for about {Coverage.Age(away)}";
        }
        string? supplyNote = null;
        var supplyLegs = going.Where(l => l.Supply).ToList();
        var warLegs = going.Where(l => !l.Supply).ToList();
        if (supplyLegs.Count > 0 && warLegs.Count > 0)
        {
            var gap = supplyLegs.Max(l => l.ArriveHour) - warLegs.Min(l => l.ArriveHour);
            var food = warLegs.Select(l => w.FindFleet(l.FleetId)).OfType<Fleet>().Select(f => SupplyChain.FoodDays(w, f)).OfType<double>().DefaultIfEmpty(double.NaN).Min();
            supplyNote = (gap > 1 ? $"Supply arrives {Coverage.Age(gap)} after the first warships" : "Supply arrives with the warships")
                         + (double.IsNaN(food) ? "" : $": they have {food:0.#} days of food");
        }
        return new(target, legs, power, enemy, note, uncovers, supplyNote, going.Count == 0 ? "None of these fleets can get there" : null);
    }

    /// <summary>What was last seen at the target, as a strength and a line saying it is a guess and how old.</summary>
    public static (double Power, string Note) EnemyAt(World w, Side side, int? bodyId, int? enemyId)
    {
        if (enemyId is int e)
        {
            if (HelpCalls.SightingOf(w, side, e) is not Sighting s) return (0, "the enemy fleet is no longer on record");
            return (HelpCalls.EnemyPower(s), $"a guess from {s.Name} ({s.Ships} ships) as seen {Coverage.Age(w.Hour - s.Hour)} ago");
        }
        if (bodyId is not int b || w.FindBody(b) is not Body body) return (0, "");
        var seen = w.Sightings.Where(s => s.SeenBy == side && !s.Own && !Lanes.CargoSighting(s) && w.Hour - s.Hour <= Coverage.ThreatDays * 24
                                          && Vec2.Distance(s.Position, body.Position) <= TargetAu * Physics.MmPerAu)
            .OrderBy(s => s.FleetId).ToList();
        var power = seen.Sum(HelpCalls.EnemyPower);
        var fort = body.Owner is Side o && o != side && Starbases.Fort(w, body) != null;
        if (fort) power += Coverage.FortShips * GameData.Ai.EnemyShipValue;
        if (seen.Count == 0 && !fort) return (0, $"nothing seen at {body.Name} in the last {Coverage.ThreatDays:0} days (a guess)");
        var bits = seen.Select(s => $"{s.Name} ({s.Ships}, {Coverage.Age(w.Hour - s.Hour)} ago)").ToList();
        if (fort) bits.Add("its station's guns");
        return (power, $"a guess from what was last seen at {body.Name}: {string.Join(", ", bits)}");
    }
}

// ---- Commands -----------------------------------------------------------------------------------

/// <summary>Give a fleet a patrol loop: ordered stops with a wait at each (empty: the loop ends). It starts at the first stop.</summary>
public sealed record SetPatrolLoopCommand(int FleetId, List<LoopStop> Stops) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Patrol loop") is not Fleet fleet) return;
        var stops = Stops.Take(Operations.MaxStops).Select(s => s.Copy()).ToList();
        foreach (var s in stops)
        {
            s.WaitHours = Math.Clamp(double.IsFinite(s.WaitHours) ? s.WaitHours : 24, 0, Operations.MaxWaitHours);
            s.T = Math.Clamp(double.IsFinite(s.T) ? s.T : 0.5, 0, 1);
        }
        if (stops.Any(s => Operations.StopAt(world, s) is not Vec2 at || !at.IsFinite))
        {
            Refuse(world, sim, fleet, "a stop on that loop no longer exists", new { FleetId, Source });
            return;
        }
        if (stops.Count > 0 && fleet.ShipIds.Count == 0)
        {
            Refuse(world, sim, fleet, "it has no ships", new { FleetId, Source });
            return;
        }
        Operations.Clear(fleet);
        if (stops.Count == 0)
        {
            sim.Log.Info("operations", "Patrol loop ended", new { FleetId, fleet = fleet.Name, Source });
            return;
        }
        fleet.Loop = stops;
        fleet.Mission = Mission.Patrol;
        fleet.MissionBodyId = stops.FirstOrDefault(s => s.BodyId != null)?.BodyId;
        fleet.MissionLeg = 0;
        fleet.MissionUntil = 0;
        if (fleet.Flight is { Started: false }) fleet.Flight = null;
        fleet.Chase = null;
        sim.Log.Info("operations", "Patrol loop set", new { FleetId, fleet = fleet.Name, stops = stops.Count, waits = stops.Select(s => s.WaitHours).ToList(), Source });
        Autonomy.Start(sim, fleet);
    }
}

/// <summary>
/// Cover it: send a fleet to cover a place. A planet or moons: it holds orbit there (no role yet, or a forward
/// guard: it takes Planet defence, so it also answers calls nearby). A cargo run: a patrol loop of both ends and
/// the middle (no role: Anti-piracy; Planet defence: Support, which answers raids too).
/// </summary>
public sealed record CoverPlaceCommand(int FleetId, string PlaceKey) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Cover it") is not Fleet fleet) return;
        if (Coverage.Find(world, fleet.Side, PlaceKey) is not CoverPlace place)
        {
            Refuse(world, sim, fleet, "that place is no longer yours to cover", new { FleetId, PlaceKey, Source });
            return;
        }
        if (!Coverage.Fighting(world, fleet))
        {
            Refuse(world, sim, fleet, "it has no working weapons to cover anything with", new { FleetId, PlaceKey, Source });
            return;
        }
        var role = fleet.Role;
        if (place.Kind == PlaceKind.Run)
        {
            var stops = new List<LoopStop>
            {
                new() { BodyId = place.FromBodyId, WaitHours = 12 },
                new() { FromBodyId = place.FromBodyId, ToBodyId = place.ToBodyId, T = 0.5, WaitHours = 24 },
                new() { BodyId = place.ToBodyId, WaitHours = 12 },
                new() { FromBodyId = place.FromBodyId, ToBodyId = place.ToBodyId, T = 0.5, WaitHours = 24 },
            };
            new SetPatrolLoopCommand(FleetId, stops) { Source = "auto" }.Apply(world, sim);
            if (fleet.Mission != Mission.Patrol) return;
            if (role is FleetRole.None or FleetRole.ForwardGuard) role = FleetRole.AntiPiracy;
            else if (role == FleetRole.Defence) role = FleetRole.Support;
        }
        else
        {
            var bodyId = place.BodyIds[0];
            Operations.Clear(fleet);
            if (fleet.Flight is { Started: false }) fleet.Flight = null;
            fleet.Chase = null;
            new SetMissionCommand(FleetId, Mission.Hold, bodyId) { Source = "auto" }.Apply(world, sim);
            if (role is FleetRole.None or FleetRole.ForwardGuard) role = FleetRole.Defence;
        }
        if (role != fleet.Role) new SetFleetRoleCommand(FleetId, role) { Source = "auto" }.Apply(world, sim);
        fleet.CoverKey = place.Key;
        fleet.CoverName = place.Name;
        sim.Log.Info("operations", "Cover it", new { FleetId, fleet = fleet.Name, place = place.Key, name = place.Name, role = fleet.Role.ToString(), Source });
    }
}

/// <summary>One fleet's part in a strike: fly this order (worked out by the planner, so the fleets arrive as planned) and show the strike against it.</summary>
public sealed record StrikeLegCommand(int FleetId, FlightRequest Request, string Name, string Target, double ArriveHour, int? PlanId = null) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Strike") is not Fleet fleet) return;
        Operations.Clear(fleet);
        if (fleet.Flight is { Started: false }) fleet.Flight = null;
        // Light lag: an order that arrives late leaves late; keep the burn it was planned with.
        var req = Request with { DepartTick = Math.Max(Request.DepartTick, world.Tick) };
        Operations.Do(sim, new PlanFlightCommand(FleetId, req));
        if (fleet.Flight == null && fleet.Chase == null) return; // refused: the reason shows against the fleet
        fleet.Strike = new StrikeOrder { PlanId = PlanId, Name = Name, Target = Target, SentHour = world.Hour, ArriveHour = fleet.Flight?.ArriveHour ?? ArriveHour };
        sim.Log.Info("operations", "Strike leg sent", new { FleetId, fleet = fleet.Name, strike = Name, Target, planned = ArriveHour, arrive = fleet.Strike.ArriveHour, PlanId, Source });
    }
}

/// <summary>Save (or replace) a strike plan to send later; Hold until I say saves without sending.</summary>
public sealed record SaveStrikePlanCommand(Side Side, string Name, List<int> FleetIds, int? TargetBodyId, int? TargetFleetId, StrikeArrival Arrival,
    double WaveGapHours, double Burn, int? ReplaceId = null) : GameCommand
{
    public const int MaxPlans = 20;

    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var plan = ReplaceId is int id ? world.StrikePlans.Find(p => p.Id == id && p.Side == Side) : null;
        if (plan == null)
        {
            if (world.StrikePlans.Count(p => p.Side == Side) >= MaxPlans)
            {
                sim.Log.Warn("operations", "Too many saved strike plans", new { side = Side.ToString(), Source });
                return;
            }
            plan = new StrikePlan { Id = world.NextId++, Side = Side };
            world.StrikePlans.Add(plan);
        }
        plan.Name = string.IsNullOrWhiteSpace(Name) ? $"Strike on {Strikes.TargetName(world, Side, TargetBodyId, TargetFleetId)}" : Name.Trim()[..Math.Min(40, Name.Trim().Length)];
        plan.FleetIds = FleetIds.Distinct().Where(f => world.FindFleet(f)?.Side == Side).ToList();
        plan.TargetBodyId = TargetBodyId;
        plan.TargetFleetId = TargetBodyId == null ? TargetFleetId : null;
        plan.Arrival = Arrival;
        plan.WaveGapHours = Math.Clamp(double.IsFinite(WaveGapHours) ? WaveGapHours : 12, 0, 10 * 24);
        plan.Burn = Math.Clamp(double.IsFinite(Burn) ? Burn : 0.8, FlightPlanner.MinSpeedFraction, 1);
        plan.SavedHour = world.Hour;
        sim.Log.Info("operations", "Strike plan saved", new { side = Side.ToString(), plan.Id, plan.Name, fleets = plan.FleetIds, TargetBodyId, TargetFleetId, Arrival = Arrival.ToString(), Source });
    }
}

/// <summary>Forget a saved strike plan.</summary>
public sealed record DeleteStrikePlanCommand(Side Side, int PlanId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var n = world.StrikePlans.RemoveAll(p => p.Id == PlanId && p.Side == Side);
        if (n > 0) sim.Log.Info("operations", "Strike plan deleted", new { side = Side.ToString(), PlanId, Source });
    }
}

/// <summary>A saved plan was sent (counts the sends; the fleets' own orders are StrikeLegCommands).</summary>
public sealed record MarkStrikeSentCommand(Side Side, int PlanId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.StrikePlans.Find(p => p.Id == PlanId && p.Side == Side) is not StrikePlan plan) return;
        plan.TimesSent++;
        sim.Log.Info("operations", "Strike plan sent", new { side = Side.ToString(), PlanId, plan.Name, plan.TimesSent, Source });
    }
}
