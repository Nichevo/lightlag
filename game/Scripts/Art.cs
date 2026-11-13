using Godot;

namespace Lightlag.Game;

/// <summary>
/// The approved drawings (game/Art, SVG), built into the game and turned into textures on first use.
/// Ships: Art/Ships/{escort,frigate,destroyer,cruiser,tanker,freighter}[-cutaway].svg, nose right.
/// Idea cards: Art/Ideas/{idea id}.svg, 560 x 192 (drawn by tools/idea-art/make_ideas.py), else the shared
/// Art/Ideas/{picture}.svg named in economy.json.
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

    /// <summary>An idea card's picture: its own drawing, else the shared one its data names, else the engine.</summary>
    public static Texture2D? IdeaPicture(string id, string? picture) =>
        Texture("Ideas", id) ?? Texture("Ideas", string.IsNullOrEmpty(picture) ? "engine" : picture);
}
