using Godot;

namespace Lightlag.Game;

/// <summary>
/// Polygon drawing that skips shapes Godot cannot fill (points on one line, all in one spot, or not finite), which
/// otherwise log "Invalid polygon data, triangulation failed" every frame they are drawn.
/// </summary>
public static class SafePolygons
{
    public static bool Fillable(Vector2[] pts)
    {
        if (pts.Length < 3) return false;
        var area = 0f;
        for (var i = 0; i < pts.Length; i++)
        {
            var a = pts[i];
            if (!float.IsFinite(a.X) || !float.IsFinite(a.Y)) return false;
            var b = pts[(i + 1) % pts.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) > 0.02f;
    }

    public static void FillPolygon(this CanvasItem item, Vector2[] pts, Color color)
    {
        if (Fillable(pts)) item.DrawColoredPolygon(pts, color);
    }

    public static void FillPolygon(this CanvasItem item, Vector2[] pts, Color[] colors)
    {
        if (Fillable(pts)) item.DrawPolygon(pts, colors);
    }
}
