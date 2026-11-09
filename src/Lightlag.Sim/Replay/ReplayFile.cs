using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Replay;

public sealed record RecordedCommand(long Tick, GameCommand Command);

/// <summary>The state hash at a moment in the game, used to check that a replay stays exact.</summary>
public sealed record Checkpoint(long Tick, string Hash);

/// <summary>
/// A replay: scenario, seed and every command with the tick it took effect, plus state hashes along
/// the way. Replaying it rebuilds the exact game, so a crash or odd behaviour can be watched again.
/// </summary>
public sealed class ReplayFile
{
    public int Format { get; set; } = 1;
    public string Build { get; set; } = "";
    public string Scenario { get; set; } = "";
    public ulong Seed { get; set; }
    public List<RecordedCommand> Commands { get; set; } = new();
    public List<Checkpoint> Checkpoints { get; set; } = new();

    public long LastTick => Math.Max(Checkpoints.Count > 0 ? Checkpoints[^1].Tick : 0,
                                     Commands.Count > 0 ? Commands[^1].Tick : 0);

    public string ToJson() => Json.Serialize(this, indented: true);
    public static ReplayFile FromJson(string json) => Json.Deserialize<ReplayFile>(json);
    public void WriteTo(string path) => File.WriteAllText(path, ToJson());
    public static ReplayFile Load(string path) => FromJson(File.ReadAllText(path));
}

public sealed record ReplayResult(bool Exact, long ReachedTick, string Message, Exception? Error = null)
{
    public bool Crashed => Error != null;
}

/// <summary>Plays a replay back and checks each recorded state hash.</summary>
public static class ReplayRunner
{
    /// <param name="untilTick">Stop here (default: the end of the replay).</param>
    /// <param name="onTick">Called after each tick, e.g. to draw or inspect the game.</param>
    public static ReplayResult Run(ReplayFile replay, GameLog log, long? untilTick = null, Action<Simulation>? onTick = null)
    {
        var sim = Simulation.NewGame(replay.Scenario, replay.Seed, log);
        var end = untilTick ?? replay.LastTick;
        var commands = replay.Commands.OrderBy(c => c.Tick).ToList();
        var checkpoints = replay.Checkpoints.ToDictionary(c => c.Tick, c => c.Hash);
        var ci = 0;
        try
        {
            if (checkpoints.TryGetValue(0, out var start) && start != sim.StateHash())
                return new ReplayResult(false, 0, "Start state differs: the scenario or this build changed how games start.");

            while (sim.World.Tick < end || (ci < commands.Count && commands[ci].Tick <= end))
            {
                while (ci < commands.Count && commands[ci].Tick == sim.World.Tick)
                    sim.Issue(commands[ci++].Command);
                sim.Step();
                onTick?.Invoke(sim);
                if (checkpoints.TryGetValue(sim.World.Tick, out var expected) && expected != sim.StateHash())
                {
                    log.Error("replay", $"Replay drifted at {GameTime.Format(sim.World.Tick)}", new { tick = sim.World.Tick, expected, actual = sim.StateHash() });
                    return new ReplayResult(false, sim.World.Tick,
                        $"Replay drifted from the recorded game at {GameTime.Format(sim.World.Tick)} (tick {sim.World.Tick}).");
                }
            }
            return new ReplayResult(true, sim.World.Tick, $"Replay matched the recorded game up to {GameTime.Format(sim.World.Tick)}.");
        }
        catch (Exception ex)
        {
            return new ReplayResult(true, sim.World.Tick,
                $"Replay reproduced a crash at {GameTime.Format(sim.World.Tick)}: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}
