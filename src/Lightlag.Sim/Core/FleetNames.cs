namespace Lightlag.Sim.Core;

/// <summary>
/// The player's fleets are named for what they are. With a role, for the role and the place it looks after:
/// "Verdant defence", "Rust run patrol", "Verdant response", "Verdant supply", "Rust guard" (the place is where it
/// last settled in orbit at one of its side's planets, its zone, or the planet it guards; without one, "Defence fleet",
/// "Patrol fleet", ...). With no role, for its ships ("Battle fleet", "Settler fleet", "Invasion fleet", "Troop fleet",
/// "Supply fleet", "Scout fleet"), or for what it is doing: "To Verdant defence" (ships on their way to join a fleet),
/// "New ships at Verdant" (launched ships waiting at the yard). The first of a kind has no number; the next ones get
/// the lowest free number. A fleet is renamed when its kind changes, unless the player gave it a name.
/// Rival fleets keep "Rival fleet N".
/// </summary>
public static class FleetNames
{
    public const int MaxLength = 32;

    /// <summary>What a fleet is called for its role or, with no role, its ships.</summary>
    public static string Kind(World w, Fleet f)
    {
        var kind = KindLong(w, f);
        return kind.Length <= MaxLength ? kind : kind[..MaxLength].TrimEnd();
    }

    private static string KindLong(World w, Fleet f)
    {
        var place = Place(w, f);
        switch (f.Role)
        {
            case FleetRole.Defence: return place != null ? $"{place} defence" : "Defence fleet";
            case FleetRole.AntiPiracy:
                return f.ZoneId is int z && w.Zones.Find(x => x.Id == z) is Zone zone ? $"{zone.Name} patrol" : place != null ? $"{place} patrol" : "Patrol fleet";
            case FleetRole.Support: return place != null ? $"{place} response" : "Response fleet";
            case FleetRole.Supply: return place != null ? $"{place} supply" : "Supply fleet";
            case FleetRole.ForwardGuard:
                return f.GuardBodyIds.Count == 1 && w.FindBody(f.GuardBodyIds[0]) is Body g ? $"{g.Name} guard"
                    : f.GuardBodyIds.Count == 2 && w.FindBody(f.GuardBodyIds[0]) is Body a && w.FindBody(f.GuardBodyIds[1]) is Body b ? $"{a.Name}-{b.Name} guard"
                    : "Guard fleet";
            case FleetRole.Scout: return "Scout";
        }
        if (f.TransferTo is int t && f.JoinFleetId == t && w.FindFleet(t) is Fleet to) return $"To {to.Name}";
        var specs = w.ShipsOf(f).Select(Fitting.Spec).ToList();
        if (specs.Count == 0) return "Fleet";
        if (f.YardPoolBodyId is int y && w.FindBody(y) is Body yard) return $"New ships at {yard.Name}";
        var armed = specs.Any(c => c.Railguns > 0 || c.Missiles > 0);
        if (specs.Any(c => c.CargoKind == "troops")) return armed ? "Invasion fleet" : "Troop fleet";
        if (specs.Any(c => c.CargoKind == "people")) return "Settler fleet";
        if (armed) return "Battle fleet";
        if (specs.All(c => c.Cargo > 0 && c.CargoKind is "fuel" or "food" or "mixed")) return "Supply fleet";
        if (specs.Any(c => c.Cargo > 0)) return "Cargo fleet";
        return "Scout fleet";
    }

    /// <summary>The place a role fleet is named for: the planet of its side it last settled in orbit at (null: none yet).</summary>
    public static string? Place(World w, Fleet f) => f.PlaceBodyId is int id && w.FindBody(id) is Body b ? b.Name : null;

    /// <summary>
    /// Keeps a fleet's naming facts current: the place it settles at (only while it sits in orbit at one of its side's
    /// planets with nothing else on, so a fleet out answering a call keeps its name), a finished transfer, a yard pool
    /// that has been given something to do.
    /// </summary>
    public static void Settle(World w, Fleet f)
    {
        if (f.TransferTo is int t && f.JoinFleetId != t) f.TransferTo = null;
        if (f.YardPoolBodyId is int y && (f.Role != FleetRole.None || f.Flight != null || f.Mission != Mission.None || f.ZoneId != null
                                          || w.FindBody(y) is not Body yard || !Economy.InOrbitAt(f, yard)))
            f.YardPoolBodyId = null;
        if (f.Flight == null && f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int o && w.FindBody(o) is Body b && b.Owner == f.Side
            && f.AnsweringCallId == null && f.SupplyRunFor == null && !f.Resupplying && f.Refit == null && !f.Retreating)
            f.PlaceBodyId = o;
    }

    /// <summary>The kind a name was made from: "Defence fleet 2" gives "Defence fleet".</summary>
    public static string KindOf(string name)
    {
        var space = name.LastIndexOf(' ');
        return space > 0 && int.TryParse(name.AsSpan(space + 1), out _) ? name[..space] : name;
    }

    /// <summary>Renames the fleet if its kind changed (player fleets the player has not named only).</summary>
    public static void Refresh(World w, Fleet f)
    {
        if (f.Side != Side.Player || f.ShipIds.Count == 0) return;
        Settle(w, f);
        if (f.NamedByPlayer) return;
        var kind = Kind(w, f);
        if ((f.Name == kind || KindOf(f.Name) == kind) && f.Name.Length > 0) return;
        f.Name = Next(w, f, kind);
    }

    /// <summary>Renames every player fleet whose kind changed (once an hour, and after loading an older save).</summary>
    public static void RefreshAll(World w)
    {
        foreach (var f in w.Fleets.OrderBy(f => f.Id)) Refresh(w, f);
    }

    /// <summary>The kind's name with the lowest number no other fleet of the side uses (no number for the first).</summary>
    private static string Next(World w, Fleet f, string kind)
    {
        var used = w.Fleets.Where(o => o.Id != f.Id && o.Side == f.Side).Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(kind)) return kind;
        for (var n = 2; ; n++)
            if (!used.Contains($"{kind} {n}")) return $"{kind} {n}";
    }

    /// <summary>Why the player can't give this fleet that name, or null.</summary>
    public static string? Problem(World w, Fleet f, string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return null; // empty: back to the automatic name
        if (n.Length > MaxLength) return $"At most {MaxLength} letters";
        if (w.Fleets.Any(o => o.Id != f.Id && o.Side == f.Side && string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase)))
            return "Another fleet has that name";
        return null;
    }
}

/// <summary>Name one of your fleets (a home order: at once, even while paused). An empty name goes back to the automatic name.</summary>
public sealed record RenameFleetCommand(Side Side, int FleetId, string Name) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindFleet(FleetId) is not Fleet f || f.Side != Side)
        {
            sim.Log.Warn("fleet", "Rename for a fleet that is not yours", new { side = Side.ToString(), FleetId, Source });
            return;
        }
        if (FleetNames.Problem(world, f, Name) is string problem)
        {
            sim.Log.Warn("fleet", "Can't rename the fleet", new { FleetId, Name, problem, Source });
            return;
        }
        var old = f.Name;
        var name = Name.Trim();
        if (name.Length == 0)
        {
            f.NamedByPlayer = false;
            f.Name = "";
            FleetNames.Refresh(world, f);
        }
        else
        {
            f.NamedByPlayer = true;
            f.Name = name;
        }
        sim.Log.Info("fleet", "Fleet renamed", new { FleetId, from = old, to = f.Name, byPlayer = f.NamedByPlayer, Source });
    }
}
