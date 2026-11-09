using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Save;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The game screen: runs the clock, turns input into commands and hosts the map and the HUD.
/// It never changes the world directly; ship orders go through commands.
///
///   Wheel              zoom at the cursor             Left click      select a ship or body
///   Middle/right drag  pan                            Double-click    follow it (F follows the selection)
///   Right click        send the selected ship there   Home            show the whole system
///   Space              pause / resume                 1 2 3 4, + -    speed 1x 2x 4x 8x
///   F5 / F9            quick save / quick load        F8              report a problem
///   Esc                autosave and back to title
///   Ctrl+Shift+F12     test crash                     Ctrl+Shift+F11  break a rule on purpose
/// </summary>
public partial class GameScreen : Node2D
{
    /// <summary>Game hours per real second at 1x, 2x, 4x, 8x. At 1x a game day takes 4 seconds.</summary>
    public static readonly double[] Speeds = { 6, 12, 24, 48 };
    public static readonly string[] SpeedNames = { "1x", "2x", "4x", "8x" };
    private const int AutosaveEveryDays = 10;

    private Main _main = null!;
    private readonly MapCamera _camera = new();
    private MapView _map = null!;
    private double _accum;
    private int _speed;
    private bool _paused;
    private int? _selected;
    private int? _follow;
    private bool _dragging;
    private bool _dragMoved;
    private int _lastAutosaveDay;
    private Hud _hud = null!;

    private Simulation Sim => _main.Sim!;
    public MapCamera Camera => _camera;
    public MapView Map => _map;
    public bool Paused => _paused;
    public int SpeedIndex => _speed;

    public override void _Ready()
    {
        _main = Main.Instance;
        _camera.ScreenSize = GetViewportRect().Size;
        _lastAutosaveDay = Sim.World.Day;

        _map = new MapView { Sim = Sim, Camera = _camera };
        AddChild(_map);

        // Start on the home planet's neighbourhood: the inner system.
        _camera.Center = Vec2.Zero;
        _camera.FitRadius(1.8 * Physics.MmPerAu, instant: true);

        _hud = new Hud(this);
        AddChild(_hud);
        _map.UpdatePositions();
    }

    public override void _Process(double delta)
    {
        if (_main.Sim == null) return;
        _camera.ScreenSize = GetViewportRect().Size;
        if (!_paused)
        {
            _accum += delta * Speeds[_speed];
            var steps = Math.Min((int)_accum, 500); // never freeze the screen catching up
            _accum -= steps;
            if (steps > 0 && !_main.RunGuarded("running the simulation", () =>
                {
                    for (var i = 0; i < steps; i++)
                    {
                        _map.BeforeStep();
                        Sim.Step();
                    }
                }))
            {
                _paused = true;
                return;
            }
            _map.Blend = Math.Clamp(_accum, 0, 1);
        }
        if (Sim.World.Day - _lastAutosaveDay >= AutosaveEveryDays)
        {
            _lastAutosaveDay = Sim.World.Day;
            _main.RunGuarded("autosave", () => SaveSlots.WriteAutosave(_main.Paths, Sim));
        }

        _map.UpdatePositions();
        if (_follow is int f && _map.DrawnPosition(f) is not { IsFinite: true }) _follow = null;
        _camera.Update(delta, _follow is int id ? _map.DrawnPosition(id) : null);
        _map.HoverId = _map.HitTest(GetViewport().GetMousePosition());
        _map.SelectedId = _selected;
        _map.QueueRedraw();
        _hud.Refresh();
    }

    // ---- Commands the HUD can call --------------------------------------------------------

    public void SetSpeed(int index)
    {
        _speed = Math.Clamp(index, 0, Speeds.Length - 1);
        _paused = false;
    }

    public void TogglePause() => _paused = !_paused;

    public void ShowWholeSystem()
    {
        _follow = null;
        _camera.Center = Vec2.Zero;
        var furthest = Sim.World.Bodies.Where(b => b.Kind == BodyKind.Planet).Select(b => b.OrbitRadius).DefaultIfEmpty(1).Max();
        _camera.FitRadius(furthest * 1.05);
    }

    /// <summary>Centre on a body or ship and keep it centred. Zooms in to a sensible size for a body.</summary>
    public void Follow(int id, bool zoom)
    {
        _follow = id;
        if (!zoom) return;
        var body = Sim.World.FindBody(id);
        if (body == null) return;
        var moons = Sim.World.Bodies.Where(m => m.ParentId == body.Id).Select(m => m.OrbitRadius).DefaultIfEmpty(0).Max();
        // A planet with moons: show the moon system. Otherwise: show the body itself, nicely large.
        if (body.Kind == BodyKind.Star) _camera.FitRadius(1.8 * Physics.MmPerAu);
        else if (moons > 0) _camera.FitRadius(moons * 1.15);
        else _camera.SetScale(60 / body.Radius);
    }

    public void Select(int? id) => _selected = id;

    public Body? SelectedBody => _selected is int id ? Sim.World.FindBody(id) : null;
    public Ship? SelectedShip => _selected is int id ? Sim.World.FindShip(id) : null;
    public bool IsFollowing(int id) => _follow == id;

    // ---- Input ----------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                var steps = wheel.ButtonIndex == MouseButton.WheelUp ? 1 : -1;
                _camera.ZoomAt(_follow != null ? _camera.ScreenCenter : wheel.Position, steps * (wheel.ShiftPressed ? 3 : 1));
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
                if (motion.Relative.Length() > 1 || _dragMoved)
                {
                    _dragMoved = true;
                    _follow = null;
                    _camera.Pan(motion.Relative);
                }
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                var hit = _map.HitTest(click.Position);
                if (click.DoubleClick && hit is int id) Follow(id, zoom: true);
                else _selected = hit;
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
            _map.BeforeStep();
            Sim.Step();
            Sim.CheckRules();
            _main.Toast($"Rule checks found {Sim.RuleViolations} failure(s); see the log");
            return;
        }
        switch (key.Keycode)
        {
            case Key.Space: TogglePause(); break;
            case Key.Key1: SetSpeed(0); break;
            case Key.Key2: SetSpeed(1); break;
            case Key.Key3: SetSpeed(2); break;
            case Key.Key4: SetSpeed(3); break;
            case Key.Equal or Key.KpAdd: SetSpeed(_speed + 1); break;
            case Key.Minus or Key.KpSubtract: SetSpeed(_speed - 1); break;
            case Key.Home or Key.H: ShowWholeSystem(); break;
            case Key.F when _selected is int id: Follow(id, zoom: !key.ShiftPressed); break;
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

    private void OrderMove(Vector2 at)
    {
        if (SelectedShip is not { Side: Side.Player } ship) return;
        var p = _camera.ToWorld(at);
        Sim.Issue(new MoveShipCommand(ship.Id, p.X, p.Y));
    }
}
