using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The balance pass: braking for head-on meetings, the combat burn in a fight, missile fleets holding at range, the opening budget.</summary>
public class BalancePassTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);

    /// <summary>The rival fleet comes straight at the player's at this speed; returns the closing speed when the gap first falls inside railgun reach.</summary>
    private static (double Closing, bool Burned) HeadOn(bool combatBurn, double speed = 400)
    {
        var sim = Sandbox(new SandboxSetup(1, 0, 1, 0, PlayerStance: Stance.Engage, RivalStance: Stance.Hold, DistanceMm: 6000));
        var mine = FleetOf(sim, Side.Player);
        var theirs = FleetOf(sim, Side.Rival);
        mine.NoCombatBurn = !combatBurn;
        theirs.Velocity = new Vec2(-speed, 0);
        var burned = false;
        for (var i = 0; i < 20000; i++)
        {
            theirs.LastBurnHour = sim.World.Hour; // its engines are warm: seen from afar (a cold fleet is only seen close up)
            sim.Step();
            if (sim.World.FindFleet(mine.Id) is not Fleet m || sim.World.FindFleet(theirs.Id) is not Fleet t) break;
            burned |= m.CombatBurning;
            var gap = t.Position - m.Position;
            if (gap.Length <= GameData.Combat.Railgun.RangeMm)
                return (-Vec2.Dot(gap, t.Velocity - m.Velocity) / gap.Length, burned);
            if (sim.World.Hour > 48) break;
        }
        return (double.NaN, burned);
    }

    [Fact]
    public void Approach_speed_never_outruns_the_brakes()
    {
        Assert.Equal(0, Combat.ApproachSpeed(0, 600, 15), 9);
        var near = Combat.ApproachSpeed(10, 600, 15);
        Assert.True(near <= Math.Sqrt(2 * 15 * Combat.BrakeMargin * 10) + 1e-9);
        Assert.Equal(360, Combat.ApproachSpeed(1e6, 600, 150), 9); // far off: 60 % of top speed
        Assert.True(Combat.ApproachSpeed(-5, 600, 15) < 0);         // too close: back off
    }

    [Fact]
    public void An_engage_fleet_brakes_for_an_enemy_coming_head_on_with_the_combat_burn()
    {
        var (closing, burned) = HeadOn(combatBurn: true);
        Assert.False(double.IsNaN(closing));
        Assert.True(burned, "the combat burn should have been used");
        Assert.True(closing < 150, $"still closing at {closing:0} Mm/h inside railgun reach");
    }

    [Fact]
    public void Without_the_combat_burn_it_cannot_shed_a_fast_meeting()
    {
        var (closing, burned) = HeadOn(combatBurn: false);
        Assert.False(burned);
        Assert.True(double.IsNaN(closing) || closing > 200, $"closing at {closing:0} Mm/h");
    }

    [Fact]
    public void A_missile_only_fleet_holds_at_missile_range()
    {
        var sim = Sandbox(new SandboxSetup(0, 1, 1, 0, PlayerStance: Stance.Engage, RivalStance: Stance.Hold, DistanceMm: 95));
        var mine = FleetOf(sim, Side.Player);
        var theirs = FleetOf(sim, Side.Rival);
        theirs.LastBurnHour = sim.World.Hour; // warm engines: seen at this range
        sim.Step();
        var w = sim.World;
        Assert.True(Combat.WouldStandAtMissileRange(w, mine, theirs));
        var want = Heat.LaunchRange(mine) * Combat.MissileStandShare;
        // While it still has missiles it closes to its stand-off and no nearer (out of missiles, it just stops).
        double closest = double.MaxValue, last = double.MaxValue;
        while (sim.World.Hour < 6 && Combat.HasWorking(w, mine, Module.Missiles))
        {
            theirs.LastBurnHour = sim.World.Hour;
            sim.Step();
            last = Vec2.Distance(mine.Position, theirs.Position);
            closest = Math.Min(closest, last);
        }
        Assert.True(closest >= want - 5, $"came in to {closest:0.0}, wanted {want:0.0}");
        Assert.True(last <= want + 10, $"still {last:0.0} out, wanted {want:0.0}");
    }

    [Fact]
    public void The_opening_moves_a_third_of_the_ships_share_to_buildings_and_settling()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory());
        var start = Budget.Of(sim.World, Side.Player).Pots.Select(p => p.Share).ToArray();
        Assert.True(Budget.InOpening(sim.World));
        Assert.InRange(start[(int)Pot.Ships], 19, 21);
        Assert.InRange(start[(int)Pot.Buildings], 29, 31);
        Assert.InRange(start[(int)Pot.Transport], 14, 16);
    }
}
