using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Ships and fittings for one fleet: each ship's cutaway drawing, its slots with click-to-swap modules,
/// the power and heat budgets, and its working numbers. Swaps are refits, done at your shipyard and
/// paid in Industry. "Save as design" makes new ships of the class come out the same way.
/// </summary>
public partial class FittingPanel : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _box = null!;
    private string _shape = "";

    public FittingPanel() : this(null!) { }
    public FittingPanel(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        Ui.StylePanel(this, 16);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(760, 560) };
        _box = new VBoxContainer { CustomMinimumSize = new Vector2(740, 0) };
        _box.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_box);
        AddChild(scroll);
    }

    public void Refresh(Simulation sim, Fleet f)
    {
        var w = sim.World;
        var ships = w.ShipsOf(f).ToList();
        var shape = string.Join(";", ships.Select(s => $"{s.Id}:{string.Join(",", Fitting.FitOf(s))}:{s.Disabled}"))
                    + "|" + Economy.Of(w, Side.Player).Researched.Count + "|" + Fitting.CanRefit(w, Side.Player, ships.FirstOrDefault() ?? new Ship())
                    + "|" + (int)(Economy.Of(w, Side.Player).Industry / 10);
        if (shape == _shape) return;
        _shape = shape;
        foreach (var c in _box.GetChildren()) c.QueueFree();

        var head = new HBoxContainer();
        head.AddChild(Ui.Label($"{f.Name}: ships and fittings", 18, Ui.Player));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.SmallButton("Close (Esc)", () => _screen.FittingFleetId = null, "Back to the map"));
        _box.AddChild(head);
        var refit = ships.Count > 0 ? Fitting.CanRefit(w, Side.Player, ships[0]) : "No ships";
        _box.AddChild(Ui.Label(refit == null
            ? $"At the shipyard: click a slot to swap its module (paid in Industry, you have {Economy.Of(w, Side.Player).Industry:0})."
            : $"Look only: {refit}. You can still save a design for new ships.", 13, refit == null ? Ui.Text : Ui.Dim));

        foreach (var s in ships) _box.AddChild(ShipCard(w, s, refit == null));
    }

    private Control ShipCard(World w, Ship s, bool canRefit)
    {
        var cls = GameData.ShipClass(s.ClassId);
        var fit = Fitting.FitOf(s).ToList();
        var spec = Fitting.Spec(s);
        var card = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color("0c111a"), BorderColor = new Color("1a2230") };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(10);
        card.AddThemeStyleboxOverride("panel", style);
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 6);
        card.AddChild(v);

        var head = new HBoxContainer();
        head.AddChild(Ui.Label($"{s.Name} · {cls.Name} · {cls.LengthM:0} m", 15, new Color("f2f5fa")));
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        head.AddChild(Ui.Label($"power {Fitting.PowerUsed(cls, fit):0}/{cls.Power:0} · heat {Fitting.HeatUsed(cls, fit):0}/{cls.HeatBudget:0}", 13, Ui.Dim));
        v.AddChild(head);

        if (Art.Texture("Ships", ShipArt.For(cls) + "-cutaway") is Texture2D tex)
        {
            var pic = new TextureRect
            {
                Texture = tex, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(700, 130),
            };
            if (s.Disabled != null) pic.Modulate = new Color(1, 0.85f, 0.85f);
            v.AddChild(pic);
        }

        var slots = new GridContainer { Columns = 3 };
        slots.AddThemeConstantOverride("h_separation", 8);
        slots.AddThemeConstantOverride("v_separation", 4);
        for (var i = 0; i < cls.SlotList.Count; i++)
        {
            var slot = cls.SlotList[i];
            var index = i;
            var label = Ui.Label($"{Fitting.SlotName(slot.Type)}{(slot.Pair ? " pair" : "")}", 12, Ui.Dim);
            label.CustomMinimumSize = new Vector2(110, 0);
            slots.AddChild(label);
            var current = GameData.Module(fit[i]);
            var pick = new MenuButton { Text = (current?.Name ?? "empty") + " ▾", Flat = false, FocusMode = FocusModeEnum.None, Disabled = !canRefit };
            pick.CustomMinimumSize = new Vector2(220, 28);
            var popup = pick.GetPopup();
            var choices = new List<string> { "" };
            choices.AddRange(GameData.Modules.Values.Where(m => m.Slot == slot.Type).Select(m => m.Id));
            for (var c = 0; c < choices.Count; c++)
            {
                var m = GameData.Module(choices[c]);
                var trial = fit.ToList();
                trial[i] = choices[c];
                var problem = Fitting.CanFit(w, Side.Player, cls, trial)
                              ?? ((m?.Cost ?? 0) > Economy.Of(w, Side.Player).Industry ? $"needs {m!.Cost:0} Industry" : null);
                popup.AddItem((m == null ? "Empty" : $"{m.Name} · {m.Cost:0} Industry · power {m.Power * slot.Mounts:0}, heat {m.Heat * slot.Mounts:0}")
                              + (problem != null ? $"  ({problem})" : ""), c);
                popup.SetItemDisabled(c, problem != null || choices[c] == fit[i]);
            }
            popup.IdPressed += id => _screen.IssueNow(new SetFitCommand(Side.Player, s.Id, index, choices[(int)id]), "refitting a ship");
            slots.AddChild(pick);
            slots.AddChild(Ui.Label(current == null ? "" : ModuleLine(current, slot.Mounts), 12, Ui.Dim));
        }
        v.AddChild(slots);

        var numbers = $"railguns {spec.Railguns} · point defence {spec.PointDefence} · missiles {spec.Missiles} · shield {Heat.MaxShield(w, s):0} · armour {Tech.MaxArmour(w, s):0}"
                      + $" · supplies {Fitting.MaxSupplies(w, s):0} · thrust {spec.AccelMmPerHour2:0.#} · burn heat x{Fitting.BurnHeatFactor(s):0.##}";
        var line = Ui.Label(numbers, 12, Ui.Text);
        line.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        v.AddChild(line);
        var save = Ui.SmallButton($"Save as the {cls.Name} design", () => _screen.IssueNow(new SetDesignCommand(Side.Player, cls.Id, fit), "saving a design"),
            $"New {cls.Name}s come out with this fit (engines are always the best you have researched)");
        save.CustomMinimumSize = new Vector2(240, 28);
        v.AddChild(save);
        return card;
    }

    private static string ModuleLine(ModuleData m, int n)
    {
        var bits = new List<string>();
        if (m.Railguns > 0) bits.Add($"{m.Railguns * n} railgun{(m.Railguns * n > 1 ? "s" : "")}");
        if (m.Missiles > 0) bits.Add($"{m.Missiles * n} missiles");
        if (m.PointDefence > 0) bits.Add($"{m.PointDefence * n} point defence");
        if (m.Shield > 0) bits.Add($"+{m.Shield * n:0} shield");
        if (m.Armour > 0) bits.Add($"+{m.Armour * n:0} armour");
        if (m.SensorMm > 0) bits.Add($"+{m.SensorMm * n * 1000:#,0} km sensors");
        if (m.Supplies > 0) bits.Add($"+{m.Supplies * n:0} supplies");
        if (m.HeatSinks > 0) bits.Add($"+{m.HeatSinks * n} heat sink");
        if (Math.Abs(m.AccelFactor - 1) > 1e-9) bits.Add($"thrust x{m.AccelFactor:0.##}");
        if (Math.Abs(m.BurnHeatFactor - 1) > 1e-9) bits.Add($"burn heat x{m.BurnHeatFactor:0.##}");
        return string.Join(", ", bits);
    }
}

/// <summary>Which approved drawing a class uses (Art/Ships).</summary>
public static class ShipArt
{
    public static string For(ShipClass cls) => cls.Icon switch
    {
        "escort" => "escort",
        "destroyer" => "destroyer",
        "cruiser" => "cruiser",
        "tanker" => "tanker",
        "freighter" => "freighter",
        _ => "frigate",
    };
}
