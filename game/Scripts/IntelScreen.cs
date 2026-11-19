using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The intelligence screen (I): what you know about the rival and how sure you are. It fills the space under the
/// top bar. Tabs: Overview (a dashboard: likely next move with Show on map and Guard, estimates against yours,
/// intel level, race, their file on you, latest notes), Their plans (cards and a listening map), Fleets and lanes,
/// Planets, Technology (their kit against yours, their ideas you are learning with Queue now, ways to learn more,
/// their research overheard at level 4) and, with three races, Contact. Old facts are greyed with their age, so a
/// guess is never mistaken for a fact.
/// </summary>
public partial class IntelScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _root = null!;
    private VBoxContainer _box = null!;
    private Container _at = null!;
    private Tab _tab = Tab.Overview;
    private string _shape = "";
    private Side? _about;
    private bool _traits, _fileOpen;

    // The offer being drawn up on the Contact tab.
    private GiftKind _gift = GiftKind.Map;
    private string? _idea;
    private double _amount;
    private AskKind _ask = AskKind.None;
    private int? _askBody;
    private bool _broadcast;

    public enum Tab { Overview, Plans, Fleets, Planets, Technology, Contact }

    /// <summary>A tab to open on the next refresh (an alert's click), then cleared.</summary>
    public static Tab? Ask;

    private static readonly Color Old = new(0.55f, 0.6f, 0.68f, 0.55f);
    private static readonly Color Gold = new("ffd27a");
    private static readonly Color CardBg = new("121b2b");
    private static readonly Color CardEdge = new("22314a");
    private static readonly Color GoldEdge = new("6a5a2a");

    public IntelScreen() : this(null!) { }
    public IntelScreen(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        var box = new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.045f, 0.97f) };
        box.SetContentMarginAll(16);
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
        // Under the top bar, like Fleets and Research, so the menu bar stays in view.
        var under = new Vector2(0, Hud.ContentTop);
        if (Position != under || Size != view - under) { Position = under; Size = view - under; }
        if (_about is not Side pick || pick == Side.Player || !Sides.InPlay(w).Contains(pick)) _about = Sides.MainEnemy(w, Side.Player);
        var about = _about!.Value;
        var f = Intel.Of(w, Side.Player, about);
        if (Ask is Tab asked) { _tab = asked; Ask = null; }
        if (_tab == Tab.Contact && !Sides.Three(w)) _tab = Tab.Overview;
        var me = Economy.Of(w, Side.Player);
        var shape = $"{_tab}|{f.Intercepts.Count(i => i.HeardHour <= w.Hour)}|{f.Intercepts.Sum(i => i.Times)}|{about}|{_traits}{_fileOpen}"
                    + $"|{(_tab == Tab.Contact ? $"{Contact.Score(w, Side.Player, about):0}|{w.Offers.Count(o => o.Known[0])}|{w.Offers.Count(o => o.Landed)}|{_gift}{_idea}{_amount}{_ask}{_askBody}{_broadcast}" : "")}"
                    + $"|{(int)(w.Hour / 6)}|{f.Points:0}|{f.Fleets.Count}|{f.Tech.Count}|{f.Reach.Count}|{me.RivalIdeas.Count}|{me.Researched.Count}"
                    + $"|{w.Orders.Count(o => o.Side == Side.Player)}|{w.Fleets.Count(x => x.Side == Side.Player && x.Flight != null)}|{w.Light.InTransit.Count}|{view}";
        if (shape == _shape || Ui.MouseHeld) { if (_map != null && IsInstanceValid(_map)) _map.Show(w, about); return; }
        _shape = shape;
        Ui.ClearChildren(_root);
        _map = null;
        Header(w, f, about);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        _box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_box);
        _root.AddChild(scroll);
        _at = _box;
        var level = Intel.Level(f);
        switch (_tab)
        {
            case Tab.Overview: Overview(w, f, level); break;
            case Tab.Plans: PlansTab(w, f, level); break;
            case Tab.Fleets: Fleets(w, f, level); break;
            case Tab.Planets: PlanetsTab(w, f); break;
            case Tab.Technology: Technology(w, f, level); break;
            case Tab.Contact: ContactTab(w, f); break;
        }
    }

    private ListenMap? _map;

    private void Header(World w, IntelFile f, Side about)
    {
        var level = Intel.Level(f);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 14);
        head.AddChild(Ui.Label("Intelligence", 22, Ui.Player));
        var rival = w.Minds.FirstOrDefault(m => m.Side == about);
        head.AddChild(Ui.Label(Races.Of(w, about).Name + (rival != null ? $" ({rival.Difficulty})" : ""), 15, Ui.SideColor(about)));
        var status = Ui.Label($"level {level} · {Intel.LevelNames[level]}" + (w.Hour - f.LastContactHour < 48 ? ", in contact" : ", fading without contact"), 13, Ui.Dim);
        head.AddChild(status);
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        foreach (var t in Enum.GetValues<Tab>())
        {
            if (t == Tab.Contact && !Sides.Three(w)) continue; // offers only come with three races
            var tab = t;
            var b = Ui.SmallButton(TabName(t) + (Flag(w, f, t) ? " ●" : ""), () => { _tab = tab; _shape = ""; }, TabTip(t));
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Modulate = t == _tab ? Ui.Player : new Color(1, 1, 1, 0.75f);
            head.AddChild(b);
        }
        head.AddChild(Ui.SmallButton("✕", () => _screen.ShowIntel = false, "Close (I or Esc)"));
        _root.AddChild(head);
        if (!Sides.Three(w)) return;
        // Two rivals: a file on each.
        var rivals = new HBoxContainer();
        rivals.AddThemeConstantOverride("separation", 4);
        rivals.AddChild(Ui.Label("File on:", 13, Ui.Dim));
        foreach (var r in Sides.Enemies(w, Side.Player))
        {
            var rr = r;
            var b = Ui.SmallButton($"{Races.Of(w, r).Name}{(Sides.Out(w, r) ? " (out)" : "")} · level {Intel.Level(Intel.Of(w, Side.Player, r))}",
                () => { _about = rr; _shape = ""; }, "Switch to your file on this rival");
            b.AddThemeFontSizeOverride("font_size", 13);
            b.Modulate = r == about ? Ui.SideColor(r) : new Color(1, 1, 1, 0.7f);
            rivals.AddChild(b);
        }
        _root.AddChild(rivals);
    }

    /// <summary>A gold dot on a tab with news: a plan heard in the last day, or an unlocked idea ready to queue.</summary>
    private static bool Flag(World w, IntelFile f, Tab t) => t switch
    {
        Tab.Plans => Intentions.Heard(w, Side.Player, f.About).Any(i => i.Kind != PlanKind.Research && w.Hour - i.HeardHour < 24),
        Tab.Technology => RivalTech.Learning(w, Side.Player).Any(l => l.Stage == RivalStage.Unlocked && !Queued(w, l.Id)),
        _ => false,
    };

    private static string TabName(Tab t) => t switch
    {
        Tab.Plans => "Their plans",
        Tab.Fleets => "Fleets and lanes",
        _ => t.ToString(),
    };

    private static string TabTip(Tab t) => t switch
    {
        Tab.Overview => "Next move, estimates against yours, intel level, race and what they know of you",
        Tab.Plans => "Plans overheard in their order traffic, and a map of where you can hear them",
        Tab.Fleets => "Their fleets as last seen, their supply lanes and their raids on your cargo",
        Tab.Planets => "Their planets: people and industry when looked at",
        Tab.Technology => "Their kit against yours, their ideas you are learning, and how to learn more",
        _ => "Their tongue, and offers (three races only)",
    };

    // ---- Layout helpers ---------------------------------------------------------------------------

    private void Section(string text) => _at.AddChild(Ui.Label(text.ToUpperInvariant(), 11, Ui.Dim));

    private void Line(string text, Color? color = null, int size = 14, string tip = "") => Text(_at, text, color, size, tip);

    private static Label Text(Control parent, string text, Color? color = null, int size = 13, string tip = "")
    {
        var l = Ui.Label(text, size, color ?? Ui.Text);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(120, 0);
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (tip.Length > 0) { l.TooltipText = tip; l.MouseFilter = MouseFilterEnum.Pass; }
        parent.AddChild(l);
        return l;
    }

    private static Label Caption(Control parent, string text) { var l = Ui.Label(text.ToUpperInvariant(), 11, Ui.Dim); parent.AddChild(l); return l; }

    /// <summary>A card: a rounded box with an optional small-caps title.</summary>
    private static VBoxContainer Card(Control parent, string? title, Color? edge = null, Color? bg = null)
    {
        var p = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat { BgColor = bg ?? CardBg, BorderColor = edge ?? CardEdge };
        sb.SetBorderWidthAll(1);
        sb.SetCornerRadiusAll(8);
        sb.SetContentMarginAll(12);
        p.AddThemeStyleboxOverride("panel", sb);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        p.AddChild(v);
        parent.AddChild(p);
        if (title != null) Caption(v, title);
        return v;
    }

    private static HBoxContainer Row(Control parent, int separation = 8)
    {
        var r = new HBoxContainer();
        r.AddThemeConstantOverride("separation", separation);
        parent.AddChild(r);
        return r;
    }

    /// <summary>Columns side by side, each a VBox with this share of the width.</summary>
    private VBoxContainer[] Columns(params float[] ratios)
    {
        var row = Row(_box, 14);
        return ratios.Select(r =>
        {
            var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = r };
            v.AddThemeConstantOverride("separation", 12);
            row.AddChild(v);
            return v;
        }).ToArray();
    }

    private static Button Act(string text, Action pressed, string tip, bool on = true)
    {
        var b = Ui.SmallButton(text, pressed, tip);
        b.AddThemeFontSizeOverride("font_size", 12);
        b.CustomMinimumSize = new Vector2(0, 26);
        b.Disabled = !on;
        return b;
    }

    private Button MapLink(Action go, string tip = "Close this screen and centre the map on it") => Act("Map ▸", go, tip);

    private GridContainer Table(Control parent, params string[] heads)
    {
        var g = new GridContainer { Columns = heads.Length };
        g.AddThemeConstantOverride("h_separation", 18);
        g.AddThemeConstantOverride("v_separation", 4);
        foreach (var h in heads) g.AddChild(Ui.Label(h, 11, Ui.Dim));
        parent.AddChild(g);
        return g;
    }

    private static void Cell(GridContainer g, string text, Color color) => g.AddChild(Ui.Label(text, 13, color));

    private static Color Fade(double sure) => sure >= 0.6 ? Ui.Text : sure >= 0.25 ? new Color(Ui.Text, 0.7f) : Old;

    private static string Ago(World w, double hour) => w.Hour - hour < 1 ? "now" : Format.Hours(w.Hour - hour);

    private static bool Queued(World w, string id) => w.Orders.Any(o => o.Side == Side.Player && o.Kind == OrderKind.Research && o.Key == id);

    // ---- Overview -----------------------------------------------------------------------------------

    private void Overview(World w, IntelFile f, int level)
    {
        var cols = Columns(1.1f, 1.3f, 1f);
        NextMove(w, f, cols[0]);
        Habits(w, f, level, cols[0]);
        Estimates(w, f, level, cols[1]);
        LevelCard(w, f, level, cols[1]);
        RaceCard(w, f, cols[2]);
        TheirFileCard(w, f, cols[2]);
        NotesCard(w, f, cols[2]);
    }

    private void NextMove(World w, IntelFile f, Control col)
    {
        var next = Intentions.Likeliest(w, Side.Player, f.About);
        var card = Card(col, "Likely next move", next != null ? GoldEdge : null, next != null ? new Color("151f31") : null);
        if (next == null)
        {
            Text(card, "Nothing overheard lately.", Ui.Dim);
            Text(card, "Plans are heard when your fleets or planets are near its fleets or its command: see Their plans for the map.", Ui.Dim, 12);
            card.AddChild(Act("Their plans ▸", () => { _tab = Tab.Plans; _shape = ""; }, "Everything heard, and where you can listen"));
            return;
        }
        Text(card, Intentions.What(next), Gold, 18);
        Text(card, $"{Format.Title(Intentions.When(w, next))}" + (next.Ships > 0 ? $" · {next.Ships} ship{(next.Ships == 1 ? "" : "s")}" : "")
                   + (next.FleetName.Length > 0 ? $" · {next.FleetName}" : ""), Ui.Text, 13);
        var sure = Intentions.SureNow(w, next);
        var bar = Row(card);
        bar.AddChild(new StepBar((1, sure, Gold)));
        bar.AddChild(Ui.Label($"{sure:P0} sure · heard {Ago(w, next.HeardHour)} ago" + (next.Times > 1 ? $" · {next.Times} times" : ""), 12, Ui.Dim));
        var acts = Row(card);
        if (next.BodyId is int b) acts.AddChild(MapLink(() => _screen.ShowBodyOnMap(b)));
        if (next.BodyId is int target && w.FindBody(target) is Body body && body.Owner == Side.Player && next.Kind is PlanKind.Attack or PlanKind.Raid)
            Guard(w, card, acts, body);
        acts.AddChild(Act("All plans ▸", () => { _tab = Tab.Plans; _shape = ""; }, "Everything heard, and where you can listen"));
        var also = Intentions.Heard(w, Side.Player, f.About).Where(i => i != next && i.Kind != PlanKind.Research).Take(2).ToList();
        if (also.Count > 0)
        {
            Caption(card, "Also heard");
            foreach (var i in also) Text(card, Intentions.Line(w, i), Fade(Intentions.SureNow(w, i)), 12);
        }
    }

    /// <summary>
    /// The Guard button: send the nearest free warship fleet to hold orbit at the planet a plan names. Its stage shows
    /// on the card (order on its way, flying, there), or why it can't (no free warships).
    /// </summary>
    private void Guard(World w, VBoxContainer card, HBoxContainer acts, Body body)
    {
        var pick = Intentions.GuardPick(w, Side.Player, body.Id);
        var sent = w.Light.InTransit.Concat(w.Light.Held).Where(t => t.Side == Side.Player).Select(t => t.Command).OfType<PlanFlightCommand>()
            .FirstOrDefault(c => c.Request.TargetBodyId == body.Id);
        if (sent != null)
        {
            acts.AddChild(Act($"Guard {body.Name}", () => { }, "Order sent", on: false));
            Text(card, $"Guard order to {w.FindFleet(sent.FleetId)?.Name ?? "a fleet"} on its way at light speed", Gold, 12);
            return;
        }
        if (pick.Already)
        {
            acts.AddChild(Act($"Guard {body.Name}", () => { }, pick.Text, on: false));
            Text(card, pick.Text, Ui.Player, 12);
            return;
        }
        var fleet = pick.Fleet;
        acts.AddChild(Act($"Guard {body.Name}", () =>
        {
            if (fleet == null) return;
            _screen.IssueNow(new PlanFlightCommand(fleet.Id, new FlightRequest(body.Id, body.Position, 0.6, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)), "guarding a planet");
            _shape = "";
        }, pick.Text, on: fleet != null));
        Text(card, pick.Text, fleet != null ? Ui.Dim : Ui.Warn, 12);
    }

    private void Habits(World w, IntelFile f, int level, Control col)
    {
        var card = Card(col, "Habits you have seen");
        if (f.Habits.Count == 0) Text(card, "Nothing yet: habits show after contact, raids and battles", Ui.Dim, 12);
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var (key, count) in f.Habits.OrderByDescending(h => h.Value))
        {
            var chip = new PanelContainer();
            var sb = new StyleBoxFlat { BgColor = new Color("1d2b42") };
            sb.SetCornerRadiusAll(10);
            sb.SetContentMarginAll(4);
            sb.ContentMarginLeft = sb.ContentMarginRight = 9;
            chip.AddThemeStyleboxOverride("panel", sb);
            chip.AddChild(Ui.Label($"{Intel.HabitText(key)} ×{count}", 12, Ui.Text));
            flow.AddChild(chip);
        }
        card.AddChild(flow);
        if (level >= 3 && Intel.Personality(f) is var (name, sure, counter))
            Text(card, $"Best guess: {name} ({sure:P0}). Counter: {counter}.", Gold, 13);
        else if (f.Habits.Count > 0)
            Text(card, "A best guess at its temperament comes at intel level 3 (Understood)", Ui.Dim, 12);
    }

    private void Estimates(World w, IntelFile f, int level, Control col)
    {
        var card = Card(col, "Estimates against yours");
        var legend = Row(card, 14);
        legend.AddChild(Ui.Label("■ theirs (range)", 11, Ui.SideColor(f.About)));
        legend.AddChild(Ui.Label("| yours", 11, Ui.Player));
        void Bar(string label, string text, double low, double high, double mine, string tip)
        {
            var row = Row(card, 10);
            var l = Ui.Label(label, 13, Ui.Text);
            l.CustomMinimumSize = new Vector2(110, 0);
            row.AddChild(l);
            var b = new RangeBar(low, high, mine, Ui.SideColor(f.About)) { TooltipText = tip };
            row.AddChild(b);
            var t = Ui.Label(text, 13, Ui.Text);
            t.CustomMinimumSize = new Vector2(150, 0);
            row.AddChild(t);
        }
        var myShips = w.Fleets.Where(x => x.Side == Side.Player && Combat.Armed(w, x)).Sum(x => x.ShipIds.Count);
        var (low, high) = Intel.EstimatedWarships(w, Side.Player, f.About);
        if (level >= 1) Bar("Warships", $"{low} to {high} (you {myShips})", low, high, myShips, "From the fleets on file; the range narrows as your intel level rises");
        else Text(card, "Warships: unknown (reach level 1 to count them)", Ui.Dim);
        var (planets, industry) = Intel.EstimatedHoldings(w, Side.Player, f.About);
        var mine = Economy.Claimable(w).Where(b => b.Owner == Side.Player).ToList();
        Bar("Planets", $"{planets} (you {mine.Count}) · {f.Planets.Count(p => p.Hour > 0)} looked at", planets, planets, mine.Count, "Who holds a planet is plain to everyone");
        var myIndustry = mine.Sum(b => b.Industry);
        if (level >= 2) Bar("Industry", myIndustry > 0 ? $"about {industry / myIndustry:0.0}× yours" : $"about {industry:0}", industry * 0.9, industry * 1.1, myIndustry, "From the planets it holds, at their known worth");
        else Text(card, "Industry: reach level 2 to judge it", Ui.Dim, 13);
        var (control, plus) = Intel.EstimatedControl(w, Side.Player, f.About);
        var myControl = Planets.Control(w, Side.Player).Share;
        Bar("Control share", $"{control:P0} ± {plus * 100:0} (you {myControl:P0})", Math.Max(0, control - plus) * 100, (control + plus) * 100, myControl * 100, "Share of the system's worth held");
    }

    private void LevelCard(World w, IntelFile f, int level, Control col)
    {
        var card = Card(col, "Intel level");
        var steps = new (double, double, Color)[Intel.LevelPoints.Length - 1];
        for (var i = 1; i < Intel.LevelPoints.Length; i++)
        {
            var from = Intel.LevelPoints[i - 1];
            var to = Intel.LevelPoints[i];
            steps[i - 1] = (to - from, (f.Points - from) / (to - from), i <= level ? Ui.Player : new Color(Ui.Player, 0.55f));
        }
        card.AddChild(new StepBar(steps) { TooltipText = string.Join("\n", Enumerable.Range(0, Intel.LevelNames.Length).Select(i => $"{i} · {Intel.LevelNames[i]}: {Intel.LevelMeaning[i]}")) });
        Text(card, $"Level {level} · {Intel.LevelNames[level]}: {Intel.LevelMeaning[level]}", Ui.Player, 14);
        if (level < 4)
        {
            var need = Intel.LevelPoints[level + 1] - f.Points;
            var days = need / (Intel.SensorPointsPerFleetHour * 24);
            Text(card, $"Next, {Intel.LevelNames[level + 1]}: {Intel.LevelMeaning[level + 1]}", Ui.Text, 13);
            Text(card, $"About {need:0} more points: {days:0} days of sensor time on one of its fleets, or {Math.Ceiling(need / Intel.BattlePoints):0} battles, "
                       + "or salvage, prisoners and captures. Knowledge fades without contact.", Gold, 12);
        }
        else Text(card, "The top level: its plans and its current research can be overheard.", Gold, 12);
    }

    private void RaceCard(World w, IntelFile f, Control col)
    {
        // Who they are is known from the start (their broadcasts give it away); what that means comes from the race's traits.
        var race = Races.Of(w, f.About);
        var card = Card(col, $"Race: {race.Name}");
        Text(card, race.Style, Ui.Text, 13);
        var pros = race.Pros ?? new();
        var cons = race.Cons ?? new();
        card.AddChild(Act(_traits ? "Hide traits" : $"Traits ({pros.Count} strengths, {cons.Count} weaknesses) ▸", () => { _traits = !_traits; _shape = ""; }, "What the race is good and bad at"));
        if (!_traits) return;
        foreach (var p in pros) Text(card, $"+ {p}", Ui.Dim, 12);
        foreach (var c in cons) Text(card, $"- {c}", Ui.Dim, 12);
    }

    private void TheirFileCard(World w, IntelFile f, Control col)
    {
        var card = Card(col, "Their file on you (estimated)");
        var mine = w.Fleets.Where(x => x.Side == Side.Player).OrderBy(x => x.Id).ToList();
        var seen = mine.Count(x => f.SeenUs.ContainsKey(x.Id));
        Text(card, $"Their intel level on you: about {Intel.LevelFor(f.TheirPointsGuess)}", Ui.Text, 13,
            "Guessed from how often their fleets have been near yours and the battles you have fought");
        Text(card, $"Your fleets they have seen: {seen} of {mine.Count}" + (seen < mine.Count ? $" · {mine.Count - seen} still a surprise" : ""), seen < mine.Count ? Gold : Ui.Text, 13);
        var lost = w.LossSpots.Count(l => l.Side == Side.Player);
        Text(card, lost > 0 ? $"They have caught your cargo {lost} times: expect them to know those runs" : "Your runs they track: none known", Ui.Text, 12);
        card.AddChild(Act(_fileOpen ? "Hide fleets" : "Fleet by fleet ▸", () => { _fileOpen = !_fileOpen; _shape = ""; }, "Which of your fleets they have probably seen"));
        if (!_fileOpen) return;
        foreach (var x in mine)
            Text(card, f.SeenUs.TryGetValue(x.Id, out var h) ? $"{x.Name}: probably seen {Ago(w, h)} ago" : $"{x.Name}: probably still a surprise",
                f.SeenUs.ContainsKey(x.Id) ? Ui.Dim : Gold, 12);
        Text(card, "Built from where their scouts and raiders have been, so running cold and decoys matter.", Ui.Dim, 11);
    }

    private void NotesCard(World w, IntelFile f, Control col)
    {
        var spoils = WarTab.SpoilsLines(w);
        if (f.Notes.Count == 0 && spoils.Count == 0) return;
        var card = Card(col, "Latest notes");
        foreach (var n in Enumerable.Reverse(f.Notes).Take(5)) Text(card, $"{GameTime.Format(n.Hour)} · {n.Text}", Ui.Dim, 12);
        if (spoils.Count == 0) return;
        Caption(card, "Prisoners, captures and wrecks");
        Text(card, spoils[0], Ui.Text, 12);
        foreach (var line in spoils.Skip(1).Take(3)) Text(card, line, Ui.Dim, 12);
    }

    // ---- Their plans ---------------------------------------------------------------------------------

    /// <summary>
    /// The rival's plans as overheard in its order traffic, as cards: what, where, when, how sure, how old, with
    /// Show on map. Beside them the listening map: your ears, their reach, the rival's command and fleets.
    /// </summary>
    private void PlansTab(World w, IntelFile f, int level)
    {
        var cols = Columns(1.25f, 1f);
        var left = cols[0];
        Caption(left, "Heard in its order traffic: late and partial, never certain");
        var heard = Intentions.Heard(w, Side.Player, f.About).Where(i => i.Kind != PlanKind.Research).ToList();
        if (heard.Count == 0) Text(left, "Nothing that still counts. Plans are overheard when your fleets or planets are near its fleets or its command: see the map.", Ui.Dim);
        foreach (var i in heard)
        {
            var sure = Intentions.SureNow(w, i);
            var color = Fade(sure);
            var hot = i.Kind is PlanKind.Attack or PlanKind.Massing && sure >= 0.5;
            var card = Card(left, null, hot ? GoldEdge : null);
            var top = Row(card);
            var what = Text(top, Intentions.What(i), hot ? Gold : color, 15);
            what.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            top.AddChild(Ui.Label($"{sure:P0} sure", 12, color));
            Text(card, $"{Format.Title(Intentions.When(w, i))} · {(i.FleetName.Length > 0 ? i.FleetName : "fleet not caught")}"
                       + (i.Ships > 0 ? $" · {i.Ships} ships" : "") + $" · heard {Ago(w, i.HeardHour)} ago" + (i.Times > 1 ? $", {i.Times} times" : ""), color, 12);
            var acts = Row(card);
            if (i.BodyId is int b) acts.AddChild(Act("Target on map ▸", () => _screen.ShowBodyOnMap(b), "Close this screen and centre the map on the planet named"));
            if (i.FleetId > 0 && Sensors.Find(w, Side.Player, i.FleetId, seat: true) != null)
                acts.AddChild(Act("Fleet on map ▸", () => _screen.ShowSightingOnMap(i.FleetId), "Where that fleet was last seen"));
            if (i.BodyId is int t && w.FindBody(t) is Body body && body.Owner == Side.Player && i.Kind is PlanKind.Attack or PlanKind.Raid && i == heard.First(x => x.BodyId == t))
                Guard(w, card, acts, body);
        }
        var faded = Intentions.Heard(w, Side.Player, f.About, old: true).Where(i => Intentions.SureNow(w, i) <= 0 && i.Kind != PlanKind.Research).Take(6).ToList();
        if (faded.Count > 0)
        {
            Caption(left, "Older: too old to count");
            foreach (var i in faded) Text(left, $"{GameTime.Format(i.HeardHour)} · {Intentions.What(i)}", Old, 12);
        }
        var pending = f.Intercepts.Count(i => i.HeardHour > w.Hour);
        if (pending > 0) Text(left, $"{pending} more caught and still being made sense of.", Ui.Dim, 12);

        var right = cols[1];
        var mapCard = Card(right, "Where you can listen");
        _map = new ListenMap { CustomMinimumSize = new Vector2(300, 340), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _map.Show(w, f.About);
        mapCard.AddChild(_map);
        var legend = new HFlowContainer();
        legend.AddThemeConstantOverride("h_separation", 12);
        legend.AddChild(Ui.Label("■ your fleets", 11, Ui.Player));
        legend.AddChild(Ui.Label("◯ heard in full / at the edge", 11, new Color(Ui.Player, 0.7f)));
        legend.AddChild(Ui.Label("▲ their fleets, last seen", 11, Ui.SideColor(f.About)));
        legend.AddChild(Ui.Label("⊕ their command", 11, Ui.SideColor(f.About)));
        legend.AddChild(Ui.Label("○ target heard", 11, Gold));
        mapCard.AddChild(legend);
        var ears = Intentions.Ears(w, Side.Player);
        var seat = ListenMap.CommandAsKnown(w, f.About) is Vec2 at ? Intentions.Cover(ears, at).Cover : 0;
        Text(mapCard, $"Each six hours a plan is caught with up to {Intentions.Chance(w, Side.Player, level):P0} odds at level {level}"
                      + (Tech.Has(w, Side.Player, Tech.SignalsAnalysis) ? ", with Signals analysis." : "; Signals analysis would raise it.")
                      + $" In full within {Intentions.ListenFullAu} AU of a fleet or planet of yours, none past {Intentions.ListenMaxAu} AU.", Ui.Text, 12);
        Text(mapCard, seat > 0 ? $"Its command is within reach ({seat:P0}): its plans can be heard before its fleets come near."
                               : "Its command is out of reach: put a fleet or a planet near it to hear plans early.", seat > 0 ? Gold : Ui.Dim, 12);
        Text(mapCard, $"Making sense of a catch takes about {Intentions.DecodeHours(w, Side.Player, level):0} hours" + (w.Light.On ? ", plus the light time." : ".")
                      + " A plan heard twice is surer; False order traffic can plant plans that never come.", Ui.Dim, 12);
    }

    // ---- Fleets and lanes ----------------------------------------------------------------------------

    private void Fleets(World w, IntelFile f, int level)
    {
        var cols = Columns(1.6f, 1f);
        var card = Card(cols[0], "Known fleets: from sightings; old facts fade");
        var g = Table(card, "FLEET", "SHIPS", "CLASSES", "SEEN", "SURE", "LAST KNOWN", "");
        var any = false;
        foreach (var x in f.Fleets.OrderByDescending(x => x.Hour).ThenBy(x => x.FleetId))
        {
            any = true;
            var sure = Intel.Sure(w, x);
            var c = Fade(sure);
            Cell(g, x.Name, x.Cargo ? Ui.Dim : c);
            Cell(g, level >= 1 ? $"{x.Ships}" : "?", c);
            Cell(g, level >= 2 && x.Classes.Count > 0 ? Classes(x.Classes) : x.Cargo ? "cargo" : "unknown", c);
            Cell(g, Ago(w, x.Hour), c);
            Cell(g, $"{sure:P0}", c);
            Cell(g, Where(w, x), c);
            var id = x.FleetId;
            var pos = x.Position;
            g.AddChild(MapLink(() => { if (Sensors.Find(w, Side.Player, id, seat: true) != null) _screen.ShowSightingOnMap(id); else _screen.ShowPointOnMap(pos); },
                "Close this screen and centre the map where it was last seen"));
        }
        if (!any) Text(card, "No rival fleet seen yet", Ui.Dim);
        var lanesCard = Card(cols[1], "Their supply lanes");
        var lanes = w.Lanes.Where(l => l.SeenBy == Side.Player).OrderByDescending(l => Lanes.Confidence(w, l)).ToList();
        if (lanes.Count == 0) Text(lanesCard, "None yet: watch their cargo ships under way to find their runs", Ui.Dim);
        foreach (var l in lanes)
        {
            var every = Lanes.EveryDays(l) is double d ? $", a ship every {d:0.#} days" : "";
            var row = Row(lanesCard);
            Text(row, $"{w.FindBody(l.FromBodyId)?.Name ?? "?"} – {w.FindBody(l.ToBodyId)?.Name ?? "?"}: {Lanes.Confidence(w, l):P0} sure{every}, last seen {Ago(w, l.LastHour)} ago",
                Fade(Lanes.Confidence(w, l)), 12);
            var from = l.FromBodyId;
            row.AddChild(MapLink(() => _screen.ShowBodyOnMap(from)));
        }
        var raids = Card(cols[1], "Their raids on your cargo");
        var losses = w.LossSpots.Where(l => l.Side == Side.Player).ToList();
        Text(raids, losses.Count == 0 ? "No cargo lost to raiders" : $"{losses.Count} cargo losses, {losses.Count(l => w.Hour - l.Hour < 30 * 24)} in the last 30 days", losses.Count > 0 ? Ui.Rival : Ui.Dim);
        foreach (var group in losses.GroupBy(l => w.Bodies.Where(b => b.Kind != BodyKind.Star).OrderBy(b => Vec2.Distance(b.Position, l.At)).First()).OrderByDescending(gr => gr.Count()).Take(5))
        {
            var row = Row(raids);
            Text(row, $"Near {group.Key.Name}: {group.Count()} time{(group.Count() == 1 ? "" : "s")}, last {Ago(w, group.Max(l => l.Hour))} ago", Ui.Text, 12);
            var id = group.Key.Id;
            row.AddChild(MapLink(() => _screen.ShowBodyOnMap(id)));
        }
    }

    private static string Classes(List<string> classes) =>
        string.Join(", ", classes.GroupBy(c => c).Select(g => (GameData.ShipClasses.TryGetValue(g.Key, out var k) ? k.Name.ToLowerInvariant() : g.Key) + (g.Count() > 1 ? $" ×{g.Count()}" : "")));

    private static string Where(World w, FleetFact x)
    {
        var near = w.Bodies.Where(b => b.Kind != BodyKind.Star).OrderBy(b => Vec2.Distance(b.Position, x.Position)).FirstOrDefault();
        var moving = x.Velocity.Length > 1e-6;
        return (near != null ? $"near {near.Name}" : "in deep space") + (moving ? $", moving {Format.Speed(x.Velocity.Length)}" : ", stopped");
    }

    // ---- Planets -------------------------------------------------------------------------------------

    private void PlanetsTab(World w, IntelFile f)
    {
        var card = Card(_box, "Rival planets: who holds them is plain; people and industry when looked at");
        var g = Table(card, "PLANET", "PEOPLE", "INDUSTRY", "BUILDINGS", "LOOKED AT", "");
        foreach (var p in f.Planets.OrderBy(p => p.BodyId))
        {
            if (w.FindBody(p.BodyId) is not Body b) continue;
            var seen = p.Hour > 0;
            var c = seen ? Fade(Intel.Sure(w, p.Hour, Intel.FleetForgetDays)) : Ui.Dim;
            Cell(g, b.Name, c);
            Cell(g, seen ? $"{p.PopK:0.#}k" : "?", c);
            Cell(g, seen ? $"{p.Industry:0.#} a day" : $"worth {b.Industry:0}", c);
            Cell(g, seen ? $"{b.Buildings.Count}" : "?", c);
            Cell(g, seen ? Ago(w, p.Hour) + " ago" : "never", c);
            var id = b.Id;
            g.AddChild(MapLink(() => _screen.ShowBodyOnMap(id)));
        }
        if (f.Planets.Count == 0) Text(card, "The rival holds no planets", Ui.Dim);
        Text(card, $"A fleet or planet of yours within {Intel.PlanetLookMm / Physics.MmPerAu:0.##} AU looks at a planet closely; each building seen teaches a little of the idea behind it.", Ui.Dim, 12);
    }

    // ---- Technology ----------------------------------------------------------------------------------

    /// <summary>
    /// Intel that extends your research: their kit against yours with counters beside the part they answer, their
    /// ideas you are learning as cards (Seen, Studied, Unlocked, with Queue now and what it skips and saves), the
    /// ways to learn more now, and at level 4 their current research overheard.
    /// </summary>
    private void Technology(World w, IntelFile f, int level)
    {
        var cols = Columns(1f, 1.35f, 0.95f);
        Kit(w, f, level, cols[0]);
        Ideas(w, cols[1]);
        WaysToLearn(w, f, cols[2]);
        TheirResearch(w, f, level, cols[2]);
    }

    private void Kit(World w, IntelFile f, int level, Control col)
    {
        var card = Card(col, "Their kit against yours");
        if (level < 2 && f.Tech.Count > 0) Text(card, "Level 2 (Studied) shows how sure these are; until then they are first impressions", Ui.Dim, 11);
        var g = Table(card, "PART", "THEIRS", "YOURS");
        var theirs = Ui.SideColor(f.About);
        foreach (var r in KnownKit.Matchup(w, Side.Player, f.About))
        {
            var fact = f.Tech.Find(t => t.Part == r.Part);
            g.AddChild(Ui.Label(Intel.PartName(r.Part), 13, fact != null ? Ui.Text : Ui.Dim));
            var a = Ui.Label(fact != null ? r.Theirs : "not seen", 13, fact != null ? new Color(theirs, (float)Math.Max(0.5, Intel.Sure(w, fact.Hour, Intel.TechForgetDays))) : Ui.Dim);
            if (fact != null) { a.TooltipText = $"{fact.Source.ToString().ToLowerInvariant()}, {GameTime.Format(fact.Hour)}"; a.MouseFilter = MouseFilterEnum.Pass; }
            g.AddChild(a);
            g.AddChild(Ui.Label(r.Yours + (r.Edge == 1 ? " ▲" : r.Edge == -1 ? " ▼" : ""), 13, r.Edge == 1 ? Ui.Player : r.Edge == -1 ? Ui.Warn : new Color(Ui.Player, 0.8f)));
        }
        foreach (var r in KnownKit.Matchup(w, Side.Player, f.About).Where(r => r.Known != null))
            Text(card, Format.Title(r.Known!), Gold, 12, "Known kit: a slight edge in battle against what you have studied");
        if (level < GameData.Weapons.R.KnownKitLevel)
            Text(card, $"At level {GameData.Weapons.R.KnownKitLevel} (Studied) you fight slightly better against the kit you have seen.", Ui.Dim, 12);

        Caption(card, "Answers to what they field");
        var counters = Intel.Counters(w, Side.Player, f.About).ToList();
        if (counters.Count == 0) Text(card, "Nothing yet: counters show once you have seen what it fields", Ui.Dim, 12);
        foreach (var (idea, why) in counters)
        {
            var row = Row(card);
            Text(row, $"{Format.Title(why)} → {GameData.Research(idea).Name}", Ui.Text, 12);
            row.AddChild(QueueButton(w, idea, "Queue"));
        }
        var fleets = w.Fleets.Where(x => x.Side == f.About && Sensors.Sees(w, Side.Player, x)).OrderBy(x => x.Id).Take(4).ToList();
        if (fleets.Count == 0) return;
        Caption(card, "Their reach");
        foreach (var x in fleets)
        {
            var guess = RivalTech.Guess(w, Side.Player, x.Id);
            Text(card, $"{x.Name}: railguns {guess.Rail:0} Mm, missiles {guess.Missile:0} Mm{(guess.Lance > 0 ? $", lances {guess.Lance:0} Mm" : "")} ({guess.How}{(guess.Plus > 0 ? $", ±{guess.Plus:P0}" : "")})",
                guess.Plus > 0 ? Ui.Dim : Ui.Text, 12, "A fleet that held back may reach further than it showed");
        }
    }

    /// <summary>A Queue button for an idea: queued with the team that finishes it first, or why it can't be (shown on the button).</summary>
    private Button QueueButton(World w, string id, string text)
    {
        if (Tech.Has(w, Side.Player, id)) return Act("Researched", () => { }, "", on: false);
        if (Queued(w, id)) return Act("Queued ✓", () => { _screen.ShowIntel = false; _screen.ShowResearch = true; }, "In your research queue: open the research screen");
        var missing = Research.Missing(w, Side.Player, id);
        if (GameData.Research(id).Later) missing = "Not in this game yet";
        return Act(text, () =>
        {
            _screen.IssueNow(new QueueOrderCommand(Side.Player, OrderKind.Research, id), "queueing research");
            _shape = "";
        }, missing ?? "Queue it with the team that would finish it first", on: missing == null && w.Winner == null);
    }

    private void Ideas(World w, Control col)
    {
        Caption(col, "Their ideas you are learning · Seen → Studied → Unlocked");
        var learning = RivalTech.Learning(w, Side.Player).ToList();
        var r = GameData.Weapons.R;
        if (learning.Count == 0)
            Text(col, "Nothing yet: battles teach what they show, stripped wrecks and captures the parts aboard, close looks at their colonies the buildings.", Ui.Dim);
        foreach (var (id, progress, stage) in learning)
        {
            var idea = GameData.Research(id);
            var unlocked = stage == RivalStage.Unlocked;
            var card = Card(col, null, unlocked ? new Color("3a4a30") : null, unlocked ? new Color("151f31") : null);
            var top = Row(card);
            var name = Text(top, stage == RivalStage.Seen ? "Something new in their kit" : idea.Name, stage == RivalStage.Seen ? Ui.Dim : Ui.Text, 15);
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            top.AddChild(Ui.Label(unlocked ? "Unlocked" : $"{RivalTech.StageName(stage)} · {progress:P0}", 13, unlocked ? Gold : stage == RivalStage.Studied ? Ui.Text : Ui.Dim));
            var seenPart = progress / r.StudiedAt;
            var studiedPart = (progress - r.StudiedAt) / (1 - r.StudiedAt);
            var tint = unlocked ? Gold : stage == RivalStage.Studied ? new Color("c6d0dc") : Ui.Dim;
            card.AddChild(new StepBar((r.StudiedAt, seenPart, tint), (1 - r.StudiedAt, studiedPart, tint)) { TooltipText = $"Seen, Studied at {r.StudiedAt:P0}, Unlocked at 100%" });
            var row = Row(card);
            switch (stage)
            {
                case RivalStage.Unlocked:
                {
                    var skipped = (idea.Needs ?? new()).Count(n => !Tech.Has(w, Side.Player, n));
                    var days = Research.BaseHours(w, Side.Player, id) / 24;
                    var normal = days / Math.Max(0.01, 1 - r.UnlockedCut);
                    Text(row, (skipped > 0 ? $"Skips {skipped} prerequisite{(skipped == 1 ? "" : "s")} · " : "") + $"{days:0} days instead of {normal:0} · {idea.Effect}", new Color("c6d0dc"), 12);
                    row.AddChild(QueueButton(w, id, "Queue now"));
                    break;
                }
                case RivalStage.Studied:
                {
                    var left = 1 - progress;
                    Text(row, $"{idea.Effect} {left:P0} to unlock: about {Math.Ceiling(left / r.WreckTeach):0} wrecks, {Math.Ceiling(left / r.CaptureTeach):0} captured ship"
                              + $"{(Math.Ceiling(left / r.CaptureTeach) == 1 ? "" : "s")} or {Math.Ceiling(left / r.BattleTeach):0} battles", Ui.Text, 12);
                    row.AddChild(QueueButton(w, id, "Research normally"));
                    break;
                }
                default:
                {
                    var part = RivalTech.ByPart.FirstOrDefault(kv => kv.Value.Contains(id)).Key;
                    Text(row, $"{(part != null ? $"A {Intel.PartName(part).ToLowerInvariant()} idea" : "An idea behind their buildings")}. Its name shows at Studied ({r.StudiedAt:P0}).", Ui.Dim, 12);
                    break;
                }
            }
        }
        Text(col, "Unlocked ideas also show in your research web, marked as theirs.", Ui.Dim, 12);
    }

    private void WaysToLearn(World w, IntelFile f, Control col)
    {
        var r = GameData.Weapons.R;
        var card = Card(col, "Ways to learn now");
        var any = false;
        // Their wrecks still holding salvage: strip one with any fleet of yours.
        foreach (var wr in w.Wrecks.Where(x => x.Side == f.About && x.Salvage.Count > 0).OrderByDescending(x => x.Hour).Take(3))
        {
            any = true;
            var near = w.Bodies.Where(b => b.Kind != BodyKind.Star).OrderBy(b => Vec2.Distance(b.Position, wr.Position)).FirstOrDefault();
            var parts = GameData.ShipClasses.TryGetValue(wr.ClassId, out var cls) ? string.Join(", ", RivalTech.PartsOf(cls).Select(p => Intel.PartName(p).ToLowerInvariant())) : "";
            var row = Row(card);
            Text(row, $"Strip the wreck of {wr.Name}{(near != null ? $" near {near.Name}" : "")}: +{r.WreckTeach:P0} {parts} ideas", Ui.Text, 12);
            var at = wr.Position;
            row.AddChild(MapLink(() => _screen.ShowPointOnMap(at)));
        }
        // Their colonies in look range teach the ideas behind their buildings each day.
        foreach (var p in f.Planets.Where(p => w.Hour - p.Hour <= 24).OrderBy(p => p.BodyId))
        {
            if (w.FindBody(p.BodyId) is not Body b) continue;
            var teach = b.Buildings.Distinct().Count(k => Planets.BuildingKind(k)?.Needs is string needs && !Tech.Has(w, Side.Player, needs) && Tech.Has(w, f.About, needs));
            if (teach == 0) continue;
            any = true;
            var row = Row(card);
            Text(row, $"{b.Name} is in look range: +{r.PlanetTeachPerDay:P0} a day each, {teach} building{(teach == 1 ? "" : "s")}", Ui.Text, 12);
            var id = b.Id;
            row.AddChild(MapLink(() => _screen.ShowBodyOnMap(id)));
        }
        // Their fleets as seen: capture a ship for the parts aboard.
        foreach (var x in f.Fleets.Where(x => !x.Cargo && x.Classes.Count > 0 && Intel.Sure(w, x) >= 0.4).OrderByDescending(x => x.Hour).Take(2))
        {
            any = true;
            var parts = x.Classes.Distinct().Where(GameData.ShipClasses.ContainsKey).SelectMany(c => RivalTech.PartsOf(GameData.ShipClasses[c])).Distinct().Select(p => Intel.PartName(p).ToLowerInvariant());
            var row = Row(card);
            Text(row, $"Capture a ship of {x.Name}: +{r.CaptureTeach:P0} each part aboard ({string.Join(", ", parts)})", Ui.Text, 12);
            var fid = x.FleetId;
            var pos = x.Position;
            row.AddChild(MapLink(() => { if (Sensors.Find(w, Side.Player, fid, seat: true) != null) _screen.ShowSightingOnMap(fid); else _screen.ShowPointOnMap(pos); }));
        }
        if (!any) Text(card, "No wreck to strip, colony in look range or fleet seen closely just now.", Ui.Dim, 12);
        Text(card, $"Any battle: +{r.BattleTeach:P0} for each part they show", Ui.Dim, 12);
    }

    private void TheirResearch(World w, IntelFile f, int level, Control col)
    {
        var card = Card(col, $"Their research now · level {Intentions.ResearchLevel}", level >= Intentions.ResearchLevel ? null : new Color("34486a"));
        if (level < Intentions.ResearchLevel)
        {
            Text(card, $"At level {Intentions.ResearchLevel} ({Intel.LevelNames[Intentions.ResearchLevel]}) their current research can be overheard near their command, like their plans.", Ui.Dim, 12);
            Text(card, "When they finish an idea you overheard, you learn a little of it too.", Ui.Dim, 12);
            return;
        }
        var heard = Intentions.Heard(w, Side.Player, f.About).Where(i => i.Kind == PlanKind.Research && i.Outcome == PlanOutcome.Pending).ToList();
        if (heard.Count == 0) Text(card, "Nothing overheard yet: it comes from near their command (see Their plans).", Ui.Dim, 12);
        foreach (var i in heard)
            Text(card, $"{Intentions.What(i)}, {Intentions.When(w, i)} · {Intentions.SureNow(w, i):P0} sure", Fade(Intentions.SureNow(w, i)), 13,
                "Overheard in their order traffic; when they finish it you learn a little of it too");
        var done = f.Intercepts.Where(i => i.Kind == PlanKind.Research && i.Outcome == PlanOutcome.CameTrue).TakeLast(3).ToList();
        foreach (var i in done) Text(card, $"Done: {i.BodyName} ({GameTime.Format(i.OutcomeHour)})", Ui.Dim, 12);
    }

    private static OptionButton Options(IReadOnlyList<string> items, int selected, Action<int> chosen, string tip)
    {
        var o = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = tip };
        o.AddThemeFontSizeOverride("font_size", 13);
        foreach (var item in items) o.AddItem(item);
        if (items.Count > 0) o.Select(Math.Clamp(selected, 0, items.Count - 1));
        o.ItemSelected += i => chosen((int)i);
        return o;
    }

    /// <summary>The language track on this rival, the offer form (three races only), and the offers you know of.</summary>
    private void ContactTab(World w, IntelFile f)
    {
        var about = f.About;
        var race = Races.Of(w, about);
        var score = Contact.Score(w, Side.Player, about);
        var stage = Contact.Stage(score);
        Section($"Their tongue: {race.Name}");
        for (var i = 0; i < Contact.D.Stages.Length; i++)
            Line($"{(i == stage ? "▶" : i < stage ? "✓" : "○")} {Contact.StageName(i)} ({Contact.D.Stages[i]:0} %)"
                 + (i == Contact.D.SendStage ? ": you can send offers" : i == Contact.D.OddsStage ? ": you see the odds of an offer" : i == 1 ? ": their broadcasts give away more" : ""),
                i == stage ? Ui.Player : i < stage ? Ui.Text : Ui.Dim, 13);
        Line($"Now {score:0.#} %. Each stage adds intel on them every day.", Ui.Text, 13);
        if (Contact.Closed(w, Side.Player, about) is string closed) Line(closed + ". Until then listening stops at Noise.", Gold, 13);
        Line($"It rises from listening while in contact ({Contact.D.ListenPerDay:0.#} a day, up to {Contact.D.ListenCap:0} %), {GameData.Research(Contact.Signals).Name} and "
             + $"{GameData.Research(Contact.Decoder).Name} research, prisoners, captured ships and stripped wrecks.", Ui.Dim, 12);

        Section("Send an offer");
        if (!Contact.OffersOn(w))
            Line(Sides.Three(w) ? "Contact offers are off in this game (New game screen, Against)" : "Offers come with three races (New game screen: Second rival). The language track still raises your intel.", Ui.Dim, 13);
        else OfferForm(w, about, stage);

        Section("Offers you know of");
        var known = Contact.KnownTo(w, Side.Player).Where(o => o.From == about || o.To == about).Take(12).ToList();
        if (known.Count == 0) Line("None yet", Ui.Dim, 13);
        foreach (var o in known)
        {
            var who = $"{Sides.Label(w, o.From)} → {Sides.Label(w, o.To)}";
            var how = o.Broadcast ? "broadcast" : "drone";
            var state = !o.Landed ? $"lands in {Math.Max(0, o.LandHour - w.Hour) / 24:0.#} days"
                : o.Ask == AskKind.None ? "landed"
                : o.From == Side.Player || o.To == Side.Player ? (o.Taken == true ? "landed: they seem to take it up" : "landed: no sign they will") : "landed";
            var heard = o.From != Side.Player && o.To != Side.Player ? " (overheard)" : "";
            Line($"{GameTime.Format(o.SentHour)} · {who} by {how}{heard}: {Contact.Describe(w, o)} · {state}", o.Landed ? Ui.Text : Gold, 13);
        }
    }

    private void OfferForm(World w, Side about, int stage)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        row.AddChild(Ui.Label("Give", 13, Ui.Dim));
        var gifts = Enum.GetValues<GiftKind>();
        row.AddChild(Options(gifts.Select(g => g switch { GiftKind.Idea => "An idea", GiftKind.Food => "Food", GiftKind.Alloys => "Alloys", _ => "Your survey map" }).ToList(),
            Array.IndexOf(gifts, _gift), i => { _gift = gifts[i]; _amount = Contact.DefaultAmount(_gift); _shape = ""; },
            $"{race(w, about)} values: " + string.Join(", ", ValuesText(w, about))));
        if (_gift == GiftKind.Idea)
        {
            var ideas = Contact.GiftIdeas(w, Side.Player, about).ToList();
            if (_idea == null || !ideas.Contains(_idea)) _idea = ideas.FirstOrDefault();
            row.AddChild(ideas.Count == 0 ? Ui.Label("you have no idea they lack", 13, Ui.Dim)
                : Options(ideas.Select(id => GameData.Research(id).Name).ToList(), ideas.IndexOf(_idea!), i => { _idea = ideas[i]; _shape = ""; }, "An idea you have that they lack: they learn it when the message lands"));
        }
        if (_gift is GiftKind.Food or GiftKind.Alloys)
        {
            if (_amount <= 0) _amount = Contact.DefaultAmount(_gift);
            var amounts = new[] { 0.5, 1, 2 }.Select(x => Math.Round(Contact.DefaultAmount(_gift) * x)).ToList();
            row.AddChild(Options(amounts.Select(a => $"{a:0}").ToList(), Math.Max(0, amounts.IndexOf(_amount)), i => { _amount = amounts[i]; _shape = ""; }, "How much, from the shipyard's store"));
        }
        row.AddChild(Ui.Label("Ask", 13, Ui.Dim));
        var third = Sides.Enemies(w, Side.Player).Where(s => s != about && !Sides.Out(w, s)).ToList();
        var asks = new List<(AskKind Ask, string Text)> { (AskKind.None, "Nothing (a gift)") };
        foreach (var t in third) asks.Add((AskKind.Attack, $"Go after {Races.Of(w, t).Name} ({Contact.D.AskDays:0} days)"));
        asks.Add((AskKind.Spare, $"Leave a planet alone ({Contact.D.AskDays:0} days)"));
        row.AddChild(Options(asks.Select(a => a.Text).ToList(), Math.Max(0, asks.FindIndex(a => a.Ask == _ask)), i => { _ask = asks[i].Ask; _shape = ""; },
            "What you ask in return. They are free to ignore it; the gift goes anyway"));
        if (_ask == AskKind.Spare)
        {
            var mine = w.Bodies.Where(b => b.Owner == Side.Player).OrderBy(b => b.Name).ToList();
            if (_askBody is not int id || mine.All(b => b.Id != id)) _askBody = mine.FirstOrDefault()?.Id;
            row.AddChild(Options(mine.Select(b => b.Name).ToList(), mine.FindIndex(b => b.Id == _askBody), i => { _askBody = mine[i].Id; _shape = ""; }, "The planet they should leave alone"));
        }
        _at.AddChild(row);

        var send = new HBoxContainer();
        send.AddThemeConstantOverride("separation", 6);
        send.AddChild(Ui.Label("By", 13, Ui.Dim));
        send.AddChild(Options(new[] { $"Messenger drone (private, {Contact.D.DroneDays:0} days)", $"Broadcast ({Contact.D.BroadcastHours:0} h, can be overheard)" },
            _broadcast ? 1 : 0, i => { _broadcast = i == 1; _shape = ""; }, "A drone is private but slow; a broadcast is quick, and anyone who reads your tongue hears it"));
        var offer = new Offer { From = Side.Player, To = about, Gift = _gift, Idea = _idea, Amount = _gift is GiftKind.Food or GiftKind.Alloys ? _amount : 0, Ask = _ask,
            AskSide = _ask == AskKind.Attack ? third.FirstOrDefault() : null, AskBodyId = _ask == AskKind.Spare ? _askBody : null, Broadcast = _broadcast };
        var why = Contact.CanSend(w, offer);
        var odds = stage >= Contact.D.OddsStage && why == null ? $" · odds they take it up: {Contact.Odds(w, offer):P0}" : stage < Contact.D.OddsStage ? " · odds show at Language" : "";
        var b = Ui.SmallButton("Send offer", () => _screen.IssueNow(new SendOfferCommand(Side.Player, about, offer.Gift, offer.Idea, offer.Amount, offer.Ask, offer.AskSide, offer.AskBodyId, offer.Broadcast), "offer"),
            why ?? "The gift leaves your stores now and lands with the message");
        b.Disabled = why != null;
        send.AddChild(b);
        send.AddChild(Ui.Label((why ?? "Ready") + odds, 13, why != null ? Ui.Dim : Gold));
        _at.AddChild(send);
        Line($"One offer to each race every {Contact.D.CooldownDays:0} days. Rivals send offers too: you see them only if you read their tongue.", Ui.Dim, 12);
    }

    private static string race(World w, Side s) => Races.Of(w, s).Name;

    private static IEnumerable<string> ValuesText(World w, Side s)
    {
        var v = Contact.D.Values.TryGetValue(Races.Of(w, s).Id, out var x) ? x : new GiftValues();
        string Word(double f) => f >= 1.3 ? "a lot" : f >= 0.9 ? "fairly" : f > 0 ? "a little" : "not at all";
        yield return $"ideas {Word(v.Idea)}";
        yield return $"food {Word(v.Food)}";
        yield return $"alloys {Word(v.Alloys)}";
        yield return $"maps {Word(v.Map)}";
    }
}
