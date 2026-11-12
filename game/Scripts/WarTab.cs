using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The Empire screen's War tab: boardings under way (progress, chance and the calls with their options),
/// ground fights (plan, front, days left, calls, pull out, open the board), fleets clearing up after a
/// battle (steps that can be unticked, and the way home), then crews (ranks, reserve crews waiting at
/// home) and what prisoners and wrecks taught about the rival. Every button issues a command, so it all
/// works while paused. The tab also pauses the game when a call waits for the player (see WatchCalls).
/// </summary>
public sealed class WarTab
{
    private const float Width = 520;
    private readonly GameScreen _screen;
    private readonly VBoxContainer _box;
    private readonly List<(Label Label, Func<World, string> Text)> _live;

    public WarTab(GameScreen screen, VBoxContainer box, List<(Label Label, Func<World, string> Text)> live)
    {
        _screen = screen;
        _box = box;
        _live = live;
    }

    /// <summary>Everything that changes which rows and buttons exist.</summary>
    public static string Shape(World w) =>
        string.Join(",", Boardings(w).Select(b => $"{b.Id}{b.Outcome}{string.Join("", b.Calls.Select(c => $"{c.Id}{c.Picked}"))}"))
        + "|" + string.Join(",", Fronts(w).Select(b => $"{b.Id}{b.Front!.Plan}{b.Front.PlanByHand}{string.Join("", b.Front.Calls.Select(c => $"{c.Id}{c.Picked}"))}"))
        + "|" + string.Join(",", Aftermaths(w).Select(a => $"{a.Id}{a.Done}{a.Home}{string.Join("", a.Tasks.Select(t => $"{t.State}{t.On}"))}"))
        + "|" + string.Join(",", Adrift(w).Select(b => $"{b.Id}:{b.RescuerFleetId}"))
        + "|" + Economy.Of(w, Side.Player).ReserveCrews.Count + "|" + Economy.Of(w, Side.Player).Spoils.Notes.Count;

    private static IEnumerable<Boarding> Boardings(World w) =>
        w.Boardings.Where(b => b.Side == Side.Player && (b.Outcome == null || w.Hour - (b.EndHour ?? 0) < 24) || b.Side != Side.Player && b.Outcome == null && w.FindShip(b.TargetShipId)?.Side == Side.Player);

    private static IEnumerable<Body> Fronts(World w) =>
        w.Bodies.Where(b => b.Front != null && (b.Front.Side == Side.Player || b.Owner == Side.Player)).OrderBy(b => b.Id);

    private static IEnumerable<Aftermath> Aftermaths(World w) =>
        w.Aftermaths.Where(a => a.Side == Side.Player && (!a.Done || w.Hour - (a.EndHour ?? 0) < 12)).OrderBy(a => a.Id);

    /// <summary>Our escape pods and lifeboats, and the rival's our ships or planets can see (picking those up takes prisoners).</summary>
    private static IEnumerable<Lifeboat> Adrift(World w) =>
        w.Lifeboats.Where(b => b.Side == Side.Player || w.Fleets.Any(f => f.Side == Side.Player && Vec2.Distance(f.Position, b.Position) < GameData.Combat.ContactRangeMm * 10))
            .OrderBy(b => b.Side == Side.Player ? 0 : 1).ThenBy(b => b.AirUntilHour).ThenBy(b => b.Id);

    public void Build(World w)
    {
        var any = false;
        var boardings = Boardings(w).ToList();
        if (boardings.Count > 0)
        {
            any = true;
            Heading("Boarding");
            foreach (var b in boardings) BoardingRows(w, b);
        }
        var fronts = Fronts(w).ToList();
        if (fronts.Count > 0)
        {
            any = true;
            Heading("Ground fighting");
            foreach (var body in fronts) FrontRows(w, body);
        }
        var after = Aftermaths(w).ToList();
        if (after.Count > 0)
        {
            any = true;
            Heading("After battle");
            foreach (var a in after) AfterRows(w, a);
        }
        var adrift = Adrift(w).ToList();
        if (adrift.Count > 0)
        {
            any = true;
            Heading("Survivors adrift");
            foreach (var lb in adrift) BoatRows(w, lb);
        }
        if (!any)
            Text("No boarding, ground fighting or clearing up just now. Board crippled enemy ships from the fleet panel (Board ▾); land troops from orbit over an enemy planet.");
        Crews(w);
        Spoils(w);
    }

    // ---- Boarding -------------------------------------------------------------------------------

    private void BoardingRows(World w, Boarding b)
    {
        var ours = b.Side == Side.Player;
        var title = ours ? $"{b.TargetName} ({b.TargetClass}), {Boarders.Approach(b.Approach)} for the {Boarders.Goal(b.Goal)}"
                         : $"The rival is boarding our {b.TargetName}";
        _box.AddChild(Ui.Label(title, 13, ours ? Ui.Text : Ui.Danger));
        var id = b.Id;
        Live(12, Ui.Dim, w2 => w2.Boardings.Find(x => x.Id == id) is not Boarding x ? ""
            : x.Outcome != null ? $"Over: {x.Outcome}"
            : $"{x.Progress * 100:0} % across · {(x.TroopsK + x.Parties.Sum(p => p.CrewK)) * 1000:0} boarders against {x.Defenders * 1000:0} · "
              + $"chance {Boarders.WinChance(w2, x) * 100:0} %");
        if (b.Outcome == null) foreach (var c in b.Calls.Where(c => c.Open)) CallRow(w, c, ours, option => new BoardingCallCommand(id, c.Id, option));
        if (b.Notes.Count > 0) Text(b.Notes[^1], 12);
    }

    // ---- Ground ---------------------------------------------------------------------------------

    private void FrontRows(World w, Body body)
    {
        var f = body.Front!;
        var ours = f.Side == Side.Player;
        _box.AddChild(Ui.Label(ours ? $"Invading {body.Name}" : $"{body.Name} is invaded", 13, ours ? Ui.Text : Ui.Danger));
        var id = body.Id;
        Live(12, Ui.Dim, w2 => w2.FindBody(id) is not Body x || x.Front is not GroundFront g ? "" :
            $"{Ground.PlanName(g.Plan)} · {Taken(x, g) * 100:0} % of the ground held by {(g.Side == Side.Player ? "us" : "them")} · "
            + $"{x.LandingTroops * 1000:0} troops against {x.Garrison * 1000:0} · "
            + (Ground.DaysLeft(w2, x) is double d ? $"about {d:0.0} days left · " : "") + $"chance {Ground.WinChance(x) * 100:0} %");
        var row = Flow();
        if (ours)
        {
            foreach (var plan in Enum.GetValues<GroundPlan>())
            {
                var p = plan;
                var button = Small(Ground.PlanName(plan), () => _screen.IssueNow(new SetGroundPlanCommand(Side.Player, id, p), "ground plan"), Ground.PlanWhat(plan));
                if (f.Plan == plan) button.Modulate = Ui.Player;
                row.AddChild(button);
            }
            if (f.PlanByHand)
                row.AddChild(Small("Auto", () => _screen.IssueNow(new SetGroundPlanCommand(Side.Player, id, null), "ground plan"), "Hand the plan back to Auto"));
            row.AddChild(Small("Pull out", () => _screen.IssueNow(new PullOutTroopsCommand(Side.Player, id), "pulling troops out"),
                "Lift the troops off again (those near the landing zone get away)"));
        }
        row.AddChild(Small("Board ▸", () => _screen.GroundScreenId = id, "Open the planet board: who holds what, the front, troops and buildings"));
        foreach (var c in f.Calls.Where(c => c.Open)) CallRow(w, c, ours, option => new GroundCallCommand(Side.Player, id, c.Id, option));
        if (f.Notes.Count > 0) Text(f.Notes[^1], 12);
    }

    /// <summary>Share of the open ground (not mountains) the attacker holds.</summary>
    public static double Taken(Body b, GroundFront f)
    {
        var open = f.Tiles.Count(c => c != 'm');
        return open == 0 ? 0 : f.Tiles.Count(c => c == 'A') / (double)open;
    }

    // ---- After battle ---------------------------------------------------------------------------

    private void AfterRows(World w, Aftermath a)
    {
        _box.AddChild(Ui.Label($"{a.FleetName}{(a.Done ? " (done)" : "")}", 13, Ui.Text));
        var id = a.Id;
        foreach (var t in a.Tasks)
        {
            var row = Row();
            var mark = t.State switch { "done" => "✓", "doing" => "▶", "skipped" => "–", _ => "○" };
            var label = Ui.Label($"{mark} {AfterBattle.StepName(t.Step)}: {t.Note}", 12, t.State == "skipped" ? Ui.Dim : Ui.Text);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(Width - 90, 0);
            row.AddChild(label);
            if (!a.Done && t.Step != AfterStep.Home && t.State == "queued")
            {
                var step = t.Step;
                var on = t.On;
                row.AddChild(Small(on ? "Skip" : "Do it", () => _screen.IssueNow(new SetAfterStepCommand(Side.Player, id, step, !on), "after battle"),
                    on ? "Leave this step out" : "Put this step back in"));
            }
        }
        if (a.Done) return;
        Text($"Way home: {(a.Home is HomeChoice h ? AfterBattle.HomeName(h) + (a.HomeByHand ? " (your pick)" : " (Auto)") : "waiting for you")} · Auto would pick {AfterBattle.HomeName(a.AutoHome)}: {a.Why}", 12);
        var choices = Flow();
        foreach (var choice in Enum.GetValues<HomeChoice>())
        {
            var c = choice;
            var b = Small(AfterBattle.HomeName(choice), () => _screen.IssueNow(new SetHomeChoiceCommand(Side.Player, id, c), "way home"), AfterBattle.HomeWhat(choice));
            if (a.Home == choice) b.Modulate = Ui.Player;
            choices.AddChild(b);
        }
    }

    private void BoatRows(World w, Lifeboat lb)
    {
        var ours = lb.Side == Side.Player;
        var boatId = lb.Id;
        var title = $"{(lb.Boat ? "Lifeboat" : "Escape pods")} of {lb.ShipName}{(ours ? "" : " (rival: picking them up takes prisoners)")}, {lb.CrewK * 1000:0} crew";
        _box.AddChild(Ui.Label(title, 13, ours ? Ui.Text : Ui.Rival));
        Live(12, Ui.Dim, w2 => w2.Lifeboats.FirstOrDefault(b => b.Id == boatId) is not Lifeboat b ? "Picked up, home or lost"
            : $"Air for {Format.Hours(Lifeboats.AirLeftHours(w2, b))}"
              + (b.HeadingBodyId is int hb && w2.FindBody(hb) is Body home ? $" · crawling towards {home.Name}" : " · drifting")
              + (b.RescuerFleetId is int rid && w2.FindFleet(rid) is Fleet rf && rf.Side == Side.Player ? $" · {rf.Name} is on the way" : ""));
        if (lb.RescuerFleetId is int r && w.FindFleet(r)?.Side == Side.Player) return;
        var fleets = w.Fleets.Where(f => f.Side == Side.Player && f.ShipIds.Count > 0 && f.Rescue == null)
            .Select(f => (Fleet: f, Why: Lifeboats.CanSend(w, f, lb), Hours: Lifeboats.HoursTo(w, f, lb)))
            .OrderBy(x => x.Why == null ? 0 : 1).ThenBy(x => x.Hours).ThenBy(x => x.Fleet.Id).Take(3).ToList();
        if (fleets.Count == 0) return;
        var row = Flow();
        foreach (var (f, why, hours) in fleets)
        {
            var fleetId = f.Id;
            var b = Small($"Send {f.Name} ({Format.Hours(hours)})", () => _screen.IssueNow(new RescueCommand(Side.Player, fleetId, boatId), "rescue"),
                why ?? $"{f.Name} flies out, picks them up and comes back");
            b.Disabled = why != null;
            row.AddChild(b);
        }
    }

    // ---- Calls ----------------------------------------------------------------------------------

    private void CallRow(World w, WarCall c, bool ours, Func<int, GameCommand> pick)
    {
        var text = Ui.Label($"⚑ {c.Text}", 13, Ui.Rival);
        text.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        text.CustomMinimumSize = new Vector2(Width, 0);
        _box.AddChild(text);
        if (!ours) return;
        var row = Flow();
        for (var i = 0; i < c.Options.Count; i++)
        {
            var option = i;
            var b = Small(c.Options[i], () => _screen.IssueNow(pick(option), "war call"), i < c.Effects.Count ? c.Effects[i] : c.Options[i]);
            if (i == c.AutoPick) b.TooltipText += " (Auto's pick)";
            row.AddChild(b);
        }
        row.AddChild(Small("Go with Auto", () => _screen.IssueNow(pick(c.AutoPick), "war call"), $"Auto picks: {c.Options[c.AutoPick]}"));
        var due = c.DueHour;
        Live(11, Ui.Dim, w2 => due > w2.Hour ? $"Auto picks in {Format.Hours(due - w2.Hour)} if you don't" : "");
    }

    // ---- Crews and spoils -----------------------------------------------------------------------

    private void Crews(World w)
    {
        Heading("Crews");
        var ships = w.Ships.Where(s => s.Side == Side.Player && Lightlag.Sim.Core.Crews.Crewed(s)).ToList();
        var ranks = Enum.GetValues<Rank>().Select(r => $"{ships.Count(s => Lightlag.Sim.Core.Crews.RankOf(s) == r)} {r}");
        Text($"Ships by crew rank: {string.Join(", ", ranks)}. Crews gain rank in battle, on patrol and at an academy; casualties replaced with new hands dilute it.");
        var reserve = Economy.Of(w, Side.Player).ReserveCrews;
        if (reserve.Count > 0)
            Text($"Rescued crews waiting for a ship: {string.Join(", ", reserve.OrderByDescending(x => x).Select(x => Lightlag.Sim.Core.Crews.RankOf(x).ToString()))}. The next ships built take them on.");
    }

    private void Spoils(World w)
    {
        var s = Economy.Of(w, Side.Player).Spoils;
        var t = Economy.Of(w, Side.Player).Totals;
        if (s.Scraps == 0 && t.PrisonersTaken <= 0 && t.ShipsCaptured == 0) return;
        Heading("What we learned of the rival");
        Text($"Intel level {Lightlag.Sim.Core.Spoils.Level(w, Side.Player)} · {s.Scraps} scraps · {t.PrisonersTaken * 1000:0} prisoners · {t.ShipsCaptured} ships captured");
        foreach (var note in s.Notes.TakeLast(5).Reverse()) Text("· " + note, 12);
    }

    // ---- Calls waiting: pause and tell -----------------------------------------------------------

    /// <summary>Calls the player asked to make: the first time one shows up, pause and say so (with a click to the War tab).</summary>
    public static bool WatchCalls(World w, HashSet<string> seen, Action<string> toast)
    {
        var found = false;
        foreach (var b in w.Boardings.Where(b => b.Side == Side.Player && b.Outcome == null))
            foreach (var c in b.Calls.Where(c => c.Open && c.DueHour > w.Hour))
                found |= Fresh(seen, $"b{b.Id}:{c.Id}", $"Boarding {b.TargetName}: {c.Text}", toast);
        foreach (var body in w.Bodies.Where(b => b.Front is { Side: Side.Player }))
            foreach (var c in body.Front!.Calls.Where(c => c.Open && c.DueHour > w.Hour))
                found |= Fresh(seen, $"g{body.Id}:{c.Id}", $"{body.Name}: {c.Text}", toast);
        foreach (var a in w.Aftermaths.Where(a => a.Side == Side.Player && !a.Done && a.Home == null && a.HomeDueHour > w.Hour))
            found |= Fresh(seen, $"a{a.Id}", $"{a.FleetName} has cleared up: pick the way home", toast);
        return found;
    }

    private static bool Fresh(HashSet<string> seen, string key, string text, Action<string> toast)
    {
        if (!seen.Add(key)) return false;
        toast(text);
        return true;
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
        row.AddThemeConstantOverride("separation", 4);
        _box.AddChild(row);
        return row;
    }

    private HFlowContainer Flow()
    {
        var row = new HFlowContainer { CustomMinimumSize = new Vector2(Width, 0) };
        row.AddThemeConstantOverride("h_separation", 4);
        row.AddThemeConstantOverride("v_separation", 4);
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
