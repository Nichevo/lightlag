using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The New game screen (title screen › Change race and rules): the race you lead, the map (see NewGameMap.cs), the
/// rivals and how strong they play, and the rules in four groups with Relaxed / Standard / Full presets. Choices are
/// kept for next time.
/// </summary>
public partial class NewGameScreen : MarginContainer
{
    private readonly Action _back;
    private VBoxContainer _rules = null!;
    private Label _summary = null!;
    private Label _presetNow = null!;
    private Control _contactRow = null!;
    private readonly List<(RulesPreset Preset, Button Button)> _presetButtons = new();
    private readonly List<(Difficulty Level, Button Button)> _levelButtons = new();

    public NewGameScreen() : this(() => { }) { }
    public NewGameScreen(Action back) => _back = back;

    private static Main M => Main.Instance;

    private static readonly (LearnMode Mode, string Name)[] LearnChoices =
    {
        (LearnMode.All, "Overnight runs and my games"), (LearnMode.Overnight, "Overnight runs only"), (LearnMode.Off, "Nothing (off)"),
    };

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "left", "right", "top", "bottom" }) AddThemeConstantOverride($"margin_{side}", 24);

        var page = new VBoxContainer();
        page.AddThemeConstantOverride("separation", 16);
        AddChild(page);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 18);
        var back = Ui.Button("‹ Back  (Esc)", Leave, 16);
        back.CustomMinimumSize = new Vector2(150, 40);
        head.AddChild(back);
        head.AddChild(Ui.Label("NEW GAME", 32, Ui.Player));
        var kept = Ui.Label("Your choices are kept for next time.", 14, Ui.Dim);
        kept.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(kept);
        page.AddChild(head);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        page.AddChild(scroll);
        var center = new CenterContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(center);
        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 16);
        center.AddChild(cols);

        var left = new VBoxContainer();
        left.AddThemeConstantOverride("separation", 12);
        left.AddChild(Ui.Panel(YouColumn(), 16));
        left.AddChild(Ui.Panel(MapColumn(), 16));
        cols.AddChild(left);
        cols.AddChild(Ui.Panel(AgainstColumn(), 16));
        _rules = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
        _rules.AddThemeConstantOverride("separation", 6);
        cols.AddChild(Ui.Panel(_rules, 16));
        BuildRules();
        Changed();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (Visible && e.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            Leave();
        }
    }

    private void Leave()
    {
        NewGameChoices.Save(M);
        _back();
    }

    private static Label Heading(string text)
    {
        var l = Ui.Label(text, 15, Ui.Dim);
        return l;
    }

    // ---- You lead ------------------------------------------------------------------------------

    private Control YouColumn()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
        col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Heading("YOU LEAD"));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        col.AddChild(row);
        var about = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(390, 0) };
        about.AddThemeFontSizeOverride("font_size", 13);
        about.AddThemeColorOverride("font_color", Ui.Text);
        var group = new ButtonGroup();
        foreach (var (id, name) in Races.Choices())
        {
            var random = id == Races.Random;
            var card = new Button
            {
                Text = name, ToggleMode = true, ButtonGroup = group, FocusMode = FocusModeEnum.None,
                Icon = random ? null : Art.Texture("Races", id, 0.22f),
                IconAlignment = HorizontalAlignment.Center, VerticalIconAlignment = VerticalAlignment.Top,
                CustomMinimumSize = new Vector2(random ? 76 : 96, 120), ButtonPressed = M.PlayerRaceChoice == id,
                TooltipText = random ? "A race picked at random when the game starts" : Races.Get(id).Blurb,
            };
            card.AddThemeFontSizeOverride("font_size", 14);
            // The picked race has a bright frame.
            var picked = new StyleBoxFlat { BgColor = new Color(0.1f, 0.16f, 0.24f, 0.95f), BorderColor = Ui.Player };
            picked.SetBorderWidthAll(2);
            picked.SetCornerRadiusAll(4);
            picked.SetContentMarginAll(4);
            card.AddThemeStyleboxOverride("pressed", picked);
            card.AddThemeStyleboxOverride("hover_pressed", picked);
            card.Pressed += () => { M.PlayerRaceChoice = id; about.Text = TitleScreen.About(id); Changed(); };
            row.AddChild(card);
        }
        about.Text = TitleScreen.About(M.PlayerRaceChoice);
        col.AddChild(about);
        return col;
    }

    // ---- Against -------------------------------------------------------------------------------

    private Control AgainstColumn()
    {
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(290, 0) };
        col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Heading("AGAINST"));

        var choices = Races.Choices().ToList();
        col.AddChild(Ui.Label("Rival", 14, Ui.Dim));
        var rival = new OptionButton { FocusMode = FocusModeEnum.None,
            TooltipText = "The rival's race. Random: rolled when the game starts; the Intelligence screen (I) says which it is" };
        foreach (var (_, name) in choices) rival.AddItem(name);
        rival.Selected = Math.Max(0, choices.FindIndex(c => c.Id == M.RivalRaceChoice));
        rival.ItemSelected += i => { M.RivalRaceChoice = choices[(int)i].Id; Changed(); };
        col.AddChild(rival);

        col.AddChild(Ui.Label("Second rival", 14, Ui.Dim));
        var third = new OptionButton { FocusMode = FocusModeEnum.None,
            TooltipText = "None: a one-on-one game. Pick a race for a three-race game: two rivals, each against everyone, and one-way contact offers once you know a rival's language" };
        var thirdChoices = new List<(string Id, string Name)> { (Races.None, "None (one on one)") }.Concat(choices).ToList();
        foreach (var (_, name) in thirdChoices) third.AddItem(name);
        third.Selected = Math.Max(0, thirdChoices.FindIndex(c => c.Id == M.ThirdRaceChoice));
        third.ItemSelected += i => { M.ThirdRaceChoice = thirdChoices[(int)i].Id; Changed(); };
        col.AddChild(third);

        _contactRow = Box("Contact offers", M.ContactOffersChoice, v => M.ContactOffersChoice = v,
            "With a second rival: once you know a race's tongue (Words), send it gifts and ask it to go after the other rival or leave a planet alone. Rivals do the same. Off: no offers; the language track still raises intel");
        col.AddChild(_contactRow);

        col.AddChild(Ui.Label("How the rivals play", 14, Ui.Dim));
        var levels = new HBoxContainer();
        levels.AddThemeConstantOverride("separation", 6);
        foreach (var level in Enum.GetValues<Difficulty>())
        {
            var l = level;
            var b = Ui.SmallButton(level.ToString(), () => { M.DifficultyChoice = l; Changed(); },
                l == Difficulty.Easy ? "Rivals that build and fight less sharply: for learning the game" : "Rivals that play by your rules at full strength");
            b.CustomMinimumSize = new Vector2(100, 34);
            levels.AddChild(b);
            _levelButtons.Add((l, b));
        }
        col.AddChild(levels);

        col.AddChild(Ui.Label("What the rivals learn", 14, Ui.Dim));
        var learnRow = new HBoxContainer();
        learnRow.AddThemeConstantOverride("separation", 6);
        var learn = new OptionButton { FocusMode = FocusModeEnum.None,
            TooltipText = "Rivals remember how past games went and lean towards what worked: their personality, leanings and the odds they want. "
                          + "From your games they also learn your habits (early attacks, missiles, raids, taking colonies) and counter them. "
                          + "They never learn where anyone starts, and every lean stays within what a normal roll could give. Games from an older balance count for less" };
        foreach (var (mode, name) in LearnChoices) learn.AddItem(name);
        learn.Selected = Math.Max(0, Array.FindIndex(LearnChoices, c => c.Mode == M.LearnChoice));
        learn.ItemSelected += i => { M.LearnChoice = LearnChoices[(int)i].Mode; Changed(); };
        learnRow.AddChild(learn);
        Button? forget = null;
        var sure = false;
        string ForgetTip() => $"The rivals' memory holds {RivalMemory.Load(M.Paths.Root).Games.Count} finished games. Forget them all and start learning again";
        forget = Ui.SmallButton("Forget", () =>
        {
            if (!sure) { sure = true; forget!.Text = "Sure?"; return; }
            RivalMemory.Forget(M.Paths.Root);
            sure = false;
            forget!.Text = "Forget";
            forget.TooltipText = ForgetTip();
            M.Toast("The rivals have forgotten every past game");
        }, ForgetTip());
        forget.MouseExited += () => { sure = false; forget.Text = "Forget"; };
        learnRow.AddChild(forget);
        col.AddChild(learnRow);

        col.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 12) });
        _summary = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(280, 0) };
        _summary.AddThemeFontSizeOverride("font_size", 13);
        _summary.AddThemeColorOverride("font_color", Ui.Text);
        col.AddChild(_summary);
        var start = Ui.Button("Start game", () => M.StartChosenGame(), 20);
        start.CustomMinimumSize = new Vector2(280, 54);
        start.Modulate = Ui.Player;
        col.AddChild(start);
        return col;
    }

    // ---- Rules ---------------------------------------------------------------------------------

    private void BuildRules()
    {
        Ui.ClearChildren(_rules);
        _presetButtons.Clear();
        _rules.AddChild(Heading("RULES"));
        var presets = new HBoxContainer();
        presets.AddThemeConstantOverride("separation", 6);
        foreach (var p in Enum.GetValues<RulesPreset>())
        {
            var preset = p;
            var b = Ui.SmallButton(p.ToString(), () => { NewGameChoices.Apply(M, preset); BuildRules(); Changed(); }, NewGameChoices.Tip(p));
            b.CustomMinimumSize = new Vector2(90, 32);
            presets.AddChild(b);
            _presetButtons.Add((p, b));
        }
        _presetNow = Ui.Label("", 14, Ui.Text);
        _presetNow.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        presets.AddChild(_presetNow);
        _rules.AddChild(presets);

        Group("LIGHT");
        Row("Light lag", LightPick(), "How long news and orders take to cross the system. Real light: Tamber is about 45 minutes from Verdant.");
        Tick("Decapitation", M.DecapitationChoice, v => M.DecapitationChoice = v,
            "With light lag on: lose your flagship with no destroyer left to take command and you lose the game (the rival too)");

        Group("THE SYSTEM");
        Row("Planet orbits", OrbitsPick(), "How fast planets go round the sun. True speed: planets move at a quarter to a half of a ship's speed, so the map rearranges mid-game and slingshots are strong. 5x slower (default): planets drift, so trips and plans hold steady; slingshots are weak. Moons always go round 5x slower than true");
        Row("Events", EventsPick(), "How often events come (flares, finds, reactor faults, rock strikes, defectors, comets): the same for the rival. Each has an Auto pick");
        Tick("People everywhere", M.SpreadPeopleChoice, v => M.SpreadPeopleChoice = v,
            "Planets start with a few independent people instead of empty: hold orbit to win them over");
        Tick("Hazards", M.HazardsChoice, v => M.HazardsChoice = v,
            "Rocks in belts, rings and battle debris hit ships at speed (big ships most), radiation belts strain crews, ships run hot close to the sun. Off: space is empty");

        Group("RUNNING AN EMPIRE");
        Tick("Supply lines", M.SupplyLinesChoice, v => M.SupplyLinesChoice = v, "Ammunition and food run down away from home. Off: ships keep full stocks (fuel still counts)");
        Tick("Fuel types", M.FuelTypesChoice, v => M.FuelTypesChoice = v,
            "Each engine burns its own fuel (water, hydrogen + oxygen, fission pellets, deuterium, helium-3, antimatter), each with its own thrust, reach, heat and signature. Off: one fuel for every engine");
        Tick("Happiness", M.HappinessChoice, v => M.HappinessChoice = v,
            "Happiness moves with food, blockades and bombing, and blockaded planets give up when unhappy. Off: only starving planets give up");
        Tick("Industry budget", M.BudgetChoice, v => M.BudgetChoice = v,
            "Each day's Industry is split into six pots (ships, buildings, stations, refits, transport, reserve) that save up and pay only for their own things; set shares and goals in the Budget menu (E). Off: one pool, spent by whatever asks first");
        Tick("Research different each game", M.ResearchVarietyChoice, v => M.ResearchVarietyChoice = v,
            "Each side rolls 4-5 leads (30 % quicker, in view from the start) and 2-3 hard problems (50 % slower) from the map and chance, and about 10 minor ideas are lost until a spark, a survey find, a capture or a trade turns them up. The rival rolls its own; intel at level 3 shows its leads. Off: the same research web every game");

        Group("ENDING AND SAVING");
        Tick("Early endings", M.EarlyEndingsChoice, v => M.EarlyEndingsChoice = v,
            "Past 80 % control the losing rival offers terms (accept and win now, or play on), the losing side's planets give up sooner and its crews aim a little worse, and a war where control hasn't moved for 60 days gets more events. Off: none of this; the game ends only at 95 %, after 30 days at 80 %, or with a shipyard. Switch it any time in the game menu (Esc). Conceding is always there");
        Tick("Ironman", M.IronmanChoice, v => M.IronmanChoice = v,
            "One save for the whole game, written over as you play and when you quit. No loading back, no quick load: every choice sticks");
    }

    private void Group(string name)
    {
        var l = Ui.Label(name, 13, Ui.Player);
        _rules.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        _rules.AddChild(l);
    }

    private void Row(string name, Control pick, string tip)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var label = Ui.Label(name, 15, Ui.Text);
        label.CustomMinimumSize = new Vector2(160, 0);
        label.TooltipText = tip;
        label.MouseFilter = MouseFilterEnum.Pass;
        label.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(label);
        pick.TooltipText = tip;
        row.AddChild(pick);
        _rules.AddChild(row);
    }

    private void Tick(string name, bool on, Action<bool> set, string tip) => _rules.AddChild(Box(name, on, set, tip));

    private Control Box(string name, bool on, Action<bool> set, string tip)
    {
        var box = new CheckBox { Text = name, ButtonPressed = on, FocusMode = FocusModeEnum.None, TooltipText = tip };
        box.AddThemeFontSizeOverride("font_size", 15);
        box.Toggled += v => { set(v); Changed(); };
        return box;
    }

    private OptionButton LightPick()
    {
        var pick = new OptionButton { FocusMode = FocusModeEnum.None };
        pick.AddItem("Real light speed");
        pick.AddItem("Light 10x slower (test)");
        pick.AddItem("Off");
        pick.Selected = !M.LightChoice.On ? 2 : M.LightChoice.SpeedFactor < 0.5 ? 1 : 0;
        pick.ItemSelected += i =>
        {
            M.LightChoice = i switch
            {
                1 => new LightSetup(true, 0.1),
                2 => new LightSetup(false, 1.0),
                _ => new LightSetup(true, 1.0),
            };
            Changed();
        };
        return pick;
    }

    private OptionButton OrbitsPick()
    {
        var pick = new OptionButton { FocusMode = FocusModeEnum.None };
        var choices = Orbits.PlanetChoices;
        foreach (var k in choices) pick.AddItem(k <= 1 ? "True speed" : $"{k:0}x slower");
        pick.Selected = Math.Max(0, Array.IndexOf(choices, M.PlanetOrbitsChoice));
        pick.ItemSelected += i => { M.PlanetOrbitsChoice = choices[(int)i]; Changed(); };
        return pick;
    }

    private OptionButton EventsPick()
    {
        var pick = new OptionButton { FocusMode = FocusModeEnum.None };
        foreach (var r in Enum.GetValues<EventRate>()) pick.AddItem(r.ToString(), (int)r);
        pick.Selected = (int)M.EventsChoice;
        pick.ItemSelected += i => { M.EventsChoice = (EventRate)(int)i; Changed(); };
        return pick;
    }

    /// <summary>Something changed: light the picked preset and level, show or hide contact offers, read the set-up back.</summary>
    private void Changed()
    {
        var now = NewGameChoices.Current(M);
        foreach (var (p, b) in _presetButtons) b.Modulate = p == now ? Ui.Player : new Color(1, 1, 1, 0.6f);
        if (_presetNow != null) _presetNow.Text = now == null ? "Custom" : "";
        foreach (var (l, b) in _levelButtons) b.Modulate = l == M.DifficultyChoice ? Ui.Player : new Color(1, 1, 1, 0.6f);
        if (_contactRow != null) _contactRow.Visible = M.ThirdRaceChoice != Races.None;
        if (_summary != null) _summary.Text = NewGameChoices.Summary(M);
        RefreshMap();
    }
}
