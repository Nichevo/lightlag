using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>M9: heat, shields, firing rules, long-range railguns, held and dropped missiles.</summary>
public class HeatTests
{
    private static Simulation Sandbox(SandboxSetup setup, ulong seed = 3)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory(), sandbox: setup);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet FleetOf(Simulation sim, Side side) => sim.World.Fleets.First(f => f.Side == side);

    [Fact]
    public void New_ships_start_cool_with_shields_up_and_sinks_loaded()
    {
        var sim = Sandbox(new SandboxSetup());
        foreach (var s in sim.World.Ships)
        {
            var cls = GameData.ShipClass(s.ClassId);
            Assert.Equal(0, s.Heat);
            Assert.Equal(cls.HeatSinks, s.HeatSinks);
            Assert.Equal(cls.Shield, s.Shield);
        }
    }

    [Fact]
    public void Running_cold_hides_the_fleet_but_heat_builds_until_it_must_vent()
    {
        var sim = Sandbox(new SandboxSetup(DistanceMm: 5000));
        var w = sim.World;
        var f = FleetOf(sim, Side.Player);
        f.AutoSinks = false;
        var before = Sensors.Signature(w, f);
        f.Radiators = RadiatorMode.Cold;
        Assert.True(Sensors.Signature(w, f) < before * 0.5);
        var clock = Heat.ColdClockHours(w, f)!.Value;
        Assert.True(clock > 24, $"cold clock only {clock} h");
        for (var i = 0; i < 2000 && !w.ShipsOf(f).Any(s => s.Overheated); i++) Heat.Update(sim, 1);
        Assert.Contains(w.ShipsOf(f), s => s.Overheated);
        Assert.Equal(before, Sensors.Signature(w, f), 6); // venting: the radiators are out, the cover is gone
    }

    [Fact]
    public void An_overheated_ship_cannot_fire_its_railguns_and_loses_its_shield()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 1, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0, DistanceMm: 30));
        var w = sim.World;
        var mine = w.Ships.First(s => s.Side == Side.Player);
        FleetOf(sim, Side.Player).AutoSinks = false;
        mine.Heat = Heat.Capacity(mine) + 1;
        for (var i = 0; i < 30; i++) sim.Step();
        Assert.True(mine.Overheated);
        Assert.Equal(0, mine.Shield);
        Assert.True(w.Battles.Count > 0);
        Assert.Equal(0, w.Battles[0].For(Side.Player).RailShots);
    }

    [Fact]
    public void A_heat_sink_dumps_heat_and_can_draw_off_missiles()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 1, PlayerDestroyers: 0, RivalFrigates: 0, RivalDestroyers: 1, DistanceMm: 30));
        var w = sim.World;
        var mine = w.Ships.First(s => s.Side == Side.Player);
        var fleet = w.FleetOf(mine)!;
        var drawn = 0;
        for (var trial = 0; trial < 40; trial++)
        {
            mine.HeatSinks = 1;
            mine.Heat = 50;
            w.Missiles.Clear();
            for (var i = 0; i < 4; i++)
                w.Missiles.Add(new Missile { Id = w.NextId++, Side = Side.Rival, TargetShipId = mine.Id, Position = mine.Position + new Vec2(5, 0), Brain = 1, ExpiresHour = w.Hour + 1 });
            Heat.EjectSink(sim, fleet, mine, "test");
            Assert.Equal(10, mine.Heat, 6);
            Assert.Equal(0, mine.HeatSinks);
            drawn += 4 - w.Missiles.Count;
        }
        Assert.InRange(drawn, 30, 90); // about a third of 160
        w.Missiles.Clear();
    }

    [Fact]
    public void Shields_soak_missile_damage_before_armour_and_heat_the_ship()
    {
        var sim = Sandbox(new SandboxSetup());
        var w = sim.World;
        var ship = w.Ships.First(s => s.Side == Side.Player && s.ClassId == "missile-destroyer");
        var battle = new Battle();
        var through = Heat.Absorb(ship, 50, missile: true, battle);
        Assert.Equal(10, through, 6);
        Assert.Equal(0, ship.Shield, 6);
        Assert.True(ship.Heat > 0);
        Assert.Equal(40, battle.For(Side.Player).ShieldSoaked, 6);
        ship.Shield = 40;
        Assert.Equal(5, Heat.Absorb(ship, 10, missile: false, battle), 6); // slugs: half soaked
    }

    [Fact]
    public void Firing_rules_pick_largest_weakest_and_split_targets()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 2, PlayerDestroyers: 0, RivalFrigates: 2, RivalDestroyers: 1, DistanceMm: 10));
        var w = sim.World;
        sim.Step();
        var fleet = FleetOf(sim, Side.Player);
        var shooters = w.ShipsOf(fleet).ToList();
        var enemies = w.Ships.Where(s => s.Side == Side.Rival).ToList();
        enemies.First(s => s.ClassId == "gun-frigate").Armour = 5;
        fleet.FireAt = FireAt.Largest;
        Assert.Equal("missile-destroyer", FireControl.Pick(sim, shooters[0], fleet, 100)!.ClassId);
        fleet.FireAt = FireAt.Weakest;
        Assert.Equal(5, FireControl.Pick(sim, shooters[0], fleet, 100)!.Armour);
        fleet.FireAt = FireAt.Split;
        Assert.NotEqual(FireControl.Pick(sim, shooters[0], fleet, 100)!.Id, FireControl.Pick(sim, shooters[1], fleet, 100)!.Id);
    }

    [Fact]
    public void Long_range_railguns_never_hit_a_dodging_fleet_but_make_it_burn()
    {
        foreach (var dodge in new[] { DodgeMode.Sidestep, DodgeMode.Off })
        {
            // 65 Mm: past railgun range, but inside what the frigates' sensors make of a lone ship running cold.
            var sim = Sandbox(new SandboxSetup(PlayerFrigates: 3, PlayerDestroyers: 0, RivalFrigates: 1, RivalDestroyers: 0,
                PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 65));
            var w = sim.World;
            FleetOf(sim, Side.Player).LongShots = true;
            var rival = FleetOf(sim, Side.Rival);
            rival.Dodge = dodge;
            var fuel = w.ShipsOf(rival).Sum(s => s.Supplies);
            for (var i = 0; i < 360 * 3; i++) { rival.Dodge = dodge; sim.Step(); }
            var b = w.Battles[0].For(Side.Player);
            Assert.True(b.RailShots > 20, $"only {b.RailShots} long shots");
            if (dodge == DodgeMode.Sidestep)
            {
                Assert.Equal(0, b.RailHits);
                Assert.True(w.ShipsOf(rival).Sum(s => s.Supplies) < fuel - 1, "dodging cost no fuel");
            }
            else Assert.True(b.RailHits > 0, "a ship holding course was never hit");
        }
    }

    [Fact]
    public void Held_missiles_wait_until_the_enemy_is_closer()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 0, PlayerDestroyers: 1, RivalFrigates: 1, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 35));
        FleetOf(sim, Side.Player).MissileLaunch = MissileLaunch.Close;
        for (var i = 0; i < 60; i++) sim.Step();
        Assert.Equal(0, sim.World.Battles[0].For(Side.Player).MissilesFired);
        FleetOf(sim, Side.Player).MissileLaunch = MissileLaunch.Mid;
        for (var i = 0; i < 60; i++) sim.Step();
        Assert.True(sim.World.Battles[0].For(Side.Player).MissilesFired > 0);
    }

    [Fact]
    public void Dropped_missiles_drift_dark_then_go_for_a_ship_that_passes()
    {
        var sim = Sandbox(new SandboxSetup(PlayerFrigates: 0, PlayerDestroyers: 1, RivalFrigates: 1, RivalDestroyers: 0,
            PlayerStance: Stance.Hold, RivalStance: Stance.Hold, DistanceMm: 3000));
        var w = sim.World;
        var mine = FleetOf(sim, Side.Player);
        sim.Issue(new DropMissilesCommand(mine.Id, 2));
        sim.Step();
        Assert.Equal(4, w.Dropped.Count);
        Assert.Empty(w.Missiles);
        // The rival flies past the drop point after the timer.
        var rival = FleetOf(sim, Side.Rival);
        var drop = w.Dropped[0].Position;
        rival.Position = drop + new Vec2(30, 0);
        for (var i = 0; i < 2000 && w.Dropped.Count > 0; i++) sim.Step();
        Assert.True(w.Hour >= 2);
        Assert.True(w.Missiles.Count > 0 || w.Battles.Any(b => b.For(Side.Player).MissileHits + b.For(Side.Player).ShotDown + b.For(Side.Player).Dodged > 0));
    }
}
