using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>How far a side has got with one of the rival's ideas (board 26).</summary>
public enum RivalStage { None, Seen, Studied, Unlocked }

/// <summary>What a side has seen of a rival fleet's weapon reach (Mm, 0: never seen), from fighting it.</summary>
public sealed class ReachFact
{
    public int FleetId { get; set; }
    public double Rail { get; set; }
    public double Missile { get; set; }
    public double Lance { get; set; }
    public double Hour { get; set; }
    public int Fights { get; set; }
}

/// <summary>A guess at a rival fleet's reach, how it was made, and how far off it may be (a share, ±).</summary>
public sealed record ReachGuess(double Rail, double Missile, double Lance, string How, double Plus);

/// <summary>
/// Learning the rival's research (board 26): what intel sees becomes ideas you can research. Each rival idea
/// goes Seen (a blurred node in your web, tagged as theirs), Studied (its name and effect show) and Unlocked
/// (you can queue it without its prerequisites, and it takes less time, since you know it works). Battles
/// teach the ideas the rival showed in them (not those it held back), stripped wrecks and captured ships the
/// parts aboard, and close looks at its colonies the buildings there. It works both ways.
/// Also here: the rival's weapon reach as a guess until you have fought that fleet.
/// </summary>
public static class RivalTech
{
    private static RivalTechData D => GameData.Weapons.R;

    /// <summary>The ideas each kind of evidence can teach.</summary>
    public static readonly Dictionary<string, string[]> ByPart = new()
    {
        ["railguns"] = new[] { Tech.FastRailguns, "capacitor-banks", Tech.RailgunMk3, "railgun-mk4", "railgun-mk5" },
        ["missiles"] = new[] { Tech.MissileBrain2, Tech.MissileBrain3, Tech.MissileBrain4, Tech.MissileBrain5, "heavy-warheads" },
        ["point-defence"] = new[] { Tech.PointDefence2, Tech.TrackingRadars, Tech.PointDefence3, Tech.CounterMissiles, "predictive-fire-control", "fleet-data-links" },
        ["shields"] = new[] { Tech.DeflectorGrid, Tech.ShieldHarmonics, Tech.ShieldsMk3, "adaptive-shields" },
        ["armour"] = new[] { Tech.HeavyArmour, Tech.ArmourMk3, "heat-sink-armour" },
        ["engines"] = new[] { Tech.EngineMk2, Tech.EngineMk3, "engine-mk4", "engine-mk5", Tech.EfficientDrives },
        ["lances"] = new[] { "free-electron-lasers", Beams.LanceIdea },
        ["jamming"] = new[] { "jamming" },
        ["spoofing"] = new[] { Tech.Spoofing },
        ["hijack"] = new[] { Countermeasures.Hijack },
        ["mines"] = new[] { Mines.Idea },
        ["fighters"] = new[] { StrikeCraft.Idea },
    };

    public static double Progress(World w, Side side, string id) => Economy.Of(w, side).RivalIdeas.GetValueOrDefault(id);

    public static RivalStage StageOf(World w, Side side, string id)
    {
        if (Tech.Has(w, side, id)) return RivalStage.None;
        var p = Progress(w, side, id);
        return p >= 1 - 1e-9 ? RivalStage.Unlocked : p >= D.StudiedAt ? RivalStage.Studied : p > 0 ? RivalStage.Seen : RivalStage.None;
    }

    public static bool Unlocked(World w, Side side, string id) => StageOf(w, side, id) == RivalStage.Unlocked;

    /// <summary>Time factor for researching an idea learned from the rival (Unlocked: less, you know it works).</summary>
    public static double TimeFactor(World w, Side side, string id) => Unlocked(w, side, id) ? 1 - D.UnlockedCut : 1;

    public static string StageName(RivalStage s) => s switch
    {
        RivalStage.Seen => "Seen",
        RivalStage.Studied => "Studied",
        RivalStage.Unlocked => "Unlocked",
        _ => "",
    };

    /// <summary>The rival ideas this side is learning, furthest on first.</summary>
    public static IEnumerable<(string Id, double Progress, RivalStage Stage)> Learning(World w, Side side) =>
        Economy.Of(w, side).RivalIdeas.Where(kv => !Tech.Has(w, side, kv.Key) && kv.Value > 0)
            .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value, StageOf(w, side, kv.Key)));

    /// <summary>Learn a little of a rival idea: only one the rival really has and this side lacks.</summary>
    public static void Teach(Simulation sim, Side side, string id, double amount, string how, Side? from = null)
    {
        var w = sim.World;
        if (!w.Loop || amount <= 0 || !GameData.ResearchById.TryGetValue(id, out var idea) || idea.Later) return;
        if (Tech.Has(w, side, id)) return;
        if (from is Side rival ? !Tech.Has(w, rival, id) : !Sides.Enemies(w, side).Any(r => Tech.Has(w, r, id))) return;
        var ideas = Economy.Of(w, side).RivalIdeas;
        var before = StageOf(w, side, id);
        ideas[id] = Math.Min(1, ideas.GetValueOrDefault(id) + amount);
        var after = StageOf(w, side, id);
        if (after == before) return;
        var text = after switch
        {
            RivalStage.Seen => $"Rival idea seen ({how}): something new in their kit",
            RivalStage.Studied => $"Rival idea studied: {idea.Name} ({how})",
            _ => $"Rival idea unlocked: {idea.Name}, ready to research without its prerequisites ({how})",
        };
        Intel.Note(w, side, IntelSource.Research, text);
        sim.Log.Info("intel", "Rival idea learned further", new { side = side.ToString(), id, stage = after.ToString(), progress = ideas[id], how });
    }

    /// <summary>A battle against the rival ended: the parts it showed teach the ideas behind them (not ones it held back all fight).</summary>
    public static void FromBattle(Simulation sim, Side side, Battle b, IEnumerable<string> parts, Side? rival = null)
    {
        var them = b.For(rival ?? Sides.MainEnemy(sim.World, side));
        var held = Secrecy.HeldAllFight(them);
        foreach (var part in parts.Distinct().OrderBy(p => p, StringComparer.Ordinal))
            foreach (var id in ByPart.GetValueOrDefault(part) ?? Array.Empty<string>())
                if (!held.Contains(id)) Teach(sim, side, id, D.BattleTeach, "seen in battle", rival ?? Sides.MainEnemy(sim.World, side));
    }

    /// <summary>The parts a ship of this class (and fit) carries, for salvage and captures.</summary>
    public static IEnumerable<string> PartsOf(ShipClass cls)
    {
        if (cls.Railguns > 0) yield return "railguns";
        if (cls.Missiles > 0) yield return "missiles";
        if (cls.PointDefence > 0) yield return "point-defence";
        if (cls.Shield > 0) yield return "shields";
        if (cls.Lances > 0) yield return "lances";
        yield return "armour";
        yield return "engines";
    }

    /// <summary>A rival wreck stripped: its parts teach, and so does its hull.</summary>
    public static void FromWreck(Simulation sim, Side side, string classId) => FromHull(sim, side, GameData.ShipClass(classId), D.WreckTeach, "wreck salvage");

    /// <summary>A rival ship captured: every part aboard teaches a lot.</summary>
    public static void FromCapture(Simulation sim, Side side, Ship ship, Side? from = null) => FromHull(sim, side, Fitting.Spec(ship), D.CaptureTeach, "captured ship", from);

    private static void FromHull(Simulation sim, Side side, ShipClass cls, double amount, string how, Side? from = null)
    {
        foreach (var part in PartsOf(cls))
            foreach (var id in ByPart[part])
                Teach(sim, side, id, amount, how, from);
        if (cls.Needs.Length > 0) Teach(sim, side, cls.Needs, amount, how, from);
    }

    /// <summary>Hourly; once a day the rival's colonies looked at closely teach the buildings standing there.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Tick % GameTime.TicksPerDay != 0) return;
        foreach (var side in Sides.InPlay(w))
            foreach (var file in Intel.Files(w, side))
            foreach (var fact in file.Planets.Where(p => w.Hour - p.Hour <= 24).OrderBy(p => p.BodyId))
            {
                if (w.FindBody(fact.BodyId) is not Body b) continue;
                foreach (var kind in b.Buildings.Distinct().OrderBy(k => k, StringComparer.Ordinal))
                    if (Planets.BuildingKind(kind)?.Needs is string needs) Teach(sim, side, needs, D.PlanetTeachPerDay, $"buildings seen on {b.Name}", file.About);
            }
    }

    // ---- Reach as a guess ----------------------------------------------------------------------

    /// <summary>After a battle: the farthest each rival fleet in it was seen to fire, by weapon.</summary>
    public static void NoteReach(World w, Side side, Battle b, Side? rival = null)
    {
        var file = Intel.Of(w, side, rival);
        var them = b.For(file.About);
        var fleets = b.Roster.Where(r => r.Side == file.About).Select(r => w.FindShip(r.Id)?.FleetId).OfType<int>().Distinct().OrderBy(i => i);
        foreach (var id in fleets)
        {
            var fact = file.Reach.Find(x => x.FleetId == id);
            if (fact == null) file.Reach.Add(fact = new ReachFact { FleetId = id });
            fact.Rail = Math.Max(fact.Rail, them.RailReach);
            fact.Missile = Math.Max(fact.Missile, them.MissileReach);
            fact.Lance = Math.Max(fact.Lance, them.LanceReach);
            fact.Hour = w.Hour;
            fact.Fights++;
        }
        file.Reach.RemoveAll(x => w.FindFleet(x.FleetId) == null && w.Hour - x.Hour > Intel.TechForgetDays * 24);
    }

    /// <summary>
    /// The side's guess at a rival fleet's reach: what it was seen to do if fought, else the most seen from
    /// other fleets of that rival (±25 %), else its own tech's reach (no contact). A rival that holds back
    /// or hides its range makes this guess short.
    /// </summary>
    public static ReachGuess Guess(World w, Side side, int fleetId)
    {
        var file = Intel.Of(w, side);
        var own = (Rail: Tech.RailRange(w, side), Missile: GameData.Combat.Missile.LaunchRangeMm, Lance: GameData.Weapons.L.RangeMm);
        if (file.Reach.Find(x => x.FleetId == fleetId) is ReachFact f)
            return new ReachGuess(f.Rail > 0 ? f.Rail : own.Rail, f.Missile > 0 ? f.Missile : own.Missile, f.Lance, $"fought {f.Fights} time{(f.Fights == 1 ? "" : "s")}", 0);
        if (file.Reach.Count > 0)
        {
            double Most(Func<ReachFact, double> pick, double fallback) => file.Reach.Max(pick) is var m && m > 0 ? m : fallback;
            return new ReachGuess(Most(x => x.Rail, own.Rail), Most(x => x.Missile, own.Missile), Most(x => x.Lance, 0), "guess from other fleets", 0.25);
        }
        return new ReachGuess(own.Rail, own.Missile, 0, "no contact: guess from your own tech", 0.25);
    }

    /// <summary>A side's true reach (what F7 shows beside the guess).</summary>
    public static ReachGuess Truth(World w, Side side) =>
        new(Tech.RailRange(w, side), GameData.Combat.Missile.LaunchRangeMm, Tech.Has(w, side, Beams.LanceIdea) ? GameData.Weapons.L.RangeMm : 0, "truth", 0);
}
