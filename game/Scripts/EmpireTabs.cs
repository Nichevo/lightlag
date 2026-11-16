using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Parts of the old Empire screen that live on elsewhere: the Needs you list (the badge, with the tray off),
/// the goods totals (top of Supply), the pull-back defaults (the fleets screen's War tab) and the automation
/// shape the tray's Automation menu uses. Built into a box; numbers update through the live list.
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
        if (state.Needs.Count == 0) Text("Nothing needs you just now. Automation handles the areas set to Auto (the tray's Automation menu sets which).");
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

    /// <summary>The rule Auto gives fleets in each situation (on the fleets screen's War tab).</summary>
    public void PullDefaults(World w)
    {
        var state = Economy.Of(w, Side.Player);
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

    // ---- Goods ------------------------------------------------------------------------------

    public void Goods(World w, float width)
    {
        Heading("Goods across your planets");
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
