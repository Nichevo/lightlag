namespace Lightlag.Sim.Core;

/// <summary>
/// A fleet's role: which calls for help it answers by itself when it is free. None answers nothing;
/// Defence answers planets that see an enemy close; AntiPiracy answers raids on cargo runs and cargo
/// ships in trouble; Support answers every call, fleets in trouble included. Supply is the only role a
/// fleet of supply ships can have: when free it takes supplies to fleets of its side that run short.
/// </summary>
public enum FleetRole { None, Defence, AntiPiracy, Support, Supply }

/// <summary>How far a role fleet goes to answer a call.</summary>
public enum RoleRange { Near, Region, Anywhere }

/// <summary>What odds a role fleet wants (with any other fleets answering) before it goes: any, about even, or clearly stronger.</summary>
public enum RoleOdds { Any, Even, Stronger }

/// <summary>Why a call for help went out (one call can have several reasons).</summary>
[Flags]
public enum CallReason { None = 0, EnemyNear = 1, Raiding = 2, FleetThreatened = 4 }

/// <summary>A fleet's role rules: how far it goes, the odds it wants, whether it goes back after, and how worn it may be.</summary>
public sealed class RoleRules
{
    public RoleRange Range { get; set; } = RoleRange.Region;
    public RoleOdds Odds { get; set; } = RoleOdds.Even;

    /// <summary>Go back to its post (and its patrol or hold mission) when the call is over.</summary>
    public bool ReturnAfter { get; set; } = true;

    /// <summary>Stay put when armour or supplies are below this share (0 to 1).</summary>
    public double MinHealth { get; set; } = 0.5;

    public RoleRules Copy() => (RoleRules)MemberwiseClone();
}

/// <summary>
/// A call for help about one enemy fleet: raised by a planet that sees it close, by cargo runs it raids, or
/// by one of the side's fleets it threatens. The call lasts while the enemy is still seen there.
/// </summary>
public sealed class HelpCall
{
    public int Id { get; set; }
    public Side Side { get; set; }
    public CallReason Reasons { get; set; }

    /// <summary>The planet that called (EnemyNear), the run raided (Raiding), and the fleet threatened (FleetThreatened).</summary>
    public int? BodyId { get; set; }
    public int? FromBodyId { get; set; }
    public int? ToBodyId { get; set; }
    public int? FleetId { get; set; }

    public int EnemyFleetId { get; set; }
    public string EnemyName { get; set; } = "";
    public int EnemyShips { get; set; }

    /// <summary>Where the enemy was when the call was last brought up to date: where the call comes from.</summary>
    public Vec2 Position { get; set; }
    public double RaisedHour { get; set; }

    /// <summary>When a reason for the call was last seen (it ends a while after the last one).</summary>
    public double LastHour { get; set; }

    public double? OverHour { get; set; }
    public bool Open => OverHour == null;

    /// <summary>What the call says, e.g. "Pale sees Rival fleet 2 (3 ships) close by".</summary>
    public string Text { get; set; } = "";
}

/// <summary>
/// Calls for help and the fleets that answer them. Each hour, for sides without an AI mind, planets that
/// see an enemy fleet close, cargo runs being raided and fleets under threat raise a call; free fleets
/// whose role covers the call, and whose rules allow it (range, odds, wear), answer it once the call's
/// light reaches them. Enough fleets go to outgun the enemy, nearest first. When the call is over they
/// go back to their post. A direct order from command takes a fleet off its call (see Autonomy.Heard).
/// </summary>
public static class HelpCalls
{
    /// <summary>A planet calls when an enemy is seen this close (AU).</summary>
    public const double NearAu = 0.15;

    /// <summary>A fleet calls when an enemy it can't beat is this close (contact ranges).</summary>
    public const double ThreatContactRanges = 3;

    /// <summary>A call ends this long after its last reason was seen (hours).</summary>
    public const double EndAfterHours = 8;

    /// <summary>Answering fleets together aim for this much more strength than the enemy.</summary>
    public const double WantedRatio = 1.5;

    /// <summary>
    /// "In hand" (no pause needed) only when the role fleets clearly outgun the enemy: about an even fight
    /// still goes ahead within the rules, but it pauses and asks first.
    /// </summary>
    public const double InHandRatio = 1.5;

    /// <summary>Calls are kept this long after they end, for the fleet screen (hours).</summary>
    public const double KeepHours = 3 * 24;

    /// <summary>A sighting older than this is too old to call about (hours).</summary>
    public const double FreshHours = 3;

    private static double Au => Physics.MmPerAu;

    public static double RangeAu(RoleRange r) => r switch { RoleRange.Near => 0.3, RoleRange.Region => 1.0, _ => double.PositiveInfinity };

    public static double OddsRatio(RoleOdds o) => o switch { RoleOdds.Any => 0, RoleOdds.Even => 1.0, _ => 1.5 };

    public static string RoleName(FleetRole r) => r switch
    {
        FleetRole.Defence => "Planet defence",
        FleetRole.AntiPiracy => "Anti-piracy",
        FleetRole.Support => "Support",
        FleetRole.Supply => "Supply",
        _ => "No role",
    };

    public static string RoleHelp(FleetRole r) => r switch
    {
        FleetRole.Defence => "Answers planets that see an enemy fleet close",
        FleetRole.AntiPiracy => "Answers raids on cargo runs and cargo ships in trouble",
        FleetRole.Support => "Answers every call: planets, raids and fleets in trouble",
        FleetRole.Supply => "Takes fuel, ammunition and food to fleets in range that run short, then goes home to load",
        _ => "Answers no calls; only does what it is told",
    };

    public static string RangeName(RoleRange r) => r switch { RoleRange.Near => "Near (0.3 AU)", RoleRange.Region => "Region (1 AU)", _ => "Anywhere" };
    public static string OddsName(RoleOdds o) => o switch { RoleOdds.Any => "Any odds", RoleOdds.Even => "About even or better", _ => "Clearly stronger (1.5 to 1)" };

    /// <summary>A fleet made only of supply ships (tankers, freighters): it can't fight, so its only role is Supply.</summary>
    public static bool SupplyOnly(World w, Fleet f) => f.ShipIds.Count > 0 && w.ShipsOf(f).All(SupplyLines.IsSupplyShip);

    /// <summary>The roles this fleet can have: None or Supply for a fleet of supply ships, the fighting roles for the rest.</summary>
    public static IEnumerable<FleetRole> RolesFor(World w, Fleet f) =>
        SupplyOnly(w, f) ? new[] { FleetRole.None, FleetRole.Supply } : new[] { FleetRole.None, FleetRole.Defence, FleetRole.AntiPiracy, FleetRole.Support };

    /// <summary>The role a fleet keeps when its ships change (a split or a merge): Supply for supply ships, never for warships.</summary>
    public static FleetRole FitRole(World w, Fleet f, FleetRole wanted) =>
        RolesFor(w, f).Contains(wanted) ? wanted : SupplyOnly(w, f) ? (wanted == FleetRole.None ? FleetRole.None : FleetRole.Supply) : FleetRole.None;

    /// <summary>Does this role answer a call with these reasons?</summary>
    public static bool Covers(FleetRole role, CallReason reasons) => role switch
    {
        FleetRole.Defence => (reasons & CallReason.EnemyNear) != 0,
        FleetRole.AntiPiracy => (reasons & CallReason.Raiding) != 0,
        FleetRole.Support => reasons != CallReason.None,
        _ => false,
    };

    /// <summary>A sighting's fighting value as its side judges it: by the classes seen, else by ship count.</summary>
    public static double EnemyPower(Sighting s)
    {
        if (s.Classes.Count >= s.Ships && s.Ships > 0)
            return s.Classes.Sum(c => GameData.ShipClasses.TryGetValue(c, out var k) ? (k.Railguns == 0 && k.Missiles == 0 ? 0.1 : 1) * k.Cost : GameData.Ai.EnemyShipValue);
        return Autonomy.PowerOf(s);
    }

    /// <summary>The side's latest sighting of an enemy fleet.</summary>
    public static Sighting? SightingOf(World w, Side side, int enemyFleetId) =>
        w.Sightings.Find(s => s.SeenBy == side && !s.Own && s.FleetId == enemyFleetId);

    /// <summary>The open call about this enemy fleet, if any.</summary>
    public static HelpCall? CallAbout(World w, Side side, int enemyFleetId) =>
        w.HelpCalls.Find(c => c.Side == side && c.Open && c.EnemyFleetId == enemyFleetId);

    /// <summary>The fleets answering a call now.</summary>
    public static List<Fleet> Answering(World w, HelpCall c) =>
        w.Fleets.Where(f => f.Side == c.Side && f.AnsweringCallId == c.Id && f.ShipIds.Count > 0).OrderBy(f => f.Id).ToList();

    /// <summary>Hours for the call's light to reach a fleet (0 with light lag off).</summary>
    public static double HearHours(World w, HelpCall c, Fleet f) =>
        w.Light.On ? Vec2.Distance(c.Position, f.Position) / Light.MmPerHour(w) : 0;

    // ---- Each hour --------------------------------------------------------------------------

    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var side in Sides.InPlay(w))
        {
            FitRoles(sim, side);
            if (Mind.Of(w, side) != null) continue;
            Raise(sim, side);
            Supplies(sim, side);
            foreach (var f in w.Fleets.Where(f => f.Side == side && f.AnsweringCallId != null).OrderBy(f => f.Id).ToList())
                KeepAt(sim, f);
            foreach (var c in w.HelpCalls.Where(c => c.Side == side && c.Open).OrderBy(c => c.Id).ToList())
                Answer(sim, c);
        }
        w.HelpCalls.RemoveAll(c => c.OverHour is double end && w.Hour - end > KeepHours);
    }

    /// <summary>A fleet whose ships changed keeps only a role it can have (a supply ship split off a warfleet takes Supply).</summary>
    private static void FitRoles(Simulation sim, Side side)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.Role != FleetRole.None).OrderBy(f => f.Id))
        {
            var fit = FitRole(w, f, f.Role);
            if (fit == f.Role) continue;
            sim.Log.Info("fleet", "Fleet role changed to fit its ships", new { fleetId = f.Id, fleet = f.Name, from = f.Role.ToString(), to = fit.ToString() });
            if (f.AnsweringCallId != null) Dropped(f);
            f.Role = fit;
            FleetNames.Refresh(w, f);
        }
    }

    /// <summary>
    /// Supply-role fleets: a free one, loaded at a held planet, takes a supply run to the nearest fleet in range
    /// that runs short and has no tanker coming. When that fleet wants nothing more and the tanker is home, the run ends.
    /// </summary>
    private static void Supplies(Simulation sim, Side side)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.Role == FleetRole.Supply).OrderBy(f => f.Id).ToList())
        {
            var home = f.Flight == null && f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int b && w.FindBody(b)?.Owner == side;
            if (f.SupplyRunByRole && f.SupplyRunFor is int served)
            {
                if (home && (w.FindFleet(served) is not Fleet t || !SupplyLines.Needs(w, t)))
                {
                    f.SupplyRunFor = null;
                    f.SupplyRunByRole = false;
                    sim.Log.Info("supply", $"{f.Name} is free for the next fleet", new { fleetId = f.Id, served });
                }
                continue;
            }
            if (!home || !Free(w, f) || SupplyLines.CargoShare(w, f) < 0.95) continue;
            var range = RangeAu(f.RoleRules.Range) * Au;
            var want = w.Fleets.Where(t => t.Side == side && t.Id != f.Id && !SupplyOnly(w, t) && t.ShipIds.Count > 0
                                            && !w.Fleets.Any(x => x.SupplyRunFor == t.Id) && SupplyLines.Short(w, t)
                                            && Vec2.Distance(t.Position, f.Position) <= range)
                .OrderBy(t => Vec2.Distance(t.Position, f.Position)).ThenBy(t => t.Id).FirstOrDefault();
            if (want == null) continue;
            f.SupplyRunFor = want.Id;
            f.SupplyRunByRole = true;
            f.SupplyRunProblem = null;
            Autonomy.Report(sim, f, $"{f.Name} (Supply) takes supplies to {want.Name}", NoteKind.Supply);
        }
    }

    /// <summary>Raise or refresh calls from what the side sees now, and end calls whose reasons have passed.</summary>
    private static void Raise(Simulation sim, Side side)
    {
        var w = sim.World;
        var mine = w.Bodies.Where(b => b.Owner == side).OrderBy(b => b.Id).ToList();
        var fresh = w.Sightings.Where(s => s.SeenBy == side && !s.Own && w.Hour - s.Hour <= FreshHours && !Lanes.CargoSighting(s))
            .OrderBy(s => s.FleetId).ToList();
        var raids = Lanes.RaidersNearRuns(w, side).ToList();
        var threatRange = GameData.Combat.ContactRangeMm * ThreatContactRanges;

        foreach (var s in fresh)
        {
            var at = s.Guess(w.Hour);
            var reasons = CallReason.None;
            Body? planet = mine.Where(b => Vec2.Distance(at, b.Position) < NearAu * Au)
                .OrderBy(b => Vec2.Distance(at, b.Position)).ThenBy(b => b.Id).FirstOrDefault();
            if (planet != null) reasons |= CallReason.EnemyNear;

            (Body From, Body To)? run = null;
            foreach (var (e, a, b) in raids)
                if (e.FleetId == s.FleetId) { run = (a, b); reasons |= CallReason.Raiding; break; }
            // Cargo lost near it lately: it is raiding.
            if ((reasons & CallReason.Raiding) == 0
                && w.LossSpots.Any(l => l.Side == side && w.Hour - l.Hour < 24 && Vec2.Distance(l.At, at) < Lanes.LossRadiusMm))
                reasons |= CallReason.Raiding;

            Fleet? threatened = null;
            foreach (var f in w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0).OrderBy(f => Vec2.Distance(f.Position, at)).ThenBy(f => f.Id))
            {
                if (Vec2.Distance(f.Position, at) > threatRange) break;
                if (f.AnsweringCallId != null) continue; // a fleet sent to fight it is not in trouble from it
                var cargo = Lanes.CargoOnly(w, f);
                if (cargo || !Combat.Armed(w, f) || EnemyPower(s) > Stats.Power(w, f) * 0.8)
                {
                    threatened = f;
                    reasons |= CallReason.FleetThreatened;
                    if (cargo) reasons |= CallReason.Raiding; // a cargo ship in trouble: pirates
                    break;
                }
            }
            if (reasons == CallReason.None) continue;

            var call = CallAbout(w, side, s.FleetId);
            if (call == null)
            {
                call = new HelpCall { Id = w.NextId++, Side = side, EnemyFleetId = s.FleetId, RaisedHour = w.Hour };
                w.HelpCalls.Add(call);
            }
            var isNew = call.Reasons == CallReason.None;
            var added = reasons & ~call.Reasons;
            call.Reasons |= reasons;
            call.LastHour = w.Hour;
            call.Position = at;
            call.EnemyName = s.Name;
            call.EnemyShips = s.Ships;
            if (planet != null) call.BodyId = planet.Id;
            if (run is var (from, to)) { call.FromBodyId = from.Id; call.ToBodyId = to.Id; }
            if (threatened != null) call.FleetId = threatened.Id;
            call.Text = Describe(w, call);
            if (isNew || added != CallReason.None)
                sim.Log.Info("calls", isNew ? "Call for help" : "Call for help grows", new { callId = call.Id, side = side.ToString(), text = call.Text, reasons = call.Reasons.ToString() });
        }

        foreach (var c in w.HelpCalls.Where(c => c.Side == side && c.Open).OrderBy(c => c.Id))
        {
            var gone = SightingOf(w, side, c.EnemyFleetId) == null;
            if (!gone && w.Hour - c.LastHour < EndAfterHours) continue;
            c.OverHour = w.Hour;
            sim.Log.Info("calls", "Call for help over", new { callId = c.Id, side = side.ToString(), text = c.Text, gone });
        }
    }

    public static string Describe(World w, HelpCall c)
    {
        var enemy = $"{c.EnemyName} ({c.EnemyShips} ship{(c.EnemyShips == 1 ? "" : "s")})";
        var parts = new List<string>();
        if ((c.Reasons & CallReason.EnemyNear) != 0 && w.FindBody(c.BodyId ?? -1) is Body b) parts.Add($"{b.Name} sees {enemy} close by");
        if ((c.Reasons & CallReason.Raiding) != 0)
            parts.Add(w.FindBody(c.FromBodyId ?? -1) is Body a && w.FindBody(c.ToBodyId ?? -1) is Body z
                ? $"{enemy} is raiding the {a.Name}–{z.Name} run" : $"{enemy} is raiding cargo ships");
        if ((c.Reasons & CallReason.FleetThreatened) != 0 && w.FindFleet(c.FleetId ?? -1) is Fleet f) parts.Add($"{f.Name} is threatened by {enemy}");
        return parts.Count > 0 ? string.Join("; ", parts) : $"{enemy} seen";
    }

    /// <summary>Where a call is about, in a word or two (for news lines).</summary>
    public static string Where(World w, HelpCall c) =>
        w.FindBody(c.BodyId ?? -1) is Body b && (c.Reasons & CallReason.EnemyNear) != 0 ? b.Name
        : w.FindFleet(c.FleetId ?? -1) is Fleet f && (c.Reasons & CallReason.FleetThreatened) != 0 ? f.Name
        : w.FindBody(c.ToBodyId ?? -1) is Body t ? $"the run to {t.Name}" : "a cargo run";

    // ---- Answering --------------------------------------------------------------------------

    /// <summary>Free to answer a call: not flying an order, chasing, pulling back, refitting, supplying, carrying people or in a zone.</summary>
    public static bool Free(World w, Fleet f)
    {
        if (f.ShipIds.Count == 0 || f.AnsweringCallId != null || f.Retreating || f.Maneuvering || f.Chase != null || f.JoinFleetId != null) return false;
        if (f.Refit != null || f.Resupplying || f.SupplyRunFor != null || f.PeopleRun != null || f.ZoneId != null || f.RejoinFleetId != null) return false;
        if (f.Mission is not (Mission.None or Mission.Hold or Mission.Patrol)) return false;
        // A patrol between stops is still free; any other flight is an order being carried out.
        return f.Flight == null || f.Mission == Mission.Patrol;
    }

    /// <summary>
    /// Why this fleet would not answer this call now (null: it would, as far as its own rules go). Odds are
    /// left to the caller, since they depend on who else goes.
    /// </summary>
    public static string? WhyNot(World w, Fleet f, HelpCall c, bool checkFree = true)
    {
        if (f.Role == FleetRole.None) return "it has no role";
        if (!Covers(f.Role, c.Reasons)) return $"{RoleName(f.Role)} does not answer this kind of call";
        if (c.FleetId == f.Id) return "it is the fleet in trouble";
        if (checkFree && !Free(w, f)) return "it is busy";
        if (!Combat.Armed(w, f)) return "it has no working weapons";
        if (f.Stance is Stance.Avoid or Stance.ReturnHome) return "its contact order is to avoid fights";
        if (Vec2.Distance(f.Position, c.Position) > RangeAu(f.RoleRules.Range) * Au) return "it is out of range";
        if (Combat.ArmourShare(w, f) < f.RoleRules.MinHealth || Combat.SupplyShare(w, f) < f.RoleRules.MinHealth) return "it is too worn or low on supplies";
        if (w.Hour - c.RaisedHour < HearHours(w, c, f)) return "the call has not reached it yet";
        return null;
    }

    private static void Answer(Simulation sim, HelpCall c)
    {
        var w = sim.World;
        if (SightingOf(w, c.Side, c.EnemyFleetId) is not Sighting s) return;
        var enemy = EnemyPower(s);
        var going = Answering(w, c);
        var have = going.Sum(f => Stats.Power(w, f));
        var wanted = enemy * WantedRatio;
        if (have >= wanted) return;

        var picked = new List<Fleet>();
        var total = have;
        foreach (var f in w.Fleets.Where(f => f.Side == c.Side).OrderBy(f => Vec2.Distance(f.Position, c.Position)).ThenBy(f => f.Id))
        {
            if (WhyNot(w, f, c) != null) continue;
            picked.Add(f);
            total += Stats.Power(w, f);
            if (total >= wanted) break;
        }
        foreach (var f in picked)
        {
            // Each fleet's own odds rule, counting everyone going.
            if (total < enemy * OddsRatio(f.RoleRules.Odds)) continue;
            Go(sim, f, c, s);
        }
    }

    private static void Go(Simulation sim, Fleet f, HelpCall c, Sighting s)
    {
        var w = sim.World;
        f.PostBodyId = f.Hold == HoldStyle.Orbit && f.OrbitBodyId != null ? f.OrbitBodyId : f.MissionBodyId;
        f.PostMission = f.Mission;
        f.PostMissionBodyId = f.MissionBodyId;
        f.Mission = Mission.None;
        f.AnsweringCallId = c.Id;
        if (!Aim(sim, f, c, s))
        {
            Restore(f);
            f.AnsweringCallId = null;
            return;
        }
        Autonomy.Report(sim, f, $"{f.Name} ({RoleName(f.Role)}) answers a call for help: {c.Text}.", NoteKind.StandingOrder);
        Choices.Note(w, f.Side, "role", $"answered a call ({c.Reasons})", f.Role.ToString(), $"ships {f.ShipIds.Count}");
    }

    /// <summary>Go after the enemy (closing to gun range), else to the planet that called.</summary>
    private static bool Aim(Simulation sim, Fleet f, HelpCall c, Sighting s)
    {
        var w = sim.World;
        var at = s.Guess(w.Hour);
        Do(sim, new PlanFlightCommand(f.Id, new FlightRequest(null, at, 0.8, ArrivalStyle.Brake, HoldStyle.Drift, w.Tick, TargetFleetId: s.FleetId, Chase: ChaseMode.GunRange)));
        if (f.Flight != null || f.Chase != null) return true;
        if (w.FindBody(c.BodyId ?? -1) is Body b)
        {
            Do(sim, new PlanFlightCommand(f.Id, new FlightRequest(b.Id, b.Position, 0.8, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
            return f.Flight != null;
        }
        return false;
    }

    /// <summary>An answering fleet: keep after the enemy while the call lasts; when it is over (or the fleet pulled back), go back to its post.</summary>
    private static void KeepAt(Simulation sim, Fleet f)
    {
        var w = sim.World;
        var c = w.HelpCalls.Find(o => o.Id == f.AnsweringCallId);
        if (f.ShipIds.Count == 0) { f.AnsweringCallId = null; return; }
        if (f.Retreating)
        {
            // Its pull-back rule decided: it goes home; the call is someone else's now.
            f.AnsweringCallId = null;
            Restore(f);
            return;
        }
        if (c != null && c.Open)
        {
            // Lost the chase but the enemy is still about: aim again.
            if (f.Flight == null && f.Chase == null && !f.Maneuvering && SightingOf(w, f.Side, c.EnemyFleetId) is Sighting s && s.Visible)
                Aim(sim, f, c, s);
            return;
        }
        f.AnsweringCallId = null;
        if (f.Maneuvering) { Restore(f); return; }
        Restore(f);
        if (!f.RoleRules.ReturnAfter)
        {
            Autonomy.Report(sim, f, $"{f.Name}: the call is over; it stays where it is.", NoteKind.StandingOrder);
            return;
        }
        if (f.Mission == Mission.None && w.FindBody(f.PostBodyId ?? -1) is Body post)
        {
            if (f.Chase != null) f.Chase = null;
            Do(sim, new PlanFlightCommand(f.Id, new FlightRequest(post.Id, post.Position, 0.5, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        }
        else if (f.Mission != Mission.None)
        {
            f.Chase = null;
            if (f.Flight != null && !f.Flight.Started) f.Flight = null;
            Autonomy.Start(sim, f);
        }
        Autonomy.Report(sim, f, $"{f.Name}: the call is over; it goes back to {(w.FindBody(f.PostBodyId ?? f.MissionBodyId ?? -1)?.Name ?? "its post")}.", NoteKind.StandingOrder);
    }

    /// <summary>Put back the mission it had before it answered.</summary>
    private static void Restore(Fleet f)
    {
        if (f.PostMission == Mission.None) return;
        f.Mission = f.PostMission;
        f.MissionBodyId = f.PostMissionBodyId;
        f.MissionLeg = 0;
        f.MissionUntil = 0;
        f.PostMission = Mission.None;
    }

    /// <summary>A direct order from command: off the call, and the post is forgotten.</summary>
    public static void Dropped(Fleet f)
    {
        f.AnsweringCallId = null;
        f.PostMission = Mission.None;
        f.PostMissionBodyId = null;
    }

    private static void Do(Simulation sim, GameCommand command) => (command with { Source = "auto" }).Apply(sim.World, sim);

    // ---- For the screen ---------------------------------------------------------------------

    /// <summary>
    /// Is an enemy fleet in hand by a role fleet within its rules: answering a call about it already, or a
    /// free role fleet that would answer it now with good enough odds? The fleet if so (for "no pause").
    /// A fleet that meets something its odds rule would not take on, or a fight that is close to even,
    /// is not "in hand": that wants orders.
    /// </summary>
    public static Fleet? HandledBy(World w, Side side, int enemyFleetId)
    {
        if (SightingOf(w, side, enemyFleetId) is not Sighting s) return null;
        var enemy = EnemyPower(s);
        var call = CallAbout(w, side, enemyFleetId);
        if (call != null)
        {
            var going = Answering(w, call);
            var have = going.Sum(f => Stats.Power(w, f));
            if (going.Count > 0 && have >= enemy * Math.Max(InHandRatio, going.Max(f => OddsRatio(f.RoleRules.Odds)))) return going[0];
        }
        // Not called about yet (just seen): would a free role fleet take it?
        var probe = call ?? new HelpCall { Side = side, EnemyFleetId = enemyFleetId, Position = s.Guess(w.Hour), RaisedHour = double.NegativeInfinity, Reasons = GuessReasons(w, side, s) };
        if (probe.Reasons == CallReason.None) return null;
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.Role != FleetRole.None).OrderBy(f => Vec2.Distance(f.Position, probe.Position)).ThenBy(f => f.Id))
        {
            if (WhyNot(w, f, probe) != null) continue;
            var own = Stats.Power(w, f);
            if (own >= enemy * Math.Max(InHandRatio, OddsRatio(f.RoleRules.Odds))) return f;
        }
        return null;
    }

    /// <summary>
    /// The enemy fleets a battle is against are all in hand by role fleets within their rules, and every
    /// one of our fleets in it is a role fleet answering one of those calls: no need to pause for it.
    /// </summary>
    public static bool BattleInHand(World w, Battle b, Side side)
    {
        var ours = w.Fleets.Where(f => f.Side == side && b.For(side).Fleets.Contains(f.Name)).ToList();
        var theirs = w.Fleets.Where(f => f.Side != side && b.For(f.Side).Fleets.Contains(f.Name)).ToList();
        if (ours.Count == 0 || theirs.Count == 0) return false;
        if (ours.Any(f => f.AnsweringCallId == null && f.Role == FleetRole.None)) return false;
        return theirs.All(e => HandledBy(w, side, e.Id) != null);
    }

    private static CallReason GuessReasons(World w, Side side, Sighting s)
    {
        var at = s.Guess(w.Hour);
        var r = CallReason.None;
        if (w.Bodies.Any(b => b.Owner == side && Vec2.Distance(at, b.Position) < NearAu * Au)) r |= CallReason.EnemyNear;
        if (Lanes.RaidersNearRuns(w, side).Any(x => x.Enemy.FleetId == s.FleetId)
            || w.Fleets.Any(f => f.Side == side && Lanes.CargoOnly(w, f) && Vec2.Distance(f.Position, at) < GameData.Combat.ContactRangeMm * ThreatContactRanges))
            r |= CallReason.Raiding;
        if (w.Fleets.Any(f => f.Side == side && f.ShipIds.Count > 0 && Vec2.Distance(f.Position, at) < GameData.Combat.ContactRangeMm * ThreatContactRanges))
            r |= CallReason.FleetThreatened;
        return r;
    }
}

/// <summary>Give a fleet a role (which calls for help it answers by itself) and its rules.</summary>
public sealed record SetFleetRoleCommand(int FleetId, FleetRole Role, RoleRules? Rules = null) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Role") is not Fleet fleet) return;
        if (!HelpCalls.RolesFor(world, fleet).Contains(Role))
        {
            sim.Log.Warn("fleet", "Role does not fit the fleet", new { FleetId, fleet = fleet.Name, Role, supplyOnly = HelpCalls.SupplyOnly(world, fleet), Source });
            return;
        }
        if (fleet.Role == FleetRole.Supply && Role != FleetRole.Supply && fleet.SupplyRunByRole) { fleet.SupplyRunFor = null; fleet.SupplyRunByRole = false; }
        fleet.Role = Role;
        if (Rules != null) fleet.RoleRules = Rules.Copy();
        if (Role == FleetRole.None && fleet.AnsweringCallId != null) HelpCalls.Dropped(fleet);
        FleetNames.Refresh(world, fleet);
        sim.Log.Info("fleet", "Fleet role set", new { FleetId, fleet = fleet.Name, Role, Rules, Source });
    }
}

/// <summary>
/// Move some ships from one fleet to another of the same side at the same place (both stopped, close
/// together, as for a merge). Moving every ship merges the fleets.
/// </summary>
public sealed record MoveShipsCommand(int FleetId, List<int> ShipIds, int ToFleetId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Move ships") is not Fleet from) return;
        if (GetFleet(world, sim, ToFleetId, "Move ships") is not Fleet to) return;
        if (Fleets.CanMerge(to, from) is string problem)
        {
            sim.Log.Warn("fleet", "Cannot move ships between fleets", new { FleetId, ToFleetId, problem, Source });
            return;
        }
        var moving = ShipIds.Where(id => from.ShipIds.Contains(id)).Distinct().ToList();
        if (moving.Count == 0) return;
        if (moving.Count == from.ShipIds.Count)
        {
            Fleets.Absorb(world, to, from);
            sim.Log.Info("fleet", "All ships moved: fleets merged", new { FleetId, ToFleetId, Source });
            return;
        }
        foreach (var id in moving)
        {
            from.ShipIds.Remove(id);
            to.ShipIds.Add(id);
            if (world.FindShip(id) is Ship s) s.FleetId = to.Id;
        }
        Fleets.Arrange(world, from);
        Fleets.Arrange(world, to);
        sim.Log.Info("fleet", "Ships moved between fleets", new { FleetId, ToFleetId, ships = moving, Source });
    }
}
