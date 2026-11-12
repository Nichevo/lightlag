using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// Damage to ships: five bars (hull, weapons, engines, defence, crew) instead of one armour pool. Every hit
/// takes hull; a share of it also lands on one other bar, picked by the weapon and the firing rule
/// (railguns spread their harm, missiles hit crew and defences, aiming for engines hits engines). A bar
/// at zero knocks out its parts (any number of them). Hull at zero cripples the ship: it can't fire,
/// limps at a quarter speed and leaves the fight; more hits destroy it and leave a wreck to salvage.
/// Damage control mends the bars part way in space; a yard or dry dock mends everything, for materials.
/// </summary>
public static class Wounds
{
    public static WoundsData D => GameData.Combat.Wounds ?? new WoundsData();

    public enum Bar { Hull, Weapons, Engines, Defence, Crew }

    public static readonly Bar[] Bars = { Bar.Hull, Bar.Weapons, Bar.Engines, Bar.Defence, Bar.Crew };

    public static string BarName(Bar b) => b switch { Bar.Hull => "hull", Bar.Weapons => "weapons", Bar.Engines => "engines", Bar.Defence => "defence", _ => "crew" };

    /// <summary>A bar as a share of full (hull: armour over full armour).</summary>
    public static double Share(World w, Ship s, Bar b) => b switch
    {
        Bar.Hull => Math.Clamp(s.Armour / Math.Max(1e-9, Tech.MaxArmour(w, s)), 0, 1),
        Bar.Weapons => s.Weapons,
        Bar.Engines => s.Engines,
        Bar.Defence => s.Defence,
        _ => s.Crew,
    };

    private static void Set(Ship s, Bar b, double v)
    {
        v = Math.Clamp(v, 0, 1);
        switch (b)
        {
            case Bar.Weapons: s.Weapons = v; break;
            case Bar.Engines: s.Engines = v; break;
            case Bar.Defence: s.Defence = v; break;
            case Bar.Crew: s.Crew = v; break;
        }
    }

    /// <summary>Speed and thrust: a crippled ship limps at a quarter, knocked-out engines at half, worn engines a little slower.</summary>
    public static double SpeedFactor(Ship s) =>
        s.Crippled ? D.CrippledSpeed : s.Knocked.Contains(Module.Engines) ? D.KnockedSpeed : 0.6 + 0.4 * s.Engines;

    /// <summary>Railgun damage with worn weapons.</summary>
    public static double WeaponFactor(Ship s) => 0.5 + 0.5 * s.Weapons;

    /// <summary>Point defence and shields with worn defences.</summary>
    public static double DefenceFactor(Ship s) => 0.4 + 0.6 * s.Defence;

    /// <summary>Reload pace with a hurt crew.</summary>
    public static double CrewFactor(Ship s) => 0.5 + 0.5 * s.Crew;

    /// <summary>Worst of the five bars (for the fleet panel and pull-back rules).</summary>
    public static double Worst(World w, Ship s) => Bars.Min(b => Share(w, s, b));

    /// <summary>Which bar takes a hit's share: by weapon and firing rule.</summary>
    private static Bar PickBar(Simulation sim, bool missile, bool engines)
    {
        if (engines) return Bar.Engines;
        var roll = sim.Rng.NextDouble();
        // railguns: weapons .3, engines .25, defence .25, crew .2; missiles: crew .35, defence .3, weapons .2, engines .15
        return missile
            ? roll < 0.35 ? Bar.Crew : roll < 0.65 ? Bar.Defence : roll < 0.85 ? Bar.Weapons : Bar.Engines
            : roll < 0.3 ? Bar.Weapons : roll < 0.55 ? Bar.Engines : roll < 0.8 ? Bar.Defence : Bar.Crew;
    }

    /// <summary>The parts a bar at zero knocks out.</summary>
    private static IEnumerable<Module> PartsOf(Ship s, Bar b)
    {
        var cls = Fitting.Spec(s);
        switch (b)
        {
            case Bar.Weapons:
                if (cls.Railguns > 0) yield return Module.Railguns;
                if (cls.Missiles > 0) yield return Module.Missiles;
                break;
            case Bar.Engines: yield return Module.Engines; break;
            case Bar.Defence: if (cls.PointDefence > 0) yield return Module.PointDefence; break;
        }
    }

    private static void Knock(Simulation sim, BattleSide? lost, Ship s, Module m)
    {
        if (s.Knocked.Contains(m)) return;
        s.Knocked.Add(m);
        lost?.Knocked.Add($"{s.Name}: {Combat.ModuleName(m)}");
        if (sim.World.Loop) Economy.Of(sim.World, s.Side).Totals.PartsKnocked++;
        sim.Log.Info("combat", $"{s.Name} loses its {Combat.ModuleName(m)}", new { shipId = s.Id, module = m });
    }

    /// <summary>A hit lands (after shields and heat): hull, one other bar, maybe a part, maybe crippled or destroyed.</summary>
    public static void Hit(Simulation sim, Battle? battle, Ship target, double amount, Side by, bool missile, bool engines)
    {
        var w = sim.World;
        var max = Tech.MaxArmour(w, target);
        var lost = battle?.For(target.Side);
        if (target.Crippled)
        {
            target.CrippleLeft -= amount;
            if (target.CrippleLeft <= 0) Kill(sim, battle, target, by);
            return;
        }
        target.Armour -= amount;
        var bar = PickBar(sim, missile, engines);
        var before = Share(w, target, bar);
        Set(target, bar, before - amount / Math.Max(1e-9, max) * D.BarShare);
        target.Shield = Math.Min(target.Shield, Heat.MaxShield(w, target)); // worn defences hold less shield
        if (before > 0 && Share(w, target, bar) <= 0)
            foreach (var m in PartsOf(target, bar)) Knock(sim, lost, target, m);
        else if (sim.Rng.NextDouble() < (engines ? Heat.D.EnginesModuleChance : GameData.Combat.ModuleHitChance))
        {
            // A lucky hit knocks out a part outright (aiming for engines: the engines).
            var parts = PartsOf(target, Bar.Weapons).Concat(PartsOf(target, Bar.Defence)).Append(Module.Engines).Where(m => !target.Knocked.Contains(m)).ToList();
            if (parts.Count > 0) Knock(sim, lost, target, engines ? Module.Engines : parts[sim.Rng.Range(0, parts.Count)]);
        }
        if (target.Armour > 0) return;
        var over = -target.Armour;
        target.Armour = 0;
        target.CrippleLeft = max * D.CrippleHold - over;
        if (target.CrippleLeft <= 0)
        {
            Kill(sim, battle, target, by); // a big enough hit finishes it outright
            return;
        }
        target.Crippled = true;
        lost?.Crippled.Add(target.Name);
        if (battle != null) BattleRecords.Mark(w, battle, target.Side, MarkKind.ShipLost, $"{target.Name} crippled");
        if (w.Loop) Economy.Of(w, target.Side).Totals.ShipsCrippled++;
        Sparks.Fire(sim, target.Side, "ship-crippled");
        sim.Log.Info("combat", $"{target.Name} is crippled", new { shipId = target.Id, by = by.ToString(), left = target.CrippleLeft });
    }

    private static void Kill(Simulation sim, Battle? battle, Ship target, Side by)
    {
        var w = sim.World;
        battle?.For(target.Side).ShipsLost.Add(target.Name);
        if (battle != null) BattleRecords.Mark(w, battle, target.Side, MarkKind.ShipLost, $"{target.Name} destroyed");
        if (w.Loop)
        {
            Economy.Of(w, target.Side).Totals.ShipsLost++;
            Economy.Of(w, by).Totals.ShipsKilled++;
        }
        if (w.FleetOf(target) is Fleet f) f.FightLost++;
        sim.Log.Info("combat", $"{target.Name} is destroyed", new { shipId = target.Id, by = by.ToString() });
        Combat.Destroy(sim, target);
    }

    /// <summary>A destroyed ship leaves a drifting wreck holding part of its materials; its crew get off in pods or a lifeboat (see Lifeboats).</summary>
    public static void LeaveWreck(World w, Ship s)
    {
        if (!w.Loop) return;
        var cls = GameData.ShipClass(s.ClassId);
        var salvage = (cls.Materials ?? new()).Where(m => m.Value * D.WreckShare >= 0.5)
            .ToDictionary(m => m.Key, m => Math.Round(m.Value * D.WreckShare, 1));
        var f = w.FleetOf(s);
        var orbit = f is { Hold: HoldStyle.Orbit, Flight: null, OrbitBodyId: int id } ? w.FindBody(id) : null;
        Lifeboats.Launch(w, s, f, orbit);
        if (salvage.Count == 0) return;
        w.Wrecks.Add(new Wreck
        {
            Id = w.NextId++, Side = s.Side, ClassId = s.ClassId, Name = s.Name, Position = s.Position,
            Velocity = orbit == null ? f?.Velocity ?? Vec2.Zero : Vec2.Zero, Hour = w.Hour, Salvage = salvage,
            BodyId = orbit?.Id, Offset = orbit == null ? Vec2.Zero : s.Position - orbit.Position,
        });
    }

    /// <summary>
    /// Hourly: wrecks drift and fade, and pods run out of air. A fleet out of battle near one (and not
    /// clearing up after its own battle: see AfterBattle) picks up the pods and strips the wreck at once.
    /// </summary>
    public static void UpdateWrecks(Simulation sim)
    {
        var w = sim.World;
        foreach (var wr in w.Wrecks.ToList())
        {
            if (wr.BodyId is int b && w.FindBody(b) is Body body) wr.Position = body.Position + wr.Offset;
            else wr.Position += wr.Velocity * GameTime.HoursPerTick;
            if (wr.CrewK > 0 && w.Hour >= wr.PodsUntilHour)
            {
                sim.Log.Info("combat", $"The pods of {wr.Name} run out of air", new { wreck = wr.Id, crewK = Math.Round(wr.CrewK, 3) });
                wr.CrewK = 0;
            }
            if (w.Hour - wr.Hour > D.WreckDays * 24 || wr.Salvage.Count == 0 && wr.CrewK <= 0) { w.Wrecks.Remove(wr); continue; }
            var f = w.Fleets.Where(f => f.ShipIds.Count > 0 && !Heat.InBattle(w, f) && !AfterBattle.Busy(w, f) && !Boarders.Grappled(w, f) && Vec2.Distance(f.Position, wr.Position) <= D.SalvageRangeMm)
                .OrderBy(f => Vec2.Distance(f.Position, wr.Position)).ThenBy(f => f.Id).FirstOrDefault();
            if (f == null) continue;
            Rescue(sim, f, wr);
            Strip(sim, f, wr);
            if (wr.Salvage.Count == 0 && wr.CrewK <= 0) w.Wrecks.Remove(wr);
        }
    }

    /// <summary>Picks up a wreck's pods: your own crew keep their rank and wait at home for a new ship; the enemy's become prisoners.</summary>
    public static void Rescue(Simulation sim, Fleet f, Wreck wr)
    {
        var w = sim.World;
        if (wr.CrewK <= 1e-9) return;
        if (wr.Side == f.Side)
        {
            Economy.Of(w, f.Side).ReserveCrews.Add(wr.CrewXp);
            Economy.Of(w, f.Side).Totals.CrewsRescued += wr.CrewK;
            sim.Log.Info("combat", $"{f.Name} picks up the crew of {wr.Name}", new { fleetId = f.Id, wreck = wr.Id, crewK = Math.Round(wr.CrewK, 3), xp = Math.Round(wr.CrewXp, 2) });
        }
        else
        {
            sim.Log.Info("combat", $"{f.Name} takes the crew of {wr.Name} prisoner", new { fleetId = f.Id, wreck = wr.Id, crewK = Math.Round(wr.CrewK, 3) });
            Spoils.TakePrisoners(sim, f.Side, wr.CrewK, wr.Name);
        }
        wr.CrewK = 0;
    }

    /// <summary>Strips a wreck: the materials go to the side's nearest store; an enemy wreck also gives a scrap of intel on its parts.</summary>
    public static void Strip(Simulation sim, Fleet f, Wreck wr)
    {
        var w = sim.World;
        if (wr.Salvage.Count == 0) return;
        var store = w.Bodies.Where(b => b.Owner == f.Side && b.Board != null).OrderBy(b => Vec2.Distance(b.Position, f.Position)).ThenBy(b => b.Id).FirstOrDefault();
        if (store == null) return;
        var total = 0.0;
        foreach (var (good, amount) in wr.Salvage.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            store.Stock[good] = Colonies.Held(store, good) + amount;
            Ledger.In(w, f.Side, good, amount, "salvage");
            total += amount;
        }
        var haul = wr.Salvage;
        wr.Salvage = new();
        if (w.Loop) Economy.Of(w, f.Side).Totals.Salvaged += total;
        sim.Log.Info("combat", $"{f.Name} salvages the wreck of {wr.Name}", new { fleetId = f.Id, wreck = wr.Id, store = store.Name, salvage = haul });
        Sparks.Fire(sim, f.Side, "wreck-salvaged");
        if (wr.Side != f.Side)
        {
            Spoils.Scrap(sim, f.Side, $"parts from the wreck of {wr.Name}", IntelSource.Wreck, part: true);
            RivalTech.FromWreck(sim, f.Side, wr.ClassId);
        }
    }

    /// <summary>What mending this ship fully at a yard will cost: alloys for the hull, electronics for the other bars.</summary>
    public static Dictionary<string, double> RepairBill(World w, Ship s)
    {
        var cls = GameData.ShipClass(s.ClassId);
        var hull = 1 - Share(w, s, Bar.Hull);
        var worn = Bars.Skip(1).Sum(b => 1 - Share(w, s, b)) / 4;
        var bill = new Dictionary<string, double>();
        if (hull > 1e-6) bill["alloys"] = (cls.Materials?.GetValueOrDefault("alloys") ?? 0) * D.RepairAlloys * hull;
        if (worn > 1e-6) bill["electronics"] = (cls.Materials?.GetValueOrDefault("electronics") ?? 0) * D.RepairElectronics * worn;
        return bill;
    }

    /// <summary>
    /// At a yard or dry dock: hull and bars mend at the repair pace and knocked-out parts are fixed. Mending
    /// costs materials from the yard's store (alloys for hull, electronics for the other bars, a share of
    /// the class's build materials per full mend); without them it goes at a quarter of the pace.
    /// </summary>
    public static void YardRepair(World w, Ship s, double hours, Body? at)
    {
        var max = Tech.MaxArmour(w, s);
        var cls = GameData.ShipClass(s.ClassId);
        var hullShare = Math.Min(1 - Share(w, s, Bar.Hull), GameData.Combat.RepairPerDay * hours / 24);
        var barsShare = Math.Min(1, D.BarRepairPerDay * hours / 24);
        var worn = Bars.Skip(1).Sum(b => Math.Min(barsShare, 1 - Share(w, s, b))) / 4;
        var alloys = (cls.Materials?.GetValueOrDefault("alloys") ?? 0) * D.RepairAlloys * hullShare;
        var elec = (cls.Materials?.GetValueOrDefault("electronics") ?? 0) * D.RepairElectronics * worn;
        var pace = 1.0;
        var crewBefore = s.Crew;
        if (w.Loop && at != null && (alloys > 0 || elec > 0))
        {
            if (Colonies.Held(at, "alloys") >= alloys && Colonies.Held(at, "electronics") >= elec)
            {
                at.Stock["alloys"] = Colonies.Held(at, "alloys") - alloys;
                at.Stock["electronics"] = Colonies.Held(at, "electronics") - elec;
                Ledger.Out(w, s.Side, "alloys", alloys, "ship repairs");
                Ledger.Out(w, s.Side, "electronics", elec, "ship repairs");
            }
            else pace = D.NoMaterialsPace;
        }
        s.Armour = Math.Min(max, s.Armour + max * hullShare * pace);
        foreach (var b in Bars.Skip(1)) Set(s, b, Share(w, s, b) + barsShare * pace);
        if (w.Loop) Crews.Replaced(s, crewBefore, s.Crew);
        if (s.Crippled && s.Armour >= max * 0.25) { s.Crippled = false; s.CrippleLeft = 0; }
        if (s.Armour >= max * 0.5) s.Knocked.RemoveAll(m => Bars.Skip(1).All(b => !PartsOf(s, b).Contains(m) || Share(w, s, b) >= 0.5));
    }

    /// <summary>Damage control in space: bars mend up to MendBarsTo, and parts whose bar is back over a third start again.</summary>
    public static void FieldMend(World w, Ship s, double rate)
    {
        if (s.Crippled) return;
        foreach (var b in Bars.Skip(1))
            if (Share(w, s, b) < D.MendBarsTo) Set(s, b, Math.Min(D.MendBarsTo, Share(w, s, b) + rate / 24));
        s.Knocked.RemoveAll(m => Bars.Skip(1).Any(b => PartsOf(s, b).Contains(m) && Share(w, s, b) >= 0.34));
    }
}
