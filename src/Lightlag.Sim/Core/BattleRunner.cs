using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>How the player's fleet comes at the enemy in a sandbox battle.</summary>
/// <summary>
/// How the player's fleet comes at the enemy in a sandbox battle. ColdDrift is the ambush: a short burn
/// at the start, then a long cold coast in, so the enemy loses track of it until it is close.
/// </summary>
public enum Approach { BrakeSlow, BrakeFast, FastPass, ColdDrift, Defend }

/// <summary>The result of one sandbox battle.</summary>
public sealed record BattleOutcome(
    string Winner, int PlayerLost, int RivalLost, double PlayerDamage, double RivalDamage, double Hours,
    int PlayerMissiles, int RivalMissiles, int ShotDown, int RailHits);

/// <summary>
/// Plays one battle in the sandbox with no one at the controls: the player's fleet attacks with the
/// chosen approach (or sits and defends while the rival attacks). Used by battle sweeps and tests.
/// </summary>
public static class BattleRunner
{
    public const double StartDistanceMm = 300;

    public static BattleOutcome Run(SandboxSetup setup, Approach approach, ulong seed, GameLog log, int maxHours = 72, double distanceMm = StartDistanceMm)
    {
        var defend = approach == Approach.Defend;
        setup = setup with
        {
            DistanceMm = distanceMm,
            PlayerStance = Stance.Engage,
            RivalStance = defend ? Stance.Engage : Stance.Hold,
        };
        var sim = Simulation.NewGame(Scenarios.BattleSandbox, seed, log, sandbox: setup);
        sim.StrictRules = true;
        sim.RuleCheckEveryTicks = 1;
        var w = sim.World;
        var player = w.Fleets.FirstOrDefault(f => f.Side == Side.Player);
        var rival = w.Fleets.FirstOrDefault(f => f.Side == Side.Rival);
        if (player == null || rival == null) throw new ArgumentException("Both sides need ships");

        var (attacker, target) = defend ? (rival, player) : (player, rival);
        var (speed, arrival) = approach switch
        {
            Approach.BrakeSlow => (0.4, ArrivalStyle.Brake),
            Approach.FastPass => (1.0, ArrivalStyle.FastPass),
            Approach.ColdDrift => (FlightPlanner.MinSpeedFraction, ArrivalStyle.FastPass),
            _ => (1.0, ArrivalStyle.Brake),
        };
        sim.Issue(new PlanFlightCommand(attacker.Id,
            new FlightRequest(null, target.Position, speed, arrival, HoldStyle.Drift, 0, TargetFleetId: target.Id)) { Source = "battle-runner" });

        // A slow cold drift takes longer to arrive.
        var trip = sim.PendingCommands.OfType<PlanFlightCommand>().Select(c => FlightPlanner.Plan(w, attacker, c.Request).Flight?.ArriveHour ?? 0).DefaultIfEmpty(0).Max();
        var end = w.Tick + Math.Max(maxHours, (int)Math.Ceiling(trip - w.Hour) + 24);
        var fought = false;
        while (w.Tick < end || w.Sub != 0)
        {
            sim.Step();
            if (w.ActiveBattle != null) fought = true;
            else if (fought) break;
            if (!w.Fleets.Any(f => f.Side == Side.Player) || !w.Fleets.Any(f => f.Side == Side.Rival)) break;
        }

        var p = new BattleSide();
        var r = new BattleSide();
        foreach (var b in w.Battles)
        {
            Add(p, b.For(Side.Player));
            Add(r, b.For(Side.Rival));
        }
        var hours = w.Battles.Count > 0 ? (w.Battles[^1].EndHour ?? w.Hour) - w.Battles[0].StartHour : 0;
        return new BattleOutcome(Winner(w, p, r), p.ShipsLost.Count, r.ShipsLost.Count, p.DamageDealt, r.DamageDealt, hours,
            p.MissilesFired, r.MissilesFired, p.ShotDown + r.ShotDown, p.RailHits + r.RailHits);
    }

    private static void Add(BattleSide total, BattleSide s)
    {
        total.ShipsLost.AddRange(s.ShipsLost);
        total.DamageDealt += s.DamageDealt;
        total.MissilesFired += s.MissilesFired;
        total.ShotDown += s.ShotDown;
        total.RailHits += s.RailHits;
    }

    /// <summary>Whoever still has an unbroken fleet; if both do, whoever did clearly more damage.</summary>
    private static string Winner(World w, BattleSide p, BattleSide r)
    {
        bool Standing(Side side) => w.Fleets.Any(f => f.Side == side && !f.Retreating);
        var ps = Standing(Side.Player);
        var rs = Standing(Side.Rival);
        if (ps && !rs) return "player";
        if (rs && !ps) return "rival";
        if (p.DamageDealt > r.DamageDealt * 1.5 + 20) return "player";
        if (r.DamageDealt > p.DamageDealt * 1.5 + 20) return "rival";
        return "draw";
    }
}
