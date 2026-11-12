using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

/// <summary>
/// The newer buildings and stations that do more than make or use goods, and research that acts every
/// hour: academies train scientists, mass drivers throw goods between a planet and its moons, skimmers
/// fill gas giants' stores with helium-3, and ships mend in space (damage control, repair drones,
/// self-mending armour). Runs once per game hour in full games, after the planets.
/// </summary>
public static class Works
{
    public static void Update(Simulation sim)
    {
        var w = sim.World;
        using (SectionTimer.Time("works")) { Tech.Refresh(w); Academies(sim); MassDrivers(sim); Skimmers(w); Mend(w); }
        using (SectionTimer.Time("wrecks")) { Wounds.UpdateWrecks(sim); Lifeboats.Update(sim); }
        using (SectionTimer.Time("war")) War.Update(sim);
        using (SectionTimer.Time("hazards")) Hazards.Hourly(sim);
        using (SectionTimer.Time("events")) { Events.Update(sim); Events.Lasting(sim); }
        using (SectionTimer.Time("refits")) Refits.Update(sim);
        using (SectionTimer.Time("battle-rules")) BattleRules.Hourly(sim);
    }

    /// <summary>Hours an academy takes per batch of scientists (Scientist training: 30 % faster).</summary>
    public static double AcademyHours(World w, Side side) => Research.T.TrainDays * 24 * Tech.Factor(w, side, "scientist-training");

    /// <summary>Each working academy trains its batch, then hands the scientists to the side's smallest ready team.</summary>
    private static void Academies(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies.Where(b => b.Owner != null && b.PopK > 1))
        {
            var side = b.Owner!.Value;
            foreach (var p in Colonies.Built(b).Where(p => Planets.BuildingKind(p.Kind)?.TrainScientists > 0))
            {
                var data = Planets.BuildingKind(p.Kind)!;
                var output = Colonies.Output(w, b, p, data);
                if (output <= 0) continue;
                p.TrainHours += GameTime.HoursPerTick * Math.Min(1, output);
                if (p.TrainHours < AcademyHours(w, side)) continue;
                p.TrainHours = 0;
                var team = Economy.Of(w, side).Teams.OrderBy(t => t.Scientists).ThenBy(t => t.Id).FirstOrDefault();
                if (team == null) continue;
                team.Scientists += data.TrainScientists;
                b.PopK = Math.Max(0.05, b.PopK - data.TrainScientists / 1000);
                sim.Log.Info("research", $"{b.Name} academy trains scientists for {team.Name}", new { side = side.ToString(), bodyId = b.Id, team = team.Id, added = data.TrainScientists });
            }
        }
    }

    /// <summary>Bodies a mass driver here can throw goods to: the planet it orbits, or its moons, or the moons it shares a planet with.</summary>
    public static IEnumerable<Body> DriverReach(World w, Body b) => w.Bodies.Where(o => o.Id != b.Id && o.Owner == b.Owner && o.Board != null
        && (o.ParentId == b.Id || b.ParentId == o.Id || b.ParentId != null && o.ParentId == b.ParentId && b.Kind == BodyKind.Moon && o.Kind == BodyKind.Moon));

    /// <summary>
    /// Mass drivers: a working one throws up to its LaunchPerDay of goods a day to bodies in reach that
    /// ask for them (under their siege target, or waiting for materials), from its own spare.
    /// </summary>
    private static void MassDrivers(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies.Where(b => b.Owner != null && !Economy.Blockaded(w, b)).OrderBy(b => b.Id))
        {
            var budget = Colonies.Built(b).Where(p => Planets.BuildingKind(p.Kind)?.LaunchPerDay > 0)
                .Sum(p => Planets.BuildingKind(p.Kind)!.LaunchPerDay * Colonies.Output(w, b, p)) / 24;
            if (budget <= 1e-9) continue;
            foreach (var to in DriverReach(w, b).Where(o => !Economy.Blockaded(w, o)).OrderBy(o => o.Id))
                foreach (var g in Colonies.Goods)
                {
                    if (budget <= 1e-9) break;
                    var short_ = Math.Max(Colonies.Target(to, g.Id) - Colonies.Held(to, g.Id), Colonies.Want(to, g.Id) - Colonies.Held(to, g.Id));
                    var send = Math.Min(Math.Min(short_, Colonies.SpareOf(b, g.Id)), budget);
                    if (send <= 1e-9) continue;
                    b.Stock[g.Id] = Colonies.Held(b, g.Id) - send;
                    to.Stock[g.Id] = Colonies.Held(to, g.Id) + send;
                    budget -= send;
                }
        }
    }

    public const string Skimmer = "skimmer";

    /// <summary>Skimmer stations over gas giants put their helium-3 into the giant's store, where freighters pick it up.</summary>
    private static void Skimmers(World w)
    {
        foreach (var side in new[] { Side.Player, Side.Rival })
            foreach (var (st, b) in Economy.Stations(w, side, Skimmer))
                if (Economy.StationKind(st.Kind)?.Makes is { } makes && !Economy.Blockaded(w, b))
                    foreach (var (good, perDay) in makes)
                    {
                        var before = Colonies.Held(b, good);
                        b.Stock[good] = Math.Min(Math.Max(200, before), before + perDay / 24);
                        Ledger.In(w, side, good, b.Stock[good] - before, "skimmers");
                    }
    }

    /// <summary>
    /// Mending in space: damage control teams mend ships out of battle, repair drones mend them even in
    /// battle, and self-mending armour regrows. Each is a share of full armour a day (their research factor).
    /// </summary>
    private static void Mend(World w)
    {
        foreach (var f in w.Fleets)
        {
            var side = f.Side;
            var rate = (Tech.Has(w, side, "repair-drones") ? GameData.Research("repair-drones").Factor : 0)
                       + (Tech.Has(w, side, "self-mending-armour") ? GameData.Research("self-mending-armour").Factor : 0);
            if (!Heat.InBattle(w, f) && Tech.Has(w, side, "damage-control")) rate += GameData.Research("damage-control").Factor;
            // Every crew patches the other bars a little out of battle (up to Wounds.MendBarsTo); research does more, and mends hull.
            var bars = rate + (Heat.InBattle(w, f) ? 0 : Wounds.D.CrewMendPerDay);
            // Grown hulls (the Hive) heal by themselves, even in battle, while the ship has food aboard to grow on.
            var grow = Races.Of(w, side).HullHealPerDay;
            foreach (var s in w.ShipsOf(f))
            {
                var max = Tech.MaxArmour(w, s);
                var heal = rate + (grow > 0 && Races.Feeds(w, s) ? grow : 0);
                if (heal > 0 && s.Armour > 0 && s.Armour < max && !s.Crippled) s.Armour = Math.Min(max, s.Armour + max * heal / 24);
                if (bars > 0) Wounds.FieldMend(w, s, bars * Crews.MendFactor(w, s));
            }
        }
    }
}
