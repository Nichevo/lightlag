using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>A minefield: round a planet (it moves with it) or dropped in space (it drifts and spreads).</summary>
public sealed class Minefield
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public Vec2 Position { get; set; }
    public Vec2 Velocity { get; set; }

    /// <summary>Laid round this body (null: loose in space).</summary>
    public int? BodyId { get; set; }

    /// <summary>How wide it is now, and when it was laid (it spreads thinner as it grows).</summary>
    public double RadiusMm { get; set; }
    public double StartRadiusMm { get; set; }
    public double Mines { get; set; }
    public double LaidHour { get; set; }

    /// <summary>Laid with IFF codes: it spares its own side's ships.</summary>
    public bool Iff { get; set; }

    /// <summary>Known to each side (Player, Rival): the layer always; the other once it is spotted or struck.</summary>
    public bool[] Known { get; set; } = { false, false };
}

/// <summary>
/// Mines (Defensive mines research). Missile racks lay them, one missile a mine: round a planet the fleet
/// orbits, or dropped in space (on a lane, or behind a fleet to stop pursuers). They are hard to see, and a
/// loose field drifts and spreads thin. A ship crossing into a field may be hit; a field you know about is
/// crossed more carefully, and point defence holding inside it sweeps it. Without IFF codes a field hits its
/// own side's ships as well.
/// </summary>
public static class Mines
{
    public const string Idea = "defensive-mines";
    private static MineData D => GameData.Weapons.M;

    public static bool Known(Minefield m, Side side) => m.Known[(int)side];

    /// <summary>Lay up to perShip mines from each ship's racks. Returns how many were laid (0 with a reason).</summary>
    public static (int Laid, string? Problem) Lay(Simulation sim, Fleet f, int perShip)
    {
        var w = sim.World;
        if (!Tech.Has(w, f.Side, Idea)) return (0, "Needs Defensive mines");
        var count = 0;
        foreach (var ship in w.ShipsOf(f).ToList())
        {
            if (ship.Missiles <= 0 || ship.Out(Module.Missiles)) continue;
            var n = Math.Min(Math.Min(perShip, D.MaxPerLay), ship.Missiles);
            ship.Missiles -= n;
            count += n;
        }
        if (count == 0) return (0, "No missiles in the racks to lay");
        var body = f.OrbitBodyId is int oid && w.FindBody(oid) is Body ob && Economy.InOrbitAt(f, ob) ? ob : null;
        var iff = Tech.Has(w, f.Side, Countermeasures.Iff);
        var field = body == null ? null : w.Minefields.Find(m => m.BodyId == body.Id && m.Side == f.Side && m.Iff == iff);
        if (field != null) field.Mines += count;
        else
        {
            var radius = body != null ? body.Radius + D.PlanetRadiusMm : D.RadiusMm;
            field = new Minefield
            {
                Id = w.NextId++, Side = f.Side, Position = body?.Position ?? f.Position, Velocity = body == null ? f.Velocity * D.DropSpeedShare : Vec2.Zero,
                BodyId = body?.Id, RadiusMm = radius, StartRadiusMm = radius, Mines = count, LaidHour = w.Hour, Iff = iff,
            };
            field.Known[(int)f.Side] = true;
            w.Minefields.Add(field);
        }
        if (w.Loop) Economy.Of(w, f.Side).Totals.MinesLaid += count;
        sim.Log.Info("combat", $"{f.Name} lays {count} mines", new { fleetId = f.Id, count, fieldId = field.Id, bodyId = body?.Id, iff });
        return (count, null);
    }

    /// <summary>Hit chance for one ship crossing into the field: more mines, more hits; spread thin or known, fewer.</summary>
    public static double HitChance(Minefield m, Side crossing)
    {
        var spread = m.StartRadiusMm / Math.Max(m.StartRadiusMm, m.RadiusMm);
        var chance = Math.Min(D.MaxHitChance, D.HitChancePerMine * m.Mines * spread * spread);
        return Known(m, crossing) ? chance * (1 - D.KnownCut) : chance;
    }

    /// <summary>Every step: fields move, spread and run out; fleets that cross into one are struck.</summary>
    public static void Update(Simulation sim, List<(Fleet Fleet, Vec2 From)> moves, double dt)
    {
        var w = sim.World;
        if (w.Minefields.Count == 0 || dt <= 0) return;
        foreach (var m in w.Minefields.ToList())
        {
            if (m.BodyId is int id && w.FindBody(id) is Body body) m.Position = body.Position;
            else
            {
                m.Position += m.Velocity * dt;
                m.RadiusMm += D.DriftMmPerDay * dt / 24;
            }
            if (m.Mines < 1 || w.Hour - m.LaidHour > D.LifetimeDays * 24)
            {
                w.Minefields.Remove(m);
                sim.Log.Info("combat", "A minefield is spent", new { fieldId = m.Id, side = m.Side.ToString(), left = m.Mines });
                continue;
            }
            foreach (var (f, from) in moves)
            {
                if (m.Mines < 1) break;
                if (m.Iff && f.Side == m.Side) continue;
                if (w.FindFleet(f.Id) == null || !Enters(from, f.Position, m.Position, m.RadiusMm)) continue;
                Cross(sim, m, f);
            }
        }
    }

    /// <summary>Did a move from a to b enter the circle (start outside, and the path reaches inside)?</summary>
    public static bool Enters(Vec2 a, Vec2 b, Vec2 centre, double radius)
    {
        if (Vec2.Distance(a, centre) <= radius) return false;
        var ab = b - a;
        var len2 = ab.X * ab.X + ab.Y * ab.Y;
        var t = len2 > 1e-12 ? Math.Clamp(Vec2.Dot(centre - a, ab) / len2, 0, 1) : 0;
        return Vec2.Distance(a + ab * t, centre) <= radius;
    }

    private static void Cross(Simulation sim, Minefield m, Fleet f)
    {
        var w = sim.World;
        var chance = HitChance(m, f.Side);
        var hits = 0;
        foreach (var ship in w.ShipsOf(f).OrderBy(s => s.Id).ToList())
        {
            if (m.Mines < 1) break;
            if (sim.Rng.NextDouble() >= chance) continue;
            m.Mines -= 1;
            hits++;
            if (w.ActiveBattle is Battle b) b.For(f.Side).MineHits++;
            if (w.Loop) Economy.Of(w, f.Side).Totals.MineHits++;
            sim.AddEffect(new CombatEffect(w.Hour, EffectKind.MineBlast, ship.Position, ship.Position, m.Side));
            Combat.Strike(sim, ship, D.DamagePerMine, m.Side);
        }
        if (hits == 0) return;
        var wasKnown = Known(m, f.Side);
        m.Known[(int)f.Side] = true;
        var own = f.Side == m.Side;
        if (w.ActiveBattle is Battle battle) BattleRecords.Mark(w, battle, f.Side, MarkKind.Decision, $"{f.Name} runs into {(own ? "its own" : "a")} minefield: {hits} hit{(hits == 1 ? "" : "s")}");
        sim.Log.Info("combat", $"{f.Name} runs into a minefield", new { fleetId = f.Id, fieldId = m.Id, hits, own, wasKnown, left = m.Mines });
    }

    /// <summary>Hourly: fields are spotted by enemy fleets close by; point defence holding inside a known enemy field sweeps it.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        foreach (var m in w.Minefields)
            foreach (var f in w.Fleets.Where(f => f.Side != m.Side).OrderBy(f => f.Id))
            {
                var d = Vec2.Distance(f.Position, m.Position);
                if (!Known(m, f.Side) && d <= m.RadiusMm + D.SpotMm)
                {
                    m.Known[(int)f.Side] = true;
                    sim.Log.Info("combat", $"{f.Name} spots a minefield", new { fleetId = f.Id, fieldId = m.Id, side = f.Side.ToString() });
                }
                if (Known(m, f.Side) && d <= m.RadiusMm && f.Flight is not { Started: true })
                {
                    var mounts = w.ShipsOf(f).Where(s => !s.Out(Module.PointDefence)).Sum(s => Fitting.Spec(s).PointDefence);
                    if (mounts > 0) m.Mines = Math.Max(0, m.Mines - mounts * D.SweepPerPdHour);
                }
            }
        AiHourly(sim);
    }

    /// <summary>
    /// Computer-run sides: a fleet running from a chase drops mines behind it; with IFF codes, a fleet holding
    /// orbit at a home planet without a field mines it.
    /// </summary>
    private static void AiHourly(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var mind in w.Minds)
        {
            if (!Tech.Has(w, mind.Side, Idea)) continue;
            foreach (var f in w.Fleets.Where(f => f.Side == mind.Side).OrderBy(f => f.Id).ToList())
            {
                var racks = w.ShipsOf(f).Sum(s => s.Missiles);
                if (racks < 8) continue;
                var chased = f.Retreating && w.Fleets.Any(o => o.Side != f.Side && o.Chase?.TargetFleetId == f.Id);
                var home = f.OrbitBodyId is int id && w.FindBody(id) is Body b && b.Owner == f.Side && b.PopK > 0 && Economy.InOrbitAt(f, b)
                           && Tech.Has(w, f.Side, Countermeasures.Iff) && !w.Minefields.Any(m => m.BodyId == b.Id && m.Side == f.Side);
                if (!chased && !home) continue;
                var (laid, _) = Lay(sim, f, 2);
                if (laid > 0) Mind.Think(w, mind, chased ? $"{f.Name} drops {laid} mines behind it" : $"{f.Name} mines its home orbit ({laid})");
            }
        }
    }
}

/// <summary>Lay mines from the fleet's missile racks: round the planet it orbits, or here in space.</summary>
public sealed record LayMinesCommand(int FleetId, int PerShip) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Lay mines") is not Fleet fleet) return;
        var (_, problem) = Mines.Lay(sim, fleet, Math.Max(1, PerShip));
        if (problem != null) sim.Log.Warn("fleet", $"{fleet.Name} can't lay mines", new { FleetId, problem, Source });
    }
}
