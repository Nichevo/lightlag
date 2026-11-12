using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The ships screen (J), board 19: Designs and Class tree. Designs lists every class with its variants
/// (Auto or yours); the picked variant's slots show the part picked against the best you could fit,
/// numbers with a gold mark for the best fit, what rival sensors see, and the build bill by material
/// (total, against the Standard, and what the shipyard holds). Below, every model of the class (active,
/// special, upgrade ready, old) and every ship in service. Edits are made to a working copy and saved as
/// a variant; nothing changes in the game until a button issues a command. The Class tree shows each hull
/// by size with what it needs, and crewed or AI-run per class.
/// </summary>
public partial class ShipsScreen : PanelContainer
{
    private readonly GameScreen _screen;
    private VBoxContainer _root = null!;
    private string _shape = "";
    private readonly List<Action<World>> _live = new();

    private string _tab = "designs";
    private string _classId = "freighter";
    private int _designId;
    private List<string>? _working;
    private string _newName = "";

    private static readonly Color Gold = new("f2c14e");
    private static readonly Color Bright = new("f2f5fa");

    public ShipsScreen() : this(null!) { }
    public ShipsScreen(GameScreen screen) => _screen = screen;

    /// <summary>Open on a class (from a fleet or ship card).</summary>
    public void Show(string classId) { _classId = classId; _designId = Designer.StandardId; _working = null; _tab = "designs"; _shape = ""; }

    public override void _Ready()
    {
        var box = new StyleBoxFlat { BgColor = new Color(0.02f, 0.03f, 0.045f, 0.97f) };
        box.SetContentMarginAll(20);
        AddThemeStyleboxOverride("panel", box);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _root = new VBoxContainer();
        _root.AddThemeConstantOverride("separation", 10);
        AddChild(_root);
    }

    private static Side Me => Side.Player;

    public void Refresh(Simulation sim)
    {
        var w = sim.World;
        var view = GetViewportRect().Size;
        if (Position != Vector2.Zero || Size != view) { Position = Vector2.Zero; Size = view; }
        if (!GameData.ShipClasses.ContainsKey(_classId)) _classId = GameData.ShipClasses.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        if (_designId != Designer.StandardId && Designer.Find(w, Me, _designId)?.ClassId != _classId) { _designId = Designer.StandardId; _working = null; }
        var me = Economy.Of(w, Me);
        var shape = $"{_tab}|{view}|{_classId}|{_designId}|{(_working == null ? "" : string.Join(",", _working))}|{me.Researched.Count}"
                    + "|" + string.Join(";", me.Designs.Select(d => $"{d.Id}:{d.Name}:{string.Join(",", d.Fit)}"))
                    + "|" + string.Join(",", me.DefaultDesign.Select(kv => $"{kv.Key}={kv.Value}")) + "|" + string.Join(",", me.AiRun)
                    + "|" + string.Join(",", w.Ships.Where(s => s.Side == Me && s.ClassId == _classId).Select(s => $"{s.Id}:{s.DesignId}:{s.Code}:{s.FleetId}"))
                    + "|" + w.Orders.Count(o => o.Side == Me && o.Kind == OrderKind.Ship) + "|" + Stewards.LevelFor(w, Me, AutoArea.Designs);
        if (shape != _shape && !Ui.MouseHeld)
        {
            _shape = shape;
            foreach (var c in _root.GetChildren()) c.QueueFree();
            _live.Clear();
            Build(w);
        }
        foreach (var update in _live) update(w);
    }

    // ---- Shared bits ---------------------------------------------------------------------------

    private static PanelContainer Box(Control content, int padding = 12, Color? border = null)
    {
        var p = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.07f, 0.11f, 0.95f), BorderColor = border ?? new Color(Ui.Player, 0.25f) };
        style.SetBorderWidthAll(border == null ? 1 : 2);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(padding);
        p.AddThemeStyleboxOverride("panel", style);
        p.AddChild(content);
        return p;
    }

    private static VBoxContainer Column(int gap = 6)
    {
        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", gap);
        return v;
    }

    private static HBoxContainer Row(int gap = 6)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", gap);
        return h;
    }

    private static Label Wrap(string text, int size = 13, Color? color = null)
    {
        var l = Ui.Label(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        l.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        l.CustomMinimumSize = new Vector2(200, 0);
        return l;
    }

    private static Label Heading(string text, string? note = null)
    {
        var l = Ui.Label(text, 17, Bright);
        if (note != null) l.Text += "   ";
        return l;
    }

    private static Control HeadingRow(string text, string note)
    {
        var row = Row(10);
        row.AddChild(Ui.Label(text, 17, Bright));
        var n = Ui.Label(note, 12, Ui.Dim);
        n.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(n);
        return row;
    }

    private void Click(Control c, string tip, Action act)
    {
        c.MouseFilter = MouseFilterEnum.Stop;
        c.TooltipText = tip;
        c.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
                Main.Instance.RunGuarded("ships screen", act);
        };
    }

    private static string ModName(string id) => GameData.Module(id)?.Name ?? "Empty";

    private static string SlotLetterName(SlotData slot) => slot.Type switch
    {
        "W" => slot.Pair ? "Weapon pair" : "Weapon",
        "D" => slot.Pair ? "Defence pair" : "Defence",
        "U" => "Internal",
        "E" => "Drive",
        _ => slot.Type,
    };

    private static string SizeOf(ShipClass c) => c.LengthM < 100 ? "S" : c.LengthM < 200 ? "M" : c.LengthM < 400 ? "L" : "H";

    private static bool Unlocked(World w, ShipClass c) => c.Needs.Length == 0 || Tech.Has(w, Me, c.Needs);

    // ---- Layout --------------------------------------------------------------------------------

    private void Build(World w)
    {
        var top = Row(16);
        top.AddChild(Ui.Label("Ships", 26, Bright));
        foreach (var (key, label, tip) in new[] { ("designs", "Designs", "Classes, variants, models and ships in service"), ("tree", "Class tree", "Every hull by size, what it needs, and crewed or AI-run") })
        {
            var k = key;
            var b = Ui.SmallButton(label, () => { _tab = k; }, tip);
            b.Modulate = _tab == key ? Ui.Player : new Color(1, 1, 1, 0.7f);
            top.AddChild(b);
        }
        var note = Ui.Label("A class is a hull; a variant is a saved fit with a job. Auto keeps the Standard current; your own variants are pinned", 13, Ui.Dim);
        note.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        top.AddChild(note);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var level = Stewards.LevelFor(w, Me, AutoArea.Designs);
        top.AddChild(Ui.Label("Design manager", 13, Ui.Dim));
        foreach (var l in Enum.GetValues<AutoLevel>())
        {
            var lv = l;
            var b = Ui.SmallButton(l.ToString(), () => _screen.IssueNow(new SetAutoLevelCommand(Me, AutoArea.Designs, lv), "design manager"),
                l switch
                {
                    AutoLevel.Auto => "Auto keeps each class's Standard current as research lands and adds variants when a role makes sense (Q-ship, long range)",
                    AutoLevel.Advise => "The same, but each change waits in Needs you for your yes",
                    _ => "Standards stay as they are until you update them; no new variants",
                });
            b.Modulate = level == l ? Ui.Player : new Color(1, 1, 1, 0.6f);
            top.AddChild(b);
        }
        top.AddChild(Ui.SmallButton("Back to map", () => _screen.ShowShips = false, "Close the ships screen (J or Esc)"));
        _root.AddChild(top);
        if (_tab == "tree") { BuildTree(w); return; }

        var body = Row(14);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        _root.AddChild(body);
        body.AddChild(ClassList(w));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var right = Column(12);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(right);
        body.AddChild(scroll);

        var cls = GameData.ShipClass(_classId);
        var fit = _working ?? Designer.FitFor(w, Me, cls.Id, _designId);
        var cols = Row(12);
        var a = Column(12);
        a.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        a.SizeFlagsStretchRatio = 1.25f;
        var b2 = Column(12);
        b2.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        a.AddChild(Editor(w, cls, fit));
        b2.AddChild(Numbers(w, cls, fit));
        b2.AddChild(CostBox(w, cls, fit));
        cols.AddChild(a);
        cols.AddChild(b2);
        right.AddChild(cols);
        right.AddChild(ModelsBox(w, cls));
    }

    // ---- Classes and variants (left) -------------------------------------------------------------

    private Control ClassList(World w)
    {
        var col = Column(4);
        col.CustomMinimumSize = new Vector2(250, 0);
        col.AddChild(HeadingRow("Classes", "hull = class; fits = variants"));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var list = Column(2);
        list.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (var cls in GameData.ShipClasses.Values.OrderBy(c => Unlocked(w, c) ? 0 : 1).ThenBy(c => c.LengthM).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            var open = Unlocked(w, cls);
            var head = Ui.Label($"{cls.Name}  {SizeOf(cls)}", 15, cls.Id == _classId ? Ui.Player : open ? Ui.Text : Ui.Dim);
            var id = cls.Id;
            Click(head, open ? "Show this class" : $"Needs {GameData.Research(cls.Needs).Name}: you can plan variants now", () => { _classId = id; _designId = Designer.StandardId; _working = null; });
            list.AddChild(head);
            var def = Designer.DefaultFor(w, Me, cls.Id);
            foreach (var (did, name, tag) in new[] { (Designer.StandardId, "Standard", "Auto") }
                         .Concat(Designer.Of(w, Me, cls.Id).Select(d => (d.Id, d.Name, d.Auto ? "Auto" : "Yours"))))
            {
                var picked = cls.Id == _classId && did == _designId;
                var row = Row(6);
                var name2 = Ui.Label($"    {name}{(did == def ? " ●" : "")}", 14, picked ? Ui.Player : Ui.Text);
                name2.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(name2);
                row.AddChild(Ui.Label(tag, 12, tag == "Yours" ? Gold : Ui.Dim));
                var d2 = did;
                Click(row, did == def ? "Yards build this variant (●)" : "Show this variant", () => { _classId = id; _designId = d2; _working = null; });
                list.AddChild(row);
            }
        }
        scroll.AddChild(list);
        col.AddChild(scroll);
        var buttons = Row(6);
        buttons.AddChild(Ui.SmallButton("New variant", () =>
        {
            var cls = GameData.ShipClass(_classId);
            _working = (_working ?? Designer.FitFor(_screen.World, Me, _classId, _designId)).ToList();
            _newName = NextName(_screen.World, cls);
            _designId = -1;
        }, "Start a variant of your own from the fit shown"));
        buttons.AddChild(Ui.SmallButton("Hand all to Auto", () => _screen.IssueNow(new HandToAutoCommand(Me, _classId), "hand to Auto"),
            "Delete your own variants of this class: its ships follow the Standard again"));
        col.AddChild(buttons);
        col.AddChild(Ui.Label("● the variant yards build", 12, Ui.Dim));
        return col;
    }

    private static string NextName(World w, ShipClass cls)
    {
        for (var i = 1; ; i++)
        {
            var n = $"Variant {i}";
            if (!Designer.Of(w, Me, cls.Id).Any(d => d.Name == n)) return n;
        }
    }

    // ---- The variant: slots, picked against best ---------------------------------------------------

    private Control Editor(World w, ShipClass cls, List<string> fit)
    {
        var col = Column(8);
        var design = Designer.Find(w, Me, _designId);
        var isNew = _designId < 0;
        var title = isNew ? _newName : design?.Name ?? "Standard";
        var head = Row(10);
        head.AddChild(Ui.Label($"{cls.Name} · {title}", 20, Ui.Player));
        head.AddChild(Ui.Label(isNew ? "New variant of yours (not saved yet)"
            : design == null ? $"Auto's Standard{(Designer.OnAuto(w, Me) ? ", kept current" : ", kept as it is until you update it")}"
            : design.Auto ? $"Auto variant: {Designer.RoleName(design.Role)}" : "Your variant: pinned", 13, Ui.Dim));
        col.AddChild(head);
        if (!Unlocked(w, cls)) col.AddChild(Ui.Label($"Needs {GameData.Research(cls.Needs).Name} before yards can build it", 13, Ui.Rival));

        var picks = Designer.PickedVsBest(w, Me, cls, fit);
        var behind = picks.Count(p => p.Picked != p.Best);
        var best = picks.Select(p => p.Best).ToList();
        var now = Fitting.Compute(cls, fit);
        var top = Fitting.Compute(cls, best);
        var gains = new List<string>();
        void Gain(string what, double a, double b) { if (a > 1e-9 && b > a * 1.01) gains.Add($"+{(b / a - 1) * 100:0} % {what}"); else if (a <= 1e-9 && b > 1e-9) gains.Add($"+{b:0.#} {what}"); }
        Gain("railguns", now.Railguns, top.Railguns);
        Gain("missiles", now.Missiles, top.Missiles);
        Gain("thrust", now.AccelMmPerHour2, top.AccelMmPerHour2);
        Gain("point defence", now.PointDefence, top.PointDefence);
        Gain("shield", now.Shield, top.Shield);
        Gain("armour", now.Armour, top.Armour);
        var extra = Designer.Industry(cls, best, false) - Designer.Industry(cls, fit, false);
        var line = behind == 0 ? "Picked vs best fit: every slot has the best part you can fit."
            : $"Picked vs best fit: {behind} of {picks.Count} slots could be better. Best fit gives {(gains.Count > 0 ? string.Join(", ", gains) : "newer parts")}"
              + (extra > 0.5 ? $", costs {extra:0} Industry more" : "") + (Fitting.HeatUsed(cls, best) > Fitting.HeatUsed(cls, fit) ? ", and runs hotter" : "") + ".";
        var bestRow = Row(8);
        bestRow.AddChild(Wrap(line, 13, behind == 0 ? Ui.Dim : Gold));
        if (behind > 0) bestRow.AddChild(Ui.SmallButton("Show best fit", () => { if (_designId == Designer.StandardId || Designer.Find(_screen.World, Me, _designId)?.Auto == true) StartOwn(cls); _working = best.ToList(); }, "Put the best part you have in every slot (save it to keep it)"));
        col.AddChild(Box(bestRow, 8));

        for (var i = 0; i < cls.SlotList.Count; i++) col.AddChild(SlotRow(w, cls, fit, i, picks[i]));

        var hidden = fit.Any(m => GameData.Module(m)?.Hidden == true);
        col.AddChild(Wrap(hidden
            ? $"Rival sensors see: {cls.Name}. Until it fires: after that, raiders near there know a Q-ship is about for {Designer.WaryDays:0} days. Better sensors or a close look are the counters."
            : $"Rival sensors see: {cls.Name}{(Planner.WarshipClass(cls.Id) ? " (armed)" : "")}", 13, hidden ? Gold : Ui.Dim));

        // Buttons.
        var buttons = Row(8);
        var name = new LineEdit { Text = isNew ? _newName : title, CustomMinimumSize = new Vector2(150, 0), MaxLength = 24, TooltipText = "The variant's name" };
        name.TextChanged += t => _newName = t;
        var mine = design is { Auto: false };
        if (isNew || mine)
        {
            buttons.AddChild(name);
            buttons.AddChild(Ui.SmallButton(isNew ? "Save variant" : "Save changes", () =>
            {
                var n = (isNew ? _newName : name.Text).Trim();
                _screen.IssueNow(new SaveVariantCommand(Me, cls.Id, isNew ? null : _designId, n, (_working ?? fit).ToList()), "saving a variant");
                if (isNew) { _working = null; _designId = Designer.Of(_screen.World, Me, cls.Id).LastOrDefault()?.Id ?? 0; }
                else _working = null;
            }, "Keep this fit as a variant of yours. Auto never changes your variants, it only flags old parts"));
        }
        else buttons.AddChild(Ui.SmallButton("Make it my own", () => StartOwn(cls), "Start a variant of yours from this fit"));
        if (_working != null && !isNew) buttons.AddChild(Ui.SmallButton("Undo changes", () => _working = null, "Back to the saved fit"));
        if (mine || design is { Auto: true }) buttons.AddChild(Ui.SmallButton(mine ? "Reset to Auto" : "Delete", () => { _screen.IssueNow(new DeleteVariantCommand(Me, _designId), "deleting a variant"); _designId = 0; _working = null; },
            "Delete this variant: its ships follow the Standard from now on"));
        if (!isNew)
        {
            var def = Designer.DefaultFor(w, Me, cls.Id) == _designId;
            var b = Ui.SmallButton(def ? "Yards build this ●" : "Yards build this", () => _screen.IssueNow(new SetDefaultVariantCommand(Me, cls.Id, _designId), "default variant"),
                "New ships of this class are built to this variant unless you ask for another");
            b.Disabled = def;
            buttons.AddChild(b);
            var build = Ui.SmallButton("Build one", () => _screen.IssueNow(new QueueOrderCommand(Me, OrderKind.Ship, cls.Id, DesignId: _designId), "building a ship"),
                Economy.CanQueue(w, Me, OrderKind.Ship, cls.Id) ?? "Add one to the shipyard queue, built to this variant");
            build.Disabled = Economy.CanQueue(w, Me, OrderKind.Ship, cls.Id) != null || _working != null;
            buttons.AddChild(build);
            if (!Designer.OnAuto(w, Me) && design == null)
                buttons.AddChild(Ui.SmallButton("Update the Standard", () => _screen.IssueNow(new UpdateStandardCommand(Me, cls.Id), "update standard"),
                    "Take the best parts you have into the Standard now (the design manager is not on Auto)"));
        }
        col.AddChild(buttons);
        if (!isNew)
        {
            var ships = w.Ships.Where(s => s.Side == Me && s.ClassId == cls.Id && s.DesignId == Math.Max(0, _designId) && Refits.Outdated(w, s)).ToList();
            if (ships.Count > 0)
                col.AddChild(Ui.SmallButton($"Refit fleet ships ({ships.Count} behind)", () => RefitAll(ships.Select(s => s.Id).ToList()),
                    "Send each fleet with ships behind this variant to the nearest yard to refit"));
        }
        col.AddChild(CrewingRow(w, cls));
        return Box(col);
    }

    private void StartOwn(ShipClass cls)
    {
        _working = (_working ?? Designer.FitFor(_screen.World, Me, cls.Id, Math.Max(0, _designId))).ToList();
        _newName = NextName(_screen.World, cls);
        _designId = -1;
    }

    private Control SlotRow(World w, ShipClass cls, List<string> fit, int i, SlotPick pick)
    {
        var slot = cls.SlotList[i];
        var row = Row(10);
        var letter = Ui.Label(slot.Type, 18, Bright);
        letter.CustomMinimumSize = new Vector2(22, 0);
        row.AddChild(letter);
        var names = Column(0);
        names.CustomMinimumSize = new Vector2(120, 0);
        names.AddChild(Ui.Label(SlotLetterName(slot), 14, Ui.Text));
        names.AddChild(Ui.Label(slot.Pair ? "mirrored pair" : "single", 11, Ui.Dim));
        row.AddChild(names);
        var current = GameData.Module(fit[i]);
        var menu = new MenuButton { Text = (current?.Name ?? "Empty") + (slot.Pair ? " pair" : "") + " ▾", Flat = false, FocusMode = FocusModeEnum.None };
        menu.CustomMinimumSize = new Vector2(230, 30);
        var popup = menu.GetPopup();
        var choices = new List<string> { "" };
        choices.AddRange(GameData.Modules.Values.Where(m => m.Slot == slot.Type).OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Id));
        for (var c = 0; c < choices.Count; c++)
        {
            var trial = fit.ToList();
            trial[i] = choices[c];
            var problem = Fitting.CanFit(w, Me, cls, trial);
            var m = GameData.Module(choices[c]);
            popup.AddItem((m == null ? "Empty (cheaper)" : $"{m.Name} · power {m.Power * slot.Mounts:0}, heat {m.Heat * slot.Mounts:0}{(m.Hidden ? " · hidden" : "")}")
                          + (problem != null ? $"  ({problem})" : ""), c);
            popup.SetItemDisabled(c, problem != null || choices[c] == fit[i]);
        }
        var idx = i;
        popup.IdPressed += id => Main.Instance.RunGuarded("pick part", () =>
        {
            if (_designId == Designer.StandardId || Designer.Find(_screen.World, Me, _designId)?.Auto == true) StartOwn(cls);
            var next = (_working ?? fit).ToList();
            next[idx] = choices[(int)id];
            _working = next;
        });
        row.AddChild(menu);
        var verdict = Column(0);
        verdict.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (pick.Picked == pick.Best)
        {
            verdict.AddChild(Ui.Label("✓ Best you can pick", 13, new Color("8fe0a0")));
            verdict.AddChild(Ui.Label(current == null ? "nothing better to put here yet" : "already the best of its kind you have", 11, Ui.Dim));
        }
        else
        {
            var b = GameData.Module(pick.Best);
            verdict.AddChild(Ui.Label($"Best: {b?.Name ?? "Empty"}", 13, Gold));
            var bits = new List<string>();
            if (current != null && b != null)
            {
                if (b.AccelFactor > current.AccelFactor + 1e-9) bits.Add($"+{(b.AccelFactor / current.AccelFactor - 1) * 100:0} % thrust");
                if (b.Railguns > current.Railguns) bits.Add($"+{(b.Railguns - current.Railguns) * slot.Mounts} railguns");
                if (b.Missiles > current.Missiles) bits.Add($"+{(b.Missiles - current.Missiles) * slot.Mounts} missiles");
                if (b.Power != current.Power) bits.Add($"{(b.Power - current.Power) * slot.Mounts:+0.#;-0.#} MW");
                if (b.Cost > current.Cost) bits.Add($"+{(b.Cost - current.Cost) * Designer.BuildPartShare:0} Industry");
                if (b.Needs.Length > 0 && b.Fuels is { Count: > 0 }) bits.Add($"burns {string.Join(", ", b.Fuels)}");
            }
            verdict.AddChild(Ui.Label(string.Join(" · ", bits), 11, Ui.Dim));
        }
        row.AddChild(verdict);
        var panel = Box(row, 8);
        return panel;
    }

    private Control CrewingRow(World w, ShipClass cls)
    {
        var row = Row(8);
        row.AddChild(Ui.Label("Crew", 14, Ui.Dim));
        var ai = Designer.AiRun(w, Me, cls.Id);
        var can = Designer.CanRunOnAi(w, Me, cls.Id);
        var crewed = Ui.SmallButton("Crewed", () => _screen.IssueNow(new SetCrewingCommand(Me, cls.Id, false), "crewing"),
            "A crew from the yard planet: ranks from Green to Elite, boarding parties, better damage control as they rise; eats food and feels strain from hard dodges");
        crewed.Modulate = ai ? new Color(1, 1, 1, 0.6f) : Ui.Player;
        row.AddChild(crewed);
        var aiButton = Ui.SmallButton("AI-run", () => _screen.IssueNow(new SetCrewingCommand(Me, cls.Id, true), "crewing"),
            can ?? $"An AI core ({Designer.CoreElectronics(cls):0} electronics a ship): no people, no food, never strained, never gets better; no boarding parties and no one to rescue");
        aiButton.Disabled = can != null;
        aiButton.Modulate = ai ? Ui.Player : new Color(1, 1, 1, 0.6f);
        row.AddChild(aiButton);
        row.AddChild(Wrap(can ?? (ai ? "New ships come out AI-run; ships flying change over at their next refit" : "New ships come out crewed"), 12, Ui.Dim));
        return row;
    }

    // ---- Numbers with the best-fit mark ------------------------------------------------------------

    private Control Numbers(World w, ShipClass cls, List<string> fit)
    {
        var col = Column(6);
        col.AddChild(HeadingRow("Numbers", "bar = picked · gold mark = best fit"));
        var best = Designer.Best(w, Me, cls, fit);
        var a = Fitting.Compute(cls, fit);
        var b = Fitting.Compute(cls, best);
        void Bar(string name, double now, double top, double max, string text)
        {
            if (now <= 1e-9 && top <= 1e-9) return;
            var row = Row(8);
            var l = Ui.Label(name, 13, Ui.Dim);
            l.CustomMinimumSize = new Vector2(110, 0);
            row.AddChild(l);
            row.AddChild(new MarkBar(now / Math.Max(1e-9, max), top / Math.Max(1e-9, max)) { CustomMinimumSize = new Vector2(180, 14), SizeFlagsVertical = SizeFlags.ShrinkCenter });
            row.AddChild(Ui.Label(text, 13, Ui.Text));
            col.AddChild(row);
        }
        Bar("Power", Fitting.PowerUsed(cls, fit), Fitting.PowerUsed(cls, best), cls.Power, $"{Fitting.PowerUsed(cls, fit):0.#} of {cls.Power:0.#} MW");
        Bar("Heat", Fitting.HeatUsed(cls, fit), Fitting.HeatUsed(cls, best), cls.HeatBudget, $"{Fitting.HeatUsed(cls, fit):0.#} of {cls.HeatBudget:0.#}");
        var accelMax = Math.Max(a.AccelMmPerHour2, b.AccelMmPerHour2) * 1.15;
        Bar("Thrust", a.AccelMmPerHour2, b.AccelMmPerHour2, accelMax, $"{a.AccelMmPerHour2 / cls.AccelMmPerHour2:0.00}× (best {b.AccelMmPerHour2 / cls.AccelMmPerHour2:0.00}×)");
        if (cls.Cargo > 0) Bar("Cargo", a.Cargo, b.Cargo, cls.Cargo, $"{a.Cargo:0} of {cls.Cargo:0}{(a.Cargo < cls.Cargo ? $" (−{(1 - a.Cargo / cls.Cargo) * 100:0} %)" : "")}");
        double Max(double x, double y) => Math.Max(1, Math.Max(x, y) * 1.2);
        Bar("Railguns", a.Railguns, b.Railguns, Max(a.Railguns, b.Railguns), $"{a.Railguns}");
        Bar("Missiles", a.Missiles, b.Missiles, Max(a.Missiles, b.Missiles), $"{a.Missiles} in the racks");
        Bar("Point defence", a.PointDefence, b.PointDefence, Max(a.PointDefence, b.PointDefence), $"{a.PointDefence}");
        Bar("Shield", a.Shield, b.Shield, Max(a.Shield, b.Shield), $"{a.Shield:0}");
        Bar("Armour", a.Armour, b.Armour, Max(a.Armour, b.Armour), $"{a.Armour:0}");
        Bar("Supplies", a.Supplies, b.Supplies, Max(a.Supplies, b.Supplies), $"{a.Supplies:0}");
        Bar("Sensors", a.SensorMm, b.SensorMm, Max(a.SensorMm, b.SensorMm), $"{a.SensorMm * 1000:#,0} km");
        if (Fitting.CanFit(w, Me, cls, fit) is string problem) col.AddChild(Ui.Label($"Can't be built: {problem}", 13, Ui.Danger));
        return Box(col);
    }

    // ---- Build cost by material --------------------------------------------------------------------

    private Control CostBox(World w, ShipClass cls, List<string> fit)
    {
        var col = Column(6);
        col.AddChild(HeadingRow("Build cost", "per part, from its bill of materials"));
        var ai = Designer.AiRun(w, Me, cls.Id);
        var bill = Designer.Bill(cls, fit, ai);
        var std = Designer.Materials(cls, Designer.StandardFit(w, Me, cls), ai);
        var total = Designer.Materials(cls, fit, ai);
        var yard = Economy.ShipyardOf(w, Me);
        var goods = bill.SelectMany(l => l.Materials.Keys).Concat(std.Keys).Distinct().OrderBy(g => Colonies.Goods.FindIndex(x => x.Id == g)).ToList();
        var grid = new GridContainer { Columns = goods.Count + 2 };
        grid.AddThemeConstantOverride("h_separation", 14);
        grid.AddThemeConstantOverride("v_separation", 3);
        void Cell(string text, Color? color = null, int size = 13) => grid.AddChild(Ui.Label(text, size, color ?? Ui.Text));
        Cell("", Ui.Dim);
        Cell("Industry", Ui.Dim, 12);
        foreach (var g in goods) Cell(Colonies.Goods.Find(x => x.Id == g)?.Short ?? g, Ui.Dim, 12);
        static string N(double v) => Math.Abs(v) < 1e-9 ? "·" : $"{v:0.#}";
        foreach (var l in bill)
        {
            Cell(l.Part);
            Cell(N(l.Industry));
            foreach (var g in goods) Cell(N(l.Materials.GetValueOrDefault(g)), l.Materials.GetValueOrDefault(g) < 0 ? new Color("8fe0a0") : null);
        }
        var industry = Designer.Industry(cls, fit, ai);
        var stdIndustry = Designer.Industry(cls, Designer.StandardFit(w, Me, cls), ai);
        Cell("Total", Bright);
        Cell($"{industry:0}", Bright);
        foreach (var g in goods) Cell(N(total.GetValueOrDefault(g)), Bright);
        Cell("vs Standard", Ui.Dim);
        Cell(Diff(industry - stdIndustry), Ui.Dim);
        foreach (var g in goods) Cell(Diff(total.GetValueOrDefault(g) - std.GetValueOrDefault(g)), Ui.Dim);
        if (yard != null)
        {
            Cell($"In stock at {yard.Name}", Ui.Dim);
            Cell($"{Economy.Of(w, Me).Industry:0}", Ui.Dim);
            foreach (var g in goods) Cell($"{Colonies.Held(yard, g):0}", Colonies.Held(yard, g) + 1e-9 < total.GetValueOrDefault(g) ? Ui.Danger : Ui.Dim);
        }
        col.AddChild(grid);
        var days = Economy.HoursFor(w, Me, OrderKind.Ship, cls.Id) / 24;
        var refitDays = Refits.HoursPerShip * Math.Clamp(cls.Cost / 100, 0.5, 3) / 24 / Math.Max(0.1, yard != null ? Colonies.ShipyardRate(w, yard) : 1);
        var crewK = cls.Cost * Planets.D.CrewKPerCost * Tech.Factor(w, Me, "ai-cores");
        var lines = new List<string>
        {
            $"Build time: {days:0} days in a bay. Refit to it: about {refitDays:0} days, a quarter of the hull's Industry and some materials.",
            ai ? "Crew: none (AI-run). No food, no strain, no ranks; no one to rescue if it is lost."
               : $"Crew: {crewK * 1000:0} people from the yard planet. Food for {cls.FoodDays:0} days; restocked at any planet of yours or by tankers.",
            cls.Railguns + Fitting.Compute(cls, fit).Railguns > 0 || Fitting.Compute(cls, fit).Missiles > 0
                ? $"Ammunition: railgun slugs and {Fitting.Compute(cls, fit).Missiles} missiles to fill, refilled by tankers and planets." : "Unarmed.",
        };
        if (cls.Lifeboats) lines.Add("Big hull: a lost ship's crew get off in a lifeboat that heads for home.");
        foreach (var l in lines) col.AddChild(Wrap(l, 12, Ui.Dim));
        if (yard != null && Economy.MaterialShort(yard, total) is string shortOf)
            col.AddChild(Wrap($"Short: {shortOf}. The shipyard waits for it (Transport brings materials in), or build the Standard now and refit later.", 12, Ui.Rival));
        return Box(col);
    }

    private static string Diff(double v) => Math.Abs(v) < 1e-9 ? "=" : v > 0 ? $"+{v:0.#}" : $"{v:0.#}";

    // ---- Models and ships in service ---------------------------------------------------------------

    private Control ModelsBox(World w, ShipClass cls)
    {
        var col = Column(8);
        col.AddChild(HeadingRow($"{cls.Name} models and ships in service", "every model: active, special, upgrade ready and old"));
        var models = Designer.Models(w, Me, cls.Id);
        var cards = new HFlowContainer();
        cards.AddThemeConstantOverride("h_separation", 10);
        cards.AddThemeConstantOverride("v_separation", 10);
        foreach (var m in models)
        {
            var c = Column(2);
            c.CustomMinimumSize = new Vector2(220, 0);
            var head = Row(8);
            head.AddChild(Ui.Label(m.DesignName, 15, Bright));
            head.AddChild(Ui.Label(m.Code, 13, Ui.Player));
            c.AddChild(head);
            var color = m.Status switch { "Active" => new Color("8fe0a0"), "Special" => Gold, "Upgrade ready" => Ui.Player, _ => Ui.Dim };
            c.AddChild(Ui.Label(m.Status, 13, color));
            c.AddChild(Ui.Label($"{m.InService} in service{(m.Building > 0 ? $" · building {m.Building}" : "")}", 12, Ui.Text));
            c.AddChild(Wrap(Designer.Changes(Fitting.StandardFit(cls), m.Fit) is var ch && ch != "no change" ? ch : "Standard parts", 11, Ui.Dim));
            c.AddChild(Wrap(m.Note, 11, Ui.Dim));
            cards.AddChild(Box(c, 8, m.Status == "Upgrade ready" ? Ui.Player : null));
        }
        col.AddChild(cards);

        var ships = w.Ships.Where(s => s.Side == Me && s.ClassId == cls.Id).OrderBy(s => s.Code, StringComparer.Ordinal).ToList();
        if (ships.Count == 0) { col.AddChild(Ui.Label($"No {cls.Name.ToLowerInvariant()}s in service", 13, Ui.Dim)); return Box(col); }
        var grid = new GridContainer { Columns = 8 };
        grid.AddThemeConstantOverride("h_separation", 16);
        grid.AddThemeConstantOverride("v_separation", 4);
        foreach (var h in new[] { "Code", "Name", "Built at", "Job", "Crew", "Condition", "Variant", "Model status" }) grid.AddChild(Ui.Label(h, 12, Ui.Dim));
        var variants = new List<(int, string)> { (Designer.StandardId, "Standard") };
        variants.AddRange(Designer.Of(w, Me, cls.Id).Select(d => (d.Id, d.Name)));
        foreach (var s in ships)
        {
            var id = s.Id;
            grid.AddChild(Ui.Label(s.Code, 13, Ui.Player));
            var name = Ui.Label(s.Name, 13, Ui.Text);
            Click(name, "Show this ship's card", () => _screen.ShipCardId = id);
            grid.AddChild(name);
            grid.AddChild(Ui.Label(s.BuiltAtBodyId is int b && w.FindBody(b) is Body at ? $"{at.Name} ({at.Type})" : "start", 13, Ui.Text));
            grid.AddChild(Ui.Label(w.FleetOf(s) is Fleet f ? $"{f.Name} · {Job(w, f)}" : "", 13, Ui.Text));
            grid.AddChild(Ui.Label(CrewText(s), 13, Crews.WornOut(s) ? Ui.Rival : Ui.Text));
            grid.AddChild(Ui.Label(s.Crippled ? "crippled" : Wounds.Worst(w, s) < 0.95 ? $"hull {s.Armour / Math.Max(1e-9, Tech.MaxArmour(w, s)):P0}" : "ok", 13, s.Crippled ? Ui.Danger : Ui.Text));
            var pick = new OptionButton { FocusMode = FocusModeEnum.None, TooltipText = "The variant this ship refits to at its next yard stop" };
            pick.AddThemeFontSizeOverride("font_size", 12);
            for (var i = 0; i < variants.Count; i++)
            {
                pick.AddItem(variants[i].Item2, i);
                if (variants[i].Item1 == s.DesignId) pick.Selected = i;
            }
            pick.ItemSelected += i => Main.Instance.RunGuarded("ship variant", () => _screen.IssueNow(new SetShipVariantCommand(Me, id, variants[(int)i].Item1), "ship variant"));
            grid.AddChild(pick);
            grid.AddChild(Ui.Label(s.RefitHoursLeft > 0 ? $"In refit, {s.RefitHoursLeft / 24:0.#} days" : Refits.Outdated(w, s) ? (Refits.CrewChange(w, s) ? "Crew change at next refit" : "Refit offered") : "Up to date",
                13, Refits.Outdated(w, s) ? Gold : new Color("8fe0a0")));
        }
        col.AddChild(grid);
        var behind = ships.Where(s => Refits.Outdated(w, s) && s.RefitHoursLeft <= 0).Select(s => s.Id).ToList();
        var row = Row(10);
        if (behind.Count > 0) row.AddChild(Ui.SmallButton($"Refit all {behind.Count} at the next yard stop", () => RefitAll(behind),
            "Send each fleet carrying these ships to the nearest yard to refit (fleets in battle wait)"));
        row.AddChild(Ui.Label("Code = type, hull mark, variant, number. Names follow the world that built them.", 12, Ui.Dim));
        col.AddChild(row);
        return Box(col);
    }

    private static string Job(World w, Fleet f)
    {
        if (f.Refit != null) return "refit";
        if (f.Rescue != null) return "rescue";
        if (f.Role != FleetRole.None) return HelpCalls.RoleName(f.Role).ToLowerInvariant();
        if (f.Mission != Mission.None) return f.Mission.ToString().ToLowerInvariant();
        return f.Flight != null ? "flying" : "holding";
    }

    public static string CrewText(Ship s)
    {
        if (!Crews.Crewed(s)) return "AI-run";
        var rank = Crews.RankOf(s);
        var stars = rank switch { Rank.Seasoned => " ★", Rank.Veteran => " ★★", Rank.Elite => " ★★★", _ => "" };
        return $"{Crews.Name(rank)}{stars}" + (s.Strain >= 0.05 ? $" · strain {s.Strain:P0}" : "");
    }

    private void RefitAll(List<int> shipIds)
    {
        var w = _screen.World;
        var sent = 0;
        foreach (var f in shipIds.Select(id => w.FindShip(id)).OfType<Ship>().Select(s => w.FleetOf(s)).OfType<Fleet>().DistinctBy(f => f.Id))
            if (Refits.CanSend(w, f) == null) { _screen.IssueNow(new SendForRefitCommand(Me, f.Id), "refit"); sent++; }
        Main.Instance.Toast(sent == 0 ? "No fleet can go for refit now (in battle, already going, or no yard)" : $"{sent} fleet{(sent == 1 ? "" : "s")} sent for refit");
    }

    // ---- Class tree ---------------------------------------------------------------------------------

    private void BuildTree(World w)
    {
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var col = Column(12);
        col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(col);
        _root.AddChild(scroll);
        col.AddChild(Wrap("Each hull by size, smallest first. A hull opens with its research; every hull takes any variant that fits its slots. "
                          + "Crewed or AI-run is chosen per class (AI cores): AI-run ships need no people or food and never feel strain, but never get better and have no one to save.", 13, Ui.Dim));
        var cols = Row(12);
        cols.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (var size in new[] { ("S", "Small (under 100 m)"), ("M", "Medium (100-200 m)"), ("L", "Large (200-400 m)"), ("H", "Huge (400 m and up)") })
        {
            var c = Column(10);
            c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            c.AddChild(Ui.Label(size.Item2, 16, Bright));
            foreach (var cls in GameData.ShipClasses.Values.Where(x => SizeOf(x) == size.Item1).OrderBy(x => x.Needs.Length == 0 ? 0 : GameData.Research(x.Needs).Tier).ThenBy(x => x.LengthM))
                c.AddChild(TreeCard(w, cls));
            cols.AddChild(c);
        }
        col.AddChild(cols);
    }

    private Control TreeCard(World w, ShipClass cls)
    {
        var open = Unlocked(w, cls);
        var c = Column(3);
        var head = Row(8);
        head.AddChild(Ui.Label(cls.Name, 15, open ? Ui.Player : Ui.Dim));
        head.AddChild(Ui.Label($"{cls.Short} · {cls.LengthM:0} m", 12, Ui.Dim));
        c.AddChild(head);
        var slots = string.Join(" ", cls.SlotList.GroupBy(s => s.Type).Select(g => $"{g.Count()}{g.Key}" + (g.Any(s => s.Pair) ? $"({g.Count(s => s.Pair)} pair)" : "")));
        c.AddChild(Ui.Label($"Slots {slots} · {cls.Power:0} MW · {cls.Cost:0} Industry", 12, Ui.Text));
        c.AddChild(Wrap(cls.Role, 11, Ui.Dim));
        if (!open)
        {
            var idea = GameData.Research(cls.Needs);
            var path = new List<string> { idea.Name };
            var cur = idea;
            for (var i = 0; i < 4 && cur.Needs is { Count: > 0 } n && GameData.ResearchById.TryGetValue(n[0], out var parent) && !Tech.Has(w, Me, parent.Id); i++) { path.Add(parent.Name); cur = parent; }
            var queued = w.Orders.Any(o => o.Side == Me && o.Kind == OrderKind.Research && o.Key == idea.Id);
            c.AddChild(Wrap($"Needs {string.Join(" ← ", path)} (tier {idea.Tier}){(queued ? " · in the research queue" : "")}", 12, Ui.Rival));
        }
        else
        {
            var flying = w.Ships.Count(s => s.Side == Me && s.ClassId == cls.Id);
            var variants = Designer.Of(w, Me, cls.Id).Count + 1;
            c.AddChild(Ui.Label($"{flying} in service · {variants} variant{(variants == 1 ? "" : "s")}", 12, Ui.Text));
        }
        c.AddChild(CrewingRow(w, cls));
        var id = cls.Id;
        var box = Box(c, 10);
        Click(box, "Open this class in Designs", () => { _classId = id; _designId = 0; _working = null; _tab = "designs"; });
        return box;
    }
}

/// <summary>A bar filled to the picked value, with a gold tick at the best fit's.</summary>
public partial class MarkBar : Control
{
    private readonly double _value, _mark;

    public MarkBar() : this(0, 0) { }
    public MarkBar(double value, double mark) { _value = value; _mark = mark; MouseFilter = MouseFilterEnum.Ignore; }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(1, 1, 1, 0.08f));
        DrawRect(new Rect2(Vector2.Zero, new Vector2(size.X * (float)Math.Clamp(_value, 0, 1), size.Y)), new Color(Ui.Player, 0.75f));
        var x = size.X * (float)Math.Clamp(_mark, 0, 1);
        DrawRect(new Rect2(new Vector2(x - 1.5f, -2), new Vector2(3, size.Y + 4)), new Color("f2c14e"));
    }
}
