using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Needs you on the map: a pin on each planet or fleet that needs you (red urgent, amber soon, blue advice),
/// lit with its tray row (hovering a row rings its pin; clicking a pin opens its row). Pins that would overlap
/// fold into one with a count. With "What automation did" on, dashed grey pins mark where automation acted
/// today. Map layers and Display options switch each kind on or off.
/// </summary>
public partial class NeedPins : Control
{
    private readonly GameScreen _screen;
    private readonly Hud _hud;
    private readonly List<Pin> _pins = new();
    private Vector2 _mouse = new(-1000, -1000);

    private sealed class Pin
    {
        public Vector2 At;
        public Rect2 Box;
        public string Text = "";
        public Color Color;
        public bool Auto;
        public readonly List<NeedItem> Items = new();
        public AutoNote? Note;
        public string Place = "";
    }

    private const float Fold = 46;

    public NeedPins() : this(null!, null!) { }

    public NeedPins(GameScreen screen, Hud hud)
    {
        _screen = screen;
        _hud = hud;
        MouseFilter = MouseFilterEnum.Pass;
        TooltipText = " ";
    }

    public override void _Ready() => SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

    /// <summary>Only the pins take the mouse; the rest of the map stays clickable.</summary>
    public override bool _HasPoint(Vector2 point) => _pins.Any(p => p.Box.Grow(2).HasPoint(point));

    public void Refresh(World w)
    {
        _pins.Clear();
        var font = ThemeDB.FallbackFont;
        var state = Economy.Of(w, Side.Player);
        var raw = new List<Pin>();
        foreach (var n in state.Needs)
        {
            var advice = n.Level == NeedLevel.Advice;
            if (advice ? !PlayerInfo.Has(MapLayer.AdvicePins) || PlayerInfo.Advice == AdviceMode.Off : !PlayerInfo.Has(MapLayer.NeedPins)) continue;
            if (!advice && PlayerInfo.PinsUrgentOnly && n.Level > NeedLevel.Soon) continue;
            if (Where(w, n.FleetId, n.BodyId) is not var (at, place)) continue;
            var pin = new Pin { At = at, Color = NeedsUi.LevelColor(n.Level), Place = place, Text = $"{place} · {NeedKinds.Bites(w, Side.Player, n)}" };
            pin.Items.Add(n);
            raw.Add(pin);
        }
        if (PlayerInfo.Has(MapLayer.AutoPins))
            foreach (var note in NeedsUi.Today(w).Take(12))
                if (Where(w, note.FleetId, note.BodyId) is var (at, place))
                    raw.Add(new Pin { At = at, Color = new Color(0.75f, 0.78f, 0.85f), Auto = true, Note = note, Place = place, Text = $"{place} · {Short(note.Text)}" });

        // Fold pins that would overlap: most urgent first, the rest join the nearest pin within reach.
        foreach (var p in raw.OrderBy(p => p.Auto).ThenBy(p => p.Items.Count > 0 ? (int)p.Items[0].Level : 9))
        {
            var near = _pins.FirstOrDefault(q => q.Auto == p.Auto && q.At.DistanceTo(p.At) < Fold);
            if (near == null) { _pins.Add(p); continue; }
            near.Items.AddRange(p.Items);
            if (near.Note != null && p.Note != null) near.Text = "";
        }
        foreach (var p in _pins)
        {
            var count = p.Auto ? raw.Count(r => r.Auto && _pins.Find(q => q.Auto && q.At.DistanceTo(r.At) < Fold) == p) : p.Items.Count;
            if (count > 1)
            {
                var places = p.Auto ? new List<string> { p.Place } : p.Items.Select(i => NeedsUi.PlaceOf(w, i) ?? "").Distinct().ToList();
                p.Text = places.Count == 1 ? $"{places[0]} {count}" : $"{p.Place} group {count}";
            }
            var size = font.GetStringSize(p.Text, HorizontalAlignment.Left, -1, 12) + new Vector2(14, 6);
            p.Box = new Rect2(p.At + new Vector2(-size.X / 2, -22 - size.Y), size);
        }
        QueueRedraw();
    }

    private static string Short(string text) => text.Length > 28 ? text[..26] + "…" : text;

    /// <summary>A planet's or your fleet's place on screen, and its name; null when off screen or unknown.</summary>
    private (Vector2 At, string Place)? Where(World w, int? fleetId, int? bodyId)
    {
        string name;
        int id;
        if (fleetId is int f && w.FindFleet(f) is { Side: Side.Player } fleet) { id = f; name = fleet.Name; }
        else if (bodyId is int b && w.FindBody(b) is Body body) { id = b; name = body.Name; }
        else return null;
        if (_screen.ScreenPosOf(id) is not Vector2 at) return null;
        var view = GetViewportRect().Size;
        if (at.X < -40 || at.Y < Hud.ContentTop || at.X > view.X + 40 || at.Y > view.Y + 40) return null;
        return (at, name);
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        foreach (var p in _pins.OrderBy(p => !p.Auto))
        {
            var lit = !p.Auto && p.Items.Any(i => i.Key == _hud.HoverNeed || i.Key == _hud.SelectedNeed);
            var hover = p.Box.HasPoint(_mouse);
            var color = p.Color;
            DrawLine(p.At, new Vector2(p.At.X, p.Box.End.Y), new Color(color, 0.8f), 1.5f);
            DrawCircle(p.At, 3, color);
            if (lit)
            {
                DrawArc(p.At, 14, 0, Mathf.Tau, 32, color, 2.5f, true);
                DrawArc(p.At, 20, 0, Mathf.Tau, 32, new Color(color, 0.4f), 1.5f, true);
            }
            DrawRect(p.Box, new Color(0.03f, 0.05f, 0.08f, p.Auto ? 0.7f : 0.88f));
            if (p.Auto)
            {
                var r = p.Box;
                var c = new Color(color, 0.8f);
                DrawDashedLine(r.Position, new Vector2(r.End.X, r.Position.Y), c, 1, 4);
                DrawDashedLine(new Vector2(r.End.X, r.Position.Y), r.End, c, 1, 4);
                DrawDashedLine(r.End, new Vector2(r.Position.X, r.End.Y), c, 1, 4);
                DrawDashedLine(new Vector2(r.Position.X, r.End.Y), r.Position, c, 1, 4);
            }
            else DrawRect(p.Box, lit || hover ? color : new Color(color, 0.7f), false, lit ? 2.5f : 1.2f);
            DrawString(font, p.Box.Position + new Vector2(7, p.Box.Size.Y - 6), p.Text, HorizontalAlignment.Left, -1, 12, p.Auto ? new Color(color, 0.9f) : Ui.Text);
        }
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        var p = _pins.LastOrDefault(q => q.Box.HasPoint(atPosition));
        if (p == null) return "";
        if (p.Auto) return p.Note != null ? $"Automation, {NeedsUi.Clock(p.Note.Hour)}: {p.Note.Text}" : "What automation did here today";
        return string.Join("\n", p.Items.Take(6).Select(i => $"{i.Level}: {i.Text}")) + (p.Items.Count > 1 ? "\nClick: zoom in and open the first in the tray" : "\nClick: open it in the tray");
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion m:
                if (_pins.Any(p => p.Box.HasPoint(m.Position) != p.Box.HasPoint(_mouse))) QueueRedraw();
                _mouse = m.Position;
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } b:
                var pin = _pins.LastOrDefault(q => q.Box.HasPoint(b.Position));
                if (pin == null) return;
                AcceptEvent();
                Main.Instance.RunGuarded("clicking a pin", () =>
                {
                    if (pin.Auto)
                    {
                        if (pin.Note?.FleetId is int f && Main.Instance.Sim?.World.FindFleet(f) != null) _screen.PickFleet(f);
                        else if (pin.Note?.BodyId is int body) _screen.PickBody(body);
                        return;
                    }
                    var first = pin.Items.OrderBy(i => i.Level).First();
                    if (pin.Items.Count > 1 && pin.Items.Select(i => i.FleetId ?? i.BodyId).Distinct().Count() > 1)
                    {
                        // A fold of several places: zoom in on it so the pins come apart.
                        _screen.Camera.Center = _screen.Camera.ToWorld(pin.At);
                        _screen.Camera.SetScale(_screen.Camera.TargetScale * 3);
                        if (PlayerInfo.Tray != TrayMode.Off) _hud.TrayOpen = true;
                        _hud.SelectedNeed = first.Key;
                        return;
                    }
                    _hud.PickNeed(first);
                });
                break;
        }
    }
}

/// <summary>
/// "Work through them": one card per item, most urgent first: what, where, when, what happens if you do nothing,
/// and your choices (the obvious order, the alternatives, Show me, Not now, Skip). Pauses while you work (a
/// Display option). Keys: Enter does it, G shows it, S not now, → skips, ← back, Esc closes.
/// </summary>
public partial class NeedCards : PanelContainer
{
    private readonly GameScreen _screen;
    private readonly Hud _hud;
    private VBoxContainer _root = null!;
    private readonly List<string> _keys = new();
    private int _index;
    private bool _pausedByUs;
    private string _shape = "";
    private NeedItem? _current;
    private Label _bites = null!;

    public NeedCards() : this(null!, null!) { }
    public NeedCards(GameScreen screen, Hud hud) { _screen = screen; _hud = hud; }

    public override void _Ready()
    {
        Ui.StylePanel(this, 18);
        _root = new VBoxContainer { CustomMinimumSize = new Vector2(620, 0) };
        _root.AddThemeConstantOverride("separation", 8);
        AddChild(_root);
    }

    public void Open()
    {
        var w = Main.Instance.Sim?.World;
        if (w == null) return;
        _keys.Clear();
        var needs = Economy.Of(w, Side.Player).Needs.Where(n => PlayerInfo.Advice != AdviceMode.Off || n.Level != NeedLevel.Advice)
            .OrderBy(n => n.Level).ThenBy(n => n.Hour).ThenBy(n => n.Key, StringComparer.Ordinal);
        _keys.AddRange(needs.Select(n => n.Key));
        _index = 0;
        _shape = "";
        Visible = true;
        _pausedByUs = false;
        if (PlayerInfo.PauseWhileWorking && !_screen.Paused) { _screen.TogglePause(); _pausedByUs = true; }
    }

    public void Close()
    {
        Visible = false;
        if (_pausedByUs && _screen.Paused) _screen.TogglePause();
        _pausedByUs = false;
    }

    private NeedItem? Current(World w)
    {
        var needs = Economy.Of(w, Side.Player).Needs;
        while (_index < _keys.Count && needs.Find(n => n.Key == _keys[_index]) == null) _index++;
        return _index < _keys.Count ? needs.Find(n => n.Key == _keys[_index]) : null;
    }

    private void Next() { _index++; _shape = ""; }

    public void Refresh(World w)
    {
        _current = Current(w);
        var shape = _current == null ? "done" : $"{_index}|{_current.Key}|{_current.Text}|{_current.Act != null}|{PlayerInfo.PauseWhileWorking}";
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            foreach (var c in _root.GetChildren()) c.QueueFree();
            Build(w);
        }
        if (_current != null && IsInstanceValid(_bites)) _bites.Text = NeedsUi.Meta(w, _current);
        var view = GetViewportRect().Size;
        Size = Vector2.Zero;
        var size = GetCombinedMinimumSize();
        Position = new Vector2((view.X - size.X) / 2, Hud.ContentTop + 40);
    }

    private void Build(World w)
    {
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 10);
        var left = _keys.Count - _index;
        head.AddChild(Ui.Label(_current == null ? "Work through them" : $"{_index + 1} of {_keys.Count}", 14, Ui.Dim));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var pause = new CheckButton { Text = "Pause while I work", ButtonPressed = PlayerInfo.PauseWhileWorking, FocusMode = FocusModeEnum.None };
        pause.AddThemeFontSizeOverride("font_size", 13);
        pause.Toggled += on =>
        {
            PlayerInfo.SetPauseWhileWorking(on);
            if (on && !_screen.Paused) { _screen.TogglePause(); _pausedByUs = true; }
        };
        head.AddChild(pause);
        head.AddChild(NeedsUi.Small("✕", Close, "Close (Esc)"));
        _root.AddChild(head);
        if (_current is not { } n)
        {
            _root.AddChild(Ui.Label(_keys.Count == 0 ? "Nothing needs you just now." : "That's all of them.", 20, Ui.Player));
            _root.AddChild(NeedsUi.Small("Close", Close, "Close (Esc)"));
            return;
        }
        var kind = NeedKinds.KindOf(n.Key);
        var level = n.Level switch { NeedLevel.Urgent => "Urgent", NeedLevel.Soon => "Soon", NeedLevel.Advice => "Advice", _ => "News" };
        _root.AddChild(Ui.Label(level + (NeedsUi.AreaShort(n.Area) is { Length: > 0 } a ? $" · {a}" : ""), 13, NeedsUi.LevelColor(n.Level)));
        var title = Ui.Label(n.Text, 20);
        title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        title.CustomMinimumSize = new Vector2(620, 0);
        _root.AddChild(title);
        _bites = Ui.Label(NeedsUi.Meta(w, n), 14, Ui.Dim);
        _root.AddChild(_bites);
        var why = Ui.Label("If you do nothing: " + NeedKinds.Why(kind), 14, Ui.Text);
        why.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        why.CustomMinimumSize = new Vector2(620, 0);
        _root.AddChild(why);
        var choices = NeedKinds.Choices(w, Side.Player, n);
        foreach (var c in choices.Where(c => c.Tip != null).Take(4))
        {
            var tip = Ui.Label($"· {c.Label}: {c.Tip}", 12, Ui.Dim);
            tip.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            tip.CustomMinimumSize = new Vector2(620, 0);
            _root.AddChild(tip);
        }
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", 6);
        row.AddThemeConstantOverride("v_separation", 6);
        _root.AddChild(row);
        if (n.Act != null) row.AddChild(NeedsUi.Small((n.ActLabel ?? "Do it") + "  Enter", () => { NeedsUi.Act(_screen, n); Next(); }, "The obvious order", Ui.Player));
        foreach (var c in choices.Take(3))
        {
            var cmd = c.Command;
            row.AddChild(NeedsUi.Small(c.Label, () => { NeedsUi.Choose(_screen, n, cmd); Next(); }, c.Tip ?? "The alternative"));
        }
        if (choices.Count == 0 && NeedsUi.ScreenChoice(_screen, w, n) is var (label, open))
            row.AddChild(NeedsUi.Small(label, () => { Close(); open(); }, "Open the screen where this is settled (closes the cards)"));
        row.AddChild(NeedsUi.Small("Show me  G", () => _hud.GoTo(n), "Fly the map there (the card stays)"));
        row.AddChild(NeedsUi.Small("Not now  S", () => { _screen.IssueNow(new SnoozeNeedCommand(Side.Player, n.Key, NeedSnooze.Day), "snoozing a need"); Next(); }, "Snooze it for a day"));
        row.AddChild(NeedsUi.Small("Skip  →", Next, $"Leave it in the list and go on ({left - 1} more after this)"));
    }

    /// <summary>Keys while the cards are up; true when the key was used.</summary>
    public bool OnKey(InputEventKey key)
    {
        var w = Main.Instance.Sim?.World;
        if (w == null) return false;
        var n = Current(w);
        switch (key.Keycode)
        {
            case Key.Escape: Close(); return true;
            case Key.Right or Key.Tab: Next(); return true;
            case Key.Left: _index = Math.Max(0, _index - 1); _shape = ""; return true;
            case Key.Enter or Key.KpEnter when n?.Act != null: NeedsUi.Act(_screen, n); Next(); return true;
            case Key.G when n != null: _hud.GoTo(n); return true;
            case Key.S when n != null: _screen.IssueNow(new SnoozeNeedCommand(Side.Player, n.Key, NeedSnooze.Day), "snoozing a need"); Next(); return true;
            case Key.Space: return false;
        }
        return false;
    }
}
