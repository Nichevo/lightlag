using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using Xunit;

namespace Lightlag.Sim.Tests;

/// <summary>
/// The hardening pass: saves keep every part of the game (intel, war, crews, fuels, hazards, events, ship
/// names) and carry on exactly as if never saved; the newer rule checks catch what they are for; and
/// crashes found by the overnight soak stay fixed.
/// </summary>
public class HardeningTests
{
    private static Body BodyNamed(Simulation sim, string key) => sim.World.Bodies.Single(b => b.Key == key);

    private static Fleet NewFleet(Simulation sim, Side side, Vec2 at, params string[] classes)
    {
        var ships = classes.Select(c => Economy.NewShip(sim.World, side, c)).ToList();
        foreach (var s in ships.Where(Planets.IsTroopShip)) s.CargoTroops = Planets.TroopHold(s);
        return Fleets.Create(sim.World, side, ships, at);
    }

    private static void PlaceInOrbit(Simulation sim, Fleet f, Body b)
    {
        f.Flight = null;
        f.Position = b.Position + new Vec2(Fleets.OrbitRadiusFor(b), 0);
        Fleets.EnterOrbit(f, b);
        foreach (var s in sim.World.ShipsOf(f)) s.Position = f.Position + s.FormationOffset;
    }

    private static Piece Put(Simulation sim, Body b, string kind)
    {
        var data = Planets.BuildingKind(kind)!;
        var (x, y, turned) = Colonies.BestSpot(b, data)!.Value;
        var p = Colonies.AddPiece(sim.World, b, data, x, y, turned, built: true);
        Colonies.SyncBuildings(b);
        return p;
    }

    /// <summary>
    /// Saves the game to text and loads it back, then checks the loaded world writes out exactly the same
    /// (nothing lost or changed on the way), and that both copies carry on in step for the given hours.
    /// </summary>
    private static Simulation RoundTrip(Simulation sim, int hours)
    {
        var text = sim.ToSave().ToJson();
        var loaded = Simulation.FromSave(SaveGame.FromJson(text), GameLog.Memory());
        loaded.RuleCheckEveryTicks = 1;
        loaded.StrictRules = true;
        Assert.Equal(Json.Serialize(sim.World), Json.Serialize(loaded.World));
        Assert.Equal(sim.PendingCommands.Count, loaded.PendingCommands.Count);
        var end = sim.World.Tick + hours;
        while (sim.World.Tick < end || sim.World.Sub != 0)
        {
            sim.Step();
            loaded.Step();
            if (sim.World.Sub == 0 && sim.World.Tick % 6 == 0) Assert.Equal(sim.StateHash(), loaded.StateHash());
        }
        Assert.Equal(sim.StateHash(), loaded.StateHash());
        return loaded;
    }

    [Fact]
    public void A_long_computer_game_with_lag_events_and_fuels_survives_save_and_load()
    {
        var options = GameOptions.AiVsAi() with { Events = EventRate.Lively, Light = new LightSetup(true, 1) };
        var sim = Simulation.NewGame(Scenarios.TestSystem, 31, GameLog.Memory(), options: options);
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.StepMany(150 * GameTime.TicksPerDay);
        var w = sim.World;

        // The game has got far enough to hold something of each part.
        Assert.NotEmpty(w.Events);
        Assert.NotEmpty(w.Belts);
        Assert.NotEmpty(w.History);
        Assert.All(w.Ships, s => Assert.False(string.IsNullOrEmpty(s.Name)));
        Assert.Contains(w.Ships, s => s.HullNumber > 0 && s.Code.Length > 0);
        Assert.NotEmpty(w.ShipNumbers);
        Assert.Contains(w.Sides, s => s.Intel.Points > 0 || s.Intel.Fleets.Count > 0 || s.Intel.Notes.Count > 0);
        Assert.True(w.Light.On);
        Assert.Equal(2, w.Minds.Count);

        // Something in each of the newer parts that a quiet game might not have reached yet.
        var ship = w.Ships.First(s => Fuels.Burns(w, s).Contains("water"));
        Fuels.Switch(w, ship, "water", null);
        ship.Xp = 2.5;
        sim.Issue(new RenameShipCommand(ship.Side, ship.Id, "Hardened Resolve"));
        sim.ApplyPending();
        Assert.Equal("Hardened Resolve", ship.Name);

        var loaded = RoundTrip(sim, 72);
        var again = loaded.World.FindShip(ship.Id);
        if (again != null)
        {
            Assert.Equal("Hardened Resolve", again.Name);
            Assert.Equal(ship.Code, again.Code);
        }
    }

    [Fact]
    public void A_boarding_and_an_invasion_under_way_survive_save_and_load()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 8, GameLog.Memory(), options: new GameOptions(Events: EventRate.Lively));
        sim.RuleCheckEveryTicks = 1;
        sim.StrictRules = true;
        sim.World.Minds.Clear();
        sim.StepMany(30 * GameTime.TicksPerDay);
        var w = sim.World;

        // Clear the rival's ships, then a crippled rival frigate and a player boarding party beside it.
        foreach (var s in w.Ships.Where(s => s.Side == Side.Rival).ToList()) Combat.Destroy(sim, s, wreck: false);
        Economy.Of(w, Side.Rival).FlagshipId = null;
        var vela = BodyNamed(sim, "vela");
        var at = vela.Position + new Vec2(400, 0);
        var rival = NewFleet(sim, Side.Rival, at, "gun-frigate");
        var target = w.ShipsOf(rival).Single();
        target.Armour = 0;
        target.Crippled = true;
        target.CrippleLeft = 20;
        var boarders = NewFleet(sim, Side.Player, at + new Vec2(5, 0), "gun-frigate", "troop-ship");
        Sensors.Update(sim);
        new BoardShipCommand(boarders.Id, target.Id, BoardApproach.Dock).Apply(w, sim);
        Assert.Single(w.Boardings);

        // A rival colony on Vela, and player troops landing on it.
        vela.Owner = Side.Rival;
        vela.PopK = 40;
        vela.Grudge = Side.Player;
        Put(sim, vela, Planets.Farm);
        Put(sim, vela, "bunker");
        var garrison = Planets.GarrisonOf(vela);
        var troops = NewFleet(sim, Side.Player, vela.Position,
            Enumerable.Repeat("troop-ship", (int)Math.Ceiling(garrison * 2.2 / GameData.ShipClass("troop-ship").Cargo)).Prepend("gun-frigate").ToArray());
        PlaceInOrbit(sim, troops, vela);
        troops.OrbitHeight = OrbitHeight.Low;
        new LandTroopsCommand(troops.Id, vela.Id, GroundPlan.Methodical).Apply(w, sim);
        Assert.NotNull(vela.Front);

        // Crews with experience, and a ship on another fuel.
        foreach (var s in w.ShipsOf(troops)) s.Xp = 1.8;
        var other = w.ShipsOf(boarders).First();
        Fuels.Switch(w, other, "water", null);

        // Save part way through the boarding and the invasion, then again once the boarding is over.
        sim.StepMany(4);
        Assert.Contains(w.Boardings, b => b.Outcome == null);
        sim = RoundTrip(sim, 30);
        Assert.Contains(sim.World.Boardings, b => b.Outcome != null);
        RoundTrip(sim, 48);
    }

    [Fact]
    public void A_flagship_hunter_under_light_lag_with_no_sighting_does_not_crash()
    {
        // Found by the soak: a personality that always fires at the flagship has no suspected flagship to
        // name when it sets its fire rule, which threw.
        // The soak game that showed it: seed 22004, people spread at the start, real light lag (the rival is a Hunter).
        var options = GameOptions.AiVsAi() with { Light = new LightSetup(true, 1), StartPopulation = true, ChoiceSpread = 0.1 };
        var sim = Simulation.NewGame(Scenarios.TestSystem, 22004, GameLog.Memory(), options: options);
        Assert.Equal(Personality.Hunter, sim.World.Minds.Single(m => m.Side == Side.Rival).Personality);
        sim.RuleCheckEveryTicks = GameTime.TicksPerDay;
        sim.StrictRules = true;
        sim.StepMany(60 * GameTime.TicksPerDay);
        Assert.True(sim.World.Tick >= 60 * GameTime.TicksPerDay);
    }

    // ---- The newer rule checks ------------------------------------------------------------------

    private static Simulation Quiet()
    {
        var sim = Simulation.NewGame(Scenarios.TestSystem, 5, GameLog.Memory());
        sim.World.Minds.Clear();
        Assert.Empty(sim.CheckRules());
        return sim;
    }

    private static void Breaks(Simulation sim, string rule) => Assert.Contains(sim.Rules.Check(sim.World), v => v.Rule == rule);

    [Fact]
    public void A_ship_code_that_does_not_match_its_hull_number_breaks_a_rule()
    {
        var sim = Quiet();
        var ships = sim.World.Ships.Where(s => s.HullNumber > 0).ToList();
        ships[0].Code = "XX9Z-99";
        Breaks(sim, "ship-codes-sane");
    }

    [Fact]
    public void Two_ships_with_one_hull_number_break_a_rule()
    {
        var sim = Quiet();
        var pair = sim.World.Ships.Where(s => s.HullNumber > 0).GroupBy(s => (s.Side, s.ClassId)).First(g => g.Count() > 1).Take(2).ToList();
        pair[1].HullNumber = pair[0].HullNumber;
        pair[1].Code = pair[0].Code;
        Breaks(sim, "ship-codes-sane");
    }

    [Fact]
    public void An_unknown_fuel_breaks_a_rule()
    {
        var sim = Quiet();
        sim.World.Ships[0].Fuel = "moonshine";
        Breaks(sim, "fuels-known");
    }

    [Fact]
    public void A_settle_order_for_a_missing_body_breaks_a_rule()
    {
        var sim = Quiet();
        sim.World.Orders.Add(new WorkOrder { Id = 999, Side = Side.Player, Kind = OrderKind.Ship, Key = Planets.SettlerClass, SettleBodyId = 12345 });
        Breaks(sim, "settle-orders-sane");
    }

    [Fact]
    public void An_unknown_event_kind_breaks_a_rule()
    {
        var sim = Quiet();
        sim.World.Events.Add(new GameEvent { Id = 77, Kind = "meteor-shower", Done = true, Call = new WarCall { Options = new List<string> { "Wait" }, Picked = 0 } });
        Breaks(sim, "events-sane");
    }

    [Fact]
    public void A_list_that_stops_being_trimmed_breaks_a_rule()
    {
        var sim = Quiet();
        for (var i = 0; i <= RuleBook.ListLimit; i++) sim.World.LossSpots.Add(new LossSpot(Side.Player, new Vec2(0, 0), 0));
        Breaks(sim, "lists-bounded");
    }

    [Fact]
    public void Ship_name_lists_cover_every_theme_and_never_repeat()
    {
        foreach (var (race, themes) in ShipNames.Data.Races)
        {
            Assert.Contains("any", themes.Keys);
            var all = themes.Values.SelectMany(l => l).ToList();
            Assert.All(all, n => Assert.InRange(n.Trim().Length, 1, ShipNames.MaxLength));
            Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            if (race == ShipNames.DefaultRace)
                foreach (var theme in new[] { "home", "water", "rocky", "ice", "storm" }) Assert.NotEmpty(themes[theme]);
        }
    }

    [Fact]
    public void Section_timing_adds_up_inner_parts_and_is_off_by_default()
    {
        Assert.Null(SectionTimer.Active);
        long now = 0;
        var t = new SectionTimer(() => now, 1000);
        SectionTimer.Active = t;
        try
        {
            using (SectionTimer.Time("outer"))
            {
                now += 5;
                using (SectionTimer.Time("inner")) now += 3;
                now += 2;
            }
        }
        finally { SectionTimer.Active = null; }
        Assert.Equal(10, t.Rows["outer"].Total);
        Assert.Equal(7, t.Rows["outer"].Self);
        Assert.Equal(3, t.Rows["inner"].Self);
        Assert.Contains("| outer |", t.Table());
    }
}
