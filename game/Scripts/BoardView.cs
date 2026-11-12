using Godot;
using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// A planet's build board: tiles (land, ocean, ice, mountains), mineral deposits, the buildings as shaped
/// pieces, and bonus links between pieces that help each other. While placing, a ghost of the piece follows
/// the mouse (green where it fits, red where not); R turns it, left click places, right click cancels.
/// </summary>
public partial class BoardView : Control
{
    public Body? Body { get; set; }
    public World? World { get; set; }
    public BuildingData? Placing { get; set; }
    public bool Turned { get; set; }
    public int? SelectedPieceId { get; set; }
    /// <summary>A good hovered in storage: buildings that make it get a green outline, those that use it amber.</summary>
    public string? HighlightGood { get; set; }

    /// <summary>Place the piece being placed at (x, y), turned or not.</summary>
    public Action<int, int, bool>? OnPlace { get; set; }
    /// <summary>A piece was clicked (null: empty ground).</summary>
    public Action<int?>? OnSelect { get; set; }
    public Action? OnCancel { get; set; }

    /// <summary>Why the ghost can't go where it is (null: it fits, or nothing is being placed).</summary>
    public string? Problem { get; private set; }
    private (int X, int Y)? _hover;

    public static readonly Dictionary<string, Color> AreaColors = new()
    {
        ["food"] = new Color("6fcf6a"), ["power"] = new Color("ffd84d"), ["defence"] = new Color("ff7b6b"), ["shipyard"] = new Color("3fd0ff"),
        ["industry"] = new Color("ffa94d"), ["research"] = new Color("b48cff"), ["housing"] = new Color("9fc3e8"),
    };

    public static readonly Dictionary<string, Color> MineralColors = new()
    {
        ["iron"] = new Color("c4703a"), ["copper"] = new Color("e6a15c"), ["rare-earths"] = new Color("c79bff"),
        ["water"] = new Color("6cc4ff"), ["uranium"] = new Color("a8ff5c"), ["helium-3"] = new Color("ffe066"),
    };

    private static Color Tile(char c) => c switch
    {
        'o' => new Color(0.10f, 0.22f, 0.40f),
        'i' => new Color(0.72f, 0.82f, 0.90f),
        'm' => new Color(0.30f, 0.29f, 0.32f),
        _ => new Color(0.33f, 0.29f, 0.21f),
    };

    public BoardView() => MouseFilter = MouseFilterEnum.Stop;

    private float Cell => Body?.Board is Board b ? Mathf.Floor(Mathf.Min(Size.X / b.W, Size.Y / b.H)) : 32;

    private (int X, int Y)? CellAt(Vector2 at)
    {
        if (Body?.Board is not Board b) return null;
        var c = Cell;
        int x = (int)Mathf.Floor(at.X / c), y = (int)Mathf.Floor(at.Y / c);
        return x >= 0 && y >= 0 && x < b.W && y < b.H ? (x, y) : null;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion m)
        {
            _hover = CellAt(m.Position);
            QueueRedraw();
        }
        else if (e is InputEventMouseButton { Pressed: true } click)
        {
            var cell = CellAt(click.Position);
            if (click.ButtonIndex == MouseButton.Right)
            {
                if (Placing != null) OnCancel?.Invoke();
                else OnSelect?.Invoke(null);
                AcceptEvent();
            }
            else if (click.ButtonIndex == MouseButton.Left && cell is var (x, y))
            {
                if (Placing != null)
                {
                    if (Problem == null) OnPlace?.Invoke(x, y, Turned);
                }
                else OnSelect?.Invoke(Body?.Pieces.FirstOrDefault(p => p.Covers(x, y))?.Id);
                AcceptEvent();
            }
        }
    }

    public override void _Input(InputEvent e)
    {
        // R turns the piece being placed (ahead of the map's own keys, where R opens the last battle report).
        if (Placing != null && IsVisibleInTree() && e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.R })
        {
            Turned = !Turned;
            QueueRedraw();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Draw()
    {
        Problem = null;
        if (Body?.Board is not Board board || World == null) return;
        var c = Cell;
        var font = ThemeDB.FallbackFont;
        for (var y = 0; y < board.H; y++)
        for (var x = 0; x < board.W; x++)
        {
            var rect = new Rect2(x * c, y * c, c, c);
            var t = board.At(x, y);
            DrawRect(rect, Tile(t));
            if (t == 'm')
                for (var k = 0.25f; k < 1; k += 0.25f)
                    DrawLine(new Vector2(x * c + k * c, y * c + c * 0.8f), new Vector2(x * c + k * c + c * 0.12f, y * c + c * 0.35f), new Color(0.5f, 0.48f, 0.5f), 2);
            DrawRect(rect, new Color(0, 0, 0, 0.35f), false, 1);
        }
        foreach (var d in board.Deposits)
        {
            var centre = new Vector2((d.X + 0.5f) * c, (d.Y + 0.5f) * c);
            var colour = MineralColors.GetValueOrDefault(d.Mineral, Colors.White);
            var r = c * (d.Rich ? 0.22f : 0.16f);
            DrawColoredPolygon(new[] { centre + new Vector2(0, -r), centre + new Vector2(r, 0), centre + new Vector2(0, r), centre + new Vector2(-r, 0) }, colour);
            var label = (Colonies.Goods.Find(g => g.Id == d.Mineral)?.Short ?? d.Mineral) + (d.Rich ? "+" : "");
            DrawString(font, new Vector2(d.X * c + 3, (d.Y + 1) * c - 4), label, HorizontalAlignment.Left, c - 4, 11, colour);
        }
        foreach (var p in Body.Pieces) DrawPiece(p, c, font);
        // Bonus links: a dot on the shared edge of two pieces that help each other.
        foreach (var (a, b) in Colonies.Links(Body))
        {
            var ca = new Vector2((a.X + a.W / 2f) * c, (a.Y + a.H / 2f) * c);
            var cb = new Vector2((b.X + b.W / 2f) * c, (b.Y + b.H / 2f) * c);
            DrawCircle((ca + cb) / 2, 5, a.Built && b.Built ? new Color("7dffb0") : new Color(0.5f, 0.8f, 0.6f, 0.5f));
        }
        if (Placing != null && _hover is var (hx, hy))
        {
            var (pw, ph) = Turned ? (Placing.H, Placing.W) : (Placing.W, Placing.H);
            Problem = Colonies.CanPlace(World, Side.Player, Body.Id, Placing.Id, hx, hy, Turned, queue: true);
            var ok = Problem == null;
            var rect = new Rect2(hx * c + 2, hy * c + 2, pw * c - 4, ph * c - 4);
            DrawRect(rect, new Color(ok ? new Color("7dffb0") : Ui.Danger, 0.3f));
            DrawRect(rect, ok ? new Color("7dffb0") : Ui.Danger, false, 2);
            if (ok)
            {
                var probe = new Piece { Kind = Placing.Id, X = hx, Y = hy, W = pw, H = ph };
                foreach (var o in Body.Pieces.Where(o => o.Built && Placing.Bonus?.Any(bn => bn.Next == o.Kind) == true && Colonies.Touch(o, probe)))
                    DrawCircle(new Vector2((o.X + o.W / 2f) * c, (o.Y + o.H / 2f) * c), 6, new Color("7dffb0"));
            }
        }
    }

    /// <summary>
    /// One building from its drawing (game/Art/Buildings, top-down, area colour as a strip on the south
    /// edge), in its state: see-through with scaffolding while being built, greyed with a bolt without
    /// power or staff, scorched (and burning when badly hurt) when damaged. Its name always shows on a dark
    /// strip at the top, so it is easy to tell what it is at any size. Falls back to a coloured block.
    /// </summary>
    private void DrawPiece(Piece p, float c, Font font)
    {
        var data = Planets.BuildingKind(p.Kind);
        var colour = AreaColors.GetValueOrDefault(data?.Area ?? "", Colors.White);
        var rect = new Rect2(p.X * c + 3, p.Y * c + 3, p.W * c - 6, p.H * c - 6);
        var tex = Art.Texture("Buildings", p.Kind, 2f);
        var idle = p.Built && (!p.Powered || p.Staff < 0.5);
        if (tex != null)
        {
            var tint = !p.Built ? new Color(1, 1, 1, 0.45f) : idle ? new Color(0.5f, 0.5f, 0.55f) : Colors.White;
            // Turned pieces lie the other way: draw the picture turned a quarter.
            var turned = data != null && data.W != data.H && p.W == data.H && p.H == data.W;
            if (turned)
            {
                var full = new Rect2(p.X * c, p.Y * c, p.W * c, p.H * c);
                DrawSetTransform(full.GetCenter(), Mathf.Pi / 2, Vector2.One);
                DrawTextureRect(tex, new Rect2(-rect.Size.Y / 2, -rect.Size.X / 2, rect.Size.Y, rect.Size.X), false, tint);
                DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            }
            else DrawTextureRect(tex, rect, false, tint);
            if (!p.Built)
            {
                // Scaffolding: thin diagonal struts over the see-through drawing.
                var step = Mathf.Max(8f, c * 0.25f);
                for (var k = -rect.Size.Y; k < rect.Size.X; k += step)
                {
                    var a = rect.Position + new Vector2(Mathf.Max(0, k), Mathf.Max(0, -k));
                    var len = Mathf.Min(rect.Size.X - Mathf.Max(0, k), rect.Size.Y - Mathf.Max(0, -k));
                    if (len > 0) DrawLine(a, a + new Vector2(len, len), new Color(0.85f, 0.75f, 0.45f, 0.45f), 1);
                }
                DrawRect(rect, new Color(0.85f, 0.75f, 0.45f, 0.6f), false, 1);
            }
            if (idle)
            {
                // A lightning bolt: no power (or too few people to run it).
                var m = rect.GetCenter() + new Vector2(0, 6);
                var r = Mathf.Clamp(c * 0.18f, 6, 14);
                var bolt = new[] { m + new Vector2(r * 0.2f, -r), m + new Vector2(-r * 0.45f, r * 0.1f), m + new Vector2(0, r * 0.1f), m + new Vector2(-r * 0.2f, r), m + new Vector2(r * 0.45f, -r * 0.1f), m + new Vector2(0, -r * 0.1f) };
                DrawColoredPolygon(bolt, !p.Powered ? new Color("ffd84d") : new Color(0.8f, 0.85f, 0.95f));
            }
            if (p.Built && p.Condition < 0.75)
            {
                // Scorch marks, more as it wears; a flickering fire when badly hurt.
                var marks = (int)Math.Round((1 - p.Condition) * 6);
                for (var i = 0; i < marks; i++)
                {
                    var at = rect.Position + new Vector2((float)Hash(p.Id, i) * rect.Size.X, (float)Hash(p.Id, i + 9) * rect.Size.Y * 0.8f);
                    var r = c * (0.08f + 0.08f * (float)Hash(p.Id, i + 20));
                    DrawTextureRect(Graphics.SoftDot, new Rect2(at - Vector2.One * r, Vector2.One * r * 2), false, new Color(0.05f, 0.04f, 0.03f, 0.8f));
                }
                if (p.Condition < 0.4)
                {
                    var at = rect.Position + new Vector2((float)Hash(p.Id, 50) * rect.Size.X * 0.8f + rect.Size.X * 0.1f, rect.Size.Y * 0.55f);
                    var flick = 0.75f + 0.25f * Mathf.Sin(Time.GetTicksMsec() / 80f + p.Id);
                    var r = c * 0.14f * flick;
                    DrawTextureRect(Graphics.SoftDot, new Rect2(at - Vector2.One * r * 1.6f, Vector2.One * r * 3.2f), false, new Color(1f, 0.45f, 0.1f, 0.75f));
                    DrawTextureRect(Graphics.SoftDot, new Rect2(at - Vector2.One * r * 0.6f, Vector2.One * r * 1.2f), false, new Color(1f, 0.9f, 0.5f, 0.95f));
                    QueueRedraw(); // keep the fire moving
                }
            }
        }
        else
        {
            DrawRect(rect, new Color(colour, p.Built ? 0.55f : 0.2f));
        }
        if (HighlightGood is string good && Body != null)
        {
            if (Colonies.PieceMakes(Body, p, good)) DrawRect(rect.Grow(2), ColonyScreen.MakesColor, false, 3);
            else if (Colonies.PieceUses(Body, p, good)) DrawRect(rect.Grow(2), ColonyScreen.UsesColor, false, 3);
        }
        if (p.Id == SelectedPieceId) DrawRect(rect.Grow(1), Colors.White, false, 3);
        else if (tex == null) DrawRect(rect, colour, false, 1.5f);

        // The name, always: a dark strip along the top with the name (and its state) in small text.
        var name = data?.Name ?? p.Kind;
        var sub = p.Waiting ? "queued"
            : !p.Built ? $"building, {p.HoursLeft / 24:0.#} d"
            : p.RefitHoursLeft > 0 ? $"refit, {p.RefitHoursLeft / 24:0.#} d"
            : !p.Powered ? "NO POWER"
            : p.Staff < 0.999 ? $"staff {p.Staff:P0}"
            : p.Level > 0 ? $"level {p.Level + 1}" : "";
        var size = c >= 40 ? 11 : 10;
        var lines = sub != "" && rect.Size.Y >= 40 ? 2 : 1;
        var strip = new Rect2(rect.Position, new Vector2(rect.Size.X, lines * (size + 3) + 2));
        DrawRect(strip, new Color(0.02f, 0.03f, 0.05f, 0.72f));
        var first = lines == 1 && sub != "" ? $"{name} · {sub}" : name;
        // Shrink the name a little to fit narrow buildings before it gets cut off.
        var nameSize = size;
        while (nameSize > 8 && font.GetStringSize(first, HorizontalAlignment.Left, -1, nameSize).X > rect.Size.X - 6) nameSize--;
        DrawString(font, strip.Position + new Vector2(3, size + 1), first, HorizontalAlignment.Left, rect.Size.X - 6, nameSize, Colors.White);
        if (lines == 2)
            DrawString(font, strip.Position + new Vector2(3, 2 * size + 4), sub, HorizontalAlignment.Left, rect.Size.X - 6, size - 1,
                !p.Powered && p.Built ? Ui.Danger : new Color(1, 1, 1, 0.8f));
        if (p.Built && p.Condition < 0.999)
        {
            var bar = new Rect2(rect.Position.X + 3, rect.End.Y - 7, (rect.Size.X - 6) * (float)p.Condition, 3);
            DrawRect(bar, p.Condition < 0.5 ? Ui.Danger : Ui.Rival);
        }
    }

    private static double Hash(int n, int k)
    {
        unchecked
        {
            var x = (uint)(n * 73856093 ^ k * 19349663) * 0x9E3779B1u;
            x ^= x >> 15; x *= 0x85EBCA77u; x ^= x >> 13;
            return x / 4294967296.0;
        }
    }
}
