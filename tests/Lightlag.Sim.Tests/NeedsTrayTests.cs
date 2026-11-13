using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>The Needs you tray's sim side: kinds, when it bites, choices, snoozes and automation notes with places.</summary>
public class NeedsTrayTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory());
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        return sim;
    }

    private static SideState Player(Simulation sim) => Economy.Of(sim.World, Side.Player);
    private static void Days(Simulation sim, double days) => sim.StepMany((int)Math.Round(days * GameTime.TicksPerDay));

    [Theory]
    [InlineData("hungry:12", "hungry")]
    [InlineData("chain:short:4:food", "chain:short")]
    [InlineData("learn-auto:Research", "learn-auto")]
    [InlineData("yard:tanker", "yard")]
    public void Kinds_come_from_the_key(string key, string kind) => Assert.Equal(kind, NeedKinds.KindOf(key));

    [Fact]
    public void Every_item_has_a_why_and_a_when()
    {
        var sim = NewGame();
        Days(sim, 2);
        Assert.NotEmpty(Player(sim).Needs);
        foreach (var n in Player(sim).Needs)
        {
            Assert.False(string.IsNullOrWhiteSpace(NeedKinds.Why(NeedKinds.KindOf(n.Key))));
            Assert.False(string.IsNullOrWhiteSpace(NeedKinds.Bites(sim.World, Side.Player, n)));
            Assert.NotNull(NeedKinds.Choices(sim.World, Side.Player, n));
        }
    }

    [Fact]
    public void Snooze_until_it_changes_hides_it_while_the_text_stays_the_same()
    {
        var sim = NewGame();
        Days(sim, 0.5);
        var need = Assert.Single(Player(sim).Needs, n => n.Key.StartsWith("research:"));
        sim.Issue(new SnoozeNeedCommand(Side.Player, need.Key, NeedSnooze.UntilChanged));
        sim.ApplyPending();
        Days(sim, 0.5);
        Assert.DoesNotContain(Player(sim).Needs, n => n.Key == need.Key && n.Text == need.Text);
        Assert.True(Player(sim).SnoozedText.ContainsKey(need.Key) || Player(sim).Needs.Any(n => n.Key == need.Key && n.Text != need.Text));
    }

    [Fact]
    public void Snooze_for_an_hour_brings_it_back()
    {
        var sim = NewGame();
        Days(sim, 0.5);
        var need = Assert.Single(Player(sim).Needs, n => n.Key.StartsWith("research:"));
        sim.Issue(new SnoozeNeedCommand(Side.Player, need.Key, NeedSnooze.Hour));
        sim.ApplyPending();
        Assert.DoesNotContain(Player(sim).Needs, n => n.Key == need.Key);
        Days(sim, 0.5);
        Assert.Contains(Player(sim).Needs, n => n.Key == need.Key);
    }

    [Fact]
    public void Never_for_this_kind_mutes_it_until_shown_again()
    {
        var sim = NewGame();
        Days(sim, 0.5);
        var need = Assert.Single(Player(sim).Needs, n => n.Key.StartsWith("research:"));
        sim.Issue(new SnoozeNeedCommand(Side.Player, need.Key, NeedSnooze.NeverThisKind));
        sim.ApplyPending();
        Days(sim, 1);
        Assert.DoesNotContain(Player(sim).Needs, n => NeedKinds.KindOf(n.Key) == "research");
        Assert.Contains("research", Player(sim).MutedKinds);
        sim.Issue(new UnmuteNeedKindCommand(Side.Player, "research"));
        sim.ApplyPending();
        Days(sim, 0.5);
        Assert.Contains(Player(sim).Needs, n => NeedKinds.KindOf(n.Key) == "research");
    }

    [Fact]
    public void A_quiet_snooze_teaches_automation_nothing()
    {
        var sim = NewGame();
        Days(sim, 0.5);
        var need = Assert.Single(Player(sim).Needs, n => n.Key.StartsWith("research:"));
        sim.Issue(new SnoozeNeedCommand(Side.Player, need.Key, NeedSnooze.Hour, Quiet: true));
        sim.ApplyPending();
        Assert.False(Player(sim).Habits.ContainsKey("dismiss:Research"));
    }

    [Fact]
    public void Auto_notes_carry_the_place_and_key()
    {
        var sim = NewGame();
        sim.Issue(new SetAutoLevelCommand(Side.Player, AutoArea.Research, AutoLevel.Auto));
        Days(sim, 0.5);
        var note = Assert.Single(Player(sim).AutoNotes, n => n.Area == AutoArea.Research);
        Assert.StartsWith("research:", note.Key);
    }

    [Fact]
    public void Snoozes_and_notes_survive_a_save()
    {
        var sim = NewGame();
        Days(sim, 0.5);
        var need = Player(sim).Needs.First(n => n.Key.StartsWith("research:"));
        sim.Issue(new SnoozeNeedCommand(Side.Player, need.Key, NeedSnooze.NeverThisKind));
        sim.ApplyPending();
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Contains("research", Economy.Of(loaded.World, Side.Player).MutedKinds);
    }
}
