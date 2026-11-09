using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>On-screen controls over the map: date and speed bar, scale bar, selection info, key help.</summary>
public partial class Hud : CanvasLayer
{
    private readonly GameScreen _screen;
    private Label _date = null!;
    private Label _status = null!;
    private readonly List<Button> _speedButtons = new();
    private Button _pause = null!;
    private ScaleBar _scaleBar = null!;
    private PanelContainer _infoPanel = null!;
    private Label _info = null!;

    public Hud() : this(null!) { }
    public Hud(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        var main = Main.Instance;

        // Top bar: date, pause and speeds on the left; build and seed on the right.
        var bar = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("separation", 6);
        bar.Position = new Vector2(16, 10);
        _date = Ui.Label("", 20);
        _date.CustomMinimumSize = new Vector2(190, 0);
        bar.AddChild(_date);
        _pause = SmallButton("II", () => _screen.TogglePause(), "Pause (Space)");
        bar.AddChild(_pause);
        for (var i = 0; i < GameScreen.Speeds.Length; i++)
        {
            var index = i;
            var b = SmallButton(GameScreen.SpeedNames[i], () => _screen.SetSpeed(index), $"Speed {GameScreen.SpeedNames[i]} (key {i + 1})");
            _speedButtons.Add(b);
            bar.AddChild(b);
        }
        bar.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0), MouseFilter = Control.MouseFilterEnum.Ignore });
        bar.AddChild(SmallButton("System", () => _screen.ShowWholeSystem(), "Show the whole system (Home)"));
        AddChild(bar);

        _status = Ui.Label("", 13, Ui.Dim);
        _status.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.KeepSize, 16);
        _status.GrowHorizontal = Control.GrowDirection.Begin;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(_status);

        _scaleBar = new ScaleBar { Camera = _screen.Camera };
        _scaleBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.KeepSize, 16);
        _scaleBar.Position += new Vector2(0, -40);
        AddChild(_scaleBar);

        var help = Ui.Label("Wheel zoom · drag pan · click select · double-click follow · right-click move · Space pause · 1-4 speed · Home system · F8 report · Esc title", 12, Ui.Dim);
        help.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.KeepSize, 16);
        help.GrowVertical = Control.GrowDirection.Begin;
        AddChild(help);

        _info = Ui.Label("", 14);
        _info.CustomMinimumSize = new Vector2(280, 0);
        _infoPanel = Ui.Panel(_info, 14);
        _infoPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 16);
        _infoPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _infoPanel.GrowVertical = Control.GrowDirection.Begin;
        AddChild(_infoPanel);
        _ = main;
    }

    private static Button SmallButton(string text, Action pressed, string tip)
    {
        var b = new Button { Text = text, TooltipText = tip, CustomMinimumSize = new Vector2(44, 32), ToggleMode = false, FocusMode = Control.FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", 15);
        b.Pressed += () => Main.Instance.RunGuarded($"button '{text}'", pressed);
        return b;
    }

    public void Refresh()
    {
        var sim = Main.Instance.Sim;
        if (sim == null) return;
        _date.Text = GameTime.Format(sim.World.Tick);
        _pause.Modulate = _screen.Paused ? Ui.Rival : Colors.White;
        for (var i = 0; i < _speedButtons.Count; i++)
            _speedButtons[i].Modulate = !_screen.Paused && i == _screen.SpeedIndex ? Ui.Player : new Color(1, 1, 1, 0.75f);
        _status.Text = $"seed {sim.World.Seed} · build {Main.Instance.Build}"
                       + (sim.RuleViolations > 0 ? $" · rule failures: {sim.RuleViolations}" : "")
                       + (_screen.Paused ? "\nPAUSED" : "");
        _scaleBar.QueueRedraw();

        var text = Describe(sim);
        _infoPanel.Visible = text != null;
        if (text != null) _info.Text = text;
    }

    private string? Describe(Simulation sim)
    {
        if (_screen.SelectedShip is Ship s)
        {
            var cls = GameData.ShipClasses.TryGetValue(s.ClassId, out var c) ? c : null;
            var lines = new List<string>
            {
                s.Name + (s.Side == Side.Player ? "" : "  (rival)"),
                cls?.Name ?? s.ClassId,
                cls?.Role ?? "",
                $"Top speed: {Format.Speed(s.MaxSpeed)}",
                s.Destination is Vec2 d ? $"Heading: {Format.Distance(Vec2.Distance(s.Position, d))} to go, about {Format.Hours(Vec2.Distance(s.Position, d) / s.MaxSpeed)}" : "Holding position",
            };
            if (_screen.IsFollowing(s.Id)) lines.Add("Following");
            return string.Join("\n", lines.Where(l => l != ""));
        }
        if (_screen.SelectedBody is Body b)
        {
            var lines = new List<string> { b.Name, $"{Format.Title(b.Kind.ToString())} · {b.Type}", $"Radius: {Format.Distance(b.Radius)}" };
            if (b.ParentId is int pid && sim.World.FindBody(pid) is Body parent)
            {
                lines.Add($"Orbits {parent.Name} at {Format.Distance(b.OrbitRadius)}");
                lines.Add($"Year: {Format.Hours(b.OrbitPeriodDays * 24)}");
            }
            var moons = sim.World.Bodies.Count(m => m.ParentId == b.Id && m.Kind == BodyKind.Moon);
            if (moons > 0) lines.Add($"Moons: {moons}");
            lines.Add(_screen.IsFollowing(b.Id) ? "Following" : "Double-click or F to follow");
            return string.Join("\n", lines);
        }
        return null;
    }
}

/// <summary>A ruler showing how far a stretch of the screen is at the current zoom.</summary>
public partial class ScaleBar : Control
{
    public MapCamera Camera = null!;

    public ScaleBar() => MouseFilter = MouseFilterEnum.Ignore;

    public override void _Draw()
    {
        if (Camera == null) return;
        var (mm, label) = Camera.NiceDistance(140);
        var px = (float)(mm * Camera.Scale);
        var y = 0f;
        DrawLine(new Vector2(0, y), new Vector2(px, y), Ui.Dim, 1.5f);
        DrawLine(new Vector2(0, y - 5), new Vector2(0, y + 5), Ui.Dim, 1.5f);
        DrawLine(new Vector2(px, y - 5), new Vector2(px, y + 5), Ui.Dim, 1.5f);
        DrawString(ThemeDB.FallbackFont, new Vector2(px + 8, y + 5), label, HorizontalAlignment.Left, -1, 13, Ui.Dim);
    }
}

/// <summary>Human-friendly numbers.</summary>
public static class Format
{
    public static string Distance(double mm)
    {
        if (mm >= Physics.MmPerAu * 0.05) return $"{mm / Physics.MmPerAu:0.00} AU";
        var km = mm * 1000;
        return km >= 100 ? $"{km:#,0} km" : $"{km:0.#} km";
    }

    public static string Speed(double mmPerHour) => $"{mmPerHour * 1000 / 3600:#,0} km/s";

    public static string Hours(double hours)
    {
        if (hours < 48) return $"{hours:0.#} hours";
        var days = hours / 24;
        if (days < 730) return $"{days:#,0} days";
        return $"{days / 365.25:0.#} years";
    }

    public static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
