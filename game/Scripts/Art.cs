using Godot;

namespace Lightlag.Game;

/// <summary>
/// The approved drawings (game/Art, SVG), built into the game and turned into textures on first use.
/// Ships: Art/Ships/{escort,frigate,destroyer,cruiser,tanker,freighter}[-cutaway].svg, nose right.
/// Idea cards: Art/Ideas/{idea id}.webp, 1120 x 384 (painted by tools/idea-art/paint_ideas.py); else the older
/// line drawing Art/Ideas/{idea id}.svg (make_ideas.py); else the shared Art/Ideas/{picture}.svg named in economy.json.
/// </summary>
public static class Art
{
    private static readonly Dictionary<string, Texture2D?> _cache = new();

    /// <summary>A drawing as a texture, e.g. Texture("Ideas", "escort"); null if there is no such drawing.</summary>
    public static Texture2D? Texture(string folder, string name, float scale = 1f)
    {
        var key = $"{folder}/{name}@{scale}";
        if (_cache.TryGetValue(key, out var t)) return t;
        var asm = typeof(Art).Assembly;
        using var stream = asm.GetManifestResourceStream($"Lightlag.Game.Art.{folder}.{name}.svg");
        Texture2D? tex = null;
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            var img = new Image();
            if (img.LoadSvgFromString(reader.ReadToEnd(), scale) == Error.Ok) tex = ImageTexture.CreateFromImage(img);
            else Main.Instance.Log.Warn("ui", "Drawing did not load", new { folder, name });
        }
        _cache[key] = tex;
        return tex;
    }

    /// <summary>A painted picture (WebP) as a texture with mipmaps, so it stays smooth when drawn smaller; null if none.</summary>
    public static Texture2D? Painting(string folder, string name)
    {
        var key = $"{folder}/{name}.webp";
        if (_cache.TryGetValue(key, out var t)) return t;
        var asm = typeof(Art).Assembly;
        using var stream = asm.GetManifestResourceStream($"Lightlag.Game.Art.{folder}.{name}.webp");
        Texture2D? tex = null;
        if (stream != null)
        {
            using var mem = new MemoryStream();
            stream.CopyTo(mem);
            var img = new Image();
            if (img.LoadWebpFromBuffer(mem.ToArray()) == Error.Ok)
            {
                img.GenerateMipmaps();
                tex = ImageTexture.CreateFromImage(img);
            }
            else Main.Instance.Log.Warn("ui", "Picture did not load", new { folder, name });
        }
        _cache[key] = tex;
        return tex;
    }

    /// <summary>An idea card's picture: its painting, else its older drawing, else the shared one its data names, else the engine.</summary>
    public static Texture2D? IdeaPicture(string id, string? picture) =>
        Painting("Ideas", id) ?? Texture("Ideas", id) ?? Texture("Ideas", string.IsNullOrEmpty(picture) ? "engine" : picture);
}
