namespace Lightlag.Sim.Core;

/// <summary>The ship name lists (Data/names.json): by race, then by theme (see ShipNames.Theme).</summary>
public sealed class NamesData
{
    public Dictionary<string, Dictionary<string, List<string>>> Races { get; set; } = new();
}

/// <summary>
/// Every ship has a designation code and a name. The code says what it is: its type, hull mark and
/// design variant, then its hull number, e.g. "DD3B-07" (missile destroyer, mark 3 hull, variant B, hull 7).
/// The hull mark is 1, 2 with Heavy armour and 3 with Armour Mk III; the variant is the ship's fit among
/// the fits its side has built of that class (A is the standard fit). A refit to a new version changes the
/// code; the hull number and the name stay. The name comes from the side's race and the kind of world
/// that built it (a water world gives fish names, the home yard old ships and virtues; see names.json),
/// picked at random from the game's seed among names no living ship has. A captured prize keeps its
/// name, with "Prize" in front. The player can rename a ship (RenameShipCommand).
/// </summary>
public static class ShipNames
{
    private static readonly Lazy<NamesData> _data = new(() => Json.Deserialize<NamesData>(GameData.ReadText("names.json")));
    public static NamesData Data => _data.Value;

    public const string DefaultRace = "human";
    public const int MaxLength = 32;

    /// <summary>The race of a side (both are human until races come in).</summary>
    public static string Race(World w, Side side) => Economy.Of(w, side).Race is { Length: > 0 } r && Data.Races.ContainsKey(r) ? r : DefaultRace;

    /// <summary>The name theme for a ship built at this yard (null: a start ship, named like the home yard's).</summary>
    public static string Theme(World w, Body? yard)
    {
        if (yard == null || yard.Shipyard) return "home";
        var parent = yard.ParentId is int p ? w.FindBody(p) : null;
        if (yard.Kind == BodyKind.Moon && parent?.Type is "gas" or "ice-giant") return "storm";
        return yard.Type switch
        {
            "terran" or "ocean" => "water",
            "ice" or "ice-giant" => "ice",
            "cloudy" or "hazy" or "gas" => "storm",
            _ => "rocky",
        };
    }

    /// <summary>Hull mark from armour research: 1, 2 (Heavy armour), 3 (Armour Mk III).</summary>
    public static int HullMark(World w, Side side) =>
        1 + (Tech.Has(w, side, "heavy-armour") ? 1 : 0) + (Tech.Has(w, side, Tech.ArmourMk3) ? 1 : 0);

    private static string FitKey(IEnumerable<string> fit) => string.Join(",", fit);

    /// <summary>The design variant letter of the ship's fit: A for the class's standard fit, then B, C... in the order the side first built them.</summary>
    public static char Variant(World w, Ship s)
    {
        var cls = GameData.ShipClass(s.ClassId);
        var seen = Economy.Of(w, s.Side).Variants;
        if (!seen.TryGetValue(cls.Id, out var list)) seen[cls.Id] = list = new List<string> { FitKey(Fitting.StandardFit(cls)) };
        var key = FitKey(Fitting.FitOf(s));
        var at = list.IndexOf(key);
        if (at < 0) { list.Add(key); at = list.Count - 1; }
        return (char)('A' + Math.Min(at, 25));
    }

    /// <summary>The variant letter a fit has (read-only: null if the side has never built that fit).</summary>
    public static char? VariantLetter(World w, Side side, ShipClass cls, IReadOnlyList<string> fit)
    {
        var key = FitKey(fit);
        if (key == FitKey(Fitting.StandardFit(cls))) return 'A';
        if (!Economy.Of(w, side).Variants.TryGetValue(cls.Id, out var list)) return null;
        var at = list.IndexOf(key);
        return at < 0 ? null : (char)('A' + Math.Min(at, 25));
    }

    public static string CodeOf(World w, Ship s) => $"{GameData.ShipClass(s.ClassId).Short}{HullMark(w, s.Side)}{Variant(w, s)}-{s.HullNumber:00}";

    /// <summary>A new ship: its hull number, code and name.</summary>
    public static void Christen(World w, Ship s, int hullNumber, Body? yard)
    {
        s.HullNumber = hullNumber;
        s.Code = CodeOf(w, s);
        s.Name = Pick(w, s.Side, Theme(w, yard), s.Id);
    }

    /// <summary>After a refit: a new version gets a new code (same hull number and name).</summary>
    public static void Recode(World w, Ship s)
    {
        if (s.HullNumber > 0) s.Code = CodeOf(w, s);
    }

    /// <summary>The name without a "Prize " in front (prizes keep their names).</summary>
    public static string Bare(string name) => name.StartsWith("Prize ", StringComparison.Ordinal) ? name[6..] : name;

    /// <summary>Is the name taken by a living ship (other than this one)?</summary>
    public static bool Taken(World w, string name, int exceptShipId = 0) =>
        w.Ships.Any(s => s.Id != exceptShipId && string.Equals(Bare(s.Name), Bare(name), StringComparison.OrdinalIgnoreCase));

    /// <summary>A name from the theme's list (then the race's "any" list, then any list) that no living ship has; numbered if all are taken.</summary>
    public static string Pick(World w, Side side, string theme, int salt)
    {
        var lists = Data.Races.TryGetValue(Race(w, side), out var race) ? race : Data.Races.Values.First();
        var used = new HashSet<string>(w.Ships.Select(s => Bare(s.Name)), StringComparer.OrdinalIgnoreCase);
        var rng = new SimRandom(w.Seed ^ (ulong)(salt * 2654435761L), 77 + (ulong)side);
        IEnumerable<List<string>> order = new[] { theme, "any" }.Where(lists.ContainsKey).Select(t => lists[t]).Concat(lists.Values);
        foreach (var list in order)
        {
            var free = list.Where(n => !used.Contains(n)).ToList();
            if (free.Count > 0) return free[rng.Range(0, free.Count)];
        }
        var all = lists.Values.SelectMany(l => l).ToList();
        var basis = all.Count > 0 ? all[rng.Range(0, all.Count)] : "Ship";
        for (var i = 2; ; i++)
        {
            var n = $"{basis} {Roman(i)}";
            if (!used.Contains(n)) return n;
        }
    }

    private static string Roman(int n)
    {
        var vals = new[] { (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var s = "";
        foreach (var (v, r) in vals) while (n >= v) { s += r; n -= v; }
        return s;
    }

    /// <summary>Why this name can't be given to the ship, or null.</summary>
    public static string? Problem(World w, Ship s, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "A name can't be empty";
        if (name.Length > MaxLength) return $"At most {MaxLength} letters";
        if (Taken(w, name, s.Id)) return "Another ship has that name";
        return null;
    }
}

/// <summary>Rename one of your ships (a home order: at once, even while paused).</summary>
public sealed record RenameShipCommand(Side Side, int ShipId, string Name) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindShip(ShipId) is not Ship s || s.Side != Side)
        {
            sim.Log.Warn("ships", "Rename for a ship that is not yours", new { side = Side.ToString(), ShipId, Source });
            return;
        }
        if (ShipNames.Problem(world, s, Name) is string problem)
        {
            sim.Log.Warn("ships", $"Can't rename the ship: {problem}", new { ShipId, Name, Source });
            return;
        }
        var old = s.Name;
        s.Name = Name.Trim();
        sim.Log.Info("ships", $"{old} is renamed {s.Name}", new { ShipId, code = s.Code, Source });
    }
}
