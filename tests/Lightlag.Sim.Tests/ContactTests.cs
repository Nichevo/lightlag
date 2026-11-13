using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Contact (Plan 3 build 7, board 26): the language track and one-way offers.</summary>
public class ContactTests
{
    private static Simulation NewGame(string player = Races.Human, string rival = Races.Hive, string? third = Races.Lattice, bool? offers = null)
    {
        var options = GameOptions.AiVsAi() with { PlayerRace = player, RivalRace = rival, ThirdRace = third, ContactOffers = offers };
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.RemoveAll(m => m.Side == Side.Player);
        return sim;
    }

    private static void Days(Simulation sim, int days, Action? each = null)
    {
        for (var d = 0; d < days; d++)
            for (var h = 0; h < 24; h++) { each?.Invoke(); sim.Step(); }
    }

    [Fact]
    public void Listening_teaches_a_tongue_but_the_lattice_needs_a_core()
    {
        var sim = NewGame();
        var w = sim.World;
        Days(sim, 40, () =>
        {
            Intel.Of(w, Side.Player, Side.Rival).LastContactHour = w.Hour;
            Intel.Of(w, Side.Player, Side.Third).LastContactHour = w.Hour;
        });
        Assert.True(Contact.Score(w, Side.Player, Side.Rival) >= 20, $"hive tongue {Contact.Score(w, Side.Player, Side.Rival)}");
        Assert.True(Contact.Score(w, Side.Player, Side.Third) <= Contact.D.ClosedCap + 1e-9);
        Assert.NotNull(Contact.Closed(w, Side.Player, Side.Third));
        Contact.Learn(w, Side.Player, Side.Third, Contact.D.CapturePoints, "captured ship");
        Assert.True(Economy.Of(w, Side.Player).Cored[(int)Side.Third]);
        Assert.Null(Contact.Closed(w, Side.Player, Side.Third));
        Assert.True(Contact.Score(w, Side.Player, Side.Third) > Contact.D.ClosedCap);
    }

    [Fact]
    public void Offers_need_three_races_words_and_the_option()
    {
        var two = NewGame(third: null);
        Assert.False(Contact.OffersOn(two.World));
        var off = NewGame(offers: false);
        Assert.False(Contact.OffersOn(off.World));
        var sim = NewGame();
        var o = new Offer { From = Side.Player, To = Side.Rival, Gift = GiftKind.Map };
        Assert.Contains("Words", Contact.CanSend(sim.World, o));
        Economy.Of(sim.World, Side.Player).Language[(int)Side.Rival] = 50;
        Assert.Null(Contact.CanSend(sim.World, o));
    }

    [Fact]
    public void A_gift_lands_with_the_message_and_a_broadcast_is_overheard()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Language[(int)Side.Rival] = 50;
        Economy.Of(w, Side.Third).Language[(int)Side.Player] = 50;
        var mine = Economy.ShipyardOf(w, Side.Player)!;
        var theirs = Economy.ShipyardOf(w, Side.Rival)!;
        mine.FoodStock = 500;
        var food = new Offer { From = Side.Player, To = Side.Rival, Gift = GiftKind.Food, Amount = 150, Ask = AskKind.Attack, AskSide = Side.Third, Broadcast = true };
        Assert.Null(Contact.CanSend(w, food));
        Contact.Send(sim, food);
        var o = Assert.Single(w.Offers);
        Assert.True(o.Known[(int)Side.Third], "the Lattice reads human and heard the broadcast");
        Assert.Equal(350, mine.FoodStock, 6);
        Assert.Equal(w.Hour + Contact.D.BroadcastHours, o.LandHour, 6);
        o.LandHour = w.Hour;
        var before = theirs.FoodStock;
        Contact.Update(sim);
        Assert.True(o.Landed);
        Assert.NotNull(o.Taken);
        Assert.Equal(before + 150, theirs.FoodStock, 6);
        if (o.Taken == true) Assert.Equal(Side.Third, Mind.Of(w, Side.Rival)!.AskedTarget);
        // One offer per rival every 30 days.
        Assert.Contains("days", Contact.CanSend(w, new Offer { From = Side.Player, To = Side.Rival, Gift = GiftKind.Map }));
    }

    [Fact]
    public void The_hive_never_turns_on_the_hive()
    {
        var sim = NewGame(player: Races.Human, rival: Races.Hive, third: Races.Hive);
        var w = sim.World;
        var o = new Offer { From = Side.Player, To = Side.Rival, Gift = GiftKind.Food, Amount = 1000, Ask = AskKind.Attack, AskSide = Side.Third };
        Assert.Equal(0, Contact.Odds(w, o));
        o.Ask = AskKind.None;
        Assert.Equal(1, Contact.Odds(w, o));
    }

    [Fact]
    public void An_idea_gift_is_learned_on_landing_and_a_drone_takes_days()
    {
        var sim = NewGame();
        var w = sim.World;
        Economy.Of(w, Side.Player).Language[(int)Side.Third] = 80;
        var idea = Contact.GiftIdeas(w, Side.Player, Side.Third).FirstOrDefault();
        if (idea == null)
        {
            idea = GameData.Economy.Research.First(r => !r.Later && !Tech.Has(w, Side.Third, r.Id)).Id;
            Economy.Of(w, Side.Player).Researched.Add(idea);
        }
        sim.Issue(new SendOfferCommand(Side.Player, Side.Third, GiftKind.Idea, idea, 0, AskKind.None, null, null, Broadcast: false));
        Days(sim, 5);
        Assert.False(Tech.Has(w, Side.Third, idea));
        Days(sim, 5);
        Assert.True(Tech.Has(w, Side.Third, idea));
        Assert.True(w.Offers.Single().Taken);
    }

    [Fact]
    public void Rival_minds_send_offers_and_saves_keep_them()
    {
        var sim = NewGame();
        var w = sim.World;
        foreach (var s in w.Sides) for (var i = 0; i < s.Language.Length; i++) s.Language[i] = 60;
        Days(sim, 90);
        Assert.True(w.Offers.Any(o => o.From != Side.Player), string.Join("; ", w.Minds.Select(m => $"{m.Side} target {m.Target} lang [{string.Join(",", Economy.Of(w, m.Side).Language)}] "
            + string.Join("/", Sides.Enemies(w, m.Side).Select(t => Contact.CanSend(w, new Offer { From = m.Side, To = t, Gift = GiftKind.Map }))))));
        Assert.Equal(0, sim.RuleViolations);
        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        Assert.Equal(sim.StateHash(), loaded.StateHash());
    }
}
