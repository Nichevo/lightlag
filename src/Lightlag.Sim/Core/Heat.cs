using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// Where a fleet's radiators are. Auto: out, but pulled in for battle (radiators are fragile and
/// glow on sensors). Out: always out (cool, but hits do more harm in battle). Cold: always in, to stay
/// hidden: the signature drops, but heat builds and the ship must vent when it is full (the cold clock).
/// </summary>
public enum RadiatorMode { Auto, Out, Cold }

/// <summary>When destroyers launch their missiles: as soon as a target is in reach, or held until closer (harder to dodge).</summary>
public enum MissileLaunch { Far, Mid, Close }

/// <summary>Heat, shields and the newer fire-control numbers (ship-classes.json, "heat"). See the comment there.</summary>
public sealed record HeatData(
    double IdlePerHour = 3, double BurnPerHour = 30, double PerRailSlug = 1.5, double PerDodge = 0.6, double DodgeCloseMax = 4,
    double RadiateOutPerHour = 45, double RadiateInPerHour = 1.5, double RecoverAt = 0.7, double SinkHeat = 40, double AutoSinkAt = 0.9,
    double DecoyMm = 15, double DecoyChance = 0.35, double DecoyBrain2Chance = 0.15, double ColdSignature = 0.4,
    double RadiatorsOutDamage = 1.15, double OverheatPdFactor = 0.5,
    double ShieldRegenPerHour = 0.3, double ShieldRailFactor = 0.5, double HeatPerShieldPoint = 0.3,
    double LongRangeMm = 100, double LongAccuracy = 0.12, double LongReloadFactor = 2,
    double EnginesHitFactor = 0.85, double EnginesModuleChance = 0.45,
    double MidLaunchMm = 40, double CloseLaunchMm = 25, double HeldDodgeCut = 0.6, double HeldPdCut = 0.4, double HeldDecoyCut = 0.5,
    double DropWaitHoursMax = 48, double DropSeekMm = 40, double DropLifetimeHours = 0.5);

/// <summary>
/// Heat. Every ship has a heat budget beside its power: reactors and crew make a little all the time,
/// main engines a lot, railgun slugs and dodging some, and a shield soaking a hit some more. Radiators
/// out shed it fast; pulled in (in battle, or running cold to hide) they shed little, so heat builds. A ship
/// past its capacity overheats: radiators are forced out, railguns and shields go off and point defence
/// fires at half rate until it has cooled to <see cref="HeatData.RecoverAt"/>. Heat sinks dump heat at once
/// and, thrown overboard hot, can pull missiles off course (decoys).
/// </summary>
public static class Heat
{
    public static HeatData D => GameData.Heat;

    public static double Capacity(Ship s) => GameData.ShipClass(s.ClassId).HeatCapacity;

    public static double MaxShield(World w, Ship s) => GameData.ShipClass(s.ClassId).Shield * Tech.Factor(w, s.Side, Tech.DeflectorGrid) * Tech.Factor(w, s.Side, Tech.ShieldsMk3);

    /// <summary>Is this fleet in the battle being fought (so Auto radiators are in)?</summary>
    public static bool InBattle(World w, Fleet f) => w.ActiveBattle is Battle b && b.For(f.Side).Fleets.Contains(f.Name);

    /// <summary>Are this ship's radiators out right now?</summary>
    public static bool RadiatorsOut(World w, Fleet f, Ship s) =>
        s.Overheated || f.Radiators == RadiatorMode.Out || f.Radiators == RadiatorMode.Auto && !InBattle(w, f);

    /// <summary>A fleet running cold with every radiator in looks smaller on enemy sensors.</summary>
    public static double SignatureFactor(World w, Fleet f) =>
        f.Radiators == RadiatorMode.Cold && w.ShipsOf(f).All(s => !s.Overheated) ? D.ColdSignature : 1;

    /// <summary>Heat gained per hour with radiators in and engines off (the cold clock), less what it sheds.</summary>
    public static double ColdNetPerHour => D.IdlePerHour - D.RadiateInPerHour;

    /// <summary>Hours until the hottest ship of a fleet running cold has to vent (null: not running cold, or never).</summary>
    public static double? ColdClockHours(World w, Fleet f)
    {
        if (f.Radiators != RadiatorMode.Cold) return null;
        var net = ColdNetPerHour + (f.Thrusting ? D.BurnPerHour : 0);
        if (net <= 0) return null;
        double? least = null;
        foreach (var s in w.ShipsOf(f))
        {
            var h = Math.Max(0, Capacity(s) - s.Heat) / net;
            least = least is double l ? Math.Min(l, h) : h;
        }
        return least;
    }

    /// <summary>Average heat of a fleet as a share of capacity.</summary>
    public static double Share(World w, Fleet f)
    {
        double heat = 0, cap = 0;
        foreach (var s in w.ShipsOf(f)) { heat += s.Heat; cap += Capacity(s); }
        return cap > 0 ? heat / cap : 0;
    }

    /// <summary>Runs every step: heat in and out, overheating and cooling down, shields recharging, automatic heat sinks.</summary>
    public static void Update(Simulation sim, double dt)
    {
        var w = sim.World;
        if (dt <= 0) return;
        foreach (var f in w.Fleets)
        {
            foreach (var s in w.ShipsOf(f))
            {
                var cap = Capacity(s);
                if (cap <= 0) continue;
                var gain = D.IdlePerHour + (f.Thrusting ? D.BurnPerHour : 0);
                var shed = RadiatorsOut(w, f, s) ? D.RadiateOutPerHour : D.RadiateInPerHour;
                s.Heat = Math.Max(0, s.Heat + (gain - shed) * dt);
                if (!s.Overheated && s.Heat > cap)
                {
                    s.Overheated = true;
                    if (InBattle(w, f) && w.ActiveBattle is Battle b) BattleRecords.Mark(w, b, f.Side, MarkKind.Decision, $"{s.Name} overheats: guns and shields off, radiators out");
                    if (w.Loop) Economy.Of(w, f.Side).Totals.Overheats++;
                    sim.Log.Info("heat", $"{s.Name} overheats", new { shipId = s.Id, heat = s.Heat, cap, radiators = f.Radiators.ToString() });
                }
                else if (s.Overheated && s.Heat <= cap * D.RecoverAt)
                {
                    s.Overheated = false;
                    sim.Log.Info("heat", $"{s.Name} has cooled down", new { shipId = s.Id, heat = s.Heat });
                }
                var max = MaxShield(w, s);
                if (!s.Overheated && s.Shield < max) s.Shield = Math.Min(max, s.Shield + max * D.ShieldRegenPerHour * dt);
                if (s.Overheated) s.Shield = 0;
                if (f.AutoSinks && s.HeatSinks > 0 && s.Heat >= cap * D.AutoSinkAt) EjectSink(sim, f, s, "runs hot");
            }
            // Missiles closing on the fleet: a hot sink thrown out may draw them off.
            if (f.AutoSinks && InBattle(w, f))
                foreach (var s in w.ShipsOf(f))
                    if (s.HeatSinks > 0 && w.Missiles.Any(m => m.Side != f.Side && m.TargetShipId == s.Id && Vec2.Distance(m.Position, s.Position) <= D.DecoyMm))
                        EjectSink(sim, f, s, "throws a decoy at incoming missiles");
        }
    }

    /// <summary>Adds heat to a ship (railgun slugs, dodging, a shield soaking a hit).</summary>
    public static void Add(Ship s, double heat) => s.Heat = Math.Max(0, s.Heat + heat);

    /// <summary>
    /// Throws one heat sink overboard: the ship sheds its heat at once, and missiles closing on it may
    /// chase the hot sink instead (brain 2 missiles are harder to fool).
    /// </summary>
    public static void EjectSink(Simulation sim, Fleet f, Ship s, string why)
    {
        var w = sim.World;
        if (s.HeatSinks <= 0) return;
        s.HeatSinks--;
        s.Heat = Math.Max(0, s.Heat - D.SinkHeat);
        var fooled = 0;
        foreach (var m in w.Missiles.ToList())
        {
            if (m.Side == f.Side || m.TargetShipId != s.Id || Vec2.Distance(m.Position, s.Position) > D.DecoyMm) continue;
            // A missile with fuel left can turn back onto the ship once the sink falls behind.
            var chance = (m.Brain >= 2 ? D.DecoyBrain2Chance : D.DecoyChance) * (1 - D.HeldDecoyCut * FuelShare(m));
            if (sim.Rng.NextDouble() >= chance) continue;
            w.Missiles.Remove(m);
            fooled++;
            if (w.ActiveBattle is Battle b) b.For(m.Side).Decoyed++;
            sim.AddEffect(new CombatEffect(w.Hour, EffectKind.PointDefenceKill, s.Position, m.Position, f.Side));
        }
        if (fooled > 0 && w.ActiveBattle is Battle battle) BattleRecords.Mark(w, battle, f.Side, MarkKind.Decision, $"{s.Name}'s heat sink draws off {fooled} missile{(fooled == 1 ? "" : "s")}");
        sim.Log.Info("heat", $"{s.Name} ejects a heat sink", new { shipId = s.Id, why, left = s.HeatSinks, fooled });
    }

    /// <summary>
    /// The shield layer, between point defence and armour: it soaks missile damage in full and railgun
    /// slugs in part (ShieldRailFactor), heating the ship for every point it takes. Returns what gets through.
    /// </summary>
    public static double Absorb(Ship s, double amount, bool missile, Battle battle)
    {
        if (s.Shield <= 0 || s.Overheated) return amount;
        var soakable = missile ? amount : amount * D.ShieldRailFactor;
        var soaked = Math.Min(s.Shield, soakable);
        s.Shield -= soaked;
        Add(s, soaked * D.HeatPerShieldPoint);
        battle.For(s.Side).ShieldSoaked += soaked;
        return amount - soaked;
    }

    /// <summary>A fresh ship (built or set up): full shield, sinks loaded, cool.</summary>
    public static void Fit(World w, Ship s)
    {
        var cls = GameData.ShipClass(s.ClassId);
        s.Shield = MaxShield(w, s);
        s.HeatSinks = cls.HeatSinks;
        s.Heat = 0;
    }

    /// <summary>At home: heat sinks are refilled.</summary>
    public static void Restock(Ship s) => s.HeatSinks = Math.Max(s.HeatSinks, GameData.ShipClass(s.ClassId).HeatSinks);

    /// <summary>A missile's fuel left as a share of a full load (held missiles arrive with more).</summary>
    public static double FuelShare(Missile m) => GameData.Combat.Missile.DeltaV > 0 ? Math.Clamp(m.DeltaVLeft / GameData.Combat.Missile.DeltaV, 0, 1) : 0;

    /// <summary>The range at which a destroyer in this fleet launches (held missiles wait until closer).</summary>
    public static double LaunchRange(Fleet f) => f.MissileLaunch switch
    {
        MissileLaunch.Mid => Math.Min(D.MidLaunchMm, GameData.Combat.Missile.LaunchRangeMm),
        MissileLaunch.Close => Math.Min(D.CloseLaunchMm, GameData.Combat.Missile.LaunchRangeMm),
        _ => GameData.Combat.Missile.LaunchRangeMm,
    };
}

/// <summary>Missiles dropped to drift dark until their timer runs out, then wake and go for the nearest enemy in reach.</summary>
public static class DroppedMissiles
{
    private static HeatData D => GameData.Heat;

    /// <summary>Every step: dropped missiles drift; once awake they look for a target within DropSeekMm, and launch at it.</summary>
    public static void Update(Simulation sim, double dt)
    {
        var w = sim.World;
        if (w.Dropped.Count == 0) return;
        foreach (var m in w.Dropped.ToList())
        {
            m.Position += m.Velocity * dt;
            if (w.Hour >= m.ExpiresHour)
            {
                w.Dropped.Remove(m);
                sim.Log.Info("combat", "A dropped missile runs out of time", new { missileId = m.Id, side = m.Side.ToString() });
                continue;
            }
            if (w.Hour < m.WakeHour) continue;
            Ship? best = null;
            var bestD = D.DropSeekMm;
            foreach (var s in w.Ships)
            {
                if (s.Side == m.Side) continue;
                var d = Vec2.Distance(s.Position, m.Position);
                if (d < bestD) { best = s; bestD = d; }
            }
            if (best == null) continue;
            w.Dropped.Remove(m);
            m.TargetShipId = best.Id;
            m.ExpiresHour = w.Hour + D.DropLifetimeHours + GameData.Combat.Missile.LifetimeHours;
            w.Missiles.Add(m);
            sim.Log.Info("combat", "A dropped missile wakes and goes for a ship", new { missileId = m.Id, side = m.Side.ToString(), shipId = best.Id, distance = bestD });
        }
    }

    /// <summary>Could a dropped missile wake and find a target within the next hour? (Then the hour runs in battle time.)</summary>
    public static bool MayWake(World w)
    {
        foreach (var m in w.Dropped)
        {
            if (m.WakeHour > w.Hour + 1) continue;
            foreach (var f in w.Fleets)
                if (f.Side != m.Side && Vec2.Distance(f.Position, m.Position) <= D.DropSeekMm + f.Velocity.Length + m.Velocity.Length + 1)
                    return true;
        }
        return false;
    }
}

/// <summary>Radiators and automatic heat sinks for a fleet.</summary>
public sealed record SetHeatCommand(int FleetId, RadiatorMode Radiators, bool AutoSinks) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Heat") is not Fleet fleet) return;
        fleet.Radiators = Radiators;
        fleet.AutoSinks = AutoSinks;
        sim.Log.Info("fleet", $"{fleet.Name} radiators {Radiators}", new { FleetId, Radiators, AutoSinks, Source });
    }
}

/// <summary>Every ship in the fleet that has one throws a heat sink overboard now.</summary>
public sealed record EjectHeatSinksCommand(int FleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Heat sinks") is not Fleet fleet) return;
        foreach (var s in world.ShipsOf(fleet).ToList()) Heat.EjectSink(sim, fleet, s, "ordered");
    }
}

/// <summary>Long-range railgun fire on or off, and when destroyers launch their missiles.</summary>
public sealed record SetFireControlCommand(int FleetId, bool LongShots, MissileLaunch Launch) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Fire control") is not Fleet fleet) return;
        fleet.LongShots = LongShots;
        fleet.MissileLaunch = Launch;
        sim.Log.Info("fleet", $"{fleet.Name} long shots {LongShots}, launch {Launch}", new { FleetId, LongShots, Launch, Source });
    }
}

/// <summary>
/// Each destroyer in the fleet drops one salvo to drift dark: the missiles wake after WaitHours and go
/// for the first enemy ship that passes within reach, until DropWaitHoursMax runs out.
/// </summary>
public sealed record DropMissilesCommand(int FleetId, double WaitHours) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Drop missiles") is not Fleet fleet) return;
        var d = GameData.Heat;
        var m = GameData.Combat.Missile;
        var wait = Math.Clamp(WaitHours, 0, d.DropWaitHoursMax);
        var count = 0;
        foreach (var ship in world.ShipsOf(fleet).ToList())
        {
            if (ship.Missiles <= 0 || ship.Disabled == Module.Missiles) continue;
            var n = Math.Min(m.Salvo, ship.Missiles);
            ship.Missiles -= n;
            for (var i = 0; i < n; i++)
            {
                world.Dropped.Add(new Missile
                {
                    Id = world.NextId++, Side = ship.Side, FromShipId = ship.Id, TargetShipId = 0,
                    Position = ship.Position + new Vec2(0.3 * i, -0.3 * i), Velocity = fleet.Velocity, DeltaVLeft = m.DeltaV,
                    Brain = Math.Clamp(world.MissileBrain[(int)ship.Side], 1, 4),
                    WakeHour = world.Hour + wait, ExpiresHour = world.Hour + wait + d.DropWaitHoursMax,
                });
                count++;
            }
        }
        sim.Log.Info("combat", $"{fleet.Name} drops {count} missiles", new { FleetId, count, wait, Source });
    }
}
