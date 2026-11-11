using Godot;

namespace Lightlag.Game;

/// <summary>How much the panels show: Simple (headers and warnings), Standard (one line per section), Full (everything open).</summary>
public enum DetailLevel { Simple, Standard, Full }

/// <summary>What the map draws, from the one Map layers menu.</summary>
[Flags]
public enum MapLayer
{
    None = 0,
    Orbits = 1,
    Shadows = 2,
    LagRing = 4,
    Lanes = 8,
    SupplyRuns = 16,
    Wells = 32,
    OldSightings = 64,
    Labels = 128,
}

/// <summary>
/// The player-information settings, kept in ui.cfg in the data folder: the detail level, which fleet
/// panel sections stay open, and the map layers. Glance, hover, open: headers and the map give icons and
/// one line, tooltips give the numbers and reasons, full sections open on demand.
/// </summary>
public static class PlayerInfo
{
    public static DetailLevel Detail { get; private set; } = DetailLevel.Standard;
    public static MapLayer Layers { get; private set; } = Default;

    public const MapLayer Default = MapLayer.Orbits | MapLayer.Shadows | MapLayer.LagRing | MapLayer.Lanes | MapLayer.Wells | MapLayer.OldSightings | MapLayer.Labels;
    public const MapLayer Calm = MapLayer.Orbits | MapLayer.Labels;
    public const MapLayer War = MapLayer.Orbits | MapLayer.Shadows | MapLayer.LagRing | MapLayer.OldSightings | MapLayer.Lanes | MapLayer.Labels;
    public const MapLayer Trade = MapLayer.Orbits | MapLayer.Lanes | MapLayer.SupplyRuns | MapLayer.Labels;

    public static readonly (MapLayer Layer, string Name, string Tip)[] LayerList =
    {
        (MapLayer.Orbits, "Orbits", "The paths planets and moons follow"),
        (MapLayer.Shadows, "Sight shadows", "Grey areas planets hide from your sensors (B cycles shadows, coverage, off)"),
        (MapLayer.LagRing, "Light lag ring", "Your flagship's command range: orders reach fleets inside it at once"),
        (MapLayer.Lanes, "Lanes and zones", "Rival supply lanes you have worked out, and your raiding and guard zones"),
        (MapLayer.SupplyRuns, "Supply runs", "Tankers and freighters on supply runs, and settlers on their way"),
        (MapLayer.Wells, "Gravity wells", "Shading round each planet where its pull is stronger than the sun's (Medium and High graphics)"),
        (MapLayer.OldSightings, "Old sightings", "Where rival fleets were last seen, greyed with age, and their tracks"),
        (MapLayer.Labels, "Labels", "Planet and moon names"),
    };

    /// <summary>Fleet panel sections kept open (by name).</summary>
    public static readonly HashSet<string> OpenSections = new();

    public static event Action? Changed;
    private static string? _file;

    public static bool Has(MapLayer l) => (Layers & l) != 0;

    public static string Name(DetailLevel d) => d switch { DetailLevel.Simple => "Simple", DetailLevel.Full => "Full", _ => "Standard" };

    public static void Load(string dataDir)
    {
        _file = System.IO.Path.Combine(dataDir, "ui.cfg");
        var cfg = new ConfigFile();
        if (cfg.Load(_file) != Error.Ok) return;
        if (Enum.TryParse<DetailLevel>((string)cfg.GetValue("ui", "detail", "Standard"), out var d)) Detail = d;
        Layers = (MapLayer)(int)cfg.GetValue("ui", "layers", (int)Default);
        foreach (var s in ((string)cfg.GetValue("ui", "open", "")).Split(',', StringSplitOptions.RemoveEmptyEntries)) OpenSections.Add(s);
    }

    private static void Save()
    {
        if (_file == null) return;
        var cfg = new ConfigFile();
        cfg.SetValue("ui", "detail", Detail.ToString());
        cfg.SetValue("ui", "layers", (int)Layers);
        cfg.SetValue("ui", "open", string.Join(",", OpenSections.OrderBy(s => s, StringComparer.Ordinal)));
        cfg.Save(_file);
        Changed?.Invoke();
    }

    public static void SetDetail(DetailLevel d)
    {
        if (d == Detail) return;
        Detail = d;
        if (d == DetailLevel.Simple) Layers = Calm;
        Main.Instance.Log.Info("ui", "Detail level changed", new { level = d.ToString() });
        Save();
    }

    public static void SetLayers(MapLayer layers)
    {
        if (layers == Layers) return;
        Layers = layers;
        Save();
    }

    public static void Toggle(MapLayer l) => SetLayers(Layers ^ l);

    public static bool IsOpen(string section) => Detail == DetailLevel.Full || OpenSections.Contains(section);

    public static void SetOpen(string section, bool open)
    {
        if (open ? !OpenSections.Add(section) : !OpenSections.Remove(section)) return;
        Save();
    }

    /// <summary>A picker for the title screen and the game menu.</summary>
    public static Control Picker()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Ui.Label("Detail:", 15, Ui.Dim));
        var pick = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Same game, less on screen. Simple: panels show headers and warnings only, map on the Calm layers. "
                          + "Standard: one line per section, open what you like. Full: every section open and every number shown",
        };
        foreach (var d in Enum.GetValues<DetailLevel>()) pick.AddItem(Name(d), (int)d);
        pick.Selected = (int)Detail;
        pick.ItemSelected += i => SetDetail((DetailLevel)(int)i);
        row.AddChild(pick);
        return row;
    }
}
