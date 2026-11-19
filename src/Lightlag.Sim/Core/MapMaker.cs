namespace Lightlag.Sim.Core;

/// <summary>The kinds of map: the hand-made Hesper system, or one built from a seed.</summary>
public enum MapType { Hesper, SunLike, RedDwarf, Surprise }

/// <summary>How big a seeded map is: more planets, moons and rocks as it grows.</summary>
public enum MapSize { Small, Standard, Large }

/// <summary>
/// The map chosen for a game. Seeded maps are built by MapMaker from Seed: the same type, size and seed give the
/// same system on any computer. Which side gets which home comes from the game's own seed; SwapHomes swaps the
/// player's and the rival's homes (the "Swap sides" replay). Stored in the replay with the other game options.
/// </summary>
public sealed record MapSetup(MapType Type = MapType.SunLike, MapSize Size = MapSize.Standard, ulong Seed = 1, bool SwapHomes = false)
{
    public bool Seeded => Type != MapType.Hesper;
    public string Code => MapCodes.Format(this);
}

/// <summary>
/// Map codes: "SUN-STD-4821-3907" (type, size, the seed's eight digits). Hesper's code is just "HESPER".
/// Pasting a code back gives the same map.
/// </summary>
public static class MapCodes
{
    public const ulong MinSeed = 10_000_000, MaxSeed = 99_999_999;

    public static string TypeCode(MapType t) => t switch { MapType.SunLike => "SUN", MapType.RedDwarf => "RED", MapType.Surprise => "ANY", _ => "HESPER" };
    public static string SizeCode(MapSize s) => s switch { MapSize.Small => "SML", MapSize.Large => "LRG", _ => "STD" };

    public static string Name(MapType t) => t switch
    {
        MapType.SunLike => "Sun-like", MapType.RedDwarf => "Red dwarf", MapType.Surprise => "Surprise me", _ => "Hesper",
    };

    public static string SeedText(ulong seed) => $"{seed / 10000 % 10000:0000}-{seed % 10000:0000}";

    public static string Format(MapSetup m) =>
        m.Seeded ? $"{TypeCode(m.Type)}-{SizeCode(m.Size)}-{SeedText(m.Seed)}" : "HESPER";

    /// <summary>A seed in the range codes show (eight digits), from any number.</summary>
    public static ulong Fit(ulong n) => MinSeed + n % (MaxSeed - MinSeed + 1);

    /// <summary>
    /// Reads a map code, or a bare seed ("4821-3907", "48213907") which keeps the given type and size.
    /// Null when the text is neither.
    /// </summary>
    public static MapSetup? Parse(string? text, MapSetup current)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().ToUpperInvariant().Split(new[] { '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0] == "HESPER") return current with { Type = MapType.Hesper };
        var type = current.Type == MapType.Hesper ? MapType.SunLike : current.Type;
        var size = current.Size;
        var digits = new List<string>();
        foreach (var p in parts)
        {
            if (Enum.GetValues<MapType>().FirstOrDefault(t => t != MapType.Hesper && TypeCode(t) == p) is var t && TypeCode(t) == p) type = t;
            else if (Enum.GetValues<MapSize>().FirstOrDefault(s => SizeCode(s) == p) is var s && SizeCode(s) == p) size = s;
            else if (p.All(char.IsAsciiDigit)) digits.Add(p);
            else return null;
        }
        var joined = string.Concat(digits);
        if (joined.Length is 0 or > 8 || !ulong.TryParse(joined, out var seed)) return null;
        return new MapSetup(type, size, seed < MinSeed ? Fit(seed) : seed, current.SwapHomes);
    }
}

/// <summary>Names for seeded maps (Data/maps.json).</summary>
public sealed record MapNamesData(List<string>? Stars = null, List<string>? Worlds = null, List<string>? Belts = null);

/// <summary>What MapMaker made: the system, the home planets it picked (best first) and how fair they came out.</summary>
public sealed record MadeMap(SystemData Data, MapType Type, MapSize Size, string StarName, double StarLight, double FlareFactor,
    List<string> Homes, List<double> Scores, int Tries, bool Fair)
{
    public int Planets => Data.Bodies.Count(b => b.Kind == BodyKind.Planet);
    public int Moons => Data.Bodies.Count(b => b.Kind == BodyKind.Moon);
    public int Rocks => Data.Bodies.Count(b => b.Kind == BodyKind.Asteroid);
    public int Giants => Data.Bodies.Count(b => b.Type is "gas" or "ice-giant");
    public int WorthClaiming => Data.Bodies.Count(b => b.Kind != BodyKind.Star && (b.Industry ?? 0) > 0);
    public int Belts => (Data.Belts ?? new()).Count(b => b.Kind == BeltKind.Belt);

    /// <summary>How far apart the best and worst home scores are, as a share of the best (0 = equal).</summary>
    public double Spread => Scores.Count < 2 || Scores.Max() <= 0 ? 0 : (Scores.Max() - Scores.Min()) / Scores.Max();
}

/// <summary>
/// Builds a star system from a seed: the star, planets spaced like a real system (each orbit about 1.4 to 2 times
/// the last), kinds of world by distance from the star (rocky close in, terran and desert in the warm band, giants
/// past the frost line), moons, belts, rings and riches. Then the fair start check: every planet that could be a
/// home is scored by its neighbourhood (its own worth plus what it reaches first), and a set of homes is accepted
/// only when their scores are within 15 % of each other. A near miss gets a small top-up; otherwise the system is
/// rebuilt from the next inner seed, up to 30 tries. Writes the same SystemData as the hand-made map, so
/// everything that reads a map keeps working. Uses only SimRandom and plain arithmetic, so it is the same on
/// every computer.
/// </summary>
public static class MapMaker
{
    public const string Name = "generated";
    public const int MaxTries = 30;
    public const double FairSpread = 0.15, TopUpSpread = 0.4;
    public const int TopUpSteps = 12;

    /// <summary>A frigate's cruise, Mm a day, for the fair check's travel days.</summary>
    public const double CruiseMmPerDay = 500 * 24;

    /// <summary>How far (days) a home's neighbourhood reaches; what is nearer to it than to any other home is its own.</summary>
    public const double ReachDays = 40;

    /// <summary>Within this share of each other's travel days, a body counts half to each home: the contested middle.</summary>
    public const double ContestedShare = 1.25;

    private static readonly Lazy<MapNamesData> _names = new(() => Json.Deserialize<MapNamesData>(GameData.ReadText("maps.json")));
    public static MapNamesData Names => _names.Value;

    /// <summary>Fewest days apart homes may start (they are turned round the sun until they are).</summary>
    public static double MinHomeDays(MapType t) => t == MapType.RedDwarf ? 2 : 5;

    /// <summary>The kind a Surprise map turns into (from its seed).</summary>
    public static MapType Resolve(MapSetup m) =>
        m.Type != MapType.Surprise ? m.Type : new SimRandom(m.Seed, 90).NextDouble() < 0.6 ? MapType.SunLike : MapType.RedDwarf;

    /// <summary>Planets for a type and size: fewest and most.</summary>
    public static (int Min, int Max) PlanetRange(MapType t, MapSize s) => (t, s) switch
    {
        (MapType.RedDwarf, MapSize.Small) => (4, 5),
        (MapType.RedDwarf, MapSize.Standard) => (5, 6),
        (MapType.RedDwarf, MapSize.Large) => (6, 8),
        (_, MapSize.Small) => (5, 6),
        (_, MapSize.Large) => (8, 10),
        _ => (6, 8),
    };

    /// <summary>Builds the map for this setup and number of sides (2 or 3), with fair homes.</summary>
    public static MadeMap Make(MapSetup setup, int sides)
    {
        var type = Resolve(setup);
        sides = Math.Clamp(sides, 2, 3);
        MadeMap? best = null;
        for (var attempt = 0; attempt < MaxTries; attempt++)
        {
            var rng = new SimRandom(setup.Seed, 91 + (ulong)attempt);
            var built = Build(type, setup.Size, sides, rng);
            var picked = PickHomes(built, type, sides, rng);
            if (picked == null) continue;
            var (homes, scores) = picked.Value;
            var spread = SpreadOf(scores);
            if (spread > FairSpread && spread <= TopUpSpread)
            {
                built = TopUp(built, homes, scores, type, sides);
                scores = homes.Select(h => Score(built.Data, h, homes, type)).ToList();
                spread = SpreadOf(scores);
            }
            var made = new MadeMap(built.Data, type, setup.Size, built.StarName, built.Light, type == MapType.RedDwarf ? 2 : 1,
                homes, scores, attempt + 1, spread <= FairSpread);
            if (made.Fair) return made;
            if (best == null || made.Spread < best.Spread) best = made;
        }
        if (best != null) return best;
        // Should not happen (every build has warm planets with moons), but never leave a game without a map.
        var fallback = Build(type, setup.Size, sides, new SimRandom(setup.Seed, 90));
        var any = fallback.Data.Bodies.Where(CanBeHome(fallback.Data)).Select(b => b.Id).Take(sides).ToList();
        return new MadeMap(fallback.Data, type, setup.Size, fallback.StarName, fallback.Light, 1, any, any.Select(_ => 0.0).ToList(), MaxTries, false);
    }

    private static double SpreadOf(IReadOnlyList<double> scores) =>
        scores.Count < 2 || scores.Max() <= 0 ? 0 : (scores.Max() - scores.Min()) / scores.Max();

    // ---- Building the system ---------------------------------------------------------------------

    private sealed record Built(SystemData Data, string StarName, double Light);

    private sealed class NamePool
    {
        private readonly List<string> _left;
        private readonly SimRandom _rng;
        private int _n;
        public NamePool(IEnumerable<string> names, SimRandom rng) { _left = names.ToList(); _rng = rng; }
        public string Next()
        {
            if (_left.Count == 0) return $"World {++_n}";
            var i = _rng.Range(0, _left.Count);
            var s = _left[i];
            _left.RemoveAt(i);
            return s;
        }
    }

    private static string Id(string name) => new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());

    private static Built Build(MapType type, MapSize size, int sides, SimRandom rng)
    {
        var red = type == MapType.RedDwarf;
        var bodies = new List<BodyData>();
        var belts = new List<BeltData>();
        var starNames = Names.Stars is { Count: > 0 } s ? s : new List<string> { "Sol" };
        var starName = starNames[rng.Range(0, starNames.Count)];
        var worlds = new NamePool((Names.Worlds ?? new()).Where(n => n != starName), rng);

        // The star: mass, and from it brightness (light), size and colour.
        var mass = red ? rng.Range(0.22, 0.45) : rng.Range(0.8, 1.2);
        var light = red ? mass * mass * 0.25 : mass * mass * mass * mass;
        var root = Math.Sqrt(light); // distances scale with the square root of brightness
        var starColor = red ? (mass < 0.32 ? "ff7a4a" : "ff9258") : light > 1.25 ? "fff0c8" : light < 0.75 ? "ffb862" : "ffd27a";
        var starId = Id(starName);
        bodies.Add(new BodyData(starId, starName, BodyKind.Star, "star", null, null, null, mass, null, 696 * (red ? mass * 1.1 : mass), starColor, null));

        var (pMin, pMax) = PlanetRange(type, size);
        var count = rng.Range(pMin, pMax + 1);
        var giants = red ? (size == MapSize.Small ? 1 : size == MapSize.Standard ? rng.Range(1, 3) : 2)
                         : (size == MapSize.Small ? 2 : size == MapSize.Standard ? rng.Range(2, 4) : rng.Range(3, 5));
        // Three races need more worlds that can be homes: one more rocky planet in place of a giant where there is one to spare.
        // (Or one more planet, when there is only one giant.)
        if (sides >= 3 && giants > 1) giants--;
        else if (sides >= 3) count++;
        var rocky = Math.Max(3, count - giants);
        giants = count - rocky;
        var frost = 2.7 * root;

        // Rocky worlds spread evenly (by ratio) from close in to well inside the frost line.
        var first = root * rng.Range(0.32, 0.42);
        var last = root * rng.Range(1.9, 2.3);
        var step = Root(last / first, rocky - 1);
        var orbits = new List<double>();
        var at = first;
        for (var i = 0; i < rocky; i++)
        {
            orbits.Add(at * rng.Range(0.93, 1.07));
            at *= step;
        }
        var lastRocky = orbits[^1];
        var giantAt = frost * rng.Range(1.15, 1.5);
        var giantOrbits = new List<double>();
        for (var i = 0; i < giants; i++)
        {
            giantOrbits.Add(giantAt);
            giantAt *= rng.Range(1.6, 2.0);
        }

        // Asteroid belt between the rocky worlds and the giants (most maps), with a few rocks big enough to settle.
        var beltChance = size == MapSize.Small ? 0.7 : 1.0;
        var hasBelt = giantOrbits.Count > 0 && rng.NextDouble() < beltChance;
        var rockOrbits = new List<double>();
        if (hasBelt)
        {
            var inner = lastRocky * 1.2;
            var outer = Math.Max(inner * 1.25, giantOrbits[0] * 0.72);
            var names = Names.Belts is { Count: > 0 } bn ? bn : new List<string> { "The Belt" };
            var beltName = names[rng.Range(0, names.Count)];
            belts.Add(new BeltData(Id(beltName), beltName, starId, Round(inner), Round(outer), "8a8170"));
            var rocks = size switch { MapSize.Small => rng.Range(2, 4), MapSize.Large => rng.Range(4, 6), _ => rng.Range(3, 5) };
            for (var i = 0; i < rocks; i++) rockOrbits.Add(inner + (outer - inner) * (i + rng.Range(0.2, 0.8)) / rocks);
        }

        // Rocky planets: kind by distance (in units of the warm band), then moons.
        var kinds = new List<string>();
        foreach (var a in orbits)
        {
            var x = a / root;
            var roll = rng.NextDouble();
            kinds.Add(x < 0.55 ? "rocky"
                : x < 0.85 ? (roll < 0.75 ? "cloudy" : "rocky")
                : x < 1.5 ? (roll < 0.55 ? "terran" : roll < 0.85 ? "desert" : "cloudy")
                : roll < 0.6 ? "desert" : "rocky");
        }
        // Enough worlds that can be homes (one more than there are sides, so there is a choice): the nearest
        // warm-band-or-beyond worlds of other kinds become desert, or terran in the warm band.
        var wanted = sides + 1 - kinds.Count(k => HomeTypes.Contains(k));
        for (var i = 0; i < orbits.Count && wanted > 0; i++)
        {
            var x = orbits[i] / root;
            if (x < 0.8 || HomeTypes.Contains(kinds[i])) continue;
            kinds[i] = x < 1.5 && rng.NextDouble() < 0.5 ? "terran" : "desert";
            wanted--;
        }
        for (var i = 0; i < orbits.Count; i++)
        {
            var a = orbits[i];
            var x = a / root;
            var kind = kinds[i];
            var (radius, color, air, worth) = kind switch
            {
                "terran" => (rng.Range(5.6, 7.0), "4f86c6", "9fd0ff", 6.0),
                "desert" => (rng.Range(3.2, 4.6), "c1663b", "e8a07c", x < 1.5 ? 6.0 : 5.0),
                "cloudy" => (rng.Range(5.4, 6.3), "d9c48f", "f0e2b0", 3.0),
                _ => (rng.Range(2.2, 3.8), "9c8f84", (string?)null, 2.0),
            };
            var name = worlds.Next();
            var id = Id(name);
            bodies.Add(new BodyData(id, name, BodyKind.Planet, kind, starId, Round(a), null, null, Round(Cube(radius / 6.37), 4), Round(radius, 2),
                Shade(color, rng), air, worth));
            var moons = x < 0.55 ? 0 : kind == "rocky" ? rng.Range(0, 3) : rng.Range(1, 3);
            var moonAt = rng.Range(12.0, 60.0);
            for (var m = 0; m < moons; m++)
            {
                var r = m == 0 ? rng.Range(0.4, 1.8) : rng.Range(0.01, 0.3);
                var mName = worlds.Next();
                bodies.Add(new BodyData(Id(mName), mName, BodyKind.Moon, "rocky", id, null, Round(moonAt, 1), null, Round(Cube(r / 6.37) * 0.6, 8),
                    Round(r, 3), Shade("a7aab0", rng), null, 1));
                moonAt *= rng.Range(2.0, 4.0);
            }
        }

        foreach (var a in rockOrbits)
        {
            var name = worlds.Next();
            var r = rng.Range(0.22, 0.48);
            bodies.Add(new BodyData(Id(name), name, BodyKind.Asteroid, "asteroid", starId, Round(a), null, null, Round(Cube(r / 6.37) * 0.5, 8),
                Round(r, 2), Shade("8c8478", rng), null, 1));
        }

        // Giants: gas giants near the frost line are worth holding (fuel, moons); far ones and ice giants are scenery.
        for (var i = 0; i < giantOrbits.Count; i++)
        {
            var a = giantOrbits[i];
            var ice = i == giantOrbits.Count - 1 && (giantOrbits.Count >= 3 || a > 4.5 * frost);
            var near = !ice && a < 3 * frost;
            var name = worlds.Next();
            var id = Id(name);
            var radius = ice ? rng.Range(23.0, 26.5) : rng.Range(52.0, 72.0);
            var massE = ice ? rng.Range(13.0, 18.0) : rng.Range(80.0, 330.0);
            bodies.Add(new BodyData(id, name, BodyKind.Planet, ice ? "ice-giant" : "gas", starId, Round(a), null, null, Round(massE, 1), Round(radius, 1),
                Shade(ice ? "8fd3df" : "c9a27a", rng), ice ? "c4eef5" : "f2d6b3", near ? 3 : null));
            var moons = ice ? rng.Range(0, 3)
                : !near ? rng.Range(1, 3)
                : size switch { MapSize.Small => rng.Range(2, 4), MapSize.Large => rng.Range(3, 6), _ => rng.Range(3, 5) };
            var moonAt = rng.Range(380.0, 460.0);
            var hazy = false;
            for (var m = 0; m < moons; m++)
            {
                var roll = rng.NextDouble();
                var kind = m == 0 && roll < 0.4 ? "volcanic" : !hazy && roll > 0.8 ? "hazy" : roll < 0.6 ? "ice" : "rocky";
                hazy |= kind == "hazy";
                var r = rng.Range(1.2, 2.7);
                var mName = worlds.Next();
                var (color, air) = kind switch
                {
                    "volcanic" => ("d8c25a", (string?)null), "hazy" => ("c9933d", "e0b46a"), "ice" => ("d6d9dc", null), _ => ("a59f94", null),
                };
                bodies.Add(new BodyData(Id(mName), mName, BodyKind.Moon, kind, id, null, Round(moonAt, 1), null, Round(Cube(r / 6.37) * 0.55, 5),
                    Round(r, 2), Shade(color, rng), air, near ? 1 : null));
                moonAt *= rng.Range(1.45, 1.8);
            }
            if (!ice || rng.NextDouble() < 0.3)
                if (rng.NextDouble() < 0.65)
                    belts.Add(new BeltData(id + "-ring", $"{name}'s ring", id, Kind: BeltKind.Ring, InnerRadii: Round(rng.Range(1.2, 1.4), 2),
                        OuterRadii: Round(rng.Range(1.8, 2.4), 2), Density: Round(rng.Range(0.5, 1.5), 2), Color: Shade("9a8a72", rng)));
            if (!ice && rng.NextDouble() < 0.7)
                belts.Add(new BeltData(id + "-radiation", $"{name}'s radiation belt", id, Kind: BeltKind.Radiation, InnerRadii: Round(rng.Range(1.2, 1.6), 2),
                    OuterRadii: Round(rng.Range(3.0, 3.5), 2), Density: Round(rng.Range(0.4, 1.0), 2), Color: "7fd18a"));
        }

        // Parents before their moons, as the map loader needs: star, then each planet followed by its moons.
        var ordered = new List<BodyData> { bodies[0] };
        foreach (var p in bodies.Where(b => b.Parent == starId).OrderBy(b => b.OrbitRadiusMm))
        {
            ordered.Add(p);
            ordered.AddRange(bodies.Where(m => m.Parent == p.Id));
        }
        ordered = Trim(ordered, size, rng);
        var data = new SystemData(Name, ordered, belts, new List<StartShipsData>(), SizeScale: 5);
        return new Built(data, starName, light);
    }

    /// <summary>Bodies worth claiming a map aims for: about 7 to 9 a side in a one-on-one Standard game.</summary>
    public static (int Min, int Max) WorthRange(MapSize s) => s switch
    {
        MapSize.Small => (11, 14), MapSize.Large => (22, 28), _ => (15, 19),
    };

    /// <summary>
    /// Game length follows the bodies worth claiming, not the planets: moons and rocks picked at random (not round
    /// worlds that could be homes) lose their worth (they stay as scenery) until the count is in the size's range,
    /// or the nearest worthless ones gain a little if it is short. Random, not furthest first, so no part of the
    /// system is always poorer.
    /// </summary>
    private static List<BodyData> Trim(List<BodyData> bodies, MapSize size, SimRandom rng)
    {
        var (min, max) = WorthRange(size);
        var target = rng.Range(min, max + 1);
        var sun = new Dictionary<string, double>();
        foreach (var b in bodies) sun[b.Id] = b.Parent == null ? 0 : sun.TryGetValue(b.Parent, out var p) && p > 0 ? p : b.OrbitRadiusMm;
        bool Small(BodyData b) => b.Kind is BodyKind.Moon or BodyKind.Asteroid;
        var worth = bodies.Count(b => b.Kind != BodyKind.Star && (b.Industry ?? 0) > 0);
        var change = new Dictionary<string, double?>();
        if (worth > target)
            foreach (var b in Shuffled(bodies.Where(b => Small(b) && (b.Industry ?? 0) > 0 && b.Parent != null && !HomeTypes.Contains(ParentType(bodies, b))).ToList(), rng)
                         .Take(worth - target))
                change[b.Id] = null;
        else if (worth < target)
            foreach (var b in bodies.Where(b => Small(b) && (b.Industry ?? 0) <= 0).OrderBy(b => sun[b.Id]).ThenBy(b => b.Id).Take(target - worth))
                change[b.Id] = 1;
        return bodies.Select(b => change.TryGetValue(b.Id, out var v) ? b with { Industry = v } : b).ToList();
    }

    private static string ParentType(List<BodyData> bodies, BodyData b) => bodies.FirstOrDefault(p => p.Id == b.Parent)?.Type ?? "";

    private static List<T> Shuffled<T>(List<T> items, SimRandom rng)
    {
        var list = items.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    // ---- Fair homes ---------------------------------------------------------------------------

    /// <summary>Worlds a seeded map's homes go on: big enough for a home's people (Hesper allows more, see Scenarios.HomeTypes).</summary>
    public static readonly string[] HomeTypes = { "terran", "desert" };

    private static Func<BodyData, bool> CanBeHome(SystemData data) => b =>
        b.Kind == BodyKind.Planet && HomeTypes.Contains(b.Type) && data.Bodies.Any(m => m.Parent == b.Id && m.Kind == BodyKind.Moon);

    /// <summary>Distance (Mm) between two bodies' orbits round the star, averaged over where they might be on them.</summary>
    private static double MeanGap(double a, double b)
    {
        if (a <= 0 || b <= 0) return Math.Abs(a - b);
        var sum = 0.0;
        const int steps = 16;
        for (var i = 0; i < steps; i++)
        {
            var c = DetMath.Cos(Math.PI * (i + 0.5) / steps);
            sum += Math.Sqrt(Math.Max(0, a * a + b * b - 2 * a * b * c));
        }
        return sum / steps;
    }

    /// <summary>Each body's distance from the star (moons: their planet's), in Mm.</summary>
    private static Dictionary<string, double> SunDistances(SystemData data)
    {
        var d = new Dictionary<string, double>();
        foreach (var b in data.Bodies)
            d[b.Id] = b.Parent == null ? 0 : d.TryGetValue(b.Parent, out var p) && p > 0 ? p : b.OrbitRadiusMm;
        return d;
    }

    private static double Days(Dictionary<string, double> sun, string a, string b) => MeanGap(sun[a], sun[b]) / CruiseMmPerDay;

    /// <summary>What a body is worth holding: its Industry, and a little for room for people.</summary>
    private static double Worth(BodyData b)
    {
        if ((b.Industry ?? 0) <= 0) return 0;
        var t = Planets.D.Types != null && Planets.D.Types.TryGetValue(b.Type, out var pt) ? pt : null;
        var cap = (t?.Cap ?? 0) * (b.Kind == BodyKind.Moon ? Planets.D.MoonCapShare : 1);
        return (b.Industry ?? 0) + Math.Min(2, cap / 50);
    }

    /// <summary>
    /// A home's score: its own worth and the people it starts with, all it reaches first within ReachDays,
    /// half of what it shares with another home, and a little for a gas giant with moons in reach (fuel).
    /// </summary>
    public static double Score(SystemData data, string home, IReadOnlyList<string> homes, MapType type)
    {
        var sun = SunDistances(data);
        var h = data.Bodies.First(b => b.Id == home);
        var t = Planets.D.Types != null && Planets.D.Types.TryGetValue(h.Type, out var pt) ? pt : null;
        // The home's own worth, and its people: a home starts with up to HomeStartK thousand, as many as it can hold.
        var score = (h.Industry ?? 0) + Math.Min(Planets.D.HomeStartK, t?.Cap ?? 0) / 10;
        var reach = ReachDays * (type == MapType.RedDwarf ? 0.5 : 1);
        var fuel = false;
        foreach (var b in data.Bodies)
        {
            if (b.Kind == BodyKind.Star || homes.Contains(b.Id)) continue;
            var mine = Days(sun, home, b.Id);
            if (b.Type == "gas" && mine <= reach && (b.Industry ?? 0) > 0) fuel = true;
            var worth = Worth(b);
            if (worth <= 0 || mine > reach) continue;
            var nearestOther = homes.Where(o => o != home).Select(o => Days(sun, o, b.Id)).DefaultIfEmpty(double.MaxValue).Min();
            if (mine * ContestedShare <= nearestOther) score += worth;          // reached first
            else if (nearestOther * ContestedShare > mine) score += worth / 2; // contested: about as near to both
        }
        return score + (fuel ? 1 : 0);
    }

    private static (List<string> Homes, List<double> Scores)? PickHomes(Built built, MapType type, int sides, SimRandom rng)
    {
        var data = built.Data;
        var spots = data.Bodies.Where(CanBeHome(data)).Select(b => b.Id).ToList();
        if (spots.Count < sides) return null;
        var sun = SunDistances(data);
        var minDays = MinHomeDays(type);
        var fair = new List<(List<string> Homes, List<double> Scores, double Spread)>();
        (List<string>, List<double>, double)? closest = null;
        foreach (var set in Sets(spots, sides))
        {
            var gaps = new List<double>();
            for (var i = 0; i < set.Count; i++)
                for (var j = i + 1; j < set.Count; j++) gaps.Add(Days(sun, set[i], set[j]));
            if (gaps.Min() < minDays) continue;
            if (sides >= 3 && gaps.Max() > 2.5 * gaps.Min()) continue; // nobody squeezed between two rivals
            var scores = set.Select(h => Score(data, h, set, type)).ToList();
            var spread = SpreadOf(scores);
            if (spread <= FairSpread) fair.Add((set, scores, spread));
            if (closest == null || spread < closest.Value.Item3) closest = (set, scores, spread);
        }
        if (fair.Count > 0)
        {
            var pick = fair[rng.Range(0, fair.Count)];
            return (pick.Homes, pick.Scores);
        }
        return closest is var (h, s, _) ? (h, s) : null;
    }

    private static IEnumerable<List<string>> Sets(List<string> spots, int size)
    {
        if (size == 0) { yield return new List<string>(); yield break; }
        for (var i = 0; i <= spots.Count - size; i++)
            foreach (var rest in Sets(spots.Skip(i + 1).ToList(), size - 1))
            {
                rest.Insert(0, spots[i]);
                yield return rest;
            }
    }

    /// <summary>
    /// Close but not quite: one Industry at a time (at most TopUpSteps), alternately the poorest home gets a little
    /// more (its own moons first, up to 3 each, then the moons and rocks it reaches first, then the home itself, up to
    /// 8) and the richest home's surroundings a little less (a moon, rock or small planet it reaches first, never
    /// below 1), until the scores are within FairSpread.
    /// </summary>
    private static Built TopUp(Built built, List<string> homes, List<double> scores, MapType type, int sides)
    {
        var data = built.Data;
        var sun = SunDistances(data);
        for (var round = 0; round < TopUpSteps; round++)
        {
            var now = homes.Select(h => Score(data, h, homes, type)).ToList();
            if (SpreadOf(now) <= FairSpread) break;
            var poor = homes[now.IndexOf(now.Min())];
            var rich = homes[now.IndexOf(now.Max())];
            bool FirstTo(string home, BodyData b) => homes.All(o => o == home || Days(sun, home, b.Id) * ContestedShare <= Days(sun, o, b.Id));
            BodyData? Raise() =>
                data.Bodies.Where(b => b.Parent == poor && b.Kind == BodyKind.Moon && (b.Industry ?? 0) is > 0 and < 3)
                    .OrderBy(b => b.Industry).ThenBy(b => b.Id).FirstOrDefault()
                ?? data.Bodies.Where(b => b.Kind is BodyKind.Moon or BodyKind.Asteroid && (b.Industry ?? 0) is > 0 and < 3 && !homes.Contains(b.Id) && FirstTo(poor, b))
                    .OrderBy(b => Days(sun, poor, b.Id)).ThenBy(b => b.Id).FirstOrDefault()
                ?? data.Bodies.FirstOrDefault(b => b.Id == poor && (b.Industry ?? 0) < 8);
            BodyData? Lower() =>
                data.Bodies.Where(b => b.Kind != BodyKind.Star && !homes.Contains(b.Id) && (b.Industry ?? 0) > 1 && (b.Industry ?? 0) <= 3 && FirstTo(rich, b))
                    .OrderBy(b => Days(sun, rich, b.Id)).ThenBy(b => b.Id).FirstOrDefault();
            var (up, down) = (Raise(), Lower());
            (BodyData Body, int By)? step = round % 2 == 0
                ? up != null ? (up, 1) : down != null ? (down, -1) : null
                : down != null ? (down, -1) : up != null ? (up, 1) : null;
            if (step is not var (pick, by)) break;
            data = data with { Bodies = data.Bodies.Select(b => b.Id == pick.Id ? b with { Industry = (b.Industry ?? 0) + by } : b).ToList() };
        }
        return built with { Data = data };
    }

    // ---- Small helpers ------------------------------------------------------------------------

    private static double Cube(double x) => x * x * x;

    private static double Round(double x, int places = 3) => Math.Round(x, places);

    /// <summary>The n-th root of x (x above 1), by halving: the same on every computer.</summary>
    private static double Root(double x, int n)
    {
        if (n <= 1) return x;
        double lo = 1, hi = Math.Max(1, x);
        for (var i = 0; i < 60; i++)
        {
            var mid = (lo + hi) / 2;
            var p = 1.0;
            for (var k = 0; k < n; k++) p *= mid;
            if (p > x) hi = mid; else lo = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>A colour a little lighter or darker, so worlds of one kind don't all look the same.</summary>
    private static string Shade(string hex, SimRandom rng)
    {
        var k = rng.Range(0.85, 1.12);
        var v = Convert.ToInt32(hex, 16);
        int C(int shift) => Math.Clamp((int)Math.Round(((v >> shift) & 0xff) * k), 0, 255);
        return $"{C(16):x2}{C(8):x2}{C(0):x2}";
    }
}
