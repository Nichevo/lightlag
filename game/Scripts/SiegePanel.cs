using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The battle view (U) laid out for a planet attack: the planet as the floor and the orbit shells as bands
/// (low, high, far) with each fleet drawn in its shell, the planet's own fighters (its station, rail batteries,
/// silos, fighter base and dome) beside it with what still shoots, a siege clock, and the orders that only exist
/// here. The per-ship strips stay as they are on either side. Only draws and issues commands.
/// </summary>
public partial class SiegePanel : PanelContainer
{
    private readonly GameScreen _screen;
    private Bands _bands = null!;
    private Label _title = null!, _defenders = null!, _clock = null!;
    private Button _low = null!, _high = null!, _far = null!, _land = null!, _board = null!;
    private MenuButton _bomb = null!;
    private int? _fleetId, _bodyId;

    public SiegePanel() : this(null!) { }
    public SiegePanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 10);
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        v.AddThemeConstantOverride("separation", 4);
        _title = Ui.Label("", 14, Ui.Text);
        v.AddChild(_title);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _bands = new Bands { CustomMinimumSize = new Vector2(250, 150) };
        row.AddChild(_bands);
        _defenders = Ui.Label("", 12, Ui.Text);
        _defenders.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _defenders.CustomMinimumSize = new Vector2(290, 0);
        row.AddChild(_defenders);
        v.AddChild(row);
        _clock = Ui.Label("", 12, Ui.Dim);
        _clock.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_clock);
        var orders = new HFlowContainer();
        orders.AddThemeConstantOverride("h_separation", 4);
        Button Height(string text, OrbitHeight h, string tip) => Ui.SmallButton(text, () =>
        {
            if (Fleet() is Fleet f) _screen.IssueNow(new SetOrbitHeightCommand(f.Id, h), "orbit height");
        }, tip);
        _low = Height("Hold low orbit", OrbitHeight.Low, "Blockade, bomb and land from here: every gun reaches you");
        _high = Height("Stand off high", OrbitHeight.High, "Still blockades, bombs slower; only silos and station missiles reach");
        _far = Height("Far orbit", OrbitHeight.Far, "Out of reach (bar an orbital ring): watch and wait for relief, but no blockade");
        _bomb = new MenuButton { Text = "Bomb ▾", Flat = false, FocusMode = FocusModeEnum.None, TooltipText = "Pick a target: farms, mines or guns (bombing the people leaves a grudge)" };
        _bomb.AddThemeFontSizeOverride("font_size", 13);
        foreach (var (label, target) in new[] { ("Only blockade", BombTarget.None), ("Bomb farms", BombTarget.Farms), ("Bomb mines", BombTarget.Mines), ("Bomb guns", BombTarget.Guns), ("Bomb the people", BombTarget.People) })
            _bomb.GetPopup().AddItem(label, (int)target);
        _bomb.GetPopup().IdPressed += id => Main.Instance.RunGuarded("bombing", () =>
        {
            if (Fleet() is Fleet f) _screen.IssueNow(new SetBombingCommand(f.Id, (BombTarget)(int)id), "bombing");
        });
        _land = Ui.SmallButton("Land troops", () =>
        {
            if (Fleet() is Fleet f && _bodyId is int b) _screen.IssueNow(new LandTroopsCommand(f.Id, b, null), "landing");
        }, "");
        _board = Ui.SmallButton("Board the station", () =>
        {
            if (Fleet() is Fleet f && _bodyId is int b && Starbases.At(Main.Instance.Sim!.World, b) is Starbase st) _screen.IssueNow(new BoardStationCommand(f.Id, st.Id), "boarding a station");
        }, "");
        foreach (var c in new Control[] { _low, _high, _far, _bomb, _land, _board }) orders.AddChild(c);
        v.AddChild(orders);
        AddChild(v);
    }

    private Fleet? Fleet() => _fleetId is int id ? Main.Instance.Sim?.World.FindFleet(id) : null;

    /// <summary>The planet fight to show: the selected (or a battle) fleet of yours in orbit of an enemy planet, or an enemy besieging yours.</summary>
    public static (Body Planet, Fleet? Mine)? Siege(World w, IEnumerable<Fleet> fleets, Fleet? selected)
    {
        foreach (var f in (selected != null ? new[] { selected } : Array.Empty<Fleet>()).Concat(fleets))
        {
            if (f.Hold != HoldStyle.Orbit || f.OrbitBodyId is not int id || w.FindBody(id) is not Body b || !Economy.InOrbitAt(f, b) || b.Owner == null) continue;
            if (f.Side == Side.Player && b.Owner != Side.Player) return (b, f);
            if (f.Side != Side.Player && b.Owner == Side.Player) return (b, w.Fleets.FirstOrDefault(o => o.Side == Side.Player && Economy.InOrbitAt(o, b)));
        }
        return null;
    }

    public void Refresh(World w, Body b, Fleet? mine)
    {
        _bodyId = b.Id;
        _fleetId = mine?.Id;
        var attacking = b.Owner != Side.Player;
        _title.Text = attacking ? $"ATTACK ON {b.Name.ToUpperInvariant()}" : $"{b.Name.ToUpperInvariant()} UNDER ATTACK";
        _bands.World = w;
        _bands.Body = b;
        _bands.QueueRedraw();

        // The planet's fighters: its station, guns and the rest, with what still shoots.
        var lines = new List<string>();
        if (Starbases.At(w, b.Id) is Starbase st)
            lines.Add((st.Side == b.Owner ? "STATION " : "TAKEN STATION ") + Starbases.Summary(w, st));
        int Count(string kind) => Colonies.Built(b).Count(p => p.Kind == kind);
        var rails = Count(Planets.Guns);
        var silos = Count("missile-silo");
        lines.Add(rails + silos > 0
            ? $"GROUND GUNS {rails} rail batter{(rails == 1 ? "y" : "ies")} (low orbit, {Starbases.D.RailReachMm:0} Mm), {silos} silo{(silos == 1 ? "" : "s")} (high orbit, {Starbases.D.SiloReachMm:0} Mm)"
            : "GROUND GUNS silenced or none");
        if (Count("fighter-base") > 0) lines.Add($"FIGHTER BASE {Count("fighter-base")}");
        if (Count("shield-dome") > 0) lines.Add($"SHIELD DOME {Count("shield-dome")} (bombing gets through less)");
        lines.Add($"GARRISON about {Planets.GarrisonOf(b):0.#}k" + (b.LandingFactor > 1 ? $" (x{b.LandingFactor:0} while the fortress stands)" : ""));
        _defenders.Text = string.Join("\n", lines);

        // The siege clock.
        var clock = new List<string>();
        if (Economy.Blockaded(w, b)) clock.Add($"Blockade day {b.BlockadeHours / 24:0.#}");
        else if (Planets.Contested(w, b)) clock.Add("Orbit contested: no blockade while its guns or station fire");
        else clock.Add("No blockade");
        clock.Add($"happiness {b.Happiness:0} (gives up below {Planets.SurrenderLine(w, attacking ? Side.Player : Side.Rival):0})");
        clock.Add($"food {Colonies.Held(b, "food"):0}");
        if (Ground.HelpComing(w, b) is var (helper, days)) clock.Add($"relief: {helper.Name} in {days:0.#} days");
        _clock.Text = string.Join(" · ", clock);

        var f = mine;
        foreach (var c in new Control[] { _low, _high, _far, _bomb, _land, _board }) c.Visible = f != null && attacking;
        if (f == null || !attacking) return;
        static Color On(bool on) => on ? Ui.Player : new Color(1, 1, 1, 0.7f);
        _low.Modulate = On(f.OrbitHeight == OrbitHeight.Low);
        _high.Modulate = On(f.OrbitHeight == OrbitHeight.High);
        _far.Modulate = On(f.OrbitHeight == OrbitHeight.Far);
        _bomb.Text = f.Bombing == BombTarget.None ? "Bomb ▾" : $"Bombing {f.Bombing.ToString().ToLowerInvariant()} ▾";
        var land = Planets.CanLand(w, f, b);
        _land.Disabled = land != null;
        _land.TooltipText = land ?? $"Land {Planets.TroopsAboard(w, f):0.#}k troops, Auto picks the plan";
        var station = Starbases.At(w, b.Id);
        _board.Visible = station != null && station.Side != f.Side;
        if (_board.Visible)
        {
            var why = Starbases.CanBoard(w, f, station);
            _board.Disabled = why != null;
            _board.TooltipText = why ?? $"{Starbases.GarrisonK(w, station!):0.#}k hold it; take it and its hull, stores and slipway are yours";
        }
    }

    /// <summary>The planet as the floor and the three shells above it, with each fleet in its shell.</summary>
    private partial class Bands : Control
    {
        public World? World;
        public Body? Body;

        public override void _Draw()
        {
            if (World is not World w || Body is not Body b) return;
            var size = Size;
            var font = ThemeDB.FallbackFont;
            // Floor: the planet.
            var floor = new Rect2(0, size.Y - 26, size.X, 26);
            DrawRect(floor, Ui.SideColor(b.Owner) * new Color(1, 1, 1, 0.35f));
            DrawString(font, new Vector2(6, size.Y - 9), $"{b.Name} (ground guns, garrison)", HorizontalAlignment.Left, -1, 11, Ui.Text);
            var shells = new[] { (OrbitHeight.Low, "LOW · rails, silos, station"), (OrbitHeight.High, "HIGH · silos, station missiles"), (OrbitHeight.Far, "FAR · out of reach") };
            var bandH = (size.Y - 30) / 3;
            for (var i = 0; i < shells.Length; i++)
            {
                var y = size.Y - 30 - (i + 1) * bandH;
                DrawRect(new Rect2(0, y, size.X, bandH - 2), new Color(0.3f, 0.45f, 0.7f, 0.10f + 0.05f * (2 - i)));
                DrawString(font, new Vector2(6, y + 12), shells[i].Item2, HorizontalAlignment.Left, -1, 10, Ui.Dim);
                var x = 8f;
                foreach (var f in w.Fleets.Where(f => Economy.InOrbitAt(f, b) && f.OrbitHeight == shells[i].Item1).OrderBy(f => f.Side).ThenBy(f => f.Id))
                {
                    var label = $"{f.Name} ({f.ShipIds.Count})";
                    DrawString(font, new Vector2(x, y + bandH - 8), label, HorizontalAlignment.Left, -1, 11, Ui.SideColor(f.Side));
                    x += Mathf.Min(150, 7 * label.Length + 12);
                }
            }
            // The station sits in low orbit.
            if (Starbases.At(w, b.Id) is Starbase st)
            {
                var y = size.Y - 30 - bandH;
                var c = Ui.SideColor(st.Side);
                DrawRect(new Rect2(size.X - 70, y + 4, 60, bandH - 10), c * new Color(1, 1, 1, 0.5f));
                DrawString(font, new Vector2(size.X - 66, y + bandH / 2 + 4), Starbases.ClassOf(st).Short, HorizontalAlignment.Left, -1, 12, Ui.Text);
            }
        }
    }
}
