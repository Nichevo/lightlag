using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The Empire screen's Supply tab: the chain view (planets and fleets, goods between them; line width is
/// the amount a day, red is short, dashed is at risk), the routes table with freighter use and the year's
/// losses, the problems Auto ranks with one-click fixes, and each planet's stock targets with siege mode.
/// Everything shown comes from SupplyChain; every button issues a command that takes effect at once,
/// paused or not. The panel keeps its filter and the stock cell being edited between rebuilds.
/// </summary>
public sealed class SupplyTab
{
    public const float Width = 1180, LeftWidth = 690, RightWidth = 470;

    private readonly GameScreen _screen;
    private string _filter = "all";
    private (int Body, string Good)? _cell;
    private string _editGood = "fuel";

    // Worked out once per tick (or after an order), not every frame.
    private (long Tick, int Commands) _key = (-1, -1);
    private List<ChainProblem> _problems = new();
    private List<RouteRow> _routes = new();
    private List<ChainFlow> _flows = new();
    private List<ChainNode> _nodes = new();
    private HaulerUse _haulers = new(0, 0, new List<string>(), 0, 0);
    private readonly List<(Button Button, Func<World, string> Text)> _liveButtons = new();
    private SupplyChainView? _view;

    /// <summary>Goods shown as columns of the stock targets grid (any good can be edited below it).</summary>
    private static readonly string[] GridGoods = { "fuel", "spares", "alloys", "electronics", "ammunition" };

    public SupplyTab(GameScreen screen) => _screen = screen;

    private Simulation? _sim;

    private void Update(Simulation sim)
    {
        _sim = sim;
        var key = (sim.World.Tick, sim.Replay.Commands.Count);
        if (key == _key) return;
        _key = key;
        var w = sim.World;
        _problems = SupplyChain.Problems(w, Side.Player);
        _routes = SupplyChain.Routes(w, Side.Player);
        _flows = SupplyChain.Flows(w, Side.Player);
        _nodes = SupplyChain.Nodes(w, Side.Player);
        _haulers = SupplyChain.Haulers(w, Side.Player);
    }

    /// <summary>Everything that changes which buttons and rows exist.</summary>
    public string Shape(Simulation sim)
    {
        Update(sim);
        var w = sim.World;
        return _filter + "|" + _cell + "|" + _editGood
               + "|" + string.Join(",", _problems.Select(p => p.Key + ":" + string.Join("/", p.Fixes.Select(f => f.Label))))
               + "|" + string.Join(",", _routes.Select(r => r.Key + r.Fixed + r.EscortFleetId + r.ViaBodyId))
               + "|" + string.Join(",", w.Bodies.Where(b => b.Owner == Side.Player && b.Board != null).Select(b => $"{b.Id}{(b.SiegeMode ? "s" : "")}"));
    }

    /// <summary>Every frame: numbers on buttons, and the chain view.</summary>
    public void Refresh(Simulation sim)
    {
        Update(sim);
        foreach (var (button, text) in _liveButtons)
            if (GodotObject.IsInstanceValid(button)) button.Text = text(sim.World);
        _view?.Show(sim.World, _nodes, _flows.Where(f => SupplyChain.Shows(_filter, f)).ToList());
    }

    public void Build(Simulation sim, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live)
    {
        Update(sim);
        _liveButtons.Clear();
        var w = sim.World;
        var columns = new HBoxContainer();
        columns.AddThemeConstantOverride("separation", 16);
        box.AddChild(columns);
        var left = new VBoxContainer { CustomMinimumSize = new Vector2(LeftWidth, 0) };
        left.AddThemeConstantOverride("separation", 6);
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(RightWidth, 0) };
        right.AddThemeConstantOverride("separation", 6);
        columns.AddChild(left);
        columns.AddChild(right);
        Chain(left);
        Routes(w, left, live);
        Problems(w, right, live);
        Stock(w, right, live);
    }

    // ---- Chain view -----------------------------------------------------------------------------

    private void Chain(VBoxContainer box)
    {
        Heading(box, "Supply chain", "Where every good comes from and where it goes");
        var row = Row(box);
        foreach (var f in SupplyChain.Filters.Concat(_filter.StartsWith("good:", StringComparison.Ordinal) ? new[] { _filter } : Array.Empty<string>()))
        {
            var filter = f;
            var b = Small(SupplyChain.FilterName(f), () => _filter = filter, "Show only these lines in the chain view");
            if (f == _filter) b.Modulate = Ui.Player;
            row.AddChild(b);
        }
        _view = new SupplyChainView(_screen) { CustomMinimumSize = new Vector2(LeftWidth, 360) };
        box.AddChild(_view);
        box.AddChild(Text("Line width = amount a day (last 30 days) · red = short · dashed = at risk on the route · click a planet or fleet to go there", 11));
    }

    // ---- Routes ---------------------------------------------------------------------------------

    private void Routes(World w, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live)
    {
        Heading(box, "Routes", "Every run that moves goods, with its risk");
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 4);
        box.AddChild(grid);
        foreach (var (head, width) in new[] { ("ROUTE", 160), ("CARRIES", 150), ("SHIPS", 120), ("HOW OFTEN", 80), ("RISK", 60), ("SET BY", 80) })
        {
            var l = Ui.Label(head, 11, Ui.Dim);
            l.CustomMinimumSize = new Vector2(width, 0);
            grid.AddChild(l);
        }
        if (_routes.Count == 0) box.AddChild(Text("No goods are moving yet. Planets ask for goods when they fall under their import line; freighters bring them.", 12));
        foreach (var r in _routes)
        {
            var name = r.Name + (r.ViaBodyId is int via && w.FindBody(via) is Body v ? $" (by {v.Name})" : "");
            grid.AddChild(Cell(name, Ui.Text, r.RiskWhy));
            grid.AddChild(Cell(string.Join(", ", r.Goods.Select(GoodName)), Ui.Dim));
            grid.AddChild(Cell(r.Ships + (r.EscortFleetId is int e && w.FindFleet(e) is Fleet escort ? $" + {escort.Name}" : ""), Ui.Dim));
            grid.AddChild(Cell(r.HowOften, Ui.Dim));
            grid.AddChild(Cell(r.Risk.ToString(), RiskColor(r.Risk), r.RiskWhy));
            grid.AddChild(SetBy(w, r));
        }
        var line = Ui.Label("", 12, Ui.Dim);
        line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        line.CustomMinimumSize = new Vector2(LeftWidth, 0);
        box.AddChild(line);
        live.Add((line, _ =>
        {
            var h = _haulers;
            var idle = h.Idle.Count == 0 ? "none idle" : $"{h.Idle.Count} idle ({string.Join(", ", h.Idle.Take(3))})";
            return $"Freighters and tankers: {h.Busy} of {h.Ships} busy · {idle}. Losses in the last year: "
                   + (h.LostShips == 0 ? "none." : $"{h.LostShips} freighter{(h.LostShips == 1 ? "" : "s")}, {h.LostCargo:0} cargo.");
        }));
        var settings = SupplyChain.Book(w, Side.Player).Routes;
        var clear = _routes.Where(r => r.ToBodyId is int t && settings.Any(x => x.FromBodyId == r.FromBodyId && x.ToBodyId == t))
            .GroupBy(r => (r.FromBodyId, r.ToBodyId)).Select(g => g.First()).ToList();
        if (clear.Count > 0)
        {
            var row = Row(box);
            foreach (var r in clear)
            {
                var (from, to) = (r.FromBodyId, r.ToBodyId!.Value);
                row.AddChild(Small($"Plain route {r.Name}", () => Issue(new SetRouteCommand(Side.Player, from, to, null, null), "clearing a route"),
                    "Drop this run's escort and detour: loads go straight again"));
            }
        }
    }

    /// <summary>Auto or Fixed: an Auto run can be fixed (a run every so many days); a fixed run can go back to Auto.</summary>
    private Control SetBy(World w, RouteRow r)
    {
        if (r.ToFleetId != null) return Cell(r.Fixed ? "By hand" : "Auto", Ui.Dim, "Supply runs to fleets are set from the fleet panel");
        if (r.Fixed && r.FixedRunId is int runId)
            return Small("Fixed run", () => Issue(new FixedRunCommand(Side.Player, 0, 0, "", 1, RemoveRunId: runId), "dropping a fixed run"),
                "Drop the fixed run: Auto carries goods here again when the planet asks");
        var good = r.Goods.Count == 1 ? r.Goods[0] : "";
        var (from, to, every) = (r.FromBodyId, r.ToBodyId!.Value, r.EveryDays);
        return Small("Auto", () => Issue(new FixedRunCommand(Side.Player, from, to, good, Math.Max(2, every)), "fixing a run"),
            $"Make it a fixed run: {(good == "" ? "all spare goods" : GoodName(good))} every {Math.Max(2, every):0} days, whether asked or not");
    }

    // ---- Problems -------------------------------------------------------------------------------

    private void Problems(World w, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live)
    {
        Heading(box, "Problems and fixes", "Auto ranks them; each fix shows its cost");
        if (_problems.Count == 0) box.AddChild(Text("Nothing wrong in the supply chain just now.", 12));
        foreach (var p in _problems)
        {
            var key = p.Key;
            var head = Row(box);
            var tag = Ui.Label(p.Kind.ToString(), 12, KindColor(p.Kind));
            tag.CustomMinimumSize = new Vector2(46, 0);
            head.AddChild(tag);
            var title = Ui.Label(p.Title, 13, Ui.Text);
            title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            title.CustomMinimumSize = new Vector2(RightWidth - 56, 0);
            head.AddChild(title);
            live.Add((title, _ => _problems.Find(x => x.Key == key)?.Title ?? title.Text));
            var cause = Ui.Label(p.Cause, 12, Ui.Dim);
            cause.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            cause.CustomMinimumSize = new Vector2(RightWidth - 56, 0);
            var causeRow = Row(box);
            causeRow.AddChild(new Control { CustomMinimumSize = new Vector2(46, 0) });
            causeRow.AddChild(cause);
            live.Add((cause, _ => _problems.Find(x => x.Key == key)?.Cause ?? cause.Text));
            var fixes = Row(box);
            fixes.AddChild(new Control { CustomMinimumSize = new Vector2(46, 0) });
            foreach (var fix in p.Fixes)
            {
                var f = fix;
                var label = f.Cost == "" ? f.Label : $"{f.Label} · {f.Cost}";
                fixes.AddChild(Small(label, () => Do(f), f.Command != null ? $"{f.Label} ({(f.Cost == "" ? "no cost" : f.Cost)})" : f.Label));
            }
        }
    }

    private void Do(ChainFix fix)
    {
        if (fix.Command != null) { Issue(fix.Command, $"fix '{fix.Label}'"); return; }
        var parts = (fix.Ui ?? "").Split(':');
        switch (parts[0])
        {
            case "chain": _filter = "good:" + parts[1]; break;
            case "fleet" when int.TryParse(parts[1], out var id): _screen.Select(id); _screen.Follow(id, zoom: true); break;
            case "planet" when int.TryParse(parts[1], out var id): _screen.PickBody(id); break;
            case "stock" when int.TryParse(parts[1], out var id): _cell = (id, parts[2]); _editGood = parts[2]; break;
        }
    }

    // ---- Stock targets --------------------------------------------------------------------------

    private void Stock(World w, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live)
    {
        Heading(box, "Stock targets", "Per planet: keep, export above, import below");
        var planets = w.Bodies.Where(b => b.Owner == Side.Player && b.Board != null).OrderBy(b => b.Id).ToList();
        var grid = new GridContainer { Columns = GridGoods.Length + 2 };
        grid.AddThemeConstantOverride("h_separation", 4);
        grid.AddThemeConstantOverride("v_separation", 3);
        box.AddChild(grid);
        grid.AddChild(Cell("", Ui.Dim, width: 64));
        foreach (var g in GridGoods) grid.AddChild(Cell(GoodName(g), Ui.Dim, width: 62));
        grid.AddChild(Cell("", Ui.Dim, width: 50));
        foreach (var b in planets)
        {
            var id = b.Id;
            grid.AddChild(Cell(b.Name, Ui.Dim, width: 64));
            foreach (var g in GridGoods)
            {
                var good = g;
                var cell = Small("", () => { _cell = (id, good); _editGood = good; }, $"{b.Name} · {GoodName(good)}: click to change its stock rule");
                cell.CustomMinimumSize = new Vector2(62, 24);
                cell.ClipText = true;
                cell.AddThemeFontSizeOverride("font_size", 11);
                if (_cell == (id, good)) cell.Modulate = Ui.Player;
                else if (b.StockRules.ContainsKey(good)) cell.Modulate = new Color("f0d08a");
                _liveButtons.Add((cell, w2 => w2.FindBody(id) is Body x ? SupplyChain.RuleText(x, good) : ""));
                grid.AddChild(cell);
            }
            var siege = Small(b.SiegeMode ? "Siege" : "Siege?", () => Issue(new SetSiegeModeCommand(Side.Player, id, !b.SiegeMode), "siege mode"),
                b.SiegeMode ? "Siege mode is on: exports locked, keep levels raised. Click to turn it off"
                            : "Turn siege mode on: lock this planet's exports and raise its keep levels");
            siege.CustomMinimumSize = new Vector2(50, 24);
            siege.AddThemeFontSizeOverride("font_size", 11);
            if (b.SiegeMode) siege.Modulate = Ui.Danger;
            grid.AddChild(siege);
        }
        box.AddChild(Text("Siege mode on a planet locks its exports and raises its keep levels. Click a cell to change a rule; numbers in yellow are set by hand.", 11));
        Editor(w, box, live, planets);
    }

    /// <summary>The rule editor for the chosen planet and good: keep, export above, import below, each Auto or a number.</summary>
    private void Editor(World w, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live, List<Body> planets)
    {
        if (_cell is not (int bodyId, string good) || w.FindBody(bodyId) is not Body b || b.Owner != Side.Player) return;
        var head = Row(box);
        head.AddChild(Ui.Label(b.Name, 13, Ui.Text));
        var pick = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(130, 24) };
        pick.AddThemeFontSizeOverride("font_size", 12);
        var goods = Colonies.Goods.Select(g => g.Id).ToList();
        foreach (var g in goods) pick.AddItem(GoodName(g));
        pick.Selected = Math.Max(0, goods.IndexOf(good));
        pick.ItemSelected += i => { _cell = (bodyId, goods[(int)i]); _editGood = goods[(int)i]; };
        head.AddChild(pick);
        var info = Ui.Label("", 12, Ui.Dim);
        head.AddChild(info);
        live.Add((info, w2 => w2.FindBody(bodyId) is Body x
            ? $"held {Colonies.Held(x, good):0} · made {Colonies.MadePerDay(x, good):0.#}/d · used {Colonies.UsePerDay(x, good):0.#}/d" : ""));

        RuleRow(box, live, b, good, "Keep", "Stock to keep here (Auto: siege days of what it uses, plus what it waits for)",
            r => r?.Keep, x => Colonies.Target(x, good) - Colonies.Want(x, good), (r, v) => (v, r?.ExportAbove, r?.ImportBelow));
        RuleRow(box, live, b, good, "Export above", "Stock above this may be shipped away (Auto: above the keep level)",
            r => r?.ExportAbove, x => SupplyChain.ExportLine(x, good), (r, v) => (r?.Keep, v, r?.ImportBelow));
        RuleRow(box, live, b, good, "Import below", "The planet asks for more below this (Auto: half the keep level)",
            r => r?.ImportBelow, x => SupplyChain.AskLine(x, good), (r, v) => (r?.Keep, r?.ExportAbove, v));
    }

    private void RuleRow(VBoxContainer box, List<(Label Label, Func<World, string> Text)> live, Body b, string good, string name, string tip,
        Func<StockRule?, double?> get, Func<Body, double> autoValue, Func<StockRule?, double?, (double? Keep, double? Export, double? Import)> with)
    {
        var bodyId = b.Id;
        var row = Row(box);
        var label = Ui.Label(name, 12, Ui.Dim);
        label.CustomMinimumSize = new Vector2(100, 0);
        label.TooltipText = tip;
        row.AddChild(label);
        var value = Ui.Label("", 12, Ui.Text);
        value.CustomMinimumSize = new Vector2(90, 0);
        row.AddChild(value);
        live.Add((value, w => w.FindBody(bodyId) is Body x
            ? get(SupplyChain.Rule(x, good)) is double v ? $"{v:0}" : $"Auto ({autoValue(x):0})" : ""));
        void Set(Func<double, double?> change)
        {
            if (_sim?.World.FindBody(bodyId) is not Body x) return;
            var rule = SupplyChain.Rule(x, good);
            var now = get(rule) ?? Math.Round(autoValue(x));
            var (keep, export, import) = with(rule, change(now));
            Issue(new SetStockRuleCommand(Side.Player, bodyId, good, keep, export, import), "setting a stock rule");
        }
        foreach (var step in new[] { -10, -1, 1, 10 })
        {
            var s = step;
            row.AddChild(Small(s > 0 ? $"+{s}" : $"{s}", () => Set(v => Math.Max(0, v + s)), $"{name} {(s > 0 ? "up" : "down")} by {Math.Abs(s)}"));
        }
        row.AddChild(Small("Auto", () => Set(_ => null), $"{name}: back to what Auto works out"));
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private void Issue(GameCommand command, string what) => _screen.IssueNow(command, what);

    private static string GoodName(string id) => id switch { "all spare" => "all spare", "food" => "food", _ => Colonies.GoodName(id) };

    public static Color GoodColor(string good) => good switch
    {
        "iron" => new Color("9aa8bd"), "copper" => new Color("d98b5f"), "rare-earths" => new Color("c08cff"), "water" => new Color("5cc8ff"),
        "uranium" => new Color("a6e05c"), "helium-3" => new Color("f2e86b"), "alloys" => new Color("c9d4e3"), "electronics" => new Color("7fe0a8"),
        "ammunition" => new Color("ffb347"), "fuel" => new Color("ff9a3c"), "spares" => new Color("e0c48f"), "food" => new Color("e0c48f"),
        _ => Ui.Text,
    };

    private static Color RiskColor(RouteRisk r) => r switch { RouteRisk.High => new Color("f8b49a"), RouteRisk.Medium => new Color("f0d08a"), _ => new Color("9be3bd") };

    private static Color KindColor(ProblemKind k) => k switch
    {
        ProblemKind.Short => new Color("f8b49a"), ProblemKind.Risk => new Color("f0d08a"), ProblemKind.Soon => new Color("a9dcf5"), _ => new Color("9be3bd"),
    };

    private static void Heading(VBoxContainer box, string text, string sub)
    {
        var row = Row(box);
        row.AddChild(Ui.Label(text, 15, Ui.Player));
        var s = Ui.Label(sub, 11, Ui.Dim);
        s.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        row.AddChild(s);
    }

    private static Label Text(string text, int size = 13)
    {
        var l = Ui.Label(text, size, Ui.Dim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(100, 0);
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return l;
    }

    private static Label Cell(string text, Color color, string tip = "", int width = 0)
    {
        var l = Ui.Label(text, 12, color);
        l.TooltipText = tip;
        l.ClipText = true;
        if (width > 0) l.CustomMinimumSize = new Vector2(width, 0);
        l.MouseFilter = tip == "" ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
        return l;
    }

    private static HBoxContainer Row(VBoxContainer box)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);
        return row;
    }

    private static Button Small(string text, Action pressed, string tip)
    {
        var b = Ui.SmallButton(text, pressed, tip);
        b.CustomMinimumSize = new Vector2(0, 24);
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }
}

/// <summary>
/// The chain view: planets and fleets as boxes in columns (sources on the left, users to the right,
/// fleets last), goods as curves between them. Width is the amount a day, red is short, dashed is at risk.
/// Click a box to go to that planet or fleet.
/// </summary>
public partial class SupplyChainView : Control
{
    private readonly GameScreen _screen;
    private List<ChainNode> _nodes = new();
    private List<ChainFlow> _flows = new();
    private readonly Dictionary<(ChainNodeKind, int), Rect2> _boxes = new();
    private string _shape = "";

    private const float BoxW = 120, BoxH = 46;

    public SupplyChainView() : this(null!) { }
    public SupplyChainView(GameScreen screen)
    {
        _screen = screen;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
    }

    public void Show(World w, List<ChainNode> nodes, List<ChainFlow> flows)
    {
        _nodes = nodes;
        _flows = flows;
        var shape = string.Join(",", nodes.Select(n => $"{n.Kind}{n.Id}{n.Sub}")) + "|" + string.Join(",", flows.Select(f => $"{f.FromId}>{f.ToId}{f.Good}{f.Label}{f.Short}{f.Risk}"));
        if (shape == _shape) return;
        _shape = shape;
        Layout();
        QueueRedraw();
    }

    /// <summary>Columns by how far down the chain each node is (planets with nothing moving stay in the first column).</summary>
    private void Layout()
    {
        _boxes.Clear();
        var depth = _nodes.ToDictionary(n => (n.Kind, n.Id), _ => 0);
        for (var pass = 0; pass < 6; pass++)
            foreach (var f in _flows)
            {
                var from = (f.FromKind, f.FromId);
                var to = (f.ToKind, f.ToId);
                if (depth.ContainsKey(from) && depth.ContainsKey(to) && depth[to] < depth[from] + 1 && depth[from] < 4) depth[to] = depth[from] + 1;
            }
        var maxPlanet = depth.Where(d => d.Key.Kind == ChainNodeKind.Planet).Select(d => d.Value).DefaultIfEmpty(0).Max();
        foreach (var k in depth.Keys.Where(k => k.Kind == ChainNodeKind.Fleet).ToList()) depth[k] = maxPlanet + 1;
        var cols = depth.Values.DefaultIfEmpty(0).Max() + 1;
        var width = Size.X > 0 ? Size.X : CustomMinimumSize.X;
        var gap = cols > 1 ? (width - 20 - BoxW) / (cols - 1) : 0;
        var rows = 0;
        for (var c = 0; c < cols; c++)
        {
            var inCol = _nodes.Where(n => depth[(n.Kind, n.Id)] == c).ToList();
            rows = Math.Max(rows, inCol.Count);
            for (var i = 0; i < inCol.Count; i++)
                _boxes[(inCol[i].Kind, inCol[i].Id)] = new Rect2(10 + c * gap, 34 + i * (BoxH + 22), BoxW, BoxH);
        }
        CustomMinimumSize = new Vector2(CustomMinimumSize.X, Math.Max(200, 44 + rows * (BoxH + 22)));
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("070a10"));
        if (_nodes.Count == 0)
        {
            DrawString(font, new Vector2(12, 24), "Nothing held yet", HorizontalAlignment.Left, -1, 12, Ui.Dim);
            return;
        }
        // Several goods between the same two nodes fan out a little.
        var seen = new Dictionary<(int, int, ChainNodeKind), int>();
        foreach (var f in _flows)
        {
            if (!_boxes.TryGetValue((f.FromKind, f.FromId), out var a) || !_boxes.TryGetValue((f.ToKind, f.ToId), out var b)) continue;
            var k = (f.FromId, f.ToId, f.ToKind);
            var n = seen.GetValueOrDefault(k);
            seen[k] = n + 1;
            var off = (n - 0.5f * Math.Min(n, 2)) * 7;
            var p0 = new Vector2(a.End.X, a.GetCenter().Y + off);
            var p3 = b.Position.X > a.Position.X ? new Vector2(b.Position.X, b.GetCenter().Y + off) : new Vector2(b.End.X, b.GetCenter().Y + off);
            var bend = Math.Max(40, Math.Abs(p3.X - p0.X) / 2);
            var p1 = p0 + new Vector2(bend, 0);
            var p2 = p3 + new Vector2(p3.X >= p0.X ? -bend : bend, 0);
            var color = f.Short ? Ui.Danger : SupplyTab.GoodColor(f.Good);
            color.A = 0.8f;
            var width = (float)Math.Clamp(1.5 + f.PerDay * 2, 1.5, 9);
            var points = new Vector2[25];
            for (var i = 0; i < points.Length; i++) points[i] = Bezier(p0, p1, p2, p3, i / (float)(points.Length - 1));
            if (f.Risk == RouteRisk.Low) DrawPolyline(points, color, width, true);
            else
                for (var i = 0; i + 1 < points.Length; i += 2) DrawLine(points[i], points[i + 1], color, width, true);
            var mid = Bezier(p0, p1, p2, p3, 0.5f) + new Vector2(0, -6 - n * 12);
            DrawString(font, mid - new Vector2(80, 0), f.Label, HorizontalAlignment.Center, 160, 10, color);
        }
        foreach (var node in _nodes)
        {
            if (!_boxes.TryGetValue((node.Kind, node.Id), out var r)) continue;
            var box = new StyleBoxFlat { BgColor = new Color("111821"), BorderColor = node.Kind == ChainNodeKind.Fleet ? new Color("9be3bd") : new Color("5fb3d9") };
            box.SetBorderWidthAll(1);
            box.SetCornerRadiusAll(7);
            DrawStyleBox(box, r);
            DrawString(font, r.Position + new Vector2(0, 19), node.Name, HorizontalAlignment.Center, r.Size.X, 13, Ui.Text);
            DrawString(font, r.Position + new Vector2(4, 36), node.Sub, HorizontalAlignment.Center, r.Size.X - 8, 10, Ui.Dim);
        }
    }

    private static Vector2 Bezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float t)
    {
        var u = 1 - t;
        return a * (u * u * u) + b * (3 * u * u * t) + c * (3 * u * t * t) + d * (t * t * t);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click || _screen == null) return;
        foreach (var ((kind, id), r) in _boxes)
        {
            if (!r.HasPoint(click.Position)) continue;
            if (kind == ChainNodeKind.Fleet) { _screen.Select(id); _screen.Follow(id, zoom: true); }
            else _screen.PickBody(id);
            AcceptEvent();
            return;
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) { Layout(); QueueRedraw(); }
    }
}
