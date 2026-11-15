using Godot;

namespace Lightlag.Game;

/// <summary>
/// Polygon drawing that skips shapes Godot cannot fill (points on one line, all in one spot, or not finite), which
/// otherwise log "Invalid polygon data, triangulation failed" every frame they are drawn. A shape that crosses
/// itself passes the area check but still can't be filled: these triangulate once here and draw the triangles
/// (what Godot's own polygon drawing does inside), skipping the shape if that fails.
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

    public static void FillPolygon(this CanvasItem item, Vector2[] pts, Color color) => Fill(item, pts, new[] { color });

    public static void FillPolygon(this CanvasItem item, Vector2[] pts, Color[] colors) => Fill(item, pts, colors);

    private static void Fill(CanvasItem item, Vector2[] pts, Color[] colors)
    {
        if (!Fillable(pts)) return;
        var indices = Geometry2D.TriangulatePolygon(pts);
        if (indices.Length < 3) return; // crosses itself: nothing sensible to fill
        // One colour for the lot, or one per point (as DrawPolygon takes them).
        var cols = colors.Length == pts.Length ? colors : Enumerable.Repeat(colors.Length > 0 ? colors[0] : Colors.White, pts.Length).ToArray();
        RenderingServer.CanvasItemAddTriangleArray(item.GetCanvasItem(), indices, pts, cols);
    }
}
