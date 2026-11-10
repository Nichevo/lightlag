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
    public Simulation? Sim { get; set; }

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

    public void StartNewGame(ulong? seed = null, Difficulty difficulty = Difficulty.Normal)
    {
        var s = seed ?? (ulong)Random.Shared.NextInt64(1, 1_000_000_000);
        RunGuarded("starting a new game", () =>
        {
            Sim = Simulation.NewGame(Scenarios.TestSystem, s, Log, Build, options: GameOptions.VsRival(difficulty));
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
    /// CI runs the game under a virtual screen with --screenshots=DIR to photograph a set of map views,
    /// so changes to how the map looks can be checked without a person at the PC.
    /// </summary>
    private async Task TakeScreenshots(string dir)
    {
        var ok = true;
        try
        {
            Directory.CreateDirectory(dir);
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
                for (var i = 0; i < 4; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                var image = GetViewport().GetTexture().GetImage();
                image.SavePng(System.IO.Path.Combine(dir, name + ".png"));
                Log.Info("game", $"Screenshot {name}");
            }
            foreach (var (name, setup) in views)
            {
                game.Select(null);
                setup();
                game.Camera.SetScale(game.Camera.TargetScale, instant: true);
                await Shoot(name);
            }

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
            Sim.Issue(new QueueOrderCommand(Lightlag.Sim.Core.Side.Player, OrderKind.Research, Tech.QuietDrives));
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

            // The planet list: pick one to fly there and follow it.
            empire.ShowEmpire = false;
            empire.PickBody(Sim.World.Bodies.Single(b => b.Key == "tamber").Id);
            empire.Camera.SetScale(empire.Camera.TargetScale, instant: true);
            empire.ShowBodyMenu(true);
            await Shoot("19-planet-list");
            empire.ShowBodyMenu(false);

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
