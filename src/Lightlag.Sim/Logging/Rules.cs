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
        .Add("ship-hold-body-exists", w => w.Ships.Where(s => s.HoldBodyId is int b && w.FindBody(b) == null)
            .Select(s => $"ship {s.Id} {s.Name} keeps station with missing body {s.HoldBodyId}"))
        .Add("ship-class-known", w => w.Ships.Where(s => !GameData.ShipClasses.ContainsKey(s.ClassId))
            .Select(s => $"ship {s.Id} {s.Name} has unknown class '{s.ClassId}'"))
        .Add("ship-speed-sane", w => w.Ships.Where(s => !(s.MaxSpeed > 0 && double.IsFinite(s.MaxSpeed)))
            .Select(s => $"ship {s.Id} {s.Name} has max speed {s.MaxSpeed}"));

    private static IEnumerable<string> PositionsFinite(World w)
    {
        foreach (var b in w.Bodies)
            if (!b.Position.IsFinite) yield return $"body {b.Id} {b.Name} is at {b.Position}";
        foreach (var s in w.Ships)
        {
            if (!s.Position.IsFinite) yield return $"ship {s.Id} {s.Name} is at {s.Position}";
            if (s.Destination is { IsFinite: false } d) yield return $"ship {s.Id} {s.Name} is heading to {d}";
        }
    }

    private static IEnumerable<string> IdsUnique(World w)
    {
        var ids = w.Bodies.Select(b => b.Id).Concat(w.Ships.Select(s => s.Id));
        foreach (var g in ids.GroupBy(i => i).Where(g => g.Count() > 1))
            yield return $"id {g.Key} is used {g.Count()} times";
        var max = w.Bodies.Select(b => b.Id).Concat(w.Ships.Select(s => s.Id)).DefaultIfEmpty(0).Max();
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
