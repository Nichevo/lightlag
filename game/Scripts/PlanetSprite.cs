using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// One planet or moon on the map, drawn by the planet shader as a lit sphere with a surface made from
/// noise (no textures). Sits behind the map's own drawing, so fleets, labels and markers stay on top.
/// On Medium and High it also has moving clouds, city lights on settled worlds, rings on the giants and
/// goes dark in its planet's shadow.
/// </summary>
public partial class PlanetSprite : Node2D
{
    private static Shader? _shader;
    private readonly ShaderMaterial _material = new();
    private float _half;
    private bool _rings;
    private float _eclipse = -1, _city = -1;
    private int _quality = -1;

    public int BodyId { get; private set; }

    public static PlanetSprite For(Body b)
    {
        _shader ??= GD.Load<Shader>("res://Shaders/planet.gdshader");
        var s = new PlanetSprite { BodyId = b.Id, ShowBehindParent = true, Name = $"Planet{b.Id}" };
        s._material.Shader = _shader;
        s.Material = s._material;
        s._material.SetShaderParameter("base_color", new Color(b.Color));
        s._material.SetShaderParameter("atmo_color", b.Atmosphere is string a ? new Color(a) : new Color(0, 0, 0, 0));
        var kind = KindOf(b);
        s._material.SetShaderParameter("kind", kind);
        var seed = (float)((b.Id * 0.6180339887) % 1.0) * 10f;
        s._material.SetShaderParameter("seed", seed);
        // Clouds on worlds with air (thick on the cloudy ones, which are cloud all over already).
        var clouds = b.Atmosphere == null ? 0f : kind switch { 1 => 1f, 2 => 0.25f, 7 => 0.3f, 3 or 8 => 0.35f, 6 => 0.2f, _ => 0f };
        s._material.SetShaderParameter("clouds", clouds);
        s._rings = b.Type is "gas" or "ice-giant";
        if (s._rings)
        {
            // Each giant gets its own ring colour and lean.
            var c = new Color(b.Color);
            s._material.SetShaderParameter("rings", 1f);
            s._material.SetShaderParameter("ring_color", c.Lerp(new Color(0.85f, 0.82f, 0.75f), 0.55f));
            s._material.SetShaderParameter("ring_tilt", 0.85f + 0.3f * (seed % 1f));
            s._material.SetShaderParameter("ring_angle", -0.4f + 0.8f * ((seed * 3.7f) % 1f));
        }
        return s;
    }

    private static int KindOf(Body b) => b.Type switch
    {
        "terran" => 1,
        "desert" => 2,
        "cloudy" => 3,
        "gas" => 4,
        "ice-giant" => 5,
        "ice" => 6,
        "volcanic" => 7,
        "hazy" => 8,
        "asteroid" => 9,
        "rocky" when b.Radius < 0.1 => 9, // tiny moons are lumps of rock
        _ => 0,
    };

    /// <summary>Place it for this frame: where, how big on screen, where the star is, and its spin.</summary>
    public void Set(Vector2 at, float radiusPx, Vector2 towardStar, float spin, float cloudTime = 0)
    {
        Position = at;
        var fancy = Graphics.Fancy;
        var half = radiusPx * (fancy && _rings ? 2.2f : 1.12f) + (fancy ? 6f : 3f);
        _material.SetShaderParameter("radius_px", radiusPx);
        _material.SetShaderParameter("light_dir", towardStar.LengthSquared() > 0 ? towardStar.Normalized() : Vector2.Right);
        _material.SetShaderParameter("spin", spin);
        if (fancy) _material.SetShaderParameter("cloud_time", cloudTime);
        var quality = fancy ? 1 : 0;
        if (quality != _quality)
        {
            _quality = quality;
            _material.SetShaderParameter("quality", quality);
        }
        if (!Mathf.IsEqualApprox(half, _half))
        {
            _half = half;
            QueueRedraw();
        }
    }

    /// <summary>How far into its planet's shadow (0..1) and how bright its city lights are (0..1).</summary>
    public void SetLights(float eclipse, float city)
    {
        if (!Mathf.IsEqualApprox(eclipse, _eclipse)) { _eclipse = eclipse; _material.SetShaderParameter("eclipse", eclipse); }
        if (!Mathf.IsEqualApprox(city, _city)) { _city = city; _material.SetShaderParameter("city", city); }
    }

    public override void _Draw() => DrawRect(new Rect2(-_half, -_half, _half * 2, _half * 2), Colors.White);
}
