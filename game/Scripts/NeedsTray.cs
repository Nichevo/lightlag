using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>Shared helpers for the Needs you tray, pins and cards.</summary>
public static class NeedsUi
{
    /// <summary>The planet or fleet an item is about (its name), or null for empire-wide items.</summary>
    public static string? PlaceOf(World w, NeedItem n) =>
        n.FleetId is int f && w.FindFleet(f) is Fleet fleet ? fleet.Name
        : n.BodyId is int b && w.FindBody(b) is Body body ? body.Name : null;

    public static bool HasPlace(World w, NeedItem n) =>
        n.FleetId is int f && w.FindFleet(f) is { Side: Side.Player } || n.BodyId is int b && w.FindBody(b) != null;

    /// <summary>A short name for an area, as the tray's second line shows it.</summary>
    public static string AreaShort(AutoArea? a) => a switch
    {
        AutoArea.Ground => "Invasion",
        AutoArea.AfterBattle => "After battle",
        AutoArea.Events => "Event",
        AutoArea.Workers => "Workers",
        null => "",
        _ => a.ToString()!,
    };

    /// <summary>The second line: where, when it bites, and the area.</summary>
    public static string Meta(World w, NeedItem n)
    {
        var parts = new List<string>();
        if (PlaceOf(w, n) is string place) parts.Add(place);
        parts.Add(NeedKinds.Bites(w, Side.Player, n));
        if (AreaShort(n.Area) is { Length: > 0 } area) parts.Add(area);
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Automation lines worth a look (amber): anything that spent real Industry, moved a fleet, answered a call,
    /// pulled back from a fight or made an event call.
    /// </summary>
    public static bool Worth(AutoArea a) => a is AutoArea.Building or AutoArea.Shipyard or AutoArea.Settling or AutoArea.Supply or AutoArea.Refits
        or AutoArea.Battle or AutoArea.AfterBattle or AutoArea.Boarding or AutoArea.Ground or AutoArea.Events;

    public static Color LevelColor(NeedLevel level) => BottomBar.ColorOf(level);

    public static Color Amber => Ui.Warn;

    /// <summary>A choice that is not an order: open the screen where the fix is.</summary>
    public static (string Label, Action Do)? ScreenChoice(GameScreen screen, World w, NeedItem n)
    {
        if (n.FleetId is int f && w.FindFleet(f) is { Side: Side.Player } fleet) return ($"Open {fleet.Name}", () => screen.OpenFleet(f));
        if (n.BodyId is int b && w.FindBody(b) is { Owner: Side.Player, Board: not null } body) return ($"Open {body.Name}", () => screen.OpenPlanet(b));
        return NeedScreens.For(w, n) switch
        {
            NeedScreens.Research => ("Research screen", () => screen.ShowResearch = true),
            NeedScreens.Ships => ("Ships screen", () => { if (!screen.ShowShips) screen.ToggleShips(); }),
            _ => null,
        };
    }

    /// <summary>Today's automation lines, newest first.</summary>
    public static List<AutoNote> Today(World w) =>
        Economy.Of(w, Side.Player).AutoNotes.Where(n => GameTime.DayOf((long)Math.Floor(n.Hour)) == w.Day).Reverse().ToList();

    public static string Clock(double hour)
    {
        var h = (long)Math.Floor(hour);
        return $"{h % GameTime.TicksPerDay:00}:{(int)((hour - h) * 60):00}";
    }

    /// <summary>A small menu button with the given items; each item runs its action.</summary>
    public static MenuButton Menu(string text, string tip, IReadOnlyList<(string Label, Action? Do, string? Tip)> items)
    {
        var mb = new MenuButton { Text = text, Flat = false, FocusMode = Control.FocusModeEnum.None, TooltipText = tip };
        mb.AddThemeFontSizeOverride("font_size", 13);
        mb.CustomMinimumSize = new Vector2(0, 28);
        var popup = mb.GetPopup();
        popup.AddThemeFontSizeOverride("font_size", 13);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Do == null) { popup.AddSeparator(items[i].Label); continue; }
            popup.AddItem(items[i].Label, i);
            if (items[i].Tip != null) popup.SetItemTooltip(popup.ItemCount - 1, items[i].Tip);
        }
        popup.IdPressed += id => { if (items[(int)id].Do is Action a) Main.Instance.RunGuarded($"menu '{items[(int)id].Label}'", a); };
        return mb;
    }

    public static Button Small(string text, Action pressed, string tip, Color? color = null)
    {
        var b = Ui.SmallButton(text, pressed, tip);
        b.AddThemeFontSizeOverride("font_size", 13);
        b.CustomMinimumSize = new Vector2(0, 28);
        if (color is Color c) b.AddThemeColorOverride("font_color", c);
        return b;
    }

    /// <summary>The snooze choices for an item (the last one mutes its whole kind).</summary>
    public static List<(string, Action?, string?)> SnoozeItems(GameScreen screen, NeedItem n)
    {
        var key = n.Key;
        var kind = NeedKinds.KindOf(key);
        void Snooze(NeedSnooze how) => screen.IssueNow(new SnoozeNeedCommand(Side.Player, key, how), "snoozing a need");
        return new List<(string, Action?, string?)>
        {
            ("For an hour", () => Snooze(NeedSnooze.Hour), null),
            ("For a day", () => Snooze(NeedSnooze.Day), null),
            ("Until it changes", () => Snooze(NeedSnooze.UntilChanged), "Back as soon as its line reads differently"),
            ($"Never for this kind ({NeedKinds.KindName(kind)})", () => Snooze(NeedSnooze.NeverThisKind), "No more items of this kind; Automation ▾ in the tray lists them, with Show again"),
        };
    }

    /// <summary>Take another choice: give its order and put the item aside for an hour (it comes back if it still applies).</summary>
    public static void Choose(GameScreen screen, NeedItem n, GameCommand command)
    {
        screen.IssueNow(command, "a Needs you choice");
        screen.IssueNow(new SnoozeNeedCommand(Side.Player, n.Key, NeedSnooze.Hour, Quiet: true), "putting a need aside");
    }

    public static void Act(GameScreen screen, NeedItem n) => screen.IssueNow(new AcceptNeedCommand(Side.Player, n.Key), "acting on a need");

    /// <summary>⟳ Auto: hand this kind, here, to automation (the area set to Auto for this planet or fleet, or everywhere).</summary>
    public static void HandOver(GameScreen screen, World w, NeedItem n)
    {
        if (n.Area is not AutoArea area) return;
        screen.IssueNow(new SetAutoLevelCommand(Side.Player, area, AutoLevel.Auto, n.BodyId, n.FleetId) { Source = "player" }, "handing over to automation");
        screen.IssueNow(new SnoozeNeedCommand(Side.Player, n.Key, NeedSnooze.Hour, Quiet: true), "putting a need aside");
        var where = PlaceOf(w, n) is string place ? $" for {place}" : " everywhere";
        Main.Instance.Toast($"{Stewards.AreaName(area)} set to Auto{where}: automation does this itself now (Automation ▾ or Take it back to undo)");
    }
}

/// <summary>
/// The Needs you tray: drops from the badge under the top bar, on the left, over the map. Each row gives what,
/// where and when it bites, the obvious order, the sensible alternative and ⋯ for the rest, with Go, ⟳ Auto and
/// Snooze. Advice from automation sits in a folded strip below, then what automation handled today (amber
/// marks the ones worth a look, with Take it back), and the Automation ▾ menu. Nothing opens a full screen:
/// Go flies the map there and opens the right panel with the tray still up.
/// </summary>
public partial class NeedsTray : PanelContainer
{
    private const float Width = 600;
    private readonly GameScreen _screen;
    private readonly Hud _hud;
    private VBoxContainer _root = null!;
    private ScrollContainer? _scroll;
    private VBoxContainer _list = null!;
    private string _shape = "";
    private bool _adviceOpen, _autoOpen, _adviceTab;
    private readonly List<(Label Label, Func<World, string> Text)> _live = new();
    private readonly Dictionary<string, PanelContainer> _rows = new();
    private string? _styledSelected;
    public float MaxHeight { get; set; } = 600;

    /// <summary>Unfold the advice and automation strips (for screenshots).</summary>
    public void Unfold() { _adviceOpen = true; _autoOpen = true; _shape = ""; }

    public NeedsTray() : this(null!, null!) { }
    public NeedsTray(GameScreen screen, Hud hud) { _screen = screen; _hud = hud; }

    public override void _Ready()
    {
        Ui.StylePanel(this, 10);
        _root = new VBoxContainer { CustomMinimumSize = new Vector2(Width, 0) };
        _root.AddThemeConstantOverride("separation", 6);
        AddChild(_root);
    }

    private static List<NeedItem> Trouble(SideState s) => s.Needs.Where(n => n.Level != NeedLevel.Advice).ToList();
    private static List<NeedItem> Advice(SideState s) => PlayerInfo.Advice == AdviceMode.Off ? new() : s.Needs.Where(n => n.Level == NeedLevel.Advice).ToList();

    /// <summary>Enter: the selected row's act button (or its first choice).</summary>
    public bool ActSelected()
    {
        var w = Main.Instance.Sim?.World;
        if (w == null || Economy.Of(w, Side.Player).Needs.Find(n => n.Key == _hud.SelectedNeed) is not { } n) return false;
        if (n.Act != null) { NeedsUi.Act(_screen, n); return true; }
        if (NeedKinds.Choices(w, Side.Player, n).FirstOrDefault() is { } c) { NeedsUi.Choose(_screen, n, c.Command); return true; }
        return false;
    }

    public void Refresh(World w)
    {
        var state = Economy.Of(w, Side.Player);
        if (_hud.SelectedNeed != null && state.Needs.All(n => n.Key != _hud.SelectedNeed)) _hud.SelectedNeed = null;
        var today = NeedsUi.Today(w);
        var shape = string.Join(";", state.Needs.Select(n => n.Key + n.Level + n.Text + (n.Act != null)))
                    + $"|{PlayerInfo.Advice}|{PlayerInfo.Has(MapLayer.NeedPins)}|{_adviceOpen}|{_autoOpen}|{_adviceTab}|{today.Count}"
                    + "|" + string.Join(",", Stewards.Areas.Select(a => Stewards.LevelFor(w, Side.Player, a)))
                    + "|" + string.Join(",", state.AutoOverrides.Select(o => $"{o.Area}{o.BodyId}{o.FleetId}{o.Level}"));
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            _live.Clear();
            _rows.Clear();
            _styledSelected = "\u0001";
            foreach (var c in _root.GetChildren()) c.QueueFree();
            Build(w, state, today);
        }
        if (_scroll == null) return; // not built yet (a mouse button was held)
        foreach (var (label, text) in _live) label.Text = text(w);
        if (_styledSelected != _hud.SelectedNeed)
        {
            _styledSelected = _hud.SelectedNeed;
            foreach (var (key, row) in _rows) StyleRow(row, key == _hud.SelectedNeed);
            if (_hud.SelectedNeed != null && _rows.TryGetValue(_hud.SelectedNeed, out var sel)) Callable.From(() => { if (IsInstanceValid(sel)) _scroll.EnsureControlVisible(sel); }).CallDeferred();
        }
        // Fit the list to its rows, up to the room there is; then let the panel shrink to fit.
        var listHeight = _list.GetCombinedMinimumSize().Y;
        var others = _root.GetCombinedMinimumSize().Y - _scroll.CustomMinimumSize.Y;
        var room = Mathf.Max(120, MaxHeight - others - 24);
        var want = new Vector2(Width, Mathf.Min(listHeight, room));
        if (_scroll.CustomMinimumSize != want) _scroll.CustomMinimumSize = want;
        Size = Vector2.Zero;
    }

    private void Build(World w, SideState state, List<AutoNote> today)
    {
        var trouble = Trouble(state);
        var advice = Advice(state);
        var urgent = trouble.Count(n => n.Level == NeedLevel.Urgent);
        var soon = trouble.Count(n => n.Level == NeedLevel.Soon);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(Ui.Label("Needs you", 18, Ui.Player));
        if (urgent > 0) head.AddChild(Ui.Label($"{urgent} urgent", 14, Ui.Danger));
        if (soon > 0) head.AddChild(Ui.Label($"{soon} soon", 14, NeedsUi.Amber));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(NeedsUi.Small("Work through them", () => _hud.OpenCards(),
            "One item at a time on a card: what, where, when, what happens if you do nothing, and your choices (pauses while you work, in Display options)"));
        var pins = PlayerInfo.Has(MapLayer.NeedPins);
        head.AddChild(NeedsUi.Small(pins ? "◉ Pins on" : "○ Pins off", () => PlayerInfo.Toggle(MapLayer.NeedPins),
            "A pin on the map for each item with a place (also in Map layers)", pins ? Ui.Player : Ui.Dim));
        head.AddChild(NeedsUi.Small("✕", () => _hud.TrayOpen = false, "Close (the badge, or Esc)"));
        _root.AddChild(head);

        if (PlayerInfo.Advice == AdviceMode.OwnList)
        {
            var tabs = new HBoxContainer();
            tabs.AddThemeConstantOverride("separation", 4);
            tabs.AddChild(NeedsUi.Small($"Trouble ({trouble.Count})", () => _adviceTab = false, "Things that need you", !_adviceTab ? Ui.Player : Ui.Dim));
            tabs.AddChild(NeedsUi.Small($"Advice ({advice.Count})", () => _adviceTab = true, "What automation would do in the areas set to Advise", _adviceTab ? Ui.Player : Ui.Dim));
            _root.AddChild(tabs);
        }
        else _adviceTab = false;

        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _list.AddThemeConstantOverride("separation", 4);
        _scroll.AddChild(_list);
        _root.AddChild(_scroll);
        var shown = _adviceTab ? advice : trouble;
        if (shown.Count == 0)
            _list.AddChild(Ui.Label(_adviceTab ? "No advice just now." : "Nothing needs you just now. Automation handles the areas set to Auto.", 13, Ui.Dim));
        foreach (var n in shown) TroubleRow(w, n);

        if (PlayerInfo.Advice == AdviceMode.Folded && advice.Count > 0) AdviceStrip(w, advice);
        AutoStrip(w, today);

        _root.AddChild(Ui.Label(". next · Enter do it · ⟳ Auto hands this kind, here, to automation", 11, Ui.Dim));
    }

    private static void StyleRow(PanelContainer row, bool selected)
    {
        var box = new StyleBoxFlat
        {
            BgColor = selected ? new Color(Ui.Player, 0.14f) : new Color(1, 1, 1, 0.03f),
            BorderColor = selected ? Ui.Player : new Color(1, 1, 1, 0.06f),
        };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(5);
        box.SetContentMarginAll(6);
        row.AddThemeStyleboxOverride("panel", box);
    }

    private void TroubleRow(World w, NeedItem n)
    {
        var key = n.Key;
        var kind = NeedKinds.KindOf(key);
        var row = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };
        StyleRow(row, false);
        _rows[key] = row;
        row.MouseEntered += () => _hud.HoverNeed = key;
        row.MouseExited += () => { if (_hud.HoverNeed == key) _hud.HoverNeed = null; };
        row.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
                && Main.Instance.Sim?.World is World now && Economy.Of(now, Side.Player).Needs.Find(x => x.Key == key) is { } item)
            {
                _hud.SelectedNeed = key;
                _hud.GoTo(item);
            }
        };
        var col = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        col.AddThemeConstantOverride("separation", 2);
        row.AddChild(col);

        var top = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        top.AddThemeConstantOverride("separation", 6);
        var dot = Ui.Label("●", 13, NeedsUi.LevelColor(n.Level));
        dot.MouseFilter = MouseFilterEnum.Ignore;
        top.AddChild(dot);
        var title = Ui.Label(n.Text, 14);
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.CustomMinimumSize = new Vector2(Width - 60, 0);
        title.TooltipText = "If you do nothing: " + NeedKinds.Why(kind);
        title.MouseFilter = MouseFilterEnum.Pass;
        top.AddChild(title);
        col.AddChild(top);

        var meta = Ui.Label("", 12, Ui.Dim);
        meta.MouseFilter = MouseFilterEnum.Ignore;
        var mapped = NeedsUi.HasPlace(w, n);
        _live.Add((meta, w2 => NeedsUi.Meta(w2, n) + (mapped ? "  ·  ◉ on map" : "")));
        col.AddChild(meta);

        var buttons = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        buttons.AddThemeConstantOverride("h_separation", 4);
        buttons.AddThemeConstantOverride("v_separation", 4);
        col.AddChild(buttons);
        if (n.Act != null)
            buttons.AddChild(NeedsUi.Small(n.ActLabel ?? "Do it", () => NeedsUi.Act(_screen, n), "The obvious order (Enter when this row is selected)", Ui.Player));
        var choices = NeedKinds.Choices(w, Side.Player, n);
        var screenChoice = NeedsUi.ScreenChoice(_screen, w, n);
        if (choices.Count > 0)
            buttons.AddChild(NeedsUi.Small(choices[0].Label, () => NeedsUi.Choose(_screen, n, choices[0].Command), choices[0].Tip ?? "The sensible alternative"));
        else if (screenChoice is var (label, open))
            buttons.AddChild(NeedsUi.Small(label, open, "Open the screen where this is settled"));
        var more = new List<(string, Action?, string?)>();
        foreach (var c in choices.Skip(1)) { var cmd = c.Command; more.Add((c.Label, () => NeedsUi.Choose(_screen, n, cmd), c.Tip)); }
        if (choices.Count > 0 && screenChoice is var (label2, open2)) more.Add((label2, open2, null));
        if (n.Area is AutoArea area)
        {
            more.Add(($"{Stewards.AreaName(area)}…", null, null));
            foreach (var level in Enum.GetValues<AutoLevel>())
            {
                var l = level;
                var where = NeedsUi.PlaceOf(w, n) is string p ? $" for {p}" : " everywhere";
                more.Add(($"{level}{where}", () => _screen.IssueNow(new SetAutoLevelCommand(Side.Player, area, l, n.BodyId, n.FleetId) { Source = "player" }, "setting automation"),
                    l switch { AutoLevel.Auto => "Automation does it", AutoLevel.Advise => "Automation suggests it here", _ => "Leave it to me" }));
            }
        }
        if (more.Count > 0) buttons.AddChild(NeedsUi.Menu("⋯", "More choices, and how automation handles this", more));
        if (mapped) buttons.AddChild(NeedsUi.Small("Go", () => { _hud.SelectedNeed = key; _hud.GoTo(n); }, "Fly the map there and open its panel (the tray stays up)"));
        if (n.Area is AutoArea a2 && n.Act != null)
        {
            var where = NeedsUi.PlaceOf(w, n) is string p ? $"for {p}" : "everywhere";
            buttons.AddChild(NeedsUi.Small("⟳ Auto", () => NeedsUi.HandOver(_screen, w, n),
                $"Hand {Stewards.AreaName(a2).ToLowerInvariant()} {where} to automation: it does this itself and the item goes away"));
        }
        buttons.AddChild(NeedsUi.Menu("Snooze ▾", "Put it aside: an hour, a day, until it changes, or never for this kind", NeedsUi.SnoozeItems(_screen, n)));
        _list.AddChild(row);
    }

    private static HBoxContainer Divider(VBoxContainer root)
    {
        root.AddChild(new HSeparator());
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        root.AddChild(head);
        return head;
    }

    private void AdviceStrip(World w, List<NeedItem> advice)
    {
        var head = Divider(_root);
        head.AddChild(NeedsUi.Small($"Advice from automation ({advice.Count}) {(_adviceOpen ? "▾" : "▸")}", () => _adviceOpen = !_adviceOpen,
            "What automation would do in the areas set to Advise", Ui.Player));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var doable = advice.Where(n => n.Act != null).ToList();
        if (doable.Count > 0)
            head.AddChild(NeedsUi.Small($"Do all {doable.Count}", () => { foreach (var n in doable) NeedsUi.Act(_screen, n); }, "Take every suggestion here"));
        head.AddChild(NeedsUi.Small("Hide advice", () =>
        {
            PlayerInfo.SetAdvice(AdviceMode.Off);
            Main.Instance.Toast("Advice hidden: only trouble reaches the tray now (Display options brings it back)");
        }, "Only trouble reaches the tray; Display options brings advice back"));
        if (!_adviceOpen) return;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        _root.AddChild(box);
        foreach (var n in advice.Take(8))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            var text = Ui.Label(n.Text + (NeedsUi.PlaceOf(w, n) is string p && !n.Text.Contains(p) ? $"  ·  {p}" : ""), 13);
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            text.CustomMinimumSize = new Vector2(300, 0);
            text.TooltipText = $"{NeedsUi.AreaShort(n.Area)} · {NeedKinds.Bites(w, Side.Player, n)}";
            text.MouseFilter = MouseFilterEnum.Pass;
            row.AddChild(text);
            var item = n;
            if (n.Act != null) row.AddChild(NeedsUi.Small(n.ActLabel ?? "Do it", () => NeedsUi.Act(_screen, item), "Take this suggestion", Ui.Player));
            if (NeedsUi.HasPlace(w, n)) row.AddChild(NeedsUi.Small("Go", () => _hud.GoTo(item), "Fly the map there"));
            row.AddChild(NeedsUi.Menu("Not this kind ▾", "Put it aside, or stop suggesting this kind", NeedsUi.SnoozeItems(_screen, n).AsEnumerable().Reverse().ToList()));
            box.AddChild(row);
        }
        if (advice.Count > 8) box.AddChild(Ui.Label($"and {advice.Count - 8} more (Empire › Needs you lists them all)", 12, Ui.Dim));
    }

    private void AutoStrip(World w, List<AutoNote> today)
    {
        var worth = today.Count(n => NeedsUi.Worth(n.Area));
        var head = Divider(_root);
        head.AddChild(NeedsUi.Small($"Automation handled {today.Count} today {(_autoOpen ? "▾" : "▸")}", () => _autoOpen = !_autoOpen,
            "Everything automation did today, newest first: nothing it does is silent"));
        if (worth > 0) head.AddChild(Ui.Label($"● {worth} worth a look", 12, NeedsUi.Amber));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(NeedsUi.Small(_hud.AutomationOpen ? "Automation ◂" : "Automation ▸", () => _hud.AutomationOpen = !_hud.AutomationOpen,
            "Hand an area over or take it back: Auto, Advise or Manual for each of the fifteen areas"));
        if (!_autoOpen) return;
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        _root.AddChild(box);
        if (today.Count == 0) box.AddChild(Ui.Label("Nothing yet today.", 12, Ui.Dim));
        foreach (var note in today.Take(8))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 5);
            row.AddChild(Ui.Label(NeedsUi.Worth(note.Area) ? "●" : " ", 12, NeedsUi.Amber));
            row.AddChild(Ui.Label(NeedsUi.Clock(note.Hour), 12, Ui.Dim));
            var text = Ui.Label(note.Text, 13);
            text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            text.CustomMinimumSize = new Vector2(280, 0);
            row.AddChild(text);
            row.AddChild(Ui.Label(Stewards.AreaName(note.Area), 11, Ui.Dim));
            var n = note;
            var level = Stewards.LevelFor(w, Side.Player, note.Area, note.BodyId, note.FleetId);
            var place = note.FleetId is int f && w.FindFleet(f) is Fleet fl ? fl.Name : note.BodyId is int b && w.FindBody(b) is Body body ? body.Name : null;
            var take = NeedsUi.Small(level == AutoLevel.Auto ? "Take it back" : "Taken back", () =>
            {
                _screen.IssueNow(new SetAutoLevelCommand(Side.Player, n.Area, AutoLevel.Advise, n.BodyId, n.FleetId) { Source = "player" }, "taking automation back");
                Main.Instance.Toast($"{Stewards.AreaName(n.Area)}{(place != null ? $" for {place}" : "")} set back to Advise: the next one comes to you in the tray");
            }, $"Undoes nothing, but sets {Stewards.AreaName(note.Area).ToLowerInvariant()}{(place != null ? $" for {place}" : "")} back to Advise, so the next one comes to you");
            take.Disabled = level != AutoLevel.Auto;
            row.AddChild(take);
            if (note.FleetId is int fid && w.FindFleet(fid) != null) row.AddChild(NeedsUi.Small("Go", () => _screen.PickFleet(fid), "Fly the map there"));
            else if (note.BodyId is int bid) row.AddChild(NeedsUi.Small("Go", () => _screen.PickBody(bid), "Fly the map there"));
            box.AddChild(row);
        }
    }
}

/// <summary>
/// The Automation menu from the tray: each of the fifteen areas Auto, Advise or Manual, a note where a planet
/// or fleet is set apart from its area, and the kinds of item you said never to show again.
/// </summary>
public partial class AutomationMenu : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _root = null!;
    private string _shape = "";

    public AutomationMenu() : this(null!) { }
    public AutomationMenu(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 10);
        _root = new VBoxContainer { CustomMinimumSize = new Vector2(520, 0) };
        _root.AddThemeConstantOverride("separation", 3);
        AddChild(_root);
    }

    public void Refresh(World w)
    {
        var state = Economy.Of(w, Side.Player);
        var shape = EmpireTabs.AutomationShape(w) + "|" + string.Join(",", state.MutedKinds);
        if (shape == _shape || Ui.MouseHeld) return;
        _shape = shape;
        foreach (var c in _root.GetChildren()) c.QueueFree();
        var head = new HBoxContainer();
        head.AddChild(Ui.Label("Automation", 17, Ui.Player));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(NeedsUi.Small("✕", () => Visible = false, "Close"));
        _root.AddChild(head);
        _root.AddChild(Ui.Label("Auto does it · Advise suggests it in the tray · Manual leaves it alone", 12, Ui.Dim));
        foreach (var area in Stewards.Areas)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            var name = Ui.Label(Stewards.AreaName(area), 13);
            name.CustomMinimumSize = new Vector2(150, 0);
            name.TooltipText = Stewards.AreaWhat(area);
            name.MouseFilter = MouseFilterEnum.Pass;
            row.AddChild(name);
            var now = Stewards.LevelFor(w, Side.Player, area);
            foreach (var level in Enum.GetValues<AutoLevel>())
            {
                var l = level;
                var a = area;
                var b = NeedsUi.Small(level.ToString(), () => _screen.IssueNow(new SetAutoLevelCommand(Side.Player, a, l) { Source = "player" }, "setting automation"),
                    Stewards.AreaWhat(area), level == now ? Ui.Player : Ui.Dim);
                row.AddChild(b);
            }
            var apart = state.AutoOverrides.Where(o => o.Area == area).ToList();
            if (apart.Count > 0)
            {
                var planets = apart.Count(o => o.BodyId != null);
                var fleets = apart.Where(o => o.FleetId != null).Select(o => w.FindFleet(o.FleetId!.Value)?.Name).OfType<string>().ToList();
                var note = string.Join(", ", new[]
                {
                    planets > 0 ? $"{planets} planet{(planets == 1 ? "" : "s")}" : null,
                    fleets.Count is > 0 and <= 2 ? string.Join(", ", fleets) : fleets.Count > 2 ? $"{fleets.Count} fleets" : null,
                }.Where(x => x != null)) + " set apart";
                var label = Ui.Label(note, 12, NeedsUi.Amber);
                label.TooltipText = string.Join("\n", apart.Select(o => $"{(o.FleetId is int f ? w.FindFleet(f)?.Name : o.BodyId is int bb ? w.FindBody(bb)?.Name : "?")}: {o.Level}"))
                                    + "\nThe Empire screen's Automation tab clears them";
                label.MouseFilter = MouseFilterEnum.Pass;
                row.AddChild(label);
            }
            _root.AddChild(row);
        }
        _root.AddChild(Ui.Label("Set one planet or fleet apart with ⟳ Auto or ⋯ on its tray row; Take it back sets it to Advise.", 11, Ui.Dim));
        if (state.MutedKinds.Count > 0)
        {
            _root.AddChild(new HSeparator());
            _root.AddChild(Ui.Label("Never shown again", 13, Ui.Text));
            foreach (var kind in state.MutedKinds.ToList())
            {
                var row = new HBoxContainer();
                var k = kind;
                var label = Ui.Label(NeedKinds.KindName(kind), 13);
                label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(label);
                row.AddChild(NeedsUi.Small("Show again", () => _screen.IssueNow(new UnmuteNeedKindCommand(Side.Player, k), "showing a kind again"), "Let this kind of item into Needs you again"));
                _root.AddChild(row);
            }
        }
        Size = Vector2.Zero;
    }
}
