using Godot;
using Lightlag.Sim.Core;

namespace Lightlag.Game;

/// <summary>
/// The star on Medium and High: a boiling disc, a churning corona and glare streaks (sun.gdshader), added
/// onto what is behind it. Sits behind the map's own drawing like the planets do.
/// </summary>
public partial class SunSprite : Node2D
{
    private static Shader? _shader;
    private readonly ShaderMaterial _material = new();
    private float _half;

    public static SunSprite For(Body star)
    {
        _shader ??= GD.Load<Shader>("res://Shaders/sun.gdshader");
        var s = new SunSprite { ShowBehindParent = true, Name = "Sun" };
        s._material.Shader = _shader;
        s.Material = s._material;
        s._material.SetShaderParameter("core_color", new Color(star.Color));
        s._material.SetShaderParameter("seed", (float)(star.Id * 0.37 % 1.0));
        return s;
    }

    /// <summary>Place it: where, and the star's radius on screen.</summary>
    public void Set(Vector2 at, float radiusPx)
    {
        Position = at;
        // The corona reaches a few radii out, and never less than a couple of hundred pixels so a far-off
        // sun still has its glare; zoomed right in the glare would fill the screen, so it fades.
        var half = Mathf.Min(Mathf.Max(radiusPx * 5f, radiusPx + 260f), radiusPx * 2.2f + 2400f);
        _material.SetShaderParameter("radius_px", radiusPx);
        _material.SetShaderParameter("half_px", half);
        _material.SetShaderParameter("boost", Graphics.Bloom ? 2.2f : 1.0f);
        _material.SetShaderParameter("glare", Mathf.Clamp(1.4f - radiusPx / 600f, 0.15f, 1f));
        if (!Mathf.IsEqualApprox(half, _half))
        {
            _half = half;
            QueueRedraw();
        }
    }

    public override void _Draw() => DrawRect(new Rect2(-_half, -_half, _half * 2, _half * 2), Colors.White);
}
