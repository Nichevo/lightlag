using System.Security.Cryptography;
using System.Text;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Replay;
using Lightlag.Sim.Save;

namespace Lightlag.Sim.Core;

/// <summary>
/// Runs the game. Holds the world, applies commands, advances time in fixed ticks, runs rule checks
/// and records the replay as it goes. Knows nothing about Godot: the game screen and the headless
/// runner both drive this same class.
/// </summary>
public sealed class Simulation
{
    private readonly Queue<GameCommand> _pending = new();
    private readonly HashSet<string> _quietRules = new();
    private readonly Dictionary<string, int> _ruleHits = new();

    private Simulation(World world, SimRandom rng, GameLog log, BuildInfo build, ReplayFile replay)
    {
        World = world;
        Rng = rng;
        Log = log;
        Build = build;
        Replay = replay;
        Log.GameContext = () => (World.Seed, World.Tick);
    }

    public World World { get; }
    public SimRandom Rng { get; }
    public GameLog Log { get; }
    public BuildInfo Build { get; }
    public RuleBook Rules { get; set; } = RuleBook.Default();

    /// <summary>The replay recorded so far: seed, scenario and every command with its tick.</summary>
    public ReplayFile Replay { get; }

    /// <summary>How often rule checks run. Every day by default; tests and debug runs can use 1.</summary>
    public int RuleCheckEveryTicks { get; set; } = GameTime.TicksPerDay;

    /// <summary>When true a broken rule throws (and so makes a crash report) instead of only logging.</summary>
    public bool StrictRules { get; set; }

    public int RuleViolations { get; private set; }
    public IReadOnlyDictionary<string, int> RuleHits => _ruleHits;
    public IReadOnlyCollection<GameCommand> PendingCommands => _pending;

    public static Simulation NewGame(string scenario, ulong seed, GameLog log, BuildInfo? build = null)
    {
        build ??= log.Build;
        var rng = new SimRandom(seed);
        var world = Scenarios.Create(scenario, seed, rng);
        world.Random = rng.Save();
        var replay = new ReplayFile { Build = build.ToString(), Scenario = scenario, Seed = seed };
        var sim = new Simulation(world, rng, log, build, replay);
        sim.Replay.Checkpoints.Add(new Checkpoint(0, sim.StateHash()));
        log.Info("game", $"New game: {scenario}, seed {seed}", new { scenario, seed });
        return sim;
    }

    public static Simulation FromSave(SaveGame save, GameLog log, BuildInfo? build = null)
    {
        build ??= log.Build;
        var sim = new Simulation(save.World, new SimRandom(save.World.Random), log, build, save.Replay);
        foreach (var c in save.PendingCommands) sim._pending.Enqueue(c);
        if (save.Build != build.ToString())
            log.Warn("save", $"Save was made by build {save.Build}, this is {build}", new { saveBuild = save.Build });
        log.Info("save", $"Loaded game at {GameTime.Format(save.World.Tick)}", new { save.World.Scenario, save.World.Seed });
        return sim;
    }

    /// <summary>Queue a command. It takes effect at the start of the next tick and is recorded in the replay.</summary>
    public void Issue(GameCommand command) => _pending.Enqueue(command);

    /// <summary>Advance one tick (one game hour).</summary>
    public void Step()
    {
        while (_pending.Count > 0)
        {
            var command = _pending.Dequeue();
            Replay.Commands.Add(new RecordedCommand(World.Tick, command));
            command.Apply(World, this);
        }

        World.Tick++;
        UpdateBodyPositions(World);
        MoveShips();
        World.Random = Rng.Save();

        if (World.Tick % GameTime.TicksPerDay == 0)
            Replay.Checkpoints.Add(new Checkpoint(World.Tick, StateHash()));

        if (RuleCheckEveryTicks > 0 && World.Tick % RuleCheckEveryTicks == 0)
            CheckRules();
    }

    public void StepMany(int ticks)
    {
        for (var i = 0; i < ticks; i++) Step();
    }

    /// <summary>Runs the rule checks now. Returns what failed.</summary>
    public List<RuleViolation> CheckRules()
    {
        var found = Rules.Check(World);
        foreach (var v in found)
        {
            RuleViolations++;
            _ruleHits[v.Rule] = _ruleHits.GetValueOrDefault(v.Rule) + 1;
            // Log each rule's first few failures in full, then go quiet so one bug cannot flood the log.
            if (_ruleHits[v.Rule] <= 5)
                Log.Error("rule", $"Rule broken: {v.Rule}: {v.Detail}", new { rule = v.Rule, detail = v.Detail });
            else if (_quietRules.Add(v.Rule))
                Log.Error("rule", $"Rule {v.Rule} keeps breaking; further failures are counted but not logged", new { rule = v.Rule });
        }
        if (StrictRules && found.Count > 0) throw new RuleBrokenException(found[0]);
        return found;
    }

    public SaveGame ToSave() => new()
    {
        Build = Build.ToString(),
        SavedAtUtc = DateTime.UtcNow.ToString("o"),
        World = World,
        PendingCommands = _pending.ToList(),
        Replay = Replay,
    };

    /// <summary>A short fingerprint of the whole game state. Two runs that agree on this are in the same state.</summary>
    public string StateHash() => HashWorld(World);

    public static string HashWorld(World world)
    {
        var json = Json.Serialize(world);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    public static void UpdateBodyPositions(World world)
    {
        // Bodies are listed parents first, so a moon's planet has already moved when the moon moves.
        foreach (var b in world.Bodies)
        {
            if (b.ParentId is not int parentId)
            {
                continue;
            }
            var parent = world.FindBody(parentId);
            if (parent == null) continue;
            if (b.OrbitPeriodDays > 0)
            {
                b.Angle += Math.PI * 2 * GameTime.HoursPerTick / (b.OrbitPeriodDays * 24);
                if (b.Angle >= Math.PI * 2) b.Angle -= Math.PI * 2;
            }
            b.Position = parent.Position + new Vec2(DetMath.Cos(b.Angle), DetMath.Sin(b.Angle)) * b.OrbitRadius;
        }
    }

    private void MoveShips()
    {
        foreach (var s in World.Ships)
        {
            if (s.Destination is not Vec2 dest) continue;
            var toGo = dest - s.Position;
            var step = s.MaxSpeed * GameTime.HoursPerTick;
            if (toGo.Length <= step)
            {
                s.Position = dest;
                s.Destination = null;
                Log.Info("sim", $"{s.Name} arrived", new { shipId = s.Id, at = dest });
            }
            else
            {
                s.Position += toGo.Normalized() * step;
            }
        }
    }
}
