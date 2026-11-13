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
    Hazards = 256,
    Possible = 512,
    TheySee = 1024,
    NeedPins = 2048,
    AdvicePins = 4096,
    AutoPins = 8192,
}

/// <summary>The menu bar (row two of the top bar): always there, slides away when idle, or off (keys only).</summary>
public enum MenuBarMode { Always, HideWhenIdle, Off }

/// <summary>The Needs you tray: opens from the badge, opens itself on something urgent, or off (badge count and "." only).</summary>
public enum TrayMode { OnBadge, OnUrgent, Off }

/// <summary>Advice from automation: a folded strip in the tray, its own list in the tray, or off (only trouble reaches you).</summary>
public enum AdviceMode { Folded, OwnList, Off }

/// <summary>
/// The player-information settings, kept in ui.cfg in the data folder: the detail level, which fleet
/// panel sections stay open, and the map layers. Glance, hover, open: headers and the map give icons and
/// one line, tooltips give the numbers and reasons, full sections open on demand.
/// </summary>
public static class PlayerInfo
{
    public static DetailLevel Detail { get; private set; } = DetailLevel.Standard;
    public static MapLayer Layers { get; private set; } = Default;

    public const MapLayer Default = MapLayer.Orbits | MapLayer.Shadows | MapLayer.LagRing | MapLayer.Lanes | MapLayer.Wells | MapLayer.OldSightings | MapLayer.Labels | MapLayer.Hazards | MapLayer.Possible | MapLayer.TheySee | MapLayer.NeedPins;

    /// <summary>The Needs you layers, which the presets leave as they are (they are about trouble, not the view).</summary>
    public const MapLayer PinLayers = MapLayer.NeedPins | MapLayer.AdvicePins | MapLayer.AutoPins;

    // ---- Top bar and Needs you tray (Display options) ----

    public static MenuBarMode MenuBar { get; private set; } = MenuBarMode.Always;
    public static bool MenuIcons { get; private set; } = true;
    public static TrayMode Tray { get; private set; } = TrayMode.OnBadge;
    /// <summary>Needs you pins only for urgent and soon items (news-level items get none).</summary>
    public static bool PinsUrgentOnly { get; private set; }
    public static AdviceMode Advice { get; private set; } = AdviceMode.Folded;
    public static bool PauseWhileWorking { get; private set; } = true;

    public static void SetMenuBar(MenuBarMode m) { if (m != MenuBar) { MenuBar = m; Save(); } }
    public static void SetMenuIcons(bool on) { if (on != MenuIcons) { MenuIcons = on; Save(); } }
    public static void SetTray(TrayMode m) { if (m != Tray) { Tray = m; Save(); } }
    public static void SetAdvice(AdviceMode m) { if (m != Advice) { Advice = m; Save(); } }
    public static void SetPauseWhileWorking(bool on) { if (on != PauseWhileWorking) { PauseWhileWorking = on; Save(); } }

    /// <summary>Pins on the map: 0 on, 1 urgent and soon only, 2 off (the Needs you pins layer).</summary>
    public static int PinMode => !Has(MapLayer.NeedPins) ? 2 : PinsUrgentOnly ? 1 : 0;

    public static void SetPinMode(int mode)
    {
        PinsUrgentOnly = mode == 1;
        Layers = mode == 2 ? Layers & ~MapLayer.NeedPins : Layers | MapLayer.NeedPins;
        Save();
    }

    public static void SetPinsUrgentOnly(bool on) { if (on != PinsUrgentOnly) { PinsUrgentOnly = on; Save(); } }
    public const MapLayer Calm = MapLayer.Orbits | MapLayer.Labels;
    public const MapLayer War = MapLayer.Orbits | MapLayer.Shadows | MapLayer.LagRing | MapLayer.OldSightings | MapLayer.Lanes | MapLayer.Labels | MapLayer.Hazards;
    public const MapLayer Battle = MapLayer.Orbits | MapLayer.Shadows | MapLayer.LagRing | MapLayer.OldSightings | MapLayer.Hazards | MapLayer.Possible | MapLayer.TheySee;
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
        (MapLayer.Possible, "Where they could be", "Round each enemy fleet out of sight: the area it could have reached since it was last seen, growing with time"),
        (MapLayer.TheySee, "What they see of you", "Round your fleets near the enemy: how far the nearest enemy fleet's sensors would pick them up as they are flying now"),
        (MapLayer.Hazards, "Hazards", "Radiation belts (green), the hot zone close to the sun (orange), battle debris (grey) and the names of rock belts and rings"),
        (MapLayer.NeedPins, "Needs you pins", "A pin on each planet or fleet that needs you; hover a tray row to ring its pin, click a pin to open its row"),
        (MapLayer.AdvicePins, "Advice pins", "Pins for automation's advice too (off by default: the map shows trouble, not suggestions)"),
        (MapLayer.AutoPins, "What automation did", "Dashed grey pins where automation acted today (off by default: history rather than trouble)"),
    };

    /// <summary>The side list (L): which rows show ("all", "mine", "rival") and their order ("order", "name", "distance", "attention").</summary>
    public static string SideShow { get; private set; } = "all";
    public static string SideSort { get; private set; } = "order";

    public static void SetSide(string show, string sort)
    {
        if (show == SideShow && sort == SideSort) return;
        SideShow = show;
        SideSort = sort;
        Save();
    }

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
        // Settings saved before the Hazards layer existed: switch it on once.
        if (!cfg.HasSectionKey("ui", "hazardsLayer")) Layers |= MapLayer.Hazards;
        // Settings saved before the Needs you pins existed: switch them on once.
        if (!cfg.HasSectionKey("ui", "pinsLayer")) Layers |= MapLayer.NeedPins;
        if (Enum.TryParse<MenuBarMode>((string)cfg.GetValue("ui", "menuBar", "Always"), out var mb)) MenuBar = mb;
        MenuIcons = (bool)cfg.GetValue("ui", "menuIcons", true);
        if (Enum.TryParse<TrayMode>((string)cfg.GetValue("ui", "tray", "OnBadge"), out var tm)) Tray = tm;
        PinsUrgentOnly = (bool)cfg.GetValue("ui", "pinsUrgentOnly", false);
        if (Enum.TryParse<AdviceMode>((string)cfg.GetValue("ui", "advice", "Folded"), out var am)) Advice = am;
        PauseWhileWorking = (bool)cfg.GetValue("ui", "pauseWhileWorking", true);
        SideShow = (string)cfg.GetValue("ui", "sideShow", "all");
        SideSort = (string)cfg.GetValue("ui", "sideSort", "order");
        foreach (var s in ((string)cfg.GetValue("ui", "open", "")).Split(',', StringSplitOptions.RemoveEmptyEntries)) OpenSections.Add(s);
    }

    private static void Save()
    {
        if (_file == null) return;
        var cfg = new ConfigFile();
        cfg.SetValue("ui", "detail", Detail.ToString());
        cfg.SetValue("ui", "layers", (int)Layers);
        cfg.SetValue("ui", "hazardsLayer", true);
        cfg.SetValue("ui", "pinsLayer", true);
        cfg.SetValue("ui", "menuBar", MenuBar.ToString());
        cfg.SetValue("ui", "menuIcons", MenuIcons);
        cfg.SetValue("ui", "tray", Tray.ToString());
        cfg.SetValue("ui", "pinsUrgentOnly", PinsUrgentOnly);
        cfg.SetValue("ui", "advice", Advice.ToString());
        cfg.SetValue("ui", "pauseWhileWorking", PauseWhileWorking);
        cfg.SetValue("ui", "sideShow", SideShow);
        cfg.SetValue("ui", "sideSort", SideSort);
        cfg.SetValue("ui", "open", string.Join(",", OpenSections.OrderBy(s => s, StringComparer.Ordinal)));
        cfg.Save(_file);
        Changed?.Invoke();
    }

    public static void SetDetail(DetailLevel d)
    {
        if (d == Detail) return;
        Detail = d;
        if (d == DetailLevel.Simple) Layers = Calm | (Layers & PinLayers);
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
