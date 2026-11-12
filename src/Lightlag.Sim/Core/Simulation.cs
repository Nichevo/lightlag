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

    /// <summary>
    /// Recent shots and kills, only for drawing (not saved, not part of the game state). Combat adds to it;
    /// entries older than a game minute or two are dropped.
    /// </summary>
    public List<CombatEffect> Effects { get; } = new();

    public void AddEffect(CombatEffect e)
    {
        Effects.Add(e);
        BattleRecords.Shot(World, e);
        if (Effects.Count > 400 || (Effects.Count > 0 && e.Hour - Effects[0].Hour > 0.05)) Effects.RemoveAll(x => e.Hour - x.Hour > 0.05);
    }
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

    public static Simulation NewGame(string scenario, ulong seed, GameLog log, BuildInfo? build = null, SandboxSetup? sandbox = null,
                                     GameOptions? options = null)
    {
        build ??= log.Build;
        var rng = new SimRandom(seed);
        var world = sandbox != null ? Scenarios.Sandbox(sandbox, seed, rng) : Scenarios.Create(scenario, seed, rng, options?.RandomStarts == true);
        if (sandbox == null)
        {
            world.Rules.Happiness = options?.Happiness ?? true;
            world.Rules.SupplyLines = options?.SupplyLines ?? true;
            world.Rules.StartPopulation = options?.StartPopulation ?? false;
            world.Rules.RandomStarts = options?.RandomStarts ?? false;
            world.Rules.FuelTypes = options?.FuelTypes ?? true;
            world.Rules.Hazards = options?.Hazards ?? true;
            world.Rules.Events = options?.Events ?? EventRate.Normal;
            Planets.EnsureStarted(world);
        }
        if (sandbox == null) Mind.Setup(world, options, seed);
        if (sandbox == null && options?.Light is LightSetup light)
        {
            world.Light.On = light.On;
            world.Light.SpeedFactor = light.SpeedFactor;
            world.Light.Decapitation = light.On && (options.Decapitation ?? true);
        }
        if (sandbox == null && options?.Autonomy is AutonomyLevel level)
        {
            foreach (var s in world.Sides) s.DefaultAutonomy = level;
            foreach (var f in world.Fleets) f.Autonomy = level;
        }
        Light.ChooseFlagships(world);
        world.Random = rng.Save();
        if (sandbox != null) scenario = Scenarios.BattleSandbox;
        var replay = new ReplayFile { Build = build.ToString(), Scenario = scenario, Seed = seed, Sandbox = sandbox, Options = options };
        var sim = new Simulation(world, rng, log, build, replay);
        Sensors.Update(sim);
        Light.Relay(sim);
        Stats.RecordDay(world);
        sim.Replay.Checkpoints.Add(new Checkpoint(0, sim.StateHash()));
        log.Info("game", $"New game: {scenario}, seed {seed}", new { scenario, seed, sandbox, options });
        foreach (var m in world.Minds)
            log.Info("ai", $"{m.Side} is run by the computer", new { side = m.Side.ToString(), difficulty = m.Difficulty.ToString(), personality = m.Personality.ToString(), m.BaseRatio, m.FirstProbeDay });
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
        using var step = SectionTimer.Time("step");
        using (SectionTimer.Time("orders")) ApplyPending();
        using (SectionTimer.Time("light")) Light.Deliver(this);

        var t0 = World.Hour;
        var velocities = World.Fleets.Select(f => (f, f.Velocity)).ToList();
        using (SectionTimer.Time("battle-check")) InBattleTime = World.Sub != 0 || Combat.BattlePossible(World);
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
            using (SectionTimer.Time("combat-steer")) Combat.Steer(this, t0, t1);
            using (SectionTimer.Time("move")) MoveFleets(t0, t1);
            using (SectionTimer.Time("drones")) Drones.Move(this, t1 - t0);
            using (SectionTimer.Time("heat")) Heat.Update(this, t1 - t0);
            using (SectionTimer.Time("missiles")) DroppedMissiles.Update(this, t1 - t0);
            UpdateFacing(velocities, World.Hour);
            using (SectionTimer.Time("sensors")) Sensors.Update(this);
            using (SectionTimer.Time("light")) Light.Relay(this);
            using (SectionTimer.Time("combat-fire")) Combat.Fire(this, t0, t1);
        }
        else
        {
            World.Tick++;
            UpdateBodyPositions(World);
            // Fleets still braking after a fight finish braking (otherwise they would drift on for ever).
            using (SectionTimer.Time("combat-steer")) Combat.Steer(this, t0, World.Hour);
            using (SectionTimer.Time("move")) MoveFleets(t0, World.Hour);
            using (SectionTimer.Time("drones")) Drones.Move(this, World.Hour - t0);
            using (SectionTimer.Time("heat")) Heat.Update(this, World.Hour - t0);
            using (SectionTimer.Time("missiles")) DroppedMissiles.Update(this, World.Hour - t0);
            UpdateFacing(velocities, World.Hour);
            using (SectionTimer.Time("sensors")) Sensors.Update(this);
            using (SectionTimer.Time("light")) Light.Relay(this);
            // Contacts between fleets that can't fight yet start (or end) a battle at the hour.
            using (SectionTimer.Time("combat-contacts")) Combat.UpdateContacts(this, World.Hour);
            if (World.ActiveBattle is Battle b) BattleRecords.Record(this, b, World.Hour);
        }
        World.Random = Rng.Save();
        if (World.Sub != 0) return;
        if (SectionTimer.Active is SectionTimer timer) timer.Hours++;
        using (SectionTimer.Time("fleets")) JoinFleets();
        using (SectionTimer.Time("fleets")) ShareSupplies();
        using (SectionTimer.Time("chases")) Chases.Update(this);
        using (SectionTimer.Time("economy")) Economy.Update(this);
        using (SectionTimer.Time("mind")) Mind.Update(this);
        using (SectionTimer.Time("stewards")) Stewards.Update(this);
        using (SectionTimer.Time("autonomy")) Autonomy.Update(this);
        using (SectionTimer.Time("lanes")) Lanes.Observe(this);
        using (SectionTimer.Time("intel")) Intel.Update(this);
        using (SectionTimer.Time("lanes")) Lanes.UpdateZones(this);
        using (SectionTimer.Time("supply")) Supply.Update(this);
        using (SectionTimer.Time("supply-lines")) SupplyLines.Update(this);
        using (SectionTimer.Time("combat-orbit")) Combat.ReturnToOrbit(this);
        using (SectionTimer.Time("flagships")) Flagships.Update(this);
        if (World.Tick % GameTime.TicksPerDay == 0) using (SectionTimer.Time("stats")) Stats.RecordDay(World);
        // The hour's work above draws from the random stream too (hazards, events, spoils): keep the saved state current.
        World.Random = Rng.Save();

        if (World.Tick % GameTime.TicksPerDay == 0)
            using (SectionTimer.Time("checkpoint")) Replay.Checkpoints.Add(new Checkpoint(World.Tick, StateHash()));

        if (RuleCheckEveryTicks > 0 && World.Tick % RuleCheckEveryTicks == 0)
            using (SectionTimer.Time("rules")) CheckRules();
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
            // Under light lag an order to a far fleet (or home) is sent now and takes effect when it arrives.
            if (!Light.Send(this, command)) Carry(command);
        }
    }

    /// <summary>Carries out an order that has reached its fleet (or home), now or after its light arrives.</summary>
    public void Carry(GameCommand command)
    {
        command.Apply(World, this);
        Autonomy.Heard(this, command);
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

    /// <summary>Ships in a fleet pass fuel and ammunition between them: each hour this share of the gap to an even load closes.</summary>
    public const double ShareSuppliesPerHour = 0.5;

    /// <summary>Evens out supplies across each fleet's ships (as a share of what each can carry); the total stays the same.</summary>
    private void ShareSupplies()
    {
        foreach (var f in World.Fleets)
        {
            if (f.ShipIds.Count < 2) continue;
            // Only ships on the same fuel can pass it between them.
            foreach (var ships in World.ShipsOf(f).GroupBy(Fuels.IdOf).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.ToList()))
            {
                if (ships.Count < 2) continue;
                var capacity = ships.Sum(s => Fitting.MaxSupplies(World, s));
                if (capacity <= 0) continue;
                var even = ships.Sum(s => s.Supplies) / capacity;
                foreach (var s in ships)
                    s.Supplies += (even * Fitting.MaxSupplies(World, s) - s.Supplies) * ShareSuppliesPerHour;
            }
        }
    }

    /// <summary>
    /// Nothing flies through a planet: a fleet that ends up inside one (drifting into its path, or dodging
    /// in a fight) is moved out to just above the surface, and stops moving towards it. Its flight, if any,
    /// moves with it. Flights leaving or reaching a planet are let be (they start and end at rest).
    /// </summary>
    private void KeepOutOfBodies(Fleet f, double hour)
    {
        foreach (var b in World.Bodies)
        {
            var gap = f.Position - b.Position;
            var d = gap.Length;
            if (d >= b.Radius || f.Flight?.Launching(World, b, hour) == true) continue;
            var dir = d > 1e-9 ? gap * (1 / d) : new Vec2(1, 0);
            var by = b.Position + dir * (b.Radius * 1.02) - f.Position;
            f.Position += by;
            var inward = Vec2.Dot(f.Velocity, dir);
            if (f.Flight is Flight flight)
            {
                flight.Legs = flight.Legs.Select(l => l.Shifted(by)).ToList();
                if (flight.TargetBodyId == null && flight.TargetFleetId == null) flight.Target += by;
            }
            else if (inward < 0) f.Velocity -= dir * inward;
            Log.Info("fleet", $"{f.Name} pulls up clear of {b.Name}", new { fleetId = f.Id, bodyId = b.Id });
        }
    }

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
                if (flight.FlybyBurn > 0 && flight.FlybyHour > t0 && flight.FlybyHour <= t1) BurnFuel(f, flight.FlybyBurn);
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
                    // Any planet you hold refuels; only the shipyard and dry docks repair and rearm.
                    Refill(f, dt, body);
                    if (Colonies.RepairsHere(World, body, f.Side)) Combat.RepairAtHome(World, f, dt, body);
                }
            }
            else
            {
                f.Position += f.Velocity * dt;
            }
            if (f.Hold != HoldStyle.Orbit || f.Flight != null) KeepOutOfBodies(f, t1);
            Formations.Keep(World, f);
            foreach (var s in World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
        }
    }

    /// <summary>
    /// Ships point their engines where the thrust goes: a fleet that changed velocity this step faces
    /// along that change (so it flips round to brake). Coasting or holding keeps the last facing.
    /// </summary>
    private static void UpdateFacing(List<(Fleet Fleet, Vec2 Velocity)> before, double hour)
    {
        foreach (var (f, v0) in before)
        {
            var dv = f.Velocity - v0;
            f.Thrusting = dv.Length > 1e-6;
            if (f.Thrusting)
            {
                f.Facing = DetMath.Atan2(dv.Y, dv.X);
                f.LastBurnHour = hour;
            }
            // Engines stay hot for a while after a burn, so the fleet is still easier to see.
            f.Warm = !f.Thrusting && hour - f.LastBurnHour <= GameData.Sensors.WarmHours;
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

    private void Refill(Fleet f, double hours, Body body)
    {
        foreach (var s in World.ShipsOf(f))
        {
            var max = Fitting.MaxSupplies(World, s);
            var want = Math.Min(max - s.Supplies, max * RefillPerDay * hours / 24);
            // Fuel comes out of the planet's store; with none it goes slowly.
            if (want > 1e-9) s.Supplies += want * Fuels.Refuel(World, s, f, body, want);
            SupplyLines.Restock(World, s, hours, World.Loop ? body : null);
        }
    }
}

public enum EffectKind { RailHit, RailMiss, PointDefenceKill, MissileHit, Volley }

/// <summary>Something to draw for a moment: a railgun shot (from, to), point defence killing a missile, a missile striking.</summary>
/// <summary>Something to draw for a moment. Width: how wide a volley's fan is at the target (Mm).</summary>
public sealed record CombatEffect(double Hour, EffectKind Kind, Vec2 From, Vec2 To, Side Side, double Width = 0);
