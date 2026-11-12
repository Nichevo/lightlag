using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (Data/hazards.json) -----------------------------------------------------------------

/// <summary>Hazard numbers. See the comments in hazards.json for what each means.</summary>
public sealed class HazardsData
{
    public double RockHitsPerDay { get; set; } = 0.04;
    public double RockSpeedMmPerHour { get; set; } = 300;
    public double RockHitHull { get; set; } = 0.04;
    public double MaxSpeedFactor { get; set; } = 2.5;
    public double RockEnginesChance { get; set; } = 0.3;
    public double HullFloor { get; set; } = 0.15;
    public double BarFloor { get; set; } = 0.2;
    public double[] SizeLengthM { get; set; } = { 100, 200, 300 };
    public double[] SizeCross { get; set; } = { 1, 2, 4, 8 };
    public double PdClears { get; set; } = 0.4;
    public double PdHeatPerHour { get; set; } = 0.6;

    public int DebrisMinLosses { get; set; } = 2;
    public double DebrisDays { get; set; } = 30;
    public double DebrisStartMm { get; set; } = 30;
    public double DebrisEndMm { get; set; } = 300;
    public double DebrisDensity { get; set; } = 4;
    public double DebrisPerLoss { get; set; } = 1;
    public double DebrisMaxDensity { get; set; } = 10;
    public double DebrisSizeShare { get; set; } = 0.35;

    public double RadCrewPerDay { get; set; } = 0.04;
    public double RadWearPerDay { get; set; } = 0.012;
    public double RadCrewFloor { get; set; } = 0.3;
    public double RadWearFloor { get; set; } = 0.5;
    public double RadShieldCut { get; set; } = 0.5;

    public double SunHotAu { get; set; } = 0.6;
    public double SunHeatPerHour { get; set; } = 3;
    public double SunDazzleDegrees { get; set; } = 8;
    public double SunDazzleFactor { get; set; } = 0.5;

    public double SlowAbove { get; set; } = 0.08;

    public EventsData Events { get; set; } = new();
}

/// <summary>A cloud of wreckage left where a big battle was fought (see Hazards): it spreads and thins out over DebrisDays.</summary>
public sealed class Debris
{
    public int Id { get; set; }
    public Vec2 Position { get; set; }

    /// <summary>Fought in orbit: the cloud stays in orbit of this body, at this offset.</summary>
    public int? BodyId { get; set; }
    public Vec2 Offset { get; set; }
    public double Hour { get; set; }
    public double Density { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>What hazards one fleet is in right now (for the fleet panel and the map).</summary>
public sealed record HazardHere(double Rocks, double Radiation, double SunHeat, string Text);

/// <summary>
/// Space hazards. Rocks (asteroid belts, planetary rings and battle debris) hit ships moving through
/// them: hits a day = density x speed x cross-section, so big ships must slow down or take hits while
/// escorts barely notice; each hit takes hull (more the faster) and sometimes engines. Radiation belts
/// round the gas giants strain crews and wear electronics. Close to the sun, ships run hot and sensors
/// looking sunward are dazzled. Hazards use the existing damage bars, so Auto repair and pull-back rules
/// already handle them, and they never take a ship below HullFloor: they wear, they don't destroy.
/// Hourly in full games (switch: GameRules.Hazards). The rival plans routes with the same numbers.
/// </summary>
public static class Hazards
{
    public static HazardsData D => GameData.Hazards;

    public const string DebrisShielding = "debris-shielding";
    public const string RockMapping = "rock-mapping";
    public const string RadiationShielding = "radiation-shielding";

    public static bool On(World w) => w.Loop && w.Rules.Hazards;

    // ---- Where the hazards are ---------------------------------------------------------------------

    public static bool IsRocks(Belt b) => b.Kind is BeltKind.Belt or BeltKind.Ring;

    /// <summary>The centre of a belt (its parent body), or null if the body is gone.</summary>
    public static Vec2? CentreOf(World w, Belt b) => w.FindBody(b.ParentId)?.Position;

    public static bool Inside(World w, Belt b, Vec2 at) =>
        CentreOf(w, b) is Vec2 c && Vec2.Distance(at, c) is var d && d >= b.InnerRadius && d <= b.OuterRadius;

    /// <summary>Where a debris cloud is now (it moves with the body it was fought over).</summary>
    public static Vec2 PositionOf(World w, Debris d) =>
        d.BodyId is int id && w.FindBody(id) is Body b ? b.Position + d.Offset : d.Position;

    /// <summary>How far a debris cloud reaches now, and how thick it still is (0 once it has thinned out).</summary>
    public static double RadiusOf(World w, Debris d) =>
        D.DebrisStartMm + (D.DebrisEndMm - D.DebrisStartMm) * Math.Clamp((w.Hour - d.Hour) / (D.DebrisDays * 24), 0, 1);

    public static double DensityOf(World w, Debris d) => d.Density * Math.Clamp(1 - (w.Hour - d.Hour) / (D.DebrisDays * 24), 0, 1);

    /// <summary>Rock density at a point: belts and rings (cut by rock mapping for this side) and battle debris (kept apart).</summary>
    public static (double Belts, double Debris) RocksAt(World w, Vec2 at)
    {
        var belts = 0.0;
        foreach (var b in w.Belts)
            if (IsRocks(b) && Inside(w, b, at)) belts += b.Density;
        var debris = 0.0;
        foreach (var d in w.Debris)
            if (Vec2.Distance(PositionOf(w, d), at) <= RadiusOf(w, d)) debris += DensityOf(w, d);
        return (belts, debris);
    }

    public static double RadiationAt(World w, Vec2 at)
    {
        var r = 0.0;
        foreach (var b in w.Belts)
            if (b.Kind == BeltKind.Radiation && Inside(w, b, at)) r += b.Density;
        return r;
    }

    /// <summary>The star (the body nothing orbits), if any.</summary>
    public static Body? Star(World w) => w.Bodies.Find(b => b.Kind == BodyKind.Star);

    /// <summary>Extra heat an hour this close to the sun (0 outside SunHotAu, or with hazards off).</summary>
    public static double SunHeat(World w, Vec2 at)
    {
        if (!On(w) || !at.IsFinite || Star(w) is not Body star) return 0;
        var d = Math.Max(star.Radius, Vec2.Distance(at, star.Position));
        var hot = D.SunHotAu * Physics.MmPerAu;
        if (d >= hot) return 0;
        var r = hot / d;
        return D.SunHeatPerHour * (r * r - 1);
    }

    // ---- Ships -----------------------------------------------------------------------------------

    /// <summary>0 small, 1 medium, 2 large, 3 huge: by hull length.</summary>
    public static int SizeOf(Ship s)
    {
        var length = Fitting.Spec(s).LengthM;
        var i = 0;
        while (i < D.SizeLengthM.Length && length >= D.SizeLengthM[i]) i++;
        return i;
    }

    public static string SizeName(int size) => size switch { 0 => "small", 1 => "medium", 2 => "large", _ => "huge" };

    public static double Cross(Ship s) => D.SizeCross[Math.Min(SizeOf(s), D.SizeCross.Length - 1)];

    /// <summary>Debris hurts all sizes much alike: only a share of the size counts.</summary>
    public static double DebrisCross(Ship s) => 1 + (Cross(s) - 1) * D.DebrisSizeShare;

    public static bool PdWorks(Ship s) => Fitting.Spec(s).PointDefence > 0 && !s.Out(Module.PointDefence) && !s.Overheated;

    /// <summary>A fleet's speed through the rocks (fleets holding orbit move with their planet: none).</summary>
    public static double SpeedOf(Fleet f) => f.Hold == HoldStyle.Orbit && f.Flight == null ? 0 : f.Velocity.Length;

    public static double SpeedFactor(double speed) => Math.Min(D.MaxSpeedFactor, speed / D.RockSpeedMmPerHour);

    /// <summary>Expected rock hits an hour for one ship at this speed in these rocks.</summary>
    public static double HitsPerHour(World w, Ship s, double speed, double belts, double debris, bool pd)
    {
        if (speed <= 1e-9 || belts + debris <= 0) return 0;
        var mapped = Tech.Factor(w, s.Side, RockMapping);
        var dens = belts * Cross(s) * mapped + debris * DebrisCross(s);
        return D.RockHitsPerDay / 24 * dens * (speed / D.RockSpeedMmPerHour) * (pd ? 1 - D.PdClears : 1);
    }

    /// <summary>Share of full hull one hit takes at this speed (research cuts it).</summary>
    public static double HitShare(World w, Side side, double speed) =>
        D.RockHitHull * Math.Max(0.25, SpeedFactor(speed)) * Tech.Factor(w, side, DebrisShielding);

    // ---- Every hour --------------------------------------------------------------------------------

    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        if (!On(w)) return;
        w.Debris.RemoveAll(d => DensityOf(w, d) <= 1e-6);
        foreach (var f in w.Fleets.ToList())
        {
            if (!w.Fleets.Contains(f) || !f.Position.IsFinite) continue;
            var (belts, debris) = RocksAt(w, f.Position);
            var rad = RadiationAt(w, f.Position);
            if (belts + debris <= 0 && rad <= 0) continue;
            var speed = SpeedOf(f);
            foreach (var s in w.ShipsOf(f).ToList())
            {
                if (belts + debris > 0 && speed > 1e-9) Rocks(sim, f, s, speed, belts, debris);
                if (rad > 0) Radiation(sim, s, rad);
            }
        }
    }

    private static void Rocks(Simulation sim, Fleet f, Ship s, double speed, double belts, double debris)
    {
        var w = sim.World;
        var pd = PdWorks(s);
        if (pd) Heat.Add(s, D.PdHeatPerHour);
        var expect = HitsPerHour(w, s, speed, belts, debris, pd);
        var hits = (int)expect;
        if (sim.Rng.NextDouble() < expect - hits) hits++;
        for (var i = 0; i < hits; i++) RockHit(sim, f, s, speed, debris > belts ? "debris" : "rocks");
    }

    /// <summary>One rock hits: hull (never below HullFloor), and sometimes the engines (never below BarFloor).</summary>
    public static void RockHit(Simulation sim, Fleet f, Ship s, double speed, string what)
    {
        var w = sim.World;
        var max = Tech.MaxArmour(w, s);
        var share = HitShare(w, s.Side, speed);
        var floor = max * D.HullFloor;
        var before = s.Armour;
        if (!s.Crippled && s.Armour > floor) s.Armour = Math.Max(floor, s.Armour - share * max);
        var engines = sim.Rng.NextDouble() < D.RockEnginesChance;
        if (engines && s.Engines > D.BarFloor) s.Engines = Math.Max(D.BarFloor, s.Engines - share * Wounds.D.BarShare);
        var lost = before - s.Armour;
        if (w.Loop)
        {
            var t = Economy.Of(w, s.Side).Totals;
            t.RockHits++;
            t.HazardHull += lost;
        }
        Sparks.Fire(sim, s.Side, "rock-hits");
        sim.Log.Info("hazard", $"{s.Name} is hit by {what}", new { shipId = s.Id, fleetId = f.Id, hull = Math.Round(lost, 1), engines, speed = Math.Round(speed) });
    }

    private static void Radiation(Simulation sim, Ship s, double rad)
    {
        var w = sim.World;
        var cut = rad * Tech.Factor(w, s.Side, RadiationShielding) * (Fitting.Spec(s).Shield > 0 ? D.RadShieldCut : 1) / 24;
        var crewBefore = s.Crew;
        if (Crews.Crewed(s) && s.Crew > D.RadCrewFloor) s.Crew = Math.Max(D.RadCrewFloor, s.Crew - D.RadCrewPerDay * cut);
        if (s.Defence > D.RadWearFloor) Wear(w, s, Math.Max(D.RadWearFloor, s.Defence - D.RadWearPerDay * cut));
        if (w.Loop) Economy.Of(w, s.Side).Totals.RadiationShipHours++;
        if (crewBefore > 0.5 && s.Crew <= 0.5)
        {
            Sparks.Fire(sim, s.Side, "radiation");
            sim.Log.Info("hazard", $"{s.Name}'s crew is sick from radiation", new { shipId = s.Id, crew = Math.Round(s.Crew, 2) });
        }
    }

    /// <summary>Worn electronics: the defence bar drops, and the shield it can hold with it.</summary>
    public static void Wear(World w, Ship s, double defence)
    {
        s.Defence = Math.Clamp(defence, 0, 1);
        s.Shield = Math.Min(s.Shield, Heat.MaxShield(w, s));
    }

    /// <summary>A battle is over: a big one leaves a debris cloud where it was fought.</summary>
    public static void OnBattleOver(Simulation sim, Battle b)
    {
        var w = sim.World;
        if (!On(w)) return;
        var losses = b.Sides.Sum(s => s.ShipsLost.Count + s.Crippled.Count);
        if (losses < D.DebrisMinLosses) return;
        var density = Math.Min(D.DebrisMaxDensity, D.DebrisDensity + D.DebrisPerLoss * (losses - D.DebrisMinLosses));
        var near = w.Bodies.Where(x => x.Kind != BodyKind.Star).OrderBy(x => Vec2.Distance(x.Position, b.Where)).FirstOrDefault();
        var d = new Debris { Id = w.NextId++, Position = b.Where, Hour = w.Hour, Density = density, Name = $"Debris of battle {b.Id}" };
        if (near != null && Vec2.Distance(near.Position, b.Where) <= Fleets.OrbitRadiusFor(near) * 3 + near.Radius)
        {
            d.BodyId = near.Id;
            d.Offset = b.Where - near.Position;
            d.Name = $"Debris over {near.Name}";
        }
        w.Debris.Add(d);
        sim.Log.Info("hazard", $"{d.Name} drifts where the battle was", new { debrisId = d.Id, battleId = b.Id, losses, density = Math.Round(density, 1) });
    }

    // ---- Sensors ---------------------------------------------------------------------------------

    private static double? _cosDazzle;
    private static double CosDazzle => _cosDazzle ??= DetMath.Cos(D.SunDazzleDegrees * Math.PI / 180);

    /// <summary>
    /// How far an observer of this side sees towards a target, as a share of its usual range: the sun
    /// dazzles sensors looking within a few degrees of it, and a solar flare blinds them for a while.
    /// </summary>
    public static double SensorFactor(World w, Side side, Vec2 from, Vec2 to)
    {
        if (!On(w)) return 1;
        var f = Events.BlindFactor(w, side);
        if (Star(w) is Body star)
        {
            var toSun = star.Position - from;
            var look = to - from;
            var ls = look.Length * toSun.Length;
            if (ls > 1e-9 && Vec2.Dot(look, toSun) / ls >= CosDazzle) f *= D.SunDazzleFactor;
        }
        return f;
    }

    // ---- Routes ------------------------------------------------------------------------------------

    /// <summary>
    /// Expected hull lost by the fleet's worst-hit ship on this flight, as a share of its full hull, and
    /// the hours spent in rocks and radiation (for the route planner's warning and the rival's choice).
    /// </summary>
    public static (double HullShare, double RockHours, double RadHours, string? Worst) Estimate(World w, Fleet f, Flight flight)
    {
        if (!On(w) || flight.EndHour <= flight.DepartHour) return (0, 0, 0, null);
        var ships = w.ShipsOf(f).ToList();
        if (ships.Count == 0) return (0, 0, 0, null);
        var hours = flight.EndHour - flight.DepartHour;
        var steps = Math.Clamp((int)(hours / 3), 4, 400);
        var dt = hours / steps;
        var worst = new double[ships.Count];
        double rockHours = 0, radHours = 0;
        for (var i = 0; i < steps; i++)
        {
            var t = flight.DepartHour + (i + 0.5) * dt;
            if (FlightPlanner.FlightState(flight, t) is not { } st) continue;
            var at = st.Position;
            var (belts, debris) = RocksAt(w, at);
            if (RadiationAt(w, at) > 0) radHours += dt;
            if (belts + debris <= 0) continue;
            rockHours += dt;
            var speed = st.Velocity.Length;
            for (var k = 0; k < ships.Count; k++)
                worst[k] += HitsPerHour(w, ships[k], speed, belts, debris, PdWorks(ships[k])) * dt * HitShare(w, f.Side, speed);
        }
        var at0 = 0;
        for (var k = 1; k < ships.Count; k++) if (worst[k] > worst[at0]) at0 = k;
        return (worst[at0], rockHours, radHours, worst[at0] > 0 ? ships[at0].Name : null);
    }

    /// <summary>A line for the route planner: what the route flies through, or null if nothing.</summary>
    public static string? RouteWarning(World w, Fleet f, Flight flight)
    {
        var (hull, rockHours, radHours, worst) = Estimate(w, f, flight);
        var bits = new List<string>();
        if (rockHours > 0) bits.Add(hull >= 0.005 ? $"rocks {rockHours / 24:0.#} days: about {hull:P0} hull off {worst} (slower is safer)" : $"rocks {rockHours / 24:0.#} days (little risk at this speed)");
        if (radHours > 0) bits.Add($"radiation {radHours / 24:0.#} days (crew strain)");
        return bits.Count == 0 ? null : "Hazards: " + string.Join("; ", bits);
    }

    /// <summary>What hazards the fleet is in now, for its panel (null: none).</summary>
    public static HazardHere? Here(World w, Fleet f)
    {
        if (!On(w) || !f.Position.IsFinite) return null;
        var (belts, debris) = RocksAt(w, f.Position);
        var rad = RadiationAt(w, f.Position);
        var sun = SunHeat(w, f.Position);
        if (belts + debris <= 0 && rad <= 0 && sun <= 0) return null;
        var bits = new List<string>();
        var speed = SpeedOf(f);
        if (belts > 0) bits.Add(speed > 1e-9 ? "in an asteroid field: rocks hit big ships at speed" : "in an asteroid field (safe while still)");
        if (debris > 0) bits.Add(speed > 1e-9 ? "in battle debris: slow down" : "in battle debris (safe while still)");
        if (rad > 0) bits.Add("in a radiation belt: crews strained, electronics wear");
        if (sun > 0) bits.Add($"close to the sun: +{sun:0.#} heat an hour");
        return new HazardHere(belts + debris, rad, sun, string.Join("; ", bits));
    }
}
