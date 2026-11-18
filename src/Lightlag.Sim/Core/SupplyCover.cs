namespace Lightlag.Sim.Core;

/// <summary>How well a fleet is kept supplied: Covered, Thin (own tenders only, or its only supplier is busy) or None.</summary>
public enum CoverVerdict { Covered, Thin, None }

/// <summary>One way a fleet gets supplies, in the order they would be used, with its state in words.</summary>
public sealed record SupplySource(string Kind, int? FleetId, string Name, string State, bool Counts, string Brief = "");

/// <summary>Every way a fleet is kept supplied and the verdict (see SupplyCover.Of).</summary>
public sealed record SupplyPicture(CoverVerdict Verdict, string Why, List<SupplySource> Sources, string? Refill);

/// <summary>
/// Who keeps a fleet supplied: its own tenders (sailing with it, or away restocking), supply fleets on a run for it,
/// Supply-role fleets that would come when it runs below half (or why they would not: out of range, busy, another
/// fleet first on their list), and the nearest planet to refill at. A view over SupplyLines and the Supply role;
/// it changes nothing, so the screens can ask it every frame.
/// </summary>
public static class SupplyCover
{
    public static SupplyPicture Of(World w, Fleet f)
    {
        var sources = new List<SupplySource>();
        foreach (var s in w.ShipsOf(f).Where(SupplyLines.IsSupplyShip))
            sources.Add(new SupplySource("tender", null, s.Name, $"sails with it, cargo {SupplyLines.CargoShare(s):P0}", true));
        foreach (var t in w.Fleets.Where(t => t.Side == f.Side && t.TenderOf == f.Id && t.ShipIds.Count > 0).OrderBy(t => t.Id))
            sources.Add(new SupplySource("tender", t.Id, t.Name, "away restocking, comes back loaded", true));
        foreach (var r in w.Fleets.Where(r => r.Side == f.Side && r.SupplyRunFor == f.Id && r.TenderOf != f.Id && r.ShipIds.Count > 0).OrderBy(r => r.Id))
            sources.Add(new SupplySource("run", r.Id, r.Name, (SupplyLines.RunStatus(w, f) ?? "on a supply run for it").Replace($"Tanker {r.Name}: ", ""), true));

        var free = false;
        var busy = false;
        foreach (var s in w.Fleets.Where(s => s.Side == f.Side && s.Id != f.Id && s.Role == FleetRole.Supply && s.ShipIds.Count > 0 && s.SupplyRunFor != f.Id)
                     .OrderBy(s => HelpCalls.Priority(s, f)).ThenBy(s => s.Id))
        {
            var range = HelpCalls.RangeAu(s.RoleRules.Range) * Physics.MmPerAu;
            var gap = Vec2.Distance(s.Position, f.Position);
            var au = gap / Physics.MmPerAu;
            var far = au < 0.05 ? "here" : $"{au:0.0} AU";
            var rank = HelpCalls.Priority(s, f);
            var order = rank < int.MaxValue ? $"{Ordinal(rank + 1)} on its list" : s.SupplyFirst.Count > 0 ? "after its list" : "nearest first";
            if (gap > range)
            {
                sources.Add(new SupplySource("role", s.Id, s.Name, $"out of range: {au:0.0} AU away, it goes {HelpCalls.RangeName(s.RoleRules.Range)}", false, $"out of range ({au:0.0} AU)"));
                continue;
            }
            if (s.SupplyRunFor is int other && w.FindFleet(other) is Fleet o)
            {
                busy = true;
                sources.Add(new SupplySource("role", s.Id, s.Name, $"in range ({far}, {order}), busy with {o.Name}", false, $"busy with {o.Name}"));
                continue;
            }
            free = true;
            sources.Add(new SupplySource("role", s.Id, s.Name, $"would come when it runs below half ({far}, {order})", true, $"would come · {far} · {order}"));
        }

        string? refill = Supply.Nearest(w, f) is (Body b, _) ? $"{b.Name}, {Vec2.Distance(b.Position, f.Position) / Physics.MmPerAu:0.0} AU" : null;
        var runs = sources.Any(x => x.Kind == "run");
        var tenders = sources.Any(x => x.Kind == "tender");
        if (runs || free) return new SupplyPicture(CoverVerdict.Covered, runs ? "a supply fleet is on a run for it" : "a supply fleet in range would come", sources, refill);
        if (tenders || busy) return new SupplyPicture(CoverVerdict.Thin, tenders ? "only its own tender looks after it" : "the only supply fleet in range is busy", sources, refill);
        return new SupplyPicture(CoverVerdict.None, "no supply fleet can reach it: it must go home to refill", sources, refill);
    }

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    /// <summary>The verdict in a word for lists.</summary>
    public static string Word(CoverVerdict v) => v switch { CoverVerdict.Covered => "Covered", CoverVerdict.Thin => "Thin", _ => "None" };
}
