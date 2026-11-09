using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Save;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The game screen: runs the clock, turns input into commands and hosts the map and the HUD.
/// It never changes the world directly; fleet orders go through commands.
///
///   Wheel              zoom at the cursor             Left click      select a fleet or body
///   Middle/right drag  pan                            Double-click    follow it (F follows the selection)
///   Right click        plan a route there             Shift+right     go there now with the last settings
///   Enter              fly the planned route          Esc             cancel the plan (else back to title)
///   O / D / S          orbit / drift / stop           Home            show the whole system
///   Space              pause / resume                 1 2 3 4, + -    speed 1x 2x 4x 8x
///   F5 / F9            quick save / quick load        F8              report a problem
///   Ctrl+Shift+F12     test crash                     Ctrl+Shift+F11  break a rule on purpose
/// </summary>
public partial class GameScreen : Node2D
{
    /// <summary>Game hours per real second at 1x, 2x, 4x, 8x. At 1x a game day takes 4 seconds.</summary>
    public static readonly double[] Speeds = { 6, 12, 24, 48 };
    public static readonly string[] SpeedNames = { "1x", "2x", "4x", "8x" };
    private const double AutosaveEverySeconds = 300; // real time spent playing (not paused)

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
    private double _sinceAutosave;
    private Hud _hud = null!;
    private double _planAge;

    /// <summary>The route planner's choices, kept between plans.</summary>
    public PlanSettings Settings { get; } = new();

    /// <summary>The route being planned, if the planner is open.</summary>
    public PlannerState? Planner { get; private set; }

    private Simulation Sim => _main.Sim!;
    public MapCamera Camera => _camera;
    public MapView Map => _map;
    public bool Paused => _paused;
    public int SpeedIndex => _speed;

    public override void _Ready()
    {
        _main = Main.Instance;
        _camera.ScreenSize = GetViewportRect().Size;

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
            _sinceAutosave += delta;
        }
        if (_sinceAutosave >= AutosaveEverySeconds)
        {
            _sinceAutosave = 0;
            _main.RunGuarded("autosave", () => SaveSlots.WriteAutosave(_main.Paths, Sim));
        }

        _map.UpdatePositions();
        if (_follow is int f && _map.DrawnPosition(f) is not { IsFinite: true }) _follow = null;
        _camera.Update(delta, _follow is int id ? _map.DrawnPosition(id) : null);
        if (_selected is int sel && Sim.World.FindBody(sel) == null && Sim.World.FindFleet(sel) == null) _selected = null;
        UpdatePlanner(delta);
        _map.HoverId = _map.HitTest(GetViewport().GetMousePosition());
        _map.SelectedId = _selected;
        _map.Preview = Planner?.Plan;
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

    /// <summary>Select a body or fleet (a ship selects its fleet).</summary>
    public void Select(int? id)
    {
        if (id is int shipId && Sim.World.FindShip(shipId) is Ship ship) id = ship.FleetId;
        if (id != _selected) Planner = null;
        _selected = id;
    }

    public Body? SelectedBody => _selected is int id ? Sim.World.FindBody(id) : null;
    public Fleet? SelectedFleet => _selected is int id ? Sim.World.FindFleet(id) : null;
    private Fleet? MyFleet => SelectedFleet is { Side: Side.Player } f ? f : null;

    // ---- Fleet orders -----------------------------------------------------------------------

    public void OrderHold(HoldStyle hold)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetHoldCommand(f.Id, hold));
    }

    public void OrderStop()
    {
        if (MyFleet is Fleet f) Sim.Issue(new StopFleetCommand(f.Id));
    }

    public void OrderSplit(IReadOnlyCollection<int> shipIds)
    {
        if (MyFleet is Fleet f && shipIds.Count > 0) Sim.Issue(new SplitFleetCommand(f.Id, shipIds.ToList()));
    }

    /// <summary>The nearest fleet of ours that could merge into the selected one.</summary>
    public Fleet? MergeCandidate() => MyFleet is Fleet f
        ? Sim.World.Fleets.Where(o => Fleets.CanMerge(f, o) == null).OrderBy(o => Vec2.Distance(o.Position, f.Position)).FirstOrDefault()
        : null;

    public void OrderMerge()
    {
        if (MyFleet is Fleet f && MergeCandidate() is Fleet other) Sim.Issue(new MergeFleetsCommand(f.Id, other.Id));
    }

    // ---- Route planner ----------------------------------------------------------------------

    public void StartPlanning(int? bodyId, Vec2 point)
    {
        if (MyFleet is not Fleet f) return;
        var name = bodyId is int b && Sim.World.FindBody(b) is Body body ? body.Name : "this point";
        Planner = new PlannerState(f.Id, bodyId, point, name);
        Replan();
    }

    public void CancelPlanning() => Planner = null;

    /// <summary>Work the plan out again (after a setting changes, and a few times a second as things move).</summary>
    public void Replan()
    {
        _planAge = 0;
        if (Planner is not PlannerState p || Sim.World.FindFleet(p.FleetId) is not Fleet f) { Planner = null; return; }
        _main.RunGuarded("planning a route", () => p.Plan = FlightPlanner.PlanRoute(Sim.World, f, Request(p)));
    }

    private FlightRequest Request(PlannerState p) =>
        new(p.TargetBodyId, p.Point, Settings.Speed, Settings.Arrival, Settings.Hold, Sim.World.Tick, Settings.Route);

    public void ConfirmPlan()
    {
        if (Planner is not PlannerState p || p.Plan is not { Ok: true, Flight: Flight flight }) return;
        var depart = Settings.Route == RouteKind.Wait ? Math.Max(Sim.World.Tick, (long)Math.Round(flight.DepartHour)) : Sim.World.Tick;
        Sim.Issue(new PlanFlightCommand(p.FleetId, Request(p) with { DepartTick = depart }));
        Planner = null;
    }

    private void UpdatePlanner(double delta)
    {
        if (Planner == null) return;
        if (MyFleet?.Id != Planner.FleetId) { Planner = null; return; }
        _planAge += delta;
        if (_planAge > 0.3) Replan();
    }
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
                    if (mb.ButtonIndex == MouseButton.Right && !_dragMoved) OrderMove(mb.Position, mb.ShiftPressed);
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
                else Select(hit);
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
            case Key.Enter or Key.KpEnter: ConfirmPlan(); break;
            case Key.O: OrderHold(HoldStyle.Orbit); break;
            case Key.D: OrderHold(HoldStyle.Drift); break;
            case Key.S: OrderStop(); break;
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
            case Key.Escape when Planner != null:
                CancelPlanning();
                break;
            case Key.Escape:
                SaveSlots.WriteAutosave(_main.Paths, Sim);
                _main.ShowTitle();
                break;
        }
    }

    /// <summary>Right click: plan a route to the body under the cursor, or to that point in space.</summary>
    private void OrderMove(Vector2 at, bool goNow)
    {
        if (MyFleet == null) return;
        var bodyId = _map.HitTestBody(at);
        var point = bodyId is int b ? Sim.World.FindBody(b)!.Position : _camera.ToWorld(at);
        StartPlanning(bodyId, point);
        if (goNow) ConfirmPlan();
    }
}

/// <summary>The route planner's settings. They stay as last used, so repeat orders are quick.</summary>
public sealed class PlanSettings
{
    public RouteKind Route = RouteKind.Direct;
    public double Speed = 0.7;
    public ArrivalStyle Arrival = ArrivalStyle.Brake;
    public HoldStyle Hold = HoldStyle.Orbit;
}

/// <summary>A route being planned: which fleet, where to, and the latest answer from the planner.</summary>
public sealed class PlannerState(int fleetId, int? targetBodyId, Vec2 point, string targetName)
{
    public int FleetId { get; } = fleetId;
    public int? TargetBodyId { get; } = targetBodyId;
    public Vec2 Point { get; } = point;
    public string TargetName { get; } = targetName;
    public FlightPlan? Plan { get; set; }
}
