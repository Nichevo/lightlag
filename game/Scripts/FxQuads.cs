using Godot;

namespace Lightlag.Game;

/// <summary>
/// A batch of shader quads drawn in one go (engine plumes): the map fills the list each frame, and each
/// quad carries its own numbers in its vertex colour (see plume.gdshader). Sits behind the map's drawing.
/// </summary>
public partial class FxQuads : Node2D
{
    private readonly ShaderMaterial _material = new();
    private readonly List<(Vector2 At, Vector2 Dir, float Length, float Width, Color Data)> _quads = new();
    private float _boost = -1;

    public static FxQuads For(string shaderPath)
    {
        var q = new FxQuads { ShowBehindParent = true, Name = "FxQuads" };
        q._material.Shader = GD.Load<Shader>(shaderPath);
        q.Material = q._material;
        return q;
    }

    public void Clear() => _quads.Clear();

    /// <summary>A quad starting at <paramref name="at"/> running <paramref name="length"/> pixels along <paramref name="dir"/>.</summary>
    public void Add(Vector2 at, Vector2 dir, float length, float width, Color data) => _quads.Add((at, dir.Normalized(), length, width, data));

    public void Flush(float boost)
    {
        if (!Mathf.IsEqualApprox(boost, _boost))
        {
            _boost = boost;
            _material.SetShaderParameter("boost", boost);
        }
        QueueRedraw();
    }

    private static readonly Vector2[] Uvs = { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };

    public override void _Draw()
    {
        foreach (var (at, dir, length, width, data) in _quads)
        {
            var across = new Vector2(-dir.Y, dir.X) * (width / 2);
            var end = at + dir * length;
            var pts = new[] { at - across, end - across, end + across, at + across };
            DrawPrimitive(pts, new[] { data, data, data, data }, Uvs);
        }
    }
}
