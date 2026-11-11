using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The research screen (T): the team slots along the top (skills, kept fields, queues, train, split,
/// found), the ideas as picture cards with filters, and the view-only discovery web. Buttons issue
/// commands; the screen rebuilds only when something it shows changes shape, and updates numbers live.
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
    private string? _selected;                 // idea lit up in the discovery web

    public enum IdeaState { Available, Queued, Researching, Done, Locked }
    private enum Sort { Quickest, Cheapest, Name }

    private static readonly string[] UnlockGroups = { "hulls", "engines", "weapons", "defence", "supply", "sensors", "industry" };

    public ResearchScreen() : this(null!) { }
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
        if (Position != Vector2.Zero || Size != view) { Position = Vector2.Zero; Size = view; }
        var shape = Shape(w);
        if (shape != _shape)
        {
            _shape = shape;
            Rebuild(w);
        }
        foreach (var update in _live) update(w);
    }

    private string Shape(World w)
    {
        var me = Economy.Of(w, Side.Player);
        return string.Join(",", w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research).Select(o => $"{o.Id}:{o.TeamId}:{o.Paid}"))
               + "|" + string.Join(",", me.Researched) + "|" + string.Join(",", me.Cuts)
               + "|" + string.Join(",", me.Teams.Select(t => $"{t.Id}:{string.Join("+", t.Kept)}:{t.TrainingUntilHour != null}:{Research.Ready(w, t)}:{t.Scientists}:{string.Join("/", Research.KeptFields(t))}"))
               + $"|{_search}|{_field}|{string.Join(",", _states.Order())}|{_unlocks}|{_sort}|{_selected}|{GetViewportRect().Size}|{w.Winner}";
    }

    // ---- Layout --------------------------------------------------------------------------------

    private void Rebuild(World w)
    {
        foreach (var c in _root.GetChildren()) c.QueueFree();
        _live.Clear();

        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 16);
        top.AddChild(Ui.Label("Research", 26, new Color("f2f5fa")));
        var note = Ui.Label($"{Research.T.Slots} team slots · speed = skill × size · fields a team doesn't keep fade over time", 13, Ui.Dim);
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(note);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var industry = Ui.Label("", 14, Ui.Text);
        industry.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _live.Add(w2 => industry.Text = $"Industry {Economy.Of(w2, Side.Player).Industry:0}");
        top.AddChild(industry);
        top.AddChild(Ui.SmallButton("Back to map", () => _screen.ShowResearch = false, "Close the research screen (T or Esc)"));
        _root.AddChild(top);

        // Team slots.
        var teams = new HBoxContainer();
        teams.AddThemeConstantOverride("separation", 14);
        var me = Economy.Of(w, Side.Player);
        for (var i = 0; i < Research.T.Slots; i++)
        {
            Control card = i < me.Teams.Count ? TeamCard(w, me.Teams[i]) : i == me.Teams.Count ? EmptySlot(w) : LockedSlot();
            card.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            card.SizeFlagsStretchRatio = 1;
            teams.AddChild(card);
        }
        _root.AddChild(teams);

        // Ideas and the discovery web.
        var bottom = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        bottom.AddThemeConstantOverride("separation", 14);
        var ideas = Box(16);
        ideas.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ideas.AddChild(IdeasPanel(w));
        bottom.AddChild(ideas);
        var web = Box(16);
        web.CustomMinimumSize = new Vector2(Mathf.Min(560, GetViewportRect().Size.X * 0.3f), 0);
        web.AddChild(WebPanel(w));
        bottom.AddChild(web);
        _root.AddChild(bottom);
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

    // ---- Teams ---------------------------------------------------------------------------------

    private Control TeamCard(World w, ResearchTeam team)
    {
        var card = Box(14, new Color("12324a"));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        card.AddChild(v);

        var head = new HBoxContainer();
        head.AddChild(Ui.Label(team.Name, 18, Ui.Player));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var size = Ui.Label("", 13, Ui.Text);
        _live.Add(w2 => size.Text = $"{team.Scientists:0} scientists"
            + (team.TrainingUntilHour is double h ? $" (+{team.TrainingScientists:0} in {(h - w2.Hour) / 24:0.#} d)" : "")
            + (!Research.Ready(w2, team) ? $" · ready in {(team.ReadyHour - w2.Hour) / 24:0.#} d" : ""));
        head.AddChild(size);
        v.AddChild(head);

        v.AddChild(Ui.Label("SKILL  (★ kept · ↓ fading · click ★ to keep a field)", 11, Ui.Dim));
        var kept = Research.KeptFields(team).ToList();
        for (var i = 0; i < Research.Fields.Length; i++)
        {
            var fi = i;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var keep = Ui.SmallButton(kept.Contains(i) ? "★" : "☆", () => ToggleKept(team, Research.Fields[fi]),
                team.Kept.Count == 0 && kept.Contains(i) ? "Kept for now as the team's best field; click to keep a field yourself (up to 2)" : "Keep this field (up to 2); fields not kept fade back to 1x");
            keep.CustomMinimumSize = new Vector2(30, 26);
            keep.Modulate = kept.Contains(i) ? Ui.Player : new Color(1, 1, 1, 0.5f);
            row.AddChild(keep);
            var name = Ui.Label(Research.FieldNames[i], 13, Ui.Text);
            name.CustomMinimumSize = new Vector2(150, 0);
            row.AddChild(name);
            var bar = Bar(120, kept.Contains(i) ? Ui.Player : new Color("8a96a8"), () => (team.Skill[fi] - 1) / Math.Max(0.01, Research.T.MaxSkill - 1), _live);
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

        v.AddChild(Ui.Label("QUEUE", 11, Ui.Dim));
        var queue = Research.QueueOf(w, Side.Player, team.Id).ToList();
        if (queue.Count == 0) v.AddChild(Ui.Label("Nothing queued: add ideas from the cards below", 12, Ui.Dim));
        for (var q = 0; q < queue.Count; q++)
        {
            var order = queue[q];
            var first = q == 0;
            var data = GameData.Research(order.Key);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            var name = Ui.Label(data.Name, 13, q == 0 ? new Color("f2f5fa") : Ui.Text);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            name.ClipText = true;
            row.AddChild(name);
            var days = Ui.Label("", 13, Ui.Dim);
            _live.Add(w2 => days.Text = order.Paid || !first ? $"{Research.DaysUntilDone(w2, order):0.#} d" : $"waits for {Economy.CostOf(order):0} Industry");
            row.AddChild(days);
            var others = Economy.Of(w, Side.Player).Teams.Where(t => t.Id != team.Id).ToList();
            if (others.Count > 0)
            {
                var move = new MenuButton { Text = "⇄", TooltipText = "Move to another team (progress is kept)", FocusMode = FocusModeEnum.None, Flat = false };
                foreach (var o in others) move.GetPopup().AddItem($"Move to {o.Name}", o.Id);
                move.GetPopup().IdPressed += id => _screen.IssueNow(new SetOrderTeamCommand(Side.Player, order.Id, (int)id), "moving research");
                row.AddChild(move);
            }
            row.AddChild(Ui.SmallButton("✕", () => _screen.CancelOrder(order.Id), order.Paid ? "Cancel (the Industry comes back)" : "Take off the queue"));
            v.AddChild(row);
            if (q == 0) v.AddChild(Bar(200, Ui.Player, () => 1 - order.HoursLeft / Math.Max(1e-6, order.TotalHours), _live));
        }

        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 6);
        var train = Ui.SmallButton($"Train (+{Research.T.TrainScientists:0} for {Research.T.TrainCost:0} Industry, {Research.T.TrainDays:0} d)",
            () => _screen.IssueNow(new TrainTeamCommand(Side.Player, team.Id), "training scientists"), "More scientists: a bigger team works faster (twice the size is about 1.7x)");
        _live.Add(w2 => train.Disabled = Research.CanTrain(w2, Side.Player, team.Id) != null);
        buttons.AddChild(train);
        if (Economy.Of(w, Side.Player).Teams.Count < Research.T.Slots)
        {
            var split = new MenuButton { Text = "Split team ▾", TooltipText = "Move half the scientists into the empty slot; the new team takes half of each skill and keeps the field you pick", FocusMode = FocusModeEnum.None, Flat = false };
            for (var i = 0; i < Research.Fields.Length; i++) split.GetPopup().AddItem($"New team keeps {Research.FieldNames[i]}", i);
            split.GetPopup().IdPressed += id => _screen.IssueNow(new SplitTeamCommand(Side.Player, team.Id, Research.Fields[id]), "splitting a team");
            split.Disabled = Research.CanSplit(w, Side.Player, team.Id, Research.Fields[0]) != null;
            buttons.AddChild(split);
        }
        v.AddChild(buttons);
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

    private Control EmptySlot(World w)
    {
        var card = Box(16, new Color("2a3446"), dashed: true);
        var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        v.AddThemeConstantOverride("separation", 10);
        card.AddChild(v);
        var title = Ui.Label("Empty team slot", 18, new Color("aab4c3"));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(title);
        var t = Research.T;
        var text = Ui.Label($"Found a new team: {t.FoundScientists:0} scientists, {t.FoundSkill:0.#}x in the speciality you pick, ready in {t.FoundDays:0} days. "
                            + $"Or split one off an existing team (it takes half of each skill).", 12, Ui.Dim);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.CustomMinimumSize = new Vector2(260, 0);
        v.AddChild(text);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 8);
        var found = new MenuButton { Text = $"Found team · {t.FoundCost:0} Industry ▾", FocusMode = FocusModeEnum.None, Flat = false };
        for (var i = 0; i < Research.Fields.Length; i++) found.GetPopup().AddItem($"Speciality: {Research.FieldNames[i]}", i);
        found.GetPopup().IdPressed += id => _screen.IssueNow(new FoundTeamCommand(Side.Player, Research.Fields[id]), "founding a team");
        _live.Add(w2 => found.Disabled = Research.CanFound(w2, Side.Player, Research.Fields[0]) != null);
        row.AddChild(found);
        var split = new MenuButton { Text = "Split from a team ▾", FocusMode = FocusModeEnum.None, Flat = false };
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

    private static Control LockedSlot()
    {
        var card = Box(16, new Color("2a3446"), dashed: true);
        var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var title = Ui.Label("Empty team slot", 18, new Color("aab4c3"));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(title);
        var text = Ui.Label("Opens once the slot before it is filled.", 12, Ui.Dim);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(text);
        card.AddChild(v);
        return card;
    }

    // ---- Ideas ---------------------------------------------------------------------------------

    public static IdeaState StateOf(World w, string id)
    {
        if (Tech.Has(w, Side.Player, id)) return IdeaState.Done;
        var order = w.Orders.Find(o => o.Side == Side.Player && o.Kind == OrderKind.Research && o.Key == id);
        if (order != null)
            return order.TeamId is int t && Research.QueueOf(w, Side.Player, t).FirstOrDefault() == order && order.Paid ? IdeaState.Researching : IdeaState.Queued;
        return Research.Missing(w, Side.Player, id) != null ? IdeaState.Locked : IdeaState.Available;
    }

    /// <summary>Days the idea would take the team that would finish it first (counting its queue).</summary>
    private static (ResearchTeam? Team, double Days) Quickest(World w, string id)
    {
        var team = Research.BestTeamFor(w, Side.Player, id);
        if (team == null) return (null, double.PositiveInfinity);
        return (team, Research.BaseHours(w, Side.Player, id) / 24 / Math.Max(1e-6, Research.Speed(team, GameData.Research(id).Field)));
    }

    private Control IdeasPanel(World w)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);

        var all = GameData.Economy.Research;
        var shown = all.Where(r => (_field == null || r.Field == _field) && (_unlocks == null || r.Unlocks == _unlocks)
                                   && _states.Contains(StateOf(w, r.Id))
                                   && (_search.Length == 0 || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                       || r.Effect.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToList();
        shown = _sort switch
        {
            Sort.Cheapest => shown.OrderBy(r => r.Cost).ThenBy(r => r.Name).ToList(),
            Sort.Name => shown.OrderBy(r => r.Name).ToList(),
            _ => shown.OrderBy(r => StateOf(w, r.Id) switch { IdeaState.Researching => 0, IdeaState.Queued => 1, IdeaState.Available => 2, IdeaState.Locked => 3, _ => 4 })
                .ThenBy(r => Quickest(w, r.Id).Days).ToList(),
        };

        var head = new HBoxContainer();
        head.AddChild(Ui.Label("Ideas", 16, new Color("f2f5fa")));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.Label($"{shown.Count} of {all.Count} shown · days are for the team that would finish first", 12, Ui.Dim));
        v.AddChild(head);

        // Filters: search, field, sort; then status and what an idea unlocks.
        var row1 = new HBoxContainer();
        row1.AddThemeConstantOverride("separation", 6);
        var search = new LineEdit { Text = _search, PlaceholderText = "Search ideas (Enter)", CustomMinimumSize = new Vector2(200, 30) };
        search.TextSubmitted += t => _search = t.Trim();
        row1.AddChild(search);
        row1.AddChild(Chip("All fields", _field == null, () => _field = null));
        for (var i = 0; i < Research.Fields.Length; i++)
        {
            var f = Research.Fields[i];
            row1.AddChild(Chip($"{Research.FieldNames[i]} {all.Count(r => r.Field == f)}", _field == f, () => _field = _field == f ? null : f));
        }
        row1.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var sort = new OptionButton { FocusMode = FocusModeEnum.None };
        sort.AddItem("Quickest first", (int)Sort.Quickest);
        sort.AddItem("Cheapest first", (int)Sort.Cheapest);
        sort.AddItem("By name", (int)Sort.Name);
        sort.Select((int)_sort);
        sort.ItemSelected += i => _sort = (Sort)(int)i;
        row1.AddChild(sort);
        v.AddChild(row1);

        var row2 = new HBoxContainer();
        row2.AddThemeConstantOverride("separation", 6);
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
        v.AddChild(row2);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 260) };
        var width = GetViewportRect().Size.X - Mathf.Min(560, GetViewportRect().Size.X * 0.3f) - 110;
        var grid = new GridContainer { Columns = Math.Max(1, (int)(width / 300)) };
        grid.AddThemeConstantOverride("h_separation", 12);
        grid.AddThemeConstantOverride("v_separation", 12);
        var cardWidth = width / grid.Columns - 12;
        foreach (var r in shown) grid.AddChild(IdeaCard(w, r, cardWidth));
        if (shown.Count == 0) grid.AddChild(Ui.Label("No ideas match these filters", 13, Ui.Dim));
        scroll.AddChild(grid);
        v.AddChild(scroll);
        return v;
    }

    private Button Chip(string text, bool on, Action toggle)
    {
        var b = Ui.SmallButton(text, toggle, "Filter");
        b.AddThemeFontSizeOverride("font_size", 12);
        b.CustomMinimumSize = new Vector2(0, 28);
        b.Modulate = on ? Ui.Player : new Color(1, 1, 1, 0.7f);
        return b;
    }

    private Control IdeaCard(World w, ResearchData r, float width)
    {
        var state = StateOf(w, r.Id);
        var border = _selected == r.Id ? Ui.Player : state switch
        {
            IdeaState.Researching => new Color("12324a"),
            IdeaState.Available when Economy.Of(w, Side.Player).Cuts.Contains(r.Id) => new Color("5a4620"),
            _ => new Color("161d29"),
        };
        var card = Box(0, border);
        card.CustomMinimumSize = new Vector2(width, 0);
        if (state == IdeaState.Locked) card.Modulate = new Color(1, 1, 1, 0.55f);
        card.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _selected = _selected == r.Id ? null : r.Id;
        };
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 0);
        card.AddChild(v);

        var pic = new TextureRect
        {
            Texture = Art.Texture("Ideas", string.IsNullOrEmpty(r.Picture) ? "engine" : r.Picture),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(width, width * 12 / 35), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore,
        };
        if (state == IdeaState.Locked) pic.Modulate = new Color(0.4f, 0.45f, 0.55f);
        // The field, and pips for how far along its line the idea is (tier).
        var tag = Ui.Label($"{Research.FieldName(r.Field)}  {new string('●', Math.Clamp(r.Tier, 1, 5))}", 11, new Color("aab4c3"));
        tag.Position = new Vector2(8, 6);
        tag.TooltipText = $"Tier {r.Tier}";
        pic.AddChild(tag);
        if (Economy.Of(w, Side.Player).Sparked.Contains(r.Id))
        {
            // A spark: something that happened in the game (a find, a hardship, a capture) brought this idea on.
            var spark = Ui.Label("✦ sparked", 11, new Color("ffd27a"));
            spark.Position = new Vector2(width - 74, 6);
            spark.TooltipText = Economy.Of(w, Side.Player).SparkNotes.LastOrDefault(n => n.Idea == r.Id)?.Why ?? "Something in the game brought this idea on";
            spark.MouseFilter = MouseFilterEnum.Pass;
            pic.AddChild(spark);
        }
        v.AddChild(pic);

        var body = new MarginContainer();
        foreach (var side in new[] { "left", "right", "top", "bottom" }) body.AddThemeConstantOverride($"margin_{side}", 10);
        var t = new VBoxContainer();
        t.AddThemeConstantOverride("separation", 4);
        body.AddChild(t);
        v.AddChild(body);

        var head = new HBoxContainer();
        var name = Ui.Label(r.Name, 14, new Color("f2f5fa"));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(name);
        var status = Ui.Label("", 12, Ui.Dim);
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
        head.AddChild(status);
        t.AddChild(head);

        var effect = Ui.Label(r.Effect, 12, new Color("aab4c3"));
        effect.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        effect.CustomMinimumSize = new Vector2(width - 24, 34);
        t.AddChild(effect);

        var foot = new HBoxContainer();
        var meta = new List<string> { $"Cost {r.Cost:0}" };
        if (Research.Missing(w, Side.Player, r.Id) is string missing) meta.Add(missing);
        else if (r.Needs is { Count: > 0 }) meta.Add("needs " + string.Join(", ", r.Needs.Select(n => GameData.Research(n).Name)));
        if (Economy.Of(w, Side.Player).Finds.Find(f => f.Idea == r.Id) is SurveyFind find)
            meta.Add($"{w.FindBody(find.BodyId)?.Name} survey: −{Research.T.SurveyCut * 100:0} % time");
        var m = Ui.Label(string.Join(" · ", meta), 11, Ui.Dim);
        m.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        m.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        foot.AddChild(m);
        if (state == IdeaState.Available && w.Winner == null)
        {
            var add = new MenuButton { Text = "Add to queue ▾", FocusMode = FocusModeEnum.None, Flat = false };
            add.AddThemeColorOverride("font_color", Ui.Player);
            var popup = add.GetPopup();
            var best = Research.BestTeamFor(w, Side.Player, r.Id);
            foreach (var team in Economy.Of(w, Side.Player).Teams)
            {
                var days = Research.BaseHours(w, Side.Player, r.Id) / 24 / Math.Max(1e-6, Research.Speed(team, r.Field));
                popup.AddItem($"{team.Name} · {days:0.#} d of work" + (team == best ? " (finishes first)" : ""), team.Id);
            }
            popup.IdPressed += id => _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Research, r.Id, TeamId: (int)id), "queueing research");
            foot.AddChild(add);
        }
        t.AddChild(foot);
        return card;
    }

    // ---- Discovery web -------------------------------------------------------------------------

    private Control WebPanel(World w)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        var head = new HBoxContainer();
        head.AddChild(Ui.Label("Discovery web", 16, new Color("f2f5fa")));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.Label("view only", 12, Ui.Dim));
        v.AddChild(head);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        var web = new DiscoveryWeb(w, _selected);
        scroll.AddChild(web);
        v.AddChild(scroll);
        var note = Ui.Label("Lines show what led to what; blurred ideas are not found yet. Click an idea card to light up its path.", 11, Ui.Dim);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(note);
        return v;
    }
}

/// <summary>
/// The discovery web: ideas in columns by how many steps of other ideas lead to them, survey finds
/// on the left in gold. Ideas whose needs aren't met yet show blurred; ones two steps away are hidden.
/// </summary>
public partial class DiscoveryWeb : Control
{
    private readonly List<(string Id, Vector2 At, string Text, Color Fill, Color Ink, bool Blur)> _nodes = new();
    private readonly List<(Vector2 From, Vector2 To, Color Color)> _lines = new();
    private const float NodeW = 130, NodeH = 24, ColW = 150, RowH = 32;

    public DiscoveryWeb() { }

    public DiscoveryWeb(World w, string? selected)
    {
        var all = GameData.Economy.Research;
        var depth = new Dictionary<string, int>();
        int Depth(string id) => depth.TryGetValue(id, out var d) ? d
            : depth[id] = (GameData.Research(id).Needs ?? new List<string>()).Select(n => Depth(n) + 1).DefaultIfEmpty(0).Max();
        var me = Economy.Of(w, Side.Player);
        bool Known(string id) => ResearchScreen.StateOf(w, id) != ResearchScreen.IdeaState.Locked;
        // Visible: known ideas, plus blurred ones whose needs are all known.
        var visible = all.Where(r => Known(r.Id) || (r.Needs ?? new()).All(Known)).ToList();
        var path = new HashSet<string>();
        void Mark(string id) { if (path.Add(id)) foreach (var n in GameData.Research(id).Needs ?? new()) Mark(n); }
        if (selected != null) Mark(selected);

        var pos = new Dictionary<string, Vector2>();
        var finds = me.Finds.ToList();
        var x0 = finds.Count > 0 ? ColW : 0;
        foreach (var col in visible.GroupBy(r => Depth(r.Id)).OrderBy(g => g.Key))
        {
            var y = 8f;
            foreach (var r in col.OrderBy(r => Research.FieldIndex(r.Field)).ThenBy(r => r.Name))
            {
                var at = new Vector2(x0 + col.Key * ColW + 8, y);
                pos[r.Id] = at;
                y += RowH;
                var state = ResearchScreen.StateOf(w, r.Id);
                var lit = path.Contains(r.Id);
                var (fill, ink) = state switch
                {
                    ResearchScreen.IdeaState.Done => (new Color("12324a"), Ui.Player),
                    ResearchScreen.IdeaState.Researching or ResearchScreen.IdeaState.Queued => (new Color("1a2230"), new Color("d6dde8")),
                    ResearchScreen.IdeaState.Available => (new Color("10151e"), new Color("aab4c3")),
                    _ => (new Color("10151e"), new Color("4c5666")),
                };
                if (lit) fill = fill.Lightened(0.15f);
                var blur = state == ResearchScreen.IdeaState.Locked;
                _nodes.Add((r.Id, at, blur ? new string('?', Math.Min(9, r.Name.Length)) : r.Name, fill, ink, blur));
            }
        }
        foreach (var r in visible)
            foreach (var n in r.Needs ?? new())
                if (pos.TryGetValue(n, out var a) && pos.TryGetValue(r.Id, out var b))
                    _lines.Add((a + new Vector2(NodeW, NodeH / 2), b + new Vector2(0, NodeH / 2),
                        path.Contains(r.Id) && path.Contains(n) ? Ui.Player : new Color("2a3446")));
        for (var i = 0; i < finds.Count; i++)
        {
            var at = new Vector2(8, 8 + i * RowH);
            var body = w.FindBody(finds[i].BodyId)?.Name ?? "?";
            _nodes.Add(("find", at, $"{body} survey", new Color("1d1a10"), new Color("f5d27a"), false));
            if (pos.TryGetValue(finds[i].Idea, out var b))
                _lines.Add((at + new Vector2(NodeW, NodeH / 2), b + new Vector2(0, NodeH / 2), new Color("5a4a20")));
        }
        var maxX = _nodes.Count > 0 ? _nodes.Max(n => n.At.X) + NodeW + 8 : 100;
        var maxY = _nodes.Count > 0 ? _nodes.Max(n => n.At.Y) + NodeH + 8 : 100;
        CustomMinimumSize = new Vector2(maxX, maxY);
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        foreach (var (from, to, color) in _lines)
        {
            var mid = (from.X + to.X) / 2;
            DrawPolyline(new[] { from, new Vector2(mid, from.Y), new Vector2(mid, to.Y), to }, color, 1.5f, true);
        }
        foreach (var n in _nodes)
        {
            DrawRect(new Rect2(n.At, new Vector2(NodeW, NodeH)), n.Fill);
            var ink = n.Blur ? new Color(n.Ink, 0.5f) : n.Ink;
            DrawString(font, n.At + new Vector2(8, 16), n.Text, HorizontalAlignment.Left, NodeW - 12, 12, ink);
            if (n.Blur) DrawString(font, n.At + new Vector2(9, 17), n.Text, HorizontalAlignment.Left, NodeW - 12, 12, new Color(n.Ink, 0.25f));
        }
    }
}
