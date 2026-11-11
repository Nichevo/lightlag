using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>A missile in flight. It steers at its target ship until it hits, is shot down, dodged, or runs dry.</summary>
public sealed class Missile
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public int TargetShipId { get; set; }
    public int FromShipId { get; set; }
    public Vec2 Position { get; set; }
    public Vec2 Velocity { get; set; }
    public double DeltaVLeft { get; set; }
    public int Brain { get; set; } = 1;
    public double ExpiresHour { get; set; }

    /// <summary>A dropped missile: drifts dark until this hour (see DroppedMissiles).</summary>
    public double WakeHour { get; set; }
}

/// <summary>How one side did in a battle: the numbers behind the combat report.</summary>
public sealed class BattleSide
{
    public int RailShots { get; set; }
    public int RailHits { get; set; }
    public int MissilesFired { get; set; }
    public int MissileHits { get; set; }
    public int ShotDown { get; set; }      // our missiles their point defence stopped
    public int Dodged { get; set; }        // our missiles that missed because the target dodged
    public int RanDry { get; set; }        // our missiles that ran out of fuel or lost their target
    public int Decoyed { get; set; }       // our missiles a heat sink drew off
    public double ShieldSoaked { get; set; } // damage this side's shields took instead of armour
    public double DamageDealt { get; set; }
    public List<string> ShipsLost { get; set; } = new();
    public List<string> Knocked { get; set; } = new();   // "FF-1: railguns"
    public List<string> Crippled { get; set; } = new();  // ships whose hull gave out (they limp home)
    /// <summary>Ships this side brought into the fight, and whether any of its fleets pulled back (and before firing a shot).</summary>
    public int ShipsIn { get; set; }
    /// <summary>Fighting power brought in (for the odds going in), each ship's class, and each fleet's pull-back preset.</summary>
    public double PowerIn { get; set; }
    public List<string> Makeup { get; set; } = new();
    public List<string> Rules { get; set; } = new();
    /// <summary>Each fleet's formation and mission when it came in, and railgun volleys a fleet-mate blocked.</summary>
    public List<string> Formations { get; set; } = new();
    public List<string> Missions { get; set; } = new();
    public int BlockedVolleys { get; set; }
    /// <summary>"Hold on" was on for one of its fleets, and freighters this side caught in the fight.</summary>
    public bool HeldOn { get; set; }
    public int CargoCaught { get; set; }
    public bool Withdrew { get; set; }
    public bool WithdrewBeforeShots { get; set; }
    public List<string> Fleets { get; set; } = new();

    /// <summary>Light lag from this side's flagship to the battle when it started (hours), and whether the flagship's fleet fought.</summary>
    public double LagHours { get; set; }
    public bool Flagship { get; set; }
}

/// <summary>A battle: from first contact until no enemies are in contact and no missiles fly.</summary>
public sealed class Battle
{
    public int Id { get; set; }
    public double StartHour { get; set; }
    public double? EndHour { get; set; }
    public Vec2 Where { get; set; }

    /// <summary>
    /// The last time anything happened: a shot, a launch, a missile in flight or a fleet closing in.
    /// After an hour with nothing, the battle is a standoff and time runs at the normal speed again.
    /// </summary>
    public double LastActionHour { get; set; }

    /// <summary>When the fighting last started: the battle's start, or the end of its last standoff.</summary>
    public double FightingSinceHour { get; set; }

    /// <summary>Something happened at this hour (a shot, a launch, a missile flying, a fleet closing in).</summary>
    public void Action(double hour)
    {
        if (hour - LastActionHour >= 1) FightingSinceHour = hour; // a standoff turned back into a fight
        LastActionHour = hour;
    }

    public BattleSide[] Sides { get; set; } = { new(), new() };

    /// <summary>How it ended (see BattleRules.Ending): set when it is over.</summary>
    public BattleEnding? Ending { get; set; }

    public BattleSide For(Side s) => Sides[(int)s];

    /// <summary>The recording, to watch the battle again from News (see BattleRecords).</summary>
    public List<BattleFrame> Frames { get; set; } = new();
    public List<RosterShip> Roster { get; set; } = new();
    public List<BattleMark> Marks { get; set; } = new();
    public List<FrameShot> PendingShots { get; set; } = new();
    public double FrameEveryHours { get; set; }
}

/// <summary>
/// Fleet combat. Runs inside battle time (10-second steps). Fleets on Engage close to railgun range;
/// destroyers fire missile salvos from long range; point defence across the whole target fleet shoots
/// at incoming missiles (faster missiles give it fewer shots); armour soaks damage, and each hit may
/// knock out one part. Approach speed decides how long fleets stay in range of each other.
/// </summary>
public static class Combat
{
    private static CombatData C => GameData.Combat;

    /// <summary>
    /// Could any two enemy fleets come into contact during the next hour, or are missiles flying?
    /// If so the hour is played in battle time.
    /// </summary>
    public static bool BattlePossible(World w)
    {
        if (w.Missiles.Count > 0 || DroppedMissiles.MayWake(w)) return true;
        var standoff = Standoff(w);
        if (w.ActiveBattle != null && !standoff) return true;
        return PossibleMeeting(w) != null;
    }

    /// <summary>A player fleet and an enemy fleet that could come into contact in the next hour (why battle time is on), if any.</summary>
    public static (Fleet Mine, Fleet Theirs)? PossibleMeeting(World w)
    {
        var to = w.Hour + 1;
        var ends = new Dictionary<int, (Vec2 A, Vec2 B, double Margin)>();
        foreach (var f in w.Fleets)
        {
            var margin = f.Maneuvering ? FleetPerformance.Of(w, f).MaxSpeed : 0;
            ends[f.Id] = (f.Position, PredictFleet(w, f, to), margin);
        }
        foreach (var a in w.Fleets)
        {
            if (a.Side != Side.Player) continue;
            foreach (var b in w.Fleets)
            {
                if (b.Side == a.Side) continue;
                var (a0, a1, ma) = ends[a.Id];
                var (b0, b1, mb) = ends[b.Id];
                // Closest approach if both moved in straight lines over the hour (orbits curve, hence the margin).
                var r0 = a0 - b0;
                var dv = (a1 - a0) - (b1 - b0);
                var s = dv.Length > 1e-12 ? Math.Clamp(-(r0.X * dv.X + r0.Y * dv.Y) / (dv.X * dv.X + dv.Y * dv.Y), 0, 1) : 0;
                var closest = (r0 + dv * s).Length;
                if (!(closest <= C.ContactRangeMm + 50 + ma + mb)) continue;
                // Fleets already close together only need battle time if something can change: one is
                // flying closer, manoeuvring, or would close in to fight. Parked or drifting neighbours that
                // can't (or won't) fight are checked once an hour instead.
                if (r0.Length > C.ContactRangeMm + 50) return (a, b);
                if (ma > 0 || mb > 0 || WouldEngage(w, a, b) || WouldEngage(w, b, a)) return (a, b);
                if ((a.Flight is { Started: true } || b.Flight is { Started: true }) && closest < r0.Length - 1) return (a, b);
            }
        }
        return null;
    }

    /// <summary>Where a fleet will be at a future hour if nothing changes.</summary>
    public static Vec2 PredictFleet(World w, Fleet f, double hour)
    {
        if (f.Flight != null && FlightPlanner.FlightState(f.Flight, hour) is { } st) return st.Position;
        return FlightPlanner.PredictHoldPosition(w, f, hour);
    }

    // ---- Steering (before fleets move) ---------------------------------------------------

    public static void Steer(Simulation sim, double t0, double t1)
    {
        var w = sim.World;
        Supply.Check(sim);
        var dt = t1 - t0;
        foreach (var f in w.Fleets.ToList())
        {
            var enemy = NearestEnemyFleet(w, f, C.ContactRangeMm);
            BattleRules.Check(sim, f, enemy);
            if (!f.Retreating && !GoingHome(w, f))
            {
                if (f.Stance == Stance.ReturnHome && enemy != null)
                { Retreat(sim, f, $"meets {enemy.Name} and breaks off for home"); StandingNote(sim, f, $"{f.Name} met {enemy.Name} and breaks off for home (standing order: Return home)"); }
                else if (f.Stance == Stance.Avoid && Threat(w, f) is string threat)
                { Retreat(sim, f, $"avoids {threat} and heads home"); StandingNote(sim, f, $"{f.Name} avoids {threat} and heads home (standing order: Avoid)"); }
            }
            if (f.Stance == Stance.HoldPosition && enemy != null && !f.Maneuvering && !f.Retreating
                && (f.Flight != null || f.Velocity.Length > 1e-6))
            {
                // Hold position: stop the flight and brake where it is (the brake below does the rest).
                f.Maneuvering = true;
                f.Flight = null;
                LeaveOrbitToFight(sim, f);
                Fleets.EnterDrift(f);
                sim.Log.Info("combat", $"{f.Name} holds position", new { fleetId = f.Id, enemyId = enemy.Id });
            }

            var wantsToClose = enemy != null && WouldEngage(w, f, enemy) && !HoldsOrbit(w, f);
            if (!wantsToClose && !f.Maneuvering) continue;

            var perf = FleetPerformance.Of(w, f);
            if (perf.DeltaVLeft <= 1e-6)
            {
                f.Maneuvering = false; // out of fuel: it drifts on as it is
                continue;
            }
            Vec2 desired;
            if (wantsToClose)
            {
                if (!f.Maneuvering)
                {
                    f.Maneuvering = true;
                    f.Flight = null;          // engaging overrides the flight
                    LeaveOrbitToFight(sim, f);
                    Fleets.EnterDrift(f);
                    sim.Log.Info("combat", $"{f.Name} closes in on {enemy!.Name}", new { fleetId = f.Id, enemyId = enemy.Id });
                }
                var gap = enemy!.Position - f.Position;
                var excess = gap.Length - C.EngageRangeMm * 0.75;
                var closing = Math.Clamp(excess / 0.05, -perf.MaxSpeed * 0.3, perf.MaxSpeed * 0.6);
                desired = enemy.Velocity + gap.Normalized() * closing;
            }
            else if (f.Chase != null)
            {
                // Lost sight while chasing: carry on as it is and let the chase take over again.
                f.Maneuvering = false;
                continue;
            }
            else
            {
                // Nothing to chase: brake to a stop, or, after leaving an orbit to fight, to the planet's own motion.
                desired = OrbitHome(w, f) is Body left ? BodyVelocity(w, left) : Vec2.Zero;
            }
            var change = desired - f.Velocity;
            var maxChange = perf.Accel * dt;
            if (change.Length > maxChange) change = change.Normalized() * maxChange;
            var cost = change.Length;
            if (cost > perf.DeltaVLeft) change = change.Normalized() * Math.Max(0, perf.DeltaVLeft);
            f.Velocity += change;
            sim.BurnFuel(f, change.Length);
            if (!wantsToClose && (f.Velocity - desired).Length < 1e-6)
            {
                f.Velocity = desired;
                f.Maneuvering = false;
                // Back with the planet it left: straight back into orbit if it is close enough.
                if (OrbitHome(w, f) is Body home && NearestEnemyFleet(w, f, C.ContactRangeMm) == null
                    && Fleets.OrbitableNear(w, f.Position)?.Id == home.Id)
                {
                    Fleets.EnterOrbit(f, home);
                    sim.Log.Info("combat", $"{f.Name} goes back into orbit at {home.Name}", new { fleetId = f.Id, bodyId = home.Id });
                }
                else if (f.Velocity.Length < 1e-6) f.Velocity = Vec2.Zero;
            }
        }
    }

    /// <summary>
    /// A fleet in orbit fights from orbit: it keeps claiming or blockading and fires at whatever comes
    /// into reach. It only breaks orbit to close in when it is being shot at from beyond its railguns
    /// (missiles coming in, nothing in railgun range), and goes back into orbit once the fight is over.
    /// </summary>
    public static bool HoldsOrbit(World w, Fleet f)
    {
        if (f.Hold != HoldStyle.Orbit || f.OrbitBodyId == null || f.Flight is { Started: true }) return false;
        var mine = f.ShipIds.ToHashSet();
        var underFire = w.Missiles.Any(m => m.Side != f.Side && mine.Contains(m.TargetShipId));
        var inReach = w.Fleets.Any(o => o.Side != f.Side && Vec2.Distance(o.Position, f.Position) <= C.Railgun.RangeMm && Sensors.Sees(w, f.Side, o));
        return !underFire || inReach;
    }

    /// <summary>The body a fleet left orbit at to fight, while it is not retreating or refilling (it goes back there afterwards).</summary>
    private static Body? OrbitHome(World w, Fleet f) =>
        f.LeftOrbitBodyId is int id && !f.Retreating && !f.Resupplying ? w.FindBody(id) : null;

    /// <summary>How fast a body moves now (Mm per hour).</summary>
    public static Vec2 BodyVelocity(World w, Body b) => FlightPlanner.PredictBody(w, b, w.Hour + 1) - FlightPlanner.PredictBody(w, b, w.Hour);

    /// <summary>Hourly: a fleet that broke orbit to fight goes back once no enemy is in contact (unless it is retreating or refilling).</summary>
    public static void ReturnToOrbit(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets)
        {
            if (f.LeftOrbitBodyId is not int id || w.FindBody(id) is not Body body) continue;
            if (f.Retreating || f.Resupplying || f.Maneuvering || f.Flight != null || f.Chase != null) continue;
            if (NearestEnemyFleet(w, f, C.ContactRangeMm) != null) continue;
            if (Fleets.OrbitableNear(w, f.Position)?.Id == body.Id && (f.Velocity - BodyVelocity(w, body)).Length < 100)
            {
                Fleets.EnterOrbit(f, body);
                sim.Log.Info("combat", $"{f.Name} goes back into orbit at {body.Name}", new { fleetId = f.Id, bodyId = body.Id });
                continue;
            }
            var plan = FlightPlanner.Plan(w, f, new FlightRequest(body.Id, body.Position, 0.5, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
            if (plan is not { Ok: true, Flight: Flight flight }) continue;
            f.Flight = flight;
            sim.Log.Info("combat", $"{f.Name} goes back into orbit at {body.Name}", new { fleetId = f.Id, bodyId = body.Id });
        }
    }

    /// <summary>A far fleet acting on a standing order tells command (late, at light speed); live fleets need no note.</summary>
    internal static void StandingNote(Simulation sim, Fleet f, string text)
    {
        if (sim.World.Light.On && Light.DelayHours(sim.World, f.Side, f.Position) > 0) Autonomy.Report(sim, f, text, NoteKind.StandingOrder);
    }

    /// <summary>A fleet in orbit breaks it to fight: remember where, so the player can be told (claims and bombardment stop until it orbits again).</summary>
    private static void LeaveOrbitToFight(Simulation sim, Fleet f)
    {
        if (f.Hold != HoldStyle.Orbit || f.OrbitBodyId is not int bodyId) return;
        f.LeftOrbitBodyId = bodyId;
        // It keeps moving with the planet (an orbit is stored with no velocity of its own); otherwise
        // the planet would race away at its orbital speed the moment the fleet left orbit.
        f.Velocity = Sensors.MotionOf(sim.World, f);
        if (sim.World.FindBody(bodyId) is Body b)
            StandingNote(sim, f, $"{f.Name} leaves orbit at {b.Name} to fight (standing order: {f.Stance}); claiming and bombarding there stop until it orbits again");
        sim.Log.Info("combat", $"{f.Name} leaves orbit to fight", new { fleetId = f.Id, bodyId });
    }

    /// <summary>Computer-run fleets fire at long range while they run (to make a chaser dodge and burn).</summary>
    internal static bool AiLongShots(World w, Fleet f) =>
        Mind.Of(w, f.Side) != null && (f.Retreating || f.Stance is Stance.Avoid or Stance.ReturnHome);

    /// <summary>Is this fleet dodging railgun fire now? (Set to dodge, and supplies not yet low.)</summary>
    public static bool Dodging(World w, Fleet f) => f.Dodge != DodgeMode.Off && !f.Resupplying && C.DodgeFactor < 1 && SupplyShare(w, f) >= C.DodgeStopsBelow;

    /// <summary>How long a slug takes to reach its target (seconds): slug speed plus the closing speed.</summary>
    public static double SlugSeconds(double distanceMm, Fleet shooter, Fleet target)
    {
        var gap = target.Position - shooter.Position;
        var closing = gap.Length > 1e-9 ? Math.Max(0, -Vec2.Dot(gap, target.Velocity - shooter.Velocity) / gap.Length) : 0;
        var speed = (C.Spread?.SlugSpeedMmPerHour ?? 180) + closing;
        return distanceMm / speed * 3600;
    }

    /// <summary>
    /// How much a dodge cuts the hit chance (1: not at all). A sidestep beats aimed fire but hardly helps
    /// against a fan; a hard burn helps against both. Close up, or closing fast, a slug arrives before
    /// the ship can move far, so the dodge does less.
    /// </summary>
    public static double DodgeFactor(DodgeMode dodge, FireMode fire, double slugSeconds)
    {
        var s = C.Spread;
        double full = dodge switch
        {
            DodgeMode.Off => 1,
            DodgeMode.HardBurn when s != null => fire == FireMode.Spread ? s.HardVsSpread : s.HardVsFocused,
            _ => s != null && fire == FireMode.Spread ? s.SidestepVsSpread : C.DodgeFactor,
        };
        var time = Math.Clamp(slugSeconds / (s?.FullDodgeSeconds ?? 1e-9), 0, 1);
        return 1 - (1 - full) * time;
    }

    /// <summary>
    /// Computer-run fleets pick their fire and dodge from what they can see: a fan against a fleet seen
    /// jinking, aimed fire otherwise; a hard burn while spread fire is coming in and there is fuel to spare.
    /// </summary>
    private static void AutoTactics(World w, Fleet shooter, Fleet target)
    {
        if (Mind.Of(w, shooter.Side) != null)
            shooter.Fire = target.Dodge == DodgeMode.Sidestep && Dodging(w, target) ? FireMode.Spread : FireMode.Focused;
        if (Mind.Of(w, target.Side) != null)
            target.Dodge = shooter.LastVolley == FireMode.Spread && SupplyShare(w, target) > 0.5 ? DodgeMode.HardBurn : DodgeMode.Sidestep;
    }

    /// <summary>Supplies left as a share of full, across the fleet.</summary>
    public static double SupplyShare(World w, Fleet f)
    {
        double have = 0, full = 0;
        foreach (var s in w.ShipsOf(f)) { have += s.Supplies; full += Fitting.MaxSupplies(w, s); }
        return full > 0 ? have / full : 0;
    }

    /// <summary>
    /// Would this fleet close in to fight that enemy? (Engage stance, sees it, has railguns and fuel, isn't
    /// retreating.) An Engage fleet breaks off a flight to fight; a Hold fleet keeps its course.
    /// </summary>
    public static bool WouldEngage(World w, Fleet f, Fleet enemy) =>
        (f.Stance == Stance.Engage || f.Stance == Stance.EngageIfStronger) && !f.Retreating && !f.Resupplying && Chases.LetsEngage(f)
        && (!enemy.Retreating || f.Chase != null) // a fleet pulling back is let go (no chase) unless told to chase it
        && Vec2.Distance(f.Position, enemy.Position) <= C.ContactRangeMm && Sensors.Sees(w, f.Side, enemy)
        && HasWorking(w, f, Module.Railguns) && FleetPerformance.Of(w, f).DeltaVLeft > 1e-3
        && (f.Stance == Stance.Engage || Stronger(w, f));

    /// <summary>How much more power a fleet set to engage-if-stronger wants than the enemies in contact.</summary>
    public const double StrongerBy = 1.2;

    /// <summary>Does this fleet clearly outgun every enemy it can see in contact range, taken together?</summary>
    public static bool Stronger(World w, Fleet f)
    {
        double theirs = 0;
        foreach (var o in w.Fleets)
            if (o.Side != f.Side && Vec2.Distance(o.Position, f.Position) <= C.ContactRangeMm && Sensors.Sees(w, f.Side, o))
                theirs += Stats.Power(w, o);
        return Stats.Power(w, f) >= theirs * StrongerBy;
    }

    /// <summary>Hours of warning an avoiding fleet wants before an enemy reaches it.</summary>
    public const double AvoidWarningHours = 12;

    /// <summary>
    /// The enemy an avoiding fleet should run from, if any: one in contact range, or one it sees coming
    /// for it within the warning time.
    /// </summary>
    public static string? Threat(World w, Fleet f)
    {
        if (NearestEnemyFleet(w, f, C.ContactRangeMm) is Fleet near) return near.Name;
        foreach (var s in w.Sightings)
            if (s.SeenBy == f.Side && s.Visible && Sensors.HeadingFor(s, f, w.Hour, Sensors.MotionOf(w, f)) is double eta && eta <= AvoidWarningHours)
                return s.Name;
        return null;
    }

    /// <summary>Is the fleet at home, or already on its way there?</summary>
    public static bool GoingHome(World w, Fleet f)
    {
        var home = Economy.HomeOf(w, f.Side);
        if (home == null) return true;
        return f.Flight?.TargetBodyId == home.Id || f.Flight == null && f.Hold == HoldStyle.Orbit && f.OrbitBodyId == home.Id;
    }

    internal static void Retreat(Simulation sim, Fleet f, string why, Body? to = null, Vec2? point = null, double speed = 1.0)
    {
        var w = sim.World;
        f.Retreating = true;
        f.Maneuvering = false;
        f.Chase = null;
        if (point is Vec2 at && to == null)
        {
            var reqAt = new FlightRequest(null, at, speed, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick);
            var planAt = FlightPlanner.Plan(w, f, reqAt);
            if (!planAt.Ok) planAt = FlightPlanner.Plan(w, f, reqAt with { SpeedFraction = 0.3 });
            if (planAt is { Ok: true, Flight: Flight fa })
            {
                f.Flight = fa;
                BattleRecords.Mark(w, f, MarkKind.Decision, $"{f.Name} {why}");
                sim.Log.Info("combat", $"{f.Name} {why}", new { fleetId = f.Id, regroup = at, armour = ArmourShare(w, f) });
                return;
            }
        }
        var home = to ?? Economy.HomeOf(w, f.Side);
        if (home == null) return;
        var req = new FlightRequest(home.Id, home.Position, speed, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick);
        var plan = FlightPlanner.Plan(w, f, req);
        if (!plan.Ok)
        {
            // Not enough fuel for a full-speed run: try slower.
            plan = FlightPlanner.Plan(w, f, req with { SpeedFraction = 0.3 });
        }
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            BattleRecords.Mark(w, f, MarkKind.Decision, $"{f.Name} {why}");
            sim.Log.Info("combat", $"{f.Name} {why}", new { fleetId = f.Id, home = home.Name, armour = ArmourShare(w, f), stance = f.Stance.ToString() });
        }
        else
        {
            sim.Log.Info("combat", $"{f.Name} has no fuel to fly home", new { fleetId = f.Id, why });
        }
    }

    // ---- Firing (after fleets move) ------------------------------------------------------

    public static void Fire(Simulation sim, double t0, double t1)
    {
        var w = sim.World;
        UpdateContacts(sim, t1);
        var battle = w.ActiveBattle;
        if (battle == null) return;
        BattleRecords.Record(sim, battle, t1);

        foreach (var ship in w.Ships.ToList())
        {
            if (w.FleetOf(ship) is not Fleet fleet) continue;
            var cls = Fitting.Spec(ship);
            if (cls.Railguns > 0 && !ship.Out(Module.Railguns) && !ship.Overheated && !fleet.Resupplying && t1 >= ship.RailsReadyHour)
                FireRailguns(sim, battle, ship, fleet, cls, t1);
            if (ship.Missiles > 0 && !ship.Out(Module.Missiles) && t1 >= ship.SalvoReadyHour)
                LaunchSalvo(sim, battle, ship, fleet, t1);
        }

        MoveMissiles(sim, battle, t0, t1);
    }

    private static void FireRailguns(Simulation sim, Battle battle, Ship ship, Fleet fleet, ShipClass cls, double t1)
    {
        var w = sim.World;
        var rail = C.Railgun;
        var railRange = Tech.RailRange(w, ship.Side);
        var reach = fleet.LongShots || AiLongShots(w, fleet) ? Math.Max(railRange, Heat.D.LongRangeMm) : railRange;
        var target = FireControl.Pick(sim, ship, fleet, railRange) ?? (reach > railRange ? NearestEnemyShip(w, ship, reach, fleet) : null);
        if (target == null) return;
        // Railguns need a clear line: with a fleet-mate in the way, try another target in reach, else hold fire a little.
        if (Formations.Blocked(w, ship, target))
        {
            var clear = FireControl.InReach(w, ship, railRange).Where(t => MayFireAt(w, fleet, t) && !Formations.Blocked(w, ship, t))
                .OrderBy(t => t.Crippled ? 1 : 0).ThenBy(t => Vec2.Distance(ship.Position, t.Position)).ThenBy(t => t.Id).FirstOrDefault();
            if (clear == null)
            {
                ship.BlockedHour = t1;
                battle.For(ship.Side).BlockedVolleys++;
                ship.RailsReadyHour = t1 + rail.ReloadSeconds * 0.5 / 3600;
                return;
            }
            target = clear;
        }
        target = Flagships.Screened(sim, target);
        var dist = Vec2.Distance(ship.Position, target.Position);
        var longShot = dist > railRange;
        target.LastTargetedHour = t1;
        var targetFleet = w.FleetOf(target)!;
        var stats = battle.For(ship.Side);
        battle.Action(t1);
        fleet.LastBurnHour = t1; // firing heats the ships up: they stay easier to see for a while
        AutoTactics(w, fleet, targetFleet);
        var spread = C.Spread;
        var mode = spread != null ? fleet.Fire : FireMode.Focused;
        var slugs = cls.Railguns * (mode == FireMode.Spread ? spread!.SlugsPerGun : 1);
        var cooldown = mode == FireMode.Spread ? spread!.Cooldown : 1; // a burst heats the guns: they wait longer
        var bridge = FlagBridge(w, fleet);
        ship.RailsReadyHour = t1 + rail.ReloadSeconds * cooldown * Tech.ReloadFactor(w, ship.Side) * (bridge?.ReloadFactor ?? 1)
            * (longShot ? Heat.D.LongReloadFactor : 1) * (SupplyLines.Hungry(ship) ? SupplyLines.HungryReload : 1) / Wounds.CrewFactor(ship) / 3600;
        fleet.LastVolley = mode;
        fleet.LastVolleyHour = t1;
        var fan = (spread == null ? 0.6 : mode == FireMode.Spread ? spread.FanMm : spread.FocusFanMm);
        sim.AddEffect(new CombatEffect(t1, EffectKind.Volley, ship.Position, target.Position, ship.Side, fan));
        var side = (target.Position - ship.Position).Normalized();
        side = new Vec2(-side.Y, side.X);
        for (var i = 0; i < slugs; i++)
        {
            if (ship.Ammo < rail.SuppliesPerShot) break;
            ship.Ammo -= rail.SuppliesPerShot;
            Heat.Add(ship, Heat.D.PerRailSlug);
            stats.RailShots++;
            var d = Vec2.Distance(ship.Position, target.Position);
            var relSpeed = (targetFleet.Velocity - fleet.Velocity).Length;
            var evasion = Fitting.Spec(target).Evasion * (SupplyLines.Hungry(target) ? SupplyLines.HungryEvasion : 1);
            // Beyond normal reach a slug flies so long that only a ship holding its course gets hit.
            var chance = longShot
                ? rail.Accuracy * Heat.D.LongAccuracy * Math.Max(0, 1 - (d - railRange) / Math.Max(1e-9, Heat.D.LongRangeMm - railRange)) * (1 - evasion)
                : rail.Accuracy * (1 - d / railRange) * (1 - evasion) / (1 + relSpeed / 1000);
            chance *= FireControl.AimFactor(fleet);
            if (mode == FireMode.Spread) chance *= spread!.Accuracy;
            chance *= (bridge?.AccuracyFactor ?? 1) * Flagships.SteadyAim(ship, t1) * (SupplyLines.Hungry(ship) ? SupplyLines.HungryAccuracy : 1);
            if (Dodging(w, targetFleet))
            {
                chance = longShot ? 0 : chance * Flagships.DodgeFactor(targetFleet, DodgeFactor(targetFleet.Dodge, mode, SlugSeconds(d, fleet, targetFleet)));
                sim.BurnFuel(targetFleet, C.DodgeDeltaV * (targetFleet.Dodge == DodgeMode.HardBurn && spread != null ? spread.HardCost : 1));
                // The closer the shooter, the harder (and hotter) the jink that clears a slug.
                Heat.Add(target, Heat.D.PerDodge * Math.Clamp(railRange / Math.Max(d, 1e-6), 1, Heat.D.DodgeCloseMax));
                targetFleet.LastBurnHour = t1;
            }
            var hit = sim.Rng.NextDouble() < chance;
            if (!hit && w.Loop && Dodging(w, targetFleet)) Economy.Of(w, ship.Side).Totals.SlugsDodged++;
            // Misses are drawn landing a little off the target, on a fixed pattern (no extra dice).
            var off = side * (fan * 0.5 * DetMath.Sin(i * 2.1 + t1 * 977));
            sim.AddEffect(new CombatEffect(t1, hit ? EffectKind.RailHit : EffectKind.RailMiss, ship.Position, target.Position + (hit ? Vec2.Zero : off), ship.Side));
            if (hit)
            {
                stats.RailHits++;
                Damage(sim, battle, target, Tech.RailDamage(w, ship) * Wounds.WeaponFactor(ship), ship.Side, missile: false, engines: fleet.FireAt == FireAt.Engines);
                if (w.FindShip(target.Id) == null) break;
            }
        }
    }

    private static void LaunchSalvo(Simulation sim, Battle battle, Ship ship, Fleet fleet, double t1)
    {
        var w = sim.World;
        var m = C.Missile;
        var launch = Heat.LaunchRange(fleet);
        var target = (fleet.FireAt == FireAt.Flagship ? Flagships.FlagTarget(sim, ship, launch) : null)
                     ?? (fleet.FireAt is FireAt.Largest or FireAt.Weakest ? FireControl.Pick(sim, ship, fleet, launch) : null)
                     ?? SalvoTarget(w, ship, launch, fleet);
        if (target == null) return;
        target.LastTargetedHour = t1;
        battle.Action(t1);
        fleet.LastBurnHour = t1;
        var count = Math.Min(m.Salvo, ship.Missiles);
        ship.Missiles -= count;
        ship.SalvoReadyHour = t1 + m.SalvoReloadSeconds / 3600;
        battle.For(ship.Side).MissilesFired += count;
        for (var i = 0; i < count; i++)
        {
            // Spread the launch a little so the salvo does not fly as one point.
            var spread = new Vec2(sim.Rng.Range(-0.2, 0.2), sim.Rng.Range(-0.2, 0.2));
            w.Missiles.Add(new Missile
            {
                Id = w.NextId++, Side = ship.Side, TargetShipId = target.Id, FromShipId = ship.Id,
                Position = ship.Position + spread, Velocity = fleet.Velocity, DeltaVLeft = m.DeltaV,
                Brain = Math.Clamp(w.MissileBrain[(int)ship.Side], 1, 5), ExpiresHour = t1 + m.LifetimeHours,
            });
        }
        sim.Log.Info("combat", $"{ship.Name} fires {count} missiles at {target.Name}", new { shipId = ship.Id, targetId = target.Id, count });
    }

    /// <summary>How fast a missile likes to close on its target (Mm/h) before it stops burning.</summary>
    private const double MissileClosingSpeed = 400;

    private static void MoveMissiles(Simulation sim, Battle battle, double t0, double t1)
    {
        var w = sim.World;
        var m = C.Missile;
        var dt = t1 - t0;
        foreach (var missile in w.Missiles.ToList())
        {
            var stats = battle.For(missile.Side);
            var target = w.FindShip(missile.TargetShipId);
            // Brain 2 missiles sent at a guessed flagship turn onto the real one once it is confirmed.
            if (target != null && missile.Brain >= 2 && Flagships.Retarget(w, missile, target) is Ship real && real.Id != target.Id)
            {
                target = real;
                missile.TargetShipId = real.Id;
                sim.Log.Info("combat", "Missile turns onto the confirmed flagship", new { missileId = missile.Id, shipId = real.Id });
            }
            if (target == null)
            {
                target = EasiestTurn(w, missile);
                if (target != null) missile.TargetShipId = target.Id;
            }
            if (target == null || t1 >= missile.ExpiresHour)
            {
                stats.RanDry++;
                w.Missiles.Remove(missile);
                continue;
            }
            var targetFleet = w.FleetOf(target)!;
            var start = missile.Position;
            var targetStart = target.Position - targetFleet.Velocity * dt; // ships have already moved this step

            // Steering: aim the velocity relative to the target straight at it, closing at least
            // MissileClosingSpeed, and burn off any sideways drift. It only burns what the correction
            // needs, so a missile on course coasts in with fuel to spare. Brain 2 cancels all the drift;
            // brain 1 only half each step, so it wastes fuel and can miss a target that jinks across its path.
            var rel = targetStart - start;
            var relVel = missile.Velocity - targetFleet.Velocity;
            var toward = rel.Normalized();
            var along = Vec2.Dot(relVel, toward);
            var sideways = relVel - toward * along;
            var correction = missile.Brain >= 2 ? 1.0 : 0.5;
            var wanted = toward * Math.Max(along, MissileClosingSpeed) + sideways * (1 - correction);
            var change = wanted - relVel;
            var burn = Math.Min(Math.Min(m.AccelMmPerHour2 * dt, missile.DeltaVLeft), change.Length);
            missile.DeltaVLeft -= burn;
            var v0 = missile.Velocity;
            if (burn > 0) missile.Velocity = v0 + change.Normalized() * burn;
            missile.Position = start + (v0 + missile.Velocity) * (0.5 * dt);

            // Closest approach to the target during this step.
            var r0 = start - targetStart;
            var step = (missile.Position - start) - (target.Position - targetStart);
            var s = step.Length > 1e-12 ? Math.Clamp(-(r0.X * step.X + r0.Y * step.Y) / (step.X * step.X + step.Y * step.Y), 0, 1) : 0;
            if ((r0 + step * s).Length > m.HitRadiusMm) continue;

            w.Missiles.Remove(missile);
            ResolveMissile(sim, battle, missile, target, targetFleet, step.Length / dt);
        }
    }

    private static void ResolveMissile(Simulation sim, Battle battle, Missile missile, Ship target, Fleet targetFleet, double closingSpeed)
    {
        var w = sim.World;
        var stats = battle.For(missile.Side);
        var pd = C.PointDefence;

        // Point defence: every working mount in the target's fleet gets shots while the missile crosses its range.
        // Point defence covers fleet-mates close by fully, those further out of the formation less.
        var mounts = w.ShipsOf(targetFleet).Where(s => !s.Out(Module.PointDefence))
            .Sum(s => Fitting.Spec(s).PointDefence * (s.Overheated ? Heat.D.OverheatPdFactor : 1) * Wounds.DefenceFactor(s) * Formations.PdCover(s, target));
        var hoursInRange = pd.RangeMm / Math.Max(closingSpeed, 1);
        var shots = mounts * hoursInRange * 3600 / pd.ShotSeconds;
        var kill = pd.KillChance * (missile.Brain >= 2 ? pd.Brain2KillFactor : 1) * BrainBeyond2(missile)
                   * Tech.PointDefence(w, targetFleet.Side, missile.Brain)
                   * (FlagBridge(w, targetFleet)?.PointDefenceFactor ?? 1) * Flagships.PointDefenceFactor(w, targetFleet, target)
                   * (1 - Heat.D.HeldPdCut * Heat.FuelShare(missile) * Heat.FuelShare(missile)); // fuel to weave on the way in
        var survive = PowSurvive(1 - kill, shots);
        if (sim.Rng.NextDouble() >= survive)
        {
            stats.ShotDown++;
            if (w.Loop)
            {
                Economy.Of(w, missile.Side).Totals.MissilesStopped++;
                Economy.Of(w, targetFleet.Side).Totals.MissilesShotDown++;
            }
            var gunner = w.ShipsOf(targetFleet).Where(s => Fitting.Spec(s).PointDefence > 0 && !s.Out(Module.PointDefence))
                .OrderBy(s => Vec2.Distance(s.Position, missile.Position)).FirstOrDefault();
            sim.AddEffect(new CombatEffect(w.Hour, EffectKind.PointDefenceKill, gunner?.Position ?? target.Position, missile.Position, targetFleet.Side));
            return;
        }
        // A missile launched close still has most of its fuel at the end: it follows a dodge.
        var fuelShare = Heat.FuelShare(missile);
        var dodge = Fitting.Spec(target).Evasion * (missile.Brain >= 2 ? C.Brain2DodgeFactor : 1) * BrainBeyond2(missile)
                    * (1 - Heat.D.HeldDodgeCut * fuelShare * fuelShare);
        if (sim.Rng.NextDouble() < dodge)
        {
            stats.Dodged++;
            return;
        }
        // Spoofing: the target's jammers lead some missiles off (researched: a share of 1 - factor).
        if (Tech.Has(w, targetFleet.Side, Tech.Spoofing) && sim.Rng.NextDouble() < 1 - GameData.Research(Tech.Spoofing).Factor * BrainSpoofResist(missile))
        {
            stats.Dodged++;
            return;
        }
        // Jamming: a share of the missiles that get this far lose their target in the noise.
        if (w.Loop && Tech.Has(w, targetFleet.Side, "jamming") && sim.Rng.NextDouble() < 0.1)
        {
            stats.Dodged++;
            return;
        }
        stats.MissileHits++;
        sim.AddEffect(new CombatEffect(w.Hour, EffectKind.MissileHit, missile.Position, target.Position, missile.Side));
        Damage(sim, battle, target, Tech.MissileDamage(w, missile.Side), missile.Side, missile: true);
    }

    /// <summary>Missile brains 3 and 4 (research): each level past 2 cuts point defence kills and dodges by a further 15 %.</summary>
    public static double BrainBeyond2(Missile m) => m.Brain switch { >= 5 => 0.85 * 0.85 * 0.85, 4 => 0.85 * 0.85, 3 => 0.85, _ => 1 };

    /// <summary>Spoofing works less on smarter missiles: brain 3 and 4 are fooled less.</summary>
    private static double BrainSpoofResist(Missile m) => m.Brain switch { >= 4 => 1.1, 3 => 1.05, _ => 1 };

    /// <summary>base^exponent for a fractional exponent, using only exact operations (see DetMath).</summary>
    private static double PowSurvive(double b, double exponent)
    {
        var whole = (int)Math.Floor(exponent);
        var result = 1.0;
        for (var i = 0; i < whole; i++) result *= b;
        // The fractional part: blend linearly towards one more factor (close enough, and deterministic).
        var frac = exponent - whole;
        return result * (1 - frac * (1 - b));
    }

    private static void Damage(Simulation sim, Battle battle, Ship target, double amount, Side by, bool missile, bool engines = false)
    {
        var w = sim.World;
        if (w.FleetOf(target) is Fleet tf && Heat.RadiatorsOut(w, tf, target)) amount *= Heat.D.RadiatorsOutDamage;
        // A ship with its shield down close to a fleet-mate with shield emitters: that shield takes part of the hit.
        if (Formations.ShieldCover(w, target) is Ship cover)
        {
            var share = amount * Formations.D.ShieldShare;
            amount = amount - share + Heat.Absorb(cover, share, missile, battle);
        }
        amount = Heat.Absorb(target, amount, missile, battle);
        battle.For(by).DamageDealt += amount;
        if (w.Loop) Economy.Of(w, by).Totals.DamageDealt += amount;
        Wounds.Hit(sim, battle, target, amount, by, missile, engines);
    }

    public static string ModuleName(Module m) => m switch
    {
        Module.Engines => "engines",
        Module.Railguns => "railguns",
        Module.Missiles => "missile racks",
        _ => "point defence",
    };

    /// <summary>The flag bridge's help, if this fleet carries its side's flagship (light lag on only).</summary>
    public static FlagBridgeData? FlagBridge(World w, Fleet f) =>
        w.Light.On && C.FlagBridge != null && Light.Flagship(w, f.Side) is Ship flag && flag.FleetId == f.Id ? C.FlagBridge : null;

    /// <summary>A ship is gone: off its fleet, and if it was the flagship, command passes on (or the side loses).</summary>
    public static void Destroy(Simulation sim, Ship ship)
    {
        Stewards.OnLost(sim.World, ship);
        Wounds.LeaveWreck(sim.World, ship);
        if (sim.World.FleetOf(ship) is Fleet hauler && hauler.ShipIds.Count == 1 && sim.World.Shipments.Find(s => s.FleetId == hauler.Id) is Shipment load)
            Lanes.OnCargoLost(sim, hauler, load);
        RemoveShip(sim.World, ship);
        if (Economy.Of(sim.World, ship.Side).FlagshipId == ship.Id) Light.Succeed(sim, ship.Side, ship.Position);
    }

    private static void RemoveShip(World w, Ship ship)
    {
        w.Ships.Remove(ship);
        if (w.FleetOf(ship) is Fleet f)
        {
            f.ShipIds.Remove(ship.Id);
            if (f.ShipIds.Count == 0) w.Fleets.Remove(f);
        }
    }

    // ---- Contacts and battle records --------------------------------------------------------

    /// <summary>
    /// A battle where nothing has happened for an hour: the fleets are in contact but out of reach of
    /// each other's weapons, or out of ammunition. Time runs at normal speed until someone moves in.
    /// </summary>
    public static bool Standoff(World w) =>
        w.ActiveBattle is { } b && w.Missiles.Count == 0 && w.Hour - b.LastActionHour >= 1;

    /// <summary>Starts, updates or ends the battle from who is in contact with whom.</summary>
    public static void UpdateContacts(Simulation sim, double t1)
    {
        var w = sim.World;
        var inContact = new List<(Fleet A, Fleet B)>();
        // Contact needs someone to see someone: a fleet that slips in cold is not in the battle until spotted.
        foreach (var a in w.Fleets.Where(f => f.Side == Side.Player))
            foreach (var b in w.Fleets.Where(f => f.Side != Side.Player))
                if (Vec2.Distance(a.Position, b.Position) <= C.ContactRangeMm
                    && (Sensors.Sees(w, a.Side, b) || Sensors.Sees(w, b.Side, a)))
                    inContact.Add((a, b));

        var battle = w.ActiveBattle;
        if (battle == null && inContact.Count > 0)
        {
            battle = new Battle { Id = w.NextId++, StartHour = t1, LastActionHour = t1, FightingSinceHour = t1, Where = inContact[0].A.Position };
            w.Battles.Add(battle);
            if (w.Battles.Count > 20) w.Battles.RemoveAt(0);
            BattleRecords.Trim(w);
            foreach (var side in new[] { Side.Player, Side.Rival }) battle.For(side).LagHours = Light.DelayHours(w, side, battle.Where);
            sim.Log.Info("combat", $"Contact: {inContact[0].A.Name} and {inContact[0].B.Name}", new
            {
                battleId = battle.Id, a = inContact[0].A.Id, b = inContact[0].B.Id,
                distance = Vec2.Distance(inContact[0].A.Position, inContact[0].B.Position),
            });
        }
        if (battle == null) return;
        if (w.Missiles.Count > 0 || inContact.Any(p => p.A.Maneuvering || p.B.Maneuvering)) battle.Action(t1);
        foreach (var (a, b) in inContact)
        {
            foreach (var f in new[] { a, b })
                if (!battle.For(f.Side).Fleets.Contains(f.Name))
                {
                    battle.For(f.Side).Fleets.Add(f.Name);
                    battle.For(f.Side).ShipsIn += f.ShipIds.Count;
                    battle.For(f.Side).PowerIn += Stats.Power(w, f);
                    battle.For(f.Side).Makeup.AddRange(w.ShipsOf(f).Select(s => s.ClassId));
                    battle.For(f.Side).Rules.Add(f.Rule.Preset.ToString());
                    battle.For(f.Side).Formations.Add(Formations.Shape(w, f).ToString());
                    battle.For(f.Side).Missions.Add(f.Mission != Mission.None ? f.Mission.ToString() : Mind.Of(w, f.Side)?.JobOf(f.Id)?.Kind.ToString() ?? "None");
                }
            foreach (var f in new[] { a, b })
                if (!battle.For(f.Side).Flagship && Light.Flagship(w, f.Side) is Ship flag && flag.FleetId == f.Id)
                {
                    battle.For(f.Side).Flagship = true;
                    sim.Log.Info("combat", $"{f.Side} flagship in battle", new { battleId = battle.Id, fleetId = f.Id, shipId = flag.Id });
                }
        }
        if (inContact.Count == 0 && w.Missiles.Count == 0)
        {
            BattleRecords.Record(sim, battle, t1, force: true);
            battle.EndHour = t1;
            // A contact where nobody fired (or pulled back) is counted as a contact, not a battle.
            var fought = battle.Sides.Any(s => s.RailShots + s.MissilesFired > 0 || s.Withdrew);
            if (w.Loop)
                foreach (var side in new[] { Side.Player, Side.Rival })
                    if (battle.For(side).Fleets.Count > 0)
                    {
                        var t = Economy.Of(w, side).Totals;
                        t.Contacts++;
                        if (!fought) continue;
                        t.Battles++;
                        if (battle.For(side).LagHours > 5.0 / 60) t.LaggedBattles++;
                        if (battle.For(side).Flagship) t.FlagshipBattles++;
                    }
            var p = battle.For(Side.Player);
            var r = battle.For(Side.Rival);
            battle.Ending = BattleRules.Ending(w, battle);
            if (w.Loop && fought)
                foreach (var side in new[] { Side.Player, Side.Rival })
                    if (battle.For(side).Fleets.Count > 0)
                    {
                        var t = Economy.Of(w, side).Totals;
                        if (battle.Ending == BattleEnding.Wipe) t.BattlesWiped++;
                        else if (battle.Ending == BattleEnding.OneWithdrew) t.BattlesOneWithdrew++;
                        else if (battle.Ending == BattleEnding.BothWithdrew) t.BattlesBothWithdrew++;
                    }
            if (fought) Choices.OnBattleOver(w, battle);
            BattleRules.OnBattleOver(sim, battle);
            sim.Log.Info("combat", "Battle over", new
            {
                battleId = battle.Id, hours = t1 - battle.StartHour, ending = battle.Ending.ToString(),
                playerLost = p.ShipsLost.Count, rivalLost = r.ShipsLost.Count,
                playerCrippled = p.Crippled.Count, rivalCrippled = r.Crippled.Count,
                playerKnocked = p.Knocked.Count, rivalKnocked = r.Knocked.Count,
                playerWithdrew = p.Withdrew, rivalWithdrew = r.Withdrew,
                playerDamage = p.DamageDealt, rivalDamage = r.DamageDealt,
            });
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    public static Side Other(Side s) => s == Side.Player ? Side.Rival : Side.Player;

    /// <summary>Has the fleet a working weapon (railguns or loaded missile racks)?</summary>
    public static bool Armed(World w, Fleet f) =>
        w.ShipsOf(f).Any(s => Fitting.Spec(s) is var c && (c.Railguns > 0 && !s.Out(Module.Railguns) || s.Missiles > 0));

    /// <summary>The nearest enemy fleet this fleet's side can see, within range.</summary>
    public static Fleet? NearestEnemyFleet(World w, Fleet f, double range)
    {
        Fleet? best = null;
        var bestD = range;
        foreach (var o in w.Fleets)
        {
            if (o.Side == f.Side || !Sensors.Sees(w, f.Side, o)) continue;
            var d = Vec2.Distance(o.Position, f.Position);
            if (d <= bestD) { best = o; bestD = d; }
        }
        return best;
    }

    /// <summary>
    /// The ship a salvo goes for: the nearest one that the missiles already on their way would not finish
    /// off, so salvos spread over a fleet instead of all chasing one ship that dies before most arrive.
    /// When every ship in reach is already covered, the nearest one.
    /// </summary>
    private static Ship? SalvoTarget(World w, Ship ship, double range, Fleet fleet)
    {
        var nearest = NearestEnemyShip(w, ship, range, fleet);
        if (nearest == null) return null;
        var inbound = w.Missiles.Where(x => x.Side == ship.Side).GroupBy(x => x.TargetShipId).ToDictionary(g => g.Key, g => g.Count());
        var seen = w.Fleets.Where(f => f.Side != ship.Side && Sensors.Sees(w, ship.Side, f)).Select(f => f.Id).ToHashSet();
        Ship? best = null;
        var bestD = range;
        foreach (var s in w.Ships)
        {
            if (s.Side == ship.Side || !seen.Contains(s.FleetId) || !MayFireAt(w, fleet, s) || s.Crippled && !nearest.Crippled) continue;
            var d = Vec2.Distance(s.Position, ship.Position);
            if (d > bestD) continue;
            // Roughly a third of missiles get through point defence: enough inbound to kill it covers it.
            var coming = inbound.TryGetValue(s.Id, out var n) ? n : 0;
            if (coming * C.Missile.Damage / 3 >= s.Armour) continue;
            best = s;
            bestD = d;
        }
        return best ?? nearest;
    }

    /// <summary>A missile whose target is gone picks the ship it can turn onto most easily: ahead of it and close.</summary>
    private static Ship? EasiestTurn(World w, Missile missile)
    {
        var side = Other(missile.Side);
        var seen = w.Fleets.Where(f => f.Side == side && Sensors.Sees(w, missile.Side, f)).ToDictionary(f => f.Id);
        Ship? best = null;
        var bestCost = double.PositiveInfinity;
        foreach (var s in w.Ships)
        {
            if (s.Side != side || !seen.TryGetValue(s.FleetId, out var fleet)) continue;
            var gap = s.Position - missile.Position;
            var d = gap.Length;
            if (d > C.ContactRangeMm) continue;
            var relVel = missile.Velocity - fleet.Velocity;
            // Speed that has to be turned: the part of its closing velocity not aimed at this ship.
            var along = d > 1e-9 ? Vec2.Dot(relVel, gap) / d : 0;
            var sideways = Math.Sqrt(Math.Max(0, Vec2.Dot(relVel, relVel) - along * along)) + Math.Max(0, -along) * 2;
            var cost = sideways + d * 0.01;
            if (cost < bestCost) { best = s; bestCost = cost; }
        }
        return best;
    }

    /// <summary>The nearest enemy ship this fleet may fire at, within range: healthy ships before crippled ones.</summary>
    private static Ship? NearestEnemyShip(World w, Ship ship, double range, Fleet fleet)
    {
        Ship? best = null;
        var bestD = range;
        var bestCrippled = true;
        var side = Other(ship.Side);
        var seen = w.Fleets.Where(f => f.Side == side && Sensors.Sees(w, ship.Side, f)).Select(f => f.Id).ToHashSet();
        foreach (var s in w.Ships)
        {
            if (s.Side != side || !seen.Contains(s.FleetId) || !MayFireAt(w, fleet, s)) continue;
            var d = Vec2.Distance(s.Position, ship.Position);
            if (d > range) continue;
            if (best == null || (bestCrippled && !s.Crippled) || (s.Crippled == bestCrippled && d <= bestD)) { best = s; bestD = d; bestCrippled = s.Crippled; }
        }
        return best;
    }

    /// <summary>
    /// Whether a fleet's guns may go for this ship. Breaking off is mutual: a fleet pulling back holds its
    /// fire and is let go, unless one of the two is chasing the other (or the shooter is set to finish off).
    /// </summary>
    /// <summary>A group this small disengages more easily (see MayFireAt) and is what raiders send out.</summary>
    public const int SmallGroup = 2;

    public static bool MayFireAt(World w, Fleet shooter, Ship target)
    {
        if (w.FleetOf(target) is not Fleet them) return true;
        // A pulling-back fleet is let go unless chased, or finished off; one or two ships slip away even then, unless chased.
        if (them.Retreating && shooter.Chase?.TargetFleetId != them.Id && (shooter.FireAt != FireAt.Weakest || them.ShipIds.Count <= SmallGroup)) return false;
        if (shooter.Retreating && them.Chase?.TargetFleetId != shooter.Id) return false;
        return true;
    }

    public static bool HasWorking(World w, Fleet f, Module m) => w.ShipsOf(f).Any(s => !s.Out(m) && m switch
    {
        Module.Railguns => Fitting.Spec(s).Railguns > 0,
        Module.Missiles => s.Missiles > 0,
        Module.PointDefence => Fitting.Spec(s).PointDefence > 0,
        _ => true,
    });

    /// <summary>The fleet's armour left, as a share of full (0..1).</summary>
    public static double ArmourShare(World w, Fleet f)
    {
        double have = 0, full = 0;
        foreach (var s in w.ShipsOf(f))
        {
            have += Math.Max(0, s.Armour);
            full += Tech.MaxArmour(w, s);
        }
        return full > 0 ? have / full : 1;
    }

    /// <summary>
    /// At the shipyard or a dry dock: hull and the other damage bars mend (for materials: see Wounds.YardRepair), knocked-out parts are fixed, missiles are reloaded. A
    /// missile takes one ammunition from the planet's store (or, with none, supplies at half the pace).
    /// </summary>
    public static void RepairAtHome(World w, Fleet f, double hours, Body? at = null)
    {
        foreach (var s in w.ShipsOf(f))
        {
            Wounds.YardRepair(w, s, hours, at);
            Heat.Restock(s);
            if (s.Missiles >= Tech.Racks(w, s) || w.Hour % 6 >= hours) continue;
            if (at != null && Colonies.Held(at, "ammunition") >= 1)
            {
                at.Stock["ammunition"] = Colonies.Held(at, "ammunition") - 1;
                s.Missiles++;
            }
            else if (s.Supplies >= 1 && w.Hour % 12 < hours)
            {
                s.Missiles++;
                s.Supplies -= 1;
            }
        }
        if (ArmourShare(w, f) >= 0.99 && !w.ShipsOf(f).Any(s => s.Crippled)) f.Retreating = false;
    }
}
