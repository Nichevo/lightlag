namespace Lightlag.Sim.Core;

/// <summary>
/// The shape a fleet keeps. Auto picks one from the fleet's loadout and the threat. Staggered: big ships
/// apart in front, small ones in the gaps behind (most guns clear, most ships near a shield). Wall: big
/// ships in front, the rest tight behind them (strong cover, but guns behind are blocked; missiles launch
/// over). Line: one row abreast (nothing blocked, little cover). Column: one behind the other (fast
/// through lanes; almost every gun blocked).
/// </summary>
public enum Formation { Auto, Staggered, Wall, Line, Column }

/// <summary>Numbers for formations and blocked fire (ship-classes.json, "formations").</summary>
public sealed record FormationData(
    double BlockMm = 0.6, double PdCoverMm = 6, double PdOutsideFactor = 0.5, double ShieldCoverMm = 3, double ShieldShare = 0.5,
    double TurnRadians = 0.35, double WallMissileShare = 0.5, double WallHeavyArmour = 300);

/// <summary>
/// Formations: where each ship sits in its fleet, which way the fleet's front points, and what that does
/// in a fight. Railguns need a clear line (a fleet-mate in the way blocks the shot); missiles launch over
/// friendly ships; point defence covers fleet-mates within its cover radius fully and the rest less;
/// shield emitters take part of the hits on an unshielded fleet-mate close by.
/// </summary>
public static class Formations
{
    public static FormationData D => GameData.Formations;

    /// <summary>The shape in use: the fleet's own pick, or Auto's.</summary>
    public static Formation Shape(World w, Fleet f) => f.Formation != Formation.Auto ? f.Formation : Pick(w, f);

    /// <summary>
    /// Auto's pick: Wall when most ships are missile boats and a heavy ship can stand in front of them;
    /// Line for an all-gun fleet facing an enemy with no missiles; Column when flying with no enemy near;
    /// otherwise Staggered.
    /// </summary>
    public static Formation Pick(World w, Fleet f)
    {
        var ships = w.ShipsOf(f).ToList();
        if (ships.Count <= 1) return Formation.Staggered;
        var enemy = NearestEnemy(w, f);
        if (enemy == null && f.Flight is { Started: true }) return Formation.Column;
        var missileBoats = ships.Count(s => Fitting.Spec(s) is { Missiles: > 0, Railguns: 0 });
        var heavy = ships.Any(s => Fitting.Spec(s).Armour >= D.WallHeavyArmour);
        if (heavy && missileBoats >= D.WallMissileShare * ships.Count) return Formation.Wall;
        if (enemy != null && ships.All(s => Fitting.Spec(s).Railguns > 0) && !w.ShipsOf(enemy).Any(s => s.Missiles > 0)) return Formation.Line;
        return Formation.Staggered;
    }

    /// <summary>The nearest enemy fleet in contact range (what the front faces in a fight), if any.</summary>
    public static Fleet? NearestEnemy(World w, Fleet f)
    {
        if (w.ActiveBattle == null) return null;
        Fleet? best = null;
        var bestD = GameData.Combat.ContactRangeMm * 1.5; // long railgun shots reach past contact range
        foreach (var e in w.Fleets)
        {
            if (e.Side == f.Side || e.ShipIds.Count == 0) continue;
            var d = Vec2.Distance(e.Position, f.Position);
            if (d < bestD) { best = e; bestD = d; }
        }
        return best;
    }

    /// <summary>Which way the front points (radians): at the nearest enemy in a fight, else along the fleet's facing.</summary>
    public static double Front(World w, Fleet f)
    {
        if (NearestEnemy(w, f) is Fleet e)
        {
            var d = e.Position - f.Position;
            if (d.Length > 1e-9) return DetMath.Atan2(d.Y, d.X);
        }
        return f.Facing;
    }

    /// <summary>Lays the ships out again when the shape, the ship count or the front (by more than a little) changed.</summary>
    public static void Keep(World w, Fleet f)
    {
        var shape = Shape(w, f);
        var front = Front(w, f);
        var turn = Math.Abs(Angle(front - f.LaidFront));
        if (shape == f.LaidShape && f.ShipIds.Count == f.LaidCount && turn < D.TurnRadians) return;
        Layout(w, f, shape, front);
    }

    /// <summary>Lays the ships out in the shape with the front this way. The biggest ships go to the front row.</summary>
    public static void Layout(World w, Fleet f, Formation shape, double front)
    {
        var ships = w.ShipsOf(f).OrderByDescending(s => Fitting.Spec(s).Armour).ThenBy(s => s.Id).ToList();
        var ahead = new Vec2(DetMath.Cos(front), DetMath.Sin(front));
        var across = new Vec2(-ahead.Y, ahead.X);
        var slots = Slots(shape, ships.Count);
        for (var i = 0; i < ships.Count; i++)
            ships[i].FormationOffset = ahead * slots[i].Fwd + across * slots[i].Lat;
        f.LaidShape = shape;
        f.LaidFront = front;
        f.LaidCount = ships.Count;
    }

    /// <summary>Each ship's place (forward, across) in Mm, biggest ship first. Ships keep FormationSpacing apart.</summary>
    public static List<(double Fwd, double Lat)> Slots(Formation shape, int n)
    {
        var s = Fleets.FormationSpacing;
        var slots = new List<(double, double)>();
        if (n == 0) return slots;
        switch (shape)
        {
            case Formation.Line:
                for (var i = 0; i < n; i++) slots.Add((0, s * (i - (n - 1) / 2.0)));
                break;
            case Formation.Column:
                for (var i = 0; i < n; i++) slots.Add((s * ((n - 1) / 2.0 - i), 0));
                break;
            case Formation.Wall:
            {
                var nf = Math.Max(1, (n + 2) / 3);
                for (var i = 0; i < nf; i++) slots.Add((0, s * (i - (nf - 1) / 2.0)));
                // The rest stand straight behind the front ships: well covered, but their guns face a friendly hull.
                for (var j = 0; j < n - nf; j++) slots.Add((-s * (1 + j / nf), s * (j % nf - (nf - 1) / 2.0)));
                break;
            }
            default: // Staggered (and Auto)
            {
                var nf = (n + 1) / 2;
                var nb = n - nf;
                // Front ships stand two spacings apart; the back row sits in the gaps (an odd number of spacings off).
                for (var i = 0; i < nf; i++) slots.Add((0, s * (2 * i - (nf - 1))));
                var shift = (nb - 1) % 2 == (nf - 1) % 2 ? s : 0;
                for (var j = 0; j < nb; j++) slots.Add((-s, s * (2 * j - (nb - 1)) + shift));
                break;
            }
        }
        return slots;
    }

    /// <summary>Is a fleet-mate in the way of a railgun shot from this ship at that one (close to the line, between the two)?</summary>
    public static bool Blocked(World w, Ship shooter, Ship target)
    {
        if (w.FleetOf(shooter) is not Fleet f) return false;
        var line = target.Position - shooter.Position;
        var length = line.Length;
        if (length < 1e-9) return false;
        var dir = line * (1 / length);
        foreach (var mate in w.ShipsOf(f))
        {
            if (mate.Id == shooter.Id) continue;
            var to = mate.Position - shooter.Position;
            var along = Vec2.Dot(to, dir);
            if (along <= 0 || along >= length) continue;
            var off = to - dir * along;
            if (off.Length < D.BlockMm) return true;
        }
        return false;
    }

    /// <summary>How much a point defence mount on this ship helps against a missile at that one: fully within cover range, less beyond.</summary>
    public static double PdCover(Ship gunner, Ship target) =>
        gunner.Id == target.Id || Vec2.Distance(gunner.Position, target.Position) <= D.PdCoverMm ? 1 : D.PdOutsideFactor;

    /// <summary>A fleet-mate with shield emitters and shield left close enough to take part of a hit on this unshielded ship.</summary>
    public static Ship? ShieldCover(World w, Ship target)
    {
        if (target.Shield > 0 || w.FleetOf(target) is not Fleet f) return null;
        Ship? best = null;
        var bestD = D.ShieldCoverMm;
        foreach (var mate in w.ShipsOf(f))
        {
            if (mate.Id == target.Id || mate.Shield <= 0 || mate.Overheated || !HasEmitter(mate)) continue;
            var d = Vec2.Distance(mate.Position, target.Position);
            if (d <= bestD) { best = mate; bestD = d; }
        }
        return best;
    }

    public static bool HasEmitter(Ship s) => Fitting.FitOf(s).Contains("shield-emitter");

    /// <summary>Ships in the fleet whose railguns were blocked by a fleet-mate in the last half hour (for the battle screen).</summary>
    public static int BlockedShips(World w, Fleet f) => w.ShipsOf(f).Count(s => w.Hour - s.BlockedHour <= 0.5);

    public static string Name(Formation f) => f switch
    {
        Formation.Wall => "Wall",
        Formation.Line => "Line abreast",
        Formation.Column => "Column",
        Formation.Staggered => "Staggered",
        _ => "Auto",
    };

    private static double Angle(double a)
    {
        while (a > Math.PI) a -= 2 * Math.PI;
        while (a < -Math.PI) a += 2 * Math.PI;
        return a;
    }
}

/// <summary>Sets the shape a fleet keeps (Auto lets the fleet pick from its loadout and the threat).</summary>
public sealed record SetFormationCommand(int FleetId, Formation Formation) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Formation") is not Fleet fleet) return;
        fleet.Formation = Formation;
        Choices.Note(world, fleet.Side, "formation", Formations.Name(Formation), "", Source);
        Formations.Keep(world, fleet);
        sim.Log.Info("fleet", $"{fleet.Name} formation {Formations.Name(Formation)}", new { FleetId, formation = Formation.ToString(), Source });
    }
}
