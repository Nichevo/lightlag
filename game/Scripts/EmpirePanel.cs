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
        + "|" + (Economy.ShipyardOf(w, Side.Player) != null) + "|" + w.Winner + "|" + _screen.BuildForFleetId + "|" + _screen.BuildAtBodyId
        + "|" + string.Join(",", YardViews.Planets(w).Select(b => b.Id + ":" + Yards.At(w, Side.Player, b).Count()))
        + "|" + string.Join(",", w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Ship && !o.Paid).Select(o => $"{o.Id}{(o.YardPinned ? "@" + o.YardBodyId : "")}"))
        + "|" + string.Join(",", w.Fleets.Where(f => f.Side == Side.Player).Select(f => $"{f.Id}:{f.Name}"));

    /// <summary>The Supply view is wide: the chain view and routes on the left, problems and stock targets on the right.</summary>
    private void RebuildSupply(Simulation sim)
    {
        Ui.ClearChildren(_box);
        _live.Clear();
        _box.CustomMinimumSize = new Vector2(SupplyTab.Width, 0);
        Header("Supply", "The supply chain: where goods come from and go, routes and their risk, problems with fixes, stock targets and siege mode", "G");
        _box.AddChild(new PotStrip(Pot.Transport, SupplyTab.Width));
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
        var yards = YardViews.Planets(w);
        Header(yards.Count > 1 ? $"Shipyards · {yards.Count}" : "Shipyard", "Build ships, put the queue in order, and pick the yard each one is built at and the fleet it joins", "V");
        if (yards.Count == 0) Live(13, Ui.Dim, _ => "Destroyed");
        // The budget lines that pay for the queue: warships from Ships, freighters and settlers from Transport.
        _box.AddChild(new PotStrip(Pot.Ships, Width));
        _box.AddChild(new PotStrip(Pot.Transport, Width));
        YardList(w, yards);
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
        if (Economy.ShipyardOf(w, Side.Player) != null && w.Winner == null)
        {
            DefaultJoinRow(w);
            if (yards.Count > 1) DefaultYardRow(w);
        }
        var ships = Budget.ShipQueue(w, Side.Player);
        if (ships.Count > 0) Live(12, Ui.Dim, w2 => QueueLine(w2));
        if (ships.Count == 0) Live(13, Ui.Dim, _ => "Nothing being built");
        var shipQueue = ships.Where(o => !o.Paid).ToList();
        for (var i = 0; i < ships.Count; i++)
        {
            OrderRow(ships[i], i == 0, shipQueue.IndexOf(ships[i]), shipQueue.Count);
            if (ships[i].SettleBodyId == null) JoinRow(w, ships[i]);
            if (ships[i].SettleBodyId == null && !ships[i].Paid && yards.Count > 1) YardRow(w, ships[i]);
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

    /// <summary>
    /// The yards: one line per planet with a berth (★ the home yard), its berths and what each is building. The
    /// name opens the planet screen, where the yard is run; with several, a stop after the home yard fell shows here.
    /// </summary>
    private void YardList(World w, List<Body> yards)
    {
        if (yards.Count == 0) return;
        Live(12, Ui.Warn, w2 => Yards.Stopped(w2, Side.Player) is string why ? "ALL YARDS STOPPED: " + why : "");
        foreach (var b in yards)
        {
            var row = Row();
            var id = b.Id;
            var name = Ui.SmallButton((Yards.Home(w, Side.Player) == b ? "★ " : "") + b.Name, () => { _screen.ShowEmpire = false; _screen.OpenPlanet(id); },
                $"Open {b.Name}'s planet screen: its berths, what they need, its station and the upgrade to a Shipyard" + (Yards.Home(w, Side.Player) == b ? ". ★ the home yard: your seat" : ""));
            name.CustomMinimumSize = new Vector2(110, 26);
            name.AddThemeFontSizeOverride("font_size", 13);
            name.ClipText = true;
            row.AddChild(name);
            var line = Ui.Label("", 12, Ui.Dim);
            line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            line.CustomMinimumSize = new Vector2(Width - 120, 0);
            row.AddChild(line);
            _live.Add((line, w2 => w2.FindBody(id) is Body x ? YardViews.PlanetLine(w2, x) : ""));
        }
    }

    /// <summary>"New ships built at": Auto, or a yard every new order is pinned to (each row can still be changed).</summary>
    private void DefaultYardRow(World w)
    {
        var row = Row();
        var label = Ui.Label("New ships built at", 13, Ui.Text);
        label.CustomMinimumSize = new Vector2(120, 0);
        row.AddChild(label);
        var current = _screen.BuildAtBodyId is int y && w.FindBody(y)?.Owner == Side.Player ? _screen.BuildAtBodyId : null;
        var pick = YardViews.YardPicker(w, null, current, current != null, id => _screen.BuildAtBodyId = id,
            "Auto: the free berth nearest the fleet it joins");
        pick.CustomMinimumSize = new Vector2(330, 26);
        row.AddChild(pick);
    }

    /// <summary>Where a queued ship will be built: Auto (and the yard it has picked) or a yard chosen by hand.</summary>
    private void YardRow(World w, WorkOrder order)
    {
        var row = Row();
        var label = Ui.Label("    at", 12, Ui.Dim);
        label.CustomMinimumSize = new Vector2(60, 0);
        row.AddChild(label);
        var auto = !order.YardPinned && order.YardBodyId is int at && w.FindBody(at) is Body chosen ? $"Auto: {chosen.Name}" : "Auto";
        var orderId = order.Id;
        var pick = YardViews.YardPicker(w, order.Key, order.YardBodyId, order.YardPinned,
            id => _screen.IssueNow(new SetOrderYardCommand(Side.Player, orderId, id), "choosing a yard"), auto);
        pick.CustomMinimumSize = new Vector2(270, 26);
        row.AddChild(pick);
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
        var where = o.YardBodyId is int y && w.FindBody(y) is Body yb && YardViews.Planets(w).Count > 1 ? $" at {yb.Name}{(o.Paid ? $" ({Yards.BerthName(o.Berth).ToLowerInvariant()})" : "")}" : "";
        if (o.Paid) return $"{name}{where}: {Format.Hours(o.HoursLeft)} left ({(1 - o.HoursLeft / o.TotalHours) * 100:0} %)";
        if (!first) return o.Waiting is string why && !why.Contains("Industry", StringComparison.Ordinal) ? $"{name}: queued, {why}" : $"{name}: queued";
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

/// <summary>
/// Early endings: the losing rival asks for terms. Accept and win now, or play on (the offer comes again later if you
/// still lead). Shown while the offer stands; the same choice is in the Needs you tray.
/// </summary>
public partial class TermsCard : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _text = null!;

    public TermsCard() : this(null!) { }
    public TermsCard(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 20);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(440, 0) };
        box.AddThemeConstantOverride("separation", 10);
        AddChild(box);
        var title = Ui.Label("Terms offered", 24, Ui.Player);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(title);
        _text = Ui.Label("", 16);
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        _text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _text.CustomMinimumSize = new Vector2(440, 0);
        box.AddChild(_text);
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 12);
        var accept = Ui.SmallButton("Accept: win now", () => _screen.AnswerTerms(true), "Take the surrender: you win the game now");
        accept.CustomMinimumSize = new Vector2(170, 40);
        row.AddChild(accept);
        var play = Ui.SmallButton("Play on", () => _screen.AnswerTerms(false),
            $"Turn the offer down and keep playing. If you still lead, it comes again in {Endings.D.OfferAgainDays:0} days");
        play.CustomMinimumSize = new Vector2(170, 40);
        row.AddChild(play);
        box.AddChild(row);
        box.AddChild(Ui.Label("Early endings can be switched off in the game menu (Esc).", 13, Ui.Dim));
    }

    public void Refresh(World w)
    {
        var who = Sides.Standing(w).Where(s => s != Side.Player).Select(s => Sides.Them(w, s)).ToList();
        var them = who.Count == 0 ? "the rival" : string.Join(" and ", who);
        _text.Text = $"You hold {w.Endings.TermsShare:P0} of the system, and {them} {(who.Count > 1 ? "ask" : "asks")} for terms.\n"
                     + "Accept and win now, or play on for a fuller victory.";
    }
}
