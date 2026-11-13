using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The fleet panel's battle-rules, refit and zone rows (the refit and zone rows live in RefitBox and
/// ZoneBox, which the fleet panel puts in their own sections): the pull-back rule (Auto or a preset), how close
/// the fleet is to it, Hold on and Pull back now; Send for refit with its estimate; and holding a zone,
/// drawing a raiding zone where the fleet is, or hunting a supply lane you have spotted.
/// </summary>
public partial class FleetRules : VBoxContainer
{
    private readonly GameScreen _screen;
    private Fleet? _fleet;
    private Label _ruleLine = null!;
    private OptionButton _preset = null!;
    private Button _holdOn = null!;
    private Button _pullNow = null!;
    private Label _refitLine = null!;
    private Button _refit = null!;
    private Button _cancelRefit = null!;
    private MenuButton _zone = null!;
    private ZonePanel _zonePanel = null!;
    private OptionButton _formation = null!;
    private Label _formationLine = null!;
    private OptionButton _shown = null!;
    private MenuButton _when = null!;
    private Label _shownLine = null!;

    private static readonly WeaponsShown[] Modes = { WeaponsShown.HoldBack, WeaponsShown.ShowAll, WeaponsShown.NeverShow };

    private static readonly Formation[] Shapes = { Formation.Auto, Formation.Staggered, Formation.Wall, Formation.Line, Formation.Column };

    /// <summary>The refit and zone rows, for the fleet panel's Refit and Zone sections.</summary>
    public VBoxContainer RefitBox { get; } = new();
    public VBoxContainer ZoneBox { get; } = new();

    private static readonly PullPreset[] Presets = { PullPreset.Cautious, PullPreset.Standard, PullPreset.FightOn, PullPreset.ToTheLast };

    public FleetRules() : this(null!) { }
    public FleetRules(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 4);

        var rules = Row();
        rules.AddChild(Caption("Pull back"));
        _preset = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "When the fleet pulls back from a fight. Auto: the default for its situation (Empire, Automation)" };
        _preset.AddThemeFontSizeOverride("font_size", 13);
        _preset.AddItem("Auto", 0);
        for (var i = 0; i < Presets.Length; i++)
        {
            _preset.AddItem(EmpireTabs.PresetName(Presets[i]), i + 1);
            _preset.SetItemTooltip(i + 1, PresetTip(Presets[i]));
        }
        _preset.ItemSelected += i => Do(f => new SetPullRuleCommand(f.Id, i == 0 ? null : Presets[i - 1]), "pull-back rule");
        rules.AddChild(_preset);
        _holdOn = Ui.SmallButton("Hold on", () => Do(f => new HoldOnCommand(f.Id, !f.HoldOn), "hold on"),
            "Ignore the pull-back rule in this fight (it comes back on when the fight ends)");
        _holdOn.ToggleMode = true;
        _pullNow = Ui.SmallButton("Pull back now", () => Do(f => new PullBackNowCommand(f.Id), "pull back now"), "Break off at once and head for safety");
        foreach (var b in new[] { _holdOn, _pullNow })
        {
            b.CustomMinimumSize = new Vector2(0, 26);
            b.AddThemeFontSizeOverride("font_size", 12);
            rules.AddChild(b);
        }
        _ruleLine = Line();

        var shape = Row();
        shape.AddChild(Caption("Formation"));
        _formation = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "The shape the fleet keeps. Railguns need a clear line; missiles launch over friendly ships; point defence and shields cover ships close by" };
        _formation.AddThemeFontSizeOverride("font_size", 13);
        for (var i = 0; i < Shapes.Length; i++)
        {
            _formation.AddItem(Formations.Name(Shapes[i]), i);
            _formation.SetItemTooltip(i, ShapeTip(Shapes[i]));
        }
        _formation.ItemSelected += i => Do(f => new SetFormationCommand(f.Id, Shapes[i]), "formation");
        shape.AddChild(_formation);
        _formationLine = Line();

        var secret = Row();
        secret.AddChild(Caption("Weapons shown"));
        _shown = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "Keep your newest weapons secret: hold them back until needed, always fire your best, or never show them (what the rival never sees, it can't learn)" };
        _shown.AddThemeFontSizeOverride("font_size", 13);
        for (var i = 0; i < Modes.Length; i++) _shown.AddItem(Secrecy.ModeName(Modes[i]), i);
        _shown.SetItemTooltip(0, "Fight with common weapons and open fire short of true reach; bring the advanced ones out when the rules below say so");
        _shown.SetItemTooltip(1, "Always fire everything at full reach");
        _shown.SetItemTooltip(2, "Never use the advanced weapons, even when losing");
        _shown.ItemSelected += i => Do(f => new SetSecrecyCommand(f.Id, Modes[i], f.Secrecy), "weapons shown");
        secret.AddChild(_shown);
        _when = new MenuButton { Text = "When ▾", Flat = false, FocusMode = FocusModeEnum.None, TooltipText = "When a fleet holding back brings its advanced weapons out, and whether it hides its range" };
        _when.AddThemeFontSizeOverride("font_size", 12);
        _when.AboutToPopup += FillWhenMenu;
        _when.GetPopup().IdPressed += id => Main.Instance.RunGuarded("weapons shown", () => PickWhen((int)id));
        secret.AddChild(_when);
        _shownLine = Line();

        RefitBox.AddThemeConstantOverride("separation", 4);
        ZoneBox.AddThemeConstantOverride("separation", 4);
        var refit = Row(RefitBox);
        _refit = Ui.SmallButton("Send for refit", () => Do(f => new SendForRefitCommand(Side.Player, f.Id), "send for refit"), "");
        _cancelRefit = Ui.SmallButton("Cancel refit", () => Do(f => new CancelRefitCommand(Side.Player, f.Id), "cancel refit"), "Stop the refit; ships already in the bays finish");
        foreach (var b in new[] { _refit, _cancelRefit })
        {
            b.CustomMinimumSize = new Vector2(0, 26);
            b.AddThemeFontSizeOverride("font_size", 12);
            refit.AddChild(b);
        }
        _zone = new MenuButton { Text = "Zone ▾", Flat = false, FocusMode = FocusModeEnum.None,
            TooltipText = "Hold a zone (drift or keep station there, and go after enemy cargo that comes by), draw one here, or hunt a supply lane you have spotted" };
        _zone.AddThemeFontSizeOverride("font_size", 12);
        _zone.AboutToPopup += FillZoneMenu;
        _zone.GetPopup().IdPressed += id => Main.Instance.RunGuarded("zone order", () => PickZone((int)id));
        _refitLine = Line(RefitBox);
        Row(ZoneBox).AddChild(_zone);
        _zonePanel = new ZonePanel(_screen);
        ZoneBox.AddChild(_zonePanel);
    }

    public void Refresh(World w, Fleet f)
    {
        _fleet = f;
        var index = f.RuleByHand ? Array.IndexOf(Presets, f.Rule.Preset) + 1 : 0;
        if (index >= 0 && _preset.Selected != index) _preset.Select(index);
        _holdOn.SetPressedNoSignal(f.HoldOn);
        _holdOn.Modulate = f.HoldOn ? Ui.Rival : Colors.White;
        _pullNow.Disabled = f.Retreating;
        _ruleLine.Text = RuleText(w, f);
        _ruleLine.AddThemeColorOverride("font_color", f.Retreating ? Ui.Rival : Ui.Dim);

        var shapeIndex = Array.IndexOf(Shapes, f.Formation);
        if (shapeIndex >= 0 && _formation.Selected != shapeIndex) _formation.Select(shapeIndex);
        _formationLine.Text = FormationText(w, f);
        _formationLine.AddThemeColorOverride("font_color", Formations.BlockedShips(w, f) > 0 ? Ui.Rival : Ui.Dim);

        var mode = Array.IndexOf(Modes, f.Shown);
        if (mode >= 0 && _shown.Selected != mode) _shown.Select(mode);
        _when.Disabled = f.Shown != WeaponsShown.HoldBack;
        _shownLine.Text = ShownText(w, f);

        var why = Refits.CanSend(w, f);
        _refit.Visible = f.Refit == null;
        _refit.Disabled = why != null;
        _refit.TooltipText = why ?? (Refits.Estimate(w, f) is { } e
            ? $"Fly to the nearest yard and refit to the current designs: about {e.Wait / 24:0.#} days to get there and a bay, {e.InBay / 24:0.#} in the bays, back in {e.Back / 24:0.#}"
            : "Refit to the current designs at the nearest yard");
        _cancelRefit.Visible = f.Refit != null;
        _refitLine.Text = Refits.Status(w, f) ?? "";
        _refitLine.Visible = _refitLine.Text.Length > 0;

        var zone = f.ZoneId is int z ? w.Zones.Find(x => x.Id == z) : null;
        _zone.Text = zone != null ? $"{(f.Mission == Mission.Hunt ? "Hunting" : "Holding")} {zone.Name} ▾" : "Zone ▾";
        _zonePanel.Refresh(w, f, zone);
    }

    private static string RuleText(World w, Fleet f)
    {
        if (f.Retreating) return $"Pulling back{(f.PullReason is { Length: > 0 } r ? $": {r}" : "")}";
        var preset = f.Rule.Preset;
        var source = f.RuleByHand ? "set by hand" : $"Auto for {BattleRules.SituationName(BattleRules.Situation(w, f))}";
        var text = $"{EmpireTabs.PresetName(preset)} ({source})";
        if (f.FightPower > 0 && f.Rule.StrengthLost is double line and < 1)
        {
            var lost = 1 - (Stats.Power(w, f) + f.FightPulledPower) / f.FightPower;
            text += $" · in this fight lost {Math.Max(0, lost):P0} of strength, pulls back at {line:P0}";
        }
        if (f.HoldOn) text += " · HOLDING ON: the rule is off for this fight";
        return text;
    }

    private static string FormationText(World w, Fleet f)
    {
        var shape = Formations.Shape(w, f);
        var text = f.Formation == Formation.Auto ? $"{Formations.Name(shape)} (Auto, from its weapons and the threat)" : $"{Formations.Name(shape)} (set by hand)";
        var blocked = Formations.BlockedShips(w, f);
        if (blocked > 0) text += $" · {blocked} of {f.ShipIds.Count} ships blocked by their own fleet";
        return text;
    }

    private static string ShownText(World w, Fleet f)
    {
        var adv = Secrecy.Advanced(w, f.Side);
        if (adv.Count == 0) return "No advanced weapons yet: nothing to hold back";
        var names = string.Join(", ", adv.Select(id => GameData.Research(id).Name));
        if (f.Shown == WeaponsShown.ShowAll) return $"Advanced ({names}) always used";
        if (f.AdvancedOut) return $"Advanced weapons OUT in this fight: {names}";
        return $"Holding back {names}{(f.Secrecy.HideRange ? $" · opens fire at {GameData.Weapons.K.HideRangeShare:P0} of reach" : "")}";
    }

    private const int WhenHurt = 1, WhenJoined = 2, WhenLosing = 3, HideRange = 4;

    private void FillWhenMenu()
    {
        var menu = _when.GetPopup();
        menu.Clear();
        if (_fleet == null) return;
        var r = _fleet.Secrecy;
        menu.AddCheckItem($"Bring out when a ship falls below {GameData.Weapons.K.BelowHull:P0} hull", WhenHurt);
        menu.SetItemChecked(menu.GetItemIndex(WhenHurt), r.WhenHurt);
        menu.AddCheckItem("Bring out when more enemy fleets join", WhenJoined);
        menu.SetItemChecked(menu.GetItemIndex(WhenJoined), r.WhenJoined);
        menu.AddCheckItem("Bring out when the odds turn against us", WhenLosing);
        menu.SetItemChecked(menu.GetItemIndex(WhenLosing), r.WhenLosing);
        menu.AddSeparator();
        menu.AddCheckItem("Hide range (open fire short of true reach)", HideRange);
        menu.SetItemChecked(menu.GetItemIndex(HideRange), r.HideRange);
    }

    private void PickWhen(int id)
    {
        if (_fleet == null) return;
        var r = _fleet.Secrecy.Copy();
        switch (id)
        {
            case WhenHurt: r.WhenHurt = !r.WhenHurt; break;
            case WhenJoined: r.WhenJoined = !r.WhenJoined; break;
            case WhenLosing: r.WhenLosing = !r.WhenLosing; break;
            case HideRange: r.HideRange = !r.HideRange; break;
            default: return;
        }
        Do(f => new SetSecrecyCommand(f.Id, f.Shown, r), "weapons shown");
    }

    private static string ShapeTip(Formation f) => f switch
    {
        Formation.Auto => "Picks for you: Wall for missile boats behind heavy ships, Line for all-gun fleets against no missiles, Column when flying, else Staggered",
        Formation.Staggered => "Big ships apart in front, small ones in the gaps: nearly every gun clear, most ships near cover",
        Formation.Wall => "Heavy ships in front, the rest tight behind: strong cover, but guns behind are blocked (missiles still launch)",
        Formation.Line => "One row abreast: nothing blocked, little cover against missiles",
        Formation.Column => "One behind the other: quick through lanes, but almost every gun is blocked",
        _ => "",
    };

    private static string PresetTip(PullPreset p) => p switch
    {
        PullPreset.Cautious => "Pull back after losing a quarter of its strength, a ship, or a hit on the flagship; ships under half hull pull out",
        PullPreset.Standard => "Pull back after losing a third of its strength or two ships, or when ammunition runs low",
        PullPreset.FightOn => "Pull back only after losing half its strength; ships pull out at a fifth of their hull",
        PullPreset.ToTheLast => "Never pull back",
        _ => "",
    };

    // ---- Zones ----------------------------------------------------------------------------------

    private const int Leave = 1, DrawHere = 2, ToggleHold = 3, DrawPinned = 4, ZoneBase = 1000, LaneBase = 100000;

    private void FillZoneMenu()
    {
        var menu = _zone.GetPopup();
        menu.Clear();
        if (_fleet == null) return;
        var w = Main.Instance.Sim!.World;
        if (_fleet.ZoneId != null)
        {
            menu.AddItem("Leave the zone", Leave);
            menu.AddItem("Switch drift / keep station / roam", ToggleHold);
            menu.AddSeparator();
        }
        menu.AddItem("Draw a raiding zone here (fixed in space)", DrawHere);
        if (Lanes.AnchorFor(w, _fleet.Position) is Body pin)
        {
            menu.AddItem($"Draw a raiding zone here, moving with {pin.Name}", DrawPinned);
            menu.SetItemTooltip(menu.GetItemIndex(DrawPinned), $"The zone keeps its place beside {pin.Name} as it orbits, so it stays between {pin.Name} and the same area");
        }
        foreach (var z in w.Zones.Where(z => z.Side == Side.Player && z.Id != _fleet.ZoneId).OrderBy(z => z.Id))
            menu.AddItem($"Hold {z.Name}", ZoneBase + z.Id);
        var lanes = w.Lanes.Where(l => l.SeenBy == Side.Player).OrderByDescending(l => Lanes.Confidence(w, l)).ThenBy(l => l.Id).ToList();
        if (lanes.Count > 0) menu.AddSeparator("Supply lanes spotted");
        foreach (var l in lanes)
        {
            var a = w.FindBody(l.FromBodyId)?.Name ?? "?";
            var b = w.FindBody(l.ToBodyId)?.Name ?? "?";
            var every = Lanes.EveryDays(l) is double d ? $", a ship every {d:0.#} d" : "";
            menu.AddItem($"Hunt the {a}–{b} run ({Lanes.Confidence(w, l):P0} sure{every})", LaneBase + l.Id);
        }
    }

    private void PickZone(int id)
    {
        if (_fleet == null) return;
        var w = Main.Instance.Sim!.World;
        var f = _fleet;
        if (id == Leave) _screen.IssueNow(new SetZoneFleetCommand(f.Id, null), "leave zone");
        else if (id == DrawHere) _screen.IssueNow(new CreateZoneCommand(Side.Player, ZoneKind.Raid, f.Position, 300, FleetId: f.Id), "draw zone");
        else if (id == DrawPinned && Lanes.AnchorFor(w, f.Position) is Body pin)
            _screen.IssueNow(new CreateZoneCommand(Side.Player, ZoneKind.Raid, f.Position, 300, FleetId: f.Id, AnchorBodyId: pin.Id), "draw pinned zone");
        else if (id == ToggleHold && w.Zones.Find(z => z.Id == f.ZoneId) is Zone zone)
            _screen.IssueNow(new EditZoneCommand(Side.Player, zone.Id, zone.Center, zone.RadiusMm, zone.FromBodyId, zone.ToBodyId, zone.T,
                zone.Hold switch { ZoneHold.Drift => ZoneHold.StationKeep, ZoneHold.StationKeep => ZoneHold.Roam, _ => ZoneHold.Drift }, zone.Prey), "zone hold");
        else if (id >= LaneBase && w.Lanes.Find(l => l.Id == id - LaneBase) is Lane lane)
            _screen.IssueNow(new HuntCommand(f.Id, lane.FromBodyId, lane.ToBodyId), "hunt");
        else if (id >= ZoneBase) _screen.IssueNow(new SetZoneFleetCommand(f.Id, id - ZoneBase), "hold zone");
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private void Do(Func<Fleet, GameCommand> make, string what)
    {
        if (_fleet != null) _screen.IssueNow(make(_fleet), what);
    }

    private HBoxContainer Row(Container? parent = null)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        (parent ?? this).AddChild(row);
        return row;
    }

    private Label Line(Container? parent = null)
    {
        var l = Ui.Label("", 12, Ui.Dim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(330, 0);
        (parent ?? this).AddChild(l);
        return l;
    }

    // ---- One-line summaries for the fleet panel's section headers ----------------------------------

    public static string BattleSummary(World w, Fleet f)
    {
        if (f.Retreating) return "pulling back";
        var preset = EmpireTabs.PresetName(f.Rule.Preset);
        return (f.RuleByHand ? preset : $"Auto · {preset}") + $" · {Formations.Name(f.Formation)}" + (f.HoldOn ? " · holding on" : "");
    }

    public static string RefitSummary(World w, Fleet f)
    {
        if (Refits.Status(w, f) is string status) return status;
        var behind = w.ShipsOf(f).Count(s => Refits.Outdated(w, s));
        if (behind == 0) return "matches the designs";
        return Refits.Estimate(w, f) is { } e ? $"{behind} behind · {(e.Wait + e.InBay) / 24:0} d" : $"{behind} behind";
    }

    public static string ZoneSummary(World w, Fleet f) =>
        f.ZoneId is int z && w.Zones.Find(x => x.Id == z) is Zone zone ? $"{(f.Mission == Mission.Hunt ? "hunting" : "holding")} {zone.Name}" : "none";

    private static Label Caption(string text)
    {
        var l = Ui.Label(text, 13, Ui.Dim);
        l.CustomMinimumSize = new Vector2(70, 0);
        return l;
    }
}
