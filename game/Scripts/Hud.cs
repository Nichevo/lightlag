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
    private FleetPanel _fleetPanel = null!;
    private PlanetPanel _planetPanel = null!;
    private PlannerPanel _plannerPanel = null!;
    private ReportPanel _reportPanel = null!;
    private ReplayPanel _replayPanel = null!;
    private Button _battle = null!;
    private SidePanel _side = null!;
    private PopupMenu _news = null!;
    private readonly List<Action> _newsActions = new();
    private GroupPanel _groupPanel = null!;
    private EmpirePanel _empire = null!;
    private ResearchScreen _research = null!;
    private ColonyScreen _colonies = null!;
    private FleetScreen _fleets = null!;
    private ShipsScreen _ships = null!;

    /// <summary>[ and ]: step the open planet, fleets or ships screen to the previous or next entry. False when none is open.</summary>
    public bool Step(World w, int dir)
    {
        if (_ground.Visible) return false;
        if (_colonies.Visible && _screen.PlanetScreenId != null) { _colonies.Step(w, dir); return true; }
        if (_fleets.Visible) { _fleets.Step(w, dir); return true; }
        if (_ships.Visible) { _ships.Step(w, dir); return true; }
        return false;
    }
    private GroundScreen _ground = null!;
    private FittingPanel _fitting = null!;
    private EndPanel _end = null!;
    private EngagementPanel _engagement = null!;
    private ContactsPanel _contacts = null!;
    private GameMenu _menu = null!;
    private PanelContainer _mindPanel = null!;
    private Label _mind = null!;
    private Button _needsBadge = null!;
    private ControlBar _control = null!;
    private MenuButton _layers = null!;
    private readonly HashSet<string> _toasted = new();
    private bool _needsPrimed;
    private ShipCard _shipCard = null!;
    private KeyHelp _keyHelp = null!;
    private ProgressScreen _progress = null!;
    private IntelScreen _intel = null!;
    private BottomBar _bottomBar = null!;
    private EventBanner _eventBanner = null!;
    private BattleStrips _strips = null!;
    private Button _battleView = null!;
    private Label _help = null!;

    public Hud() : this(null!) { }
    public Hud(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        var main = Main.Instance;

        _side = new SidePanel(_screen) { Position = new Vector2(16, 96) };
        AddChild(_side);

        _empire = new EmpirePanel(_screen);
        _empire.Position = new Vector2(16, 96);
        AddChild(_empire);

        _status = Ui.Label("", 13, Ui.Dim);
        _status.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.KeepSize, 16);
        _status.Position += new Vector2(0, 40); // under the top bar, which can reach the right edge
        _status.GrowHorizontal = Control.GrowDirection.Begin;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(_status);

        _scaleBar = new ScaleBar { Camera = _screen.Camera };
        _scaleBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.KeepSize, 16);
        _scaleBar.Position += new Vector2(0, -60);
        AddChild(_scaleBar);

        var help = _help = Ui.Label("Wheel zoom · WASD pan · click select · right-click route or attack · Space pause · F1 every key", 12, Ui.Dim);
        help.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.KeepSize, 16);
        help.GrowVertical = Control.GrowDirection.Begin;
        AddChild(help);
        _bottomBar = new BottomBar(JumpTo) { Visible = false };
        AddChild(_bottomBar);
        _eventBanner = new EventBanner(_screen) { Visible = false };
        AddChild(_eventBanner);

        _info = Ui.Label("", 14);
        _info.CustomMinimumSize = new Vector2(280, 0);
        _infoPanel = Ui.Panel(_info, 14);
        _infoPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 16);
        _infoPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _infoPanel.GrowVertical = Control.GrowDirection.Begin;
        AddChild(_infoPanel);

        _planetPanel = new PlanetPanel(_screen);
        _planetPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 16);
        _planetPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _planetPanel.GrowVertical = Control.GrowDirection.Begin;
        AddChild(_planetPanel);

        _fleetPanel = new FleetPanel(_screen);
        _fleetPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 16);
        _fleetPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _fleetPanel.GrowVertical = Control.GrowDirection.Begin;
        AddChild(_fleetPanel);

        _groupPanel = new GroupPanel(_screen);
        _groupPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.KeepSize, 16);
        _groupPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _groupPanel.GrowVertical = Control.GrowDirection.Begin;
        AddChild(_groupPanel);

        // Battle time banner: says which fleets and where; click it to go there.
        _battle = new Button { Flat = true, FocusMode = Control.FocusModeEnum.None, TooltipText = "Click to go to the fleets involved" };
        _battle.AddThemeFontSizeOverride("font_size", 18);
        _battle.AddThemeColorOverride("font_color", Ui.Danger);
        _battle.AddThemeColorOverride("font_hover_color", Ui.Text);
        _battle.Pressed += () => Main.Instance.RunGuarded("go to battle", _screen.FocusBattle);
        _battle.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.KeepSize, 112);
        _battle.GrowHorizontal = Control.GrowDirection.Both;
        AddChild(_battle);

        _reportPanel = new ReportPanel(_screen);
        _reportPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.KeepSize);
        _reportPanel.GrowHorizontal = Control.GrowDirection.Both;
        _reportPanel.GrowVertical = Control.GrowDirection.Both;
        AddChild(_reportPanel);

        _replayPanel = new ReplayPanel(_screen) { Visible = false };
        _replayPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.KeepSize);
        _replayPanel.GrowHorizontal = Control.GrowDirection.Both;
        _replayPanel.GrowVertical = Control.GrowDirection.Both;
        AddChild(_replayPanel);

        _plannerPanel = new PlannerPanel(_screen);
        _plannerPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.KeepSize, 16);
        _plannerPanel.GrowHorizontal = Control.GrowDirection.Begin;
        _plannerPanel.Position += new Vector2(0, 60);
        AddChild(_plannerPanel);

        _mind = Ui.Label("", 12);
        _mind.CustomMinimumSize = new Vector2(420, 0);
        _mind.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _mindPanel = Ui.Panel(_mind, 10);
        _mindPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterLeft, Control.LayoutPresetMode.KeepSize, 16);
        _mindPanel.GrowVertical = Control.GrowDirection.Both;
        _mindPanel.MouseFilter = Control.MouseFilterEnum.Ignore;
        AddChild(_mindPanel);

        // Coming contacts: top left, under the menu bar and the sight-view line.
        _contacts = new ContactsPanel(_screen) { Position = new Vector2(16, 124) };
        AddChild(_contacts);

        _engagement = new EngagementPanel(_screen);
        AddChild(_engagement);

        _fitting = new FittingPanel(_screen) { Visible = false };
        _fitting.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.KeepSize);
        _fitting.GrowHorizontal = Control.GrowDirection.Both;
        _fitting.GrowVertical = Control.GrowDirection.Both;
        AddChild(_fitting);
        _research = new ResearchScreen(_screen) { Visible = false };
        AddChild(_research);
        _colonies = new ColonyScreen(_screen) { Visible = false };
        AddChild(_colonies);
        _ground = new GroundScreen(_screen) { Visible = false };
        AddChild(_ground);
        _fleets = new FleetScreen(_screen) { Visible = false };
        AddChild(_fleets);
        _ships = new ShipsScreen(_screen) { Visible = false };
        AddChild(_ships);

        _shipCard = new ShipCard(_screen) { Visible = false };
        AddChild(_shipCard);
        _progress = new ProgressScreen(_screen) { Visible = false };
        AddChild(_progress);
        _intel = new IntelScreen(_screen) { Visible = false };
        AddChild(_intel);
        _strips = new BattleStrips(_screen) { Visible = false };
        AddChild(_strips);
        _keyHelp = new KeyHelp(_screen) { Visible = false };
        AddChild(_keyHelp);

        _pins = new NeedPins(_screen, this) { Visible = false };
        AddChild(_pins);
        MoveChild(_pins, 0); // under every panel
        _tray = new NeedsTray(_screen, this) { Visible = false };
        AddChild(_tray);
        _automation = new AutomationMenu(_screen) { Visible = false };
        AddChild(_automation);
        _cards = new NeedCards(_screen, this) { Visible = false };
        AddChild(_cards);
        BuildBars(); // over the screens, so the menu bar stays in view

        _menu = new GameMenu(_screen) { Visible = false };
        _end = new EndPanel(_screen);
        _end.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.KeepSize);
        _end.GrowHorizontal = Control.GrowDirection.Both;
        _end.GrowVertical = Control.GrowDirection.Both;
        AddChild(_end);
        _menu.ZIndex = 50;
        AddChild(_menu); // last and on top, so it covers everything
        _ = main;
    }

    private static Button SmallButton(string text, Action pressed, string tip) => Ui.SmallButton(text, pressed, tip);

    /// <summary>The top bar's research button: what the first busy team is on, and days left.</summary>
    private static string ResearchLine(World w)
    {
        var orders = w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research && o.Paid).ToList();
        // A team held up because its next idea can't be paid for yet: say so, so it doesn't look like research stopped.
        var held = w.Orders.FirstOrDefault(o => o.Side == Side.Player && o.Kind == OrderKind.Research && !o.Paid && o.Waiting != null && Research.Next(w, o));
        if (orders.Count == 0) return held != null ? $"Research WAITING: {held.Waiting} ▸" : "Research: idle ▸";
        var first = orders.OrderBy(o => Research.DaysUntilDone(w, o)).First();
        return $"Research: {GameData.Research(first.Key).Name} {Research.DaysUntilDone(w, first):0.#} d"
               + (orders.Count > 1 ? $" (+{orders.Count - 1})" : "") + " ▸";
    }

    public void Refresh()
    {
        var sim = Main.Instance.Sim;
        if (sim == null) return;
        BenchProbe.Start();
        _date.Text = _screen.InBattle ? GameTime.Format(sim.World.Hour) : GameTime.Format(sim.World.Tick);
        _battle.Text = (_screen.InBattle ? _screen.BattleLine() : "")
                       + (_screen.RevealAll ? (_screen.InBattle ? "\n" : "") + "DEBUG: showing every fleet (F7)" : "");
        _reportPanel.Visible = _screen.Report != null;
        if (_screen.Report != null) _reportPanel.Refresh(_screen.Report);
        _replayPanel.Visible = _screen.Replay != null;
        if (_screen.Replay != null) _replayPanel.Open(_screen.Replay);
        _pause.Modulate = _screen.Paused ? Ui.Rival : Colors.White;
        var silent = Economy.Of(sim.World, Side.Player).RadioSilence;
        for (var i = 0; i < _speedButtons.Count; i++)
            _speedButtons[i].Modulate = !_screen.Paused && i == _screen.SpeedIndex ? Ui.Player : new Color(1, 1, 1, 0.75f);
        var rival = sim.World.Minds.FirstOrDefault(m => m.Side == Side.Rival);
        // Second line: the rival's difficulty, then PAUSED and radio silence.
        var second = string.Join(" · ", new[] { rival != null ? $"{rival.Difficulty} rival" : null, _screen.Paused ? "PAUSED" : null,
            silent ? $"RADIO SILENCE ({sim.World.Light.Held.Count(t => t.Side == Side.Player)} held)" : null }.Where(x => x != null));
        _status.Text = $"seed {sim.World.Seed} · build {Main.Instance.Build}"
                       + (sim.RuleViolations > 0 ? $" · rule failures: {sim.RuleViolations}" : "")
                       + (second.Length > 0 ? "\n" + second : "");
        _status.Modulate = silent ? Ui.Danger : Colors.White;
        _scaleBar.QueueRedraw();
        BenchProbe.Lap("top-status");

        var w = sim.World;
        if (w.Loop)
        {
            var me = Economy.Of(w, Side.Player);
            RefreshNeeds(w, me);
            _control.Set(w);
        }
        BenchProbe.Lap("empire-button+needs+control");
        BenchProbe.Lap("research-line");
        _research.Visible = w.Loop && _screen.ShowResearch;
        if (_research.Visible) _research.Refresh(sim);
        BenchProbe.Lap("research-screen");
        _colonies.Visible = w.Loop && (_screen.ShowPlanets || _screen.PlanetScreenId != null) && !_research.Visible;
        if (_colonies.Visible) _colonies.Refresh(sim);
        BenchProbe.Lap("colonies");
        _ground.Visible = w.Loop && _screen.GroundScreenId != null && !_research.Visible;
        if (_ground.Visible) _ground.Refresh(sim);
        if (_ground.Visible) _colonies.Visible = false;
        _fleets.Visible = _screen.ShowFleets && !_research.Visible && !_colonies.Visible && !_ground.Visible && _screen.FittingFleetId == null;
        if (_fleets.Visible) _fleets.Refresh(sim);
        BenchProbe.Lap("fleets-screen");
        _ships.Visible = w.Loop && _screen.ShowShips && !_research.Visible && !_colonies.Visible && !_ground.Visible && !_fleets.Visible;
        if (_ships.Visible) _ships.Refresh(sim);
        BenchProbe.Lap("ships-screen");
        _bottomBar.UrgentOnly = _screen.Engagement != null;
        if (w.Loop)
        {
            var view = GetViewport().GetVisibleRect().Size;
            var barWidth = Math.Min(view.X * 0.55f, 900);
            _bottomBar.Position = new Vector2(16, view.Y - 64);
            _bottomBar.Size = new Vector2(barWidth, 30);
            _bottomBar.Refresh(w, barWidth);
        BenchProbe.Lap("bottombar");
            // Wide events (a flare, a comet pass): a banner centred under the top bar, below the battle line if there is one.
            _eventBanner.Refresh(w);
        BenchProbe.Lap("banner");
            var under = _battle.Text.Length > 0 ? _battle.Position.Y + _battle.Size.Y + 4 : ContentTop + 4;
            _eventBanner.Position = new Vector2(Mathf.Max(16, (view.X - _eventBanner.Size.X) / 2), under);
        }
        else { _bottomBar.Visible = false; _eventBanner.Visible = false; }
        if (_screen.EmpireTabWanted is EmpirePanel.Tab tab) { _empire.Show(tab); _screen.EmpireTabWanted = null; }
        _empire.Visible = w.Loop && _screen.ShowEmpire && !_research.Visible && !_colonies.Visible && !_ground.Visible && !_fleets.Visible && !_ships.Visible;
        if (_empire.Visible) _empire.Refresh(sim);
        BenchProbe.Lap("empire-panel");
        var rivalMind = Mind.Of(w, Side.Rival);
        _mindPanel.Visible = _screen.RevealAll && rivalMind != null && !_empire.Visible;
        if (_mindPanel.Visible) _mind.Text = MindText(w, rivalMind!);
        _end.Visible = w.Winner != null && _screen.ShowEnd;
        if (_end.Visible) _end.Refresh(w);
        BenchProbe.Lap("mind+end");

        var fleet = _screen.SelectedFleet;
        _fleetPanel.Visible = fleet != null && !_fleets.Visible && !_ships.Visible;
        if (fleet != null) _fleetPanel.Refresh(sim, fleet);
        BenchProbe.Lap("fleet-panel");
        var group = _screen.Group;
        _groupPanel.Visible = group.Count >= 2;
        if (group.Count >= 2) _groupPanel.Refresh(sim, group);
        _plannerPanel.Visible = _screen.Planner != null;
        if (_screen.Planner != null) _plannerPanel.Refresh(sim, _screen.Planner);
        BenchProbe.Lap("group+planner");
        // On a short screen the planner and the fleet panel would overlap: the planner wins while it is open.
        if (_plannerPanel.Visible && _fleetPanel.Visible && _plannerPanel.GetGlobalRect().End.Y > _fleetPanel.GetGlobalRect().Position.Y)
            _fleetPanel.Visible = false;

        if (_screen.MenuOpen && !_menu.Visible) { _menu.Open(_screen.MenuPage); _screen.MenuPage = null; }
        else if (!_screen.MenuOpen) _menu.Visible = false;

        var engagement = _screen.EngagementNow;
        // A panel you opened yourself (empire, planner, a report or a replay) sits where the engagement
        // panel draws, so while it is open the engagement panel steps aside; it comes back when you close it.
        var fittingFleet = _screen.FittingFleetId is int ff ? w.FindFleet(ff) : null;
        if (fittingFleet == null || fittingFleet.Side != Side.Player) _screen.FittingFleetId = null;
        _fitting.Visible = fittingFleet != null && fittingFleet.Side == Side.Player && !_research.Visible && !_colonies.Visible;
        if (_fitting.Visible) _fitting.Refresh(sim, fittingFleet!);
        BenchProbe.Lap("menu+fitting");
        var screen = _side.GetViewportRect().Size;
        _side.Visible = _screen.ShowSide && !_research.Visible && !_colonies.Visible && !_fleets.Visible && !_ships.Visible && !_ground.Visible && !_empire.Visible && !_fitting.Visible && engagement == null && !_menu.Visible;
        RefreshNeedsUi(sim);
        if (_side.Visible) _side.Refresh(sim, Mathf.Max(200, screen.Y - ContentTop - 8 - 110));
        BenchProbe.Lap("side-panel");
        var leftEdge = _side.Visible ? 16 + SidePanel.Width + 36 : _tray.Visible ? _tray.Position.X + _tray.Size.X + 16 : 16;
        _contacts.Position = new Vector2(leftEdge, ContentTop + 8);
        _mindPanel.Position = new Vector2(leftEdge, _mindPanel.Position.Y);
        var covered = _research.Visible || _colonies.Visible || _fleets.Visible || _ships.Visible || _ground.Visible || _fitting.Visible || _empire.Visible || _plannerPanel.Visible || _reportPanel.Visible || _replayPanel.Visible || _menu.Visible;
        _engagement.Visible = engagement != null && !covered;
        _contacts.Refresh();
        BenchProbe.Lap("contacts");
        if (engagement != null || _empire.Visible || _mindPanel.Visible) _contacts.Visible = false;
        if (engagement != null)
        {
            if (_engagement.Visible) _engagement.Refresh(sim, engagement);
            _fleetPanel.Visible = false;
            _groupPanel.Visible = false;
            _mindPanel.Visible = false;
            _battle.Visible = false;
        }
        else _battle.Visible = true;
        BenchProbe.Lap("engagement");

        // Ship card, How am I doing, intelligence and key help: opened on demand, over the map.
        var card = _screen.ShipCardId is int cid ? w.Ships.Find(x => x.Id == cid) : null;
        if (card == null || (card.Side != Side.Player && !_screen.RevealAll && !Sensors.Sees(w, Side.Player, w.FleetOf(card)!, seat: true))) _screen.ShipCardId = null;
        _shipCard.Visible = _screen.ShipCardId != null && !_menu.Visible;
        if (_shipCard.Visible)
        {
            _shipCard.Refresh(sim, card!);
            // Beside the fleet panel (to its left), level with the bottom of the screen.
            var view = _shipCard.GetViewportRect().Size;
            var right = _fleetPanel.Visible ? _fleetPanel.GetGlobalRect().Position.X - 12 : view.X - 16;
            var size = _shipCard.GetCombinedMinimumSize();
            _shipCard.Position = new Vector2(right - size.X, view.Y - size.Y - 16);
        }
        _progress.Visible = w.Loop && _screen.ShowProgress && !_menu.Visible;
        if (_progress.Visible) _progress.Refresh(sim);
        BenchProbe.Lap("shipcard+progress");
        _intel.Visible = w.Loop && _screen.ShowIntel && !_menu.Visible && !_progress.Visible;
        if (_intel.Visible) _intel.Refresh(sim);
        BenchProbe.Lap("intel");
        _keyHelp.Visible = _screen.ShowHelp && !_menu.Visible;

        // In a full game a selected planet gets the planet screen; the sandbox keeps the short description.
        var planet = fleet == null && group.Count < 2 && engagement == null && w.Loop ? _screen.SelectedBody : null;
        _planetPanel.Visible = planet != null && planet.Kind != BodyKind.Star && !_colonies.Visible && !_fleets.Visible && !_ships.Visible && !_ground.Visible && !_research.Visible;
        if (_planetPanel.Visible) _planetPanel.Refresh(sim, planet!);
        BenchProbe.Lap("planet-panel");
        var text = fleet == null && group.Count < 2 && engagement == null && !_planetPanel.Visible ? Describe(sim) : null;
        _infoPanel.Visible = text != null;
        if (text != null) _info.Text = text;
        RefreshBars(w);
        PlaceUnderBars();
        ApplyBattleView(sim);
        BenchProbe.Lap("info+battleview");
    }

    /// <summary>Battle view (U): hide the menus and panels, keep the date, speeds and the control share, and show a strip per ship.</summary>
    private void ApplyBattleView(Simulation sim)
    {
        var on = _screen.BattleView;
        _strips.Visible = on && !_menu.Visible;
        _help.Visible = !on;
        _status.Visible = !on;
        if (!on) return;
        foreach (var c in new Control[] { _side, _empire, _contacts, _fleetPanel, _groupPanel, _infoPanel, _planetPanel, _engagement, _mindPanel, _plannerPanel,
                     _research, _colonies, _ground, _fleets, _ships, _intel, _progress, _tray, _automation, _cards })
            c.Visible = false;
        if (_strips.Visible) _strips.Refresh(sim);
    }

    /// <summary>Debug (F7): the rival's temperament, beliefs, jobs and latest thoughts.</summary>
    private static string MindText(World w, MindState m)
    {
        var lines = new List<string>
        {
            $"RIVAL MIND (debug, F7) · {m.Personality} · {m.Difficulty}",
            $"Attack ratio {m.Ratio:0.00} (rolled {m.BaseRatio:0.00}) · first probe day {m.FirstProbeDay:0}",
            $"Power {m.OwnPower:0} · guesses yours at {m.EnemyPowerGuess:0}",
            $"Goal: {m.Goal}",
        };
        var jobs = m.Jobs.GroupBy(j => j.Kind).Select(g => $"{g.Key} {g.Count()}");
        lines.Add("Jobs: " + (m.Jobs.Count > 0 ? string.Join(", ", jobs) : "none"));
        lines.Add("");
        lines.Add("INTEL (belief vs truth)");
        lines.AddRange(Intel.BeliefVsTruth(w, Side.Player));
        lines.AddRange(Intel.BeliefVsTruth(w, m.Side).Take(3));
        lines.Add("");
        foreach (var t in m.Thoughts.TakeLast(8).Reverse())
            lines.Add($"{GameTime.Format(t.Hour)}: {t.Text}");
        return string.Join("\n", lines);
    }

    public void TickShip(int shipId) => _fleetPanel.TickOnly(shipId);

    /// <summary>Panels that open under the top bar follow its height (the menu bar can be hidden or slid away).</summary>
    private void PlaceUnderBars()
    {
        var top = ContentTop + 8;
        _side.Position = new Vector2(16, top);
        _empire.Position = new Vector2(16, top);
        var view = GetViewport().GetVisibleRect().Size;
        var statusSize = _status.GetCombinedMinimumSize();
        _status.Position = new Vector2(view.X - statusSize.X - 16, ContentTop + 2);
        _plannerPanel.Position = new Vector2(_plannerPanel.Position.X, ContentTop + 44);
        _battle.Position = new Vector2(_battle.Position.X, ContentTop + 4);
    }

    /// <summary>The Needs you badge opens the list, and closes it again when it is already showing.</summary>
    public bool SideVisible => _side.Visible;

    /// <summary>The badge drops the tray (or, with the tray off, opens the Empire screen's Needs you tab); again closes it.</summary>
    private void ToggleNeeds()
    {
        if (PlayerInfo.Tray == TrayMode.Off)
        {
            if (_empire.Visible && _empire.Current == EmpirePanel.Tab.Needs) { _screen.ShowEmpire = false; return; }
            _screen.ShowEmpire = true;
            _empire.Show(EmpirePanel.Tab.Needs);
            return;
        }
        TrayOpen = !TrayOpen;
    }

    // ---- Needs you tray, pins and cards ----------------------------------------------------------

    private NeedPins _pins = null!;
    private NeedsTray _tray = null!;
    private AutomationMenu _automation = null!;
    private NeedCards _cards = null!;

    /// <summary>The tray is down (set by the badge, a pin or the "." key; never by itself).</summary>
    public bool TrayOpen { get; set; }

    /// <summary>The tray row the mouse is over (its pin rings), and the selected one (lit with its pin).</summary>
    public string? HoverNeed { get; set; }
    public string? SelectedNeed { get; set; }

    public bool AutomationOpen { get => _automation.Visible; set => _automation.Visible = value; }

    /// <summary>For screenshots: the tray with its strips unfolded.</summary>
    public void UnfoldTray() => _tray.Unfold();

    /// <summary>"Work through them": the one-at-a-time cards.</summary>
    public bool CardsOpen => _cards.Visible;
    public void OpenCards() { TrayOpen = false; _cards.Open(); }
    public bool CardKey(InputEventKey key) => _cards.OnKey(key);

    /// <summary>A pin was clicked: open the tray on that row and go there.</summary>
    public void PickNeed(NeedItem n)
    {
        if (PlayerInfo.Tray != TrayMode.Off) TrayOpen = true;
        SelectedNeed = n.Key;
        GoTo(n);
    }

    /// <summary>Go: fly the map there and open the right panel (fleet or planet), leaving the tray up.</summary>
    public void GoTo(NeedItem n)
    {
        var w = Main.Instance.Sim?.World;
        if (n.FleetId is int f && w?.FindFleet(f) != null) _screen.PickFleet(f);
        else if (n.BodyId is int b && w?.FindBody(b) != null) _screen.PickBody(b);
        else JumpTo(n);
    }

    /// <summary>Enter with the tray down: the selected row's act button.</summary>
    public bool TrayAct()
    {
        if (!_tray.Visible || SelectedNeed == null) return false;
        return _tray.ActSelected();
    }

    /// <summary>Lay out and refresh the tray, the automation menu, the pins and the cards.</summary>
    private void RefreshNeedsUi(Simulation sim)
    {
        var w = sim.World;
        if (PlayerInfo.Tray == TrayMode.Off) TrayOpen = false;
        var covered = _research.Visible || _colonies.Visible || _fleets.Visible || _ships.Visible || _ground.Visible || _fitting.Visible || _empire.Visible || _menu.Visible;
        _tray.Visible = w.Loop && TrayOpen && !covered && !_cards.Visible && !_screen.BattleView;
        if (_tray.Visible)
        {
            var view = GetViewport().GetVisibleRect().Size;
            _tray.Position = new Vector2(16, ContentTop + 8);
            _tray.MaxHeight = Mathf.Max(220, view.Y - _tray.Position.Y - 90);
            _tray.Refresh(w);
        }
        else _automation.Visible = false;
        if (_automation.Visible)
        {
            _automation.Position = new Vector2(_tray.Position.X + _tray.Size.X + 8, _tray.Position.Y);
            _automation.Refresh(w);
        }
        _cards.Visible = _cards.Visible && w.Loop && !_menu.Visible;
        if (_cards.Visible) _cards.Refresh(w);
        _pins.Visible = w.Loop && !_screen.BattleView && !covered && (PlayerInfo.Has(MapLayer.NeedPins) || PlayerInfo.Has(MapLayer.AdvicePins) || PlayerInfo.Has(MapLayer.AutoPins));
        if (_pins.Visible) _pins.Refresh(w);
        if (_tray.Visible) { _side.Visible = false; }
    }

    /// <summary>The Needs you badge, and a toast once for each new Urgent item.</summary>
    private void RefreshNeeds(World w, SideState me)
    {
        var urgent = me.Needs.Where(n => n.Level == NeedLevel.Urgent).ToList();
        var trouble = me.Needs.Count(n => n.Level != NeedLevel.Advice);
        _needsBadge.Text = me.Needs.Count == 0 ? "✓ Nothing needs you" : trouble == 0 ? $"✓ {me.Needs.Count} advice" : $"⚠ {trouble} need{(trouble == 1 ? "s" : "")} you" + (urgent.Count > 0 ? $" ({urgent.Count} urgent)" : "");
        var badgeColor = urgent.Count > 0 ? Ui.Danger : me.Needs.Count > 0 ? new Color("ffd34d") : new Color(1, 1, 1, 0.6f);
        // A new urgent item flashes the badge (the tray never opens by itself) until you open the tray or the list.
        if (urgent.Count == 0 || TrayOpen || _empire.Visible && _empire.Current == EmpirePanel.Tab.Needs) _flashing = false;
        if (_flashing)
        {
            var on = Time.GetTicksMsec() / 450 % 2 == 0;
            badgeColor = on ? Ui.Danger : Colors.White;
            _needsBadge.SelfModulate = on ? Colors.White : new Color(1, 1, 1, 0.55f);
        }
        else _needsBadge.SelfModulate = Colors.White;
        _needsBadge.Modulate = badgeColor;
        _needsBadge.TooltipText = me.Needs.Count == 0 ? "Nothing waits for you" : string.Join("\n", me.Needs.OrderBy(n => n.Level).Take(6).Select(n => $"{n.Level}: {n.Text}"))
                                                                               + (PlayerInfo.Tray == TrayMode.Off ? "\nClick for the Needs you list" : "\nClick for the Needs you tray (. next item)");
        foreach (var n in urgent)
        {
            if (!_toasted.Add(n.Key) || !_needsPrimed) continue;
            var need = n;
            Main.Instance.Toast(n.Text, onClick: () => PickNeed(need));
            _flashing = true; // the badge flashes until you look

        }
        _needsPrimed = true; // items already there when the game opens don't pop up
        _toasted.IntersectWith(me.Needs.Select(n => n.Key)); // an item that clears and comes back pops up again
    }

    private bool _flashing;

    private int _nextNeed;

    /// <summary>The "." key: go to the next thing that needs you, most urgent first, wrapping round.</summary>
    public void JumpToNextNeed(World w)
    {
        var needs = Economy.Of(w, Side.Player).Needs
            .OrderBy(n => n.Level).ThenBy(n => n.Hour).ThenBy(n => n.Key, StringComparer.Ordinal).ToList();
        if (needs.Count == 0) { Main.Instance.Toast("Nothing needs you right now"); return; }
        var i = _nextNeed % needs.Count;
        _nextNeed = i + 1;
        var n = needs[i];
        if (PlayerInfo.Tray != TrayMode.Off && n.Level != NeedLevel.Advice)
        {
            TrayOpen = true;
            SelectedNeed = n.Key;
            GoTo(n);
            return;
        }
        Main.Instance.Toast($"{i + 1} of {needs.Count}: {n.Text}");
        JumpTo(n);
    }

    private void JumpTo(NeedItem n)
    {
        // Go where the fix is: the research or ships screen, your planet's screen, or the fleet on the map.
        var w = Main.Instance.Sim?.World;
        var screen = w == null ? NeedScreens.Empire : NeedScreens.For(w, n);
        if (screen == NeedScreens.Research && n.FleetId == null && n.BodyId == null) _screen.ShowResearch = true;
        else if (screen == NeedScreens.Ships && n.FleetId == null) { if (!_screen.ShowShips) _screen.ToggleShips(); }
        else if (n.FleetId is int f && w?.FindFleet(f) != null) _screen.PickFleet(f);
        else if (n.BodyId is int b && screen == NeedScreens.Planets && w?.FindBody(b)?.Owner == Side.Player) { _screen.PickBody(b); _screen.OpenPlanet(b); }
        else if (n.BodyId is int b2) _screen.PickBody(b2);
        else { _screen.ShowEmpire = true; _empire.Show(EmpirePanel.Tab.Needs); }
    }

    private const int PinsUrgentOnlyId = 999;
    private const int PresetCalm = 1000, PresetWar = 1001, PresetTrade = 1002, PresetDefault = 1003, PresetBattle = 1004;

    private void FillLayersMenu()
    {
        var popup = _layers.GetPopup();
        popup.Clear();
        foreach (var (layer, name, tip) in PlayerInfo.LayerList)
        {
            popup.AddCheckItem(name, (int)layer);
            var i = popup.ItemCount - 1;
            popup.SetItemChecked(i, PlayerInfo.Has(layer));
            popup.SetItemTooltip(i, tip);
            if (layer == MapLayer.NeedPins)
            {
                popup.AddCheckItem("    urgent and soon only", PinsUrgentOnlyId);
                popup.SetItemChecked(popup.ItemCount - 1, PlayerInfo.PinsUrgentOnly);
                popup.SetItemDisabled(popup.ItemCount - 1, !PlayerInfo.Has(MapLayer.NeedPins));
                popup.SetItemTooltip(popup.ItemCount - 1, "Pins only for urgent and soon items, not for news");
            }
        }
        popup.AddSeparator("Presets");
        popup.AddItem("Calm: orbits and labels", PresetCalm);
        popup.AddItem("War: sight, lag, old sightings, lanes", PresetWar);
        popup.AddItem("Battle: sight, lag, where they could be, what they see of you", PresetBattle);
        popup.AddItem("Trade: lanes and supply runs", PresetTrade);
        popup.AddItem("Everything as it was", PresetDefault);
    }

    private void PickLayer(int id)
    {
        if (id == PinsUrgentOnlyId) { PlayerInfo.SetPinsUrgentOnly(!PlayerInfo.PinsUrgentOnly); FillLayersMenu(); return; }
        var layers = id switch
        {
            PresetCalm => PlayerInfo.Calm,
            PresetWar => PlayerInfo.War,
            PresetTrade => PlayerInfo.Trade,
            PresetBattle => PlayerInfo.Battle,
            PresetDefault => PlayerInfo.Default,
            _ => PlayerInfo.Layers ^ (MapLayer)id,
        };
        // Presets set the view; the Needs you pins stay as they were.
        if (id >= PresetCalm) layers = (layers & ~PlayerInfo.PinLayers) | (PlayerInfo.Layers & PlayerInfo.PinLayers);
        PlayerInfo.SetLayers(layers);
        // The sight view follows the Sight shadows layer.
        if (PlayerInfo.Has(MapLayer.Shadows) && _screen.Sight == MapView.SightView.Off) _screen.Sight = MapView.SightView.Shadows;
        if (!PlayerInfo.Has(MapLayer.Shadows)) _screen.Sight = MapView.SightView.Off;
        if (id < PinsUrgentOnlyId) FillLayersMenu();
    }

    /// <summary>The fleet list: your fleets with their ships, then the rival fleets.</summary>
    private void FillNewsMenu()
    {
        var popup = _news;
        popup.Clear();
        _newsActions.Clear();
        var news = Main.Instance.News;
        if (news.Count == 0) { popup.AddItem("No messages yet", -1); popup.SetItemDisabled(0, true); return; }
        foreach (var n in Enumerable.Reverse(news))
        {
            var text = $"{GameTime.Format(n.Hour)}   {n.Text}" + (n.Click != null ? "  →" : "");
            if (n.Click != null) { popup.AddItem(text, _newsActions.Count); _newsActions.Add(n.Click); }
            else { popup.AddItem(text, -1); popup.SetItemDisabled(popup.ItemCount - 1, true); }
        }
    }

    /// <summary>Open the planet list (for screenshots); false goes back to the fleets.</summary>
    public void ShowBodyMenu(bool open)
    {
        _screen.ShowSide = true;
        _side.ShowPlanets = open;
    }

    private string? Describe(Simulation sim)
    {
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
            var w = sim.World;
            if (b.Owner is Side owner)
                lines.Add((owner == Side.Player ? "Yours" : "Held by the rival")
                          + (b.Shipyard ? $" · shipyard, armour {b.ShipyardArmour / GameData.Economy.Shipyard.Armour * 100:0} %" : "")
                          + (owner == Side.Player ? (b.Shipyard ? ": fleets here refill, repair and rearm" : ": fleets here refill supplies") : ""));
            if (w.Loop && b.Industry > 0) lines.Add($"Industry: {b.Industry:0} a day for whoever holds it");
            else if (w.Loop && b.Kind != BodyKind.Star) lines.Add("No industry: it can't be claimed and doesn't count towards winning");
            if (b.ClaimSide is Side claimer)
                lines.Add($"{(claimer == Side.Player ? "You are" : "The rival is")} claiming it: {b.ClaimHours / 24:0.#} of {Economy.ClaimHoursFor(b) / 24:0} days");
            else if (w.Loop && b.Industry > 0 && b.Owner != Side.Player && !b.Shipyard)
                lines.Add($"Hold orbit here {Economy.ClaimHoursFor(b) / 24:0} days with no enemy in orbit to claim it");
            else if (b.Shipyard && b.Owner is Side.Rival or Side.Third && Economy.BombardStatus(w, b, Side.Player) is string attack)
                lines.Add("Shipyards can't be claimed, only destroyed. " + attack);
            if (_screen.SelectedFleet == null && sim.World.Fleets.Any(f => f.Side == Side.Player)) lines.Add("Select a fleet, then right-click here to fly");
            lines.Add(b.Owner == Side.Player && b.Board != null && w.Loop
                ? (_screen.IsFollowing(b.Id) ? "Following · double-click to open the planet" : "Double-click to open the planet · F to follow")
                : _screen.IsFollowing(b.Id) ? "Following" : "Double-click or F to follow");
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

    /// <summary>A light-lag delay or age: "under a minute", "18 min", "1 h 12 min".</summary>
    public static string Lag(double hours)
    {
        var minutes = (int)Math.Round(hours * 60);
        if (minutes < 1) return "under a minute";
        if (minutes < 60) return $"{minutes} min";
        if (hours < 48) return $"{minutes / 60} h {minutes % 60:00} min";
        return Hours(hours);
    }

    public static string Title(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
