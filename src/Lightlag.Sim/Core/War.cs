using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (Data/economy.json, "war") -----------------------------------------------------------

/// <summary>Crew ranks: what each is worth, and how crews gain and lose it (see Crews).</summary>
public sealed class CrewsData
{
    /// <summary>Experience at which each rank starts: Green, Trained, Seasoned, Veteran, Elite.</summary>
    public double[] RankXp { get; set; } = { 0, 1, 3, 6, 10 };
    /// <summary>Per rank: railgun hit chance, damage control pace, boarding strength.</summary>
    public double[] Aim { get; set; } = { 0.9, 1, 1.05, 1.1, 1.15 };
    public double[] Mend { get; set; } = { 0.7, 1, 1.1, 1.25, 1.4 };
    public double[] Boarding { get; set; } = { 0.8, 1, 1.1, 1.25, 1.5 };
    /// <summary>Experience from a battle fought, extra for one the other side left, and per hit landed.</summary>
    public double BattleXp { get; set; } = 1;
    public double WinXp { get; set; } = 0.5;
    /// <summary>Patrols and zones (out of port on a mission) teach up to Seasoned; an academy at the planet a fleet sits at teaches up to Trained.</summary>
    public double PatrolXpPerDay { get; set; } = 0.04;
    public double PatrolCap { get; set; } = 3;
    public double AcademyXpPerDay { get; set; } = 0.1;
    public double AcademyCap { get; set; } = 1;
    /// <summary>Auto spreads veterans: in port, a fleet whose best and worst crews are this many points apart evens a share of the gap.</summary>
    public double SpreadGap { get; set; } = 3;
    public double SpreadShare { get; set; } = 0.25;
    /// <summary>Veterans hold on longer: their ship rule's hull line is this much lower.</summary>
    public double VeteranHold { get; set; } = 0.75;

    /// <summary>
    /// Crew strain (0..1): each slug dodged adds StrainPerDodge (times HardBurnStrain in a hard burn, and up
    /// to the heat's close-range factor); it comes off at StrainRecoverPerHour out of battle, PortRecover
    /// times as fast in orbit at a planet of yours. A fully strained crew aims StrainAimCut worse, mends
    /// StrainMendCut slower and keeps only 1 - StrainDodgeCut of its dodge. AI-run ships feel none of it.
    /// </summary>
    public double StrainPerDodge { get; set; } = 0.0015;
    public double HardBurnStrain { get; set; } = 2.5;
    public double StrainRecoverPerHour { get; set; } = 0.01;
    public double PortRecover { get; set; } = 4;
    public double StrainAimCut { get; set; } = 0.25;
    public double StrainMendCut { get; set; } = 0.3;
    public double StrainDodgeCut { get; set; } = 0.6;
    /// <summary>Strain at which the bars and Needs you warn (the crew is worn out).</summary>
    public double StrainHigh { get; set; } = 0.6;

    /// <summary>An AI core takes this many electronics per 100 Industry of hull.</summary>
    public double AiCoreElectronicsPer100 { get; set; } = 2;
}

/// <summary>Boarding crippled ships (see Boarding).</summary>
public sealed class BoardingData
{
    public double RangeMm { get; set; } = 100;
    /// <summary>Share of each crewed warship's crew that goes over as a boarding party (troops aboard go first).</summary>
    public double PartyShare { get; set; } = 0.15;
    public double BreachHours { get; set; } = 4;
    public double DockHours { get; set; } = 6;
    public double DockEdge { get; set; } = 1.25;
    /// <summary>Boarding marines research: boarding parties fight this much harder.</summary>
    public double MarinesTech { get; set; } = 1.3;
    /// <summary>Share of the defenders who survive as prisoners when the ship is taken.</summary>
    public double PrisonerShare { get; set; } = 0.6;
    /// <summary>A prize flies with this much of its crew bar (a prize crew from the boarders).</summary>
    public double PrizeCrew { get; set; } = 0.3;
}

/// <summary>Ground invasions (see Ground).</summary>
public sealed class GroundData
{
    /// <summary>Each side wears the other down by this share of its strength an hour (before the plan).</summary>
    public double LossPerHour { get; set; } = 0.005;
    /// <summary>Per plan (Blitz, Methodical, Starve): how hard the attackers hit, how much they lose, damage to buildings taken, happiness after.</summary>
    public double[] PlanAttack { get; set; } = { 1.8, 1.0, 0.35 };
    public double[] PlanLoss { get; set; } = { 1.3, 0.9, 0.4 };
    public double[] PlanDamage { get; set; } = { 0.6, 0.15, 0.05 };
    public double[] PlanHappiness { get; set; } = { -10, 0, 3 };

    /// <summary>Happiness a captured planet starts at, per plan: a Blitz no happier than this, the others at it (before PlanHappiness).</summary>
    public double[] PlanStartHappiness { get; set; } = { 35, 50, 50 };
    /// <summary>Militia from habitats: k per k of housing, scaled by happiness.</summary>
    public double MilitiaPerHousingK { get; set; } = 0.02;
    /// <summary>Planet guns shoot at landers: troops (k) lost per point of gun damage when they land.</summary>
    public double LandingLossPerGun { get; set; } = 0.02;
    /// <summary>Unanswered guns keep shooting landers: troops (k) a day per point of gun damage.</summary>
    public double GunLossPerDay { get; set; } = 0.01;
    /// <summary>Bunkers hold their ground this many times as hard while their tiles are held.</summary>
    public double BunkerHold { get; set; } = 3;
    /// <summary>Defenders without farms (all taken) lose this share a day; blockades weaken a garrison by WeekWeakens a week (down to WeakestShare).</summary>
    public double HungryPerDay { get; set; } = 0.08;
    public double WeekWeakens { get; set; } = 0.08;
    public double WeakestShare { get; set; } = 0.4;
    /// <summary>Partisans after an invasion: days, and how often they sabotage a building (unless troops stay as garrison).</summary>
    public double PartisanDays { get; set; } = 20;
    public double SabotageEveryDays { get; set; } = 5;
    public double GarrisonStaysK { get; set; } = 0.3;
    /// <summary>An orbital strike: missiles taken from the fleet above, happiness lost below.</summary>
    public int StrikeMissiles { get; set; } = 6;
    public double StrikeHappiness { get; set; } = -3;
}

/// <summary>After a battle (see AfterBattle).</summary>
public sealed class AfterData
{
    public double ReachMm { get; set; } = 150;
    public double RescueHoursPerWreck { get; set; } = 2;
    public double SalvageHoursPerWreck { get; set; } = 4;
    /// <summary>Share of a lost ship's crew that gets off in pods, and days they last.</summary>
    public double PodShare { get; set; } = 0.5;
    public double PodDays { get; set; } = 6;
    /// <summary>Big hulls (class "lifeboats"): share of the crew in the lifeboat, days of air, and its crawl home (Mm an hour).</summary>
    public double BoatShare { get; set; } = 0.8;
    public double BoatDays { get; set; } = 20;
    public double BoatSpeedMmPerHour { get; set; } = 6;
    /// <summary>Pods push off from the wreck at this speed; in orbit they drift out at OrbitDrift of it.</summary>
    public double PodScatterMmPerHour { get; set; } = 1.5;
    public double OrbitDrift { get; set; } = 0.2;
    /// <summary>A fleet out of battle this close picks survivors up; a lifeboat this close to a planet of its side is home.</summary>
    public double PickupMm { get; set; } = 3;
    public double HomeMm { get; set; } = 2;
    /// <summary>How often Auto (and the rival) looks for free fleets to send for survivors, and the share of top speed they fly at.</summary>
    public int SendEveryTicks { get; set; } = 6;
    public double RescueSpeed { get; set; } = 0.7;
    /// <summary>A towed crippled ship and its tow crawl at this share of full speed.</summary>
    public double TowSpeed { get; set; } = 0.3;
    /// <summary>Prisoners (k) per scrap of intel.</summary>
    public double PrisonersPerScrapK { get; set; } = 0.05;
}

public sealed class WarData
{
    public CrewsData Crews { get; set; } = new();
    public BoardingData Boarding { get; set; } = new();
    public GroundData Ground { get; set; } = new();
    public AfterData After { get; set; } = new();
}

/// <summary>
/// A call to make during a boarding or an invasion: the question, the options (with what each does) and
/// Auto's pick. With Auto it is made at once; with "Ask me" it waits for the player until DueHour, then
/// Auto makes it.
/// </summary>
public sealed class WarCall
{
    public int Id { get; set; }
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Options { get; set; } = new();
    public List<string> Effects { get; set; } = new();
    public int AutoPick { get; set; }
    public double Hour { get; set; }
    public double DueHour { get; set; }
    public int? Picked { get; set; }
    public bool ByAuto { get; set; }

    public bool Open => Picked == null;
}

/// <summary>What one side learns of the other from prisoners, captures and wrecks (see Spoils).</summary>
public sealed class SpoilsState
{
    public double PrisonersK { get; set; }
    public double PrisonerProgressK { get; set; }
    public int Scraps { get; set; }
    public List<string> Notes { get; set; } = new();
}

/// <summary>
/// Intelligence on the rival from prisoners, captured ships and stripped wrecks: each scrap speeds up
/// (or brings into view) one idea the rival already has, and raises the intel level (Seen, Studied, Known).
/// </summary>
public static class Spoils
{
    public static string Level(World w, Side side) => Economy.Of(w, side).Spoils.Scraps switch
    {
        >= 8 => "Known",
        >= 3 => "Studied",
        _ => "Seen",
    };

    /// <summary>One scrap of intel: a research the other side has and this side lacks is sparked (in view, or a quarter off if queued).</summary>
    public static void Scrap(Simulation sim, Side side, string why, IntelSource source, double points = 0, bool part = false, Side? from = null)
    {
        var w = sim.World;
        if (!w.Loop) return;
        var state = Economy.Of(w, side);
        state.Spoils.Scraps++;
        var them = from is Side f && f != side ? f : Sides.MainEnemy(w, side);
        var theirs = Economy.Of(w, them).Researched;
        var options = theirs.Where(id => !state.Researched.Contains(id) && GameData.ResearchById.ContainsKey(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        string text;
        if (options.Count > 0)
        {
            var id = options[sim.Rng.Range(0, options.Count)];
            var idea = GameData.Research(id);
            var queued = w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Research && o.Key == id).ToList();
            foreach (var o in queued) o.HoursLeft = Math.Max(0, o.HoursLeft * (1 - Sparks.Cut));
            if (!state.Sparked.Contains(id)) state.Sparked.Add(id);
            state.SparkNotes.Add(new SparkNote(id, $"Intel: {why}", w.Hour, queued.Count > 0));
            if (state.SparkNotes.Count > 60) state.SparkNotes.RemoveAt(0);
            text = $"{why}: the rival has {idea.Name}";
        }
        else text = $"{why}: nothing new on the rival's research";
        Learn(sim, side, source, text, points, part ? Parts[sim.Rng.Range(0, Parts.Length)] : null, them);
        sim.Log.Info("intel", "Intel scrap", new { side = side.ToString(), why, text, scraps = state.Spoils.Scraps });
    }

    /// <summary>Parts a captured ship or a stripped wreck can tell us the rival's level of (see Intel).</summary>
    private static readonly string[] Parts = { "railguns", "missiles", "point-defence", "shields", "engines", "armour" };

    /// <summary>The one hook where war spoils become intel: a note here (for the War tab) and in the intel file on the rival.</summary>
    public static void Learn(Simulation sim, Side side, IntelSource source, string text, double points = 0, string? part = null, Side? about = null)
    {
        var spoils = Economy.Of(sim.World, side).Spoils;
        spoils.Notes.Add(text);
        if (spoils.Notes.Count > 20) spoils.Notes.RemoveAt(0);
        Intel.Note(sim.World, side, source, text, points, part, about);
    }

    /// <summary>Prisoners taken (k): they talk, a scrap for every PrisonersPerScrapK.</summary>
    public static void TakePrisoners(Simulation sim, Side side, double k, string where, Side? from = null)
    {
        if (k <= 1e-9 || !sim.World.Loop || Races.Of(sim.World, side).NoCaptures) return; // machines take no prisoners
        var intel = Economy.Of(sim.World, side).Spoils;
        intel.PrisonersK += k;
        intel.PrisonerProgressK += k;
        Economy.Of(sim.World, side).Totals.PrisonersTaken += k;
        var per = War.D.After.PrisonersPerScrapK;
        var scraps = 0;
        while (intel.PrisonerProgressK >= per && scraps < 3)
        {
            intel.PrisonerProgressK -= per;
            scraps++;
            Scrap(sim, side, $"prisoners from {where}", IntelSource.Prisoners, Intel.SalvagePoints, from: from);
            Contact.Learn(sim.World, side, from ?? Sides.MainEnemy(sim.World, side), Contact.D.PrisonerPoints, "prisoners");
        }
        if (intel.PrisonerProgressK >= per) intel.PrisonerProgressK = 0; // more than three at once teaches no more
    }
}

/// <summary>Shared helpers for boarding, ground war and the after-battle steps.</summary>
public static class War
{
    public static WarData D => GameData.Economy.War ?? Default;
    private static readonly WarData Default = new();

    /// <summary>Crew aboard a ship of this class when full (k): the same people its building took from the yard planet.</summary>
    public static double CrewK(Ship s) => Crews.Crewed(s) ? Fitting.Spec(s).Cost * Planets.D.CrewKPerCost : 0;

    /// <summary>Is this side's call in this area made by Auto (true), or does it ask the player?</summary>
    public static bool AutoCalls(World w, Side side, AutoArea area, int? bodyId = null, int? fleetId = null) =>
        Mind.Of(w, side) != null || Stewards.LevelFor(w, side, area, bodyId, fleetId) == AutoLevel.Auto;

    /// <summary>Make a new call: Auto picks at once, else it waits for the player (up to askHours).</summary>
    public static WarCall Call(World w, string kind, string text, int autoPick, bool auto, double askHours, params (string Label, string Effect)[] options) => new()
    {
        Id = w.NextId++, Kind = kind, Text = text, AutoPick = autoPick, Hour = w.Hour, DueHour = auto ? w.Hour : w.Hour + askHours,
        Options = options.Select(o => o.Label).ToList(), Effects = options.Select(o => o.Effect).ToList(),
    };

    /// <summary>Calls whose time is up are made by Auto; returns those made now (in order).</summary>
    public static List<WarCall> Due(World w, List<WarCall> calls)
    {
        var made = new List<WarCall>();
        foreach (var c in calls.Where(c => c.Open && w.Hour >= c.DueHour - 1e-9))
        {
            c.Picked = c.AutoPick;
            c.ByAuto = true;
            made.Add(c);
        }
        return made;
    }

    /// <summary>Hourly in full games: crews, boardings, invasions and the after-battle steps.</summary>
    public static void Update(Simulation sim)
    {
        if (!sim.World.Loop) return;
        Designer.Update(sim);
        Crews.Hourly(sim);
        Boarders.Hourly(sim);
        AfterBattle.Hourly(sim);
    }
}

/// <summary>Ranks: what a crew is.</summary>
public enum Rank { Green, Trained, Seasoned, Veteran, Elite }

/// <summary>
/// Veteran crews. Each crewed ship's crew has experience: Green, Trained, Seasoned, Veteran, Elite. It
/// grows from battles, patrols and an academy; replacing casualties at a yard dilutes it with green
/// crew. Better crews aim better, mend faster, board harder and hold on longer. Crews rescued from
/// pods keep their rank and man the next ship built. AI-run ships have no crew to train.
/// Auto (the Crews area) spreads veterans over new ships in port and keeps the best crew on the flagship.
/// </summary>
public static class Crews
{
    private static CrewsData C => War.D.Crews;

    public static bool Crewed(Ship s) => !s.AiRun && !Fitting.Spec(s).AiCrew;

    /// <summary>Drone crews are bred trained and stay so: they never gain or lose experience.</summary>
    public const double DroneXp = 1;

    /// <summary>The crew can gain experience (drone crews never do: see Races).</summary>
    public static bool Learns(World w, Ship s) => Crewed(s) && Races.Of(w, s.Side).CrewRanks;

    /// <summary>People a new crew for this class takes from its yard (AI cores cut it; drone crews take fewer).</summary>
    public static double CrewPeopleK(World w, Side side, string classId) =>
        GameData.ShipClass(classId).Cost * Planets.D.CrewKPerCost * Tech.Factor(w, side, "ai-cores") * Races.Of(w, side).CrewPop;

    public static Rank RankOf(Ship s) => RankOf(s.Xp);

    public static Rank RankOf(double xp)
    {
        var r = Rank.Green;
        for (var i = 0; i < C.RankXp.Length && i <= (int)Rank.Elite; i++) if (xp >= C.RankXp[i] - 1e-9) r = (Rank)i;
        return r;
    }

    public static string Name(Rank r) => r.ToString();

    public static double At(double[] table, Rank r) => table.Length > (int)r ? table[(int)r] : 1;

    /// <summary>Railgun hit chance for the crew (1 for AI ships and outside full games).</summary>
    public static double AimFactor(World w, Ship s) => w.Loop && Crewed(s) ? At(C.Aim, RankOf(s)) * (1 - C.StrainAimCut * s.Strain) : 1;
    public static double MendFactor(World w, Ship s) => w.Loop && Crewed(s) ? At(C.Mend, RankOf(s)) * (1 - C.StrainMendCut * s.Strain) : 1;

    /// <summary>How much of a dodge a strained crew still manages: the dodge factor moved back towards 1 (no dodge).</summary>
    public static double StrainedDodge(World w, Ship s, double factor) => w.Loop && Crewed(s) ? 1 - (1 - factor) * (1 - C.StrainDodgeCut * s.Strain) : factor;

    /// <summary>A slug dodged: the crew takes strain (more in a hard burn, and the closer the shooter the harder the jink).</summary>
    public static void Strain(World w, Ship s, bool hardBurn, double closeness)
    {
        if (!w.Loop || !Crewed(s)) return;
        s.Strain = Math.Min(1, s.Strain + C.StrainPerDodge * (hardBurn ? C.HardBurnStrain : 1) * closeness * Races.Of(w, s.Side).Strain);
    }

    public static bool WornOut(Ship s) => Crewed(s) && s.Strain >= C.StrainHigh;

    /// <summary>Changed over at a refit: a crew taken off goes home to the reserve; a ship given a crew is manned like a new one.</summary>
    public static void ChangeOver(World w, Ship s, Body? yard)
    {
        var toAi = Designer.AiRun(w, s.Side, s.ClassId);
        if (toAi)
        {
            if (Crewed(s)) Economy.Of(w, s.Side).ReserveCrews.Add(s.Xp);
            s.AiRun = true;
            s.Xp = 0;
            s.Strain = 0;
            s.Food = 0;
        }
        else
        {
            s.AiRun = false;
            Man(w, s, yard);
            if (yard != null && yard.PopK > 0) yard.PopK = Math.Max(1, yard.PopK - CrewPeopleK(w, s.Side, s.ClassId));
            s.Food = SupplyLines.MaxFood(s);
        }
    }
    public static double BoardFactor(Ship s) => Crewed(s) ? At(C.Boarding, RankOf(s)) : 1;

    /// <summary>The ship rule's hull line, lower for veteran crews (they hold on longer).</summary>
    public static double HullLine(World w, Ship s, double line) => w.Loop && Crewed(s) && RankOf(s) >= Rank.Veteran ? line * C.VeteranHold : line;

    /// <summary>A crew mended at a yard: casualties are replaced with green crew, which dilutes the rank.</summary>
    public static void Replaced(Ship s, double crewBefore, double crewAfter)
    {
        if (crewAfter <= crewBefore + 1e-9 || crewAfter <= 1e-9) return;
        s.Xp *= Math.Max(0, crewBefore) / crewAfter;
    }

    /// <summary>A new ship's crew: a rescued crew waiting at home keeps its rank; else green, or trained with an academy at the yard.</summary>
    public static void Man(World w, Ship s, Body? yard)
    {
        if (!w.Loop) return;
        if (!Crewed(s)) { s.Xp = 0; return; }
        if (!Learns(w, s)) { s.Xp = DroneXp; return; }
        var reserve = Economy.Of(w, s.Side).ReserveCrews;
        if (reserve.Count > 0)
        {
            var best = reserve.Max();
            reserve.Remove(best);
            s.Xp = best;
            return;
        }
        s.Xp = yard != null && Colonies.Built(yard).Any(p => p.Kind == "academy") ? C.AcademyCap : 0;
    }

    /// <summary>A battle is over: every crewed ship that fought gains experience (more if the other side left the field).</summary>
    public static void OnBattleOver(Simulation sim, Battle b)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var side in Sides.InPlay(w))
        {
            var mine = b.For(side);
            var theirs = b.Foes(side);
            if (mine.RailShots + mine.MissilesFired == 0 && theirs.RailShots + theirs.MissilesFired == 0) continue;
            var won = !mine.Withdrew && (theirs.Withdrew || theirs.ShipsIn > 0 && theirs.ShipsLost.Count >= theirs.ShipsIn);
            foreach (var r in b.Roster.Where(r => r.Side == side).OrderBy(r => r.Id))
            {
                if (w.FindShip(r.Id) is not Ship s || s.Side != side || !Learns(w, s)) continue;
                var before = RankOf(s);
                s.Xp += C.BattleXp + (won ? C.WinXp : 0);
                if (RankOf(s) > before)
                    sim.Log.Info("crews", $"{s.Name}'s crew is now {Name(RankOf(s))}", new { shipId = s.Id, xp = Math.Round(s.Xp, 2) });
            }
        }
    }

    /// <summary>Hourly: patrols and academies teach; the rival's crews are spread by its mind, the player's by Auto.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        foreach (var f in w.Fleets.Where(f => f.ShipIds.Count > 0))
        {
            var at = f.OrbitBodyId is int id && f.Hold == HoldStyle.Orbit && f.Flight == null ? w.FindBody(id) : null;
            var inPort = at != null && at.Owner == f.Side;
            var patrol = !inPort && (f.Mission != Mission.None || f.ZoneId != null) && !Heat.InBattle(w, f);
            var academy = inPort && Colonies.Built(at!).Any(p => p.Kind == "academy");
            // Strain eases off out of battle, faster in port.
            if (!Heat.InBattle(w, f))
                foreach (var s in w.ShipsOf(f).Where(s => s.Strain > 0))
                    s.Strain = Math.Max(0, s.Strain - C.StrainRecoverPerHour * (inPort ? C.PortRecover : 1) * GameTime.HoursPerTick);
            if (!patrol && !academy) continue;
            foreach (var s in w.ShipsOf(f).Where(s => Learns(w, s)))
            {
                if (patrol && s.Xp < C.PatrolCap) s.Xp = Math.Min(C.PatrolCap, s.Xp + C.PatrolXpPerDay / 24);
                if (academy && s.Xp < C.AcademyCap) s.Xp = Math.Min(C.AcademyCap, s.Xp + C.AcademyXpPerDay / 24);
            }
        }
        if (w.Tick % GameTime.TicksPerDay != 0) return;
        foreach (var side in Sides.InPlay(w))
            if (Mind.Of(w, side) != null) Steward(sim, side, (_, _, _, act, _, _, _, _) => { (act with { Source = "ai" }).Apply(w, sim); return true; });
    }

    /// <summary>What Auto would do with crews: in port, the flagship gets the fleet's best crew, and veterans are spread to green ships.</summary>
    public static void Steward(Simulation sim, Side side, OfferFn offer)
    {
        var w = sim.World;
        if (!w.Loop) return;
        var flagId = Economy.Of(w, side).FlagshipId;
        foreach (var f in w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 1).OrderBy(f => f.Id).ToList())
        {
            if (f.OrbitBodyId is not int id || w.FindBody(id) is not Body at || at.Owner != side || !Economy.InOrbitAt(f, at) || Heat.InBattle(w, f)) continue;
            var ships = w.ShipsOf(f).Where(Crewed).OrderBy(s => s.Id).ToList();
            if (ships.Count < 2) continue;
            var best = ships.OrderByDescending(s => s.Xp).ThenBy(s => s.Id).First();
            if (ships.Find(s => s.Id == flagId) is Ship flag && best.Id != flag.Id && RankOf(best) > RankOf(flag))
            {
                offer(AutoArea.Crews, $"crew-flag:{f.Id}", $"{f.Name}: the {Name(RankOf(best))} crew of {best.Name} moves to the flagship {flag.Name}",
                    new TransferCrewCommand(side, best.Id, flag.Id, Swap: true), "Swap crews", fleetId: f.Id);
                continue;
            }
            var others = ships.Where(s => s.Id != flagId).OrderByDescending(s => s.Xp).ThenBy(s => s.Id).ToList();
            if (others.Count < 2) continue;
            var top = others[0];
            var low = others[^1];
            if (top.Xp - low.Xp < C.SpreadGap) continue;
            offer(AutoArea.Crews, $"crew-spread:{f.Id}", $"{f.Name}: veterans from {top.Name} move over to train the {Name(RankOf(low)).ToLowerInvariant()} crew of {low.Name}",
                new TransferCrewCommand(side, top.Id, low.Id), "Spread veterans", fleetId: f.Id);
        }
    }
}

/// <summary>Move veterans from one ship to another in the same fleet (a share of the gap), or swap two crews outright.</summary>
public sealed record TransferCrewCommand(Side Side, int FromShipId, int ToShipId, bool Swap = false) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => world.FindShip(FromShipId) is Ship s ? (Side, s.Position) : null;

    public override void Apply(World world, Simulation sim)
    {
        if (world.FindShip(FromShipId) is not Ship a || world.FindShip(ToShipId) is not Ship b || a.Side != Side || b.Side != Side
            || a.FleetId != b.FleetId || !Crews.Crewed(a) || !Crews.Crewed(b))
        {
            sim.Log.Warn("crews", "Bad crew transfer", new { side = Side.ToString(), FromShipId, ToShipId, Source });
            return;
        }
        if (Swap) (a.Xp, b.Xp) = (b.Xp, a.Xp);
        else
        {
            var move = (a.Xp - b.Xp) * War.D.Crews.SpreadShare;
            if (move <= 0) return;
            a.Xp -= move;
            b.Xp += move;
        }
        sim.Log.Info("crews", Swap ? $"{a.Name} and {b.Name} swap crews" : $"Veterans from {a.Name} move to {b.Name}",
            new { side = Side.ToString(), FromShipId, ToShipId, Swap, Source });
    }
}
