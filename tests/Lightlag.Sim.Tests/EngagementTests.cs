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
            var sim = Sandbox(new SandboxSetup(3, 0, 3, 0, 1, 1, Stance.Engage, Stance.Hold, 5), seed: 21);
            var w = sim.World;
            var rival = w.Fleets.First(f => f.Side == Side.Rival);
            rival.Dodge = dodge;
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
}
