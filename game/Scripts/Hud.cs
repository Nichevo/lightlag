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
    private Button _sideButton = null!;
    private SidePanel _side = null!;
    private MenuButton _news = null!;
    private readonly List<Action> _newsActions = new();
    private GroupPanel _groupPanel = null!;
    private Button _pauseOnContact = null!;
    private Button _silence = null!;
    private Button _empireButton = null!;
    private EmpirePanel _empire = null!;
    private Button _researchButton = null!;
    private ResearchScreen _research = null!;
    private ColonyScreen _colonies = null!;
    private GroundScreen _ground = null!;
    private Button _planetsButton = null!;
    private FittingPanel _fitting = null!;
    private EndPanel _end = null!;
    private EngagementPanel _engagement = null!;
    private ContactsPanel _contacts = null!;
    private GameMenu _menu = null!;
    private PanelContainer _mindPanel = null!;
    private Label _mind = null!;
    private Button _intelButton = null!, _needsBadge = null!, _progressButton = null!;
    private ControlBar _control = null!;
    private MenuButton _layers = null!;
    private HBoxContainer _row2 = null!;
    private readonly HashSet<string> _toasted = new();
    private bool _needsPrimed;
    private ShipCard _shipCard = null!;
    private KeyHelp _keyHelp = null!;
    private ProgressScreen _progress = null!;
    private IntelScreen _intel = null!;

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
        bar.AddChild(SmallButton("Zoom in", () => _screen.ZoomToSelected(), "Zoom right in on what is selected and follow it (Z)"));
        _sideButton = SmallButton("Fleets & planets", () => _screen.ToggleSide(), "The list of your fleets, the rival fleets you know of and the planets (L). Tab: next fleet");
        bar.AddChild(_sideButton);
        _news = new MenuButton { Text = "News ▾", TooltipText = "The latest messages; click one to go there", FocusMode = Control.FocusModeEnum.None, Flat = false };
        _news.CustomMinimumSize = new Vector2(80, 32);
        _news.AddThemeFontSizeOverride("font_size", 15);
        var newsPopup = _news.GetPopup();
        newsPopup.AddThemeFontSizeOverride("font_size", 14);
        _news.AboutToPopup += FillNewsMenu;
        newsPopup.IdPressed += id => { if (id >= 0 && id < _newsActions.Count) Main.Instance.RunGuarded("news", _newsActions[(int)id]); };
        bar.AddChild(_news);
        _pauseOnContact = SmallButton("Pause on contact", () => _screen.PauseOnContact = !_screen.PauseOnContact, "Pause when an enemy fleet comes into sight (P)");
        bar.AddChild(_pauseOnContact);
        _silence = SmallButton("Radio silence", () => _screen.ToggleRadioSilence(),
            "No orders leave your flagship (they wait until you lift it), so the rival can't follow your signal traffic to find it. Fleets in command range still get orders");
        bar.AddChild(_silence);
        _empireButton = SmallButton("Empire", () => _screen.ShowEmpire = !_screen.ShowEmpire, "Industry, shipyard, research and claims (E)");
        bar.AddChild(_empireButton);
        _researchButton = SmallButton("Research", () => _screen.ShowResearch = !_screen.ShowResearch, "Research teams, ideas and the discovery web (T)");
        bar.AddChild(_researchButton);
        _planetsButton = SmallButton("Planets ▸", () => _screen.TogglePlanets(), "Your planets and every other world: people, goods, status (Y)");
        bar.AddChild(_planetsButton);
        AddChild(bar);

        // Second row: Needs you, the control bar, Map layers, How am I doing and key help.
        var row2 = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row2.AddThemeConstantOverride("separation", 8);
        row2.Position = new Vector2(16, 48);
        _needsBadge = SmallButton("", ToggleNeeds, "What wants your look: click for the Needs you list (Empire), click again to close it");
        row2.AddChild(_needsBadge);
        _control = new ControlBar();
        row2.AddChild(_control);
        _layers = new MenuButton { Text = "Map layers ▾", Flat = false, FocusMode = Control.FocusModeEnum.None, TooltipText = "What the map shows, with Calm, War and Trade presets" };
        _layers.CustomMinimumSize = new Vector2(0, 32);
        _layers.AddThemeFontSizeOverride("font_size", 15);
        _layers.GetPopup().AddThemeFontSizeOverride("font_size", 14);
        _layers.GetPopup().HideOnCheckableItemSelection = false;
        _layers.AboutToPopup += FillLayersMenu;
        _layers.GetPopup().IdPressed += id => Main.Instance.RunGuarded("map layers", () => PickLayer((int)id));
        row2.AddChild(_layers);
        _intelButton = SmallButton("Intel", () => _screen.ShowIntel = !_screen.ShowIntel, "What you know about the rival, and how sure you are (I)");
        row2.AddChild(_intelButton);
        _progressButton = SmallButton("How am I doing?", () => _screen.ShowProgress = !_screen.ShowProgress, "Control share over time, Industry, fleet strength, research and losses (N)");
        row2.AddChild(_progressButton);
        row2.AddChild(SmallButton("Keys (F1)", () => _screen.ShowHelp = !_screen.ShowHelp, "Every key, grouped (F1)"));
        AddChild(row2);
        _row2 = row2;

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

        var help = Ui.Label("Wheel zoom · WASD pan · click select · right-click route or attack · Space pause · F1 every key", 12, Ui.Dim);
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
        _battle.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop, Control.LayoutPresetMode.KeepSize, 100);
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

        _shipCard = new ShipCard(_screen) { Visible = false };
        AddChild(_shipCard);
        _progress = new ProgressScreen(_screen) { Visible = false };
        AddChild(_progress);
        _intel = new IntelScreen(_screen) { Visible = false };
        AddChild(_intel);
        _keyHelp = new KeyHelp(_screen) { Visible = false };
        AddChild(_keyHelp);

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
        _date.Text = _screen.InBattle ? GameTime.Format(sim.World.Hour) : GameTime.Format(sim.World.Tick);
        _battle.Text = (_screen.InBattle ? _screen.BattleLine() : "")
                       + (_screen.RevealAll ? (_screen.InBattle ? "\n" : "") + "DEBUG: showing every fleet (F7)" : "");
        _reportPanel.Visible = _screen.Report != null;
        if (_screen.Report != null) _reportPanel.Refresh(_screen.Report);
        _replayPanel.Visible = _screen.Replay != null;
        if (_screen.Replay != null) _replayPanel.Open(_screen.Replay);
        _pause.Modulate = _screen.Paused ? Ui.Rival : Colors.White;
        _pauseOnContact.Modulate = _screen.PauseOnContact ? Ui.Player : new Color(1, 1, 1, 0.6f);
        _silence.Visible = sim.World.Light.On;
        var silent = Economy.Of(sim.World, Side.Player).RadioSilence;
        _silence.Text = silent ? $"RADIO SILENCE ({sim.World.Light.Held.Count(t => t.Side == Side.Player)} held)" : "Radio silence";
        _silence.Modulate = silent ? Ui.Danger : new Color(1, 1, 1, 0.6f);
        for (var i = 0; i < _speedButtons.Count; i++)
            _speedButtons[i].Modulate = !_screen.Paused && i == _screen.SpeedIndex ? Ui.Player : new Color(1, 1, 1, 0.75f);
        var rival = sim.World.Minds.FirstOrDefault(m => m.Side == Side.Rival);
        // Second line: the rival's difficulty, then PAUSED (the first line is long enough to reach the menus).
        var second = string.Join(" · ", new[] { rival != null ? $"{rival.Difficulty} rival" : null, _screen.Paused ? "PAUSED" : null }.Where(x => x != null));
        _status.Text = $"seed {sim.World.Seed} · build {Main.Instance.Build}"
                       + (sim.RuleViolations > 0 ? $" · rule failures: {sim.RuleViolations}" : "")
                       + (second.Length > 0 ? "\n" + second : "");
        _scaleBar.QueueRedraw();

        var w = sim.World;
        _empireButton.Visible = w.Loop;
        if (w.Loop)
        {
            var me = Economy.Of(w, Side.Player);
            var held = Economy.Claimable(w).Count(b => b.Owner == Side.Player);
            var attacked = Economy.ShipyardOf(w, Side.Player) is { LastBombardedHour: double h } && w.Hour - h <= 24;
            var tipped = Planets.Tipped(w, Side.Player) ? " TIPPED" : Planets.Tipped(w, Side.Rival) ? " (rival tipped)" : "";
            _empireButton.Text = $"Industry {me.Industry:0} (+{Economy.IncomePerDay(w, Side.Player):0.#}/d) · planets {held}/{Economy.Claimable(w).Count()}"
                                 + $" · {Planets.PopulationOf(w, Side.Player):0}k people{tipped}"
                                 + (attacked ? " · SHIPYARD UNDER ATTACK" : "") + " · Empire ▾";
            RefreshNeeds(w, me);
            _control.Set(w);
            var urgent = me.Needs.Any(n => n.Level == NeedLevel.Urgent);
            _empireButton.Modulate = attacked || urgent ? Ui.Danger : _screen.ShowEmpire ? Ui.Player : Colors.White;
        }
        _row2.Visible = w.Loop;
        _intelButton.Visible = w.Loop;
        _intelButton.Modulate = _screen.ShowIntel ? Ui.Player : Colors.White;
        _progressButton.Modulate = _screen.ShowProgress ? Ui.Player : Colors.White;
        _researchButton.Visible = w.Loop;
        if (w.Loop) { _researchButton.Text = ResearchLine(w); _researchButton.Modulate = _researchButton.Text.Contains("WAITING") ? Ui.Danger : Colors.White; }
        _planetsButton.Visible = w.Loop;
        _research.Visible = w.Loop && _screen.ShowResearch;
        if (_research.Visible) _research.Refresh(sim);
        _colonies.Visible = w.Loop && (_screen.ShowPlanets || _screen.PlanetScreenId != null) && !_research.Visible;
        if (_colonies.Visible) _colonies.Refresh(sim);
        _ground.Visible = w.Loop && _screen.GroundScreenId != null && !_research.Visible;
        if (_ground.Visible) _ground.Refresh(sim);
        if (_ground.Visible) _colonies.Visible = false;
        if (_screen.EmpireTabWanted is EmpirePanel.Tab tab) { _empire.Show(tab); _screen.EmpireTabWanted = null; }
        _empire.Visible = w.Loop && _screen.ShowEmpire && !_research.Visible && !_colonies.Visible && !_ground.Visible;
        if (_empire.Visible) _empire.Refresh(sim);
        var rivalMind = Mind.Of(w, Side.Rival);
        _mindPanel.Visible = _screen.RevealAll && rivalMind != null && !_empire.Visible;
        if (_mindPanel.Visible) _mind.Text = MindText(w, rivalMind!);
        _end.Visible = w.Winner != null && _screen.ShowEnd;
        if (_end.Visible) _end.Refresh(w);

        var fleet = _screen.SelectedFleet;
        _fleetPanel.Visible = fleet != null;
        if (fleet != null) _fleetPanel.Refresh(sim, fleet);
        var group = _screen.Group;
        _groupPanel.Visible = group.Count >= 2;
        if (group.Count >= 2) _groupPanel.Refresh(sim, group);
        _plannerPanel.Visible = _screen.Planner != null;
        if (_screen.Planner != null) _plannerPanel.Refresh(sim, _screen.Planner);
        // On a short screen the planner and the fleet panel would overlap: the planner wins while it is open.
        if (_plannerPanel.Visible && _fleetPanel.Visible && _plannerPanel.GetGlobalRect().End.Y > _fleetPanel.GetGlobalRect().Position.Y)
            _fleetPanel.Visible = false;

        if (_screen.MenuOpen && !_menu.Visible) _menu.Open();
        else if (!_screen.MenuOpen) _menu.Visible = false;

        var engagement = _screen.EngagementNow;
        // A panel you opened yourself (empire, planner, a report or a replay) sits where the engagement
        // panel draws, so while it is open the engagement panel steps aside; it comes back when you close it.
        var fittingFleet = _screen.FittingFleetId is int ff ? w.FindFleet(ff) : null;
        if (fittingFleet == null || fittingFleet.Side != Side.Player) _screen.FittingFleetId = null;
        _fitting.Visible = fittingFleet != null && fittingFleet.Side == Side.Player && !_research.Visible && !_colonies.Visible;
        if (_fitting.Visible) _fitting.Refresh(sim, fittingFleet!);
        var screen = _side.GetViewportRect().Size;
        _side.Visible = _screen.ShowSide && !_research.Visible && !_colonies.Visible && !_ground.Visible && !_empire.Visible && !_fitting.Visible && engagement == null && !_menu.Visible;
        if (_side.Visible) _side.Refresh(sim, Mathf.Max(200, screen.Y - 96 - 110));
        _sideButton.Modulate = _side.Visible ? Ui.Player : new Color(1, 1, 1, 0.75f);
        var leftEdge = _side.Visible ? 16 + SidePanel.Width + 36 : 16;
        _contacts.Position = new Vector2(leftEdge, 124);
        _mindPanel.Position = new Vector2(leftEdge, _mindPanel.Position.Y);
        var covered = _research.Visible || _colonies.Visible || _ground.Visible || _fitting.Visible || _empire.Visible || _plannerPanel.Visible || _reportPanel.Visible || _replayPanel.Visible || _menu.Visible;
        _engagement.Visible = engagement != null && !covered;
        _contacts.Refresh();
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
        _intel.Visible = w.Loop && _screen.ShowIntel && !_menu.Visible && !_progress.Visible;
        if (_intel.Visible) _intel.Refresh(sim);
        _keyHelp.Visible = _screen.ShowHelp && !_menu.Visible;

        // In a full game a selected planet gets the planet screen; the sandbox keeps the short description.
        var planet = fleet == null && group.Count < 2 && engagement == null && w.Loop ? _screen.SelectedBody : null;
        _planetPanel.Visible = planet != null && planet.Kind != BodyKind.Star && !_colonies.Visible && !_ground.Visible && !_research.Visible;
        if (_planetPanel.Visible) _planetPanel.Refresh(sim, planet!);
        var text = fleet == null && group.Count < 2 && engagement == null && !_planetPanel.Visible ? Describe(sim) : null;
        _infoPanel.Visible = text != null;
        if (text != null) _info.Text = text;
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
        lines.AddRange(Intel.BeliefVsTruth(w, Side.Rival).Take(3));
        lines.Add("");
        foreach (var t in m.Thoughts.TakeLast(8).Reverse())
            lines.Add($"{GameTime.Format(t.Hour)}: {t.Text}");
        return string.Join("\n", lines);
    }

    public void TickShip(int shipId) => _fleetPanel.TickOnly(shipId);

    /// <summary>The Needs you badge opens the list, and closes it again when it is already showing.</summary>
    public bool SideVisible => _side.Visible;

    private void ToggleNeeds()
    {
        if (_empire.Visible && _empire.Current == EmpirePanel.Tab.Needs) { _screen.ShowEmpire = false; return; }
        _screen.ShowEmpire = true;
        _empire.Show(EmpirePanel.Tab.Needs);
    }

    /// <summary>The Needs you badge, and a toast once for each new Urgent item.</summary>
    private void RefreshNeeds(World w, SideState me)
    {
        var urgent = me.Needs.Where(n => n.Level == NeedLevel.Urgent).ToList();
        _needsBadge.Text = me.Needs.Count == 0 ? "✓ Nothing needs you" : $"⚠ {me.Needs.Count} need{(me.Needs.Count == 1 ? "s" : "")} you" + (urgent.Count > 0 ? $" ({urgent.Count} urgent)" : "");
        _needsBadge.Modulate = urgent.Count > 0 ? Ui.Danger : me.Needs.Count > 0 ? new Color("ffd34d") : new Color(1, 1, 1, 0.6f);
        _needsBadge.TooltipText = me.Needs.Count == 0 ? "Nothing waits for you" : string.Join("\n", me.Needs.OrderBy(n => n.Level).Take(6).Select(n => $"{n.Level}: {n.Text}"))
                                                                               + "\nClick for the Needs you list";
        foreach (var n in urgent)
        {
            if (!_toasted.Add(n.Key) || !_needsPrimed) continue;
            var need = n;
            Main.Instance.Toast(n.Text, onClick: () => JumpTo(need));
        }
        _needsPrimed = true; // items already there when the game opens don't pop up
        _toasted.IntersectWith(me.Needs.Select(n => n.Key)); // an item that clears and comes back pops up again
    }

    private void JumpTo(NeedItem n)
    {
        if (n.FleetId is int f && Main.Instance.Sim?.World.FindFleet(f) != null) _screen.PickFleet(f);
        else if (n.BodyId is int b) _screen.PickBody(b);
        else { _screen.ShowEmpire = true; _empire.Show(EmpirePanel.Tab.Needs); }
    }

    private const int PresetCalm = 1000, PresetWar = 1001, PresetTrade = 1002, PresetDefault = 1003;

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
        }
        popup.AddSeparator("Presets");
        popup.AddItem("Calm: orbits and labels", PresetCalm);
        popup.AddItem("War: sight, lag, old sightings, lanes", PresetWar);
        popup.AddItem("Trade: lanes and supply runs", PresetTrade);
        popup.AddItem("Everything as it was", PresetDefault);
    }

    private void PickLayer(int id)
    {
        var layers = id switch
        {
            PresetCalm => PlayerInfo.Calm,
            PresetWar => PlayerInfo.War,
            PresetTrade => PlayerInfo.Trade,
            PresetDefault => PlayerInfo.Default,
            _ => PlayerInfo.Layers ^ (MapLayer)id,
        };
        PlayerInfo.SetLayers(layers);
        // The sight view follows the Sight shadows layer.
        if (PlayerInfo.Has(MapLayer.Shadows) && _screen.Sight == MapView.SightView.Off) _screen.Sight = MapView.SightView.Shadows;
        if (!PlayerInfo.Has(MapLayer.Shadows)) _screen.Sight = MapView.SightView.Off;
        if (id < PresetCalm) FillLayersMenu();
    }

    /// <summary>The fleet list: your fleets with their ships, then the rival fleets.</summary>
    private void FillNewsMenu()
    {
        var popup = _news.GetPopup();
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
            else if (b.Shipyard && b.Owner == Side.Rival && Economy.BombardStatus(w, b, Side.Player) is string attack)
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
