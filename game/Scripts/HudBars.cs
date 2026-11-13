using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The top bar, in two rows. Row one floats over the map: the time box on the left (date, pause, speeds and
/// the next auto-pause), Needs you and the control share in the centre, map tools on the right (System, zoom,
/// Map layers, the list and ☰ for rarer view toggles). Row two is a solid menu bar of the eight screens, centred,
/// each with its icon, key and Needs you badge; Battle view and the game menu sit at its right end.
/// Display options: the menu bar always shown, hidden when idle, or off; icons on or off (off below ~1300 px).
/// </summary>
public partial class Hud
{
    /// <summary>Where the space under the top bar starts (full screens and panels open below it).</summary>
    public static float ContentTop { get; private set; } = 108;

    private PanelContainer _timeBox = null!;
    private Label _nextPause = null!;
    private HBoxContainer _row1Center = null!, _row1Right = null!;
    private ColorRect _row1Back = null!;
    private PanelContainer _menuBar = null!;
    private HBoxContainer _menuTabs = null!, _menuRight = null!;
    private MenuButton _viewMenu = null!, _gameMenu = null!;
    private Button _listButton = null!;
    private readonly List<MenuTab> _tabs = new();
    private double _menuSlide; // 0 shown, 1 slid away
    private ulong _menuActiveMs;
    private ulong _lastBarsMs;
    private const float Row1Y = 8, Row2Gap = 6;

    /// <summary>The menu bar's screens: id, name, key, what it holds.</summary>
    private static readonly (string Id, string Name, string Key, string Tip)[] Screens =
    {
        ("empire", "Empire", "E", "Industry, shipyard, stations, claims, war and automation"),
        ("planets", "Planets", "Y", "Your planets and every other world: people, goods, status"),
        ("fleets", "Fleets", "K", "Every fleet: roles, ships, supplies, distances and calls for help"),
        ("ships", "Ships", "J", "Ship designs, models in service, crewing and the class tree"),
        ("supply", "Supply", "G", "Where goods come from and go, routes and their risk (G with no fleet selected)"),
        ("research", "Research", "T", "Research teams, ideas and the discovery web"),
        ("intel", "Intel", "I", "What you know about each rival, and how sure you are"),
        ("progress", "Progress", "N", "Control share over time, Industry, fleet strength, research and losses"),
    };

    private void BuildBars()
    {
        // Row one, left: the time box.
        var time = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        time.AddThemeConstantOverride("separation", 0);
        var speeds = new HBoxContainer();
        speeds.AddThemeConstantOverride("separation", 4);
        _date = Ui.Label("", 18);
        _date.CustomMinimumSize = new Vector2(170, 0);
        _date.VerticalAlignment = VerticalAlignment.Center;
        speeds.AddChild(_date);
        _pause = SmallButton("❚❚", () => _screen.TogglePause(), "Pause (Space)");
        speeds.AddChild(_pause);
        for (var i = 0; i < GameScreen.Speeds.Length; i++)
        {
            var index = i;
            var b = SmallButton(GameScreen.SpeedNames[i], () => _screen.SetSpeed(index), $"Speed {GameScreen.SpeedNames[i]} (key {i + 1})");
            _speedButtons.Add(b);
            speeds.AddChild(b);
        }
        time.AddChild(speeds);
        _nextPause = Ui.Label("", 12, Ui.Dim);
        _nextPause.TooltipText = "The next thing set to stop the game (P toggles pausing on contact; ☰ Game › Pause settings for the rest)";
        _nextPause.MouseFilter = Control.MouseFilterEnum.Pass;
        time.AddChild(_nextPause);
        _timeBox = Ui.Panel(time, 6);
        _timeBox.Position = new Vector2(16, Row1Y);
        _timeBox.MouseFilter = Control.MouseFilterEnum.Pass;

        // Row one, centre: Needs you and the control share.
        _row1Center = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _row1Center.AddThemeConstantOverride("separation", 10);
        _needsBadge = SmallButton("", ToggleNeeds, "What wants your look: click for the Needs you tray");
        _row1Center.AddChild(_needsBadge);
        _control = new ControlBar();
        _row1Center.AddChild(_control);

        // Row one, right: looking at the map.
        _row1Right = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _row1Right.AddThemeConstantOverride("separation", 4);
        _row1Right.AddChild(SmallButton("⌂ System", () => _screen.ShowWholeSystem(), "Show the whole system (Home or H)"));
        _row1Right.AddChild(SmallButton("Zoom", () => _screen.ZoomToSelected(), "Zoom right in on what is selected and follow it (Z)"));
        _row1Right.AddChild(SmallButton("+", () => _screen.Camera.SetScale(_screen.Camera.TargetScale * 1.6), "Zoom in (mouse wheel)"));
        _row1Right.AddChild(SmallButton("−", () => _screen.Camera.SetScale(_screen.Camera.TargetScale / 1.6), "Zoom out (mouse wheel)"));
        _layers = new MenuButton { Text = "Map layers ▾", Flat = false, FocusMode = Control.FocusModeEnum.None, TooltipText = "What the map shows, with Calm, War and Trade presets; also the Needs you pins" };
        _layers.CustomMinimumSize = new Vector2(0, 32);
        _layers.AddThemeFontSizeOverride("font_size", 15);
        _layers.GetPopup().AddThemeFontSizeOverride("font_size", 14);
        _layers.GetPopup().HideOnCheckableItemSelection = false;
        _layers.AboutToPopup += FillLayersMenu;
        _layers.GetPopup().IdPressed += id => Main.Instance.RunGuarded("map layers", () => PickLayer((int)id));
        _row1Right.AddChild(_layers);
        _listButton = SmallButton("List L", () => _screen.ToggleSide(), "The list of your fleets, the rival fleets you know of and the planets (L). Tab: next fleet");
        _row1Right.AddChild(_listButton);
        _viewMenu = new MenuButton { Text = "☰", Flat = false, FocusMode = Control.FocusModeEnum.None, TooltipText = "News, sight view, keys and the game menu" };
        _viewMenu.CustomMinimumSize = new Vector2(40, 32);
        _viewMenu.AddThemeFontSizeOverride("font_size", 15);
        _viewMenu.GetPopup().AddThemeFontSizeOverride("font_size", 14);
        _news = new PopupMenu();
        _news.AddThemeFontSizeOverride("font_size", 14);
        _news.IdPressed += id => { if (id >= 0 && id < _newsActions.Count) Main.Instance.RunGuarded("news", _newsActions[(int)id]); };
        _viewMenu.AboutToPopup += FillViewMenu;
        _viewMenu.GetPopup().IdPressed += id => Main.Instance.RunGuarded("view menu", () => PickView((int)id));
        _row1Right.AddChild(_viewMenu);

        // A dark strip behind row one while a full screen is open below it.
        _row1Back = new ColorRect { Color = new Color(0.02f, 0.03f, 0.045f, 0.97f), MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        AddChild(_row1Back);
        AddChild(_timeBox);
        AddChild(_row1Center);
        AddChild(_row1Right);

        // Row two: the menu bar.
        var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.07f, 0.11f, 0.97f), BorderColor = new Color(Ui.Player, 0.25f) };
        style.BorderWidthTop = 1;
        style.BorderWidthBottom = 1;
        style.SetContentMarginAll(3);
        _menuBar = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
        _menuBar.AddThemeStyleboxOverride("panel", style);
        var inner = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, CustomMinimumSize = new Vector2(0, 36) };
        _menuBar.AddChild(inner);
        _menuTabs = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _menuTabs.AddThemeConstantOverride("separation", 2);
        inner.AddChild(_menuTabs);
        foreach (var (id, name, key, tip) in Screens)
        {
            var screenId = id;
            var tab = new MenuTab(id, name, key, $"{name} ({key}): {tip}", () => OpenScreen(screenId));
            _tabs.Add(tab);
            _menuTabs.AddChild(tab);
        }
        _menuRight = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        _menuRight.AddThemeConstantOverride("separation", 4);
        _battleView = SmallButton("Battle view  U", () => _screen.BattleView = !_screen.BattleView,
            "Clear the screen to each ship's state in the fight: hull, weapons, engines, defence, crew, heat, with Eject heat sink buttons (U)");
        _menuRight.AddChild(_battleView);
        _gameMenu = new MenuButton { Text = "☰ Game  Esc", Flat = false, FocusMode = Control.FocusModeEnum.None, TooltipText = "Pause rules, radio silence, saving and loading, display options (Esc opens the full menu)" };
        _gameMenu.CustomMinimumSize = new Vector2(0, 32);
        _gameMenu.AddThemeFontSizeOverride("font_size", 15);
        _gameMenu.GetPopup().AddThemeFontSizeOverride("font_size", 14);
        _gameMenu.AboutToPopup += FillGameMenu;
        _gameMenu.GetPopup().IdPressed += id => Main.Instance.RunGuarded("game menu", () => PickGame((int)id));
        _menuRight.AddChild(_gameMenu);
        inner.AddChild(_menuRight);
        AddChild(_menuBar);
    }

    /// <summary>A screen key or tab: open that screen (or close it when it is the one showing).</summary>
    public void OpenScreen(string id)
    {
        _menuActiveMs = Time.GetTicksMsec();
        _menuSlide = 0;
        switch (id)
        {
            case "empire":
                var onEmpire = _screen.ShowEmpire && _empire.Current != EmpirePanel.Tab.Supply;
                _screen.ShowShips = false;
                _screen.ShowEmpire = !onEmpire;
                if (!onEmpire && _empire.Current == EmpirePanel.Tab.Supply) _screen.EmpireTabWanted = EmpirePanel.Tab.Overview;
                break;
            case "supply":
                var open = _screen.ShowEmpire && _empire.Current == EmpirePanel.Tab.Supply;
                _screen.ShowShips = false;
                _screen.ShowEmpire = !open;
                if (!open) _screen.EmpireTabWanted = EmpirePanel.Tab.Supply;
                break;
            case "planets": _screen.ShowShips = false; _screen.TogglePlanets(); break;
            case "fleets": _screen.ShowShips = false; _screen.ToggleFleets(); break;
            case "ships": _screen.ToggleShips(); break;
            case "research": _screen.ShowResearch = !_screen.ShowResearch; break;
            case "intel": _screen.ShowIntel = !_screen.ShowIntel; break;
            case "progress": _screen.ShowProgress = !_screen.ShowProgress; break;
        }
    }

    /// <summary>Which screen is showing (for the highlighted tab).</summary>
    private string? OpenScreenId() =>
        _research.Visible ? "research" : _colonies.Visible ? "planets" : _fleets.Visible ? "fleets" : _ships.Visible ? "ships"
        : _empire.Visible ? (_empire.Current == EmpirePanel.Tab.Supply ? "supply" : "empire")
        : _intel.Visible ? "intel" : _progress.Visible ? "progress" : null;

    /// <summary>Full screens cover the map under the bars; row one then gets a solid strip behind it.</summary>
    private bool FullScreenOpen => _research.Visible || _colonies.Visible || _fleets.Visible || _ships.Visible || _ground.Visible;

    /// <summary>The Needs you items each screen's tab counts: supply chain problems on Supply, the rest by NeedScreens.</summary>
    private static string ScreenOf(World w, NeedItem n) => n.Key.StartsWith("chain:", StringComparison.Ordinal) ? "supply" : NeedScreens.For(w, n);

    /// <summary>Row one, the menu bar and their places; called from Refresh.</summary>
    private void RefreshBars(World w)
    {
        var now = Time.GetTicksMsec();
        var dt = _lastBarsMs == 0 ? 0 : Math.Min(0.1, (now - _lastBarsMs) / 1000.0);
        _lastBarsMs = now;
        var view = GetViewport().GetVisibleRect().Size;
        var battleView = _screen.BattleView;

        // The time box: what will stop the game next.
        _pause.Text = _screen.Paused ? "▶" : "❚❚";
        _pause.TooltipText = _screen.Paused ? "Paused: Space or click to run" : "Pause (Space)";
        _nextPause.Text = NextPauseText(w);
        _nextPause.Visible = _nextPause.Text.Length > 0;

        // Row one: centre and right.
        _row1Center.Visible = w.Loop;
        var centerSize = _row1Center.GetCombinedMinimumSize();
        _row1Center.Size = centerSize;
        _row1Center.Position = new Vector2((view.X - centerSize.X) / 2, Row1Y + 4);
        var rightSize = _row1Right.GetCombinedMinimumSize();
        _row1Right.Size = rightSize;
        _row1Right.Position = new Vector2(view.X - rightSize.X - 16, Row1Y + 4);
        // Narrow windows: the centre drops under the time box rather than overlap it.
        var timeRight = _timeBox.Position.X + _timeBox.Size.X + 12;
        if (_row1Center.Position.X < timeRight || _row1Center.Position.X + centerSize.X > _row1Right.Position.X - 12)
            _row1Center.Position = new Vector2(Mathf.Max(timeRight, _row1Right.Position.X - 12 - centerSize.X), Row1Y + 4);
        _listButton.Modulate = _side.Visible ? Ui.Player : new Color(1, 1, 1, 0.75f);
        var row1Bottom = Row1Y + Mathf.Max(_timeBox.Size.Y, 40);

        // Row two: the menu bar.
        var mode = PlayerInfo.MenuBar;
        var icons = PlayerInfo.MenuIcons && view.X >= 1300;
        var open = OpenScreenId();
        var groups = Economy.Of(w, Side.Player).Needs.GroupBy(n => ScreenOf(w, n)).ToDictionary(g => g.Key, g => g.ToList());
        var me = Economy.Of(w, Side.Player);
        foreach (var tab in _tabs)
        {
            tab.Visible = !battleView && (w.Loop || tab.Id == "fleets");
            var list = groups.GetValueOrDefault(tab.Id);
            NeedLevel? worst = list is { Count: > 0 } ? list.Min(n => n.Level) : null;
            var tip = list is { Count: > 0 } ? "\n\nNeeds you here:\n" + string.Join("\n", list.OrderBy(n => n.Level).Take(5).Select(n => $"{n.Level}: {n.Text}")) : "";
            var extra = "";
            if (tab.Id == "empire" && w.Loop)
            {
                var held = Economy.Claimable(w).Count(b => b.Owner == Side.Player);
                var attacked = Economy.ShipyardOf(w, Side.Player) is { LastBombardedHour: double h } && w.Hour - h <= 24;
                extra = $"\n\nIndustry {me.Industry:0} (+{Economy.IncomePerDay(w, Side.Player):0.#}/d) · planets {held}/{Economy.Claimable(w).Count()} · {Planets.PopulationOf(w, Side.Player):0}k people"
                        + (Planets.Tipped(w, Side.Player) ? " · TIPPED" : "") + (attacked ? "\nSHIPYARD UNDER ATTACK" : "");
                if (attacked) worst = NeedLevel.Urgent;
            }
            if (tab.Id == "research" && w.Loop)
            {
                var line = ResearchLine(w);
                extra = "\n\n" + line.TrimEnd(' ', '▸');
                if (line.Contains("WAITING") && worst is null or > NeedLevel.Soon) worst = NeedLevel.Soon;
            }
            tab.Set(open == tab.Id, worst, list?.Count ?? 0, icons, tip + extra);
        }
        _battleView.Visible = true;
        _battleView.Text = battleView ? "Menus back  U" : "Battle view  U";
        _battleView.Modulate = battleView ? Ui.Player : _screen.InBattle ? Ui.Danger : new Color(1, 1, 1, 0.8f);
        _gameMenu.Visible = !battleView;

        // Hide when idle: slides away after 5 s; the mouse at the bottom edge of row one, or any screen open, brings it back.
        var mouse = GetViewport().GetMousePosition();
        var barHeight = _menuBar.GetCombinedMinimumSize().Y;
        if (mode != MenuBarMode.HideWhenIdle || open != null || battleView || mouse.Y <= row1Bottom + Row2Gap + barHeight + 4 && mouse.Y >= 0)
            _menuActiveMs = now;
        var hide = mode == MenuBarMode.HideWhenIdle && now - _menuActiveMs > 5000;
        _menuSlide = Mathf.MoveToward((float)_menuSlide, hide ? 1 : 0, (float)(dt * 5));
        _menuBar.Visible = mode != MenuBarMode.Off && _menuSlide < 0.999 || battleView;
        var barY = row1Bottom + Row2Gap - (float)_menuSlide * (barHeight + row1Bottom);
        _menuBar.Position = new Vector2(0, barY);
        _menuBar.Size = new Vector2(view.X, barHeight);
        _menuBar.Modulate = new Color(1, 1, 1, 1 - (float)_menuSlide);
        var tabsSize = _menuTabs.GetCombinedMinimumSize();
        var rightEnd = _menuRight.GetCombinedMinimumSize();
        _menuRight.Size = rightEnd;
        _menuRight.Position = new Vector2(view.X - rightEnd.X - 16, (barHeight - 6 - rightEnd.Y) / 2);
        _menuTabs.Size = tabsSize;
        var tabsX = (view.X - tabsSize.X) / 2;
        // Never under the right-hand buttons: shift left (then from the left edge) when the window is narrow.
        if (tabsX + tabsSize.X > _menuRight.Position.X - 8) tabsX = Mathf.Max(8, _menuRight.Position.X - 8 - tabsSize.X);
        _menuTabs.Position = new Vector2(tabsX, (barHeight - 6 - tabsSize.Y) / 2);

        ContentTop = _menuBar.Visible && _menuSlide < 0.5 ? barY + barHeight + 4 : row1Bottom + 4;
        _row1Back.Visible = FullScreenOpen;
        _row1Back.Position = Vector2.Zero;
        _row1Back.Size = new Vector2(view.X, ContentTop);
    }

    /// <summary>"pauses on contact in 3 d": the soonest coming contact that will stop the game.</summary>
    private string NextPauseText(World w)
    {
        if (!_screen.PauseOnContact) return "won't pause on contact (P)";
        var next = _screen.Engagements.Where(p => p.ContactHours is > 0 && !_screen.QuietEnemies.Contains(p.Enemy)).OrderBy(p => p.ContactHours).FirstOrDefault();
        return next?.ContactHours is double h ? $"pauses on contact in {Format.Hours(h)}" : "";
    }

    private const int ViewKeys = 1, ViewSight = 2, ViewNews = 3, ViewMenu = 4, ViewDetail = 5;

    private void FillViewMenu()
    {
        var popup = _viewMenu.GetPopup();
        popup.Clear();
        FillNewsMenu();
        if (_news.GetParent() == null) popup.AddChild(_news);
        popup.AddSubmenuNodeItem("Recent news", _news, ViewNews);
        popup.AddItem($"Sight view: {_screen.Sight} (B cycles)", ViewSight);
        popup.AddItem("Every key (F1)", ViewKeys);
        popup.AddItem("Display options…", ViewDetail);
        popup.AddItem("Game menu (Esc)", ViewMenu);
    }

    private void PickView(int id)
    {
        switch (id)
        {
            case ViewKeys: _screen.ShowHelp = !_screen.ShowHelp; break;
            case ViewSight:
                _screen.Sight = _screen.Sight switch
                {
                    MapView.SightView.Shadows => MapView.SightView.Coverage,
                    MapView.SightView.Coverage => MapView.SightView.Off,
                    _ => MapView.SightView.Shadows,
                };
                if ((_screen.Sight != MapView.SightView.Off) != PlayerInfo.Has(MapLayer.Shadows)) PlayerInfo.Toggle(MapLayer.Shadows);
                break;
            case ViewDetail: _screen.OpenMenu("display"); break;
            case ViewMenu: _screen.OpenMenu(); break;
        }
    }

    private const int GamePauseContact = 1, GamePauses = 2, GameSilence = 3, GameSave = 4, GameLoad = 5, GameDisplay = 6, GameMenuFull = 7;

    private void FillGameMenu()
    {
        var popup = _gameMenu.GetPopup();
        popup.Clear();
        var w = Main.Instance.Sim?.World;
        popup.AddCheckItem("Pause on contact (P)", GamePauseContact);
        popup.SetItemChecked(popup.ItemCount - 1, _screen.PauseOnContact);
        popup.AddItem("Pause settings…", GamePauses);
        if (w is { Light.On: true })
        {
            var silent = Economy.Of(w, Side.Player).RadioSilence;
            popup.AddCheckItem(silent ? $"Radio silence ({w.Light.Held.Count(t => t.Side == Side.Player)} orders held)" : "Radio silence", GameSilence);
            popup.SetItemChecked(popup.ItemCount - 1, silent);
            popup.SetItemTooltip(popup.ItemCount - 1, "No orders leave your flagship (they wait until you lift it), so the rival can't follow your signal traffic to find it. Fleets in command range still get orders");
        }
        popup.AddSeparator();
        popup.AddItem("Save game", GameSave);
        if (w?.Rules.Ironman != true) popup.AddItem("Load game…", GameLoad);
        popup.AddItem("Display options…", GameDisplay);
        popup.AddItem("Game menu (Esc)", GameMenuFull);
    }

    private void PickGame(int id)
    {
        switch (id)
        {
            case GamePauseContact: _screen.PauseOnContact = !_screen.PauseOnContact; break;
            case GamePauses: _screen.OpenMenu("pause"); break;
            case GameSilence: _screen.ToggleRadioSilence(); break;
            case GameSave: _screen.SaveManual(); break;
            case GameLoad: _screen.OpenMenu("load"); break;
            case GameDisplay: _screen.OpenMenu("display"); break;
            case GameMenuFull: _screen.OpenMenu(); break;
        }
    }
}

/// <summary>
/// One screen in the menu bar: its icon (drawn, so it never depends on a font having the symbol), name, key
/// in grey and a badge for the Needs you items that belong to it (red urgent, amber soon, blue worth a look).
/// The open screen is highlighted.
/// </summary>
public partial class MenuTab : Button
{
    public readonly string Id;
    private readonly HBoxContainer _row;
    private readonly MenuIcon _icon;
    private readonly Label _name, _key, _badge;
    private readonly PanelContainer _badgeBox;
    private string _state = "";

    public MenuTab() : this("", "", "", "", () => { }) { }

    public MenuTab(string id, string name, string key, string tip, Action pressed)
    {
        Id = id;
        FocusMode = FocusModeEnum.None;
        Flat = true;
        TooltipText = tip;
        Pressed += () => Main.Instance.RunGuarded($"menu {name}", pressed);
        _row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 5);
        _row.Position = new Vector2(10, 0);
        _icon = new MenuIcon(id) { CustomMinimumSize = new Vector2(16, 16), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore };
        _row.AddChild(_icon);
        _name = Ui.Label(name, 15);
        _name.MouseFilter = MouseFilterEnum.Ignore;
        _row.AddChild(_name);
        _key = Ui.Label(key, 12, Ui.Dim);
        _key.MouseFilter = MouseFilterEnum.Ignore;
        _key.VerticalAlignment = VerticalAlignment.Center;
        _row.AddChild(_key);
        _badge = Ui.Label("", 12, new Color("0b0f16"));
        _badge.MouseFilter = MouseFilterEnum.Ignore;
        _badgeBox = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter, Visible = false };
        _badgeBox.AddChild(_badge);
        _row.AddChild(_badgeBox);
        AddChild(_row);
        _tipBase = tip;
    }

    private readonly string _tipBase;

    public void Set(bool active, NeedLevel? worst, int count, bool icons, string tipExtra)
    {
        var state = $"{active}|{worst}|{count}|{icons}|{tipExtra}";
        if (state == _state) return;
        _state = state;
        _icon.Visible = icons;
        _badgeBox.Visible = worst != null;
        if (worst is NeedLevel level)
        {
            _badge.Text = count > 0 ? count.ToString() : "!";
            var box = new StyleBoxFlat { BgColor = BottomBar.ColorOf(level) };
            box.SetCornerRadiusAll(7);
            box.ContentMarginLeft = box.ContentMarginRight = 5;
            box.ContentMarginTop = box.ContentMarginBottom = 0;
            _badgeBox.AddThemeStyleboxOverride("panel", box);
        }
        var color = active ? Ui.Player : Ui.Text;
        _name.AddThemeColorOverride("font_color", color);
        _icon.Color = active ? Ui.Player : new Color(Ui.Text, 0.8f);
        _icon.QueueRedraw();
        foreach (var st in new[] { "normal", "hover", "pressed" })
        {
            var bg = new StyleBoxFlat { BgColor = active ? new Color(Ui.Player, 0.16f) : st == "hover" ? new Color(1, 1, 1, 0.07f) : new Color(0, 0, 0, 0) };
            if (active) { bg.BorderColor = Ui.Player; bg.BorderWidthBottom = 2; }
            bg.SetCornerRadiusAll(4);
            AddThemeStyleboxOverride(st, bg);
        }
        TooltipText = _tipBase + tipExtra;
        var min = _row.GetCombinedMinimumSize();
        CustomMinimumSize = new Vector2(min.X + 20, 32);
        _row.Position = new Vector2(10, (32 - min.Y) / 2);
    }
}

/// <summary>The small line icons of the menu bar, drawn rather than taken from a font.</summary>
public partial class MenuIcon : Control
{
    private readonly string _id;
    public Color Color = Ui.Text;

    public MenuIcon() : this("") { }
    public MenuIcon(string id) => _id = id;

    public override void _Draw()
    {
        var s = Size;
        var c = Color;
        const float w = 1.6f;
        var m = new Vector2(s.X / 2, s.Y / 2);
        switch (_id)
        {
            case "empire": // a grid: holdings
                DrawRect(new Rect2(2, 2, s.X - 4, s.Y - 4), c, false, w);
                DrawLine(new Vector2(m.X, 2), new Vector2(m.X, s.Y - 2), c, w);
                DrawLine(new Vector2(2, m.Y), new Vector2(s.X - 2, m.Y), c, w);
                break;
            case "planets": // a planet and its ring
                DrawArc(m, s.X * 0.28f, 0, Mathf.Tau, 24, c, w, true);
                DrawLine(new Vector2(0.5f, m.Y + 3), new Vector2(s.X - 0.5f, m.Y - 3), c, w);
                break;
            case "fleets": // a chevron flying right
                DrawPolyline(new[] { new Vector2(2, 3), new Vector2(s.X - 2, m.Y), new Vector2(2, s.Y - 3), new Vector2(6, m.Y), new Vector2(2, 3) }, c, w, true);
                break;
            case "ships": // a hull side-on
                DrawPolyline(new[] { new Vector2(1, m.Y - 3), new Vector2(s.X - 5, m.Y - 3), new Vector2(s.X - 1, m.Y), new Vector2(s.X - 5, m.Y + 3), new Vector2(1, m.Y + 3), new Vector2(1, m.Y - 3) }, c, w, true);
                DrawLine(new Vector2(4, m.Y - 3), new Vector2(6, m.Y - 6), c, w);
                break;
            case "supply": // two arrows, there and back
                DrawLine(new Vector2(2, 5), new Vector2(s.X - 2, 5), c, w);
                DrawPolyline(new[] { new Vector2(s.X - 5, 2), new Vector2(s.X - 2, 5), new Vector2(s.X - 5, 8) }, c, w, true);
                DrawLine(new Vector2(2, s.Y - 5), new Vector2(s.X - 2, s.Y - 5), c, w);
                DrawPolyline(new[] { new Vector2(5, s.Y - 8), new Vector2(2, s.Y - 5), new Vector2(5, s.Y - 2) }, c, w, true);
                break;
            case "research": // a flask
                DrawPolyline(new[] { new Vector2(m.X - 2, 1), new Vector2(m.X - 2, 6), new Vector2(2, s.Y - 2), new Vector2(s.X - 2, s.Y - 2), new Vector2(m.X + 2, 6), new Vector2(m.X + 2, 1) }, c, w, true);
                break;
            case "intel": // an eye
                DrawArc(new Vector2(m.X, m.Y + 6), 8.5f, -2.36f, -0.78f, 12, c, w, true);
                DrawArc(new Vector2(m.X, m.Y - 6), 8.5f, 0.78f, 2.36f, 12, c, w, true);
                DrawCircle(m, 2.2f, c);
                break;
            case "progress": // a line going up
                DrawPolyline(new[] { new Vector2(1, s.Y - 2), new Vector2(6, s.Y - 7), new Vector2(9, s.Y - 5), new Vector2(s.X - 1, 2) }, c, w, true);
                DrawPolyline(new[] { new Vector2(s.X - 6, 2), new Vector2(s.X - 1, 2), new Vector2(s.X - 1, 7) }, c, w, true);
                break;
        }
    }
}
