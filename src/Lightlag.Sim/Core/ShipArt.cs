namespace Lightlag.Sim.Core;

/// <summary>
/// Which approved drawing (game/Art/Ships, nose right) each class uses. Classes without their own drawing
/// borrow the nearest hull (by icon) and are tagged "drawing to come". Plain data, no drawing code.
/// </summary>
public static class ShipArt
{
    /// <summary>Every drawing in game/Art/Ships; each has a "-cutaway" twin.</summary>
    public static readonly string[] Drawings = { "escort", "frigate", "destroyer", "cruiser", "tanker", "freighter" };

    /// <summary>Classes drawn for themselves; the rest borrow.</summary>
    private static readonly Dictionary<string, string> Own = new(StringComparer.Ordinal)
    {
        ["escort"] = "escort",
        ["gun-frigate"] = "frigate",
        ["missile-destroyer"] = "destroyer",
        ["cruiser"] = "cruiser",
        ["tanker"] = "tanker",
        ["freighter"] = "freighter",
    };

    public static string For(ShipClass cls) => Own.TryGetValue(cls.Id, out var own) ? own : cls.Icon switch
    {
        "escort" => "escort",
        "destroyer" => "destroyer",
        "cruiser" => "cruiser",
        "tanker" => "tanker",
        "freighter" => "freighter",
        _ => "frigate",
    };

    /// <summary>True when the class has a drawing of its own (not a borrowed hull).</summary>
    public static bool HasOwn(ShipClass cls) => Own.ContainsKey(cls.Id);

    /// <summary>How much of a drawing's width is hull (the rest is engine plume).</summary>
    public static float HullShare(string art) => art switch
    {
        "escort" => 0.645f,
        "destroyer" => 0.819f,
        "cruiser" => 0.79f,
        "tanker" => 0.774f,
        "freighter" => 0.76f,
        _ => 0.745f,
    };
}
