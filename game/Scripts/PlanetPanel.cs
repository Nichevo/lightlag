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
    private HBoxContainer _build = null!, _people = null!;
    private readonly Dictionary<string, Button> _buildButtons = new();
    private MenuButton _settle = null!;
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
        foreach (var data in Planets.D.Buildings ?? new List<BuildingData>())
        {
            var kind = data.Id;
            var b = Ui.SmallButton($"Build {data.Name.ToLowerInvariant()}", () => _screen.IssueNow(new BuildOnPlanetCommand(Side.Player, _bodyId, kind), "building"),
                $"{data.Effect}. {data.Cost:0} Industry, {data.Days:0} days, needs {data.StaffK:0.#}k people to run");
            _buildButtons[kind] = b;
            _build.AddChild(b);
        }
        box.AddChild(_build);

        _people = new HBoxContainer();
        _people.AddThemeConstantOverride("separation", 6);
        _settle = new MenuButton { Text = "Settle ▾", Flat = false, FocusMode = FocusModeEnum.None };
        _settle.AddThemeFontSizeOverride("font_size", 14);
        _settle.AboutToPopup += FillSettleMenu;
        _settle.GetPopup().IdPressed += id => Main.Instance.RunGuarded("settle", () =>
            _screen.IssueNow(new SettleCommand(Side.Player, _bodyId, id < 0 ? null : (int)id), "settle"));
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
        popup.AddItem("Send settlers (no escort)", -1);
        foreach (var f in w.Fleets.Where(f => f.Side == Side.Player && f.PeopleRun == null && Combat.Armed(w, f)).OrderBy(f => f.Id))
            popup.AddItem($"Send settlers, escorted by {f.Name} ({f.ShipIds.Count} ships)", f.Id);
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
        else if (b.PopK <= 0)
            lines.Add($"Empty: room for {cap:0}k people. " + (mine ? $"Makes half its industry ({b.Industry * Planets.D.UnsettledShare:0.#}/day) until settlers arrive" : "Settle it from here (a settler ship is built at your shipyard)"));
        else
        {
            var growth = b.Hungry ? "shrinking: HUNGRY" : b.PopK >= cap * 0.99 ? "full" : "growing";
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
            var line = $"Buildings ({b.Buildings.Count}/{Planets.Plots(b)} plots): " + (b.Buildings.Count > 0 ? string.Join(", ", built) : "none");
            if (b.Building != null) line += $" · building a {Planets.BuildingKind(b.Building)?.Name.ToLowerInvariant()}, {b.BuildingHoursLeft / 24:0.#} days left";
            lines.Add(line);
        }
        if (b.LandingSide is Side lander)
            lines.Add($"GROUND FIGHT: {(lander == Side.Player ? "your" : "rival")} troops {b.LandingTroops:0.##}k against a garrison of {b.Garrison:0.##}k");
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
        _body.Text = string.Join("\n", lines);

        _build.Visible = mine && b.PopK > 0;
        foreach (var (kind, button) in _buildButtons)
        {
            var why = Planets.CanBuild(w, Side.Player, b.Id, kind);
            button.Disabled = why != null;
            var data = Planets.BuildingKind(kind)!;
            button.TooltipText = (why != null ? why + ". " : "") + $"{data.Effect}. {data.Cost:0} Industry, {data.Days:0} days, needs {data.StaffK:0.#}k people to run";
            button.Visible = kind != Planets.Farm || Planets.FarmFactor(b) > 0;
        }
        _people.Visible = cap > 0 && (mine || owner == null && b.PopK <= 0);
        if (_people.Visible)
        {
            var why = Planets.CanSettle(w, Side.Player, b.Id);
            var cls = GameData.ShipClass(Planets.SettlerClass);
            _settle.Disabled = why != null;
            _settle.TooltipText = (why != null ? why + ". " : "")
                + $"The shipyard builds a settler ship ({cls.Cost:0} Industry, {cls.Cost / GameData.Economy.BuildIndustryPerDay:0.#} days). It takes up to {cls.Cargo:0}k people from "
                + $"{Economy.ShipyardOf(w, Side.Player)?.Name ?? "the shipyard planet"}, flies here and lands for good. On the way it can be blockaded or shot down: "
                + "pick a fleet to escort it, and that fleet comes to fetch it, flies out with it and stays in orbit here";
            _topUp.Visible = mine;
            _topUp.SetPressedNoSignal(b.AutoTopUp);
        }
    }
}
