using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>Shared text and pickers for yards and their berths (Shipyard drop-down and planet screen; see Yards).</summary>
public static class YardViews
{
    private const Side Me = Side.Player;

    /// <summary>The player's planets with at least one working berth: the home yard first, then by name.</summary>
    public static List<Body> Planets(World w)
    {
        var home = Yards.Home(w, Me);
        return w.Bodies.Where(b => b.Owner == Me && Yards.At(w, Me, b).Any())
            .OrderBy(b => b == home ? 0 : 1).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>"Ground · Shipyard 180 m: Gun frigate, 2 d left" or "...: free".</summary>
    public static string BerthLine(World w, Berth b)
    {
        var o = Yards.On(w, Me, b);
        var head = $"{Yards.BerthName(b.Key)} · {b.Name} {b.MaxM:0} m{(b.Rate < 0.95 ? $" ({b.Rate * 100:0} % speed)" : "")}";
        if (o == null) return head + ": free";
        var state = o.Blockaded ? "BLOCKADED" : o.Waiting != null ? "PAUSED, " + o.Waiting : $"{Format.Hours(o.HoursLeft)} left";
        return $"{head}: {Economy.NameOf(o)}, {state}";
    }

    /// <summary>One line for a yard planet in the Shipyard drop-down: its berths and the Shipyard's armour.</summary>
    public static string PlanetLine(World w, Body b)
    {
        var berths = Yards.At(w, Me, b).ToList();
        var bits = berths.Select(x => Yards.On(w, Me, x) is WorkOrder o ? $"{Yards.BerthName(x.Key)}: {Economy.NameOf(o)} {Format.Hours(o.HoursLeft)}" : $"{Yards.BerthName(x.Key)} {x.MaxM:0} m free");
        var armour = b.Shipyard ? $" · armour {b.ShipyardArmour / GameData.Economy.Shipyard.Armour * 100:0} %" : "";
        var attack = b.LastBombardedHour is double h && w.Hour - h <= 24 ? " · UNDER ATTACK" : Economy.Blockaded(w, b) ? " · BLOCKADED" : "";
        return string.Join(" · ", bits) + armour + attack;
    }

    /// <summary>A picker of the yards a ship of this class can be built at: Auto first (null), then each planet with a berth that takes it.</summary>
    public static OptionButton YardPicker(World w, string? classId, int? current, bool pinned, Action<int?> picked, string autoText = "Auto")
    {
        var pick = new OptionButton { FocusMode = Control.FocusModeEnum.None, FitToLongestItem = false, ClipText = true,
            TooltipText = "Where it is built. Auto: the free berth that takes the hull and has the materials, nearest the fleet it joins (else the home yard). A planet: it waits for a berth there" };
        pick.AddThemeFontSizeOverride("font_size", 12);
        var ids = new List<int?> { null };
        pick.AddItem(autoText);
        foreach (var b in Planets(w))
        {
            if (classId != null && !Yards.At(w, Me, b).Any(x => Yards.Fits(x, classId))) continue;
            ids.Add(b.Id);
            pick.AddItem(b.Name + (Yards.Home(w, Me) == b ? " ★" : ""));
        }
        pick.Selected = pinned ? Math.Max(0, ids.IndexOf(current)) : 0;
        pick.ItemSelected += index => picked(ids[(int)index]);
        return pick;
    }

    /// <summary>The materials the ships placed at this planet still need, against what it holds: "alloys 38 of 50".</summary>
    public static List<(string Good, double Held, double Need)> Needs(World w, Body b)
    {
        var need = new Dictionary<string, double>();
        foreach (var o in w.Orders.Where(o => o.Side == Me && o.Kind == OrderKind.Ship && !o.Paid && o.YardBodyId == b.Id))
            foreach (var (good, amount) in Races.Materials(w, Me, Economy.MaterialsOf(o)) ?? new Dictionary<string, double>())
                need[good] = need.GetValueOrDefault(good) + amount;
        return need.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => (n.Key, Colonies.Held(b, n.Key), n.Value)).ToList();
    }
}

/// <summary>
/// The planet screen's Yard box (above the Station box): the planet's berths and what each is building, Build
/// here, the ships placed here and the materials they still need (with a button to keep them stocked), the
/// home yard star, and the upgrade from dry dock to Shipyard.
/// </summary>
public static class YardPanel
{
    private const Side Me = Side.Player;

    /// <summary>What the box shows that needs a rebuild when it changes (live text updates on its own).</summary>
    public static string Shape(World w, Body b) =>
        $"{b.Owner}|{b.Shipyard}|{b.YardUpgradeHoursLeft != null}|{Yards.Home(w, Me)?.Id}|{string.Join(",", Yards.At(w, Me, b).Select(x => x.Key))}"
        + $"|{Colonies.Built(b).Count(p => p.Kind == Colonies.DryDock)}|{Economy.Of(w, Me).Researched.Count}|{w.Winner}";

    public static Control Build(World w, Body b, Func<Func<World, string>, int, Color?, Label> live, Action<GameCommand, string> issue, GameScreen screen)
    {
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 4);
        var berths = Yards.At(w, Me, b).ToList();
        var home = Yards.Home(w, Me) == b;
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 8);
        var title = Ui.Label(berths.Count == 0 ? "Yard" : home ? "Yard · ★ home yard" : Yards.IsYardPlanet(w, Me, b) ? "Yard · yard planet" : "Yard", 16);
        title.TooltipText = "A yard planet (a Shipyard, or a station with a great slipway) keeps you in the game: you lose when the last one falls. The home yard is your seat; if it falls the next one takes over and every yard stops for a while";
        title.MouseFilter = Control.MouseFilterEnum.Pass;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        head.AddChild(title);
        if (b.Owner == Me && !home && Yards.IsYardPlanet(w, Me, b))
            head.AddChild(Ui.SmallButton("Make home yard ★", () => issue(new SetHomeYardCommand(Me, b.Id), "moving the home yard"),
                "Make this your home yard (your seat): orders go out from here and Auto measures from here"));
        col.AddChild(head);
        if (b.Owner != Me) return col;

        if (berths.Count == 0)
            col.AddChild(Wrap("No yard here. A dry dock builds hulls up to 120 m once Colony shipyards is researched (and can be upgraded to a Shipyard); a station's yard bay builds in orbit (a starbase's great slipway: up to 260 m).", 13, Ui.Dim));
        foreach (var x in berths)
        {
            var key = x.Key;
            col.AddChild(live(w2 => Yards.At(w2, Me, w2.FindBody(b.Id)!).FirstOrDefault(y => y.Key == key) is Berth y ? YardViews.BerthLine(w2, y) : "", 13, null));
        }
        if (berths.Count > 0)
        {
            col.AddChild(live(w2 => Yards.Stopped(w2, Me) is string why ? "STOPPED: " + why
                : Economy.Blockaded(w2, b) ? "BLOCKADED: enemy warships in orbit stop this yard"
                : Economy.Fort(w2, b) != null ? "The station's guns keep a blockade off: this yard keeps building under attack"
                : "No armed station over it: enemy warships holding orbit would stop this yard", 12, Ui.Dim));
            col.AddChild(BuildHere(w, b, berths, screen));
            col.AddChild(live(w2 =>
            {
                var here = w2.Orders.Where(o => o.Side == Me && o.Kind == OrderKind.Ship && !o.Paid && o.YardBodyId == b.Id).ToList();
                return here.Count == 0 ? "No ships queued here" : "Queued here: " + string.Join(", ", here.Select(o => Economy.NameOf(o) + (o.YardPinned ? "" : " (Auto)")));
            }, 12, Ui.Dim));
            var needs = new HBoxContainer();
            needs.AddThemeConstantOverride("separation", 6);
            var line = live(w2 => YardViews.Needs(w2, b) is { Count: > 0 } n
                ? "Materials for them here: " + string.Join(" · ", n.Select(x => $"{Colonies.GoodName(x.Good).ToLowerInvariant()} {x.Held:0} of {x.Need:0}{(x.Held + 1e-9 < x.Need ? " SHORT" : "")}"))
                : "", 12, null);
            line.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            needs.AddChild(line);
            needs.AddChild(Ui.SmallButton("Keep stocked", () =>
            {
                var n = YardViews.Needs(screen.World, b);
                if (n.Count == 0) { Main.Instance.Toast("Nothing queued here needs materials"); return; }
                foreach (var x in n) issue(new SetStockRuleCommand(Me, b.Id, x.Good, Math.Ceiling(x.Need), null, Math.Ceiling(x.Need)), "keeping the yard stocked");
                Main.Instance.Toast($"{b.Name} now keeps what its queued ships need: transport brings the rest");
            }, "Set this planet's stock rules to keep and import what the ships queued here need (Supply, G, shows the rules)"));
            col.AddChild(needs);
        }

        // The upgrade from dry dock to Shipyard.
        if (!b.Shipyard && Colonies.Built(b).Any(p => p.Kind == Colonies.DryDock))
        {
            if (b.YardUpgradeHoursLeft != null)
                col.AddChild(live(w2 => w2.FindBody(b.Id)?.YardUpgradeHoursLeft is double h ? $"Upgrading the dry dock to a Shipyard: {Format.Hours(h)} left" + (Economy.Blockaded(w2, b) ? " (paused: blockaded)" : "") : "", 13, Ui.Warn));
            else
            {
                var why = Yards.CanUpgrade(w, Me, b.Id);
                var button = Ui.SmallButton($"Upgrade dry dock to Shipyard · {GameData.Economy.Shipyard.UpgradeCost:0}", () => issue(new UpgradeYardCommand(Me, b.Id), "upgrading to a shipyard"),
                    (why != null ? why + ". " : "") + Yards.UpgradeText());
                button.Disabled = why != null;
                col.AddChild(button);
                if (why != null) col.AddChild(live(w2 => Yards.CanUpgrade(w2, Me, b.Id) is string now ? "Upgrade: " + now : "Upgrade: ready", 12, Ui.Dim));
            }
        }
        return col;
    }

    /// <summary>"Build here": a picker of the hulls a berth here takes; picking one queues it pinned to this yard.</summary>
    private static Control BuildHere(World w, Body b, List<Berth> berths, GameScreen screen)
    {
        var pick = new OptionButton { FocusMode = Control.FocusModeEnum.None, FitToLongestItem = false, ClipText = true, CustomMinimumSize = new Vector2(260, 28),
            TooltipText = "Queue a ship built here (pinned to this yard: it waits for a berth here). The Shipyard drop-down (V) has the whole queue" };
        pick.AddThemeFontSizeOverride("font_size", 13);
        pick.AddItem("Build here ▾");
        var keys = new List<string> { "" };
        foreach (var cls in GameData.ShipClasses.Values)
        {
            if (cls.Id == Planets.SettlerClass || cls.Station || !berths.Any(x => Yards.Fits(x, cls.Id))) continue;
            if (cls.Needs.Length > 0 && !Tech.Has(w, Me, cls.Needs)) continue;
            keys.Add(cls.Id);
            pick.AddItem($"{cls.Name} · {cls.Cost:0}");
        }
        pick.Selected = 0;
        pick.ItemSelected += index =>
        {
            if (index <= 0) return;
            screen.QueueAt(keys[(int)index], b.Id);
            pick.Selected = 0;
        };
        return pick;
    }

    private static Label Wrap(string text, int size, Color color)
    {
        var l = Ui.Label(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        l.CustomMinimumSize = new Vector2(120, 0);
        return l;
    }
}
