using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The Empire screen's tabs other than Overview: Needs you, Automation, Resources and Buildings. Built
/// into the panel's box by EmpirePanel when the tab's shape changes; numbers update through the live list.
/// </summary>
public sealed class EmpireTabs
{
    private const float Width = 520;
    private readonly GameScreen _screen;
    private readonly VBoxContainer _box;
    private readonly List<(Label Label, Func<World, string> Text)> _live;

    public EmpireTabs(GameScreen screen, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live)
    {
        _screen = screen;
        _box = box;
        _live = live;
    }

    // ---- Needs you ------------------------------------------------------------------------------

    public static string NeedsShape(World w) => string.Join(",", Economy.Of(w, Side.Player).Needs.Select(n => n.Key + n.Level))
                                                + "|" + Economy.Of(w, Side.Player).AutoNotes.Count
                                                + "|" + string.Join(",", w.Events.Where(e => e.Side == Side.Player).Select(e => $"{e.Id}{e.Call.Picked}{e.Hit}{e.Done}"));

    private static Color LevelColor(NeedLevel level) => level switch
    {
        NeedLevel.Urgent => Ui.Danger,
        NeedLevel.Soon => Ui.Rival,
        NeedLevel.Advice => Ui.Player,
        _ => Ui.Dim,
    };

    public void Needs(World w)
    {
        var state = Economy.Of(w, Side.Player);
        EventsSection(w);
        Heading("Needs you");
        if (state.Needs.Count == 0) Text("Nothing needs you just now. Automation handles the areas set to Auto (see the Automation tab).");
        foreach (var level in Enum.GetValues<NeedLevel>())
        {
            var items = state.Needs.Where(n => n.Level == level && !n.Key.StartsWith("event:", StringComparison.Ordinal)).ToList();
            if (items.Count == 0) continue;
            var head = Ui.Label(level switch
            {
                NeedLevel.Urgent => "Urgent",
                NeedLevel.Soon => "Soon",
                NeedLevel.Advice => "Advice (automation suggests)",
                _ => "News",
            }, 13, LevelColor(level));
            _box.AddChild(head);
            foreach (var n in items) NeedRow(w, n);
        }
        var notes = state.AutoNotes.TakeLast(8).Reverse().ToList();
        if (notes.Count > 0)
        {
            Heading("Auto handled");
            foreach (var note in notes) Text($"{GameTime.Format(note.Hour)} · {(NeedsUi.Worth(note.Area) ? "● " : "")}{note.Area}: {note.Text}", 12);
        }
    }

    // ---- Events ---------------------------------------------------------------------------------

    /// <summary>Events waiting for a call (every option, Auto's pick and the deadline), then what came of the last few.</summary>
    private void EventsSection(World w)
    {
        var open = Events.Open(w, Side.Player).OrderBy(e => e.Call.DueHour).ToList();
        var recent = w.Events.Where(e => e.Side == Side.Player && !e.Call.Open).OrderByDescending(e => e.Hour).Take(5).ToList();
        if (open.Count == 0 && recent.Count == 0) return;
        Heading("Events");
        foreach (var e in open)
        {
            var title = Ui.Label($"⚑ {Events.KindName(e.Kind)}: {e.Title}", 14, Ui.Rival);
            _box.AddChild(title);
            Text(e.Call.Text, 13, Ui.Text);
            var row = new HFlowContainer();
            row.AddThemeConstantOverride("h_separation", 6);
            _box.AddChild(row);
            for (var i = 0; i < e.Call.Options.Count; i++)
            {
                var option = i;
                var id = e.Id;
                var tip = i < e.Call.Effects.Count ? e.Call.Effects[i] : e.Call.Options[i];
                var b = Small(e.Call.Options[i] + (i == e.Call.AutoPick ? " (Auto)" : ""), () => _screen.IssueNow(new PickEventCommand(Side.Player, id, option), "event call"), tip);
                row.AddChild(b);
            }
            if (e.BodyId is int bodyId) row.AddChild(Small("Go", () => _screen.PickBody(bodyId), "Go to the planet"));
            else if (e.FleetId is int fleetId && w.FindFleet(fleetId) != null)
                row.AddChild(Small("Go", () => { _screen.Select(fleetId); _screen.Follow(fleetId, zoom: true); }, "Select the fleet and go there"));
            for (var i = 0; i < e.Call.Options.Count && i < e.Call.Effects.Count; i++) Text($"· {e.Call.Options[i]}: {e.Call.Effects[i]}", 12);
            var due = e.Call.DueHour;
            var auto = e.Call.Options[e.Call.AutoPick];
            var l = Ui.Label("", 12, Ui.Dim);
            _box.AddChild(l);
            _live.Add((l, w2 => due > w2.Hour ? $"Auto picks {auto} in {Format.Hours(due - w2.Hour)} if you don't" : ""));
        }
        foreach (var e in recent)
        {
            var pick = e.Call.Picked is int p ? e.Call.Options[p] + (e.Call.ByAuto ? " (Auto)" : "") : "";
            Text($"{GameTime.Format(e.Hour)} · {e.Title}: {pick}{(e.Outcome.Length > 0 ? " · " + e.Outcome : e.Hit ? "" : " · still to come")}", 12);
        }
    }

    /// <summary>
    /// New events for the player: a toast for each (click: the Needs you tab). Returns true to pause:
    /// an event waiting for a call with the Events area on Manual ("ask me and stop").
    /// </summary>
    public static bool WatchEvents(World w, HashSet<int> seen, Action<string, bool> toast)
    {
        var pause = false;
        foreach (var e in w.Events.Where(e => e.Side == Side.Player && !e.Done))
        {
            if (!seen.Add(e.Id)) continue;
            var stop = e.Call.Open && Stewards.LevelFor(w, Side.Player, AutoArea.Events) == AutoLevel.Manual;
            var text = e.Call.Open ? $"{Events.KindName(e.Kind)}: {e.Title}. Your call (Needs you); Auto picks {e.Call.Options[e.Call.AutoPick]} in {Format.Hours(e.Call.DueHour - w.Hour)}"
                : $"{Events.KindName(e.Kind)}: {e.Title}. Auto picked {e.Call.Options[e.Call.Picked ?? e.Call.AutoPick]}";
            toast(text, stop);
            pause |= stop;
        }
        return pause;
    }

    private void NeedRow(World w, NeedItem n)
    {
        var row = Row();
        var dot = Ui.Label("●", 12, LevelColor(n.Level));
        row.AddChild(dot);
        var text = Ui.Label(n.Text, 13);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.CustomMinimumSize = new Vector2(Width - 210, 0);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        if (n.Area is AutoArea area) text.TooltipText = $"Area: {area}";
        row.AddChild(text);
        var key = n.Key;
        if (n.Act != null)
            row.AddChild(Small(n.ActLabel ?? "Do it", () => _screen.IssueNow(new AcceptNeedCommand(Side.Player, key), "acting on a need"),
                "Give the order this suggests"));
        if (n.FleetId is int fleetId && w.FindFleet(fleetId) != null)
            row.AddChild(Small("Go", () => { _screen.Select(fleetId); _screen.Follow(fleetId, zoom: true); }, "Select the fleet and go there"));
        else if (n.BodyId is int bodyId)
            row.AddChild(Small("Go", () => _screen.PickBody(bodyId), "Go to the planet"));
        row.AddChild(NeedsUi.Menu("Snooze ▾", "Put it aside: an hour, a day, until it changes, or never for this kind", NeedsUi.SnoozeItems(_screen, n)));
    }

    // ---- Automation -----------------------------------------------------------------------------

    public static string AutomationShape(World w)
    {
        var state = Economy.Of(w, Side.Player);
        return string.Join(",", Stewards.Areas.Select(a => Stewards.LevelFor(w, Side.Player, a)))
               + "|" + string.Join(",", state.AutoOverrides.Select(o => $"{o.Area}{o.BodyId}{o.FleetId}{o.Level}"))
               + "|" + string.Join(",", state.PullDefaults.Select(p => p.Key + p.Value));
    }

    private static readonly string[] Situations = { "home", "colony", "general", "raid", "outnumbered", "support" };

    private static string AreaTip(AutoArea a) => a switch
    {
        AutoArea.Building => "Planet buildings: what each planet lays down next",
        AutoArea.Workers => "Moving people where buildings need staff",
        AutoArea.Transport => "Freighters carrying goods where they run short",
        AutoArea.Settling => "Settling empty planets and moons",
        AutoArea.Research => "Keeping research teams busy",
        AutoArea.Shipyard => "Ships to build (lost warships are always offered, never rebuilt by themselves)",
        AutoArea.Supply => "Supply ships and refill runs for fleets",
        AutoArea.Refits => "Sending fleets to the yard to refit to the current designs",
        AutoArea.Battle => "The pull-back rule each fleet uses (fleets set by hand keep theirs)",
        AutoArea.Crews => "Spreading veteran crews across a fleet, and the best crew on the flagship",
        AutoArea.Boarding => "The calls during a boarding (seal the bridge, hostages, scuttle charge); Manual or Advise asks you and pauses",
        AutoArea.Ground => "The ground war plan and its calls (the guns, spare the mines, the last stand); Manual or Advise asks you and pauses",
        AutoArea.AfterBattle => "After a battle: rescue, capture, salvage, then the way home; Manual or Advise waits for your pick of the way home",
        _ => "",
    };

    public void Automation(World w)
    {
        var state = Economy.Of(w, Side.Player);
        Heading("Automation");
        Text("Auto does it, Advise suggests it under Needs you, Manual leaves it to you. Orders you give by hand are never undone. "
             + "A planet or fleet can have its own level (from its panel).", 12);
        foreach (var area in Stewards.Areas)
        {
            var row = Row();
            var a = area;
            var name = Ui.Label(area.ToString(), 13);
            name.CustomMinimumSize = new Vector2(100, 0);
            name.TooltipText = AreaTip(area);
            row.AddChild(name);
            var now = Stewards.LevelFor(w, Side.Player, area);
            foreach (var level in Enum.GetValues<AutoLevel>())
            {
                var l = level;
                var b = Small(level.ToString(), () => _screen.IssueNow(new SetAutoLevelCommand(Side.Player, a, l), "setting automation"),
                    $"{area}: {level}. {AreaTip(area)}");
                b.CustomMinimumSize = new Vector2(70, 26);
                if (level == now) b.Modulate = Ui.Player;
                row.AddChild(b);
            }
            var score = Ui.Label("", 11, Ui.Dim);
            row.AddChild(score);
            _live.Add((score, w2 => Economy.Of(w2, Side.Player).AutoScores.TryGetValue(a, out var sc) && sc.Good + sc.Bad > 0
                ? $"judged {sc.Good} good, {sc.Bad} poor" : ""));
        }
        if (state.AutoOverrides.Count > 0)
        {
            Heading("Own levels for planets and fleets");
            foreach (var o in state.AutoOverrides)
            {
                var row = Row();
                var who = o.FleetId is int f ? w.FindFleet(f)?.Name ?? "a fleet" : o.BodyId is int b ? w.FindBody(b)?.Name ?? "a planet" : "?";
                var label = Ui.Label($"{who}: {o.Area} {o.Level}", 13);
                label.CustomMinimumSize = new Vector2(Width - 60, 0);
                row.AddChild(label);
                var ov = o;
                row.AddChild(Small("✕", () => _screen.IssueNow(new SetAutoLevelCommand(Side.Player, ov.Area, null, ov.BodyId, ov.FleetId), "clearing an override"),
                    "Back to the area's level"));
            }
        }
        Heading("Pull-back defaults");
        Text("The rule Auto gives fleets in each situation. Change it here, or keep changing fleets by hand and it will offer to learn.", 12);
        foreach (var situation in Situations)
        {
            var row = Row();
            var label = Ui.Label(Format.Title(BattleRules.SituationName(situation)), 13);
            label.CustomMinimumSize = new Vector2(170, 0);
            row.AddChild(label);
            var pick = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(200, 26) };
            pick.AddThemeFontSizeOverride("font_size", 12);
            var presets = Enum.GetValues<PullPreset>().Where(p => p != PullPreset.Custom).ToList();
            var learned = state.PullDefaults.ContainsKey(situation);
            pick.AddItem($"Built in ({PresetName(BattleRules.BuiltIn(situation))})");
            foreach (var p in presets) pick.AddItem(PresetName(p));
            pick.Selected = learned ? 1 + presets.IndexOf(state.PullDefaults[situation]) : 0;
            var s = situation;
            pick.ItemSelected += index => _screen.IssueNow(new SetPullDefaultCommand(Side.Player, s, index == 0 ? null : presets[(int)index - 1]), "setting a pull-back default");
            row.AddChild(pick);
        }
    }

    public static string PresetName(PullPreset p) => p switch
    {
        PullPreset.Cautious => "Cautious",
        PullPreset.Standard => "Standard",
        PullPreset.FightOn => "Fight on",
        PullPreset.ToTheLast => "To the last",
        _ => p.ToString(),
    };

    // ---- Resources ------------------------------------------------------------------------------

    public void Resources(World w)
    {
        Heading("Goods");
        Text("Across your planets: held now, made and used a day. Red: short (a building or order waits for it, or it runs down).", 12);
        var grid = new GridContainer { Columns = 5 };
        grid.AddThemeConstantOverride("h_separation", 14);
        _box.AddChild(grid);
        foreach (var head in new[] { "Good", "Held", "Made/day", "Used/day", "Net" })
            grid.AddChild(Ui.Label(head, 12, Ui.Dim));
        foreach (var g in Colonies.Goods)
        {
            var id = g.Id;
            var name = Ui.Label(g.Name, 13);
            name.CustomMinimumSize = new Vector2(150, 0);
            grid.AddChild(name);
            Cell(grid, w2 => $"{Mine(w2).Sum(b => Colonies.Held(b, id)):0}");
            Cell(grid, w2 => $"{Mine(w2).Sum(b => Colonies.MadePerDay(b, id)):0.#}");
            Cell(grid, w2 => $"{Mine(w2).Sum(b => Colonies.UsePerDay(b, id)):0.#}");
            Cell(grid, w2 =>
            {
                var net = Mine(w2).Sum(b => Colonies.MadePerDay(b, id) - Colonies.UsePerDay(b, id));
                return $"{(net >= 0 ? "+" : "")}{net:0.#}";
            });
            var label = name;
            _live.Add((label, w2 =>
            {
                label.AddThemeColorOverride("font_color", Planner.MaterialShort(w2, Side.Player, id) ? Ui.Danger : Ui.Text);
                return g.Name;
            }));
        }
        Heading("By planet");
        foreach (var b in Mine(w).OrderByDescending(b => b.Home).ThenBy(b => b.Id))
        {
            var body = b;
            var label = Ui.Label("", 12, Ui.Dim);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(Width, 0);
            _box.AddChild(label);
            _live.Add((label, _ => $"{body.Name}: " + (body.Stock.Count == 0 ? "nothing held"
                : string.Join(", ", body.Stock.Where(kv => kv.Value >= 0.5).OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Value:0} {Colonies.GoodName(kv.Key).ToLowerInvariant()}")))));
        }
    }

    private static IEnumerable<Body> Mine(World w) => w.Bodies.Where(b => b.Owner == Side.Player);

    private void Cell(GridContainer grid, Func<World, string> text)
    {
        var l = Ui.Label("", 13);
        l.HorizontalAlignment = HorizontalAlignment.Right;
        l.CustomMinimumSize = new Vector2(64, 0);
        grid.AddChild(l);
        _live.Add((l, text));
    }

    // ---- Buildings ------------------------------------------------------------------------------

    public void Buildings(World w)
    {
        Heading("Buildings");
        Text("Lay them down from a planet's screen. Dim ones need research first.", 12);
        foreach (var d in (Planets.D.Buildings ?? new()).Where(d => d.Buildable).OrderBy(d => d.Needs != null && !Tech.Has(w, Side.Player, d.Needs)).ThenBy(d => d.Cost))
        {
            var open = d.Needs == null || Tech.Has(w, Side.Player, d.Needs);
            var parts = new List<string> { $"{d.Cost:0} Industry, {d.Days:0} d", $"{d.W}x{d.H} {d.Terrain}" };
            if (d.StaffK > 0) parts.Add($"{d.StaffK:0.#}k staff");
            if (d.Power > 0) parts.Add($"makes {d.Power:0} power");
            if (d.Power < 0) parts.Add($"uses {-d.Power:0} power");
            if (d.Materials is { Count: > 0 } m) parts.Add("needs " + string.Join(", ", m.Select(kv => $"{kv.Value:0} {Colonies.GoodName(kv.Key).ToLowerInvariant()}")));
            if (d.Makes is { Count: > 0 } mk) parts.Add("makes " + string.Join(", ", mk.Select(kv => $"{kv.Value:0.#} {Colonies.GoodName(kv.Key).ToLowerInvariant()}/d")));
            if (d.Uses is { Count: > 0 } us) parts.Add("uses " + string.Join(", ", us.Select(kv => $"{kv.Value:0.#} {Colonies.GoodName(kv.Key).ToLowerInvariant()}/d")));
            if (!open) parts.Add($"research {GameData.Research(d.Needs!).Name}");
            var name = Ui.Label(d.Name, 14, open ? Ui.Text : Ui.Dim);
            _box.AddChild(name);
            Text($"{d.Effect}\n{string.Join(" · ", parts)}", 12, open ? Ui.Dim : new Color(Ui.Dim, 0.6f));
        }
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private void Heading(string text) => _box.AddChild(Ui.Label(text, 15, Ui.Player));

    private void Text(string text, int size = 13, Color? color = null)
    {
        var l = Ui.Label(text, size, color ?? Ui.Dim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(Width, 0);
        _box.AddChild(l);
    }

    private HBoxContainer Row()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        _box.AddChild(row);
        return row;
    }

    private static Button Small(string text, Action pressed, string tip)
    {
        var b = Ui.SmallButton(text, pressed, tip);
        b.CustomMinimumSize = new Vector2(0, 26);
        b.AddThemeFontSizeOverride("font_size", 12);
        return b;
    }
}
