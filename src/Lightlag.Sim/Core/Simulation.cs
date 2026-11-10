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

    public static Simulation NewGame(string scenario, ulong seed, GameLog log, BuildInfo? build = null, SandboxSetup? sandbox = null)
    {
        build ??= log.Build;
        var rng = new SimRandom(seed);
        var world = sandbox != null ? Scenarios.Sandbox(sandbox, seed, rng) : Scenarios.Create(scenario, seed, rng);
        world.Random = rng.Save();
        if (sandbox != null) scenario = Scenarios.BattleSandbox;
        var replay = new ReplayFile { Build = build.ToString(), Scenario = scenario, Seed = seed, Sandbox = sandbox };
        var sim = new Simulation(world, rng, log, build, replay);
        Sensors.Update(sim);
        sim.Replay.Checkpoints.Add(new Checkpoint(0, sim.StateHash()));
        log.Info("game", $"New game: {scenario}, seed {seed}", new { scenario, seed, sandbox });
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

    /// <summary>True when the last step was a battle-time step (10 game seconds) rather than a whole hour.</summary>
    public bool InBattleTime { get; private set; }

    /// <summary>
    /// Advance the game: one whole hour normally, or one 10-second battle-time step when enemy fleets
    /// could meet this hour (or are fighting). Both kinds of step replay exactly.
    /// </summary>
    public void Step()
    {
        ApplyPending();

        var t0 = World.Hour;
        var velocities = World.Fleets.Select(f => (f, f.Velocity)).ToList();
        InBattleTime = World.Sub != 0 || Combat.BattlePossible(World);
        if (InBattleTime)
        {
            World.Sub++;
            if (World.Sub == GameTime.SubSteps)
            {
                World.Sub = 0;
                World.Tick++;
                UpdateBodyPositions(World);
            }
            var t1 = World.Hour;
            Combat.Steer(this, t0, t1);
            MoveFleets(t0, t1);
            UpdateFacing(velocities);
            Sensors.Update(this);
            Combat.Fire(this, t0, t1);
        }
        else
        {
            World.Tick++;
            UpdateBodyPositions(World);
            MoveFleets(t0, World.Hour);
            UpdateFacing(velocities);
            Sensors.Update(this);
            // Contacts between fleets that can't fight yet start (or end) a battle at the hour.
            Combat.UpdateContacts(this, World.Hour);
        }
        World.Random = Rng.Save();
        if (World.Sub != 0) return;
        JoinFleets();
        Economy.Update(this);

        if (World.Tick % GameTime.TicksPerDay == 0)
            Replay.Checkpoints.Add(new Checkpoint(World.Tick, StateHash()));

        if (RuleCheckEveryTicks > 0 && World.Tick % RuleCheckEveryTicks == 0)
            CheckRules();
    }

    /// <summary>
    /// Applies queued commands now instead of at the next step (so a paused game shows their effect).
    /// Replays the same: nothing happens between this and the start of the next step.
    /// </summary>
    public void ApplyPending()
    {
        while (_pending.Count > 0)
        {
            var command = _pending.Dequeue();
            Replay.Commands.Add(new RecordedCommand(World.Tick, command, World.Sub));
            command.Apply(World, this);
        }
    }

    /// <summary>How much game time the next step will probably take (for the screen's clock; the step itself decides).</summary>
    public double PeekStepHours() => World.Sub != 0 || Combat.BattlePossible(World) ? GameTime.SubStepHours : GameTime.HoursPerTick;

    /// <summary>Advance this many whole hours (battle time included).</summary>
    public void StepMany(int ticks)
    {
        var end = World.Tick + ticks;
        while (World.Tick < end || World.Sub != 0) Step();
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

    /// <summary>Supplies a fleet in orbit at a planet its side holds gets back each day, as a share of a full load.</summary>
    public const double RefillPerDay = 0.1;

    private void MoveFleets(double t0, double t1)
    {
        var dt = t1 - t0;
        foreach (var f in World.Fleets)
        {
            if (f.Flight is Flight flight && FlightPlanner.FlightState(flight, t1) is { } state)
            {
                if (!flight.Started)
                {
                    flight.Started = true;
                    Fleets.EnterDrift(f);
                    f.Maneuvering = false;
                    Log.Info("fleet", $"{f.Name} departs", new { fleetId = f.Id, flight.TargetBodyId });
                }
                BurnFuel(f, FlightPlanner.DeltaVBetween(flight, t0, t1));
                f.Position = state.Position;
                f.Velocity = state.Velocity;
                if (t1 >= flight.EndHour) Arrive(f, flight, t1);
            }
            else if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int bodyId && World.FindBody(bodyId) is Body body)
            {
                f.OrbitAngle += 2 * Math.PI * dt / Fleets.OrbitPeriodHours(body, f.OrbitRadius);
                if (f.OrbitAngle >= Math.PI * 2) f.OrbitAngle -= Math.PI * 2;
                f.Position = FlightPlanner.PredictBody(World, body, t1) + new Vec2(DetMath.Cos(f.OrbitAngle), DetMath.Sin(f.OrbitAngle)) * f.OrbitRadius;
                f.Velocity = Vec2.Zero;
                if (body.Owner == f.Side && World.ActiveBattle == null)
                {
                    // Any planet you hold refuels; only the shipyard repairs and rearms.
                    Refill(f, dt);
                    if (body.Shipyard) Combat.RepairAtHome(World, f, dt);
                }
            }
            else
            {
                f.Position += f.Velocity * dt;
            }
            foreach (var s in World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        }
    }

    /// <summary>
    /// Ships point their engines where the thrust goes: a fleet that changed velocity this step faces
    /// along that change (so it flips round to brake). Coasting or holding keeps the last facing.
    /// </summary>
    private static void UpdateFacing(List<(Fleet Fleet, Vec2 Velocity)> before)
    {
        foreach (var (f, v0) in before)
        {
            var dv = f.Velocity - v0;
            f.Thrusting = dv.Length > 1e-6;
            if (f.Thrusting) f.Facing = DetMath.Atan2(dv.Y, dv.X);
        }
    }

    /// <summary>Fleets told to join another do so once both are stopped together.</summary>
    private void JoinFleets()
    {
        foreach (var f in World.Fleets.Where(f => f.JoinFleetId != null).ToList())
        {
            // An earlier join this hour may have removed this fleet or changed its target.
            if (!World.Fleets.Contains(f) || f.JoinFleetId is not int joinId) continue;
            if (World.FindFleet(joinId) is not Fleet into || into.Side != f.Side || into.Id == f.Id)
            {
                f.JoinFleetId = null;
                continue;
            }
            if (!Fleets.CanJoin(f, into))
            {
                // Both stopped but apart (the other fleet moved on, or there was no route before): once a day, set off
                // again to where it is now. A fleet still flying is met where it stops, so that case waits.
                if (World.Tick % GameTime.TicksPerDay == 0 && Stopped(f) && Stopped(into)
                    && Fleets.FlyToJoin(World, f, into, Economy.ReinforceSpeed) == null)
                    Log.Info("fleet", $"{f.Name} sets off again to join {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id });
                continue;
            }
            Fleets.Absorb(World, into, f);
            Log.Info("fleet", $"{f.Name} joins {into.Name}", new { fleetId = f.Id, intoFleetId = into.Id });
        }
    }

    private static bool Stopped(Fleet f) => f.Flight == null && f.Velocity.Length < 1e-9 && !f.Maneuvering && !f.Retreating;

    private void Arrive(Fleet f, Flight flight, double now)
    {
        f.Flight = null;
        var body = flight.TargetBodyId is int id ? World.FindBody(id) : null;
        if (flight.Arrival == ArrivalStyle.Brake)
        {
            f.Velocity = Vec2.Zero;
            if (flight.HoldAfter == HoldStyle.Orbit && (body ?? Fleets.OrbitableNear(World, f.Position)) is Body orbit)
            {
                // The flight ended part way through the last hour; keep the spot it reached relative to the body.
                f.Position = FlightPlanner.PredictBody(World, orbit, now) + (f.Position - FlightPlanner.PredictBody(World, orbit, flight.EndHour));
                Fleets.EnterOrbit(f, orbit);
            }
            else
                Fleets.EnterDrift(f);
        }
        else
        {
            Fleets.EnterDrift(f);
        }
        var where = body != null ? (flight.Arrival == ArrivalStyle.FastPass ? $"passes {body.Name}" : $"arrives at {body.Name}") : "arrives";
        Log.Info("fleet", $"{f.Name} {where}", new { fleetId = f.Id, at = f.Position, hold = f.Hold, speed = f.Velocity.Length });
    }

    public void BurnFuel(Fleet f, double deltaV)
    {
        if (deltaV <= 0) return;
        foreach (var s in World.ShipsOf(f))
            s.Supplies = Math.Max(0, s.Supplies - deltaV * Tech.SuppliesPerDeltaV(World, s));
    }

    private void Refill(Fleet f, double hours)
    {
        foreach (var s in World.ShipsOf(f))
        {
            var max = GameData.ShipClass(s.ClassId).Supplies;
            s.Supplies = Math.Min(max, s.Supplies + max * RefillPerDay * hours / 24);
        }
    }
}
