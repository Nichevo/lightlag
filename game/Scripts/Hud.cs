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
    private PlannerPanel _plannerPanel = null!;
    private ReportPanel _reportPanel = null!;
    private Button _battle = null!;
    private MenuButton _fleets = null!;
    private readonly List<Action> _fleetMenuActions = new();
    private MenuButton _bodies = null!;
    private MenuButton _news = null!;
    private readonly List<Action> _newsActions = new();
    private readonly List<Action> _bodyMenuActions = new();
    private GroupPanel _groupPanel = null!;
    private Button _pauseOnContact = null!;
    private Button _empireButton = null!;
    private EmpirePanel _empire = null!;
    private EndPanel _end = null!;
    private EngagementPanel _engagement = null!;
    private ContactsPanel _contacts = null!;
    private PanelContainer _mindPanel = null!;
    private Label _mind = null!;

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
        _fleets = new MenuButton { Text = "Fleets ▾", TooltipText = "Pick a fleet or a ship (Tab: next fleet)", FocusMode = Control.FocusModeEnum.None, Flat = false };
        _fleets.CustomMinimumSize = new Vector2(80, 32);
        _fleets.AddThemeFontSizeOverride("font_size", 15);
        var popup = _fleets.GetPopup();
        popup.AddThemeFontSizeOverride("font_size", 15);
        _fleets.AboutToPopup += FillFleetMenu;
        popup.IdPressed += id => { if (id >= 0 && id < _fleetMenuActions.Count) Main.Instance.RunGuarded("fleet list", _fleetMenuActions[(int)id]); };
        bar.AddChild(_fleets);
        _bodies = new MenuButton { Text = "Planets ▾", TooltipText = "Go to a planet or moon and follow it (pan the map to stop following)", FocusMode = Control.FocusModeEnum.None, Flat = false };
        _bodies.CustomMinimumSize = new Vector2(90, 32);
        _bodies.AddThemeFontSizeOverride("font_size", 15);
        var bodyPopup = _bodies.GetPopup();
        bodyPopup.AddThemeFontSizeOverride("font_size", 15);
        _bodies.AboutToPopup += FillBodyMenu;
        bodyPopup.IdPressed += id => { if (id >= 0 && id < _bodyMenuActions.Count) Main.Instance.RunGuarded("planet list", _bodyMenuActions[(int)id]); };
        bar.AddChild(_bodies);
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
        _empireButton = SmallButton("Empire", () => _screen.ShowEmpire = !_screen.ShowEmpire, "Industry, shipyard, research and claims (E)");
        bar.AddChild(_empireButton);
        AddChild(bar);

        _empire = new EmpirePanel(_screen);
        _empire.Position = new Vector2(16, 56);
        AddChild(_empire);

        _status = Ui.Label("", 13, Ui.Dim);
        _status.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight, Control.LayoutPresetMode.KeepSize, 16);
        _status.GrowHorizontal = Control.GrowDirection.Begin;
        _status.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(_status);

        _scaleBar = new ScaleBar { Camera = _screen.Camera };
        _scaleBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft, Control.LayoutPresetMode.KeepSize, 16);
        _scaleBar.Position += new Vector2(0, -60);
        AddChild(_scaleBar);

        var help = Ui.Label("Wheel zoom · WASD, arrows or right-drag pan · click select · drag a box or Shift+click to pick several · Tab next fleet · double-click follow\n"
            + "Right-click route or attack (Shift: go now) · Enter go · O orbit · G drift · C change route · X stop · M merge or gather · E empire · R report · P pause on contact · B sight view · Z zoom in · Space pause · 1-4 speed · Home · F8", 12, Ui.Dim);
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
        _contacts = new ContactsPanel(_screen) { Position = new Vector2(16, 84) };
        AddChild(_contacts);

        _engagement = new EngagementPanel(_screen);
        AddChild(_engagement);

        _end = new EndPanel(_screen);
        _end.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center, Control.LayoutPresetMode.KeepSize);
        _end.GrowHorizontal = Control.GrowDirection.Both;
        _end.GrowVertical = Control.GrowDirection.Both;
        AddChild(_end);
        _ = main;
    }

    private static Button SmallButton(string text, Action pressed, string tip) => Ui.SmallButton(text, pressed, tip);

    public void Refresh()
    {
        var sim = Main.Instance.Sim;
        if (sim == null) return;
        _date.Text = _screen.InBattle ? GameTime.Format(sim.World.Hour) : GameTime.Format(sim.World.Tick);
        _battle.Text = (_screen.InBattle ? _screen.BattleLine() : "")
                       + (_screen.RevealAll ? (_screen.InBattle ? "\n" : "") + "DEBUG: showing every fleet (F7)" : "");
        _reportPanel.Visible = _screen.Report != null;
        if (_screen.Report != null) _reportPanel.Refresh(_screen.Report);
        _pause.Modulate = _screen.Paused ? Ui.Rival : Colors.White;
        _pauseOnContact.Modulate = _screen.PauseOnContact ? Ui.Player : new Color(1, 1, 1, 0.6f);
        for (var i = 0; i < _speedButtons.Count; i++)
            _speedButtons[i].Modulate = !_screen.Paused && i == _screen.SpeedIndex ? Ui.Player : new Color(1, 1, 1, 0.75f);
        var rival = sim.World.Minds.FirstOrDefault(m => m.Side == Side.Rival);
        _status.Text = (rival != null ? $"{rival.Difficulty} rival · " : "") + $"seed {sim.World.Seed} · build {Main.Instance.Build}"
                       + (sim.RuleViolations > 0 ? $" · rule failures: {sim.RuleViolations}" : "")
                       + (_screen.Paused ? "\nPAUSED" : "");
        _scaleBar.QueueRedraw();

        var w = sim.World;
        _empireButton.Visible = w.Loop;
        if (w.Loop)
        {
            var me = Economy.Of(w, Side.Player);
            var held = Economy.Claimable(w).Count(b => b.Owner == Side.Player);
            var attacked = Economy.ShipyardOf(w, Side.Player) is { LastBombardedHour: double h } && w.Hour - h <= 24;
            _empireButton.Text = $"Industry {me.Industry:0} (+{Economy.IncomePerDay(w, Side.Player):0.#}/day) · planets {held}/{Economy.Claimable(w).Count()}"
                                 + (attacked ? " · SHIPYARD UNDER ATTACK" : "") + " · Empire ▾";
            _empireButton.Modulate = attacked ? Ui.Danger : _screen.ShowEmpire ? Ui.Player : Colors.White;
        }
        _empire.Visible = w.Loop && _screen.ShowEmpire;
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

        var engagement = _screen.EngagementNow;
        _engagement.Visible = engagement != null;
        _contacts.Refresh();
        if (engagement != null || _empire.Visible || _mindPanel.Visible) _contacts.Visible = false;
        if (engagement != null)
        {
            _engagement.Refresh(sim, engagement);
            _fleetPanel.Visible = false;
            _groupPanel.Visible = false;
            _mindPanel.Visible = false;
            _battle.Visible = false;
        }
        else _battle.Visible = true;

        var text = fleet == null && group.Count < 2 && engagement == null ? Describe(sim) : null;
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
        foreach (var t in m.Thoughts.TakeLast(8).Reverse())
            lines.Add($"{GameTime.Format(t.Hour)}: {t.Text}");
        return string.Join("\n", lines);
    }

    public void TickShip(int shipId) => _fleetPanel.TickOnly(shipId);

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

    private void FillFleetMenu()
    {
        var sim = Main.Instance.Sim;
        var popup = _fleets.GetPopup();
        popup.Clear();
        _fleetMenuActions.Clear();
        if (sim == null) return;
        var w = sim.World;
        void Item(string text, Action act)
        {
            popup.AddItem(text, _fleetMenuActions.Count);
            _fleetMenuActions.Add(act);
        }
        var mine = w.Fleets.Where(f => f.Side == Side.Player).ToList();
        if (mine.Count == 0) popup.AddSeparator("No fleets");
        foreach (var f in mine)
        {
            var id = f.Id;
            Item($"{f.Name} ({f.ShipIds.Count}) · {FleetPanel.Doing(w, f)}", () => _screen.PickFleet(id));
            foreach (var s in w.ShipsOf(f))
            {
                var shipId = s.Id;
                Item($"      {s.Name}  {GameData.ShipClass(s.ClassId).Name}", () => _screen.PickShip(shipId));
            }
        }
        var rivals = w.Sightings.Where(s => s.SeenBy == Side.Player && w.FindFleet(s.FleetId) != null).OrderByDescending(s => s.Visible).ToList();
        if (rivals.Count > 0) popup.AddSeparator("Rival fleets you know of");
        foreach (var s in rivals)
        {
            var id = s.FleetId;
            var when = s.Visible ? "in sight" : $"last seen {Format.Hours(w.Hour - s.Hour)} ago";
            Item($"{s.Name} ({s.Ships}) · {when}", () => _screen.Select(id));
        }
    }

    /// <summary>Open the planet list (for screenshots); false closes it.</summary>
    public void ShowBodyMenu(bool open)
    {
        if (!open) { _bodies.GetPopup().Hide(); return; }
        FillBodyMenu();
        var popup = _bodies.GetPopup();
        popup.Position = (Vector2I)(_bodies.GlobalPosition + new Vector2(0, _bodies.Size.Y));
        popup.Popup();
    }

    /// <summary>The planet list: the star, then each planet outward from it with its moons under it.</summary>
    private void FillBodyMenu()
    {
        var sim = Main.Instance.Sim;
        var popup = _bodies.GetPopup();
        popup.Clear();
        _bodyMenuActions.Clear();
        if (sim == null) return;
        var w = sim.World;
        void Item(Body b, string indent)
        {
            var id = b.Id;
            var tags = new List<string> { Format.Title(b.Kind.ToString()) };
            if (b.Owner == Side.Player) tags.Add("yours");
            else if (b.Owner == Side.Rival) tags.Add("rival");
            if (b.Shipyard) tags.Add("shipyard");
            if (b.ClaimSide == Side.Player) tags.Add("you are claiming it");
            if (w.Loop && b.Industry <= 0 && b.Kind != BodyKind.Star) tags.Add("can't be claimed");
            else if (w.Loop && b.Industry > 0) tags.Add($"industry {b.Industry:0}");
            popup.AddItem($"{indent}{b.Name} · {string.Join(", ", tags)}", _bodyMenuActions.Count);
            _bodyMenuActions.Add(() => _screen.PickBody(id));
        }
        void Tree(Body b, string indent)
        {
            Item(b, indent);
            foreach (var child in w.Bodies.Where(c => c.ParentId == b.Id).OrderBy(c => c.OrbitRadius))
                Tree(child, indent + "      ");
        }
        foreach (var root in w.Bodies.Where(b => b.ParentId == null || w.FindBody(b.ParentId.Value) == null))
            Tree(root, "");
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
            else if (b.Shipyard && b.Owner == Side.Rival)
                lines.Add("Park an armed fleet in orbit with no defenders near to wear the shipyard down");
            if (_screen.SelectedFleet == null && sim.World.Fleets.Any(f => f.Side == Side.Player)) lines.Add("Select a fleet, then right-click here to fly");
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
