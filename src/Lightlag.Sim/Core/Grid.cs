namespace Lightlag.Sim.Core;

/// <summary>
/// A planet's power grid (Plan 3 build 5, People and power). Each hour the planet's plants, solar fields and
/// small generators make power; battery banks add what they hold (up to their hourly rate); buildings are
/// powered in their power order (High, Normal, Low; within each, the planet's area order). When power runs
/// short the grid sheds whole buildings, and gives what is left to the first shed building as a brownout
/// (it runs at that share) if it covers enough of its draw. Spare power charges the batteries.
/// Solar fields make nothing while the body is in the shadow of a bigger body (a moon behind its planet),
/// which is what batteries are best for; they also carry a planet through a reactor fault or a fuel gap.
/// </summary>
public static class Grid
{
    public static GridData D => Colonies.C.Grid ?? new GridData();

    public const string Battery = "battery-bank";

    /// <summary>A battery bank's power-hours, after wear (worn banks hold less).</summary>
    public static double RoomOf(Piece p, BuildingData data) => p.Built ? data.Battery * (0.5 + 0.5 * p.Condition) : 0;

    /// <summary>Most a planet's batteries can give in one hour.</summary>
    public static double Rate(Body b) => b.BatteryRoom * D.DischargeShare;

    /// <summary>Is a body in the shadow of a bigger body (its planet, for a moon)? Smaller bodies only dim it.</summary>
    public static bool Shaded(World w, Body b)
    {
        var star = Sails.Star(w);
        var sun = star?.Position ?? Vec2.Zero;
        var d = b.Position - sun;
        var len2 = Vec2.Dot(d, d);
        if (len2 <= 1e-18) return false;
        foreach (var o in w.Bodies)
        {
            if (o.Kind == BodyKind.Star || o.Id == b.Id || o.Radius <= b.Radius) continue;
            var t = Vec2.Dot(o.Position - sun, d) / len2;
            if (t <= 0 || t >= 1) continue;
            if (Vec2.Distance(sun + d * t, o.Position) < o.Radius) return true;
        }
        return false;
    }

    /// <summary>One hour of the grid: power each building, use or fill the batteries, note shedding.</summary>
    public static void Run(World w, Body b, List<(Piece Piece, BuildingData Data)> built, double made)
    {
        b.InShadow = built.Any(x => x.Data.Solar) && Shaded(w, b);
        if (b.InShadow) b.LastShadowHour = w.Tick;
        b.PowerMade = made;
        b.BatteryRoom = built.Where(x => x.Data.Battery > 0).Sum(x => RoomOf(x.Piece, x.Data));
        b.BatteryCharge = Math.Clamp(b.BatteryCharge, 0, b.BatteryRoom);
        var available = made + Math.Min(b.BatteryCharge, Rate(b));

        // First pass: whole buildings, in power order; one that does not fit is skipped (a smaller one later may fit).
        var used = 0.0;
        var order = built.OrderBy(x => Colonies.PowerTier(x.Piece.PowerUse)).ToList();
        var dark = new List<(Piece Piece, double Draw)>();
        foreach (var (p, data) in order)
        {
            p.PowerShare = 1;
            if (data.Power <= 0) { p.Powered = true; continue; }
            if (p.PowerUse == PowerUse.Off) { p.Powered = false; continue; }
            var draw = data.Power * Munitions.DrawFactor(p);
            p.Powered = used + draw <= available + 1e-9;
            if (p.Powered) used += draw;
            else dark.Add((p, draw));
        }
        // Brownout: what is left goes to the first building shed, if it covers enough of its draw.
        var left = available - used;
        if (dark.Count > 0 && left > 1e-9 && dark[0].Draw > 0 && left / dark[0].Draw >= D.BrownoutMin)
        {
            var (p, draw) = dark[0];
            p.Powered = true;
            p.PowerShare = Math.Clamp(left / draw, 0, 1);
            used += left;
        }
        b.PowerUsed = used;
        b.PowerWanted = built.Where(x => x.Data.Power > 0 && x.Piece.PowerUse != PowerUse.Off).Sum(x => x.Data.Power * Munitions.DrawFactor(x.Piece));
        if (dark.Count > 0)
        {
            b.ShedSinceHour ??= w.Tick;
            b.LastShedHour = w.Tick;
            if (b.Owner is Side owner) Stats.Of(w, owner).ShedHours++;
        }
        else b.ShedSinceHour = null;

        // Batteries: cover the gap, or store what is spare.
        var net = made - used;
        b.FromBattery = net < 0 ? Math.Min(-net, b.BatteryCharge) : 0;
        b.BatteryCharge = net < 0
            ? Math.Max(0, b.BatteryCharge + net)
            : Math.Min(b.BatteryRoom, b.BatteryCharge + net * D.ChargeEfficiency);
    }

    /// <summary>Hours the batteries would carry today's gap (null when nothing is drawn from them).</summary>
    public static double? HoursLeft(Body b) => b.FromBattery > 1e-6 ? b.BatteryCharge / b.FromBattery : null;

    /// <summary>Is the grid shedding a building this hour?</summary>
    public static bool Shedding(World w, Body b) => b.LastShedHour == w.Tick;

    /// <summary>Buildings shed now (dark) and the one on a brownout, by name.</summary>
    public static (List<string> Shed, string? Brownout) ShedList(Body b)
    {
        var shed = Colonies.Built(b).Where(p => !p.Powered && p.PowerUse != PowerUse.Off && Planets.BuildingKind(p.Kind) is { Power: > 0 })
            .GroupBy(p => Planets.BuildingKind(p.Kind)!.Name).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList();
        var brown = Colonies.Built(b).FirstOrDefault(p => p.Powered && p.PowerShare < 0.999);
        return (shed, brown == null ? null : $"{Planets.BuildingKind(brown.Kind)?.Name} at {brown.PowerShare:P0}");
    }

    /// <summary>The grid in one line for the planet screen: made, used, batteries and shadow.</summary>
    public static string Summary(World w, Body b)
    {
        var parts = new List<string> { $"Grid: makes {b.PowerMade:0}, uses {b.PowerUsed:0} of {b.PowerWanted:0} wanted" };
        if (b.BatteryRoom > 0)
        {
            var line = $"batteries {b.BatteryCharge:0}/{b.BatteryRoom:0}";
            if (HoursLeft(b) is double h) line += $", giving {b.FromBattery:0.#} an hour ({h:0} h left)";
            else if (b.BatteryCharge < b.BatteryRoom - 0.5 && b.PowerMade > b.PowerUsed) line += ", charging";
            parts.Add(line);
        }
        if (b.InShadow) parts.Add("IN SHADOW: solar fields dark");
        var (shed, brown) = ShedList(b);
        if (shed.Count > 0) parts.Add("SHED: " + string.Join(", ", shed));
        if (brown != null) parts.Add("brownout: " + brown);
        return string.Join(" · ", parts);
    }
}
