using System.Text;
using Lightlag.Sim.Core;

namespace Lightlag.Headless;

/// <summary>
/// "maps": builds many seeded maps without playing them and reports what came out: planets, moons, rocks,
/// bodies worth claiming, how many builds the fair start check needed and how close the home scores were.
/// --show prints one map body by body.
/// </summary>
public static class MapsReport
{
    public static int Run(Options opts)
    {
        var count = (int)(opts.GetLong("count") ?? 200);
        var first = opts.GetULong("seed") ?? 10_000_000;
        var sides = opts.Has("three") ? 3 : 2;
        var types = TypesFrom(opts.Get("type"));
        var sizes = SizesFrom(opts.Get("size"));

        if (opts.Get("show") is string code)
        {
            var setup = MapCodes.Parse(code, new MapSetup()) ?? throw new ArgumentException($"'{code}' is not a map code");
            Show(MapMaker.Make(setup, sides), setup);
            return 0;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Seeded maps, {count} seeds from {first}, {sides} sides");
        sb.AppendLine();
        sb.AppendLine("| Map | Planets | Moons | Rocks | Worth claiming | Builds (avg / max) | Fair | Home spread (avg / worst) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var type in types)
            foreach (var size in sizes)
            {
                var maps = Enumerable.Range(0, count).Select(i => MapMaker.Make(new MapSetup(type, size, MapCodes.Fit(first + (ulong)i)), sides)).ToList();
                string R(Func<MadeMap, double> f) => $"{maps.Min(f):0}-{maps.Max(f):0}";
                sb.AppendLine($"| {MapCodes.Name(type)} {size} | {R(m => m.Planets)} | {R(m => m.Moons)} | {R(m => m.Rocks)} | {R(m => m.WorthClaiming)} " +
                              $"| {maps.Average(m => m.Tries):0.0} / {maps.Max(m => m.Tries)} | {maps.Count(m => m.Fair) * 100 / maps.Count} % " +
                              $"| {maps.Average(m => m.Spread) * 100:0} % / {maps.Max(m => m.Spread) * 100:0} % |");
            }
        Console.Write(sb.ToString());
        return 0;
    }

    private static void Show(MadeMap m, MapSetup setup)
    {
        Console.WriteLine($"{setup.Code}: {MapCodes.Name(m.Type)} {m.Size}, star {m.StarName} (light {m.StarLight:0.000}), built in {m.Tries} tries, fair: {m.Fair}");
        Console.WriteLine($"Homes: {string.Join(", ", m.Homes.Zip(m.Scores, (h, s) => $"{h} {s:0.0}"))}");
        foreach (var b in m.Data.Bodies)
        {
            var indent = b.Kind == BodyKind.Moon ? "    " : b.Kind == BodyKind.Star ? "" : "  ";
            var orbit = b.OrbitAu is double au ? $"{au:0.000} AU" : b.OrbitMm is double mm ? $"{mm:0} Mm" : "";
            Console.WriteLine($"{indent}{b.Name,-14} {b.Kind,-8} {b.Type,-10} {orbit,-12} r {b.RadiusMm:0.###} Mm  industry {b.Industry ?? 0}");
        }
        foreach (var belt in m.Data.Belts ?? new())
            Console.WriteLine($"  {belt.Name} ({belt.Kind})");
    }

    private static List<MapType> TypesFrom(string? s) => s?.ToLowerInvariant() switch
    {
        null or "all" => new() { MapType.SunLike, MapType.RedDwarf },
        "sun" or "sunlike" or "sun-like" => new() { MapType.SunLike },
        "red" or "reddwarf" or "red-dwarf" => new() { MapType.RedDwarf },
        _ => throw new ArgumentException($"--type: sun, red or all, not '{s}'"),
    };

    private static List<MapSize> SizesFrom(string? s) => s?.ToLowerInvariant() switch
    {
        null or "all" => Enum.GetValues<MapSize>().ToList(),
        "small" => new() { MapSize.Small },
        "standard" or "std" => new() { MapSize.Standard },
        "large" => new() { MapSize.Large },
        _ => throw new ArgumentException($"--size: small, standard, large or all, not '{s}'"),
    };
}
