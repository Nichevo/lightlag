using System.Diagnostics;
using System.Text;
using Lightlag.Sim;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;

namespace Lightlag.Headless;

public sealed record GameResult(
    int Index, ulong Seed, string Outcome, long Ticks, double Seconds, int RuleFailures,
    int Errors, int Warnings, string? Error, string? ReportFolder, string? Winner = null, int? WinDay = null,
    string? WinReason = null, string? PlayerMind = null, string? RivalMind = null, SideEnd? Player = null, SideEnd? Rival = null,
    double? WarshipsMid = null, double[]? WarshipCurve = null, double WorstStepMs = 0, long WorstStepTick = 0,
    string? PlayerRace = null, string? RivalRace = null, string? ThirdRace = null, SideEnd? Third = null);

/// <summary>One side at the end of a batch game (for the summary).</summary>
public sealed record SideEnd(int Planets, int Ships, double Power, int ShipsBuilt, int ShipsLost, int Battles, int PlanetsClaimed, double ShipyardArmour,
    int LaggedBattles = 0, int FlagshipBattles = 0, int OwnDecisions = 0, int Overheats = 0,
    int ShipsCrippled = 0, int PartsKnocked = 0, int PullBacks = 0, int PulledBeforeShots = 0, int Wiped = 0, int OneWithdrew = 0, int BothWithdrew = 0,
    double MaterialStallHours = 0, double Salvaged = 0, int Warships = 0, int Contacts = 0,
    int AmbushCatches = 0, double CargoCaptured = 0, int CargoLost = 0, int RockHits = 0, double HazardHull = 0, double RadiationShipHours = 0, int Events = 0,
    double LiftSpent = 0, int SailShips = 0, int SatellitesLaunched = 0, int SatellitesLost = 0,
    int Batteries = 0, double PeopleLeftK = 0, int WorldsAbandoned = 0, int ShedHours = 0)
{
    public static SideEnd Of(World w, Side side)
    {
        var t = Stats.Of(w, side);
        return new SideEnd(Economy.Claimable(w).Count(b => b.Owner == side), w.Ships.Count(s => s.Side == side),
            Math.Round(Stats.Power(w, side)), t.ShipsBuilt, t.ShipsLost, t.Battles, t.PlanetsClaimed,
            Math.Round(Economy.ShipyardOf(w, side)?.ShipyardArmour ?? 0), t.LaggedBattles, t.FlagshipBattles, t.OwnDecisions, t.Overheats,
            t.ShipsCrippled, t.PartsKnocked, t.PullBacks, t.PulledBeforeShots, t.BattlesWiped, t.BattlesOneWithdrew, t.BattlesBothWithdrew,
            t.MaterialStallHours, t.Salvaged, w.Ships.Count(s => s.Side == side && Planner.Warship(s)), t.Contacts,
            t.AmbushCatches, Math.Round(t.CargoCaptured), Economy.Of(w, side).CargoLost, t.RockHits, Math.Round(t.HazardHull), Math.Round(t.RadiationShipHours), t.Events,
            Math.Round(t.LiftSpent), w.Ships.Count(s => s.Side == side && Sails.Is(s)), t.SatellitesLaunched, t.SatellitesLost,
            w.Bodies.Where(b => b.Owner == side).Sum(b => Lightlag.Sim.Core.Planets.Count(b, Grid.Battery)), Math.Round(t.PeopleLeftK, 1), t.WorldsAbandoned, t.ShedHours);
    }
}

/// <summary>Runs many games back to back and writes the morning summary.</summary>
public static class Batch
{
    public static int Run(Options opts, bool nightly)
    {
        var build = Cli.LoadBuild();
        var data = Cli.DataFolder(opts);
        var games = (int)(opts.GetLong("games") ?? (nightly ? 200 : 10));
        var days = (int)(opts.GetLong("days") ?? (nightly ? 365 : 120));
        var minutes = opts.GetLong("minutes") ?? (nightly ? 360 : 60);
        var scenario = opts.Get("scenario") ?? Scenarios.TestSystem;
        var strict = !opts.Has("lenient");
        var pilot = opts.Get("pilot") ?? "ai";
        if (pilot is not ("ai" or "test" or "easy" or "easy-v-normal" or "test-v-ai"))
            throw new ArgumentException("--pilot must be ai, easy, easy-v-normal, test-v-ai or test");
        // --lag 1 plays with real light lag, --lag 0.1 with light ten times slower; off by default.
        _light = opts.Get("lag") is string lag
            ? new LightSetup(true, double.Parse(lag, System.Globalization.CultureInfo.InvariantCulture)) : null;
        // --autonomy strict|sensible|free: what fleets decide for themselves when cut off (default sensible).
        _autonomy = opts.Get("autonomy") is string a ? Enum.Parse<AutonomyLevel>(a, ignoreCase: true) : null;
        // --random-starts, --spread (start with some people everywhere), --no-happiness, --no-supply-lines, --single-fuel: the optional systems.
        _rules = (opts.Has("random-starts"), opts.Has("spread"), opts.Has("no-happiness"), opts.Has("no-supply-lines"), opts.Has("single-fuel"));
        // --no-hazards: no rocks, radiation or sun heat; --events off|rare|normal|lively (default normal).
        _noHazards = opts.Has("no-hazards");
        // --no-budget: one Industry pool, first come first served (to compare with the budget).
        _noBudget = opts.Has("no-budget");
        _events = opts.Get("events") is string ev ? Enum.Parse<EventRate>(ev, ignoreCase: true) : null;
        // --player-race / --rival-race human|hive|random: each side's race (batch: human; nightly: random, so both races get played).
        _races = (opts.Get("player-race") ?? (nightly ? Races.Random : null), opts.Get("rival-race") ?? (nightly ? Races.Random : null));
        // --third-race human|hive|lattice|random: a three-race game with a second rival (default none).
        _third = opts.Get("third-race") is string third && third != Races.None ? third : null;
        foreach (var r in new[] { _races.Player, _races.Rival, _third })
            if (r != null && r != Races.Random && !Races.Known(r)) throw new ArgumentException($"Unknown race '{r}': use {string.Join(", ", Races.All.Select(x => x.Id))} or random");
        var firstSeed = opts.GetULong("seed") ?? (ulong)DateTime.UtcNow.Ticks % 1_000_000_000UL;

        var started = DateTime.UtcNow;
        var runDir = Path.Combine(data.Nightly, DataPaths.StampedName(started, nightly ? "nightly" : "batch"));
        var runPaths = new DataPaths(runDir).EnsureCreated();
        var logPath = Path.Combine(runDir, "log.jsonl");
        Console.WriteLine($"Lightlag {build}: {games} games of {days} days, {scenario}, first seed {firstSeed}, writing to {runDir}");

        var results = new List<GameResult>();
        WhatMatters.Rows.Clear();
        ResearchReport.Rows.Clear();
        BudgetReport.Rows.Clear();
        _report = null;
        Overnight.Fights.Clear();
        Audit.Clear();
        Overnight.Raids.Clear();
        Timing.Clear();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < games; i++)
        {
            if (clock.Elapsed.TotalMinutes >= minutes)
            {
                Console.WriteLine($"Time budget of {minutes} minutes used; stopping after {i} games.");
                break;
            }
            var seed = firstSeed + (ulong)i;
            var r = PlayOne(i + 1, seed, scenario, days, strict, build, logPath, runPaths, pilot);
            results.Add(r);
            Console.WriteLine($"  game {r.Index,4}  seed {r.Seed,-12} {r.Outcome,-9} {r.Seconds,7:0.00}s  rules {r.RuleFailures}  errors {r.Errors}"
                              + (r.Winner != null ? $"  {r.Winner} wins day {r.WinDay} ({r.WinReason})" : "  no winner")
                              + (r.PlayerMind != null ? $"  [{r.PlayerMind} v {r.RivalMind}]" : "")
                              + (r.PlayerRace != Races.Human || r.RivalRace != Races.Human || r.ThirdRace != null ? $"  ({r.PlayerRace} v {r.RivalRace}{(r.ThirdRace != null ? $" v {r.ThirdRace}" : "")})" : "")
                              + (r.Error != null ? $"  {r.Error}" : ""));
        }

        var summary = Summarise(build, scenario, days, strict, started, clock.Elapsed, games, results, runDir, pilot);
        File.WriteAllText(Path.Combine(runDir, "summary.md"), summary);
        if (_report != null) File.WriteAllText(Path.Combine(runDir, "summary.html"), _report);
        File.WriteAllText(Path.Combine(runDir, "summary.json"), Json.Serialize(new
        {
            build = build.ToString(), scenario, days, strict, started = started.ToString("o"),
            seconds = clock.Elapsed.TotalSeconds, requested = games, results,
        }, indented: true));
        File.WriteAllText(Path.Combine(data.Nightly, "latest-summary.md"), summary);
        File.WriteAllText(Path.Combine(data.Nightly, "latest-summary.json"), Json.Serialize(new
        {
            folder = runDir, build = build.ToString(), finished = DateTime.UtcNow.ToString("o"),
            games = results.Count,
            crashed = results.Count(r => r.Outcome != "ok"),
            ruleFailures = results.Sum(r => r.RuleFailures),
            averageSeconds = results.Count > 0 ? results.Average(r => r.Seconds) : 0,
        }, indented: true));

        Console.WriteLine();
        Console.WriteLine(summary);
        return results.Any(r => r.Outcome != "ok") ? 1 : 0;
    }

    private static LightSetup? _light;

    /// <summary>The overnight report as a page (summary.html), when the batch had computer-vs-computer games.</summary>
    private static string? _report;

    /// <summary>Headless games use the full choice spread (normal games a smaller one), so the computer plays more varied games to compare.</summary>
    public const double HeadlessSpread = 0.1;

    private static GameOptions? OptionsFor(string pilot) =>
        _races != default || _third != null
            ? (OptionsForRules(pilot) ?? new GameOptions()) with { PlayerRace = _races.Player, RivalRace = _races.Rival, ThirdRace = _third }
            : OptionsForRules(pilot);

    private static string? _third;

    private static (string? Player, string? Rival) _races;

    private static GameOptions? OptionsForRules(string pilot) =>
        _light == null && _autonomy == null && _rules == default && !_noHazards && !_noBudget && _events == null ? BaseOptions(pilot) is GameOptions basic ? basic with { ChoiceSpread = HeadlessSpread } : null
            : (BaseOptions(pilot) ?? new GameOptions()) with
            {
                ChoiceSpread = HeadlessSpread,
                Light = _light, Autonomy = _autonomy,
                RandomStarts = _rules.Random ? true : null, StartPopulation = _rules.Spread ? true : null,
                Happiness = _rules.NoHappiness ? false : null, SupplyLines = _rules.NoSupply ? false : null, FuelTypes = _rules.SingleFuel ? false : null,
                Hazards = _noHazards ? false : null, Events = _events, Budget = _noBudget ? false : null,
            };

    private static bool _noHazards;
    private static bool _noBudget;
    private static EventRate? _events;

    private static (bool Random, bool Spread, bool NoHappiness, bool NoSupply, bool SingleFuel) _rules;

    private static AutonomyLevel? _autonomy;

    private static GameOptions? BaseOptions(string pilot) => pilot switch
    {
        "test" => null,
        "easy" => GameOptions.AiVsAi(Difficulty.Easy),
        "easy-v-normal" => new GameOptions(new List<AiSetup> { new(Side.Player, Difficulty.Easy), new(Side.Rival, Difficulty.Normal) }),
        "test-v-ai" => GameOptions.VsRival(Difficulty.Normal),
        _ => GameOptions.AiVsAi(),
    };

    private static string Describe(string pilot) => pilot switch
    {
        "test" => "test pilot on both sides",
        "easy" => "computer on both sides, Easy",
        "easy-v-normal" => "player side Easy computer, rival Normal computer",
        "test-v-ai" => "player side test pilot, rival Normal computer",
        _ => "computer on both sides, Normal",
    };

    private static GameResult PlayOne(int index, ulong seed, string scenario, int days, bool strict,
                                      BuildInfo build, string logPath, DataPaths runPaths, string pilotKind)
    {
        using var log = new GameLog(build, logPath);
        var timer = Stopwatch.StartNew();
        Simulation? sim = null;
        // Time each part of the step (see Timing): the batch summary lists where the time goes.
        var sections = new SectionTimer(Stopwatch.GetTimestamp, Stopwatch.Frequency);
        SectionTimer.Active = sections;
        double worstMs = 0;
        long worstTick = 0;
        try
        {
            var options = OptionsFor(pilotKind);
            sim = Simulation.NewGame(scenario, seed, log, build, options: options);
            sim.StrictRules = strict;
            sim.RuleCheckEveryTicks = 1;
            var end = (long)days * GameTime.TicksPerDay;
            // The test pilot plays any side the computer does not. The game stops when someone wins.
            var testPilot = options?.Ai is { Count: 2 } ? null : new TestPilot(seed);
            while ((sim.World.Tick < end || sim.World.Sub != 0) && sim.World.Winner == null)
            {
                testPilot?.Act(sim);
                var before = Stopwatch.GetTimestamp();
                sim.Step();
                var ms = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                if (ms > worstMs) (worstMs, worstTick) = (ms, sim.World.Tick);
            }
            Stats.RecordDay(sim.World);
            log.Info("game", $"Game finished at {GameTime.Format(sim.World.Tick)}", new { hash = sim.StateHash() });
            var w = sim.World;
            Directory.CreateDirectory(Path.Combine(runPaths.Root, "stats"));
            File.WriteAllText(Path.Combine(runPaths.Root, "stats", $"game-{index:000}-seed-{seed}.csv"), Stats.ToCsv(w));
            File.WriteAllText(Path.Combine(runPaths.Root, "stats", $"game-{index:000}-seed-{seed}-choices.csv"), Choices.ToCsv(w));
            if (w.Minds.Count >= 2)
                foreach (var side in Sides.InPlay(w))
                {
                    WhatMatters.Rows.Add(WhatMatters.Row.Of(w, side));
                    ResearchReport.Rows.Add(ResearchReport.Of(w, side));
                    WarReport.Rows.Add(WarReport.Row.Of(w, side));
                    if (BudgetReport.Row.Of(w, side) is BudgetReport.Row budgetRow) BudgetReport.Rows.Add(budgetRow);
                }
            Overnight.Collect(w);
            Audit.Collect(w);
            var (mid, curve) = Overnight.Warships(w, days);
            string? MindName(Side side) => Mind.Of(w, side) is MindState m ? $"{m.Personality} {m.BaseRatio:0.00}" : null;
            return new GameResult(index, seed, "ok", w.Tick, timer.Elapsed.TotalSeconds, sim.RuleViolations,
                log.Count(LogLevel.Error), log.Count(LogLevel.Warn), null, null,
                w.Winner?.ToString(), w.WinHour is double h ? (int)(h / 24) : null, w.WinReason,
                MindName(Side.Player), MindName(Side.Rival), SideEnd.Of(w, Side.Player), SideEnd.Of(w, Side.Rival), mid, curve, Math.Round(worstMs, 1), worstTick,
                Economy.Of(w, Side.Player).Race, Economy.Of(w, Side.Rival).Race,
                Sides.Three(w) ? Economy.Of(w, Side.Third).Race : null, Sides.Three(w) ? SideEnd.Of(w, Side.Third) : null);
        }
        catch (Exception ex)
        {
            var folder = Reports.WriteCrash(runPaths, log, sim, ex, $"headless game {index}");
            var outcome = ex is RuleBrokenException ? "rule" : "crash";
            return new GameResult(index, seed, outcome, sim?.World.Tick ?? 0, timer.Elapsed.TotalSeconds,
                sim?.RuleViolations ?? 0, log.Count(LogLevel.Error), log.Count(LogLevel.Warn),
                $"{ex.GetType().Name}: {ex.Message}", folder, WorstStepMs: Math.Round(worstMs, 1), WorstStepTick: worstTick);
        }
        finally
        {
            SectionTimer.Active = null;
            Timing.Add(sections);
        }
    }

    /// <summary>Where the batch's time went, all games together.</summary>
    public static readonly SectionTimer Timing = new(Stopwatch.GetTimestamp, Stopwatch.Frequency);

    public static string Summarise(BuildInfo build, string scenario, int days, bool strict, DateTime started,
                                   TimeSpan elapsed, int requested, List<GameResult> results, string runDir, string pilot = "ai")
    {
        var bad = results.Where(r => r.Outcome != "ok").ToList();
        var md = new StringBuilder();
        md.AppendLine($"# Lightlag overnight summary, {started.ToLocalTime():ddd d MMM yyyy HH:mm}");
        md.AppendLine();
        md.AppendLine(bad.Count == 0
            ? $"**All {results.Count} games finished cleanly.**"
            : $"**{bad.Count} of {results.Count} games failed.** Details below.");
        md.AppendLine();
        md.AppendLine($"- Build: {build}");
        md.AppendLine($"- Scenario: {scenario}, {days} game days each, rule checks every tick ({(strict ? "strict" : "lenient")})");
        md.AppendLine(_light is { On: true } l ? $"- Light lag: on, light at {l.SpeedFactor:0.##} x real speed" : "- Light lag: off");
        md.AppendLine(_rules.SingleFuel ? "- Fuel types: off (one fuel)" : "- Fuel types: on");
        if (_autonomy is AutonomyLevel level) md.AppendLine($"- Fleet autonomy: {level}");
        var ended = results.Where(r => r.Player != null && r.Rival != null).ToList();
        if (_light is { On: true } && ended.Count > 0)
            md.AppendLine($"- Under lag, per game: battles more than 5 light-minutes from the flagship: player {ended.Average(r => r.Player!.LaggedBattles):0.0}, "
                          + $"rival {ended.Average(r => r.Rival!.LaggedBattles):0.0} (of {ended.Average(r => r.Player!.Battles):0.0} and {ended.Average(r => r.Rival!.Battles):0.0}); "
                          + $"flagship in battle: player {ended.Average(r => r.Player!.FlagshipBattles):0.0}, rival {ended.Average(r => r.Rival!.FlagshipBattles):0.0}; "
                          + $"fleets' own decisions: player {ended.Average(r => r.Player!.OwnDecisions):0.0}, rival {ended.Average(r => r.Rival!.OwnDecisions):0.0}");
        if (ended.Count > 0)
        {
            md.AppendLine($"- Heat, per game: ships overheated: player {ended.Average(r => r.Player!.Overheats):0.0}, rival {ended.Average(r => r.Rival!.Overheats):0.0}");
            md.AppendLine($"- Hazards, per game (player / rival): rock hits {ended.Average(r => r.Player!.RockHits):0.0} / {ended.Average(r => r.Rival!.RockHits):0.0}, "
                + $"hull lost to rocks {ended.Average(r => r.Player!.HazardHull):0} / {ended.Average(r => r.Rival!.HazardHull):0}, "
                + $"ship-hours in radiation {ended.Average(r => r.Player!.RadiationShipHours):0} / {ended.Average(r => r.Rival!.RadiationShipHours):0}, "
                + $"events {ended.Average(r => r.Player!.Events):0.0} / {ended.Average(r => r.Rival!.Events):0.0}");
            md.AppendLine($"- Transport and orbit, per game (player / rival): Industry paid for lift {ended.Average(r => r.Player!.LiftSpent):0} / {ended.Average(r => r.Rival!.LiftSpent):0}, "
                + $"sail ships at the end {ended.Average(r => r.Player!.SailShips):0.0} / {ended.Average(r => r.Rival!.SailShips):0.0}, "
                + $"satellites launched {ended.Average(r => r.Player!.SatellitesLaunched):0.0} / {ended.Average(r => r.Rival!.SatellitesLaunched):0.0}, "
                + $"lost {ended.Average(r => r.Player!.SatellitesLost):0.0} / {ended.Average(r => r.Rival!.SatellitesLost):0.0}");
            md.AppendLine($"- People and power, per game (player / rival): battery banks at the end {ended.Average(r => r.Player!.Batteries):0.0} / {ended.Average(r => r.Rival!.Batteries):0.0}, "
                + $"hungry people who moved away {ended.Average(r => r.Player!.PeopleLeftK):0.#}k / {ended.Average(r => r.Rival!.PeopleLeftK):0.#}k, "
                + $"worlds abandoned {ended.Average(r => r.Player!.WorldsAbandoned):0.0} / {ended.Average(r => r.Rival!.WorldsAbandoned):0.0}, "
                + $"planet-hours shedding {ended.Average(r => r.Player!.ShedHours):0} / {ended.Average(r => r.Rival!.ShedHours):0}");
        }
        md.AppendLine($"- Games: {results.Count} played of {requested} asked for, in {elapsed:hh\\:mm\\:ss}");
        if (results.Count > 0)
        {
            var slowest = results.MaxBy(r => r.Seconds)!;
            md.AppendLine($"- Time per game: average {results.Average(r => r.Seconds):0.00}s, slowest {slowest.Seconds:0.00}s (seed {slowest.Seed}). Target is under 60s.");
            var stall = results.MaxBy(r => r.WorstStepMs)!;
            md.AppendLine($"- Slowest single step: {stall.WorstStepMs:0} ms (seed {stall.Seed}, {GameTime.Format(stall.WorstStepTick)}); steps over 1 s: "
                          + $"{results.Count(r => r.WorstStepMs > 1000)} games");
            md.AppendLine($"- Rule failures: {results.Sum(r => r.RuleFailures)}; log errors: {results.Sum(r => r.Errors)}; warnings: {results.Sum(r => r.Warnings)}");
            var won = results.Where(r => r.Winner != null).ToList();
            var who = Describe(pilot);
            md.AppendLine($"- Games won ({who}): player {won.Count(r => r.Winner == "Player")}, rival {won.Count(r => r.Winner == "Rival")}, nobody {results.Count - won.Count}"
                          + (won.Count > 0 ? $"; average win on day {won.Average(r => r.WinDay ?? 0):0}" : ""));
            if (won.Count > 0)
                md.AppendLine($"- How games were won: " + string.Join(", ", won.GroupBy(r => r.WinReason ?? "?").Select(g => $"{g.Count()} {g.Key}")));
        }
        AppendBalance(md, results);
        md.AppendLine($"- Folder: `{runDir}`");
        if (Timing.Rows.Count > 0)
        {
            md.AppendLine();
            md.AppendLine($"## Where the time goes ({Timing.Hours} game hours, rule checks every hour)");
            md.AppendLine();
            md.Append(Timing.Table(20));
        }
        if (bad.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## Failed games");
            md.AppendLine();
            md.AppendLine("| Game | Seed | What | Game time | Error | Report |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var r in bad)
                md.AppendLine($"| {r.Index} | {r.Seed} | {r.Outcome} | {GameTime.Format(r.Ticks)} | {r.Error?.Replace("|", "/")} | `{Path.GetFileName(r.ReportFolder)}` |");
            md.AppendLine();
            md.AppendLine("Each report folder has a README, save.json and replay.json. Replay one with `Lightlag.Headless replay <folder>/replay.json`.");
        }
        return md.ToString();
    }

    /// <summary>
    /// How fights went: battles a game-day, how they ended (target: under a third wipes), ships crippled
    /// against lost, pull-backs (and those before a shot), warships at the end (target 6-12 a side), and
    /// days the shipyard stood waiting for materials. Near-zero battles is a failure.
    /// </summary>
    private static void AppendFights(StringBuilder md, List<GameResult> ai)
    {
        double Avg(Func<SideEnd, double> f) => ai.Average(r => (f(r.Player!) + f(r.Rival!)) / 2);
        var battles = Avg(e => e.Battles);
        var days = ai.Average(r => r.Ticks / (double)GameTime.TicksPerDay);
        var wiped = Avg(e => e.Wiped);
        var ended = wiped + Avg(e => e.OneWithdrew) + Avg(e => e.BothWithdrew);
        md.AppendLine();
        md.AppendLine("## Fights");
        md.AppendLine();
        md.AppendLine($"- Battles per game (shots fired): {battles:0.0} ({battles / Math.Max(1, days) * 100:0.0} per 100 game days), of {Avg(e => e.Contacts):0.0} contacts"
                      + (battles < 1 ? " **(too few: fights should happen)**" : ""));
        md.AppendLine($"- Endings per game: {wiped:0.0} wipes, {Avg(e => e.OneWithdrew):0.0} one side withdrew, {Avg(e => e.BothWithdrew):0.0} both withdrew, "
                      + $"{Math.Max(0, battles - ended):0.0} drifted apart"
                      + (battles > 0 ? $"; wipes are {100 * wiped / battles:0} % of battles (target under 33 %)" : ""));
        md.AppendLine($"- Per side per game: {Avg(e => e.ShipsCrippled):0.0} ships crippled, {Avg(e => e.ShipsLost):0.0} lost, {Avg(e => e.PartsKnocked):0.0} parts knocked out; "
                      + $"warships lost per battle {(battles > 0 ? Avg(e => e.ShipsLost) / battles : 0):0.00}");
        md.AppendLine($"- Pull-backs per side per game: {Avg(e => e.PullBacks):0.0}, of them before firing a shot: {Avg(e => e.PulledBeforeShots):0.0}");
        md.AppendLine($"- Warships per side at the end: {Avg(e => e.Warships):0.0} (target 6-12); materials salvaged: {Avg(e => e.Salvaged):0}");
        md.AppendLine($"- Raiding per side per game: {Avg(e => e.AmbushCatches):0.0} freighters caught, {Avg(e => e.CargoCaptured):0} cargo taken, {Avg(e => e.CargoLost):0.0} freighters lost");
        md.AppendLine($"- Shipyard days waiting for materials, per side: {Avg(e => e.MaterialStallHours) / 24:0.0}");
    }

    /// <summary>Balance tables for computer-vs-computer runs: personalities, and how games went on average.</summary>
    private static void AppendBalance(StringBuilder md, List<GameResult> results)
    {
        var ai = results.Where(r => r.PlayerMind != null && r.Player != null && r.Rival != null).ToList();
        if (ai.Count == 0) return;
        md.AppendLine();
        md.AppendLine("## Balance");
        md.AppendLine();
        md.AppendLine("| Personality | Games | Wins | Win % | Avg planets at end | Ships built | Ships lost | Battles | Warships at end | Freighters caught | Stall days |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        var rows = ai.SelectMany(r => new[]
        {
            (Name: r.PlayerMind!.Split(' ')[0], Won: r.Winner == "Player", End: r.Player!),
            (Name: r.RivalMind!.Split(' ')[0], Won: r.Winner == "Rival", End: r.Rival!),
        }).GroupBy(x => x.Name).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var html = new StringBuilder("<table><thead><tr><th>Personality</th><th>Games</th><th>Win %</th><th>Planets</th><th>Built</th><th>Lost</th><th>Battles</th><th>Freighters caught</th></tr></thead><tbody>");
        foreach (var g in rows)
        {
            md.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(x => x.Won)} | {100.0 * g.Count(x => x.Won) / g.Count():0} | "
                          + $"{g.Average(x => x.End.Planets):0.0} | {g.Average(x => x.End.ShipsBuilt):0.0} | {g.Average(x => x.End.ShipsLost):0.0} | {g.Average(x => x.End.Battles):0.0} | "
                          + $"{g.Average(x => x.End.Warships):0.0} | {g.Average(x => x.End.AmbushCatches):0.0} | {g.Average(x => x.End.MaterialStallHours) / 24:0} |");
            html.Append($"<tr><td>{g.Key}</td><td>{g.Count()}</td><td>{100.0 * g.Count(x => x.Won) / g.Count():0}</td><td>{g.Average(x => x.End.Planets):0.0}</td>"
                        + $"<td>{g.Average(x => x.End.ShipsBuilt):0.0}</td><td>{g.Average(x => x.End.ShipsLost):0.0}</td><td>{g.Average(x => x.End.Battles):0.0}</td><td>{g.Average(x => x.End.AmbushCatches):0.0}</td></tr>");
        }
        html.Append("</tbody></table>");
        AppendRaces(md, ai);
        md.AppendLine();
        var report = Overnight.Build(ai, rows.ToDictionary(g => g.Key, g => (g.Count(), g.Count(x => x.Won))));
        Overnight.Append(md, report);
        Audit.Append(md);
        _report = Overnight.Html(report, "Lightlag overnight report", html.ToString());
        AppendFights(md, ai);
        WarReport.Append(md);
        BudgetReport.Append(md);
        WhatMatters.Append(md);
        ResearchReport.Append(md);
        md.AppendLine();
        md.AppendLine($"Per game: average {ai.Average(r => r.Player!.Battles + r.Rival!.Battles) / 2:0.0} battles, "
                      + $"{ai.Average(r => r.Player!.ShipsLost + r.Rival!.ShipsLost):0.0} ships lost, "
                      + $"{ai.Average(r => r.Player!.Planets + r.Rival!.Planets):0.0} planets held by the two sides at the end. "
                      + "Day-by-day numbers for each game are in the `stats` folder (CSV).");
    }

    /// <summary>Races table (when any game had a race other than humans): wins by race, and how the Hive's own systems did.</summary>
    private static void AppendRaces(StringBuilder md, List<GameResult> ai)
    {
        var rows = ai.SelectMany(r => new[]
        {
            (Race: r.PlayerRace ?? Races.Human, Won: r.Winner == "Player", End: r.Player!, Mirror: r.PlayerRace == r.RivalRace),
            (Race: r.RivalRace ?? Races.Human, Won: r.Winner == "Rival", End: r.Rival!, Mirror: r.PlayerRace == r.RivalRace),
        }.Concat(r.ThirdRace != null && r.Third != null
            ? new[] { (Race: r.ThirdRace, Won: r.Winner == "Third", End: r.Third, Mirror: r.ThirdRace == r.PlayerRace && r.ThirdRace == r.RivalRace) }
            : Array.Empty<(string Race, bool Won, SideEnd End, bool Mirror)>())).ToList();
        if (rows.All(x => x.Race == Races.Human)) return;
        md.AppendLine();
        md.AppendLine("## Races");
        md.AppendLine();
        md.AppendLine("Games against the other race only (mirror games left out).");
        md.AppendLine();
        md.AppendLine("| Race | Sides played | Wins | Win % | Avg planets at end | Ships built | Ships lost | Warships at end | Stall days |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var g in rows.Where(x => !x.Mirror).GroupBy(x => x.Race).OrderBy(g => g.Key, StringComparer.Ordinal))
            md.AppendLine($"| {Races.Get(g.Key).Name} | {g.Count()} | {g.Count(x => x.Won)} | {100.0 * g.Count(x => x.Won) / g.Count():0} | {g.Average(x => x.End.Planets):0.0} | "
                          + $"{g.Average(x => x.End.ShipsBuilt):0.0} | {g.Average(x => x.End.ShipsLost):0.0} | {g.Average(x => x.End.Warships):0.0} | {g.Average(x => x.End.MaterialStallHours) / 24:0} |");
    }
}
