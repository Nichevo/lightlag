namespace Lightlag.Sim.Core;

/// <summary>A choice beside a Needs you item's act button: its label and the order it gives.</summary>
public sealed record NeedChoice(string Label, GameCommand Command, string? Tip = null);

/// <summary>
/// What the Needs you tray and the one-at-a-time cards show for an item beyond its text: when it bites
/// ("hungry in 3 d", "Auto picks in 5 h", "for 2 d"), what happens if you do nothing, and the other choices
/// (the sensible alternative first). Written once per kind of item, not per planet. Reads the world only.
/// </summary>
public static class NeedKinds
{
    /// <summary>The kind of an item, from its key: "hungry" for "hungry:12"; "chain:short" for the supply chain's keys.</summary>
    public static string KindOf(string key)
    {
        var parts = key.Split(':');
        return parts.Length > 1 && parts[0] == "chain" ? $"chain:{parts[1]}" : parts[0];
    }

    /// <summary>A short name for a kind, for the "never for this kind" lists.</summary>
    public static string KindName(string kind) => kind switch
    {
        "hungry" => "Hungry planets",
        "abandoned" => "Abandoned planets",
        "grid" => "Planets short of power",
        "blockaded" => "Blockaded planets",
        "enemy-near" => "Enemies near a planet",
        "raiders" => "Raiders on a supply route",
        "waiting" => "Orders waiting",
        "piece-waiting" => "Buildings waiting",
        "idle-team" => "Idle research teams",
        "spark" => "Sparked ideas",
        "lost" => "Rebuild a lost warship",
        "chain:short" => "Supply: planets short",
        "chain:risk" => "Supply: risky routes",
        "chain:soon" => "Supply: fleets running low",
        "chain:siege" => "Supply: sieges",
        "chain:full" => "Supply: full stores",
        "chain:idle" => "Supply: idle stock",
        "build" => "Building suggestions",
        "topup" => "Worker top-ups",
        "run" or "run-stop" => "Standing transport runs",
        "ship" => "Freighter loads",
        "settle" => "Settling suggestions",
        "research" => "Research suggestions",
        "low" => "Fleets low on supplies",
        "supply" => "Tanker runs",
        "yard" => "Support ship builds",
        "munitions" => "Munitions grades",
        "contact" => "Lost contact",
        "refit" => "Refits",
        "rescue" => "Rescues",
        "rule" => "Pull-back rules",
        "crew-flag" or "crew-spread" => "Crew moves",
        "design-old" or "design-role" or "design-std" => "Design updates",
        "event" => "Event calls",
        "after-home" => "After battle: the way home",
        "board-call" => "Boarding calls",
        "ground-call" => "Invasion calls",
        "hurt" => "Damaged fleets far from a yard",
        _ when kind.StartsWith("learn-", StringComparison.Ordinal) => "Automation learning",
        _ => kind,
    };

    /// <summary>What happens if you do nothing, written once per kind of item.</summary>
    public static string Why(string kind) => kind switch
    {
        "hungry" => "People put up with hunger for a few days, then start leaving for your nearest fed world; after 20 days the planet is abandoned.",
        "abandoned" => "The planet makes nothing and counts for nothing until settlers land again.",
        "grid" => "The grid keeps shedding its lowest buildings, so they make nothing until power is found.",
        "blockaded" => "The planet lives off its own farms and stores, makes no Industry, and grows unhappy; in the end it may give itself up.",
        "enemy-near" => "The enemy can blockade, bomb or land troops while nothing stands in its way.",
        "raiders" => "Cargo on this route can be taken; the planets it feeds go short.",
        "waiting" => "The order sits in the queue and nothing is built until what it waits for turns up.",
        "piece-waiting" => "The building stays unfinished and its land and workers do nothing.",
        "idle-team" => "Its scientists sit idle and research falls behind.",
        "spark" => "Nothing: a sparked idea stays cheaper for a while, then the spark fades.",
        "lost" => "Your fleet stays one warship short.",
        "chain:short" => "The planet runs out and whatever uses it stops.",
        "chain:risk" => "Cargo runs through danger and can be lost.",
        "chain:soon" => "The fleet runs dry: hungry crews strain, empty tanks can't burn, empty racks can't fire.",
        "chain:siege" => "The siege eats its own supplies and gives up when they run out.",
        "chain:full" => "Stores are full, so the extra is wasted.",
        "chain:idle" => "Goods sit unused while others go short.",
        "build" => "Automation is only advising here, so nothing is built until you say.",
        "topup" => "Buildings run below full output for lack of workers.",
        "run" or "run-stop" => "The planet keeps going short, or the run keeps going when it isn't needed.",
        "ship" => "The goods stay where they are and the planet that wants them goes short.",
        "settle" => "The world stays empty, or someone else takes it.",
        "research" => "The team waits for an idea.",
        "low" => "The fleet runs dry: hungry crews strain, empty tanks can't burn, empty racks can't fire.",
        "supply" => "The fleet runs dry unless a tanker goes out.",
        "yard" => "Supply runs and cargo wait for a free ship.",
        "munitions" => "Your missiles and slugs are worse against what the enemy fields.",
        "contact" => "The enemy fleet stays lost and may turn up somewhere you don't expect.",
        "refit" => "The fleet keeps flying older fits.",
        "rescue" => "Crews in the pods are lost when their air runs out.",
        "rule" => "The fleet keeps its present pull-back rule.",
        "crew-flag" or "crew-spread" => "Veteran crews stay where they are.",
        "design-old" or "design-role" or "design-std" => "New ships keep being built to the older design.",
        "event" => "Auto makes its pick when the time runs out.",
        "after-home" => "Auto picks the way home when the time runs out.",
        "board-call" or "ground-call" => "Auto makes the call when the time runs out.",
        "hurt" => "The damaged ships stay out, and a crippled one can be finished off.",
        _ when kind.StartsWith("learn-", StringComparison.Ordinal) => "Automation keeps working as it does now.",
        _ => "It waits here until it is dealt with or goes away.",
    };

    private static string Span(double hours) =>
        hours < 1 ? "under an hour" : hours < 48 ? FormattableString.Invariant($"{hours:0} h") : FormattableString.Invariant($"{hours / 24:0.#} d");

    private static int? IdAt(string key, int index)
    {
        var parts = key.Split(':');
        return parts.Length > index && int.TryParse(parts[index], out var id) ? id : null;
    }

    /// <summary>When it bites: the clock that decides whether you care. Falls back to how long it has waited.</summary>
    public static string Bites(World w, Side side, NeedItem n)
    {
        var kind = KindOf(n.Key);
        var body = n.BodyId is int b ? w.FindBody(b) : null;
        var fleet = n.FleetId is int f ? w.FindFleet(f) : null;
        switch (kind)
        {
            case "hungry" when body != null:
            {
                var days = body.HungryHours / 24;
                var start = Planets.D.LeaveStartHungryDays;
                return days < start ? $"people leave in {Span((start - days) * 24)}" : $"all gone in {Span(Math.Max(0, Planets.D.LeaveAfterHungryDays - days) * 24)}";
            }
            case "blockaded" when body != null && Colonies.HoldsOutDays(body) is double hold:
                return $"holds out ~{Span(hold * 24)}";
            case "low" or "supply" or "chain:soon" when fleet != null && SupplyChain.FoodDays(w, fleet) is double food:
                return $"food for {Span(food * 24)}";
            case "event" when IdAt(n.Key, 1) is int eid && w.Events.Find(e => e.Id == eid) is { } ev:
                return $"Auto picks in {Span(Math.Max(0, ev.Call.DueHour - w.Hour))}";
            case "board-call" when CallOf(w, side, n.Key) is { } bc:
                return $"Auto picks in {Span(Math.Max(0, bc.Call.DueHour - w.Hour))}";
            case "ground-call" when CallOf(w, side, n.Key) is { } gc:
                return $"Auto picks in {Span(Math.Max(0, gc.Call.DueHour - w.Hour))}";
            case "chain:siege" when body != null && Ground.DaysLeft(w, body) is double left:
                return $"fighting over in ~{Span(left * 24)}";
            case "idle-team" or "piece-waiting" or "waiting" or "grid" or "abandoned":
                return $"for {Span(w.Hour - n.Hour)}";
        }
        var age = w.Hour - n.Hour;
        return age < 1 ? "new" : $"for {Span(age)}";
    }

    /// <summary>A boarding or invasion call by its Needs you key, with the order that picks each option.</summary>
    private static (WarCall Call, Func<int, GameCommand> Pick)? CallOf(World w, Side side, string key)
    {
        if (IdAt(key, 1) is not int id) return null;
        if (key.StartsWith("board-call:", StringComparison.Ordinal))
            foreach (var b in w.Boardings.Where(b => b.Side == side))
                if (b.Calls.Find(c => c.Id == id) is { } c) return (c, i => new BoardingCallCommand(b.Id, c.Id, i));
        if (key.StartsWith("ground-call:", StringComparison.Ordinal))
            foreach (var body in w.Bodies.Where(x => x.Front is { } fr && fr.Side == side))
                if (body.Front!.Calls.Find(c => c.Id == id) is { } c) return (c, i => new GroundCallCommand(side, body.Id, c.Id, i));
        return null;
    }

    /// <summary>
    /// The choices beside the act button, the sensible alternative first: an event's or a call's other options,
    /// the supply chain's next fix, or for a fleet in trouble, home to the yard. Empty when only the act fits.
    /// </summary>
    public static List<NeedChoice> Choices(World w, Side side, NeedItem n)
    {
        var list = new List<NeedChoice>();
        var kind = KindOf(n.Key);
        if (n.Alt != null) list.Add(new NeedChoice(n.AltLabel ?? "Other fix", n.Alt));
        if (kind == "event" && IdAt(n.Key, 1) is int eid && w.Events.Find(e => e.Id == eid) is { } ev && ev.Call.Open)
        {
            for (var i = 0; i < ev.Call.Options.Count; i++)
                if (i != ev.Call.AutoPick)
                    list.Add(new NeedChoice(ev.Call.Options[i], new PickEventCommand(side, ev.Id, i), i < ev.Call.Effects.Count ? ev.Call.Effects[i] : null));
        }
        else if (CallOf(w, side, n.Key) is var (call, pick) && call.Open)
        {
            for (var i = 0; i < call.Options.Count; i++)
                if (i != call.AutoPick)
                    list.Add(new NeedChoice(call.Options[i], pick(i), i < call.Effects.Count ? call.Effects[i] : null));
        }
        else if (kind is "low" or "supply" or "chain:soon" or "contact" or "hurt" or "rule" or "refit"
                 && n.FleetId is int fid && w.FindFleet(fid) is { } fleet && fleet.Side == side
                 && Economy.ShipyardOf(w, side) is { } yard && fleet.OrbitBodyId != yard.Id && fleet.Flight?.TargetBodyId != yard.Id)
        {
            if (kind == "hurt") list.Add(new NeedChoice($"Home to {yard.Name}", Home(w, fleet, yard), "Fly home to the yard without asking for a refit"));
            else list.Add(new NeedChoice($"Send {fleet.Name} home", Home(w, fleet, yard), $"Fly back to {yard.Name}: supplies, repairs and rearming there"));
        }
        // Never offer the act itself twice.
        return list.Where(c => n.Act == null || c.Command != n.Act).ToList();
    }

    private static GameCommand Home(World w, Fleet f, Body yard) =>
        new PlanFlightCommand(f.Id, new FlightRequest(yard.Id, yard.Position, 0.8, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick));
}
