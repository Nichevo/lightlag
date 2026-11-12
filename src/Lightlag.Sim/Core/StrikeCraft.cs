using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// Planet fighters and bombers (Strike craft research, fighter base building). Local defence only: each
/// base keeps a fighter and a bomber squadron that sortie at enemy fleets within reach of the planet.
/// Bombers hit ships hard; fighters escort them (fewer losses), add a little damage, cut bombing of the
/// planet and pick off missiles aimed at ships in orbit there. Enemy point defence shoots squadrons down;
/// lost squadrons are rebuilt over a few days from alloys in the planet's store.
/// </summary>
public static class StrikeCraft
{
    public const string Idea = "strike-craft";
    public const string Base = "fighter-base";
    private static StrikeCraftData D => GameData.Weapons.S;

    /// <summary>Fighter base squadrons of each kind on this planet (one fighter and one bomber per base).</summary>
    public static int Bases(Body b) => b.Owner == null ? 0 : Planets.Count(b, Base);

    public static int Fighters(Body b) => Math.Max(0, Bases(b) - b.FightersLost);
    public static int Bombers(Body b) => Math.Max(0, Bases(b) - b.BombersLost);

    /// <summary>Share of missiles aimed at a ship in this fleet that the planet's fighters pick off (0 when not in orbit at a base).</summary>
    public static double MissileScreen(World w, Fleet f)
    {
        if (f.OrbitBodyId is not int id || w.FindBody(id) is not Body b || b.Owner != f.Side || !Economy.InOrbitAt(f, b)) return 0;
        return Math.Min(0.5, Fighters(b) * D.FighterMissileKill);
    }

    /// <summary>Extra cut to bombing from the planet's fighters (a divisor above 1).</summary>
    public static double BombCover(Body b) => 1 + Fighters(b) * D.FighterBombShield;

    /// <summary>Hourly: rebuild lost squadrons, then send what is ready at the nearest enemy fleet in reach.</summary>
    public static void Hourly(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var b in w.Bodies.Where(b => Bases(b) > 0 || b.FightersLost + b.BombersLost > 0).ToList())
        {
            b.FightersLost = Math.Clamp(b.FightersLost, 0, Bases(b));
            b.BombersLost = Math.Clamp(b.BombersLost, 0, Bases(b));
            Rebuild(sim, b);
            if (b.Owner is not Side owner || w.Hour - b.LastSortieHour < D.SortieHours - 1e-9) continue;
            var target = w.Fleets.Where(f => f.Side != owner && f.ShipIds.Count > 0 && Vec2.Distance(f.Position, b.Position) <= D.RangeMm
                                             && Sensors.Sees(w, owner, f))
                .OrderBy(f => Vec2.Distance(f.Position, b.Position)).ThenBy(f => f.Id).FirstOrDefault();
            if (target == null || Bombers(b) + Fighters(b) == 0) continue;
            Sortie(sim, b, owner, target);
        }
    }

    private static void Rebuild(Simulation sim, Body b)
    {
        if (b.FightersLost + b.BombersLost == 0) { b.RebuildHours = 0; return; }
        b.RebuildHours += GameTime.HoursPerTick;
        if (b.RebuildHours < D.RebuildDays * 24 || Colonies.Held(b, "alloys") < D.RebuildAlloys) return;
        b.Stock["alloys"] = Colonies.Held(b, "alloys") - D.RebuildAlloys;
        Ledger.Out(sim.World, b.Owner, "alloys", D.RebuildAlloys, "squadrons");
        b.RebuildHours = 0;
        if (b.BombersLost >= b.FightersLost) b.BombersLost--; else b.FightersLost--;
        sim.Log.Info("planets", $"{b.Name} rebuilds a squadron", new { bodyId = b.Id, fightersLost = b.FightersLost, bombersLost = b.BombersLost });
    }

    private static void Sortie(Simulation sim, Body b, Side owner, Fleet target)
    {
        var w = sim.World;
        b.LastSortieHour = w.Hour;
        var fighters = Fighters(b);
        var bombers = Bombers(b);
        var pd = w.ShipsOf(target).Where(s => !s.Out(Module.PointDefence)).Sum(s => Fitting.Spec(s).PointDefence * Wounds.DefenceFactor(s));
        var loss = Math.Min(D.MaxLoss, D.LossPerPd * pd);
        var lostF = 0;
        var lostB = 0;
        var dealt = 0.0;
        var battle = w.ActiveBattle;
        Economy.Of(w, owner).Totals.Sorties++;
        if (battle != null) battle.For(owner).Sorties++;
        sim.AddEffect(new CombatEffect(w.Hour, EffectKind.Sortie, b.Position, target.Position, owner));
        for (var i = 0; i < bombers + fighters; i++)
        {
            var bomber = i < bombers;
            var escorted = bomber && fighters > 0;
            if (sim.Rng.NextDouble() < loss * (escorted ? 1 - D.EscortCut : 1) * (bomber ? 1 : 0.5))
            {
                if (bomber) lostB++; else lostF++;
                continue;
            }
            var ship = w.ShipsOf(target).Where(s => !s.Crippled).OrderByDescending(s => Fitting.Spec(s).Cost).ThenBy(s => s.Id).FirstOrDefault()
                       ?? w.ShipsOf(target).OrderBy(s => s.Id).FirstOrDefault();
            if (ship == null) break;
            var amount = bomber ? D.BomberDamage : D.FighterDamage;
            dealt += amount;
            Combat.Strike(sim, ship, amount, owner);
        }
        b.FightersLost += lostF;
        b.BombersLost += lostB;
        if (lostF + lostB > 0)
        {
            Economy.Of(w, owner).Totals.SquadronsLost += lostF + lostB;
            if (battle != null) battle.For(owner).SquadronsLost += lostF + lostB;
        }
        if (battle != null)
            BattleRecords.Mark(w, battle, owner, MarkKind.Decision, $"{b.Name} sends {bombers} bomber and {fighters} fighter squadron{(bombers + fighters == 1 ? "" : "s")} at {target.Name}"
                                                                  + (lostF + lostB > 0 ? $"; {lostF + lostB} lost" : ""));
        sim.Log.Info("planets", $"{b.Name} sends squadrons at {target.Name}", new { bodyId = b.Id, fleetId = target.Id, bombers, fighters, lostF, lostB, dealt, pd });
    }
}
