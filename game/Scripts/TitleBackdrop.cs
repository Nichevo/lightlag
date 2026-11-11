using Godot;
using Lightlag.Sim.Core;
using Lightlag.Sim.Logging;

namespace Lightlag.Game;

/// <summary>
/// Behind the title screen (Medium and High): a live computer-v-computer game in the test system, drawn
/// quietly (no labels, routes or panels) under a slow drifting camera, with the star field behind it.
/// The game is thrown away when the title screen closes; it never touches saves or the game log.
/// </summary>
public partial class TitleBackdrop : Node2D
{
    private Simulation? _sim;
    private MapView? _map;
    private readonly MapCamera _camera = new();
    private double _accum;
    private double _t;
    private bool _failed;

    /// <summary>Game hours per real second.</summary>
    private const double Pace = 2.5;

    public override void _Ready()
    {
        if (!Graphics.Fancy) return;
        try
        {
            var seed = (ulong)Random.Shared.NextInt64(1, 1_000_000_000);
            var log = new GameLog(Main.Instance.Build, null, LogLevel.Warn);
            _sim = Simulation.NewGame(Scenarios.TestSystem, seed, log, Main.Instance.Build,
                options: GameOptions.AiVsAi() with { RandomStarts = true, Light = new LightSetup(false, 1.0) });
            // Start a few weeks in, so fleets are already out and about.
            _sim.StepMany(24 * 24);
            _camera.ScreenSize = GetViewportRect().Size;
            AddChild(Backdrop.For(seed, _camera));
            _map = new MapView { Sim = _sim, Camera = _camera, RevealAll = true, Quiet = true };
            AddChild(_map);
            _t = Random.Shared.NextDouble() * Math.Tau;
            Place(0);
            _map.DrawHour = _sim.World.Hour;
            _map.UpdatePositions();
            // A dark veil so the menu reads over the moving map.
            AddChild(new ColorRect { Color = new Color(0.01f, 0.015f, 0.03f, 0.45f), MouseFilter = Control.MouseFilterEnum.Ignore, Size = _camera.ScreenSize, Name = "Veil" });
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        _failed = true;
        Main.Instance.Log.Warn("ui", "Title backdrop stopped", new { error = ex.Message });
        foreach (var c in GetChildren()) c.QueueFree();
    }

    /// <summary>Drift: a slow circle round the inner system, the zoom breathing in and out.</summary>
    private void Place(double delta)
    {
        _t += delta * 0.012;
        var au = Physics.MmPerAu;
        _camera.Center = new Vec2(Math.Cos(_t) * 0.55 * au, Math.Sin(_t * 0.8) * 0.4 * au);
        _camera.FitRadius((1.6 + 0.35 * Math.Sin(_t * 1.7)) * au, instant: true);
    }

    public override void _Process(double delta)
    {
        if (_failed || _sim == null || _map == null) return;
        try
        {
            _camera.ScreenSize = GetViewportRect().Size;
            if (GetNodeOrNull<ColorRect>("Veil") is ColorRect veil) veil.Size = _camera.ScreenSize;
            _accum += delta * Pace / GameTime.HoursPerTick;
            var steps = 0;
            while (_accum >= 1 && steps < 4)
            {
                _map.BeforeStep();
                _sim.Step();
                _accum -= 1;
                steps++;
            }
            if (steps == 4) _accum = 0;
            _map.Blend = Math.Clamp(_accum, 0, 1);
            _map.DrawHour = _sim.World.Hour - GameTime.HoursPerTick * (1 - _map.Blend);
            _map.TurnShips(delta);
            Place(delta);
            _map.UpdatePositions();
            _map.QueueRedraw();
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }
}
