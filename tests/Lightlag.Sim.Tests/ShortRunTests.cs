using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Standing runs set up by transport automation for planets with a lasting shortage (ShortRuns).</summary>
public class ShortRunTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 3, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        Planets.EnsureStarted(sim.World);
        return sim;
    }

    private static (Body Home, Body Moon) Planets2(World w)
    {
        var home = Economy.HomeOf(w, Side.Player)!;
        var moon = w.Bodies.First(m => m.ParentId == home.Id && m.Kind == BodyKind.Moon && m.Owner == Side.Player);
        return (home, moon);
    }

    /// <summary>The moon burns iron (as a fuel draw would) with none of its own; home has plenty.</summary>
    private static void Drain(Body home, Body moon, string good = "iron", double perDay = 3)
    {
        moon.FuelUse[good] = perDay;
        moon.Stock[good] = 0;
        home.Stock[good] = 2000;
    }

    /// <summary>Steps a number of ticks, keeping the drain going (fuel draws fade on their own).</summary>
    private static void Steps(Simulation sim, Body moon, int ticks, params string[] goods)
    {
        for (var i = 0; i < ticks; i++)
        {
            foreach (var g in goods) moon.FuelUse[g] = 3;
            sim.Step();
        }
    }

    [Fact]
    public void The_game_starts_with_a_plain_freighter_on_each_side_free_for_transport()
    {
        var sim = NewGame();
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var freighters = sim.World.Ships.Where(s => s.Side == side && s.ClassId == "freighter").ToList();
            Assert.Single(freighters);
            Assert.Contains(Colonies.FreeHaulers(sim.World, side), f => f.ShipIds.Contains(freighters[0].Id));
        }
    }

    [Fact]
    public void A_lasting_shortage_gets_an_auto_run_from_a_planet_with_spare_and_a_freighter_carries_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = Planets2(w);
        Drain(home, moon);

        var plan = Assert.Single(ShortRuns.Wanted(w, Side.Player), p => p.To.Id == moon.Id && p.Good == "iron");
        Assert.Equal(home.Id, plan.From.Id);
        Assert.InRange(plan.EveryDays, ShortRuns.MinEveryDays, ShortRuns.MaxEveryDays);

        Assert.Equal(AutoLevel.Auto, Stewards.LevelFor(w, Side.Player, AutoArea.Transport));
        Stewards.Update(sim);
        var run = Assert.Single(w.FixedRuns, r => r.Side == Side.Player && r.ToBodyId == moon.Id && r.Good == "iron");
        Assert.True(run.Auto);
        Assert.Equal(home.Id, run.FromBodyId);
        Assert.Contains("more than it makes", run.Why);
        Assert.Contains(Economy.Of(w, Side.Player).AutoNotes, n => n.Area == AutoArea.Transport && n.Text.Contains("run: iron"));
        // Covered now: not offered again.
        Assert.DoesNotContain(ShortRuns.Wanted(w, Side.Player), p => p.To.Id == moon.Id && p.Good == "iron");

        // A freighter takes it and brings iron.
        Steps(sim, moon, 6 * 24, "iron");
        Assert.True(Colonies.Held(moon, "iron") > 0 || w.Shipments.Any(s => s.RunId == run.Id));
        Steps(sim, moon, 20 * 24, "iron");
        Assert.True(SupplyChain.Book(w, Side.Player).Deliveries.Any(d => d.RunId == run.Id), "the run delivered");
    }

    [Fact]
    public void Advise_offers_the_run_and_Manual_leaves_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = Planets2(w);
        Drain(home, moon);
        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Transport, AutoLevel.Advise));
        sim.ApplyPending();
        Stewards.Update(sim);
        Assert.Empty(w.FixedRuns.Where(r => r.Side == Side.Player));
        var need = Assert.Single(Economy.Of(w, Side.Player).Needs, n => n.Key == $"run:{moon.Id}:iron");
        Assert.IsType<FixedRunCommand>(need.Act);
        // The one-off send for the same good is not listed twice.
        Assert.DoesNotContain(Economy.Of(w, Side.Player).Needs, n => n.Key == $"ship:{moon.Id}:iron");

        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Transport, AutoLevel.Manual));
        sim.ApplyPending();
        Stewards.Update(sim);
        Assert.Empty(w.FixedRuns.Where(r => r.Side == Side.Player));
        Assert.DoesNotContain(Economy.Of(w, Side.Player).Needs, n => n.Key == $"run:{moon.Id}:iron");
    }

    [Fact]
    public void One_freighter_serves_two_runs_one_after_another()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = Planets2(w);
        Drain(home, moon, "iron");
        Drain(home, moon, "copper");
        Assert.Single(w.Fleets.Where(f => f.Side == Side.Player && w.ShipsOf(f).Any(SupplyLines.IsFreighter)));
        foreach (var good in new[] { "iron", "copper" })
            sim.Issue(new FixedRunCommand(Side.Player, home.Id, moon.Id, good, 10, Auto: true, Why: "test"));
        sim.ApplyPending();
        Steps(sim, moon, 30 * 24, "iron", "copper");
        var runs = w.FixedRuns.Where(r => r.Side == Side.Player && r.ToBodyId == moon.Id).Select(r => r.Id).ToList();
        Assert.Equal(2, runs.Count);
        var delivered = SupplyChain.Book(w, Side.Player).Deliveries.Where(d => d.RunId != null).Select(d => d.RunId!.Value).Distinct().ToList();
        Assert.All(runs, id => Assert.Contains(id, delivered));
    }

    [Fact]
    public void Status_says_why_a_run_waits_and_auto_stops_a_run_when_the_shortage_ends()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = Planets2(w);
        Drain(home, moon);
        Stewards.Update(sim);
        var run = Assert.Single(w.FixedRuns, r => r.Side == Side.Player && r.Auto);

        // Every freighter busy elsewhere: the run waits and says so.
        foreach (var f in Colonies.FreeHaulers(w, Side.Player).ToList()) f.SupplyRunFor = -1;
        Assert.StartsWith("WAITING", ShortRuns.Status(w, run));
        foreach (var f in w.Fleets.Where(f => f.SupplyRunFor == -1)) f.SupplyRunFor = null;
        Assert.Equal("due now", ShortRuns.Status(w, run));

        // The moon stops burning iron and is full: Auto stops the run, with a note.
        moon.FuelUse.Remove("iron");
        moon.Stock["iron"] = Colonies.Target(moon, "iron") + 50;
        Assert.NotNull(ShortRuns.RetireWhy(w, run));
        Stewards.Update(sim);
        Assert.DoesNotContain(w.FixedRuns, r => r.Id == run.Id);
        Assert.Contains(Economy.Of(w, Side.Player).AutoNotes, n => n.Text.StartsWith("Stop the iron run"));
    }

    [Fact]
    public void A_hand_stopped_auto_run_is_not_set_up_again_and_a_kept_run_is_never_stopped()
    {
        var sim = NewGame();
        var w = sim.World;
        var (home, moon) = Planets2(w);
        Drain(home, moon);
        Stewards.Update(sim);
        var run = Assert.Single(w.FixedRuns, r => r.Side == Side.Player && r.Auto);
        sim.Issue(new FixedRunCommand(Side.Player, 0, 0, "", 0, RemoveRunId: run.Id));
        sim.ApplyPending();
        Stewards.Update(sim);
        Assert.Empty(w.FixedRuns.Where(r => r.Side == Side.Player));

        // A run kept by hand stays when the shortage ends.
        sim.Issue(new FixedRunCommand(Side.Player, home.Id, moon.Id, "iron", 10, Auto: true));
        sim.ApplyPending();
        var kept = w.FixedRuns.Single(r => r.Side == Side.Player);
        sim.Issue(new KeepRunCommand(Side.Player, kept.Id));
        sim.ApplyPending();
        Assert.False(kept.Auto);
        moon.FuelUse.Remove("iron");
        moon.Stock["iron"] = Colonies.Target(moon, "iron") + 50;
        Stewards.Update(sim);
        Assert.Contains(w.FixedRuns, r => r.Id == kept.Id);
    }
}
