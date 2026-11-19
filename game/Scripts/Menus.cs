using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Save;

namespace Lightlag.Game;

/// <summary>
/// The game menu (Esc): resume, save, load, how to play, settings, early endings, concede, and back to the title
/// screen. The game is paused while it is open.
/// </summary>
public partial class GameMenu : Control
{
    private readonly GameScreen _screen;
    private Control _main = null!;
    private SavesList _saves = null!;
    private HowToPlay _help = null!;
    private SettingsPage _settings = null!;

    public GameMenu() : this(null!) { }
    public GameMenu(GameScreen screen) => _screen = screen;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // A dim layer over the map; clicks on it do nothing.
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), AnchorRight = 1, AnchorBottom = 1 });

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        col.AddChild(Ui.Label("Game menu", 26, Ui.Player));
        col.AddChild(Ui.Button("Resume  (Esc)", () => _screen.CloseMenu()));
        col.AddChild(Ui.Button("Save game", () => _screen.SaveManual()));
        // Ironman: no loading back mid-game.
        if (Main.Instance.Sim?.World.Rules.Ironman != true) col.AddChild(Ui.Button("Load game", () => Show(_saves)));
        col.AddChild(Ui.Button("How to play", () => Show(_help)));
        col.AddChild(Ui.Button("Settings", () => { _settings.ShowTab(SettingsPage.DisplayTab); Show(_settings); }));
        if (Main.Instance.Sim?.World is { Loop: true, Winner: null } world)
        {
            Button? endings = null;
            string EndingsText() => $"Early endings: {(Main.Instance.Sim?.World.Rules.EarlyEndings == true ? "On" : "Off")}";
            endings = Ui.Button(EndingsText(), () =>
            {
                _screen.SetEarlyEndings(Main.Instance.Sim?.World.Rules.EarlyEndings != true);
                endings!.Text = EndingsText();
            }, 18);
            endings.TooltipText = "On: past 80 % control the losing rival offers terms (accept and win now, or play on), the losing side's planets give up sooner "
                                  + "and its crews aim a little worse, and a war where control hasn't moved for 60 days gets more events. Off: none of this happens";
            col.AddChild(endings);
            // Concede: always there, with a second click to be sure.
            var sure = new HBoxContainer { Visible = false, Alignment = BoxContainer.AlignmentMode.Center };
            sure.AddThemeConstantOverride("separation", 8);
            var yes = Ui.SmallButton("Yes, concede", () => _screen.Concede(), "The game ends now and the rival wins");
            yes.Modulate = Ui.Rival;
            sure.AddChild(yes);
            sure.AddChild(Ui.SmallButton("No, play on", () => sure.Visible = false, "Back to the game menu"));
            var concede = Ui.Button("Concede the game…", () => sure.Visible = !sure.Visible, 18);
            concede.TooltipText = Sides.Three(world) ? "Give up: the game ends and the strongest rival wins" : "Give up: the game ends and the rival wins";
            col.AddChild(concede);
            col.AddChild(sure);
        }
        col.AddChild(Ui.Button("Quit to title screen", () => _screen.QuitToTitle()));
        col.AddChild(Ui.Label(Main.Instance.Sim?.World.Rules.Ironman == true
            ? "Ironman: the game keeps one save and writes over it as you play (and when you quit). No loading back."
            : "Quitting saves an autosave first. F5 quick save, F9 quick load.", 13, Ui.Dim));
        _main = Ui.Centered(Ui.Panel(col, 24));
        AddChild(_main);

        _saves = new SavesList(() => Show(_main));
        _saves.Visible = false;
        AddChild(_saves);
        _help = new HowToPlay(() => Show(_main));
        _help.Visible = false;
        AddChild(_help);
        _settings = new SettingsPage(() => Show(_main)) { Visible = false };
        AddChild(_settings);
    }

    /// <summary>Opens on the main page, or straight on Settings ("settings"; at Pauses for "pause", Interface for "display"), or on the saves ("load").</summary>
    public void Open(string? page = null)
    {
        Visible = true;
        if (page is "pause" or "display" or "settings")
            _settings.ShowTab(page switch { "pause" => SettingsPage.PausesTab, "display" => SettingsPage.InterfaceTab, _ => SettingsPage.DisplayTab });
        Show(page switch
        {
            "pause" or "display" or "settings" => _settings,
            "load" when Main.Instance.Sim?.World.Rules.Ironman != true => _saves,
            _ => _main,
        });
    }

    private void Show(Control page)
    {
        _main.Visible = page == _main;
        _saves.Visible = page == _saves;
        _help.Visible = page == _help;
        _settings.Visible = page == _settings;
        if (page == _saves) _saves.Refresh();
    }
}

/// <summary>Every save, newest first, with its game day; click one to load it.</summary>
public partial class SavesList : CenterContainer
{
    private readonly Action _back;
    private VBoxContainer _rows = null!;

    public SavesList() : this(() => { }) { }
    public SavesList(Action back) => _back = back;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var col = new VBoxContainer { CustomMinimumSize = new Vector2(560, 0) };
        col.AddThemeConstantOverride("separation", 8);
        col.AddChild(Ui.Label("Load game", 26, Ui.Player));
        _rows = new VBoxContainer();
        _rows.AddThemeConstantOverride("separation", 4);
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(560, 420), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(_rows);
        _rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        col.AddChild(scroll);
        col.AddChild(Ui.Button("Back", _back, 16));
        AddChild(Ui.Panel(col, 20));
    }

    public void Refresh()
    {
        Ui.ClearChildren(_rows);
        var saves = SaveSlots.List(Main.Instance.Paths);
        if (saves.Count == 0) _rows.AddChild(Ui.Label("No saves yet.", 15, Ui.Dim));
        foreach (var s in saves.Take(40))
        {
            var path = s.Path;
            var when = s.SavedUtc.ToLocalTime();
            var text = $"{s.Kind}  ·  {GameTime.Format(s.Tick)}" + (s.Rival != null ? $"  ·  {Format.Title(s.Rival)} rival" : "")
                       + (s.Over ? "  ·  game over" : "") + $"\n{when:ddd d MMM, HH:mm}";
            var b = new Button { Text = text, Alignment = HorizontalAlignment.Left, CustomMinimumSize = new Vector2(520, 52) };
            b.AddThemeFontSizeOverride("font_size", 15);
            b.Pressed += () => Main.Instance.LoadGame(path);
            _rows.AddChild(b);
        }
    }
}

/// <summary>A one-page guide: what you are trying to do, the basics, and the keys.</summary>
public partial class HowToPlay : CenterContainer
{
    private readonly Action _back;

    public HowToPlay() : this(() => { }) { }
    public HowToPlay(Action back) => _back = back;

    public const string Text =
        "THE GOAL\n" +
        "Control the system. Your control share (top bar) is the average of your share of people, Industry, planets and fleet power. At 80 % you have tipped the game: hold it for 30 days, or reach 95 %, to win. Destroying the rival's shipyard by bombarding it from orbit also wins. "
        + "With Early endings on (the default; switch it in the game menu), a rival that has lost the lead for a few days asks for terms: accept and win now, or play on. "
        + "The side behind a leader loses heart (its planets give up sooner, its crews aim a little worse), and a war where control hasn't moved for 60 days gets more events. "
        + "You can concede from the game menu at any time. Q runs the game at the fastest safe speed until something needs you.\n\n" +
        "PLANETS AND PEOPLE\n" +
        "• Only the two homes start with people. Click a planet for its screen: people, happiness, food, buildings, Settle and Auto top-up. Settle builds a one-trip settler ship at your shipyard that flies the people over and lands (landing makes an empty world yours); it can be blockaded or shot down, so you can pick a fleet to escort it.\n" +
        "• People eat food from farms, shared between your planets. A blockade (armed ships holding orbit) cuts a planet off: it lives off its own farms and store, makes no Industry, and grows unhappy.\n" +
        "• An unhappy blockaded planet gives itself up to the blockader. Bombing its farms starves it sooner; bombing its people leaves a grudge, and it will never surrender to you. Troop ships land soldiers from low orbit to take a planet by force, or board the ship on a blockaded enemy slipway.\n" +
        "• Planet guns shoot ships in low orbit; high orbit is out of their reach but can't bomb or land troops.\n" +
        "• Hungry people put up with it for a few days, then start leaving for your nearest fed world with room (newcomers there are a little unhappy). After 20 hungry days the rest go and the world is abandoned: it makes nothing until settlers land. A blockaded world's people can't leave.\n" +
        "• Power: each planet's grid powers buildings High first, then Normal, then Low. Short of power, it sheds whole buildings and runs the next in line on a brownout (part power, part output). Battery banks store spare power and carry the planet through a shadow (solar fields on a moon go dark behind its planet), a reactor fault or a fuel gap.\n\n" +
        "RACES\n" +
        "• Pick your race and the rival's on the New game screen (title screen › Change race and rules). Humans are the all-rounders. The Hive is many hungry people: its ships are grown from food (a starving Hive grows none), their hulls heal while fed, drone crews stay Trained, its boarders swarm, its buildings are dug in against bombs and invaders, and its sensors are weak. The Lattice is a machine race: every ship runs on AI cores (no food, no crew strain), its people eat nothing but its buildings draw much more power, it settles airless rock at twice the room, turns minerals into Industry faster and aims well, but its missiles and cores are easy to jam and hijack, and a planet it takes loses its people. The Intelligence screen (I) lists each rival's strengths and weaknesses.\n\n" +
        "• Second rival: pick one on the New game screen for a three-race game. Each rival picks whom to go after (and switches now and then); losing its last shipyard puts a side out, and the last side standing, or the first to win on control, wins. With two rivals the Intelligence screen keeps a file on each.\n\n" +
        "• Contact: the Intelligence screen's Contact tab shows how well you know each rival's tongue (Noise, Patterns, Words, Language, Fluent). Listening while in contact, Signals research, prisoners, captures and wrecks raise it; the Lattice needs a captured core or Decode alien signals. Each stage adds intel. In a three-race game, from Words you can send a one-way offer: a gift (an idea, food, alloys or your map) and an ask (go after the other rival, or leave a planet alone, for 60 days), by drone (private, 9 days) or broadcast (2 hours, overheard). The gift goes either way; the odds show at Language.\n\n" +
        "THE BASICS\n" +
        "• The top bar: the time box on the left (date, pause, speeds and what will pause the game next), Needs you and your control share in the middle, map tools on the right. Under it the menu bar of screens: Empire E, Planets Y, Fleets K, Ships J, Supply G, Research T, Intel I, Progress N, each with a badge for what needs you there; Battle view U and the game menu at the right end. Settings › Interface (Esc menu) can hide the menu bar or its icons.\n" +
        "• Needs you: click the badge for the tray. Each row says where, when it bites, and gives the obvious order, an alternative and ⋯ for more, with Go, ⟳ Auto (hand this kind, here, to automation) and Snooze. Pins on the map mark each item with a place. Advice and what automation did today sit in folded strips at the bottom; Take it back sets that area to Advise there. Work through them shows one item at a time. The . key steps through, Enter does the selected row.\n" +
        "• Space pauses; 1-4 set the speed (1x to 8x) and ` (the key left of 1) gives half speed. The game pauses itself when enemies are spotted (P turns that off) and when fighting starts. Settings › Pauses (☰ Game, or the Esc menu) sets what happens for each kind of event: pause, slow to 1x, a pop-up or only a News line, with Watchful, Standard and Hands off presets.\n" +
        "• Ironman (New game screen, Rules): one save for the whole game, written over as you play. No loading back.\n" +
        "• Click a fleet to select it. Right-click a planet or a point to plan a route, then Enter to go. Shift+right-click goes at once.\n" +
        "• Empty planets with Industry can be claimed: park a fleet in orbit and keep the enemy away for 10 days (30 if the rival holds it). A held planet makes half its Industry until settlers arrive.\n" +
        "• Industry pays for ships at your shipyard and for buildings; research costs only scientist time (Empire panel, E).\n" +
        "• More than one yard: a dry dock at a colony of 10k+ people can be upgraded to a Shipyard (planet screen, with Colony shipyards), and a station's yard bays build in orbit (a starbase's great slipway takes cruisers). Every berth builds one ship at a time from one queue; Auto picks the free berth nearest the fleet the ship joins, or pick a yard yourself (Shipyard, V). You lose when your last Shipyard or slipway station falls; if the home yard (★) falls the next one takes over and every yard stops for 10 days.\n" +
        "• You only see what your ships and planets can see. Burning engines show from far away; coasting cold hides you. B cycles the sight view.\n" +
        "• Right-click an enemy fleet to attack it. U opens the battle view: your ships and theirs, approach, burn and weapon cards, rings and a timeline.\n" +
        "• Railguns hit hard up close, missiles reach further, point defence shoots missiles down, shields soak what gets through before armour.\n" +
        "• Heat: firing, dodging and burning heat the ships; radiators come in only while under fire. Overheated ships lose guns and shields until they cool. Heat sinks dump heat and can draw off missiles. Run cold to hide, until the cold clock runs out.\n" +
        "• Fleet panel: firing rule (nearest, largest, finish off, engines, split), long-range fire to make a chaser dodge, hold missiles until closer, or drop them to wake later.\n" +
        "• Click a finished battle in News to watch it again, with the key moments marked. The Battles tab on the left list (Shift+R) lists every battle you fought and fights you saw, with the ships lost on each side.\n\n" +
        "LIGHT LAG (if on)\n" +
        "• You command from your flagship (the flag mark). Orders and news travel at light speed: fleets inside the command ring are live, far ones show where they were seen, with its age.\n" +
        "• Planets and moons block signals. Build relays to get round them, and sensor stations so held planets see further (Empire panel).\n" +
        "• Give far fleets standing orders (In contact) and a mission (hold, patrol, raid, escort, scout). Autonomy sets what they decide for themselves when cut off; their decisions reach News late.\n" +
        "• The engagement screen shows the order deadline: the last moment an order still arrives before contact. Lose the flagship and command passes to the nearest destroyer.\n\n" +
        "KEYS\n" +
        "Wheel zoom · WASD or right-drag pan · Z zoom to the selection · H whole system · Tab next fleet · M merge or gather · " +
        "O orbit · G drift (with a fleet selected; otherwise the Supply screen) · X stop · C change route · R last combat report · F5 quick save · F9 quick load · F8 report a problem · Esc menu";

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 12);
        col.AddChild(Ui.Label("How to play", 26, Ui.Player));
        var body = Ui.Label(Text, 15);
        body.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.CustomMinimumSize = new Vector2(720, 0);
        col.AddChild(body);
        col.AddChild(Ui.Button("Back", _back, 16));
        AddChild(Ui.Panel(col, 24));
    }
}
