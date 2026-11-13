namespace Lightlag.Sim.Core;

/// <summary>
/// Which approved drawing (game/Art/Ships, nose right) each class uses. Classes without their own drawing
/// borrow the nearest hull (by icon) and are tagged "drawing to come". Plain data, no drawing code.
/// </summary>
public static class ShipArt
{
    /// <summary>Every drawing in game/Art/Ships; each has a "-cutaway" twin.</summary>
    public static readonly string[] Drawings = { "escort", "frigate", "destroyer", "cruiser", "tanker", "freighter" };

    /// <summary>Sail ships (board 28): drawn for every race, no cutaway.</summary>
    public static readonly string[] Sails = { "sail-courier", "sail-freighter" };

    /// <summary>
    /// Race skins (board 28, option B: the class is the skeleton, the race the skin): game/Art/Ships/{race}/{drawing}.svg,
    /// same length, outline and slot places as the human drawing. A race with no frigate of its own uses its escort.
    /// </summary>
    public static readonly string[] RaceDrawings = { "escort", "destroyer", "cruiser", "tanker", "freighter", "sail-courier", "sail-freighter" };
    public static readonly string[] SkinnedRaces = { Races.Hive, Races.Lattice };

    /// <summary>Classes drawn for themselves; the rest borrow.</summary>
    private static readonly Dictionary<string, string> Own = new(StringComparer.Ordinal)
    {
        ["escort"] = "escort",
        ["gun-frigate"] = "frigate",
        ["missile-destroyer"] = "destroyer",
        ["cruiser"] = "cruiser",
        ["tanker"] = "tanker",
        ["freighter"] = "freighter",
        ["sail-courier"] = "sail-courier",
        ["sail-freighter"] = "sail-freighter",
    };

    public static string For(ShipClass cls) => Own.TryGetValue(cls.Id, out var own) ? own : cls.Icon switch
    {
        "escort" => "escort",
        "destroyer" => "destroyer",
        "cruiser" => "cruiser",
        "tanker" => "tanker",
        "freighter" => "freighter",
        "sail" => "sail-courier",
        _ => "frigate",
    };

    /// <summary>The drawing for a class in a race's skin: "hive.cruiser" (Art/Ships/hive/cruiser.svg), else the human drawing.</summary>
    public static string For(ShipClass cls, string? race)
    {
        var art = For(cls);
        if (race == null || !SkinnedRaces.Contains(race)) return art;
        if (art == "frigate") art = "escort";
        return RaceDrawings.Contains(art) ? $"{race}.{art}" : For(cls);
    }

    /// <summary>True when the class has a drawing of its own (not a borrowed hull).</summary>
    public static bool HasOwn(ShipClass cls) => Own.ContainsKey(cls.Id);

    /// <summary>How much of a drawing's width is hull (the rest is engine plume).</summary>
    public static float HullShare(string art) => art switch
    {
        "sail-courier" or "sail-freighter" or "hive.sail-courier" or "hive.sail-freighter" or "lattice.sail-courier" or "lattice.sail-freighter" => 1f,
        "hive.escort" => 0.827f,
        "lattice.escort" => 0.827f,
        "hive.destroyer" => 0.915f,
        "lattice.destroyer" => 0.917f,
        "hive.cruiser" => 0.943f,
        "lattice.cruiser" => 0.945f,
        "hive.freighter" => 0.909f,
        "lattice.freighter" => 0.906f,
        "hive.tanker" => 0.901f,
        "lattice.tanker" => 0.898f,
        "escort" => 0.645f,
        "destroyer" => 0.819f,
        "cruiser" => 0.79f,
        "tanker" => 0.774f,
        "freighter" => 0.76f,
        _ => 0.745f,
    };
}
