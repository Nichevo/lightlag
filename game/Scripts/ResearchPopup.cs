using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// A small research panel under the Research tab of the menu bar: each team with the idea it works on now (and its
/// picture), progress, days left or why it waits, what comes next and any training. Clicking a team, or "Open", opens
/// the full research screen; clicking anywhere else closes it. The T key still opens the full screen directly.
/// </summary>
public partial class ResearchPopup : PanelContainer
{
    private const float Width = 380;
    private readonly GameScreen _screen;
    private readonly VBoxContainer _rows;
    private readonly List<Action<World>> _live = new();
    private string _shape = "";

    /// <summary>The place on screen of the Research tab, so a click on it isn't taken as a click outside.</summary>
    public Func<Rect2>? TabRect { get; set; }

    public ResearchPopup() : this(null!) { }

    public ResearchPopup(GameScreen screen)
    {
        _screen = screen;
        MouseFilter = MouseFilterEnum.Stop;
        var box = new StyleBoxFlat { BgColor = new Color(0.035f, 0.05f, 0.08f, 0.98f), BorderColor = new Color(Ui.Player, 0.45f) };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(8);
        box.SetContentMarginAll(12);
        box.ShadowColor = new Color(0, 0, 0, 0.5f);
        box.ShadowSize = 10;
        AddThemeStyleboxOverride("panel", box);
        CustomMinimumSize = new Vector2(Width, 0);
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 8);
        AddChild(_rows);
    }

    /// <summary>Open the full research screen (on one team when given) and close the popup.</summary>
    private void OpenFull(int? team)
    {
        if (team is int t) ResearchScreen.ShowTeam(t);
        Visible = false;
        _screen.CloseMenuScreens();
        _screen.ShowResearch = true;
    }

    public override void _Input(InputEvent e)
    {
        // A click anywhere but the popup or the Research tab closes it (the click still goes on to what is under it).
        if (!Visible) return;
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Visible = false;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventMouseButton { Pressed: true } click) return;
        if (GetGlobalRect().HasPoint(click.Position) || TabRect?.Invoke().HasPoint(click.Position) == true) return;
        Visible = false;
    }

    public void Refresh(World w)
    {
        var me = Economy.Of(w, Side.Player);
        var shape = string.Join(",", me.Teams.Select(t => $"{t.Id}:{t.Scientists}:{t.TrainingUntilHour != null}:{t.TrainingQueued}:{Research.Ready(w, t)}:"
                                                          + string.Join("/", Research.QueueOf(w, Side.Player, t.Id).Select(o => $"{o.Key}{o.Paid}"))));
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            Rebuild(w);
        }
        foreach (var update in _live) update(w);
        Size = new Vector2(Width, GetCombinedMinimumSize().Y);
    }

    private void Rebuild(World w)
    {
        Ui.ClearChildren(_rows);
        _live.Clear();
        var me = Economy.Of(w, Side.Player);

        var head = new HBoxContainer();
        head.AddChild(Ui.Label("Research teams", 16, new Color("f2f5fa")));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var open = Ui.SmallButton("Open research  T", () => OpenFull(null), "The full research screen: ideas, the web and each team's queue");
        open.AddThemeFontSizeOverride("font_size", 13);
        open.CustomMinimumSize = new Vector2(0, 28);
        head.AddChild(open);
        _rows.AddChild(head);

        foreach (var team in me.Teams) _rows.AddChild(TeamRow(w, team));
        var empty = Research.T.Slots - me.Teams.Count;
        if (empty > 0)
        {
            var l = Ui.Label($"{empty} empty team slot{(empty == 1 ? "" : "s")}: found or split a team on the research screen", 11, Ui.Dim);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.CustomMinimumSize = new Vector2(Width - 24, 0);
            _rows.AddChild(l);
        }
    }

    private Control TeamRow(World w, ResearchTeam team)
    {
        var queue = Research.QueueOf(w, Side.Player, team.Id).ToList();
        var first = queue.FirstOrDefault();
        var row = new Button { FocusMode = FocusModeEnum.None, TooltipText = $"Open {team.Name} on the research screen" };
        row.Pressed += () => Main.Instance.RunGuarded("research popup", () => OpenFull(team.Id));
        var line = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        line.AddThemeConstantOverride("separation", 10);
        line.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        line.OffsetLeft = 6;
        line.OffsetTop = 6;
        line.OffsetRight = -8;
        line.OffsetBottom = -6;
        row.AddChild(line);

        const float picW = 120;
        if (first != null)
            line.AddChild(new TextureRect
            {
                Texture = Art.IdeaPicture(first.Key, GameData.Research(first.Key).Picture),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                CustomMinimumSize = new Vector2(picW, picW * 12 / 35), SizeFlagsVertical = SizeFlags.ShrinkCenter,
                MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.LinearWithMipmaps,
            });
        var text = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        text.AddThemeConstantOverride("separation", 2);
        line.AddChild(text);

        var name = Ui.Label($"{team.Name} · {team.Scientists:0}", 12, Ui.Player);
        name.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(name);
        var idea = Ui.Label(first == null ? "Nothing queued" : GameData.Research(first.Key).Name, 14, first == null ? Ui.Dim : new Color("f2f5fa"));
        idea.ClipText = true;
        idea.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        idea.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(idea);

        var progress = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        progress.AddThemeConstantOverride("separation", 8);
        var back = new ColorRect { Color = new Color("1a2230"), CustomMinimumSize = new Vector2(70, 5), SizeFlagsVertical = SizeFlags.ShrinkCenter,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        var fill = new ColorRect { Color = Ui.Player, MouseFilter = MouseFilterEnum.Ignore };
        back.AddChild(fill);
        progress.AddChild(back);
        var days = Ui.Label("", 12, Ui.Dim);
        days.ClipText = true;
        days.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        days.HorizontalAlignment = HorizontalAlignment.Right;
        days.MouseFilter = MouseFilterEnum.Ignore;
        progress.AddChild(days);
        if (first != null) text.AddChild(progress);

        // What comes next on this team's queue: up to three ideas, each with the days until it would be done.
        if (first != null)
        {
            var next = queue.Skip(1).ToList();
            var nextHead = Ui.Label(next.Count == 0 ? "Next: nothing queued after this" : "NEXT", 11, next.Count == 0 ? Ui.Warn : Ui.Dim);
            nextHead.MouseFilter = MouseFilterEnum.Ignore;
            text.AddChild(nextHead);
            foreach (var order in next.Take(3))
            {
                var r = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
                var n = Ui.Label(GameData.Research(order.Key).Name, 12, Ui.Text);
                n.ClipText = true;
                n.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                n.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                n.MouseFilter = MouseFilterEnum.Ignore;
                r.AddChild(n);
                var d = Ui.Label("", 11, Ui.Dim);
                d.MouseFilter = MouseFilterEnum.Ignore;
                r.AddChild(d);
                _live.Add(w2 => d.Text = $"done in {Research.DaysUntilDone(w2, order):0} d");
                text.AddChild(r);
            }
            if (next.Count > 3)
            {
                var rest = Ui.Label($"+{next.Count - 3} more", 11, Ui.Dim);
                rest.MouseFilter = MouseFilterEnum.Ignore;
                text.AddChild(rest);
            }
        }
        var training = Ui.Label("", 11, Ui.Dim);
        training.MouseFilter = MouseFilterEnum.Ignore;
        text.AddChild(training);

        _live.Add(w2 =>
        {
            var share = first == null ? 0 : 1 - first.HoursLeft / Math.Max(1e-6, first.TotalHours);
            fill.Size = new Vector2((float)(Math.Clamp(share, 0, 1) * back.Size.X), 5);
            var stuck = first != null && !first.Paid && first.Waiting != null;
            days.Text = !Research.Ready(w2, team) ? $"ready in {(team.ReadyHour - w2.Hour) / 24:0.#} d"
                : first == null ? "" : first.Paid ? $"{Research.DaysUntilDone(w2, first):0.#} d left" : stuck ? $"WAITING: {first.Waiting}" : "starts next hour";
            days.AddThemeColorOverride("font_color", stuck ? Ui.Danger : Ui.Dim);
            training.Text = ResearchScreen.TrainingText(w2, team);
            training.Visible = training.Text.Length > 0;
            if (first == null) idea.AddThemeColorOverride("font_color", Ui.Warn);
            row.CustomMinimumSize = new Vector2(Width - 24, Mathf.Max(picW * 12 / 35, text.GetCombinedMinimumSize().Y) + 12);
        });
        // A Button doesn't size to its children: it gets the height its content needs (kept up to date above).
        row.CustomMinimumSize = new Vector2(Width - 24, 80);
        return row;
    }
}
