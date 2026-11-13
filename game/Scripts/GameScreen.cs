using Godot;
using Lightlag.Sim;
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
///   Middle/right drag  pan (also WASD / arrows)       Double-click    open your planet, or follow it (F follows the selection)
///   Left drag          box: pick several fleets       Shift+click     add or remove a fleet from the pick
///   Tab                next fleet of yours            M               merge, or gather the picked fleets
///   Right click        plan a route there             Shift+right     go there now with the last settings
///   Enter              fly the planned route          Esc             cancel the plan (else back to title)
///   O / G / X          orbit / drift / stop           Home            show the whole system
///   P                  pause when enemies are spotted F7              debug: show every fleet
///   Ctrl+right click   add a waypoint to the route    Drag a waypoint move it (the route replans live)
///   Right click enemy  plan an attack on it           R               combat report
///   Space              pause / resume                 1 2 3 4, + -    speed 1x 2x 4x 8x
///   E                  empire: build, research, claims B               sight shadows / sensor coverage / off
///   F5 / F9            quick save / quick load        F8              report a problem
///   Z                  zoom right in on the selection (also the Zoom in button)
///   Ctrl+Shift+F12     test crash                     Ctrl+Shift+F11  break a rule on purpose
/// </summary>
public partial class GameScreen : Node2D
{
    /// <summary>Game hours per real second at 1x, 2x, 4x, 8x. At 1x a game day takes 4 seconds.</summary>
    public static readonly double[] Speeds = { 6, 12, 24, 48 };
    public static readonly string[] SpeedNames = { "1x", "2x", "4x", "8x" };
    private const double AutosaveEverySeconds = 300; // real time spent playing (not paused)
    private const double IronmanSaveEverySeconds = 60;

    private Main _main = null!;
    private readonly MapCamera _camera = new();
    private MapView _map = null!;
    private double _accum;
    private int _speed;
    private bool _paused;
    private int? _selected;
    /// <summary>What the camera follows. Setting it drops any other ships the view was locked on to as well.</summary>
    private int? _follow
    {
        get => _followId;
        set { _followId = value; _followAlso.Clear(); }
    }
    private int? _followId;

    /// <summary>More fleets kept in view with the followed one (both sides of a battle, a picked group).</summary>
    private readonly List<int> _followAlso = new();

    /// <summary>Lock the view on several fleets or bodies: centred between them, zooming out when they spread apart.</summary>
    public void FollowAll(IReadOnlyList<int> ids)
    {
        if (ids.Count == 0) return;
        _follow = ids[0];
        _followAlso.AddRange(ids.Skip(1).Distinct().Where(id => id != ids[0]));
    }

    /// <summary>Where the camera should be: the followed thing, or the middle of everything locked on (zooming out to keep it all on screen).</summary>
    private Vec2? FollowPoint()
    {
        if (_follow is not int id || _map.DrawnPosition(id) is not { IsFinite: true } first) return null;
        if (_followAlso.Count == 0) return first;
        var points = new List<Vec2> { first };
        foreach (var other in _followAlso)
            if (_map.DrawnPosition(other) is { IsFinite: true } p) points.Add(p);
        var min = new Vec2(points.Min(p => p.X), points.Min(p => p.Y));
        var max = new Vec2(points.Max(p => p.X), points.Max(p => p.Y));
        var mid = (min + max) * 0.5;
        var spread = Vec2.Distance(min, max) * 0.5;
        // Zoom out when they no longer fit; zooming back in is left to you.
        if (spread * _camera.TargetScale > Math.Min(_camera.ScreenSize.X, _camera.ScreenSize.Y) * 0.4) _camera.FitRadius(spread * 1.25);
        return mid;
    }
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

    /// <summary>Pause the game when an enemy fleet comes into sight (the Enemy spotted line of the pause settings: Pause or Pop-up).</summary>
    public bool PauseOnContact
    {
        get => PauseSettings.Rules[PauseEvent.EnemySpotted] == PauseResponse.Pause;
        set => PauseSettings.Set(PauseEvent.EnemySpotted, value ? PauseResponse.Pause : PauseResponse.Notice);
    }

    /// <summary>
    /// Something happened: pause, slow to 1x, pop up or only a News line, as the pause settings say for it
    /// (PauseSettings, Pause settings button). With <paramref name="inNews"/> the caller has already put it in News.
    /// </summary>
    private void Alert(PauseEvent e, string text, Action? onClick = null, bool inNews = false)
    {
        switch (PauseSettings.Rules[e])
        {
            case PauseResponse.Pause:
                _main.Toast(text + ". Paused.", onClick: onClick);
                _paused = true;
                break;
            case PauseResponse.Slow:
                _main.Toast(text + (_speed > 0 ? ". Slowed to 1x." : ""), onClick: onClick);
                _speed = 0;
                break;
            case PauseResponse.Notice:
                _main.Toast(text, onClick: onClick);
                break;
            default:
                if (!inNews) _main.AddNews(text, onClick);
                break;
        }
    }

    /// <summary>Skip those pauses when a role fleet is already dealing with the enemy within its rules (see HelpCalls).</summary>
    public bool QuietWhenHandled { get; set; } = true;

    /// <summary>Debug view (F7): every fleet is drawn, seen or not.</summary>
    public bool RevealAll { get; private set; }
    /// <summary>What the map shows of your sensors: shadows, full coverage or nothing (B cycles).</summary>
    public MapView.SightView Sight { get => _map.Sight; set => _map.Sight = value; }

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

    /// <summary>The fleet whose ships and fittings are shown (null: none).</summary>
    public int? FittingFleetId { get; set; }

    /// <summary>The research screen (T) covers the map while open.</summary>
    public bool ShowResearch { get; set; }

    /// <summary>The planets list (Y) covers the map while open.</summary>
    public bool ShowPlanets { get; set; }

    /// <summary>The fleets screen (K), and the fleet picked on it.</summary>
    public bool ShowFleets { get; set; }
    /// <summary>The ships screen (J): designs, models in service and the class tree.</summary>
    public bool ShowShips { get; set; }
    public int? FleetScreenId { get; set; }

    /// <summary>The world now (for screens that need to read it outside a refresh).</summary>
    public World World => Sim.World;

    /// <summary>The planet whose screen (board, priorities, storage, transport) is open, over the planets list.</summary>
    public int? PlanetScreenId { get; set; }

    /// <summary>The planet whose ground fight board is open (see GroundScreen).</summary>
    public int? GroundScreenId { get; set; }

    /// <summary>A tab the empire panel should switch to on its next refresh (from a toast, say).</summary>
    public EmpirePanel.Tab? EmpireTabWanted { get; set; }

    private readonly HashSet<string> _warCallsSeen = new();
    private readonly HashSet<int> _eventsSeen = new();

    /// <summary>The ship whose card is open (click ⓘ on a ship row; null: none).</summary>
    public int? ShipCardId { get; set; }

    /// <summary>The key help overlay (F1), the "How am I doing" chart (N) and the intelligence screen (I).</summary>
    public bool ShowHelp { get; set; }

    /// <summary>Battle view (U): clears the screen to per-ship status strips and shrinks the menus.</summary>
    public bool BattleView { get; set; }

    /// <summary>Is this fleet drawn for the player (seen, or theirs, or every fleet in debug)?</summary>
    public bool ShownFleet(Fleet f) => _map.Shown(f);
    public bool ShowProgress { get; set; }
    public bool ShowIntel { get; set; }

    /// <summary>The fleets and planets list down the left (L folds it away).</summary>
    public bool ShowSide { get; set; } = true;

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

    // ---- Game menu ------------------------------------------------------------------------------

    /// <summary>The Esc menu is open (the game is paused under it).</summary>
    public bool MenuOpen { get; private set; }
    private bool _pausedBeforeMenu;

    /// <summary>The menu page to open on next ("pause" for the pause settings), taken by the HUD.</summary>
    public string? MenuPage { get; set; }

    public void OpenMenu(string? page = null)
    {
        if (MenuOpen) return;
        MenuPage = page;
        MenuOpen = true;
        _pausedBeforeMenu = _paused;
        _paused = true;
    }

    public void CloseMenu()
    {
        if (!MenuOpen) return;
        MenuOpen = false;
        _paused = _pausedBeforeMenu;
    }

    public void SaveManual()
    {
        var path = SaveSlots.WriteManual(_main.Paths, Sim);
        _main.Log.Info("save", "Game saved", new { file = System.IO.Path.GetFileName(path) });
        _main.Toast(Sim.World.Rules.Ironman ? $"Saved (Ironman, the game's one save): {GameTime.Format(Sim.World.Tick)}" : $"Saved: {GameTime.Format(Sim.World.Tick)}");
        CloseMenu();
    }

    public void QuitToTitle()
    {
        SaveSlots.WriteAutosave(_main.Paths, Sim);
        _main.ShowTitle();
    }
    public int SpeedIndex => _speed;

    public override void _Ready()
    {
        _main = Main.Instance;
        _camera.ScreenSize = GetViewportRect().Size;

        AddChild(Backdrop.For(Sim.World.Seed, _camera));
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
        SelectSplitShip();
        if (!_paused)
        {
            // _accum counts simulation steps owed. A step is an hour normally, or 10 game seconds in
            // battle time, which runs 360 times slower: 1x is then one game minute per second.
            var cost = Sim.PeekStepHours();
            var rate = Speeds[_speed] * (cost < GameTime.HoursPerTick && SlowForBattle() ? BattleSlowdown : 1);
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
            WatchNotes();
            WatchChasers();
            WatchEconomy();
            if (Sim.World.Loop) RivalAdvisor.Tick(Sim, _main);
            WarTab.WatchCalls(Sim.World, _warCallsSeen, text => Alert(PauseEvent.CommandCall, text, () => { ShowEmpire = true; EmpireTabWanted = EmpirePanel.Tab.War; }));
            EmpireTabs.WatchEvents(Sim.World, _eventsSeen, (text, stop) => Alert(stop ? PauseEvent.EventCall : PauseEvent.EventAutoPicked, text,
                () => { ShowEmpire = true; EmpireTabWanted = EmpirePanel.Tab.Needs; }));
            // Pausing, or switching in or out of battle time: show where things really are now, so the
            // map neither stops part way back to the last step nor jumps when time runs again.
            if (switched || _paused) SnapToNow();
        }
        // Ironman keeps its one save fresh, so a crash or a closed window loses little.
        if (_sinceAutosave >= (Sim.World.Rules.Ironman ? IronmanSaveEverySeconds : AutosaveEverySeconds))
        {
            _sinceAutosave = 0;
            _main.RunGuarded("autosave", () => SaveSlots.WriteAutosave(_main.Paths, Sim));
        }

        KeyboardPan(delta);
        _map.TurnShips(delta);
        _map.DrawHour = Sim.World.Hour - (1 - _map.Blend) * _lastStepHours;
        _map.UpdatePositions();
        if (_follow is int f && _map.DrawnPosition(f) is not { IsFinite: true }) _follow = null;
        _camera.Update(delta, FollowPoint());
        _map.SyncSprites(); // after the camera moves, so planet pictures sit on their orbits
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
    public void SnapToNow()
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

    /// <summary>How hard the engagement screen's approach choices burn (share of top speed).</summary>
    public double EngagementBurn { get; private set; } = 1.0;

    public void SetEngagementBurn(double burn)
    {
        EngagementBurn = burn;
        _engagementAge = 1; // work the choices out again now
    }
    private double _engagementAge;
    private double _engagementLostFor;
    private const double EngagementGraceSeconds = 3;

    /// <summary>Open the engagement screen for one of your fleets (against the given enemy, or the one it is most concerned with).</summary>
    public void OpenEngagement(int mineId, int? enemyId = null)
    {
        var w = Sim.World;
        if (w.FindFleet(mineId) is not { Side: Side.Player } mine) return;
        var enemy = enemyId ?? Engagement_.EnemyFor(w, mine);
        if (enemy is not int e || Sensors.Find(w, Side.Player, e, seat: true) == null)
        {
            _main.Toast($"{mine.Name} has no enemy fleet in sight or on record");
            return;
        }
        Planner = null;
        ShowEmpire = false;
        SaveView();
        Engagement = (mineId, e);
        Select(mineId);
        _engagementAge = 1;
        UpdateEngagement(0);
        if (EngagementNow is EngagementInfo info)
        {
            // Fit both fleets and the closest pass, and at least the missile ranges.
            var spread = Math.Max(Vec2.Distance(mine.Position, info.EnemyAt), GameData.Combat.Missile.LaunchRangeMm);
            FollowAll(new[] { mineId, e }); // both fleets stay in view as they move
            _camera.FitRadius(spread * 1.4);
            _camera.Glide();
        }
    }

    /// <summary>The map view before an engagement opened, to glide back to when it closes.</summary>
    private (int? Follow, Vec2 Center, double Scale)? _viewBefore;

    private void SaveView() => _viewBefore ??= (_follow, _camera.Center, _camera.TargetScale);

    /// <summary>Ease back out to the view from before the engagement, so you don't lose your place.</summary>
    private void RestoreView()
    {
        if (_viewBefore is not { } view) return;
        _viewBefore = null;
        var (follow, center, scale) = view;
        _follow = follow is int id && Sim.World.FindFleet(id) != null || follow is int b && Sim.World.FindBody(b) != null ? follow : null;
        _camera.SetScale(scale);
        _camera.Glide(_follow == null ? center : null);
    }

    /// <summary>Every engagement you could look at now, nearest first (refreshed with the screen).</summary>
    public List<Engagement_.Pair> Engagements { get; private set; } = new();
    private double _contactsAge = 1;

    /// <summary>Enemy fleets you said not to pause for when contact with them starts.</summary>
    public HashSet<int> QuietEnemies { get; } = new();

    public void ToggleQuiet(int enemyId)
    {
        if (!QuietEnemies.Remove(enemyId)) QuietEnemies.Add(enemyId);
    }

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
        if (Engagement != null) RestoreView();
        Engagement = null;
        EngagementNow = null;
        _engagementLostFor = 0;
    }

    public void ToggleEngagement()
    {
        if (Engagement != null) { CloseEngagement(); return; }
        if (MyFleet is Fleet f) OpenEngagement(f.Id);
        else _main.Toast("Select one of your fleets first (V opens its engagement screen)");
    }

    private void UpdateEngagement(double delta)
    {
        // The contacts list (map side panel and engagement tabs) stays fresh even with no engagement open.
        _contactsAge += delta;
        if (_contactsAge >= 0.25)
        {
            _contactsAge = 0;
            _main.RunGuarded("contacts", () => Engagements = Engagement_.All(Sim.World, Side.Player));
        }
        if (Engagement is not (int mineId, int enemyId)) { _map.Engagement = null; return; }
        _engagementAge += delta;
        if (_engagementAge < 0.25 && EngagementNow != null) { _map.Engagement = EngagementNow; return; }
        _engagementAge = 0;
        var w = Sim.World;
        // A sighting can drop for a moment (a hard turn, a planet's shadow): keep the screen open for a
        // few seconds before closing, so it does not flick between the map and the engagement.
        var mine = w.FindFleet(mineId);
        if (mine == null || Sensors.Find(w, Side.Player, enemyId, seat: true) == null)
        {
            _engagementLostFor += 0.25;
            if (mine == null || _engagementLostFor >= EngagementGraceSeconds || EngagementNow == null)
            {
                CloseEngagement();
                _map.Engagement = null;
            }
            else _map.Engagement = EngagementNow;
            return;
        }
        _engagementLostFor = 0;
        _main.RunGuarded("engagement", () =>
        {
            EngagementNow = Engagement_.Of(w, mine, enemyId, EngagementBurn);
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
        var s = Sensors.Find(w, Side.Player, fleetId, seat: true);
        if (s == null || w.FindFleet(fleetId) is not Fleet f) return;
        Select(fleetId);
        if (s.Visible || RevealAll) _follow = f.Id;
        else { _follow = null; _camera.Center = s.Guess(w.Hour); }
        if (_camera.TargetScale < 0.002) _camera.FitRadius(20000);
    }

    /// <summary>The fleets that put the game in battle time: an active battle's, or a pair that could meet this hour.</summary>
    public List<Fleet> BattleFleets()
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
        SaveView();
        _camera.Glide();
        var center = fleets.Aggregate(Vec2.Zero, (a, f) => a + f.Position) * (1.0 / fleets.Count);
        var spread = fleets.Max(f => Vec2.Distance(f.Position, center));
        var mine = fleets.FirstOrDefault(f => f.Side == Side.Player);
        if (mine != null) { _follow = mine.Id; Select(mine.Id); }
        else { _follow = null; _viewBefore = null; _camera.Glide(center); }
        _camera.FitRadius(Math.Max(spread * 2.5, 300));
        var theirs = fleets.FirstOrDefault(f => f.Side != Side.Player && Sensors.Find(Sim.World, Side.Player, f.Id, seat: true) != null);
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

    /// <summary>
    /// Zoom right down on the selection and follow it: a planet or moon nearly fills the screen; a fleet is
    /// shown close enough to see each ship. Nothing selected: does nothing.
    /// </summary>
    public void ZoomToSelected()
    {
        if (_selected is not int id) { _main.Toast("Select a fleet or a planet first"); return; }
        _follow = id;
        if (Sim.World.FindBody(id) is Body body)
            _camera.SetScale(_camera.ScreenSize.Y * 0.35 / body.Radius);
        else if (Sim.World.FindFleet(id) != null)
            _camera.SetScale(30 / Fleets.FormationSpacing);
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

    /// <summary>
    /// Fleets and planets list (L): shows it, closing whatever screen covers it (empire, research, planets,
    /// fitting, the engagement view); hides it when it is already showing.
    /// </summary>
    public void ToggleSide()
    {
        if (_hud.SideVisible) { ShowSide = false; return; }
        ShowSide = true;
        ShowEmpire = false;
        ShowResearch = false;
        ShowPlanets = false;
        PlanetScreenId = null;
        GroundScreenId = null;
        FittingFleetId = null;
        if (Engagement != null) CloseEngagement();
    }

    /// <summary>The fleets screen (K): opens it over anything else, picking the selected fleet; closes it when open.</summary>
    public void ToggleFleets()
    {
        if (ShowFleets) { ShowFleets = false; return; }
        ShowResearch = false;
        ShowPlanets = false;
        PlanetScreenId = null;
        GroundScreenId = null;
        ShowEmpire = false;
        FittingFleetId = null;
        if (SelectedFleet is Fleet f && f.Side == Side.Player) FleetScreenId = f.Id;
        ShowFleets = true;
    }

    /// <summary>The ships screen (J): opens it over anything else; closes it when open.</summary>
    public void ToggleShips()
    {
        if (ShowShips) { ShowShips = false; return; }
        ShowResearch = false;
        ShowPlanets = false;
        PlanetScreenId = null;
        GroundScreenId = null;
        ShowEmpire = false;
        ShowFleets = false;
        FittingFleetId = null;
        ShowShips = true;
    }

    /// <summary>Open one of your planets' screens (as a double-click on the map does).</summary>
    public void OpenPlanet(int bodyId)
    {
        ShowResearch = false;
        GroundScreenId = null;
        ShowFleets = false;
        ShowShips = false;
        PlanetScreenId = bodyId;
    }

    /// <summary>Open the fleets screen (K) on one of your fleets.</summary>
    public void OpenFleet(int fleetId)
    {
        if (!ShowFleets) ToggleFleets();
        ShowShips = false;
        FleetScreenId = fleetId;
    }

    /// <summary>The planets screen (Y): opens it over anything else (research hid it before); closes it when open.</summary>
    public void TogglePlanets()
    {
        if (PlanetScreenId != null || ShowPlanets) { PlanetScreenId = null; ShowPlanets = false; return; }
        ShowResearch = false;
        GroundScreenId = null;
        ShowPlanets = true;
    }

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
    public int? SelectedId => _selected;

    public Fleet? SelectedFleet => _selected is int id ? Sim.World.FindFleet(id) : null;
    private Fleet? MyFleet => SelectedFleet is { Side: Side.Player } f ? f : null;

    // ---- Battles ------------------------------------------------------------------------------

    /// <summary>Has news of something that happened at this hour and place reached your flagship yet?</summary>
    private bool Heard(double hour, Vec2 where) => Sim.World.Hour - hour >= Light.DelayHours(Sim.World, Side.Player, where) - 1e-9;

    /// <summary>
    /// Should battle time slow the clock? Without light lag, whenever the game runs in battle time. With
    /// it, only when you could know: a battle whose news has reached you, or enemies close to a fleet
    /// within your command range. A far battle you have not heard of yet runs at the normal speed.
    /// </summary>
    private bool SlowForBattle()
    {
        var w = Sim.World;
        if (!w.Light.On) return true;
        if (w.ActiveBattle is Battle b && Heard(b.StartHour, b.Where)) return true;
        var near = GameData.Combat.ContactRangeMm * 2;
        foreach (var mine in w.Fleets.Where(f => f.Side == Side.Player && Light.DelayHours(w, Side.Player, f.Position) <= 0))
            if (w.Fleets.Any(e => e.Side != Side.Player && Vec2.Distance(e.Position, mine.Position) <= near)
                || w.Missiles.Any(m => Vec2.Distance(m.Position, mine.Position) <= near))
                return true;
        return false;
    }

    /// <summary>Pause when a battle starts (so orders can be given), and show the report when it ends.</summary>
    private void WatchBattles()
    {
        var w = Sim.World;
        if (w.ActiveBattle is Battle b && b.Id != _lastBattleSeen && Heard(b.StartHour, b.Where))
        {
            _lastBattleSeen = b.Id;
            // The same fleets meeting again soon after a contact ended (drifting in and out of range): no new pause.
            var again = w.Battles.Any(o => o.Id != b.Id && o.EndHour is double end && b.StartHour - end < RecontactQuietHours
                                           && o.For(Side.Player).Fleets.Intersect(b.For(Side.Player).Fleets).Any()
                                           && o.For(Side.Rival).Fleets.Intersect(b.For(Side.Rival).Fleets).Any());
            // Every enemy fleet in it is one you said not to pause for: a news line instead.
            var rivals = w.Fleets.Where(f => f.Side == Side.Rival && b.For(Side.Rival).Fleets.Contains(f.Name)).Select(f => f.Id).ToList();
            var quiet = rivals.Count > 0 && rivals.All(QuietEnemies.Contains);
            // Our role fleets are handling every enemy in it within their rules: no pause either.
            var handled = !quiet && QuietWhenHandled && HelpCalls.BattleInHand(w, b, Side.Player);
            quiet |= handled;
            var far = w.Light.On && b.For(Side.Player).LagHours > 5.0 / 60;
            if (far)
            {
                // A far battle: its light is only now arriving. Watch it as the light comes in.
                var battle = b;
                _main.AddNews($"Battle far away ({Format.Hours(b.For(Side.Player).LagHours)} of light): {string.Join(", ", b.For(Side.Player).Fleets)} and "
                              + $"{string.Join(", ", b.For(Side.Rival).Fleets)}. Out of command range: your fleets fight on their standing orders ({FarOrders(battle)}), "
                + "and anything they decide for themselves shows as ON ITS OWN. Click to watch as the light arrives", () => OpenReplay(battle), keep: true);
                if (!(again || quiet))
                    Alert(PauseEvent.FarBattle, $"Light from a far battle: {string.Join(", ", b.For(Side.Player).Fleets)} met {string.Join(", ", b.For(Side.Rival).Fleets)} "
                                + $"{Format.Hours(Sim.World.Hour - b.StartHour)} ago; click to watch", () => OpenReplay(battle), inNews: true);
            }
            else if (again || quiet)
            {
                _main.AddNews($"{(again ? "Contact again" : handled ? "Contact, role fleets handling it (no pause)" : "Contact (no pause)")}: {string.Join(", ", b.For(Side.Player).Fleets)} and {string.Join(", ", b.For(Side.Rival).Fleets)}", FocusBattle);
            }
            else
            {
                Alert(PauseEvent.BattleStarts, $"Contact: {string.Join(", ", b.For(Side.Player).Fleets)} and {string.Join(", ", b.For(Side.Rival).Fleets)}. Battle time",
                    FocusBattle);
            }
        }
        var last = w.Battles.LastOrDefault(x => x.EndHour is double end && Heard(end, x.Where));
        if (last != null && last.Id > _lastReportShown)
        {
            _lastReportShown = last.Id;
            if (!last.Fought)
            {
                // Came within range and parted with no shots: a News line, not a battle report.
                _main.AddNews($"Contact ended, no shots fired: {string.Join(", ", last.For(Side.Player).Fleets)} and {string.Join(", ", last.For(Side.Rival).Fleets)}");
                return;
            }
            if (Replay?.Id != last.Id) Report = last;
            // Keep the result in the News menu: who fought, losses on each side; click to reopen the report.
            var us = last.For(Side.Player);
            var them = last.For(Side.Rival);
            var battle = last;
            var lost = us.ShipsLost.Count == 0 ? "no ships lost" : $"lost {us.ShipsLost.Count} ({string.Join(", ", us.ShipsLost)})";
            var sunk = them.ShipsLost.Count == 0 ? "none of theirs destroyed" : $"destroyed {them.ShipsLost.Count} of theirs";
            _main.AddNews($"Battle over: {string.Join(", ", us.Fleets)} and {string.Join(", ", them.Fleets)}: {lost}, {sunk}. "
                          + $"Railgun hits {us.RailHits}/{us.RailShots}, missile hits {us.MissileHits}/{us.MissilesFired}. Click to watch it again",
                () => OpenReplay(battle), keep: true);
        }
    }

    /// <summary>The standing orders and autonomy of our fleets in a battle, for news of a far fight.</summary>
    private string FarOrders(Battle b) => string.Join("; ", Sim.World.Fleets
        .Where(f => f.Side == Side.Player && b.For(Side.Player).Fleets.Contains(f.Name))
        .Select(f => $"{f.Name}: {f.Stance}, autonomy {f.Autonomy}"));

    private int _lastNoteId;

    /// <summary>Decisions our fleets made for themselves: a News line once the news reaches the flagship.</summary>
    private void WatchNotes()
    {
        var w = Sim.World;
        foreach (var n in w.Light.Notes)
        {
            if (n.Id <= _lastNoteId || n.Side != Side.Player || n.ArriveHour > w.Hour + 1e-9) continue;
            _lastNoteId = Math.Max(_lastNoteId, n.Id);
            if (n.ArriveHour < w.Hour - 1) continue; // already heard before the game was loaded
            var ago = w.Hour - n.Hour > 0.02 ? $" ({Format.Hours(w.Hour - n.Hour)} ago)" : "";
            var fleet = w.FindFleet(n.FleetId);
            // Say plainly who decided: the fleet on its own, a standing order you gave, or its supplies.
            var who = n.Kind switch
            {
                NoteKind.OwnDecision => $"ON ITS OWN (autonomy {fleet?.Autonomy.ToString() ?? "?"}, no order from you): ",
                NoteKind.StandingOrder => "STANDING ORDER (out of command range): ",
                _ => "SUPPLIES: ",
            };
            _main.AddNews(who + n.Text + ago, GoToFleet(n.FleetId));
            if (n.Kind == NoteKind.Supply) Alert(PauseEvent.SupplyNote, n.Text + ago, GoToFleet(n.FleetId), inNews: true);
        }
        WatchOrbits();
    }

    private readonly Dictionary<int, int?> _leftOrbit = new();

    /// <summary>A fleet of ours broke orbit to fight: say so, since claims and bombardment stop until it orbits again.</summary>
    private void WatchOrbits()
    {
        var w = Sim.World;
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player))
        {
            _leftOrbit.TryGetValue(f.Id, out var before);
            if (f.LeftOrbitBodyId == before) continue;
            _leftOrbit[f.Id] = f.LeftOrbitBodyId;
            if (f.LeftOrbitBodyId is int id && w.FindBody(id) is Body b && Light.DelayHours(w, Side.Player, f.Position) <= 0)
                _main.AddNews($"{f.Name} leaves orbit at {b.Name} to fight. Claiming and bombarding there stop until it orbits again (select it, O)", GoToFleet(f.Id), keep: true);
        }
    }

    /// <summary>A new enemy fleet in sight: say so, and pause if asked to.</summary>
    private void WatchSightings()
    {
        var w = Sim.World;
        foreach (var s in Sensors.Known(w, seat: true).Where(s => !s.Own))
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
            if (QuietWhenHandled && HelpCalls.HandledBy(w, Side.Player, s.FleetId) is Fleet handler)
            {
                // A role fleet has it in hand within its rules: a news line, no pop-up and no pause.
                var seen = s.FleetId;
                _main.AddNews($"Spotted {s.Name} ({s.Ships} ships). {handler.Name} ({HelpCalls.RoleName(handler.Role)}) is dealing with it", () => GoToSighting(seen));
                continue;
            }
            var nearest = w.Fleets.Where(f => f.Side == Side.Player).OrderBy(f => Vec2.Distance(f.Position, s.Position)).FirstOrDefault();
            var where = nearest != null ? $", {Vec2.Distance(nearest.Position, s.Position) * 1000:#,0} km from {nearest.Name}" : "";
            var fleetId = s.FleetId;
            Alert(PauseEvent.EnemySpotted, $"Spotted {s.Name} ({s.Ships} ships, {FleetPanel.StateText(s.State)}){where}", () => GoToSighting(fleetId));
        }
    }

    /// <summary>Once an hour: warn when a seen enemy fleet's track is closing on one of your fleets.</summary>
    private void WatchChasers()
    {
        var w = Sim.World;
        if (w.Tick == _chaseCheckedTick) return;
        _chaseCheckedTick = w.Tick;
        foreach (var s in Sensors.Known(w, seat: true).Where(s => !s.Own && s.SeenBy == Side.Player && s.Visible))
            foreach (var mine in w.Fleets.Where(f => f.Side == Side.Player))
            {
                if (Sensors.HeadingFor(s, mine, w.Hour) is not double eta) continue;
                var key = (s.FleetId, mine.Id);
                if (_chaseWarned.TryGetValue(key, out var when) && w.Hour - when < 24) { _chaseWarned[key] = w.Hour; continue; }
                _chaseWarned[key] = w.Hour;
                var enemyId = s.FleetId;
                var mineId = mine.Id;
                Alert(PauseEvent.EnemyClosing, $"{s.Name} ({s.Ships} ships) is heading for {mine.Name}: about {Format.Hours(eta)} away at {Format.Speed((s.Velocity - mine.Velocity).Length)} closing",
                    () => OpenEngagement(mineId, enemyId));
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
            Alert(PauseEvent.ResearchDone, $"Research done: {GameData.Research(me.Researched[^1]).Name}", () => ShowResearch = true);
        _researchSeen = me.Researched.Count;

        foreach (var s in w.Ships.Where(s => s.Side == Side.Player))
            if (_knownShips.Add(s.Id) && !first && w.FleetOf(s) is Fleet f)
                Alert(PauseEvent.ShipLaunched, f.JoinFleetId is int j && w.FindFleet(j) is Fleet to && f.ShipIds.Count == 1
                    ? $"New {Fitting.Spec(s).Name} {s.Name} launched: flying out to join {to.Name}"
                    : $"New {Fitting.Spec(s).Name} {s.Name} joins {f.Name}", GoToFleet(f.Id));

        foreach (var b in Economy.Claimable(w))
        {
            var had = _owners.TryGetValue(b.Id, out var o) ? o : b.Owner;
            if (!first && had != b.Owner)
            {
                var text = b.Owner == Side.Player ? $"You claimed {b.Name}" : had == Side.Player ? $"You lost {b.Name} to the rival" : $"The rival claimed {b.Name}";
                Alert(PauseEvent.PlanetChangedHands, text, GoToBody(b.Id));
            }
            _owners[b.Id] = b.Owner;
            if (b.ClaimSide == Side.Rival && b.Owner == Side.Player)
            {
                if (_claimsWarned.Add(b.Id)) Alert(PauseEvent.PlanetClaimed, $"The rival is claiming {b.Name}: drive it out within {Economy.ClaimHoursFor(b) / 24:0} days", GoToBody(b.Id));
            }
            else _claimsWarned.Remove(b.Id);
        }

        if (Economy.ShipyardOf(w, Side.Player) is { LastBombardedHour: double hit } yard && (_yardAttackSeen == null || hit - _yardAttackSeen > 24))
        {
            if (!first) Alert(PauseEvent.ShipyardAttacked, $"Your shipyard at {yard.Name} is under attack", GoToBody(yard.Id));
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

    /// <summary>Issue a command from a panel and apply it at once (it still travels at light speed if lag is on).</summary>
    public void IssueNow(GameCommand command, string what)
    {
        Sim.Issue(command);
        _main.RunGuarded(what, Sim.ApplyPending);
    }

    public void CancelOrder(int orderId)
    {
        Sim.Issue(new CancelOrderCommand(Side.Player, orderId));
        _main.RunGuarded("cancelling an order", Sim.ApplyPending);
    }

    public void ShowLastReport() => Report = Sim.World.Battles.LastOrDefault();
    public void CloseReport() => Report = null;
    public void ShowReportOf(Battle? b) { Replay = null; Report = b; }

    /// <summary>The battle being watched again, if any (see ReplayPanel).</summary>
    public Battle? Replay { get; private set; }
    public void OpenReplay(Battle b, double? at = null) { Report = null; Replay = b; ReplaySeek = at; }

    /// <summary>Where a replay just opened should start (null: at the beginning). The replay panel takes it once.</summary>
    public double? ReplaySeek { get; set; }
    public void CloseReplay() => Replay = null;

    public void OrderStance(Stance stance, bool retreat)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetStanceCommand(f.Id, stance, retreat));
    }

    // ---- Fleet orders -----------------------------------------------------------------------

    public void BuildStation(int bodyId, string kind)
    {
        if (Economy.CanBuildStation(Sim.World, Side.Player, bodyId, kind) is string problem) { _main.Toast(problem); return; }
        Sim.Issue(new BuildStationCommand(Side.Player, bodyId, kind));
    }

    public void OrderMission(Mission mission, int? bodyId, int? escortFleetId)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetMissionCommand(f.Id, mission, bodyId, escortFleetId));
    }

    public void OrderCover(CoverStyle cover, FireAt fireAt)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetCoverCommand(f.Id, cover, fireAt));
    }

    public void OrderProtect(int shipId, bool on)
    {
        if (Sim.World.FindShip(shipId) is Ship s && s.Side == Side.Player) Sim.Issue(new SetProtectCommand(s.FleetId, s.Id, on));
    }

    public void OrderHeat(RadiatorMode radiators, bool autoSinks)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetHeatCommand(f.Id, radiators, autoSinks));
    }

    public void EjectSinks()
    {
        if (MyFleet is Fleet f) Sim.Issue(new EjectHeatSinksCommand(f.Id));
    }

    public void OrderFireControl(bool longShots, MissileLaunch launch)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetFireControlCommand(f.Id, longShots, launch));
    }

    public void DropMissiles(double waitHours)
    {
        if (MyFleet is not Fleet f) return;
        Sim.Issue(new DropMissilesCommand(f.Id, waitHours));
        _main.Toast(waitHours <= 0 ? $"{f.Name} drops missiles: they go for the first enemy that passes close" : $"{f.Name} drops missiles: they wake in {Format.Hours(waitHours)}");
    }

    public void SendDrone()
    {
        if (MyFleet is not Fleet f) return;
        if (Drones.CanSend(Sim.World, Side.Player, f.Id) is string problem) { _main.Toast(problem); return; }
        Sim.Issue(new SendDroneCommand(Side.Player, f.Id));
        _main.Toast($"Drone away to {f.Name}");
    }

    public void ToggleRadioSilence() =>
        Sim.Issue(new SetRadioSilenceCommand(Side.Player, !Economy.Of(Sim.World, Side.Player).RadioSilence));

    public void OrderAutonomy(AutonomyLevel level)
    {
        if (MyFleet is Fleet f) Sim.Issue(new SetAutonomyCommand(f.Id, level));
    }

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
        if (MyFleet is Fleet f && shipIds.Count > 0) IssueNow(new SplitFleetCommand(f.Id, shipIds.ToList()), "splitting a fleet");
    }

    /// <summary>Split one ship off into a fleet of its own and select it once the order lands, ready for its own orders.</summary>
    public void SplitOne(int shipId)
    {
        if (MyFleet is not Fleet f || f.ShipIds.Count < 2 || !f.ShipIds.Contains(shipId)) return;
        IssueNow(new SplitFleetCommand(f.Id, new List<int> { shipId }), "splitting a ship off");
        _selectAfterSplit = (shipId, f.Id);
    }

    private (int Ship, int From)? _selectAfterSplit;

    /// <summary>Selects the ship split off by SplitOne once it has left its old fleet (light lag may delay it).</summary>
    private void SelectSplitShip()
    {
        if (_selectAfterSplit is not var (shipId, from)) return;
        var ship = Sim.World.FindShip(shipId);
        if (ship == null) { _selectAfterSplit = null; return; }
        if (ship.FleetId != from) { Select(ship.FleetId); _selectAfterSplit = null; }
    }

    /// <summary>The nearest fleet of ours that could merge into the selected one.</summary>
    public Fleet? MergeCandidate() => MyFleet is Fleet f
        ? Sim.World.Fleets.Where(o => Fleets.CanMerge(f, o) == null).OrderBy(o => Vec2.Distance(o.Position, f.Position)).FirstOrDefault()
        : null;

    public void OrderMerge()
    {
        if (Group.Count >= 2) { OrderGather(); return; }
        if (MyFleet is Fleet f && MergeCandidate() is Fleet other) IssueNow(new MergeFleetsCommand(f.Id, other.Id), "merging fleets");
    }

    /// <summary>Send the selected fleet to join another of ours (merging at once if they are together).</summary>
    public void OrderJoin(int intoId)
    {
        if (MyFleet is not Fleet f || Sim.World.FindFleet(intoId) is not Fleet into) return;
        if (Fleets.JoinProblem(Sim.World, f, into, Settings.Speed) is string problem)
        {
            _main.Toast($"{f.Name} cannot join {into.Name}: {problem}", onClick: GoToFleet(f.Id));
            return;
        }
        Sim.Issue(new GatherFleetsCommand(into.Id, new List<int> { f.Id }, Settings.Speed));
        _main.Toast(Fleets.CanJoin(f, into) ? $"{f.Name} joins {into.Name}" : $"{f.Name} flies to join {into.Name}");
    }

    /// <summary>Merge the picked fleets into the largest: close ones at once, the rest fly to it first.</summary>
    public void OrderGather()
    {
        var group = Group;
        if (group.Count < 2) return;
        var into = group[0];
        var stuck = group.Skip(1).Select(f => (f, Problem: Fleets.JoinProblem(Sim.World, f, into, Settings.Speed)))
            .Where(x => x.Problem != null).ToList();
        if (stuck.Count == group.Count - 1)
        {
            _main.Toast($"Cannot gather into {into.Name}: {stuck[0].f.Name}: {stuck[0].Problem}");
            return;
        }
        Sim.Issue(new GatherFleetsCommand(into.Id, group.Skip(1).Where(f => stuck.All(x => x.f != f)).Select(f => f.Id).ToList(), Settings.Speed));
        _main.Toast($"Gathering {group.Count - stuck.Count} fleets into {into.Name}"
                    + string.Concat(stuck.Select(x => $". {x.f.Name} cannot join: {x.Problem}")));
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
        foreach (var wp in fl.ChosenWaypointsAhead(Sim.World.Hour))
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
        : new(p.TargetBodyId, p.Point, Settings.Speed, Settings.Arrival, Settings.Hold, Sim.World.Tick, Settings.Route == RouteKind.Slingshot && p.Waypoints.Count > 0 ? RouteKind.Direct : Settings.Route, p.TargetFleetId,
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
        var request = Request(p) with { DepartTick = depart, FlybyBodyId = flight.FlybyBodyId };
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
        if (_planAge > (Settings.Route == RouteKind.Slingshot ? 2.0 : 0.3)) Replan(); // a slingshot search takes longer
    }
    public bool IsFollowing(int id) => _follow == id || _followAlso.Contains(id);

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
                    if (left.DoubleClick && hit is int id)
                    {
                        // A planet of yours opens its planet screen; anything else is followed close up.
                        if (Sim.World.FindBody(id) is { Owner: Side.Player, Board: not null } && Sim.World.Loop) PlanetScreenId = id;
                        else Follow(id, zoom: true);
                        _leftDown = null;
                        break;
                    }
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
                    else
                    {
                        Select(hit);
                        // Zoomed in with ships drawn apart: clicking one ship also opens its card.
                        if (_map.HitTestShip(down) is int shipId) ShipCardId = shipId;
                    }
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
        // With the game menu open, Esc closes it and nothing else reaches the map.
        if (MenuOpen)
        {
            if (key.Keycode == Key.Escape) CloseMenu();
            return;
        }
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
            case Key.F when Group.Count >= 2:
                FollowAll(Group.Select(g => g.Id).ToList());
                _main.Toast($"View locked on {Group.Count} fleets: it keeps them all on screen");
                break;
            case Key.F when _selected is int id: Follow(id, zoom: !key.ShiftPressed); break;
            case Key.Z: ZoomToSelected(); break;
            case Key.Enter or Key.KpEnter: ConfirmPlan(); break;
            case Key.O: OrderHold(HoldStyle.Orbit); break;
            case Key.G: OrderHold(HoldStyle.Drift); break;
            case Key.X: OrderStop(); break;
            case Key.C when Planner == null: EditRoute(); break;
            case Key.L: ToggleSide(); break;
            case Key.M: OrderMerge(); break;
            case Key.V: ToggleEngagement(); break;
            case Key.U:
                BattleView = !BattleView;
                _main.Toast(BattleView ? "Battle view: ship status only (U to bring the menus back)" : "Battle view off");
                break;
            case Key.Escape when BattleView: BattleView = false; break;
            case Key.Escape when Engagement != null: CloseEngagement(); break;
            case Key.Tab when Engagement != null: CycleEngagement(key.ShiftPressed ? -1 : 1); break;
            case Key.Tab: NextFleet(key.ShiftPressed ? -1 : 1); break;
            case Key.P: PauseOnContact = !PauseOnContact; _main.Toast(PauseOnContact ? "Will pause when enemies are spotted" : "Will not pause when enemies are spotted"); break;
            case Key.F1: ShowHelp = !ShowHelp; break;
            case Key.Escape when ShowHelp: ShowHelp = false; break;
            case Key.I when Sim.World.Loop: ShowIntel = !ShowIntel; break;
            case Key.Escape when ShowIntel: ShowIntel = false; break;
            case Key.N when Sim.World.Loop: ShowProgress = !ShowProgress; break;
            case Key.Escape when ShowProgress: ShowProgress = false; break;
            case Key.Escape when ShipCardId != null: ShipCardId = null; break;
            case Key.B:
                _map.Sight = _map.Sight switch
                {
                    MapView.SightView.Shadows => MapView.SightView.Coverage,
                    MapView.SightView.Coverage => MapView.SightView.Off,
                    _ => MapView.SightView.Shadows,
                };
                if ((_map.Sight != MapView.SightView.Off) != PlayerInfo.Has(MapLayer.Shadows)) PlayerInfo.Toggle(MapLayer.Shadows);
                _main.Toast(_map.Sight switch
                {
                    MapView.SightView.Coverage => "Sensor coverage: what your ships and planets can see, and what bodies hide (B)",
                    MapView.SightView.Off => "Sight shadows hidden (B to show)",
                    _ => "Sight shadows: grey areas planets hide from your sensors (B)",
                });
                break;
            case Key.F7: ToggleRevealAll(); _main.Toast(RevealAll ? "Debug: showing every fleet (F7 to hide)" : "Showing only what your sensors see"); break;
            case Key.R: if (Report != null) CloseReport(); else ShowLastReport(); break;
            case Key.E when Sim.World.Loop: ShowShips = false; ShowEmpire = !ShowEmpire; break;
            case Key.T when Sim.World.Loop: ShowResearch = !ShowResearch; break;
            case Key.Y when Sim.World.Loop: ShowShips = false; TogglePlanets(); break;
            case Key.K: ShowShips = false; ToggleFleets(); break;
            case Key.J when Sim.World.Loop: ToggleShips(); break;
            case Key.Bracketleft: _hud.Step(Sim.World, -1); break;
            case Key.Bracketright: _hud.Step(Sim.World, 1); break;
            case Key.Period when Sim.World.Loop: _hud.JumpToNextNeed(Sim.World); break;
            case Key.Escape when ShowShips && ShipCardId == null: ShowShips = false; break;
            case Key.Escape when ShowFleets && ShipCardId == null: ShowFleets = false; break;
            case Key.Escape when GroundScreenId != null: GroundScreenId = null; break;
            case Key.Escape when ShowResearch: ShowResearch = false; break;
            case Key.Escape when PlanetScreenId != null: PlanetScreenId = null; break;
            case Key.Escape when ShowPlanets: ShowPlanets = false; break;
            case Key.Escape when FittingFleetId != null: FittingFleetId = null; break;
            case Key.F5:
                SaveSlots.WriteQuick(_main.Paths, Sim);
                _main.Log.Info("save", "Quick save", new { ironman = Sim.World.Rules.Ironman });
                _main.Toast(Sim.World.Rules.Ironman ? "Saved (Ironman: one save, no quick load)" : "Quick saved");
                break;
            case Key.F9 when Sim.World.Rules.Ironman:
                _main.Toast("Ironman: no loading back. The game saves itself as you play");
                break;
            case Key.F9:
                var path = SaveSlots.PathFor(_main.Paths, SaveSlots.QuickSave);
                if (File.Exists(path)) _main.LoadGame(path);
                else _main.Toast("No quick save yet");
                break;
            case Key.Escape when Planner != null:
                CancelPlanning();
                break;
            case Key.Escape when Replay != null:
                CloseReplay();
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
                OpenMenu();
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
            var guess = Sensors.Predict(Sim.World, Side.Player, enemy.Id, Sim.World.Hour, seat: true) ?? enemy.Position;
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
