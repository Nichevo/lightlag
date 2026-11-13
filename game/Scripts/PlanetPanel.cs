using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The planet screen, bottom right when a planet or moon is selected: its people, happiness and why,
/// food, industry, buildings (with build buttons), settlers (Settle with an optional escort, Auto top-up) and, for an
/// enemy planet, how close it is to giving up.
/// </summary>
public partial class PlanetPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Label _title = null!, _body = null!;
    private HBoxContainer _build = null!, _people = null!, _war = null!;
    private MenuButton _settle = null!, _satellite = null!;
    private CheckButton _topUp = null!;
    private int _bodyId;

    public PlanetPanel() : this(null!) { }
    public PlanetPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 14);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        AddChild(box);
        _title = Ui.Label("", 18, Ui.Text);
        box.AddChild(_title);
        _body = Ui.Label("", 14);
        _body.CustomMinimumSize = new Vector2(380, 0);
        _body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        box.AddChild(_body);

        _build = new HBoxContainer();
        _build.AddThemeConstantOverride("separation", 4);
        _build.AddChild(Ui.SmallButton("Open planet ▸", () => _screen.PlanetScreenId = _bodyId,
            "The planet screen: lay out buildings on its map, set priorities, storage and transport"));
        _build.AddChild(Ui.SmallButton("Planets list", () => _screen.ShowPlanets = true, "All worlds in one list (Y)"));
        box.AddChild(_build);

        // Satellites (Plan 3 build 4): over any planet or moon, or at its leading or trailing point.
        var sats = new HBoxContainer();
        _satellite = new MenuButton { Text = "Satellite ▾", Flat = false, FocusMode = FocusModeEnum.None };
        _satellite.AddThemeFontSizeOverride("font_size", 14);
        _satellite.AboutToPopup += FillSatelliteMenu;
        _satellite.GetPopup().IdPressed += id => Main.Instance.RunGuarded("satellite", () => LaunchSatellite((int)id));
        sats.AddChild(_satellite);
        box.AddChild(sats);

        _war = new HBoxContainer();
        _war.AddChild(Ui.SmallButton("Ground fight ▸", () => _screen.GroundScreenId = _bodyId,
            "The planet's board in the fight: who holds what, the front, troops and buildings, the plan and the calls"));
        box.AddChild(_war);

        _people = new HBoxContainer();
        _people.AddThemeConstantOverride("separation", 6);
        _settle = new MenuButton { Text = "Settle ▾", Flat = false, FocusMode = FocusModeEnum.None };
        _settle.AddThemeFontSizeOverride("font_size", 14);
        _settle.AboutToPopup += FillSettleMenu;
        _settle.GetPopup().IdPressed += id => Main.Instance.RunGuarded("settle", () =>
            _screen.IssueNow(new SettleCommand(Side.Player, _bodyId, id == Ui.MenuNone ? null : (int)id), "settle"));
        _people.AddChild(_settle);
        _topUp = new CheckButton { Text = "Auto top-up", FocusMode = FocusModeEnum.None,
            TooltipText = "Settlers are ordered for this planet by themselves, one ship at a time, until it is nearly full" };
        _topUp.AddThemeFontSizeOverride("font_size", 14);
        _topUp.Toggled += on => Main.Instance.RunGuarded("auto top-up", () => _screen.IssueNow(new SetAutoTopUpCommand(Side.Player, _bodyId, on), "auto top-up"));
        _people.AddChild(_topUp);
        box.AddChild(_people);
    }

    /// <summary>Settle alone, or with one of your fleets as escort (it comes to the shipyard, flies out with the settlers and stays).</summary>
    private void FillSettleMenu()
    {
        var popup = _settle.GetPopup();
        popup.Clear();
        var w = Main.Instance.Sim!.World;
        // Say where they leave from: the shipyard planet builds the ship and gives the people.
        if (Planets.SettleSource(w, Side.Player, _bodyId) is { } src)
        {
            popup.AddItem($"From {src.From.Name} (your shipyard): about {src.PeopleK:0.#}k people, {Format.Distance(src.DistanceMm)} away", Ui.MenuNone - 1);
            popup.SetItemDisabled(popup.ItemCount - 1, true);
            popup.AddItem($"Trip risk: {src.Risk} ({src.RiskWhy})", Ui.MenuNone - 2);
            popup.SetItemDisabled(popup.ItemCount - 1, true);
            popup.AddSeparator();
        }
        var src2 = Planets.SettleSource(w, Side.Player, _bodyId);
        popup.AddItem("Send settlers (no escort)" + (src2 != null ? $" · risk {src2.Risk}" : ""), Ui.MenuNone);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && f.PeopleRun == null && Combat.Armed(w, f)).OrderBy(f => f.Id))
            popup.AddItem($"Send settlers, escorted by {f.Name} ({f.ShipIds.Count} ships)" + (src2 != null ? $" · risk {src2.EscortedRisk}" : ""), f.Id);
    }

    /// <summary>Menu ids: kind index times 3 plus the spot.</summary>
    private void FillSatelliteMenu()
    {
        var popup = _satellite.GetPopup();
        popup.Clear();
        var w = Main.Instance.Sim!.World;
        var b = w.FindBody(_bodyId);
        if (b == null) return;
        for (var k = 0; k < Satellites.Kinds.Count; k++)
        {
            var kind = Satellites.Kinds[k];
            foreach (var spot in Enum.GetValues<SatSpot>())
            {
                var id = k * 3 + (int)spot;
                var why = Satellites.CanLaunch(w, Side.Player, kind.Id, b.Id, spot);
                var from = Satellites.LaunchSite(w, Side.Player, b.Position);
                var cost = Satellites.CostOf(w, kind, from);
                popup.AddItem($"{kind.Name} to {Satellites.SpotName(b.Name, spot)} · {cost:0}" + (why != null ? $" ({why})" : ""), id);
                popup.SetItemDisabled(popup.GetItemIndex(id), why != null);
                popup.SetItemTooltip(popup.GetItemIndex(id), $"{kind.Effect}. Launched from {from?.Name ?? "your nearest world"}: {kind.Cost:0} Industry plus {cost - kind.Cost:0.#} for the lift to orbit");
            }
        }
    }

    private void LaunchSatellite(int id)
    {
        var k = id / 3;
        if (k < 0 || k >= Satellites.Kinds.Count) return;
        var cmd = new LaunchSatelliteCommand(Side.Player, Satellites.Kinds[k].Id, _bodyId, (SatSpot)(id % 3));
        if (Satellites.CanLaunch(Main.Instance.Sim!.World, Side.Player, cmd.Kind, cmd.BodyId, cmd.Spot) is string problem) { Main.Instance.Toast(problem); return; }
        _screen.IssueNow(cmd, "launching a satellite");
    }

    private static string Signed(double v) => v >= 0 ? $"+{v:0}" : $"−{-v:0}";

    public void Refresh(Simulation sim, Body b)
    {
        var w = sim.World;
        _bodyId = b.Id;
        var owner = b.Owner;
        var mine = owner == Side.Player;
        _title.Text = $"{b.Name} · {Format.Title(b.Kind.ToString().ToLowerInvariant())}, {b.Type}" + (b.Home ? " · home world" : "");
        _title.AddThemeColorOverride("font_color", mine ? Ui.Player : owner == Side.Rival ? Ui.Rival : Ui.Text);
        var lines = new List<string>();
        lines.Add(owner switch
        {
            Side.Player => "Yours" + (b.Shipyard ? $" · shipyard, armour {b.ShipyardArmour / GameData.Economy.Shipyard.Armour:P0}" : ""),
            Side.Rival => "Held by the rival" + (b.Shipyard ? $" · shipyard, armour {b.ShipyardArmour / GameData.Economy.Shipyard.Armour:P0}" : ""),
            _ => b.PopK > 0 ? "Independent: hold orbit to win it over" : "Nobody's",
        });
        var cap = Planets.Cap(b);
        if (cap <= 0) lines.Add("Nobody can live here (a giant): it can be held, but not settled");
        else if (b.PopK <= 0 && b.Abandoned && owner != null)
            lines.Add("ABANDONED: its hungry people left. It makes nothing until settlers land" + (mine ? " (settle it from here)" : ""));
        else if (b.PopK <= 0)
            lines.Add($"Empty: room for {cap:0}k people. " + (mine ? $"Makes half its industry ({b.Industry * Planets.D.UnsettledShare:0.#}/day) until settlers arrive" : "Settle it from here (a settler ship is built at your shipyard)"));
        else
        {
            var growth = b.Hungry ? (b.LeavingK > 0 ? "shrinking: HUNGRY, people leaving" : "shrinking: HUNGRY") : b.PopK >= cap * 0.99 ? "full" : "growing";
            lines.Add($"People: {b.PopK:0.#}k of {cap:0}k ({growth})" + (b.OwnShare < 0.995 && owner != null ? $" · {b.OwnShare:P0} are the holder's own" : ""));
        }
        if (b.PopK > 0 && owner != null)
        {
            if (w.Rules.Happiness)
            {
                var (target, parts) = Planets.TargetHappiness(w, b);
                lines.Add($"Happiness {b.Happiness:0} → {target:0}: " + string.Join(" · ", parts.Select(p => $"{p.Why} {Signed(p.Amount)}")));
            }
            var made = Planets.FoodMadePerDay(b);
            var need = Planets.FoodNeedPerDay(b);
            var cut = Economy.Blockaded(w, b);
            lines.Add($"Food: grows {made:0.##}, eats {need:0.##} a day · store {(need > 0 ? b.FoodStock / need : 0):0} days"
                      + (cut ? " · BLOCKADED: living off its own farms and store" : " · shared with your other planets"));
            if (mine && Planets.LeavingNote(w, b) is string leaving) lines.Add(leaving);
            if (mine && b.NewcomersK > 0.05) lines.Add($"Newcomers: {b.NewcomersK:0.#}k hungry people from other worlds settling in");
            lines.Add($"Industry {Planets.IndustryPerDay(w, b):0.#} a day · staffed {Planets.Staffing(b):P0} (needs {Planets.StaffNeeded(b):0.#}k)"
                      + (cut ? " · nothing while blockaded" : ""));
        }
        else if (owner != null && cap <= 0 && b.Industry > 0)
            lines.Add($"Industry {Planets.IndustryPerDay(w, b):0.#} a day");
        else if (owner == null && b.Industry > 0)
            lines.Add($"Worth {b.Industry:0} Industry a day once settled (half that while it is only held)");
        if (b.Buildings.Count > 0 || b.Building != null || (mine && b.PopK > 0))
        {
            var built = b.Buildings.GroupBy(x => x).Select(g => $"{Planets.BuildingKind(g.Key)?.Name ?? g.Key} ×{g.Count()}");
            var line = "Buildings: " + (b.Buildings.Count > 0 ? string.Join(", ", built) : "none");
            if (b.Building != null) line += $" · building a {Planets.BuildingKind(b.Building)?.Name.ToLowerInvariant()}, {b.BuildingHoursLeft / 24:0.#} days left";
            lines.Add(line);
            // Say plainly which buildings can't start or go on, and what they need.
            var stuck = Colonies.HeldUp(w, b);
            if (stuck.Count > 0) lines.Add("WAITING: " + string.Join(" · ", stuck.Select(x => $"{x.Name} ({x.Why})")));
            var queued = b.Pieces.Count(p => p.Waiting);
            if (queued > 0) lines.Add($"Queued to build: {queued} (one at a time, in order)");
        }
        if (mine && b.Board != null)
        {
            lines.Add($"Power {b.PowerUsed:0}/{b.PowerMade:0}" + (b.PowerWanted > b.PowerMade + 1e-6 ? $" (SHORT: wants {b.PowerWanted:0})" : "")
                      + $" · priorities: {Colonies.PresetName(b.Preset)}"
                      + (Colonies.HoldsOutDays(b) is double days ? $" · holds out {days:0} days if cut off" : ""));
            if (b.BatteryRoom > 0 || b.InShadow || Grid.Shedding(w, b)) lines.Add(Grid.Summary(w, b));
            if (Colonies.PowerNote(w, b) is string powerNote) lines.Add("POWER: " + powerNote);
            var held = Colonies.Goods.Where(g => Colonies.Held(b, g.Id) >= 0.5).Select(g => $"{g.Short} {Colonies.Held(b, g.Id):0}");
            lines.Add("Goods: " + (held.Any() ? string.Join(" · ", held) : "none"));
        }
        if (b.LandingSide is Side lander)
            lines.Add($"GROUND FIGHT: {(lander == Side.Player ? "your" : "rival")} troops {b.LandingTroops:0.##}k against a garrison of {b.Garrison:0.##}k"
                      + (b.Front is GroundFront front ? $" · {Ground.PlanName(front.Plan)}, {WarTab.Taken(b, front):P0} of the ground taken"
                                                         + (Ground.DaysLeft(w, b) is double left ? $", about {left:0.#} days left" : "") : ""));
        if (b.BlockadeHours >= 7 * 24 && owner != null && b.PopK > 0)
            lines.Add($"Blockaded {b.BlockadeHours / 24:0} days: the garrison is down to {Ground.BlockadeFactor(b):P0} of its strength");
        if (b.Grudge is Side g)
            lines.Add(g == Side.Player ? "Holds a grudge against you (you bombed its people): it will never surrender to you, only to troops" : "Holds a grudge against the rival: it will never surrender to it");
        if (owner == Side.Rival && b.PopK > 0)
        {
            var line = Planets.SurrenderLine(w, Side.Player);
            lines.Add(Planets.Blockader(w, b) == Side.Player
                ? $"You are blockading it: it gives up below happiness {line:0} (now {b.Happiness:0})"
                : $"To take it: blockade it with armed ships (it gives up below happiness {line:0}), or land troops");
        }
        else if (owner == null && b.Industry > 0 && b.PopK <= 0 && b.ClaimSide is Side claimer)
            lines.Add($"{(claimer == Side.Player ? "You are" : "The rival is")} claiming it: {b.ClaimHours / 24:0.#} of {Economy.ClaimHoursFor(b) / 24:0} days");
        else if (owner != Side.Player && b.Industry > 0 && b.PopK <= 0 && !b.Shipyard)
            lines.Add($"Hold orbit here {Economy.ClaimHoursFor(b) / 24:0} days with no enemy in orbit to claim it" + (cap > 0 && owner == null ? ", or land settlers to take it at once" : ""));
        if (b.Shipyard && owner == Side.Rival && Economy.BombardStatus(w, b, Side.Player) is string attack) lines.Add("Shipyard: " + attack);
        if (w.Loop && Planets.SettlerStatus(w, Side.Player, b.Id) is string settlers) lines.Add("SETTLERS: " + settlers);
        if (w.Loop && mine && b.Board != null) lines.Add(Lift.Describe(w, b));
        var here = w.Satellites.Where(s => s.Side == Side.Player && s.BodyId == b.Id).ToList();
        if (here.Count > 0) lines.Add("Satellites: " + string.Join("; ", here.Select(s => Satellites.Describe(w, s))));
        _body.Text = string.Join("\n", lines);

        _build.Visible = mine && b.Board != null;
        _war.Visible = b.Front != null && b.Board != null;
        _satellite.GetParent<Control>().Visible = w.Loop && b.Kind != BodyKind.Star;
        var satWhy = Satellites.D.Needs.Length > 0 && !Tech.Has(w, Side.Player, Satellites.D.Needs) ? $"Research {GameData.Research(Satellites.D.Needs).Name} first (research screen, T)" : null;
        _satellite.Disabled = satWhy != null;
        _satellite.TooltipText = satWhy ?? "Launch a sensor or relay satellite to this body's orbit, or to its leading or trailing point (60° ahead or behind on its orbit)";
        _people.Visible = cap > 0 && (mine || owner == null && b.PopK <= 0);
        if (_people.Visible)
        {
            var why = Planets.CanSettle(w, Side.Player, b.Id);
            var cls = GameData.ShipClass(Planets.SettlerClass);
            _settle.Disabled = why != null;
            _settle.Text = Planets.SettleSource(w, Side.Player, b.Id) is { } from ? $"Settle from {from.From.Name} ▾" : "Settle ▾";
            _settle.TooltipText = (why != null ? why + ". " : "")
                + $"The shipyard builds a settler ship ({cls.Cost:0} Industry, {cls.Cost / GameData.Economy.BuildIndustryPerDay:0.#} days). It takes up to {cls.Cargo:0}k people from "
                + $"{Economy.ShipyardOf(w, Side.Player)?.Name ?? "the shipyard planet"}, flies here and lands for good. On the way it can be blockaded or shot down: "
                + "pick a fleet to escort it, and that fleet comes to fetch it, flies out with it and stays in orbit here";
            _topUp.Visible = mine;
            _topUp.SetPressedNoSignal(b.AutoTopUp);
        }
    }
}
