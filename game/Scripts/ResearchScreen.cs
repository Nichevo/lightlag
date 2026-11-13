using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The research screen (T), layout A: a strip of compact team cards along the top (each unfolds to its
/// skills, kept fields, queue, train and split), the ideas as picture cards filling the screen, and the
/// discovery web as a slide-out on the right. Buttons issue commands; the screen rebuilds only when
/// something it shows changes shape, and updates numbers live.
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
    private ScrollContainer? _ideaScroll;
    private (int Ideas, int WebV, int WebH)? _keepScroll; // scroll places to put back after a rebuild
    private static bool _allSkills;            // team cards show every field's skill, not only the kept ones (folded by default)
    private static int? _openTeam;             // the team whose full card (skills, queue, train, split) is unfolded
    private static bool _webOpen;              // the discovery web slid out on the right

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
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            // A rebuild (clicking a card, queueing an idea) used to jump the lists back to the top.
            if (_ideaScroll != null && IsInstanceValid(_ideaScroll))
                _keepScroll = (_ideaScroll.ScrollVertical, 0, 0);
            Rebuild(w);
        }
        else if (_keepScroll is var (ideas, _, _) && _ideaScroll != null && IsInstanceValid(_ideaScroll))
        {
            // Put the scroll place back once the new list has its size (a frame after the rebuild).
            _ideaScroll.ScrollVertical = ideas;
            _keepScroll = null;
        }
        foreach (var update in _live) update(w);
    }

    private string Shape(World w)
    {
        var me = Economy.Of(w, Side.Player);
        return string.Join(",", w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research).Select(o => $"{o.Id}:{o.TeamId}:{o.Paid}"))
               + "|" + string.Join(",", me.Researched) + "|" + string.Join(",", me.Cuts)
               + "|" + string.Join(",", me.Teams.Select(t => $"{t.Id}:{string.Join("+", t.Kept)}:{t.TrainingUntilHour != null}:{Research.Ready(w, t)}:{t.Scientists}:{string.Join("/", Research.KeptFields(t))}"))
               + "|" + string.Join(",", Intel.Counters(w, Side.Player).Select(c => c.Idea))
               + "|" + string.Join(",", RivalTech.Learning(w, Side.Player).Select(l => $"{l.Id}:{l.Stage}"))
               + $"|{_search}|{_field}|{string.Join(",", _states.Order())}|{_unlocks}|{_sort}|{_selected}|{_allSkills}|{_openTeam}|{_webOpen}|{GetViewportRect().Size}|{w.Winner}";
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

        // Team strip: one compact card per slot; "Skills and queue" unfolds a team's full card.
        var teams = new HBoxContainer();
        teams.AddThemeConstantOverride("separation", 12);
        var me = Economy.Of(w, Side.Player);
        if (_openTeam is int open && me.Teams.All(t => t.Id != open)) _openTeam = null;
        for (var i = 0; i < Research.T.Slots; i++)
        {
            Control card = i < me.Teams.Count ? (_openTeam == me.Teams[i].Id ? TeamCard(w, me.Teams[i]) : TeamStrip(w, me.Teams[i]))
                : i == me.Teams.Count ? EmptySlot(w) : LockedSlot();
            card.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            card.SizeFlagsStretchRatio = 1;
            teams.AddChild(card);
        }
        _root.AddChild(teams);

        // Ideas fill the screen; the discovery web slides out from a tab on the right.
        var bottom = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        bottom.AddThemeConstantOverride("separation", 12);
        var ideas = Box(16);
        ideas.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        ideas.AddChild(IdeasPanel(w));
        bottom.AddChild(ideas);
        if (_webOpen)
        {
            var web = Box(12);
            web.CustomMinimumSize = new Vector2(WebWidth, 0);
            web.AddChild(WebPanel(w));
            bottom.AddChild(web);
        }
        else
        {
            var tab = new Button { Text = "◂\nDiscovery\nweb", FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(TabWidth, 0),
                TooltipText = "Open the discovery web: every idea you know of and what leads to what" };
            tab.AddThemeFontSizeOverride("font_size", 12);
            tab.AddThemeColorOverride("font_color", new Color("7cc4ff"));
            tab.Pressed += () => _webOpen = true;
            bottom.AddChild(tab);
        }
        _root.AddChild(bottom);
    }

    private const float TabWidth = 76;
    private float WebWidth => Mathf.Max(560, GetViewportRect().Size.X * 0.55f);

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
        head.AddChild(Ui.SmallButton("▴ Fold", () => _openTeam = null, "Fold this team back to its compact card"));
        v.AddChild(head);

        // Folded by default to the kept (primary) fields, so the Ideas below get the room; one click shows them all.
        var kept = Research.KeptFields(team).ToList();
        var skillHead = new HBoxContainer();
        skillHead.AddChild(Ui.Label(_allSkills ? "SKILL  (★ kept · ↓ fading · click ★ to keep a field)" : "SKILL", 11, Ui.Dim));
        skillHead.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var hidden = Research.Fields.Length - kept.Count;
        var fadingCount = Enumerable.Range(0, Research.Fields.Length).Count(i => !kept.Contains(i) && team.Skill[i] > 1.0001);
        skillHead.AddChild(Ui.SmallButton(_allSkills ? "▴ Kept only" : $"▸ All fields ({hidden} more{(fadingCount > 0 ? $", {fadingCount} fading" : "")})",
            () => _allSkills = !_allSkills, _allSkills ? "Show only the kept fields" : "Show every field's skill, and pick which to keep"));
        v.AddChild(skillHead);
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
            _live.Add(w2 =>
            {
                // The first idea is paid for when the team starts it: say plainly when it is held up for Industry.
                var stuck = !order.Paid && first && order.Waiting != null;
                days.Text = order.Paid || !first ? $"{Research.DaysUntilDone(w2, order):0.#} d"
                    : stuck ? $"WAITING: {order.Waiting}" : "starts next hour";
                days.AddThemeColorOverride("font_color", stuck ? Ui.Danger : Ui.Dim);
            });
            row.AddChild(days);
            var others = Economy.Of(w, Side.Player).Teams.Where(t => t.Id != team.Id).ToList();
            if (others.Count > 0)
            {
                var move = new MenuButton { Text = "⇄", TooltipText = "Move to another team (progress is kept)", FocusMode = FocusModeEnum.None, Flat = false };
                foreach (var o in others) move.GetPopup().AddItem($"Move to {o.Name}", o.Id);
                move.GetPopup().IdPressed += id => _screen.IssueNow(new SetOrderTeamCommand(Side.Player, order.Id, (int)id), "moving research");
                row.AddChild(move);
            }
            row.AddChild(Ui.SmallButton("✕", () => _screen.CancelOrder(order.Id), "Take off the queue"));
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

    /// <summary>A team folded to one compact card: kept skills, what it works on now and what comes next.</summary>
    private Control TeamStrip(World w, ResearchTeam team)
    {
        var card = Box(10, new Color("12324a"));
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 5);
        card.AddChild(v);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(Ui.Label(team.Name, 16, Ui.Player));
        var size = Ui.Label("", 12, Ui.Dim);
        size.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _live.Add(w2 => size.Text = $"{team.Scientists:0} scientists"
            + (team.TrainingUntilHour is double h ? $" (+{team.TrainingScientists:0} in {(h - w2.Hour) / 24:0.#} d)" : "")
            + (!Research.Ready(w2, team) ? $" · ready in {(team.ReadyHour - w2.Hour) / 24:0.#} d" : ""));
        head.AddChild(size);
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var skills = Ui.Label("", 12, Ui.Text);
        skills.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _live.Add(_ => skills.Text = "★ " + string.Join(" · ", Research.KeptFields(team).Select(i => $"{Research.FieldNames[i].Split(' ')[0]} {team.Skill[i]:0.0}x")));
        head.AddChild(skills);
        v.AddChild(head);

        var queue = Research.QueueOf(w, Side.Player, team.Id).ToList();
        var now = new HBoxContainer();
        now.AddThemeConstantOverride("separation", 10);
        var first = queue.FirstOrDefault();
        now.AddChild(Ui.Label(first == null ? "Nothing queued" : GameData.Research(first.Key).Name, 13, first == null ? Ui.Dim : new Color("f2f5fa")));
        var bar = Bar(80, Ui.Player, () => first == null ? 0 : 1 - first.HoursLeft / Math.Max(1e-6, first.TotalHours), _live);
        bar.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        now.AddChild(bar);
        var left = Ui.Label("", 12, Ui.Dim);
        _live.Add(w2 =>
        {
            var stuck = first != null && !first.Paid && first.Waiting != null;
            left.Text = first == null ? "idle" : first.Paid ? $"{Research.DaysUntilDone(w2, first):0.#} d" : stuck ? $"WAITING: {first.Waiting}" : "starts next hour";
            left.AddThemeColorOverride("font_color", stuck ? Ui.Danger : Ui.Dim);
        });
        now.AddChild(left);
        v.AddChild(now);

        var next = new HBoxContainer();
        next.AddThemeConstantOverride("separation", 6);
        var after = queue.Skip(1).Select(o => GameData.Research(o.Key).Name).ToList();
        var nextText = Ui.Label(after.Count > 0 ? "Next: " + string.Join(" · ", after) : first == null ? "Pick an idea below: Queue on a card" : "Next: nothing yet", 12, Ui.Dim);
        nextText.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nextText.ClipText = true;
        next.AddChild(nextText);
        next.AddChild(Ui.SmallButton("▾ Skills and queue", () => _openTeam = team.Id, "Unfold this team: skills, kept fields, queue order, train and split"));
        v.AddChild(next);
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
        var card = Box(10, new Color("2a3446"), dashed: true);
        var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        v.AddThemeConstantOverride("separation", 6);
        card.AddChild(v);
        var title = Ui.Label("Empty team slot", 15, new Color("aab4c3"));
        title.HorizontalAlignment = HorizontalAlignment.Center;
        v.AddChild(title);
        var t = Research.T;
        card.TooltipText = $"Found a new team: {t.FoundScientists:0} scientists, {t.FoundSkill:0.#}x in the speciality you pick, ready in {t.FoundDays:0} days. "
                           + "Or split one off an existing team (it takes half of each skill).";
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
        var card = Box(10, new Color("2a3446"), dashed: true);
        var v = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        var title = Ui.Label("Empty team slot", 15, new Color("aab4c3"));
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

    private Control IdeasPanel(World w)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);

        // Only ideas the side has come across: ones it can work on now, has done or queued, or whose needs
        // are partly done. Undiscovered ideas (the old "????") and ideas for a later build stay hidden.
        var all = GameData.Economy.Research.Where(r => Discovered(w, r)).ToList();
        var shown = all.Where(r => (_field == null || r.Field == _field) && (_unlocks == null || r.Unlocks == _unlocks)
                                   && _states.Contains(StateOf(w, r.Id))
                                   && (_search.Length == 0 || r.Name.Contains(_search, StringComparison.OrdinalIgnoreCase)
                                       || r.Effect.Contains(_search, StringComparison.OrdinalIgnoreCase))).ToList();
        shown = _sort switch
        {
            Sort.Cheapest => shown.OrderBy(r => r.Days * Research.Pace(r.Tier)).ThenBy(r => r.Name).ToList(),
            Sort.Name => shown.OrderBy(r => r.Name).ToList(),
            _ => shown.OrderBy(r => StateOf(w, r.Id) switch { IdeaState.Researching => 0, IdeaState.Queued => 1, IdeaState.Available => 2, IdeaState.Locked => 3, _ => 4 })
                .ThenBy(r => Quickest(w, r.Id).Days).ToList(),
        };

        var head = new HBoxContainer();
        head.AddChild(Ui.Label("Ideas", 16, new Color("f2f5fa")));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.Label($"{shown.Count} of {all.Count} shown · days are for the team that would finish first", 12, Ui.Dim));
        v.AddChild(head);
        // Intelligence hints: what the rival has been seen to field, and the idea that answers it.
        var counters = Intel.Counters(w, Side.Player).GroupBy(c => c.Idea).Select(g => g.First()).Take(3).ToList();
        if (counters.Count > 0)
            v.AddChild(Ui.Label("Intel: " + string.Join(" · ", counters.Select(c => $"{c.Why} → {GameData.Research(c.Idea).Name}")), 12, new Color("ffd27a")));
        var learning = RivalTech.Learning(w, Side.Player).Take(4).ToList();
        if (learning.Count > 0)
            v.AddChild(Ui.Label("Their research we are learning: " + string.Join(" · ", learning.Select(l =>
                l.Stage == RivalStage.Seen ? $"something unknown ({l.Progress:P0})" : $"{GameData.Research(l.Id).Name} ({RivalTech.StageName(l.Stage)})")), 12, Ui.Rival));

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
        sort.AddItem("Shortest first", (int)Sort.Cheapest);
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

        var scroll = _ideaScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(0, 260) };
        var width = GetViewportRect().Size.X - (_webOpen ? WebWidth : TabWidth) - 110;
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
        card.TooltipText = state == IdeaState.Available ? "Click to light up its path in the web · double-click to queue it with the team that finishes it first"
            : "Click to light up its path in the web";
        card.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            if (click.DoubleClick)
            {
                // Double-click: queue it with the team that would finish it first (a home order, so it works paused).
                _selected = r.Id;
                if (StateOf(Main.Instance.Sim!.World, r.Id) == IdeaState.Available && Main.Instance.Sim.World.Winner == null)
                    _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Research, r.Id), "queueing research");
                card.AcceptEvent();
                return;
            }
            _selected = _selected == r.Id ? null : r.Id;
        };
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 0);
        card.AddChild(v);

        var pic = new TextureRect
        {
            Texture = Art.IdeaPicture(r.Id, r.Picture),
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
        if (Intel.Counters(w, Side.Player).FirstOrDefault(c => c.Idea == r.Id) is { Idea: not null } counter)
        {
            var hint = Ui.Label("⚔ counter", 11, new Color("ffd27a"));
            hint.Position = new Vector2(8, width * 12 / 35 - 20);
            hint.TooltipText = $"Intel: {counter.Why}. This idea answers it";
            hint.MouseFilter = MouseFilterEnum.Pass;
            pic.AddChild(hint);
        }
        if (RivalTech.StageOf(w, Side.Player, r.Id) is var stage and not RivalStage.None)
        {
            // Learned from the rival (board 26): Studied shows its name; Unlocked skips its prerequisites and is quicker.
            var theirs = Ui.Label($"◆ theirs: {RivalTech.StageName(stage)}", 11, Ui.Rival);
            theirs.Position = new Vector2(width - 130, width * 12 / 35 - 20);
            theirs.TooltipText = stage == RivalStage.Unlocked
                ? $"Learned from the rival: queue it without its prerequisites, {GameData.Weapons.R.UnlockedCut:P0} quicker"
                : $"Learning it from the rival: {RivalTech.Progress(w, Side.Player, r.Id):P0} (unlocked at 100 %). Battles, wrecks, captures and looks at their colonies teach it";
            theirs.MouseFilter = MouseFilterEnum.Pass;
            pic.AddChild(theirs);
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
        var meta = new List<string> { $"about {Research.BaseHours(w, Side.Player, r.Id) / 24:0} days" };
        if (Research.Missing(w, Side.Player, r.Id) is string missing) meta.Add(missing);
        else if (r.Needs is { Count: > 0 }) meta.Add("needs " + string.Join(", ", r.Needs.Select(n => GameData.Research(n).Name)));
        if (Economy.Of(w, Side.Player).Finds.Find(f => f.Idea == r.Id) is SurveyFind find)
            meta.Add($"{w.FindBody(find.BodyId)?.Name} survey: −{Research.T.SurveyCut * 100:0} % time");
        var m = Ui.Label(string.Join(" · ", meta), 11, Ui.Dim);
        m.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        m.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        foot.AddChild(m);
        t.AddChild(foot);
        if (state == IdeaState.Available && w.Winner == null && QueueButtons(w, r) is Control queue) t.AddChild(queue);
        return card;
    }

    /// <summary>"Queue → Vega · 9 d": one click queues with the team that finishes it first; ▾ picks another team.</summary>
    private Control? QueueButtons(World w, ResearchData r)
    {
        var best = Research.BestTeamFor(w, Side.Player, r.Id);
        if (best == null) return null;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var quick = Ui.SmallButton($"Queue → {best.Name} · {Quickest(w, r.Id).Days:0.#} d",
            () => _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Research, r.Id, TeamId: best.Id), "queueing research"),
            $"Queue with {best.Name}, the team that would finish it first (double-clicking the card does the same)");
        quick.AddThemeColorOverride("font_color", Ui.Player);
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

    // ---- Discovery web -------------------------------------------------------------------------

    private Control WebPanel(World w)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        head.AddChild(Ui.Label("Discovery web", 16, new Color("f2f5fa")));
        var how = Ui.Label("drag to pan · wheel to zoom · click an idea", 11, Ui.Dim);
        how.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(how);
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.SmallButton("Fit", DiscoveryWeb.Refit, "Fit the whole web in view"));
        head.AddChild(Ui.SmallButton("▸ Hide", () => _webOpen = false, "Slide the web away so the ideas get the room"));
        v.AddChild(head);
        var web = new DiscoveryWeb(w, _selected, id => _selected = _selected == id ? null : id)
        {
            SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipContents = true,
        };
        v.AddChild(web);
        v.AddChild(DiscoveryWeb.Legend());
        if (_selected != null && GameData.Economy.Research.Find(r => r.Id == _selected) is ResearchData sel) v.AddChild(Picked(w, sel));
        return v;
    }

    /// <summary>The idea picked in the web: what it does, what it needs and what it opens the way to.</summary>
    private Control Picked(World w, ResearchData r)
    {
        var box = Box(10, Ui.Player);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        box.AddChild(v);
        var state = StateOf(w, r.Id);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        head.AddChild(Ui.Label(r.Name, 15, new Color("f2f5fa")));
        var meta = Ui.Label($"{Research.FieldName(r.Field)} · tier {r.Tier} · {state}", 12, Ui.Dim);
        meta.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(meta);
        v.AddChild(head);
        var effect = Ui.Label(r.Effect, 12, new Color("aab4c3"));
        effect.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(effect);
        string Mark(string id) => GameData.Research(id).Name + (Tech.Has(w, Side.Player, id) ? " ✓" : "");
        if (r.Needs is { Count: > 0 }) v.AddChild(Ui.Label("Needs: " + string.Join(", ", r.Needs.Select(Mark)), 12, Ui.Text));
        var opens = GameData.Economy.Research.Where(o => Discovered(w, o) && (o.Needs?.Contains(r.Id) ?? false))
            .Select(o => o.Name + ((o.Needs!.Where(n => n != r.Id && !Tech.Has(w, Side.Player, n)).Select(n => GameData.Research(n).Name).ToList() is { Count: > 0 } also)
                ? $" (also needs {string.Join(", ", also)})" : "")).ToList();
        if (opens.Count > 0)
        {
            var o = Ui.Label("Opens the way to: " + string.Join(" · ", opens), 12, Ui.Text);
            o.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            v.AddChild(o);
        }
        if (state == IdeaState.Available && w.Winner == null && QueueButtons(w, r) is Control queue) v.AddChild(queue);
        return box;
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

    public DiscoveryWeb(World w, string? selected, Action<string> pick)
    {
        _pick = pick;
        MouseFilter = MouseFilterEnum.Stop;
        var all = GameData.Economy.Research.Where(r => !r.Later).ToList();
        var known = all.Where(r => ResearchScreen.Discovered(w, r)).Select(r => r.Id).ToHashSet();
        // One step beyond: ideas that something you know leads to, shown blurred and nameless.
        var beyond = all.Where(r => !known.Contains(r.Id) && (r.Needs ?? new()).Any(known.Contains)).Select(r => r.Id).ToHashSet();
        var shown = all.Where(r => known.Contains(r.Id) || beyond.Contains(r.Id)).ToList();

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
