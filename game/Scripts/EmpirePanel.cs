using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The drop-downs under the menu bar (the Empire menu is gone; its parts live where they are about):
/// Shipyard (V: build ships, the queue with its order and the fleet each ship joins), Budget (E: the
/// Industry budget), Supply (G: the supply chain, with the goods totals) and the Needs you list (the
/// badge, when the tray is off). Buttons issue commands; the panel rebuilds its buttons only when what
/// it shows changes shape, and updates the numbers every frame.
/// </summary>
public partial class EmpirePanel : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _box = null!;
    private string _shape = "";
    private readonly List<(Label Label, Func<World, string> Text)> _live = new();
    private Tab _tab = Tab.Shipyard;

    public enum Tab { Shipyard, Budget, Supply, Needs }

    private SupplyTab? _supply;
    private SupplyTab SupplyView => _supply ??= new SupplyTab(_screen);

    /// <summary>Open on a view (from the menu bar, a key or the Needs you badge).</summary>
    public void Show(Tab tab) { _tab = tab; _shape = ""; }

    public EmpirePanel() : this(null!) { }
    public EmpirePanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 14);
        _box = new VBoxContainer { CustomMinimumSize = new Vector2(Width, 0) };
        _box.AddThemeConstantOverride("separation", 4);
        // Scrolls when the panel would run off the bottom of the screen.
        _scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        _scroll.AddChild(_box);
        AddChild(_scroll);
    }

    private ScrollContainer _scroll = null!;

    /// <summary>As tall as the contents, but no taller than the screen allows.</summary>
    private void FitHeight()
    {
        var room = GetViewportRect().Size.Y - Position.Y - 150;
        var want = new Vector2(_box.GetCombinedMinimumSize().X + 12, Mathf.Min(_box.GetCombinedMinimumSize().Y, Mathf.Max(200, room)));
        if (_scroll.CustomMinimumSize == want) return;
        _scroll.CustomMinimumSize = want;
        Size = Vector2.Zero; // shrink back to the new minimum
    }

    public void Refresh(Simulation sim)
    {
        var w = sim.World;
        var shape = Shape(w) + (_tab == Tab.Supply ? SupplyView.Shape(sim) : "");
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            if (_tab == Tab.Supply) RebuildSupply(sim);
            else Rebuild(w);
        }
        if (_tab == Tab.Supply) SupplyView.Refresh(sim);
        foreach (var (label, text) in _live) label.Text = text(w);
        FitHeight();
    }

    private const float Width = 520;

    /// <summary>Everything that changes which buttons and rows exist.</summary>
    private string Shape(World w) => _tab + "|" + _tab switch
    {
        Tab.Needs => EmpireTabs.NeedsShape(w),
        Tab.Budget => BudgetTab.Shape(w),
        Tab.Supply => "",
        _ => ShipyardShape(w),
    };

    private string ShipyardShape(World w) =>
        string.Join(",", w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Ship).Select(o => $"{o.Id}{(o.Paid ? "p" : "")}{o.JoinFleetId}"))
        + "|" + string.Join(",", Budget.ShipQueue(w, Side.Player).Select(o => o.Id))
        + "|" + Economy.Of(w, Side.Player).Researched.Count
        + "|" + (Economy.ShipyardOf(w, Side.Player) != null) + "|" + w.Winner + "|" + _screen.BuildForFleetId
        + "|" + string.Join(",", w.Fleets.Where(f => f.Side == Side.Player).Select(f => $"{f.Id}:{f.Name}"));

    /// <summary>The Supply view is wide: the chain view and routes on the left, problems and stock targets on the right.</summary>
    private void RebuildSupply(Simulation sim)
    {
        Ui.ClearChildren(_box);
        _live.Clear();
        _box.CustomMinimumSize = new Vector2(SupplyTab.Width, 0);
        Header("Supply", "The supply chain: where goods come from and go, routes and their risk, problems with fixes, stock targets and siege mode", "G");
        new EmpireTabs(_screen, _box, _live).Goods(sim.World, SupplyTab.Width);
        SupplyView.Build(sim, _box, _live);
    }

    private void Rebuild(World w)
    {
        _box.CustomMinimumSize = new Vector2(Width, 0);
        Ui.ClearChildren(_box);
        _live.Clear();
        switch (_tab)
        {
            case Tab.Needs:
                Header("Needs you", "What wants your look, with a button to act on each", "");
                new EmpireTabs(_screen, _box, _live).Needs(w);
                return;
            case Tab.Budget:
                Header("Budget", "The Industry budget: six pots with shares of each day's Industry, goals to save for, and the items each pot pays for in your order", "E");
                new BudgetTab(_screen, _box, _live).Build(w);
                return;
            default:
                Shipyard(w);
                return;
        }
    }

    /// <summary>The view's name, a line of what it is, and a close button.</summary>
    private void Header(string title, string tip, string key)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var name = Ui.Label(title, 17, Ui.Player);
        name.TooltipText = tip;
        name.MouseFilter = MouseFilterEnum.Pass;
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(name);
        var close = Ui.SmallButton("✕", () => _screen.ShowEmpire = false, key.Length > 0 ? $"Close ({key} or Esc)" : "Close (Esc)");
        close.CustomMinimumSize = new Vector2(28, 28);
        row.AddChild(close);
        _box.AddChild(row);
    }

    /// <summary>The view showing now.</summary>
    public Tab Current => _tab;

    // ---- Shipyard -------------------------------------------------------------------------------

    private void Shipyard(World w)
    {
        Header("Shipyard", "Build ships, put the queue in order and pick the fleet each one joins", "V");
        Live(13, Ui.Dim, w2 => Economy.ShipyardOf(w2, Side.Player) is Body yard
            ? $"At {yard.Name} · armour {yard.ShipyardArmour / GameData.Economy.Shipyard.Armour * 100:0} %"
              + (yard.LastBombardedHour is double h && w2.Hour - h <= 24 ? " · UNDER ATTACK" : "")
            : "Destroyed");
        if (Economy.ShipyardOf(w, Side.Player) != null && w.Winner == null)
        {
            // Two compact columns, so the list stays inside the panel's width.
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 4);
            grid.AddThemeConstantOverride("v_separation", 4);
            _box.AddChild(grid);
            foreach (var cls in GameData.ShipClasses.Values)
            {
                if (cls.Id == Planets.SettlerClass) continue; // built by a settle order on a planet
                var id = cls.Id;
                var days = cls.Cost / GameData.Economy.BuildIndustryPerDay;
                var b = Ui.SmallButton($"{cls.Name} · {cls.Cost:0} · {days:0.#} d", () => _screen.Queue(OrderKind.Ship, id),
                    $"Build a {cls.Name}: {cls.Cost:0} Industry"
                    + (Races.BiomassFor(w, Side.Player, cls.Cost) is var bio && bio > 0 ? $" and {bio:0.#} biomass (food)" : "")
                    + $", {days:0.#} days. {cls.Role}");
                b.CustomMinimumSize = new Vector2(250, 26);
                b.AddThemeFontSizeOverride("font_size", 13);
                b.ClipText = true;
                if (cls.Needs.Length > 0 && !Tech.Has(w, Side.Player, cls.Needs))
                {
                    b.Disabled = true;
                    b.TooltipText = $"{cls.Name}: needs {GameData.Research(cls.Needs).Name} (research screen, T). {cls.Role}";
                }
                grid.AddChild(b);
            }
        }
        if (Economy.ShipyardOf(w, Side.Player) != null && w.Winner == null) DefaultJoinRow(w);
        var ships = Budget.ShipQueue(w, Side.Player);
        if (ships.Count > 0) Live(12, Ui.Dim, w2 => QueueLine(w2));
        if (ships.Count == 0) Live(13, Ui.Dim, _ => "Nothing being built");
        var shipQueue = ships.Where(o => !o.Paid).ToList();
        for (var i = 0; i < ships.Count; i++)
        {
            OrderRow(ships[i], i == 0, shipQueue.IndexOf(ships[i]), shipQueue.Count);
            if (ships[i].SettleBodyId == null) JoinRow(w, ships[i]);
        }
        if (Races.Of(w, Side.Player).Grown) Live(12, Ui.Dim, BiomassLine);
        var links = Row();
        links.AddChild(Ui.SmallButton("Designs and refits (J)", () => { _screen.ShowEmpire = false; if (!_screen.ShowShips) _screen.ToggleShips(); }, "The ships screen: designs, variants and the class tree"));
        links.AddChild(Ui.SmallButton("Ships pot (E)", () => Show(Tab.Budget), "The Industry budget, where the Ships pot's share is set"));
    }

    /// <summary>"Queue: 3 ships, all done in about 19 d": the queue's length and when the last is done at the yard's pace.</summary>
    private static string QueueLine(World w)
    {
        var ships = Budget.ShipQueue(w, Side.Player);
        var hours = ships.Sum(o => o.Paid ? o.HoursLeft : o.TotalHours > 0 ? o.TotalHours : Economy.CostOf(o) / GameData.Economy.BuildIndustryPerDay * 24);
        return $"Queue: {ships.Count} ship{(ships.Count == 1 ? "" : "s")}, about {Format.Hours(hours)} of yard time";
    }

    /// <summary>"New ships join": the fleet each new order starts with (each row can still be changed).</summary>
    private void DefaultJoinRow(World w)
    {
        var row = Row();
        var label = Ui.Label("New ships join", 13, Ui.Text);
        label.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(label);
        var pick = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(330, 26), FitToLongestItem = false, ClipText = true,
            TooltipText = "The fleet new ships join when they are launched: set it once here instead of on every ship. A fleet away from the shipyard: the ship flies out to it" };
        pick.AddThemeFontSizeOverride("font_size", 12);
        var ids = new List<int?> { null };
        pick.AddItem("the fleet at the shipyard (or a new one)");
        var yard = Economy.ShipyardOf(w, Side.Player);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player))
        {
            ids.Add(f.Id);
            var where = yard != null && Economy.InOrbitAt(f, yard) && f.Flight == null ? "at the shipyard" : FleetPanel.Doing(w, f);
            pick.AddItem($"{f.Name} ({f.ShipIds.Count}) · {where}");
        }
        var current = _screen.BuildForFleetId is int j && w.FindFleet(j)?.Side == Side.Player ? _screen.BuildForFleetId : null;
        pick.Selected = Math.Max(0, ids.IndexOf(current));
        pick.ItemSelected += index => _screen.BuildForFleetId = ids[(int)index];
        row.AddChild(pick);
    }

    /// <summary>For a race that grows its ships: the biomass at the shipyard.</summary>
    private static string BiomassLine(World w)
    {
        var me = Races.Of(w, Side.Player);
        if (Economy.ShipyardOf(w, Side.Player) is not Body yard) return "";
        return yard.Hungry ? "Your planets are STARVING: the brood yard grows no ships until they are fed."
            : $"Biomass (food) in store at {yard.Name}: {Races.BiomassAt(w, yard):0.#}; each ship takes {me.BiomassPerIndustry * 100:0.#} per 100 Industry of its price.";
    }

    private void OrderRow(WorkOrder order, bool first, int place = -1, int queued = 0)
    {
        var row = Row();
        var id = order.Id;
        var label = Ui.Label("", 13, first ? Ui.Text : Ui.Dim);
        label.CustomMinimumSize = new Vector2(place >= 0 && queued > 1 ? 266 : 330, 0);
        row.AddChild(label);
        _live.Add((label, w => w.Orders.Find(o => o.Id == id) is WorkOrder o ? OrderText(w, o, first) : ""));
        // Ships not started yet can be moved up or down the queue (the one under way keeps going).
        if (place >= 0 && queued > 1)
        {
            var up = Ui.SmallButton("▲", () => _screen.IssueNow(new MoveShipOrderCommand(Side.Player, id, -1), "ship queue order"), "Build it sooner: swap with the ship above");
            up.Disabled = place == 0;
            up.CustomMinimumSize = new Vector2(30, 26);
            row.AddChild(up);
            var down = Ui.SmallButton("▼", () => _screen.IssueNow(new MoveShipOrderCommand(Side.Player, id, 1), "ship queue order"), "Build it later: swap with the ship below");
            down.Disabled = place == queued - 1;
            down.CustomMinimumSize = new Vector2(30, 26);
            row.AddChild(down);
        }
        var cancel = Ui.SmallButton("✕", () => _screen.CancelOrder(id), "Cancel (a started order gives its Industry back)");
        cancel.CustomMinimumSize = new Vector2(32, 26);
        row.AddChild(cancel);
    }

    /// <summary>Which fleet the ship being built will join: the one at the shipyard, or any of yours (it flies out to it).</summary>
    private void JoinRow(World w, WorkOrder order)
    {
        var row = Row();
        var label = Ui.Label("    joins", 12, Ui.Dim);
        label.CustomMinimumSize = new Vector2(60, 0);
        row.AddChild(label);
        var pick = new OptionButton { FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(270, 26), FitToLongestItem = false, ClipText = true,
            TooltipText = "The fleet this ship joins when it is launched. A fleet away from the shipyard: the ship flies out to it" };
        pick.AddThemeFontSizeOverride("font_size", 12);
        var ids = new List<int?> { null };
        pick.AddItem("the fleet at the shipyard (or a new one)");
        var yard = Economy.ShipyardOf(w, Side.Player);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player))
        {
            ids.Add(f.Id);
            var where = yard != null && Economy.InOrbitAt(f, yard) && f.Flight == null ? "at the shipyard" : FleetPanel.Doing(w, f);
            pick.AddItem($"{f.Name} ({f.ShipIds.Count}) · {where}");
        }
        pick.Selected = Math.Max(0, ids.IndexOf(order.JoinFleetId));
        var orderId = order.Id;
        pick.ItemSelected += index => _screen.SetOrderFleet(orderId, ids[(int)index]);
        row.AddChild(pick);
    }

    private static string OrderText(World w, WorkOrder o, bool first)
    {
        var name = Economy.NameOf(o) + (o.EscortFleetId is int e && w.FindFleet(e) is Fleet escort ? $" (escort {escort.Name})" : o.Auto ? " (Auto top-up)" : "");
        if (o.Blockaded) return $"{name}: BLOCKADED, an enemy fleet in orbit stops the shipyard";
        if (o.Paid && o.Waiting != null) return $"{name}: PAUSED, {o.Waiting}";
        if (o.Paid) return $"{name}: {Format.Hours(o.HoursLeft)} left ({(1 - o.HoursLeft / o.TotalHours) * 100:0} %)";
        if (!first) return $"{name}: queued";
        // Short of something other than Industry: materials, or biomass for a grown ship.
        if (o.Waiting != null && !o.Waiting.Contains("more Industry", StringComparison.Ordinal)) return $"{name}: WAITING, {o.Waiting}";
        var cost = Economy.CostOf(o);
        // With the Industry budget on, the order waits on its own pot, not the side's whole Industry (the old sum
        // gave a negative wait when the side held plenty but the pot did not).
        if (Budget.On(w))
            return Budget.ShortOf(w, Side.Player, Budget.PotOf(o), cost, "order:" + o.Id) is string why
                ? $"{name}: waiting for {cost:0} Industry: {why}" : $"{name}: starts next hour";
        var have = Economy.Of(w, Side.Player).Industry;
        var income = Economy.IncomePerDay(w, Side.Player);
        var wait = have >= cost ? ", starts next hour" : income > 0 ? $", about {Format.Hours((cost - have) / income * 24)}" : ", no Industry coming in";
        return $"{name}: waiting for {cost:0} Industry{wait}";
    }

    private void Heading(string text)
    {
        var l = Ui.Label(text, 15, Ui.Player);
        _box.AddChild(l);
    }

    private void Live(int size, Color color, Func<World, string> text)
    {
        var l = Ui.Label("", size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Width, 0);
        _box.AddChild(l);
        _live.Add((l, text));
    }

    private HBoxContainer Row()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _box.AddChild(row);
        return row;
    }
}

/// <summary>Shown once when the game is won or lost.</summary>
public partial class EndPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!;
    private Label _text = null!;

    public EndPanel() : this(null!) { }
    public EndPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 24);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(460, 0) };
        box.AddThemeConstantOverride("separation", 12);
        AddChild(box);
        _title = Ui.Label("", 30);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(_title);
        _text = Ui.Label("", 16);
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _text.CustomMinimumSize = new Vector2(460, 0);
        box.AddChild(_text);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 12);
        var look = Ui.SmallButton("Keep looking", () => _screen.CloseEnd(), "Close this and look around the map");
        look.CustomMinimumSize = new Vector2(150, 40);
        row.AddChild(look);
        var title = Ui.SmallButton("Title screen", () => Main.Instance.ShowTitle(), "Back to the title screen");
        title.CustomMinimumSize = new Vector2(150, 40);
        row.AddChild(title);
        box.AddChild(row);
    }

    public void Refresh(World w)
    {
        var won = w.Winner == Side.Player;
        _title.Text = won ? "Victory" : "Defeat";
        _title.AddThemeColorOverride("font_color", won ? Ui.Player : Ui.Rival);
        var who = won ? "You" : w.Winner is Side ws ? (Sides.Three(w) ? Races.Of(w, ws).Name : "The rival") : "The rival";
        var mine = w.Ships.Count(s => s.Side == Side.Player);
        var theirs = w.Ships.Count(s => s.Side is Side.Rival or Side.Third);
        _text.Text = $"{who} {w.WinReason} on {GameTime.Format(w.WinHour ?? w.Hour)}.\n"
                     + $"Ships left: you {mine}, rival {theirs}. Planets: you {w.Bodies.Count(b => b.Owner == Side.Player && b.Industry > 0)}, "
                     + $"rival {w.Bodies.Count(b => b.Owner is Side.Rival or Side.Third && b.Industry > 0)}.\n\n"
                     + Stats.Summary(w)
                     + string.Concat(w.Minds.Where(m => m.Side != Side.Player).Select(m => $"\n\n{(Sides.Three(w) ? Races.Of(w, m.Side).Name : "The rival")} was a {m.Personality} on {m.Difficulty}."));
    }
}
