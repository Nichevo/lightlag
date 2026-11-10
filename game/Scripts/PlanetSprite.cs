using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// One planet or moon on the map, drawn by the planet shader as a lit sphere with a surface made from
/// noise (no textures). Sits behind the map's own drawing, so fleets, labels and markers stay on top.
/// </summary>
public partial class PlanetSprite : Node2D
{
    private static Shader? _shader;
    private readonly ShaderMaterial _material = new();
    private float _half;

    public int BodyId { get; private set; }

    public static PlanetSprite For(Body b)
    {
        _shader ??= GD.Load<Shader>("res://Shaders/planet.gdshader");
        var s = new PlanetSprite { BodyId = b.Id, ShowBehindParent = true, Name = $"Planet{b.Id}" };
        s._material.Shader = _shader;
        s.Material = s._material;
        s._material.SetShaderParameter("base_color", new Color(b.Color));
        s._material.SetShaderParameter("atmo_color", b.Atmosphere is string a ? new Color(a) : new Color(0, 0, 0, 0));
        s._material.SetShaderParameter("kind", KindOf(b.Type));
        s._material.SetShaderParameter("seed", (float)((b.Id * 0.6180339887) % 1.0) * 10f);
        return s;
    }

    private static int KindOf(string? type) => type switch
    {
        "terran" => 1,
        "desert" => 2,
        "cloudy" => 3,
        "gas" => 4,
        "ice-giant" => 5,
        "ice" => 6,
        "volcanic" => 7,
        "hazy" => 8,
        _ => 0,
    };

    /// <summary>Place it for this frame: where, how big on screen, where the star is, and its spin.</summary>
    public void Set(Vector2 at, float radiusPx, Vector2 towardStar, float spin)
    {
        Position = at;
        var half = radiusPx * 1.12f + 3f;
        _material.SetShaderParameter("radius_px", radiusPx);
        _material.SetShaderParameter("light_dir", towardStar.LengthSquared() > 0 ? towardStar.Normalized() : Vector2.Right);
        _material.SetShaderParameter("spin", spin);
        if (!Mathf.IsEqualApprox(half, _half))
        {
            _half = half;
            QueueRedraw();
        }
    }

    public override void _Draw() => DrawRect(new Rect2(-_half, -_half, _half * 2, _half * 2), Colors.White);
}
