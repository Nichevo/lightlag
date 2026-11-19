using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>The rule presets on the New game screen. Custom is any mix that matches none of them.</summary>
public enum RulesPreset { Relaxed, Standard, Full }

/// <summary>
/// The new-game choices (race, rivals, rules), kept in newgame.cfg in the data folder so the next launch starts
/// from the last game's set-up. Only a normal launch reads it; screenshot, smoke-test and probe runs keep the
/// built-in defaults, and they never write it (only the title screen's Start does).
/// </summary>
public static class NewGameChoices
{
    private static string? _file;

    public static void Load(Main main, string dataDir)
    {
        _file = System.IO.Path.Combine(dataDir, "newgame.cfg");
        var cfg = new ConfigFile();
        if (cfg.Load(_file) != Error.Ok) return;
        // Plain getters (Godot's generic Variant calls only take Variant-compatible type arguments).
        string Get(string key, string fallback) => cfg.GetValue("newgame", key, fallback).VariantType == Variant.Type.String
            ? (string)cfg.GetValue("newgame", key, fallback) : fallback;
        bool GetBool(string key, bool fallback) => cfg.GetValue("newgame", key, fallback).VariantType == Variant.Type.Bool
            ? (bool)cfg.GetValue("newgame", key, fallback) : fallback;
        double GetNumber(string key, double fallback)
        {
            var v = cfg.GetValue("newgame", key, fallback);
            return v.VariantType switch { Variant.Type.Float => (double)v, Variant.Type.Int => (long)v, _ => fallback };
        }
        bool Has(string id) => Races.Choices().Any(c => c.Id == id);

        var player = Get("player_race", main.PlayerRaceChoice);
        if (Has(player)) main.PlayerRaceChoice = player;
        var rival = Get("rival_race", main.RivalRaceChoice);
        if (Has(rival)) main.RivalRaceChoice = rival;
        var third = Get("third_race", main.ThirdRaceChoice);
        if (third == Races.None || Has(third)) main.ThirdRaceChoice = third;
        if (Enum.TryParse<Difficulty>(Get("difficulty", main.DifficultyChoice.ToString()), out var d)) main.DifficultyChoice = d;

        var lightOn = GetBool("light_on", main.LightChoice.On);
        var lightSpeed = GetNumber("light_speed", main.LightChoice.SpeedFactor);
        main.LightChoice = new LightSetup(lightOn, lightSpeed < 0.5 ? 0.1 : 1.0);
        main.DecapitationChoice = GetBool("decapitation", main.DecapitationChoice);
        main.RandomStartsChoice = GetBool("random_homes", main.RandomStartsChoice);
        main.SpreadPeopleChoice = GetBool("people_everywhere", main.SpreadPeopleChoice);
        main.HazardsChoice = GetBool("hazards", main.HazardsChoice);
        if (Enum.TryParse<EventRate>(Get("events", main.EventsChoice.ToString()), out var e)) main.EventsChoice = e;
        var orbits = GetNumber("planet_orbits", main.PlanetOrbitsChoice);
        if (Orbits.PlanetChoices.Contains(orbits)) main.PlanetOrbitsChoice = orbits;
        main.SupplyLinesChoice = GetBool("supply_lines", main.SupplyLinesChoice);
        main.FuelTypesChoice = GetBool("fuel_types", main.FuelTypesChoice);
        main.HappinessChoice = GetBool("happiness", main.HappinessChoice);
        main.BudgetChoice = GetBool("industry_budget", main.BudgetChoice);
        main.ContactOffersChoice = GetBool("contact_offers", main.ContactOffersChoice);
        main.EarlyEndingsChoice = GetBool("early_endings", main.EarlyEndingsChoice);
        main.ResearchVarietyChoice = GetBool("research_variety", main.ResearchVarietyChoice);
        main.IronmanChoice = GetBool("ironman", main.IronmanChoice);
        if (Enum.TryParse<LearnMode>(Get("rivals_learn", main.LearnChoice.ToString()), out var learn)) main.LearnChoice = learn;
        var mapType = Enum.TryParse<MapType>(Get("map_type", main.MapChoice.Type.ToString()), out var mt) ? mt : main.MapChoice.Type;
        var mapSize = Enum.TryParse<MapSize>(Get("map_size", main.MapChoice.Size.ToString()), out var ms) ? ms : main.MapChoice.Size;
        var mapSeed = ulong.TryParse(Get("map_seed", ""), out var seed) && seed >= MapCodes.MinSeed && seed <= MapCodes.MaxSeed ? seed : main.MapChoice.Seed;
        main.MapChoice = new MapSetup(mapType, mapSize, mapSeed);
        main.NewMapSeedChoice = GetBool("new_map_seed", main.NewMapSeedChoice);
    }

    /// <summary>Write the current choices. Does nothing until Load has named the file (a normal launch).</summary>
    public static void Save(Main main)
    {
        if (_file == null) return;
        var cfg = new ConfigFile();
        void Set(string key, Variant value) => cfg.SetValue("newgame", key, value);
        Set("player_race", main.PlayerRaceChoice);
        Set("rival_race", main.RivalRaceChoice);
        Set("third_race", main.ThirdRaceChoice);
        Set("difficulty", main.DifficultyChoice.ToString());
        Set("light_on", main.LightChoice.On);
        Set("light_speed", main.LightChoice.SpeedFactor);
        Set("decapitation", main.DecapitationChoice);
        Set("random_homes", main.RandomStartsChoice);
        Set("people_everywhere", main.SpreadPeopleChoice);
        Set("hazards", main.HazardsChoice);
        Set("events", main.EventsChoice.ToString());
        Set("planet_orbits", main.PlanetOrbitsChoice);
        Set("supply_lines", main.SupplyLinesChoice);
        Set("fuel_types", main.FuelTypesChoice);
        Set("happiness", main.HappinessChoice);
        Set("industry_budget", main.BudgetChoice);
        Set("contact_offers", main.ContactOffersChoice);
        Set("early_endings", main.EarlyEndingsChoice);
        Set("research_variety", main.ResearchVarietyChoice);
        Set("ironman", main.IronmanChoice);
        Set("rivals_learn", main.LearnChoice.ToString());
        Set("map_type", main.MapChoice.Type.ToString());
        Set("map_size", main.MapChoice.Size.ToString());
        Set("map_seed", main.MapChoice.Seed.ToString());
        Set("new_map_seed", main.NewMapSeedChoice);
        if (cfg.Save(_file) != Error.Ok) main.Log.Warn("ui", "Could not save the new game choices", new { file = _file });
    }

    // ---- Presets -------------------------------------------------------------------------------

    /// <summary>
    /// The rules a preset sets: happiness, supply lines, fuel types, people everywhere, hazards, events and the
    /// Industry budget. Light lag, decapitation, random homes, planet orbits, contact offers, early endings and
    /// Ironman are left as they are.
    /// </summary>
    private static (bool Happiness, bool Supply, bool Fuel, bool People, bool Hazards, EventRate Events, bool Budget) Rules(RulesPreset p) => p switch
    {
        RulesPreset.Relaxed => (true, false, false, false, false, EventRate.Rare, true),
        RulesPreset.Full => (true, true, true, true, true, EventRate.Lively, true),
        _ => (true, true, true, false, true, EventRate.Normal, true),
    };

    public static string Tip(RulesPreset p) => p switch
    {
        RulesPreset.Relaxed => "Fewer things to look after: no supply lines, one fuel for every engine, no hazards, rare events",
        RulesPreset.Full => "Everything on: planets start with independent people, and events are lively",
        _ => "The usual game: every system on, planets start empty, normal events",
    };

    public static void Apply(Main main, RulesPreset p)
    {
        var r = Rules(p);
        main.HappinessChoice = r.Happiness;
        main.SupplyLinesChoice = r.Supply;
        main.FuelTypesChoice = r.Fuel;
        main.SpreadPeopleChoice = r.People;
        main.HazardsChoice = r.Hazards;
        main.EventsChoice = r.Events;
        main.BudgetChoice = r.Budget;
    }

    /// <summary>The preset the current rules match, or null (Custom).</summary>
    public static RulesPreset? Current(Main main)
    {
        foreach (var p in Enum.GetValues<RulesPreset>())
        {
            var r = Rules(p);
            if (main.HappinessChoice == r.Happiness && main.SupplyLinesChoice == r.Supply && main.FuelTypesChoice == r.Fuel
                && main.SpreadPeopleChoice == r.People && main.HazardsChoice == r.Hazards && main.EventsChoice == r.Events
                && main.BudgetChoice == r.Budget)
                return p;
        }
        return null;
    }

    // ---- Reading the set-up back -----------------------------------------------------------------

    private static string RaceName(string id, bool start) =>
        id == Races.Random ? (start ? "A random race" : "a random race") : Races.Get(id).Name;

    public static string LightName(LightSetup l) => !l.On ? "no light lag" : l.SpeedFactor < 0.5 ? "light 10x slower" : "real light speed";

    /// <summary>Two lines: who plays whom, then the rules in short.</summary>
    public static string Summary(Main main)
    {
        var who = $"{RaceName(main.PlayerRaceChoice, true)} against {RaceName(main.RivalRaceChoice, false)}";
        var three = main.ThirdRaceChoice != Races.None;
        who += three ? $" and {RaceName(main.ThirdRaceChoice, false)}" : ", one on one";
        who += $" · {main.DifficultyChoice} {(three ? "rivals" : "rival")}";
        var rules = $"{(Current(main) is RulesPreset p ? p.ToString() : "Custom")} rules · {LightName(main.LightChoice)} · "
                    + (main.PlanetOrbitsChoice <= 1 ? "planets at true speed" : $"planets {main.PlanetOrbitsChoice:0}x slower");
        if (!main.EarlyEndingsChoice) rules += " · no early endings";
        if (!main.ResearchVarietyChoice) rules += " · same research every game";
        if (main.IronmanChoice) rules += " · Ironman";
        who += main.MapChoice.Seeded ? $" · {MapCodes.Name(main.MapChoice.Type)}, {main.MapChoice.Size} map ({MapCodes.SeedText(main.MapChoice.Seed)})" : " · Hesper";
        if (main.LearnChoice != LearnMode.All) who += main.LearnChoice == LearnMode.Off ? " · rivals don't learn" : " · rivals learn from overnight runs only";
        return who + "\n" + rules;
    }
}
