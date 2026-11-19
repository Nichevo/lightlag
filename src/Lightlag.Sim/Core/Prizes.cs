using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>Prize numbers (hazards.json, "prizes"). See the comment there.</summary>
public sealed class PrizesData
{
    public int MinCount { get; set; } = 2;
    public int MaxCount { get; set; } = 3;
    public double FirstOpenDayMin { get; set; } = 35;
    public double FirstOpenDayMax { get; set; } = 50;
    public double GapDaysMin { get; set; } = 22;
    public double GapDaysMax { get; set; } = 35;
    public double AnnounceDays { get; set; } = 20;
    public double ReachMm { get; set; } = 250;

    public double BoardDays { get; set; } = 3;
    public double DerelictDays { get; set; } = 40;
    public double DerelictArmour { get; set; } = 0.5;
    public double DerelictSupplies { get; set; } = 0.3;

    public double CometDays { get; set; } = 40;
    public double CometFuelPerDay { get; set; } = 4;
    public double CometWaterPerDay { get; set; } = 4;
    public double CometHelium3PerDay { get; set; } = 2;

    public double RelayTakeDays { get; set; } = 2;
    public double RelaySensorMm { get; set; } = 1200;
}

public enum PrizeKind { Derelict, Comet, Relay }

/// <summary>
/// A contested prize: something both sides see coming, at a point in space between their territories, open from one
/// hour to another. Warships that sit at it with no enemy warships there work on it hour by hour; with both sides
/// there nobody gains.
/// </summary>
public sealed class Prize
{
    public int Id { get; set; }
    public PrizeKind Kind { get; set; }
    public string Name { get; set; } = "";
    public Vec2 At { get; set; }

    /// <summary>The two sides it lies between (its place is worked out when it is announced).</summary>
    public Side A { get; set; }
    public Side B { get; set; }

    public double AnnounceHour { get; set; }
    public double OpenHour { get; set; }

    /// <summary>When it is gone (the derelict drifts off, the comet leaves); null: it stays (the relay).</summary>
    public double? CloseHour { get; set; }

    public bool Announced { get; set; }
    public bool Over { get; set; }

    /// <summary>Hours of work each side has put in (boarding, mining, taking the relay), indexed by Side.</summary>
    public double[] Hours { get; set; } = new double[Sides.Max];

    /// <summary>Comet: what each side has mined so far (all goods together), indexed by Side.</summary>
    public double[] Mined { get; set; } = new double[Sides.Max];

    /// <summary>Who has it: the derelict's taker, the relay's holder, the comet's best miner once it has gone.</summary>
    public Side? Holder { get; set; }

    /// <summary>Last hour: the one side working on it alone, or contested (more than one side there).</summary>
    public Side? WorkedBy { get; set; }
    public bool Contested { get; set; }

    public string Outcome { get; set; } = "";
    public double? OutcomeHour { get; set; }

    public bool Open(World w) => Announced && !Over && w.Hour >= OpenHour;
    public bool Coming(World w) => Announced && !Over && w.Hour < OpenHour;
}

/// <summary>
/// Contested prizes (Plan 4, build 4): two or three per game, each announced to every side at once some days before
/// it opens, placed between two sides' home worlds and timed for the middle game. A derelict cruiser to board and
/// keep; a comet rich in helium-3 to mine while it passes; an old relay that gives wide sensor cover to whoever holds
/// it. Armed fleets within ReachMm of the prize count as there; one side there alone works on it, two sides there
/// contest it and nobody gains (they are close enough to fight). A game-start switch turns them off.
/// The rolls come from the game seed (no random stream moves), so turning prizes on or off changes nothing else.
/// </summary>
public static class Prizes
{
    public static PrizesData D => GameData.Hazards.Prizes;

    public static bool On(World w) => w.Loop && w.Rules.Prizes;

    public static string KindName(PrizeKind k) => k switch
    {
        PrizeKind.Derelict => "Derelict cruiser",
        PrizeKind.Comet => "Helium-3 comet",
        _ => "Old relay",
    };

    /// <summary>What it is and what holding it does, for tooltips and the announcement.</summary>
    public static string What(PrizeKind k) => k switch
    {
        PrizeKind.Derelict => $"an old cruiser drifting in with nobody aboard: hold it with warships for {D.BoardDays:0} days, with no enemy warships there, to board it and keep it",
        PrizeKind.Comet => $"a comet rich in helium-3 passing for {D.CometDays:0} days: while your warships hold it alone, your shipyard gets {D.CometFuelPerDay:0} fuel, {D.CometWaterPerDay:0} water and {D.CometHelium3PerDay:0} helium-3 a day",
        _ => $"an old relay station that wakes up: hold it alone for {D.RelayTakeDays:0} days to take it; whoever holds it sees far round it, until someone takes it from them",
    };

    public static double NeedHours(Prize p) => p.Kind switch
    {
        PrizeKind.Derelict => D.BoardDays * 24,
        PrizeKind.Relay => D.RelayTakeDays * 24,
        _ => (p.CloseHour ?? p.OpenHour) - p.OpenHour,
    };

    /// <summary>The prizes a side knows of now (announced), soonest first.</summary>
    public static IEnumerable<Prize> Known(World w) => w.Prizes.Where(p => p.Announced).OrderBy(p => p.OpenHour).ThenBy(p => p.Id);

    /// <summary>The prizes still to play for (coming or open).</summary>
    public static IEnumerable<Prize> Live(World w) => Known(w).Where(p => !p.Over);

    /// <summary>The side's armed fleets at the prize (within ReachMm, not running home).</summary>
    public static IEnumerable<Fleet> There(World w, Prize p, Side side) =>
        w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0 && f.Position.IsFinite && !f.Retreating && Combat.Armed(w, f)
                            && Vec2.Distance(f.Position, p.At) <= D.ReachMm).OrderBy(f => f.Id);

    /// <summary>The live prize a fleet is at, if any.</summary>
    public static Prize? At(World w, Fleet f) =>
        Live(w).FirstOrDefault(p => f.Position.IsFinite && Vec2.Distance(f.Position, p.At) <= D.ReachMm);

    /// <summary>The live prize a point is at (a flight's end, a click on the map).</summary>
    public static Prize? Near(World w, Vec2 at, double withinMm) =>
        Live(w).Where(p => Vec2.Distance(p.At, at) <= withinMm).OrderBy(p => Vec2.Distance(p.At, at)).FirstOrDefault();

    // ---- Every hour ---------------------------------------------------------------------------------

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (!On(w) || w.Winner != null || w.WinHour != null) return;
        if (!w.PrizesPlanned) Plan(w);
        foreach (var p in w.Prizes.OrderBy(p => p.Id))
        {
            if (p.Over) continue;
            if (!p.Announced && w.Hour >= p.AnnounceHour - 1e-9) Announce(sim, p);
            if (!p.Open(w)) continue;
            Work(sim, p);
            if (!p.Over && p.CloseHour is double close && w.Hour >= close - 1e-9) Close(sim, p);
        }
    }

    /// <summary>Rolls the game's prizes: how many, which kinds and when (where comes when each is announced).</summary>
    public static void Plan(World w)
    {
        w.PrizesPlanned = true;
        var standing = Sides.Standing(w).ToList();
        if (standing.Count < 2) return;
        var rng = new SimRandom(w.Seed ^ 0x5A1E9C0FFEEUL, 91);
        var count = rng.Range(D.MinCount, D.MaxCount + 1);
        var kinds = new List<PrizeKind> { PrizeKind.Derelict, PrizeKind.Comet, PrizeKind.Relay };
        // Shuffle (the same each replay).
        for (var i = kinds.Count - 1; i > 0; i--)
        {
            var j = rng.Range(0, i + 1);
            (kinds[i], kinds[j]) = (kinds[j], kinds[i]);
        }
        // A game loaded from before prizes existed starts its prizes from now.
        var open = Math.Max(rng.Range(D.FirstOpenDayMin, D.FirstOpenDayMax), w.Hour / 24 + D.AnnounceDays + 2);
        var pairs = standing.SelectMany((a, i) => standing.Skip(i + 1).Select(b => (a, b))).ToList();
        for (var n = 0; n < Math.Min(count, kinds.Count); n++)
        {
            var kind = kinds[n];
            var (a, b) = pairs[n % pairs.Count];
            var openHour = Math.Round(open * 24);
            w.Prizes.Add(new Prize
            {
                Id = n + 1, Kind = kind, Name = KindName(kind), A = a, B = b,
                OpenHour = openHour, AnnounceHour = openHour - D.AnnounceDays * 24,
                CloseHour = kind switch
                {
                    PrizeKind.Derelict => openHour + D.DerelictDays * 24,
                    PrizeKind.Comet => openHour + D.CometDays * 24,
                    _ => null,
                },
            });
            open += rng.Range(D.GapDaysMin, D.GapDaysMax);
        }
    }

    /// <summary>
    /// Where a prize between two sides goes: at the angle halfway between their home worlds (the shorter way round),
    /// about halfway between them, a little off that line, and clear of any held world.
    /// </summary>
    public static Vec2 Place(World w, Prize p)
    {
        var star = w.Bodies.FirstOrDefault(b => b.ParentId == null)?.Position ?? Vec2.Zero;
        var ha = Economy.HomeOf(w, p.A)?.Position ?? star + new Vec2(Physics.MmPerAu, 0);
        var hb = Economy.HomeOf(w, p.B)?.Position ?? star - new Vec2(Physics.MmPerAu, 0);
        var da = ha - star;
        var db = hb - star;
        var angA = DetMath.Atan2(da.Y, da.X);
        var angB = DetMath.Atan2(db.Y, db.X);
        var gap = DetMath.Wrap(angB - angA);
        var mid = angA + gap / 2;
        // On the bisector, pulled in towards the star when the homes are far apart round it (the straight line between
        // them, but never closer to the star than half their distance from it): a shorter trip for both.
        var radius = (da.Length + db.Length) / 2 * Math.Max(0.5, DetMath.Cos(gap / 2));
        var rng = new SimRandom(w.Seed ^ (0x77AB1EUL * (ulong)p.Id), 93);
        Vec2 best = Vec2.Zero;
        var bestScore = double.NegativeInfinity;
        for (var tries = 0; tries < 8; tries++)
        {
            var ang = mid + rng.Range(-0.15, 0.15) * Math.Max(0.4, Math.Abs(gap));
            var r = radius * rng.Range(0.85, 1.15);
            var at = star + new Vec2(DetMath.Cos(ang), DetMath.Sin(ang)) * r;
            var clear = w.Bodies.Where(b => b.Owner != null && b.Position.IsFinite).Select(b => Vec2.Distance(b.Position, at)).DefaultIfEmpty(Physics.MmPerAu).Min();
            var fair = Math.Abs(Vec2.Distance(at, ha) - Vec2.Distance(at, hb));
            var score = Math.Min(clear, 0.3 * Physics.MmPerAu) - 0.5 * fair;
            if (score > bestScore) (best, bestScore) = (at, score);
        }
        return best;
    }

    private static void Announce(Simulation sim, Prize p)
    {
        var w = sim.World;
        p.Announced = true;
        p.At = Place(w, p);
        sim.Log.Info("prizes", "Prize announced", new { prizeId = p.Id, kind = p.Kind.ToString(), at = p.At.ToString(), opens = p.OpenHour, closes = p.CloseHour, between = $"{p.A}-{p.B}" });
    }

    /// <summary>One hour at an open prize: who is there, and what their hour of work brings.</summary>
    private static void Work(Simulation sim, Prize p)
    {
        var w = sim.World;
        var there = Sides.Standing(w).Where(s => There(w, p, s).Any()).ToList();
        p.Contested = there.Count > 1;
        p.WorkedBy = there.Count == 1 ? there[0] : null;
        if (p.WorkedBy is not Side side) return;
        var i = (int)side;
        switch (p.Kind)
        {
            case PrizeKind.Derelict:
                p.Hours[i] += GameTime.HoursPerTick;
                if (p.Hours[i] >= NeedHours(p) - 1e-9) Board(sim, p, side);
                break;
            case PrizeKind.Comet:
                p.Hours[i] += GameTime.HoursPerTick;
                if (Economy.ShipyardOf(w, side) is Body yard)
                {
                    Gain(w, side, yard, Colonies.Fuel, D.CometFuelPerDay / 24);
                    Gain(w, side, yard, "water", D.CometWaterPerDay / 24);
                    Gain(w, side, yard, "helium-3", D.CometHelium3PerDay / 24);
                    p.Mined[i] += (D.CometFuelPerDay + D.CometWaterPerDay + D.CometHelium3PerDay) / 24;
                }
                break;
            case PrizeKind.Relay:
                if (p.Holder == side)
                {
                    // The holder there alone wears down anyone's half-done attempt to take it.
                    for (var k = 0; k < p.Hours.Length; k++) p.Hours[k] = Math.Max(0, p.Hours[k] - GameTime.HoursPerTick);
                    break;
                }
                p.Hours[i] += GameTime.HoursPerTick;
                if (p.Hours[i] >= NeedHours(p) - 1e-9) TakeRelay(sim, p, side);
                break;
        }
    }

    private static void Gain(World w, Side side, Body b, string good, double amount)
    {
        b.Stock[good] = Colonies.Held(b, good) + amount;
        Ledger.In(w, side, good, amount, "comet mining");
    }

    /// <summary>The boarders get the cruiser going: a damaged cruiser with a thin crew joins the side beside the fleet that held it.</summary>
    private static void Board(Simulation sim, Prize p, Side side)
    {
        var w = sim.World;
        var holder = There(w, p, side).First();
        var ship = Economy.NewShip(w, side, "cruiser", Economy.ShipyardOf(w, side));
        ship.BuiltHour = w.Hour;
        Crews.Man(w, ship, null);
        var name = new[] { "Long Watch", "Quiet Answer", "Old Promise", "Far Lantern", "Patient Hand" }
            .FirstOrDefault(n => !ShipNames.Taken(w, n)) ?? ShipNames.Bare(ship.Name);
        ship.Name = $"Prize {name}";
        ship.Armour *= D.DerelictArmour;
        ship.Supplies *= D.DerelictSupplies;
        ship.Missiles = 0;
        if (Crews.Crewed(ship)) ship.Crew = War.D.Boarding.PrizeCrew;
        var fleet = Fleets.Create(w, side, new[] { ship }, holder.Position + new Vec2(3, 3));
        fleet.Hold = HoldStyle.Drift;
        fleet.Velocity = holder.Velocity;
        fleet.Rule = PullRule.For(PullPreset.Cautious);
        FleetNames.Refresh(w, fleet);
        foreach (var s in w.ShipsOf(fleet)) s.Position = fleet.Position + s.FormationOffset;
        Economy.Of(w, side).Totals.ShipsCaptured++;
        p.Holder = side;
        Settle(sim, p, $"{Chronicle.Who(w, side)} boarded the derelict cruiser: {ship.Name} joins {(side == Side.Player ? "your" : "their")} side");
        sim.Log.Info("prizes", "Derelict boarded", new { prizeId = p.Id, side = side.ToString(), shipId = ship.Id, fleetId = fleet.Id, by = holder.Id });
    }

    private static void TakeRelay(Simulation sim, Prize p, Side side)
    {
        var w = sim.World;
        var was = p.Holder;
        p.Holder = side;
        for (var k = 0; k < p.Hours.Length; k++) p.Hours[k] = 0;
        var text = was == null ? $"{Chronicle.Who(w, side)} took the old relay" : $"{Chronicle.Who(w, side)} took the old relay from {Sides.Label(w, was.Value)}";
        p.Outcome = text;
        p.OutcomeHour = w.Hour;
        w.Chronicle.Moments.Add(new Moment(w.Hour, "prize", text, side, 3));
        sim.Log.Info("prizes", "Relay taken", new { prizeId = p.Id, side = side.ToString(), was = was?.ToString() });
    }

    private static void Close(Simulation sim, Prize p)
    {
        var w = sim.World;
        if (p.Kind == PrizeKind.Comet)
        {
            var best = Sides.InPlay(w).Where(s => p.Mined[(int)s] > 0).OrderByDescending(s => p.Mined[(int)s]).ThenBy(s => (int)s).ToList();
            p.Holder = best.Count > 0 ? best[0] : null;
            var parts = best.Select(s => $"{Chronicle.Who(w, s)} {p.Mined[(int)s]:0}");
            Settle(sim, p, best.Count == 0 ? "The comet passed with nobody mining it" : $"The comet has passed: mined {string.Join(", ", parts)}");
        }
        else Settle(sim, p, "The derelict cruiser drifted off into the dark before anyone boarded it");
        sim.Log.Info("prizes", "Prize gone", new { prizeId = p.Id, kind = p.Kind.ToString(), holder = p.Holder?.ToString() });
    }

    private static void Settle(Simulation sim, Prize p, string text)
    {
        var w = sim.World;
        p.Over = true;
        p.Outcome = text;
        p.OutcomeHour = w.Hour;
        p.WorkedBy = null;
        p.Contested = false;
        w.Chronicle.Moments.Add(new Moment(w.Hour, "prize", text, p.Holder, p.Holder != null ? 3 : 1));
    }

    /// <summary>The relay is an extra pair of eyes for its holder.</summary>
    public static IEnumerable<(Vec2 At, double Sensor, int? Body)> Observers(World w, Side side)
    {
        foreach (var p in w.Prizes)
            if (p.Kind == PrizeKind.Relay && p.Holder == side && p.Open(w))
                yield return (p.At, D.RelaySensorMm * Tech.SensorReach(w, side), null);
    }

    // ---- What a side sees -------------------------------------------------------------------------

    /// <summary>Enemy fleets this side has seen at the prize lately (its own sightings, so no cheating).</summary>
    public static List<Sighting> SeenThere(World w, Prize p, Side side, double withinHours = 12) =>
        Sensors.Known(w, seat: true).Where(s => s.SeenBy == side && !s.Own && s.Of != side && w.Hour - s.Hour <= withinHours
                                               && Vec2.Distance(s.Guess(w.Hour), p.At) <= D.ReachMm * 2).OrderBy(s => s.FleetId).ToList();

    /// <summary>The prize's state as this side knows it, one line: "opens in 3 d", "boarding 40 %", "contested", "held by the rival".</summary>
    public static string Status(World w, Prize p, Side side, bool times = true)
    {
        if (p.Over) return p.Outcome;
        if (!p.Announced) return "";
        var mine = p.Hours[(int)side];
        var need = NeedHours(p);
        if (p.Coming(w)) return $"opens in {Hours(p.OpenHour - w.Hour)}";
        var here = There(w, p, side).Any();
        var seen = SeenThere(w, p, side);
        var foe = seen.Count > 0 ? $"{Sides.Them(w, seen[0].Of)} ships seen there ({seen.Sum(s => s.Ships)})" : "";
        if (here && p.Contested) return $"contested: your warships and {(seen.Count > 0 ? Sides.Them(w, seen[0].Of) + "'s" : "the enemy's")} are both there, so nobody gains";
        var left = times && p.CloseHour is double c ? $" · gone in {Hours(c - w.Hour)}" : "";
        switch (p.Kind)
        {
            case PrizeKind.Derelict:
                if (here) return $"boarding: {mine / need:P0} ({mine / 24:0.#} of {need / 24:0} days){left}";
                return (mine > 0 ? $"your boarding {mine / need:P0} done; nobody of yours there" : "no warships of yours there") + (foe.Length > 0 ? $" · {foe}" : "") + left;
            case PrizeKind.Comet:
                if (here) return $"mining: {D.CometFuelPerDay:0} fuel, {D.CometWaterPerDay:0} water, {D.CometHelium3PerDay:0} helium-3 a day to your shipyard ({p.Mined[(int)side]:0} so far){left}";
                return (p.Mined[(int)side] > 0 ? $"you mined {p.Mined[(int)side]:0}; nobody of yours there" : "no warships of yours there") + (foe.Length > 0 ? $" · {foe}" : "") + left;
            default:
                var held = p.Holder == side ? "yours: wide sensor cover round it" : p.Holder is Side h ? $"held by {Sides.Them(w, h)}" : "nobody holds it";
                if (here && p.Holder != side) return $"{held} · taking it: {mine / need:P0} ({mine / 24:0.#} of {need / 24:0} days)";
                return held + (mine > 0 && p.Holder != side ? $" · your taking {mine / need:P0} done" : "") + (foe.Length > 0 ? $" · {foe}" : "");
        }
    }

    /// <summary>For the fleet panel: the prize this fleet is at or flying to, and where the work on it stands.</summary>
    public static string? FleetStatus(World w, Fleet f)
    {
        if (!On(w)) return null;
        if (At(w, f) is Prize here)
            return here.Coming(w) ? $"Waiting at the {here.Name.ToLowerInvariant()}: it opens in {Hours(here.OpenHour - w.Hour)}"
                : !Combat.Armed(w, f) ? $"At the {here.Name.ToLowerInvariant()}, but unarmed ships don't count: send warships"
                : f.Retreating ? $"Leaving the {here.Name.ToLowerInvariant()}: retreating ships don't count"
                : $"{here.Name}: {Status(w, here, f.Side)}";
        if (f.Flight is Flight fl && fl.TargetBodyId == null && fl.TargetFleetId == null && Near(w, fl.Target, D.ReachMm) is Prize going)
            return $"Heading for the {going.Name.ToLowerInvariant()}" + (going.Coming(w) ? $" (opens in {Hours(going.OpenHour - w.Hour)})" : "")
                   + (!Combat.Armed(w, f) ? ": unarmed ships won't count there" : "");
        return null;
    }

    /// <summary>Short words for what holding it gives, for the banner while it is coming.</summary>
    public static string Short(PrizeKind k) => k switch
    {
        PrizeKind.Derelict => $"hold it {D.BoardDays:0} days with warships, no enemy there, to board and keep a cruiser",
        PrizeKind.Comet => "hold it with warships to mine fuel, water and helium-3 while it passes",
        _ => $"hold it {D.RelayTakeDays:0} days to take it: wide sensor cover for whoever holds it",
    };

    private static string Hours(double h)
    {
        h = Math.Max(0, h);
        return h < 24 ? $"{Math.Ceiling(h):0} h" : $"{Math.Floor(h / 24):0} d {Math.Floor(h % 24):0} h";
    }

    /// <summary>For the after-game screen: each prize and what came of it, with the hours each side put in.</summary>
    public static string Summary(World w)
    {
        var shown = w.Prizes.Where(p => p.Announced).ToList();
        if (shown.Count == 0) return "";
        string Line(Prize p)
        {
            var work = string.Join(", ", Sides.InPlay(w).Where(s => p.Hours[(int)s] > 0).Select(s => $"{Chronicle.Who(w, s)} {p.Hours[(int)s] / 24:0.#} d"));
            var end = p.Over || p.Holder != null ? p.Outcome : p.Open(w) ? "still open at the end" : "never opened";
            return $"{p.Name} (day {p.OpenHour / 24:0}): {end}" + (p.Kind != PrizeKind.Relay && work.Length > 0 ? $" · time there alone: {work}" : "");
        }
        return "Contested prizes:\n" + string.Join("\n", shown.Select(Line));
    }

    /// <summary>For F8 reports and batch runs: each prize, when and where, and what came of it.</summary>
    public static string Describe(World w) =>
        "# Contested prizes\n\n" + (w.Prizes.Count == 0 ? "None this game.\n" : string.Join("\n", w.Prizes.Select(p =>
            $"- {p.Name}: opens day {p.OpenHour / 24:0.#}{(p.CloseHour is double c ? $", gone day {c / 24:0.#}" : "")}; "
            + (p.Announced ? $"at {p.At}; " : "not announced yet; ")
            + $"hours worked {string.Join("/", Sides.InPlay(w).Select(s => $"{s} {p.Hours[(int)s]:0}"))}; "
            + (p.Over || p.Holder != null ? p.Outcome : "under way")))) + "\n";
}
