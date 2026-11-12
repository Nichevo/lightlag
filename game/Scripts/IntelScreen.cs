using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The intelligence screen (I): what you know about the rival and how sure you are. Tabs: Overview (intel
/// level, what the next level needs, estimates, habits, notes), Fleets, Planets, Technology (with research
/// counters), Lanes and raids, and Their file on you (a guess at what the rival knows). Old facts are
/// greyed with their age, so a guess is never mistaken for a fact.
/// </summary>
public partial class IntelScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _box = null!;
    private Tab _tab = Tab.Overview;
    private string _shape = "";

    public enum Tab { Overview, Fleets, Planets, Technology, Lanes, TheirFile }

    private static readonly Color Old = new(0.55f, 0.6f, 0.68f, 0.55f);
    private static readonly Color Gold = new("ffd27a");

    public IntelScreen() : this(null!) { }
    public IntelScreen(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 18);
        SetAnchorsAndOffsetsPreset(LayoutPreset.Center, LayoutPresetMode.KeepSize);
        GrowHorizontal = GrowDirection.Both;
        GrowVertical = GrowDirection.Both;
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(820, 560) };
        _box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _box.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(_box);
        AddChild(scroll);
    }

    public void Refresh(Simulation sim)
    {
        var w = sim.World;
        var f = Intel.Of(w, Side.Player);
        var shape = $"{_tab}|{GameTime.DayOf(w.Tick)}|{f.Points:0}|{f.Fleets.Count}|{f.Tech.Count}|{f.Reach.Count}|{Economy.Of(w, Side.Player).RivalIdeas.Count}|{GetViewportRect().Size}";
        if (shape == _shape || Ui.MouseHeld) return;
        _shape = shape;
        foreach (var c in _box.GetChildren()) c.QueueFree();
        var head = new HBoxContainer();
        var title = Ui.Label("Intelligence", 22, Ui.Player);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(title);
        head.AddChild(Ui.SmallButton("✕", () => _screen.ShowIntel = false, "Close (I or Esc)"));
        _box.AddChild(head);
        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 4);
        foreach (var t in Enum.GetValues<Tab>())
        {
            var tab = t;
            var b = Ui.SmallButton(TabName(t), () => { _tab = tab; _shape = ""; }, "");
            b.Modulate = t == _tab ? Ui.Player : new Color(1, 1, 1, 0.7f);
            tabs.AddChild(b);
        }
        _box.AddChild(tabs);
        var level = Intel.Level(f);
        var rival = w.Minds.FirstOrDefault(m => m.Side == Side.Rival);
        _box.AddChild(Ui.Label($"Rival{(rival != null ? $" ({rival.Difficulty})" : "")} · intel level {level} · {Intel.LevelNames[level]}"
                               + (w.Hour - f.LastContactHour < 48 ? ", in contact" : ", fading without contact"), 14, Ui.Text));
        switch (_tab)
        {
            case Tab.Overview: Overview(w, f, level); break;
            case Tab.Fleets: Fleets(w, f, level); break;
            case Tab.Planets: PlanetsTab(w, f); break;
            case Tab.Technology: Technology(w, f, level); break;
            case Tab.Lanes: LanesTab(w, f); break;
            case Tab.TheirFile: TheirFile(w, f); break;
        }
    }

    private static string TabName(Tab t) => t switch
    {
        Tab.Lanes => "Lanes and raids",
        Tab.TheirFile => "Their file on you",
        _ => t.ToString(),
    };

    private void Section(string text) => _box.AddChild(Ui.Label(text.ToUpperInvariant(), 12, Ui.Dim));

    private void Line(string text, Color? color = null, int size = 14, string tip = "")
    {
        var l = Ui.Label(text, size, color ?? Ui.Text);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(780, 0);
        if (tip.Length > 0) { l.TooltipText = tip; l.MouseFilter = MouseFilterEnum.Pass; }
        _box.AddChild(l);
    }

    private GridContainer Table(params string[] heads)
    {
        var g = new GridContainer { Columns = heads.Length };
        g.AddThemeConstantOverride("h_separation", 18);
        g.AddThemeConstantOverride("v_separation", 4);
        foreach (var h in heads) g.AddChild(Ui.Label(h, 11, Ui.Dim));
        _box.AddChild(g);
        return g;
    }

    private static void Cell(GridContainer g, string text, Color color) => g.AddChild(Ui.Label(text, 13, color));

    private static Color Fade(double sure) => sure >= 0.6 ? Ui.Text : sure >= 0.25 ? new Color(Ui.Text, 0.7f) : Old;

    private static string Ago(World w, double hour) => w.Hour - hour < 1 ? "now" : Format.Hours(w.Hour - hour);

    private void Overview(World w, IntelFile f, int level)
    {
        // Who they are is known from the start (their broadcasts give it away); what that means comes from the race's traits.
        var race = Races.Of(w, Side.Rival);
        Section($"Race: {race.Name}");
        Line(race.Style, Ui.Text, 13);
        foreach (var p in race.Pros ?? new()) Line($"+ {p}", Ui.Dim, 13);
        foreach (var c in race.Cons ?? new()) Line($"- {c}", Ui.Dim, 13);
        Section("Intel level");
        for (var i = 0; i < Intel.LevelNames.Length; i++)
            Line($"{(i == level ? "▶" : i < level ? "✓" : "○")} {i} · {Intel.LevelNames[i]}: {Intel.LevelMeaning[i]}", i == level ? Ui.Player : i < level ? Ui.Text : Ui.Dim, 13);
        if (level < 4)
        {
            var need = Intel.LevelPoints[level + 1] - f.Points;
            var days = need / (Intel.SensorPointsPerFleetHour * 24);
            Line($"Next level needs about {need:0} more points: {days:0} days of sensor time on one of its fleets, or {Math.Ceiling(need / Intel.BattlePoints):0} battles, "
                 + "or salvage, prisoners and captures. Knowledge fades without contact.", Gold, 13);
        }
        Section("Estimate");
        var (low, high) = Intel.EstimatedWarships(w, Side.Player);
        var (planets, industry) = Intel.EstimatedHoldings(w, Side.Player);
        var mine = Economy.Claimable(w).Where(b => b.Owner == Side.Player).Sum(b => b.Industry);
        var (control, plus) = Intel.EstimatedControl(w, Side.Player);
        Line(level >= 1 ? $"Warships: {low} to {high}" : "Warships: unknown (reach level 1 to count them)",
            tip: "From the fleets on file; the range narrows as your intel level rises");
        Line($"Planets: {planets} ({f.Planets.Count(p => p.Hour > 0)} looked at closely)");
        Line(level >= 2 && mine > 0 ? $"Industry: about {industry / mine:0.0}× yours" : level >= 2 ? $"Industry: about {industry:0} a day" : "Industry: reach level 2 to judge it",
            tip: "From the planets it holds, at their known worth");
        Line($"Control share: {control:P0} ± {plus * 100:0}");
        Section("Habits you have seen");
        if (f.Habits.Count == 0) Line("Nothing yet: habits show after contact, raids and battles", Ui.Dim, 13);
        foreach (var (key, count) in f.Habits.OrderByDescending(h => h.Value))
            Line($"{Intel.HabitText(key)} · {count} time{(count == 1 ? "" : "s")}", Ui.Text, 13);
        if (level >= 3 && Intel.Personality(f) is var (name, sure, counter))
            Line($"Best guess: {name} ({sure:P0}). Counter: {counter}.", Gold, 14);
        else if (f.Habits.Count > 0)
            Line("A best guess at its temperament comes at intel level 3 (Understood)", Ui.Dim, 12);
        if (f.Notes.Count > 0)
        {
            Section("Notes");
            foreach (var n in Enumerable.Reverse(f.Notes).Take(8)) Line($"{GameTime.Format(n.Hour)} · {n.Source}: {n.Text}", Ui.Dim, 12);
        }
    }

    private void Fleets(World w, IntelFile f, int level)
    {
        Section("Known fleets: from sightings; old facts fade");
        var g = Table("FLEET", "SHIPS", "CLASSES", "SEEN", "SURE", "LAST KNOWN");
        var any = false;
        foreach (var x in Intel.FleetsKnown(w, Side.Player, cargo: true))
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
        }
        if (!any) Line("No rival fleet seen yet", Ui.Dim, 13);
    }

    private static string Classes(List<string> classes) =>
        string.Join(", ", classes.GroupBy(c => c).Select(g => (GameData.ShipClasses.TryGetValue(g.Key, out var k) ? k.Name.ToLowerInvariant() : g.Key) + (g.Count() > 1 ? $" ×{g.Count()}" : "")));

    private static string Where(World w, FleetFact x)
    {
        var near = w.Bodies.Where(b => b.Kind != BodyKind.Star).OrderBy(b => Vec2.Distance(b.Position, x.Position)).FirstOrDefault();
        var moving = x.Velocity.Length > 1e-6;
        return (near != null ? $"near {near.Name}" : "in deep space") + (moving ? $", moving {Format.Speed(x.Velocity.Length)}" : ", stopped");
    }

    private void PlanetsTab(World w, IntelFile f)
    {
        Section("Rival planets: who holds them is plain; people and industry when looked at");
        var g = Table("PLANET", "PEOPLE", "INDUSTRY", "LOOKED AT");
        foreach (var p in f.Planets.OrderBy(p => p.BodyId))
        {
            if (w.FindBody(p.BodyId) is not Body b) continue;
            var seen = p.Hour > 0;
            var c = seen ? Fade(Intel.Sure(w, p.Hour, Intel.FleetForgetDays)) : Ui.Dim;
            Cell(g, b.Name, c);
            Cell(g, seen ? $"{p.PopK:0.#}k" : "?", c);
            Cell(g, seen ? $"{p.Industry:0.#} a day" : $"worth {b.Industry:0}", c);
            Cell(g, seen ? Ago(w, p.Hour) + " ago" : "never", c);
        }
        if (f.Planets.Count == 0) Line("The rival holds no planets", Ui.Dim, 13);
    }

    private void Technology(World w, IntelFile f, int level)
    {
        Section("Their technology: seen, salvaged or captured");
        if (level < 2 && f.Tech.Count > 0) Line("Intel level 2 (Studied) shows how sure these are; until then they are first impressions", Ui.Dim, 12);
        var g = Table("PART", "LEVEL", "HOW WE KNOW");
        foreach (var part in Intel.Parts)
        {
            var t = f.Tech.Find(x => x.Part == part);
            Cell(g, Intel.PartName(part), t != null ? Ui.Text : Ui.Dim);
            Cell(g, t != null ? Intel.LevelName(part, t.Level) : "not seen", t != null ? Fade(Intel.Sure(w, t.Hour, Intel.TechForgetDays)) : Ui.Dim);
            Cell(g, t != null ? $"{t.Source.ToString().ToLowerInvariant()}, {GameTime.Format(t.Hour)}" : $"{f.Habits.GetValueOrDefault("battles")} battles", t != null ? Ui.Text : Ui.Dim);
        }
        Section("Their research we are learning");
        var learning = RivalTech.Learning(w, Side.Player).ToList();
        if (learning.Count == 0) Line("Nothing yet: battles teach what they show, stripped wrecks and captures the parts aboard, close looks at their colonies the buildings", Ui.Dim, 13);
        foreach (var l in learning)
            Line(l.Stage == RivalStage.Seen ? $"Something new in their kit · {l.Progress:P0}" : $"{GameData.Research(l.Id).Name} · {RivalTech.StageName(l.Stage)} · {l.Progress:P0}"
                 + (l.Stage == RivalStage.Unlocked ? " · queue it without its prerequisites" : ""), l.Stage == RivalStage.Unlocked ? Gold : Ui.Text, 13);
        Section("Their weapon reach");
        var fleets = w.Fleets.Where(x => x.Side == Side.Rival && Sensors.Sees(w, Side.Player, x)).OrderBy(x => x.Id).ToList();
        if (fleets.Count == 0) Line("No rival fleet in sight", Ui.Dim, 13);
        foreach (var x in fleets)
        {
            var guess = RivalTech.Guess(w, Side.Player, x.Id);
            Line($"{x.Name}: railguns {guess.Rail:0} Mm, missiles {guess.Missile:0} Mm{(guess.Lance > 0 ? $", lances {guess.Lance:0} Mm" : "")} ({guess.How}{(guess.Plus > 0 ? $", ±{guess.Plus:P0}" : "")})",
                guess.Plus > 0 ? Ui.Dim : Ui.Text, 13, "A fleet that held back may reach further than it showed");
        }
        Section("Research that answers it");
        var counters = Intel.Counters(w, Side.Player).ToList();
        if (counters.Count == 0) Line("Nothing yet: counters show once you have seen what it fields", Ui.Dim, 13);
        foreach (var (idea, why) in counters)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            row.AddChild(Ui.Label($"{Format.Title(why)} → {GameData.Research(idea).Name}", 14, Gold));
            row.AddChild(Ui.SmallButton("Research ▸", () => { _screen.ShowIntel = false; _screen.ShowResearch = true; }, "Open the research screen; the idea is marked ⚔ counter"));
            _box.AddChild(row);
        }
    }

    private void LanesTab(World w, IntelFile f)
    {
        Section("Their supply lanes you have worked out");
        var lanes = w.Lanes.Where(l => l.SeenBy == Side.Player).OrderByDescending(l => Lanes.Confidence(w, l)).ToList();
        if (lanes.Count == 0) Line("None yet: watch their cargo ships under way to find their runs", Ui.Dim, 13);
        foreach (var l in lanes)
        {
            var every = Lanes.EveryDays(l) is double d ? $", a ship every {d:0.#} days" : "";
            Line($"{w.FindBody(l.FromBodyId)?.Name ?? "?"} – {w.FindBody(l.ToBodyId)?.Name ?? "?"}: {Lanes.Confidence(w, l):P0} sure{every}, last seen {Ago(w, l.LastHour)} ago",
                Fade(Lanes.Confidence(w, l)), 13);
        }
        Section("Their raids on your cargo");
        var losses = w.LossSpots.Where(l => l.Side == Side.Player).ToList();
        Line(losses.Count == 0 ? "No cargo lost to raiders" : $"{losses.Count} cargo losses, {losses.Count(l => w.Hour - l.Hour < 30 * 24)} in the last 30 days", losses.Count > 0 ? Ui.Rival : Ui.Dim, 13);
        foreach (var group in losses.GroupBy(l => w.Bodies.Where(b => b.Kind != BodyKind.Star).OrderBy(b => Vec2.Distance(b.Position, l.At)).First().Name).OrderByDescending(g => g.Count()).Take(5))
            Line($"Near {group.Key}: {group.Count()} time{(group.Count() == 1 ? "" : "s")}, last {Ago(w, group.Max(l => l.Hour))} ago", Ui.Text, 13);
    }

    private void TheirFile(World w, IntelFile f)
    {
        Section("Estimated: what the rival probably knows");
        Line($"Their intel level on you: about {Intel.LevelFor(f.TheirPointsGuess)}",
            tip: "Guessed from how often their fleets have been near yours and the battles you have fought");
        var mine = w.Fleets.Where(x => x.Side == Side.Player).ToList();
        Line($"Your fleets they have seen: {mine.Count(x => f.SeenUs.ContainsKey(x.Id))} of {mine.Count}");
        foreach (var x in mine.OrderBy(x => x.Id))
            Line(f.SeenUs.TryGetValue(x.Id, out var h) ? $"  {x.Name}: probably seen {Ago(w, h)} ago" : $"  {x.Name}: probably still a surprise",
                f.SeenUs.ContainsKey(x.Id) ? Ui.Text : Gold, 13);
        var lost = w.LossSpots.Count(l => l.Side == Side.Player);
        Line(lost > 0 ? $"Your runs they track: they have caught your cargo {lost} times, so expect them to know those runs" : "Your runs they track: none known",
            Ui.Text, 13);
        Line("Your planets: who holds them is plain to everyone", Ui.Dim, 13);
        Line("Built from where their scouts and raiders have been. It tells you what is still a surprise, so running cold and decoys matter.", Ui.Dim, 12);
    }
}
