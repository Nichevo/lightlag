using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The planets list (Y) and the planet screen (double-click a row, or "Open planet" on the planet panel).
/// The list filters by owner, kind, mineral and what a planet has, and sorts by people, Industry,
/// happiness, distance or name. The planet screen has the build board and build list, the priorities,
/// storage and siege stock, defence and transport. Like the research screen, it rebuilds only when its
/// shape changes and updates numbers live.
/// </summary>
public partial class ColonyScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _root = null!;
    private string _shape = "";
    private readonly List<Action<World>> _live = new();
    private readonly List<(Button Button, BuildingData Kind)> _buildButtons = new();

    /// <summary>Where each scrolling box was scrolled to (by planet and box), so a rebuild keeps the place.</summary>
    private readonly Dictionary<string, double> _scrolled = new();
    private string? _hoverGood, _shownGood;
    private BoardView? _board;

    // List filters.
    private string _owner = "all", _kind = "all", _mineral = "", _has = "", _sort = "people";
    // Planet screen.
    private string _area = "";
    private bool _onlyHere = true;
    private string _buildSort = "type";
    private string? _placing;
    private int? _selectedPiece;
    private int _runTo = -1;
    private string _runGood = "";
    private int _runEvery = 20;
    private int _runEscort = -1;

    private static readonly string[] Owners = { "all", "yours", "rival", "nobody" };
    private static readonly string[] KindsShown = { "all", "planets", "moons", "asteroids" };
    private static readonly string[] HasShown = { "", "farms", "mines", "shipyard", "free space" };
    private static readonly string[] Sorts = { "people", "industry", "happiness", "distance", "name" };
    private static readonly string[] BuildSorts = { "type", "name", "cost", "staff", "power" };
    private static readonly string[] Minerals = { "iron", "copper", "rare-earths", "water", "uranium", "helium-3" };

    public ColonyScreen() : this(null!) { }
    public ColonyScreen(GameScreen screen) => _screen = screen;

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
        var body = _screen.PlanetScreenId is int id ? w.FindBody(id) : null;
        if (body != null && (body.Owner != Side.Player || body.Board == null)) body = null;
        var shape = body != null ? PlanetShape(w, body) : ListShape(w);
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            foreach (var c in _root.GetChildren()) c.QueueFree();
            _live.Clear();
            _buildButtons.Clear();
            _shownGood = null;
            _board = null;
            if (body != null) BuildPlanet(w, body);
            else BuildList(w);
        }
        foreach (var update in _live) update(w);
        if (body != null && _hoverGood != _shownGood) ShowGood(body, _hoverGood);
        if (_board != null)
        {
            _board.World = w;
            _board.Body = body;
            _board.HighlightGood = _hoverGood;
            _board.QueueRedraw();
        }
    }

    /// <summary>
    /// Back (-1) or Next (+1): open the previous or next of your planets, in the planets list's order
    /// (its filters when they include this planet, else all of yours). Wraps around. A view change, so it works while paused.
    /// </summary>
    public void Step(World w, int dir)
    {
        if (_screen.PlanetScreenId is not int id) return;
        bool Openable(Body b) => b.Owner == Side.Player && b.Board != null;
        var list = Rows(w).Where(Openable).ToList();
        if (!list.Any(b => b.Id == id)) list = Sorted(w, w.Bodies.Where(Openable)).ToList();
        if (list.Count < 2) return;
        var at = list.FindIndex(b => b.Id == id);
        _screen.PlanetScreenId = list[((at < 0 ? 0 : at) + dir + list.Count) % list.Count].Id;
    }

    public static readonly Color MakesColor = new("6fe08a"), UsesColor = new("f2a83e");

    /// <summary>Outline the build list's buildings that make (green) or use (amber) the hovered good; none when null.</summary>
    private void ShowGood(Body b, string? good)
    {
        _shownGood = good;
        foreach (var (button, kind) in _buildButtons)
        {
            if (!IsInstanceValid(button)) continue;
            var makes = good != null && Colonies.KindMakes(b, kind, good);
            var uses = good != null && !makes && Colonies.KindUses(kind, good);
            foreach (var state in new[] { "normal", "hover", "pressed", "disabled" })
            {
                if (!makes && !uses) { button.RemoveThemeStyleboxOverride(state); continue; }
                var box = new StyleBoxFlat { BgColor = new Color(0.08f, 0.1f, 0.14f, 0.95f), BorderColor = makes ? MakesColor : UsesColor };
                box.SetBorderWidthAll(2);
                box.SetCornerRadiusAll(4);
                box.SetContentMarginAll(6);
                button.AddThemeStyleboxOverride(state, box);
            }
        }
    }

    /// <summary>The hover card for a good in storage: the buildings here that make and use it, and what could be built for it.</summary>
    private static Control GoodCard(Body b, string good, string name)
    {
        string Count(IEnumerable<Piece> ps) => string.Join(", ", ps.GroupBy(p => Planets.BuildingKind(p.Kind)?.Name ?? p.Kind)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key));
        var made = b.Pieces.Where(p => Colonies.PieceMakes(b, p, good)).ToList();
        var used = b.Pieces.Where(p => Colonies.PieceUses(b, p, good)).ToList();
        var could = Colonies.Kinds.Where(k => k.Buildable && Colonies.KindMakes(b, k, good)).Select(k => k.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return Tips.Card(name, Ui.Text, new (string, Color?)[]
        {
            ($"Made by: {(made.Count > 0 ? Count(made) : "nothing here")}", MakesColor),
            ($"Used by: {(used.Count > 0 ? Count(used) : good == "food" ? "the people" : "nothing here")}" + (good == "food" && used.Count > 0 ? " and the people" : ""), UsesColor),
            (could.Count > 0 ? $"Could build for it: {string.Join(", ", could)}" : "", Ui.Dim),
            ("Green outlines: buildings that make it. Amber: buildings that use it.", Ui.Dim),
        });
    }

    /// <summary>A good's name in the storage list: hovering it outlines what makes and uses it, with a card saying which.</summary>
    private Control GoodName(Body b, string good, string name)
    {
        var l = new TipLabel { Text = name, Tip = () => GoodCard(b, good, name) };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", Ui.Text);
        l.MouseEntered += () => _hoverGood = good;
        l.MouseExited += () => { if (_hoverGood == good) _hoverGood = null; };
        return l;
    }

    // ---- Shared bits ---------------------------------------------------------------------------

    private static PanelContainer Box(Control content, int padding = 12)
    {
        var p = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.07f, 0.11f, 0.95f), BorderColor = new Color(Ui.Player, 0.25f) };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(padding);
        p.AddThemeStyleboxOverride("panel", style);
        p.AddChild(content);
        return p;
    }

    private static VBoxContainer Column(int gap = 6)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", gap);
        return v;
    }

    private static HBoxContainer Row(int gap = 6)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", gap);
        return h;
    }

    private Label Live(Func<World, string> text, int size = 14, Color? color = null)
    {
        var l = Ui.Label("", size, color);
        _live.Add(w => l.Text = text(w));
        return l;
    }

    /// <summary>A drop-down that shows labels and calls back with the chosen key.</summary>
    private static OptionButton Pick(IReadOnlyList<(string Key, string Label)> items, string current, Action<string> chosen, string tip)
    {
        var o = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = tip };
        o.AddThemeFontSizeOverride("font_size", 14);
        for (var i = 0; i < items.Count; i++)
        {
            o.AddItem(items[i].Label, i);
            if (items[i].Key == current) o.Selected = i;
        }
        o.ItemSelected += i => Main.Instance.RunGuarded("drop-down", () => chosen(items[(int)i].Key));
        return o;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string GoodShort(string id) => Colonies.Goods.Find(g => g.Id == id)?.Short ?? id;

    /// <summary>Air and temperature, from the planet type.</summary>
    private static string Climate(Body b) => b.Type switch
    {
        "terran" => "Air · mild",
        "desert" => "Thin air · hot",
        "cloudy" => "Thick air · hot",
        "rocky" => "No air · cold",
        "ice" => "No air · frozen",
        "volcanic" => "Toxic · very hot",
        "hazy" => "Haze · cold",
        "asteroid" => "No air · cold",
        _ => "—",
    };

    private static double DistanceAu(World w, Body b) =>
        Economy.ShipyardOf(w, Side.Player) is Body home ? Vec2.Distance(home.Position, b.Position) / Physics.MmPerAu : 0;

    /// <summary>Tiles still free to build on (not mountains, not covered).</summary>
    private static int FreeTiles(Body b)
    {
        if (b.Board is not Board board) return 0;
        var free = 0;
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
            if (board.At(x, y) != 'm' && !b.Pieces.Any(p => p.Covers(x, y))) free++;
        return free;
    }

    private static int UsableTiles(Body b) => b.Board is Board board ? board.Tiles.Count(t => t != 'm') : 0;

    // ---- The planets list ----------------------------------------------------------------------

    private string ListShape(World w) =>
        $"list|{_owner}|{_kind}|{_mineral}|{_has}|{_sort}|{GetViewportRect().Size}|"
        + string.Join(",", Rows(w).Select(b => $"{b.Id}:{b.Owner}:{b.Pieces.Count}"));

    private IEnumerable<Body> Rows(World w)
    {
        var rows = w.Bodies.Where(b => b.Kind != BodyKind.Star);
        rows = _owner switch
        {
            "yours" => rows.Where(b => b.Owner == Side.Player),
            "rival" => rows.Where(b => b.Owner is Side.Rival or Side.Third),
            "nobody" => rows.Where(b => b.Owner == null),
            _ => rows,
        };
        rows = _kind switch
        {
            "planets" => rows.Where(b => b.Kind == BodyKind.Planet),
            "moons" => rows.Where(b => b.Kind == BodyKind.Moon),
            "asteroids" => rows.Where(b => b.Kind == BodyKind.Asteroid),
            _ => rows,
        };
        if (_mineral != "") rows = rows.Where(b => b.Board?.Deposits.Any(d => d.Mineral == _mineral) == true);
        rows = _has switch
        {
            "farms" => rows.Where(b => Colonies.Built(b).Any(p => Planets.BuildingKind(p.Kind)?.Food > 0)),
            "mines" => rows.Where(b => Colonies.Built(b).Any(p => Planets.BuildingKind(p.Kind)?.Mines > 0 || Planets.BuildingKind(p.Kind)?.Makes?.ContainsKey("water") == true)),
            "shipyard" => rows.Where(b => b.Shipyard),
            "free space" => rows.Where(b => FreeTiles(b) > 0),
            _ => rows,
        };
        return Sorted(w, rows);
    }

    private IEnumerable<Body> Sorted(World w, IEnumerable<Body> rows)
    {
        return _sort switch
        {
            "industry" => rows.OrderByDescending(b => Planets.IndustryPerDay(w, b)).ThenBy(b => b.Name),
            "happiness" => rows.OrderByDescending(b => b.PopK > 0 ? b.Happiness : -1).ThenBy(b => b.Name),
            "distance" => rows.OrderBy(b => DistanceAu(w, b)).ThenBy(b => b.Name),
            "name" => rows.OrderBy(b => b.Name),
            _ => rows.OrderByDescending(b => b.PopK).ThenByDescending(b => Planets.Cap(b)).ThenBy(b => b.Name),
        };
    }

    private void BuildList(World w)
    {
        var top = Row(16);
        top.AddChild(Ui.Label("Planets", 26, new Color("f2f5fa")));
        var note = Ui.Label("Double-click a planet of yours to open it, any other to go to it on the map", 13, Ui.Dim);
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(note);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        top.AddChild(Live(w2 =>
        {
            var me = Economy.Of(w2, Side.Player);
            return $"Freighters lost with cargo: {me.CargoLost} · Auto transport {(me.AutoTransport ? "on" : "off")}";
        }, 13, Ui.Dim));
        top.AddChild(Ui.SmallButton("Back to map", () => _screen.ShowPlanets = false, "Close the planets list (Y or Esc)"));
        _root.AddChild(top);

        var filters = Row(10);
        filters.AddChild(Ui.Label("Owner", 13, Ui.Dim));
        filters.AddChild(Pick(Owners.Select(o => (o, o == "all" ? "All" : o == "nobody" ? "Nobody's" : Cap(o))).ToList(), _owner, k => _owner = k, "Whose planets to show"));
        filters.AddChild(Ui.Label("Kind", 13, Ui.Dim));
        filters.AddChild(Pick(KindsShown.Select(k => (k, Cap(k))).ToList(), _kind, k => _kind = k, "Planets, moons or asteroids"));
        filters.AddChild(Ui.Label("Mineral", 13, Ui.Dim));
        filters.AddChild(Pick(new[] { ("", "Any") }.Concat(Minerals.Select(m => (m, Colonies.GoodName(m)))).ToList(), _mineral, k => _mineral = k, "Only planets with a deposit of this mineral"));
        filters.AddChild(Ui.Label("Has", 13, Ui.Dim));
        filters.AddChild(Pick(HasShown.Select(h => (h, h == "" ? "Anything" : Cap(h))).ToList(), _has, k => _has = k, "Only planets with farms, mines, a shipyard or free space"));
        filters.AddChild(Ui.Label("Sort by", 13, Ui.Dim));
        filters.AddChild(Pick(Sorts.Select(s => (s, Cap(s))).ToList(), _sort, k => _sort = k, "Order of the list"));
        _root.AddChild(filters);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var grid = new GridContainer { Columns = 10, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        grid.AddThemeConstantOverride("h_separation", 18);
        grid.AddThemeConstantOverride("v_separation", 6);
        foreach (var head in new[] { "", "Planet", "Owner", "People", "Happiness", "Makes", "Key minerals", "Air · temperature", "Distance", "Space used · status" })
            grid.AddChild(Ui.Label(head, 13, Ui.Dim));
        foreach (var b in Rows(w)) AddRow(grid, w, b);
        scroll.AddChild(grid);
        _root.AddChild(scroll);
    }

    private void AddRow(GridContainer grid, World w, Body b)
    {
        var mine = b.Owner == Side.Player;
        var rival = b.Owner is Side.Rival or Side.Third;
        var colour = mine ? Ui.Player : rival ? Ui.Rival : Ui.Text;
        var cells = new List<Control>();
        cells.Add(new PlanetPicture(b));
        cells.Add(Ui.Label($"{b.Name}\n{Format.Title(b.Kind.ToString().ToLowerInvariant())} · {b.Type}{(b.Home ? " · home" : "")}", 14, colour));
        cells.Add(Ui.Label(mine ? "Yours" : rival ? "Rival" : "Nobody's", 14, colour));
        var cap = Planets.Cap(b);
        // The rival's numbers are rough: what the map shows of its worlds.
        cells.Add(Live(_ => cap <= 0 ? "Nobody can live here"
            : b.PopK <= 0 ? $"Empty, {cap:0}k room"
            : rival ? $"About {Math.Round(b.PopK / 5) * 5:0}k" : $"{b.PopK:0.#}k of {cap:0}k"));
        cells.Add(Live(_ => b.PopK <= 0 || b.Owner == null ? "—" : rival ? (b.Happiness >= 50 ? "Content" : "Unhappy") : $"{b.Happiness:0}"));
        cells.Add(Live(w2 => mine ? Makes(w2, b) : rival ? "Not seen" : b.Industry > 0 ? $"Worth {b.Industry:0} Industry" : "—", 13));
        var minerals = b.Board?.Deposits.GroupBy(d => d.Mineral).Select(g => $"{GoodShort(g.Key)}{(g.Any(d => d.Rich) ? "+" : "")}") ?? Enumerable.Empty<string>();
        cells.Add(Ui.Label(string.Join(" ", minerals) is { Length: > 0 } m ? m : "—", 13));
        cells.Add(Ui.Label(Climate(b), 13));
        cells.Add(Live(w2 =>
        {
            var au = DistanceAu(w2, b);
            return $"{au:0.00} AU · light {au * 8.317:0} min";
        }, 13));
        cells.Add(Live(w2 => b.Board == null ? "No ground: stations only" : $"{UsableTiles(b) - FreeTiles(b)}/{UsableTiles(b)} tiles · {Status(w2, b)}", 13));
        foreach (var cell in cells)
        {
            cell.MouseFilter = MouseFilterEnum.Pass;
            cell.MouseFilter = MouseFilterEnum.Stop;
            cell.TooltipText = mine && b.Board != null ? $"Double-click to open {b.Name}" : $"Double-click to go to {b.Name} on the map";
            cell.GuiInput += e =>
            {
                if (e is not InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left }) return;
                Main.Instance.RunGuarded("open planet", () =>
                {
                    if (mine && b.Board != null) _screen.PlanetScreenId = b.Id;
                    else { _screen.ShowPlanets = false; _screen.PickBody(b.Id); }
                });
            };
            grid.AddChild(cell);
        }
    }

    private static string Makes(World w, Body b)
    {
        var parts = new List<string> { $"Ind {Planets.IndustryPerDay(w, b):0.#}" };
        var food = Planets.FoodMadePerDay(b);
        if (food > 0.01) parts.Add($"food {food:0.#}");
        foreach (var g in Colonies.Goods)
        {
            var made = Colonies.MadePerDay(b, g.Id);
            if (made > 0.05) parts.Add($"{g.Short} {made:0.#}");
        }
        return string.Join(" · ", parts);
    }

    private static string Status(World w, Body b)
    {
        if (b.Owner is Side.Rival or Side.Third) return Sides.Three(w) ? $"{Races.Of(w, b.Owner.Value).Name} world" : "Rival world";
        if (b.Owner == null) return b.PopK > 0 ? "Independent" : "Free to settle";
        var parts = new List<string>();
        if (Economy.Blockaded(w, b)) parts.Add("BLOCKADED");
        if (b.Abandoned && b.PopK <= 0) parts.Add("ABANDONED");
        if (b.Hungry) parts.Add(b.LeavingK > 0 ? "HUNGRY, people leaving" : "HUNGRY");
        if (b.Thirsty) parts.Add("short of water");
        if (b.PowerWanted > b.PowerMade + 1e-6) parts.Add(b.FromBattery > 0 ? "on batteries" : "short of power");
        if (b.Pieces.FirstOrDefault(p => !p.Built) is Piece building)
            parts.Add($"building {Planets.BuildingKind(building.Kind)?.Name.ToLowerInvariant()}");
        if (b.Pieces.Any(p => p.RefitHoursLeft > 0)) parts.Add("refitting");
        return parts.Count > 0 ? string.Join(", ", parts) : "Fine";
    }

    /// <summary>A small coloured disc: the planet's picture in the list.</summary>
    /// <summary>The planet as it looks on the map (same drawing), big enough to tell the worlds apart.</summary>
    private partial class PlanetPicture : Control
    {
        private readonly Body? _body;
        public PlanetPicture() { }
        public PlanetPicture(Body b) { _body = b; CustomMinimumSize = new Vector2(96, 96); MouseFilter = MouseFilterEnum.Pass; }

        public override void _Ready()
        {
            if (_body == null) return;
            var sprite = PlanetSprite.For(_body);
            sprite.ShowBehindParent = false;
            AddChild(sprite);
            // Rough sizes by kind, so a giant still looks bigger than a moon, a moon than a rock.
            var r = _body.Type is "gas" or "ice-giant" ? 30f : _body.Kind == BodyKind.Planet ? 40f : _body.Type == "asteroid" || _body.Radius < 0.1 ? 26f : 32f;
            sprite.Set(new Vector2(48, 48), r, new Vector2(-1, -0.35f), (float)(_body.Id * 1.3), 0);
        }
    }

    private partial class Dot : Control
    {
        private readonly Color _colour;
        public Dot() : this(Colors.White) { }
        public Dot(Color colour) { _colour = colour; CustomMinimumSize = new Vector2(30, 30); }
        public override void _Draw()
        {
            DrawCircle(new Vector2(15, 15), 12, _colour);
            DrawCircle(new Vector2(19, 11), 6, new Color(1, 1, 1, 0.15f));
        }
    }

    // ---- The planet screen ---------------------------------------------------------------------

    /// <summary>
    /// The screen is rebuilt when what it shows changes (a building done, Industry passing a step): this puts a
    /// scrolling box back where the player left it instead of jumping to the top. The place is set once the new
    /// content is laid out (the scroll bar's range changes), and recorded whenever the player scrolls.
    /// </summary>
    private void KeepScroll(ScrollContainer scroll, string key)
    {
        var bar = scroll.GetVScrollBar();
        var restoring = _scrolled.TryGetValue(key, out var want) && want > 0;
        bar.Changed += () =>
        {
            if (!restoring) return;
            bar.Value = want;
            if (bar.Value >= want - 0.5 || bar.MaxValue - bar.Page >= want) restoring = false;
        };
        bar.ValueChanged += v => { if (!restoring) _scrolled[key] = v; };
    }

    private string PlanetShape(World w, Body b) =>
        $"planet|{b.Id}|{string.Join(",", b.Pieces.Select(p => $"{p.Id}:{p.Built}"))}|{b.Preset}|{string.Join(",", Colonies.Priority(b))}|{b.SiegeDays}"
        + $"|{Economy.Of(w, Side.Player).AutoTransport}|{string.Join(",", w.FixedRuns.Where(r => r.Side == Side.Player).Select(r => r.Id))}"
        + $"|{Economy.Of(w, Side.Player).Researched.Count}|{_area}|{_onlyHere}|{_buildSort}|{_placing}|{_selectedPiece}"
        + $"|{_runTo}|{_runGood}|{_runEvery}|{_runEscort}|{GetViewportRect().Size}|{(int)(Economy.Of(w, Side.Player).Industry / 20)}";

    private void BuildPlanet(World w, Body b)
    {
        var top = Row(16);
        top.AddChild(Ui.Label(b.Name, 26, Ui.Player));
        var kind = Ui.Label($"{Format.Title(b.Kind.ToString().ToLowerInvariant())} · {b.Type} · {Climate(b)}{(b.Home ? " · home world" : "")}", 14, Ui.Dim);
        kind.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(kind);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        top.AddChild(Ui.SmallButton("◀ Back", () => Step(w, -1), "Your previous planet in the planets list ([)"));
        top.AddChild(Ui.SmallButton("Next ▶", () => Step(w, 1), "Your next planet in the planets list (])"));
        top.AddChild(Ui.SmallButton("Planets list", () => _screen.PlanetScreenId = null, "Back to the planets list (Esc)"));
        top.AddChild(Ui.SmallButton("Back to map", () => { _screen.PlanetScreenId = null; _screen.ShowPlanets = false; }, "Close the planet screen"));
        _root.AddChild(top);
        _root.AddChild(Live(w2 => $"People {b.PopK:0.#}k of {Planets.Cap(b):0}k · happiness {b.Happiness:0} · power used {b.PowerUsed:0} of {b.PowerMade:0}"
                                  + (b.PowerWanted > b.PowerMade + 1e-6 ? $" (SHORT: wants {b.PowerWanted:0})" : "")
                                  + $" · makes {Makes(w2, b)} · your Industry {Economy.Of(w2, Side.Player).Industry:0}"
                                  + (Economy.Blockaded(w2, b) ? " · BLOCKADED" : ""), 14));
        // At a glance: the ground left, power made against what is used, people against jobs, and stores.
        _root.AddChild(LiveWrap(w2 => Colonies.BoardSummary(b), 13, Ui.Dim));
        _root.AddChild(LiveWrap(w2 =>
        {
            var spare = b.PowerMade - b.PowerWanted;
            var jobs = Planets.StaffNeeded(b);
            return $"Power: makes {b.PowerMade:0}, buildings want {b.PowerWanted:0}, "
                   + (spare >= 0 ? $"{spare:0} spare" : $"SHORT {-spare:0}")
                   + $" · jobs {jobs:0.#}k for {b.PopK:0.#}k people" + (b.PopK < jobs ? " (SHORT of workers)" : "")
                   + $" · {Colonies.Built(b).Count()} buildings, {b.Pieces.Count(p => !p.Built)} going up"
                   + $" · Industry {Planets.IndustryPerDay(w2, b):0.#} a day";
        }, 13, Ui.Dim));
        _root.AddChild(LiveWrap(w2 => Grid.Summary(w2, b), 13, Ui.Dim));
        _root.AddChild(LiveWrap(w2 => Colonies.PowerNote(w2, b) is string note ? "POWER: " + note : "", 13, Ui.Rival));
        _root.AddChild(LiveWrap(w2 => Planets.LeavingNote(w2, b) is string note ? "PEOPLE: " + note : "", 13, Ui.Rival));

        var main = Row(14);
        main.SizeFlagsVertical = SizeFlags.ExpandFill;
        _root.AddChild(main);

        // Left: the board, then what is selected or being placed.
        var left = Column(8);
        left.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var leftScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.4f, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        KeepScroll(leftScroll, $"{b.Id}:left");
        leftScroll.AddChild(left);
        main.AddChild(leftScroll);
        var board = b.Board!;
        var view = GetViewportRect().Size;
        var cell = Mathf.Clamp(Mathf.Floor(Mathf.Min(view.X * 0.42f / board.W, view.Y * 0.45f / board.H)), 28, 84);
        _board = new BoardView
        {
            Body = b, World = w, CustomMinimumSize = new Vector2(cell * board.W, cell * board.H),
            Placing = _placing != null ? Planets.BuildingKind(_placing) : null, SelectedPieceId = _selectedPiece,
            OnPlace = (x, y, turned) =>
            {
                _screen.IssueNow(new BuildOnPlanetCommand(Side.Player, b.Id, _placing!, x, y, turned, Queue: true), "building");
                _placing = null;
            },
            OnSelect = id => _selectedPiece = id,
            OnCancel = () => _placing = null,
        };
        left.AddChild(_board);
        left.AddChild(LiveWrap(_ => _placing != null
            ? $"Placing a {Planets.BuildingKind(_placing)?.Name.ToLowerInvariant()}: R turns it, right click cancels" + (_board?.Problem is string p ? $" · {p}" : "")
            : "Pick a building on the right to place it (it queues if one is under way or the Industry isn't there yet) · click a building to see it, take it down or call it off", 13, Ui.Dim));
        left.AddChild(Wrap("Tiles: brown land, blue ocean, white ice, grey mountains (nothing builds there). Diamonds are mineral deposits (+ rich). Green dots link buildings that help each other.", 12, Ui.Dim));
        if (b.Pieces.Any(p => !p.Built)) left.AddChild(BuildQueue(w, b));
        if (_selectedPiece is int pid && b.Pieces.Find(p => p.Id == pid) is Piece piece) left.AddChild(PieceInfo(w, b, piece));
        var under = Row(10);
        under.AddChild(Priorities(w, b));
        under.AddChild(Defence(w, b));
        left.AddChild(under);

        // Middle: build list.
        main.AddChild(BuildList(w, b));

        // Right: storage and transport.
        var rightScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        KeepScroll(rightScroll, $"{b.Id}:right");
        var right = Column(10);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightScroll.AddChild(right);
        main.AddChild(rightScroll);
        right.AddChild(Storage(w, b));
        right.AddChild(Transport(w, b));
    }

    /// <summary>A label that wraps to the width it is given rather than widening the screen.</summary>
    private static Label Wrap(string text, int size = 13, Color? color = null)
    {
        var l = Ui.Label(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.CustomMinimumSize = new Vector2(120, 0);
        return l;
    }

    private Label LiveWrap(Func<World, string> text, int size = 13, Color? color = null)
    {
        var l = Wrap("", size, color);
        _live.Add(w => l.Text = text(w));
        return l;
    }

    /// <summary>What is being built here and what is queued behind it, each with a button to call it off.</summary>
    private Control BuildQueue(World w, Body b)
    {
        var col = Column(4);
        col.AddChild(Ui.Label("Building here", 16));
        foreach (var p in b.Pieces.Where(p => !p.Built).OrderBy(p => p.Waiting).ThenBy(p => p.Id))
        {
            var data = Planets.BuildingKind(p.Kind);
            var row = Row(8);
            var name = Ui.SmallButton(data?.Name ?? p.Kind, () => _selectedPiece = p.Id, "Show it on the board");
            name.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(name);
            var state = LiveWrap(w2 => p.Waiting ? $"Queued: {Colonies.QueuedWhy(w2, b, p)} ({data?.Cost ?? 0:0} Industry when it starts)"
                : $"{p.HoursLeft / 24:0.#} days left" + (Colonies.MaterialShort(b, p) is string waits ? $" · WAITING: {waits}" : ""), 13);
            row.AddChild(state);
            row.AddChild(Ui.SmallButton("✕", () =>
            {
                _screen.IssueNow(new DemolishCommand(Side.Player, b.Id, p.Kind, p.Id), "call off");
                if (_selectedPiece == p.Id) _selectedPiece = null;
            }, p.Waiting ? "Take it out of the queue (nothing was paid yet)" : "Stop building it (no refund)"));
            col.AddChild(row);
        }
        return Box(col, 10);
    }

    private Control PieceInfo(World w, Body b, Piece p)
    {
        var data = Planets.BuildingKind(p.Kind)!;
        var col = Column(4);
        col.AddChild(Ui.Label(data.Name, 16, BoardView.AreaColors.GetValueOrDefault(data.Area, Ui.Text)));
        col.AddChild(Wrap(data.Effect, 13, Ui.Dim));
        col.AddChild(LiveWrap(w2 => p.Waiting ? $"Queued: {Colonies.QueuedWhy(w2, b, p)}"
            : !p.Built ? $"Being built: {p.HoursLeft / 24:0.#} days left" + (Colonies.MaterialShort(b, p) is string waits ? $" · WAITING: {waits}" : "")
            : data.Battery > 0 ? $"Holds {Grid.RoomOf(p, data):0} power-hours (planet: {b.BatteryCharge:0}/{b.BatteryRoom:0} stored{(b.FromBattery > 0 ? $", giving {b.FromBattery:0.#} an hour" : "")}) · condition {p.Condition:P0}"
            : $"Output {Colonies.Output(w2, b, p):P0} · staff {p.Staff:P0} · {(p.PowerUse == PowerUse.Off && data.Power > 0 ? "SWITCHED OFF" : p.Powered ? (p.PowerShare < 0.999 ? $"BROWNOUT {p.PowerShare:P0} power" : "powered") : "NO POWER (shed)")}{(p.PowerUse is PowerUse.High or PowerUse.Low ? $" ({Colonies.PowerUseName(p.PowerUse).ToLowerInvariant()} power priority)" : "")} · condition {p.Condition:P0}"
              + (p.Level > 0 ? $" · level {p.Level + 1}" : "") + (p.RefitHoursLeft > 0 ? $" · refit {p.RefitHoursLeft / 24:0.#} d" : "")
              + (Colonies.BonusFor(b, p) > 0 ? $" · bonus +{Colonies.BonusFor(b, p):P0}" : "")
              + (Colonies.DepositUnder(b, p) is Deposit d ? $" · digs {Colonies.GoodName(d.Mineral).ToLowerInvariant()}{(d.Rich ? " (rich)" : "")}" : ""), 13));
        if (Munitions.IsPlant(p))
        {
            col.AddChild(LiveWrap(w2 => Munitions.Describe(w2, b, p), 13));
            var modeRow = Row(6);
            modeRow.AddChild(Ui.Label("Makes", 13, Ui.Dim));
            modeRow.AddChild(Pick(Enum.GetValues<PlantMode>().Select(m => (m.ToString(), Munitions.ModeName(m))).ToList(), p.PlantMode.ToString(),
                k => _screen.IssueNow(new SetPlantModeCommand(Side.Player, b.Id, p.Id, Enum.Parse<PlantMode>(k)), "plant mode"),
                "Basic only: plain missiles and slugs, 30 % more of them. Best: refits for your latest missile brain and Railgun Mk III slugs, needing 25 % more staff and power per upgrade"));
            col.AddChild(modeRow);
        }
        if (data.Power > 0)
        {
            var row = Row(6);
            row.AddChild(Ui.Label("Power", 13, Ui.Dim));
            row.AddChild(Pick(Enum.GetValues<PowerUse>().Select(u => (u.ToString(), Colonies.PowerUseName(u))).ToList(), p.PowerUse.ToString(),
                k => _screen.IssueNow(new SetPowerUseCommand(Side.Player, b.Id, p.Id, Enum.Parse<PowerUse>(k)), "power setting"),
                "When power is short, High buildings are powered first and Low ones shed first (the next in line may run on a brownout, at part power); batteries cover a gap until they run flat. Off switches it off and frees its staff"));
            col.AddChild(row);
        }
        if (p.Kind != Colonies.Shipyard)
            col.AddChild(Ui.SmallButton(p.Built ? "Take down" : p.Waiting ? "Take out of the queue" : "Call off", () =>
            {
                _screen.IssueNow(new DemolishCommand(Side.Player, b.Id, p.Kind, p.Id), "demolish");
                _selectedPiece = null;
            }, p.Built ? "Take it down to free its tiles and people (no refund)" : "Stop building it (no refund)"));
        return Box(col, 10);
    }

    private Control BuildList(World w, Body b)
    {
        var col = Column(6);
        col.CustomMinimumSize = new Vector2(290, 0);
        col.AddChild(Ui.Label("Build", 18));
        var filters = Row(6);
        filters.AddChild(Pick(new[] { ("", "All areas") }.Concat(Colonies.Areas.Where(a => a != "shipyard").Select(a => (a, Colonies.AreaName(a)))).ToList(), _area, k => _area = k, "Show one area"));
        filters.AddChild(Pick(BuildSorts.Select(s => (s, "By " + s)).ToList(), _buildSort, k => _buildSort = k, "Order of the list"));
        col.AddChild(filters);
        // This planet's own automation level for buildings (or the Empire screen's level for the area).
        var own = Economy.Of(w, Side.Player).AutoOverrides.Find(o => o.Area == AutoArea.Building && o.BodyId == b.Id && o.FleetId == null);
        var autoRow = Row(6);
        autoRow.AddChild(Ui.Label("Automation", 13, Ui.Dim));
        var levels = new List<(string, string)> { ("", $"As the empire ({Stewards.LevelFor(w, Side.Player, AutoArea.Building)})") }
            .Concat(Enum.GetValues<AutoLevel>().Select(l => (l.ToString(), l.ToString()))).ToList();
        autoRow.AddChild(Pick(levels, own?.Level.ToString() ?? "", k => _screen.IssueNow(new SetAutoLevelCommand(Side.Player, AutoArea.Building,
            k == "" ? null : Enum.Parse<AutoLevel>(k), b.Id), "planet automation"),
            "Auto lays down this planet's buildings, Advise suggests them under Needs you, Manual leaves them to you"));
        col.AddChild(autoRow);
        var only = new CheckBox { Text = "Only what I can build here", ButtonPressed = _onlyHere, FocusMode = FocusModeEnum.None };
        only.AddThemeFontSizeOverride("font_size", 13);
        only.Toggled += on => _onlyHere = on;
        col.AddChild(only);

        var kinds = Colonies.Kinds.Where(k => _area == "" || k.Area == _area);
        kinds = _buildSort switch
        {
            "name" => kinds.OrderBy(k => k.Name),
            "cost" => kinds.OrderBy(k => k.Cost),
            "staff" => kinds.OrderBy(k => k.StaffK),
            "power" => kinds.OrderBy(k => k.Power),
            _ => kinds.OrderBy(k => Array.IndexOf(Colonies.Areas, k.Area)).ThenBy(k => k.Name),
        };
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        KeepScroll(scroll, $"{b.Id}:build");
        var list = Column(4);
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var busy = b.Pieces.Any(p => !p.Built);
        foreach (var k in kinds)
        {
            // Buildings go up one at a time, but more can be queued behind, even before the Industry is there.
            var why = Colonies.CanPlace(w, Side.Player, b.Id, k.Id, null, null, false, queue: true);
            var queues = why == null && (busy || Economy.Of(w, Side.Player).Industry < k.Cost);
            if (_onlyHere && why != null) continue;
            var power = k.Power < 0 ? $"+{-k.Power:0} power" : k.Power > 0 ? $"{k.Power:0} power" : "no power";
            var kind = k;
            var note = why != null ? why + "." : queues ? "Queues behind what is under way here, paid when it starts." : "";
            var button = new TipButton
            {
                Text = $"{k.Name}  {k.W}×{k.H}\n{k.Cost:0} Ind · {Races.BuildHours(w, b, k) / 24:0} d · {k.StaffK:0.#}k staff · {power}"
                       + (k.Materials is { Count: > 0 } mats ? "\n" + string.Join(", ", mats.Select(m => $"{m.Value:0} {Colonies.GoodName(m.Key).ToLowerInvariant()}")) : ""),
                Alignment = HorizontalAlignment.Left, FocusMode = FocusModeEnum.None, ToggleMode = true, ButtonPressed = _placing == k.Id,
                Disabled = why != null, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                TooltipText = k.Name,
                Tip = () => BuildingCard(b, kind, note, why != null),
            };
            _buildButtons.Add((button, kind));
            button.AddThemeFontSizeOverride("font_size", 13);
            button.AddThemeColorOverride("font_color", BoardView.AreaColors.GetValueOrDefault(k.Area, Ui.Text));
            var id = k.Id;
            button.Pressed += () => Main.Instance.RunGuarded("pick building", () =>
            {
                _placing = _placing == id ? null : id;
                _selectedPiece = null;
            });
            list.AddChild(button);
        }
        scroll.AddChild(list);
        col.AddChild(scroll);
        return Box(col, 10);
    }

    /// <summary>The hover card for a building in the build list: what it costs, takes, makes and uses, and what it does.</summary>
    private static Control BuildingCard(Body b, BuildingData k, string note, bool blocked)
    {
        string Goods(Dictionary<string, double> d, string per) => string.Join(", ", d.Select(x => $"{x.Value:0.##} {Colonies.GoodName(x.Key).ToLowerInvariant()}{per}"));
        var makes = new List<string>();
        if (k.Makes is { Count: > 0 }) makes.Add(Goods(k.Makes, " a day"));
        if (k.Food > 0) makes.Add($"{k.Food * Planets.D.FarmFoodPerDay:0.##} food a day" + (k.AnyAir || k.Terrain == "ocean" ? "" : " (less where little grows)"));
        if (k.Mines > 0) makes.Add($"{k.Mines:0.##} a day of the mineral under it (more if rich)");
        if (k.Industry > 0) makes.Add($"{k.Industry:0.##} Industry a day");
        if (k.Research > 0) makes.Add("research");
        if (k.HousingK > 0) makes.Add($"homes for {k.HousingK:0.#}k people");
        var power = k.Power < 0 ? $"makes {-k.Power:0} power" : k.Power > 0 ? $"needs {k.Power:0} power" : "needs no power";
        var lines = new List<(string, Color?)>
        {
            ($"{Colonies.AreaName(k.Area)} · {k.W}×{k.H} tiles on {TerrainName(k.Terrain)}", Ui.Dim),
            ($"Cost {k.Cost:0} Industry · {k.Days:0} days to build" + (k.Materials is { Count: > 0 } mats ? $" · materials {Goods(mats, "")}" : ""), null),
            ($"Staff {k.StaffK:0.#}k people · {power}", null),
            (makes.Count > 0 ? "Makes " + string.Join("; ", makes) : "", new Color("8fe0a0")),
            ((k.Uses is { Count: > 0 } uses ? "Uses " + Goods(uses, " a day") + " · " : "") + $"upkeep {k.Upkeep:0.##} spare parts a day", new Color("f2c14e")),
            (k.Bonus is { Count: > 0 } bonus ? "Bonus beside " + string.Join(", ", bonus.Select(x => $"a {Planets.BuildingKind(x.Next)?.Name.ToLowerInvariant()} (+{x.Share:P0})")) : "", null),
            (k.Effect, Ui.Text),
            (note, blocked ? Ui.Rival : Ui.Dim),
        };
        return Tips.Card(k.Name, BoardView.AreaColors.GetValueOrDefault(k.Area, Ui.Text), lines);
    }

    private static string TerrainName(string t) => t switch
    {
        "ocean" => "ocean", "ice" => "ice", "oceanOrIce" => "ocean or ice", "landOrIce" => "land or ice", "deposit" => "a mineral deposit", _ => "land",
    };

    private Control Priorities(World w, Body b)
    {
        var col = Column(4);
        var head = Row(8);
        head.AddChild(Ui.Label("Priorities", 16));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var presets = Colonies.PresetNames.Select(p => (p, Colonies.PresetName(p))).Append(("custom", "Custom")).ToList();
        head.AddChild(Pick(presets, b.Preset, k =>
        {
            if (k == "custom") _screen.IssueNow(new SetPriorityCommand(Side.Player, b.Id, "custom", Colonies.Priority(b)), "priorities");
            else _screen.IssueNow(new SetPriorityCommand(Side.Player, b.Id, k), "priorities");
        }, "Who gets people and power first when there aren't enough"));
        col.AddChild(head);
        col.AddChild(Wrap("Top areas get people and power first; the lowest stop first when short.", 12, Ui.Dim));
        var order = Colonies.Priority(b);
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 10);
        foreach (var h in new[] { "Area", "", "", "Staff", "Kept" }) grid.AddChild(Ui.Label(h, 12, Ui.Dim));
        for (var i = 0; i < order.Count; i++)
        {
            var area = order[i];
            var index = i;
            grid.AddChild(Ui.Label($"{i + 1}. {Colonies.AreaName(area)}", 13, BoardView.AreaColors.GetValueOrDefault(area, Ui.Text)));
            grid.AddChild(Move(b, order, index, -1, "▲"));
            grid.AddChild(Move(b, order, index, 1, "▼"));
            grid.AddChild(Live(_ => Colonies.Built(b).Where(p => Planets.BuildingKind(p.Kind)?.Area == area).ToList() is { Count: > 0 } ps ? $"{ps.Average(p => p.Staff):P0}" : "—", 13));
            grid.AddChild(Live(_ => Colonies.Built(b).Where(p => Planets.BuildingKind(p.Kind)?.Area == area).ToList() is { Count: > 0 } ps ? $"{ps.Count(p => p.Powered) / (double)ps.Count:P0}" : "—", 13));
        }
        col.AddChild(grid);
        return Box(col, 10);
    }

    private Button Move(Body b, List<string> order, int index, int by, string arrow)
    {
        var target = index + by;
        var button = Ui.SmallButton(arrow, () =>
        {
            var next = new List<string>(order);
            (next[index], next[target]) = (next[target], next[index]);
            _screen.IssueNow(new SetPriorityCommand(Side.Player, b.Id, "custom", next), "priorities");
        }, by < 0 ? "Move up: served sooner" : "Move down: served later");
        button.CustomMinimumSize = new Vector2(30, 26);
        button.Disabled = target < 0 || target >= order.Count;
        return button;
    }

    private Control Storage(World w, Body b)
    {
        var col = Column(4);
        col.AddChild(Ui.Label("Storage and upkeep", 16));
        var head = Row(6);
        head.AddChild(Ui.Label("Siege stock", 13, Ui.Dim));
        foreach (var days in new[] { 30, 60, 90 })
        {
            var button = Ui.SmallButton($"{days} d", () => _screen.IssueNow(new SetSiegeStockCommand(Side.Player, b.Id, days), "siege stock"),
                $"Keep {days} days of what this planet uses in store; Auto transport tops it up, and only the rest is spare");
            button.Modulate = b.SiegeDays == days ? Ui.Player : Colors.White;
            head.AddChild(button);
        }
        col.AddChild(head);
        col.AddChild(LiveWrap(_ => Colonies.HoldsOutDays(b) is double d ? $"Cut off, it holds out {d:0} days · store {Colonies.StoreDaysOf(b):0} days" : $"Cut off, it holds out as long as it likes · store {Colonies.StoreDaysOf(b):0} days", 13));
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 16);
        foreach (var h in new[] { "Good", "Made /day", "Used /day", "Held", "Spare" }) grid.AddChild(Ui.Label(h, 12, Ui.Dim));
        grid.AddChild(GoodName(b, "food", "Food"));
        grid.AddChild(Live(_ => $"{Planets.FoodMadePerDay(b):0.##}", 13));
        grid.AddChild(Live(_ => $"{Planets.FoodNeedPerDay(b):0.##}", 13));
        grid.AddChild(Live(_ => $"{b.FoodStock:0}", 13));
        grid.AddChild(Ui.Label("shared", 13, Ui.Dim));
        foreach (var g in Colonies.Goods)
        {
            var id = g.Id;
            grid.AddChild(GoodName(b, id, g.Name));
            grid.AddChild(Live(_ => Colonies.MadePerDay(b, id) is var v && v > 0.001 ? $"{v:0.##}" : "—", 13));
            grid.AddChild(Live(_ => Colonies.UsePerDay(b, id) is var v && v > 0.001 ? $"{v:0.##}" : "—", 13));
            grid.AddChild(Live(_ => $"{Colonies.Held(b, id):0}", 13));
            grid.AddChild(Live(_ => $"{Colonies.SpareOf(b, id):0}", 13));
        }
        col.AddChild(grid);
        col.AddChild(Wrap("Without spare parts buildings wear and work less; workshops make them from iron.", 12, Ui.Dim));
        return Box(col, 10);
    }

    private Control Defence(World w, Body b)
    {
        var col = Column(4);
        col.AddChild(Ui.Label("Defence", 16));
        col.AddChild(LiveWrap(_ => $"Garrison {b.Garrison:0.#}k" + (Economy.Blockaded(w, b) ? " · BLOCKADED: no building or refits, nothing comes in" : ""), 13));
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", 6);
        foreach (var k in Colonies.Kinds.Where(k => k.Area == "defence"))
        {
            var id = k.Id;
            var count = Colonies.Built(b).Count(p => p.Kind == id);
            var why = Colonies.CanPlace(w, Side.Player, b.Id, id, null, null, false);
            var button = Ui.SmallButton($"{k.Name} ×{count}", () => { _placing = id; _selectedPiece = null; },
                (why != null ? why + ". " : "Click to place one. ") + k.Effect);
            button.Disabled = why != null && !why.StartsWith("Needs ");
            button.AddThemeFontSizeOverride("font_size", 13);
            row.AddChild(button);
        }
        col.AddChild(row);
        var box = Box(col, 10);
        box.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return box;
    }

    /// <summary>
    /// Suggested loads both ways (Colonies.Ideas, the same rules Auto transport uses), nearest first, each with
    /// Send once and Set up run, or a note that Auto is already on it. Rebuilt every few game hours.
    /// </summary>
    private Control Ideas(Body b)
    {
        var box = Column(3);
        var shown = "";
        _live.Add(w2 =>
        {
            var stamp = $"{(long)(w2.Hour / 6)}|{w2.Shipments.Count}|{w2.FixedRuns.Count}|{Economy.Of(w2, Side.Player).AutoTransport}";
            if (stamp == shown || Ui.MouseHeld) return;
            shown = stamp;
            foreach (var c in box.GetChildren()) c.QueueFree();
            var ideas = Colonies.Ideas(w2, Side.Player, b);
            box.AddChild(Ui.Label("Suggested loads", 13, Ui.Dim));
            if (ideas.Count == 0) { box.AddChild(Wrap("Nothing needed: no shortages here, and no spare another planet is short of.", 12, Ui.Dim)); return; }
            foreach (var idea in ideas)
            {
                var row = Row(6);
                var good = Colonies.GoodName(idea.Good).ToLowerInvariant();
                var risk = idea.Risk switch { RouteRisk.High => "HIGH risk", RouteRisk.Medium => "some risk", _ => "low risk" };
                var text = Wrap($"{(idea.Bring ? "Bring" : "Send")} {idea.Amount:0} {good} {(idea.Bring ? "from" : "to")} {idea.OtherName} · {idea.TripDays:0.#} d · {risk}", 12,
                    idea.Risk == RouteRisk.High ? Ui.Rival : Ui.Text);
                text.MouseFilter = MouseFilterEnum.Pass;
                text.TooltipText = $"Route: {idea.RiskWhy}";
                row.AddChild(text);
                var from = idea.Bring ? idea.OtherId : b.Id;
                var to = idea.Bring ? b.Id : idea.OtherId;
                if (idea.AutoDoing) row.AddChild(Ui.Label("Auto is already doing this", 12, new Color("8fe0a0")));
                else
                {
                    var (g, amount) = (idea.Good, idea.Amount);
                    row.AddChild(Ui.SmallButton("Send once", () => _screen.IssueNow(new ShipGoodsCommand(Side.Player, from, to, g, amount), "send goods"),
                        "The nearest free freighter carries one load now"));
                    row.AddChild(Ui.SmallButton("Set up run", () => _screen.IssueNow(new FixedRunCommand(Side.Player, from, to, g, 20), "fixed run"),
                        "A fixed run: every 20 days a free freighter carries what can be spared (stop it with ✕ below)"));
                }
                box.AddChild(row);
            }
        });
        return box;
    }

    private Control Transport(World w, Body b)
    {
        var me = Economy.Of(w, Side.Player);
        var col = Column(4);
        var head = Row(8);
        head.AddChild(Ui.Label("Transport", 16));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var auto = new CheckButton { Text = "Auto first", ButtonPressed = me.AutoTransport, FocusMode = FocusModeEnum.None,
            TooltipText = "Free freighters carry goods to planets that run short, from the planet with the most to spare (all your planets)" };
        auto.AddThemeFontSizeOverride("font_size", 13);
        auto.Toggled += on => Main.Instance.RunGuarded("auto transport", () => _screen.IssueNow(new SetAutoTransportCommand(Side.Player, on), "auto transport"));
        head.AddChild(auto);
        col.AddChild(head);
        col.AddChild(LiveWrap(w2 =>
        {
            var free = Colonies.FreeHaulers(w2, Side.Player).Count();
            var busy = w2.Shipments.Count(s => s.Side == Side.Player);
            return $"Freighters: {free} free, {busy} carrying · lost with cargo: {Economy.Of(w2, Side.Player).CargoLost}";
        }, 13));
        col.AddChild(LiveWrap(w2 => string.Join("\n", w2.Shipments.Where(s => s.Side == Side.Player && (s.FromBodyId == b.Id || s.ToBodyId == b.Id))
            .Select(s => $"{(s.ToBodyId == b.Id ? "Coming from" : "Going to")} {w2.FindBody(s.ToBodyId == b.Id ? s.FromBodyId : s.ToBodyId)?.Name}: "
                         + string.Join(", ", s.Cargo.Select(c => $"{c.Value:0} {GoodShort(c.Key)}")) + (s.Loaded ? "" : " (loading)"))) is { Length: > 0 } t ? t : "Nothing on the way", 13));

        col.AddChild(Ideas(b));

        foreach (var run in w.FixedRuns.Where(r => r.Side == Side.Player && (r.FromBodyId == b.Id || r.ToBodyId == b.Id)))
        {
            var row = Row(6);
            var other = w.FindBody(run.FromBodyId == b.Id ? run.ToBodyId : run.FromBodyId)?.Name ?? "?";
            row.AddChild(Wrap($"Fixed run {(run.FromBodyId == b.Id ? "to" : "from")} {other}: {(run.Good == "" ? "any spare goods" : Colonies.GoodName(run.Good).ToLowerInvariant())} every {run.EveryDays:0} days"
                                  + (run.EscortFleetId is int e && w.FindFleet(e) is Fleet ef ? $", escorted by {ef.Name}" : ""), 13));
            var id = run.Id;
            row.AddChild(Ui.SmallButton("✕", () => _screen.IssueNow(new FixedRunCommand(Side.Player, 0, 0, "", 0, null, id), "fixed run"), "Stop this run"));
            col.AddChild(row);
        }

        // + Fixed run from here.
        var targets = w.Bodies.Where(x => x.Owner == Side.Player && x.Id != b.Id).OrderBy(x => x.Name).ToList();
        if (targets.Count > 0)
        {
            if (targets.All(t => t.Id != _runTo)) _runTo = targets[0].Id;
            var add = new HFlowContainer();
            add.AddThemeConstantOverride("h_separation", 6);
            add.AddThemeConstantOverride("v_separation", 6);
            add.AddChild(Ui.Label("+ Fixed run to", 13));
            add.AddChild(Pick(targets.Select(t => (t.Id.ToString(), t.Name)).ToList(), _runTo.ToString(), k => _runTo = int.Parse(k), "Where the goods go"));
            add.AddChild(Pick(new[] { ("", "Any spare") }.Concat(Colonies.Goods.Select(g => (g.Id, g.Name))).ToList(), _runGood, k => _runGood = k, "What to carry"));
            add.AddChild(Pick(new[] { 10, 20, 30, 60 }.Select(d => (d.ToString(), $"every {d} d")).ToList(), _runEvery.ToString(), k => _runEvery = int.Parse(k), "How often"));
            var escorts = w.Fleets.Where(f => f.Side == Side.Player && Combat.Armed(w, f)).OrderBy(f => f.Id).ToList();
            add.AddChild(Pick(new[] { ("-1", "No escort") }.Concat(escorts.Select(f => (f.Id.ToString(), $"Escort: {f.Name}"))).ToList(), _runEscort.ToString(), k => _runEscort = int.Parse(k), "A fleet that flies with the freighter"));
            add.AddChild(Ui.SmallButton("Add", () => _screen.IssueNow(new FixedRunCommand(Side.Player, b.Id, _runTo, _runGood, _runEvery, _runEscort >= 0 ? _runEscort : null), "fixed run"),
                "Every so often a free freighter carries what this planet can spare there; a freighter shot down loses its cargo"));
            col.AddChild(add);
        }
        else col.AddChild(Wrap("Fixed runs need a second planet of yours.", 12, Ui.Dim));
        return Box(col, 10);
    }
}
