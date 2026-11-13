using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The Forward guard role: a fleet waits at a post central to the planets picked for it and answers any of them, however far.</summary>
public class ForwardGuardTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Body Named(Simulation sim, string key) => sim.World.Bodies.First(b => b.Key == key);

    [Fact]
    public void Its_post_is_central_to_the_planets_it_guards()
    {
        var sim = NewGame();
        var w = sim.World;
        var guard = w.Fleets.First(f => f.Side == Side.Player);
        var a = Named(sim, "vela");
        var b = Named(sim, "rust");
        a.Owner = Side.Player;
        b.Owner = Side.Player;
        new SetFleetRoleCommand(guard.Id, FleetRole.ForwardGuard).Apply(w, sim);
        new SetGuardPlanetsCommand(guard.Id, new List<int> { a.Id, b.Id, Named(sim, "tamber").Id }).Apply(w, sim);
        // Only planets of its own side count.
        Assert.Equal(new[] { a.Id, b.Id }.OrderBy(x => x).ToList(), guard.GuardBodyIds);
        var (at, planet) = HelpCalls.PostOf(w, guard)!.Value;
        var mid = (a.Position + b.Position) * 0.5;
        if (planet == null) Assert.True(Vec2.Distance(at, mid) < 1);
        // The farthest guarded planet is no farther from the post than from either planet's own orbit.
        var far = Math.Max(Vec2.Distance(at, a.Position), Vec2.Distance(at, b.Position));
        Assert.True(far <= Vec2.Distance(a.Position, b.Position) + 1);
        Assert.Contains("Guarding", HelpCalls.GuardStatus(w, guard));
    }

    [Fact]
    public void A_guard_answers_its_planets_however_far_and_not_other_planets()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = Economy.HomeOf(w, Side.Player)!;
        var guard = w.Fleets.First(f => f.Side == Side.Player);
        var enemy = w.Fleets.First(f => f.Side == Side.Rival);
        guard.Flight = null;
        enemy.Flight = null;
        Fleets.EnterDrift(enemy);
        enemy.Velocity = Vec2.Zero;
        enemy.Position = home.Position + new Vec2(0.05 * Physics.MmPerAu, 0);
        Sensors.Reveal(w, Side.Player, enemy);
        // The guard sits far out, beyond a Defence fleet's Region range.
        Fleets.EnterDrift(guard);
        guard.Velocity = Vec2.Zero;
        guard.Position = home.Position + new Vec2(0, 1.6 * Physics.MmPerAu);
        HelpCalls.Update(sim);
        var call = HelpCalls.CallAbout(w, Side.Player, enemy.Id)!;
        new SetFleetRoleCommand(guard.Id, FleetRole.Defence).Apply(w, sim);
        Assert.Equal("it is out of range", HelpCalls.WhyNot(w, guard, call, checkFree: false));

        new SetFleetRoleCommand(guard.Id, FleetRole.ForwardGuard).Apply(w, sim);
        var other = w.Bodies.First(b => b.Owner == Side.Player && b.Id != call.BodyId);
        new SetGuardPlanetsCommand(guard.Id, new List<int> { other.Id }).Apply(w, sim);
        Assert.Equal("it guards other planets", HelpCalls.WhyNot(w, guard, call, checkFree: false));
        new SetGuardPlanetsCommand(guard.Id, new List<int> { call.BodyId!.Value }).Apply(w, sim);
        // Range doesn't stop it now (only the call's light still on its way may).
        var why = HelpCalls.WhyNot(w, guard, call, checkFree: false);
        Assert.True(why is null or "the call has not reached it yet", why);
    }

    [Fact]
    public void A_free_guard_flies_to_its_post_and_counts_as_free_on_the_way()
    {
        var sim = NewGame();
        var w = sim.World;
        var guard = w.Fleets.First(f => f.Side == Side.Player);
        guard.Flight = null;
        var vela = Named(sim, "vela");
        vela.Owner = Side.Player;
        new SetFleetRoleCommand(guard.Id, FleetRole.ForwardGuard).Apply(w, sim);
        new SetGuardPlanetsCommand(guard.Id, new List<int> { vela.Id }).Apply(w, sim);
        HelpCalls.Update(sim);
        Assert.True(guard.GuardPosting);
        Assert.NotNull(guard.Flight);
        Assert.True(HelpCalls.Free(w, guard));
        Assert.Contains("on its way to its post", HelpCalls.GuardStatus(w, guard));
        // A direct order takes it off the trip to its post.
        sim.Issue(new StopFleetCommand(guard.Id));
        sim.ApplyPending();
        Assert.False(guard.GuardPosting);
    }
}
