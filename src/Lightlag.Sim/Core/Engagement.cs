namespace Lightlag.Sim.Core;

/// <summary>A stretch of time, in hours from now (To can be infinity: it does not end within the look-ahead).</summary>
public sealed record TimeWindow(double From, double To);

/// <summary>One way to handle an engagement, worked out now: what it does, or why it cannot be done.</summary>
public sealed record ApproachOption(string Key, string Label, string Summary, string? Problem, FlightRequest? Request, bool Current);

/// <summary>Missiles flying at one side's fleet, grouped by when they should arrive.</summary>
public sealed record IncomingSalvo(int Count, double ImpactHours, bool Ours);

/// <summary>
/// One of your fleets and one enemy fleet: how they are moving relative to each other, when each side's
/// weapons can reach, missiles in flight, and the ways to play it. Worked out from what your side
/// knows (the enemy's last seen track), never from where it really is, and for the screen only:
/// nothing here changes the world.
/// </summary>
public sealed class EngagementInfo
{
    public required Fleet Mine { get; init; }
    public required int EnemyId { get; init; }
    public required string EnemyName { get; init; }
    public int EnemyShips { get; init; }
    public bool InSight { get; init; }
    public double SeenHoursAgo { get; init; }
    public Vec2 EnemyAt { get; init; }
    public Vec2 EnemyVelocity { get; init; }
    public double Gap { get; init; }

    /// <summary>How fast the gap is shrinking (negative: growing).</summary>
    public double Closing { get; init; }
    public double ClosestInHours { get; init; }
    public double ClosestGap { get; init; }

    /// <summary>Hours until they come within contact range (battle time) on these tracks, 0 if they are, null if not within two days.</summary>
    public double? ContactInHours { get; init; }
    public double LookAheadHours { get; init; }
    public double OurTopSpeed { get; init; }
    public bool WeHaveMissiles { get; init; }
    public bool WeHaveRailguns { get; init; }

    /// <summary>"1 Gun frigate, 1 Missile destroyer" once seen close enough to tell, else null.</summary>
    public string? EnemyClasses { get; init; }
    public double ClassesSeenHoursAgo { get; init; }

    /// <summary>Whether they carry missiles and railguns, from a close look (null: not known yet).</summary>
    public bool? TheyHaveMissiles { get; init; }
    public bool? TheyHaveRailguns { get; init; }

    /// <summary>How far this fleet's own sensors pick the enemy up when it coasts cold, has warm engines, or burns.</summary>
    public double SpotCold { get; init; }
    public double SpotWarm { get; init; }
    public double SpotBurn { get; init; }

    /// <summary>Spotting ranges above include the help from knowing their track (Cued); this is the cold range without it.</summary>
    public double SpotColdUncued { get; init; }
    public bool Cued { get; init; }

    /// <summary>When the enemy, coasting as predicted, comes within cold spotting range of this fleet (null: not in the look-ahead).</summary>
    public TimeWindow? SpotWindow { get; init; }

    /// <summary>What your fleet is doing now on their sensors, and how far their ships would see it (more with sensor research you can't know about).</summary>
    public SignatureState OurState { get; init; }
    public double TheySeeUsAt { get; init; }

    public TimeWindow? OurRailguns { get; init; }
    public TimeWindow? OurMissiles { get; init; }
    public TimeWindow? TheirRailguns { get; init; }
    public TimeWindow? TheirMissiles { get; init; }
    public List<IncomingSalvo> Salvos { get; init; } = new();
    public List<ApproachOption> Options { get; init; } = new();

    /// <summary>How hard the approach choices burn, as a share of the fleet's top speed.</summary>
    public double Burn { get; init; } = 1;
}

public static class Engagement
{
    private static CombatData C => GameData.Combat;

    /// <summary>The enemy fleet your fleet is most concerned with: in a battle with it, else the nearest one seen.</summary>
    public static int? EnemyFor(World w, Fleet mine)
    {
        if (mine.Chase is { } c && Sensors.Find(w, mine.Side, c.TargetFleetId, seat: true) != null) return c.TargetFleetId;
        int? best = null;
        var bestD = double.MaxValue;
        foreach (var s in Sensors.Known(w, seat: true))
        {
            if (s.SeenBy != mine.Side || s.Own) continue;
            var d = Vec2.Distance(s.Guess(w.Hour), mine.Position);
            if (!s.Visible) d *= 3; // prefer what is in sight
            if (d < bestD) { bestD = d; best = s.FleetId; }
        }
        return best;
    }

    /// <summary>
    /// One engagement you could look at: your fleet, an enemy fleet, the gap between them now, and the
    /// hours until they come within contact range if both keep going as they are (0: in contact now;
    /// null: not closing to contact).
    /// </summary>
    public sealed record Pair(int Mine, int Enemy, string MineName, string EnemyName, double Gap, bool InSight, double? ContactHours = null);

    /// <summary>Hours until two straight-line tracks come within this range (0 if already), or null if they never do.</summary>
    public static double? HoursUntilWithin(Vec2 gap, Vec2 closingVelocity, double range)
    {
        // |gap + v t| = range: a t^2 + b t + c = 0, wanting the first t >= 0.
        var c = Vec2.Dot(gap, gap) - range * range;
        if (c <= 0) return 0;
        var a = Vec2.Dot(closingVelocity, closingVelocity);
        var b = 2 * Vec2.Dot(gap, closingVelocity);
        if (a < 1e-12 || b >= 0) return null;
        var disc = b * b - 4 * a * c;
        if (disc < 0) return null;
        return (-b - Math.Sqrt(disc)) / (2 * a);
    }

    /// <summary>
    /// Every engagement worth a look for one side, nearest first: each of its fleets against each enemy
    /// fleet it knows of that is within a few contact ranges, heading for it, or being chased by it.
    /// Fleets with nothing near are left out.
    /// </summary>
    public static List<Pair> All(World w, Side side)
    {
        var list = new List<Pair>();
        var near = C.ContactRangeMm * 3;
        foreach (var mine in w.Fleets)
        {
            if (mine.Side != side || !mine.Position.IsFinite || !w.ShipsOf(mine).Any()) continue;
            foreach (var s in Sensors.Known(w, seat: true))
            {
                if (s.SeenBy != side || s.Own) continue;
                var gap = Vec2.Distance(s.Guess(w.Hour), mine.Position);
                var chased = mine.Chase?.TargetFleetId == s.FleetId;
                if (gap > near && !chased && Sensors.HeadingFor(s, mine, w.Hour) == null) continue;
                var contact = HoursUntilWithin(s.Guess(w.Hour) - mine.Position, s.Velocity - mine.Velocity, C.ContactRangeMm);
                list.Add(new Pair(mine.Id, s.FleetId, mine.Name, s.Name, gap, s.Visible, contact));
            }
        }
        return list.OrderBy(p => p.Gap).ThenBy(p => p.Mine).ThenBy(p => p.Enemy).ToList();
    }

    /// <summary>Two fleets timed to reach one enemy together: each one's order and when they both get there, or why not.</summary>
    public sealed record TogetherPlan(FlightRequest? First, FlightRequest? Second, double ArriveHour, string? Problem);

    /// <summary>
    /// "Arrive together": both fleets close on the enemy (gun range if they have working railguns, else missile range)
    /// and the one that would get there first eases its burn so both arrive at about the same hour. Read-only.
    /// </summary>
    public static TogetherPlan Together(World w, Fleet first, Fleet second, int enemyId, double burn = 1.0)
    {
        if (Sensors.Find(w, first.Side, enemyId, seat: true) is not Sighting s) return new(null, null, 0, "That fleet is no longer on record");
        FlightRequest Req(Fleet f, double speed)
        {
            var rails = w.ShipsOf(f).Any(x => Fitting.Spec(x).Railguns > 0 && !x.Out(Module.Railguns));
            return new FlightRequest(null, s.Guess(w.Hour), speed, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick, TargetFleetId: enemyId,
                Chase: rails ? ChaseMode.GunRange : ChaseMode.MissileRange);
        }
        var a = FlightPlanner.Plan(w, first, Req(first, burn));
        var b = FlightPlanner.Plan(w, second, Req(second, burn));
        if (a.Flight == null) return new(null, null, 0, $"{first.Name}: {a.Problem ?? "can't get there"}");
        if (b.Flight == null) return new(null, null, 0, $"{second.Name}: {b.Problem ?? "can't get there"}");
        var late = Math.Max(a.Flight.ArriveHour, b.Flight.ArriveHour);
        var early = a.Flight.ArriveHour <= b.Flight.ArriveHour ? first : second;
        // Ease the early fleet's burn until it arrives with the other (slower arrives later); halving the range each try.
        double lo = 0.1, hi = burn, best = burn, bestArrive = Math.Min(a.Flight.ArriveHour, b.Flight.ArriveHour);
        for (var i = 0; i < 12 && Math.Abs(bestArrive - late) > 0.1; i++)
        {
            var mid = (lo + hi) / 2;
            var p = FlightPlanner.Plan(w, early, Req(early, mid));
            if (p.Flight == null) { lo = mid; continue; }
            if (Math.Abs(p.Flight.ArriveHour - late) < Math.Abs(bestArrive - late)) { best = mid; bestArrive = p.Flight.ArriveHour; }
            if (p.Flight.ArriveHour < late) hi = mid; else lo = mid;
        }
        var eased = Req(early, best);
        return early == first ? new(eased, Req(second, burn), late, null) : new(Req(first, burn), eased, late, null);
    }

    /// <param name="burn">How hard to burn for the approach choices: a share of the fleet's top speed (0.1 to 1).</param>
    public static EngagementInfo? Of(World w, Fleet mine, int enemyId, double burn = 1.0)
    {
        var s = Sensors.Find(w, mine.Side, enemyId, seat: true);
        if (s == null || !mine.Position.IsFinite) return null;
        var now = w.Hour;
        Vec2 Them(double t) => Sensors.Predict(w, mine.Side, enemyId, now + t, seat: true) ?? s.Guess(now + t);
        Vec2 Us(double t) => Combat.PredictFleet(w, mine, now + t);
        double GapAt(double t) => Vec2.Distance(Us(t), Them(t));

        var ships = w.ShipsOf(mine).ToList();
        var weMissiles = ships.Any(x => x.Missiles > 0 && !x.Out(Module.Missiles));
        var weRails = ships.Any(x => Fitting.Spec(x).Railguns > 0 && !x.Out(Module.Railguns));

        // Closest approach in the next two days, then a look-ahead that shows it with room either side.
        var gap0 = GapAt(0);
        double closestT = 0, closest = gap0;
        for (var i = 1; i <= 960; i++)
        {
            var t = i * 0.05;
            var g = GapAt(t);
            if (g < closest) { closest = g; closestT = t; }
        }
        // When the gap barely changes (matching speed at the end of a chase, say), take the first time it
        // gets that close, so the closest pass does not jump between nearly equal moments as time runs.
        var nearEnough = closest + Math.Max(0.05, closest * 0.02);
        for (var i = 0; i <= 960; i++)
        {
            var t = i * 0.05;
            if (t >= closestT) break;
            var g = GapAt(t);
            if (g <= nearEnough) { closest = g; closestT = t; break; }
        }
        for (var i = -20; i <= 20; i++) // finer around it
        {
            var t = Math.Max(0, closestT + i * 0.0025);
            var g = GapAt(t);
            if (g < closest) { closest = g; closestT = t; }
        }
        double? contactT = null;
        for (var i = 0; i <= 960 && contactT == null; i++)
            if (GapAt(i * 0.05) <= GameData.Combat.ContactRangeMm) contactT = i * 0.05;
        // Rounded up to a fixed step so the timeline's scale holds still while time runs.
        var want = Math.Clamp(Math.Max(closestT * 1.6, closestT + 0.25), 0.25, 48);
        var ahead = new[] { 0.25, 0.5, 1, 2, 3, 6, 12, 24, 48 }.First(x => x >= want - 1e-9);

        TimeWindow? Window(double range)
        {
            const int n = 480;
            double? from = null, to = null;
            for (var i = 0; i <= n; i++)
            {
                var t = ahead * i / n;
                if (GapAt(t) <= range)
                {
                    from ??= t;
                    to = t;
                }
                else if (from != null) break;
            }
            if (from == null) return null;
            return new TimeWindow(from.Value, to >= ahead - 1e-9 ? double.PositiveInfinity : to!.Value);
        }

        var rel = s.Velocity - mine.Velocity;
        var gapVec = s.Guess(now) - mine.Position;
        var closing = gapVec.Length > 1e-9 ? -Vec2.Dot(gapVec, rel) / gapVec.Length : 0;
        var perf = FleetPerformance.Of(w, mine);

        // Spotting: this fleet's sensors against the enemy's size, and theirs against ours. Worked out from
        // the classes seen on a close look; before that, from the ship count, assuming the smallest
        // ships (the hardest to spot) carrying the best sensors.
        var classes = s.Classes.Select(GameData.ShipClass).ToList();
        var known = classes.Count > 0;
        var extra = 1 + GameData.Sensors.ExtraShip * Math.Max(0, s.Ships - 1);
        var theirSize = (known ? classes.Max(k => k.SignatureSize) : GameData.ShipClasses.Values.Min(k => k.SignatureSize)) * extra;
        // Their sensor research is unknown, so this is a floor on how far they see you.
        var theirSensor = known ? classes.Max(k => k.SensorMm) : GameData.ShipClasses.Values.Max(k => k.SensorMm);
        var ourSensor = Sensors.SensorOf(w, mine);
        // A known track (in sight now, or seen before): the sensors know where to look, as long as they keep course.
        var cue = Math.Max(1, GameData.Sensors.CuedFactor);
        double Spot(SignatureState st) => Sensors.Range(ourSensor, theirSize * Sensors.StateFactor(st)) * cue;
        var spotCold = Spot(SignatureState.Cold);
        bool? theyMissiles = known ? classes.Any(k => k.Missiles > 0) : null;
        bool? theyRails = known ? classes.Any(k => k.Railguns > 0) : null;

        var info = new EngagementInfo
        {
            Mine = mine,
            EnemyId = enemyId,
            EnemyName = s.Name,
            EnemyShips = s.Ships,
            InSight = s.Visible,
            SeenHoursAgo = now - s.Hour,
            EnemyAt = Them(0),
            EnemyVelocity = s.Velocity,
            Gap = gap0,
            Closing = closing,
            ClosestInHours = closestT,
            ClosestGap = closest,
            ContactInHours = contactT,
            LookAheadHours = ahead,
            OurTopSpeed = perf.MaxSpeed,
            WeHaveMissiles = weMissiles,
            WeHaveRailguns = weRails,
            SpotCold = spotCold,
            SpotColdUncued = spotCold / cue,
            Cued = cue > 1,
            SpotWarm = Spot(SignatureState.Warm),
            SpotBurn = Spot(SignatureState.Burn),
            SpotWindow = s.Visible ? null : Window(spotCold),
            OurState = Sensors.StateOf(mine),
            TheySeeUsAt = Sensors.Range(theirSensor, Sensors.Signature(w, mine)),
            OurRailguns = weRails ? Window(C.Railgun.RangeMm) : null,
            OurMissiles = weMissiles ? Window(C.Missile.LaunchRangeMm) : null,
            TheirRailguns = theyRails == false ? null : Window(C.Railgun.RangeMm),
            TheirMissiles = theyMissiles == false ? null : Window(C.Missile.LaunchRangeMm),
            EnemyClasses = Sensors.Describe(s),
            ClassesSeenHoursAgo = known ? now - s.ClassesHour : 0,
            TheyHaveMissiles = theyMissiles,
            TheyHaveRailguns = theyRails,
            Salvos = Salvos(w, mine, enemyId),
            Options = Options(w, mine, enemyId, s, perf, Math.Clamp(burn, 0.1, 1.0)),
            Burn = Math.Clamp(burn, 0.1, 1.0),
        };
        return info;
    }

    /// <summary>Missiles flying at this fleet (theirs) and at the enemy fleet (ours), with rough times to impact.</summary>
    private static List<IncomingSalvo> Salvos(World w, Fleet mine, int enemyId)
    {
        var list = new List<(int Bucket, bool Ours, double T)>();
        var enemy = w.FindFleet(enemyId);
        foreach (var m in w.Missiles)
        {
            var target = w.FindShip(m.TargetShipId);
            if (target == null) continue;
            var targetFleet = w.FleetOf(target);
            var ours = m.Side == mine.Side;
            if (ours ? targetFleet != enemy : targetFleet != mine) continue;
            var gap = target.Position - m.Position;
            var d = gap.Length;
            var v = d > 1e-9 ? -Vec2.Dot(gap, targetFleet!.Velocity - m.Velocity) / d : 0;
            var a = C.Missile.AccelMmPerHour2;
            var t = (-v + Math.Sqrt(Math.Max(0, v * v + 2 * a * d))) / a;
            list.Add(((int)(t * 3600 / 10), ours, t)); // grouped in 10-second buckets
        }
        return list.GroupBy(x => (x.Bucket, x.Ours))
            .Select(g => new IncomingSalvo(g.Count(), g.Min(x => x.T), g.Key.Ours))
            .OrderBy(x => x.ImpactHours).ToList();
    }

    private static List<ApproachOption> Options(World w, Fleet mine, int enemyId, Sighting s, FleetPerformance perf, double burn)
    {
        var list = new List<ApproachOption>();
        var enemy = w.FindFleet(enemyId);
        string Fuel(FlightPlan p) => $"uses {p.FuelShare:P0} of its fuel";
        string When(double hours) => hours < 1 / 60.0 ? $"{hours * 3600:0} s" : hours < 2 ? $"{hours * 60:0} min" : $"{hours:0.#} h";
        var theirSpeed = s.Velocity.Length;

        void Chase(string key, string label, ChaseMode mode, double speed, Func<FlightPlan, string> summary, string? noGo = null)
        {
            var req = new FlightRequest(null, s.Guess(w.Hour), speed, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick, TargetFleetId: enemyId, Chase: mode);
            var current = mine.Chase is { } c && c.TargetFleetId == enemyId && c.Mode == mode;
            if (noGo != null || enemy == null)
            {
                list.Add(new ApproachOption(key, label, "", noGo ?? "That fleet is gone", null, current));
                return;
            }
            var plan = FlightPlanner.Plan(w, mine, req);
            list.Add(plan is { Ok: true, Flight: Flight f }
                ? new ApproachOption(key, label, summary(plan) + (current && Chases.PullingAway(w, mine) is string away ? $" NOW: {away}." : ""), null, req, current)
                : new ApproachOption(key, label, "", plan.Problem ?? "Cannot do that", null, current));
        }

        double Peak(Flight f) => f.Legs.Count == 0 ? 0 : f.Legs.Max(l => Math.Max(l.V0.Length, l.VelocityAt(l.End).Length));
        var ships = w.ShipsOf(mine).ToList();
        var hasRails = ships.Any(x => Fitting.Spec(x).Railguns > 0 && !x.Out(Module.Railguns));
        var hasMissiles = ships.Any(x => x.Missiles > 0 && !x.Out(Module.Missiles));

        // Say which ship holds the burn back (a slow tender or freighter drags the whole fleet).
        var held = FleetPerformance.HeldBackBy(w, mine) is { } hb
            ? $" Burn held to {hb.Ship}'s {hb.Accel / 3.6:0.#} km/s each hour (the others could do {hb.Others / 3.6:0.#}): split it off to go faster."
            : "";
        Chase("engage", "Burn and engage", ChaseMode.GunRange, burn,
            p => $"Burn at {burn:P0} thrust, peaking at {FlightPlanner.Kms(Peak(p.Flight!))}, then brake to {C.EngageRangeMm * 0.75 * 1000:#,0} km and keep pace: there in {When(p.Flight!.ArriveHour - w.Hour)}, {Fuel(p)}.{held}",
            hasRails ? null : "No working railguns in this fleet");
        Chase("missiles", "Hold at missile range", ChaseMode.MissileRange, burn,
            p => $"Sit inside missile range ({C.Missile.LaunchRangeMm * 0.8 * 1000:#,0} km) out of their railguns' reach: there in {When(p.Flight!.ArriveHour - w.Hour)}, {Fuel(p)}.{held}",
            hasMissiles ? null : "No missiles left in this fleet");
        Chase("burn", "Burn past", ChaseMode.Overtake, burn, p =>
        {
            var last = p.Flight!.Legs[^1];
            var pass = (last.VelocityAt(last.End) - s.Velocity).Length;
            var inRange = pass > 1e-6 ? 2 * C.Railgun.RangeMm / pass : double.PositiveInfinity;
            return $"Fly past {FlightPlanner.OvertakePassMm * 1000:#,0} km to one side of them (never into them) at {FlightPlanner.Kms(pass)} faster than they move: about {When(inRange)} in railgun range each way, passes in {When(p.Flight.ArriveHour - w.Hour)}, {Fuel(p)}.";
        });
        Chase("shadow", "Shadow them", ChaseMode.ShadowMissiles, burn,
            p => $"Keep pace just outside their missile range ({C.Missile.LaunchRangeMm * 1.1 * 1000:#,0} km): watch without being shot at, {Fuel(p)}.");

        // Running: home, or straight away from them.
        var faster = theirSpeed > perf.MaxSpeed * 1.001;
        var warn = faster
            ? $"They are already moving at {FlightPlanner.Kms(theirSpeed)}, faster than this fleet's top speed of {FlightPlanner.Kms(perf.MaxSpeed)}: running only buys time."
            : $"This fleet's top speed is {FlightPlanner.Kms(perf.MaxSpeed)}; they are moving at {FlightPlanner.Kms(theirSpeed)} now.";
        if (Economy.HomeOf(w, mine.Side) is Body home)
        {
            var req = new FlightRequest(home.Id, home.Position, 1.0, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick);
            var plan = FlightPlanner.Plan(w, mine, req);
            list.Add(plan is { Ok: true, Flight: Flight f }
                ? new ApproachOption("home", "Run for home", $"Full speed to {home.Name}, there in {When(f.ArriveHour - w.Hour)}, {Fuel(plan)}. {warn}", null, req, false)
                : new ApproachOption("home", "Run for home", "", plan.Problem ?? "Cannot get home", null, false));
        }
        var away = (mine.Position - s.Guess(w.Hour)).Normalized();
        if (away == Vec2.Zero) away = new Vec2(1, 0);
        {
            var point = mine.Position + away * 2000;
            var req = new FlightRequest(null, point, 1.0, ArrivalStyle.FastPass, HoldStyle.Drift, w.Tick);
            var plan = FlightPlanner.Plan(w, mine, req);
            list.Add(plan is { Ok: true }
                ? new ApproachOption("away", "Turn away", $"Burn straight away from them at full speed and coast on, {Fuel(plan)}. {warn}", null, req, false)
                : new ApproachOption("away", "Turn away", "", plan.Problem ?? "Cannot turn away", null, false));
        }
        return list;
    }
}
