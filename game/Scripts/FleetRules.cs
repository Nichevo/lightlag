using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The fleet panel's battle-rules, refit and zone rows: the pull-back rule (Auto or a preset), how close
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

        var refit = Row();
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
        refit.AddChild(_zone);
        _refitLine = Line();
        _zonePanel = new ZonePanel(_screen);
        AddChild(_zonePanel);
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

    private static string PresetTip(PullPreset p) => p switch
    {
        PullPreset.Cautious => "Pull back after losing a quarter of its strength, a ship, or a hit on the flagship; ships under half hull pull out",
        PullPreset.Standard => "Pull back after losing a third of its strength or two ships, or when ammunition runs low",
        PullPreset.FightOn => "Pull back only after losing half its strength; ships pull out at a fifth of their hull",
        PullPreset.ToTheLast => "Never pull back",
        _ => "",
    };

    // ---- Zones ----------------------------------------------------------------------------------

    private const int Leave = 1, DrawHere = 2, ToggleHold = 3, ZoneBase = 1000, LaneBase = 100000;

    private void FillZoneMenu()
    {
        var menu = _zone.GetPopup();
        menu.Clear();
        if (_fleet == null) return;
        var w = Main.Instance.Sim!.World;
        if (_fleet.ZoneId != null)
        {
            menu.AddItem("Leave the zone", Leave);
            menu.AddItem("Switch drift / keep station", ToggleHold);
            menu.AddSeparator();
        }
        menu.AddItem("Draw a raiding zone here", DrawHere);
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
        else if (id == ToggleHold && w.Zones.Find(z => z.Id == f.ZoneId) is Zone zone)
            _screen.IssueNow(new EditZoneCommand(Side.Player, zone.Id, zone.Center, zone.RadiusMm, zone.FromBodyId, zone.ToBodyId, zone.T,
                zone.Hold == ZoneHold.Drift ? ZoneHold.StationKeep : ZoneHold.Drift, zone.Prey), "zone hold");
        else if (id >= LaneBase && w.Lanes.Find(l => l.Id == id - LaneBase) is Lane lane)
            _screen.IssueNow(new HuntCommand(f.Id, lane.FromBodyId, lane.ToBodyId), "hunt");
        else if (id >= ZoneBase) _screen.IssueNow(new SetZoneFleetCommand(f.Id, id - ZoneBase), "hold zone");
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private void Do(Func<Fleet, GameCommand> make, string what)
    {
        if (_fleet != null) _screen.IssueNow(make(_fleet), what);
    }

    private HBoxContainer Row()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        AddChild(row);
        return row;
    }

    private Label Line()
    {
        var l = Ui.Label("", 12, Ui.Dim);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.CustomMinimumSize = new Vector2(330, 0);
        AddChild(l);
        return l;
    }

    private static Label Caption(string text)
    {
        var l = Ui.Label(text, 13, Ui.Dim);
        l.CustomMinimumSize = new Vector2(70, 0);
        return l;
    }
}
