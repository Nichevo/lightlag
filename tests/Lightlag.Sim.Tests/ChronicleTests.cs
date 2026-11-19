using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Plan 4 build 2: the game's story for the after-game screen (replay frames, turning points, the rival's mind by day).</summary>
public class ChronicleTests
{
    private static Simulation NewGame()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal) with { Budget = false });
        Starts.Classic(sim);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    [Fact]
    public void Frames_hold_every_fleet_where_it_really_was_twice_a_day()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.True(w.Chronicle.Started);
        Days(sim, 10);
        var c = w.Chronicle;
        Assert.InRange(c.Frames.Count, 20, 22);
        Assert.All(c.Frames.Zip(c.Frames.Skip(1)), p => Assert.True(p.Second.Hour > p.First.Hour));
        Assert.All(c.Frames, f => Assert.Equal(0, f.Hour % Chronicle.FrameHours));

        // The last frame is now: every fleet with ships, at its real position, rival fleets included (no fog).
        var last = c.Frames[^1];
        Assert.Equal(w.Hour, last.Hour);
        var marks = Chronicle.FleetsIn(last);
        Assert.Equal(w.Fleets.Count(f => f.ShipIds.Count > 0), marks.Count);
        Assert.Contains(marks, m => m.Side == Side.Rival);
        foreach (var m in marks)
        {
            var f = w.FindFleet(m.Id)!;
            Assert.Equal(f.Side, m.Side);
            Assert.Equal(f.ShipIds.Count, m.Ships);
            Assert.True((f.Position - m.Position).Length <= Chronicle.PositionUnitMm);
        }
        // Holders: both homes are held from the start.
        var owners = Chronicle.OwnersIn(c.Frames[0]);
        Assert.Contains(Side.Player, owners.Values);
        Assert.Contains(Side.Rival, owners.Values);
    }

    [Fact]
    public void Beliefs_are_what_each_command_knew_not_where_the_fleets_were()
    {
        var sim = NewGame();
        var w = sim.World;
        Assert.Empty(Chronicle.FleetsIn(new ChronicleFrame(0, "", "", "")));
        Days(sim, 15);
        var known = Sensors.Known(w, seat: true).Where(s => !s.Own && s.SeenBy != s.Of && w.Hour - s.Hour <= Chronicle.BeliefMaxAgeHours).ToList();
        var beliefs = Chronicle.BeliefsIn(w.Chronicle.Frames[^1]);
        Assert.Equal(known.Count, beliefs.Count);
        foreach (var b in beliefs)
        {
            Assert.NotEqual(b.By, b.Of);
            var s = known.Single(k => k.SeenBy == b.By && k.FleetId == b.FleetId);
            Assert.Equal(s.Visible, b.Seen);
            Assert.Equal(s.Ships, b.Ships);
            // Where the command expected it to be now: its guess, which can be far from the truth when the report is old.
            var guess = Sensors.Predict(w, b.By, b.FleetId, w.Hour, seat: true) ?? s.Position;
            Assert.True((guess - b.Position).Length <= Chronicle.PositionUnitMm);
        }
    }

    [Fact]
    public void The_rivals_goal_and_reasons_are_kept_each_day()
    {
        var sim = NewGame();
        var w = sim.World;
        Days(sim, 12);
        var days = Chronicle.MindDaysOf(w, Side.Rival);
        Assert.Equal(12, w.Chronicle.MindDays.Count(d => d.Side == Side.Rival));
        Assert.Equal(12, days.Count);
        Assert.Equal(Enumerable.Range(1, 12), days.Select(d => d.Day));
        Assert.All(days, d => Assert.False(string.IsNullOrWhiteSpace(d.Goal)));
        Assert.All(days, d => Assert.True(d.EnemyPowerTrue > 0));
        // Today's record is the mind as it stands now.
        var mind = Mind.Of(w, Side.Rival)!;
        Assert.StartsWith(days[^1].Goal, mind.Goal);
        Assert.Equal(days[^1].Day, Chronicle.MindOn(w, Side.Rival, w.Day)!.Day);
        Assert.Equal(5, Chronicle.MindOn(w, Side.Rival, 5)!.Day);
        // Stored days leave out what did not change; filled in, every day has its goal.
        Assert.Contains(w.Chronicle.MindDays, d => d.Side == Side.Rival && d.Day > 1 && d.Goal == null);
        // Each of its thoughts lands in a day's record.
        var thoughts = days.SelectMany(d => d.Thoughts!).ToList();
        Assert.All(mind.Thoughts.Where(t => t.Hour > 0 && t.Hour <= w.Hour), t => Assert.Contains(t.Text, thoughts));
        Assert.True(thoughts.Count <= days.Count * 40);
    }

    [Fact]
    public void Planets_changing_hands_and_the_end_are_turning_points()
    {
        var sim = NewGame();
        var w = sim.World;
        Days(sim, 1);
        var free = Economy.Claimable(w).First(b => b.Owner == null);
        free.Owner = Side.Player;
        sim.Step();
        var claimed = Assert.Single(w.Chronicle.Moments, m => m.BodyId == free.Id);
        Assert.Equal("planet", claimed.Kind);
        Assert.Equal(Side.Player, claimed.Side);
        Assert.Contains($"You claimed {free.Name}", claimed.Text);

        free.Owner = Side.Rival;
        sim.Step();
        Assert.Contains(w.Chronicle.Moments, m => m.Text == $"The rival took {free.Name} from you" && m.Side == Side.Rival);

        sim.Issue(new ConcedeCommand(Side.Player));
        sim.Step();
        Assert.Equal(Side.Rival, w.Winner);
        var end = Assert.Single(w.Chronicle.Moments, m => m.Kind == "end");
        Assert.Equal(Side.Rival, end.Side);
        Assert.Equal(w.WinHour, w.Chronicle.Frames[^1].Hour);
        var points = Chronicle.TurningPoints(w, 2);
        Assert.Equal(2, points.Count);
        Assert.Contains(end, points);
        Assert.True(points[0].Hour <= points[1].Hour);

        // Nothing more is added once the game is over.
        var count = w.Chronicle.Moments.Count;
        sim.StepMany(48);
        Assert.Equal(count, w.Chronicle.Moments.Count);
    }

    [Fact]
    public void Saves_keep_the_story_and_old_saves_start_it_on_load()
    {
        var sim = NewGame();
        Days(sim, 3);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(sim.World.Chronicle.Frames, loaded.World.Chronicle.Frames);
        Assert.Equal(sim.World.Chronicle.MindDays.Count, loaded.World.Chronicle.MindDays.Count);

        // A save from before the record: it begins at the first hour after loading, from then on.
        var old = NewGame();
        Days(old, 2);
        old.World.Chronicle = new ChronicleState();
        old.Step();
        Assert.True(old.World.Chronicle.Started);
        Assert.Equal(old.World.Hour, old.World.Chronicle.FromHour);
        Assert.Single(old.World.Chronicle.Frames);
        Assert.Empty(old.World.Chronicle.Moments);
    }

    [Fact]
    public void The_story_replays_exactly()
    {
        var sim = NewGame();
        Days(sim, 6);
        var again = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: GameOptions.VsRival(Difficulty.Normal) with { Budget = false });
        Starts.Classic(again);
        Days(again, 6);
        Assert.Equal(Lightlag.Sim.Json.Serialize(sim.World.Chronicle), Lightlag.Sim.Json.Serialize(again.World.Chronicle));
    }
}
