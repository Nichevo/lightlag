using Godot;
using Lightlag.Sim;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;
using Lightlag.Sim.Save;
using FileAccess = Godot.FileAccess;

namespace Lightlag.Game;

/// <summary>
/// The app root. Sets up logging and crash reporting before anything else, then swaps between the
/// title screen and the game screen. F8 (player report) works on every screen.
/// </summary>
public partial class Main : Node
{
    public static Main Instance { get; private set; } = null!;

    public BuildInfo Build { get; private set; } = BuildInfo.Dev;
    public DataPaths Paths { get; private set; } = null!;
    public GameLog Log { get; private set; } = null!;

    /// <summary>The game being played, if any. Crash and F8 reports include it.</summary>
    public Simulation? Sim
    {
        get => _sim;
        set
        {
            if (_sim != null) _sim.OrderRefused -= OnRefused;
            _sim = value;
            if (_sim != null) _sim.OrderRefused += OnRefused;
        }
    }
    private Simulation? _sim;

    /// <summary>Any order of yours that is turned down says why on screen, not only in the log.</summary>
    private void OnRefused(GameCommand command, string why)
    {
        var fleetId = (command as IFleetCommand)?.FleetId;
        Toast($"Order not carried out: {why}", onClick: fleetId is int id && _screen is GameScreen g ? () => g.OpenFleet(id) : null);
    }

    private Node? _screen;
    private CanvasLayer _overlay = null!;
    private ReportDialog? _report;
    private bool _crashing;

    public override void _Ready()
    {
        Instance = this;
        Build = LoadBuild();
        Paths = new DataPaths(ResolveDataDir()).EnsureCreated();
        GameLog.PruneOldFiles(Paths.Logs, keep: 30);
        Log = new GameLog(Build, System.IO.Path.Combine(Paths.Logs, $"lightlag_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jsonl"));
        if (OS.HasFeature("editor")) Log.Echo = (_, line) => GD.Print(line);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Crash(e.ExceptionObject as Exception ?? new Exception($"{e.ExceptionObject}"), "unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("game", "Background task failed", new { error = e.Exception.ToString() });
        OS.AddLogger(new GodotLogBridge(Log));

        Log.Info("game", "Lightlag started", new
        {
            build = Build.ToString(),
            godot = (string)Engine.GetVersionInfo()["string"],
            os = OS.GetName() + " " + OS.GetVersion(),
            dataDir = Paths.Root,
        });

        _overlay = new CanvasLayer { Layer = 100 };
        AddChild(_overlay);
        Graphics.Load(this, Paths.Root);
        var args = OS.GetCmdlineUserArgs();
        Display.Load(this, Paths.Root, applyWindow: !args.Contains("--smoke-test") && !args.Any(a => a.StartsWith("--screenshots=")));
        PlayerInfo.Load(Paths.Root);
        PauseSettings.Load(Paths.Root);
        RivalAdvisor.Load(Paths.Root);
        if (OS.GetCmdlineUserArgs().Contains("--smoke-test"))
        {
            CallDeferred(MethodName.RunSmokeTest);
            return;
        }
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--screenshots=")) is string shots)
        {
            _ = TakeScreenshots(shots["--screenshots=".Length..].Trim('"'));
            return;
        }
        ShowTitle();
    }

    // ---- Screens ---------------------------------------------------------------------------

    public void ShowTitle()
    {
        Sim = null;
        SetScreen(new TitleScreen());
    }

    /// <summary>Light lag for new games, chosen on the title screen: real light speed, ten times slower, or off.</summary>
    public LightSetup LightChoice { get; set; } = new(true, 1.0);

    /// <summary>Decapitation for the next new game: losing the flagship with no destroyer left loses the game.</summary>
    public bool DecapitationChoice { get; set; } = true;

    /// <summary>The optional systems for the next new game (see GameRules). Random starts are on by default in the game.</summary>
    public bool HappinessChoice { get; set; } = true;
    public bool SupplyLinesChoice { get; set; } = true;
    public bool SpreadPeopleChoice { get; set; }
    public bool FuelTypesChoice { get; set; } = true;
    public bool HazardsChoice { get; set; } = true;
    public EventRate EventsChoice { get; set; } = EventRate.Normal;
    public bool RandomStartsChoice { get; set; } = true;
    public bool IronmanChoice { get; set; }

    /// <summary>Races for the next new game: yours, and the rival's (human, hive or random; see Races).</summary>
    public string PlayerRaceChoice { get; set; } = Races.Human;
    public string RivalRaceChoice { get; set; } = Races.Random;

    /// <summary>A second rival (a three-race game), or none.</summary>
    public string ThirdRaceChoice { get; set; } = Races.None;

    public void StartNewGame(ulong? seed = null, Difficulty difficulty = Difficulty.Normal)
    {
        var s = seed ?? (ulong)Random.Shared.NextInt64(1, 1_000_000_000);
        RunGuarded("starting a new game", () =>
        {
            Sim = Simulation.NewGame(Scenarios.TestSystem, s, Log, Build, options: GameOptions.VsRival(difficulty) with
            {
                Light = LightChoice, Decapitation = DecapitationChoice, Happiness = HappinessChoice, SupplyLines = SupplyLinesChoice,
                StartPopulation = SpreadPeopleChoice, RandomStarts = RandomStartsChoice, FuelTypes = FuelTypesChoice,
                Hazards = HazardsChoice, Events = EventsChoice, PlayerRace = PlayerRaceChoice, RivalRace = RivalRaceChoice,
                Ironman = IronmanChoice, ThirdRace = ThirdRaceChoice == Races.None ? null : ThirdRaceChoice,
            });
            if (IronmanChoice) SaveSlots.WriteIronman(Paths, Sim);
            SetScreen(new GameScreen());
        });
    }

    /// <summary>The last battle sandbox set-up, so "Restart battle" can run it again.</summary>
    public SandboxSetup LastSandbox { get; private set; } = new();

    public void StartSandbox(SandboxSetup setup, ulong? seed = null)
    {
        LastSandbox = setup;
        var s = seed ?? (ulong)Random.Shared.NextInt64(1, 1_000_000_000);
        RunGuarded("starting the battle sandbox", () =>
        {
            Sim = Simulation.NewGame(Scenarios.BattleSandbox, s, Log, Build, setup);
            var screen = new GameScreen();
            SetScreen(screen);
            screen.FocusSandbox();
        });
    }

    public void ShowSandboxSetup() => SetScreen(new SandboxScreen());

    public void LoadGame(string path)
    {
        RunGuarded($"loading {System.IO.Path.GetFileName(path)}", () =>
        {
            Sim = Simulation.FromSave(SaveGame.Load(path), Log, Build);
            SetScreen(new GameScreen());
            Toast($"Loaded {System.IO.Path.GetFileNameWithoutExtension(path)}");
        });
    }

    private void SetScreen(Node screen)
    {
        if (screen is GameScreen) News.Clear();
        _screen?.QueueFree();
        _screen = screen;
        AddChild(screen);
        MoveChild(_overlay, -1);
    }

    // ---- Reports and crashes ---------------------------------------------------------------

    public override void _Input(InputEvent e)
    {
        // F11 or Alt+Enter: full screen on or off, on every screen.
        if (e is InputEventKey { Pressed: true, Echo: false } k && !k.CtrlPressed && !k.ShiftPressed
            && ((k.Keycode == Key.F11 && !k.AltPressed) || (k.AltPressed && k.Keycode is Key.Enter or Key.KpEnter)))
        {
            GetViewport().SetInputAsHandled();
            RunGuarded("full screen toggle", Display.ToggleFullscreen);
            return;
        }
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.F8 } && _report == null)
        {
            GetViewport().SetInputAsHandled();
            OpenReport();
        }
    }

    private void OpenReport()
    {
        // Grab the screen before the dialog covers it.
        var shot = GetViewport().GetTexture().GetImage();
        var screenName = _screen?.GetType().Name ?? "none";
        var wasPaused = GetTree().Paused;
        GetTree().Paused = true;
        _report = new ReportDialog();
        _report.Finished += note =>
        {
            if (note != null)
            {
                try
                {
                    var dir = Reports.WritePlayerReport(Paths, Log, Sim, note, screenName);
                    shot.SavePng(System.IO.Path.Combine(dir, "screenshot.png"));
                    Toast("Report saved. Thanks!", dir);
                }
                catch (Exception ex)
                {
                    Log.Error("report", "Could not write player report", new { error = ex.ToString() });
                    Toast("Could not save the report (see log)");
                }
            }
            _report?.QueueFree();
            _report = null;
            GetTree().Paused = wasPaused;
        };
        _overlay.AddChild(_report);
    }

    /// <summary>Runs an action; if it throws, writes a crash report instead of losing the error.</summary>
    public bool RunGuarded(string what, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Crash(ex, what);
            return false;
        }
    }

    /// <summary>Writes a crash report and shows it. Play stops; the player can go back to the title.</summary>
    public void Crash(Exception error, string where)
    {
        if (_crashing) return; // a crash while handling a crash: the first report is the useful one
        _crashing = true;
        try
        {
            var dir = Reports.WriteCrash(Paths, Log, Sim, error, where);
            CallDeferred(MethodName.ShowCrashPanel, dir, $"{error.GetType().Name}: {error.Message}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"Crash report failed: {ex}");
        }
        finally
        {
            _crashing = false;
        }
    }

    private void ShowCrashPanel(string folder, string message)
    {
        GetTree().Paused = true;
        var panel = new CrashPanel(folder, message);
        panel.BackToTitle += () =>
        {
            panel.QueueFree();
            GetTree().Paused = false;
            ShowTitle();
        };
        _overlay.AddChild(panel);
    }

    public void Toast(string text, string? folderToOpen = null, Action? onClick = null)
    {
        _overlay.AddChild(new Toast(text, folderToOpen, onClick));
        if (Sim == null || folderToOpen != null) return;
        AddNews(text, onClick);
    }

    /// <summary>
    /// Adds a line to the News menu without a pop-up. Ordinary lines drop off after the latest 30;
    /// kept ones (battle results) stay for the whole game.
    /// </summary>
    public void AddNews(string text, Action? onClick = null, bool keep = false)
    {
        if (Sim == null) return;
        News.Add(new NewsItem(Sim.World.Hour, text, onClick, keep));
        while (News.Count(n => !n.Keep) > 30) News.RemoveAt(News.FindIndex(n => !n.Keep));
    }

    /// <summary>The latest messages of this game (the News menu), newest last.</summary>
    public List<NewsItem> News { get; } = new();

    public void Quit(int exitCode = 0)
    {
        OnClosing();
        GetTree().Quit(exitCode);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest) OnClosing();
    }

    private bool _closed;

    private void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        if (Sim != null) RunGuarded("autosave on quit", () => SaveSlots.WriteAutosave(Paths, Sim));
        Log.Info("game", "Lightlag closed");
        Log.Dispose();
    }

    /// <summary>
    /// Light lag: a frigate sent off towards Rust is out of command range, so the map shows it where its
    /// news left it, with its age, and an order to it still on its way. Light is ten times slower here so
    /// the delays read clearly. Afterwards the first game is restored for the remaining pictures.
    /// </summary>
    private async Task ShootLightLag(Func<string, Task> shoot)
    {
        var keep = Sim!;
        var choice = LightChoice;
        LightChoice = new LightSetup(true, 0.1);
        StartNewGame(7);
        var game = (GameScreen)_screen!;
        game.TogglePause();
        var w = Sim!.World;
        var home = w.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player);
        var frigate = w.ShipsOf(home).First(s => s.ClassId == "gun-frigate");
        Sim.Issue(new SplitFleetCommand(home.Id, new List<int> { frigate.Id }));
        Sim.Step();
        var lone = w.FleetOf(frigate)!;
        var rust = w.Bodies.First(b => b.Key == "rust");
        Sim.Issue(new PlanFlightCommand(lone.Id, new FlightRequest(rust.Id, Vec2.Zero, 0.8, ArrivalStyle.Brake, HoldStyle.Orbit, w.Tick)));
        Sim.Step();
        while (Light.DelayHours(w, Lightlag.Sim.Core.Side.Player, lone.Position) < 0.25 && lone.InFlight) Sim.Step();
        Sim.Issue(new SetHoldCommand(lone.Id, HoldStyle.Drift));
        Sim.ApplyPending();
        game.SnapToNow();
        game.Camera.Center = (Light.Seat(w, Lightlag.Sim.Core.Side.Player) + lone.Position) * 0.5;
        game.Camera.FitRadius(Vec2.Distance(Light.Seat(w, Lightlag.Sim.Core.Side.Player), lone.Position) * 0.75, instant: true);
        game.Select(lone.Id);
        await shoot("29-light-lag");
        LightChoice = choice;
        Sim = keep;
        SetScreen(new GameScreen());
        ((GameScreen)_screen!).TogglePause();
    }

    /// <summary>
    /// CI runs the game under a virtual screen with --screenshots=DIR to photograph a set of map views,
    /// so changes to how the map looks can be checked without a person at the PC.
    /// </summary>
    private async Task TakeScreenshots(string dir)
    {
        var ok = true;
        try
        {
            Directory.CreateDirectory(dir);
            RandomStartsChoice = false; // the shots below expect the usual homes
            StartNewGame(7);
            var game = (GameScreen)_screen!;
            Sim!.StepMany(3 * GameTime.TicksPerDay);
            game.TogglePause();
            int Key(string key) => Sim.World.Bodies.First(b => b.Key == key).Id;
            var fleet = Sim.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player);
            // A fleet on its way to Vela, so routes show in the pictures.
            Sim.Issue(new PlanFlightCommand(fleet.Id, new FlightRequest(Key("vela"), Vec2.Zero, 0.7, ArrivalStyle.Brake, HoldStyle.Orbit, Sim.World.Tick)));
            Sim.StepMany(30);
            var views = new (string Name, Action Setup)[]
            {
                ("1-inner-system", () => { }),
                ("2-whole-system", () => game.ShowWholeSystem()),
                ("3-verdant-and-pale", () => { game.Follow(Key("verdant"), true); game.Select(Key("pale")); }),
                ("4-tamber-moons", () => game.Follow(Key("tamber"), true)),
                ("5-rust-moons", () => game.Follow(Key("rust"), true)),
                ("6-flint-close", () => { game.Follow(Key("flint"), true); game.Select(Key("flint")); }),
                ("7-fleet", () => { game.Follow(fleet.Id, false); game.Camera.SetScale(0.02); game.Select(fleet.Id); }),
                ("8-fleet-close", () => { game.Follow(fleet.Id, false); game.Camera.SetScale(3); game.Select(fleet.Id); }),
                ("9-route-planner", () =>
                {
                    game.Camera.Center = Vec2.Zero;
                    game.Camera.FitRadius(2.0 * Physics.MmPerAu);
                    game.Select(fleet.Id);
                    game.Settings.Route = RouteKind.Wait;
                    game.StartPlanning(Key("rust"), Vec2.Zero);
                }),
                ("23-verdant-close", () => { game.Follow(Key("verdant"), false); game.Camera.SetScale(9); }),
                ("24-tamber-close", () => { game.Follow(Key("tamber"), false); game.Camera.SetScale(0.8); }),
                ("25-pale-shadow", () => { game.Follow(Key("pale"), false); game.Camera.SetScale(2.4); }),
                ("26-rust-close", () => { game.Follow(Key("rust"), false); game.Camera.SetScale(17); }),
                ("27-coverage", () => { game.Follow(Key("verdant"), false); game.Camera.SetScale(0.6); game.Sight = MapView.SightView.Coverage; }),
                ("10-rust-orbit", () =>
                {
                    var rival = Sim.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Rival);
                    game.Follow(rival.Id, false);
                    game.Camera.SetScale(2);
                    game.Select(rival.Id);
                }),
            };
            async Task Shoot(string name)
            {
                for (var i = 0; i < 10; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var image = GetViewport().GetTexture().GetImage();
                image.SavePng(System.IO.Path.Combine(dir, name + ".png"));
                Log.Info("game", $"Screenshot {name}");
            }
            foreach (var (name, setup) in views)
            {
                game.Select(null);
                game.Sight = MapView.SightView.Shadows;
                setup();
                game.Camera.SetScale(game.Camera.TargetScale, instant: true);
                await Shoot(name);
            }

            // The Esc game menu over the map.
            game.OpenMenu();
            await Shoot("28-game-menu");
            game.CloseMenu();

            await ShootLightLag(Shoot);
            game = (GameScreen)_screen!;
            fleet = Sim.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player && f.InFlight);

            // A change of course mid-flight: the planner shows the curving turn onto the new heading.
            game.Select(fleet.Id);
            game.Follow(fleet.Id, false);
            game.Camera.FitRadius(0.5 * Physics.MmPerAu, instant: true);
            game.Settings.Route = RouteKind.Direct;
            game.StartPlanning(Key("cinder"), Vec2.Zero);
            game.Replan();
            await Shoot("14-course-change");

            // The same route with waypoints: through one point, then stop and wait at another.
            var from = fleet.Position;
            var to = Sim.World.Bodies.First(b => b.Key == "cinder").Position;
            game.AddWaypoint(from + (to - from) * 0.35 + new Vec2(-(to - from).Y, (to - from).X) * 0.25);
            game.AddWaypoint(from + (to - from) * 0.7);
            game.Planner!.Waypoints[1].Stop = true;
            game.Planner.Waypoints[1].WaitHours = 12;
            game.Planner.Waypoints[1].Speed = 0.3;
            game.Replan();
            game.ShowWholeSystem(); // stops following the fleet
            game.Camera.Center = from + (to - from) * 0.5;
            game.Camera.FitRadius(Vec2.Distance(from, to) * 0.7, instant: true);
            await Shoot("16-waypoints");

            // Sent on its way: the waypoints still ahead stay on the map, and can be dragged to reopen the route.
            game.ConfirmPlan();
            Sim.StepMany(24);
            game.Select(fleet.Id);
            await Shoot("16b-waypoints-flying");
            game.EditRoute();
            await Shoot("16c-change-route");
            game.CancelPlanning();
            if (fleet.Flight?.WaypointPlan.Count > 0)
            {
                // Close up on the first corner: braking into it, then speeding up onto the next stretch.
                game.Camera.Center = fleet.Flight.WaypointPlan[0].Point;
                game.Camera.FitRadius(2500, instant: true);
                await Shoot("16d-route-colours");
            }

            // A split at home: the new fleet sits a little ahead on the orbit; both picked with a box.
            StartNewGame(8);
            var home = (GameScreen)_screen!;
            var homeFleet = Sim!.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player);
            Sim.Issue(new SplitFleetCommand(homeFleet.Id, new List<int> { homeFleet.ShipIds[0] }));
            Sim.Step();
            home.TogglePause();
            home.Follow(Key("verdant"), true);
            home.Camera.SetScale(home.Camera.TargetScale, instant: true);
            home.SelectGroup(Sim.World.Fleets.Where(f => f.Side == Lightlag.Sim.Core.Side.Player).Select(f => f.Id));
            await Shoot("15-split-and-pick");

            // The empire panel a few days in: a frigate building, research under way, a claim on Pale.
            StartNewGame(8);
            var empire = (GameScreen)_screen!;
            Sim!.Issue(new QueueOrderCommand(Lightlag.Sim.Core.Side.Player, OrderKind.Ship, "gun-frigate"));
            Sim.Issue(new QueueOrderCommand(Lightlag.Sim.Core.Side.Player, OrderKind.Ship, "missile-destroyer",
                Sim.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player).Id));
            Sim.Issue(new QueueOrderCommand(Lightlag.Sim.Core.Side.Player, OrderKind.Research, Tech.EfficientDrives));
            Sim.Issue(new QueueOrderCommand(Lightlag.Sim.Core.Side.Player, OrderKind.Research, Tech.EngineMk2));
            Sim.StepMany(3 * 24);
            var pale = Sim.World.Bodies.Single(b => b.Key == "pale");
            pale.ClaimSide = Lightlag.Sim.Core.Side.Player;
            pale.ClaimHours = 4 * 24;
            empire.TogglePause();
            empire.ShowEmpire = true;
            empire.ShowWholeSystem(); // stop following, then frame Verdant and Pale
            empire.Camera.Center = Sim.World.Bodies.Single(b => b.Key == "verdant").Position + new Vec2(-150, 0);
            empire.Camera.FitRadius(600, instant: true);
            await Shoot("17-empire");

            // The research screen: the first team at work, two empty slots, idea cards and the discovery web.
            empire.ShowEmpire = false;
            empire.ShowResearch = true;
            await Shoot("17b-research");
            empire.ShowResearch = false;

            // The planet list: pick one to fly there and follow it.
            empire.ShowEmpire = false;
            empire.PickBody(Sim.World.Bodies.Single(b => b.Key == "tamber").Id);
            empire.Camera.SetScale(empire.Camera.TargetScale, instant: true);
            empire.ShowBodyMenu(true);
            await Shoot("19-planet-list");
            empire.ShowBodyMenu(false);

            // The planet screen: the home world's people, happiness, food and buildings.
            empire.PickBody(Sim.World.Bodies.Single(b => b.Key == "verdant").Id);
            empire.Camera.SetScale(empire.Camera.TargetScale, instant: true);
            await Shoot("19b-planet-screen");

            // Colonies: the planets list, then Verdant's planet screen with a mine being placed.
            empire.Select(null);
            empire.ShowPlanets = true;
            await Shoot("19c-planets-screen");
            empire.PlanetScreenId = Sim.World.Bodies.Single(b => b.Key == "verdant").Id;
            await Shoot("19d-colony-board");
            empire.PlanetScreenId = null;
            empire.ShowPlanets = false;

            // The end of a game: the rival shipyard is gone.
            var rust = Sim.World.Bodies.Single(b => b.Key == "rust");
            rust.Shipyard = false;
            rust.ShipyardArmour = 0;
            Sim.Step();
            empire.ShowEmpire = false;
            empire.OpenEnd();
            await Shoot("18-victory");

            // The rival's thinking (F7): its fleets' jobs on the map and its mind panel, two months in.
            StartNewGame(9);
            var minds = (GameScreen)_screen!;
            Sim!.StepMany(60 * GameTime.TicksPerDay);
            minds.TogglePause();
            minds.ToggleRevealAll();
            minds.ShowWholeSystem();
            minds.Camera.SetScale(minds.Camera.TargetScale, instant: true);
            await Shoot("20-rival-mind");

            // Player information, two months in: the cleaned-up fleet panel, a ship card, How am I doing,
            // the intelligence screen and the key list.
            minds.ToggleRevealAll();
            var own = Sim.World.Fleets.Where(f => f.Side == Lightlag.Sim.Core.Side.Player).OrderByDescending(f => f.ShipIds.Count).First();
            minds.Select(own.Id);
            minds.Follow(own.Id, false);
            minds.Camera.SetScale(0.05, instant: true);
            PlayerInfo.SetOpen("Ships", true);
            await Shoot("50-fleet-panel");
            PlayerInfo.SetOpen("Orders", true);
            PlayerInfo.SetOpen("Battle rules", true);
            await Shoot("50b-fleet-panel-open");
            PlayerInfo.SetOpen("Orders", false);
            PlayerInfo.SetOpen("Battle rules", false);
            PlayerInfo.SetOpen("Ships", false);
            minds.ShipCardId = own.ShipIds[0];
            await Shoot("51-ship-card");
            minds.ShipCardId = null;
            minds.Select(null);
            minds.ShowProgress = true;
            await Shoot("52-how-am-i-doing");
            minds.ShowProgress = false;
            // Screenshot only: show the file as it looks after some contact (a look at its fleets and a battle).
            foreach (var rf in Sim.World.Fleets.Where(f => f.Side == Lightlag.Sim.Core.Side.Rival).ToList()) Sensors.Reveal(Sim.World, Lightlag.Sim.Core.Side.Player, rf);
            Intel.Update(Sim);
            Intel.Note(Sim.World, Lightlag.Sim.Core.Side.Player, IntelSource.Battle, "Screenshot: railguns seen in a skirmish", 45, "railguns");
            minds.ShowIntel = true;
            await Shoot("53-intelligence");
            minds.ShowIntel = false;
            minds.ShowHelp = true;
            await Shoot("54-keys");
            minds.ShowHelp = false;

            // The fleets screen (K): the biggest fleet given a role, with a call for help from home about a rival fleet.
            Sim.Issue(new SetFleetRoleCommand(own.Id, FleetRole.Support));
            Sim.ApplyPending();
            if (Economy.HomeOf(Sim.World, Lightlag.Sim.Core.Side.Player) is Body homeWorld && Sim.World.Sightings.FirstOrDefault(s => s.SeenBy == Lightlag.Sim.Core.Side.Player && !s.Own) is Sighting seen)
            {
                // Screenshot only: pretend the rival fleet was just seen by the home planet.
                Sim.World.HelpCalls.Add(new HelpCall { Id = Sim.World.NextId++, Side = Lightlag.Sim.Core.Side.Player, Reasons = CallReason.EnemyNear, BodyId = homeWorld.Id,
                    EnemyFleetId = seen.FleetId, EnemyName = seen.Name, EnemyShips = seen.Ships, Position = homeWorld.Position, RaisedHour = Sim.World.Hour, LastHour = Sim.World.Hour,
                    Text = $"{homeWorld.Name} sees {seen.Name} ({seen.Ships} ships) close by" });
            }
            minds.FleetScreenId = own.Id;
            minds.ShowFleets = true;
            await Shoot("55-fleet-screen");
            minds.ShowFleets = false;

            // The battle sandbox: an attack run, mid-battle with missiles flying, then the combat report.
            StartSandbox(new SandboxSetup(3, 1, 2, 2, DistanceMm: 150), 7);
            var battle = (GameScreen)_screen!;
            var mine = Sim!.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player);
            var theirs = Sim.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Rival);
            battle.Settings.Route = RouteKind.Direct;
            battle.StartPlanning(null, theirs.Position, theirs.Id);
            await Shoot("11-attack-plan");
            battle.ConfirmPlan();
            for (var i = 0; i < 20000 && Sim.World.Missiles.Count < 4; i++) Sim.Step();
            battle.Select(mine.Id);
            battle.FocusSandbox();
            battle.Camera.SetScale(battle.Camera.TargetScale, instant: true);
            await Shoot("12-battle");
            battle.OpenEngagement(mine.Id, theirs.Id);
            battle.Camera.SetScale(battle.Camera.TargetScale, instant: true);
            await Shoot("21-engagement");
            // Closer in, the railguns: spread fans from your guns, aimed fire coming back.
            Sim.Issue(new SetFireModeCommand(mine.Id, FireMode.Spread));
            for (var i = 0; i < 20000 && !Sim.Effects.Any(e => e.Kind == EffectKind.Volley && e.Hour >= Sim.World.Hour - 1e-9); i++) Sim.Step();
            battle.OpenEngagement(mine.Id, theirs.Id);
            battle.Camera.FitRadius(15, instant: true);
            await Shoot("22-railgun-fans");
            battle.CloseEngagement();
            var end = Sim.World.Tick + 72;
            while ((Sim.World.ActiveBattle != null || Sim.World.Sub != 0) && Sim.World.Tick < end) Sim.Step();
            battle.ShowLastReport();
            await Shoot("13-combat-report");
            // The replay of that battle, a third of the way in, with its moments listed.
            battle.CloseReport();
            var fought = Sim.World.Battles[^1];
            battle.OpenReplay(fought, fought.StartHour + ((fought.EndHour ?? Sim.World.Hour) - fought.StartHour) / 3);
            await Shoot("40-battle-replay");
            battle.CloseReplay();
            await ShootVisuals(Shoot);
        }
        catch (Exception ex)
        {
            ok = false;
            Crash(ex, "taking screenshots");
        }
        GD.Print(ok ? "SCREENSHOTS DONE" : "SCREENSHOTS FAILED");
        Quit(ok ? 0 : 1);
    }

    /// <summary>
    /// The visual upgrades: battle effects close up, ship damage art and wrecks, the map with the plain
    /// (Low) look for comparison, and the title screen over a live computer-v-computer game.
    /// </summary>
    private async Task ShootVisuals(Func<string, Task> shoot)
    {
        // Mid-battle close up: tracers, flashes, debris, missile smoke and engine plumes.
        StartSandbox(new SandboxSetup(3, 1, 2, 2, DistanceMm: 60), 11);
        var battle = (GameScreen)_screen!;
        var mine = Sim!.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player);
        var theirs = Sim.World.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Rival);
        battle.Settings.Route = RouteKind.Direct;
        battle.StartPlanning(null, theirs.Position, theirs.Id);
        battle.ConfirmPlan();
        var hits = 0;
        for (var i = 0; i < 40000 && hits < 3; i++)
        {
            battle.Map.BeforeStep();
            Sim.Step();
            hits = Sim.Effects.Count(e => e.Kind is EffectKind.RailHit or EffectKind.MissileHit && Sim.World.Hour - e.Hour < 0.4 / 60);
        }
        battle.SnapToNow();
        battle.Select(null);
        battle.Camera.Center = (mine.Position + theirs.Position) * 0.5;
        battle.Camera.FitRadius(Math.Max(8, Vec2.Distance(mine.Position, theirs.Position) * 0.6), instant: true);
        await shoot("41-battle-effects");

        // Damage art close up: one ship hit (a railgun out, hull worn), one crippled, and a wreck nearby.
        StartSandbox(new SandboxSetup(3, 1, 2, 2, DistanceMm: 400), 12);
        var hurt = (GameScreen)_screen!;
        var w = Sim!.World;
        var fleet = w.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player);
        var ships = w.ShipsOf(fleet).ToList();
        ships[0].Armour = Tech.MaxArmour(w, ships[0]) * 0.45;
        ships[0].Weapons = 0.2;
        ships[0].Knocked.Add(Module.Railguns);
        if (ships.Count > 1)
        {
            ships[1].Crippled = true;
            ships[1].Armour = 0;
            ships[1].Engines = 0.1;
        }
        w.Wrecks.Add(new Wreck { Id = w.NextId++, Side = Lightlag.Sim.Core.Side.Rival, ClassId = "gun-frigate", Name = "RF-9", Position = fleet.Position + new Vec2(5, 2.5), Hour = w.Hour });
        hurt.SnapToNow();
        hurt.Camera.Center = fleet.Position + new Vec2(3, 1);
        hurt.Camera.SetScale(70, instant: true);
        await shoot("42-damage-close");
        hurt.Camera.SetScale(4, instant: true);
        await shoot("43-damage-map");

        // Lanes and a raiding zone: two rival runs spotted (one a regular run), your own run, and a fleet
        // hunting on the regular one, with its zone panel open and the lane's sightings shown.
        RandomStartsChoice = false;
        StartNewGame(7);
        var lanes = (GameScreen)_screen!;
        w = Sim!.World;
        Sim.StepMany(30 * GameTime.TicksPerDay);
        lanes.TogglePause();
        int Body(string key) => w.Bodies.First(b => b.Key == key).Id;
        var rustId = Body("rust");
        var regular = new Lane { Id = w.NextId++, SeenBy = Lightlag.Sim.Core.Side.Player, FromBodyId = rustId, ToBodyId = Body("kesh"), Seen = 4,
            FirstHour = w.Hour - 22 * 24, LastHour = w.Hour - 30, Speed = 40 };
        for (var i = 0; i < 4; i++)
        {
            var hour = w.Hour - 30 - (3 - i) * 6 * 24;
            var a = FlightPlanner.PredictBody(w, w.FindBody(rustId)!, hour);
            var b = FlightPlanner.PredictBody(w, w.FindBody(Body("kesh"))!, hour);
            regular.Recent.Add(new LaneSighting(hour, a + (b - a) * (0.2 + 0.2 * i)));
        }
        w.Lanes.Add(regular);
        w.Lanes.Add(new Lane { Id = w.NextId++, SeenBy = Lightlag.Sim.Core.Side.Player, FromBodyId = rustId, ToBodyId = Body("orrin"), Seen = 2,
            FirstHour = w.Hour - 9 * 24, LastHour = w.Hour - 4 * 24, Speed = 30 });
        var hunter = w.Fleets.First(f => f.Side == Lightlag.Sim.Core.Side.Player);
        Sim.Issue(new HuntCommand(hunter.Id, rustId, Body("kesh")));
        Sim.ApplyPending();
        lanes.SnapToNow();
        lanes.Select(hunter.Id);
        var mid = (w.FindBody(rustId)!.Position + w.FindBody(Body("kesh"))!.Position) * 0.5;
        lanes.Camera.Center = mid;
        lanes.Camera.FitRadius(Vec2.Distance(w.FindBody(rustId)!.Position, w.FindBody(Body("kesh"))!.Position) * 0.75, instant: true);
        lanes.Map.UpdatePositions();
        Input.WarpMouse(lanes.Camera.ToScreen(w.FindBody(rustId)!.Position).Lerp(lanes.Camera.ToScreen(w.FindBody(Body("kesh"))!.Position), 0.7f));
        await shoot("46-lanes");

        // The same inner system on Low (the original look), then back.
        var level = Graphics.Level;
        Graphics.Set(this, GraphicsLevel.Low);
        StartNewGame(7);
        ((GameScreen)_screen!).TogglePause();
        await shoot("44-low-graphics");
        Graphics.Set(this, level);

        // The title screen, drifting over a live game.
        ShowTitle();
        for (var i = 0; i < 30; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await shoot("45-title");
    }

    /// <summary>
    /// CI runs the exported game with --smoke-test: play 30 game days, write an F8-style report,
    /// check the files exist, then quit with exit code 0 (ok) or 1 (failed).
    /// </summary>
    private void RunSmokeTest()
    {
        var ok = RunGuarded("smoke test", () =>
        {
            StartNewGame(12345);
            Sim!.RuleCheckEveryTicks = 1;
            Sim.StrictRules = true;
            var pilot = new TestPilot(12345);
            for (var i = 0; i < 30 * GameTime.TicksPerDay; i++) { pilot.Act(Sim); Sim.Step(); }
            var dir = Reports.WritePlayerReport(Paths, Log, Sim, "smoke test", "smoke");
            foreach (var f in new[] { "README.md", "save.json", "replay.json", "log-tail.jsonl", "stats.csv", "rival-mind.md" })
                if (!File.Exists(System.IO.Path.Combine(dir, f))) throw new FileNotFoundException($"smoke test report is missing {f}");
            Log.Info("game", "Smoke test passed", new { day = Sim.World.Day, hash = Sim.StateHash() });
        });
        GD.Print(ok ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");
        Quit(ok ? 0 : 1);
    }

    // ---- Startup helpers -------------------------------------------------------------------

    /// <summary>The data folder: --data-dir=PATH (the launcher passes it), else Godot's user folder.</summary>
    private static string ResolveDataDir()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
            if (arg.StartsWith("--data-dir="))
                return arg["--data-dir=".Length..].Trim('"');
        return System.IO.Path.Combine(OS.GetUserDataDir(), "data");
    }

    /// <summary>CI writes build_info.json into the game and next to the .exe.</summary>
    private static BuildInfo LoadBuild()
    {
        try
        {
            if (FileAccess.FileExists("res://build_info.json"))
                return BuildInfo.FromJson(FileAccess.GetFileAsString("res://build_info.json"));
        }
        catch (Exception) { }
        return BuildInfo.Load(System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? ".");
    }
}

/// <summary>One message in the News menu: when, what, and where clicking it goes (if anywhere).</summary>
public sealed record NewsItem(double Hour, string Text, Action? Click, bool Keep = false);
