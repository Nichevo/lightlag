namespace Lightlag.Sim.Core;

/// <summary>How a fact was learned.</summary>
public enum IntelSource { Sensors, Battle, Wreck, Prisoners, Capture, Research, Guess }

/// <summary>A rival fleet as last known: kept after its sighting fades, losing sureness with age.</summary>
public sealed class FleetFact
{
    public int FleetId { get; set; }
    public string Name { get; set; } = "";
    public int Ships { get; set; }

    /// <summary>Each ship's class from the last close look (empty: never seen close).</summary>
    public List<string> Classes { get; set; } = new();
    public Vec2 Position { get; set; }
    public Vec2 Velocity { get; set; }
    public double FirstHour { get; set; }
    public double Hour { get; set; }
    public IntelSource Source { get; set; }

    /// <summary>All cargo ships (freighters, tankers): not a threat, but tells you about lanes.</summary>
    public bool Cargo { get; set; }
}

/// <summary>A rival planet: who holds it is plain to see; people and industry only when looked at.</summary>
public sealed class PlanetFact
{
    public int BodyId { get; set; }
    public double PopK { get; set; }
    public double Industry { get; set; }
    public double Hour { get; set; }
}

/// <summary>A piece of rival technology seen in use, salvaged or captured.</summary>
public sealed class TechFact
{
    /// <summary>railguns, missiles, point-defence, shields, engines or armour.</summary>
    public string Part { get; set; } = "";
    public int Level { get; set; }
    public IntelSource Source { get; set; }
    public double Hour { get; set; }
}

/// <summary>Something fed in from outside the sensors (prisoners, captures, salvage), for the screen's log.</summary>
public sealed record IntelNote(double Hour, IntelSource Source, string Text);

/// <summary>
/// One side's file on a rival: fleets, planets, technology and habits it has learned, kept after sightings
/// fade. Sureness falls with age. The intel level (0-4) rises with sensor time, battles, salvage, prisoners
/// and captures, and slowly falls without contact. Kept in the side's SideState, so saves keep it.
/// </summary>
public sealed class IntelFile
{
    public Side About { get; set; } = Side.Rival;
    public double Points { get; set; }
    public double LastContactHour { get; set; } = -1e9;
    public List<FleetFact> Fleets { get; set; } = new();
    public List<PlanetFact> Planets { get; set; } = new();
    public List<TechFact> Tech { get; set; } = new();

    /// <summary>Personality tells, each with how often it was seen (keys in Intel.HabitText).</summary>
    public Dictionary<string, int> Habits { get; set; } = new();
    public List<IntelNote> Notes { get; set; } = new();
    public int LastBattleId { get; set; }
    public int LossSpotsSeen { get; set; }
    public double SalvagedSeen { get; set; }
    public List<int> ClaimsSeen { get; set; } = new();

    /// <summary>"Their file on you", as you can guess it: your fleets the rival has probably seen, and when.</summary>
    public Dictionary<int, double> SeenUs { get; set; } = new();
    public double TheirPointsGuess { get; set; }
}

/// <summary>
/// The shared intelligence file. Every system that wants to know about a rival asks Intel.Of rather than
/// reading raw sightings, so they agree, and learning from battles, salvage and captures counts everywhere.
/// The rival keeps its file on you by the same rules.
/// </summary>
public static class Intel
{
    public static readonly string[] LevelNames = { "Unknown", "Seen", "Studied", "Understood", "Read" };
    public static readonly string[] LevelMeaning =
    {
        "Nothing beyond contact blips",
        "Fleet counts and where",
        "Classes, weapons seen in battle, rough strength",
        "Fits, research fields, its habits (personality tells)",
        "Plans in motion: massing, strikes, its next research",
    };

    /// <summary>Points needed for each level.</summary>
    public static readonly double[] LevelPoints = { 0, 10, 40, 100, 200 };

    public const double SensorPointsPerFleetHour = 0.1;
    public const double SensorPointsMaxPerHour = 0.3;
    public const double BattlePoints = 8;
    public const double SalvagePoints = 4;
    public const double FadePerDay = 0.02;
    public const double FleetForgetDays = 30;
    public const double PlanetLookMm = 0.2 * Physics.MmPerAu;
    public const double SeenUsMm = 0.25 * Physics.MmPerAu;
    public const double TechForgetDays = 120;

    public static readonly string[] Parts = { "railguns", "missiles", "point-defence", "shields", "engines", "armour" };

    /// <summary>The side's file on a rival (the other side, while there are only two).</summary>
    public static IntelFile Of(World w, Side side, Side? rival = null)
    {
        var file = Economy.Of(w, side).Intel;
        file.About = rival ?? Combat.Other(side);
        return file;
    }

    public static int Level(IntelFile f) => LevelFor(f.Points);

    public static int LevelFor(double points)
    {
        var level = 0;
        for (var i = 1; i < LevelPoints.Length; i++) if (points >= LevelPoints[i]) level = i;
        return level;
    }

    /// <summary>How sure a fact of this age is: 1 when fresh, 0 after the forget time.</summary>
    public static double Sure(World w, double hour, double forgetDays) => Math.Clamp(1 - (w.Hour - hour) / (forgetDays * 24), 0, 1);

    public static double Sure(World w, FleetFact f) => Sure(w, f.Hour, FleetForgetDays);

    /// <summary>The fleets believed to exist, freshest first (cargo runs included unless asked).</summary>
    public static IEnumerable<FleetFact> FleetsKnown(World w, Side side, bool cargo = false) =>
        Of(w, side).Fleets.Where(f => cargo || !f.Cargo).OrderByDescending(f => f.Hour).ThenBy(f => f.FleetId);

    /// <summary>Rival warship fleets seen within the last days near any leg (used for route risk).</summary>
    public static int FleetsNear(World w, Side side, IEnumerable<(Vec2 A, Vec2 B)> legs, double days, double radiusMm)
    {
        var list = legs.ToList();
        return Of(w, side).Fleets.Count(f => !f.Cargo && w.Hour - f.Hour < days * 24 && list.Any(g => Lanes.DistToSegment(f.Position, g.A, g.B) < radiusMm));
    }

    /// <summary>Rival fleets seen within the last hours (fresh contacts, for warnings).</summary>
    public static IEnumerable<FleetFact> Recent(World w, Side side, double hours) =>
        Of(w, side).Fleets.Where(f => w.Hour - f.Hour < hours).OrderBy(f => f.FleetId);

    /// <summary>Estimated rival warships: a range that narrows as the intel level rises.</summary>
    public static (int Low, int High) EstimatedWarships(World w, Side side)
    {
        var file = Of(w, side);
        var level = Level(file);
        var known = file.Fleets.Where(f => !f.Cargo).ToList();
        var sure = known.Where(f => Sure(w, f) >= 0.5).Sum(f => f.Ships);
        var all = known.Sum(f => f.Ships);
        var margin = (int)Math.Ceiling(all * (0.6 - 0.12 * level)) + (level < 2 ? 2 : 0);
        return (sure, all + Math.Max(0, margin));
    }

    /// <summary>The rival's control share as you can tell it, and how far off it may be (±).</summary>
    public static (double Share, double Plus) EstimatedControl(World w, Side side)
    {
        var mine = Planets.Control(w, side).Share;
        return (1 - mine, (4 - Level(Of(w, side))) * 0.03);
    }

    /// <summary>The rival's planets and the Industry they are worth (the system's figures, known to all).</summary>
    public static (int Planets, double Industry) EstimatedHoldings(World w, Side side)
    {
        var rival = Of(w, side).About;
        var held = Economy.Claimable(w).Where(b => b.Owner == rival).ToList();
        return (held.Count, held.Sum(b => b.Industry));
    }

    /// <summary>The rival's level in a part as seen (0: not seen yet).</summary>
    public static TechFact? TechSeen(World w, Side side, string part) => Of(w, side).Tech.Find(t => t.Part == part);

    public static string PartName(string part) => part switch
    {
        "railguns" => "Railguns",
        "missiles" => "Missiles",
        "point-defence" => "Point defence",
        "shields" => "Shields",
        "engines" => "Engines",
        "armour" => "Armour",
        _ => part,
    };

    public static string LevelName(string part, int level) => part switch
    {
        "missiles" => $"brain {level}",
        "shields" => level == 0 ? "none" : $"Mk {Roman(level)}",
        _ => $"Mk {Roman(level)}",
    };

    private static string Roman(int n) => n switch { 1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", _ => n.ToString() };

    /// <summary>The true level of a side's part, from its research (what a look at its ships would show).</summary>
    public static int TrueLevel(World w, Side side, string part)
    {
        bool Has(string id) => Lightlag.Sim.Core.Tech.Has(w, side, id);
        return part switch
        {
            "railguns" => Has("railgun-mk5") ? 5 : Has("railgun-mk4") ? 4 : Has(Core.Tech.RailgunMk3) ? 3 : Has(Core.Tech.FastRailguns) ? 2 : 1,
            "missiles" => Has("missile-brain-5") ? 5 : Has(Core.Tech.MissileBrain4) ? 4 : Has(Core.Tech.MissileBrain3) ? 3 : Has(Core.Tech.MissileBrain2) ? 2 : 1,
            "point-defence" => Has(Core.Tech.PointDefence3) ? 3 : Has(Core.Tech.PointDefence2) ? 2 : 1,
            "shields" => Has(Core.Tech.ShieldsMk3) ? 3 : Has(Core.Tech.DeflectorGrid) ? 2 : w.Ships.Any(s => s.Side == side && Heat.MaxShield(w, s) > 0) ? 1 : 0,
            "engines" => Has("engine-mk5") ? 5 : Has("engine-mk4") ? 4 : Has("engine-mk3") ? 3 : Has("engine-mk2") ? 2 : 1,
            "armour" => Has(Core.Tech.ArmourMk3) ? 3 : Has(Core.Tech.HeavyArmour) ? 2 : 1,
            _ => 0,
        };
    }

    /// <summary>A research idea that answers what the rival has been seen to field, with why.</summary>
    public static IEnumerable<(string Idea, string Why)> Counters(World w, Side side)
    {
        var mine = side;
        bool Has(string id) => Core.Tech.Has(w, mine, id);
        foreach (var t in Of(w, side).Tech.OrderBy(t => Array.IndexOf(Parts, t.Part)))
        {
            var seen = $"they have {LevelName(t.Part, t.Level)} {PartName(t.Part).ToLowerInvariant()}";
            switch (t.Part)
            {
                case "missiles" when t.Level >= 3 && !Has("predictive-fire-control"): yield return ("predictive-fire-control", seen); break;
                case "missiles" when t.Level >= 2 && !Has(Core.Tech.PointDefence2): yield return (Core.Tech.PointDefence2, seen); break;
                case "missiles" when t.Level >= 2 && !Has(Core.Tech.PointDefence3): yield return (Core.Tech.PointDefence3, seen); break;
                case "railguns" when t.Level >= 2 && !Has(Core.Tech.HeavyArmour): yield return (Core.Tech.HeavyArmour, seen); break;
                case "railguns" when t.Level >= 3 && !Has(Core.Tech.ArmourMk3): yield return (Core.Tech.ArmourMk3, seen); break;
                case "shields" when t.Level >= 1 && !Has(Core.Tech.RailgunMk3): yield return (Core.Tech.RailgunMk3, seen); break;
                case "point-defence" when t.Level >= 2 && !Has(Core.Tech.MissileBrain2): yield return (Core.Tech.MissileBrain2, seen); break;
                case "point-defence" when t.Level >= 3 && !Has(Core.Tech.MissileBrain3): yield return (Core.Tech.MissileBrain3, seen); break;
                case "engines" when t.Level >= 3 && !Has(Core.Tech.LongSensors): yield return (Core.Tech.LongSensors, seen); break;
            }
        }
    }

    public static string HabitText(string key) => key switch
    {
        "raids-cargo" => "Raids your cargo runs",
        "pulls-back" => "Pulls back from fights",
        "small-groups" => "Small groups, three ships or fewer",
        "big-fleets" => "Big fleets, five ships or more",
        "missile-heavy" => "Fights with missiles first",
        "gun-heavy" => "Fights with railguns first",
        "claims" => "Claims planets",
        "battles" => "Battles fought",
        _ => key,
    };

    /// <summary>The best guess at the rival's temperament from its tells, with how sure (0-1).</summary>
    public static (string Name, double Sure, string Counter)? Personality(IntelFile f)
    {
        int H(string k) => f.Habits.GetValueOrDefault(k);
        var scores = new (string Name, double Score, string Counter)[]
        {
            ("Raider", 2 * H("raids-cargo") + H("small-groups"), "escort runs, picket the lanes, bait with a decoy convoy"),
            ("Expander", 2 * H("claims") + H("big-fleets"), "strike its new colonies before they grow, blockade the far ones"),
            ("Turtle", 2 * H("pulls-back") + (H("battles") == 0 ? 0 : 1), "out-build it; it seldom comes for you, so expand fast"),
        };
        var total = scores.Sum(s => s.Score);
        if (total < 3) return null;
        var best = scores.OrderByDescending(s => s.Score).First();
        return (best.Name, best.Score / total, best.Counter);
    }

    // ---- Learning --------------------------------------------------------------------------------

    /// <summary>
    /// Feed in a fact from outside the sensors: prisoners, a captured ship or colony, salvage, a research find.
    /// Adds the points, logs the note, and with a part named, records the rival's true level of it.
    /// </summary>
    public static void Note(World w, Side side, IntelSource source, string text, double points = 0, string? part = null)
    {
        var f = Of(w, side);
        f.Points += Math.Max(0, points);
        f.Notes.Add(new IntelNote(w.Hour, source, text));
        if (f.Notes.Count > 40) f.Notes.RemoveAt(0);
        if (part != null) LearnTech(w, side, f, part, source);
    }

    private static void LearnTech(World w, Side side, IntelFile f, string part, IntelSource source)
    {
        var level = TrueLevel(w, f.About, part);
        var fact = f.Tech.Find(t => t.Part == part);
        if (fact == null) f.Tech.Add(fact = new TechFact { Part = part });
        fact.Level = level;
        fact.Source = source;
        fact.Hour = w.Hour;
    }

    private static void Habit(IntelFile f, string key, int by = 1) => f.Habits[key] = f.Habits.GetValueOrDefault(key) + by;

    private static bool IsCargo(int ships, List<string> classes) =>
        ships > 0 && classes.Count >= ships && classes.All(c => GameData.ShipClasses.TryGetValue(c, out var k) && k.Cargo > 0 && k.Railguns == 0 && k.Missiles == 0);

    /// <summary>Once a game hour: copy fresh sightings into the file, learn from finished battles, age and forget.</summary>
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        if (w.Sides.Length < 2) return;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var f = Of(w, side);
            var rival = f.About;
            var visible = 0;
            foreach (var s in w.Sightings.Where(s => s.SeenBy == side && !s.Own).OrderBy(s => s.FleetId))
            {
                var fact = f.Fleets.Find(x => x.FleetId == s.FleetId);
                if (fact == null)
                {
                    fact = new FleetFact { FleetId = s.FleetId, FirstHour = s.Hour };
                    f.Fleets.Add(fact);
                    if (s.Ships <= 3) Habit(f, "small-groups");
                    else if (s.Ships >= 5) Habit(f, "big-fleets");
                }
                if (s.Hour < fact.Hour) continue;
                fact.Name = s.Name;
                fact.Ships = s.Ships;
                if (s.Classes.Count > 0) fact.Classes = s.Classes.ToList();
                fact.Position = s.Position;
                fact.Velocity = s.Velocity;
                fact.Hour = s.Hour;
                fact.Source = IntelSource.Sensors;
                fact.Cargo = IsCargo(fact.Ships, fact.Classes);
                if (s.Visible) visible++;
            }
            if (visible > 0)
            {
                f.Points += Math.Min(SensorPointsMaxPerHour, visible * SensorPointsPerFleetHour);
                f.LastContactHour = w.Hour;
            }
            // Fleets seen to be gone (destroyed or merged in sight: their sighting went with them) and old facts.
            f.Fleets.RemoveAll(x => Sure(w, x) <= 0 || (x.Hour >= w.Hour - 1 && !w.Sightings.Any(s => s.SeenBy == side && s.FleetId == x.FleetId)
                                                          && w.FindFleet(x.FleetId) == null));
            f.Tech.RemoveAll(t => Sure(w, t.Hour, TechForgetDays) <= 0);

            // Battles that ended: what the rival used, how it fought.
            foreach (var b in w.Battles.Where(b => b.Id > f.LastBattleId && b.EndHour != null).OrderBy(b => b.Id).ToList())
            {
                f.LastBattleId = b.Id;
                var them = b.For(rival);
                var us = b.For(side);
                if (them.ShipsIn == 0 || us.ShipsIn == 0) continue;
                foreach (var r in b.Roster.Where(r => r.Side == side))
                    if (w.Ships.Find(x => x.Id == r.Id) is Ship ship)
                    {
                        ship.Battles++;
                        ship.EnemyLostInBattles += them.ShipsLost.Count;
                    }
                f.Points += BattlePoints;
                f.TheirPointsGuess += BattlePoints;
                f.LastContactHour = w.Hour;
                Habit(f, "battles");
                if (them.Withdrew) Habit(f, "pulls-back");
                if (them.MissilesFired > them.RailShots) Habit(f, "missile-heavy");
                else if (them.RailShots > 0) Habit(f, "gun-heavy");
                if (them.RailShots > 0) LearnTech(w, side, f, "railguns", IntelSource.Battle);
                if (them.MissilesFired > 0) LearnTech(w, side, f, "missiles", IntelSource.Battle);
                if (us.ShotDown > 0) LearnTech(w, side, f, "point-defence", IntelSource.Battle);
                if (them.ShieldSoaked > 0) LearnTech(w, side, f, "shields", IntelSource.Battle);
                if (us.DamageDealt > 0) LearnTech(w, side, f, "armour", IntelSource.Battle);
                LearnTech(w, side, f, "engines", IntelSource.Battle);
            }
            if (w.Battles.Count > 0 && w.Battles[^1].EndHour != null) f.LastBattleId = Math.Max(f.LastBattleId, w.Battles[^1].Id);

            // Cargo lost to raiders, salvage taken, planets the rival started to claim.
            var losses = w.LossSpots.Count(l => l.Side == side);
            if (losses > f.LossSpotsSeen) Habit(f, "raids-cargo", losses - f.LossSpotsSeen);
            f.LossSpotsSeen = losses;
            if (w.Loop)
            {
                var salvaged = Economy.Of(w, side).Totals.Salvaged;
                if (salvaged > f.SalvagedSeen + 1e-9)
                {
                    f.Points += SalvagePoints;
                    f.SalvagedSeen = salvaged;
                }
            }
            foreach (var b in w.Bodies.Where(b => b.ClaimSide == rival && !f.ClaimsSeen.Contains(b.Id)).OrderBy(b => b.Id))
            {
                f.ClaimsSeen.Add(b.Id);
                Habit(f, "claims");
            }
            f.ClaimsSeen.RemoveAll(id => w.FindBody(id) is not Body cb || cb.ClaimSide != rival);

            // Rival planets: who holds them is plain; people and industry when one of ours is near enough to look.
            var lookers = w.Fleets.Where(x => x.Side == side && x.Position.IsFinite).Select(x => x.Position)
                .Concat(w.Bodies.Where(b => b.Owner == side).Select(b => b.Position)).ToList();
            foreach (var b in w.Bodies.Where(b => b.Owner == rival).OrderBy(b => b.Id))
            {
                var fact = f.Planets.Find(p => p.BodyId == b.Id);
                if (fact == null) f.Planets.Add(fact = new PlanetFact { BodyId = b.Id, Hour = -1e9 });
                if (lookers.Any(p => Vec2.Distance(p, b.Position) < PlanetLookMm))
                {
                    fact.PopK = b.PopK;
                    fact.Industry = w.Loop ? Planets.IndustryPerDay(w, b) : b.Industry;
                    fact.Hour = w.Hour;
                }
            }
            f.Planets.RemoveAll(p => w.FindBody(p.BodyId)?.Owner != rival);

            // Their file on you: our fleets near a rival fleet in sight now have probably been seen.
            var near = 0;
            foreach (var mine in w.Fleets.Where(x => x.Side == side && x.Position.IsFinite).OrderBy(x => x.Id))
            {
                if (!f.Fleets.Any(x => x.Hour >= w.Hour && Vec2.Distance(x.Position, mine.Position) < SeenUsMm)) continue;
                f.SeenUs[mine.Id] = w.Hour;
                near++;
            }
            foreach (var gone in f.SeenUs.Keys.Where(id => w.FindFleet(id) == null).ToList()) f.SeenUs.Remove(gone);
            f.TheirPointsGuess += Math.Min(SensorPointsMaxPerHour, near * SensorPointsPerFleetHour);

            // Knowledge fades without contact, a little each day.
            if (w.Tick % GameTime.TicksPerDay == 0 && w.Hour - f.LastContactHour > 24)
            {
                f.Points *= 1 - FadePerDay;
                f.TheirPointsGuess *= 1 - FadePerDay;
            }
        }
    }

    // ---- Debug ----------------------------------------------------------------------------------

    /// <summary>Belief against the truth, for F7 and reports: what the side thinks of its rival and what is so.</summary>
    public static List<string> BeliefVsTruth(World w, Side side)
    {
        var f = Of(w, side);
        var rival = f.About;
        var (low, high) = EstimatedWarships(w, side);
        var trueWarships = w.Ships.Count(s => s.Side == rival && (Fitting.Spec(s).Railguns > 0 || Fitting.Spec(s).Missiles > 0));
        var trueFleets = w.Fleets.Count(x => x.Side == rival);
        var lines = new List<string>
        {
            $"{side} on {rival}: intel level {Level(f)} ({f.Points:0} points)",
            $"Warships: thinks {low}-{high}, truly {trueWarships}",
            $"Fleets on file: {f.Fleets.Count} ({f.Fleets.Count(x => w.FindFleet(x.FleetId) == null)} gone), truly {trueFleets}",
        };
        foreach (var t in f.Tech.OrderBy(t => Array.IndexOf(Parts, t.Part)))
            lines.Add($"{PartName(t.Part)}: thinks {LevelName(t.Part, t.Level)}, truly {LevelName(t.Part, TrueLevel(w, rival, t.Part))}");
        if (Personality(f) is var (name, sure, _))
            lines.Add($"Temperament: guesses {name} ({sure:P0})" + (Mind.Of(w, rival) is MindState m ? $", truly {m.Personality}" : ""));
        var theirs = Of(w, rival);
        lines.Add($"Guess of their level on us: {LevelFor(f.TheirPointsGuess)}, truly {Level(theirs)}");
        return lines;
    }

    /// <summary>Report text: both sides' belief against the truth.</summary>
    public static string Describe(World w) =>
        "# Intelligence: belief vs truth\n\n" + string.Join("\n\n", new[] { Side.Player, Side.Rival }.Select(s => string.Join("\n", BeliefVsTruth(w, s).Select(l => "- " + l))));
}
