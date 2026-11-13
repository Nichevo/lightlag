using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>How an invasion is fought: Blitz (fast, storms everything), Methodical (spares mines and labs), Starve (ring the farms and wait).</summary>
public enum GroundPlan { Blitz, Methodical, Starve }

/// <summary>
/// An invasion drawn on the planet's board: every tile is held by the attacker ('A') or the defender ('D')
/// (mountains 'm' are nobody's); the front is where they touch. Buildings are taken intact, damaged or
/// wrecked as the front passes, depending on the plan and the calls.
/// </summary>
public sealed class GroundFront
{
    public Side Side { get; set; }
    public GroundPlan Plan { get; set; } = GroundPlan.Methodical;
    public bool PlanByHand { get; set; }
    public string Tiles { get; set; } = "";
    public int LandX { get; set; }
    public int LandY { get; set; }
    public int FleetId { get; set; }
    public double StartHour { get; set; }
    public double StartTroops { get; set; }
    public double StartGarrison { get; set; }
    public double Militia { get; set; }

    /// <summary>Strength factors from the calls (attackers), and what the calls settled.</summary>
    public double Edge { get; set; } = 1;
    public bool GunsSilenced { get; set; }
    public bool PushForGuns { get; set; }
    public bool SpareAll { get; set; }
    public bool Surround { get; set; }
    public bool Storm { get; set; }

    /// <summary>Buildings already passed by the front (piece ids), how each came out, and counts.</summary>
    public List<int> Passed { get; set; } = new();
    public List<int> Damaged { get; set; } = new();
    public int Wrecked { get; set; }
    public int LabsTaken { get; set; }

    public List<string> CallsDone { get; set; } = new();
    public List<WarCall> Calls { get; set; } = new();
    public List<string> Notes { get; set; } = new();

    public char At(Board b, int x, int y) => x < 0 || y < 0 || x >= b.W || y >= b.H || Tiles.Length != b.W * b.H ? 'm' : Tiles[y * b.W + x];
}

/// <summary>
/// Ground invasions: troops land in a landing zone (planet guns shoot at them), break out and push the
/// front tile by tile. The fight itself is a formula (troops against garrison and militia, each wearing
/// the other down every hour); the board shows it. Buildings matter: barracks and bunkers hold out,
/// guns shoot landers, shield domes block orbital strikes, habitats raise militia, farms feed the
/// defenders, power plants run nearby defences, labs give research scraps if taken intact. Up to three
/// calls come up: answer the guns, spare the mines, storm or surround the last stand. Auto makes them
/// unless the player asks to (the Ground war calls area). A planet under blockade has a weaker garrison
/// each week, so blockade and invasion both work: blockade is slow and cheap, invasion fast and costly.
/// </summary>
public static class Ground
{
    private static GroundData D => War.D.Ground;

    public static string PlanName(GroundPlan p) => p switch { GroundPlan.Blitz => "Blitz", GroundPlan.Starve => "Starve them", _ => "Methodical" };

    public static string PlanWhat(GroundPlan p) => p switch
    {
        GroundPlan.Blitz => "Fast, about 3 days: storms everything, many buildings damaged, unhappy planet after",
        GroundPlan.Starve => "Ring the farms and wait: slowest, almost no damage, happiest planet after",
        _ => "About 6 days: spares mines and labs, fewer losses",
    };

    private static BuildingData? Kind(Piece p) => Planets.BuildingKind(p.Kind);

    private static bool IsPower(BuildingData d) => d.Power < 0;
    private static bool IsLastStand(BuildingData d) => d.Id is "barracks" or "bunker" || d.HousingK > 0;
    private static bool IsSpared(BuildingData d) => d.Area is "industry" or "research" || d.Mines > 0;

    /// <summary>Militia the people raise from their homes: more with more housing and happier people.</summary>
    public static double MilitiaOf(Body b) =>
        Colonies.Built(b).Sum(p => Kind(p)?.HousingK ?? 0) * D.MilitiaPerHousingK * Math.Clamp(b.Happiness / 60, 0.2, 1.6);

    /// <summary>A blockade wears the garrison down: weaker each week, down to the weakest share.</summary>
    public static double BlockadeFactor(Body b) => Math.Max(D.WeakestShare, 1 - D.WeekWeakens * Math.Floor(b.BlockadeHours / (7 * 24)));

    /// <summary>Is this defence building dark (a power plant near it is in the attacker's hands)?</summary>
    private static bool Dark(Body b, GroundFront f, Piece p) =>
        b.Board is Board board && Colonies.Built(b).Any(o => Kind(o) is BuildingData d && IsPower(d) && Held(board, f, o) == 'A'
            && Math.Abs(o.X + o.W / 2.0 - (p.X + p.W / 2.0)) <= 3 && Math.Abs(o.Y + o.H / 2.0 - (p.Y + p.H / 2.0)) <= 3);

    /// <summary>Who holds a piece: 'A' all its tiles, 'D' all, 'c' contested.</summary>
    public static char Held(Board board, GroundFront f, Piece p)
    {
        int a = 0, n = 0;
        for (var y = p.Y; y < p.Y + p.H; y++)
        for (var x = p.X; x < p.X + p.W; x++)
        {
            n++;
            if (f.At(board, x, y) == 'A') a++;
        }
        return a == n ? 'A' : a == 0 ? 'D' : 'c';
    }

    /// <summary>Guns still shooting at landers: built, held by the defender, not dark, not silenced.</summary>
    public static double GunsOn(Body b, GroundFront? f) =>
        f is { GunsSilenced: true } ? 0
        : Colonies.Built(b).Where(p => Kind(p)?.GunDamage > 0 && (f == null || b.Board == null || Held(b.Board, f, p) != 'A' && !Dark(b, f, p))).Sum(p => Kind(p)!.GunDamage);

    private static int BunkersHeld(Body b, GroundFront f) =>
        b.Board is Board board ? Colonies.Built(b).Count(p => p.Kind == "bunker" && Held(board, f, p) == 'D' && !Dark(b, f, p)) : 0;

    /// <summary>The plan Auto picks: Blitz if enemy relief could arrive soon, Starve with too few troops to storm and farms to ring, else Methodical.</summary>
    public static GroundPlan AutoPlan(World w, Side side, Body b, double troops)
    {
        var garrison = Planets.GarrisonOf(b);
        if (ReliefNear(w, side, b.Position, 4 * 24)) return GroundPlan.Blitz;
        if (troops < garrison * 1.3 && Colonies.Built(b).Any(p => Kind(p)?.Food > 0)) return GroundPlan.Starve;
        return GroundPlan.Methodical;
    }

    /// <summary>Could armed enemy fleets this side knows of reach this spot within the hours given?</summary>
    public static bool ReliefNear(World w, Side side, Vec2 at, double hours) =>
        w.Sightings.Any(s => s.SeenBy == side && !s.Own && w.Hour - s.Hour < 48 && w.FindFleet(s.FleetId) is Fleet f && f.Side != side && f.ShipIds.Count > 0
                             && Combat.Armed(w, f) && !w.ShipsOf(f).All(Boarders.Helpless)
                             && Vec2.Distance(s.Position, at) <= hours * w.ShipsOf(f).Min(x => x.MaxSpeed) * 0.6);

    // ---- Landing --------------------------------------------------------------------------------

    /// <summary>Troops land: guns take their shot, a front starts on the board (or more troops join it).</summary>
    public static void Land(Simulation sim, Fleet fleet, Body b, double troops, GroundPlan? plan)
    {
        var w = sim.World;
        var side = fleet.Side;
        var guns = GunsOn(b, b.Front?.Side == side ? b.Front : null);
        var shotDown = Math.Min(troops * 0.5, guns * D.LandingLossPerGun);
        troops -= shotDown;
        Economy.Of(w, side).Totals.TroopsLostK += shotDown;
        if (b.LandingSide == side && b.Front != null)
        {
            b.LandingTroops += troops;
            b.Front.Notes.Add($"{troops * 1000:0} more troops land" + (shotDown > 0 ? $" ({shotDown * 1000:0} lost to the guns)" : ""));
            sim.Log.Info("ground", $"More {side} troops land on {b.Name}", new { bodyId = b.Id, troops = Math.Round(troops, 3), shotDown = Math.Round(shotDown, 3) });
            return;
        }
        var militia = MilitiaOf(b);
        b.LandingSide = side;
        b.LandingTroops = troops;
        b.Garrison = Planets.GarrisonOf(b);
        var auto = War.AutoCalls(w, side, AutoArea.Ground, b.Id, fleet.Id);
        var front = new GroundFront
        {
            Side = side, FleetId = fleet.Id, StartHour = w.Hour, StartTroops = troops, StartGarrison = Math.Max(1e-6, b.Garrison), Militia = militia,
            Plan = plan ?? AutoPlan(w, side, b, troops), PlanByHand = plan != null,
        };
        b.Front = front;
        if (b.Board is Board board)
        {
            var tiles = new char[board.W * board.H];
            for (var y = 0; y < board.H; y++)
            for (var x = 0; x < board.W; x++)
                tiles[y * board.W + x] = board.At(x, y) == 'm' ? 'm' : 'D';
            var (lx, ly) = LandingZone(b, board);
            front.LandX = lx;
            front.LandY = ly;
            tiles[ly * board.W + lx] = 'A';
            front.Tiles = new string(tiles);
            // A mass driver or spaceport near the landing makes the breakout quicker.
            var port = Colonies.Built(b).Any(p => p.Kind == "mass-driver" && Math.Abs(p.X - lx) <= 2 && Math.Abs(p.Y - ly) <= 2);
            for (var i = 0; i < (port ? 3 : 1); i++) TakeTile(sim, b, front, board);
        }
        front.Notes.Add($"{troops * 1000:0} troops land" + (shotDown > 0 ? $"; the guns shoot down {shotDown * 1000:0}" : "")
                        + $" against {b.Garrison * 1000:0} defenders ({militia * 1000:0} of them militia). Plan: {PlanName(front.Plan)}"
                        + (front.PlanByHand ? "" : " (Auto's pick)"));
        Economy.Of(w, side).Totals.Invasions++;
        Choices.Note(w, side, "invasion", b.Name, front.Plan.ToString(), $"{troops:0.##}k v {b.Garrison:0.##}k");
        if (guns > 0) AddCall(w, b, front, "guns", auto);
        sim.Log.Info("ground", $"{side} lands troops on {b.Name}", new { bodyId = b.Id, troops = Math.Round(troops, 3), garrison = Math.Round(b.Garrison, 3),
            plan = front.Plan.ToString(), shotDown = Math.Round(shotDown, 3) });
    }

    /// <summary>Where troops come down: next to a mass driver if there is one, else open land far from bunkers and barracks.</summary>
    private static (int X, int Y) LandingZone(Body b, Board board)
    {
        var strong = Colonies.Built(b).Where(p => p.Kind is "bunker" or "barracks" or "guns" or "missile-silo").ToList();
        var driver = Colonies.Built(b).FirstOrDefault(p => p.Kind == "mass-driver");
        (int X, int Y)? best = null;
        var bestScore = double.NegativeInfinity;
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
        {
            if (board.At(x, y) == 'm') continue;
            var covered = b.Pieces.Any(p => p.Covers(x, y));
            var score = covered ? -100 : 0;
            if (driver != null) score -= Math.Abs(driver.X - x) + Math.Abs(driver.Y - y);
            else score += strong.Count == 0 ? -(Math.Abs(x - board.W / 2) + Math.Abs(y - board.H / 2)) : strong.Min(p => Math.Abs(p.X - x) + Math.Abs(p.Y - y));
            if (score > bestScore) { bestScore = score; best = (x, y); }
        }
        return best ?? (0, 0);
    }

    // ---- The front -----------------------------------------------------------------------------

    /// <summary>Order the front takes tiles in: what the plan and calls go for first; the last stand last.</summary>
    private static int Priority(Body b, GroundFront f, int x, int y)
    {
        var p = b.Pieces.FirstOrDefault(p => p.Built && p.Covers(x, y));
        if (p == null || Kind(p) is not BuildingData d) return 1;
        if (f.Plan == GroundPlan.Starve && d.Food > 0) return 0;
        if (f.PushForGuns && d.GunDamage > 0) return 0;
        if (IsPower(d)) return 1; // take the power and the defences go dark
        if (IsLastStand(d)) return 4;
        if (d.Area == "defence") return 3;
        return 2;
    }

    /// <summary>The attacker takes one more tile on the front; buildings fully taken are passed (intact, damaged or wrecked).</summary>
    private static bool TakeTile(Simulation sim, Body b, GroundFront f, Board board)
    {
        (int X, int Y)? pick = null;
        var best = (int.MaxValue, int.MaxValue);
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
        {
            if (f.At(board, x, y) != 'D') continue;
            if (f.At(board, x - 1, y) != 'A' && f.At(board, x + 1, y) != 'A' && f.At(board, x, y - 1) != 'A' && f.At(board, x, y + 1) != 'A') continue;
            var key = (Priority(b, f, x, y), Math.Abs(x - f.LandX) + Math.Abs(y - f.LandY));
            if (key.CompareTo(best) < 0) { best = key; pick = (x, y); }
        }
        if (pick is not var (px, py))
        {
            // Nothing touches the front (an island of tiles): jump to the nearest defender tile.
            for (var y = 0; y < board.H && pick == null; y++)
            for (var x = 0; x < board.W && pick == null; x++)
                if (f.At(board, x, y) == 'D') pick = (x, y);
            if (pick is not var (qx, qy)) return false;
            (px, py) = (qx, qy);
        }
        var chars = f.Tiles.ToCharArray();
        chars[py * board.W + px] = 'A';
        f.Tiles = new string(chars);
        foreach (var p in Colonies.Built(b).Where(p => !f.Passed.Contains(p.Id) && p.Covers(px, py) && Held(board, f, p) == 'A').ToList())
            Pass(sim, b, f, p);
        return true;
    }

    /// <summary>The front passes a building: Methodical (or "spare them") keeps mines and labs intact; otherwise the plan's damage.</summary>
    private static void Pass(Simulation sim, Body b, GroundFront f, Piece p)
    {
        f.Passed.Add(p.Id);
        if (Kind(p) is not BuildingData d) return;
        var spared = IsSpared(d) && (f.Plan == GroundPlan.Methodical || f.SpareAll);
        var damage = spared ? 0 : D.PlanDamage[(int)f.Plan] * (f.Storm && IsLastStand(d) ? 1.5 : 1);
        if (d.Research > 0 && damage < 0.3) f.LabsTaken++;
        if (damage <= 0) return;
        var roll = sim.Rng.NextDouble();
        if (roll < damage * 0.4 && d.Id != Colonies.Shipyard)
        {
            Colonies.RemoveOne(b, x => x == p);
            f.Wrecked++;
            f.Notes.Add($"The {d.Name.ToLowerInvariant()} is wrecked in the fighting");
            Economy.Of(sim.World, f.Side).Totals.InvasionBuildingsLost++;
        }
        else if (roll < damage)
        {
            p.Condition = Math.Max(0.05, p.Condition - 0.5);
            f.Damaged.Add(p.Id);
        }
    }

    // ---- Calls --------------------------------------------------------------------------------

    private static void AddCall(World w, Body b, GroundFront f, string kind, bool auto)
    {
        if (f.CallsDone.Contains(kind) || f.CallsDone.Count >= 3) return;
        f.CallsDone.Add(kind);
        var fleet = w.FindFleet(f.FleetId);
        var missiles = fleet?.Side == f.Side ? w.ShipsOf(fleet).Sum(s => s.Missiles) : 0;
        var domed = Colonies.Built(b).Any(p => p.Kind == "shield-dome");
        f.Calls.Add(kind switch
        {
            "guns" => War.Call(w, kind, $"Guns on {b.Name} are shooting at the landers", missiles >= D.StrikeMissiles ? 0 : 1, auto, 12,
                ("Orbital strike", $"{D.StrikeMissiles} missiles from the fleet, {D.StrikeHappiness:0} happiness, guns wrecked" + (domed ? " (a shield dome covers some)" : "")),
                ("Push troops in", "Slower; the guns are taken intact"),
                ("Ignore", "Lose some troops every day while the guns stand")),
            "spare" => War.Call(w, kind, $"The front reaches the mines and labs of {b.Name}", f.Plan == GroundPlan.Blitz ? 1 : 0, auto, 12,
                ("Spare them", "Taken intact; the push is a little slower"),
                ("Take them fast", "Damaged in the fighting")),
            _ => War.Call(w, kind, $"The last defenders of {b.Name} hold out in their strongholds",
                f.Plan == GroundPlan.Blitz || ReliefNear(w, f.Side, b.Position, 3 * 24) ? 0 : 1, auto, 12,
                ("Storm them", "Quick, costly; the strongholds are damaged"),
                ("Surround and wait", "Slow; they starve, few losses")),
        });
    }

    /// <summary>A call is made: its effect on the invasion.</summary>
    public static void Apply(Simulation sim, Body b, GroundFront f, WarCall c)
    {
        var w = sim.World;
        var pick = c.Picked ?? c.AutoPick;
        var label = c.Options[Math.Clamp(pick, 0, c.Options.Count - 1)];
        f.Notes.Add($"{(c.ByAuto ? "Auto chose" : "You chose")}: {label}");
        switch (c.Kind, pick)
        {
            case ("guns", 0):
                var fleet = w.FindFleet(f.FleetId);
                var left = D.StrikeMissiles;
                if (fleet != null)
                    foreach (var s in w.ShipsOf(fleet).Where(s => s.Missiles > 0).OrderByDescending(s => s.Missiles).ThenBy(s => s.Id))
                    {
                        var take = Math.Min(left, s.Missiles);
                        s.Missiles -= take;
                        left -= take;
                        if (left == 0) break;
                    }
                var domed = Colonies.Built(b).Any(p => p.Kind == "shield-dome");
                var guns = Colonies.Built(b).Where(p => Kind(p)?.GunDamage > 0).OrderBy(p => p.Id).ToList();
                foreach (var g in domed ? guns.Take((guns.Count + 1) / 2) : guns)
                {
                    Colonies.RemoveOne(b, x => x == g);
                    f.Wrecked++;
                    Economy.Of(w, f.Side).Totals.InvasionBuildingsLost++;
                }
                f.GunsSilenced = !domed;
                b.Happiness = Math.Max(0, b.Happiness + D.StrikeHappiness);
                b.LastStruckHour = w.Hour;
                break;
            case ("guns", 1): f.PushForGuns = true; f.Edge *= 0.9; break;
            case ("spare", 0): f.SpareAll = true; f.Edge *= 0.9; break;
            case ("laststand", 0): f.Storm = true; f.Edge *= 1.4; break;
            case ("laststand", _): f.Surround = true; f.Edge *= 0.5; break;
        }
        sim.Log.Info("ground", $"Ground call at {b.Name}: {label}", new { bodyId = b.Id, kind = c.Kind, pick, byAuto = c.ByAuto });
    }

    /// <summary>The player (or anyone) makes a waiting call.</summary>
    public static string? Choose(Simulation sim, Body b, int callId, int option)
    {
        if (b.Front is not GroundFront f) return "No invasion there";
        if (f.Calls.Find(c => c.Id == callId) is not WarCall c || !c.Open) return "No such call waiting";
        if (option < 0 || option >= c.Options.Count) return "No such option";
        c.Picked = option;
        Apply(sim, b, f, c);
        return null;
    }

    // ---- The hour -------------------------------------------------------------------------------

    /// <summary>Hourly: every invasion wears both sides down, moves its front, brings up its calls; partisans sabotage.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies.Where(b => b.LandingSide != null).ToList())
        {
            var side = b.LandingSide!.Value;
            if (b.Owner == side) { Clear(b); continue; }
            var f = b.Front ??= new GroundFront { Side = side, StartHour = w.Hour, StartTroops = b.LandingTroops, StartGarrison = Math.Max(1e-6, b.Garrison) };
            var plan = (int)f.Plan;
            var bunkers = BunkersHeld(b, f);
            // Dug-in defenders (the Hive) fight from tunnels and lose fewer.
            var defLoss = Math.Max(1 / D.BunkerHold, 1 - 0.25 * bunkers) * (b.Owner is Side holder ? Races.Of(w, holder).GroundDefence : 1);
            // Cut off from orbit (enemy warships above and none of ours), the troops fight harder to hold on.
            var cut = w.Fleets.Any(o => o.Side != side && Economy.InOrbitAt(o, b) && Combat.Armed(w, o))
                      && !w.Fleets.Any(o => o.Side == side && Economy.InOrbitAt(o, b) && Combat.Armed(w, o));
            var troops = b.LandingTroops - b.Garrison * D.LossPerHour * D.PlanLoss[plan] * (cut ? 1.5 : 1) * (f.Storm ? 1.5 : 1);
            var garrison = b.Garrison - b.LandingTroops * D.LossPerHour * D.PlanAttack[plan] * f.Edge * defLoss;
            // Guns left standing keep shooting at the landing zone; defenders with no farms left go hungry.
            var guns = f.PushForGuns ? GunsOn(b, f) * 0.3 : GunsOn(b, f);
            troops -= guns * D.GunLossPerDay / 24;
            var farms = Colonies.Built(b).Where(p => Kind(p)?.Food > 0).ToList();
            if (b.Board != null && (farms.Count == 0 && f.Plan == GroundPlan.Starve || farms.Count > 0 && farms.All(p => Held(b.Board, f, p) == 'A')))
                garrison -= b.Garrison * D.HungryPerDay / 24 * (f.Surround || f.Plan == GroundPlan.Starve ? 1.5 : 1);
            Economy.Of(w, side).Totals.TroopsLostK += Math.Max(0, b.LandingTroops - Math.Max(0, troops));
            b.LandingTroops = Math.Max(0, troops);
            b.Garrison = Math.Max(0, garrison);
            var auto = War.AutoCalls(w, side, AutoArea.Ground, b.Id, f.FleetId);
            if (b.Board is Board board)
            {
                var land = f.Tiles.Count(c => c != 'm');
                var progress = Math.Clamp(1 - b.Garrison / f.StartGarrison, 0, 1);
                var want = b.Garrison <= 1e-6 ? land : Math.Max(1, (int)Math.Ceiling(land * progress * 0.97));
                for (var i = 0; i < 3 && f.Tiles.Count(c => c == 'A') < want; i++) TakeTile(sim, b, f, board);
                var held = f.Tiles.Count(c => c == 'A');
                // Calls: the front reaches spared buildings; the last stand.
                if (f.Plan != GroundPlan.Methodical && Colonies.Built(b).Any(p => Kind(p) is BuildingData d && IsSpared(d) && Held(board, f, p) == 'c'))
                    AddCall(w, b, f, "spare", auto);
                if (held >= land * 0.7 && Colonies.Built(b).Any(p => Kind(p) is BuildingData d && IsLastStand(d) && Held(board, f, p) != 'A'))
                    AddCall(w, b, f, "laststand", auto);
            }
            foreach (var c in War.Due(w, f.Calls)) Apply(sim, b, f, c);
            if (b.Garrison <= 1e-6)
            {
                Won(sim, b, f);
            }
            else if (b.LandingTroops <= 1e-6)
            {
                Sparks.Fire(sim, side, "landing-failed");
                f.Notes.Add("The landing is beaten off");
                sim.Log.Info("planets", $"The landing on {b.Name} is beaten off", new { bodyId = b.Id, side = side.ToString() });
                Clear(b);
            }
        }
        Partisans(sim);
    }

    private static void Won(Simulation sim, Body b, GroundFront f)
    {
        var w = sim.World;
        var side = f.Side;
        var t = Economy.Of(w, side).Totals;
        var leftK = b.LandingTroops;
        var fightDays = (w.Hour - f.StartHour) / 24;
        var siegeDays = Math.Max(fightDays, b.BlockadeHours / 24);
        b.PopK *= 0.95;
        var loser = b.Owner;
        Planets.Take(sim, b, side, "troops took it");
        // The payoff of a careful invasion: a Blitz leaves a sullen planet, Methodical and Starve a content one.
        var start = D.PlanStartHappiness[(int)f.Plan];
        var baseHappiness = f.Plan == GroundPlan.Blitz ? Math.Min(b.Happiness, start) : start;
        b.Happiness = Math.Clamp(baseHappiness + D.PlanHappiness[(int)f.Plan], 0, 100);
        t.InvasionWins++;
        t.InvasionDays += (w.Hour - f.StartHour) / 24;
        t.InvasionHappiness += b.Happiness;
        CaptureTally.Add(t, f.Plan.ToString(), siegeDays, fightDays, b, f.Damaged.Count, f.Wrecked);
        for (var i = 0; i < f.LabsTaken; i++) Spoils.Scrap(sim, side, $"a research lab taken intact on {b.Name}", IntelSource.Capture, Intel.SalvagePoints, from: loser);
        Spoils.TakePrisoners(sim, side, Math.Min(f.StartGarrison * 0.2, 0.5), b.Name, loser);
        if (leftK < D.GarrisonStaysK)
        {
            b.PartisansUntilHour = w.Hour + D.PartisanDays * 24;
            b.PartisansAgainst = side;
        }
        Choices.Note(w, side, "invasion-won", b.Name, f.Plan.ToString(), $"{(w.Hour - f.StartHour) / 24:0.#} days, {f.Damaged.Count} damaged, {f.Wrecked} wrecked");
        sim.Log.Info("ground", $"{side} takes {b.Name} by invasion", new { bodyId = b.Id, days = Math.Round((w.Hour - f.StartHour) / 24, 1), plan = f.Plan.ToString(),
            damaged = f.Damaged.Count, wrecked = f.Wrecked, troopsLeft = Math.Round(leftK, 3) });
    }

    /// <summary>Partisans: for a while after an invasion with too few troops left to garrison, a building is sabotaged every few days.</summary>
    private static void Partisans(Simulation sim)
    {
        var w = sim.World;
        var every = (long)Math.Max(1, D.SabotageEveryDays * 24);
        if (w.Tick % every != 0) return;
        foreach (var b in w.Bodies.Where(b => b.PartisansAgainst != null).ToList())
        {
            if (w.Hour >= b.PartisansUntilHour || b.Owner != b.PartisansAgainst || b.PopK <= 0) { b.PartisansAgainst = null; continue; }
            var built = Colonies.Built(b).Where(p => p.Kind != Colonies.Shipyard).OrderBy(p => p.Id).ToList();
            if (built.Count == 0) continue;
            var p = built[sim.Rng.Range(0, built.Count)];
            p.Condition = Math.Max(0.05, p.Condition - 0.3);
            sim.Log.Info("ground", $"Partisans sabotage a {Kind(p)?.Name.ToLowerInvariant() ?? p.Kind} on {b.Name}", new { bodyId = b.Id, pieceId = p.Id });
        }
    }

    public static void Clear(Body b)
    {
        b.LandingSide = null;
        b.LandingTroops = 0;
        b.Front = null;
    }

    /// <summary>Pull the troops out: as many as fit go back up to the troop ships in orbit; the rest are lost.</summary>
    public static string? PullOut(Simulation sim, Body b, Side side)
    {
        var w = sim.World;
        if (b.LandingSide != side || b.Front == null) return "No landing of yours there";
        var troops = b.LandingTroops;
        foreach (var f in w.Fleets.Where(f => f.Side == side && Economy.InOrbitAt(f, b)).OrderBy(f => f.Id))
            foreach (var s in w.ShipsOf(f).Where(Planets.IsTroopShip).OrderBy(s => s.Id))
            {
                var put = Math.Min(troops, Planets.TroopHold(s) - s.CargoTroops);
                s.CargoTroops += put;
                troops -= put;
            }
        Economy.Of(w, side).Totals.TroopsLostK += troops;
        sim.Log.Info("ground", $"{side} pulls its troops out of {b.Name}", new { bodyId = b.Id, back = Math.Round(b.LandingTroops - troops, 3), lost = Math.Round(troops, 3) });
        Clear(b);
        return null;
    }

    /// <summary>Days of fighting left, roughly, at the present rate (null: it isn't moving).</summary>
    public static double? DaysLeft(World w, Body b)
    {
        if (b.Front is not GroundFront f || b.LandingSide == null) return null;
        var hours = w.Hour - f.StartHour;
        var done = 1 - b.Garrison / f.StartGarrison;
        if (hours < 6 || done <= 0.02) return null;
        return Math.Max(0, hours * (1 - done) / done / 24);
    }

    /// <summary>The attacker's chance, roughly: troops against garrison under the plan (for the screen).</summary>
    public static double WinChance(Body b)
    {
        if (b.Front is not GroundFront f) return 0;
        // Dug-in defenders (the Hive) lose fewer to the same attack.
        var a = b.LandingTroops * D.PlanAttack[(int)f.Plan] * f.Edge * Races.Get(b.PeopleRace).GroundDefence;
        var d = b.Garrison * D.PlanLoss[(int)f.Plan];
        // Square law: the side with more strength squared wins.
        return Math.Clamp(a * a / Math.Max(1e-12, a * a + d * d * 1.2), 0.02, 0.98);
    }
}

/// <summary>Change an invasion's plan (null: back to Auto's pick).</summary>
public sealed record SetGroundPlanCommand(Side Side, int BodyId, GroundPlan? Plan) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindBody(BodyId) is not Body b || b.Front is not GroundFront f || f.Side != Side)
        {
            sim.Log.Warn("ground", "Bad ground plan order", new { side = Side.ToString(), BodyId, Plan, Source });
            return;
        }
        f.Plan = Plan ?? Ground.AutoPlan(world, Side, b, b.LandingTroops);
        f.PlanByHand = Plan != null;
        f.Notes.Add($"Plan changed to {Ground.PlanName(f.Plan)}");
        sim.Log.Info("ground", $"Invasion of {b.Name}: plan {f.Plan}", new { side = Side.ToString(), BodyId, plan = f.Plan.ToString(), Source });
    }
}

/// <summary>Make a waiting call in an invasion.</summary>
public sealed record GroundCallCommand(Side Side, int BodyId, int CallId, int Option) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        var problem = world.FindBody(BodyId) is Body b && b.Front?.Side == Side ? Ground.Choose(sim, b, CallId, Option) : "No invasion of yours there";
        if (problem != null) sim.Log.Warn("ground", $"Can't make that call: {problem}", new { side = Side.ToString(), BodyId, CallId, Option, Source });
    }
}

/// <summary>Pull the troops off a planet (into troop ships in orbit, as many as fit).</summary>
public sealed record PullOutTroopsCommand(Side Side, int BodyId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => (Side, world.FindBody(BodyId)?.Position ?? Light.Seat(world, Side));

    public override void Apply(World world, Simulation sim)
    {
        var problem = world.FindBody(BodyId) is Body b ? Ground.PullOut(sim, b, Side) : "No such planet";
        if (problem != null) sim.Log.Warn("ground", $"Can't pull out: {problem}", new { side = Side.ToString(), BodyId, Source });
    }
}
