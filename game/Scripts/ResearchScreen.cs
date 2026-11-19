using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The research screen (T). Teams sit in a slim column on the left, each showing what it works on and what comes
/// next. The middle has two tabs: Ideas (a calm first look: three suggested ideas, then every field folded to one
/// line that opens to its cards; Filters brings back the full filter rows) and Web (the discovery web, one field at
/// a time or all). The right-hand pane shows the picked idea in full, or a team's skills, queue and training.
/// Buttons issue commands; the screen rebuilds only when something it shows changes shape, and updates numbers live.
/// </summary>
public partial class ResearchScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _root = null!;
    private string _shape = "";
    private readonly List<Action<World>> _live = new();

    // Filters (kept while the game runs).
    private string _search = "";
    private string? _field;                    // null: all fields
    private readonly HashSet<IdeaState> _states = new() { IdeaState.Available, IdeaState.Queued, IdeaState.Researching, IdeaState.Locked };
    private string? _unlocks;                  // null: everything
    private Sort _sort = Sort.Quickest;
    private ScrollContainer? _ideaScroll;
    private int? _keepScroll;                  // the Ideas scroll place to put back after a rebuild
    private List<string> _suggested = new();
    private static string? _selected;          // the idea shown in the right-hand pane (null: the first suggested)
    private static bool _allSkills;            // the team pane shows every field's skill, not only the kept ones
    private static int? _openTeam;             // the team shown in the right-hand pane instead of an idea
    private static bool _webOpen;              // the Web tab rather than Ideas
    private static bool _showFilters;          // the full filter rows and a flat list of cards
    private static string? _webField;          // the web shows one field (null: all)
    private static readonly HashSet<string> _openFields = new();

    public enum IdeaState { Available, Queued, Researching, Done, Locked }
    private enum Sort { Quickest, Cheapest, Name }

    private static readonly string[] UnlockGroups = { "hulls", "engines", "weapons", "defence", "supply", "sensors", "industry" };

    private const float TeamsWidth = 268;
    private float DetailWidth => Mathf.Clamp(GetViewportRect().Size.X * 0.25f, 330, 420);
    /// <summary>Room inside the middle box for cards (window less the margins, the team column, the pane and a scroll bar).</summary>
    private float CentreWidth => GetViewportRect().Size.X - 40 - TeamsWidth - DetailWidth - 24 - 32 - 16;

    public ResearchScreen() : this(null!) { }
    internal static void OpenWebForShots(bool open) => _webOpen = open;
    internal static void OpenTeamForShots(int? team) => _openTeam = team;
    internal static void SelectForShots(string? idea) => _selected = idea;
    internal static void OpenFieldForShots(string? field)
    {
        _openFields.Clear();
        if (field != null) _openFields.Add(field);
        _webField = field;
    }
    /// <summary>The popup on the game screen opens the full screen on one team.</summary>
    public static void ShowTeam(int team) { _openTeam = team; _webOpen = false; }
    public ResearchScreen(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        var box = new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.045f, 0.97f) };
        box.SetContentMarginAll(20);
        AddThemeStyleboxOverride("panel", box);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _root = new VBoxContainer();
        _root.AddThemeConstantOverride("separation", 12);
        AddChild(_root);
    }

    public void Refresh(Simulation sim)
    {
        var w = sim.World;
        // Fill the window: under a CanvasLayer the anchors alone can leave the screen only as tall as its
        // contents need, and then the idea cards' scroll area gets no height at all.
        var view = GetViewportRect().Size;
        // Under the top bar, so the menu bar stays in view.
        var under = new Vector2(0, Hud.ContentTop);
        if (Position != under || Size != view - under) { Position = under; Size = view - under; }
        var shape = Shape(w);
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            // A rebuild (clicking a card, queueing an idea) used to jump the lists back to the top.
            if (_ideaScroll != null && IsInstanceValid(_ideaScroll)) _keepScroll = _ideaScroll.ScrollVertical;
            Rebuild(w);
        }
        else if (_keepScroll is int place && _ideaScroll != null && IsInstanceValid(_ideaScroll))
        {
            // Put the scroll place back once the new list has its size (a frame after the rebuild).
            _ideaScroll.ScrollVertical = place;
            _keepScroll = null;
        }
        foreach (var update in _live) update(w);
    }

    private string Shape(World w)
    {
        var me = Economy.Of(w, Side.Player);
        return string.Join(",", w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research).Select(o => $"{o.Id}:{o.TeamId}:{o.Paid}"))
               + "|" + string.Join(",", me.Researched) + "|" + string.Join(",", me.Cuts) + "|" + string.Join(",", me.Sparked)
               + "|" + string.Join(",", me.Teams.Select(t => $"{t.Id}:{string.Join("+", t.Kept)}:{t.TrainingUntilHour != null}:{t.TrainingQueued}:{Research.Ready(w, t)}:{t.Scientists}:{string.Join("/", Research.KeptFields(t))}"))
               + "|" + string.Join(",", Intel.Counters(w, Side.Player).Select(c => c.Idea))
               + "|" + string.Join(",", RivalTech.Learning(w, Side.Player).Select(l => $"{l.Id}:{l.Stage}"))
               + $"|{_search}|{_field}|{string.Join(",", _states.Order())}|{_unlocks}|{_sort}|{_selected}|{_allSkills}|{_openTeam}|{_webOpen}"
               + $"|{_showFilters}|{_webField}|{string.Join(",", _openFields.Order())}|{GetViewportRect().Size}|{w.Winner}";
    }

    // ---- Layout --------------------------------------------------------------------------------

    private void Rebuild(World w)
    {
        Ui.ClearChildren(_root);
        _live.Clear();
        var me = Economy.Of(w, Side.Player);
        if (_openTeam is int open && me.Teams.All(t => t.Id != open)) _openTeam = null;
        var available = GameData.Economy.Research.Where(r => Discovered(w, r) && StateOf(w, r.Id) == IdeaState.Available).Select(r => r.Id).ToList();
        _suggested = ResearchValue.Rank(w, Side.Player, ResearchValue.For(w, Side.Player), available).Take(3).Select(x => x.Id).ToList();

        _root.AddChild(Header(w));

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);

        // Left: the teams, always in view.
        var teams = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(TeamsWidth, 0) };
        teams.AddChild(TeamsColumn(w));
        body.AddChild(teams);

        // Middle: Ideas or the web.
        var middle = Box(16);
        middle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        middle.AddChild(_webOpen ? WebPanel(w) : IdeasPanel(w));
        body.AddChild(middle);

        // Right: the picked idea, or a team's skills, queue and training.
        var pane = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(DetailWidth, 0) };
        var paneWidth = DetailWidth - 14;
        if (_openTeam is int t && Research.Team(w, Side.Player, t) is ResearchTeam team) pane.AddChild(TeamCard(w, team, paneWidth));
        else if (Shown(w) is ResearchData idea) pane.AddChild(IdeaDetail(w, idea, paneWidth));
        else pane.AddChild(Wrap(Ui.Label("Pick an idea to see it in full here.", 13, Ui.Dim)));
        body.AddChild(pane);
        _root.AddChild(body);
    }

    /// <summary>The idea in the right-hand pane: the one picked, else the first suggested, else one under way.</summary>
    private ResearchData? Shown(World w)
    {
        var id = _selected ?? _suggested.FirstOrDefault()
                 ?? w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research).Select(o => o.Key).FirstOrDefault();
        return id != null && GameData.ResearchById.TryGetValue(id, out var r) ? r : null;
    }

    private Control Header(World w)
    {
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        top.AddChild(Ui.Label("Research", 26, new Color("f2f5fa")));
        top.AddChild(new Control { CustomMinimumSize = new Vector2(TeamsWidth - 150, 0) });
        top.AddChild(Tab("Ideas", !_webOpen, () => _webOpen = false, "The ideas you know of: suggested ones first, then each field"));
        top.AddChild(Tab("Web", _webOpen, () => _webOpen = true, "The discovery web: what leads to what, one field at a time"));
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (!_webOpen)
        {
            var search = new LineEdit { Text = _search, PlaceholderText = "Search ideas (Enter)", CustomMinimumSize = new Vector2(220, 32) };
            search.TextSubmitted += text => _search = text.Trim();
            top.AddChild(search);
            if (_search.Length > 0) top.AddChild(Ui.SmallButton("✕", () => _search = "", "Clear the search"));
            var filtered = _field != null || _unlocks != null || _sort != Sort.Quickest || _states.Count != 4 || _states.Contains(IdeaState.Done);
            var filters = Ui.SmallButton(_showFilters ? "Filters ▴" : filtered ? "Filters ● ▾" : "Filters ▾", () => _showFilters = !_showFilters,
                _showFilters ? "Hide the filters and go back to the calm view" : "Filter by field, status and what an idea unlocks, and sort");
            filters.Modulate = _showFilters || filtered ? Ui.Player : new Color(1, 1, 1, 0.8f);
            top.AddChild(filters);
        }
        var industry = Ui.Label("", 14, Ui.Text);
        industry.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _live.Add(w2 => industry.Text = $"Industry {Economy.Of(w2, Side.Player).Industry:0}");
        top.AddChild(industry);
        top.AddChild(Ui.SmallButton("Back to map", () => _screen.ShowResearch = false, "Close the research screen (T or Esc)"));
        return top;
    }

    private static Button Tab(string text, bool on, Action press, string tip)
    {
        var b = Ui.SmallButton(text, press, tip);
        b.CustomMinimumSize = new Vector2(90, 34);
        b.AddThemeFontSizeOverride("font_size", 16);
        b.AddThemeColorOverride("font_color", on ? Ui.Player : Ui.Dim);
        if (on)
        {
            var line = new StyleBoxFlat { BgColor = new Color("0f1a28"), BorderColor = Ui.Player };
            line.BorderWidthBottom = 2;
            line.SetContentMarginAll(6);
            b.AddThemeStyleboxOverride("normal", line);
        }
        return b;
    }

    /// <summary>A label that cuts its text to the room it gets (whole text in the tooltip), so it never widens the screen.</summary>
    private static Label Fit(Label l)
    {
        l.ClipText = true;
        l.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.HorizontalAlignment = HorizontalAlignment.Right;
        l.TooltipText = l.Text;
        l.MouseFilter = MouseFilterEnum.Pass;
        return l;
    }

    /// <summary>A label cut to its room on the left (whole text in the tooltip).</summary>
    private static Label Clip(Label l)
    {
        Fit(l);
        l.HorizontalAlignment = HorizontalAlignment.Left;
        return l;
    }

    /// <summary>A label drawn over a painted picture: a dark outline keeps it readable on bright skies and planets.</summary>
    private static Label OnPicture(Label l)
    {
        l.AddThemeConstantOverride("outline_size", 5);
        l.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.85f));
        return l;
    }

    /// <summary>A label that wraps onto more lines rather than widening the screen.</summary>
    private static Label Wrap(Label l)
    {
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    private static PanelContainer Box(int padding, Color? border = null, bool dashed = false)
    {
        var p = new PanelContainer();
        var box = new StyleBoxFlat { BgColor = dashed ? new Color("080b11") : new Color("0c111a"), BorderColor = border ?? new Color("1a2230") };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(8);
        box.SetContentMarginAll(padding);
        p.AddThemeStyleboxOverride("panel", box);
        return p;
    }

    private static Control Bar(float width, Color color, Func<double> share, List<Action<World>> live)
    {
        var back = new ColorRect { Color = new Color("1a2230"), CustomMinimumSize = new Vector2(width, 6), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        var fill = new ColorRect { Color = color, Size = new Vector2(0, 6) };
        back.AddChild(fill);
        live.Add(_ => fill.Size = new Vector2((float)(Math.Clamp(share(), 0, 1) * back.Size.X), 6));
        return back;
    }

    private static TextureRect Picture(ResearchData r, float width)
    {
        return new TextureRect
        {
            Texture = Art.IdeaPicture(r.Id, r.Picture),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(width, width * 12 / 35), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore,
            TextureFilter = TextureFilterEnum.LinearWithMipmaps,
        };
    }

    /// <summary>What a team's first idea shows: days left, or why it is held up.</summary>
    private static (string Text, bool Stuck) Progress(World w, WorkOrder? first)
    {
        if (first == null) return ("idle", false);
        var stuck = !first.Paid && first.Waiting != null;
        return (first.Paid ? $"{Research.DaysUntilDone(w, first):0.#} d" : stuck ? $"WAITING: {first.Waiting}" : "starts next hour", stuck);
    }

    /// <summary>"Training +4 in 3.2 d · 2 more queued": empty when nothing is training or queued.</summary>
    public static string TrainingText(World w, ResearchTeam team)
    {
        var parts = new List<string>();
        if (team.TrainingUntilHour is double h) parts.Add($"Training +{team.TrainingScientists:0} in {(h - w.Hour) / 24:0.#} d");
        if (team.TrainingQueued > 0)
        {
            var waiting = Research.TrainingWaiting(w, Side.Player, team);
            parts.Add($"{team.TrainingQueued} {(team.TrainingUntilHour != null ? "more " : "")}training{(team.TrainingQueued == 1 ? "" : "s")} queued"
                      + (waiting != null && team.TrainingUntilHour == null ? $" (WAITING: {waiting})" : ""));
        }
        return string.Join(" · ", parts);
    }

    // ---- Teams ---------------------------------------------------------------------------------

    private Control TeamsColumn(World w)
    {
        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 10);
        var me = Economy.Of(w, Side.Player);
        var head = new HBoxContainer();
        head.AddChild(Ui.Label("TEAMS", 11, Ui.Dim));
        head.AddChild(Fit(Ui.Label($"{me.Teams.Count} of {Research.T.Slots} slots", 11, Ui.Dim)));
        v.AddChild(head);
        foreach (var team in me.Teams) v.AddChild(TeamTile(w, team));
        if (me.Teams.Count < Research.T.Slots) v.AddChild(EmptySlot(w));
        var locked = Research.T.Slots - me.Teams.Count - 1;
        if (locked > 0)
            v.AddChild(Wrap(Ui.Label($"{locked} more slot{(locked == 1 ? "" : "s")} {(locked == 1 ? "opens" : "open")} once the one before is filled", 11, Ui.Dim)));
        v.AddChild(Wrap(Ui.Label("Speed = skill × size. Fields a team doesn't keep fade over time.", 11, new Color("566275"))));
        return v;
    }

    /// <summary>A team in the left column: what it works on now (with its picture), what comes next, and training.</summary>
    private Control TeamTile(World w, ResearchTeam team)
    {
        var open = _openTeam == team.Id;
        var card = Box(10, open ? Ui.Player : new Color("12324a"));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 5);
        card.AddChild(v);
        var inner = TeamsWidth - 34;

        var head = new HBoxContainer();
        head.AddChild(Ui.Label(team.Name, 15, Ui.Player));
        var size = Fit(Ui.Label("", 12, Ui.Dim));
        _live.Add(w2 => size.Text = $"{team.Scientists:0} scientists" + (!Research.Ready(w2, team) ? $" · ready in {(team.ReadyHour - w2.Hour) / 24:0.#} d" : ""));
        head.AddChild(size);
        v.AddChild(head);

        var queue = Research.QueueOf(w, Side.Player, team.Id).ToList();
        var first = queue.FirstOrDefault();
        if (first != null)
        {
            var data = GameData.Research(first.Key);
            var pic = Picture(data, inner);
            pic.MouseFilter = MouseFilterEnum.Stop;
            pic.TooltipText = "Show this idea on the right";
            pic.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) { _selected = first.Key; _openTeam = null; } };
            v.AddChild(pic);
            v.AddChild(Clip(Ui.Label(data.Name, 13, new Color("f2f5fa"))));
        }
        else v.AddChild(Wrap(Ui.Label("Nothing queued: pick an idea and Queue it", 12, Ui.Dim)));
        var now = new HBoxContainer();
        now.AddThemeConstantOverride("separation", 8);
        var bar = Bar(80, Ui.Player, () => first == null ? 0 : 1 - first.HoursLeft / Math.Max(1e-6, first.TotalHours), _live);
        bar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        now.AddChild(bar);
        var left = Fit(Ui.Label("", 12, Ui.Dim));
        left.SizeFlagsStretchRatio = 0.9f;
        _live.Add(w2 =>
        {
            var (text, stuck) = Progress(w2, first);
            left.Text = text;
            left.TooltipText = text;
            left.AddThemeColorOverride("font_color", stuck ? Ui.Danger : Ui.Dim);
        });
        now.AddChild(left);
        if (first != null) v.AddChild(now);

        foreach (var o in queue.Skip(1).Take(3)) v.AddChild(Clip(Ui.Label("then " + GameData.Research(o.Key).Name, 12, Ui.Text)));
        if (queue.Count > 4) v.AddChild(Ui.Label($"+{queue.Count - 4} more", 11, Ui.Dim));

        var training = Wrap(Ui.Label("", 11, Ui.Dim));
        _live.Add(w2 => { training.Text = TrainingText(w2, team); training.Visible = training.Text.Length > 0; });
        v.AddChild(training);

        var more = Ui.SmallButton(open ? "◂ Shown on the right" : "Skills, queue, train ▸", () => _openTeam = open ? null : team.Id,
            open ? "Show the picked idea on the right again" : "Show this team on the right: skills, kept fields, queue order, train and split");
        more.AddThemeFontSizeOverride("font_size", 13);
        more.CustomMinimumSize = new Vector2(0, 28);
        if (open) more.Modulate = Ui.Player;
        v.AddChild(more);
        return card;
    }

    /// <summary>A team in full, in the right-hand pane: skills and kept fields, the queue in order, training and splitting.</summary>
    private Control TeamCard(World w, ResearchTeam team, float width)
    {
        var card = Box(14, Ui.Player);
        card.CustomMinimumSize = new Vector2(width, 0);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        card.AddChild(v);

        var head = new HBoxContainer();
        head.AddChild(Ui.Label(team.Name, 18, Ui.Player));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.SmallButton("✕", () => _openTeam = null, "Close the team and show the picked idea again"));
        v.AddChild(head);
        var size = Wrap(Ui.Label("", 13, Ui.Text));
        _live.Add(w2 => size.Text = $"{team.Scientists:0} scientists · {team.Ideas} ideas finished"
            + (!Research.Ready(w2, team) ? $" · ready in {(team.ReadyHour - w2.Hour) / 24:0.#} d" : ""));
        v.AddChild(size);

        // Folded by default to the kept (primary) fields; one click shows them all.
        var kept = Research.KeptFields(team).ToList();
        var skillHead = new HBoxContainer();
        skillHead.AddChild(Ui.Label("SKILL", 11, Ui.Dim));
        skillHead.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var hidden = Research.Fields.Length - kept.Count;
        var fadingCount = Enumerable.Range(0, Research.Fields.Length).Count(i => !kept.Contains(i) && team.Skill[i] > 1.0001);
        var allButton = Ui.SmallButton(_allSkills ? "▴ Kept only" : $"▸ All fields ({hidden} more{(fadingCount > 0 ? $", {fadingCount} fading" : "")})",
            () => _allSkills = !_allSkills, _allSkills ? "Show only the kept fields" : "Show every field's skill, and pick which to keep");
        allButton.AddThemeFontSizeOverride("font_size", 12);
        skillHead.AddChild(allButton);
        v.AddChild(skillHead);
        if (_allSkills) v.AddChild(Ui.Label("★ kept · ↓ fading · click ☆ to keep a field (up to 2)", 11, Ui.Dim));
        for (var i = 0; i < Research.Fields.Length; i++)
        {
            if (!_allSkills && !kept.Contains(i)) continue;
            var fi = i;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var keep = Ui.SmallButton(kept.Contains(i) ? "★" : "☆", () => ToggleKept(team, Research.Fields[fi]),
                team.Kept.Count == 0 && kept.Contains(i) ? "Kept for now as the team's best field; click to keep a field yourself (up to 2)" : "Keep this field (up to 2); fields not kept fade back to 1x");
            keep.CustomMinimumSize = new Vector2(30, 26);
            keep.Modulate = kept.Contains(i) ? Ui.Player : new Color(1, 1, 1, 0.5f);
            row.AddChild(keep);
            var name = Ui.Label(Research.FieldNames[i], 13, Ui.Text);
            name.CustomMinimumSize = new Vector2(140, 0);
            row.AddChild(name);
            var bar = Bar(60, kept.Contains(i) ? Ui.Player : new Color("8a96a8"), () => (team.Skill[fi] - 1) / Math.Max(0.01, Research.T.MaxSkill - 1), _live);
            bar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(bar);
            var value = Ui.Label("", 13, Ui.Text);
            value.CustomMinimumSize = new Vector2(56, 0);
            value.HorizontalAlignment = HorizontalAlignment.Right;
            _live.Add(_ =>
            {
                var fading = !kept.Contains(fi) && team.Skill[fi] > 1.0001;
                value.Text = $"{team.Skill[fi]:0.0#}x" + (fading ? " ↓" : "");
                value.AddThemeColorOverride("font_color", fading ? Ui.Rival : Ui.Text);
            });
            row.AddChild(value);
            v.AddChild(row);
        }

        v.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        v.AddChild(Ui.Label("QUEUE", 11, Ui.Dim));
        var queue = Research.QueueOf(w, Side.Player, team.Id).ToList();
        if (queue.Count == 0) v.AddChild(Wrap(Ui.Label("Nothing queued: pick an idea and Queue it", 12, Ui.Dim)));
        var others = Economy.Of(w, Side.Player).Teams.Where(t => t.Id != team.Id).ToList();
        for (var q = 0; q < queue.Count; q++)
        {
            var order = queue[q];
            var first = q == 0;
            var data = GameData.Research(order.Key);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);
            var name = Clip(Ui.Label($"{q + 1}. {data.Name}", 13, first ? new Color("f2f5fa") : Ui.Text));
            row.AddChild(name);
            var days = Fit(Ui.Label("", 12, Ui.Dim));
            days.SizeFlagsStretchRatio = 0.5f;
            _live.Add(w2 =>
            {
                var stuck = !order.Paid && first && order.Waiting != null;
                days.Text = order.Paid || !first ? $"{Research.DaysUntilDone(w2, order):0.#} d" : stuck ? $"WAITING: {order.Waiting}" : "starts next hour";
                days.TooltipText = days.Text;
                days.AddThemeColorOverride("font_color", stuck ? Ui.Danger : Ui.Dim);
            });
            row.AddChild(days);
            if (queue.Count > 1)
            {
                var id = order.Id;
                var up = Ui.SmallButton("▲", () => _screen.IssueNow(new MoveResearchOrderCommand(Side.Player, id, -1), "moving research up"), "Move up the queue (progress is kept)");
                up.Disabled = q == 0;
                up.CustomMinimumSize = new Vector2(30, 28);
                row.AddChild(up);
                var down = Ui.SmallButton("▼", () => _screen.IssueNow(new MoveResearchOrderCommand(Side.Player, id, 1), "moving research down"), "Move down the queue (progress is kept)");
                down.Disabled = q == queue.Count - 1;
                down.CustomMinimumSize = new Vector2(30, 28);
                row.AddChild(down);
            }
            if (others.Count > 0)
            {
                var move = new MenuButton { Text = "⇄", TooltipText = "Move to another team (progress is kept)", FocusMode = FocusModeEnum.None, Flat = false };
                foreach (var o in others) move.GetPopup().AddItem($"Move to {o.Name}", o.Id);
                move.GetPopup().IdPressed += id => _screen.IssueNow(new SetOrderTeamCommand(Side.Player, order.Id, (int)id), "moving research");
                row.AddChild(move);
            }
            var cancel = Ui.SmallButton("✕", () => _screen.CancelOrder(order.Id), "Take off the queue");
            cancel.CustomMinimumSize = new Vector2(30, 28);
            row.AddChild(cancel);
            v.AddChild(row);
            if (first) v.AddChild(Bar(200, Ui.Player, () => 1 - order.HoursLeft / Math.Max(1e-6, order.TotalHours), _live));
        }

        // Training: one batch at a time; more can be queued and each starts by itself when it can be paid for.
        v.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        v.AddChild(Ui.Label("TRAINING", 11, Ui.Dim));
        var status = Wrap(Ui.Label("", 12, Ui.Text));
        v.AddChild(status);
        var trainRow = new HBoxContainer();
        trainRow.AddThemeConstantOverride("separation", 6);
        const string trainTip = "More scientists: a bigger team works faster (twice the size is about 1.7x). Paid from the Buildings pot of the Industry budget when "
                                + "the training starts. Queue more and each starts by itself after the one before";
        var train = Ui.SmallButton("", () => _screen.IssueNow(new TrainTeamCommand(Side.Player, team.Id), "training scientists"), trainTip);
        train.AddThemeFontSizeOverride("font_size", 13);
        train.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        train.ClipText = true;
        trainRow.AddChild(train);
        var less = Ui.SmallButton("− one", () => _screen.IssueNow(new CancelTrainingCommand(Side.Player, team.Id), "cancelling a training"),
            "Take one queued training off (one under way carries on)");
        less.AddThemeFontSizeOverride("font_size", 13);
        trainRow.AddChild(less);
        v.AddChild(trainRow);
        _live.Add(w2 =>
        {
            var text = TrainingText(w2, team);
            status.Text = text.Length > 0 ? text : $"+{Research.T.TrainScientists:0} scientists for {Research.TrainCost(w2, Side.Player):0} Industry, "
                                                   + $"{Research.T.TrainDays * Tech.Factor(w2, Side.Player, "scientist-training"):0} days";
            status.AddThemeColorOverride("font_color", text.Contains("WAITING") ? Ui.Danger : Ui.Text);
            var now = Research.CanTrain(w2, Side.Player, team.Id) == null && team.TrainingQueued == 0;
            var why = Research.CanQueueTraining(w2, Side.Player, team.Id);
            train.Text = now ? $"Train (+{Research.T.TrainScientists:0} for {Research.TrainCost(w2, Side.Player):0} Industry)"
                : $"Queue a training (+{Research.T.TrainScientists:0}, {Research.TrainCost(w2, Side.Player):0} Industry when it starts)";
            train.Disabled = why != null;
            train.TooltipText = why != null ? $"Can't: {why}\n{trainTip}" : trainTip;
            less.Visible = team.TrainingQueued > 0;
        });

        // Split: always shown, with the reason when it can't be done (too few scientists, or no empty slot).
        const string splitTip = "Move half the scientists into an empty slot; the new team takes half of each skill and keeps the field you pick";
        var split = new MenuButton { Text = "Split team ▾", TooltipText = splitTip, FocusMode = FocusModeEnum.None, Flat = false };
        for (var i = 0; i < Research.Fields.Length; i++) split.GetPopup().AddItem($"New team keeps {Research.FieldNames[i]}", i);
        split.GetPopup().IdPressed += id => _screen.IssueNow(new SplitTeamCommand(Side.Player, team.Id, Research.Fields[id]), "splitting a team");
        v.AddChild(split);
        var cant = Wrap(Ui.Label("", 11, Ui.Dim));
        v.AddChild(cant);
        _live.Add(w2 =>
        {
            var why = Research.CanSplit(w2, Side.Player, team.Id, Research.Fields[0]);
            split.Disabled = why != null;
            split.TooltipText = why != null ? $"Can't split: {why}\n{splitTip}" : splitTip;
            cant.Text = why != null ? $"Split: {why}" : "";
            cant.Visible = why != null;
        });
        return card;
    }

    private void ToggleKept(ResearchTeam team, string field)
    {
        var kept = team.Kept.ToList();
        if (kept.Contains(field)) kept.Remove(field);
        else
        {
            kept.Add(field);
            if (kept.Count > 2) kept.RemoveAt(0);
        }
        _screen.IssueNow(new SetTeamKeptCommand(Side.Player, team.Id, kept), "choosing kept fields");
    }

    /// <summary>The next empty slot, folded to one small box: found a team, or split one off.</summary>
    private Control EmptySlot(World w)
    {
        var card = Box(10, new Color("2a3446"), dashed: true);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        card.AddChild(v);
        v.AddChild(Ui.Label("Empty team slot", 13, new Color("aab4c3")));
        var t = Research.T;
        card.TooltipText = $"Found a new team: {t.FoundScientists:0} scientists, {t.FoundSkill:0.#}x in the speciality you pick, ready in {t.FoundDays:0} days. "
                           + "Or split one off an existing team (it takes half of each skill).";
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var found = new MenuButton { Text = $"+ Found · {t.FoundCost:0} Industry ▾", FocusMode = FocusModeEnum.None, Flat = false };
        found.AddThemeFontSizeOverride("font_size", 13);
        for (var i = 0; i < Research.Fields.Length; i++) found.GetPopup().AddItem($"Speciality: {Research.FieldNames[i]}", i);
        found.GetPopup().IdPressed += id => _screen.IssueNow(new FoundTeamCommand(Side.Player, Research.Fields[id]), "founding a team");
        _live.Add(w2 =>
        {
            var why = Research.CanFound(w2, Side.Player, Research.Fields[0]);
            found.Disabled = why != null;
            found.TooltipText = why != null ? $"Can't: {why}" : "Paid at once from the Buildings pot of the Industry budget";
        });
        row.AddChild(found);
        var split = new MenuButton { Text = "Split ▾", FocusMode = FocusModeEnum.None, Flat = false, TooltipText = "Split a new team off one you have" };
        split.AddThemeFontSizeOverride("font_size", 13);
        var choices = new List<(int Team, string Field)>();
        foreach (var team in Economy.Of(w, Side.Player).Teams)
            for (var i = 0; i < Research.Fields.Length; i++)
            {
                split.GetPopup().AddItem($"From {team.Name}: new team keeps {Research.FieldNames[i]}", choices.Count);
                choices.Add((team.Id, Research.Fields[i]));
            }
        split.GetPopup().IdPressed += id => _screen.IssueNow(new SplitTeamCommand(Side.Player, choices[(int)id].Team, choices[(int)id].Field), "splitting a team");
        row.AddChild(split);
        v.AddChild(row);
        return card;
    }

    // ---- Ideas ---------------------------------------------------------------------------------

    public static IdeaState StateOf(World w, string id)
    {
        if (Tech.Has(w, Side.Player, id)) return IdeaState.Done;
        var order = w.Orders.Find(o => o.Side == Side.Player && o.Kind == OrderKind.Research && o.Key == id);
        if (order != null)
            return order.TeamId is int t && Research.QueueOf(w, Side.Player, t).FirstOrDefault() == order && order.Paid ? IdeaState.Researching : IdeaState.Queued;
        return Research.Missing(w, Side.Player, id) != null || GameData.Research(id).Later ? IdeaState.Locked : IdeaState.Available;
    }

    /// <summary>Has the player come across this idea (so it shows on the screen)? Later-build ideas never show.</summary>
    public static bool Discovered(World w, ResearchData r) =>
        !r.Later && (StateOf(w, r.Id) != IdeaState.Locked || Research.Seen(w, Side.Player, r.Id));

    /// <summary>Days the idea would take the team that would finish it first (counting its queue).</summary>
    private static (ResearchTeam? Team, double Days) Quickest(World w, string id)
    {
        var team = Research.BestTeamFor(w, Side.Player, id);
        if (team == null) return (null, double.PositiveInfinity);
        return (team, Research.BaseHours(w, Side.Player, id) / 24 / Math.Max(1e-6, Research.Speed(team, GameData.Research(id).Field)));
    }

    private static int Order(World w, ResearchData r) =>
        StateOf(w, r.Id) switch { IdeaState.Researching => 0, IdeaState.Queued => 1, IdeaState.Available => 2, IdeaState.Locked => 3, _ => 4 };

    private Control IdeasPanel(World w)
    {
        var scroll = _ideaScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(v);

        // Intelligence hints: what the rival has been seen to field, and the idea that answers it.
        var counters = Intel.Counters(w, Side.Player).GroupBy(c => c.Idea).Select(g => g.First()).Take(3).ToList();
        if (counters.Count > 0)
            v.AddChild(Wrap(Ui.Label("Intel: " + string.Join(" · ", counters.Select(c => $"{c.Why} → {GameData.Research(c.Idea).Name}")), 12, new Color("ffd27a"))));
        var learning = RivalTech.Learning(w, Side.Player).Take(4).ToList();
        if (learning.Count > 0)
            v.AddChild(Wrap(Ui.Label("Their research we are learning: " + string.Join(" · ", learning.Select(l =>
                l.Stage == RivalStage.Seen ? $"something unknown ({l.Progress:P0})" : $"{GameData.Research(l.Id).Name} ({RivalTech.StageName(l.Stage)})")), 12, Ui.Rival)));

        if (_showFilters || _search.Length > 0) Filtered(w, v);
        else Calm(w, v);
        return scroll;
    }

    /// <summary>The first look: three suggested ideas, big; then every field folded to one line that opens to its cards.</summary>
    private void Calm(World w, VBoxContainer v)
    {
        var width = CentreWidth;
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(Ui.Label("Suggested next", 18, new Color("f2f5fa")));
        var why = ResearchValue.Pressing(ResearchValue.Read(w, Side.Player));
        var note = Clip(Ui.Label(why is var (_, reason) ? $"picked for what your empire needs now: {reason}" : "picked for what your empire needs now", 12, Ui.Dim));
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(note);
        v.AddChild(head);
        if (_suggested.Count == 0) v.AddChild(Ui.Label("Every idea you can start is already queued.", 13, Ui.Dim));
        else
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var cardWidth = (width - 24) / 3;
            foreach (var id in _suggested) row.AddChild(IdeaCard(w, GameData.Research(id), cardWidth));
            v.AddChild(row);
        }

        v.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        var allHead = new HBoxContainer();
        allHead.AddChild(Ui.Label("Every idea, by field", 16, new Color("f2f5fa")));
        allHead.AddChild(Fit(Ui.Label("click a field to open it · Filters for the full list", 12, Ui.Dim)));
        v.AddChild(allHead);
        var all = GameData.Economy.Research.Where(r => Discovered(w, r)).ToList();
        for (var i = 0; i < Research.Fields.Length; i++)
        {
            var field = Research.Fields[i];
            var ideas = all.Where(r => r.Field == field).ToList();
            if (ideas.Count == 0) continue;
            var open = _openFields.Contains(field);
            v.AddChild(Shelf(w, i, ideas, open, width));
            if (!open) continue;
            var grid = Grid(width);
            var cardWidth = width / grid.Columns - 12;
            foreach (var r in ideas.Where(r => StateOf(w, r.Id) != IdeaState.Done).OrderBy(r => Order(w, r)).ThenBy(r => Quickest(w, r.Id).Days))
                grid.AddChild(IdeaCard(w, r, cardWidth));
            v.AddChild(grid);
            var done = ideas.Where(r => StateOf(w, r.Id) == IdeaState.Done).Select(r => r.Name + " ✓").ToList();
            if (done.Count > 0) v.AddChild(Wrap(Ui.Label("Done: " + string.Join(" · ", done), 12, Ui.Dim)));
        }
    }

    /// <summary>A field folded to one line: its name, how many ideas are ready, under way and done, and a few small pictures.</summary>
    private Control Shelf(World w, int fieldIndex, List<ResearchData> ideas, bool open, float width)
    {
        var field = Research.Fields[fieldIndex];
        var color = DiscoveryWeb.FieldColors[fieldIndex % DiscoveryWeb.FieldColors.Length];
        var b = new Button { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(0, 56),
            TooltipText = open ? "Fold this field away" : "Open this field: every idea in it" };
        b.Pressed += () => { if (!_openFields.Remove(field)) _openFields.Add(field); };
        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.OffsetLeft = 14;
        row.OffsetRight = -10;
        row.AddThemeConstantOverride("separation", 12);
        b.AddChild(row);
        var name = Ui.Label($"{(open ? "▾" : "▸")}  {Research.FieldNames[fieldIndex]}", 15, color);
        name.CustomMinimumSize = new Vector2(210, 0);
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        name.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(name);
        int Count(IdeaState s) => ideas.Count(r => StateOf(w, r.Id) == s);
        var ready = Count(IdeaState.Available);
        var busy = Count(IdeaState.Queued) + Count(IdeaState.Researching);
        var parts = new List<string> { $"{ready} ready" };
        if (busy > 0) parts.Add($"{busy} under way");
        parts.Add($"{Count(IdeaState.Done)} done");
        if (Count(IdeaState.Locked) is var locked and > 0) parts.Add($"{locked} later");
        var counts = Ui.Label(string.Join(" · ", parts), 12, ready > 0 ? Ui.Text : Ui.Dim);
        counts.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        counts.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        counts.ClipText = true;
        counts.MouseFilter = MouseFilterEnum.Ignore;
        row.AddChild(counts);
        if (!open)
        {
            var thumbs = ideas.Where(r => StateOf(w, r.Id) == IdeaState.Available).OrderBy(r => Quickest(w, r.Id).Days).Take(width > 900 ? 4 : 3);
            foreach (var r in thumbs)
            {
                var pic = Picture(r, 112);
                pic.CustomMinimumSize = new Vector2(112, 38);
                pic.SizeFlagsVertical = SizeFlags.ShrinkCenter;
                row.AddChild(pic);
            }
        }
        return b;
    }

    private GridContainer Grid(float width)
    {
        var grid = new GridContainer { Columns = Math.Max(1, (int)(width / 270)) };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);
        return grid;
    }

    /// <summary>The full list: filter rows (when Filters is open) and every idea that matches, as cards.</summary>
    private void Filtered(World w, VBoxContainer v)
    {
        var all = GameData.Economy.Research.Where(r => Discovered(w, r)).ToList();
        var shown = all.Where(r => (_field == null || r.Field == _field) && (_unlocks == null || r.Unlocks == _unlocks)
                                   && _states.Contains(StateOf(w, r.Id))
                                   && (_search.Length == 0 || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                       || r.Effect.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToList();
        shown = _sort switch
        {
            Sort.Cheapest => shown.OrderBy(r => r.Days * Research.Pace(r.Tier)).ThenBy(r => r.Name).ToList(),
            Sort.Name => shown.OrderBy(r => r.Name).ToList(),
            _ => shown.OrderBy(r => Order(w, r)).ThenBy(r => Quickest(w, r.Id).Days).ToList(),
        };

        var head = new HBoxContainer();
        head.AddChild(Ui.Label(_search.Length > 0 ? $"Ideas matching “{_search}”" : "All ideas", 16, new Color("f2f5fa")));
        head.AddChild(Fit(Ui.Label($"{shown.Count} of {all.Count} shown · days are for the team that would finish first", 12, Ui.Dim)));
        v.AddChild(head);

        if (_showFilters)
        {
            // Flow rows wrap when the middle is narrow, instead of pushing the screen past the window's edge.
            var row1 = new HFlowContainer();
            row1.AddThemeConstantOverride("h_separation", 6);
            row1.AddThemeConstantOverride("v_separation", 6);
            row1.AddChild(Chip("All fields", _field == null, () => _field = null));
            for (var i = 0; i < Research.Fields.Length; i++)
            {
                var f = Research.Fields[i];
                row1.AddChild(Chip($"{Research.FieldNames[i]} {all.Count(r => r.Field == f)}", _field == f, () => _field = _field == f ? null : f));
            }
            var sort = new OptionButton { FocusMode = FocusModeEnum.None };
            sort.AddItem("Quickest first", (int)Sort.Quickest);
            sort.AddItem("Shortest first", (int)Sort.Cheapest);
            sort.AddItem("By name", (int)Sort.Name);
            sort.Select((int)_sort);
            sort.ItemSelected += i => _sort = (Sort)(int)i;
            row1.AddChild(sort);
            v.AddChild(row1);

            var row2 = new HFlowContainer();
            row2.AddThemeConstantOverride("h_separation", 6);
            row2.AddThemeConstantOverride("v_separation", 6);
            row2.AddChild(Ui.Label("Status", 12, Ui.Dim));
            foreach (var s in Enum.GetValues<IdeaState>())
            {
                var st = s;
                row2.AddChild(Chip($"{s} {all.Count(r => StateOf(w, r.Id) == s)}", _states.Contains(s), () => { if (!_states.Remove(st)) _states.Add(st); }));
            }
            row2.AddChild(new Control { CustomMinimumSize = new Vector2(12, 0) });
            row2.AddChild(Ui.Label("Unlocks", 12, Ui.Dim));
            foreach (var g in UnlockGroups)
                row2.AddChild(Chip(char.ToUpperInvariant(g[0]) + g[1..], _unlocks == g, () => _unlocks = _unlocks == g ? null : g));
            row2.AddChild(Ui.SmallButton("Reset", () =>
            {
                _field = null;
                _unlocks = null;
                _sort = Sort.Quickest;
                _states.Clear();
                _states.UnionWith(new[] { IdeaState.Available, IdeaState.Queued, IdeaState.Researching, IdeaState.Locked });
            }, "Back to every field, every status but Done, quickest first"));
            v.AddChild(row2);
        }

        var width = CentreWidth;
        var grid = Grid(width);
        var cardWidth = width / grid.Columns - 12;
        foreach (var r in shown) grid.AddChild(IdeaCard(w, r, cardWidth));
        if (shown.Count == 0) grid.AddChild(Ui.Label("No ideas match these filters", 13, Ui.Dim));
        v.AddChild(grid);
    }

    private Button Chip(string text, bool on, Action toggle)
    {
        var b = Ui.SmallButton(text, toggle, "Filter");
        b.AddThemeFontSizeOverride("font_size", 12);
        b.CustomMinimumSize = new Vector2(0, 28);
        b.Modulate = on ? Ui.Player : new Color(1, 1, 1, 0.7f);
        return b;
    }

    /// <summary>The marks on a picture: field and tier at the top left; sparked, counter and theirs where they apply.</summary>
    private static void Badges(World w, ResearchData r, TextureRect pic, float width)
    {
        var height = width * 12 / 35;
        var tag = OnPicture(Ui.Label($"{Research.FieldName(r.Field)}  {new string('●', Math.Clamp(r.Tier, 1, 5))}", 11, new Color("c4ccd8")));
        tag.Position = new Vector2(8, 6);
        tag.TooltipText = $"Tier {r.Tier}";
        pic.AddChild(tag);
        // Top right: what makes this idea special in this game (a spark, a survey find). Bottom: war (counter, theirs).
        var topRight = new List<(string Text, Color Color, string Tip)>();
        var me = Economy.Of(w, Side.Player);
        if (me.Sparked.Contains(r.Id))
            topRight.Add(("✦ sparked", new Color("ffd27a"), me.SparkNotes.LastOrDefault(n => n.Idea == r.Id)?.Why ?? "Something in the game brought this idea on"));
        if (me.Finds.Find(f => f.Idea == r.Id) is SurveyFind find)
            topRight.Add(("⌖ survey", new Color("6fe08a"), $"{w.FindBody(find.BodyId)?.Name} survey: −{Research.T.SurveyCut * 100:0} % time"));
        var y = 6f;
        foreach (var (text, color, tip) in topRight)
        {
            var l = OnPicture(Ui.Label(text, 11, color));
            l.Position = new Vector2(width - 8 - 7 * text.Length, y);
            l.TooltipText = tip;
            l.MouseFilter = MouseFilterEnum.Pass;
            pic.AddChild(l);
            y += 16;
        }
        if (Intel.Counters(w, Side.Player).FirstOrDefault(c => c.Idea == r.Id) is { Idea: not null } counter)
        {
            var hint = OnPicture(Ui.Label("⚔ counter", 11, new Color("ffd27a")));
            hint.Position = new Vector2(8, height - 20);
            hint.TooltipText = $"Intel: {counter.Why}. This idea answers it";
            hint.MouseFilter = MouseFilterEnum.Pass;
            pic.AddChild(hint);
        }
        if (RivalTech.StageOf(w, Side.Player, r.Id) is var stage and not RivalStage.None)
        {
            // Learned from the rival (board 26): Studied shows its name; Unlocked skips its prerequisites and is quicker.
            var theirs = OnPicture(Ui.Label($"◆ theirs: {RivalTech.StageName(stage)}", 11, Ui.Rival));
            theirs.Position = new Vector2(width - 130, height - 20);
            theirs.TooltipText = stage == RivalStage.Unlocked
                ? $"Learned from the rival: queue it without its prerequisites, {GameData.Weapons.R.UnlockedCut:P0} quicker"
                : $"Learning it from the rival: {RivalTech.Progress(w, Side.Player, r.Id):P0} (unlocked at 100 %). Battles, wrecks, captures and looks at their colonies teach it";
            theirs.MouseFilter = MouseFilterEnum.Pass;
            pic.AddChild(theirs);
        }
    }

    /// <summary>"Available · 9 d", "Queued · First team · 12 d": the idea's state, kept up to date.</summary>
    private Label StatusLabel(ResearchData r, IdeaState state, int size)
    {
        var status = Ui.Label("", size, Ui.Dim);
        _live.Add(w2 =>
        {
            var order = w2.Orders.Find(o => o.Side == Side.Player && o.Kind == OrderKind.Research && o.Key == r.Id);
            var team = order?.TeamId is int id ? Research.Team(w2, Side.Player, id) : null;
            (status.Text, var color) = state switch
            {
                IdeaState.Done => ("Done", Ui.Dim),
                IdeaState.Researching => ($"Researching · {team?.Name} · {Research.DaysUntilDone(w2, order!):0.#} d", Ui.Player),
                IdeaState.Queued => ($"Queued · {team?.Name} · {Research.DaysUntilDone(w2, order!):0.#} d", Ui.Text),
                IdeaState.Locked => ("Locked", Ui.Dim),
                _ => ($"Available · {Quickest(w2, r.Id).Days:0.#} d", new Color("6fdc8c")),
            };
            status.AddThemeColorOverride("font_color", color);
        });
        return status;
    }

    private Control IdeaCard(World w, ResearchData r, float width)
    {
        var state = StateOf(w, r.Id);
        var picked = Shown(w)?.Id == r.Id && _openTeam == null;
        var border = picked ? Ui.Player : state switch
        {
            IdeaState.Researching => new Color("12324a"),
            IdeaState.Available when Economy.Of(w, Side.Player).Cuts.Contains(r.Id) => new Color("5a4620"),
            _ => new Color("161d29"),
        };
        var card = Box(0, border);
        card.CustomMinimumSize = new Vector2(width, 0);
        if (state == IdeaState.Locked) card.Modulate = new Color(1, 1, 1, 0.55f);
        card.TooltipText = state == IdeaState.Available ? "Click to see it in full on the right · double-click to queue it with the team that finishes it first"
            : "Click to see it in full on the right";
        card.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            _selected = r.Id;
            _openTeam = null;
            if (click.DoubleClick)
            {
                // Double-click: queue it with the team that would finish it first (a home order, so it works paused).
                if (StateOf(Main.Instance.Sim!.World, r.Id) == IdeaState.Available && Main.Instance.Sim.World.Winner == null)
                    _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Research, r.Id), "queueing research");
                card.AcceptEvent();
            }
        };
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 0);
        card.AddChild(v);

        var pic = Picture(r, width);
        if (state == IdeaState.Locked) pic.Modulate = new Color(0.4f, 0.45f, 0.55f);
        Badges(w, r, pic, width);
        v.AddChild(pic);

        var body = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) body.AddThemeConstantOverride($"margin_{side}", 10);
        var t = new VBoxContainer();
        t.AddThemeConstantOverride("separation", 4);
        body.AddChild(t);
        v.AddChild(body);

        var name = Ui.Label(r.Name, 14, new Color("f2f5fa"));
        name.ClipText = true;
        name.CustomMinimumSize = new Vector2(width - 24, 0);
        t.AddChild(name);
        t.AddChild(StatusLabel(r, state, 12));

        var effect = Ui.Label(r.Effect, 12, new Color("aab4c3"));
        effect.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        effect.CustomMinimumSize = new Vector2(width - 24, 34);
        t.AddChild(effect);
        if (state == IdeaState.Available && w.Winner == null && QueueButtons(w, r) is Control queue) t.AddChild(queue);
        else if (state == IdeaState.Locked && Research.Missing(w, Side.Player, r.Id) is string missing)
            t.AddChild(Wrap(Ui.Label(missing, 11, Ui.Dim)));
        return card;
    }

    /// <summary>"Queue → Vega · 9 d": one click queues with the team that finishes it first; ▾ picks another team.</summary>
    private Control? QueueButtons(World w, ResearchData r)
    {
        var best = Research.BestTeamFor(w, Side.Player, r.Id);
        if (best == null) return null;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        var quick = Ui.SmallButton($"Queue → {best.Name} · {Quickest(w, r.Id).Days:0.#} d",
            () => _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Research, r.Id, TeamId: best.Id), "queueing research"),
            $"Queue with {best.Name}, the team that would finish it first (double-clicking the card does the same)");
        quick.AddThemeColorOverride("font_color", Ui.Player);
        quick.AddThemeFontSizeOverride("font_size", 13);
        quick.ClipText = true;
        quick.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        quick.CustomMinimumSize = new Vector2(60, 30);
        row.AddChild(quick);
        var others = Economy.Of(w, Side.Player).Teams.Where(t => t != best).ToList();
        if (others.Count > 0)
        {
            var pick = new MenuButton { Text = "▾", FocusMode = FocusModeEnum.None, Flat = false, TooltipText = "Queue with another team" };
            foreach (var team in others)
            {
                var days = Research.BaseHours(w, Side.Player, r.Id) / 24 / Math.Max(1e-6, Research.Speed(team, r.Field));
                pick.GetPopup().AddItem($"{team.Name} · {days:0.#} d of work", team.Id);
            }
            pick.GetPopup().IdPressed += id => _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Research, r.Id, TeamId: (int)id), "queueing research");
            row.AddChild(pick);
        }
        return row;
    }

    // ---- The picked idea -----------------------------------------------------------------------

    /// <summary>The idea in full: big picture with its marks, what it does, needs, leads to, time for each team, and Queue.</summary>
    private Control IdeaDetail(World w, ResearchData r, float width)
    {
        var state = StateOf(w, r.Id);
        var box = Box(0, new Color("1e2a3a"));
        box.CustomMinimumSize = new Vector2(width, 0);
        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 0);
        box.AddChild(outer);
        var pic = Picture(r, width - 2);
        if (state == IdeaState.Locked) pic.Modulate = new Color(0.5f, 0.55f, 0.65f);
        Badges(w, r, pic, width - 2);
        outer.AddChild(pic);
        var body = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) body.AddThemeConstantOverride($"margin_{side}", 14);
        outer.AddChild(body);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        body.AddChild(v);

        v.AddChild(Wrap(Ui.Label(r.Name, 20, new Color("f2f5fa"))));
        var meta = new HBoxContainer();
        meta.AddThemeConstantOverride("separation", 10);
        var fi = Math.Max(0, Research.FieldIndex(r.Field));
        meta.AddChild(Ui.Label($"{Research.FieldName(r.Field)} · tier {r.Tier}", 12, DiscoveryWeb.FieldColors[fi % DiscoveryWeb.FieldColors.Length]));
        meta.AddChild(StatusLabel(r, state, 12));
        v.AddChild(meta);
        v.AddChild(Wrap(Ui.Label(r.Effect, 14, new Color("cfd8e3"))));

        // Why it stands out in this game: a spark, a survey find, a counter, learning it from the rival.
        var me = Economy.Of(w, Side.Player);
        if (me.Sparked.Contains(r.Id))
            v.AddChild(Wrap(Ui.Label("✦ Sparked: " + (me.SparkNotes.LastOrDefault(n => n.Idea == r.Id)?.Why ?? "something in the game brought it on"), 12, new Color("ffd27a"))));
        if (me.Finds.Find(f => f.Idea == r.Id) is SurveyFind find)
            v.AddChild(Wrap(Ui.Label($"⌖ {w.FindBody(find.BodyId)?.Name} survey: −{Research.T.SurveyCut * 100:0} % time", 12, new Color("6fe08a"))));
        if (Intel.Counters(w, Side.Player).FirstOrDefault(c => c.Idea == r.Id) is { Idea: not null } counter)
            v.AddChild(Wrap(Ui.Label($"⚔ Counter: {counter.Why}", 12, new Color("ffd27a"))));
        if (RivalTech.StageOf(w, Side.Player, r.Id) is var stage and not RivalStage.None)
            v.AddChild(Wrap(Ui.Label(stage == RivalStage.Unlocked
                ? $"◆ Learned from the rival: no prerequisites needed, {GameData.Weapons.R.UnlockedCut:P0} quicker"
                : $"◆ Learning it from the rival: {RivalTech.Progress(w, Side.Player, r.Id):P0}, {RivalTech.StageName(stage)}", 12, Ui.Rival)));

        string Mark(string id) => GameData.Research(id).Name + (Tech.Has(w, Side.Player, id) ? " ✓" : "");
        if (r.Needs is { Count: > 0 }) v.AddChild(Section("NEEDS", string.Join(" · ", r.Needs.Select(Mark))));
        if (state == IdeaState.Locked && Research.Missing(w, Side.Player, r.Id) is string missing)
            v.AddChild(Wrap(Ui.Label(missing, 12, Ui.Warn)));
        var opens = GameData.Economy.Research.Where(o => Discovered(w, o) && (o.Needs?.Contains(r.Id) ?? false))
            .Select(o => o.Name + ((o.Needs!.Where(n => n != r.Id && !Tech.Has(w, Side.Player, n)).Select(n => GameData.Research(n).Name).ToList() is { Count: > 0 } also)
                ? $" (also needs {string.Join(", ", also)})" : "")).ToList();
        var hidden = GameData.Economy.Research.Count(o => !o.Later && !Discovered(w, o) && (o.Needs?.Contains(r.Id) ?? false));
        if (opens.Count > 0 || hidden > 0)
            v.AddChild(Section("LEADS TO", string.Join(" · ", opens) + (hidden > 0 ? $"{(opens.Count > 0 ? " · " : "")}{hidden} idea{(hidden == 1 ? "" : "s")} not yet guessed at" : "")));

        if (state is IdeaState.Available or IdeaState.Locked && me.Teams.Count > 0)
        {
            v.AddChild(Ui.Label("WORK FOR EACH TEAM", 11, Ui.Dim));
            var best = Research.BestTeamFor(w, Side.Player, r.Id);
            foreach (var team in me.Teams)
            {
                var days = Research.BaseHours(w, Side.Player, r.Id) / 24 / Math.Max(1e-6, Research.Speed(team, r.Field));
                var row = new HBoxContainer();
                row.AddChild(Ui.Label(team.Name, 13, team == best ? Ui.Player : Ui.Text));
                row.AddChild(Fit(Ui.Label($"{days:0.#} d of work{(team == best ? " · finishes first" : "")}", 12, team == best ? Ui.Player : Ui.Dim)));
                v.AddChild(row);
            }
        }
        if (state == IdeaState.Available && w.Winner == null && QueueButtons(w, r) is Control queue) v.AddChild(queue);
        if (state is IdeaState.Queued or IdeaState.Researching
            && w.Orders.Find(o => o.Side == Side.Player && o.Kind == OrderKind.Research && o.Key == r.Id) is WorkOrder order)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            if (order.TeamId is int tid && Research.Team(w, Side.Player, tid) is ResearchTeam team)
                row.AddChild(Ui.SmallButton($"{team.Name}'s queue ▸", () => _openTeam = team.Id, "Show the team's queue, to put it in order"));
            row.AddChild(Ui.SmallButton("Take off the queue", () => _screen.CancelOrder(order.Id), "Take this idea off its team's queue"));
            v.AddChild(row);
        }
        if (!_webOpen)
            v.AddChild(Ui.SmallButton("Show in the web", () => { _webOpen = true; _webField = null; }, "Open the discovery web with this idea's path lit up"));
        return box;
    }

    private static Control Section(string title, string text)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 2);
        v.AddChild(Ui.Label(title, 11, Ui.Dim));
        v.AddChild(Wrap(Ui.Label(text, 13, Ui.Text)));
        return v;
    }

    // ---- Discovery web -------------------------------------------------------------------------

    private Control WebPanel(World w)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(Ui.Label("Discovery web", 16, new Color("f2f5fa")));
        var fields = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Show one field (with the ideas just outside it that link in), or all" };
        fields.AddItem("All fields", 0);
        for (var i = 0; i < Research.Fields.Length; i++) fields.AddItem(Research.FieldNames[i], i + 1);
        fields.Select(_webField == null ? 0 : Research.FieldIndex(_webField) + 1);
        fields.ItemSelected += i => { _webField = i == 0 ? null : Research.Fields[i - 1]; DiscoveryWeb.Refit(); };
        head.AddChild(fields);
        var how = Ui.Label("drag to pan · wheel to zoom · click an idea", 11, Ui.Dim);
        how.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(how);
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.SmallButton("Fit", DiscoveryWeb.Refit, "Fit the whole web in view"));
        v.AddChild(head);
        var web = new DiscoveryWeb(w, Shown(w)?.Id, id => { _selected = id; _openTeam = null; }, _webField)
        {
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true,
        };
        v.AddChild(web);
        v.AddChild(DiscoveryWeb.Legend());
        return v;
    }
}

/// <summary>
/// The discovery web: every idea you know of as a node, pulled together by field into clusters, with
/// curved links from each idea to what it opens (links between fields stretch across). Ideas one step
/// beyond what you know show as blurred dots. Picking an idea lights its whole chain: what it needs,
/// back to what you know, and everything it leads to. Drag to pan, wheel to zoom.
/// </summary>
public partial class DiscoveryWeb : Control
{
    private enum Look { Known, Busy, Open, Next, Blur }
    private readonly List<(string Id, string Name, int Field, Look Look, bool Lit)> _nodes = new();
    private readonly List<(int From, int To, bool Lit)> _links = new();
    private readonly Action<string>? _pick;
    private Dictionary<string, Vector2> _at = new();
    private int _hover = -1;
    private Vector2 _press, _panAtPress;
    private bool _dragging;

    // Where the view is, kept while the game runs (the screen rebuilds the web when anything changes).
    private static Vector2 _pan;
    private static float _zoom;                         // 0: fit to the panel on the next draw
    private static string _layoutKey = "";
    private static Dictionary<string, Vector2> _layout = new();

    public static readonly Color[] FieldColors =
        { new("b58cff"), new("ff8a6b"), new("5fd6c8"), new("7cc4ff"), new("ffd27a"), new("6fe08a") };

    public static void Refit() => _zoom = 0;

    public DiscoveryWeb() { }

    /// <summary>The web of ideas you know of. With a field, only that field's ideas show, plus the ideas just outside
    /// it that link in or out (so its links still lead somewhere).</summary>
    public DiscoveryWeb(World w, string? selected, Action<string> pick, string? field = null)
    {
        _pick = pick;
        MouseFilter = MouseFilterEnum.Stop;
        var all = GameData.Economy.Research.Where(r => !r.Later).ToList();
        var known = all.Where(r => ResearchScreen.Discovered(w, r)).Select(r => r.Id).ToHashSet();
        // One step beyond: ideas that something you know leads to, shown blurred and nameless.
        var beyond = all.Where(r => !known.Contains(r.Id) && (r.Needs ?? new()).Any(known.Contains)).Select(r => r.Id).ToHashSet();
        var shown = all.Where(r => known.Contains(r.Id) || beyond.Contains(r.Id)).ToList();
        if (field != null)
        {
            var inField = shown.Where(r => r.Field == field).Select(r => r.Id).ToHashSet();
            shown = shown.Where(r => inField.Contains(r.Id) || (r.Needs ?? new()).Any(inField.Contains)
                                     || shown.Any(o => inField.Contains(o.Id) && (o.Needs ?? new()).Contains(r.Id))).ToList();
        }

        var lit = new HashSet<string>();
        if (selected != null)
        {
            void Up(string id) { if (lit.Add(id)) foreach (var n in GameData.Research(id).Needs ?? new()) Up(n); }
            void Down(string id) { foreach (var r in shown.Where(r => (r.Needs ?? new()).Contains(id))) if (lit.Add(r.Id)) Down(r.Id); }
            Up(selected);
            Down(selected);
        }
        var index = new Dictionary<string, int>();
        foreach (var r in shown)
        {
            var look = beyond.Contains(r.Id) ? Look.Blur : ResearchScreen.StateOf(w, r.Id) switch
            {
                ResearchScreen.IdeaState.Done => Look.Known,
                ResearchScreen.IdeaState.Researching or ResearchScreen.IdeaState.Queued => Look.Busy,
                ResearchScreen.IdeaState.Available => Look.Open,
                _ => Look.Next,
            };
            index[r.Id] = _nodes.Count;
            _nodes.Add((r.Id, r.Name, Math.Max(0, Research.FieldIndex(r.Field)), look, lit.Contains(r.Id)));
        }
        foreach (var r in shown)
            foreach (var n in r.Needs ?? new())
                if (index.TryGetValue(n, out var a))
                    _links.Add((a, index[r.Id], lit.Contains(n) && lit.Contains(r.Id) && selected != null));
        _at = Layout(shown);
    }

    /// <summary>Field centres in two rows of three; a few hundred rounds of springs (links), push (nodes) and pull
    /// (to the field's centre). Worked out once per set of ideas shown, so the web holds still between rebuilds.</summary>
    private Dictionary<string, Vector2> Layout(List<ResearchData> shown)
    {
        var key = string.Join(",", shown.Select(r => r.Id));
        if (key == _layoutKey) return _layout;
        var centre = new Vector2[Research.Fields.Length];
        for (var i = 0; i < centre.Length; i++) centre[i] = new Vector2(200 + i % 3 * 340, 200 + i / 3 * 400);
        var rng = new Random(7);
        var pos = shown.Select(r => centre[Math.Max(0, Research.FieldIndex(r.Field))] + new Vector2(rng.Next(-60, 60), rng.Next(-60, 60))).ToArray();
        var n = pos.Length;
        var force = new Vector2[n];
        for (var round = 0; round < 500; round++)
        {
            Array.Clear(force);
            for (var a = 0; a < n; a++)
                for (var b = a + 1; b < n; b++)
                {
                    var d = pos[a] - pos[b];
                    var e = new Vector2(d.X * 0.45f, d.Y); // labels are wide: count sideways distance less
                    var d2 = e.LengthSquared() + 0.01f;
                    if (d2 > 120 * 120) continue;
                    var f = d.Normalized() * (900 / d2);
                    force[a] += f;
                    force[b] -= f;
                }
            foreach (var (from, to, _) in _links)
            {
                var d = pos[from] - pos[to];
                var len = d.Length() + 0.01f;
                var rest = _nodes[from].Field == _nodes[to].Field ? 55 : 140;
                var f = d / len * (0.04f * (len - rest));
                force[to] += f;
                force[from] -= f;
            }
            var step = round < 330 ? 0.9f : 0.4f;
            for (var i = 0; i < n; i++)
            {
                force[i] += (centre[_nodes[i].Field] - pos[i]) * 0.02f;
                pos[i] += force[i].LimitLength(8) * step;
            }
        }
        _layoutKey = key;
        _layout = shown.Select((r, i) => (r.Id, pos[i])).ToDictionary(p => p.Id, p => p.Item2);
        return _layout;
    }

    private Vector2 Screen(Vector2 p) => p * _zoom + _pan;

    private void Fit()
    {
        if (_at.Count == 0 || Size.X < 10) return;
        var min = new Vector2(_at.Values.Min(p => p.X), _at.Values.Min(p => p.Y)) - new Vector2(70, 40);
        var max = new Vector2(_at.Values.Max(p => p.X), _at.Values.Max(p => p.Y)) + new Vector2(70, 30);
        _zoom = Mathf.Clamp(Mathf.Min(Size.X / (max.X - min.X), Size.Y / (max.Y - min.Y)), 0.3f, 2.5f);
        _pan = (Size - (max - min) * _zoom) / 2 - min * _zoom;
    }

    private int NodeAt(Vector2 screen)
    {
        var best = -1;
        var bestD = 14f * 14f;
        for (var i = 0; i < _nodes.Count; i++)
        {
            if (_nodes[i].Look == Look.Blur) continue;
            var d = (Screen(_at[_nodes[i].Id]) - screen).LengthSquared();
            if (d < bestD) { best = i; bestD = d; }
        }
        return best;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown, Pressed: true } wheel:
            {
                var old = _zoom;
                _zoom = Mathf.Clamp(_zoom * (wheel.ButtonIndex == MouseButton.WheelUp ? 1.15f : 1 / 1.15f), 0.3f, 3f);
                _pan = wheel.Position - (wheel.Position - _pan) * (_zoom / old);
                QueueRedraw();
                AcceptEvent();
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } click:
                if (click.Pressed) { _press = click.Position; _panAtPress = _pan; _dragging = false; }
                else if (!_dragging && NodeAt(click.Position) is var i and >= 0) _pick?.Invoke(_nodes[i].Id);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion:
                if (Input.IsMouseButtonPressed(MouseButton.Left))
                {
                    if ((motion.Position - _press).Length() > 4) _dragging = true;
                    if (_dragging) { _pan = _panAtPress + motion.Position - _press; QueueRedraw(); }
                }
                else if (NodeAt(motion.Position) is var h && h != _hover) { _hover = h; QueueRedraw(); }
                break;
        }
    }

    public override void _Draw()
    {
        if (_zoom <= 0) Fit();
        if (_zoom <= 0) return;
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("0a0e15"));

        // Field clusters: a faint ring round each field's ideas, and its name on top.
        for (var f = 0; f < Research.Fields.Length; f++)
        {
            var pts = _nodes.Where(n => n.Field == f).Select(n => _at[n.Id]).ToList();
            if (pts.Count == 0) continue;
            var mid = pts.Aggregate(Vector2.Zero, (a, b) => a + b) / pts.Count;
            var r = pts.Max(p => (p - mid).Length()) + 26;
            var c = FieldColors[f % FieldColors.Length];
            DrawCircle(Screen(mid), r * _zoom, new Color(c, 0.04f));
            DrawArc(Screen(mid), r * _zoom, 0, Mathf.Tau, 96, new Color(c, 0.18f), 1, true);
            var name = Research.FieldNames[f].ToUpperInvariant();
            var size = Mathf.Clamp((int)(13 * _zoom), 10, 18);
            var at = Screen(mid - new Vector2(0, r - 16));
            DrawString(font, at - new Vector2(240, 0), name, HorizontalAlignment.Center, 480, size, new Color(c, 0.9f));
        }

        // Links: a gentle curve from each idea to what it opens; the picked chain on top.
        foreach (var lit in new[] { false, true })
            foreach (var (from, to, isLit) in _links)
            {
                if (isLit != lit) continue;
                var a = Screen(_at[_nodes[from].Id]);
                var b = Screen(_at[_nodes[to].Id]);
                var (fa, fb) = (_nodes[from].Look, _nodes[to].Look);
                var color = isLit ? Ui.Player
                    : fa == Look.Known && fb == Look.Known ? new Color("3d6a4a")
                    : fa == Look.Known ? new Color("4a5a70")
                    : fb == Look.Blur ? new Color("1e2633") : new Color("2a3446");
                var d = b - a;
                var ctrl = (a + b) / 2 + new Vector2(-d.Y, d.X) * 0.15f;
                var pts = new Vector2[13];
                for (var k = 0; k <= 12; k++)
                {
                    var t = k / 12f;
                    pts[k] = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * ctrl + t * t * b;
                }
                DrawPolyline(pts, color, isLit ? 2.2f : 1.2f, true);
            }

        // Ideas: filled dots for what you know, rings for what you can start or are working on, dim rings
        // for what comes after, blurred dots beyond that. Names on everything but known ideas (hover shows them).
        for (var i = 0; i < _nodes.Count; i++)
        {
            var n = _nodes[i];
            var p = Screen(_at[n.Id]);
            if (p.X < -100 || p.Y < -40 || p.X > Size.X + 100 || p.Y > Size.Y + 40) continue;
            var c = FieldColors[n.Field % FieldColors.Length];
            var r = 6.5f * Mathf.Clamp(_zoom, 0.7f, 1.4f);
            switch (n.Look)
            {
                case Look.Blur:
                    DrawCircle(p, r * 1.6f, new Color("2a3446", 0.25f));
                    DrawCircle(p, r, new Color("2a3446", 0.6f));
                    continue;
                case Look.Known:
                    DrawCircle(p, r * 0.8f, c);
                    break;
                case Look.Busy:
                    DrawCircle(p, r, new Color("0c111a"));
                    DrawArc(p, r, 0, Mathf.Tau, 24, Ui.Player, 2, true);
                    DrawArc(p, r + 4, 0, Mathf.Tau, 24, new Color(Ui.Player, 0.5f), 1, true);
                    break;
                case Look.Open:
                    DrawCircle(p, r, new Color("0c111a"));
                    DrawArc(p, r, 0, Mathf.Tau, 24, c, 2, true);
                    break;
                default:
                    DrawCircle(p, r, new Color("0c111a"));
                    DrawArc(p, r, 0, Mathf.Tau, 24, new Color("3a4658"), 1.2f, true);
                    break;
            }
            if (n.Lit && _links.Any(l => l.Lit) || i == _hover) DrawArc(p, r + 7, 0, Mathf.Tau, 32, new Color(Ui.Player, i == _hover ? 0.6f : 0.9f), 1.5f, true);
            if (n.Look == Look.Known && !n.Lit && i != _hover) continue;
            var ink = n.Lit || i == _hover ? new Color("f2f5fa")
                : n.Look switch { Look.Busy => new Color("7cc4ff"), Look.Open => new Color("e6ebf2"), Look.Known => new Color("8a96a8"), _ => new Color("6b7688") };
            var size = Mathf.Clamp((int)(11 * _zoom), 9, 16);
            var text = n.Name + (n.Look == Look.Known ? " ✓" : "");
            var pos = p + new Vector2(-110, r + size + 2);
            DrawString(font, pos + new Vector2(1, 1), text, HorizontalAlignment.Center, 220, size, new Color(0, 0, 0, 0.8f));
            DrawString(font, pos, text, HorizontalAlignment.Center, 220, size, ink);
        }
    }

    /// <summary>The key under the web.</summary>
    public static Control Legend()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        foreach (var (mark, text, color) in new[]
                 {
                     ("●", "Known (hover for names)", new Color("ff8a6b")), ("◎", "Researching or queued", Ui.Player), ("○", "Can start", new Color("6fe08a")),
                     ("○", "Comes after that", new Color("3a4658")), ("•", "Not yet guessed at", new Color("2a3446")),
                 })
        {
            var item = new HBoxContainer();
            item.AddThemeConstantOverride("separation", 4);
            item.AddChild(Ui.Label(mark, 13, color));
            item.AddChild(Ui.Label(text, 11, Ui.Dim));
            row.AddChild(item);
        }
        return row;
    }
}
