using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>A spark that fired for a side: the idea it brought into view (or sped up), why, and when.</summary>
public sealed record SparkNote(string Idea, string Why, double Hour, bool SpedUp);

/// <summary>
/// Sparks: some ideas name an event (a find, a sight, a hardship, a milestone or salvage) in their data.
/// When it happens to a side, the idea comes into view at once even if none of its needs is done, or,
/// if it is already queued, its remaining work is cut by a quarter. Each idea sparks once per side. The
/// rival follows the same rules. Events that are counts (slugs dodged, deliveries) are checked hourly;
/// one-off events (a landing beaten off, a fleet running dry) call Fire where they happen.
/// </summary>
public static class Sparks
{
    /// <summary>Share of an idea's remaining work a spark takes off when it is already queued.</summary>
    public const double Cut = 0.25;

    public static string Describe(string kind) => kind switch
    {
        "survey-2" => "Find: two planets surveyed",
        "survey-volcanic" => "Find: a volcanic world surveyed",
        "survey-rare-earths" => "Find: a rare-earth deposit",
        "survey-helium-3" => "Find: helium-3 in a survey",
        "overheat" => "Hardship: a ship overheats in battle",
        "heat-shutdown" => "Hardship: ships shut down from heat again and again",
        "slugs-dodged" => "Hardship: many slugs dodged",
        "missiles-stopped" => "Hardship: missiles fail to get through",
        "fleet-dry" => "Hardship: a fleet runs dry",
        "drone-lost" => "Hardship: a drone shot down",
        "landing-failed" => "Hardship: a landing fails",
        "blockading" => "Milestone: first blockade",
        "blockaded" => "Milestone: a planet of yours blockaded",
        "hungry" => "Hardship: a colony goes hungry",
        "unhappy" => "Hardship: a planet below 40 happiness",
        "damaged-far" => "Hardship: a damaged fleet far from home",
        "freighter-trips" => "Milestone: 10 freighter deliveries",
        "small-body-year" => "Milestone: a year on a small body",
        "uranium-out" => "Hardship: a plant runs out of uranium",
        "sight-brain-3" => "Sight: enemy smart seekers",
        "sight-brain-4" => "Sight: enemy pack missiles",
        "flagship-targeted" => "Hardship: your flagship targeted",
        "missile-salvage" => "Salvage: enemy missiles shot down and recovered",
        "freighter-raided" => "Hardship: a freighter or supply ship raided",
        "ship-crippled" => "Hardship: a ship crippled in battle",
        "wreck-salvaged" => "Salvage: a wreck stripped for materials",
        "yard-waiting" => "Hardship: the shipyard waits ten days for materials",
        _ => kind,
    };

    /// <summary>The ideas that list this spark.</summary>
    public static IEnumerable<ResearchData> IdeasFor(string kind) => GameData.Economy.Research.Where(r => r.Spark == kind);

    /// <summary>A spark event happened to a side: every idea that lists it comes into view, or is sped up if queued.</summary>
    public static void Fire(Simulation sim, Side side, string kind)
    {
        var w = sim.World;
        if (!w.Loop) return;
        var state = Economy.Of(w, side);
        foreach (var idea in IdeasFor(kind))
        {
            if (state.Sparked.Contains(idea.Id) || state.Researched.Contains(idea.Id)) continue;
            state.Sparked.Add(idea.Id);
            var queued = w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Research && o.Key == idea.Id).ToList();
            foreach (var o in queued)
            {
                o.HoursLeft = Math.Max(0, o.HoursLeft - o.HoursLeft * Cut);
            }
            state.SparkNotes.Add(new SparkNote(idea.Id, Describe(kind), w.Hour, queued.Count > 0));
            if (state.SparkNotes.Count > 60) state.SparkNotes.RemoveAt(0);
            sim.Log.Info("research", queued.Count > 0 ? $"{side} spark speeds up {idea.Name}" : $"{side} spark: {idea.Name} comes into view",
                new { side = side.ToString(), idea = idea.Id, kind });
        }
    }

    /// <summary>Hourly: the sparks that are counts or states rather than one-off events.</summary>
    public static void Check(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop || w.Tick % 6 != 0) return;
        foreach (var side in new[] { Side.Player, Side.Rival })
        {
            var t = Economy.Of(w, side).Totals;
            var enemy = Combat.Other(side);
            var mine = w.Bodies.Where(b => b.Owner == side).ToList();
            if (t.Overheats >= 1) Fire(sim, side, "overheat");
            if (t.Overheats >= 6) Fire(sim, side, "heat-shutdown");
            if (t.SlugsDodged >= 40) Fire(sim, side, "slugs-dodged");
            if (t.MissilesStopped >= 24) Fire(sim, side, "missiles-stopped");
            if (t.Deliveries >= 10) Fire(sim, side, "freighter-trips");
            if (t.MissilesShotDown >= 20) Fire(sim, side, "missile-salvage");
            if (w.Bodies.Any(b => b.Owner == enemy && Planets.Blockader(w, b) == side)) Fire(sim, side, "blockading");
            if (mine.Any(b => Economy.Blockaded(w, b))) Fire(sim, side, "blockaded");
            if (mine.Any(b => b.Hungry && b.PopK > 0)) Fire(sim, side, "hungry");
            if (w.Rules.Happiness && mine.Any(b => b.PopK > 0 && b.Happiness < 40)) Fire(sim, side, "unhappy");
            if (w.Day >= 365 && mine.Any(b => b.PopK > 0 && b.Kind is BodyKind.Asteroid or BodyKind.Moon && Planets.TypeOf(b).Cap <= 30)) Fire(sim, side, "small-body-year");
            if (mine.Any(b => Colonies.Built(b).Any(p => p.Kind == "power-plant") && Colonies.Held(b, "uranium") < 0.01 && Colonies.Held(b, "helium-3") < 0.01))
                Fire(sim, side, "uranium-out");
            if (w.Battles.Count > 0 && w.MissileBrain[(int)enemy] >= 3) Fire(sim, side, "sight-brain-3");
            if (w.Battles.Count > 0 && w.MissileBrain[(int)enemy] >= 4) Fire(sim, side, "sight-brain-4");
            if (Economy.Of(w, side).FlagshipId is int flag && w.FindShip(flag) is Ship fs && fs.Armour < Tech.MaxArmour(w, fs) * 0.95) Fire(sim, side, "flagship-targeted");
            if (Economy.HomeOf(w, side) is Body home)
                foreach (var f in w.Fleets.Where(f => f.Side == side && f.ShipIds.Count > 0))
                    if (Combat.ArmourShare(w, f) < 0.6 && Vec2.Distance(f.Position, home.Position) > Physics.MmPerAu)
                    {
                        Fire(sim, side, "damaged-far");
                        break;
                    }
        }
    }

    /// <summary>A side finished surveying a body: survey-count, volcanic, rare-earth and helium-3 finds.</summary>
    public static void OnSurvey(Simulation sim, Side side, Body b)
    {
        var w = sim.World;
        if (w.Bodies.Count(x => x.Surveyed[(int)side]) >= 2) Fire(sim, side, "survey-2");
        if (b.Type == "volcanic") Fire(sim, side, "survey-volcanic");
        if (b.Board?.Deposits.Any(d => d.Mineral == "rare-earths") == true) Fire(sim, side, "survey-rare-earths");
        if (b.Type is "gas" or "ice-giant" || b.Board?.Deposits.Any(d => d.Mineral == "helium-3") == true) Fire(sim, side, "survey-helium-3");
    }
}
