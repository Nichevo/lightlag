using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;
using SimEngagement = Lightlag.Sim.Core.Engagement;

namespace Lightlag.Game;

/// <summary>
/// The battle view (U): one screen for a fight. Your ships down the left, what you know of the enemy down the right,
/// the map in the middle with paths and reach rings, a header with the gap, what your fleet is doing and orders on
/// their way, and across the bottom: fleet tabs, a card per weapon or system (see <see cref="BattleCards"/>), and a
/// timeline with a row per fleet.
/// </summary>
public partial class BattleScreen : Control
{
    private readonly GameScreen _screen;
    private PanelContainer _headPanel = null!, _leftPanel = null!, _rightPanel = null!, _bottomPanel = null!;
    private Label _header = null!, _now = null!, _lag = null!, _transit = null!, _fight = null!;
    private ScrollContainer _leftScroll = null!, _rightScroll = null!;
    private OurShipsColumn _ours = null!;
    private EnemyColumn _enemy = null!;
    private VBoxContainer _right = null!;
    private Button _legendButton = null!;
    private VBoxContainer _legend = null!;
    private string? _legendKey;
    private HBoxContainer _tabs = null!, _cardsRow = null!;
    private Button _fold = null!;
    private Label _timelineTitle = null!;
    private string _tabsKey = "";
    private readonly List<CardView> _cards = new();
    private string _cardsKey = "";
    private BattleTimeline _timeline = null!;
    private SiegePanel _siege = null!;
    private CardContext? _context;

    // Each of your fleets' engagement with the enemy on screen, worked out a few times a second.
    private readonly Dictionary<int, EngagementInfo> _infos = new();
    private ulong _infosAt;
    private string _infosKey = "";

    private const string LegendSection = "battle-legend";

    public BattleScreen() : this(null!) { }
    public BattleScreen(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // Header: top middle.
        var head = new VBoxContainer();
        head.AddThemeConstantOverride("separation", 3);
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        _header = Ui.Label("", 14, Ui.Text);
        _header.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _header.CustomMinimumSize = new Vector2(760, 0);
        top.AddChild(_header);
        top.AddChild(Ui.SmallButton("Close (U)", () => _screen.CloseBattle(), "Back to the system map"));
        head.AddChild(top);
        _now = Line(head, 13, Ui.Text);
        _lag = Line(head, 13, Ui.Warn);
        _transit = Line(head, 12, Ui.Warn);
        _fight = Line(head, 12, Ui.Dim);
        _headPanel = Ui.Panel(head, 10);
        AddChild(_headPanel);

        // Left: your ships.
        _ours = new OurShipsColumn(_screen) { CustomMinimumSize = new Vector2(330, 0) };
        _leftScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _leftScroll.AddChild(_ours);
        _leftPanel = Ui.Panel(_leftScroll, 10);
        AddChild(_leftPanel);

        // Right: the enemy, then the rings and paths legend.
        _right = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        _right.AddThemeConstantOverride("separation", 6);
        _enemy = new EnemyColumn(_screen);
        _right.AddChild(_enemy);
        _legendButton = new Button { Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, TooltipText = "What the rings and lines on the map are (click to open or fold)" };
        _legendButton.AddThemeFontSizeOverride("font_size", 12);
        _legendButton.Pressed += () => { PlayerInfo.SetOpen(LegendSection, !PlayerInfo.OpenSections.Contains(LegendSection)); _legendKey = null; };
        _right.AddChild(_legendButton);
        _legend = new VBoxContainer();
        _legend.AddThemeConstantOverride("separation", 2);
        _right.AddChild(_legend);
        _rightScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _rightScroll.AddChild(_right);
        _rightPanel = Ui.Panel(_rightScroll, 10);
        AddChild(_rightPanel);

        // Bottom, the whole width: a title line (fold, fleet tabs, what the timeline shows), the cards, the timeline.
        var bottom = new VBoxContainer();
        bottom.AddThemeConstantOverride("separation", 4);
        var titleRow = new HBoxContainer();
        titleRow.AddThemeConstantOverride("separation", 6);
        _fold = CardBuilder.Small("▾ Orders", "Fold the cards and timeline down to this line, or open them again (remembered)", () => _screen.ToggleBattleFold());
        _fold.AddThemeFontSizeOverride("font_size", 12);
        titleRow.AddChild(_fold);
        _tabs = new HBoxContainer();
        _tabs.AddThemeConstantOverride("separation", 4);
        titleRow.AddChild(_tabs);
        var gap = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        titleRow.AddChild(gap);
        _timelineTitle = Ui.Label("", 12, Ui.Text);
        titleRow.AddChild(_timelineTitle);
        var help = CardBuilder.Small("?", BattleTimeline.Legend, () => { });
        help.AddThemeFontSizeOverride("font_size", 12);
        titleRow.AddChild(help);
        bottom.AddChild(titleRow);
        _cardsRow = new HBoxContainer();
        _cardsRow.AddThemeConstantOverride("separation", 6);
        bottom.AddChild(_cardsRow);
        _timeline = new BattleTimeline { Picked = (hour, what) => _screen.SetPauseAt(hour, what) };
        bottom.AddChild(_timeline);
        _bottomPanel = Ui.Panel(bottom, 8);
        AddChild(_bottomPanel);

        _siege = new SiegePanel(_screen) { Visible = false };
        AddChild(_siege);
    }

    private static Label Line(VBoxContainer parent, int size, Color color)
    {
        var l = Ui.Label("", size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(760, 0);
        parent.AddChild(l);
        return l;
    }

    /// <summary>Kept in ui.cfg: the bottom panel is folded.</summary>
    public const string FoldKey = "battle-panel-folded";

    /// <summary>The top of the cards and timeline panel (for the news line above it).</summary>
    public float BottomTop => _bottomPanel.Position.Y;

    public void Refresh(Simulation sim)
    {
        var w = sim.World;
        var fleets = _screen.BattleOurFleets();
        var all = _screen.BattleAll && fleets.Count > 1;
        var lead = _screen.EngagementNow?.Mine is Fleet m && fleets.Contains(m) ? m
            : _screen.SelectedFleet is Fleet sel && fleets.Contains(sel) ? sel : fleets.FirstOrDefault();
        var tabFleets = all ? fleets : lead != null ? new List<Fleet> { lead } : new List<Fleet>();
        var enemyIds = _screen.BattleEnemyIds();
        RefreshInfos(w, fleets);

        RefreshHeader(w, lead, tabFleets);
        RefreshTabs(fleets, all ? null : lead?.Id);
        _context = tabFleets.Count == 0 ? null : new CardContext { Screen = _screen, Sim = sim, Fleets = tabFleets, Infos = _infos };
        RefreshCards();
        _timeline.Rows = fleets.Where(f => _infos.ContainsKey(f.Id)).Select(f => _infos[f.Id]).ToList();
        _screen.SetBattleRows(_timeline.Rows);
        _timeline.Lit = all ? null : lead?.Id;
        _timeline.PauseAt = _screen.PauseAtHour;
        _timeline.CustomMinimumSize = new Vector2(0, _timeline.WantHeight);
        _timeline.QueueRedraw();
        var folded = PlayerInfo.OpenSections.Contains(FoldKey);
        _fold.Text = folded ? "▸ Orders" : "▾ Orders";
        _cardsRow.Visible = !folded;
        _timeline.Visible = !folded;
        _timelineTitle.Text = folded ? FoldedSummary() : _timeline.Title;
        _timelineTitle.AddThemeColorOverride("font_color", _timeline.Rows.All(r => r.InSight) ? Ui.Dim : Ui.Warn);
        _ours.Refresh(w, fleets, all ? null : lead?.Id);
        _enemy.Refresh(w, fleets, enemyIds);
        RefreshLegend();
        Layout();
        RefreshSiege(w);
        HoverReach();
    }

    /// <summary>Each of your fleets' engagement with the enemy on screen: the open one from the game screen, the rest worked out here.</summary>
    private void RefreshInfos(World w, List<Fleet> fleets)
    {
        var enemy = _screen.Engagement?.Enemy;
        var key = $"{enemy}|{_screen.EngagementBurn}|{string.Join(",", fleets.Select(f => f.Id))}";
        var now = Time.GetTicksMsec();
        if (key == _infosKey && now - _infosAt < 250) return;
        _infosKey = key;
        _infosAt = now;
        _infos.Clear();
        foreach (var f in fleets)
        {
            if (_screen.EngagementNow is EngagementInfo open && open.Mine.Id == f.Id) { _infos[f.Id] = open; continue; }
            var e = enemy ?? SimEngagement.EnemyFor(w, f);
            if (e is int id && Sensors.Find(w, Side.Player, id, seat: true) != null)
                Main.Instance.RunGuarded("battle view engagement", () =>
                {
                    if (SimEngagement.Of(w, f, id, _screen.EngagementBurn) is EngagementInfo info) _infos[f.Id] = info;
                });
        }
    }

    private void RefreshHeader(World w, Fleet? lead, List<Fleet> tabFleets)
    {
        var e = lead != null && _infos.TryGetValue(lead.Id, out var info) ? info : null;
        if (e != null)
        {
            var seen = e.InSight ? "in sight" : $"not in sight for {SimEngagement.Duration(e.SeenHoursAgo)}: the marker is where they should be if they kept going";
            var how = e.Closing > 1e-3 ? $"closing at {Format.Speed(e.Closing)}" : e.Closing < -1e-3 ? $"pulling apart at {Format.Speed(-e.Closing)}" : "holding distance";
            var pass = e.ClosestInHours < 1e-3 ? "as close as they will get" : $"closest pass in {SimEngagement.Duration(e.ClosestInHours)} at {Format.Distance(e.ClosestGap)}";
            var which = _screen.Engagements.Count > 1 ? $"   (engagement {Math.Max(1, _screen.Engagements.FindIndex(p => p.Mine == e.Mine.Id && p.Enemy == e.EnemyId) + 1)} of {_screen.Engagements.Count}: Tab for the next)" : "";
            _header.Text = $"BATTLE: {e.Mine.Name} and {e.EnemyName} ({e.EnemyShips} ship{(e.EnemyShips == 1 ? "" : "s")}, {seen})\n{Format.Distance(e.Gap)} apart, {how}; {pass}, if neither changes course.{which}";
        }
        else _header.Text = lead == null ? "BATTLE VIEW: select one of your fleets" : $"BATTLE VIEW: {lead.Name} · no enemy fleet in sight or on record";

        // What the fleet is doing, in the fleet panel's words (orbit slides and standoffs included), and the enemy's slide while you see it.
        var now = new List<string>();
        if (lead != null)
        {
            now.Add($"{lead.Name}: {FleetPanel.DoingNow(w, lead)}");
            if (w.Hour < lead.OrbitSlideRestUntil)
                now.Add($"gave up the chase, tries again in {SimEngagement.Duration(lead.OrbitSlideRestUntil - w.Hour)}");
        }
        if (e != null && w.FindFleet(e.EnemyId) is Fleet foe && Sensors.Sees(w, Side.Player, foe) && foe.OrbitSlide != 0 && foe.OrbitBodyId is int ob && w.FindBody(ob) is Body round)
            now.Add(foe.OrbitSlide > 0 ? $"{foe.Name} is sliding round {round.Name} into railgun reach" : $"{foe.Name} is sliding round {round.Name} to hide behind it");
        _now.Text = string.Join(" · ", now);

        _lag.Text = lead != null && e != null ? LagLine(w, lead, e) : "";
        _lag.Visible = _lag.Text != "";
        _transit.Text = Transit(w, tabFleets);
        _transit.Visible = _transit.Text != "";
        _fight.Text = FightLine(w, lead);
    }

    /// <summary>Under light lag: how long orders take, how old the picture is, and the time left to give orders before contact.</summary>
    private static string LagLine(World w, Fleet f, EngagementInfo e)
    {
        var lag = Light.DelayHours(w, Side.Player, f.Position);
        if (lag <= 0) return "";
        var line = $"Orders take {Format.Lag(lag)} to reach {f.Name}; this picture is {Format.Lag(Light.AgeHours(w, Side.Player, e.EnemyId))} old.";
        if (!Light.SignalClear(w, Side.Player, f.Position)) return "⏱ " + line + " A planet blocks the signal: orders wait until it clears.";
        if (e.ContactInHours is double contact && contact > 0)
        {
            var left = contact - lag;
            line += left > 0 ? $" ⏱ {Format.Lag(left)} left to give orders before contact." : " Too late for orders before contact: it fights by its standing orders.";
        }
        return line;
    }

    /// <summary>Orders on their way to these fleets at light speed (or held until a planet stops blocking the signal).</summary>
    private static string Transit(World w, List<Fleet> fleets)
    {
        var ids = fleets.Select(f => f.Id).ToHashSet();
        var lines = new List<string>();
        foreach (var t in w.Light.InTransit.Where(t => t.Side == Side.Player && t.Command is IFleetCommand fc && ids.Contains(fc.FleetId)).Take(3))
            lines.Add($"Order on its way to {w.FindFleet(((IFleetCommand)t.Command).FleetId)?.Name}: {OrderName(t.Command)}, arrives in {Format.Lag(Math.Max(0, t.ArriveHour - w.Hour))}");
        foreach (var t in w.Light.Held.Where(t => t.Side == Side.Player && t.Command is IFleetCommand fc && ids.Contains(fc.FleetId)).Take(2))
            lines.Add($"Order held: {OrderName(t.Command)} waits until the planet stops blocking the signal");
        return string.Join("\n", lines);
    }

    private static string OrderName(GameCommand c)
    {
        var name = c.GetType().Name.Replace("Command", "");
        return string.Concat(name.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));
    }

    /// <summary>The score once shots fly, else when battle time starts; and missiles in flight.</summary>
    private string FightLine(World w, Fleet? lead)
    {
        var parts = new List<string>();
        if (w.ActiveBattle is Battle battle)
        {
            string Side_(BattleSide s) => $"{s.ShipsLost.Count} lost, {s.Crippled.Count} crippled, {s.DamageDealt:0} damage dealt";
            parts.Add($"Score: you {Side_(battle.For(Side.Player))} · them {Side_(battle.Foes(Side.Player))}");
        }
        else if (lead != null)
        {
            var contact = GameData.Combat.ContactRangeMm;
            parts.Add($"No battle yet: battle time starts within {Format.Distance(contact)} of an enemy you see, {Format.Distance(contact * Combat.ClosingContactFactor)} if closing");
        }
        var ours = w.Missiles.Count(m => m.Side == Side.Player);
        var theirs = w.Missiles.Count(m => m.Side != Side.Player && w.FindShip(m.TargetShipId)?.Side == Side.Player);
        if (ours + theirs > 0) parts.Add($"Missiles in flight: {ours} yours, {theirs} at you");
        return string.Join(" · ", parts);
    }

    /// <summary>The folded panel's line: each card's folded summary, run together.</summary>
    private string FoldedSummary() =>
        _context is CardContext c ? string.Join("  ·  ", _cards.Select(v => $"{v.Card.Title}: {v.Card.Folded(c).Replace("\n", ", ")}")) : "";

    /// <summary>A tab per fleet of yours in the fight (Ctrl+1, 2, 3...) and All fleets.</summary>
    private void RefreshTabs(List<Fleet> fleets, int? lit)
    {
        var key = string.Join(",", fleets.Select(f => $"{f.Id}:{f.Name}")) + $"|{lit}";
        if (key == _tabsKey || Ui.MouseHeld) return;
        _tabsKey = key;
        Ui.ClearChildren(_tabs);
        _tabs.Visible = fleets.Count > 1;
        if (fleets.Count < 2) return;
        _tabs.AddChild(Ui.Label("for", 12, Ui.Dim));
        for (var i = 0; i < fleets.Count; i++)
        {
            var id = fleets[i].Id;
            var b = CardBuilder.Small($"{i + 1} {fleets[i].Name}", $"Orders for {fleets[i].Name} only (Ctrl+{i + 1})", () => _screen.ChooseBattleTab(id));
            b.AddThemeFontSizeOverride("font_size", 12);
            b.Modulate = lit == id ? Ui.Player : new Color(1, 1, 1, 0.75f);
            _tabs.AddChild(b);
        }
        var allB = CardBuilder.Small("All fleets", "Orders go to every fleet of yours here; a setting that differs between them says \"mixed\" (Ctrl+0)", () => _screen.ChooseBattleTab(null));
        allB.AddThemeFontSizeOverride("font_size", 12);
        allB.Modulate = lit == null ? Ui.Player : new Color(1, 1, 1, 0.75f);
        _tabs.AddChild(allB);
    }

    private void RefreshCards()
    {
        if (_context is not CardContext c)
        {
            foreach (var v in _cards) v.QueueFree();
            _cards.Clear();
            _cardsKey = "";
            return;
        }
        var shown = BattleCards.Make().Where(card => card.Shows(c)).ToList();
        var key = string.Join(",", c.Fleets.Select(f => f.Id)) + "|" + string.Join(",", shown.Select(x => x.Id));
        if (key != _cardsKey && !Ui.MouseHeld)
        {
            _cardsKey = key;
            foreach (var v in _cards) v.QueueFree();
            _cards.Clear();
            foreach (var card in shown)
            {
                var view = new CardView(card, () => _context);
                _cardsRow.AddChild(view);
                _cards.Add(view);
            }
        }
        foreach (var v in _cards) Main.Instance.RunGuarded($"battle card {v.Card.Id}", () => v.Refresh(c));
    }

    /// <summary>Fold cards that don't fit, rightmost first; a card you pinned open or folded keeps your choice.</summary>
    private void FitCards(float room)
    {
        var open = _cards.Select(v => v.Pin != CardPin.Folded).ToList();
        float Width() => _cards.Select((v, i) => open[i] ? v.OpenWidth : CardView.FoldedWidth).Sum() + 6 * Math.Max(0, _cards.Count - 1);
        for (var i = _cards.Count - 1; i >= 0 && Width() > room; i--)
            if (open[i] && _cards[i].Pin == CardPin.Auto) open[i] = false;
        for (var i = 0; i < _cards.Count; i++) _cards[i].SetOpen(open[i]);
    }

    /// <summary>
    /// Sets a control's size and position only when they change. (Shrinking a panel to nothing and growing it back
    /// each refresh, as ResetSize then Size did, made Godot lay out and re-wrap every label inside it twice a refresh.)
    /// </summary>
    private static void Place(Control c, Vector2 size, Vector2? at = null)
    {
        if (c.Size != size) c.Size = size;
        if (at is Vector2 p && c.Position != p) c.Position = p;
    }

    private static void Fit(Control c) => Place(c, c.GetCombinedMinimumSize());

    private void Layout()
    {
        var size = GetViewportRect().Size;
        Place(this, size, Vector2.Zero);
        var top = Hud.ContentTop + 6;

        var width = size.X - 32;
        FitCards(width - 16);
        _timeline.CustomMinimumSize = new Vector2(width - 16, _timeline.WantHeight);
        var bottom = new Vector2(width, _bottomPanel.GetCombinedMinimumSize().Y);
        Place(_bottomPanel, bottom, new Vector2(16, size.Y - bottom.Y - 12));

        // The side columns run from under the top bar to the bottom panel, scrolling when they don't fit.
        var room = _bottomPanel.Position.Y - top - 30;
        foreach (var (panel, scroll, inner) in new[] { (_leftPanel, _leftScroll, (Control)_ours), (_rightPanel, _rightScroll, _right) })
        {
            var want = inner.GetCombinedMinimumSize();
            scroll.CustomMinimumSize = new Vector2(want.X + 12, Mathf.Max(80, Mathf.Min(want.Y, room)));
            Fit(panel);
        }
        Place(_leftPanel, _leftPanel.Size, new Vector2(16, top));
        Place(_rightPanel, _rightPanel.Size, new Vector2(size.X - _rightPanel.Size.X - 16, top));
        // The header sits between the columns, below the top bar.
        var between = _rightPanel.Position.X - (_leftPanel.Position.X + _leftPanel.Size.X) - 24;
        var headW = Mathf.Clamp(between - 20, 380, 760);
        foreach (var l in new[] { _header, _now, _lag, _transit, _fight }) l.CustomMinimumSize = new Vector2(headW - (l == _header ? 110 : 0), 0);
        Fit(_headPanel);
        Place(_headPanel, _headPanel.Size, new Vector2(_leftPanel.Position.X + _leftPanel.Size.X + 12 + Mathf.Max(0, (between - _headPanel.Size.X) / 2), top));
    }

    private void RefreshSiege(World w)
    {
        var fleets = _screen.BattleFleets();
        if (SiegePanel.Siege(w, fleets, _screen.SelectedFleet) is var (body, mine))
        {
            _siege.Visible = true;
            _siege.Refresh(w, body, mine);
            Fit(_siege);
            // Between the header and the cards, shrunk to fit when the screen is short.
            var top = _headPanel.Position.Y + _headPanel.Size.Y + 8;
            var room = _bottomPanel.Position.Y - 8 - top;
            var scale = Mathf.Clamp(room / Mathf.Max(1, _siege.Size.Y), 0.6f, 1f);
            _siege.Scale = new Vector2(scale, scale);
            _siege.Position = new Vector2((Size.X - _siege.Size.X * scale) / 2, top);
        }
        else _siege.Visible = false;
    }

    /// <summary>The mouse over a weapon card lights its reach ring on the map.</summary>
    private void HoverReach()
    {
        var mouse = GetGlobalMousePosition();
        var over = _cards.FirstOrDefault(v => v.Visible && v.GetGlobalRect().HasPoint(mouse));
        _screen.SetBattleReach(over?.Card.Reach);
    }

    /// <summary>What each ring and line on the map is, and whether it is on screen at this zoom (folds away).</summary>
    private void RefreshLegend()
    {
        var open = PlayerInfo.OpenSections.Contains(LegendSection);
        _legendButton.Text = open ? "▾ Rings and paths on the map" : "▸ Rings and paths on the map";
        var e = _screen.EngagementNow;
        var rows = new List<(Color Color, string Text, bool OnScreen, bool Dashed)>();
        if (open)
        {
            rows.Add((MapView.SpeedUp, "Your planned course: speeding up", true, false));
            rows.Add((MapView.Braking, "Your planned course: braking (◇ where it flips to brake)", true, false));
            rows.Add((Ui.Rival, "Their predicted track, if they keep going", true, true));
            rows.Add((new Color(1f, 0.83f, 0.47f), "Closest pass (✕ where a chase meets them)", true, false));
            rows.Add((new Color(Ui.Rival, 0.6f), "Last seen here (hollow marker) when out of sight", true, true));
            if (e != null)
            {
                var cam = _screen.Camera;
                var view = new Rect2(Vector2.Zero, GetViewportRect().Size);
                foreach (var ring in EngagementRings.For(e))
                {
                    var at = cam.ToScreen(ring.AroundMine ? e.Mine.Position : e.EnemyAt);
                    var r = (float)(ring.Mm * cam.Scale);
                    var where = EngagementRings.Status(at, r, view) switch { RingStatus.TooSmall => " (zoom in)", RingStatus.Outside => " (zoom out)", _ => "" };
                    rows.Add((ring.Color, $"{ring.Label} {Format.Distance(ring.Mm)}{where}", where == "", ring.Dashed));
                }
            }
        }
        var key = $"{open}|" + string.Join("\n", rows.Select(x => $"{x.Color}{x.Text}"));
        if (key == _legendKey || Ui.MouseHeld) return;
        _legendKey = key;
        Ui.ClearChildren(_legend);
        foreach (var (color, text, onScreen, dashed) in rows)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            row.AddChild(new ColorRect { Color = dashed ? new Color(color, 0.55f) : color, CustomMinimumSize = new Vector2(14, dashed ? 2 : 3), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            var l = Ui.Label(text, 11, onScreen ? color.Lightened(0.2f) : Ui.Dim);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(290, 0);
            row.AddChild(l);
            _legend.AddChild(row);
        }
    }
}

public enum CardPin { Auto, Open, Folded }

/// <summary>
/// One card on the battle view: a title you click to open or fold it (the choice is remembered; right-click goes back to
/// folding by itself when short of room), its rows when open, and one or two lines when folded.
/// </summary>
public partial class CardView : PanelContainer
{
    public readonly BattleCard Card;
    private readonly List<Action<CardContext>> _live = new();
    private readonly Button _head;
    private readonly VBoxContainer _body;
    private readonly Label _folded;
    public bool Open { get; private set; } = true;
    public const float FoldedWidth = 150;

    public CardView(BattleCard card, Func<CardContext?> context)
    {
        Card = card;
        Ui.StylePanel(this, 6);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 3);
        _head = new Button { Flat = true, Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None,
            TooltipText = "Click to open or fold (remembered); right-click to let it fold by itself when short of room" };
        _head.AddThemeFontSizeOverride("font_size", 12);
        _head.AddThemeColorOverride("font_color", Ui.Text);
        _head.GuiInput += OnHead;
        v.AddChild(_head);
        _body = new VBoxContainer();
        _body.AddThemeConstantOverride("separation", 3);
        v.AddChild(_body);
        _folded = Ui.Label("", 11, Ui.Dim);
        _folded.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _folded.CustomMinimumSize = new Vector2(FoldedWidth - 14, 0);
        _folded.MaxLinesVisible = 2;
        _folded.Visible = false;
        v.AddChild(_folded);
        AddChild(v);
        card.Build(new CardBuilder(_body, _live, context));
    }

    private static string OpenKey(string id) => $"battle-card-open:{id}";
    private static string FoldKey(string id) => $"battle-card-fold:{id}";

    public CardPin Pin => PlayerInfo.OpenSections.Contains(OpenKey(Card.Id)) ? CardPin.Open
        : PlayerInfo.OpenSections.Contains(FoldKey(Card.Id)) ? CardPin.Folded : CardPin.Auto;

    private void SetPin(CardPin pin)
    {
        PlayerInfo.SetOpen(OpenKey(Card.Id), pin == CardPin.Open);
        PlayerInfo.SetOpen(FoldKey(Card.Id), pin == CardPin.Folded);
    }

    private void OnHead(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true } mb) return;
        if (mb.ButtonIndex == MouseButton.Left) SetPin(Open ? CardPin.Folded : CardPin.Open);
        else if (mb.ButtonIndex == MouseButton.Right) SetPin(CardPin.Auto);
        else return;
        AcceptEvent();
    }

    /// <summary>The width the card takes open.</summary>
    public float OpenWidth => Math.Max(_body.GetCombinedMinimumSize().X, _head.GetCombinedMinimumSize().X) + 14;

    public void SetOpen(bool open)
    {
        if (open == Open) return;
        Open = open;
        _body.Visible = open;
        _folded.Visible = !open;
        ResetSize();
    }

    public void Refresh(CardContext c)
    {
        var pin = Pin == CardPin.Auto ? "" : "  📌";
        _head.Text = $"{(Open ? "▾" : "▸")} {Card.Title}{pin}";
        // Rows keep updating while folded, so the open width is right when there is room again.
        foreach (var live in _live) live(c);
        if (!Open) _folded.Text = Card.Folded(c);
    }
}
