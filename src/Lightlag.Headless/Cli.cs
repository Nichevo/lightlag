using Lightlag.Sim;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;

namespace Lightlag.Headless;

/// <summary>Command-line entry: batch (overnight runs), battles (battle sweeps), replay, logs.</summary>
public static class Cli
{
    public const string Usage = """
        Lightlag headless runner

          batch   [--games N] [--seed S] [--days D] [--minutes M] [--scenario NAME] [--pilot ai|easy|easy-v-normal|test-v-ai|test] [--lag 1|0.1] [--autonomy strict|sensible|free] [--random-starts] [--spread] [--no-happiness] [--no-supply-lines] [--single-fuel] [--no-hazards] [--no-budget] [--events off|rare|normal|lively] [--player-race R] [--rival-race R] [--third-race R] [--data-dir DIR] [--lenient]
                  Plays N games, computer against computer (--pilot ai, the default; easy; easy-v-normal; test-v-ai, a random test pilot against the rival; or test, the test pilot on both sides), and writes a summary to <data>/nightly/<time>_batch/.
                  Rule checks run every tick and a broken rule counts as a crash, unless --lenient.
                  The summary lists where the time goes (each part of the hour, slowest first) and the slowest single step.
                  Stops starting new games after M minutes. The newest summary is also copied to
                  <data>/nightly/latest-summary.md (the "morning summary").
                  --player-race / --rival-race: human, hive, lattice or random (each game rolls its own). Default human; nightly: random.
                  --third-race: adds a second rival (a three-race game): human, hive, lattice or random. Default none.
          nightly Same as batch with overnight defaults (--games 200 --days 365 --minutes 360, races random).

          battles [--repeats N] [--seed S] [--data-dir DIR]
                  Battle sweep: every fleet mix x approach x missile brain in the sandbox, N times each.
                  Writes battles.csv and a summary to <data>/nightly/<time>_battles/ and latest-battles.md.

          replay  <replay.json | save.json> [--until-tick T]
                  Replays a game and checks it matches what was recorded. Exit code 0 = exact.

          ai-trace [--seed S] [--days D] [--every N] [--easy] [--economy] [--thoughts] [--player-race R] [--rival-race R]
                  Plays one computer-vs-computer game and prints each side's goal and fleet jobs every N days.
                  --economy also prints each held planet's buildings and materials, and what the yard waits for; --thoughts its recent thoughts.

          logs    <file.jsonl | folder> [--level warn] [--area rule] [--text words] [--seed S] [--days A-B] [--max N]
                  Searches game logs. --level shows that level and worse.

        Data folder: --data-dir, else the LIGHTLAG_DATA environment variable, else ./data.
        """;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }
        var opts = Options.Parse(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "batch" => Batch.Run(opts, nightly: false),
                "nightly" => Batch.Run(opts, nightly: true),
                "battles" => BattleSweep.Run(opts),
                "replay" => RunReplay(opts),
                "logs" => LogSearch.Run(opts),
                "ai-trace" => AiTrace.Run(opts),
                _ => Fail($"Unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (ArgumentException ex)
        {
            return Fail(ex.Message);
        }
    }

    public static BuildInfo LoadBuild() => BuildInfo.Load(AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, ".."));

    public static DataPaths DataFolder(Options opts) =>
        new DataPaths(opts.Get("data-dir") ?? Environment.GetEnvironmentVariable("LIGHTLAG_DATA") ?? "data").EnsureCreated();

    private static int RunReplay(Options opts)
    {
        var file = opts.Positional.FirstOrDefault() ?? throw new ArgumentException("replay needs a replay.json or save.json file");
        var text = File.ReadAllText(file);
        var replay = text.Contains("\"world\"") ? SaveGame.FromJson(text).Replay : ReplayFile.FromJson(text);
        using var log = GameLog.Memory(LoadBuild());
        log.MinLevel = LogLevel.Warn;
        log.Echo = (_, line) => Console.WriteLine(line);
        Console.WriteLine($"Replaying {replay.Scenario} seed {replay.Seed}, recorded by build {replay.Build}, this is {log.Build}");
        var until = opts.GetLong("until-tick");
        var result = ReplayRunner.Run(replay, log, until);
        Console.WriteLine(result.Message);
        if (result.Error != null) Console.WriteLine(result.Error);
        return result.Exact ? 0 : 1;
    }

    public static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }
}

/// <summary>Tiny "--name value" parser so the runner has no dependencies.</summary>
public sealed class Options
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = new();

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (!a.StartsWith("--")) { o.Positional.Add(a); continue; }
            var name = a[2..];
            string? value = null;
            var eq = name.IndexOf('=');
            if (eq >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
            else if (i + 1 < list.Count && !list[i + 1].StartsWith("--")) value = list[++i];
            o._named[name] = value;
        }
        return o;
    }

    public bool Has(string name) => _named.ContainsKey(name);
    public string? Get(string name) => _named.TryGetValue(name, out var v) ? v : null;

    public long? GetLong(string name)
    {
        var v = Get(name);
        if (v == null) return null;
        return long.TryParse(v, out var n) ? n : throw new ArgumentException($"--{name} needs a whole number, got '{v}'");
    }

    public ulong? GetULong(string name)
    {
        var v = Get(name);
        if (v == null) return null;
        return ulong.TryParse(v, out var n) ? n : throw new ArgumentException($"--{name} needs a whole number, got '{v}'");
    }
}
