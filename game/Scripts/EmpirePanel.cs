using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The empire screen (E), in tabs: Overview (Industry, the shipyard and its build queue, research,
/// stations and claims), Needs you (what wants a look, with act, jump and dismiss), Automation (the nine
/// areas, each Auto, Advise or Manual, with overrides and the pull-back defaults), Resources (goods made,
/// used and held) and Buildings (the catalogue). Buttons issue commands; the panel rebuilds its buttons
/// only when what it shows changes shape, and updates the numbers every frame.
/// </summary>
public partial class EmpirePanel : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _box = null!;
    private string _shape = "";
    private readonly List<(Label Label, Func<World, string> Text)> _live = new();
    private Tab _tab = Tab.Overview;

    public enum Tab { Overview, Needs, War, Budget, Automation, Resources, Supply, Buildings }

    private SupplyTab? _supply;
    private SupplyTab SupplyView => _supply ??= new SupplyTab(_screen);

    /// <summary>Open on a tab (from the HUD's Needs you count, for example).</summary>
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
    private string Shape(World w) => _tab + "|" + Economy.Of(w, Side.Player).Needs.Count + Economy.Of(w, Side.Player).Needs.Any(n => n.Level == NeedLevel.Urgent) + "|" + _tab switch
    {
        Tab.Needs => EmpireTabs.NeedsShape(w),
        Tab.War => WarTab.Shape(w),
        Tab.Budget => BudgetTab.Shape(w),
        Tab.Automation => EmpireTabs.AutomationShape(w),
        Tab.Resources => string.Join(",", w.Bodies.Where(b => b.Owner == Side.Player).Select(b => b.Id)),
        Tab.Buildings => string.Join(",", Economy.Of(w, Side.Player).Researched),
        Tab.Supply => "",
        _ => OverviewShape(w),
    };

    private static string OverviewShape(World w) =>
        string.Join(",", w.Orders.Where(o => o.Side == Side.Player).Select(o => $"{o.Id}{(o.Paid ? "p" : "")}"))
        + "|" + string.Join(",", Economy.Of(w, Side.Player).Researched)
        + "|" + (Economy.ShipyardOf(w, Side.Player) != null) + "|" + w.Winner
        + "|" + string.Join(",", w.Orders.Where(o => o.Side == Side.Player).Select(o => o.JoinFleetId))
        + "|" + string.Join(",", w.Fleets.Where(f => f.Side == Side.Player).Select(f => $"{f.Id}:{f.Name}"))
        + "|" + string.Join(",", w.Bodies.Where(b => b.Owner == Side.Player).Select(b => b.Id))
        + "|" + string.Join(",", w.Stations.Where(st => st.Side == Side.Player).Select(st => $"{st.Id}{(w.Hour >= st.ReadyHour ? "r" : "")}"))
        + "|" + string.Join(",", w.Starbases.Select(st => $"{st.Id}{st.Side}{st.ClassId}"))
        + "|" + string.Join(",", w.Satellites.Where(s => s.Side == Side.Player && s.LostHour == null).Select(s => s.Id));

    /// <summary>The Supply tab is wide: the chain view and routes on the left, problems and stock targets on the right.</summary>
    private void RebuildSupply(Simulation sim)
    {
        foreach (var c in _box.GetChildren()) c.QueueFree();
        _live.Clear();
        _box.CustomMinimumSize = new Vector2(SupplyTab.Width, 0);
        Tabs(sim.World);
        SupplyView.Build(sim, _box, _live);
    }

    private void Rebuild(World w)
    {
        _box.CustomMinimumSize = new Vector2(Width, 0);
        foreach (var c in _box.GetChildren()) c.QueueFree();
        _live.Clear();
        Tabs(w);
        var tabs = new EmpireTabs(_screen, _box, _live);
        switch (_tab)
        {
            case Tab.Needs: tabs.Needs(w); return;
            case Tab.War: new WarTab(_screen, _box, _live).Build(w); return;
            case Tab.Budget: new BudgetTab(_screen, _box, _live).Build(w); return;
            case Tab.Automation: tabs.Automation(w); return;
            case Tab.Resources: tabs.Resources(w); return;
            case Tab.Buildings: tabs.Buildings(w); return;
        }

        Heading("Empire");
        Live(14, Ui.Text, w2 =>
        {
            var me = Economy.Of(w2, Side.Player);
            return $"Industry {me.Industry:0} · +{Economy.IncomePerDay(w2, Side.Player):0.#} a day";
        });
        Live(13, Ui.Dim, PlanetsLine);
        Live(13, Ui.Dim, RaceLine);

        // Shipyard and build queue.
        Heading("Shipyard");
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
                var b = Ui.SmallButton($"{cls.Name} · {cls.Cost:0}", () => _screen.Queue(OrderKind.Ship, id),
                    $"Build a {cls.Name}: {cls.Cost:0} Industry"
                    + (Races.BiomassFor(w, Side.Player, cls.Cost) is var bio && bio > 0 ? $" and {bio:0.#} biomass (food)" : "")
                    + $", {days:0.#} days. {cls.Role}");
                b.CustomMinimumSize = new Vector2(186, 26);
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
        var ships = w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Ship).ToList();
        if (ships.Count == 0) Live(13, Ui.Dim, _ => "Nothing being built");
        for (var i = 0; i < ships.Count; i++)
        {
            OrderRow(ships[i], i == 0);
            if (ships[i].SettleBodyId == null) JoinRow(w, ships[i]);
        }

        // Research: a summary here; teams, ideas and the discovery web are on the research screen (T).
        Heading("Research");
        var queued = w.Orders.Where(o => o.Side == Side.Player && o.Kind == OrderKind.Research).ToList();
        foreach (var o in queued)
        {
            var order = o;
            Live(12, Ui.Dim, w2 => $"{GameData.Research(order.Key).Name} · "
                + (order.TeamId is int t && Research.Team(w2, Side.Player, t) is ResearchTeam team ? team.Name : "no team")
                + $" · {Research.DaysUntilDone(w2, order):0.#} d");
        }
        if (queued.Count == 0) Live(12, Ui.Dim, _ => "No research queued");
        var open = Row();
        var rb = Ui.SmallButton("Open research (T)", () => _screen.ShowResearch = true, "Teams, ideas and the discovery web");
        rb.CustomMinimumSize = new Vector2(190, 28);
        open.AddChild(rb);

        // Stations on the bodies you hold.
        if (GameData.Economy.Stations is { Count: > 0 } kinds)
        {
            Heading("Stations");
            Live(12, Ui.Dim, w2 => string.Join("\n", kinds.Where(k => k.Bay == null).Select(k => $"{k.Name} ({k.Cost:0}, {k.Days:0} d): {k.Effect}"
                + (k.Id == Economy.Relay && !w.Light.On ? " (light lag only)" : "")
                + (k.Needs is string n && !Tech.Has(w2, Side.Player, n) ? $" · needs {GameData.Research(n).Name}" : ""))));
            foreach (var b in w.Bodies.Where(b => b.Owner == Side.Player).OrderBy(b => b.Id))
            {
                var row = Row();
                var name = Ui.Label(b.Name, 13, Ui.Text);
                name.CustomMinimumSize = new Vector2(110, 0);
                row.AddChild(name);
                // The station line: what is over it (build and upgrade it on the planet screen, or here while there is none).
                var lineLabel = Ui.Label("", 12, Ui.Dim);
                lineLabel.CustomMinimumSize = new Vector2(150, 0);
                var bid = b.Id;
                _live.Add((lineLabel, w2 => w2.FindBody(bid) is Body bb ? StationPanel.Short(w2, bb) : ""));
                row.AddChild(lineLabel);
                if (Starbases.At(w, b.Id) == null && w.Winner == null && Starbases.TopTier(w, Side.Player) > 0)
                {
                    var t1 = Starbases.TierAt(1)!;
                    var cls = GameData.ShipClass(t1.ClassId);
                    var why = Starbases.CanBuild(w, Side.Player, b.Id, t1.ClassId);
                    var build = Ui.SmallButton($"{cls.Name} · {cls.Cost:0}", () => _screen.IssueNow(new BuildStarbaseCommand(Side.Player, bid, t1.ClassId), "building a station"),
                        (why != null ? why + ". " : "") + $"Start the station line over {b.Name}: {cls.Cost:0} Industry, {t1.Days:0} days. {t1.Blurb} Upgrade it on the planet screen, or build a higher tier there.");
                    build.Disabled = why != null;
                    build.CustomMinimumSize = new Vector2(130, 26);
                    row.AddChild(build);
                }
                foreach (var k in kinds)
                {
                    if (k.Id == Economy.SensorStation && b.Shipyard) continue; // the shipyard planet already sees full range
                    var st = w.Stations.Find(x => x.Side == Side.Player && x.BodyId == b.Id && x.Kind == k.Id);
                    if (st != null)
                    {
                        var stId = st.Id;
                        var label = Ui.Label("", 12, Ui.Dim);
                        label.CustomMinimumSize = new Vector2(130, 0);
                        row.AddChild(label);
                        _live.Add((label, w2 => w2.Stations.Find(x => x.Id == stId) is Station s2
                            ? w2.Hour < s2.ReadyHour ? $"{k.Name}: {Format.Hours(s2.ReadyHour - w2.Hour)}"
                            : k.Armour > 0 ? $"{k.Name} ✓ armour {100 * s2.Armour / k.Armour:0} %" + (s2.LastHitHour is double hit && w2.Hour - hit < 2 ? " UNDER FIRE" : "")
                            : $"{k.Name} ✓" : ""));
                        continue;
                    }
                    if (w.Winner != null || k.Bay != null) continue; // relays, sensor and laser stations are bays on the line now
                    // Only stations that can go here and are researched (the list above says what the rest need).
                    if (k.Needs is string needs && !Tech.Has(w, Side.Player, needs)) continue;
                    if (k.OnTypes is { Count: > 0 } types && !types.Contains(b.Type) || k.Ground && b.Board == null) continue;
                    var bodyId = b.Id;
                    var kind = k.Id;
                    var button = Ui.SmallButton($"{k.Name} · {k.Cost:0}", () => _screen.BuildStation(bodyId, kind),
                        $"Build a {k.Name.ToLowerInvariant()} at {b.Name}: {k.Cost:0} Industry, {k.Days:0} days. {k.Effect}. The order goes out at light speed.");
                    button.CustomMinimumSize = new Vector2(130, 26);
                    row.AddChild(button);
                }
            }
        }

        // Satellites: each one, where it is and how it is doing, with a button to scrap it; and what the lift to orbit costs.
        Heading("Satellites and lift");
        Live(12, Ui.Dim, w2 =>
        {
            var lines = new List<string>();
            var mine = w2.Satellites.Where(s => s.Side == Side.Player).OrderBy(s => s.Id).ToList();
            lines.Add(mine.Count == 0
                ? (Tech.Has(w2, Side.Player, Satellites.D.Needs) ? "No satellites. Launch one from a planet's panel (Satellite ▾)" : $"Satellites need {GameData.Research(Satellites.D.Needs).Name}")
                : $"{Satellites.Live(w2, Side.Player).Count()} of {Satellites.D.MaxPerSide} satellites:");
            lines.AddRange(mine.Select(s => "  " + Satellites.Describe(w2, s)));
            lines.Add($"Lift to orbit paid so far: {Economy.Of(w2, Side.Player).Totals.LiftSpent:0} Industry");
            return string.Join("\n", lines);
        });
        var scrap = Row();
        foreach (var s in w.Satellites.Where(s => s.Side == Side.Player && s.LostHour == null).OrderBy(s => s.Id))
        {
            var id = s.Id;
            var button = Ui.SmallButton($"Scrap {s.Name}", () => _screen.IssueNow(new ScrapSatelliteCommand(Side.Player, id), "scrapping a satellite"),
                $"Switch {s.Name} off and let it fall: frees its place under the limit of {Satellites.D.MaxPerSide}");
            scrap.AddChild(button);
        }

        // Claims under way.
        Heading("Claims");
        Live(13, Ui.Dim, ClaimsText);
    }

    /// <summary>The tab row along the top; the Needs you tab shows how many items wait (red with urgent ones).</summary>
    private void Tabs(World w)
    {
        // Wraps onto a second line rather than pushing tabs off a scaled screen.
        var row = new HFlowContainer { CustomMinimumSize = new Vector2(Width, 0) };
        row.AddThemeConstantOverride("h_separation", 3);
        row.AddThemeConstantOverride("v_separation", 3);
        _box.AddChild(row);
        foreach (var tab in Enum.GetValues<Tab>())
        {
            var t = tab;
            var needs = Economy.Of(w, Side.Player).Needs;
            var name = tab switch
            {
                Tab.Needs => $"Needs you ({needs.Count})",
                _ => tab.ToString(),
            };
            var b = Ui.SmallButton(name, () => Show(t), TabTip(tab));
            b.CustomMinimumSize = new Vector2(0, 28);
            b.AddThemeFontSizeOverride("font_size", 13);
            if (tab == _tab) b.Modulate = Ui.Player;
            else if (tab == Tab.Needs && needs.Any(n => n.Level == NeedLevel.Urgent)) b.Modulate = Ui.Danger;
            row.AddChild(b);
        }
        var research = Ui.SmallButton("Research ↗", () => _screen.ShowResearch = true, "Teams, ideas and the discovery web (T)");
        research.CustomMinimumSize = new Vector2(0, 28);
        research.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(research);
        var close = Ui.SmallButton("✕", () => _screen.ShowEmpire = false, "Close (E, or click the Needs you badge again)");
        close.CustomMinimumSize = new Vector2(28, 28);
        row.AddChild(close);
    }

    /// <summary>The tab showing now.</summary>
    public Tab Current => _tab;

    private static string TabTip(Tab tab) => tab switch
    {
        Tab.Needs => "What wants your look: troubles, things due soon, advice and news, with a button to act on each",
        Tab.War => "Boarding, ground fighting, clearing up after battles, crews and what prisoners told us",
        Tab.Budget => "The Industry budget: six pots with shares of each day's Industry, goals to save for, and the items each pot pays for in your order",
        Tab.Automation => "Hand each area to automation (Auto), have it suggest (Advise) or keep it yourself (Manual)",
        Tab.Resources => "Goods made, used and held across your planets",
        Tab.Supply => "The supply chain: where goods come from and go, routes and their risk, problems with fixes, stock targets and siege mode",
        Tab.Buildings => "Every building: what it costs, needs and does",
        _ => "Industry, the shipyard, research, stations and claims",
    };

    private void OrderRow(WorkOrder order, bool first)
    {
        var row = Row();
        var id = order.Id;
        var label = Ui.Label("", 13, first ? Ui.Text : Ui.Dim);
        label.CustomMinimumSize = new Vector2(330, 0);
        row.AddChild(label);
        _live.Add((label, w => w.Orders.Find(o => o.Id == id) is WorkOrder o ? OrderText(w, o, first) : ""));
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

    /// <summary>Your race and the rival's, and for a race that grows its ships, the biomass at the shipyard.</summary>
    private static string RaceLine(World w)
    {
        var me = Races.Of(w, Side.Player);
        var them = Sides.Enemies(w, Side.Player).Select(r => Races.Of(w, r).Name + (Sides.Out(w, r) ? " (out)" : "")).ToList();
        var line = them.Count > 1 ? $"You are {me.Name}; the rivals are {string.Join(" and ", them)}." : $"You are {me.Name}; the rival is {them[0]}.";
        if (me.Grown && Economy.ShipyardOf(w, Side.Player) is Body yard)
            line += yard.Hungry ? " Your planets are STARVING: the brood yard grows no ships until they are fed."
                : $" Biomass (food) in store at {yard.Name}: {Races.BiomassAt(w, yard):0.#}; each ship takes {me.BiomassPerIndustry * 100:0.#} per 100 Industry of its price.";
        return line;
    }

    private static string PlanetsLine(World w)
    {
        var d = Planets.D;
        var c = Planets.Control(w, Side.Player);
        var mine = Economy.Claimable(w).Count(b => b.Owner == Side.Player);
        var rivals = string.Join(", ", Sides.Enemies(w, Side.Player).Select(r =>
            $"{(Sides.Three(w) ? Races.Of(w, r).Name : "rival")} {Economy.Claimable(w).Count(b => b.Owner == r)} planets, {Planets.PopulationOf(w, r):0}k people"));
        var line = $"Control: you {c.Share:P0} (people {c.People:P0}, Industry {c.Industry:P0}, planets {c.Bodies:P0}, fleets {c.Fleets:P0}). "
                   + $"Held: you {mine} planets of {Economy.Claimable(w).Count()}, {Planets.PopulationOf(w, Side.Player):0}k people; {rivals}.\n"
                   + $"At {d.TipShare:P0} control a side has tipped the game: planets it blockades give up sooner (below happiness {d.SurrenderBelowTipped:0}, not {d.SurrenderBelow:0}). "
                   + $"Hold {d.TipShare:P0} for {d.WinAfterDays:0} days, or reach {d.WinShare:P0}, to win. Destroying the rival shipyard also wins (with two rivals, it puts that rival out; the last one standing wins).";
        foreach (var side in Sides.InPlay(w))
            if (Economy.Of(w, side).MajoritySinceHour is double since)
                line += $"\n{(side == Side.Player ? "You win" : "The rival wins")} on {GameTime.Format(since + d.WinAfterDays * 24)} if nothing changes.";
        return line;
    }

    private static string ClaimsText(World w)
    {
        var lines = w.Bodies.Where(b => b.ClaimSide != null).Select(b =>
            $"{b.Name}: {(b.ClaimSide == Side.Player ? "you are" : "the rival is")} claiming it, {b.ClaimHours / 24:0.#} of {Economy.ClaimHoursFor(b) / 24:0} days").ToList();
        var e = GameData.Economy;
        return lines.Count > 0 ? string.Join("\n", lines)
            : $"Hold orbit at a planet or moon, with no enemy in orbit, to claim it: {e.ClaimDays:0} days for an empty one, {(e.ClaimHeldDays > 0 ? e.ClaimHeldDays : e.ClaimDays):0} for one the rival holds.";
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
