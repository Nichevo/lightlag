using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class EngagementTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    [Fact]
    public void Head_on_approach_shows_closest_pass_weapon_windows_and_options()
    {
        var sim = Sandbox(new SandboxSetup(2, 1, 2, 0, 1, 1, Stance.Hold, Stance.Hold, 400));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        theirs.Velocity = (mine.Position - theirs.Position).Normalized() * 100;
        Sensors.Reveal(w, Side.Player, theirs);

        var e = Engagement.Of(w, mine, theirs.Id)!;
        Assert.InRange(e.Closing, 99, 101);
        Assert.InRange(e.ClosestInHours, 3.5, 4.5);
        Assert.True(e.ClosestGap < 3);
        Assert.NotNull(e.OurMissiles);
        Assert.InRange(e.OurMissiles!.From, (400 - 60) / 100.0 - 0.1, (400 - 60) / 100.0 + 0.1);
        Assert.NotNull(e.TheirRailguns);
        Assert.True(e.TheirRailguns!.From > e.OurMissiles.From);
        Assert.Contains(e.Options, o => o.Key == "engage" && o.Problem == null);
        Assert.Contains(e.Options, o => o.Key == "home");
        Assert.Contains(e.Options, o => o.Key == "away" && o.Problem == null);
    }

    [Fact]
    public void Missile_option_says_why_when_the_fleet_has_no_missiles()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 2, 0, 1, 1, Stance.Hold, Stance.Hold, 400));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        var e = Engagement.Of(w, mine, theirs.Id)!;
        var missiles = e.Options.Single(o => o.Key == "missiles");
        Assert.NotNull(missiles.Problem);
        Assert.Null(missiles.Request);
    }

    [Fact]
    public void Dodging_costs_fuel_and_takes_fewer_hits()
    {
        double Run(bool dodge, out double fuelUsed, out double dodgeCost)
        {
            // 20,000 km apart: slugs take about 80 s to arrive, time enough to dodge.
            var sim = Sandbox(new SandboxSetup(3, 0, 3, 0, 1, 1, Stance.Engage, Stance.Hold, 20), seed: 21);
            var w = sim.World;
            var rival = w.Fleets.First(f => f.Side == Side.Rival);
            rival.Dodge = dodge ? DodgeMode.Sidestep : DodgeMode.Off;
            var before = w.ShipsOf(rival).Sum(s => s.Supplies);
            sim.StepMany(3);
            var hits = w.Battles.Sum(b => b.For(Side.Player).RailHits);
            var shots = w.Battles.Sum(b => b.For(Side.Player).RailShots);
            fuelUsed = before - w.ShipsOf(rival).Sum(s => s.Supplies) - w.Battles.Sum(b => b.For(Side.Rival).RailShots) * GameData.Combat.Railgun.SuppliesPerShot;
            // Each slug at a dodging fleet costs every ship in it the dodge's speed change.
            dodgeCost = shots * GameData.Combat.DodgeDeltaV * 0.025 * 3;
            return shots == 0 ? 0 : (double)hits / shots;
        }
        var plain = Run(false, out _, out _);
        var dodging = Run(true, out var fuelDodging, out var cost);
        Assert.True(dodging < plain * 0.8, $"hit rate {dodging:0.000} dodging vs {plain:0.000}");
        Assert.True(fuelDodging >= cost * 0.5, $"fuel {fuelDodging:0.00}, dodges should cost about {cost:0.00}");
    }

    [Fact]
    public void Sidestep_beats_aimed_fire_a_fan_beats_a_sidestep_and_a_hard_burn_beats_both()
    {
        const double far = 200; // seconds: time enough to dodge fully
        var sidestepFocused = Combat.DodgeFactor(DodgeMode.Sidestep, FireMode.Focused, far);
        var sidestepSpread = Combat.DodgeFactor(DodgeMode.Sidestep, FireMode.Spread, far);
        var hardSpread = Combat.DodgeFactor(DodgeMode.HardBurn, FireMode.Spread, far);
        Assert.True(sidestepFocused < 0.6);
        Assert.True(sidestepSpread > 0.85);
        Assert.True(hardSpread < sidestepSpread);
        Assert.Equal(1, Combat.DodgeFactor(DodgeMode.Off, FireMode.Focused, far));
    }

    [Fact]
    public void Dodging_does_less_close_up()
    {
        var far = Combat.DodgeFactor(DodgeMode.HardBurn, FireMode.Focused, 120);
        var near = Combat.DodgeFactor(DodgeMode.HardBurn, FireMode.Focused, 10);
        Assert.True(near > far + 0.3, $"near {near:0.00} far {far:0.00}");
    }

    [Fact]
    public void Spread_fire_sends_more_slugs_and_the_guns_cool_longer()
    {
        int Shots(FireMode mode, out double readyIn)
        {
            var sim = Sandbox(new SandboxSetup(1, 0, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 5), seed: 4);
            var w = sim.World;
            var mine = w.Fleets.First(f => f.Side == Side.Player);
            mine.Fire = mode;
            readyIn = 0;
            for (var i = 0; i < 2000 && w.Battles.Sum(b => b.For(Side.Player).RailShots) == 0; i++) sim.Step();
            readyIn = w.ShipsOf(mine).First().RailsReadyHour - w.Hour;
            return w.Battles.Sum(b => b.For(Side.Player).RailShots);
        }
        var focused = Shots(FireMode.Focused, out var focusedWait);
        var spread = Shots(FireMode.Spread, out var spreadWait);
        Assert.True(spread >= focused * 3, $"{spread} vs {focused}");
        Assert.True(spreadWait > focusedWait * 2, $"{spreadWait} vs {focusedWait}");
    }

    [Fact]
    public void All_lists_near_or_closing_engagements_and_skips_far_quiet_ones()
    {
        var sim = Sandbox(new SandboxSetup(2, 1, 2, 0, 1, 1, Stance.Hold, Stance.Hold, 200));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        mine.Velocity = Vec2.Zero;
        theirs.Velocity = Vec2.Zero;
        Sensors.Reveal(w, Side.Player, theirs);

        var near = Engagement.All(w, Side.Player);
        var pair = Assert.Single(near);
        Assert.Equal((mine.Id, theirs.Id), (pair.Mine, pair.Enemy));
        Assert.InRange(pair.Gap, 199, 201);

        // Far away and drifting off: nothing to look at.
        theirs.Position = mine.Position + new Vec2(5000, 0);
        theirs.Velocity = new Vec2(10, 0);
        Sensors.Reveal(w, Side.Player, theirs);
        Assert.Empty(Engagement.All(w, Side.Player));

        // Far away but heading this way: listed.
        theirs.Velocity = new Vec2(-200, 0);
        Sensors.Reveal(w, Side.Player, theirs);
        Assert.Single(Engagement.All(w, Side.Player));
    }

    [Fact]
    public void Ship_classes_are_told_only_on_a_close_look()
    {
        var sim = Sandbox(new SandboxSetup(2, 1, 1, 1, 1, 1, Stance.Hold, Stance.Hold, 400));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        w.Sightings.Clear();
        // Burning far off: seen and counted, but not told apart.
        theirs.Thrusting = true;
        Sensors.Update(sim);
        var s = Sensors.Find(w, Side.Player, theirs.Id);
        Assert.NotNull(s);
        Assert.Equal(2, s!.Ships);
        Assert.Empty(s.Classes);
        Assert.Null(Engagement.Of(w, mine, theirs.Id)!.TheyHaveMissiles);

        // Close up: classes and what they carry.
        theirs.Position = mine.Position + new Vec2(GameData.Sensors.IdentifyMm * 0.5, 0);
        Sensors.Update(sim);
        Assert.Equal(2, s.Classes.Count);
        var e = Engagement.Of(w, mine, theirs.Id)!;
        Assert.True(e.TheyHaveMissiles);
        Assert.Contains("Missile destroyer", e.EnemyClasses);
    }

    [Fact]
    public void A_known_track_lets_sensors_spot_a_cold_fleet_further_off()
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 400));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        mine.Velocity = theirs.Velocity = Vec2.Zero;
        // Cold, twice as far as a frigate sees a cold frigate unaided.
        var plain = Sensors.Range(Sensors.SensorOf(w, mine), Sensors.Signature(w, theirs));
        theirs.Position = mine.Position + new Vec2(plain * 2, 0);
        foreach (var b in w.Bodies.Where(b => b.Owner == Side.Player)) b.Owner = null; // no planet sensors

        w.Sightings.Clear();
        Sensors.Update(sim);
        Assert.Null(Sensors.Find(w, Side.Player, theirs.Id));

        // Seen once, where it still is: the sensors know where to look.
        Sensors.Reveal(w, Side.Player, theirs);
        Sensors.Find(w, Side.Player, theirs.Id)!.Visible = false;
        Sensors.Update(sim);
        Assert.True(Sensors.Find(w, Side.Player, theirs.Id)!.Visible);

        // Far off its predicted track: no help.
        Sensors.Find(w, Side.Player, theirs.Id)!.Position = theirs.Position + new Vec2(0, 500);
        Sensors.Find(w, Side.Player, theirs.Id)!.Visible = false;
        Sensors.Update(sim);
        Assert.False(Sensors.Find(w, Side.Player, theirs.Id)!.Visible);
    }

    [Fact]
    public void A_harder_burn_closes_sooner_for_more_fuel()
    {
        var sim = Sandbox(new SandboxSetup(2, 0, 2, 0, 1, 1, Stance.Hold, Stance.Hold, 400));
        var w = sim.World;
        var mine = w.Fleets.First(f => f.Side == Side.Player);
        var theirs = w.Fleets.First(f => f.Side == Side.Rival);
        FlightPlan Plan(double burn)
        {
            var o = Engagement.Of(w, mine, theirs.Id, burn)!.Options.Single(x => x.Key == "engage");
            Assert.Null(o.Problem);
            return FlightPlanner.Plan(w, mine, o.Request!);
        }
        var gentle = Plan(0.4);
        var hard = Plan(1.0);
        Assert.True(hard.Flight!.ArriveHour < gentle.Flight!.ArriveHour, $"hard {hard.Flight.ArriveHour - w.Hour:0.00} h {hard.DeltaV:0} dv, gentle {gentle.Flight.ArriveHour - w.Hour:0.00} h {gentle.DeltaV:0} dv, cruise {hard.Flight.CruiseSpeed:0}/{gentle.Flight.CruiseSpeed:0}");
        Assert.True(hard.DeltaV > gentle.DeltaV);
    }

    [Fact]
    public void A_fleet_can_catch_up_with_and_join_a_drifting_fleet()
    {
        var sim = Sandbox(new SandboxSetup(3, 0, 1, 0, 1, 1, Stance.Hold, Stance.Hold, 3000));
        var w = sim.World;
        var lead = w.Fleets.First(f => f.Side == Side.Player);
        sim.Issue(new SplitFleetCommand(lead.Id, new List<int> { lead.ShipIds[^1] }));
        sim.StepMany(1);
        var straggler = w.Fleets.Last(f => f.Side == Side.Player);
        Assert.NotEqual(lead.Id, straggler.Id);
        // The lead drifts on at 50 Mm/h; the straggler is told to join it.
        lead.Flight = null;
        lead.Hold = HoldStyle.Drift;
        lead.OrbitBodyId = null;
        lead.Velocity = new Vec2(50, 0);
        sim.Issue(new GatherFleetsCommand(lead.Id, new List<int> { straggler.Id }, 1.0));
        sim.StepMany(1);
        Assert.Equal(ChaseMode.Join, straggler.Chase?.Mode);
        for (var i = 0; i < 200 && w.FindFleet(straggler.Id) != null; i++) sim.Step();
        Assert.Null(w.FindFleet(straggler.Id));
        Assert.Equal(3, lead.ShipIds.Count);
    }
}
