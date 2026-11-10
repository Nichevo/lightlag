using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;

namespace Lightlag.Headless;

/// <summary>Plays one computer-vs-computer game and prints what each side's mind is doing every few days.</summary>
public static class AiTrace
{
    public static int Run(Options opts)
    {
        var seed = opts.GetULong("seed") ?? 1;
        var days = (int)(opts.GetLong("days") ?? 120);
        var every = (int)(opts.GetLong("every") ?? 10);
        using var log = GameLog.Memory(Cli.LoadBuild());
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, log, options: GameOptions.AiVsAi(opts.Has("easy") ? Difficulty.Easy : Difficulty.Normal));
        var w = sim.World;
        while (w.Tick < days * GameTime.TicksPerDay && w.Winner == null)
        {
            sim.Step();
            if (w.Sub != 0 || w.Tick % (every * GameTime.TicksPerDay) != 0) continue;
            Console.WriteLine($"=== {GameTime.Format(w.Tick)}");
            foreach (var m in w.Minds)
            {
                var me = Economy.Of(w, m.Side);
                Console.WriteLine($"  {m.Side} {m.Personality} ratio {m.Ratio:0.00}: Industry {me.Industry:0}, planets {Economy.Claimable(w).Count(b => b.Owner == m.Side)}, "
                                  + $"power {m.OwnPower:0} (guess enemy {m.EnemyPowerGuess:0}). {m.Goal}");
                foreach (var f in w.Fleets.Where(f => f.Side == m.Side))
                {
                    var job = m.JobOf(f.Id);
                    var where = f.OrbitBodyId is int b && f.Flight == null ? $"at {w.FindBody(b)!.Name}" : f.Flight != null ? $"flying to {(f.Flight.TargetBodyId is int t ? w.FindBody(t)!.Name : "a point")}" : "drifting";
                    Console.WriteLine($"    {f.Name,-16} {f.ShipIds.Count} ships {Stats.Power(w, f),5:0} power {where,-22} {(job == null ? "no job" : $"{job.Kind}: {job.Why}")}"
                                      + $" armour {Combat.ArmourShare(w, f):P0}" + (f.Retreating ? " RETREATING" : "") + (f.Maneuvering ? " maneuvering" : "") + (f.JoinFleetId != null ? " joining" : "")
                                      + (f.Flight != null ? $" (left day {f.Flight.DepartHour / 24:0}, arrives day {f.Flight.ArriveHour / 24:0})" : ""));
                }
            }
        }
        Console.WriteLine(w.Winner != null ? $"{w.Winner} {w.WinReason} on {GameTime.Format(w.WinHour ?? w.Hour)}" : "No winner");
        Console.WriteLine(Stats.Summary(w));
        return 0;
    }
}
