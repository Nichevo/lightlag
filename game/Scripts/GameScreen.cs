using Godot;
using Lightlag.Sim.Core;
using Engagement_ = Lightlag.Sim.Core.Engagement;
using Lightlag.Sim.Save;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The game screen: runs the clock, turns input into commands and hosts the map and the HUD.
/// It never changes the world directly; fleet orders go through commands.
///
///   Wheel              zoom at the cursor             Left click      select a fleet or body
///   Middle/right drag  pan (also WASD / arrows)       Double-click    follow it (F follows the selection)
///   Left drag          box: pick several fleets       Shift+click     add or remove a fleet from the pick
///   Tab                next fleet of yours            M               merge, or gather the picked fleets
///   Right click        plan a route there             Shift+right     go there now with the last settings
///   Enter              fly the planned route          Esc             cancel the plan (else back to title)
///   O / G / X          orbit / drift / stop           Home            show the whole system
///   P                  pause when enemies are spotted F7              debug: show every fleet
///   Ctrl+right click   add a waypoint to the route    Drag a waypoint move it (the route replans live)
///   Right click enemy  plan an attack on it           R               combat report
///   Space              pause / resume                 1 2 3 4, + -    speed 1x 2x 4x 8x
///   E                  empire: build, research, claims
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
    private double _lastStepHours = 1;
    private int _lastBattleSeen;
    private int _lastReportShown;
    private readonly List<int> _group = new();
    private Vector2? _leftDown;
    private bool _boxing;
    private int? _draggingWaypoint;
    private bool _draggingEnd;
    private readonly HashSet<(int, double)> _spotted = new();

    /// <summary>When each enemy fleet was last in sight: once spotted, it never pauses the game again when it slips out of sight and back.</summary>
    private readonly Dictionary<int, double> _lastInSight = new();

    /// <summary>The same fleets meeting again within this many hours of their last contact ending do not pause the game again.</summary>
    private const double RecontactQuietHours = 48;

    /// <summary>Pause the game when an enemy fleet comes into sight.</summary>
    public bool PauseOnContact { get; set; } = true;

    /// <summary>Debug view (F7): every fleet is drawn, seen or not.</summary>
    public bool RevealAll { get; private set; }
    public void ToggleRevealAll() { RevealAll = !RevealAll; _map.RevealAll = RevealAll; }

    /// <summary>Map panning with the keyboard, in screen pixels per real second (Shift for faster).</summary>
    private const float KeyPanSpeed = 900;

    /// <summary>Battle time runs this much slower than normal time at the same speed setting.</summary>
    public const double BattleSlowdown = 1.0 / 360;

    /// <summary>The battle whose report is open, if any.</summary>
    public Battle? Report { get; private set; }

    /// <summary>Battle time is running (a standoff, where nobody can fight, runs at normal speed).</summary>
    public bool InBattle => Sim.World.Sub != 0 || (Sim.World.ActiveBattle != null && !Combat.Standoff(Sim.World));

    /// <summary>The empire panel (E): Industry, shipyard, research, claims.</summary>
    public bool ShowEmpire { get; set; }

    /// <summary>The win or lose panel is open (it opens once, when the game ends).</summary>
    public bool ShowEnd { get; private set; }
    private bool _endSeen;
    public void CloseEnd() => ShowEnd = false;
    public void OpenEnd() { _endSeen = true; ShowEnd = true; }

    private readonly Dictionary<int, Side?> _owners = new();
    private readonly HashSet<int> _knownShips = new();
    private int _researchSeen = -1;
    private double? _yardAttackSeen;
    private readonly HashSet<int> _claimsWarned = new();
    private readonly Dictionary<(int Enemy, int Mine), double> _chaseWarned = new();
    private long _chaseCheckedTick = -1;

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
        _map.DrawHour = Sim.World.Hour;
        _map.UpdatePositions();
    }

    public override void _Process(double delta)
    {
        if (_main.Sim == null) return;
        _camera.ScreenSize = GetViewportRect().Size;
        if (!_paused)
        {
            // _accum counts simulation steps owed. A step is an hour normally, or 10 game seconds in
            // battle time, which runs 360 times slower: 1x is then one game minute per second.
            var cost = Sim.PeekStepHours();
            var rate = Speeds[_speed] * (cost < GameTime.HoursPerTick ? BattleSlowdown : 1);
            _accum += delta * rate / cost;
            var switched = false;
            var ok = _main.RunGuarded("running the simulation", () =>
            {
                var done = 0;
                while (_accum >= 1 && done < 2000) // never freeze the screen catching up
                {
                    var next = Sim.PeekStepHours();
                    if (next != cost) { switched = true; break; } // switching in or out of battle time
                    _map.BeforeStep();
                    Sim.Step();
                    _lastStepHours = next;
                    _accum -= 1;
                    done++;
                }
                if (done == 2000) _accum = 0;
            });
            if (!ok)
            {
                _paused = true;
                return;
            }
            _map.Blend = Math.Clamp(_accum, 0, 1);
            _sinceAutosave += delta;
            WatchBattles();
            WatchSightings();
            WatchChasers();
            WatchEconomy();
            // Pausing, or switching in or out of battle time: show where things really are now, so the
            // map neither stops part way back to the last step nor jumps when time runs again.
            if (switched || _paused) SnapToNow();
        }
        if (_sinceAutosave >= AutosaveEverySeconds)
        {
            _sinceAutosave = 0;
            _main.RunGuarded("autosave", () => SaveSlots.WriteAutosave(_main.Paths, Sim));
        }

        KeyboardPan(delta);
        _map.TurnShips(delta);
        _map.DrawHour = Sim.World.Hour - (1 - _map.Blend) * _lastStepHours;
        _map.UpdatePositions();
        if (_follow is int f && _map.DrawnPosition(f) is not { IsFinite: true }) _follow = null;
        _camera.Update(delta, _follow is int id ? _map.DrawnPosition(id) : null);
        if (_selected is int sel && Sim.World.FindBody(sel) == null && Sim.World.FindFleet(sel) == null) _selected = null;
        UpdatePlanner(delta);
        UpdateEngagement(delta);
        _map.HoverId = _map.HitTest(GetViewport().GetMousePosition());
        _map.SelectedId = _selected;
        _group.RemoveAll(g => Sim.World.FindFleet(g) == null);
        if (_group.Count == 1) { var only = _group[0]; _group.Clear(); _selected = only; }
        _map.GroupIds = _group;
        _map.Preview = Planner?.Plan;
        _map.PlannerWaypoints = Planner?.Waypoints;
        _map.DrawHour = Sim.World.Hour - (1 - _map.Blend) * _lastStepHours;
        _map.QueueRedraw();
        _hud.Refresh();
    }

    // ---- Commands the HUD can call --------------------------------------------------------

    public void SetSpeed(int index)
    {
        _speed = Math.Clamp(index, 0, Speeds.Length - 1);
        _paused = false;
    }

    public void TogglePause()
    {
        _paused = !_paused;
        if (_paused) SnapToNow();
    }

    /// <summary>Draw the simulation exactly as it is now: no blending back towards the step before.</summary>
    private void SnapToNow()
    {
        _map.BeforeStep();
        _accum = 0;
        _lastStepHours = 0;
        _map.Blend = 0;
    }

    public bool IsSandbox => Sim.World.Scenario == Scenarios.BattleSandbox;

    // ---- Engagement screen --------------------------------------------------------------------

    /// <summary>The engagement on screen: your fleet and the enemy fleet (null when the map is shown).</summary>
    public (int Mine, int Enemy)? Engagement { get; private set; }
    public EngagementInfo? EngagementNow { get; private set; }
    private double _engagementAge;

    /// <summary>Open the engagement screen for one of your fleets (against the given enemy, or the one it is most concerned with).</summary>
    public void OpenEngagement(int mineId, int? enemyId = null)
    {
        var w = Sim.World;
        if (w.FindFleet(mineId) is not { Side: Side.Player } mine) return;
        var enemy = enemyId ?? Engagement_.EnemyFor(w, mine);
        if (enemy is not int e || Sensors.Find(w, Side.Player, e) == null)
        {
            _main.Toast($"{mine.Name} has no enemy fleet in sight or on record");
            return;
        }
        Planner = null;
        ShowEmpire = false;
        Engagement = (mineId, e);
        Select(mineId);
        _engagementAge = 1;
        UpdateEngagement(0);
        if (EngagementNow is EngagementInfo info)
        {
            // Fit both fleets and the closest pass, and at least the missile ranges.
            var spread = Math.Max(Vec2.Distance(mine.Position, info.EnemyAt), GameData.Combat.Missile.LaunchRangeMm);
            _follow = mineId;
            _camera.FitRadius(spread * 1.4);
        }
    }

    /// <summary>Every engagement you could look at now, nearest first (refreshed with the screen).</summary>
    public List<Engagement_.Pair> Engagements { get; private set; } = new();

    /// <summary>Go to the next (or previous) engagement in the list, keeping the zoom.</summary>
    public void CycleEngagement(int step)
    {
        Engagements = Engagement_.All(Sim.World, Side.Player);
        if (Engagements.Count == 0) { _main.Toast("No other engagements: no enemy fleets near your fleets"); return; }
        var at = Engagement is (int m, int e) ? Engagements.FindIndex(p => p.Mine == m && p.Enemy == e) : -1;
        if (at < 0 && step < 0) at = 0;
        var next = Engagements[((at + step) % Engagements.Count + Engagements.Count) % Engagements.Count];
        OpenEngagement(next.Mine, next.Enemy);
    }

    public void CloseEngagement()
    {
        Engagement = null;
        EngagementNow = null;
    }

    public void ToggleEngagement()
    {
        if (Engagement != null) { CloseEngagement(); return; }
        if (MyFleet is Fleet f) OpenEngagement(f.Id);
        else _main.Toast("Select one of your fleets first (V opens its engagement screen)");
    }

    private void UpdateEngagement(double delta)
    {
        if (Engagement is not (int mineId, int enemyId)) { _map.Engagement = null; return; }
        _engagementAge += delta;
        if (_engagementAge < 0.25 && EngagementNow != null) { _map.Engagement = EngagementNow; return; }
        _engagementAge = 0;
        var w = Sim.World;
        if (w.FindFleet(mineId) is not Fleet mine || Sensors.Find(w, Side.Player, enemyId) == null)
        {
            CloseEngagement();
            _map.Engagement = null;
            return;
        }
        _main.RunGuarded("engagement", () =>
        {
            EngagementNow = Engagement_.Of(w, mine, enemyId);
            Engagements = Engagement_.All(w, Side.Player);
        });
        _map.Engagement = EngagementNow;
    }

    /// <summary>Sandbox start: paused, looking at both fleets, with yours selected.</summary>
    private Action GoToFleet(int id) => () => { if (Sim.World.FindFleet(id) != null) { Select(id); _follow = id; } };
    private Action GoToBody(int id) => () => { if (Sim.World.FindBody(id) != null) { Select(id); Follow(id, true); } };

    /// <summary>Centre on an enemy fleet: follow it while in sight, else go to where it is expected to be.</summary>
    public void GoToSighting(int fleetId)
    {
        var w = Sim.World;
        var s = Sensors.Find(w, Side.Player, fleetId);
        if (s == null || w.FindFleet(fleetId) is not Fleet f) return;
        Select(fleetId);
        if (s.Visible || RevealAll) _follow = f.Id;
        else { _follow = null; _camera.Center = s.Guess(w.Hour); }
        if (_camera.TargetScale < 0.002) _camera.FitRadius(20000);
    }

    /// <summary>The fleets that put the game in battle time: an active battle's, or a pair that could meet this hour.</summary>
    private List<Fleet> BattleFleets()
    {
        var w = Sim.World;
        if (w.ActiveBattle is Battle b && !Combat.Standoff(w))
        {
            var names = b.For(Side.Player).Fleets.Concat(b.For(Side.Rival).Fleets).ToHashSet();
            var inIt = w.Fleets.Where(f => names.Contains(f.Name)).ToList();
            if (inIt.Count > 0) return inIt;
        }
        if (Combat.PossibleMeeting(w) is var (mine, theirs)) return new List<Fleet> { mine, theirs };
        // Only missiles left in flight: whoever fired or is targeted.
        return w.Missiles.Select(m => w.FindShip(m.TargetShipId)).OfType<Ship>().Select(s => w.FleetOf(s)).OfType<Fleet>().Distinct().ToList();
    }

    /// <summary>The battle time banner: who, where, and missiles in flight. The player only hears about enemies they can see.</summary>
    public string BattleLine()
    {
        var w = Sim.World;
        var fleets = BattleFleets();
        var mine = fleets.Where(f => f.Side == Side.Player).Select(f => f.Name).ToList();
        var seen = fleets.Where(f => f.Side != Side.Player && _map.Shown(f)).Select(f => f.Name).ToList();
        var at = fleets.Count > 0 ? fleets[0].Position : Vec2.Zero;
        var near = w.Bodies.Where(b => b.Kind != BodyKind.Star).OrderBy(b => Vec2.Distance(b.Position, at)).FirstOrDefault();
        var where = near != null ? $" near {near.Name}" : "";
        var fighting = w.ActiveBattle != null && !Combat.Standoff(w);
        var who = mine.Count == 0 ? "" : fighting
            ? $": {string.Join(", ", mine)}{(seen.Count > 0 ? " against " + string.Join(", ", seen) : "")}"
            : $": {string.Join(", ", mine)} could meet {(seen.Count > 0 ? string.Join(", ", seen) : "an enemy")} within the hour";
        var missiles = w.Missiles.Count > 0 ? $"  ·  {w.Missiles.Count} missiles in flight" : "";
        return $"BATTLE TIME{who}{where}{missiles}  (click to go there)";
    }

    /// <summary>Centre the map on the fleets in the battle (or about to meet) and follow the player's one.</summary>
    public void FocusBattle()
    {
        var fleets = BattleFleets();
        if (fleets.Count == 0) return;
        var center = fleets.Aggregate(Vec2.Zero, (a, f) => a + f.Position) * (1.0 / fleets.Count);
        var spread = fleets.Max(f => Vec2.Distance(f.Position, center));
        var mine = fleets.FirstOrDefault(f => f.Side == Side.Player);
        if (mine != null) { _follow = mine.Id; Select(mine.Id); }
        else { _follow = null; _camera.Center = center; }
        _camera.FitRadius(Math.Max(spread * 2.5, 300));
        var theirs = fleets.FirstOrDefault(f => f.Side != Side.Player && Sensors.Find(Sim.World, Side.Player, f.Id) != null);
        if (mine != null) OpenEngagement(mine.Id, theirs?.Id);
    }

    public void FocusSandbox()
    {
        _paused = true;
        var fleets = Sim.World.Fleets;
        if (fleets.Count == 0) return;
        var center = fleets.Aggregate(Vec2.Zero, (a, f) => a + f.Position) * (1.0 / fleets.Count);
        var spread = fleets.Max(f => Vec2.Distance(f.Position, center));
        _camera.Center = center;
        _camera.FitRadius(Math.Max(spread * 1.6, 120), instant: true);
        if (fleets.FirstOrDefault(f => f.Side == Side.Player) is Fleet mine) Select(mine.Id);
        _main.Toast("Battle sandbox: right-click the enemy fleet to plan an attack, then Enter");
    }

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
        _group.Clear();
    }

    /// <summary>Several of your fleets picked at once (two or more), largest first; empty otherwise.</summary>
    public IReadOnlyList<Fleet> Group => _group.Count < 2 ? Array.Empty<Fleet>()
        : _group.Select(id => Sim.World.FindFleet(id)).OfType<Fleet>()
            .OrderByDescending(f => f.ShipIds.Count).ThenBy(f => f.Id).ToList();

    /// <summary>Pick these fleets together (only yours count).</summary>
    public void SelectGroup(IEnumerable<int> fleetIds)
    {
        var mine = fleetIds.Where(id => Sim.World.FindFleet(id) is { Side: Side.Player }).Distinct().ToList();
        Planner = null;
        _group.Clear();
        if (mine.Count == 0) { _selected = null; return; }
        if (mine.Count == 1) { _selected = mine[0]; return; }
        _group.AddRange(mine);
        _selected = null;
    }

    /// <summary>Shift+click: add a fleet to the pick, or take it out.</summary>
    private void ToggleInGroup(int fleetId)
    {
        if (Sim.World.FindFleet(fleetId) is not { Side: Side.Player }) { Select(fleetId); return; }
        var picked = _group.Count > 0 ? new List<int>(_group) : _selected is int s && Sim.World.FindFleet(s) is { Side: Side.Player } ? new List<int> { s } : new List<int>();
        if (!picked.Remove(fleetId)) picked.Add(fleetId);
        SelectGroup(picked);
    }

    /// <summary>Select a fleet from the fleet list and centre on it.</summary>
    public void PickFleet(int fleetId)
    {
        Select(fleetId);
        if (Sim.World.FindFleet(fleetId) != null) Follow(fleetId, zoom: false);
    }

    public void ShowBodyMenu(bool open) => _hud.ShowBodyMenu(open);

    /// <summary>Pick a planet or moon from the list: select it, fly the view there and keep following it (pan to stop).</summary>
    public void PickBody(int bodyId)
    {
        Select(bodyId);
        if (Sim.World.FindBody(bodyId) != null) Follow(bodyId, zoom: true);
    }

    /// <summary>Select a ship from the fleet list: its fleet is selected and the ship ticked, ready to split off.</summary>
    public void PickShip(int shipId)
    {
        if (Sim.World.FindShip(shipId) is not Ship ship) return;
        PickFleet(ship.FleetId);
        _hud.TickShip(shipId);
    }

    /// <summary>Tab: the next of your fleets.</summary>
    public void NextFleet(int step)
    {
        var mine = Sim.World.Fleets.Where(f => f.Side == Side.Player).Select(f => f.Id).ToList();
        if (mine.Count == 0) return;
        var at = _selected is int id ? mine.IndexOf(id) : -1;
        PickFleet(mine[((at + step) % mine.Count + mine.Count) % mine.Count]);
    }

    public Body? SelectedBody => _selected is int id ? Sim.World.FindBody(id) : null;
    public Fleet? SelectedFleet => _selected is int id ? Sim.World.FindFleet(id) : null;
    private Fleet? MyFleet => SelectedFleet is { Side: Side.Player } f ? f : null;

    // ---- Battles ------------------------------------------------------------------------------

    /// <summary>Pause when a battle starts (so orders can be given), and show the report when it ends.</summary>
    private void WatchBattles()
    {
        var w = Sim.World;
        if (w.ActiveBattle is Battle b && b.Id != _lastBattleSeen)
        {
            _lastBattleSeen = b.Id;
            // The same fleets meeting again soon after a contact ended (drifting in and out of range): no new pause.
            var again = w.Battles.Any(o => o.Id != b.Id && o.EndHour is double end && b.StartHour - end < RecontactQuietHours
                                           && o.For(Side.Player).Fleets.Intersect(b.For(Side.Player).Fleets).Any()
                                           && o.For(Side.Rival).Fleets.Intersect(b.For(Side.Rival).Fleets).Any());
            if (again)
            {
                _main.AddNews($"Contact again: {string.Join(", ", b.For(Side.Player).Fleets)} and {string.Join(", ", b.For(Side.Rival).Fleets)}", FocusBattle);
            }
            else
            {
                _paused = true;
                _main.Toast($"Contact: {string.Join(", ", b.For(Side.Player).Fleets)} and {string.Join(", ", b.For(Side.Rival).Fleets)}. Battle time; paused.",
                    onClick: FocusBattle);
            }
        }
        var last = w.Battles.Count > 0 ? w.Battles[^1] : null;
        if (last is { EndHour: not null } && last.Id != _lastReportShown)
        {
            _lastReportShown = last.Id;
            Report = last;
            // Keep the result in the News menu: who fought, losses on each side; click to reopen the report.
            var us = last.For(Side.Player);
            var them = last.For(Side.Rival);
            var battle = last;
            var lost = us.ShipsLost.Count == 0 ? "no ships lost" : $"lost {us.ShipsLost.Count} ({string.Join(", ", us.ShipsLost)})";
            var sunk = them.ShipsLost.Count == 0 ? "none of theirs destroyed" : $"destroyed {them.ShipsLost.Count} of theirs";
            _main.AddNews($"Battle over: {string.Join(", ", us.Fleets)} and {string.Join(", ", them.Fleets)}: {lost}, {sunk}. "
                          + $"Railgun hits {us.RailHits}/{us.RailShots}, missile hits {us.MissileHits}/{us.MissilesFired}",
                () => Report = battle, keep: true);
        }
    }

    /// <summary>A new enemy fleet in sight: say so, and pause if asked to.</summary>
    private void WatchSightings()
    {
        var w = Sim.World;
        foreach (var s in w.Sightings)
        {
            if (s.SeenBy != Side.Player || !s.Visible) continue;
            var seenBefore = _lastInSight.ContainsKey(s.FleetId);
            _lastInSight[s.FleetId] = w.Hour;
            if (!_spotted.Add((s.FleetId, s.SpottedHour))) continue;
            if (s.SpottedHour < w.Hour - 1) continue; // already in sight when the game was loaded
            if (seenBefore)
            {
                // Spotted before this session: back in sight is a quiet news line, no pop-up and no pause.
                var id = s.FleetId;
                _main.AddNews($"{s.Name} back in sight", () => GoToSighting(id));
                continue;
            }
            var nearest = w.Fleets.Where(f => f.Side == Side.Player).OrderBy(f => Vec2.Distance(f.Position, s.Position)).FirstOrDefault();
            var where = nearest != null ? $", {Vec2.Distance(nearest.Position, s.Position) * 1000:#,0} km from {nearest.Name}" : "";
            var fleetId = s.FleetId;
            _main.Toast($"Spotted {s.Name} ({s.Ships} ships, {FleetPanel.StateText(s.State)}){where}" + (PauseOnContact ? ". Paused." : ""),
                onClick: () => GoToSighting(fleetId));
            if (PauseOnContact) _paused = true;
        }
    }

    /// <summary>Once an hour: warn when a seen enemy fleet's track is closing on one of your fleets.</summary>
    private void WatchChasers()
    {
        var w = Sim.World;
        if (w.Tick == _chaseCheckedTick) return;
        _chaseCheckedTick = w.Tick;
        foreach (var s in w.Sightings.Where(s => s.SeenBy == Side.Player && s.Visible))
            foreach (var mine in w.Fleets.Where(f => f.Side == Side.Player))
            {
                if (Sensors.HeadingFor(s, mine, w.Hour) is not double eta) continue;
                var key = (s.FleetId, mine.Id);
                if (_chaseWarned.TryGetValue(key, out var when) && w.Hour - when < 24) { _chaseWarned[key] = w.Hour; continue; }
                _chaseWarned[key] = w.Hour;
                var enemyId = s.FleetId;
                var mineId = mine.Id;
                _main.Toast($"{s.Name} ({s.Ships} ships) is heading for {mine.Name}: about {Format.Hours(eta)} away at {Format.Speed((s.Velocity - mine.Velocity).Length)} closing",
                    onClick: () => OpenEngagement(mineId, enemyId));
            }
    }

    /// <summary>Says when ships are launched, research finishes, planets change hands, and when the game ends.</summary>
    private void WatchEconomy()
    {
        var w = Sim.World;
        if (!w.Loop) return;
        var first = _researchSeen < 0;
        var me = Economy.Of(w, Side.Player);
        if (!first && me.Researched.Count > _researchSeen)
            _main.Toast($"Research done: {GameData.Research(me.Researched[^1]).Name}");
        _researchSeen = me.Researched.Count;

        foreach (var s in w.Ships.Where(s => s.Side == Side.Player))
            if (_knownShips.Add(s.Id) && !first && w.FleetOf(s) is Fleet f)
                _main.Toast(f.JoinFleetId is int j && w.FindFleet(j) is Fleet to && f.ShipIds.Count == 1
                    ? $"New {GameData.ShipClass(s.ClassId).Name} {s.Name} launched: flying out to join {to.Name}"
                    : $"New {GameData.ShipClass(s.ClassId).Name} {s.Name} joins {f.Name}", onClick: GoToFleet(f.Id));

        foreach (var b in Economy.Claimable(w))
        {
            var had = _owners.TryGetValue(b.Id, out var o) ? o : b.Owner;
            if (!first && had != b.Owner)
            {
                var text = b.Owner == Side.Player ? $"You claimed {b.Name}" : had == Side.Player ? $"You lost {b.Name} to the rival" : $"The rival claimed {b.Name}";
                _main.Toast(text, onClick: GoToBody(b.Id));
            }
            _owners[b.Id] = b.Owner;
            if (b.ClaimSide == Side.Rival && b.Owner == Side.Player)
            {
                if (_claimsWarned.Add(b.Id)) { _main.Toast($"The rival is claiming {b.Name}: drive it out within {Economy.ClaimHoursFor(b) / 24:0} days", onClick: GoToBody(b.Id)); _paused = true; }
            }
            else _claimsWarned.Remove(b.Id);
        }

        if (Economy.ShipyardOf(w, Side.Player) is { LastBombardedHour: double hit } yard && (_yardAttackSeen == null || hit - _yardAttackSeen > 24))
        {
            if (!first) { _main.Toast($"Your shipyard at {yard.Name} is under attack!", onClick: GoToBody(yard.Id)); _paused = true; }
            _yardAttackSeen = hit;
        }

        if (w.Winner != null && !_endSeen)
        {
            _endSeen = true;
            ShowEnd = true;
            _paused = true;
        }
    }

    public void Queue(OrderKind kind, string key)
    {
        if (Economy.CanQueue(Sim.World, Side.Player, kind, key) is string problem) { _main.Toast(problem); return; }
        // New ships go to the fleet picked for the last one, while it still exists.
        var join = kind == OrderKind.Ship && BuildForFleetId is int j && Sim.World.FindFleet(j)?.Side == Side.Player ? BuildForFleetId : null;
        Sim.Issue(new QueueOrderCommand(Side.Player, kind, key, join));
        _main.RunGuarded("queueing an order", Sim.ApplyPending);
    }

    /// <summary>The fleet last picked for a ship being built (new orders start with it).</summary>
    public int? BuildForFleetId { get; private set; }

    /// <summary>Choose the fleet a ship being built will join (null: the fleet at the shipyard).</summary>
    public void SetOrderFleet(int orderId, int? fleetId)
    {
        BuildForFleetId = fleetId;
        Sim.Issue(new SetOrderFleetCommand(Side.Player, orderId, fleetId));
        _main.RunGuarded("choosing a fleet", Sim.ApplyPending);
    }

    public void CancelOrder(int orderId)
    {
        Sim.Issue(new CancelOrderCommand(Side.Player, orderId));
        _main.RunGuarded("cancelling an order", Sim.ApplyPending);
    }

    public void ShowLastReport() => Report = Sim.World.Battles.LastOrDefault();
    public void CloseReport() => Report = null;

    public void OrderStance(Stance stance, bool retreat)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetStanceCommand(f.Id, stance, retreat));
    }

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
        if (Group.Count >= 2) { OrderGather(); return; }
        if (MyFleet is Fleet f && MergeCandidate() is Fleet other) Sim.Issue(new MergeFleetsCommand(f.Id, other.Id));
    }

    /// <summary>Merge the picked fleets into the largest: close ones at once, the rest fly to it first.</summary>
    public void OrderGather()
    {
        var group = Group;
        if (group.Count < 2) return;
        var into = group[0];
        Sim.Issue(new GatherFleetsCommand(into.Id, group.Skip(1).Select(f => f.Id).ToList(), Settings.Speed));
        _main.Toast($"Gathering {group.Count} fleets into {into.Name}");
        Select(into.Id);
    }

    /// <summary>Does this fleet have ships faster than its slowest (so some could go ahead)?</summary>
    public bool HasFastShips(Fleet f)
    {
        var speeds = Sim.World.ShipsOf(f).Select(Fleets.ShipSpeed).ToList();
        return speeds.Count > 1 && speeds.Max() > speeds.Min() * 1.0001;
    }

    // ---- Route planner ----------------------------------------------------------------------

    public void StartPlanning(int? bodyId, Vec2 point, int? enemyFleetId = null)
    {
        if (MyFleet is not Fleet f) return;
        var name = bodyId is int b && Sim.World.FindBody(b) is Body body ? body.Name : "this point";
        if (enemyFleetId is int e && Sim.World.FindFleet(e) is Fleet enemy) { name = enemy.Name; bodyId = null; }
        var same = Planner?.FleetId == f.Id ? Planner : null; // a new target keeps the waypoints
        Planner = new PlannerState(f.Id, bodyId, point, name) { TargetFleetId = enemyFleetId, Editing = same?.Editing ?? false };
        if (same != null) Planner.Waypoints.AddRange(same.Waypoints);
        Replan();
    }

    public void CancelPlanning() => Planner = null;

    /// <summary>Can the selected fleet's current route be opened in the planner and changed?</summary>
    public bool CanEditRoute => MyFleet is { Flight: not null, Retreating: false, Maneuvering: false };

    /// <summary>
    /// Open the selected fleet's current route in the planner: same target, settings and the waypoints it
    /// has not reached yet. Optionally start dragging one of those waypoints.
    /// </summary>
    public void EditRoute(int? grab = null)
    {
        if (!CanEditRoute || MyFleet is not Fleet f || f.Flight is not Flight fl) return;
        Settings.Speed = fl.SpeedFraction;
        if (fl.Chase != ChaseMode.None) Settings.Chase = fl.Chase;
        else Settings.Arrival = fl.Arrival;
        if (fl.TargetBodyId != null) Settings.Hold = fl.HoldAfter;
        Settings.Route = fl.Started ? RouteKind.Direct : fl.Route;
        Planner = null; // don't carry over waypoints from an earlier plan
        if (fl.TargetFleetId is int enemy && Sim.World.FindFleet(enemy) != null) StartPlanning(null, fl.Target, enemy);
        else StartPlanning(fl.TargetBodyId, fl.Target);
        if (Planner == null) return;
        foreach (var wp in fl.WaypointsAhead(Sim.World.Hour))
            Planner.Waypoints.Add(new WaypointDraft { Point = wp.Point, Stop = wp.Stop, Speed = wp.SpeedFraction, WaitHours = wp.WaitHours });
        Planner.Editing = true;
        Replan();
        if (grab is int g && g < Planner.Waypoints.Count) _draggingWaypoint = g;
    }

    /// <summary>Work the plan out again (after a setting changes, and a few times a second as things move).</summary>
    public void Replan()
    {
        _planAge = 0;
        if (Planner is not PlannerState p || Sim.World.FindFleet(p.FleetId) is not Fleet f) { Planner = null; return; }
        _main.RunGuarded("planning a route", () =>
        {
            p.Plan = FlightPlanner.PlanRoute(Sim.World, f, Request(p));
            p.ChaseProblems.Clear();
            if (p.TargetFleetId != null)
                foreach (var mode in ChaseModes)
                    p.ChaseProblems[mode] = mode == Settings.Chase ? p.Plan?.Problem
                        : FlightPlanner.Plan(Sim.World, f, Request(p) with { Chase = mode }).Problem;
        });
    }

    public static readonly ChaseMode[] ChaseModes =
        { ChaseMode.MatchSpeed, ChaseMode.GunRange, ChaseMode.MissileRange, ChaseMode.ShadowGuns, ChaseMode.ShadowMissiles, ChaseMode.Overtake };

    private FlightRequest Request(PlannerState p) => p.TargetFleetId != null
        ? new(null, p.Point, Settings.Speed, ArrivalStyle.Brake, HoldStyle.Drift, Sim.World.Tick, RouteKind.Direct, p.TargetFleetId,
            Chase: Settings.Chase)
        : new(p.TargetBodyId, p.Point, Settings.Speed, Settings.Arrival, Settings.Hold, Sim.World.Tick, Settings.Route, p.TargetFleetId,
            p.Waypoints.Count > 0 ? p.Waypoints.Select(d => new Waypoint(d.Point, d.Stop, d.Speed, d.Stop ? d.WaitHours : 0)).ToList() : null);

    /// <summary>Add a waypoint on the way, in the stretch of the route it is closest to.</summary>
    public void AddWaypoint(Vec2 at)
    {
        if (Planner is not PlannerState p || Sim.World.FindFleet(p.FleetId) is not Fleet f) return;
        var corners = new List<Vec2> { f.Position };
        corners.AddRange(p.Waypoints.Select(w => w.Point));
        corners.Add(p.Plan?.Flight?.Target ?? p.Point);
        var best = corners.Count - 2;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < corners.Count - 1; i++)
        {
            var d = corners[i + 1] - corners[i];
            var t = Math.Clamp(Vec2.Dot(at - corners[i], d) / Math.Max(Vec2.Dot(d, d), 1e-12), 0, 1);
            var dist = Vec2.Distance(at, corners[i] + d * t);
            if (dist < bestDistance) { bestDistance = dist; best = i; }
        }
        p.Waypoints.Insert(best, new WaypointDraft { Point = at });
        Replan();
    }

    /// <summary>
    /// Ctrl+right-click: a new destination (the old one becomes a waypoint), or a waypoint on the way, as the
    /// planner is set. With no planner open it opens the selected fleet's route first (or starts a new one).
    /// </summary>
    public void AddRoutePoint(Vector2 screen)
    {
        if (Planner == null)
        {
            if (!CanEditRoute) { OrderMove(screen, false); return; }
            EditRoute();
            if (Planner == null) return;
        }
        var at = _camera.ToWorld(screen);
        if (!Settings.AddAtEnd || Planner.TargetFleetId != null) { AddWaypoint(at); return; }
        Planner.Waypoints.Add(new WaypointDraft { Point = Planner.Plan?.Flight?.Target ?? Planner.Point });
        var bodyId = _map.HitTestBody(screen);
        StartPlanning(bodyId, bodyId is int b ? Sim.World.FindBody(b)!.Position : at);
    }

    public void RemoveWaypoint(int index)
    {
        if (Planner == null || index < 0 || index >= Planner.Waypoints.Count) return;
        Planner.Waypoints.RemoveAt(index);
        Replan();
    }

    public void ConfirmPlan()
    {
        if (Planner is not PlannerState p || p.Plan is not { Ok: true, Flight: Flight flight }) return;
        var depart = Settings.Route == RouteKind.Wait && p.TargetFleetId == null ? Math.Max(Sim.World.Tick, (long)Math.Round(flight.DepartHour)) : Sim.World.Tick;
        var request = Request(p) with { DepartTick = depart };
        if (p.TargetFleetId == null && Settings.FastAhead && Settings.Arrival == ArrivalStyle.Brake && Sim.World.FindFleet(p.FleetId) is Fleet f && HasFastShips(f))
            Sim.Issue(new SendAheadCommand(p.FleetId, request));
        else
            Sim.Issue(new PlanFlightCommand(p.FleetId, request));
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
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right, CtrlPressed: true } addWaypoint when MyFleet != null:
                AddRoutePoint(addWaypoint.Position);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, CtrlPressed: true } when MyFleet != null:
                break; // the release of a Ctrl+right click
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
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } endDrop when _draggingEnd:
                _draggingEnd = false;
                // Dropped on a planet or moon: go there; otherwise to the point in space.
                if (Planner != null && _map.HitTestBody(endDrop.Position) is int dropBody) StartPlanning(dropBody, Sim.World.FindBody(dropBody)!.Position);
                break;
            case InputEventMouseMotion endMotion when _draggingEnd && Planner != null:
                StartPlanning(null, _camera.ToWorld(endMotion.Position));
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } when _draggingWaypoint != null:
                _draggingWaypoint = null;
                Replan();
                break;
            case InputEventMouseMotion waypointMotion when _draggingWaypoint is int wi && Planner != null && wi < Planner.Waypoints.Count:
                Planner.Waypoints[wi].Point = _camera.ToWorld(waypointMotion.Position);
                Replan();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed && Planner != null && _map.HitTestWaypoint(left.Position) is int grabbed)
                {
                    _draggingWaypoint = grabbed;
                    break;
                }
                if (left.Pressed && MyFleet != null && _map.HitTestRouteEnd(left.Position)
                    && (Planner != null ? Planner.TargetFleetId == null : CanEditRoute && MyFleet.Flight?.TargetFleetId == null))
                {
                    if (Planner == null) EditRoute(); // grabbing the end of a route already flying reopens it
                    _draggingEnd = Planner != null;
                    if (_draggingEnd) break;
                }
                if (left.Pressed && Planner == null && CanEditRoute && _map.HitTestFlightWaypoint(left.Position) is int ahead)
                {
                    EditRoute(ahead); // grabbing a waypoint of a route already flying reopens it in the planner
                    break;
                }
                if (left.Pressed)
                {
                    var hit = _map.HitTest(left.Position);
                    if (left.DoubleClick && hit is int id) { Follow(id, zoom: true); _leftDown = null; break; }
                    _leftDown = left.Position;
                    _boxing = false;
                }
                else if (_leftDown is Vector2 down)
                {
                    _leftDown = null;
                    _map.SelectionBox = null;
                    if (_boxing) { SelectGroup(FleetsInBox(BoxFrom(down, left.Position))); break; }
                    var hit = _map.HitTest(down);
                    if (left.ShiftPressed && hit is int fid && Sim.World.FindFleet(fid) != null) ToggleInGroup(fid);
                    else Select(hit);
                }
                break;
            case InputEventMouseMotion boxMotion when _leftDown is Vector2 start:
                if (_boxing || boxMotion.Position.DistanceTo(start) > 6)
                {
                    _boxing = true;
                    _map.SelectionBox = BoxFrom(start, boxMotion.Position);
                }
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
            case Key.G: OrderHold(HoldStyle.Drift); break;
            case Key.X: OrderStop(); break;
            case Key.C when Planner == null: EditRoute(); break;
            case Key.M: OrderMerge(); break;
            case Key.V: ToggleEngagement(); break;
            case Key.Escape when Engagement != null: CloseEngagement(); break;
            case Key.Tab when Engagement != null: CycleEngagement(key.ShiftPressed ? -1 : 1); break;
            case Key.Tab: NextFleet(key.ShiftPressed ? -1 : 1); break;
            case Key.P: PauseOnContact = !PauseOnContact; _main.Toast(PauseOnContact ? "Will pause when enemies are spotted" : "Will not pause when enemies are spotted"); break;
            case Key.F7: ToggleRevealAll(); _main.Toast(RevealAll ? "Debug: showing every fleet (F7 to hide)" : "Showing only what your sensors see"); break;
            case Key.R: if (Report != null) CloseReport(); else ShowLastReport(); break;
            case Key.E when Sim.World.Loop: ShowEmpire = !ShowEmpire; break;
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
            case Key.Escape when Report != null:
                CloseReport();
                break;
            case Key.F6 when IsSandbox:
                _main.StartSandbox(_main.LastSandbox);
                break;
            case Key.Escape when IsSandbox:
                _main.ShowSandboxSetup();
                break;
            case Key.Escape:
                SaveSlots.WriteAutosave(_main.Paths, Sim);
                _main.ShowTitle();
                break;
        }
    }

    private static Rect2 BoxFrom(Vector2 a, Vector2 b) => new Rect2(a, Vector2.Zero).Expand(b);

    /// <summary>Your fleets with any ship inside the box (or the fleets themselves when zoomed out).</summary>
    private List<int> FleetsInBox(Rect2 box)
    {
        var found = new List<int>();
        foreach (var f in Sim.World.Fleets.Where(f => f.Side == Side.Player))
        {
            var points = Sim.World.ShipsOf(f).Select(s => _map.DrawnPosition(s.Id)).Append(_map.DrawnPosition(f.Id));
            if (points.Any(p => p is Vec2 v && v.IsFinite && box.HasPoint(_camera.ToScreen(v)))) found.Add(f.Id);
        }
        return found;
    }

    /// <summary>WASD or the arrow keys pan the map (not while typing in a text box).</summary>
    private void KeyboardPan(double delta)
    {
        if (GetViewport().GuiGetFocusOwner() is LineEdit or TextEdit) return;
        if (Input.IsKeyPressed(Key.Ctrl) || Input.IsKeyPressed(Key.Alt)) return;
        var dir = Vector2.Zero;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) dir.Y -= 1;
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) dir.Y += 1;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) dir.X -= 1;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) dir.X += 1;
        if (dir == Vector2.Zero) return;
        var speed = KeyPanSpeed * (Input.IsKeyPressed(Key.Shift) ? 2.5f : 1f);
        _follow = null;
        // Pan moves the map with the mouse; keys move the view, so the map goes the other way.
        _camera.Pan(-dir.Normalized() * speed * (float)delta);
    }

    /// <summary>Right click: plan a route to the body under the cursor, or to that point in space.</summary>
    private void OrderMove(Vector2 at, bool goNow)
    {
        if (MyFleet == null) return;
        // An enemy fleet under the cursor: attack it.
        if (_map.HitTest(at) is int hit && Sim.World.FindFleet(hit) is Fleet enemy && enemy.Side != Side.Player)
        {
            // Aim at where it is thought to be: in sight, or guessed from the last sighting.
            var guess = Sensors.Predict(Sim.World, Side.Player, enemy.Id, Sim.World.Hour) ?? enemy.Position;
            StartPlanning(null, guess, enemy.Id);
            if (goNow) ConfirmPlan();
            return;
        }
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
    public bool FastAhead;

    /// <summary>How to chase when the target is an enemy fleet.</summary>
    public ChaseMode Chase = ChaseMode.GunRange;

    /// <summary>Ctrl+right-click sets a new destination (true) or adds a waypoint on the way (false).</summary>
    public bool AddAtEnd = true;
}

/// <summary>A route being planned: which fleet, where to, and the latest answer from the planner.</summary>
public sealed class PlannerState(int fleetId, int? targetBodyId, Vec2 point, string targetName)
{
    public int FleetId { get; } = fleetId;
    public int? TargetBodyId { get; } = targetBodyId;
    public Vec2 Point { get; } = point;
    public string TargetName { get; } = targetName;
    public int? TargetFleetId { get; init; }
    public FlightPlan? Plan { get; set; }

    /// <summary>For a chase: why each chase mode would not work right now (null: it would).</summary>
    public Dictionary<ChaseMode, string?> ChaseProblems { get; } = new();

    /// <summary>Opened from a route the fleet is already flying (Go replaces it).</summary>
    public bool Editing { get; set; }
    public List<WaypointDraft> Waypoints { get; } = new();
}

/// <summary>A waypoint being set up in the planner (becomes a sim Waypoint when the route is sent).</summary>
public sealed class WaypointDraft
{
    public Vec2 Point;
    public bool Stop;
    public double? Speed;     // null: the route's speed
    public double WaitHours;
}
