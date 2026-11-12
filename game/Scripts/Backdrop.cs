using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The sky behind the map (Medium and High): three star layers with parallax, a galaxy band and two
/// nebulae, all from the game's seed (see starfield.gdshader). Each layer drifts by a share of how far
/// the view moves on screen, so panning, or following a moving fleet, shows depth. Low draws nothing,
/// leaving the plain background.
/// </summary>
public partial class Backdrop : ColorRect
{
    private static Shader? _shader;
    private readonly ShaderMaterial _material = new();
    private MapCamera? _camera;
    private Vec2 _lastCenter;
    private double _lastScale;
    private Vector2 _far, _mid, _near;

    /// <summary>Layer drift as a share of the on-screen pan (far, mid, near).</summary>
    private const float FarRate = 0.015f, MidRate = 0.04f, NearRate = 0.09f;

    /// <summary>Most a layer may move in one frame (a jump across the system would otherwise smear).</summary>
    private const float MaxStep = 30f;

    public static Backdrop For(ulong seed, MapCamera? camera)
    {
        _shader ??= GD.Load<Shader>("res://Shaders/starfield.gdshader");
        var b = new Backdrop { _camera = camera, MouseFilter = MouseFilterEnum.Ignore, Name = "Backdrop", Color = Colors.White };
        b._material.Shader = _shader;
        b.Material = b._material;
        b.SetSeed(seed);
        return b;
    }

    private void SetSeed(ulong seed)
    {
        // Small, fixed rolls from the seed: where the band runs and where the nebulae sit, and their colours.
        double R(int n)
        {
            unchecked
            {
                var x = seed + (ulong)n * 0x9E3779B97F4A7C15UL;
                x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
                x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
                x ^= x >> 31;
                return (x >> 11) / (double)(1UL << 53);
            }
        }
        var angle = (float)(R(1) * Math.PI);
        _material.SetShaderParameter("seed", (float)(R(2) * 97.0));
        _material.SetShaderParameter("band_dir", new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)));
        _material.SetShaderParameter("band_offset", (float)((R(3) - 0.5) * 500));
        var palette = new[]
        {
            new Color(0.22f, 0.45f, 0.62f), new Color(0.55f, 0.24f, 0.46f), new Color(0.6f, 0.38f, 0.2f),
            new Color(0.3f, 0.52f, 0.42f), new Color(0.4f, 0.3f, 0.65f),
        };
        var c1 = (int)(R(4) * palette.Length);
        var c2 = (c1 + 1 + (int)(R(5) * (palette.Length - 1))) % palette.Length;
        _material.SetShaderParameter("neb1", new Vector2((float)(R(6) * 1400 + 100), (float)(R(7) * 700 + 100)));
        _material.SetShaderParameter("neb2", new Vector2((float)(R(8) * 1400 + 100), (float)(R(9) * 700 + 100)));
        _material.SetShaderParameter("neb1_color", palette[c1]);
        _material.SetShaderParameter("neb2_color", palette[c2]);
        _material.SetShaderParameter("neb1_size", (float)(300 + R(10) * 250));
        _material.SetShaderParameter("neb2_size", (float)(260 + R(11) * 220));
        var tint = R(12);
        _material.SetShaderParameter("band_tint", tint < 0.5 ? new Color(0.75f, 0.72f, 0.9f) : new Color(0.9f, 0.82f, 0.7f));
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Graphics.Changed += Refresh;
        Refresh();
    }

    public override void _ExitTree() => Graphics.Changed -= Refresh;

    private void Refresh()
    {
        Visible = Graphics.Fancy;
        _material.SetShaderParameter("brightness", Graphics.Bloom ? 1.6f : 1.0f);
    }

    public override void _Process(double delta)
    {
        Size = GetViewportRect().Size;
        _material.SetShaderParameter("ui_scale", Display.Scale);
        if (!Visible || _camera == null) return;
        var center = _camera.Center;
        var scale = _camera.Scale;
        if (_lastScale > 0)
        {
            // How far the view moved on screen this frame (at the old zoom).
            var dx = (float)((center.X - _lastCenter.X) * _lastScale);
            var dy = (float)((center.Y - _lastCenter.Y) * _lastScale);
            var step = new Vector2(dx, dy);
            if (float.IsFinite(step.X) && float.IsFinite(step.Y))
            {
                Vector2 Move(Vector2 at, float rate) => Wrap(at + (step * rate).LimitLength(MaxStep));
                _far = Move(_far, FarRate);
                _mid = Move(_mid, MidRate);
                _near = Move(_near, NearRate);
                _material.SetShaderParameter("pan_far", _far);
                _material.SetShaderParameter("pan_mid", _mid);
                _material.SetShaderParameter("pan_near", _near);
            }
        }
        _lastCenter = center;
        _lastScale = scale;
    }

    /// <summary>Keep offsets small so the shader's float maths stays sharp.</summary>
    private static Vector2 Wrap(Vector2 v) => new(Mathf.PosMod(v.X, 22f * 46f * 110f), Mathf.PosMod(v.Y, 22f * 46f * 110f));
}
