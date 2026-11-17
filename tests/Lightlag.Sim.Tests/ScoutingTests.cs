using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The start scout and the cautious Scout role: survey first, look at enemy worlds, run from armed contacts.</summary>
public class ScoutingTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions { Budget = false, Events = EventRate.Off });
        sim.World.Minds.Clear();
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static Fleet ScoutOf(Simulation sim, Side side) => sim.World.Fleets.Single(f => f.Side == side && Scouting.ScoutOnly(sim.World, f));

    [Fact]
    public void Each_side_starts_with_an_unarmed_scout_on_the_Scout_role()
    {
        var sim = NewGame();
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var scout = ScoutOf(sim, side);
            Assert.Equal(FleetRole.Scout, scout.Role);
            Assert.Equal(Stance.Avoid, scout.Stance);
            Assert.Equal(OrbitHeight.High, scout.OrbitHeight);
            var k = GameData.ShipClass(Scouting.ScoutClass);
            Assert.Equal(0, k.Railguns + k.Missiles);
        }
    }

    [Fact]
    public void The_scout_surveys_worlds_by_itself()
    {
        var sim = NewGame();
        var w = sim.World;
        var before = w.Bodies.Count(b => Scouting.WantsSurvey(b, Side.Player));
        Assert.True(before > 0);
        sim.StepMany(30 * GameTime.TicksPerDay);
        var after = w.Bodies.Count(b => Scouting.WantsSurvey(b, Side.Player));
        Assert.True(after < before, $"{before} -> {after} still to survey");
        Assert.False(string.IsNullOrEmpty(Scouting.Status(w, ScoutOf(sim, Side.Player))));
    }

    [Fact]
    public void A_world_with_armed_enemies_seen_near_it_is_not_safe()
    {
        var sim = NewGame();
        var w = sim.World;
        var kesh = w.Bodies.Single(b => b.Key == "kesh");
        Assert.True(Scouting.Safe(w, Side.Player, kesh));
        w.Sightings.Add(new Sighting { SeenBy = Side.Player, Of = Side.Rival, FleetId = 999, Name = "Raiders", Ships = 2, Position = kesh.Position, Hour = w.Hour, Classes = new() { "gun-frigate", "gun-frigate" } });
        Assert.False(Scouting.Safe(w, Side.Player, kesh));
        // An unarmed sighting (another scout) is no danger.
        w.Sightings.Clear();
        w.Sightings.Add(new Sighting { SeenBy = Side.Player, Of = Side.Rival, FleetId = 999, Name = "Scout", Ships = 1, Position = kesh.Position, Hour = w.Hour, Classes = new() { Scouting.ScoutClass } });
        Assert.True(Scouting.Safe(w, Side.Player, kesh));
    }

    [Fact]
    public void An_armed_contact_close_by_sends_the_scout_home_to_lie_low()
    {
        var sim = NewGame();
        var w = sim.World;
        var scout = ScoutOf(sim, Side.Player);
        sim.StepMany(5 * GameTime.TicksPerDay); // out on its way
        Assert.NotNull(scout.MissionBodyId);
        var home = Economy.HomeOf(w, Side.Player)!;
        w.Sightings.Add(new Sighting { SeenBy = Side.Player, Of = Side.Rival, FleetId = 999, Name = "Raiders", Ships = 2, Position = scout.Position + new Vec2(1000, 0), Hour = w.Hour, Visible = true, Classes = new() { "gun-frigate" , "gun-frigate" } });
        Scouting.Work(sim, scout);
        Assert.Equal(home.Id, scout.MissionBodyId);
        Assert.True(scout.MissionUntil >= w.Hour + Scouting.LayLowHours - 1e-6);
        Assert.Contains(w.Light.Notes, n => n.FleetId == scout.Id && n.Text.Contains("turns for"));
    }
}
