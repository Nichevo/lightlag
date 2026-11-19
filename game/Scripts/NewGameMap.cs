using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The Map panel of the New game screen: the kind of map (Hesper or a seeded one), its size and seed, a map code
/// to copy or paste, and a preview of the system that redraws on every change. Homes are not shown.
/// </summary>
public partial class NewGameScreen
{
    private VBoxContainer _map = null!;
    private MapPreview _preview = null!;
    private Label _mapFacts = null!;
    private LineEdit _seedBox = null!;
    private readonly List<(MapType Type, Button Button)> _typeButtons = new();
    private readonly List<(MapSize Size, Button Button)> _sizeButtons = new();
    private Control _sizeRow = null!, _seedRow = null!, _homesRow = null!;
    private (MapSetup Setup, int Sides)? _shown;

    private static string TypeTip(MapType t) => t switch
    {
        MapType.Hesper => "The hand-made system you know: 7 planets, Verdant, Rust, the Scatter, Tamber. The same every game except the start date and, with Random homes, the homes",
        MapType.SunLike => "A yellow or orange star with 5 to 10 planets laid out like a real system: rocky worlds inside, a belt near the frost line, giants with moons and rings outside. Never the same twice",
        MapType.RedDwarf => "A dim red star: small, crowded and fast. Homes sit close together and close to the star, light lag is short, and flares come twice as often",
        _ => "Sun-like or Red dwarf, picked from the seed",
    };

    private Control MapColumn()
    {
        _map = new VBoxContainer { CustomMinimumSize = new Vector2(400, 0) };
        _map.AddThemeConstantOverride("separation", 8);
        _map.AddChild(Heading("THE MAP"));

        var types = new HBoxContainer();
        types.AddThemeConstantOverride("separation", 6);
        foreach (var t in Enum.GetValues<MapType>())
        {
            var type = t;
            var b = Ui.SmallButton(MapCodes.Name(t), () => SetMap(M.MapChoice with { Type = type }), TypeTip(t));
            b.CustomMinimumSize = new Vector2(0, 32);
            types.AddChild(b);
            _typeButtons.Add((t, b));
        }
        _map.AddChild(types);

        var sizes = new HBoxContainer();
        sizes.AddThemeConstantOverride("separation", 6);
        sizes.AddChild(RowLabel("Size"));
        foreach (var s in Enum.GetValues<MapSize>())
        {
            var size = s;
            var tip = s switch
            {
                MapSize.Small => "Fewer planets and moons (11 to 14 worth claiming): a shorter game",
                MapSize.Large => "More planets, moons and rocks (22 to 28 worth claiming): for two rivals or a long game",
                _ => "The usual size (15 to 19 worth claiming), like Hesper",
            };
            var b = Ui.SmallButton(s.ToString(), () => SetMap(M.MapChoice with { Size = size }), tip);
            b.CustomMinimumSize = new Vector2(84, 30);
            sizes.AddChild(b);
            _sizeButtons.Add((s, b));
        }
        _sizeRow = sizes;
        _map.AddChild(sizes);

        var seed = new HBoxContainer();
        seed.AddThemeConstantOverride("separation", 6);
        seed.AddChild(RowLabel("Seed"));
        _seedBox = new LineEdit
        {
            CustomMinimumSize = new Vector2(120, 30), MaxLength = 24,
            TooltipText = "The map's seed: the same seed, kind and size give the same map. Type or paste a seed or a whole map code (SUN-STD-4821-3907), then press Enter",
        };
        _seedBox.TextSubmitted += ReadSeedBox;
        _seedBox.FocusExited += () => ReadSeedBox(_seedBox.Text);
        seed.AddChild(_seedBox);
        seed.AddChild(Ui.SmallButton("New seed", () => SetMap(M.MapChoice with { Seed = Main.NewMapSeed() }), "Roll a new map"));
        seed.AddChild(Ui.SmallButton("Copy map code", () =>
        {
            DisplayServer.ClipboardSet(M.MapChoice.Code);
            M.Toast($"Map code {M.MapChoice.Code} copied");
        }, "Copy this map's code, to play it again later or pass it on"));
        _seedRow = seed;
        _map.AddChild(seed);
        var every = new CheckBox
        {
            Text = "New seed every game", ButtonPressed = M.NewMapSeedChoice, FocusMode = FocusModeEnum.None,
            TooltipText = "On: each game started here gets a new map, and the seed box shows the next one. Off: keep this map until you change it",
        };
        every.AddThemeFontSizeOverride("font_size", 14);
        every.Toggled += v => { M.NewMapSeedChoice = v; Changed(); };
        _map.AddChild(every);
        _homesRow = Box("Random homes", M.RandomStartsChoice, v => M.RandomStartsChoice = v,
            "You and the rival start on planets picked at random. (Seeded maps always pick fair homes at random.)");
        _map.AddChild(_homesRow);

        var view = new HBoxContainer();
        view.AddThemeConstantOverride("separation", 12);
        _preview = new MapPreview { CustomMinimumSize = new Vector2(220, 220) };
        view.AddChild(_preview);
        _mapFacts = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(170, 0), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _mapFacts.AddThemeFontSizeOverride("font_size", 13);
        _mapFacts.AddThemeColorOverride("font_color", Ui.Text);
        view.AddChild(_mapFacts);
        _map.AddChild(view);
        return _map;
    }

    private static Label RowLabel(string text)
    {
        var l = Ui.Label(text, 14, Ui.Dim);
        l.CustomMinimumSize = new Vector2(44, 0);
        l.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return l;
    }

    private void SetMap(MapSetup map)
    {
        M.MapChoice = map;
        Changed();
    }

    private void ReadSeedBox(string text)
    {
        if (MapCodes.Parse(text, M.MapChoice) is MapSetup read && read != M.MapChoice) SetMap(read);
        else _seedBox.Text = MapCodes.SeedText(M.MapChoice.Seed);
    }

    /// <summary>Light the picked kind and size, show the seed, and redraw the preview if the map changed.</summary>
    private void RefreshMap()
    {
        if (_preview == null) return;
        var map = M.MapChoice;
        foreach (var (t, b) in _typeButtons) b.Modulate = t == map.Type ? Ui.Player : new Color(1, 1, 1, 0.6f);
        foreach (var (s, b) in _sizeButtons) b.Modulate = s == map.Size ? Ui.Player : new Color(1, 1, 1, 0.6f);
        _sizeRow.Visible = _seedRow.Visible = map.Seeded;
        _homesRow.Visible = !map.Seeded;
        if (!_seedBox.HasFocus()) _seedBox.Text = MapCodes.SeedText(map.Seed);
        var sides = M.ThirdRaceChoice != Races.None ? 3 : 2;
        if (_shown == (map, sides)) return;
        _shown = (map, sides);
        try
        {
            if (map.Seeded)
            {
                var made = MapMaker.Make(map, sides);
                _preview.Show(made.Data, made.StarLight);
                var star = made.Type == MapType.RedDwarf ? "a dim red star" : made.StarLight > 1.2 ? "a bright yellow-white star" : made.StarLight < 0.75 ? "an orange star" : "a yellow star";
                _mapFacts.Text = $"{MapCodes.Name(made.Type)} · {map.Size}\n{map.Code}\n\n{made.StarName}, {star}\n"
                                 + $"{made.Planets} planets ({made.Giants} giants), {made.Moons} moons, {made.Rocks} rocks\n"
                                 + $"{made.WorthClaiming} worth claiming\n\n"
                                 + (made.Fair ? $"Fair start check: passed, home scores within {made.Spread * 100:0} %."
                                     : $"Fair start check: as close as it gets, homes within {made.Spread * 100:0} %.{(sides == 3 && map.Size != MapSize.Large ? " Three races play fairer on Large." : "")}")
                                 + "\n\nHomes stay hidden until the game starts.";
            }
            else
            {
                var data = GameData.LoadSystem(Scenarios.TestSystem + ".json");
                _preview.Show(data, 1);
                _mapFacts.Text = "Hesper · the hand-made map\n\n"
                                 + $"{data.Bodies.Count(b => b.Kind == BodyKind.Planet)} planets, {data.Bodies.Count(b => b.Kind == BodyKind.Moon)} moons, "
                                 + $"{data.Bodies.Count(b => b.Kind == BodyKind.Asteroid)} rocks\n"
                                 + $"{data.Bodies.Count(b => b.Kind != BodyKind.Star && (b.Industry ?? 0) > 0)} worth claiming\n\n"
                                 + "The same every game except the start date" + (M.RandomStartsChoice ? " and the homes." : ".");
            }
        }
        catch (Exception ex)
        {
            M.Log.Warn("ui", "Could not build the map preview", new { map = map.Code, error = ex.Message });
            _mapFacts.Text = "The preview could not be drawn; the game will still build this map.";
        }
    }
}

/// <summary>
/// A small top-down drawing of a system for the New game screen: orbits spaced by the log of their size (so the
/// inner worlds don't crowd), the warm band, belts, and each planet with its moons. Positions are only for show.
/// </summary>
public partial class MapPreview : Control
{
    private SystemData? _data;
    private double _light = 1;

    public void Show(SystemData data, double starLight)
    {
        _data = data;
        _light = starLight;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.03f, 0.05f));
        if (_data == null) return;
        var c = size / 2;
        var maxR = Math.Min(size.X, size.Y) / 2 - 10;
        var star = _data.Bodies.FirstOrDefault(b => b.Parent == null);
        if (star == null) return;
        var planets = _data.Bodies.Where(b => b.Parent == star.Id).ToList();
        if (planets.Count == 0) return;
        var inner = planets.Min(p => p.OrbitRadiusMm);
        var outer = planets.Max(p => p.OrbitRadiusMm);
        var span = Math.Max(1e-6, Math.Log(outer / inner));
        float R(double mm) => (float)(maxR * (0.16 + 0.84 * Math.Log(Math.Max(mm, inner) / inner) / span));

        // The warm band, where homes can be, and belts round the star.
        var root = Math.Sqrt(Math.Max(1e-6, _light));
        var warmIn = R(0.85 * root * Physics.MmPerAu);
        var warmOut = R(1.5 * root * Physics.MmPerAu);
        DrawArc(c, (warmIn + warmOut) / 2, 0, Mathf.Tau, 96, new Color(0.3f, 0.6f, 0.35f, 0.16f), Math.Max(2, warmOut - warmIn));
        foreach (var belt in (_data.Belts ?? new()).Where(b => b.Parent == star.Id && b.Kind == BeltKind.Belt))
        {
            var r1 = R(belt.InnerAu * Physics.MmPerAu);
            var r2 = R(belt.OuterAu * Physics.MmPerAu);
            DrawArc(c, (r1 + r2) / 2, 0, Mathf.Tau, 96, new Color(0.55f, 0.5f, 0.42f, 0.35f), Math.Max(2, r2 - r1));
        }
        foreach (var p in planets) DrawArc(c, R(p.OrbitRadiusMm), 0, Mathf.Tau, 96, new Color(0.45f, 0.55f, 0.7f, 0.22f), 1, true);
        DrawCircle(c, 6, new Color(star.Color));

        // Planets spread round by the golden angle (the real start positions come with the game).
        var i = 0;
        foreach (var p in planets.OrderBy(p => p.OrbitRadiusMm))
        {
            var a = i++ * 2.39996f;
            var at = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * R(p.OrbitRadiusMm);
            var size2 = p.Kind == BodyKind.Asteroid ? 1.4f : p.Type is "gas" ? 5.5f : p.Type == "ice-giant" ? 4.5f : 3f;
            if (p.Type is "gas" or "ice-giant" && (_data.Belts ?? new()).Any(b => b.Parent == p.Id && b.Kind == BeltKind.Ring))
                DrawArc(at, size2 + 2.5f, 0, Mathf.Tau, 24, new Color(0.8f, 0.75f, 0.6f, 0.5f), 1, true);
            DrawCircle(at, size2, new Color(p.Color));
            var moons = _data.Bodies.Where(m => m.Parent == p.Id).ToList();
            for (var m = 0; m < moons.Count; m++)
            {
                var ma = a + 1.3f + m * 0.9f;
                DrawCircle(at + new Vector2(Mathf.Cos(ma), Mathf.Sin(ma)) * (size2 + 4 + m * 1.6f), 1.1f, new Color(0.75f, 0.75f, 0.75f));
            }
        }
    }
}
