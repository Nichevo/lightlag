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
    public double DamageDealt { get; set; }
    public List<string> ShipsLost { get; set; } = new();
    public List<string> Knocked { get; set; } = new();   // "FF-1: railguns"
    public List<string> Fleets { get; set; } = new();
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

    public BattleSide For(Side s) => Sides[(int)s];
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
        if (w.Missiles.Count > 0) return true;
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
        var dt = t1 - t0;
        foreach (var f in w.Fleets)
        {
            var enemy = NearestEnemyFleet(w, f, C.ContactRangeMm);
            if (enemy != null && f.RetreatWhenDamaged && !f.Retreating && ArmourShare(w, f) < C.RetreatBelowArmour)
                Retreat(sim, f);

            var wantsToClose = enemy != null && WouldEngage(w, f, enemy);
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
                desired = Vec2.Zero; // nothing to chase: brake to a stop
            }
            var change = desired - f.Velocity;
            var maxChange = perf.Accel * dt;
            if (change.Length > maxChange) change = change.Normalized() * maxChange;
            var cost = change.Length;
            if (cost > perf.DeltaVLeft) change = change.Normalized() * Math.Max(0, perf.DeltaVLeft);
            f.Velocity += change;
            sim.BurnFuel(f, change.Length);
            if (!wantsToClose && f.Velocity.Length < 1e-6)
            {
                f.Velocity = Vec2.Zero;
                f.Maneuvering = false;
            }
        }
    }

    /// <summary>Is this fleet dodging railgun fire now? (Set to dodge, and supplies not yet low.)</summary>
    public static bool Dodging(World w, Fleet f) => f.Dodge != DodgeMode.Off && C.DodgeFactor < 1 && SupplyShare(w, f) >= C.DodgeStopsBelow;

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
        foreach (var s in w.ShipsOf(f)) { have += s.Supplies; full += GameData.ShipClass(s.ClassId).Supplies; }
        return full > 0 ? have / full : 0;
    }

    /// <summary>
    /// Would this fleet close in to fight that enemy? (Engage stance, sees it, has railguns and fuel, isn't
    /// retreating.) An Engage fleet breaks off a flight to fight; a Hold fleet keeps its course.
    /// </summary>
    public static bool WouldEngage(World w, Fleet f, Fleet enemy) =>
        f.Stance == Stance.Engage && !f.Retreating && Chases.LetsEngage(f)
        && Vec2.Distance(f.Position, enemy.Position) <= C.ContactRangeMm && Sensors.Sees(w, f.Side, enemy)
        && HasWorking(w, f, Module.Railguns) && FleetPerformance.Of(w, f).DeltaVLeft > 1e-3;

    private static void Retreat(Simulation sim, Fleet f)
    {
        var w = sim.World;
        f.Retreating = true;
        f.Maneuvering = false;
        f.Chase = null;
        var home = Economy.HomeOf(w, f.Side);
        if (home == null) return;
        var req = new FlightRequest(home.Id, home.Position, 1.0, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick);
        var plan = FlightPlanner.Plan(w, f, req);
        if (!plan.Ok)
        {
            // Not enough fuel for a full-speed run: try slower.
            plan = FlightPlanner.Plan(w, f, req with { SpeedFraction = 0.3 });
        }
        if (plan is { Ok: true, Flight: Flight flight })
        {
            f.Flight = flight;
            sim.Log.Info("combat", $"{f.Name} is badly damaged and retreats to {home.Name}", new { fleetId = f.Id, armour = ArmourShare(w, f) });
        }
        else
        {
            sim.Log.Info("combat", $"{f.Name} is badly damaged but has no fuel to retreat", new { fleetId = f.Id });
        }
    }

    // ---- Firing (after fleets move) ------------------------------------------------------

    public static void Fire(Simulation sim, double t0, double t1)
    {
        var w = sim.World;
        UpdateContacts(sim, t1);
        var battle = w.ActiveBattle;
        if (battle == null) return;

        foreach (var ship in w.Ships.ToList())
        {
            if (w.FleetOf(ship) is not Fleet fleet) continue;
            var cls = GameData.ShipClass(ship.ClassId);
            if (cls.Railguns > 0 && ship.Disabled != Module.Railguns && t1 >= ship.RailsReadyHour)
                FireRailguns(sim, battle, ship, fleet, cls, t1);
            if (ship.Missiles > 0 && ship.Disabled != Module.Missiles && t1 >= ship.SalvoReadyHour)
                LaunchSalvo(sim, battle, ship, fleet, t1);
        }

        MoveMissiles(sim, battle, t0, t1);
    }

    private static void FireRailguns(Simulation sim, Battle battle, Ship ship, Fleet fleet, ShipClass cls, double t1)
    {
        var w = sim.World;
        var rail = C.Railgun;
        var target = NearestEnemyShip(w, ship, rail.RangeMm);
        if (target == null) return;
        var targetFleet = w.FleetOf(target)!;
        var stats = battle.For(ship.Side);
        battle.Action(t1);
        fleet.LastBurnHour = t1; // firing heats the ships up: they stay easier to see for a while
        AutoTactics(w, fleet, targetFleet);
        var spread = C.Spread;
        var mode = spread != null ? fleet.Fire : FireMode.Focused;
        var slugs = cls.Railguns * (mode == FireMode.Spread ? spread!.SlugsPerGun : 1);
        var cooldown = mode == FireMode.Spread ? spread!.Cooldown : 1; // a burst heats the guns: they wait longer
        ship.RailsReadyHour = t1 + rail.ReloadSeconds * cooldown * Tech.Factor(w, ship.Side, Tech.FastRailguns) / 3600;
        fleet.LastVolley = mode;
        fleet.LastVolleyHour = t1;
        var fan = spread == null ? 0.6 : mode == FireMode.Spread ? spread.FanMm : spread.FocusFanMm;
        sim.AddEffect(new CombatEffect(t1, EffectKind.Volley, ship.Position, target.Position, ship.Side, fan));
        var side = (target.Position - ship.Position).Normalized();
        side = new Vec2(-side.Y, side.X);
        for (var i = 0; i < slugs; i++)
        {
            if (ship.Supplies < rail.SuppliesPerShot) break;
            ship.Supplies -= rail.SuppliesPerShot;
            stats.RailShots++;
            var d = Vec2.Distance(ship.Position, target.Position);
            var relSpeed = (targetFleet.Velocity - fleet.Velocity).Length;
            var evasion = GameData.ShipClass(target.ClassId).Evasion;
            var chance = rail.Accuracy * (1 - d / rail.RangeMm) * (1 - evasion) / (1 + relSpeed / 1000);
            if (mode == FireMode.Spread) chance *= spread!.Accuracy;
            if (Dodging(w, targetFleet))
            {
                chance *= DodgeFactor(targetFleet.Dodge, mode, SlugSeconds(d, fleet, targetFleet));
                sim.BurnFuel(targetFleet, C.DodgeDeltaV * (targetFleet.Dodge == DodgeMode.HardBurn && spread != null ? spread.HardCost : 1));
                targetFleet.LastBurnHour = t1;
            }
            var hit = sim.Rng.NextDouble() < chance;
            // Misses are drawn landing a little off the target, on a fixed pattern (no extra dice).
            var off = side * (fan * 0.5 * DetMath.Sin(i * 2.1 + t1 * 977));
            sim.AddEffect(new CombatEffect(t1, hit ? EffectKind.RailHit : EffectKind.RailMiss, ship.Position, target.Position + (hit ? Vec2.Zero : off), ship.Side));
            if (hit)
            {
                stats.RailHits++;
                Damage(sim, battle, target, rail.Damage, ship.Side);
                if (w.FindShip(target.Id) == null) break;
            }
        }
    }

    private static void LaunchSalvo(Simulation sim, Battle battle, Ship ship, Fleet fleet, double t1)
    {
        var w = sim.World;
        var m = C.Missile;
        var target = NearestEnemyShip(w, ship, m.LaunchRangeMm);
        if (target == null) return;
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
                Brain = Math.Clamp(w.MissileBrain[(int)ship.Side], 1, 2), ExpiresHour = t1 + m.LifetimeHours,
            });
        }
        sim.Log.Info("combat", $"{ship.Name} fires {count} missiles at {target.Name}", new { shipId = ship.Id, targetId = target.Id, count });
    }

    private static void MoveMissiles(Simulation sim, Battle battle, double t0, double t1)
    {
        var w = sim.World;
        var m = C.Missile;
        var dt = t1 - t0;
        foreach (var missile in w.Missiles.ToList())
        {
            var stats = battle.For(missile.Side);
            var target = w.FindShip(missile.TargetShipId);
            if (target == null)
            {
                target = NearestShipOfSide(w, Other(missile.Side), missile.Position, C.ContactRangeMm);
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

            // Steering: aim the velocity relative to the target straight at it, burning off any sideways
            // drift. Brain 2 does this fully; brain 1 only corrects half the drift each step, so it wastes
            // fuel and can miss a target that jinks across its path.
            var rel = targetStart - start;
            var relVel = missile.Velocity - targetFleet.Velocity;
            var closing = Math.Max(400, relVel.Length);
            var correction = missile.Brain >= 2 ? 1.0 : 0.5;
            var dir = (rel.Normalized() * closing - relVel * correction).Normalized();
            var burn = Math.Min(m.AccelMmPerHour2 * dt, missile.DeltaVLeft);
            missile.DeltaVLeft -= burn;
            var v0 = missile.Velocity;
            missile.Velocity = v0 + dir * burn;
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
        var mounts = w.ShipsOf(targetFleet).Where(s => s.Disabled != Module.PointDefence)
            .Sum(s => GameData.ShipClass(s.ClassId).PointDefence);
        var hoursInRange = pd.RangeMm / Math.Max(closingSpeed, 1);
        var shots = mounts * hoursInRange * 3600 / pd.ShotSeconds;
        var kill = pd.KillChance * (missile.Brain >= 2 ? pd.Brain2KillFactor : 1) * Tech.Factor(w, targetFleet.Side, Tech.PointDefence2);
        var survive = PowSurvive(1 - kill, shots);
        if (sim.Rng.NextDouble() >= survive)
        {
            stats.ShotDown++;
            var gunner = w.ShipsOf(targetFleet).Where(s => GameData.ShipClass(s.ClassId).PointDefence > 0 && s.Disabled != Module.PointDefence)
                .OrderBy(s => Vec2.Distance(s.Position, missile.Position)).FirstOrDefault();
            sim.AddEffect(new CombatEffect(w.Hour, EffectKind.PointDefenceKill, gunner?.Position ?? target.Position, missile.Position, targetFleet.Side));
            return;
        }
        var dodge = GameData.ShipClass(target.ClassId).Evasion * (missile.Brain >= 2 ? C.Brain2DodgeFactor : 1);
        if (sim.Rng.NextDouble() < dodge)
        {
            stats.Dodged++;
            return;
        }
        stats.MissileHits++;
        sim.AddEffect(new CombatEffect(w.Hour, EffectKind.MissileHit, missile.Position, target.Position, missile.Side));
        Damage(sim, battle, target, C.Missile.Damage, missile.Side);
    }

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

    private static void Damage(Simulation sim, Battle battle, Ship target, double amount, Side by)
    {
        var w = sim.World;
        target.Armour -= amount;
        battle.For(by).DamageDealt += amount;
        if (w.Loop) Economy.Of(w, by).Totals.DamageDealt += amount;
        var lost = battle.For(target.Side);
        if (target.Armour <= 0)
        {
            lost.ShipsLost.Add(target.Name);
            if (w.Loop)
            {
                Economy.Of(w, target.Side).Totals.ShipsLost++;
                Economy.Of(w, by).Totals.ShipsKilled++;
            }
            sim.Log.Info("combat", $"{target.Name} is destroyed", new { shipId = target.Id, by = by.ToString() });
            RemoveShip(w, target);
            return;
        }
        if (target.Disabled == null && sim.Rng.NextDouble() < C.ModuleHitChance)
        {
            var cls = GameData.ShipClass(target.ClassId);
            var parts = new List<Module> { Module.Engines, Module.PointDefence };
            if (cls.Railguns > 0) parts.Add(Module.Railguns);
            if (cls.Missiles > 0) parts.Add(Module.Missiles);
            target.Disabled = parts[sim.Rng.Range(0, parts.Count)];
            lost.Knocked.Add($"{target.Name}: {ModuleName(target.Disabled.Value)}");
            sim.Log.Info("combat", $"{target.Name} loses its {ModuleName(target.Disabled.Value)}", new { shipId = target.Id, module = target.Disabled });
        }
    }

    public static string ModuleName(Module m) => m switch
    {
        Module.Engines => "engines",
        Module.Railguns => "railguns",
        Module.Missiles => "missile racks",
        _ => "point defence",
    };

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
            if (!battle.For(a.Side).Fleets.Contains(a.Name)) battle.For(a.Side).Fleets.Add(a.Name);
            if (!battle.For(b.Side).Fleets.Contains(b.Name)) battle.For(b.Side).Fleets.Add(b.Name);
        }
        if (inContact.Count == 0 && w.Missiles.Count == 0)
        {
            battle.EndHour = t1;
            if (w.Loop)
                foreach (var side in new[] { Side.Player, Side.Rival })
                    if (battle.For(side).Fleets.Count > 0) Economy.Of(w, side).Totals.Battles++;
            var p = battle.For(Side.Player);
            var r = battle.For(Side.Rival);
            sim.Log.Info("combat", "Battle over", new
            {
                battleId = battle.Id, hours = t1 - battle.StartHour,
                playerLost = p.ShipsLost.Count, rivalLost = r.ShipsLost.Count,
                playerDamage = p.DamageDealt, rivalDamage = r.DamageDealt,
            });
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    public static Side Other(Side s) => s == Side.Player ? Side.Rival : Side.Player;

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

    private static Ship? NearestEnemyShip(World w, Ship ship, double range) =>
        NearestShipOfSide(w, Other(ship.Side), ship.Position, range);

    /// <summary>The nearest ship of a side that the other side can see, within range.</summary>
    private static Ship? NearestShipOfSide(World w, Side side, Vec2 at, double range)
    {
        Ship? best = null;
        var bestD = range;
        var seen = w.Fleets.Where(f => f.Side == side && Sensors.Sees(w, Other(side), f)).Select(f => f.Id).ToHashSet();
        foreach (var s in w.Ships)
        {
            if (s.Side != side || !seen.Contains(s.FleetId)) continue;
            var d = Vec2.Distance(s.Position, at);
            if (d <= bestD) { best = s; bestD = d; }
        }
        return best;
    }

    public static bool HasWorking(World w, Fleet f, Module m) => w.ShipsOf(f).Any(s => s.Disabled != m && m switch
    {
        Module.Railguns => GameData.ShipClass(s.ClassId).Railguns > 0,
        Module.Missiles => s.Missiles > 0,
        Module.PointDefence => GameData.ShipClass(s.ClassId).PointDefence > 0,
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

    /// <summary>At home: armour mends, knocked-out parts are fixed, missiles are reloaded (costing supplies).</summary>
    public static void RepairAtHome(World w, Fleet f, double hours)
    {
        foreach (var s in w.ShipsOf(f))
        {
            var cls = GameData.ShipClass(s.ClassId);
            var max = Tech.MaxArmour(w, s);
            s.Armour = Math.Min(max, s.Armour + max * C.RepairPerDay * hours / 24);
            if (s.Armour >= max * 0.5) s.Disabled = null;
            if (s.Missiles < cls.Missiles && s.Supplies >= 1 && w.Hour % 6 < hours)
            {
                s.Missiles++;
                s.Supplies -= 1;
            }
        }
        if (ArmourShare(w, f) >= 0.99) f.Retreating = false;
    }
}
