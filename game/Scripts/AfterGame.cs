using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The after-game screen (Plan 4, "Lift the fog at the end"): who won and how, then four pages. Story: control share
/// over the whole game with the turning points marked and listed. Replay: the map with the fog lifted, every fleet
/// where it really was, and (switchable) where the rival believed your fleets were and where you believed its fleets
/// were. Rival's mind: its goals and reasons day by day, with what it thought and how strong it guessed you were.
/// Numbers: the totals the old end panel showed. Reads the game's story (Chronicle); it only draws.
/// </summary>
public partial class AfterGameScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!;
    private Label _summary = null!;
    private HBoxContainer _mapRow = null!;
    private readonly List<Button> _tabs = new();
    private readonly List<Control> _pages = new();
    private int _tab;
    private string _built = "";

    // Story
    private ControlGraph _graph = null!;
    private VBoxContainer _points = null!;
    private Label _storyNote = null!;

    // Replay
    private ReplayMap _map = null!;
    private HSlider _slider = null!;
    private Button _play = null!;
    private Label _when = null!;
    private Label _replayMind = null!;
    private Label _replayMoments = null!;
    private Label _replayNote = null!;
    private bool _playing;
    private int _speed = 1;
    private static readonly double[] DaysPerSecond = { 1, 3, 8, 20 };

    // Rival's mind and numbers
    private VBoxContainer _mindList = null!;
    private Label _numbers = null!;

    public static readonly string[] Pages = { "Story", "Replay", "Rival's mind", "Numbers" };

    public AfterGameScreen() : this(null!) { }
    public AfterGameScreen(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 16);
        // Solid, so the map and its labels don't show through a page full of text.
        if (GetThemeStylebox("panel") is StyleBoxFlat panel) panel.BgColor = new Color(0.03f, 0.045f, 0.075f);
        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        AddChild(root);

        // Header: the result, then the buttons that were on the old end panel.
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 16);
        _title = Ui.Label("", 30);
        head.AddChild(_title);
        _summary = Ui.Label("", 15);
        _summary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _summary.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        head.AddChild(_summary);
        var buttons = new HBoxContainer();
        buttons.AddThemeConstantOverride("separation", 8);
        buttons.AddChild(Wide(Ui.SmallButton("Keep looking", () => _screen.CloseEnd(), "Close this and look around the map (Esc). The game menu opens it again"), 130));
        buttons.AddChild(Wide(Ui.SmallButton("Title screen", () => Main.Instance.ShowTitle(), "Back to the title screen"), 120));
        head.AddChild(buttons);
        root.AddChild(head);
        // Seeded maps: the same map again, or the same start from the rival's side (see Main.PlayMapAgain).
        _mapRow = new HBoxContainer { Visible = false };
        _mapRow.AddThemeConstantOverride("separation", 8);
        _mapRow.AddChild(Wide(Ui.SmallButton("Play this map again", () => Main.Instance.PlayMapAgain(swapSides: false),
            "The same map, rules and races, with a new start: a new start date and homes shuffled"), 170));
        _mapRow.AddChild(Wide(Ui.SmallButton("Swap sides", () => Main.Instance.PlayMapAgain(swapSides: true),
            "The same map and start, but you take the rival's home and the rival takes yours"), 120));
        root.AddChild(_mapRow);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        for (var i = 0; i < Pages.Length; i++)
        {
            var index = i;
            var b = Wide(Ui.SmallButton(Pages[i], () => ShowPage(index), ""), 120);
            _tabs.Add(b);
            tabs.AddChild(b);
        }
        root.AddChild(tabs);

        var pages = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(pages);
        foreach (var page in new[] { StoryPage(), ReplayPage(), MindPage(), NumbersPage() })
        {
            page.SizeFlagsVertical = SizeFlags.ExpandFill;
            page.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _pages.Add(page);
            pages.AddChild(page);
        }
        ShowPage(0);
    }

    private static Button Wide(Button b, float width)
    {
        b.CustomMinimumSize = new Vector2(width, 34);
        return b;
    }

    private void ShowPage(int i)
    {
        _tab = i;
        for (var p = 0; p < _pages.Count; p++) _pages[p].Visible = p == i;
        for (var t = 0; t < _tabs.Count; t++) _tabs[t].Modulate = t == i ? Ui.Player : new Color(1, 1, 1, 0.7f);
        if (i != 1) _playing = false;
    }

    // ---- Pages ------------------------------------------------------------------------------------

    private Control StoryPage()
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 8);
        var h = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        h.AddThemeConstantOverride("separation", 12);
        _graph = new ControlGraph { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(480, 300) };
        _graph.OnPick = m => GoTo(m.Hour - 12);
        h.AddChild(_graph);
        var side = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        side.AddThemeConstantOverride("separation", 6);
        side.AddChild(Ui.Label("Turning points", 18, Ui.Player));
        side.AddChild(Hint("Click one to watch it in the replay."));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _points = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _points.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_points);
        side.AddChild(scroll);
        h.AddChild(side);
        v.AddChild(h);
        _storyNote = Hint("");
        v.AddChild(_storyNote);
        return v;
    }

    private Control ReplayPage()
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        var h = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        h.AddThemeConstantOverride("separation", 12);
        _map = new ReplayMap();
        h.AddChild(_map);
        var side = new VBoxContainer { CustomMinimumSize = new Vector2(340, 0) };
        side.AddThemeConstantOverride("separation", 8);
        _when = Ui.Label("", 18, Ui.Player);
        side.AddChild(_when);
        side.AddChild(Ui.Label("Show", 14, Ui.Dim));
        side.AddChild(Toggle("Where the rival thought your fleets were", true, on => _map.ShowRivalBeliefs = on,
            "Rings in the rival's colour where its command believed your fleets were, joined to where they really were"));
        side.AddChild(Toggle("Where you thought its fleets were", false, on => _map.ShowYourBeliefs = on,
            "Rings in your colour where your command believed its fleets were, joined to where they really were"));
        side.AddChild(Ui.Label("The rival's aim that day", 14, Ui.Dim));
        _replayMind = Ui.Label("", 14);
        _replayMind.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _replayMind.CustomMinimumSize = new Vector2(330, 0);
        side.AddChild(_replayMind);
        side.AddChild(Ui.Label("Around now", 14, Ui.Dim));
        _replayMoments = Ui.Label("", 14);
        _replayMoments.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _replayMoments.CustomMinimumSize = new Vector2(330, 0);
        side.AddChild(_replayMoments);
        h.AddChild(side);
        v.AddChild(h);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 8);
        _play = Wide(Ui.SmallButton("▶ Play", () => { _playing = !_playing; if (_playing && _slider.Value >= _slider.MaxValue) Seek(_slider.MinValue); }, "Play or pause the replay"), 90);
        bar.AddChild(_play);
        var speed = Wide(Ui.SmallButton("", () => { }, "Replay speed: game days per second"), 90);
        speed.Text = $"{DaysPerSecond[_speed]:0} d/s";
        speed.Pressed += () => { _speed = (_speed + 1) % DaysPerSecond.Length; speed.Text = $"{DaysPerSecond[_speed]:0} d/s"; };
        bar.AddChild(speed);
        _slider = new HSlider { SizeFlagsHorizontal = SizeFlags.ExpandFill, Step = 1, CustomMinimumSize = new Vector2(200, 30), FocusMode = FocusModeEnum.None };
        _slider.ValueChanged += value => { _map.Hour = value; };
        bar.AddChild(_slider);
        v.AddChild(bar);
        _replayNote = Hint("Every fleet is shown where it really was: filled marks, sized by ships. Wheel zooms, right-drag pans, double-click resets the view.");
        v.AddChild(_replayNote);
        return v;
    }

    private Control MindPage()
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _mindList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mindList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_mindList);
        return scroll;
    }

    private Control NumbersPage()
    {
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _numbers = Ui.Label("", 15);
        _numbers.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _numbers.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(_numbers);
        return scroll;
    }

    private static Label Hint(string text)
    {
        var l = Ui.Label(text, 13, Ui.Dim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return l;
    }

    private static CheckButton Toggle(string text, bool on, Action<bool> changed, string tip)
    {
        var b = new CheckButton { Text = text, ButtonPressed = on, TooltipText = tip, FocusMode = FocusModeEnum.None };
        b.AddThemeFontSizeOverride("font_size", 14);
        b.Toggled += v => changed(v);
        return b;
    }

    // ---- Refresh ----------------------------------------------------------------------------------

    public void Refresh(World w)
    {
        var view = GetViewportRect().Size;
        var under = new Vector2(8, Hud.ContentTop + 4);
        var size = view - under - new Vector2(8, 8);
        if (Position != under || Size != size) { Position = under; Size = size; }
        var c = w.Chronicle;
        var key = $"{w.WinHour}|{c.Frames.Count}|{c.Moments.Count}|{c.MindDays.Count}|{w.Map.Seeded}";
        if (key != _built)
        {
            _built = key;
            Build(w);
        }
        if (_screen.EndPageWanted is int page) { ShowPage(page); _screen.EndPageWanted = null; }
        if (_screen.EndHourWanted is double hour) { GoTo(hour); _screen.EndHourWanted = null; }
    }

    private void Build(World w)
    {
        var c = w.Chronicle;
        _mapRow.Visible = w.Map.Seeded;
        var won = w.Winner == Side.Player;
        _title.Text = won ? "Victory" : "Defeat";
        _title.AddThemeColorOverride("font_color", won ? Ui.Player : Ui.Rival);
        var who = won ? "You" : w.Winner is Side ws ? Chronicle.Who(w, ws) : "The rival";
        var rivals = w.Minds.Where(m => m.Side != Side.Player)
            .Select(m => $"{Chronicle.Who(w, m.Side)} was a {m.Personality} on {m.Difficulty}.");
        _summary.Text = $"{who} {w.WinReason} on {GameTime.Format(w.WinHour ?? w.Hour)}. " + string.Join(" ", rivals);

        // Story
        var points = Chronicle.TurningPoints(w);
        _graph.World = w;
        _graph.Points = points;
        _graph.QueueRedraw();
        Ui.ClearChildren(_points);
        if (points.Count == 0) _points.AddChild(Hint("Nothing was recorded for this game."));
        for (var i = 0; i < points.Count; i++)
        {
            var m = points[i];
            var b = new Button
            {
                Text = $"{i + 1}.  {GameTime.Format(m.Hour)}\n{m.Text}", Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None,
                TooltipText = "Watch it in the replay", AutowrapMode = TextServer.AutowrapMode.WordSmart,
                CustomMinimumSize = new Vector2(360, 0),
            };
            b.AddThemeFontSizeOverride("font_size", 14);
            b.AddThemeColorOverride("font_color", Ui.SideColor(m.Side));
            var hour = m.Hour;
            b.Pressed += () => Main.Instance.RunGuarded("turning point", () => GoTo(hour - 12));
            _points.AddChild(b);
        }
        var older = c.FromHour > 0 ? $" This game began before the after-game record existed, so it starts on {GameTime.Format(c.FromHour)}." : "";
        _storyNote.Text = "Each side's share of control over the game (the 80 % line tips it). Numbered marks are the turning points: "
                          + "planets changing hands, costly battles, the lead changing, terms and the end." + older;

        // Replay
        _map.World = w;
        if (c.Frames.Count > 0)
        {
            _slider.MinValue = c.Frames[0].Hour;
            _slider.MaxValue = Math.Max(c.Frames[0].Hour + 1, c.Frames[^1].Hour);
            Seek(_slider.MaxValue);
        }
        _replayNote.Text = c.Frames.Count == 0
            ? "No replay was recorded for this game."
            : "Every fleet is shown where it really was: filled marks, sized by ships. Rings are where a side's command believed an enemy fleet was "
              + "(dashed when it was out of sight), joined to where it really was. Wheel zooms, right-drag pans, double-click resets the view." + older;

        // Rival's mind
        BuildMinds(w);

        // Numbers
        var mine = w.Ships.Count(s => s.Side == Side.Player);
        var theirs = w.Ships.Count(s => s.Side is Side.Rival or Side.Third);
        _numbers.Text = $"Ships left: you {mine}, rival {theirs}. Planets: you {w.Bodies.Count(b => b.Owner == Side.Player && b.Industry > 0)}, "
                        + $"rival {w.Bodies.Count(b => b.Owner is Side.Rival or Side.Third && b.Industry > 0)}.\n\n"
                        + Stats.Summary(w)
                        + string.Concat(w.Minds.Where(m => m.Side != Side.Player).Select(m => $"\n\n{Chronicle.Who(w, m.Side)} was a {m.Personality} on {m.Difficulty}."
                            + (Lessons.Summary(m) is { Length: > 0 } learned ? " " + learned : "")))
                        + (Main.Instance.LearnChoice != LearnMode.Off && w.Minds.Count > 0 ? "\n\nThis game is now in the rivals' memory: they will learn from it next time." : "")
                        + $"\n\nMap: {w.Map.Describe()}";
    }

    /// <summary>One block per run of days with the same goal: the days, the goal and why, its guess of your strength, and what it thought each day.</summary>
    private void BuildMinds(World w)
    {
        Ui.ClearChildren(_mindList);
        var sides = w.Chronicle.MindDays.Select(d => d.Side).Where(s => s != Side.Player).Distinct().OrderBy(s => (int)s).ToList();
        if (sides.Count == 0)
        {
            _mindList.AddChild(Hint("No record of the rival's thinking was kept for this game."));
            return;
        }
        _mindList.AddChild(Hint("What the rival was trying to do, day by day, and why. Click a run of days to watch it in the replay. "
                                + "\"Guessed your strength\" is the fighting power it believed you had against what you really had."));
        foreach (var side in sides)
        {
            if (sides.Count > 1) _mindList.AddChild(Ui.Label(Chronicle.Who(w, side), 20, Ui.SideColor(side)));
            var days = Chronicle.MindDaysOf(w, side);
            var i = 0;
            while (i < days.Count)
            {
                var j = i;
                while (j + 1 < days.Count && days[j + 1].Goal == days[i].Goal) j++;
                AddMindBlock(w, side, days.GetRange(i, j - i + 1));
                i = j + 1;
            }
        }
    }

    private void AddMindBlock(World w, Side side, List<MindDay> run)
    {
        var first = run[0];
        var last = run[^1];
        var span = first.Day == last.Day ? $"Day {first.Day}" : $"Days {first.Day}–{last.Day}";
        var b = new Button { Text = $"{span} · {first.Goal}", Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, TooltipText = "Watch these days in the replay" };
        b.AddThemeFontSizeOverride("font_size", 16);
        b.AddThemeColorOverride("font_color", Ui.SideColor(side));
        var hour = first.Day * 24.0;
        b.Pressed += () => Main.Instance.RunGuarded("mind days", () => GoTo(hour));
        _mindList.AddChild(b);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 2);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddChild(box);
        _mindList.AddChild(margin);
        void Line(string text, Color? colour = null, int size = 14)
        {
            var l = Ui.Label(text, size, colour);
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            box.AddChild(l);
        }
        // The reasons as they changed through the run (the same reason on following days once).
        string? why = null;
        foreach (var d in run)
        {
            if (d.Why != "" && d.Why != why) Line($"Day {d.Day}: {d.Why}");
            why = d.Why;
            foreach (var t in d.Thoughts!.Where(t => !t.StartsWith("Goal now", StringComparison.Ordinal)))
                Line($"Day {d.Day}: {t}", Ui.Dim, 13);
        }
        var mid = run[run.Count / 2];
        var extras = new List<string>();
        if (mid.EnemyPowerTrue > 0 || mid.EnemyPowerGuess > 0)
            extras.Add($"Guessed your strength at {mid.EnemyPowerGuess:0} (really {mid.EnemyPowerTrue:0}; its own {mid.OwnPower:0})");
        if (mid.Jobs != "") extras.Add($"Fleets: {mid.Jobs}");
        if (mid.Research != "") extras.Add($"Research leaned to {mid.Research}");
        if (extras.Count > 0) Line(string.Join(" · ", extras), new Color(Ui.Text, 0.75f), 13);
    }

    // ---- Replay -----------------------------------------------------------------------------------

    private void GoTo(double hour)
    {
        ShowPage(1);
        _playing = false;
        Seek(hour);
    }

    private void Seek(double hour)
    {
        _slider.Value = Math.Clamp(hour, _slider.MinValue, _slider.MaxValue);
        _map.Hour = _slider.Value;
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree() || _map.World is not World w) return;
        if (_playing)
        {
            var next = _slider.Value + delta * DaysPerSecond[_speed] * 24;
            if (next >= _slider.MaxValue) { next = _slider.MaxValue; _playing = false; }
            _slider.SetValueNoSignal(next);
            _map.Hour = next;
        }
        _play.Text = _playing ? "❚❚ Pause" : "▶ Play";
        if (_tab != 1) return;
        var hour = _map.Hour;
        _when.Text = GameTime.Format(hour);
        var side = w.Chronicle.MindDays.Select(d => d.Side).FirstOrDefault(s => s != Side.Player, Side.Rival);
        _replayMind.Text = Chronicle.MindOn(w, side, (int)(hour / 24)) is MindDay m
            ? $"{m.Goal}" + (m.Why != "" ? $": {m.Why}" : "") + (m.EnemyPowerGuess > 0 ? $"\nGuessed your strength at {m.EnemyPowerGuess:0} (really {m.EnemyPowerTrue:0})." : "")
            : "No record.";
        var near = w.Chronicle.Moments.Where(x => x.Hour <= hour && x.Hour > hour - 72).OrderByDescending(x => x.Hour).Take(4)
            .Select(x => $"{GameTime.Format(x.Hour)}: {x.Text}").ToList();
        _replayMoments.Text = near.Count > 0 ? string.Join("\n", near) : "Nothing in the last three days.";
    }
}

/// <summary>Control share by day for every side, with the turning points as numbered marks (click one to watch it) and the replay's hour.</summary>
public partial class ControlGraph : Control
{
    public World? World;
    public List<Moment> Points = new();
    public Action<Moment> OnPick = _ => { };
    private readonly List<(Vector2 At, Moment M)> _spots = new();

    public ControlGraph()
    {
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            var hit = _spots.Where(s => s.At.DistanceTo(click.Position) < 14).OrderBy(s => s.At.DistanceTo(click.Position)).Select(s => s.M).FirstOrDefault();
            if (hit != null) { AcceptEvent(); Main.Instance.RunGuarded("turning point mark", () => OnPick(hit)); }
        }
        else if (e is InputEventMouseMotion motion)
        {
            var hit = _spots.Where(s => s.At.DistanceTo(motion.Position) < 14).Select(s => s.M).FirstOrDefault();
            TooltipText = hit != null ? $"{GameTime.Format(hit.Hour)}: {hit.Text}" : "";
        }
    }

    public override void _Draw()
    {
        _spots.Clear();
        if (World is not World w) return;
        var font = ThemeDB.FallbackFont;
        var rect = new Rect2(44, 30, Size.X - 56, Size.Y - 58);
        DrawRect(rect, new Color(1, 1, 1, 0.03f));
        var sides = w.History.Select(r => r.Side).Distinct().OrderBy(s => (int)s).ToList();
        if (w.History.Count == 0 || sides.Count == 0) return;
        var firstDay = w.History.Min(r => r.Day);
        var lastDay = Math.Max(firstDay + 1, w.History.Max(r => r.Day));
        Vector2 P(double day, double v) => new(rect.Position.X + (float)((day - firstDay) / (lastDay - firstDay)) * rect.Size.X, rect.End.Y - (float)v * rect.Size.Y);

        for (var i = 0; i <= 4; i++)
        {
            var y = P(firstDay, i / 4.0).Y;
            DrawLine(new Vector2(rect.Position.X, y), new Vector2(rect.End.X, y), new Color(1, 1, 1, 0.06f), 1);
            DrawString(font, new Vector2(0, y + 4), $"{i * 25} %", HorizontalAlignment.Left, 40, 11, Ui.Dim);
        }
        var tip = P(firstDay, Planets.D.TipShare).Y;
        DrawDashedLine(new Vector2(rect.Position.X, tip), new Vector2(rect.End.X, tip), new Color(Ui.Text, 0.5f), 1, 4);
        DrawString(font, new Vector2(rect.End.X - 80, tip - 4), $"{Planets.D.TipShare:P0} tips it", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        DrawString(font, new Vector2(rect.Position.X, rect.End.Y + 18), $"day {firstDay}", HorizontalAlignment.Left, -1, 11, Ui.Dim);
        DrawString(font, new Vector2(rect.End.X - 60, rect.End.Y + 18), $"day {lastDay}", HorizontalAlignment.Left, -1, 11, Ui.Dim);

        foreach (var side in sides)
        {
            var pts = w.History.Where(r => r.Side == side).OrderBy(r => r.Day).Select(r => P(r.Day, r.Control)).ToArray();
            if (pts.Length >= 2) DrawPolyline(pts, Ui.SideColor(side), side == Side.Player ? 2.5f : 2f, true);
        }
        var legend = new Vector2(rect.Position.X + 8, rect.Position.Y - 10);
        foreach (var side in sides)
        {
            DrawLine(legend, legend + new Vector2(18, 0), Ui.SideColor(side), 3);
            var name = Chronicle.Who(w, side);
            DrawString(font, legend + new Vector2(24, 5), name, HorizontalAlignment.Left, -1, 13, Ui.SideColor(side));
            legend.X += 40 + font.GetStringSize(name, HorizontalAlignment.Left, -1, 13).X;
        }

        // The turning points: a line down the day and a numbered mark on the leading line.
        for (var i = 0; i < Points.Count; i++)
        {
            var m = Points[i];
            var day = m.Hour / 24;
            var x = P(day, 0).X;
            var colour = Ui.SideColor(m.Side);
            DrawLine(new Vector2(x, rect.Position.Y), new Vector2(x, rect.End.Y), new Color(colour, 0.25f), 1);
            var share = m.Side is Side s ? w.History.Where(r => r.Side == s && r.Day <= day).OrderByDescending(r => r.Day).Select(r => r.Control).FirstOrDefault() : 0.5;
            var at = P(day, share);
            DrawCircle(at, 9, new Color(0.03f, 0.05f, 0.08f));
            DrawArc(at, 9, 0, Mathf.Tau, 24, colour, 2);
            var label = (i + 1).ToString();
            var size = font.GetStringSize(label, HorizontalAlignment.Left, -1, 11);
            DrawString(font, at + new Vector2(-size.X / 2, 4), label, HorizontalAlignment.Left, -1, 11, Ui.Text);
            _spots.Add((at, m));
        }
    }
}

/// <summary>
/// The fog-off replay map: the system at a past hour from the game's story, every fleet where it really was (filled,
/// sized by ships), and rings where a side's command believed the other side's fleets were. Wheel zooms, right-drag
/// pans, double-click resets.
/// </summary>
public partial class ReplayMap : Control
{
    public World? World;
    public double Hour;
    public bool ShowRivalBeliefs = true;
    public bool ShowYourBeliefs;

    private float _zoom = 1;
    private Vector2 _pan;
    private bool _dragging;
    private Vec2 _centre;
    private double _scale = 1;

    public ReplayMap()
    {
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(420, 320);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) QueueRedraw();
    }

    private Vector2 ToScreen(Vec2 p) => Size / 2 + _pan + new Vector2((float)((p.X - _centre.X) * _scale), (float)(-(p.Y - _centre.Y) * _scale));

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } up:
                ZoomAt(up.Position, 1.2f);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } down:
                ZoomAt(down.Position, 1 / 1.2f);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right } r:
                _dragging = r.Pressed;
                AcceptEvent();
                break;
            case InputEventMouseMotion m when _dragging:
                _pan += m.Relative;
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true }:
                _zoom = 1;
                _pan = Vector2.Zero;
                AcceptEvent();
                break;
        }
    }

    private void ZoomAt(Vector2 at, float factor)
    {
        var z = Math.Clamp(_zoom * factor, 0.25f, 60f);
        factor = z / _zoom;
        var fromCentre = at - Size / 2 - _pan;
        _pan -= fromCentre * (factor - 1);
        _zoom = z;
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.015f, 0.02f, 0.035f));
        if (World is not World w || w.Chronicle.Frames.Count == 0) return;
        var font = ThemeDB.FallbackFont;
        var frames = w.Chronicle.Frames;
        var i = Chronicle.FrameAt(w, Hour);
        var a = frames[i];
        var b = i + 1 < frames.Count ? frames[i + 1] : a;
        var t = b.Hour > a.Hour ? Math.Clamp((Hour - a.Hour) / (b.Hour - a.Hour), 0, 1) : 0;

        // Fit where the game was played, round the star: worlds ever held and most of the fleets' travels.
        var star = w.Bodies.FirstOrDefault(x => x.Kind == BodyKind.Star);
        var centre = star?.Position ?? Vec2.Zero;
        if (_fitFrames != frames.Count) { _fitFrames = frames.Count; _reach = Reach(w, centre); }
        _centre = centre;
        _scale = Math.Min(Size.X, Size.Y) / (_reach * 2.2) * _zoom;

        // Orbits and the star.
        var sc = ToScreen(centre);
        if (star != null)
        {
            foreach (var p in w.Bodies.Where(x => x.Kind == BodyKind.Planet && x.ParentId == star.Id))
                DrawArc(sc, (float)(p.OrbitRadius * _scale), 0, Mathf.Tau, 128, new Color(0.45f, 0.55f, 0.7f, 0.18f), 1);
            DrawCircle(sc, 6, new Color("ffd27a"));
        }

        // Bodies, coloured by who held them then.
        var owners = Chronicle.OwnersIn(a);
        foreach (var body in w.Bodies.Where(x => x.Kind is BodyKind.Planet or BodyKind.Moon))
        {
            var at = ToScreen(FlightPlanner.PredictBody(w, body, Hour));
            if (at.X < -40 || at.Y < -40 || at.X > Size.X + 40 || at.Y > Size.Y + 40) continue;
            var moon = body.Kind == BodyKind.Moon;
            var held = owners.TryGetValue(body.Id, out var s) ? (Side?)s : null;
            var col = held is Side hs ? Ui.SideColor(hs) : new Color(body.Color).Darkened(0.3f);
            DrawCircle(at, moon ? 2.5f : 5f, col);
            if (held != null) DrawArc(at, moon ? 5 : 8, 0, Mathf.Tau, 24, new Color(col, 0.7f), 1.5f);
            if (!moon || _zoom > 3 || held != null)
                DrawString(font, at + new Vector2(8, moon ? 12 : 4), body.Name, HorizontalAlignment.Left, -1, moon ? 10 : 12, new Color(col, moon ? 0.7f : 0.95f));
        }

        // Fleets where they really were, moved smoothly between frames.
        var now = Chronicle.FleetsIn(a);
        var next = Chronicle.FleetsIn(b).ToDictionary(f => f.Id);
        var truth = new Dictionary<int, Vector2>();
        foreach (var f in now)
        {
            var pos = next.TryGetValue(f.Id, out var n) ? Lerp(f.Position, n.Position, t) : f.Position;
            var fade = next.ContainsKey(f.Id) || ReferenceEquals(a, b) ? 1f : (float)(1 - t);
            var at = ToScreen(pos);
            truth[f.Id] = at;
            var col = new Color(Ui.SideColor(f.Side), fade);
            var r = 3 + 1.6f * Mathf.Sqrt(f.Ships);
            if (f.Warships > 0) DrawColoredPolygon(new[] { at + new Vector2(0, -r - 1), at + new Vector2(r, r * 0.8f), at + new Vector2(-r, r * 0.8f) }, col);
            else DrawCircle(at, r * 0.7f, new Color(col, 0.6f * fade));
            if (f.Ships > 1) DrawString(font, at + new Vector2(r + 2, -2), f.Ships.ToString(), HorizontalAlignment.Left, -1, 10, new Color(col, 0.85f * fade));
        }
        // Fleets that appear in the next frame (just built or split off) fade in.
        foreach (var n in next.Values.Where(n => now.All(f => f.Id != n.Id)))
        {
            var at = ToScreen(n.Position);
            truth[n.Id] = at;
            var r = 3 + 1.6f * Mathf.Sqrt(n.Ships);
            DrawCircle(at, r * 0.7f, new Color(Ui.SideColor(n.Side), (float)t * 0.8f));
        }

        // Beliefs: a ring in the believer's colour where its command thought the fleet was, joined to the truth.
        foreach (var belief in Chronicle.BeliefsIn(a))
        {
            var rival = belief.By != Side.Player;
            if (rival ? !ShowRivalBeliefs : !ShowYourBeliefs) continue;
            var at = ToScreen(belief.Position);
            var col = new Color(Ui.SideColor(belief.By), belief.Seen ? 0.9f : 0.6f);
            var r = 5 + 1.6f * Mathf.Sqrt(Math.Max(1, belief.Ships));
            if (truth.TryGetValue(belief.FleetId, out var real) && real.DistanceTo(at) > r)
                DrawDashedLine(at, real, new Color(col, 0.45f), 1, 4);
            if (belief.Seen) DrawArc(at, r, 0, Mathf.Tau, 24, col, 1.5f);
            else
                for (var k = 0; k < 8; k++) DrawArc(at, r, k * Mathf.Tau / 8, k * Mathf.Tau / 8 + Mathf.Tau / 16, 4, col, 1.5f);
            if (!truth.ContainsKey(belief.FleetId)) DrawString(font, at + new Vector2(r + 2, 10), "gone", HorizontalAlignment.Left, -1, 10, col);
            else if (!belief.Seen && belief.AgeHours >= 24) DrawString(font, at + new Vector2(r + 2, 10), $"{belief.AgeHours / 24:0}d old", HorizontalAlignment.Left, -1, 10, col);
        }

        // Legend.
        var y = Size.Y - 12;
        DrawString(font, new Vector2(10, y), "▲ warships  ● other ships  ○ believed position (dashed: out of sight)", HorizontalAlignment.Left, -1, 12, Ui.Dim);
    }

    private int _fitFrames = -1;
    private double _reach = Physics.MmPerAu;

    /// <summary>How far from the star the view reaches: the farthest world anyone held, or most fleet positions (outliers left out).</summary>
    private static double Reach(World w, Vec2 centre)
    {
        double Out(Body b)
        {
            while (b.ParentId is int p && w.FindBody(p) is Body parent && parent.Kind != BodyKind.Star) b = parent;
            return b.OrbitRadius;
        }
        var held = w.Chronicle.Frames.SelectMany(f => Chronicle.OwnersIn(f).Keys).Distinct()
            .Select(id => w.FindBody(id)).Where(b => b != null).Select(b => Out(b!)).DefaultIfEmpty(0).Max();
        var fleets = w.Chronicle.Frames.SelectMany(Chronicle.FleetsIn).Select(f => (f.Position - centre).Length).OrderBy(d => d).ToList();
        var most = fleets.Count > 0 ? fleets[(int)(fleets.Count * 0.95)] : 0;
        return Math.Max(0.3 * Physics.MmPerAu, Math.Max(held, most) * 1.1);
    }

    private static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}
