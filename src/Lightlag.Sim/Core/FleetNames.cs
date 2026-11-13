namespace Lightlag.Sim.Core;

/// <summary>
/// The player's fleets are named for what they are: their role ("Defence fleet", "Pirate patrol",
/// "Support fleet", "Supply fleet") or, with no role, their ships ("Battle fleet", "Settler fleet",
/// "Invasion fleet", "Troop fleet", "Supply fleet", "Scout fleet"). The first of a kind has no number;
/// the next ones get the lowest free number ("Defence fleet 2"). A fleet keeps its name while its kind
/// stays the same, and is renamed when the kind changes, unless the player gave it a name.
/// Rival fleets keep "Rival fleet N".
/// </summary>
public static class FleetNames
{
    public const int MaxLength = 32;

    /// <summary>What a fleet is called for its role or, with no role, its ships.</summary>
    public static string Kind(World w, Fleet f)
    {
        switch (f.Role)
        {
            case FleetRole.Defence: return "Defence fleet";
            case FleetRole.AntiPiracy: return "Pirate patrol";
            case FleetRole.Support: return "Support fleet";
            case FleetRole.Supply: return "Supply fleet";
            case FleetRole.ForwardGuard: return "Guard fleet";
        }
        var specs = w.ShipsOf(f).Select(Fitting.Spec).ToList();
        if (specs.Count == 0) return "Fleet";
        var armed = specs.Any(c => c.Railguns > 0 || c.Missiles > 0);
        if (specs.Any(c => c.CargoKind == "troops")) return armed ? "Invasion fleet" : "Troop fleet";
        if (specs.Any(c => c.CargoKind == "people")) return "Settler fleet";
        if (armed) return "Battle fleet";
        if (specs.All(c => c.Cargo > 0 && c.CargoKind is "fuel" or "food" or "mixed")) return "Supply fleet";
        if (specs.Any(c => c.Cargo > 0)) return "Cargo fleet";
        return "Scout fleet";
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
        if (f.Side != Side.Player || f.NamedByPlayer || f.ShipIds.Count == 0) return;
        var kind = Kind(w, f);
        if (KindOf(f.Name) == kind && f.Name.Length > 0) return;
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
