using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>Ship variants and the build bill, Q-ships, crewed vs AI-run ships, crew strain, and escape pods and lifeboats.</summary>
public class ShipsCrewsTests
{
    private static Simulation NewGame(ulong seed = 8)
    {
        var sim = Starts.Classic(Simulation.NewGame(Scenarios.TestSystem, seed, GameLog.Memory()));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        return sim;
    }

    private static void Learn(Simulation sim, Side side, params string[] ids)
    {
        foreach (var id in ids)
            if (!Economy.Of(sim.World, side).Researched.Contains(id)) Economy.Of(sim.World, side).Researched.Add(id);
    }

    private static Fleet NewFleet(Simulation sim, Side side, Vec2 at, params string[] classes) =>
        Fleets.Create(sim.World, side, classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList(), at);

    /// <summary>A ship made only to be lost or looked at: out of the world again so the rule checks don't see a ship with no fleet.</summary>
    private static Ship Loose(Simulation sim, Side side, string classId, Vec2 at)
    {
        var s = Economy.NewShip(sim.World, side, classId);
        sim.World.Ships.Remove(s);
        s.Position = at;
        return s;
    }

    private static Vec2 FarAway(Simulation sim) =>
        sim.World.Bodies.Single(b => b.Kind == BodyKind.Star).Position + new Vec2(0, 60000);

    // ---- Variants -----------------------------------------------------------------------------

    [Fact]
    public void Saved_variant_is_built_and_priced_by_its_fit()
    {
        var sim = NewGame();
        var w = sim.World;
        var cls = GameData.ShipClass("gun-frigate");
        var fit = Designer.FitFor(w, Side.Player, cls.Id, Designer.StandardId);
        var u = Enumerable.Range(0, fit.Count).FirstOrDefault(i => cls.SlotList[i].Type == "D", -1);
        Assert.True(u >= 0, "the gun frigate needs a defence slot for this test");
        var mine = fit.ToList();
        mine[u] = "armour-plate";
        sim.Issue(new SaveVariantCommand(Side.Player, cls.Id, null, "Heavy", mine));
        sim.StepMany(1);
        var d = Assert.Single(Designer.Of(w, Side.Player, cls.Id), x => x.Name == "Heavy");
        Assert.False(d.Auto);

        // The bill: hull and Standard, then what the variant changes; armour costs alloys.
        var bill = Designer.Bill(cls, d.Fit, aiRun: false);
        Assert.True(bill.Count >= 2);
        Assert.True(Designer.Materials(cls, d.Fit, false).GetValueOrDefault("alloys") > Designer.Materials(cls, fit, false).GetValueOrDefault("alloys"));

        // A yard order for it carries its price and launches a ship that keeps to it.
        sim.Issue(new QueueOrderCommand(Side.Player, OrderKind.Ship, cls.Id, DesignId: d.Id));
        sim.StepMany(1);
        var order = w.Orders.Last(o => o.Side == Side.Player && o.Key == cls.Id);
        Assert.Equal(d.Id, order.DesignId);
        Assert.NotNull(order.FitCost);
        var ship = Economy.NewShip(w, Side.Player, cls.Id, null, d.Id);
        w.Ships.Remove(ship);
        Assert.Equal(d.Id, ship.DesignId);
        Assert.Equal(d.Fit, Designer.FitFor(w, Side.Player, cls.Id, ship.DesignId));

        // Deleting it ("Reset to Auto") sends its ships and orders back to the Standard.
        sim.Issue(new DeleteVariantCommand(Side.Player, d.Id));
        sim.StepMany(1);
        Assert.Null(order.DesignId);
        Assert.Empty(Designer.Of(w, Side.Player, cls.Id).Where(x => x.Name == "Heavy"));
    }

    [Fact]
    public void Default_variant_is_what_yards_build_and_names_must_be_unique()
    {
        var sim = NewGame();
        var w = sim.World;
        var fit = Designer.FitFor(w, Side.Player, "gun-frigate", Designer.StandardId);
        sim.Issue(new SaveVariantCommand(Side.Player, "gun-frigate", null, "Mine", fit));
        sim.Issue(new SaveVariantCommand(Side.Player, "gun-frigate", null, "mine", fit));
        sim.StepMany(1);
        var d = Assert.Single(Designer.Of(w, Side.Player, "gun-frigate"), x => !x.Auto);
        sim.Issue(new SetDefaultVariantCommand(Side.Player, "gun-frigate", d.Id));
        sim.StepMany(1);
        Assert.Equal(d.Id, Designer.DefaultFor(w, Side.Player, "gun-frigate"));
        Assert.Equal(d.Id, Designer.ForBuild(w, Side.Player, "gun-frigate", null));
        sim.Issue(new HandToAutoCommand(Side.Player, "gun-frigate"));
        sim.StepMany(1);
        Assert.Equal(Designer.StandardId, Designer.DefaultFor(w, Side.Player, "gun-frigate"));
    }

    [Fact]
    public void Best_fit_only_uses_parts_researched_and_still_fits()
    {
        var sim = NewGame();
        var w = sim.World;
        foreach (var cls in GameData.ShipClasses.Values)
        {
            var best = Designer.Best(w, Side.Player, cls, Fitting.StandardFit(cls));
            Assert.Equal(Fitting.StandardFit(cls).Count, best.Count);
            Assert.Null(Fitting.CanFit(w, Side.Player, cls, best));
            Assert.Equal(0, Designer.Behind(w, Side.Player, cls, best));
        }
    }

    [Fact]
    public void Models_list_the_standard_and_variants()
    {
        var sim = NewGame();
        var w = sim.World;
        var fit = Designer.FitFor(w, Side.Player, "gun-frigate", Designer.StandardId);
        sim.Issue(new SaveVariantCommand(Side.Player, "gun-frigate", null, "Mine", fit));
        sim.StepMany(1);
        var models = Designer.Models(w, Side.Player, "gun-frigate");
        Assert.Contains(models, m => m.DesignId == Designer.StandardId || m.DesignName == "Standard");
        Assert.Contains(models, m => m.DesignName == "Mine" && m.Status == "Special");
    }

    // ---- Q-ships ------------------------------------------------------------------------------

    [Fact]
    public void Q_ship_needs_concealed_mounts_and_firing_makes_raiders_wary()
    {
        var sim = NewGame();
        var w = sim.World;
        var food = GameData.ShipClasses.Values.Where(c => c.CargoKind == "food" && c.SlotList.Any(s => s.Type == "U")).OrderBy(c => c.Id, StringComparer.Ordinal).First();
        Assert.Null(Designer.RoleVariant(w, Side.Player, food, Designer.QShip));
        Learn(sim, Side.Player, GameData.Research(Designer.ConcealedMounts).Needs.Append(Designer.ConcealedMounts).ToArray());
        var v = Designer.RoleVariant(w, Side.Player, food, Designer.QShip);
        Assert.NotNull(v);
        Assert.Contains("hidden-gun", v!.Value.Fit);

        // A Q-ship has less hold than the plain freighter.
        Assert.True(Fitting.Compute(food, v.Value.Fit).Cargo < food.Cargo);

        var ship = Loose(sim, Side.Player, food.Id, FarAway(sim));
        ship.Fit = v.Value.Fit.ToList();
        Assert.True(Designer.Hidden(ship));
        Designer.Revealed(sim, ship, Side.Rival);
        Assert.True(Designer.Wary(w, Side.Rival, ship.Position + new Vec2(100, 0)));
        Assert.False(Designer.Wary(w, Side.Rival, ship.Position + new Vec2(Designer.WaryRadiusMm * 3, 0)));
        Assert.False(Designer.Wary(w, Side.Player, ship.Position));
        sim.StepMany((int)(Designer.WaryDays * GameTime.TicksPerDay) + GameTime.TicksPerDay);
        Assert.False(Designer.Wary(w, Side.Rival, ship.Position));
    }

    // ---- Crewed or AI-run ---------------------------------------------------------------------

    [Fact]
    public void Ai_run_needs_ai_cores_and_never_for_people_carriers()
    {
        var sim = NewGame();
        var w = sim.World;
        sim.Issue(new SetCrewingCommand(Side.Player, "freighter", true));
        sim.StepMany(1);
        Assert.False(Designer.AiRun(w, Side.Player, "freighter"));
        Learn(sim, Side.Player, "ai-cores");
        sim.Issue(new SetCrewingCommand(Side.Player, "freighter", true));
        sim.Issue(new SetCrewingCommand(Side.Player, "colony-ship", true));
        sim.StepMany(1);
        Assert.True(Designer.AiRun(w, Side.Player, "freighter"));
        Assert.False(Designer.AiRun(w, Side.Player, "colony-ship"));
        Assert.NotNull(Designer.CanRunOnAi(w, Side.Player, "troop-ship"));

        var s = Loose(sim, Side.Player, "freighter", FarAway(sim));
        Assert.True(s.AiRun);
        Assert.False(Crews.Crewed(s));
        Assert.Equal(0, SupplyLines.MaxFood(s));
        // AI cores cost electronics on the bill instead of a crew.
        var cls = GameData.ShipClass("freighter");
        var fit = Fitting.StandardFit(cls);
        Assert.True(Designer.Materials(cls, fit, true).GetValueOrDefault("electronics") > Designer.Materials(cls, fit, false).GetValueOrDefault("electronics"));
    }

    [Fact]
    public void Change_over_at_refit_sends_the_crew_home_or_mans_the_ship()
    {
        var sim = NewGame();
        var w = sim.World;
        Learn(sim, Side.Player, "ai-cores");
        var s = Loose(sim, Side.Player, "freighter", FarAway(sim));
        s.Xp = 5;
        Assert.True(Crews.Crewed(s));
        var reserve = Economy.Of(w, Side.Player).ReserveCrews.Count;
        Economy.Of(w, Side.Player).AiRun.Add("freighter");
        Crews.ChangeOver(w, s, null);
        Assert.True(s.AiRun);
        Assert.Equal(reserve + 1, Economy.Of(w, Side.Player).ReserveCrews.Count);
        Economy.Of(w, Side.Player).AiRun.Remove("freighter");
        Crews.ChangeOver(w, s, null);
        Assert.False(s.AiRun);
        Assert.True(Crews.Crewed(s));
    }

    // ---- Strain -------------------------------------------------------------------------------

    [Fact]
    public void Dodging_strains_crews_but_not_ai_run_ships_and_rest_mends_it()
    {
        var sim = NewGame();
        var w = sim.World;
        var crewed = Economy.NewShip(w, Side.Player, "gun-frigate");
        var ai = Economy.NewShip(w, Side.Player, "gun-frigate");
        ai.AiRun = true;
        ai.Food = 0;
        for (var i = 0; i < 100; i++)
        {
            Crews.Strain(w, crewed, hardBurn: true, closeness: 1);
            Crews.Strain(w, ai, hardBurn: true, closeness: 1);
        }
        Assert.True(crewed.Strain > 0.3);
        Assert.Equal(0, ai.Strain);
        // A strained crew aims and dodges worse.
        Assert.True(Crews.StrainedDodge(w, crewed, 0.5) > 0.5);
        Assert.Equal(0.5, Crews.StrainedDodge(w, ai, 0.5));
        var before = crewed.Strain;
        var fleet = Fleets.Create(w, Side.Player, new List<Ship> { crewed, ai }, FarAway(sim));
        sim.StepMany(24);
        Assert.True(crewed.Strain < before);
        Assert.NotNull(fleet);
    }

    // ---- Lifeboats ----------------------------------------------------------------------------

    [Fact]
    public void Big_hulls_launch_a_lifeboat_and_small_ones_pods_ai_run_ships_none()
    {
        var sim = NewGame();
        var w = sim.World;
        var at = FarAway(sim);
        var cruiser = Loose(sim, Side.Player, "cruiser", at);
        var boat = Lifeboats.Launch(w, cruiser, null, null);
        Assert.NotNull(boat);
        Assert.True(boat!.Boat);
        Assert.NotNull(boat.HeadingBodyId);
        var frigate = Loose(sim, Side.Player, "gun-frigate", at);
        var pods = Lifeboats.Launch(w, frigate, null, null);
        Assert.NotNull(pods);
        Assert.False(pods!.Boat);
        Assert.Null(pods.HeadingBodyId);
        var ai = Loose(sim, Side.Player, "gun-frigate", at);
        ai.AiRun = true;
        Assert.Null(Lifeboats.Launch(w, ai, null, null));
        Assert.True(pods.AirUntilHour < boat.AirUntilHour);
    }

    [Fact]
    public void Pods_left_alone_run_out_of_air()
    {
        var sim = NewGame();
        var w = sim.World;
        var s = Loose(sim, Side.Player, "gun-frigate", FarAway(sim));
        var pods = Lifeboats.Launch(w, s, null, null)!;
        var crew = pods.CrewK;
        // So far out no fleet of ours can get there before the air runs out.
        Assert.All(w.Fleets.Where(f => f.Side == Side.Player && f.ShipIds.Count > 0), f => Assert.NotNull(Lifeboats.CanSend(w, f, pods)));
        sim.StepMany((int)Math.Ceiling(Lifeboats.AirLeftHours(w, pods) / GameTime.HoursPerTick) + 2);
        Assert.DoesNotContain(pods, w.Lifeboats);
        Assert.True(Economy.Of(w, Side.Player).CrewsLostK >= crew - 1e-9);
    }

    [Fact]
    public void Own_fleet_rescues_survivors_and_enemy_takes_them_prisoner()
    {
        var sim = NewGame();
        var w = sim.World;
        var at = FarAway(sim);
        var a = Loose(sim, Side.Player, "gun-frigate", at);
        a.Xp = 7;
        var ours = Lifeboats.Launch(w, a, null, null)!;
        var reserve = Economy.Of(w, Side.Player).ReserveCrews.Count;
        NewFleet(sim, Side.Player, ours.Position, "gun-frigate");
        sim.StepMany(1);
        Assert.DoesNotContain(ours, w.Lifeboats);
        Assert.Equal(reserve + 1, Economy.Of(w, Side.Player).ReserveCrews.Count);
        Assert.Contains(7.0, Economy.Of(w, Side.Player).ReserveCrews);

        var at2 = at + new Vec2(20000, 0);
        var b = Loose(sim, Side.Rival, "gun-frigate", at2);
        var theirs = Lifeboats.Launch(w, b, null, null)!;
        var prisoners = Economy.Of(w, Side.Player).Spoils.PrisonersK;
        NewFleet(sim, Side.Player, theirs.Position, "gun-frigate");
        sim.StepMany(1);
        Assert.DoesNotContain(theirs, w.Lifeboats);
        Assert.True(Economy.Of(w, Side.Player).Spoils.PrisonersK > prisoners);
    }

    [Fact]
    public void Rescue_order_sends_a_fleet_out_and_back()
    {
        var sim = NewGame();
        var w = sim.World;
        var home = w.Fleets.First(f => f.Side == Side.Player && f.OrbitBodyId != null && f.ShipIds.Count > 0);
        var s = Loose(sim, Side.Player, "gun-frigate", home.Position + new Vec2(400, 0));
        var pods = Lifeboats.Launch(w, s, null, null)!;
        Assert.Null(Lifeboats.CanSend(w, home, pods));
        sim.Issue(new RescueCommand(Side.Player, home.Id, pods.Id));
        sim.StepMany(1);
        Assert.NotNull(home.Rescue);
        Assert.Equal(home.Id, pods.RescuerFleetId);
        for (var i = 0; i < 24 * 10 && w.Lifeboats.Contains(pods); i++) sim.StepMany(1);
        Assert.DoesNotContain(pods, w.Lifeboats);
        Assert.Null(home.Rescue);
        Assert.NotNull(home.Flight); // heading back to where it came from
    }

    // ---- Saves --------------------------------------------------------------------------------

    [Fact]
    public void Variants_crewing_strain_and_lifeboats_survive_a_save()
    {
        var sim = NewGame();
        var w = sim.World;
        Learn(sim, Side.Player, "ai-cores");
        var fit = Designer.FitFor(w, Side.Player, "gun-frigate", Designer.StandardId);
        sim.Issue(new SaveVariantCommand(Side.Player, "gun-frigate", null, "Mine", fit));
        sim.Issue(new SetCrewingCommand(Side.Player, "freighter", true));
        sim.StepMany(1);
        var ship = w.Ships.First(s => s.Side == Side.Player && Crews.Crewed(s));
        ship.Strain = 0.4;
        var lost = Loose(sim, Side.Player, "cruiser", FarAway(sim));
        Lifeboats.Launch(w, lost, null, null);

        var loaded = Simulation.FromSave(SaveGame.FromJson(sim.ToSave().ToJson()), GameLog.Memory());
        var w2 = loaded.World;
        Assert.Contains(Designer.Of(w2, Side.Player, "gun-frigate"), d => d.Name == "Mine");
        Assert.True(Designer.AiRun(w2, Side.Player, "freighter"));
        Assert.Equal(0.4, w2.FindShip(ship.Id)!.Strain, 6);
        Assert.Single(w2.Lifeboats);
        Assert.True(w2.Lifeboats[0].Boat);
    }
}
