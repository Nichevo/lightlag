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
        T Get<T>(string key, T fallback)
        {
            var v = cfg.GetValue("newgame", key, Variant.From(fallback));
            try { return v.As<T>(); }
            catch (Exception) { return fallback; }
        }
        bool Has(string id) => Races.Choices().Any(c => c.Id == id);

        var player = Get("player_race", main.PlayerRaceChoice);
        if (Has(player)) main.PlayerRaceChoice = player;
        var rival = Get("rival_race", main.RivalRaceChoice);
        if (Has(rival)) main.RivalRaceChoice = rival;
        var third = Get("third_race", main.ThirdRaceChoice);
        if (third == Races.None || Has(third)) main.ThirdRaceChoice = third;
        if (Enum.TryParse<Difficulty>(Get("difficulty", main.DifficultyChoice.ToString()), out var d)) main.DifficultyChoice = d;

        var lightOn = Get("light_on", main.LightChoice.On);
        var lightSpeed = Get("light_speed", main.LightChoice.SpeedFactor);
        main.LightChoice = new LightSetup(lightOn, lightSpeed < 0.5 ? 0.1 : 1.0);
        main.DecapitationChoice = Get("decapitation", main.DecapitationChoice);
        main.RandomStartsChoice = Get("random_homes", main.RandomStartsChoice);
        main.SpreadPeopleChoice = Get("people_everywhere", main.SpreadPeopleChoice);
        main.HazardsChoice = Get("hazards", main.HazardsChoice);
        if (Enum.TryParse<EventRate>(Get("events", main.EventsChoice.ToString()), out var e)) main.EventsChoice = e;
        var orbits = Get("planet_orbits", main.PlanetOrbitsChoice);
        if (Orbits.PlanetChoices.Contains(orbits)) main.PlanetOrbitsChoice = orbits;
        main.SupplyLinesChoice = Get("supply_lines", main.SupplyLinesChoice);
        main.FuelTypesChoice = Get("fuel_types", main.FuelTypesChoice);
        main.HappinessChoice = Get("happiness", main.HappinessChoice);
        main.BudgetChoice = Get("industry_budget", main.BudgetChoice);
        main.ContactOffersChoice = Get("contact_offers", main.ContactOffersChoice);
        main.EarlyEndingsChoice = Get("early_endings", main.EarlyEndingsChoice);
        main.IronmanChoice = Get("ironman", main.IronmanChoice);
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
        Set("ironman", main.IronmanChoice);
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
        if (main.IronmanChoice) rules += " · Ironman";
        return who + "\n" + rules;
    }
}
