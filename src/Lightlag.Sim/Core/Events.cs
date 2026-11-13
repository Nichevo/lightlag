using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>Event numbers (hazards.json, "events"). See the comment there.</summary>
public sealed class EventsData
{
    public double[] PerSideDays { get; set; } = { 0, 50, 25, 12 };
    public double FirstDay { get; set; } = 12;
    public double AskHours { get; set; } = 24;
    public Dictionary<string, double> Weights { get; set; } = new();

    public double FlareWarnHours { get; set; } = 2;
    public double FlareWatchHours { get; set; } = 9;
    public double FlareHours { get; set; } = 12;
    public double FlareCrew { get; set; } = 0.2;
    public double FlareWear { get; set; } = 0.08;
    public double FlareBlind { get; set; } = 0.5;
    public double FlareShelterBlind { get; set; } = 0.2;
    public double FlareHideSignature { get; set; } = 0.5;

    public double DerelictDays { get; set; } = 4;
    public double DerelictAlloys { get; set; } = 15;
    public double DerelictElectronics { get; set; } = 8;
    public double DerelictRivalChance { get; set; } = 0.4;

    public double FaultShare { get; set; } = 0.3;
    public double FaultFastSpares { get; set; } = 20;
    public double FaultFastDays { get; set; } = 2;
    public double FaultSlowSpares { get; set; } = 6;
    public double FaultSlowDays { get; set; } = 10;
    public double FaultOffShare { get; set; } = 0.6;
    public double FaultOffDays { get; set; } = 4;
    public double FaultNoSparesDays { get; set; } = 20;

    public double RockWarnDays { get; set; } = 3;
    public double RockMappedDays { get; set; } = 6;
    public double RockPushIndustry { get; set; } = 25;
    public double RockShootChance { get; set; } = 0.6;
    public double RockShootAu { get; set; } = 0.3;
    public double RockPeopleLost { get; set; } = 0.1;
    public double RockFragmentPeopleLost { get; set; } = 0.05;

    public double DefectorPlantChance { get; set; } = 0.25;

    public double CometWarnDays { get; set; } = 10;
    public double CometTelescopeDays { get; set; } = 30;
    public double CometDays { get; set; } = 20;
    public double CometIndustry { get; set; } = 10;
    public double CometWaterPerDay { get; set; } = 3;
    public double CometFuelPerDay { get; set; } = 1;
}

/// <summary>
/// Something that happens to one side: a solar flare, a derelict found, a reactor fault, a rock on course
/// for a colony, a defector, a comet passing. It comes with a call (the options and Auto's pick, see
/// WarCall) and, for most, an hour it hits (HitsHour) and an hour its effects end (UntilHour).
/// </summary>
public sealed class GameEvent
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public double Hour { get; set; }
    public WarCall Call { get; set; } = new();
    public double HitsHour { get; set; }
    public double UntilHour { get; set; }
    public int? BodyId { get; set; }
    public int? FleetId { get; set; }
    public Vec2 At { get; set; }

    /// <summary>Hit: it has struck (its effects run until UntilHour). Done: all over.</summary>
    public bool Hit { get; set; }
    public bool Done { get; set; }

    /// <summary>What came of it, for the events list.</summary>
    public string Outcome { get; set; } = "";

    /// <summary>Hidden from the player: the defector is a plant; the derelict is of the rival's make.</summary>
    public bool Plant { get; set; }
    public bool RivalMake { get; set; }
}

/// <summary>
/// Events: rare things that happen, warned in advance where it makes sense, each with options and an
/// Auto pick. A game-start setting sets how often (Off, Rare, Normal, Lively); both sides get them at
/// the same rate, all rolled from SimRandom so replays match. With the Events area on Auto the pick is
/// made at once; on Advise the event waits in Needs you until its deadline and then Auto picks; on
/// Manual it also pauses the game. Better research means more warning: a sun watch for flares, rock
/// mapping for rock strikes, deep-space telescopes for comets. Events fire sparks and feed the intel
/// file. No event can end a game on its own: disasters hurt, but there is always a counter.
/// </summary>
public static class Events
{
    public static readonly string[] Kinds = { "flare", "derelict", "reactor-fault", "rock-strike", "defector", "comet" };

    public const string SunWatch = "sun-watch";
    public const string Telescopes = "deep-space-telescopes";

    private static EventsData E => GameData.Hazards.Events;

    public static string RateName(EventRate r) => r.ToString();

    public static string KindName(string kind) => kind switch
    {
        "flare" => "Space weather",
        "derelict" => "Find",
        "reactor-fault" => "Colony",
        "rock-strike" => "Disaster",
        "defector" => "Rival",
        "comet" => "Opportunity",
        _ => kind,
    };

    /// <summary>Sensors of this side see this share as far (a flare dazzles everyone; sheltering blinds you more).</summary>
    public static double BlindFactor(World w, Side side)
    {
        var f = 1.0;
        if (w.Hour < w.FlareUntilHour) f = E.FlareBlind;
        var s = Economy.Of(w, side);
        if (w.Hour < s.BlindUntilHour) f = Math.Min(f, E.FlareShelterBlind);
        return f;
    }

    /// <summary>This side's fleets show this share of their signature (used a flare to hide).</summary>
    public static double SignatureFactor(World w, Side side) =>
        w.Loop && w.Hour < Economy.Of(w, side).HiddenUntilHour ? E.FlareHideSignature : 1;

    /// <summary>A reactor fault's power cut at a body now (0 if none).</summary>
    public static double PowerCut(World w, Body b) => w.Hour < b.FaultUntilHour ? b.FaultShare : 0;

    /// <summary>
    /// An event or hazard with wide impact, for the banner under the top bar: what it is, what it does, until when
    /// (Coming: the hour it hits). Local trouble (one planet, one ship) stays in Needs you.
    /// </summary>
    public sealed record WideEvent(string Key, string Title, string Effect, double UntilHour, bool Coming, int? BodyId, int? EventId);

    /// <summary>The wide events this side knows of now, soonest ending first. Read-only.</summary>
    public static List<WideEvent> Wide(World w, Side side)
    {
        var list = new List<WideEvent>();
        if (!w.Loop) return list;
        var state = Economy.Of(w, side);
        foreach (var e in w.Events.Where(e => e.Side == side && !e.Done && !e.Hit && e.Kind == "flare").OrderBy(e => e.HitsHour))
            list.Add(new WideEvent($"flare-coming:{e.Id}", "Solar flare coming",
                $"ships out of a planet's shadow take crew strain; all sensors dazzled for {E.FlareHours:0} h", e.HitsHour, true, null, e.Id));
        if (w.Hour < w.FlareUntilHour)
        {
            var effect = $"every side's sensors see {E.FlareBlind:P0} as far";
            if (w.Hour < state.BlindUntilHour) effect += $"; yours are sheltered: {E.FlareShelterBlind:P0} as far";
            if (w.Hour < state.HiddenUntilHour) effect += $"; your fleets hide in the glare ({E.FlareHideSignature:P0} signature)";
            list.Add(new WideEvent("flare", "Solar flare", effect, w.FlareUntilHour, false, null, null));
        }
        foreach (var e in w.Events.Where(e => e.Side == side && !e.Done && e.Kind == "comet").OrderBy(e => e.HitsHour))
        {
            var where = e.BodyId is int id ? w.FindBody(id)?.Name : null;
            var mining = e.Hit && e.Call.Picked == 0;
            list.Add(new WideEvent($"comet:{e.Id}", e.Title,
                !e.Hit ? $"arrives near {where ?? "your world"}: water and fuel to mine if you send miners"
                    : mining ? $"miners at {where} bring in water and fuel while it lasts" : "passing; not being mined",
                e.Hit ? e.UntilHour : e.HitsHour, !e.Hit, e.BodyId, e.Id));
        }
        return list.OrderBy(x => x.UntilHour).ToList();
    }

    public static IEnumerable<GameEvent> Open(World w, Side side) => w.Events.Where(e => e.Side == side && e.Call.Open && !e.Done);

    // ---- Every hour --------------------------------------------------------------------------------

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Winner != null) return;
        var rate = w.Rules.Events;
        if (rate != EventRate.Off && E.PerSideDays[(int)rate] > 0)
        {
            foreach (var side in new[] { Side.Player, Side.Rival })
            {
                var state = Economy.Of(w, side);
                var days = E.PerSideDays[(int)rate];
                if (state.NextEventHour <= 0)
                    state.NextEventHour = (E.FirstDay + sim.Rng.Range(0, days * 0.5)) * 24;
                else if (w.Hour >= state.NextEventHour)
                {
                    state.NextEventHour = w.Hour + days * sim.Rng.Range(0.5, 1.5) * 24;
                    Spawn(sim, side);
                }
            }
        }
        foreach (var e in w.Events.Where(e => !e.Done).OrderBy(e => e.Id).ToList())
        {
            if (e.Call.Open && w.Hour >= e.Call.DueHour - 1e-9) Choose(sim, e, e.Call.AutoPick, byAuto: true);
            if (!e.Call.Open && !e.Hit && w.Hour >= e.HitsHour - 1e-9) Strike(sim, e);
            if (e.Hit && !e.Done && w.Hour >= e.UntilHour - 1e-9) Finish(sim, e);
        }
        // Keep the list short: the last few done events per side.
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var done = w.Events.Where(e => e.Side == side && e.Done).ToList();
            for (var i = 0; i < done.Count - 12; i++) w.Events.Remove(done[i]);
        }
    }

    /// <summary>Rolls a kind of event for the side among those that can happen now, and starts it.</summary>
    public static GameEvent? Spawn(Simulation sim, Side side, string? kind = null)
    {
        var w = sim.World;
        if (kind == null)
        {
            var options = Kinds.Where(k => E.Weights.GetValueOrDefault(k) > 0 && CanHappen(w, side, k)).ToList();
            if (options.Count == 0) return null;
            var roll = sim.Rng.NextDouble() * options.Sum(k => E.Weights[k]);
            kind = options[^1];
            foreach (var k in options)
            {
                roll -= E.Weights[k];
                if (roll <= 0) { kind = k; break; }
            }
        }
        if (!CanHappen(w, side, kind)) return null;
        var e = kind switch
        {
            "flare" => Flare(sim, side),
            "derelict" => Derelict(sim, side),
            "reactor-fault" => ReactorFault(sim, side),
            "rock-strike" => RockStrike(sim, side),
            "defector" => Defector(sim, side),
            _ => Comet(sim, side),
        };
        if (e == null) return null;
        w.Events.Add(e);
        Economy.Of(w, side).Totals.Events++;
        sim.Log.Info("events", $"{side}: {e.Title}", new { side = side.ToString(), eventId = e.Id, e.Kind, text = e.Call.Text, hits = Math.Round(e.HitsHour, 1), due = Math.Round(e.Call.DueHour, 1) });
        // A flare is weather: it reaches the other side too, with the warning its own research gives.
        if (kind == "flare" && !w.Events.Any(x => x.Kind == "flare" && x.Side != side && !x.Done))
        {
            var other = Flare(sim, Combat.Other(side), e.HitsHour);
            w.Events.Add(other);
            Economy.Of(w, other.Side).Totals.Events++;
            if (other.Call.DueHour <= w.Hour + 1e-9) Choose(sim, other, other.Call.AutoPick, byAuto: true);
        }
        if (e.Call.DueHour <= w.Hour + 1e-9) Choose(sim, e, e.Call.AutoPick, byAuto: true);
        return e;
    }

    public static bool CanHappen(World w, Side side, string kind) => kind switch
    {
        "flare" => !w.Events.Any(e => e.Kind == "flare" && !e.Done) && w.Hour >= w.FlareUntilHour,
        "derelict" => FinderFleet(w, side) != null,
        "reactor-fault" => FaultTargets(w, side).Any(),
        "rock-strike" => StrikeTargets(w, side).Any(),
        "defector" => Intel.Of(w, side).LastContactHour > 0 && !w.Events.Any(e => e.Kind == "defector" && e.Side == side && !e.Done),
        "comet" => w.Bodies.Any(b => b.Owner == side && b.PopK > 0) && !w.Events.Any(e => e.Kind == "comet" && e.Side == side && !e.Done),
        _ => false,
    };

    private static GameEvent New(World w, Side side, string kind, string title, double hitsHour, double askHours, int autoPick, string text,
        params (string Label, string Effect)[] options)
    {
        var auto = War.AutoCalls(w, side, AutoArea.Events);
        var due = Math.Min(Math.Max(w.Hour + 1, hitsHour), w.Hour + askHours);
        var call = War.Call(w, kind, text, autoPick, auto, Math.Max(0, due - w.Hour), options);
        return new GameEvent { Id = call.Id, Side = side, Kind = kind, Title = title, Hour = w.Hour, Call = call, HitsHour = Math.Max(w.Hour, hitsHour), UntilHour = Math.Max(w.Hour, hitsHour) };
    }

    // ---- The kinds -----------------------------------------------------------------------------------

    private static GameEvent Flare(Simulation sim, Side side, double? hits = null)
    {
        var w = sim.World;
        var warn = Tech.Has(w, side, SunWatch) ? E.FlareWatchHours : E.FlareWarnHours;
        var at = hits ?? w.Hour + warn;
        var seen = Math.Max(0, at - w.Hour);
        var e = New(w, side, "flare", $"Solar flare in {seen:0} hours", at, seen, 0,
            $"A solar flare in {seen:0} hours{(Tech.Has(w, side, SunWatch) ? " (the sun watch saw it coming)" : "")}. Ships out of a planet's shadow take crew strain; sensors are dazzled for {E.FlareHours:0} hours.",
            ("Shelter", "Fleets not in battle turn their tanks to the sun: a quarter of the strain, but your sensors are blind while it lasts"),
            ("Ride it out", "Ships out in space take the full strain; sensors only dazzled"),
            ("Use it", "Your fleets hide in the glare (half their signature) while it lasts, and take the full strain"));
        e.UntilHour = at + E.FlareHours;
        return e;
    }

    /// <summary>A fleet of the side out in space (not in orbit, not fighting) that could spot a derelict.</summary>
    private static Fleet? FinderFleet(World w, Side side) =>
        w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0 && f.Hold != HoldStyle.Orbit && !Heat.InBattle(w, f) && f.Position.IsFinite)
            .OrderBy(f => f.Id).FirstOrDefault();

    private static GameEvent? Derelict(Simulation sim, Side side)
    {
        var w = sim.World;
        if (FinderFleet(w, side) is not Fleet f) return null;
        var where = w.Belts.FirstOrDefault(b => Hazards.IsRocks(b) && Hazards.Inside(w, b, f.Position))?.Name ?? "deep space";
        var e = New(w, side, "derelict", $"Derelict ship found by {f.Name}", w.Hour, E.AskHours, 0,
            $"{f.Name} spots a derelict drifting in {where}. Old hull, origin unknown. Salvage takes {E.DerelictDays:0} days.",
            ("Salvage it", $"Materials ({E.DerelictAlloys:0} alloys, {E.DerelictElectronics:0} electronics) and a research spark in {E.DerelictDays:0} days"),
            ("Leave it", "Nothing"));
        e.FleetId = f.Id;
        e.At = f.Position;
        e.RivalMake = sim.Rng.NextDouble() < E.DerelictRivalChance;
        e.HitsHour = e.UntilHour = w.Hour + E.DerelictDays * 24;
        e.Call.DueHour = Math.Min(e.Call.DueHour, e.HitsHour);
        return e;
    }

    private static IEnumerable<(Body Body, Piece Plant)> FaultTargets(World w, Side side) =>
        w.Bodies.Where(b => b.Owner == side && b.PopK > 0 && w.Hour >= b.FaultUntilHour)
            .SelectMany(b => Colonies.Built(b).Where(p => Planets.BuildingKind(p.Kind) is { Power: < 0 }).Select(p => (b, p)));

    private static GameEvent? ReactorFault(Simulation sim, Side side)
    {
        var w = sim.World;
        var targets = FaultTargets(w, side).ToList();
        if (targets.Count == 0) return null;
        var (b, plant) = targets[sim.Rng.Range(0, targets.Count)];
        var name = Planets.BuildingKind(plant.Kind)?.Name ?? plant.Kind;
        var spares = Colonies.Held(b, Colonies.Spares);
        var auto = spares >= E.FaultFastSpares * 2 ? 0 : 1;
        var e = New(w, side, "reactor-fault", $"Reactor fault on {b.Name}", w.Hour, E.AskHours, auto,
            $"The {name.ToLowerInvariant()} on {b.Name} has a fault: power {E.FaultShare:P0} down until it is fixed ({spares:0} spare parts in store).",
            ("Fix fast", $"{E.FaultFastSpares:0} spare parts, fixed in {E.FaultFastDays:0} days"),
            ("Fix slow", $"{E.FaultSlowSpares:0} spare parts, fixed in {E.FaultSlowDays:0} days"),
            ("Shut it down", $"No parts, but {E.FaultOffShare:P0} of the power off for {E.FaultOffDays:0} days"));
        e.BodyId = b.Id;
        // The fault bites at once; the pick decides how long it lasts.
        b.FaultShare = E.FaultShare;
        b.FaultUntilHour = w.Hour + E.FaultNoSparesDays * 24;
        return e;
    }

    private static IEnumerable<Body> StrikeTargets(World w, Side side) =>
        w.Bodies.Where(b => b.Owner == side && Colonies.Built(b).Any(p => p.Kind != Colonies.Shipyard) && !w.Events.Any(e => e.Kind == "rock-strike" && e.BodyId == b.Id && !e.Done));

    private static GameEvent? RockStrike(Simulation sim, Side side)
    {
        var w = sim.World;
        // Small bodies in the belt are hit most; any body you hold can be.
        var targets = StrikeTargets(w, side).ToList();
        if (targets.Count == 0) return null;
        var weights = targets.Select(b => b.Kind == BodyKind.Asteroid ? 4.0 : b.Kind == BodyKind.Moon ? 2.0 : 1.0).ToList();
        var roll = sim.Rng.NextDouble() * weights.Sum();
        var target = targets[^1];
        for (var i = 0; i < targets.Count; i++) { roll -= weights[i]; if (roll <= 0) { target = targets[i]; break; } }
        var days = Tech.Has(w, side, Hazards.RockMapping) ? E.RockMappedDays : E.RockWarnDays;
        var driver = HasDriver(w, target);
        var shooters = Shooters(w, side, target).Any();
        var industry = Economy.Of(w, side).Industry;
        var auto = driver || industry >= E.RockPushIndustry ? 0 : shooters ? 2 : 1;
        var e = New(w, side, "rock-strike", $"Rock strike on {target.Name} in {days:0} days", w.Hour + days * 24, E.AskHours, auto,
            $"A rock a few hundred metres across is on course for {target.Name}: it hits in {days:0} days{(days > E.RockWarnDays ? " (rock mapping saw it early)" : "")}.",
            ("Push it", driver ? "The mass driver nudges it off course: sure, and free" : $"Tugs nudge it off course: sure, {E.RockPushIndustry:0} Industry"),
            ("Evacuate", "It hits: one building lost, but nobody dies"),
            ("Shoot it", shooters ? $"Warships near by break it up: {E.RockShootChance:P0} it works, else fragments damage a building and kill a few" : "No warships near enough: it hits as if left alone"));
        e.BodyId = target.Id;
        return e;
    }

    private static bool HasDriver(World w, Body b) =>
        Colonies.Built(b).Any(p => p.Kind == "mass-driver") || Works.DriverReach(w, b).Any(o => Colonies.Built(o).Any(p => p.Kind == "mass-driver"));

    private static IEnumerable<Fleet> Shooters(World w, Side side, Body b) =>
        w.Fleets.Where(f => f.Side == side && Combat.Armed(w, f) && Vec2.Distance(f.Position, b.Position) <= E.RockShootAu * Physics.MmPerAu);

    private static GameEvent? Defector(Simulation sim, Side side)
    {
        var w = sim.World;
        var file = Intel.Of(w, side);
        var level = Intel.Level(file);
        var e = New(w, side, "defector", "Defector from the rival", w.Hour, E.AskHours, 0,
            "A rival officer offers to come over with fleet plans.",
            ("Accept", level < Intel.LevelPoints.Length - 1 ? $"Intel on the rival rises a level ({Intel.LevelNames[level]} to {Intel.LevelNames[level + 1]}), fading as usual" : "More intel on the rival"),
            ("Refuse", "Could be a plant: nothing happens"));
        e.Plant = sim.Rng.NextDouble() < E.DefectorPlantChance;
        return e;
    }

    private static GameEvent? Comet(Simulation sim, Side side)
    {
        var w = sim.World;
        var bodies = w.Bodies.Where(b => b.Owner == side && b.PopK > 0).OrderBy(b => b.Id).ToList();
        if (bodies.Count == 0) return null;
        var b = bodies[sim.Rng.Range(0, bodies.Count)];
        var days = Tech.Has(w, side, Telescopes) ? E.CometTelescopeDays : E.CometWarnDays;
        var e = New(w, side, "comet", $"Comet passing {b.Name}", w.Hour + days * 24, E.AskHours, 0,
            $"A comet will pass within reach of {b.Name} in {days:0} days: ice and volatiles worth months of mining, for {E.CometDays:0} days.",
            ("Send ice miners", $"{E.CometIndustry:0} Industry: {b.Name} gets {E.CometWaterPerDay:0} water and {E.CometFuelPerDay:0} fuel a day while it passes"),
            ("Ignore", "Nothing"));
        e.BodyId = b.Id;
        e.UntilHour = e.HitsHour + E.CometDays * 24;
        return e;
    }

    // ---- Picks, strikes and endings ---------------------------------------------------------------------

    /// <summary>The side picks an option (or Auto does): costs are paid now; what it does comes when the event hits.</summary>
    public static string? Choose(Simulation sim, GameEvent e, int option, bool byAuto)
    {
        var w = sim.World;
        if (e.Done || !e.Call.Open) return "That call is made";
        if (option < 0 || option >= e.Call.Options.Count) return "No such option";
        e.Call.Picked = option;
        e.Call.ByAuto = byAuto;
        var state = Economy.Of(w, e.Side);
        var body = e.BodyId is int id ? w.FindBody(id) : null;
        switch (e.Kind)
        {
            case "reactor-fault" when body != null:
                var (spares, days, share) = option switch
                {
                    0 => (E.FaultFastSpares, E.FaultFastDays, E.FaultShare),
                    1 => (E.FaultSlowSpares, E.FaultSlowDays, E.FaultShare),
                    _ => (0.0, E.FaultOffDays, E.FaultOffShare),
                };
                if (spares > 0 && Colonies.Held(body, Colonies.Spares) < spares)
                {
                    e.Outcome = "Not enough spare parts: the fault drags on";
                    days = E.FaultNoSparesDays;
                }
                else if (spares > 0)
                {
                    body.Stock[Colonies.Spares] = Colonies.Held(body, Colonies.Spares) - spares;
                    Ledger.Out(w, e.Side, Colonies.Spares, spares, "reactor repair");
                }
                body.FaultShare = share;
                body.FaultUntilHour = w.Hour + days * 24;
                e.HitsHour = w.Hour;
                e.UntilHour = body.FaultUntilHour;
                break;
            case "rock-strike" when option == 0 && body != null && !HasDriver(w, body):
                if (state.Industry < E.RockPushIndustry) { e.Call.Picked = 1; e.Outcome = "Not enough Industry for the tugs: evacuating instead"; break; }
                state.Industry -= E.RockPushIndustry;
                state.Totals.IndustrySpent += E.RockPushIndustry;
                break;
            case "comet" when option == 0:
                if (state.Industry < E.CometIndustry) { e.Call.Picked = 1; e.Outcome = "Not enough Industry to send miners"; break; }
                state.Industry -= E.CometIndustry;
                state.Totals.IndustrySpent += E.CometIndustry;
                break;
        }
        Choices.Note(w, e.Side, "event", $"{e.Kind}: {e.Call.Options[e.Call.Picked!.Value]}", body?.Name ?? "", byAuto ? "auto" : "by hand");
        sim.Log.Info("events", $"{e.Side} picks {e.Call.Options[e.Call.Picked!.Value]} for {e.Title}", new { side = e.Side.ToString(), eventId = e.Id, option = e.Call.Picked, byAuto });
        if (w.Hour >= e.HitsHour - 1e-9) Strike(sim, e);
        return null;
    }

    /// <summary>The event hits: what the pick does.</summary>
    private static void Strike(Simulation sim, GameEvent e)
    {
        var w = sim.World;
        e.Hit = true;
        var pick = e.Call.Picked ?? e.Call.AutoPick;
        var state = Economy.Of(w, e.Side);
        var body = e.BodyId is int id ? w.FindBody(id) : null;
        switch (e.Kind)
        {
            case "flare":
                w.FlareUntilHour = Math.Max(w.FlareUntilHour, e.UntilHour);
                if (pick == 0) state.BlindUntilHour = e.UntilHour;
                if (pick == 2) state.HiddenUntilHour = e.UntilHour;
                var strained = FlareStrain(sim, e.Side, shelter: pick == 0);
                e.Outcome = $"{strained} ships took crew strain or wear";
                Sparks.Fire(sim, e.Side, "flare");
                break;
            case "derelict":
                if (pick != 0) { e.Outcome = "Left alone"; break; }
                var store = Economy.ShipyardOf(w, e.Side) ?? w.Bodies.Find(b => b.Owner == e.Side);
                if (store != null)
                {
                    Gain(w, e.Side, store, "alloys", E.DerelictAlloys, "derelict salvage");
                    Gain(w, e.Side, store, "electronics", E.DerelictElectronics, "derelict salvage");
                }
                var idea = SparkSomething(sim, e.Side, "Find: a derelict salvaged");
                if (e.RivalMake) Spoils.Scrap(sim, e.Side, "a derelict of rival make", IntelSource.Wreck, Intel.SalvagePoints);
                e.Outcome = $"Salvaged: materials to {store?.Name ?? "nowhere"}{(idea != null ? $", and {idea} came into view" : "")}{(e.RivalMake ? "; it was of rival make" : "")}";
                Sparks.Fire(sim, e.Side, "wreck-salvaged");
                break;
            case "reactor-fault":
                e.Outcome = e.Outcome.Length > 0 ? e.Outcome : body != null ? $"Power down until day {GameTime.DayOf((long)body.FaultUntilHour)}" : "";
                break;
            case "rock-strike":
                e.Outcome = body == null || body.Owner != e.Side ? "The rock missed" : RockStrikes(sim, e, body, pick);
                Sparks.Fire(sim, e.Side, "rock-strike");
                break;
            case "defector":
                if (pick != 0) { e.Outcome = "Turned away"; break; }
                var file = Intel.Of(w, e.Side);
                var level = Intel.Level(file);
                var gain = level < Intel.LevelPoints.Length - 1 ? Intel.LevelPoints[level + 1] - file.Points + 1 : 20;
                Intel.Note(w, e.Side, IntelSource.Prisoners, "A defector brings fleet plans", gain);
                if (e.Plant)
                {
                    var theirs = Intel.Of(w, Combat.Other(e.Side));
                    Intel.Note(w, Combat.Other(e.Side), IntelSource.Prisoners, "Our agent among them reports back", Math.Max(10, gain * 0.5));
                    sim.Log.Info("events", $"The defector to {e.Side} was a plant", new { side = e.Side.ToString(), theirPoints = Math.Round(theirs.Points, 1) });
                }
                e.Outcome = "Intel on the rival rises";
                break;
            case "comet":
                e.Outcome = pick == 0 ? "Miners on the comet" : "Let it pass";
                break;
        }
        sim.Log.Info("events", $"{e.Title}: {e.Outcome}", new { side = e.Side.ToString(), eventId = e.Id, e.Kind, pick });
        if (e.UntilHour <= w.Hour + 1e-9) Finish(sim, e);
    }

    /// <summary>Hourly while an event lasts (a comet being mined).</summary>
    public static void Lasting(Simulation sim)
    {
        var w = sim.World;
        foreach (var e in w.Events.Where(e => e.Hit && !e.Done && e.Kind == "comet" && e.Call.Picked == 0))
            if (e.BodyId is int id && w.FindBody(id) is Body b && b.Owner == e.Side)
            {
                Gain(w, e.Side, b, "water", E.CometWaterPerDay / 24, "comet mining");
                Gain(w, e.Side, b, Colonies.Fuel, E.CometFuelPerDay / 24, "comet mining");
            }
    }

    private static void Finish(Simulation sim, GameEvent e)
    {
        e.Done = true;
        sim.Log.Info("events", $"{e.Title} is over", new { side = e.Side.ToString(), eventId = e.Id, e.Kind });
    }

    private static void Gain(World w, Side side, Body b, string good, double amount, string why)
    {
        b.Stock[good] = Colonies.Held(b, good) + amount;
        Ledger.In(w, side, good, amount, why);
    }

    /// <summary>A flare hits: crewed ships out of a planet's shadow take crew strain, AI ships wear. Returns how many were hurt.</summary>
    private static int FlareStrain(Simulation sim, Side side, bool shelter)
    {
        var w = sim.World;
        var hurt = 0;
        foreach (var f in w.Fleets.Where(f => f.Side == side).ToList())
        {
            var exposure = f.Hold == HoldStyle.Orbit && f.Flight == null ? 0.25 : 1.0;
            if (shelter && !Heat.InBattle(w, f)) exposure *= 0.25;
            if (f.Hold == HoldStyle.Orbit && f.Flight == null && shelter) exposure = 0;
            if (exposure <= 0) continue;
            foreach (var s in w.ShipsOf(f))
            {
                var cut = exposure * Tech.Factor(w, side, Hazards.RadiationShielding) * (Fitting.Spec(s).Shield > 0 ? Hazards.D.RadShieldCut : 1);
                if (Crews.Crewed(s)) s.Crew = Math.Max(Hazards.D.RadCrewFloor, s.Crew - E.FlareCrew * cut);
                if (s.Defence > Hazards.D.RadWearFloor) Hazards.Wear(w, s, Math.Max(Hazards.D.RadWearFloor, s.Defence - E.FlareWear * cut));
                hurt++;
            }
        }
        return hurt;
    }

    /// <summary>The rock hits (or doesn't): a building lost, maybe people.</summary>
    private static string RockStrikes(Simulation sim, GameEvent e, Body b, int pick)
    {
        var w = sim.World;
        if (pick == 0) return HasDriver(w, b) ? "The mass driver pushed it clear" : "Tugs pushed it clear";
        if (pick == 2 && Shooters(w, e.Side, b).Any())
        {
            if (sim.Rng.NextDouble() < E.RockShootChance) return "Railguns broke it up";
            var hit = Colonies.Built(b).Where(p => p.Kind != Colonies.Shipyard).OrderBy(p => p.Id).ToList();
            if (hit.Count > 0) hit[sim.Rng.Range(0, hit.Count)].Condition = 0.2;
            var lost = b.PopK * E.RockFragmentPeopleLost;
            b.PopK -= lost;
            return $"Fragments hit {b.Name}: a building badly damaged, {lost * 1000:0} people lost";
        }
        var pieces = Colonies.Built(b).Where(p => p.Kind != Colonies.Shipyard).OrderBy(p => p.Id).ToList();
        var gone = "";
        if (pieces.Count > 0)
        {
            var p = pieces[sim.Rng.Range(0, pieces.Count)];
            Colonies.RemoveOne(b, x => x.Id == p.Id);
            gone = Planets.BuildingKind(p.Kind)?.Name ?? p.Kind;
        }
        var dead = pick == 1 ? 0 : b.PopK * E.RockPeopleLost;
        b.PopK -= dead;
        var (moved, homeless) = Planets.Overflow(w, b);
        var after = (moved > 0 ? $"; {moved * 1000:0} people had to move to other worlds" : "")
                    + (homeless > 0 ? $"; {homeless * 1000:0} found no room and were lost" : "");
        if (pick == 1)
        {
            if (w.Rules.Happiness) b.Happiness = Math.Max(0, b.Happiness - 3);
            return $"It hit {b.Name}: the {gone.ToLowerInvariant()} is lost, but everyone got out" + after;
        }
        return $"It hit {b.Name}: the {gone.ToLowerInvariant()} is lost and {dead * 1000:0} people with it" + after;
    }

    /// <summary>A find brings an idea the side hasn't got into view (or a quarter off if queued). Returns its name, or null.</summary>
    private static string? SparkSomething(Simulation sim, Side side, string why)
    {
        var w = sim.World;
        var state = Economy.Of(w, side);
        var options = GameData.Economy.Research.Where(r => !r.Later && !state.Researched.Contains(r.Id) && !state.Sparked.Contains(r.Id))
            .Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
        if (options.Count == 0) return null;
        var id = options[sim.Rng.Range(0, options.Count)];
        var queued = w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Research && o.Key == id).ToList();
        foreach (var o in queued) o.HoursLeft = Math.Max(0, o.HoursLeft * (1 - Sparks.Cut));
        state.Sparked.Add(id);
        state.SparkNotes.Add(new SparkNote(id, why, w.Hour, queued.Count > 0));
        if (state.SparkNotes.Count > 60) state.SparkNotes.RemoveAt(0);
        return GameData.Research(id).Name;
    }

    /// <summary>Needs you lines for the player's open events (see Stewards): Auto's pick as the act button.</summary>
    public static void Needs(World w, Side side, Action<string, string, int?, int?, GameCommand, string> need)
    {
        foreach (var e in Open(w, side).OrderBy(e => e.Call.DueHour))
            need($"event:{e.Id}", $"{e.Title}: {e.Call.Text}", e.BodyId, e.FleetId, new PickEventCommand(side, e.Id, e.Call.AutoPick),
                $"Auto's pick: {e.Call.Options[e.Call.AutoPick]}");
    }
}

/// <summary>Make the call on an event: pick one of its options.</summary>
public sealed record PickEventCommand(Side Side, int EventId, int Option) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null;

    public override void Apply(World world, Simulation sim)
    {
        var problem = world.Events.Find(e => e.Id == EventId) is GameEvent e && e.Side == Side ? Events.Choose(sim, e, Option, byAuto: false) : "No such event";
        if (problem != null) sim.Log.Warn("events", $"Can't make that call: {problem}", new { side = Side.ToString(), EventId, Option, Source });
    }
}
