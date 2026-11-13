using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (ship-classes.json, "weapons") -----------------------------------------------------

/// <summary>Pulse lance numbers. See the comment in ship-classes.json.</summary>
public sealed record LanceData(double RangeMm = 25, double ReloadSeconds = 45, double Accuracy = 0.8, double Damage = 9,
    double HeatToTarget = 5, double HeatPerShot = 4, double ShieldHeatFactor = 2);

/// <summary>Minefield numbers. See the comment in ship-classes.json.</summary>
public sealed record MineData(double DamagePerMine = 35, double RadiusMm = 4, double PlanetRadiusMm = 3, double HitChancePerMine = 0.05,
    double MaxHitChance = 0.6, double KnownCut = 0.5, double DriftMmPerDay = 0.5, double LifetimeDays = 120, double SpotMm = 3,
    double SweepPerPdHour = 0.5, int MaxPerLay = 8, double DropSpeedShare = 0);

/// <summary>Planet fighter and bomber squadrons. See the comment in ship-classes.json.</summary>
public sealed record StrikeCraftData(double RangeMm = 40, double BomberDamage = 30, double FighterDamage = 6, double LossPerPd = 0.04,
    double MaxLoss = 0.5, double EscortCut = 0.5, double FighterBombShield = 0.15, double FighterMissileKill = 0.1, double RebuildDays = 4,
    double RebuildAlloys = 2, double SortieHours = 2);

/// <summary>The missile counter-race: jamming and hijack by missile brain (index = brain - 1). See ship-classes.json.</summary>
public sealed record CountersData(double[]? JamByBrain = null, double HijackMm = 12, double[]? HijackByBrain = null, double IffHijackCut = 0.75)
{
    public double Jam(int brain) => At(JamByBrain ?? new[] { 0, 0.15, 0.1, 0.06, 0.03 }, brain);
    public double Hijack(int brain) => At(HijackByBrain ?? new[] { 0, 0.14, 0.11, 0.08, 0.05 }, brain);
    private static double At(double[] table, int brain) => table.Length == 0 ? 0 : table[Math.Clamp(brain, 1, table.Length) - 1];
}

/// <summary>Keeping tech secret. See ship-classes.json.</summary>
public sealed record SecrecyData(double HideRangeShare = 0.7, double BelowHull = 0.6, double OddsBelow = 0.5);

/// <summary>Learning the rival's research. See ship-classes.json.</summary>
public sealed record RivalTechData(double BattleTeach = 0.08, double WreckTeach = 0.2, double CaptureTeach = 0.6, double PlanetTeachPerDay = 0.01,
    double StudiedAt = 0.3, double UnlockedCut = 0.2);

public sealed record WeaponsData(LanceData? Lance = null, MineData? Mines = null, StrikeCraftData? StrikeCraft = null, CountersData? Counters = null,
    SecrecyData? Secrecy = null, RivalTechData? RivalTech = null)
{
    public LanceData L => Lance ?? new LanceData();
    public MineData M => Mines ?? new MineData();
    public StrikeCraftData S => StrikeCraft ?? new StrikeCraftData();
    public CountersData C => Counters ?? new CountersData();
    public SecrecyData K => Secrecy ?? new SecrecyData();
    public RivalTechData R => RivalTech ?? new RivalTechData();
}

// ---- Pulse lance -----------------------------------------------------------------------------

/// <summary>
/// Pulse lances: beam mounts in weapon slots (Pulse lance research). A beam arrives at once, so it can't be
/// dodged; it does a little less than a railgun pair, reaches less far, and pushes heat into the target
/// (more into a shield). Each shot heats the firing ship a lot, so a lance ship must watch its heat.
/// </summary>
public static class Beams
{
    public const string LanceIdea = "pulse-lance";
    public const string LanceModule = "pulse-lance";
    private static LanceData L => GameData.Weapons.L;

    /// <summary>How far this fleet's lances fire now (less while it hides its range).</summary>
    public static double Range(World w, Fleet f) => L.RangeMm * Secrecy.ReachShare(w, f);

    /// <summary>Lances fire when loaded, the ship is not overheated and the weapons are working.</summary>
    public static void Fire(Simulation sim, Battle battle, Ship ship, Fleet fleet, ShipClass cls, double t1)
    {
        var w = sim.World;
        if (Secrecy.Quiet(w, fleet, LanceIdea))
        {
            battle.For(ship.Side).Note(Secrecy.HeldKey, LanceIdea);
            return;
        }
        var range = Range(w, fleet);
        var target = FireControl.Pick(sim, ship, fleet, range);
        if (target == null) return;
        var stats = battle.For(ship.Side);
        var d = Vec2.Distance(ship.Position, target.Position);
        ship.LanceReadyHour = t1 + L.ReloadSeconds * Tech.ReloadFactor(w, ship.Side) / Wounds.CrewFactor(ship) / 3600;
        battle.Action(t1);
        fleet.LastBurnHour = t1; // the beam's glow shows on sensors
        target.LastTargetedHour = t1;
        stats.Note(Secrecy.ShownKey, LanceIdea);
        stats.LanceReach = Math.Max(stats.LanceReach, d);
        sim.AddEffect(new CombatEffect(t1, EffectKind.Beam, ship.Position, target.Position, ship.Side));
        for (var i = 0; i < cls.Lances; i++)
        {
            stats.LanceShots++;
            Heat.Add(ship, L.HeatPerShot);
            var chance = L.Accuracy * (1 - 0.5 * Math.Clamp(d / Math.Max(1e-9, range), 0, 1)) * FireControl.AimFactor(fleet) * Crews.AimFactor(w, ship) * (w.Loop ? Races.Of(w, ship.Side).Precision : 1);
            if (sim.Rng.NextDouble() >= chance) continue;
            stats.LanceHits++;
            // The beam's heat goes into the shield first (which takes more of it), else into the hull.
            Heat.Add(target, L.HeatToTarget * (target.Shield > 0 && !target.Overheated ? L.ShieldHeatFactor : 1));
            Combat.Damage(sim, battle, target, L.Damage * Wounds.WeaponFactor(ship), ship.Side, missile: true);
            if (w.FindShip(target.Id) == null) break;
        }
        if (ship.Overheated) BattleRecords.Mark(w, battle, ship.Side, MarkKind.Decision, $"{ship.Name}'s lances drive it past its heat limit");
    }
}

// ---- Keeping tech secret ---------------------------------------------------------------------

/// <summary>Battle rules › Weapons shown: always fire your best, hold the advanced ones back until needed, or never show them.</summary>
public enum WeaponsShown { HoldBack, ShowAll, NeverShow }

/// <summary>When a fleet holding back brings its advanced weapons out (each can be switched off).</summary>
public sealed class SecrecyRules
{
    /// <summary>Another rival fleet with warships joins the fight after it started.</summary>
    public bool WhenJoined { get; set; } = true;

    /// <summary>Any of our ships falls below SecrecyData.BelowHull of its hull.</summary>
    public bool WhenHurt { get; set; } = true;

    /// <summary>The odds turn against us (our strength under SecrecyData.OddsBelow of theirs).</summary>
    public bool WhenLosing { get; set; } = true;

    /// <summary>Open fire at a share of true reach until the advanced weapons are out.</summary>
    public bool HideRange { get; set; } = true;

    public SecrecyRules Copy() => (SecrecyRules)MemberwiseClone();
}

/// <summary>
/// Keeping tech secret (board 26). A side marks its newest weapon ideas as advanced (Auto: the two newest;
/// the player can change the list). A fleet holding back fights with its common weapons: older slugs,
/// smarter missile brains held back, lances quiet, and it opens fire short of its true reach. It brings
/// the advanced ones out when it needs them (hurt, losing, or more enemies join). What a side never shows
/// in a battle, the rival can't learn from it (see RivalTech and Intel).
/// </summary>
public static class Secrecy
{
    private static SecrecyData D => GameData.Weapons.K;

    /// <summary>BattleSide note keys: ideas held back in the fight, and ideas shown.</summary>
    public const string HeldKey = "held", ShownKey = "shown";

    /// <summary>Weapon ideas whose use shows in battle, so they can be held back.</summary>
    public static readonly string[] Markable =
    {
        Tech.RailgunMk3, "railgun-mk4", "railgun-mk5", Tech.MissileBrain2, Tech.MissileBrain3, Tech.MissileBrain4, Tech.MissileBrain5,
        "heavy-warheads", Beams.LanceIdea,
    };

    /// <summary>The side's advanced weapons: its own list if it set one, else the two newest it has researched.</summary>
    public static List<string> Advanced(World w, Side side)
    {
        var state = Economy.Of(w, side);
        if (state.Advanced != null) return state.Advanced.Where(id => Tech.Has(w, side, id)).ToList();
        return state.Researched.Where(id => Array.IndexOf(Markable, id) >= 0).TakeLast(2).ToList();
    }

    /// <summary>Is this fleet holding its advanced weapons back right now?</summary>
    public static bool Holding(World w, Fleet f) => w.Loop && f.Shown != WeaponsShown.ShowAll && !f.AdvancedOut && Advanced(w, f.Side).Count > 0;

    /// <summary>Is this idea held back in this fleet's fire now?</summary>
    public static bool Quiet(World w, Fleet f, string idea) => Holding(w, f) && Tech.Has(w, f.Side, idea) && Advanced(w, f.Side).Contains(idea);

    /// <summary>Share of true reach the fleet opens fire at (less while it hides its range).</summary>
    public static double ReachShare(World w, Fleet f) => Holding(w, f) && f.Secrecy.HideRange ? D.HideRangeShare : 1;

    /// <summary>The smartest missile brain the fleet will launch now (held-back brains are not loaded).</summary>
    public static int BrainCap(World w, Fleet f)
    {
        var cap = 5;
        for (var k = 5; k >= 2; k--)
            if (Quiet(w, f, $"missile-brain-{k}")) cap = Math.Min(cap, k - 1);
        return cap;
    }

    /// <summary>A railgun slug's damage, with held-back slugs (Mk III, Mk V) left in the magazine.</summary>
    public static double RailDamage(World w, Fleet f, Ship s)
    {
        var dmg = GameData.Combat.Railgun.Damage;
        if (s.RailGrade >= 2 && !Quiet(w, f, Tech.RailgunMk3)) dmg *= Tech.Factor(w, s.Side, Tech.RailgunMk3);
        if (Tech.BigHull(s) && !Quiet(w, f, "railgun-mk5")) dmg *= Tech.Factor(w, s.Side, "railgun-mk5");
        return dmg;
    }

    /// <summary>Railgun reach now: coil boost (Mk IV) only when shown, then cut while hiding range.</summary>
    public static double RailRange(World w, Fleet f) =>
        GameData.Combat.Railgun.RangeMm * (Quiet(w, f, "railgun-mk4") ? 1 : Tech.Factor(w, f.Side, "railgun-mk4")) * ReachShare(w, f);

    /// <summary>Missile damage factor a salvo launched now carries (heavy warheads only when shown).</summary>
    public static bool HeavyWarheads(World w, Fleet f) => Tech.Has(w, f.Side, "heavy-warheads") && !Quiet(w, f, "heavy-warheads");

    /// <summary>
    /// Every battle step, for fleets in the battle: decide whether a fleet holding back brings its advanced
    /// weapons out, and put them away again once it is out of the fight.
    /// </summary>
    public static void Check(Simulation sim, Battle battle)
    {
        var w = sim.World;
        foreach (var f in w.Fleets)
        {
            if (!Heat.InBattle(w, f))
            {
                if (f.AdvancedOut || f.FightEnemyFleets > 0) { f.AdvancedOut = false; f.FightEnemyFleets = 0; }
                continue;
            }
            var enemies = w.Fleets.Where(o => o.Side != f.Side && Vec2.Distance(o.Position, f.Position) <= GameData.Combat.ContactRangeMm
                                               && Sensors.Sees(w, f.Side, o) && Combat.Armed(w, o)).ToList();
            if (f.FightEnemyFleets == 0) f.FightEnemyFleets = Math.Max(1, enemies.Count);
            var side = battle.For(f.Side);
            if (f.Shown == WeaponsShown.ShowAll || f.AdvancedOut || !Holding(w, f))
            {
                foreach (var id in Advanced(w, f.Side)) side.Note(ShownKey, id);
                continue;
            }
            foreach (var id in Advanced(w, f.Side)) side.Note(HeldKey, id);
            if (f.Shown == WeaponsShown.NeverShow) continue;
            string? why = null;
            var rules = f.Secrecy;
            if (rules.WhenHurt && w.ShipsOf(f).Any(s => s.Armour < Tech.MaxArmour(w, s) * D.BelowHull)) why = "a ship is badly hurt";
            else if (rules.WhenJoined && enemies.Count > f.FightEnemyFleets) why = "more enemy fleets join";
            else if (rules.WhenLosing && enemies.Count > 0 && Stats.Power(w, f) < D.OddsBelow * enemies.Sum(o => Stats.Power(w, o))) why = "the odds turn against it";
            if (why == null) continue;
            f.AdvancedOut = true;
            BattleRecords.Mark(w, battle, f.Side, MarkKind.Decision, $"{f.Name} brings out its advanced weapons: {why}");
            sim.Log.Info("combat", $"{f.Name} brings out its advanced weapons", new { fleetId = f.Id, why, advanced = Advanced(w, f.Side) });
        }
    }

    /// <summary>What the rival learned of this side's weapons in a battle: ideas shown, less those held back all fight.</summary>
    public static HashSet<string> HeldAllFight(BattleSide side) => side.Notes(HeldKey).Except(side.Notes(ShownKey)).ToHashSet();

    public static string ModeName(WeaponsShown m) => m switch
    {
        WeaponsShown.ShowAll => "Show everything",
        WeaponsShown.NeverShow => "Never show",
        _ => "Hold back best",
    };
}

/// <summary>A fleet's Weapons shown rule (Battle rules) and when it brings advanced weapons out.</summary>
public sealed record SetSecrecyCommand(int FleetId, WeaponsShown Shown, SecrecyRules Rules) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Weapons shown") is not Fleet fleet) return;
        fleet.Shown = Shown;
        fleet.Secrecy = Rules.Copy();
        sim.Log.Info("fleet", $"{fleet.Name} weapons shown: {Secrecy.ModeName(Shown)}", new { FleetId, Shown, Rules, Source });
    }
}

/// <summary>The side's list of advanced weapons (null: Auto, the two newest).</summary>
public sealed record SetAdvancedCommand(Side Side, List<string>? Ideas) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        Economy.Of(world, Side).Advanced = Ideas?.Where(id => Array.IndexOf(Secrecy.Markable, id) >= 0).Distinct().ToList();
        sim.Log.Info("fleet", $"{Side} marks its advanced weapons", new { side = Side.ToString(), ideas = Ideas, Source });
    }
}
