namespace Lightlag.Sim.Core;

/// <summary>
/// Fleets chasing an enemy fleet. The chase flight is planned against the enemy's last seen track and
/// planned again only when the enemy is seen to change course (a new burn), so the chase carries on
/// while it is out of sight. It ends when the enemy is gone or forgotten, after an overtake, or on any
/// other order.
/// </summary>
public static class Chases
{
    /// <summary>How far (Mm) the enemy may stray from the planned track before the chase re-aims.</summary>
    public const double ReaimDriftMm = 1.0;

    public static ChaseOrder Order(World w, Fleet f, int targetId, ChaseMode mode, double speedFraction)
    {
        var s = Sensors.Find(w, f.Side, targetId);
        var own = w.FindFleet(targetId) is { } t && t.Side == f.Side ? t : null; // joining one of its own fleets
        f.LostContact = null;
        return new ChaseOrder
        {
            TargetFleetId = targetId,
            Mode = mode,
            SpeedFraction = speedFraction,
            TargetAt = Sensors.Predict(w, f.Side, targetId, w.Hour) ?? Vec2.Zero,
            TargetVelocity = own?.Velocity ?? s?.Velocity ?? Vec2.Zero,
            PlannedHour = w.Hour,
        };
    }

    /// <summary>A quarry's track passing this close (Mm) to a planet of ours counts as heading for it.</summary>
    public const double MeetMm = 0.01 * Physics.MmPerAu;

    /// <summary>
    /// Is the quarry heading for a planet the fleet's side holds? Its seen track (straight on at its seen velocity)
    /// passes within MeetMm of the planet, ahead of it: the first such planet, and hours until it is closest there.
    /// A quarry holding orbit, or barely moving, is heading nowhere.
    /// </summary>
    /// <remarks>A planet and its moon both lie on the track: prefer, when it is one of them, the body the asking fleet is at.</remarks>
    public static (Body Body, double Hours)? HeadingFor(World w, Side side, Sighting s, int? preferBodyId = null)
    {
        if (s.OrbitBodyId != null || s.Velocity.Length < 1e-3) return null;
        var at = Sensors.Predict(w, side, s.FleetId, w.Hour) ?? s.Guess(w.Hour);
        var v = s.Velocity;
        (Body, double)? best = null;
        foreach (var b in w.Bodies.Where(b => b.Owner == side).OrderBy(b => b.Id))
        {
            var rel = b.Position - at;
            var t = (rel.X * v.X + rel.Y * v.Y) / (v.X * v.X + v.Y * v.Y);
            if (t <= 0) continue;
            // The planet moves on its orbit meanwhile: aim once more at where it will be then.
            var then = FlightPlanner.PredictBody(w, b, w.Hour + t);
            rel = then - at;
            t = (rel.X * v.X + rel.Y * v.Y) / (v.X * v.X + v.Y * v.Y);
            if (t <= 0 || Vec2.Distance(at + v * t, FlightPlanner.PredictBody(w, b, w.Hour + t)) > MeetMm) continue;
            if (b.Id == preferBodyId) return (b, t);
            if (best is not var (_, bt) || t < bt) best = (b, t);
        }
        return best;
    }

    /// <summary>
    /// A chase order against a quarry coming to a planet of ours: wait there for it (already in orbit), or fly there
    /// when that is sooner than the quarry arrives. Null: chase as usual. Chasing out past a fleet on its way to
    /// the planet the chaser just left let it bomb that planet unopposed.
    /// </summary>
    public static (Body Body, Flight? Flight)? MeetAt(World w, Fleet f, int quarryId, ChaseMode mode, double speedFraction)
    {
        if (mode is not (ChaseMode.GunRange or ChaseMode.MissileRange)) return null;
        if (w.FindFleet(quarryId) is not Fleet q || q.Side == f.Side || Sensors.Find(w, f.Side, quarryId) is not Sighting s) return null;
        if (HeadingFor(w, f.Side, s, f.OrbitBodyId) is not var (body, hours)) return null;
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId == body.Id && f.Flight is not { Started: true }) return (body, null);
        var plan = FlightPlanner.Plan(w, f, new FlightRequest(body.Id, body.Position, speedFraction, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
        return plan is { Ok: true, Flight: Flight flight } && flight.ArriveHour <= w.Hour + hours ? (body, flight) : null;
    }

    /// <summary>Only a gun-range chase (or no chase) hands over to close combat; the others keep their distance.</summary>
    public static bool LetsEngage(Fleet f) => f.Chase is not { } c || c.Mode == ChaseMode.GunRange;

    /// <summary>Where the current chase flight expects the enemy to be now.</summary>
    public static Vec2 Expected(ChaseOrder c, double hour) => c.TargetAt + c.TargetVelocity * (hour - c.PlannedHour);

    /// <summary>
    /// A quarry out of sight this long, once the chase has reached its standoff from the guess, is looked for:
    /// the chase closes to within its own sensor range of the guess (not its weapons standoff), so the spot is
    /// really seen, and an empty spot ends the chase. Fresher guesses are trusted (closing in on a real quarry
    /// to look would walk into its guns).
    /// </summary>
    public const double SearchAfterHours = 72;

    /// <summary>Share of the fleet's cold-target sensor range a search closes to.</summary>
    public const double SearchShare = 0.6;

    /// <summary>
    /// Far from its quarry (beyond this many standoffs), a chase re-aims at most this often while the quarry is
    /// seen burning: re-aiming every hour of a long braking burn only spammed new flights.
    /// </summary>
    public const double FarStandoffs = 4, FarReaimHours = 4;

    /// <summary>How close a searching chase comes to the guess: well inside what it would see of a cold fleet.</summary>
    public static double SearchStandoff(World w, Fleet f) => Sensors.Range(Sensors.SensorOf(w, f), GameData.Sensors.Cold) * SearchShare;

    /// <summary>How long a chase goes on without closing on its quarry before the fleet gives up.</summary>
    public const double GiveUpHours = 10 * 24;

    /// <summary>Once an hour: end finished chases and re-aim those whose target has changed course.</summary>
    /// <summary>Hours without closing in before a chase says the quarry is pulling away.</summary>
    public const double PullingAwayHours = 2;

    /// <summary>
    /// The quarry is getting away: the chase has not closed in for PullingAwayHours (it is burning away as hard
    /// as we chase). Says the gap now and the closest it came, for the fleet panel and the engagement screen.
    /// </summary>
    public static string? PullingAway(World w, Fleet f)
    {
        if (f.Chase is not { } c || c.Mode == ChaseMode.Join || c.ClosestHour <= 0 || w.Hour - c.ClosestHour < PullingAwayHours) return null;
        if (Sensors.Find(w, f.Side, c.TargetFleetId) is not Sighting s) return null;
        var gap = Vec2.Distance(f.Position, Sensors.Predict(w, f.Side, c.TargetFleetId, w.Hour) ?? s.Guess(w.Hour));
        if (gap <= c.ClosestGap) return null;
        return $"they are pulling away: {gap * 1000:#,0} km now, closest was {c.ClosestGap * 1000:#,0} km {w.Hour - c.ClosestHour:0} h ago. "
               + $"Their burn matches or beats yours; the chase closes once they stop burning (gives up after {GiveUpHours / 24:0} days)";
    }

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        var now = w.Hour;
        foreach (var f in w.Fleets.Where(f => f.Chase != null).ToList())
        {
            var c = f.Chase!;
            var target = w.FindFleet(c.TargetFleetId);
            if (c.Mode == ChaseMode.Join)
            {
                Join(sim, f, c, target);
                continue;
            }
            var s = Sensors.Find(w, f.Side, c.TargetFleetId);
            if (target == null || s == null || f.Retreating)
            {
                f.Chase = null;
                if (!f.Retreating) LostQuarry(sim, f, c, target == null);
                continue;
            }
            if (c.StartHour <= 0) c.StartHour = now;
            c.LastBodyId = s.OrbitBodyId;
            c.LastSeenHour = s.Hour;
            c.LastSeenAt = s.Position;
            // Closing in counts as progress however long it takes (a far quarry holding orbit takes weeks to reach);
            // a quarry that keeps ahead for days is let go: endless chases kept battles from ever ending.
            var gapNow = Vec2.Distance(f.Position, Sensors.Predict(w, f.Side, c.TargetFleetId, now) ?? s.Guess(now));
            if (c.ClosestHour <= 0 || gapNow < c.ClosestGap * 0.98) { c.ClosestGap = gapNow; c.ClosestHour = now; }
            if (now - c.ClosestHour > GiveUpHours && !f.Maneuvering)
            {
                f.Chase = null;
                sim.Log.Info("fleet", $"{f.Name} gives up the chase", new { fleetId = f.Id, targetFleetId = c.TargetFleetId, hours = Math.Round(now - c.StartHour) });
                Autonomy.Report(sim, f, $"{f.Name} gives up chasing {s.Name}: it has not closed in for {GiveUpHours / 24:0} days. It flies on to where it was heading and waits there.", NoteKind.StandingOrder);
                continue;
            }
            if (f.Maneuvering) continue; // in a fight: combat steers
            if (c.WaitBodyId is int waitAt)
            {
                // Waiting at (or flying to) a planet of ours the quarry is heading for: carry on while it still is and
                // is still far off; once it is close, or turns elsewhere, chase it as usual.
                var close = gapNow <= (FlightPlanner.ChaseDistance(c.Mode) ?? 0) * 2 + GameData.Combat.ContactRangeMm;
                if (!close && HeadingFor(w, f.Side, s, waitAt) is var (b, _) && b.Id == waitAt) continue;
                c.WaitBodyId = null;
                c.PlannedHour = -1; // forces a fresh plan below
                sim.Log.Info("fleet", $"{f.Name} stops waiting and chases", new { fleetId = f.Id, targetFleetId = c.TargetFleetId, close });
            }
            var detoured = c.Detouring && f.Flight is not { Detour: true };
            if (c.Mode == ChaseMode.Overtake && f.Flight == null && !detoured)
            {
                f.Chase = null;
                sim.Log.Info("fleet", $"{f.Name} has overtaken {target.Name}", new { fleetId = f.Id, targetFleetId = target.Id });
                continue;
            }
            var guess = Sensors.Predict(w, f.Side, c.TargetFleetId, now) ?? s.Guess(now);
            var turned = c.PlannedHour < 0 || s.Hour >= c.PlannedHour && ((s.Velocity - c.TargetVelocity).Length > Math.Max(0.5, 0.02 * s.Velocity.Length)
                                       || Vec2.Distance(guess, Expected(c, now)) > ReaimDriftMm);
            // Far off, a quarry seen burning is re-aimed at every few hours, not every hour of its burn.
            var want = FlightPlanner.ChaseDistance(c.Mode);
            if (turned && !detoured && now - c.PlannedHour < FarReaimHours && gapNow > (want ?? 0) * FarStandoffs + 50) turned = false;
            // Out of sight for a while: close to where the fleet's own sensors would show it, to look.
            var search = !s.Visible && now - s.Hour >= SearchAfterHours && c.Mode != ChaseMode.Overtake && (c.Searching || f.Flight == null);
            double? standoff = null;
            if (search)
            {
                var look = SearchStandoff(w, f);
                if (want is not double wd || wd > look) standoff = look;
                if (!c.Searching && standoff != null)
                {
                    c.Searching = true;
                    turned = true;
                    sim.Log.Info("fleet", $"{f.Name} closes in to look for {s.Name}", new { fleetId = f.Id, targetFleetId = c.TargetFleetId, lastSeenHour = s.Hour, standoff });
                }
            }
            else if (c.Searching && s.Visible) { c.Searching = false; turned = true; }
            // A chase that has arrived but drifted off its spot (after a fight, say) sets off again.
            var spot = standoff ?? want;
            var adrift = f.Flight == null && spot is double d && Math.Abs(Vec2.Distance(f.Position, guess) - d) > Math.Max(2, d * 0.25);
            if (!turned && !adrift && !detoured) continue;

            var perf = FleetPerformance.Of(w, f);
            var plan = FlightPlanner.PlanChase(w, f, target, c.Mode, c.SpeedFraction, perf, standoff);
            c.TargetAt = guess;
            c.TargetVelocity = s.Velocity;
            c.PlannedHour = now;
            c.Detouring = false;
            if (plan is { Ok: true, Flight: Flight flight })
            {
                // Re-aimed just after leaving orbit: still climbing away from the planet it left, so it keeps the
                // launch allowance for its first hours.
                if (f.Flight is { FromBodyId: int from } old && flight.FromBodyId == null && now < old.DepartHour + Flight.LaunchHours)
                    flight.FromBodyId = from;
                f.Flight = flight;
                c.Detouring = flight.Detour;
                c.Problem = null;
                sim.Log.Info("fleet", $"{f.Name} re-aims its chase", new
                {
                    fleetId = f.Id, targetFleetId = target.Id, mode = c.Mode.ToString(), flight.ArriveHour, plan.DeltaV, turned, adrift,
                });
            }
            else if (c.Problem != plan.Problem)
            {
                // The wording carries live numbers (the quarry's speed), so only a new kind of problem is logged.
                if (Kind(c.Problem) != Kind(plan.Problem))
                    sim.Log.Warn("fleet", $"{f.Name} cannot keep up the chase", new { fleetId = f.Id, targetFleetId = target.Id, problem = plan.Problem });
                c.Problem = plan.Problem;
            }
        }
    }

    /// <summary>The kind of problem, without its live numbers (the text before any '(' or ':').</summary>
    private static string? Kind(string? problem) => problem?.Split('(', ':')[0].Trim();

    /// <summary>
    /// The quarry is gone (destroyed or merged) or its track was lost. A quarry last seen holding orbit is
    /// looked for at that planet: the fleet flies there and takes up orbit. Otherwise it flies on to where the
    /// quarry was expected and waits there. Either way command is told why the chase ended.
    /// </summary>
    private static void LostQuarry(Simulation sim, Fleet f, ChaseOrder c, bool gone)
    {
        var w = sim.World;
        var name = w.FindFleet(c.TargetFleetId)?.Name ?? "its quarry";
        sim.Log.Info("fleet", $"{f.Name} loses track of its quarry and stops chasing", new { fleetId = f.Id, targetFleetId = c.TargetFleetId, gone, c.LastBodyId });
        if (gone)
        {
            Autonomy.Report(sim, f, $"{f.Name} stops chasing: {name} is gone", NoteKind.StandingOrder);
            return;
        }
        if (c.LastBodyId is int bodyId && w.FindBody(bodyId) is Body body)
        {
            new PlanFlightCommand(f.Id, new FlightRequest(bodyId, body.Position, Math.Max(0.5, c.SpeedFraction), ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick))
                { Source = "auto" }.Apply(w, sim);
            Autonomy.Report(sim, f, $"{f.Name} lost sight of {name}, last seen in orbit at {body.Name}: it goes on to {body.Name} to look for it", NoteKind.StandingOrder);
            return;
        }
        var likely = LikelyBase(w, f.Side, c.LastSeenAt ?? f.Position);
        var age = Math.Max(0, w.Hour - c.LastSeenHour);
        f.LostContact = new LostContact(c.TargetFleetId, name, w.Hour, c.LastSeenHour, likely?.Id);
        Autonomy.Report(sim, f, $"{name} is not where expected (last seen {Ago(age)} ago): {f.Name} stops chasing and waits"
                                + (likely != null ? $". Its likely base is {likely.Name}: search there, or bring the fleet home" : ""), NoteKind.StandingOrder);
    }

    private static string Ago(double hours) => hours < 48 ? $"{hours:0} hours" : $"{hours / 24:0} days";

    /// <summary>Where a lost rival fleet most likely went: the nearest rival planet with a shipyard, else the nearest rival planet.</summary>
    public static Body? LikelyBase(World w, Side side, Vec2 near) =>
        w.Bodies.Where(b => b.Owner != null && b.Owner != side)
            .OrderBy(b => b.Shipyard ? 0 : 1).ThenBy(b => Vec2.Distance(b.Position, near)).ThenBy(b => b.Id).FirstOrDefault();

    /// <summary>Joining one of its own fleets that is moving: merge once alongside, else re-aim when it changes course.</summary>
    private static void Join(Simulation sim, Fleet f, ChaseOrder c, Fleet? into)
    {
        var w = sim.World;
        var now = w.Hour;
        if (into == null || into.Side != f.Side || f.Retreating)
        {
            f.Chase = null;
            f.JoinFleetId = null;
            sim.Log.Info("fleet", $"{f.Name} stops flying to join: that fleet is gone", new { fleetId = f.Id, targetFleetId = c.TargetFleetId });
            return;
        }
        var gap = Vec2.Distance(f.Position, into.Position);
        if (gap <= FlightPlanner.JoinDistanceMm * 4 && (f.Velocity - into.Velocity).Length <= 2)
        {
            f.Chase = null;
            f.Flight = null;
            if (f.SupplyRunFor == into.Id)
            {
                // A supply run keeps its own fleet: it stays alongside, matching speed, to hand over cargo and go home.
                f.Velocity = into.Velocity;
                sim.Log.Info("supply", $"{f.Name} comes alongside {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id });
                return;
            }
            sim.Log.Info("fleet", $"{f.Name} catches up and joins {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id });
            Fleets.Absorb(w, into, f);
            return;
        }
        if (f.Maneuvering) return;
        // The fleet to join has settled into orbit (or is flying to stop at a planet): stop chasing a point that
        // circles the planet and fly to the planet instead; the two merge once both hold orbit there.
        if (Fleets.Meeting(w, into, c.SpeedFraction) is { TargetBodyId: int meetAt } && w.FindBody(meetAt) is Body body)
        {
            var problem = Fleets.FlyToJoin(w, f, into, c.SpeedFraction);
            if (f.SupplyRunFor != into.Id) f.JoinFleetId = into.Id; // a supply run meets it there without merging
            if (problem == null)
            {
                sim.Log.Info("fleet", $"{f.Name} goes to {body.Name} to join {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id, bodyId = body.Id });
                return;
            }
            f.Chase = c;
        }
        var turned = (into.Velocity - c.TargetVelocity).Length > Math.Max(0.5, 0.02 * into.Velocity.Length)
                     || Vec2.Distance(into.Position, Expected(c, now)) > ReaimDriftMm;
        if (!turned && f.Flight != null) return;
        var perf = FleetPerformance.Of(w, f);
        var plan = FlightPlanner.PlanChase(w, f, into, ChaseMode.Join, c.SpeedFraction, perf);
        // Too slow at the speed it was given: a join may burn at full speed to catch up.
        if (!plan.Ok && c.SpeedFraction < 1) plan = FlightPlanner.PlanChase(w, f, into, ChaseMode.Join, 1.0, perf);
        c.TargetAt = into.Position;
        c.TargetVelocity = into.Velocity;
        c.PlannedHour = now;
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            c.Problem = null;
            c.StuckSinceHour = null;
            return;
        }
        // The wording carries live numbers (the other fleet's speed), so only a new kind of problem is logged.
        if (Kind(c.Problem) != Kind(plan.Problem))
            sim.Log.Warn("fleet", $"{f.Name} cannot catch up with {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id, problem = plan.Problem });
        c.Problem = plan.Problem;
        c.StuckSinceHour ??= now;
        if (now - c.StuckSinceHour.Value >= JoinGiveUpHours)
        {
            f.Chase = null;
            f.JoinFleetId = null;
            sim.Log.Info("fleet", $"{f.Name} gives up joining {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id, problem = plan.Problem });
            Autonomy.Report(sim, f, $"{f.Name} cannot catch up with {into.Name} ({plan.Problem}) and stops trying: order {into.Name} to stop or wait, then join again",
                NoteKind.StandingOrder);
        }
    }

    /// <summary>A join chase that has found no way to catch up for this long gives up and says why.</summary>
    public const double JoinGiveUpHours = 24;
}
