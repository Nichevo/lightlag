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
    public double LookAheadHours { get; init; }
    public double OurTopSpeed { get; init; }
    public bool WeHaveMissiles { get; init; }
    public bool WeHaveRailguns { get; init; }

    public TimeWindow? OurRailguns { get; init; }
    public TimeWindow? OurMissiles { get; init; }
    public TimeWindow? TheirRailguns { get; init; }
    public TimeWindow? TheirMissiles { get; init; }
    public List<IncomingSalvo> Salvos { get; init; } = new();
    public List<ApproachOption> Options { get; init; } = new();
}

public static class Engagement
{
    private static CombatData C => GameData.Combat;

    /// <summary>The enemy fleet your fleet is most concerned with: in a battle with it, else the nearest one seen.</summary>
    public static int? EnemyFor(World w, Fleet mine)
    {
        if (mine.Chase is { } c && Sensors.Find(w, mine.Side, c.TargetFleetId) != null) return c.TargetFleetId;
        int? best = null;
        var bestD = double.MaxValue;
        foreach (var s in w.Sightings)
        {
            if (s.SeenBy != mine.Side) continue;
            var d = Vec2.Distance(s.Guess(w.Hour), mine.Position);
            if (!s.Visible) d *= 3; // prefer what is in sight
            if (d < bestD) { bestD = d; best = s.FleetId; }
        }
        return best;
    }

    public static EngagementInfo? Of(World w, Fleet mine, int enemyId)
    {
        var s = Sensors.Find(w, mine.Side, enemyId);
        if (s == null || !mine.Position.IsFinite) return null;
        var now = w.Hour;
        Vec2 Them(double t) => Sensors.Predict(w, mine.Side, enemyId, now + t) ?? s.Guess(now + t);
        Vec2 Us(double t) => Combat.PredictFleet(w, mine, now + t);
        double GapAt(double t) => Vec2.Distance(Us(t), Them(t));

        var ships = w.ShipsOf(mine).ToList();
        var weMissiles = ships.Any(x => x.Missiles > 0 && x.Disabled != Module.Missiles);
        var weRails = ships.Any(x => GameData.ShipClass(x.ClassId).Railguns > 0 && x.Disabled != Module.Railguns);

        // Closest approach in the next two days, then a look-ahead that shows it with room either side.
        var gap0 = GapAt(0);
        double closestT = 0, closest = gap0;
        for (var i = 1; i <= 960; i++)
        {
            var t = i * 0.05;
            var g = GapAt(t);
            if (g < closest) { closest = g; closestT = t; }
        }
        for (var i = -20; i <= 20; i++) // finer around it
        {
            var t = Math.Max(0, closestT + i * 0.0025);
            var g = GapAt(t);
            if (g < closest) { closest = g; closestT = t; }
        }
        var ahead = Math.Clamp(Math.Max(closestT * 1.6, closestT + 0.25), 0.25, 48);

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
            LookAheadHours = ahead,
            OurTopSpeed = perf.MaxSpeed,
            WeHaveMissiles = weMissiles,
            WeHaveRailguns = weRails,
            OurRailguns = weRails ? Window(C.Railgun.RangeMm) : null,
            OurMissiles = weMissiles ? Window(C.Missile.LaunchRangeMm) : null,
            TheirRailguns = Window(C.Railgun.RangeMm),
            TheirMissiles = Window(C.Missile.LaunchRangeMm),
            Salvos = Salvos(w, mine, enemyId),
            Options = Options(w, mine, enemyId, s, perf),
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

    private static List<ApproachOption> Options(World w, Fleet mine, int enemyId, Sighting s, FleetPerformance perf)
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
                ? new ApproachOption(key, label, summary(plan), null, req, current)
                : new ApproachOption(key, label, "", plan.Problem ?? "Cannot do that", null, current));
        }

        var ships = w.ShipsOf(mine).ToList();
        var hasRails = ships.Any(x => GameData.ShipClass(x.ClassId).Railguns > 0 && x.Disabled != Module.Railguns);
        var hasMissiles = ships.Any(x => x.Missiles > 0 && x.Disabled != Module.Missiles);

        Chase("engage", "Brake and engage", ChaseMode.GunRange, 1.0,
            p => $"Close to railgun range and keep pace: there in {When(p.Flight!.ArriveHour - w.Hour)}, {Fuel(p)}.",
            hasRails ? null : "No working railguns in this fleet");
        Chase("missiles", "Hold at missile range", ChaseMode.MissileRange, 1.0,
            p => $"Sit inside missile range ({C.Missile.LaunchRangeMm * 0.8 * 1000:#,0} km) out of their railguns' reach: there in {When(p.Flight!.ArriveHour - w.Hour)}, {Fuel(p)}.",
            hasMissiles ? null : "No missiles left in this fleet");
        Chase("burn", "Burn through", ChaseMode.Overtake, 1.0, p =>
        {
            var last = p.Flight!.Legs[^1];
            var pass = (last.VelocityAt(last.End) - s.Velocity).Length;
            var inRange = pass > 1e-6 ? 2 * C.Railgun.RangeMm / pass : double.PositiveInfinity;
            return $"Fly straight through at {FlightPlanner.Kms(pass)} faster than them: about {When(inRange)} in railgun range each way, passes in {When(p.Flight.ArriveHour - w.Hour)}, {Fuel(p)}.";
        });
        Chase("shadow", "Shadow them", ChaseMode.ShadowMissiles, 1.0,
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
