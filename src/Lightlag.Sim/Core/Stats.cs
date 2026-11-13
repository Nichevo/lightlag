using System.Globalization;
using System.Text;

namespace Lightlag.Sim.Core;

/// <summary>Running totals for one side over the whole game (kept in SideState, so saves keep them).</summary>
public sealed class SideTotals
{
    /// <summary>Weapons and counters (Plan 3 build 2): missiles of ours hijacked, mines laid and hit, squadron sorties and losses.</summary>
    public int MissilesHijacked { get; set; }
    public int MinesLaid { get; set; }
    public int MineHits { get; set; }
    public int Sorties { get; set; }
    public int SquadronsLost { get; set; }

    public int ShipsBuilt { get; set; }
    public int ShipsLost { get; set; }
    public int ShipsKilled { get; set; }
    /// <summary>Enemy defence stations wrecked.</summary>
    public int FortsWrecked { get; set; }
    public double IndustryEarned { get; set; }
    public double IndustrySpent { get; set; }

    /// <summary>Industry paid to lift goods and satellites to orbit (part of IndustrySpent; see Lift).</summary>
    public double LiftSpent { get; set; }
    public int SatellitesLaunched { get; set; }
    public int SatellitesLost { get; set; }

    /// <summary>People and power: thousands of hungry people who moved away, worlds abandoned, planet-hours of shedding.</summary>
    public double PeopleLeftK { get; set; }
    public int WorldsAbandoned { get; set; }
    public int ShedHours { get; set; }
    public int PlanetsClaimed { get; set; }
    public int PlanetsLost { get; set; }
    public int Battles { get; set; }

    /// <summary>Battles that started more than five light-minutes from the side's flagship, and battles its flagship fought in.</summary>
    public int LaggedBattles { get; set; }
    public int FlagshipBattles { get; set; }

    /// <summary>Decisions its fleets made for themselves (see Autonomy).</summary>
    public int OwnDecisions { get; set; }

    /// <summary>Times its ships overheated: in all, in a battle, with engines burning, and running cold.</summary>
    public int Overheats { get; set; }
    public int OverheatsInBattle { get; set; }
    public int OverheatsBurning { get; set; }
    public int OverheatsCold { get; set; }

    /// <summary>Ship-hours spent in battle, and of them overheated (guns and shields off).</summary>
    public double BattleShipHours { get; set; }
    public double OverheatedBattleHours { get; set; }
    public double DamageDealt { get; set; }
    public double ShipyardDamage { get; set; }

    /// <summary>Spark counters: our slugs a dodge made miss, our missiles shot down, enemy missiles we shot down, deliveries made.</summary>
    public int SlugsDodged { get; set; }
    public int MissilesStopped { get; set; }
    public int MissilesShotDown { get; set; }
    public int Deliveries { get; set; }

    /// <summary>Hours a ship order at a yard waited for materials (alloys, electronics), not Industry.</summary>
    public double MaterialStallHours { get; set; }

    /// <summary>Damage and battle endings (see Wounds and BattleRules).</summary>
    public int Contacts { get; set; }

    /// <summary>Hazards and events (see Hazards, Events): rock hits taken, hull they took, ship-hours in radiation, events had.</summary>
    public int RockHits { get; set; }
    public double HazardHull { get; set; }
    public double RadiationShipHours { get; set; }
    public int Events { get; set; }
    public int ShipsCrippled { get; set; }
    public int PartsKnocked { get; set; }
    public int PullBacks { get; set; }
    public int PulledBeforeShots { get; set; }
    public int BattlesWiped { get; set; }
    public int BattlesOneWithdrew { get; set; }
    public int BattlesBothWithdrew { get; set; }
    public double Salvaged { get; set; }
    public int AmbushCatches { get; set; }
    public double CargoCaptured { get; set; }

    /// <summary>Boarding, rescue and captures (see Boarding and AfterBattle).</summary>
    public int Boardings { get; set; }
    public int ShipsCaptured { get; set; }
    public int BoardingsFailed { get; set; }
    public double PrisonersTaken { get; set; }
    public double CrewsRescued { get; set; }
    public int Scuttled { get; set; }

    /// <summary>Blockade against invasion (see Ground): planets taken each way, days it took, troops lost, buildings lost and happiness after.</summary>
    public int Invasions { get; set; }
    public int InvasionWins { get; set; }
    public double InvasionDays { get; set; }
    public double TroopsLostK { get; set; }
    public int InvasionBuildingsLost { get; set; }
    public double InvasionHappiness { get; set; }
    public int BlockadeWins { get; set; }
    public double BlockadeDays { get; set; }
    public double BlockadeHappiness { get; set; }

    /// <summary>Blockades of a day or more this side began, and how many its enemy broke with a fleet or it left.</summary>
    public int BlockadesStarted { get; set; }
    public int BlockadesBroken { get; set; }
    public int BlockadesLeft { get; set; }

    /// <summary>
    /// Sieges of this side's planets (enemy warships in orbit a day or more, contested or blockaded) and how each
    /// ended: help reached it, the attackers left (with or without help on the way), or it fell (no help sent, help
    /// still on its way, or help there and beaten). HelpDays: days off the help was when it set out; HelpLateDays:
    /// for planets that fell with help on the way, how much later it would have arrived.
    /// </summary>
    public int Sieges { get; set; }
    public int SiegesRelieved { get; set; }
    public int SiegesLeftHelpSent { get; set; }
    public int SiegesLeftNoHelp { get; set; }
    public int SiegesLostNoHelp { get; set; }
    public int SiegesLostHelpLate { get; set; }
    public int SiegesLostHelpThere { get; set; }
    public double SiegeHelpDays { get; set; }
    public double SiegeHelpSentAfterDays { get; set; }
    public double SiegeHelpLateDays { get; set; }

    /// <summary>Sieges this side gave up (it left the planet's orbit with the planet still the enemy's), by why its fleets left.</summary>
    public Dictionary<string, int> SiegeLeftWhy { get; set; } = new();

    /// <summary>The computer's reason, once per siege, for sending no relief: where the power it needed was.</summary>
    public Dictionary<string, int> ReliefWhyNot { get; set; } = new();

    /// <summary>Planets taken by method ("Blockade", "Blitz", "Methodical", "Starve"): days, and what was left to take.</summary>
    public Dictionary<string, CaptureTally> Captures { get; set; } = new();

    /// <summary>The goods ledger (see Ledger): per good, amounts gained and used by reason.</summary>
    public Dictionary<string, Dictionary<string, double>> GoodsIn { get; set; } = new();
    public Dictionary<string, Dictionary<string, double>> GoodsOut { get; set; } = new();
}

/// <summary>One side at the end of one game day.</summary>
public sealed record DayRecord(
    int Day, Side Side, double Industry, double Income, int Planets, int Ships, int Frigates, int Destroyers, int Fleets,
    double Power, int Researched, int ShipsBuilt, int ShipsLost, int ShipsKilled, int Battles, double IndustryEarned,
    double IndustrySpent, int PlanetsClaimed, int PlanetsLost, double ShipyardArmour, double People = 0, double Control = 0, int Warships = 0);

/// <summary>
/// Game statistics for reviewing playtests and headless runs: a record per side per day, plus the running
/// totals. Written out as CSV with F8 reports, at the end of a game and by the batch runner.
/// </summary>
public static class Stats
{
    public static SideTotals Of(World w, Side side) => Economy.Of(w, side).Totals;

    /// <summary>A rough fighting value: each ship's cost, scaled down for damage and (destroyers) empty racks.</summary>
    /// <param name="racks">Count empty missile racks against it (false: damage only, for "strength lost" in a fight).</param>
    public static double ShipPower(World w, Ship s, bool racks = true)
    {
        var cls = Fitting.Spec(s);
        var armour = Math.Clamp(s.Armour / Tech.MaxArmour(w, s), 0, 1);
        var loaded = cls.Missiles > 0 && racks ? 0.4 + 0.6 * s.Missiles / cls.Missiles : 1;
        return cls.Cost * (0.3 + 0.7 * armour) * loaded * (s.Crippled ? 0.2 : s.Knocked.Count > 0 ? 0.7 : 1) * (0.6 + 0.1 * (s.Weapons + s.Engines + s.Defence + s.Crew));
    }

    public static double Power(World w, Fleet f) => w.ShipsOf(f).Sum(s => ShipPower(w, s));

    public static double Power(World w, Side side) => w.Ships.Where(s => s.Side == side).Sum(s => ShipPower(w, s));

    /// <summary>Called at each new game day (and once at the end of a game).</summary>
    public static void RecordDay(World w)
    {
        if (!w.Loop) return;
        var day = w.Day;
        if (w.History.Count > 0 && w.History[^1].Day == day) return;
        foreach (var side in Sides.InPlay(w))
        {
            var t = Of(w, side);
            var ships = w.Ships.Where(s => s.Side == side).ToList();
            w.History.Add(new DayRecord(
                day, side,
                Math.Round(Economy.Of(w, side).Industry, 2),
                Math.Round(Economy.IncomePerDay(w, side), 2),
                Economy.Claimable(w).Count(b => b.Owner == side),
                ships.Count,
                ships.Count(s => s.ClassId == "gun-frigate"),
                ships.Count(s => s.ClassId == "missile-destroyer"),
                w.Fleets.Count(f => f.Side == side),
                Math.Round(Power(w, side), 1),
                Economy.Of(w, side).Researched.Count,
                t.ShipsBuilt, t.ShipsLost, t.ShipsKilled, t.Battles,
                Math.Round(t.IndustryEarned, 1), Math.Round(t.IndustrySpent, 1), t.PlanetsClaimed, t.PlanetsLost,
                Math.Round(Economy.ShipyardOf(w, side)?.ShipyardArmour ?? 0, 1),
                Math.Round(Planets.PopulationOf(w, side), 1),
                Math.Round(Planets.Control(w, side).Share, 3),
                ships.Count(Planner.Warship)));
        }
    }

    public const string CsvHeader =
        "day,side,industry,income,planets,ships,frigates,destroyers,fleets,power,researched,ships_built,ships_lost,ships_killed,"
        + "battles,industry_earned,industry_spent,planets_claimed,planets_lost,shipyard_armour,people,control,warships";

    public static string ToCsv(World w)
    {
        var sb = new StringBuilder(CsvHeader).Append('\n');
        foreach (var r in w.History) sb.Append(CsvLine(r)).Append('\n');
        return sb.ToString();
    }

    public static string CsvLine(DayRecord r)
    {
        string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        return string.Join(",", r.Day, r.Side, N(r.Industry), N(r.Income), r.Planets, r.Ships, r.Frigates, r.Destroyers, r.Fleets,
            N(r.Power), r.Researched, r.ShipsBuilt, r.ShipsLost, r.ShipsKilled, r.Battles, N(r.IndustryEarned), N(r.IndustrySpent),
            r.PlanetsClaimed, r.PlanetsLost, N(r.ShipyardArmour), N(r.People), N(r.Control), r.Warships);
    }

    /// <summary>A few lines on how the game went, for the end screen and reports.</summary>
    public static string Summary(World w)
    {
        var sb = new StringBuilder();
        foreach (var side in Sides.InPlay(w))
        {
            var t = Of(w, side);
            var who = Sides.Label(w, side);
            sb.AppendLine($"{who}: built {t.ShipsBuilt} ships, lost {t.ShipsLost}, destroyed {t.ShipsKilled}; "
                          + $"{t.Battles} battles; took {t.PlanetsClaimed} planets, lost {t.PlanetsLost}; "
                          + $"earned {t.IndustryEarned:0} Industry, spent {t.IndustrySpent:0}.");
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Planets taken one way: how long the siege took in all, and the people, buildings and happiness taken.</summary>
public sealed class CaptureTally
{
    public int Count { get; set; }
    /// <summary>Days from the start of the blockade (or the landing, if longer) to the capture.</summary>
    public double Days { get; set; }
    /// <summary>Days of ground fighting only (0 for a blockade).</summary>
    public double FightDays { get; set; }
    public double PeopleK { get; set; }
    public int BuildingsKept { get; set; }
    public int BuildingsDamaged { get; set; }
    public int BuildingsWrecked { get; set; }
    public double Happiness { get; set; }

    public static void Add(SideTotals t, string how, double days, double fightDays, Body b, int damaged, int wrecked)
    {
        if (!t.Captures.TryGetValue(how, out var c)) t.Captures[how] = c = new CaptureTally();
        c.Count++;
        c.Days += days;
        c.FightDays += fightDays;
        c.PeopleK += b.PopK;
        c.BuildingsKept += b.Buildings.Count;
        c.BuildingsDamaged += damaged;
        c.BuildingsWrecked += wrecked;
        c.Happiness += b.Happiness;
    }
}
