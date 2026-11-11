using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The fleets and planets list down the left of the map, in place of the old drop-downs. Fleets come in
/// three groups: those that need you (in a fight, low on fuel, hungry or out of ammunition), the rest of
/// yours, and the rival fleets you know of. Planets are listed outward from the star with their moons.
/// Click a row to select it and centre the map; double-click to zoom in and follow. « folds it away (L).
/// </summary>
public partial class SidePanel : PanelContainer
{
    public const float Width = 300;

    private readonly GameScreen _screen;
    private VBoxContainer _rows = null!;
    private Button _fleetTab = null!, _planetTab = null!;
    private bool _planets;

    /// <summary>Show the planets tab rather than the fleets.</summary>
    public bool ShowPlanets { get => _planets; set { if (_planets != value) _shape = ""; _planets = value; } }
    private string _shape = "";

    public SidePanel() : this(null!) { }
    public SidePanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 10);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        _fleetTab = Ui.SmallButton("Fleets", () => { _planets = false; _shape = ""; }, "Your fleets, then the rival fleets you know of");
        _planetTab = Ui.SmallButton("Planets", () => { _planets = true; _shape = ""; }, "Every planet and moon, outward from the star");
        tabs.AddChild(_fleetTab);
        tabs.AddChild(_planetTab);
        tabs.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        tabs.AddChild(Ui.SmallButton("«", () => _screen.ShowSide = false, "Fold the list away (L)"));
        box.AddChild(tabs);
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            CustomMinimumSize = new Vector2(Width, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _rows.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_rows);
        box.AddChild(scroll);
        var hint = Ui.Label("Click a row to centre the map on it · double-click to follow", 11, Ui.Dim);
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        hint.CustomMinimumSize = new Vector2(Width, 0);
        box.AddChild(hint);
    }

    private sealed record Row(string Group, int Id, bool IsBody, string Title, string? Badge, Color BadgeColor, string Line, Color TitleColor);

    public void Refresh(Simulation sim, float height)
    {
        CustomMinimumSize = new Vector2(Width + 20, height);
        Size = new Vector2(Width + 20, height);
        var w = sim.World;
        var mine = w.Fleets.Count(f => f.Side == Side.Player);
        var held = w.Bodies.Count(b => b.Owner == Side.Player);
        _fleetTab.Text = $"Fleets ({mine})";
        _planetTab.Text = $"Planets ({held})";
        _fleetTab.Modulate = _planets ? new Color(1, 1, 1, 0.6f) : Ui.Player;
        _planetTab.Modulate = _planets ? Ui.Player : new Color(1, 1, 1, 0.6f);

        var rows = _planets ? BodyRows(w) : FleetRows(w);
        var selected = _screen.SelectedId;
        var shape = string.Join("|", rows.Select(r => $"{r.Group}{r.Id}{r.Title}{r.Badge}{r.Line}")) + "#" + selected;
        if (shape == _shape) return;
        _shape = shape;
        foreach (var c in _rows.GetChildren()) c.QueueFree();
        string? group = null;
        foreach (var r in rows)
        {
            if (r.Group != group)
            {
                group = r.Group;
                _rows.AddChild(Ui.Label(group, 12, Ui.Dim));
            }
            _rows.AddChild(RowControl(r, r.Id == selected));
        }
        if (rows.Count == 0) _rows.AddChild(Ui.Label(_planets ? "No planets" : "No fleets", 13, Ui.Dim));
    }

    private Control RowControl(Row r, bool selected)
    {
        var card = new PanelContainer { MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        var style = new StyleBoxFlat { BgColor = selected ? new Color("16233a") : new Color("0c111a"), BorderColor = selected ? Ui.Player : new Color("1a2230") };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(5);
        style.SetContentMarginAll(6);
        card.AddThemeStyleboxOverride("panel", style);
        var v = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        v.AddThemeConstantOverride("separation", 1);
        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        var title = Ui.Label(r.Title, 14, r.TitleColor);
        title.MouseFilter = MouseFilterEnum.Ignore;
        title.ClipText = true;
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        if (r.Badge != null)
        {
            var badge = Ui.Label(r.Badge, 11, r.BadgeColor);
            badge.MouseFilter = MouseFilterEnum.Ignore;
            head.AddChild(badge);
        }
        v.AddChild(head);
        if (r.Line.Length > 0)
        {
            var line = Ui.Label(r.Line, 12, Ui.Dim);
            line.MouseFilter = MouseFilterEnum.Ignore;
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            line.CustomMinimumSize = new Vector2(Width - 20, 0);
            v.AddChild(line);
        }
        card.AddChild(v);
        card.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            Main.Instance.RunGuarded("side list", () =>
            {
                if (r.IsBody) _screen.PickBody(r.Id);
                else if (click.DoubleClick) { _screen.Select(r.Id); _screen.ZoomToSelected(); }
                else _screen.PickFleet(r.Id);
            });
            card.AcceptEvent();
        };
        return card;
    }

    /// <summary>Why a fleet of yours needs you, if it does: a fight, low fuel, hunger or no ammunition.</summary>
    private static (string Badge, Color Color)? Trouble(World w, Fleet f)
    {
        if (Engagement.EnemyFor(w, f) is int enemy && w.FindFleet(enemy) is { } e && Vec2.Distance(e.Position, f.Position) < Heat.D.LongRangeMm * 2)
            return ("IN BATTLE", Ui.Danger);
        var ships = w.ShipsOf(f).ToList();
        if (ships.Count == 0) return null;
        if (ships.Any(SupplyLines.Hungry)) return ("HUNGRY", Ui.Danger);
        var fuel = ships.Min(s => s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w, s)));
        if (fuel < 0.25) return ("LOW FUEL", Ui.Rival);
        var armed = ships.Where(s => SupplyLines.MaxAmmo(s) > 0).ToList();
        if (armed.Count > 0 && armed.All(s => s.Ammo <= 0)) return ("NO AMMO", Ui.Rival);
        return null;
    }

    private static List<Row> FleetRows(World w)
    {
        var rows = new List<Row>();
        var later = new List<Row>();
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player).OrderBy(f => f.Id))
        {
            var ships = f.ShipIds.Count;
            var lag = w.Light.On ? Light.DelayHours(w, Side.Player, f.Position) : 0;
            var title = $"{f.Name} · {ships} ship{(ships == 1 ? "" : "s")}";
            var line = FleetPanel.Doing(w, f);
            if (f.Flight is { FlybyBodyId: int via } fl && fl.FlybyHour > w.Hour && w.FindBody(via) is Body swing)
                line += $" · slingshot at {swing.Name}";
            if (f.SupplyRunFor is int runFor && w.FindFleet(runFor) is Fleet served) line = $"Supply run (Auto) for {served.Name} · " + line;
            if (lag > 1.0 / 60) line += $" · {Format.Hours(lag)} of light";
            if (Trouble(w, f) is var (badge, color))
                rows.Add(new Row("Needs you", f.Id, false, title, badge, color, line, Ui.Player));
            else
                later.Add(new Row("On their orders", f.Id, false, title, null, Ui.Dim, line, Ui.Player));
        }
        rows.AddRange(later);
        foreach (var s in Sensors.Known(w, seat: true).Where(s => !s.Own && s.SeenBy == Side.Player && w.FindFleet(s.FleetId) != null)
                     .OrderByDescending(s => s.Visible).ThenBy(s => s.FleetId))
        {
            var near = w.Bodies.Where(b => b.Kind != BodyKind.Star).OrderBy(b => Vec2.Distance(b.Position, s.Guess(w.Hour))).FirstOrDefault();
            rows.Add(new Row("Enemy (last seen)", s.FleetId, false, $"{s.Name} · {s.Ships} ship{(s.Ships == 1 ? "" : "s")}",
                s.Visible ? "seen now" : $"{Format.Hours(w.Hour - s.Hour)} ago", s.Visible ? Ui.Danger : Ui.Dim,
                near != null ? $"Near {near.Name}" : "", Ui.Rival));
        }
        return rows;
    }

    private static List<Row> BodyRows(World w)
    {
        var rows = new List<Row>();
        void Add(Body b, int depth)
        {
            var tags = new List<string> { Format.Title(b.Kind.ToString()) };
            if (b.Shipyard) tags.Add("shipyard");
            if (b.ClaimSide == Side.Player) tags.Add("you are claiming it");
            if (w.Loop && b.PopK > 0)
                tags.Add($"{b.PopK:0.#}k people" + (b.Owner != null && w.Rules.Happiness ? $", happy {b.Happiness:0}" : "") + (b.Hungry ? ", HUNGRY" : ""));
            else if (w.Loop && Planets.Settleable(b)) tags.Add($"empty, room for {Planets.Cap(b):0}k");
            if (w.Loop && b.Owner != null && b.Industry > 0) tags.Add($"industry {Planets.IndustryPerDay(w, b):0.#}");
            else if (w.Loop && b.Industry > 0) tags.Add($"industry {b.Industry:0}");
            else if (w.Loop && b.Kind != BodyKind.Star) tags.Add("can't be claimed");
            if (w.Loop && b.Owner != null && Economy.Blockaded(w, b)) tags.Add("BLOCKADED");
            if (w.Loop && b.LandingSide != null) tags.Add("ground fight");
            var (badge, color) = b.Owner switch
            {
                Side.Player => ("yours", Ui.Player),
                Side.Rival => ("rival", Ui.Rival),
                _ => ((string?)null, Ui.Dim),
            };
            rows.Add(new Row("Outward from the star", b.Id, true, new string(' ', depth * 4) + b.Name, badge, color, string.Join(" · ", tags),
                b.Owner == Side.Player ? Ui.Player : b.Owner == Side.Rival ? Ui.Rival : Ui.Text));
            foreach (var child in w.Bodies.Where(c => c.ParentId == b.Id).OrderBy(c => c.OrbitRadius)) Add(child, depth + 1);
        }
        foreach (var root in w.Bodies.Where(b => b.ParentId == null || w.FindBody(b.ParentId.Value) == null)) Add(root, 0);
        return rows;
    }
}
