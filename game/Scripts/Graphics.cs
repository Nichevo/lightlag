using Godot;

namespace Lightlag.Game;

/// <summary>How much the map draws: Low is the plain look (fast on older PCs), Medium adds the shaders and effects, High adds bloom.</summary>
public enum GraphicsLevel { Low, Medium, High }

/// <summary>
/// The graphics setting, kept in graphics.cfg in the data folder, and the glow (bloom) that High turns on.
/// Low keeps the original look: flat background, plain sun, plain orbits and effects.
/// </summary>
public static class Graphics
{
    public static GraphicsLevel Level { get; private set; } = GraphicsLevel.High;

    /// <summary>Medium or High: shaders, particles and the extra effects.</summary>
    public static bool Fancy => Level >= GraphicsLevel.Medium;

    /// <summary>High: 2D bloom from a WorldEnvironment glow (bright colours above 1 spill light).</summary>
    public static bool Bloom => Level == GraphicsLevel.High;

    /// <summary>A colour pushed above 1 so it blooms on High (on the others it is clamped to plain bright).</summary>
    public static Color Hot(Color c, float boost) => Bloom ? new Color(c.R * boost, c.G * boost, c.B * boost, c.A) : c;

    public static event Action? Changed;

    private static WorldEnvironment? _env;
    private static string? _file;

    public static string Name(GraphicsLevel l) => l switch
    {
        GraphicsLevel.Low => "Low",
        GraphicsLevel.Medium => "Medium",
        _ => "High",
    };

    /// <summary>Read the saved setting and set up the glow. Call once from Main.</summary>
    public static void Load(Node root, string dataDir)
    {
        _file = System.IO.Path.Combine(dataDir, "graphics.cfg");
        var cfg = new ConfigFile();
        if (cfg.Load(_file) == Error.Ok)
        {
            var saved = (string)cfg.GetValue("graphics", "level", "High");
            if (Enum.TryParse<GraphicsLevel>(saved, out var l)) Level = l;
        }
        _env = new WorldEnvironment { Name = "Glow", Environment = MakeEnvironment() };
        root.AddChild(_env);
        Apply(root);
    }

    public static void Set(Node root, GraphicsLevel level)
    {
        if (level == Level) return;
        Level = level;
        if (_file != null)
        {
            var cfg = new ConfigFile();
            cfg.SetValue("graphics", "level", level.ToString());
            cfg.Save(_file);
        }
        Apply(root);
        Main.Instance.Log.Info("ui", "Graphics setting changed", new { level = level.ToString() });
        Changed?.Invoke();
    }

    private static void Apply(Node root)
    {
        // Bloom needs colours above 1, which only an HDR 2D buffer keeps. Low and Medium keep the plain buffer
        // so their colours and see-through layers blend exactly as before.
        root.GetViewport().UseHdr2D = Bloom;
        if (_env != null) _env.Environment.GlowEnabled = Bloom;
    }

    private static Godot.Environment MakeEnvironment()
    {
        var e = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Canvas,
            // Only the map (canvas layer 0) glows; the HUD and menus sit on higher layers and stay crisp.
            BackgroundCanvasMaxLayer = 0,
            GlowEnabled = false,
            GlowIntensity = 0.9f,
            GlowStrength = 1.0f,
            GlowBloom = 0f,
            GlowHdrThreshold = 1.0f,
            GlowHdrScale = 2.0f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive,
        };
        // A wide, soft spill: more weight on the larger blur levels.
        e.SetGlowLevel(0, 0f);
        e.SetGlowLevel(1, 0.6f);
        e.SetGlowLevel(2, 0.8f);
        e.SetGlowLevel(3, 1.0f);
        e.SetGlowLevel(4, 0.9f);
        e.SetGlowLevel(5, 0.6f);
        e.SetGlowLevel(6, 0.3f);
        return e;
    }

    /// <summary>A soft round spot (white, fading to clear at the edge), for glows, puffs and wells.</summary>
    public static Texture2D SoftDot => _softDot ??= MakeSoftDot();
    private static Texture2D? _softDot;

    private static Texture2D MakeSoftDot()
    {
        var g = new Gradient();
        g.SetColor(0, new Color(1, 1, 1, 1));
        g.SetColor(1, new Color(1, 1, 1, 0));
        g.AddPoint(0.2f, new Color(1, 1, 1, 0.75f));
        g.AddPoint(0.45f, new Color(1, 1, 1, 0.32f));
        g.AddPoint(0.7f, new Color(1, 1, 1, 0.09f));
        return new GradientTexture2D
        {
            Gradient = g, Width = 128, Height = 128, Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        };
    }

    /// <summary>A picker for the title screen and the game menu.</summary>
    public static Control Picker(Node root)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Ui.Label("Graphics:", 15, Ui.Dim));
        var pick = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Low: the plain look, fastest. Medium: sun corona, star field, planet clouds and lights, engine plumes and battle effects. High: Medium plus bloom (bright things glow)",
        };
        foreach (var l in Enum.GetValues<GraphicsLevel>()) pick.AddItem(Name(l), (int)l);
        pick.Selected = (int)Level;
        pick.ItemSelected += i => Set(root, (GraphicsLevel)(int)i);
        row.AddChild(pick);
        return row;
    }
}
