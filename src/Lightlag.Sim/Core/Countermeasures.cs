using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// The missile counter-race beyond point defence: jamming leads guided missiles off (Combat.ResolveMissile),
/// spoofing fools them, and hijack takes over a guided missile's brain and turns it on its own side. Dumb
/// salvos (brain 1) carry no receiver to take over or jam, but are easy to dodge and shoot down; IFF codes
/// on the launching side make hijack much harder. Each loop has its answer.
/// </summary>
public static class Countermeasures
{
    public const string Hijack = "missile-hijack";
    public const string Iff = "iff-codes";
    private static CountersData C => GameData.Weapons.C;

    /// <summary>Chance a missile of this brain from this side is hijacked by the other side (0 if the target can't).</summary>
    public static double HijackChance(World w, Side launcher, int brain)
    {
        var target = Combat.Other(launcher);
        if (!w.Loop || brain < 2 || !Tech.Has(w, target, Hijack)) return 0;
        return C.Hijack(brain) * (Tech.Has(w, launcher, Iff) ? 1 - C.IffHijackCut : 1);
    }

    /// <summary>
    /// A missile closing on its target within hijack reach: the target's side may take it over. The missile
    /// changes side and goes for the nearest ship of the side that fired it. Returns its new target, or null.
    /// </summary>
    public static Ship? TryHijack(Simulation sim, Battle battle, Missile m, Ship target)
    {
        var w = sim.World;
        var chance = HijackChance(w, m.Side, m.Brain);
        if (chance <= 0 || sim.Rng.NextDouble() >= chance) return null;
        var from = m.Side;
        var turned = w.FindShip(m.FromShipId) is Ship shooter && shooter.Side == from ? shooter
            : w.Ships.Where(s => s.Side == from).OrderBy(s => Vec2.Distance(s.Position, m.Position)).ThenBy(s => s.Id).FirstOrDefault();
        if (turned == null) return null;
        battle.For(from).Hijacked++;
        if (w.Loop) Economy.Of(w, from).Totals.MissilesHijacked++;
        m.Side = target.Side;
        m.TargetShipId = turned.Id;
        BattleRecords.Mark(w, battle, target.Side, MarkKind.Decision, $"A {from} missile is hijacked and turned on {turned.Name}");
        sim.Log.Info("combat", "A missile is hijacked", new { missileId = m.Id, from = from.ToString(), newTargetId = turned.Id, m.Brain });
        return turned;
    }

    /// <summary>Hourly: computer-run sides fire dumb salvos once they know the enemy hijacks missiles and their own have no IFF.</summary>
    public static void AiHourly(Simulation sim)
    {
        var w = sim.World;
        if (!w.Loop) return;
        foreach (var mind in w.Minds)
        {
            var dumb = Intel.TechSeen(w, mind.Side, "hijack") is { Level: >= 1 } && !Tech.Has(w, mind.Side, Iff);
            foreach (var f in w.Fleets.Where(f => f.Side == mind.Side && f.DumbSalvos != dumb))
            {
                f.DumbSalvos = dumb;
                sim.Log.Info("ai", $"{f.Name} switches to {(dumb ? "dumb" : "guided")} salvos", new { fleetId = f.Id, side = mind.Side.ToString() });
            }
        }
    }
}

/// <summary>Guided or dumb missile salvos for a fleet.</summary>
public sealed record SetSalvoGuidanceCommand(int FleetId, bool Dumb) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Salvo guidance") is not Fleet fleet) return;
        fleet.DumbSalvos = Dumb;
        sim.Log.Info("fleet", $"{fleet.Name} fires {(Dumb ? "dumb" : "guided")} salvos", new { FleetId, Dumb, Source });
    }
}

/// <summary>Throw one heat sink overboard from one ship (the battle view's per-ship button).</summary>
public sealed record EjectShipSinkCommand(int FleetId, int ShipId) : GameCommand, IFleetCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (GetFleet(world, sim, FleetId, "Heat sink") is not Fleet fleet) return;
        if (world.FindShip(ShipId) is not Ship ship || ship.FleetId != fleet.Id)
        {
            sim.Log.Warn("fleet", "Heat sink for a ship not in the fleet", new { FleetId, ShipId, Source });
            return;
        }
        Heat.EjectSink(sim, fleet, ship, "ordered");
    }
}
