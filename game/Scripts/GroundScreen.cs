using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// A ground fight on a planet's board (from the War tab or the planet panel): the usual board with the
/// buildings and their names, and over it who holds each tile, the front, badges on the buildings that
/// matter to the fight, troop chips and the landing zone. On the right: the plan (Blitz, Methodical,
/// Starve), the numbers, the calls waiting, pull out, and what has happened. Esc or Back closes it.
/// </summary>
public partial class GroundScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _root = null!;
    private string _shape = "";
    private readonly List<Action<World>> _live = new();
    private BoardView? _board;
    private GroundOverlay? _overlay;

    public GroundScreen() : this(null!) { }
    public GroundScreen(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        var box = new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.045f, 0.97f) };
        box.SetContentMarginAll(20);
        AddThemeStyleboxOverride("panel", box);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _root = new VBoxContainer();
        _root.AddThemeConstantOverride("separation", 10);
        AddChild(_root);
    }

    public void Refresh(Simulation sim)
    {
        var w = sim.World;
        var view = GetViewportRect().Size;
        if (Position != Vector2.Zero || Size != view) { Position = Vector2.Zero; Size = view; }
        var body = _screen.GroundScreenId is int id ? w.FindBody(id) : null;
        if (body?.Board == null) { _screen.GroundScreenId = null; return; }
        var f = body.Front;
        var shape = $"{body.Id}|{f != null}|{f?.Plan}|{f?.PlanByHand}|{string.Join(",", f?.Calls.Select(c => $"{c.Id}{c.Picked}") ?? Array.Empty<string>())}|{f?.Notes.Count}|{body.Pieces.Count}";
        if (shape != _shape)
        {
            _shape = shape;
            foreach (var c in _root.GetChildren()) c.QueueFree();
            _live.Clear();
            Build(w, body);
        }
        foreach (var update in _live) update(w);
        if (_board != null) { _board.World = w; _board.Body = body; _board.QueueRedraw(); }
        if (_overlay != null) { _overlay.World = w; _overlay.Body = body; _overlay.QueueRedraw(); }
    }

    private void Build(World w, Body b)
    {
        var f = b.Front;
        var ours = f?.Side == Side.Player;
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 10);
        top.AddChild(Ui.Label(f == null ? $"{b.Name}: no fighting" : ours ? $"Invading {b.Name}" : $"{b.Name} is invaded", 22, ours ? Ui.Player : Ui.Danger));
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        top.AddChild(spacer);
        top.AddChild(Ui.SmallButton("Back to map", () => _screen.GroundScreenId = null, "Close the ground board (Esc)"));
        _root.AddChild(top);

        var main = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        main.AddThemeConstantOverride("separation", 20);
        _root.AddChild(main);

        var board = b.Board!;
        var view = GetViewportRect().Size;
        var cell = Mathf.Clamp(Mathf.Floor(Mathf.Min(view.X * 0.55f / board.W, view.Y * 0.7f / board.H)), 28, 96);
        var holder = new Control { CustomMinimumSize = new Vector2(cell * board.W, cell * board.H) };
        _board = new BoardView { Body = b, World = w, OnSelect = _ => { }, OnCancel = () => { } };
        _board.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _board.MouseFilter = MouseFilterEnum.Ignore;
        holder.AddChild(_board);
        _overlay = new GroundOverlay { Body = b, World = w, MouseFilter = MouseFilterEnum.Ignore };
        _overlay.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        holder.AddChild(_overlay);
        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 6);
        left.AddChild(holder);
        left.AddChild(Wrap("Blue tiles are ours, red theirs; the bright line is the front. The ring marks the landing zone. Chips give the troops on each side. "
                           + "Badges: B barracks, ▣ bunker, G guns, D shield dome, H habitat (militia), F farm (feeds the defenders), P power (runs defences near it), L lab. "
                           + "A building with an amber cross was damaged when the front passed.", 12, Ui.Dim));
        main.AddChild(left);

        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 8);
        main.AddChild(right);
        if (f == null)
        {
            right.AddChild(Wrap("Nobody is fighting here. Land troops from a fleet in low orbit over an enemy planet (fleet panel), or blockade it and let the garrison weaken each week.", 14, Ui.Dim));
            return;
        }
        var id = b.Id;
        right.AddChild(Live(w2 => w2.FindBody(id) is Body x && x.Front is GroundFront g
            ? $"{x.LandingTroops * 1000:0} troops against {x.Garrison * 1000:0} defenders · {WarTab.Taken(x, g) * 100:0} % of the ground held by the attackers"
            : "", 15, Ui.Text));
        right.AddChild(Live(w2 => w2.FindBody(id) is Body x && x.Front is GroundFront g
            ? $"Day {(w2.Hour - g.StartHour) / 24:0.0} · " + (Ground.DaysLeft(w2, x) is double d ? $"about {d:0.0} days to go · " : "")
              + $"attackers' chance {Ground.WinChance(x) * 100:0} % · guns still firing {Ground.GunsOn(x, g):0.#}"
            : "", 13, Ui.Dim));
        right.AddChild(Ui.Label($"Plan: {Ground.PlanName(f.Plan)}{(f.PlanByHand ? " (yours)" : " (Auto)")}", 15, Ui.Player));
        right.AddChild(Wrap(Ground.PlanWhat(f.Plan), 13, Ui.Dim));
        if (ours)
        {
            var plans = new HFlowContainer();
            plans.AddThemeConstantOverride("h_separation", 6);
            foreach (var plan in Enum.GetValues<GroundPlan>())
            {
                var p = plan;
                var button = Ui.SmallButton(Ground.PlanName(plan), () => _screen.IssueNow(new SetGroundPlanCommand(Side.Player, id, p), "ground plan"), Ground.PlanWhat(plan));
                if (plan == f.Plan) button.Modulate = Ui.Player;
                plans.AddChild(button);
            }
            if (f.PlanByHand) plans.AddChild(Ui.SmallButton("Back to Auto", () => _screen.IssueNow(new SetGroundPlanCommand(Side.Player, id, null), "ground plan"), "Auto picks the plan again"));
            plans.AddChild(Ui.SmallButton("Pull out", () => _screen.IssueNow(new PullOutTroopsCommand(Side.Player, id), "pulling troops out"), "Lift the troops off again"));
            right.AddChild(plans);
        }
        foreach (var c in f.Calls.Where(c => c.Open))
        {
            right.AddChild(Wrap("⚑ " + c.Text, 14, Ui.Rival));
            if (!ours) continue;
            var options = new HFlowContainer();
            options.AddThemeConstantOverride("h_separation", 6);
            for (var i = 0; i < c.Options.Count; i++)
            {
                var option = i;
                var callId = c.Id;
                var button = Ui.SmallButton(c.Options[i], () => _screen.IssueNow(new GroundCallCommand(Side.Player, id, callId, option), "ground call"),
                    (i < c.Effects.Count ? c.Effects[i] : "") + (i == c.AutoPick ? " (Auto's pick)" : ""));
                options.AddChild(button);
            }
            right.AddChild(options);
        }
        if (f.Notes.Count > 0)
        {
            right.AddChild(Ui.Label("What has happened", 14, Ui.Player));
            foreach (var note in f.Notes.TakeLast(10).Reverse()) right.AddChild(Wrap("· " + note, 12, Ui.Dim));
        }
    }

    private Label Live(Func<World, string> text, int size, Color color)
    {
        var l = Wrap("", size, color);
        _live.Add(w => l.Text = text(w));
        return l;
    }

    private static Label Wrap(string text, int size, Color color)
    {
        var l = Ui.Label(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(320, 0);
        return l;
    }
}

/// <summary>Drawn over the board: who holds each tile, the front, building badges, troop chips and the landing zone.</summary>
public partial class GroundOverlay : Control
{
    public Body? Body { get; set; }
    public World? World { get; set; }

    private float Cell => Body?.Board is Board b ? Mathf.Floor(Mathf.Min(Size.X / b.W, Size.Y / b.H)) : 32;

    private static string? Badge(Piece p, BuildingData? d) => p.Kind switch
    {
        "barracks" => "B",
        "bunker" => "▣",
        _ when d?.GunDamage > 0 => "G",
        _ when p.Kind.Contains("dome") || p.Kind.Contains("shield") => "D",
        _ when d?.HousingK > 0 => "H",
        _ when d?.Food > 0 => "F",
        _ when d?.Power < 0 => "P",
        _ when d?.Area == "research" => "L",
        _ => null,
    };

    public override void _Draw()
    {
        if (Body?.Board is not Board board || Body.Front is not GroundFront f) return;
        var c = Cell;
        var attacker = f.Side == Side.Player ? Ui.Player : Ui.Danger;
        var defender = f.Side == Side.Player ? Ui.Danger : Ui.Player;
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
        {
            var t = f.At(board, x, y);
            if (t == 'm') continue;
            DrawRect(new Rect2(x * c, y * c, c, c), new Color(t == 'A' ? attacker : defender, 0.16f));
        }
        // The front: edges between an attacker tile and a defender tile.
        var front = new Color(1f, 0.95f, 0.6f, 0.95f);
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
        {
            if (f.At(board, x, y) != 'A') continue;
            if (f.At(board, x + 1, y) == 'D') DrawLine(new Vector2((x + 1) * c, y * c), new Vector2((x + 1) * c, (y + 1) * c), front, 3);
            if (f.At(board, x - 1, y) == 'D') DrawLine(new Vector2(x * c, y * c), new Vector2(x * c, (y + 1) * c), front, 3);
            if (f.At(board, x, y + 1) == 'D') DrawLine(new Vector2(x * c, (y + 1) * c), new Vector2((x + 1) * c, (y + 1) * c), front, 3);
            if (f.At(board, x, y - 1) == 'D') DrawLine(new Vector2(x * c, y * c), new Vector2((x + 1) * c, y * c), front, 3);
        }
        var font = ThemeDB.FallbackFont;
        foreach (var p in Body.Pieces)
        {
            var d = Planets.BuildingKind(p.Kind);
            var rect = new Rect2(p.X * c, p.Y * c, p.W * c, p.H * c);
            var held = Ground.Held(board, f, p);
            if (held == 'c') DrawRect(rect.Grow(-2), front, false, 2);
            if (f.Damaged.Contains(p.Id))
            {
                DrawLine(rect.Position + new Vector2(4, 4), rect.End - new Vector2(4, 4), Ui.Rival, 3);
                DrawLine(new Vector2(rect.End.X - 4, rect.Position.Y + 4), new Vector2(rect.Position.X + 4, rect.End.Y - 4), Ui.Rival, 3);
            }
            if (Badge(p, d) is not string badge) continue;
            var at = new Vector2(rect.End.X - 18, rect.Position.Y + 2);
            DrawCircle(at + new Vector2(8, 8), 9, new Color(held == 'A' ? attacker : held == 'D' ? defender : front, 0.95f));
            DrawString(font, at + new Vector2(2, 13), badge, HorizontalAlignment.Center, 12, 11, Colors.Black);
        }
        // Landing zone.
        var land = new Vector2((f.LandX + 0.5f) * c, (f.LandY + 0.5f) * c);
        DrawArc(land, c * 1.1f, 0, Mathf.Tau, 32, new Color(attacker, 0.9f), 2);
        // Troop chips: attackers at the landing zone, defenders at the middle of what they hold.
        Chip(font, land + new Vector2(0, c * 0.9f), $"{Body.LandingTroops * 1000:0}", attacker);
        var held2 = new List<Vector2>();
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
            if (f.At(board, x, y) == 'D') held2.Add(new Vector2((x + 0.5f) * c, (y + 0.5f) * c));
        if (held2.Count > 0)
            Chip(font, held2.Aggregate(Vector2.Zero, (a, v) => a + v) / held2.Count, $"{Body.Garrison * 1000:0}", defender);
    }

    private void Chip(Font font, Vector2 centre, string text, Color colour)
    {
        var rect = new Rect2(centre - new Vector2(26, 11), new Vector2(52, 22));
        DrawRect(rect, new Color(0.02f, 0.03f, 0.05f, 0.85f));
        DrawRect(rect, colour, false, 2);
        DrawString(font, rect.Position + new Vector2(0, 16), "⚔ " + text, HorizontalAlignment.Center, 52, 12, colour);
    }
}
