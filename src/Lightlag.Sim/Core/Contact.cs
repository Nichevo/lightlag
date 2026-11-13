namespace Lightlag.Sim.Core;

// ---- Data (Data/contact.json) ---------------------------------------------------------------

public sealed class GiftValues
{
    public double Idea { get; set; } = 1;
    public double Food { get; set; } = 1;
    public double Alloys { get; set; } = 1;
    public double Map { get; set; } = 0.6;
}

public sealed class AskCosts
{
    public double None { get; set; }
    public double Attack { get; set; } = 1.4;
    public double Spare { get; set; } = 0.8;
}

public sealed class ContactData
{
    public double[] Stages { get; set; } = { 0, 20, 45, 70, 90 };
    public string[] StageNames { get; set; } = { "Noise", "Patterns", "Words", "Language", "Fluent" };
    public double ListenPerDay { get; set; } = 1.0;
    public double ListenCap { get; set; } = 75;
    public double ClosedCap { get; set; } = 19;
    public double SignalsPerDay { get; set; } = 0.3;
    public double DecoderPerDay { get; set; } = 0.6;
    public double PrisonerPoints { get; set; } = 6;
    public double CapturePoints { get; set; } = 10;
    public double CorePoints { get; set; } = 30;
    public double WreckPoints { get; set; } = 3;
    public double IntelPerStagePerDay { get; set; } = 0.15;
    public int SendStage { get; set; } = 2;
    public int OddsStage { get; set; } = 3;
    public double CooldownDays { get; set; } = 30;
    public double AskDays { get; set; } = 60;
    public double DroneDays { get; set; } = 9;
    public double BroadcastHours { get; set; } = 2;
    public double FoodGift { get; set; } = 150;
    public double AlloysGift { get; set; } = 40;
    public double AiOfferChancePerDay { get; set; } = 0.08;
    public Dictionary<string, GiftValues> Values { get; set; } = new();
    public AskCosts AskCost { get; set; } = new();
}

// ---- State ----------------------------------------------------------------------------------

public enum GiftKind { Idea, Food, Alloys, Map }
public enum AskKind { None, Attack, Spare }

/// <summary>A one-way offer: a gift, and maybe an ask. It lands after the drone's trip or the broadcast's delay.</summary>
public sealed class Offer
{
    public int Id { get; set; }
    public Side From { get; set; }
    public Side To { get; set; }
    public GiftKind Gift { get; set; }
    public string? Idea { get; set; }
    public double Amount { get; set; }
    public AskKind Ask { get; set; }
    public Side? AskSide { get; set; }
    public int? AskBodyId { get; set; }
    public bool Broadcast { get; set; }
    public double SentHour { get; set; }
    public double LandHour { get; set; }
    public bool Landed { get; set; }
    /// <summary>Taken up (null until it lands; an offer with no ask is always taken).</summary>
    public bool? Taken { get; set; }
    /// <summary>The odds it had of being taken up when sent.</summary>
    public double Odds { get; set; }
    /// <summary>Sides that know of it: the two ends, and anyone who heard a broadcast in a tongue they read.</summary>
    public bool[] Known { get; set; } = new bool[Sides.Max];
}

/// <summary>
/// Contact (board 26). Each side learns each rival's language on a track (Noise, Patterns, Words, Language,
/// Fluent) from listening while in contact, Signals research, prisoners, captures and stripped wrecks. The
/// Lattice speaks in machine code: listening alone never gets past Noise without a captured core or Decode
/// alien signals. Every stage adds intel on that rival. With three or more races (and the option on) a side
/// at Words can send one-way offers: a gift (an idea, food, alloys or its survey map) and an ask (go after a
/// third side for 60 days, leave a planet alone for 60 days, or nothing), by messenger drone (private, 9
/// days) or broadcast (2 hours, heard by anyone who reads the sender's tongue). One offer per rival every 30
/// days; the gift goes when the message lands, taken up or not. Rival minds send the same offers.
/// </summary>
public static class Contact
{
    private static readonly Lazy<ContactData> _data = new(() => Json.Deserialize<ContactData>(GameData.ReadText("contact.json")));
    public static ContactData D => _data.Value;

    public const string Signals = "signals-analysis", Decoder = "decode-signals";

    /// <summary>The side's language score on a rival's tongue (0 to 100).</summary>
    public static double Score(World w, Side side, Side about) => Economy.Of(w, side).Language[(int)about];

    public static int Stage(double score)
    {
        var s = 0;
        for (var i = 1; i < D.Stages.Length; i++) if (score >= D.Stages[i]) s = i;
        return s;
    }

    public static int Stage(World w, Side side, Side about) => Stage(Score(w, side, about));

    public static string StageName(int stage) => D.StageNames[Math.Clamp(stage, 0, D.StageNames.Length - 1)];

    /// <summary>Offers can be sent at all in this game (three races and the option on).</summary>
    public static bool OffersOn(World w) => w.Rules.ContactOffers && Sides.Three(w);

    /// <summary>How far listening and wrecks can take the side on this tongue: all the way, or Noise for a closed tongue not yet cracked.</summary>
    public static double Cap(World w, Side side, Side about) =>
        Races.Of(w, about).ListenOnly || Economy.Of(w, side).Cored[(int)about] || Tech.Has(w, side, Decoder) ? 100
            : D.ClosedCap;

    /// <summary>Why this tongue stops at Noise, or null.</summary>
    public static string? Closed(World w, Side side, Side about) =>
        Cap(w, side, about) < 100 ? $"{Races.Of(w, about).Name} talk in machine code: capture one of their ships (its core) or research {GameData.Research(Decoder).Name}" : null;

    /// <summary>Adds language points on a rival's tongue (prisoners, captures, wrecks). A captured machine core cracks a closed tongue.</summary>
    public static void Learn(World w, Side side, Side about, double points, string how)
    {
        if (side == about || (int)about >= w.Sides.Length) return;
        var me = Economy.Of(w, side);
        if (how == "captured ship" && !Races.Of(w, about).ListenOnly && !me.Cored[(int)about])
        {
            me.Cored[(int)about] = true;
            points += D.CorePoints;
            Intel.Note(w, side, IntelSource.Capture, $"A captured core cracks the {Races.Of(w, about).Name} machine tongue", about: about);
        }
        Raise(w, side, about, points);
    }

    private static void Raise(World w, Side side, Side about, double points)
    {
        var me = Economy.Of(w, side);
        var i = (int)about;
        var before = Stage(me.Language[i]);
        me.Language[i] = Math.Min(Cap(w, side, about), me.Language[i] + Math.Max(0, points));
        var after = Stage(me.Language[i]);
        if (after > before)
            Intel.Note(w, side, IntelSource.Sensors, $"Their tongue: {StageName(after)}", about: about);
    }

    /// <summary>Daily: listening, Signals research, and the intel each stage brings. Then landing offers and rival minds' offers.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var o in w.Offers.Where(o => !o.Landed && w.Hour >= o.LandHour).ToList()) Land(sim, o);
        if (w.Tick % GameTime.TicksPerDay != 0) return;
        foreach (var side in Sides.InPlay(w))
            foreach (var about in Sides.Enemies(w, side))
            {
                var file = Intel.Of(w, side, about);
                var points = 0.0;
                if (w.Hour - file.LastContactHour < 48) points += D.ListenPerDay * (Score(w, side, about) < D.ListenCap ? 1 : 0);
                if (Tech.Has(w, side, Signals)) points += D.SignalsPerDay;
                if (Tech.Has(w, side, Decoder)) points += D.DecoderPerDay;
                Raise(w, side, about, points);
                file.Points += Stage(w, side, about) * D.IntelPerStagePerDay;
            }
        if (OffersOn(w)) MindOffers(sim);
    }

    // ---- Offers ---------------------------------------------------------------------------------

    public static double LastOfferHour(World w, Side from, Side to) =>
        w.Offers.Where(o => o.From == from && o.To == to).Select(o => o.SentHour).DefaultIfEmpty(-1e9).Max();

    /// <summary>The ideas the side could give: ones it has that the other lacks (no later ones).</summary>
    public static IEnumerable<string> GiftIdeas(World w, Side from, Side to) =>
        Economy.Of(w, from).Researched.Where(id => !Tech.Has(w, to, id) && GameData.Economy.Research.Any(r => r.Id == id && !r.Later)).OrderBy(id => id);

    public static double DefaultAmount(GiftKind g) => g switch { GiftKind.Food => D.FoodGift, GiftKind.Alloys => D.AlloysGift, _ => 0 };

    /// <summary>Why this offer can't be sent, or null.</summary>
    public static string? CanSend(World w, Offer o)
    {
        if (!OffersOn(w)) return w.Rules.ContactOffers ? "Offers need three races in the game" : "Contact offers are off in this game";
        if (o.From == o.To || !Sides.InPlay(w).Contains(o.To) || !Sides.InPlay(w).Contains(o.From)) return "No such side";
        if (Sides.Out(w, o.To)) return $"{Races.Of(w, o.To).Name} is out of the game";
        if (Stage(w, o.From, o.To) < D.SendStage) return $"Your grasp of their tongue is {StageName(Stage(w, o.From, o.To))}: offers need {StageName(D.SendStage)}";
        var since = w.Hour - LastOfferHour(w, o.From, o.To);
        if (since < D.CooldownDays * 24) return $"One offer every {D.CooldownDays:0} days: the next in {(D.CooldownDays * 24 - since) / 24:0.#} days";
        var yard = Economy.ShipyardOf(w, o.From);
        switch (o.Gift)
        {
            case GiftKind.Idea when o.Idea == null || !GiftIdeas(w, o.From, o.To).Contains(o.Idea): return "Pick an idea you have and they lack";
            case GiftKind.Food when yard == null || yard.FoodStock < o.Amount: return $"Needs {o.Amount:0} food at the shipyard";
            case GiftKind.Alloys when yard == null || Colonies.Held(yard, "alloys") < o.Amount: return $"Needs {o.Amount:0} alloys at the shipyard";
            case GiftKind.Food or GiftKind.Alloys when o.Amount <= 0: return "Give something";
        }
        switch (o.Ask)
        {
            case AskKind.Attack when o.AskSide is not Side t || t == o.To || t == o.From || !Sides.InPlay(w).Contains(t) || Sides.Out(w, t):
                return "Pick a third side for them to go after";
            case AskKind.Spare when o.AskBodyId is not int id || w.FindBody(id)?.Owner != o.From:
                return "Pick one of your planets for them to leave alone";
        }
        return null;
    }

    private static GiftValues ValuesOf(World w, Side side) =>
        D.Values.TryGetValue(Races.Of(w, side).Id, out var v) ? v : new GiftValues();

    /// <summary>What the gift is worth to the side it goes to (1: about an idea's worth).</summary>
    public static double GiftValue(World w, Offer o)
    {
        var v = ValuesOf(w, o.To);
        return o.Gift switch
        {
            GiftKind.Idea => v.Idea,
            GiftKind.Food => v.Food * o.Amount / Math.Max(1, D.FoodGift),
            GiftKind.Alloys => v.Alloys * o.Amount / Math.Max(1, D.AlloysGift),
            _ => v.Map,
        };
    }

    /// <summary>
    /// The odds a rival mind takes the ask up: the gift's worth to it against what the ask costs it. Going
    /// after a side it already targets costs little; the Hive never turns on the Hive; a grudge against the
    /// sender (it is the one it goes after) halves the odds. The player is never bound by an ask.
    /// </summary>
    public static double Odds(World w, Offer o)
    {
        if (o.Ask == AskKind.None) return 1;
        var m = Mind.Of(w, o.To);
        if (m == null) return 0;
        var cost = o.Ask == AskKind.Attack ? D.AskCost.Attack : D.AskCost.Spare;
        if (o.Ask == AskKind.Attack && o.AskSide is Side t)
        {
            if (Races.Of(w, t).Id == Races.Hive && Races.Of(w, o.To).Id == Races.Hive) return 0;
            if (m.Target == t) cost *= 0.3;
            if (Races.Of(w, t).Id == Races.Of(w, o.To).Id) cost *= 2;
        }
        var value = GiftValue(w, o) * (m.Personality == Personality.Trader ? 1.4 : m.Personality == Personality.Opportunist ? 1.2 : 1);
        var odds = value / (value + cost);
        if (m.Target == o.From) odds *= 0.5;
        return Math.Clamp(odds, 0.02, 0.95);
    }

    /// <summary>Sends an offer: the gift leaves the sender's stores now and lands with the message.</summary>
    public static void Send(Simulation sim, Offer o)
    {
        var w = sim.World;
        var yard = Economy.ShipyardOf(w, o.From)!;
        if (o.Gift == GiftKind.Food) yard.FoodStock -= o.Amount;
        if (o.Gift == GiftKind.Alloys) yard.Stock["alloys"] = Colonies.Held(yard, "alloys") - o.Amount;
        o.Id = w.NextId++;
        o.SentHour = w.Hour;
        o.LandHour = w.Hour + (o.Broadcast ? D.BroadcastHours : D.DroneDays * 24);
        o.Odds = Odds(w, o);
        o.Known = new bool[Sides.Max];
        o.Known[(int)o.From] = true;
        // A broadcast is heard by anyone who reads the sender's tongue.
        if (o.Broadcast)
            foreach (var s in Sides.InPlay(w).Where(s => s != o.From && s != o.To))
                if (Stage(w, s, o.From) >= D.SendStage)
                {
                    o.Known[(int)s] = true;
                    Intel.Note(w, s, IntelSource.Sensors, $"Heard {Races.Of(w, o.From).Name} offer {Races.Of(w, o.To).Name}: {Describe(w, o)}", 3, about: o.From);
                }
        w.Offers.Add(o);
        sim.Log.Info("contact", $"{o.From} sends an offer to {o.To}", new { from = o.From.ToString(), to = o.To.ToString(), gift = o.Gift.ToString(), o.Idea, o.Amount, ask = o.Ask.ToString(), askSide = o.AskSide?.ToString(), o.AskBodyId, o.Broadcast, o.Odds });
    }

    private static void Land(Simulation sim, Offer o)
    {
        var w = sim.World;
        o.Landed = true;
        if (Sides.Out(w, o.To)) { o.Taken = false; return; }
        // The gift arrives whatever the answer.
        switch (o.Gift)
        {
            case GiftKind.Idea when o.Idea != null && !Tech.Has(w, o.To, o.Idea):
                Economy.Of(w, o.To).Researched.Add(o.Idea);
                Tech.OnLearn(sim, o.To, o.Idea);
                Colonies.OnResearched(sim, o.To, o.Idea);
                Munitions.OnResearched(sim, o.To);
                break;
            case GiftKind.Food when Economy.ShipyardOf(w, o.To) is Body yard:
                yard.FoodStock += o.Amount;
                break;
            case GiftKind.Alloys when Economy.ShipyardOf(w, o.To) is Body yard:
                yard.Stock["alloys"] = Colonies.Held(yard, "alloys") + o.Amount;
                break;
            case GiftKind.Map:
                foreach (var b in w.Bodies)
                    if (b.Surveyed[(int)o.From]) b.Surveyed[(int)o.To] = true;
                break;
        }
        // The other end reads it only if it has Words in the sender's tongue (a drone always reaches it, but may not be understood).
        var reads = Stage(w, o.To, o.From) >= D.SendStage;
        o.Known[(int)o.To] = true;
        var m = Mind.Of(w, o.To);
        if (o.Ask == AskKind.None || m == null) o.Taken = o.Ask == AskKind.None;
        else if (!reads) o.Taken = false;
        else
        {
            o.Taken = sim.Rng.Chance(o.Odds);
            if (o.Taken == true && o.Ask == AskKind.Attack)
            {
                m.AskedTarget = o.AskSide;
                m.AskedUntilHour = w.Hour + D.AskDays * 24;
                Mind.PickTarget(sim, m);
            }
            if (o.Taken == true && o.Ask == AskKind.Spare)
            {
                m.SpareBodyId = o.AskBodyId;
                m.SpareUntilHour = w.Hour + D.AskDays * 24;
            }
        }
        if (o.To == Side.Player || reads)
            Intel.Note(w, o.To, IntelSource.Sensors, reads ? $"Offer from {Races.Of(w, o.From).Name}: {Describe(w, o)}" : $"A message from {Races.Of(w, o.From).Name} we can't read, and a gift: {GiftText(w, o)}", 2, about: o.From);
        // The sender sees whether it was taken up by what the other side does next; intel notes the answer.
        Intel.Note(w, o.From, IntelSource.Sensors, $"Our offer to {Races.Of(w, o.To).Name} landed" + (o.Ask != AskKind.None && m != null ? o.Taken == true ? ": they seem to take it up" : ": no sign they will" : ""), about: o.To);
        sim.Log.Info("contact", $"Offer from {o.From} lands with {o.To}", new { o.Id, taken = o.Taken, reads });
    }

    public static string GiftText(World w, Offer o) => o.Gift switch
    {
        GiftKind.Idea => o.Idea != null ? GameData.Research(o.Idea).Name : "an idea",
        GiftKind.Food => $"{o.Amount:0} food",
        GiftKind.Alloys => $"{o.Amount:0} alloys",
        _ => "their survey map",
    };

    public static string AskText(World w, Offer o) => o.Ask switch
    {
        AskKind.Attack when o.AskSide is Side s => $"go after {Races.Of(w, s).Name} for {D.AskDays:0} days",
        AskKind.Spare when o.AskBodyId is int id => $"leave {w.FindBody(id)?.Name ?? "a planet"} alone for {D.AskDays:0} days",
        _ => "nothing in return",
    };

    public static string Describe(World w, Offer o) => $"gives {GiftText(w, o)}, asks {AskText(w, o)}";

    /// <summary>The offers this side knows of, newest first.</summary>
    public static IEnumerable<Offer> KnownTo(World w, Side side) => w.Offers.Where(o => o.Known[(int)side]).OrderByDescending(o => o.SentHour);

    /// <summary>A mind leaves a planet alone while it agreed to.</summary>
    public static bool Spared(World w, MindState m, Body b) => m.SpareBodyId == b.Id && w.Hour < m.SpareUntilHour;

    // ---- Rival minds' offers --------------------------------------------------------------------

    /// <summary>
    /// Now and then a rival mind with Words in another side's tongue asks it to go after the side the mind
    /// itself is after, with the cheapest gift it can spare (food unless it is short, else alloys, an idea or its map).
    /// </summary>
    private static void MindOffers(Simulation sim)
    {
        var w = sim.World;
        foreach (var m in w.Minds.Where(m => !Sides.Out(w, m.Side)).ToList())
        {
            if (m.Target is not Side target) continue;
            foreach (var to in Sides.Enemies(w, m.Side).Where(s => s != target && !Sides.Out(w, s)))
            {
                if (!sim.Rng.Chance(D.AiOfferChancePerDay)) continue;
                var yard = Economy.ShipyardOf(w, m.Side);
                if (yard == null) continue;
                var o = new Offer { From = m.Side, To = to, Ask = AskKind.Attack, AskSide = target, Broadcast = sim.Rng.Chance(0.3) };
                if (Races.Of(w, m.Side).Eats && yard.FoodStock > D.FoodGift * 3) { o.Gift = GiftKind.Food; o.Amount = D.FoodGift; }
                else if (Colonies.Held(yard, "alloys") > D.AlloysGift * 3) { o.Gift = GiftKind.Alloys; o.Amount = D.AlloysGift; }
                else if (GiftIdeas(w, m.Side, to).FirstOrDefault() is string idea) { o.Gift = GiftKind.Idea; o.Idea = idea; }
                else o.Gift = GiftKind.Map;
                if (CanSend(w, o) != null) continue;
                Send(sim, o);
            }
        }
    }
}

/// <summary>Sends a one-way offer to another side (see Contact).</summary>
public sealed record SendOfferCommand(Side Side, Side To, GiftKind Gift, string? Idea, double Amount, AskKind Ask, Side? AskSide, int? AskBodyId, bool Broadcast) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // the message leaves at once; its trip is the delay

    public override void Apply(World world, Simulation sim)
    {
        var o = new Offer { From = Side, To = To, Gift = Gift, Idea = Idea, Amount = Gift is GiftKind.Food or GiftKind.Alloys ? Amount : 0, Ask = Ask, AskSide = AskSide, AskBodyId = AskBodyId, Broadcast = Broadcast };
        if (Contact.CanSend(world, o) is string why)
        {
            sim.Log.Warn("contact", "Offer not sent", new { side = Side.ToString(), why, Source });
            return;
        }
        Contact.Send(sim, o);
    }
}
