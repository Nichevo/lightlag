using Lightlag.Sim.Core;

namespace Lightlag.Sim.Logging;

/// <summary>A "should never happen" check that failed.</summary>
public sealed record RuleViolation(string Rule, string Detail);

/// <summary>Thrown in strict mode (headless runs, tests) when a rule check fails.</summary>
public sealed class RuleBrokenException(RuleViolation violation)
    : Exception($"Rule broken: {violation.Rule}: {violation.Detail}")
{
    public RuleViolation Violation { get; } = violation;
}

/// <summary>
/// Rule checks: things that should always be true about the game state. The simulation runs them
/// regularly. In the game a failed rule is logged (area "rule") and play carries on; in headless runs
/// and tests "strict" mode turns it into a crash report so it cannot be missed.
/// Add a rule whenever a bug is found that a check could have caught early.
/// </summary>
public sealed class RuleBook
{
    private readonly List<(string Name, Func<World, IEnumerable<string>> Check)> _rules = new();

    public IReadOnlyList<string> Names => _rules.Select(r => r.Name).ToList();

    public RuleBook Add(string name, Func<World, IEnumerable<string>> check)
    {
        _rules.Add((name, check));
        return this;
    }

    public List<RuleViolation> Check(World world)
    {
        var found = new List<RuleViolation>();
        foreach (var (name, check) in _rules)
        {
            try
            {
                foreach (var detail in check(world)) found.Add(new RuleViolation(name, detail));
            }
            catch (Exception ex)
            {
                found.Add(new RuleViolation(name, $"the check itself threw {ex.GetType().Name}: {ex.Message}"));
            }
        }
        return found;
    }

    /// <summary>The rules every game uses.</summary>
    public static RuleBook Default() => new RuleBook()
        .Add("time-not-negative", w => w.Tick < 0 ? new[] { $"tick is {w.Tick}" } : Array.Empty<string>())
        .Add("positions-finite", PositionsFinite)
        .Add("ids-unique", IdsUnique)
        .Add("orbits-hold-radius", OrbitsHoldRadius)
        .Add("fleet-orbit-body-exists", w => w.Fleets.Where(f => f.OrbitBodyId is int b && w.FindBody(b) == null)
            .Select(f => $"fleet {f.Id} {f.Name} orbits missing body {f.OrbitBodyId}"))
        .Add("fleets-hold-their-ships", FleetsHoldTheirShips)
        .Add("fleet-joins-own-side", w => w.Fleets.Where(f => f.JoinFleetId is int j
                && (j == f.Id || w.FindFleet(j) is Fleet other && other.Side != f.Side))
            .Select(f => $"fleet {f.Id} {f.Name} is set to join fleet {f.JoinFleetId}"))
        .Add("sightings-sane", w => w.Sightings.Where(s => !s.Position.IsFinite || !s.Velocity.IsFinite
                || w.FindFleet(s.FleetId) is Fleet f && f.Side == s.SeenBy)
            .Select(s => $"{s.SeenBy} sighting of fleet {s.FleetId} is broken"))
        .Add("fleets-not-empty", w => w.Fleets.Where(f => f.ShipIds.Count == 0).Select(f => $"fleet {f.Id} {f.Name} has no ships"))
        .Add("supplies-in-range", w => w.Ships.Where(s => GameData.ShipClasses.TryGetValue(s.ClassId, out var c)
                && !(s.Supplies >= 0 && s.Supplies <= c.Supplies + 1e-6))
            .Select(s => $"ship {s.Id} {s.Name} has {s.Supplies} supplies"))
        .Add("ship-class-known", w => w.Ships.Where(s => !GameData.ShipClasses.ContainsKey(s.ClassId))
            .Select(s => $"ship {s.Id} {s.Name} has unknown class '{s.ClassId}'"))
        .Add("battle-step-in-range", w => w.Sub is < 0 or >= GameTime.SubSteps ? new[] { $"battle step {w.Sub}" } : Array.Empty<string>())
        .Add("armour-in-range", w => w.Ships.Where(s => GameData.ShipClasses.ContainsKey(s.ClassId)
                && !(s.Armour > 0 && s.Armour <= Tech.MaxArmour(w, s) + 1e-6) || s.Missiles < 0)
            .Select(s => $"ship {s.Id} {s.Name} has armour {s.Armour} and {s.Missiles} missiles"))
        .Add("missiles-sane", w => w.Missiles.Where(m => !m.Position.IsFinite || !m.Velocity.IsFinite || m.DeltaVLeft < -1e-9)
            .Select(m => $"missile {m.Id} is at {m.Position} moving {m.Velocity} with {m.DeltaVLeft} fuel"))
        .Add("battle-not-endless", w => w.ActiveBattle is { } b && w.Hour - b.StartHour > 30 * 24 && !Combat.Standoff(w)
            ? new[] { $"battle {b.Id} has gone on since {GameTime.Format(b.StartHour)}" } : Array.Empty<string>())
        .Add("ship-speed-sane", w => w.Ships.Where(s => !(s.MaxSpeed > 0 && double.IsFinite(s.MaxSpeed)))
            .Select(s => $"ship {s.Id} {s.Name} has max speed {s.MaxSpeed}"))
        .Add("industry-sane", w => w.Sides.Where(s => !(double.IsFinite(s.Industry) && s.Industry >= -1e-6))
            .Select(s => $"{s.Side} has {s.Industry} Industry"))
        .Add("orders-sane", w => w.Orders.Where(o => !double.IsFinite(o.HoursLeft) || o.HoursLeft > o.TotalHours + 1e-6
                || (o.Kind == OrderKind.Ship ? !GameData.ShipClasses.ContainsKey(o.Key) : !GameData.ResearchById.ContainsKey(o.Key)))
            .Select(o => $"order {o.Id} ({o.Kind} {o.Key}) has {o.HoursLeft} of {o.TotalHours} hours left"))
        .Add("claims-sane", w => w.Bodies.Where(b => b.ClaimHours < 0 || b.ClaimHours > GameData.Economy.ClaimDays * 24
                || (b.ClaimSide == null && b.ClaimHours != 0) || (b.ClaimSide != null && b.ClaimSide == b.Owner))
            .Select(b => $"{b.Name} claim by {b.ClaimSide} at {b.ClaimHours} hours (owner {b.Owner})"))
        .Add("shipyards-sane", w => w.Bodies.Where(b => b.Shipyard && (b.Owner == null || !(b.ShipyardArmour > 0)
                || b.ShipyardArmour > GameData.Economy.Shipyard.Armour + 1e-6))
            .Select(b => $"{b.Name} shipyard has armour {b.ShipyardArmour} and owner {b.Owner}"))
        .Add("winner-only-in-loop", w => w.Winner != null && !w.Loop ? new[] { "a sandbox game has a winner" } : Array.Empty<string>());

    private static IEnumerable<string> PositionsFinite(World w)
    {
        foreach (var b in w.Bodies)
            if (!b.Position.IsFinite) yield return $"body {b.Id} {b.Name} is at {b.Position}";
        foreach (var s in w.Ships)
        {
            if (!s.Position.IsFinite) yield return $"ship {s.Id} {s.Name} is at {s.Position}";
        }
        foreach (var f in w.Fleets)
        {
            if (!f.Position.IsFinite || !f.Velocity.IsFinite) yield return $"fleet {f.Id} {f.Name} is at {f.Position} moving {f.Velocity}";
            if (f.Flight is { } fl && fl.Legs.Any(l => !l.P0.IsFinite || !l.V0.IsFinite || !l.A.IsFinite || !double.IsFinite(l.End)))
                yield return $"fleet {f.Id} {f.Name} has a flight plan with bad numbers";
        }
    }

    private static IEnumerable<string> FleetsHoldTheirShips(World w)
    {
        foreach (var f in w.Fleets)
        {
            if (f.ShipIds.Count == 0) yield return $"fleet {f.Id} {f.Name} has no ships";
            foreach (var id in f.ShipIds)
                if (w.FindShip(id) is not Ship s) yield return $"fleet {f.Id} {f.Name} lists missing ship {id}";
                else if (s.FleetId != f.Id) yield return $"fleet {f.Id} {f.Name} lists ship {s.Name}, which thinks it is in fleet {s.FleetId}";
                else if (s.Side != f.Side) yield return $"fleet {f.Id} {f.Name} has ship {s.Name} from the other side";
        }
        foreach (var s in w.Ships)
            if (w.Fleets.Count(f => f.ShipIds.Contains(s.Id)) != 1)
                yield return $"ship {s.Id} {s.Name} is in {w.Fleets.Count(f => f.ShipIds.Contains(s.Id))} fleets";
    }

    private static IEnumerable<string> IdsUnique(World w)
    {
        var ids = w.Bodies.Select(b => b.Id).Concat(w.Ships.Select(s => s.Id)).Concat(w.Fleets.Select(f => f.Id)).ToList();
        foreach (var g in ids.GroupBy(i => i).Where(g => g.Count() > 1))
            yield return $"id {g.Key} is used {g.Count()} times";
        var max = ids.DefaultIfEmpty(0).Max();
        if (w.NextId <= max) yield return $"next id {w.NextId} is not above the highest id {max}";
    }

    private static IEnumerable<string> OrbitsHoldRadius(World w)
    {
        foreach (var b in w.Bodies)
        {
            if (b.ParentId is not int parentId) continue;
            var parent = w.FindBody(parentId);
            if (parent == null) { yield return $"body {b.Id} {b.Name} orbits missing body {parentId}"; continue; }
            var r = Vec2.Distance(b.Position, parent.Position);
            if (Math.Abs(r - b.OrbitRadius) > Math.Max(1e-6, b.OrbitRadius * 1e-6))
                yield return $"body {b.Id} {b.Name} is {r:0.###} Mm from {parent.Name}, orbit says {b.OrbitRadius:0.###}";
        }
    }
}
