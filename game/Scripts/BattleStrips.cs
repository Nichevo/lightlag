using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Battle view (U): the screen cleared to one strip per ship in the fight, yours on the left and the enemy
/// ships you can see on the right. Each strip shows hull, weapons, engines, defence and crew as bars, heat
/// and shield, missiles left, and heat sinks left with an Eject button for your own ships.
/// </summary>
public partial class BattleStrips : Control
{
    private readonly GameScreen _screen;
    private VBoxContainer _ours = null!, _theirs = null!;
    private PanelContainer _oursPanel = null!, _theirsPanel = null!;
    private string _key = "";
    private readonly List<Action<World>> _live = new();

    public BattleStrips() : this(null!) { }
    public BattleStrips(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        (_oursPanel, _ours) = Column("Your ships");
        (_theirsPanel, _theirs) = Column("Enemy ships you can see");
        BuildInfo();
    }

    // The right-hand panel under the enemy ships: missiles in flight, quick orders for one of your fleets, and the score.
    private PanelContainer _infoPanel = null!;
    private Label _flying = null!, _score = null!, _ordersTitle = null!;
    private Button _holdOn = null!, _pullBack = null!, _dodgeOff = null!, _sidestep = null!, _hardBurn = null!, _focused = null!, _spread = null!;
    private int? _ordersFleet;

    private void BuildInfo()
    {
        var v = new VBoxContainer { CustomMinimumSize = new Vector2(330, 0) };
        v.AddThemeConstantOverride("separation", 4);
        v.AddChild(Ui.Label("MISSILES IN FLIGHT", 12, Ui.Dim));
        _flying = Ui.Label("", 12, Ui.Text);
        _flying.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_flying);
        _ordersTitle = Ui.Label("", 12, Ui.Dim);
        v.AddChild(_ordersTitle);
        Button B(string text, string tip, Func<Fleet, GameCommand> make) => Ui.SmallButton(text, () =>
        {
            if (_ordersFleet is int id && Main.Instance.Sim?.World.FindFleet(id) is Fleet f) _screen.IssueNow(make(f), "battle order");
        }, tip);
        _holdOn = B("Hold on", "Keep fighting past this fleet's pull-back rule (press again to follow the rule)", f => new HoldOnCommand(f.Id, !f.HoldOn));
        _pullBack = B("Pull back now", "Break off now and head for safety by its pull-back rule", f => new PullBackNowCommand(f.Id));
        _pullBack.AddThemeColorOverride("font_color", Ui.Danger);
        _dodgeOff = B("No dodge", "Fly straight: no fuel spent on dodging", f => new SetDodgeCommand(f.Id, DodgeMode.Off));
        _sidestep = B("Sidestep", "Jink as slugs come in", f => new SetDodgeCommand(f.Id, DodgeMode.Sidestep));
        _hardBurn = B("Hard burn", "Burn hard out of the line of fire: more fuel", f => new SetDodgeCommand(f.Id, DodgeMode.HardBurn));
        _focused = B("Focused", "Aimed slugs", f => new SetFireModeCommand(f.Id, FireMode.Focused));
        _spread = B("Spread", "A fan of slugs: harder to dodge", f => new SetFireModeCommand(f.Id, FireMode.Spread));
        foreach (var row in new[] { new[] { _holdOn, _pullBack }, new[] { _dodgeOff, _sidestep, _hardBurn }, new[] { _focused, _spread } })
        {
            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 4);
            foreach (var b in row) h.AddChild(b);
            v.AddChild(h);
        }
        v.AddChild(Ui.Label("SCORE SO FAR", 12, Ui.Dim));
        _score = Ui.Label("", 12, Ui.Text);
        _score.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(_score);
        _infoPanel = Ui.Panel(v, 10);
        AddChild(_infoPanel);
    }

    /// <summary>Missiles in flight, grouped: yours by the fleet they go for, theirs by the ship they go for, soonest first.</summary>
    private string Flying(World w)
    {
        var a = GameData.Combat.Missile.AccelMmPerHour2;
        var groups = new List<(double T, string Text, bool Ours)>();
        foreach (var g in w.Missiles.GroupBy(m => (m.Side, m.TargetShipId)))
        {
            if (w.FindShip(g.Key.TargetShipId) is not Ship target || w.FleetOf(target) is not Fleet tf) continue;
            var ours = g.Key.Side == Side.Player;
            if (ours && !_screen.ShownFleet(tf)) continue;
            var t = g.Min(m =>
            {
                var gap = target.Position - m.Position;
                var d = gap.Length;
                var v = d > 1e-9 ? -Vec2.Dot(gap, tf.Velocity - m.Velocity) / d : 0;
                return (-v + Math.Sqrt(Math.Max(0, v * v + 2 * a * d))) / a;
            });
            groups.Add((t, ours ? $"{g.Count()} yours at {(tf.Side == Side.Player ? target.Name : tf.Name)}" : $"{g.Count()} at {target.Name} ({tf.Name})", ours));
        }
        if (groups.Count == 0) return "None";
        return string.Join("\n", groups.OrderBy(x => x.T).Take(6).Select(x => $"{(x.Ours ? "●" : "▲")} {x.Text}, land in {Format.Hours(Math.Max(0, x.T))}"));
    }

    private void RefreshInfo(World w, List<Fleet> fleets)
    {
        _flying.Text = Flying(w);
        var mine = fleets.Where(f => f.Side == Side.Player).ToList();
        var pick = _screen.SelectedFleet is Fleet sel && mine.Contains(sel) ? sel : mine.FirstOrDefault();
        _ordersFleet = pick?.Id;
        _ordersTitle.Text = pick == null ? "ORDERS: select one of your fleets" : $"ORDERS FOR {pick.Name.ToUpperInvariant()}";
        foreach (var b in new[] { _holdOn, _pullBack, _dodgeOff, _sidestep, _hardBurn, _focused, _spread }) b.Disabled = pick == null;
        if (pick != null)
        {
            static Color On(bool on) => on ? Ui.Player : new Color(1, 1, 1, 0.7f);
            _holdOn.Modulate = On(pick.HoldOn);
            _dodgeOff.Modulate = On(pick.Dodge == DodgeMode.Off);
            _sidestep.Modulate = On(pick.Dodge == DodgeMode.Sidestep);
            _hardBurn.Modulate = On(pick.Dodge == DodgeMode.HardBurn);
            _focused.Modulate = On(pick.Fire == FireMode.Focused);
            _spread.Modulate = On(pick.Fire == FireMode.Spread);
        }
        if (w.ActiveBattle is Battle battle)
        {
            string Side_(BattleSide s) => $"{s.ShipsLost.Count} lost, {s.Crippled.Count} crippled · dealt {s.DamageDealt:0} damage, {s.RailHits + s.MissileHits} hits";
            _score.Text = $"You: {Side_(battle.For(Side.Player))}\nThem: {Side_(battle.For(Side.Rival))}";
        }
        else _score.Text = "No battle under way";
    }

    private (PanelContainer, VBoxContainer) Column(string title)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(330, 0) };
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);
        v.AddChild(Ui.Label(title.ToUpperInvariant(), 12, Ui.Dim));
        v.AddChild(scroll);
        var panel = Ui.Panel(v, 10);
        AddChild(panel);
        list.SetMeta("scroll", scroll);
        return (panel, list);
    }

    public void Refresh(Simulation sim)
    {
        var w = sim.World;
        var fleets = _screen.BattleFleets();
        // Out of battle: the selected fleet, so the view still works for a look at its state.
        if (!fleets.Any(f => f.Side == Side.Player) && _screen.SelectedFleet is Fleet sel && sel.Side == Side.Player) fleets = fleets.Append(sel).ToList();
        var ours = fleets.Where(f => f.Side == Side.Player).SelectMany(f => w.ShipsOf(f)).OrderBy(s => s.FleetId).ThenBy(s => s.Id).ToList();
        var theirs = fleets.Where(f => f.Side != Side.Player && _screen.ShownFleet(f)).SelectMany(f => w.ShipsOf(f)).OrderBy(s => s.FleetId).ThenBy(s => s.Id).ToList();
        var key = string.Join(",", ours.Select(s => s.Id)) + "|" + string.Join(",", theirs.Select(s => s.Id)) + $"|{GetViewportRect().Size}";
        if (key != _key && !Ui.MouseHeld)
        {
            _key = key;
            _live.Clear();
            Fill(_ours, w, ours, mine: true);
            Fill(_theirs, w, theirs, mine: false);
            Layout();
        }
        foreach (var update in _live) update(w);
        RefreshInfo(w, fleets);
        PlaceInfo();
    }

    private void PlaceInfo()
    {
        var view = GetViewportRect().Size;
        _infoPanel.ResetSize();
        _infoPanel.Position = new Vector2(view.X - _infoPanel.Size.X - 16, view.Y - _infoPanel.Size.Y - 110);
    }

    private void Layout()
    {
        var view = GetViewportRect().Size;
        var height = Mathf.Max(200, view.Y - 110 - 40);
        foreach (var (panel, list) in new[] { (_oursPanel, _ours), (_theirsPanel, _theirs) })
        {
            var scroll = (ScrollContainer)list.GetMeta("scroll");
            // The enemy column stops above the missiles, orders and score panel.
            var room = list == _theirs ? height - 40 - _infoPanel.GetCombinedMinimumSize().Y - 16 : height - 40;
            scroll.CustomMinimumSize = new Vector2(330, Mathf.Max(60, Mathf.Min(room, list.GetCombinedMinimumSize().Y + 4)));
            panel.ResetSize();
        }
        _oursPanel.Position = new Vector2(16, 60);
        _theirsPanel.Position = new Vector2(view.X - _theirsPanel.GetCombinedMinimumSize().X - 16, 60);
        _theirsPanel.Visible = _theirs.GetChildCount() > 0;
    }

    private void Fill(VBoxContainer list, World w, List<Ship> ships, bool mine)
    {
        foreach (var c in list.GetChildren()) c.QueueFree();
        if (ships.Count == 0)
        {
            list.AddChild(Ui.Label(mine ? "No ships of yours in a fight (select a fleet to see its ships)" : "None in sight", 12, Ui.Dim));
            return;
        }
        int? fleetId = null;
        foreach (var s in ships)
        {
            // Your ships come grouped under each fleet, with a line for the whole fleet.
            if (mine && s.FleetId != fleetId && w.FleetOf(s) is Fleet f)
            {
                fleetId = s.FleetId;
                var head = Ui.Label("", 12, Ui.Player);
                var id = f.Id;
                _live.Add(world =>
                {
                    if (world.FindFleet(id) is not Fleet g) { head.Text = ""; return; }
                    var all = world.ShipsOf(g).ToList();
                    var hull = all.Count == 0 ? 0 : all.Average(x => x.Armour / Math.Max(1, Tech.MaxArmour(world, x)));
                    var missiles = all.Sum(x => x.Missiles);
                    var hurt = all.Count(x => x.Crippled);
                    head.Text = $"{g.Name} · {all.Count} ship{(all.Count == 1 ? "" : "s")} · hull {hull:P0}" + (missiles > 0 ? $" · {missiles} missiles" : "")
                                + (hurt > 0 ? $" · {hurt} crippled" : "") + (g.HoldOn ? " · holding on" : "");
                });
                list.AddChild(head);
            }
            list.AddChild(Strip(w, s, mine));
        }
    }

    private Control Strip(World w, Ship s, bool mine)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 1);
        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        var name = Ui.Label($"{s.Name} ({s.Code})", 13, mine ? Ui.Player : Ui.Rival);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.ClipText = true;
        name.MouseFilter = MouseFilterEnum.Pass;
        name.TooltipText = "Click to open the ship card";
        var id = s.Id;
        name.GuiInput += e => { if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }) _screen.ShipCardId = id; };
        head.AddChild(name);
        var state = Ui.Label("", 11, Ui.Dim);
        head.AddChild(state);
        v.AddChild(head);

        var bars = new GridContainer { Columns = 5 };
        bars.AddThemeConstantOverride("h_separation", 4);
        var hull = new StatBar("Hull");
        var weapons = new StatBar("Wpn");
        var engines = new StatBar("Eng");
        var defence = new StatBar("Def");
        var crew = new StatBar("Crew");
        foreach (var b in new[] { hull, weapons, engines, defence, crew }) bars.AddChild(b);
        v.AddChild(bars);

        var foot = new HBoxContainer();
        foot.AddThemeConstantOverride("separation", 6);
        var heat = new StatBar("Heat") { CustomMinimumSize = new Vector2(120, 14) };
        foot.AddChild(heat);
        var extra = Ui.Label("", 11, Ui.Text);
        extra.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foot.AddChild(extra);
        Button? eject = null;
        if (mine)
        {
            eject = Ui.SmallButton("Eject sink", () =>
            {
                var ship = Main.Instance.Sim!.World.FindShip(id);
                if (ship != null) _screen.IssueNow(new EjectShipSinkCommand(ship.FleetId, id), "eject heat sink");
            }, "Dump a heat sink from this ship now: drops heat at once (the fleet only does it by itself when a ship overheats)");
            eject.CustomMinimumSize = new Vector2(0, 22);
            eject.AddThemeFontSizeOverride("font_size", 11);
            foot.AddChild(eject);
        }
        v.AddChild(foot);

        _live.Add(world =>
        {
            if (world.FindShip(id) is not Ship ship) { v.Modulate = new Color(1, 1, 1, 0.35f); state.Text = "lost"; return; }
            var cls = Fitting.Spec(ship);
            hull.Set(ship.Armour / Math.Max(1, Tech.MaxArmour(world, ship)), $"Hull {ship.Armour:0} of {Tech.MaxArmour(world, ship):0}");
            var armed = new List<string>();
            if (cls.Railguns > 0) armed.Add(ship.Out(Module.Railguns) ? "railguns OUT" : "railguns");
            if (cls.Lances > 0) armed.Add(ship.Out(Module.Railguns) ? "lances OUT" : "lances");
            if (cls.Missiles > 0) armed.Add(ship.Out(Module.Missiles) ? "missiles OUT" : "missiles");
            weapons.Set(ship.Weapons, $"Weapons {ship.Weapons:P0}: {(armed.Count > 0 ? string.Join(", ", armed) : "unarmed")}",
                ship.Out(Module.Railguns) || ship.Out(Module.Missiles));
            engines.Set(ship.Engines, $"Engines {ship.Engines:P0}", ship.Out(Module.Engines));
            defence.Set(ship.Defence, $"Defence (point defence, shields) {ship.Defence:P0}" + (cls.PointDefence > 0 && ship.Out(Module.PointDefence) ? ": point defence OUT" : ""),
                ship.Out(Module.PointDefence));
            crew.Set(ship.Crew, $"Crew {ship.Crew:P0}");
            var cap = Math.Max(1e-6, Heat.Capacity(ship));
            heat.Set(ship.Heat / cap, ship.Overheated ? "OVERHEATED: weapons hold fire until it cools; eject a sink" : $"Heat {ship.Heat / cap:P0} of capacity", ship.Overheated, hot: true);
            var bits = new List<string>();
            if (cls.Missiles > 0) bits.Add($"msl {ship.Missiles}");
            if (Heat.MaxShield(world, ship) > 0) bits.Add($"shield {ship.Shield / Math.Max(1e-6, Heat.MaxShield(world, ship)):P0}");
            if (mine) bits.Add($"sinks {ship.HeatSinks}");
            extra.Text = string.Join(" · ", bits);
            state.Text = ship.Crippled ? "CRIPPLED" : ship.Overheated ? "OVERHEATED" : ship.Disabled is Module m ? $"{Combat.ModuleName(m)} out" : "";
            state.AddThemeColorOverride("font_color", ship.Crippled || ship.Overheated ? Ui.Danger : Ui.Dim);
            if (eject != null)
            {
                eject.Disabled = ship.HeatSinks <= 0 || ship.Heat <= 0;
                eject.Text = $"Eject sink ({ship.HeatSinks})";
                eject.Modulate = ship.Overheated && ship.HeatSinks > 0 ? Ui.Danger : Colors.White;
            }
        });
        return Ui.Panel(v, 6);
    }
}

/// <summary>A thin labelled bar: green when whole, amber, then red; or for heat, red as it fills.</summary>
public partial class StatBar : Control
{
    private readonly string _label;
    private double _value = 1;
    private bool _alarm;
    private bool _hot;

    public StatBar() : this("") { }
    public StatBar(string label)
    {
        _label = label;
        CustomMinimumSize = new Vector2(58, 14);
        MouseFilter = MouseFilterEnum.Pass;
    }

    public void Set(double value, string tip, bool alarm = false, bool hot = false)
    {
        value = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);
        if (Math.Abs(value - _value) < 1e-3 && alarm == _alarm && TooltipText == tip) return;
        _value = value;
        _alarm = alarm;
        _hot = hot;
        TooltipText = tip;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.1f, 0.13f, 0.18f));
        var good = _hot ? 1 - _value : _value;
        var color = _alarm ? Ui.Danger : good > 0.66 ? new Color("5fd6b0") : good > 0.33 ? Ui.Rival : Ui.Danger;
        DrawRect(new Rect2(Vector2.Zero, new Vector2((float)(size.X * _value), size.Y)), new Color(color, 0.75f));
        DrawString(ThemeDB.FallbackFont, new Vector2(3, size.Y - 3), _label, HorizontalAlignment.Left, size.X - 4, 10, Colors.White);
    }
}
