using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The Operations tab of the fleets screen (K): your fleets down the left grouped by role (tick several for a
/// strike), the operations map in the middle, and on the right the picked fleet (the cover it gives, what its
/// leaving uncovers, its reach, its patrol loop), Who covers what (every planet, moon group and cargo run judged
/// covered, thin or open, with Cover it), and the strike planner with saved plans. Every order works while paused.
/// </summary>
public partial class FleetScreen
{
    private string _opsPane = "fleet";
    private string? _placeKey;
    private readonly List<int> _strikeIds = new();
    private int? _strikeBody;
    private int? _strikeEnemy;
    private StrikeArrival _strikeArrival = StrikeArrival.Together;
    private double _strikeGap = 12;
    private double _strikeBurn = 0.8;
    private int? _strikePlanId;
    private List<LoopStop>? _loopDraft;
    private int? _loopFleetId;
    private int _opsVersion;
    private readonly HashSet<string> _layers = new() { "zones", "loops", "runs", "enemies", "cover", "reach" };

    // Worked out once a game hour (or after an order), not every frame.
    private long _judgedTick = -1;
    private int _judgedVersion = -1;
    private List<CoverJudgement> _judged = new();
    private string _previewKey = "";
    private StrikePreview? _preview;

    private static readonly Color Warn = new("ffb347");
    private static readonly Color Good = new("6fdc8c");

    private static Color StateColor(CoverState s) => s switch { CoverState.Covered => Good, CoverState.Thin => Warn, _ => Ui.Danger };

    private List<CoverJudgement> Judged(World w)
    {
        if (_judgedTick == w.Tick && _judgedVersion == _opsVersion) return _judged;
        _judgedTick = w.Tick;
        _judgedVersion = _opsVersion;
        _judged = Coverage.JudgeAll(w, Side.Player);
        return _judged;
    }

    /// <summary>Issue an operations order now (it still travels at light speed) and work the views out again.</summary>
    private void Order(GameCommand command, string what)
    {
        _screen.IssueNow(command, what);
        _opsVersion++;
    }

    /// <summary>The parts of the Operations tab whose shape decides a rebuild.</summary>
    private string OpsShape(World w)
    {
        var places = string.Join(",", Coverage.Places(w, Side.Player).Select(p => p.Key));
        var plans = string.Join(";", w.StrikePlans.Where(p => p.Side == Side.Player).Select(p => $"{p.Id}:{p.Name}"));
        var loops = string.Join(";", w.Fleets.Where(f => f.Side == Side.Player && f.Loop.Count > 0).Select(f => $"{f.Id}:{f.Loop.Count}:{f.Mission}"));
        var draft = _loopDraft == null ? "" : string.Join(";", _loopDraft.Select(s => $"{s.BodyId}/{s.FromBodyId}/{s.ToBodyId}/{s.T:0.00}/{s.WaitHours:0}"));
        var enemies = string.Join(",", w.Sightings.Where(s => s.SeenBy == Side.Player && !s.Own && !Lanes.CargoSighting(s) && w.Hour - s.Hour <= Coverage.ThreatDays * 24)
            .Select(s => s.FleetId).OrderBy(x => x));
        return $"ops|{_opsPane}|{_placeKey}|{string.Join(",", _strikeIds)}|{_strikeBody}|{_strikeEnemy}|{_strikeArrival}|{_strikeGap}|{_strikeBurn}|{_strikePlanId}"
               + $"|{places}|{plans}|{loops}|{_loopFleetId}|{draft}|{enemies}|{string.Join(",", _layers.OrderBy(x => x))}|"
               + string.Join(",", w.Fleets.Where(f => f.Side == Side.Player).Select(f => $"{f.Id}:{f.CoverKey}"));
    }

    // ---- Layout --------------------------------------------------------------------------------

    private void BuildOperations(World w, List<Fleet> fleets, Fleet? picked)
    {
        var body = Row(12);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        _root.AddChild(body);

        // Left: fleets by role, with ticks for a strike.
        var left = Column(6);
        left.CustomMinimumSize = new Vector2(300, 0);
        left.AddChild(Heading("Your fleets"));
        left.AddChild(Ui.Label("Tick several to give them one strike plan", 12, Ui.Dim));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = Column(4);
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (var group in fleets.GroupBy(f => f.Role).OrderBy(g => g.Key == FleetRole.None ? 99 : (int)g.Key))
        {
            list.AddChild(Ui.Label(group.Key == FleetRole.None ? "No role" : HelpCalls.RoleName(group.Key), 13, new Color("9fd6ff")));
            foreach (var f in group.OrderBy(f => f.Name, StringComparer.Ordinal)) list.AddChild(OpsFleetRow(w, f, f.Id == picked?.Id));
        }
        if (fleets.Count == 0) list.AddChild(Ui.Label("You have no fleets", 14, Ui.Dim));
        scroll.AddChild(list);
        left.AddChild(scroll);
        body.AddChild(left);

        // Middle: the map, with its layers.
        var mid = Column(6);
        mid.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var layers = Row(10);
        layers.AddChild(Ui.Label("Show", 12, Ui.Dim));
        foreach (var (key, label) in new[] { ("cover", "Cover"), ("zones", "Zones"), ("loops", "Loops"), ("runs", "Cargo runs"), ("enemies", "Enemy last seen"), ("reach", "Reach") })
        {
            var box = new CheckBox { Text = label, ButtonPressed = _layers.Contains(key), FocusMode = FocusModeEnum.None };
            box.AddThemeFontSizeOverride("font_size", 12);
            box.Toggled += on => { if (on) _layers.Add(key); else _layers.Remove(key); };
            layers.AddChild(box);
        }
        mid.AddChild(layers);
        var map = new OpsMap
        {
            GetWorld = () => Main.Instance.Sim?.World,
            Picked = () => _screen.FleetScreenId,
            PickedPlace = () => _placeKey,
            Judged = () => Main.Instance.Sim?.World is World w2 ? Judged(w2) : Array.Empty<CoverJudgement>(),
            Ticked = () => _strikeIds,
            OnPickFleet = id => { _screen.FleetScreenId = id; _opsPane = "fleet"; },
            OnPickPlace = key => { _placeKey = key; _opsPane = "cover"; },
            Layers = _layers,
        };
        mid.AddChild(map);
        mid.AddChild(Ui.Label("Red dashed rings: nothing covers it. Amber: thin cover. Triangles: enemy fleets where last seen, fading with age", 12, Ui.Dim));
        body.AddChild(mid);

        // Right: the picked fleet, who covers what, or the strike planner.
        var right = Column(8);
        right.CustomMinimumSize = new Vector2(470, 0);
        var tabs = Row(4);
        foreach (var (key, label) in new[] { ("fleet", "Fleet"), ("cover", "Who covers what"), ("strike", "Strike planner") })
        {
            var b = Ui.SmallButton(label, () => _opsPane = key, label);
            b.ToggleMode = true;
            b.ButtonPressed = _opsPane == key;
            tabs.AddChild(b);
        }
        right.AddChild(tabs);
        var paneScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var pane = Column(10);
        pane.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        paneScroll.AddChild(pane);
        right.AddChild(paneScroll);
        body.AddChild(right);
        switch (_opsPane)
        {
            case "cover": CoverPane(w, pane); break;
            case "strike": StrikePane(w, pane); break;
            default:
                if (picked != null) FleetPane(w, picked, pane);
                else pane.AddChild(Ui.Label("Pick a fleet", 14, Ui.Dim));
                break;
        }
    }

    private Control OpsFleetRow(World w, Fleet f, bool picked)
    {
        var id = f.Id;
        var row = Row(6);
        var tick = new CheckBox { ButtonPressed = _strikeIds.Contains(id), FocusMode = FocusModeEnum.None, TooltipText = "Tick to add it to the strike planner" };
        tick.Toggled += on =>
        {
            _strikeIds.Remove(id);
            if (on) { _strikeIds.Add(id); _opsPane = "strike"; }
        };
        row.AddChild(tick);
        var col = Column(1);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddChild(Ui.Label($"{f.Name}", 14, picked ? Ui.Player : Ui.Text));
        col.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? $"{g.ShipIds.Count} ships · {Fresh(w2, g)} · {Place(w2, g)}" : "", 12, Ui.Dim));
        col.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? Activity(w2, g) : "", 12, new Color("9fd6ff"), wrap: true));
        row.AddChild(col);
        var box = Box(row, 6);
        if (picked && box.GetThemeStylebox("panel") is StyleBoxFlat st)
        {
            var copy = (StyleBoxFlat)st.Duplicate();
            copy.BorderColor = Ui.Player;
            copy.SetBorderWidthAll(2);
            box.AddThemeStyleboxOverride("panel", copy);
        }
        box.MouseFilter = MouseFilterEnum.Stop;
        box.TooltipText = "Click to pick it; double-click to go to it on the main map";
        box.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
            Main.Instance.RunGuarded("pick fleet", () =>
            {
                if (click.DoubleClick) { _screen.ShowFleets = false; _screen.PickFleet(id); return; }
                _screen.FleetScreenId = id;
                _opsPane = "fleet";
            });
        };
        return box;
    }

    private static string Fresh(World w, Fleet f)
    {
        var hurt = w.ShipsOf(f).Count(s => s.Armour < Tech.MaxArmour(w, s) * 0.95 || s.Crippled);
        var low = Combat.SupplyShare(w, f) < 0.35;
        if (hurt == 0 && !low) return "fresh";
        var bits = new List<string>();
        if (hurt > 0) bits.Add($"{hurt} hurt");
        if (low) bits.Add("low on supplies");
        return string.Join(", ", bits);
    }

    private static string Place(World w, Fleet f)
    {
        if (f.Flight?.TargetBodyId is int t && w.FindBody(t) is Body to) return $"to {to.Name}";
        if (f.Hold == HoldStyle.Orbit && f.OrbitBodyId is int o && w.FindBody(o) is Body at) return $"at {at.Name}";
        var near = w.Bodies.Where(b => b.Kind is BodyKind.Planet or BodyKind.Moon).OrderBy(b => Vec2.Distance(b.Position, f.Position)).ThenBy(b => b.Id).FirstOrDefault();
        return near == null ? "in space" : $"{Vec2.Distance(near.Position, f.Position) / Physics.MmPerAu:0.##} AU from {near.Name}";
    }

    /// <summary>What it is doing about its job, in one line: operations first, then its role, then what it is flying.</summary>
    private static string Activity(World w, Fleet f)
    {
        var ops = Operations.Status(w, f);
        if (ops.Count > 0) return ops[0];
        if (f.AnsweringCallId != null || f.Role != FleetRole.None) return RoleState(w, f);
        return FleetPanel.Doing(w, f);
    }

    // ---- Fleet pane ----------------------------------------------------------------------------

    private void FleetPane(World w, Fleet f, VBoxContainer pane)
    {
        var id = f.Id;
        var head = Column(4);
        var top = Row(8);
        top.AddChild(Ui.Label(f.Name, 20, Ui.Player));
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        top.AddChild(Ui.SmallButton("Plan a strike", () =>
        {
            if (!_strikeIds.Contains(id)) _strikeIds.Add(id);
            _opsPane = "strike";
        }, "Open the strike planner with this fleet in it"));
        var roles = HelpCalls.RolesFor(w, f).Select(r => (r, HelpCalls.RoleName(r))).ToList();
        top.AddChild(Pick(roles, f.Role, r => { SetRole(id, r, null); _opsVersion++; }, "Which calls for help it answers by itself when free"));
        head.AddChild(top);
        head.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? $"{g.ShipIds.Count} ships · strength {Stats.Power(w2, g):0} · {Condition(w2, g)}" : "", 13, Ui.Dim));
        head.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g ? FleetPanel.Doing(w2, g) : "", 13, wrap: true));
        pane.AddChild(Box(head));

        var cover = Column(4);
        cover.AddChild(Heading("Cover it is giving"));
        cover.AddChild(Live(w2 =>
        {
            if (w2.FindFleet(id) is not Fleet g) return "";
            var given = Judged(w2).Where(j => j.Who.Any(c => c.FleetId == g.Id)).ToList();
            if (given.Count == 0)
                return g.Role == FleetRole.None && g.ZoneId == null && g.Mission == Mission.None
                    ? "Nothing: it has no role, no zone and no post, so it answers no calls."
                    : "Nothing it counts for now (too far, too busy, or its role does not answer there).";
            return string.Join("\n", given.Select(j =>
            {
                var c = j.Who.First(x => x.FleetId == g.Id);
                return $"{j.Place.Name}: {(c.OnSpot ? c.How : $"{c.How}, {Coverage.Age(c.Hours)} away")} ({Coverage.StateName(j.State)})";
            }));
        }, 13, wrap: true));
        pane.AddChild(Box(cover));

        var leaves = Column(4);
        leaves.AddChild(Heading("If it leaves"));
        leaves.AddChild(Live(w2 =>
        {
            if (w2.FindFleet(id) is not Fleet g) return "";
            var worse = Coverage.Uncovered(w2, Side.Player, new[] { g.Id });
            if (worse.Count == 0) return "Nothing is left uncovered: other fleets or planet guns still cover what it covers.";
            return string.Join("\n", worse.Select(x => $"{x.Now.Place.Name} goes from {Coverage.StateName(x.Now.State)} to {Coverage.StateName(x.Without.State)}: {x.Without.Why}"));
        }, 13, Warn, wrap: true));
        pane.AddChild(Box(leaves));

        var reach = Column(4);
        reach.AddChild(Heading("Reach"));
        reach.AddChild(Live(w2 =>
        {
            if (w2.FindFleet(id) is not Fleet g) return "";
            var (trips, burns, _) = Operations.Reach(w2, g);
            var line = trips.Count == 0 ? "It can't fly anywhere on the fuel it has" : string.Join(" · ", trips.Select(t => $"{t.Body.Name} {Coverage.Age(t.Hours)}")) + " at full burn";
            return $"{line}\nFuel allows {burns} full burn{(burns == 1 ? "" : "s")} (speed up and brake) before a tanker is needed";
        }, 13, wrap: true));
        pane.AddChild(Box(reach));

        pane.AddChild(LoopBox(w, f));
    }

    // ---- Patrol loop editor --------------------------------------------------------------------

    private Control LoopBox(World w, Fleet f)
    {
        var id = f.Id;
        if (_loopFleetId != id)
        {
            _loopFleetId = id;
            _loopDraft = f.Loop.Select(s => s.Copy()).ToList();
        }
        var draft = _loopDraft!;
        var col = Column(6);
        var head = Row(8);
        head.AddChild(Heading("Patrol loop"));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Live(w2 => w2.FindFleet(id) is Fleet g && draft.Count > 0 ? Operations.LapText(w2, g, draft) : "", 12, Ui.Dim));
        col.AddChild(head);
        col.AddChild(Ui.Label(f.Loop.Count > 0 && f.Mission == Mission.Patrol
            ? "Flying this loop. A call for help in range still wins, and it comes back to the stop it left"
            : "Stops in order, with a wait at each. A call for help in range still wins, and it comes back to the stop it left", 12, Ui.Dim));

        var waits = new List<(double, string)> { (0, "no wait"), (6, "wait 6 h"), (12, "wait 12 h"), (24, "wait 1 d"), (48, "wait 2 d"), (72, "wait 3 d"), (120, "wait 5 d"), (168, "wait 7 d") };
        for (var i = 0; i < draft.Count; i++)
        {
            var at = i;
            var stop = draft[i];
            var row = Row(6);
            var current = f.Mission == Mission.Patrol && f.Loop.Count == draft.Count && f.MissionLeg % Math.Max(1, f.Loop.Count) == i;
            row.AddChild(Ui.Label($"{i + 1}", 14, current ? Ui.Player : Ui.Dim));
            var name = Ui.Label(Operations.StopName(w, stop), 13);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(name);
            var options = waits.Any(x => Math.Abs(x.Item1 - stop.WaitHours) < 0.5) ? waits : waits.Append((stop.WaitHours, $"wait {Coverage.Age(stop.WaitHours)}")).ToList();
            row.AddChild(Pick(options, options.First(x => Math.Abs(x.Item1 - stop.WaitHours) < 0.5).Item1, h => { draft[at].WaitHours = h; _shape = ""; }, "How long it waits at this stop"));
            row.AddChild(Ui.SmallButton("▲", () => { if (at > 0) (draft[at - 1], draft[at]) = (draft[at], draft[at - 1]); _shape = ""; }, "Move this stop earlier"));
            row.AddChild(Ui.SmallButton("▼", () => { if (at < draft.Count - 1) (draft[at + 1], draft[at]) = (draft[at], draft[at + 1]); _shape = ""; }, "Move this stop later"));
            row.AddChild(Ui.SmallButton("✕", () => { draft.RemoveAt(at); _shape = ""; }, "Take this stop off the loop"));
            col.AddChild(row);
        }
        if (draft.Count == 0) col.AddChild(Ui.Label("No stops yet", 13, Ui.Dim));

        var tools = Row(6);
        var add = new MenuButton { Text = "Add a stop ▾", FocusMode = FocusModeEnum.None, TooltipText = "A planet or moon orbit, a point on a cargo run, or where the fleet is now" };
        add.AddThemeFontSizeOverride("font_size", 14);
        var options2 = new List<LoopStop>();
        add.AboutToPopup += () =>
        {
            var popup = add.GetPopup();
            popup.Clear();
            options2.Clear();
            if (Main.Instance.Sim?.World is not World w2 || w2.FindFleet(id) is not Fleet g) return;
            foreach (var b in w2.Bodies.Where(b => b.Kind is BodyKind.Planet or BodyKind.Moon or BodyKind.Asteroid)
                         .OrderBy(b => b.Owner == Side.Player ? 0 : 1).ThenBy(b => Vec2.Distance(b.Position, g.Position)).ThenBy(b => b.Id).Take(14))
            {
                options2.Add(new LoopStop { BodyId = b.Id, WaitHours = 24 });
                popup.AddItem($"{b.Name} orbit{(b.Owner == Side.Player ? " (yours)" : b.Owner != null ? " (enemy)" : "")}", options2.Count - 1);
            }
            foreach (var p in Coverage.Places(w2, Side.Player).Where(p => p.Kind == PlaceKind.Run))
            {
                options2.Add(new LoopStop { FromBodyId = p.FromBodyId, ToBodyId = p.ToBodyId, T = 0.5, WaitHours = 48 });
                popup.AddItem($"{p.Name} cargo run, halfway", options2.Count - 1);
            }
            options2.Add(new LoopStop { Point = g.Position, WaitHours = 72 });
            popup.AddItem("Where the fleet is now (sit cold and watch)", options2.Count - 1);
        };
        add.GetPopup().IdPressed += pick => Main.Instance.RunGuarded("add stop", () =>
        {
            if (pick < 0 || pick >= options2.Count || draft.Count >= Operations.MaxStops) return;
            draft.Add(options2[(int)pick].Copy());
            _shape = "";
        });
        tools.AddChild(add);
        tools.AddChild(Ui.SmallButton("Run it backwards", () => { draft.Reverse(); _shape = ""; }, "Fly the stops in the other order"));
        tools.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (f.Loop.Count > 0)
            tools.AddChild(Ui.SmallButton("End loop", () =>
            {
                Order(new SetPatrolLoopCommand(id, new List<LoopStop>()), "end loop");
                _loopFleetId = null;
            }, "Stop flying the loop (it stays where it is)"));
        var start = Ui.SmallButton(f.Loop.Count > 0 ? "Update loop" : "Start loop", () =>
        {
            if (draft.Count == 0) { Main.Instance.Toast("Add a stop first"); return; }
            Order(new SetPatrolLoopCommand(id, draft.Select(s => s.Copy()).ToList()), "patrol loop");
            _loopFleetId = null;
        }, "Give the fleet this loop: it starts at stop 1");
        tools.AddChild(start);
        col.AddChild(tools);
        return Box(col);
    }

    // ---- Who covers what -----------------------------------------------------------------------

    private void CoverPane(World w, VBoxContainer pane)
    {
        pane.AddChild(Heading("Who covers what"));
        var note = Ui.Label("Each place judged against the strongest enemy seen near it lately (or a raid of two ships). "
                            + "Covered: enough help is there or within a day. Thin: help would come, but too slowly or too weakly. Open: nobody would come", 12, Ui.Dim);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        note.CustomMinimumSize = new Vector2(420, 0);
        pane.AddChild(note);
        var places = Judged(w);
        if (places.Count == 0) pane.AddChild(Ui.Label("You hold no planets yet", 13, Ui.Dim));
        foreach (var j in places.OrderBy(j => j.State == CoverState.Open ? 0 : j.State == CoverState.Thin ? 1 : 2).ThenBy(j => j.Place.Kind).ThenBy(j => j.Place.Name, StringComparer.Ordinal))
        {
            var key = j.Place.Key;
            var col = Column(3);
            var top = Row(8);
            top.AddChild(Ui.Label(j.Place.Name, 15, _placeKey == key ? Ui.Player : Ui.Text));
            top.AddChild(Ui.Label(j.Place.Worth, 12, Ui.Dim));
            top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var state = Live(w2 => Judged(w2).Find(x => x.Place.Key == key) is { } x ? Coverage.StateName(x.State).ToUpperInvariant() : "", 13);
            _live.Add(w2 => { if (Judged(w2).Find(x => x.Place.Key == key) is { } x) state.AddThemeColorOverride("font_color", StateColor(x.State)); });
            top.AddChild(state);
            top.AddChild(CoverMenu(key));
            col.AddChild(top);
            col.AddChild(Live(w2 => Judged(w2).Find(x => x.Place.Key == key) is { } x ? x.Why : "", 12, Ui.Dim, wrap: true));
            var box = Box(col, 8);
            box.MouseFilter = MouseFilterEnum.Stop;
            box.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _placeKey = key;
            };
            pane.AddChild(box);
        }
    }

    /// <summary>Cover it ▾: the fleets that could, quickest first, with how long each takes and what its leaving uncovers. Picking one sends it.</summary>
    private MenuButton CoverMenu(string key)
    {
        var menu = new MenuButton { Text = "Cover it ▾", FocusMode = FocusModeEnum.None, TooltipText = "Send a fleet to cover this place: it flies there and holds it (a cargo run: it patrols the run)" };
        menu.AddThemeFontSizeOverride("font_size", 14);
        var offers = new List<CoverOffer>();
        menu.AboutToPopup += () =>
        {
            var popup = menu.GetPopup();
            popup.Clear();
            offers.Clear();
            if (Main.Instance.Sim?.World is not World w2 || Coverage.Find(w2, Side.Player, key) is not CoverPlace place) return;
            offers.AddRange(Coverage.Offers(w2, Side.Player, place));
            if (offers.Count == 0) { popup.AddItem("No fighting fleet is free to send", -1); popup.SetItemDisabled(0, true); return; }
            for (var i = 0; i < offers.Count; i++)
            {
                var o = offers[i];
                var text = o.Problem != null ? $"{o.Name}: can't ({o.Problem})"
                    : $"{o.Name}: {Coverage.Age(o.Hours ?? 0)} · strength {o.Power:0}" + (o.Leaves == "nothing" ? "" : $" · leaves {o.Leaves}");
                popup.AddItem(text, i);
                if (o.Problem != null) popup.SetItemDisabled(i, true);
            }
        };
        menu.GetPopup().IdPressed += pick => Main.Instance.RunGuarded("cover it", () =>
        {
            if (pick < 0 || pick >= offers.Count || offers[(int)pick].Problem != null) return;
            var o = offers[(int)pick];
            Order(new CoverPlaceCommand(o.FleetId, key), "cover it");
            _placeKey = key;
            Main.Instance.Toast($"{o.Name} goes to cover it");
        });
        return menu;
    }

    // ---- Strike planner ------------------------------------------------------------------------

    private StrikePreview? Preview(World w)
    {
        // Worked out again when the plan changes, and every few game hours as fleets and targets move.
        var key = $"{string.Join(",", _strikeIds)}|{_strikeBody}|{_strikeEnemy}|{_strikeArrival}|{_strikeGap}|{_strikeBurn}|{w.Tick / 6}|{_opsVersion}";
        if (key == _previewKey) return _preview;
        _previewKey = key;
        _preview = _strikeIds.Count == 0 && _strikeBody == null && _strikeEnemy == null ? null
            : Strikes.Preview(w, Side.Player, _strikeIds, _strikeBody, _strikeEnemy, _strikeArrival, _strikeGap, _strikeBurn);
        return _preview;
    }

    private void StrikePane(World w, VBoxContainer pane)
    {
        _strikeIds.RemoveAll(id => w.FindFleet(id) is not { ShipIds.Count: > 0 });
        var col = Column(8);
        var title = Row(8);
        title.AddChild(Heading(_strikeBody != null || _strikeEnemy != null ? $"Strike on {Strikes.TargetName(w, Side.Player, _strikeBody, _strikeEnemy)}" : "Plan a strike"));
        title.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        if (_strikeIds.Count > 0 || _strikeBody != null || _strikeEnemy != null)
            title.AddChild(Ui.SmallButton("Clear", () => { _strikeIds.Clear(); _strikeBody = null; _strikeEnemy = null; _strikePlanId = null; }, "Start a new plan"));
        col.AddChild(title);

        // Target: an enemy fleet as last seen, or a planet.
        var targets = new List<(string, string)> { ("", "Pick a target…") };
        foreach (var s in w.Sightings.Where(s => s.SeenBy == Side.Player && !s.Own && !Lanes.CargoSighting(s) && w.Hour - s.Hour <= Coverage.ThreatDays * 24).OrderBy(s => s.FleetId))
            targets.Add(($"f{s.FleetId}", $"{s.Name} ({s.Ships} ships), seen {(s.Visible ? "now" : Coverage.Age(w.Hour - s.Hour) + " ago")}"));
        foreach (var b in w.Bodies.Where(b => b.Kind is BodyKind.Planet or BodyKind.Moon).OrderBy(b => b.Owner == Side.Player ? 2 : b.Owner == null ? 1 : 0).ThenBy(b => b.Name, StringComparer.Ordinal))
            targets.Add(($"b{b.Id}", $"{b.Name}{(b.Owner is Side o && o != Side.Player ? " (enemy)" : b.Owner == Side.Player ? " (yours)" : "")}"));
        var current = _strikeEnemy is int e ? $"f{e}" : _strikeBody is int tb ? $"b{tb}" : "";
        var trow = Row(8);
        trow.AddChild(Ui.Label("Target", 13, Ui.Dim));
        trow.AddChild(Pick(targets, current, k =>
        {
            _strikeBody = k.StartsWith('b') ? int.Parse(k[1..]) : null;
            _strikeEnemy = k.StartsWith('f') ? int.Parse(k[1..]) : null;
        }, "Where the strike goes: an enemy fleet (where it was last seen, chased from there) or a planet"));
        col.AddChild(trow);

        // How they arrive, and how hard they burn.
        var arow = Row(4);
        foreach (var a in Enum.GetValues<StrikeArrival>())
        {
            var b = Ui.SmallButton(Strikes.ArrivalName(a), () => _strikeArrival = a, a switch
            {
                StrikeArrival.Together => "The fleets that would get there first ease off (or leave later) so all arrive together",
                StrikeArrival.AsSoonAsEach => "Each fleet goes at the burn below and arrives when it can",
                _ => "One wave after another, the gap below apart, in the order they can get there",
            });
            b.ToggleMode = true;
            b.ButtonPressed = _strikeArrival == a;
            arow.AddChild(b);
        }
        col.AddChild(arow);
        var orow = Row(8);
        orow.AddChild(Ui.Label("Burn", 13, Ui.Dim));
        orow.AddChild(Pick(new List<(double, string)> { (0.4, "0.4 (thrifty)"), (0.6, "0.6"), (0.8, "0.8"), (1.0, "1.0 (all out)") }, _strikeBurn, v => _strikeBurn = v,
            "Share of top speed: faster gets there sooner but burns more fuel"));
        if (_strikeArrival == StrikeArrival.Waves)
        {
            orow.AddChild(Ui.Label("Waves apart", 13, Ui.Dim));
            orow.AddChild(Pick(new List<(double, string)> { (6, "6 h"), (12, "12 h"), (24, "1 d"), (48, "2 d") }, _strikeGap, v => _strikeGap = v, "Time between waves"));
        }
        col.AddChild(orow);

        // The fleets, each with its burn and arrival.
        if (_strikeIds.Count == 0) col.AddChild(Ui.Label("Tick fleets in the list on the left to add them", 13, Ui.Dim));
        foreach (var fid in _strikeIds.ToList())
        {
            var id = fid;
            var row = Row(8);
            var name = Ui.Label(w.FindFleet(id)?.Name ?? "?", 14);
            name.CustomMinimumSize = new Vector2(140, 0);
            row.AddChild(name);
            var info = Live(w2 =>
            {
                if (Preview(w2)?.Legs.Find(l => l.FleetId == id) is not { } leg) return "";
                if (leg.Problem != null) return $"can't: {leg.Problem}";
                var leaves = leg.Request is { } r && r.DepartTick > w2.Tick ? $" · leaves in {Coverage.Age(r.DepartTick - w2.Tick)}" : "";
                return $"{(w2.FindFleet(id) is Fleet g ? $"{g.ShipIds.Count} ships · " : "")}burn {leg.Burn:0.0#}{leaves} · arrives {GameTime.Format(leg.ArriveHour)}{(leg.Supply ? " · supply" : "")}";
            }, 12, Ui.Dim, wrap: true);
            var infoCol = Column(0);
            infoCol.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            infoCol.AddChild(info);
            row.AddChild(infoCol);
            row.AddChild(Ui.SmallButton("Drop", () => _strikeIds.Remove(id), "Take this fleet out of the plan"));
            col.AddChild(row);
        }

        // Odds (a guess), supply, and what leaving uncovers.
        col.AddChild(Live(w2 =>
        {
            if (Preview(w2) is not { } p) return "";
            if (p.Problem != null) return p.Problem;
            var odds = p.Odds is double o ? $"Together they {(o >= 1 ? "outgun" : "are outgunned by")} what we last saw: {(o >= 1 ? o : 1 / o):0.#} to 1" : "Nothing seen there to fight";
            return $"{odds}\n({p.EnemyNote})";
        }, 13, wrap: true));
        col.AddChild(Live(w2 => Preview(w2)?.SupplyNote ?? "", 13, Ui.Dim, wrap: true));
        col.AddChild(Live(w2 => Preview(w2) is { Problem: null } p ? $"Leaving now uncovers: {p.Uncovers}" : "", 13, Warn, wrap: true));

        var buttons = Row(8);
        buttons.AddChild(Ui.SmallButton("Send it", () => SendStrike(), "Send each fleet its order now (works while paused; under light lag each order travels to its fleet)"));
        buttons.AddChild(Ui.SmallButton(_strikePlanId != null ? "Save over plan" : "Hold until I say (save)", () => SaveStrike(), "Save the plan to send later: the arrival times are worked out again when it is sent"));
        if (_strikePlanId != null)
            buttons.AddChild(Ui.SmallButton("Save as new", () => { _strikePlanId = null; SaveStrike(); }, "Save a copy as a new plan"));
        col.AddChild(buttons);
        pane.AddChild(Box(col));

        // Saved plans.
        var saved = Column(6);
        saved.AddChild(Heading("Saved plans"));
        var plans = w.StrikePlans.Where(p => p.Side == Side.Player).OrderBy(p => p.Id).ToList();
        if (plans.Count == 0) saved.AddChild(Ui.Label("None yet. Hold until I say saves a plan here; it can be sent again later", 12, Ui.Dim));
        foreach (var p in plans)
        {
            var pid = p.Id;
            var row = Row(6);
            var label = Ui.Label($"{p.Name} · {p.FleetIds.Count} fleets · {Strikes.ArrivalName(p.Arrival).ToLowerInvariant()}" + (p.TimesSent > 0 ? $" · sent {p.TimesSent}×" : ""), 13,
                _strikePlanId == pid ? Ui.Player : Ui.Text);
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            row.AddChild(label);
            row.AddChild(Ui.SmallButton("Open", () => LoadPlan(pid), "Load it into the planner to look it over or change it"));
            row.AddChild(Ui.SmallButton("Send again", () => { LoadPlan(pid); SendStrike(); }, "Send it now with arrival times worked out afresh"));
            row.AddChild(Ui.SmallButton("Delete", () => { Order(new DeleteStrikePlanCommand(Side.Player, pid), "delete plan"); if (_strikePlanId == pid) _strikePlanId = null; }, "Forget this plan"));
            saved.AddChild(row);
        }
        pane.AddChild(Box(saved));
    }

    private void LoadPlan(int planId)
    {
        if (_screen.World.StrikePlans.Find(p => p.Id == planId) is not StrikePlan p) return;
        _strikePlanId = p.Id;
        _strikeIds.Clear();
        _strikeIds.AddRange(p.FleetIds.Where(id => _screen.World.FindFleet(id) is { ShipIds.Count: > 0 }));
        _strikeBody = p.TargetBodyId;
        _strikeEnemy = p.TargetFleetId;
        _strikeArrival = p.Arrival;
        _strikeGap = p.WaveGapHours;
        _strikeBurn = p.Burn;
        _previewKey = "";
        _opsPane = "strike";
    }

    private void SaveStrike()
    {
        if (_strikeIds.Count == 0 || _strikeBody == null && _strikeEnemy == null) { Main.Instance.Toast("Pick a target and tick some fleets first"); return; }
        var w = _screen.World;
        var name = _strikePlanId is int id && w.StrikePlans.Find(p => p.Id == id) is { } old ? old.Name : "";
        Order(new SaveStrikePlanCommand(Side.Player, name, _strikeIds.ToList(), _strikeBody, _strikeEnemy, _strikeArrival, _strikeGap, _strikeBurn, _strikePlanId), "save strike plan");
        _strikePlanId ??= w.StrikePlans.Where(p => p.Side == Side.Player).Select(p => (int?)p.Id).LastOrDefault();
        Main.Instance.Toast("Strike plan saved: send it from Saved plans when you are ready");
    }

    private void SendStrike()
    {
        var w = _screen.World;
        _previewKey = "";
        if (Preview(w) is not { } p || p.Problem != null) { Main.Instance.Toast(Preview(w)?.Problem ?? "Pick a target and tick some fleets first"); return; }
        var name = _strikePlanId is int pid && w.StrikePlans.Find(x => x.Id == pid) is { } plan ? plan.Name : $"Strike on {p.TargetName}";
        var sent = 0;
        foreach (var leg in p.Legs.Where(l => l.Problem == null && l.Request != null))
        {
            _screen.IssueNow(new StrikeLegCommand(leg.FleetId, leg.Request!, name, p.TargetName, leg.ArriveHour, _strikePlanId), "strike");
            sent++;
        }
        if (_strikePlanId is int planId) _screen.IssueNow(new MarkStrikeSentCommand(Side.Player, planId), "strike sent");
        _opsVersion++;
        var skipped = p.Legs.Count(l => l.Problem != null);
        Main.Instance.Toast($"{name}: {sent} fleet{(sent == 1 ? "" : "s")} sent" + (skipped > 0 ? $", {skipped} can't go" : "") + ". Each fleet shows its stage");
    }
}
