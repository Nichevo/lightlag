using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

public class IntelTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet RivalFleet(World w) => w.Fleets.First(f => f.Side == Side.Rival);

    [Fact]
    public void A_fleet_stays_on_file_after_its_sighting_is_dropped_and_fades_with_age()
    {
        var sim = NewGame();
        var w = sim.World;
        var rival = RivalFleet(w);
        Sensors.Reveal(w, Side.Player, rival);
        Intel.Update(sim);
        var fact = Assert.Single(Intel.Of(w, Side.Player).Fleets, f => f.FleetId == rival.Id);
        Assert.Equal(rival.ShipIds.Count, fact.Ships);

        w.Sightings.RemoveAll(s => s.SeenBy == Side.Player);
        sim.StepMany(GameTime.TicksPerDay * 5);
        fact = Assert.Single(Intel.Of(w, Side.Player).Fleets, f => f.FleetId == rival.Id);
        Assert.InRange(Intel.Sure(w, fact), 0.01, 0.99);
    }

    [Fact]
    public void A_finished_battle_raises_the_level_and_records_what_the_rival_used()
    {
        var sim = NewGame();
        var w = sim.World;
        var before = Intel.Of(w, Side.Player).Points;
        var battle = new Battle { Id = 77, StartHour = w.Hour, EndHour = w.Hour };
        battle.For(Side.Player).ShipsIn = 2;
        battle.For(Side.Player).DamageDealt = 10;
        battle.For(Side.Rival).ShipsIn = 2;
        battle.For(Side.Rival).RailShots = 5;
        battle.For(Side.Rival).Withdrew = true;
        w.Battles.Add(battle);
        Intel.Update(sim);
        var file = Intel.Of(w, Side.Player);
        Assert.True(file.Points >= before + Intel.BattlePoints);
        Assert.NotNull(Intel.TechSeen(w, Side.Player, "railguns"));
        Assert.Null(Intel.TechSeen(w, Side.Player, "missiles"));
        Assert.Equal(1, file.Habits["pulls-back"]);
        // The rival learns about you by the same rules.
        Assert.True(Intel.Of(w, Side.Rival).Points >= Intel.BattlePoints);
    }

    [Fact]
    public void Notes_from_other_systems_add_points_and_tech()
    {
        var sim = NewGame();
        var w = sim.World;
        Intel.Note(w, Side.Player, IntelSource.Prisoners, "Prisoners talked", 50, "missiles");
        var file = Intel.Of(w, Side.Player);
        Assert.Equal(2, Intel.Level(file));
        Assert.Equal(IntelSource.Prisoners, Intel.TechSeen(w, Side.Player, "missiles")!.Source);
        Assert.Contains(file.Notes, n => n.Text == "Prisoners talked");
        Assert.NotEmpty(Intel.BeliefVsTruth(w, Side.Player));
    }

    [Fact]
    public void Brain_two_missiles_seen_suggest_point_defence()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Rival).Researched.Add(Tech.MissileBrain2);
        Intel.Note(w, Side.Player, IntelSource.Battle, "Missiles seen", 0, "missiles");
        Assert.Contains(Intel.Counters(w, Side.Player), c => c.Idea == Tech.PointDefence2);
    }

    [Fact]
    public void Knowledge_fades_without_contact()
    {
        var sim = NewGame();
        var w = sim.World;
        w.Sightings.Clear();
        Intel.Of(w, Side.Player).Points = 100;
        Intel.Of(w, Side.Player).LastContactHour = -1e9;
        for (var day = 0; day < 10; day++)
        {
            w.Tick += GameTime.TicksPerDay;
            w.Sightings.Clear();
            Intel.Update(sim);
        }
        Assert.InRange(Intel.Of(w, Side.Player).Points, 50, 99);
    }
}
