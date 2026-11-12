using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The fleets screen (K), laid out like the planets screen: every fleet of yours down the left with its
/// role, what it is doing and its condition; the picked fleet on the right with its role and role rules,
/// standing orders, supplies and supply runs, distances to your planets, and every ship with its bars.
/// Ships can be ticked and moved to another fleet at the same place, or split off. The calls for help
/// list shows what planets, runs and fleets are calling about, who answers, and why the picked fleet
/// would or would not. Rebuilds only when its shape changes; numbers update live.
/// </summary>
public partial class FleetScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _root = null!;
    private string _shape = "";
    private readonly List<Action<World>> _live = new();
    private readonly HashSet<int> _ticked = new();
    private string _sort = "name";

    private static readonly string[] Sorts = { "name", "role", "ships", "condition", "distance" };

    public FleetScreen() : this(null!) { }
    public FleetScreen(GameScreen screen) => _screen = screen;

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
        var fleets = Mine(w);
        var picked = _screen.FleetScreenId is int id ? w.FindFleet(id) : null;
        if (picked == null || picked.Side != Side.Player) picked = fleets.FirstOrDefault();
        _screen.FleetScreenId = picked?.Id;
        _ticked.RemoveWhere(s => picked == null || !picked.ShipIds.Contains(s));
        var shape = $"{_sort}|{view}|{picked?.Id}|{string.Join(",", _ticked)}|"
                    + string.Join(";", fleets.Select(f => $"{f.Id}:{string.Join(",", f.ShipIds)}:{f.Role}:{f.RoleRules.Range}:{f.RoleRules.Odds}:{f.RoleRules.ReturnAfter}:{f.RoleRules.MinHealth}"))
                    + "|" + string.Join(",", w.HelpCalls.Where(c => c.Side == Side.Player).Select(c => $"{c.Id}{(c.Open ? "o" : "")}"))
                    + "|" + (picked != null ? string.Join(",", Here(w, picked).Select(f => f.Id)) : "")
                    + "|" + _screen.QuietWhenHandled;
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            foreach (var c in _root.GetChildren()) c.QueueFree();
            _live.Clear();
            Build(w, fleets, picked);
        }
        foreach (var update in _live) update(w);
    }

    /// <summary>Back (-1) or Next (+1): the previous or next of your fleets in this screen's list order. Wraps around; works while paused.</summary>
    public void Step(World w, int dir)
    {
        var list = Mine(w);
        if (list.Count < 2) return;
        var at = list.FindIndex(f => f.Id == _screen.FleetScreenId);
        _screen.FleetScreenId = list[((at < 0 ? 0 : at) + dir + list.Count) % list.Count].Id;
    }

    private List<Fleet> Mine(World w)
    {
        var rows = w.Fleets.Where(f => f.Side == Side.Player && f.ShipIds.Count > 0);
        var home = Economy.HomeOf(w, Side.Player)?.Position ?? Vec2.Zero;
        return (_sort switch
        {
            "role" => rows.OrderBy(f => f.Role == FleetRole.None ? 99 : (int)f.Role).ThenBy(f => f.Name),
            "ships" => rows.OrderByDescending(f => f.ShipIds.Count).ThenBy(f => f.Name),
            "condition" => rows.OrderBy(f => Combat.ArmourShare(w, f)).ThenBy(f => f.Name),
            "distance" => rows.OrderBy(f => Vec2.Distance(f.Position, home)).ThenBy(f => f.Name),
            _ => rows.OrderBy(f => f.Name.Length).ThenBy(f => f.Name, StringComparer.Ordinal),
        }).ToList();
    }

    /// <summary>Other fleets of ours close enough to swap ships with this one now.</summary>
    private static List<Fleet> Here(World w, Fleet f) =>
        w.Fleets.Where(o => o.Side == f.Side && o.Id != f.Id && o.ShipIds.Count > 0 && Fleets.CanMerge(o, f) == null).OrderBy(o => o.Id).ToList();

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

    private Label Live(Func<World, string> text, int size = 14, Color? color = null, bool wrap = false)
    {
        var l = Ui.Label("", size, color);
        if (wrap)
        {
            // Only inside a column: a wrapping label in a row shrinks to nothing.
            l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            l.CustomMinimumSize = new Vector2(200, 0);
        }
        _live.Add(w => l.Text = text(w));
        return l;
    }

    private static OptionButton Pick<T>(IReadOnlyList<(T Key, string Label)> items, T current, Action<T> chosen, string tip)
    {
        var o = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = tip };
        o.AddThemeFontSizeOverride("font_size", 14);
        for (var i = 0; i < items.Count; i++)
        {
            o.AddItem(items[i].Label, i);
            if (EqualityComparer<T>.Default.Equals(items[i].Key, current)) o.Selected = i;
        }
        o.ItemSelected += i => Main.Instance.RunGuarded("drop-down", () => chosen(items[(int)i].Key));
        return o;
    }

    private static Label Heading(string text) => Ui.Label(text, 17, new Color("f2f5fa"));

    private static string Condition(World w, Fleet f)
    {
        var armour = Combat.ArmourShare(w, f);
        var supply = Combat.SupplyShare(w, f);
        var crippled = w.ShipsOf(f).Count(s => s.Crippled);
        return $"hull {armour:P0} · supplies {supply:P0}" + (crippled > 0 ? $" · {crippled} crippled" : "");
    }

    /// <summary>The role and what it is doing about calls, in a few words.</summary>
    private static string RoleState(World w, Fleet f)
    {
        if (f.AnsweringCallId is int cid && w.HelpCalls.Find(c => c.Id == cid) is HelpCall call)
            return $"{HelpCalls.RoleName(f.Role)}: answering, {HelpCalls.Where(w, call)}";
        if (f.Role == FleetRole.None) return "No role";
        return HelpCalls.Free(w, f) ? $"{HelpCalls.RoleName(f.Role)}: ready" : $"{HelpCalls.RoleName(f.Role)}: busy";
    }

    // ---- Layout --------------------------------------------------------------------------------

    private void Build(World w, List<Fleet> fleets, Fleet? picked)
    {
        var top = Row(16);
        top.AddChild(Ui.Label("Fleets", 26, new Color("f2f5fa")));
        var note = Ui.Label("Pick a fleet to see its ships, role and supplies. Roles let free fleets answer calls for help by themselves", 13, Ui.Dim);
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(note);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var quiet = new CheckBox { Text = "No pause when a role fleet has it in hand", ButtonPressed = _screen.QuietWhenHandled, FocusMode = FocusModeEnum.None,
            TooltipText = "When an enemy is spotted or a fight starts that your role fleets are already dealing with inside their rules (range, odds, wear), "
                          + "show a news line instead of pausing. Anything outside their rules still pauses and asks you" };
        quiet.AddThemeFontSizeOverride("font_size", 13);
        quiet.Toggled += on => _screen.QuietWhenHandled = on;
        top.AddChild(quiet);
        top.AddChild(Ui.SmallButton("Back to map", () => _screen.ShowFleets = false, "Close the fleets screen (K or Esc)"));
        _root.AddChild(top);

        var body = Row(14);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        _root.AddChild(body);

        // Left: the list of fleets.
        var left = Column(8);
        left.CustomMinimumSize = new Vector2(360, 0);
        var sortRow = Row(8);
        sortRow.AddChild(Ui.Label("Sort by", 13, Ui.Dim));
        sortRow.AddChild(Pick(Sorts.Select(s => (s, Format.Title(s))).ToList(), _sort, k => _sort = k, "Order of the list"));
        left.AddChild(sortRow);
        var listScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = Column(6);
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (var f in fleets) list.AddChild(FleetRow(w, f, f.Id == picked?.Id));
        if (fleets.Count == 0) list.AddChild(Ui.Label("You have no fleets", 14, Ui.Dim));
        listScroll.AddChild(list);
        left.AddChild(listScroll);
        body.AddChild(left);

        // Right: the picked fleet.
        var rightScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var right = Column(10);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rightScroll.AddChild(right);
        body.AddChild(rightScroll);
        if (picked == null) return;
        right.AddChild(Header(w, picked));
        var cols = Row(10);
        var a = Column(10);
        a.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var b = Column(10);
        b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        a.AddChild(RoleBox(w, picked));
        a.AddChild(OrdersBox(w, picked));
        b.AddChild(SupplyBox(w, picked));
        b.AddChild(DistanceBox(w, picked));
        cols.AddChild(a);
        cols.AddChild(b);
        right.AddChild(cols);
        right.AddChild(ShipsBox(w, picked));
        right.AddChild(CallsBox(w, picked));
    }

    private Control FleetRow(World w, Fleet f, bool picked)
    {
        var col = Column(2);
        var id = f.Id;
        col.AddChild(Ui.Label($"{f.Name}  ·  {f.ShipIds.Count} ship{(f.ShipIds.Count == 1 ? "" : "s")}", 15, picked ? Ui.Player : Ui.Text));
        col.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? RoleState(w2, g) : "", 13, new Color("9fd6ff")));
        col.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? FleetPanel.Doing(w2, g) : "", 12, Ui.Dim));
        col.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? Condition(w2, g) : "", 12, Ui.Dim));
        var box = Box(col, 8);
        if (picked && box.GetThemeStylebox("panel") is StyleBoxFlat st)
        {
            var copy = (StyleBoxFlat)st.Duplicate();
            copy.BorderColor = Ui.Player;
            copy.SetBorderWidthAll(2);
            box.AddThemeStyleboxOverride("panel", copy);
        }
        box.MouseFilter = MouseFilterEnum.Stop;
        box.TooltipText = "Click to see this fleet; double-click to go to it on the map";
        box.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            Main.Instance.RunGuarded("pick fleet", () =>
            {
                if (click.DoubleClick) { _screen.ShowFleets = false; _screen.PickFleet(id); return; }
                _screen.FleetScreenId = id;
            });
        };
        return box;
    }

    private Control Header(World w, Fleet f)
    {
        var id = f.Id;
        var col = Column(4);
        var row = Row(12);
        row.AddChild(Ui.Label(f.Name, 22, Ui.Player));
        row.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? $"{g.ShipIds.Count} ships · strength {Stats.Power(w2, g):0} · {Condition(w2, g)}" : "", 14, Ui.Dim));
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        row.AddChild(Ui.SmallButton("◀ Back", () => Step(w, -1), "Your previous fleet in the list ([)"));
        row.AddChild(Ui.SmallButton("Next ▶", () => Step(w, 1), "Your next fleet in the list (])"));
        row.AddChild(Ui.SmallButton("Show on map", () => { _screen.ShowFleets = false; _screen.PickFleet(id); }, "Close this screen and select the fleet on the map"));
        row.AddChild(Ui.SmallButton("Ships and fittings", () => { _screen.ShowFleets = false; _screen.PickFleet(id); _screen.FittingFleetId = id; },
            "Each ship's drawing, slots and modules"));
        if (Sim(w)) row.AddChild(Ui.SmallButton("Send for refit", () => _screen.IssueNow(new SendForRefitCommand(Side.Player, id), "refit"),
            "Fly to the shipyard for repairs and the current design"));
        col.AddChild(row);
        col.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? FleetPanel.Doing(w2, g) + (g.Mission != Mission.None ? $" · mission: {FleetPanel.MissionText(w2, g)}" : "") : "", 14, wrap: true));
        return Box(col);
    }

    private static bool Sim(World w) => w.Loop;

    // ---- Role ----------------------------------------------------------------------------------

    private Control RoleBox(World w, Fleet f)
    {
        var id = f.Id;
        var col = Column(6);
        col.AddChild(Heading("Role"));
        var roles = Enum.GetValues<FleetRole>().Select(r => (r, HelpCalls.RoleName(r))).ToList();
        var row = Row(8);
        row.AddChild(Pick(roles, f.Role, r => SetRole(id, r, null), "Which calls for help this fleet answers by itself when it is free"));
        row.AddChild(Ui.Label(HelpCalls.RoleHelp(f.Role), 13, Ui.Dim));
        col.AddChild(row);
        if (f.Role != FleetRole.None)
        {
            var rules = f.RoleRules;
            var grid = new GridContainer { Columns = 2 };
            grid.AddThemeConstantOverride("h_separation", 12);
            grid.AddThemeConstantOverride("v_separation", 6);
            grid.AddChild(Ui.Label("Goes up to", 13, Ui.Dim));
            grid.AddChild(Pick(Enum.GetValues<RoleRange>().Select(r => (r, HelpCalls.RangeName(r))).ToList(), rules.Range,
                r => SetRule(id, x => x.Range = r), "How far from the trouble the fleet may be and still go"));
            grid.AddChild(Ui.Label("Odds it wants", 13, Ui.Dim));
            grid.AddChild(Pick(Enum.GetValues<RoleOdds>().Select(o => (o, HelpCalls.OddsName(o))).ToList(), rules.Odds,
                o => SetRule(id, x => x.Odds = o), "Its strength (with any other fleets answering) against the enemy's, as your sensors judge it. Below this it stays and the call waits for you"));
            grid.AddChild(Ui.Label("Stays put when", 13, Ui.Dim));
            var wear = new (double, string)[] { (0, "Never"), (0.25, "Hull or supplies below 25 %"), (0.5, "Hull or supplies below 50 %"), (0.75, "Hull or supplies below 75 %") };
            grid.AddChild(Pick(wear, wear.OrderBy(x => Math.Abs(x.Item1 - rules.MinHealth)).First().Item1,
                v => SetRule(id, x => x.MinHealth = v), "Too worn or too short of supplies to answer"));
            grid.AddChild(Ui.Label("Afterwards", 13, Ui.Dim));
            var back = new CheckBox { Text = "Go back to its post and mission", ButtonPressed = rules.ReturnAfter, FocusMode = FocusModeEnum.None,
                TooltipText = "When the call is over, fly back to where it was (and carry on with its patrol or hold). Off: it stays where the fight ended" };
            back.AddThemeFontSizeOverride("font_size", 13);
            back.Toggled += on => SetRule(id, x => x.ReturnAfter = on);
            grid.AddChild(back);
            col.AddChild(grid);
        }
        col.AddChild(Live(w2 =>
        {
            if (w2.FindFleet(id) is not Fleet g) return "";
            if (g.AnsweringCallId is int cid && w2.HelpCalls.Find(c => c.Id == cid) is HelpCall call)
                return $"Answering: {call.Text}." + (g.RoleRules.ReturnAfter && w2.FindBody(g.PostBodyId ?? -1) is Body post ? $" Goes back to {post.Name} after." : "");
            if (g.Role == FleetRole.None) return "Answers no calls. Give it a role to have it help planets, cargo runs or other fleets by itself.";
            return HelpCalls.Free(w2, g) ? "Free: it will answer the next call its role covers, within these rules."
                : "Busy with an order or job: it answers calls once that is done (a patrol or hold counts as free).";
        }, 13, new Color("9fd6ff"), wrap: true));
        return Box(col);
    }

    private void SetRole(int id, FleetRole role, RoleRules? rules) =>
        _screen.IssueNow(new SetFleetRoleCommand(id, role, rules), "fleet role");

    private void SetRule(int id, Action<RoleRules> change)
    {
        if (_screen.World.FindFleet(id) is not Fleet f) return;
        var rules = f.RoleRules.Copy();
        change(rules);
        SetRole(id, f.Role, rules);
    }

    // ---- Standing orders (a summary: the fleet panel changes them) -----------------------------

    private Control OrdersBox(World w, Fleet f)
    {
        var id = f.Id;
        var col = Column(4);
        col.AddChild(Heading("Standing orders"));
        col.AddChild(Live(w2 =>
        {
            if (w2.FindFleet(id) is not Fleet g) return "";
            var lines = new List<string>
            {
                $"On contact: {g.Stance}",
                $"Pull back: {PullRule.Name(g.Rule.Preset)}{(g.RuleByHand ? " (set by hand)" : "")}",
                $"Formation: {Formations.Name(g.Formation)}",
                $"Cut off from command: {g.Autonomy}",
                $"Mission: {FleetPanel.MissionText(w2, g)}",
            };
            return string.Join("\n", lines);
        }, 13));
        col.AddChild(Ui.Label("Change these in the fleet panel on the map (Show on map)", 12, Ui.Dim));
        return Box(col);
    }

    // ---- Supply --------------------------------------------------------------------------------

    private Control SupplyBox(World w, Fleet f)
    {
        var id = f.Id;
        var col = Column(6);
        col.AddChild(Heading("Supply"));
        col.AddChild(Live(w2 =>
        {
            if (w2.FindFleet(id) is not Fleet g) return "";
            var ships = w2.ShipsOf(g).ToList();
            var fuel = ships.Average(s => s.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w2, s)));
            var gunners = ships.Where(s => SupplyLines.MaxAmmo(s) > 0).ToList();
            var eaters = ships.Where(s => SupplyLines.MaxFood(s) > 0).ToList();
            var racks = ships.Where(s => Fitting.Spec(s).Missiles > 0).ToList();
            var parts = new List<string> { $"Fuel {fuel:P0}" };
            if (gunners.Count > 0) parts.Add($"railgun ammo {gunners.Average(s => s.Ammo / SupplyLines.MaxAmmo(s)):P0}");
            if (racks.Count > 0) parts.Add($"missiles {racks.Sum(s => s.Missiles)}/{racks.Sum(s => Tech.Racks(w2, s))}");
            if (eaters.Count > 0) parts.Add(eaters.Any(SupplyLines.Hungry) ? "food: HUNGRY" : $"food {eaters.Min(s => s.Food):0} days");
            var runners = w2.Fleets.Where(o => o.Side == g.Side && o.SupplyRunFor == g.Id).Select(o => o.Name).ToList();
            var line2 = runners.Count > 0 ? $"Kept topped up by {string.Join(", ", runners)}" : "No supply run to it";
            if (g.Resupplying) line2 += " · on its way to refill";
            return $"{string.Join(" · ", parts)}\n{line2}\nFuel: {(g.Fuel == "" ? "Auto" : Fuels.Data(g.Fuel).Name)}";
        }, 13));
        // Send one of the fleets with tankers or freighters on a run to this one, or stop a run.
        var suppliers = w.Fleets.Where(o => o.Side == Side.Player && o.Id != f.Id && w.ShipsOf(o).Any(SupplyLines.IsSupplyShip)).OrderBy(o => o.Id).ToList();
        var row = Row(8);
        if (suppliers.Count > 0)
        {
            var items = new List<(int, string)> { (-1, "Send a supply fleet…") };
            items.AddRange(suppliers.Select(o => (o.Id, $"{o.Name}{(o.SupplyRunFor == f.Id ? " (running for it)" : o.SupplyRunFor is int t ? $" (busy: {w.FindFleet(t)?.Name})" : "")}")));
            row.AddChild(Pick(items, -1, sid => { if (sid >= 0) _screen.IssueNow(new SetSupplyRunCommand(Side.Player, sid, id), "supply run"); },
                "Have a fleet with tankers or freighters fly out to this one, top it up and repeat"));
        }
        if (w.Fleets.Any(o => o.Side == Side.Player && o.SupplyRunFor == f.Id))
            row.AddChild(Ui.SmallButton("Stop supply runs", () =>
            {
                foreach (var o in _screen.World.Fleets.Where(o => o.Side == Side.Player && o.SupplyRunFor == id).ToList())
                    _screen.IssueNow(new SetSupplyRunCommand(Side.Player, o.Id, null), "supply run");
            }, "Call off the supply fleets running to this one"));
        if (w.Loop)
        {
            row.AddChild(Ui.SmallButton("Order a tanker", () => _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Ship, "tanker", id), "ordering a tanker"),
                "Build a tanker that flies out to join this fleet"));
        }
        col.AddChild(row);
        return Box(col);
    }

    // ---- Distances -----------------------------------------------------------------------------

    private Control DistanceBox(World w, Fleet f)
    {
        var id = f.Id;
        var col = Column(4);
        col.AddChild(Heading("Distances"));
        col.AddChild(Live(w2 =>
        {
            if (w2.FindFleet(id) is not Fleet g) return "";
            var lines = new List<string>();
            var lag = Light.DelayHours(w2, Side.Player, g.Position);
            lines.Add(w2.Light.On ? (lag <= 0 ? "In command range: orders arrive at once" : $"Orders take {Format.Hours(lag)} to reach it") : "Light lag off");
            var home = Economy.HomeOf(w2, Side.Player);
            foreach (var b in w2.Bodies.Where(b => b.Owner == Side.Player).OrderBy(b => Vec2.Distance(b.Position, g.Position)).ThenBy(b => b.Id).Take(5))
            {
                var d = Vec2.Distance(b.Position, g.Position);
                lines.Add($"{b.Name}{(b == home ? " (home)" : "")}: {Format.Distance(d)}" + (Economy.InOrbitAt(g, b) ? " · here" : ""));
            }
            var enemy = w2.Sightings.Where(s => s.SeenBy == Side.Player && !s.Own && w2.Hour - s.Hour < 24)
                .OrderBy(s => Vec2.Distance(s.Guess(w2.Hour), g.Position)).FirstOrDefault();
            if (enemy != null) lines.Add($"Nearest enemy seen: {enemy.Name} ({enemy.Ships}), {Format.Distance(Vec2.Distance(enemy.Guess(w2.Hour), g.Position))}");
            return string.Join("\n", lines);
        }, 13));
        return Box(col);
    }

    // ---- Ships ---------------------------------------------------------------------------------

    private Control ShipsBox(World w, Fleet f)
    {
        var id = f.Id;
        var col = Column(6);
        var head = Row(10);
        head.AddChild(Heading("Ships"));
        head.AddChild(Ui.Label("Bars: hull, weapons, engines, defence, crew, fuel. Tick ships to move them", 12, Ui.Dim));
        col.AddChild(head);
        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var h in new[] { "", "Ship", "Class", "Condition", "Supplies", "Crew" }) grid.AddChild(Ui.Label(h, 12, Ui.Dim));
        foreach (var s in w.ShipsOf(f).ToList())
        {
            var shipId = s.Id;
            var tick = new CheckBox { ButtonPressed = _ticked.Contains(s.Id), FocusMode = FocusModeEnum.None };
            tick.Toggled += on => { if (on) _ticked.Add(shipId); else _ticked.Remove(shipId); };
            grid.AddChild(tick);
            var name = new Button { Text = $"{s.Name}  {s.Code}" + (Light.Flagship(w, s.Side)?.Id == s.Id ? "  ★" : ""), Flat = true, FocusMode = FocusModeEnum.None,
                TooltipText = "The ship's card: its bars, crew, fit and history", Alignment = HorizontalAlignment.Left };
            name.AddThemeFontSizeOverride("font_size", 14);
            name.Pressed += () => _screen.ShipCardId = shipId;
            grid.AddChild(name);
            grid.AddChild(Ui.Label(Fitting.Spec(s).Name, 13));
            var bars = new ShipBars();
            _live.Add(w2 =>
            {
                if (w2.FindShip(shipId) is not Ship x) return;
                var armour = Math.Clamp(x.Armour / Tech.MaxArmour(w2, x), 0, 1);
                var fuel = x.Supplies / Math.Max(1e-9, Fitting.MaxSupplies(w2, x));
                Color Bar(double v) => WoundBars.BarColor(v);
                bars.Set((armour, Bar(armour)), (x.Weapons, Bar(x.Weapons)), (x.Engines, Bar(x.Engines)), (x.Defence, Bar(x.Defence)), (x.Crew, Bar(x.Crew)),
                    (fuel, fuel < 0.2 ? Ui.Danger : new Color("5fb0ff")));
                bars.TooltipText = $"Hull {armour:P0} · weapons {x.Weapons:P0} · engines {x.Engines:P0} · defence {x.Defence:P0} · crew {x.Crew:P0} · fuel {fuel:P0}"
                                   + (x.Crippled ? "\nCRIPPLED" : "");
            });
            grid.AddChild(bars);
            grid.AddChild(Live(w2 =>
            {
                if (w2.FindShip(shipId) is not Ship x) return "";
                var bits = new List<string>();
                if (SupplyLines.MaxAmmo(x) > 0) bits.Add($"ammo {x.Ammo / SupplyLines.MaxAmmo(x):P0}");
                if (Fitting.Spec(x).Missiles > 0) bits.Add(x.Missiles <= 0 ? "NO MISSILES" : $"missiles {x.Missiles}/{Tech.Racks(w2, x)}");
                if (SupplyLines.MaxFood(x) > 0) bits.Add(SupplyLines.Hungry(x) ? "HUNGRY" : $"food {x.Food:0} d");
                if (SupplyLines.IsSupplyShip(x)) bits.Add($"cargo {SupplyLines.CargoShare(x):P0}");
                return bits.Count > 0 ? string.Join(" · ", bits) : "—";
            }, 12));
            grid.AddChild(Live(w2 => w2.FindShip(shipId) is Ship x ? (w2.Loop && Crews.Crewed(x) ? $"{Crews.RankOf(x)}" : w2.Loop ? "AI crewed" : "—") : "", 12));
        }
        col.AddChild(grid);

        // Moving ticked ships: to a fleet at the same place, or off on their own.
        var moveRow = Row(8);
        var here = Here(w, f);
        if (here.Count > 0)
        {
            var target = here[0].Id;
            moveRow.AddChild(Ui.Label("Move ticked ships to", 13, Ui.Dim));
            moveRow.AddChild(Pick(here.Select(o => (o.Id, $"{o.Name} ({o.ShipIds.Count} ships)")).ToList(), target, t => target = t,
                "Fleets of yours at the same place (both stopped, close together)"));
            moveRow.AddChild(Ui.SmallButton("Move", () =>
            {
                if (_ticked.Count == 0) { Main.Instance.Toast("Tick the ships to move first"); return; }
                _screen.IssueNow(new MoveShipsCommand(id, _ticked.ToList(), target), "moving ships");
                _ticked.Clear();
            }, "Move the ticked ships into that fleet (all of them: the fleets merge)"));
        }
        else
            moveRow.AddChild(Ui.Label("No other fleet of yours is here to swap ships with (fleets must be stopped together)", 12, Ui.Dim));
        moveRow.AddChild(Ui.SmallButton("Split ticked into a new fleet", () =>
        {
            if (_ticked.Count == 0) { Main.Instance.Toast("Tick the ships to split off first"); return; }
            _screen.IssueNow(new SplitFleetCommand(id, _ticked.ToList()), "split");
            _ticked.Clear();
        }, "The ticked ships become a new fleet here, with the same orders and role"));
        col.AddChild(moveRow);
        return Box(col);
    }

    // ---- Calls for help ------------------------------------------------------------------------

    private Control CallsBox(World w, Fleet picked)
    {
        var col = Column(6);
        col.AddChild(Heading("Calls for help"));
        var calls = w.HelpCalls.Where(c => c.Side == Side.Player).OrderByDescending(c => c.Open).ThenByDescending(c => c.RaisedHour).Take(8).ToList();
        if (calls.Count == 0)
        {
            col.AddChild(Ui.Label("None. Planets call when they see an enemy fleet close, cargo runs when raiders hit them, and fleets when an enemy they can't beat comes near", 13, Ui.Dim));
            return Box(col);
        }
        var pickedId = picked.Id;
        foreach (var call in calls)
        {
            var callId = call.Id;
            col.AddChild(Live(w2 =>
            {
                if (w2.HelpCalls.Find(c => c.Id == callId) is not HelpCall c) return "";
                var when = GameTime.Format(c.RaisedHour);
                if (!c.Open) return $"Over · {c.Text} (from {when})";
                var going = HelpCalls.Answering(w2, c);
                var who = going.Count > 0 ? $"Answered by {string.Join(", ", going.Select(g => g.Name))}" : "Nobody answering: it waits for your orders";
                var why = "";
                if (w2.FindFleet(pickedId) is Fleet p && p.AnsweringCallId != c.Id)
                    why = HelpCalls.WhyNot(w2, p, c) is string no ? $" · {p.Name} stays: {no}" : $" · {p.Name} could go";
                return $"{c.Text} (since {when})\n   {who}{why}";
            }, 13, call.Open ? Ui.Text : Ui.Dim, wrap: true));
        }
        return Box(col);
    }
}
