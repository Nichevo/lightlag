namespace Lightlag.Sim;

/// <summary>Things the game can stop for. The game's end always pauses and is not listed.</summary>
public enum PauseEvent
{
    EnemySpotted, BattleStarts, FarBattle, EnemyClosing, PlanetClaimed, ShipyardAttacked, CommandCall, EventCall,
    EventAutoPicked, ResearchDone, ShipLaunched, PlanetChangedHands, SupplyNote,
}

/// <summary>
/// What happens when one comes: stop the clock, drop to the slowest speed and keep going, show a pop-up, or only
/// a News line.
/// </summary>
public enum PauseResponse { Pause, Slow, Notice, Ignore }

/// <summary>
/// The player's per-event pause settings (an interface setting, kept outside the game and its saves, so it never
/// touches the simulation). Each event has a response; presets set them all at once. Smarter pauses still apply on
/// top: a contact that a role fleet has in hand, or the same fleets meeting again, never pause.
/// </summary>
public sealed class PauseRules
{
    public const string Watchful = "Watchful", Standard = "Standard", HandsOff = "Hands off";
    public static readonly string[] Presets = { Watchful, Standard, HandsOff };

    private readonly Dictionary<PauseEvent, PauseResponse> _map = new();

    public PauseRules() => Apply(Standard);

    public PauseResponse this[PauseEvent e]
    {
        get => _map.TryGetValue(e, out var r) ? r : Default(Standard, e);
        set => _map[e] = value;
    }

    /// <summary>Sets every event to the preset's response.</summary>
    public void Apply(string preset)
    {
        foreach (var e in Enum.GetValues<PauseEvent>()) _map[e] = Default(preset, e);
    }

    /// <summary>The preset these settings match exactly, or null when changed by hand.</summary>
    public string? Preset => Presets.FirstOrDefault(p => Enum.GetValues<PauseEvent>().All(e => this[e] == Default(p, e)));

    public static PauseResponse Default(string preset, PauseEvent e) => preset switch
    {
        Watchful => e switch
        {
            PauseEvent.ResearchDone or PauseEvent.ShipLaunched or PauseEvent.EventAutoPicked => PauseResponse.Notice,
            PauseEvent.SupplyNote or PauseEvent.PlanetChangedHands => PauseResponse.Slow,
            _ => PauseResponse.Pause,
        },
        HandsOff => e switch
        {
            PauseEvent.BattleStarts or PauseEvent.ShipyardAttacked or PauseEvent.CommandCall or PauseEvent.EventCall => PauseResponse.Pause,
            PauseEvent.EnemySpotted or PauseEvent.FarBattle or PauseEvent.PlanetClaimed => PauseResponse.Slow,
            PauseEvent.ResearchDone or PauseEvent.ShipLaunched or PauseEvent.EventAutoPicked => PauseResponse.Ignore,
            _ => PauseResponse.Notice,
        },
        // Standard: how the game behaved before these settings existed.
        _ => e switch
        {
            PauseEvent.EnemySpotted or PauseEvent.BattleStarts or PauseEvent.FarBattle or PauseEvent.PlanetClaimed
                or PauseEvent.ShipyardAttacked or PauseEvent.CommandCall or PauseEvent.EventCall => PauseResponse.Pause,
            _ => PauseResponse.Notice,
        },
    };

    public static string Name(PauseEvent e) => e switch
    {
        PauseEvent.EnemySpotted => "Enemy fleet spotted",
        PauseEvent.BattleStarts => "Battle starts near you",
        PauseEvent.FarBattle => "Light arrives from a far battle",
        PauseEvent.EnemyClosing => "Enemy heading for one of your fleets",
        PauseEvent.PlanetClaimed => "Rival claiming one of your planets",
        PauseEvent.ShipyardAttacked => "Shipyard under attack",
        PauseEvent.CommandCall => "Boarding, landing or after-battle call",
        PauseEvent.EventCall => "Event waiting for your call",
        PauseEvent.EventAutoPicked => "Event handled by Auto",
        PauseEvent.ResearchDone => "Research done",
        PauseEvent.ShipLaunched => "New ship launched",
        PauseEvent.PlanetChangedHands => "Planet changes hands",
        PauseEvent.SupplyNote => "Fleet supply warning",
        _ => e.ToString(),
    };

    public static string Name(PauseResponse r) => r switch
    {
        PauseResponse.Pause => "Pause",
        PauseResponse.Slow => "Slow to 1x",
        PauseResponse.Notice => "Pop-up",
        _ => "News only",
    };

    /// <summary>One "Event=Response" line each, for the settings file.</summary>
    public string ToText() => string.Join("\n", Enum.GetValues<PauseEvent>().Select(e => $"{e}={this[e]}")) + "\n";

    /// <summary>Reads the settings file; unknown or broken lines are skipped (they keep the Standard response).</summary>
    public static PauseRules Parse(string? text)
    {
        var rules = new PauseRules();
        foreach (var line in (text ?? "").Split('\n'))
        {
            var parts = line.Trim().Split('=', 2);
            if (parts.Length == 2 && Enum.TryParse<PauseEvent>(parts[0].Trim(), out var e) && Enum.TryParse<PauseResponse>(parts[1].Trim(), out var r))
                rules[e] = r;
        }
        return rules;
    }
}
