using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Save;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The map. Draws the simulation and turns input into commands; it never changes the world directly.
///
///   Mouse wheel        zoom at the cursor            Left click      select one of your ships
///   Middle/right drag  pan                           Right click     send the selected ship there
///   Space              pause / resume                1 2 3 4         game speed
///   F5 / F9            quick save / quick load       F8              report a problem
///   Esc                autosave and back to title
///   Ctrl+Shift+F12     test crash (checks crash reports)   Ctrl+Shift+F11  break a rule on purpose
/// </summary>
public partial class GameScreen : Node2D
{
    // Ticks (game hours) per real second at each speed.
    private static readonly double[] Speeds = { 4, 24, 96, 480 };
    private const int AutosaveEveryDays = 10;

    private Main _main = null!;
    private double _scale = 0.002;       // pixels per Mm
    private Vector2 _offset;             // screen position of the sun
    private double _accum;
    private int _speed;
    private bool _paused;
    private int? _selected;
    private bool _dragging;
    private bool _dragMoved;
    private Label _hud = null!;
    private int _lastAutosaveDay;
    private Font _font = null!;

    private Simulation Sim => _main.Sim!;

    public override void _Ready()
    {
        _main = Main.Instance;
        _font = ThemeDB.FallbackFont;
        _offset = GetViewportRect().Size / 2;
        _lastAutosaveDay = Sim.World.Day;

        var layer = new CanvasLayer();
        _hud = Ui.Label("", 15);
        _hud.Position = new Vector2(16, 12);
        layer.AddChild(_hud);
        var help = Ui.Label("Wheel zoom · drag pan · click select · right-click move · Space pause · 1-4 speed · F5/F9 save/load · F8 report · Esc title", 13, Ui.Dim);
        help.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.KeepSize, 12);
        help.GrowVertical = Control.GrowDirection.Begin;
        layer.AddChild(help);
        AddChild(layer);
    }

    public override void _Process(double delta)
    {
        if (_main.Sim == null) return;
        if (!_paused)
        {
            _accum += delta * Speeds[_speed];
            var steps = Math.Min((int)_accum, 2000); // never freeze the screen catching up
            _accum -= steps;
            if (steps > 0 && !_main.RunGuarded("running the simulation", () => Sim.StepMany(steps)))
            {
                _paused = true;
                return;
            }
        }
        if (Sim.World.Day - _lastAutosaveDay >= AutosaveEveryDays)
        {
            _lastAutosaveDay = Sim.World.Day;
            _main.RunGuarded("autosave", () => SaveSlots.WriteAutosave(_main.Paths, Sim));
        }
        _hud.Text = $"{GameTime.Format(Sim.World.Tick)}    {(_paused ? "PAUSED" : $"speed {_speed + 1}")}    seed {Sim.World.Seed}    build {_main.Build}"
                    + (Sim.RuleViolations > 0 ? $"    rule failures: {Sim.RuleViolations}" : "");
        QueueRedraw();
    }

    // ---- Drawing --------------------------------------------------------------------------

    private Vector2 ToScreen(Vec2 p) => _offset + new Vector2((float)(p.X * _scale), (float)(p.Y * _scale));
    private Vec2 ToWorld(Vector2 s) => new((s.X - _offset.X) / _scale, (s.Y - _offset.Y) / _scale);

    public override void _Draw()
    {
        if (_main.Sim == null) return;
        var w = Sim.World;
        foreach (var b in w.Bodies)
        {
            if (b.ParentId is int pid && w.FindBody(pid) is { } parent)
            {
                var r = (float)(b.OrbitRadius * _scale);
                if (r > 6) DrawArc(ToScreen(parent.Position), r, 0, Mathf.Tau, 256, Ui.Orbit, 1, true);
            }
        }
        foreach (var b in w.Bodies)
        {
            var at = ToScreen(b.Position);
            var color = b.Kind switch
            {
                BodyKind.Star => new Color("ffd27a"),
                BodyKind.Moon => new Color("9aa3ad"),
                _ => new Color("6f9fd8"),
            };
            var minR = b.Kind == BodyKind.Star ? 9f : b.Kind == BodyKind.Moon ? 2.5f : 5f;
            var r = Math.Max(minR, (float)(b.Radius * _scale));
            DrawCircle(at, r, color);
            if (b.Kind != BodyKind.Moon || _scale > 0.05)
                DrawString(_font, at + new Vector2(r + 4, 4), b.Name, HorizontalAlignment.Left, -1, 13, Ui.Dim);
        }
        // Orders given while paused only reach the simulation on its next tick; show them now anyway.
        var orders = PendingDestinations();
        foreach (var s in w.Ships)
        {
            if (!s.Position.IsFinite) continue;
            var at = ToScreen(s.Position);
            var color = s.Side == Side.Player ? Ui.Player : Ui.Rival;
            var dest = orders.TryGetValue(s.Id, out var pending) ? pending : s.Destination;
            if (dest is Vec2 d) DrawDashedLine(at, ToScreen(d), new Color(color, 0.5f), 1, 6);
            DrawColoredPolygon(new[] { at + new Vector2(0, -7), at + new Vector2(6, 5), at + new Vector2(-6, 5) }, color);
            if (s.Id == _selected) DrawArc(at, 12, 0, Mathf.Tau, 32, color, 1.5f, true);
            DrawString(_font, at + new Vector2(9, -6), s.Name, HorizontalAlignment.Left, -1, 12, color);
        }
    }

    /// <summary>Where queued (not yet applied) orders will send each ship; null means a stop order.</summary>
    private Dictionary<int, Vec2?> PendingDestinations()
    {
        var result = new Dictionary<int, Vec2?>();
        foreach (var c in Sim.PendingCommands)
        {
            if (c is MoveShipCommand m) result[m.ShipId] = new Vec2(m.X, m.Y);
            else if (c is StopShipCommand st) result[st.ShipId] = null;
        }
        return result;
    }

    // ---- Input ----------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                Zoom(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp ? 1.25 : 0.8);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle or MouseButton.Right } mb:
                if (mb.Pressed) { _dragging = true; _dragMoved = false; }
                else
                {
                    _dragging = false;
                    if (mb.ButtonIndex == MouseButton.Right && !_dragMoved) OrderMove(mb.Position);
                }
                break;
            case InputEventMouseMotion motion when _dragging:
                _offset += motion.Relative;
                if (motion.Relative.Length() > 2) _dragMoved = true;
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                Select(click.Position);
                break;
            case InputEventKey { Pressed: true, Echo: false } key:
                OnKey(key);
                break;
        }
    }

    private void OnKey(InputEventKey key)
    {
        if (key.CtrlPressed && key.ShiftPressed && key.Keycode == Key.F12)
        {
            _main.Log.Warn("ui", "Test crash requested from the keyboard");
            Sim.Issue(new DebugCrashCommand("Ctrl+Shift+F12 pressed") { Source = "player" });
            _paused = false;
            return;
        }
        if (key.CtrlPressed && key.ShiftPressed && key.Keycode == Key.F11)
        {
            _main.Log.Warn("ui", "Rule break requested from the keyboard");
            Sim.Issue(new DebugBreakRuleCommand());
            Sim.Step();
            Sim.CheckRules();
            _main.Toast($"Rule checks found {Sim.RuleViolations} failure(s); see the log");
            return;
        }
        switch (key.Keycode)
        {
            case Key.Space: _paused = !_paused; break;
            case Key.Key1: _speed = 0; _paused = false; break;
            case Key.Key2: _speed = 1; _paused = false; break;
            case Key.Key3: _speed = 2; _paused = false; break;
            case Key.Key4: _speed = 3; _paused = false; break;
            case Key.F5:
                Sim.ToSave().WriteTo(SaveSlots.PathFor(_main.Paths, SaveSlots.QuickSave));
                _main.Log.Info("save", "Quick save");
                _main.Toast("Quick saved");
                break;
            case Key.F9:
                var path = SaveSlots.PathFor(_main.Paths, SaveSlots.QuickSave);
                if (File.Exists(path)) _main.LoadGame(path);
                else _main.Toast("No quick save yet");
                break;
            case Key.Escape:
                SaveSlots.WriteAutosave(_main.Paths, Sim);
                _main.ShowTitle();
                break;
        }
    }

    private void Zoom(Vector2 at, double factor)
    {
        var before = ToWorld(at);
        _scale = Math.Clamp(_scale * factor, 0.0002, 2.0);
        var after = ToScreen(before);
        _offset += at - after;
    }

    private void Select(Vector2 at)
    {
        _selected = null;
        var best = 14f;
        foreach (var s in Sim.World.Ships)
        {
            if (s.Side != Side.Player || !s.Position.IsFinite) continue;
            var d = ToScreen(s.Position).DistanceTo(at);
            if (d < best) { best = d; _selected = s.Id; }
        }
    }

    private void OrderMove(Vector2 at)
    {
        if (_selected is not int id) return;
        var p = ToWorld(at);
        Sim.Issue(new MoveShipCommand(id, p.X, p.Y));
    }
}
