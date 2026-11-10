using Lightlag.Sim.Core;
using Lightlag.Sim.Replay;

namespace Lightlag.Sim.Save;

/// <summary>
/// A saved game: the world, any commands not yet applied, and the replay so far (so a save attached
/// to a crash report can also be replayed from the start).
/// </summary>
public sealed class SaveGame
{
    public int Format { get; set; } = 1;
    public string Build { get; set; } = "";
    public string SavedAtUtc { get; set; } = "";
    public World World { get; set; } = new();
    public List<GameCommand> PendingCommands { get; set; } = new();
    public ReplayFile Replay { get; set; } = new();

    public string ToJson() => Json.Serialize(this, indented: true);
    public static SaveGame FromJson(string json) => Json.Deserialize<SaveGame>(json);

    public void WriteTo(string path)
    {
        // Write to a temp file first so a crash mid-save never leaves a half-written save behind.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    public static SaveGame Load(string path) => FromJson(File.ReadAllText(path));
}

/// <summary>Manual saves, quick save and rotating autosaves, all in the saves folder.</summary>
public static class SaveSlots
{
    public const string QuickSave = "quicksave";
    public const int AutosavesKept = 5;

    public static string PathFor(DataPaths paths, string slot) => Path.Combine(paths.Saves, slot + ".json");

    public static string WriteAutosave(DataPaths paths, Simulation sim)
    {
        var path = Path.Combine(paths.Saves, $"autosave_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        sim.ToSave().WriteTo(path);
        foreach (var old in new DirectoryInfo(paths.Saves).GetFiles("autosave_*.json")
                     .OrderByDescending(f => f.Name).Skip(AutosavesKept))
            old.Delete();
        return path;
    }

    /// <summary>A manual save, named by when it was made, so it never overwrites another.</summary>
    public static string WriteManual(DataPaths paths, Simulation sim)
    {
        Directory.CreateDirectory(paths.Saves);
        var path = Path.Combine(paths.Saves, $"save_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        sim.ToSave().WriteTo(path);
        return path;
    }

    /// <summary>One save file as the load list shows it.</summary>
    public sealed record Entry(string Path, string Kind, DateTime SavedUtc, int Day, long Tick, string? Rival, bool Over);

    /// <summary>
    /// Every save in the folder, newest first, with the game day it was made on. Reads only the few
    /// fields the list needs; a file that cannot be read is left out.
    /// </summary>
    public static List<Entry> List(DataPaths paths)
    {
        var list = new List<Entry>();
        if (!Directory.Exists(paths.Saves)) return list;
        foreach (var f in new DirectoryInfo(paths.Saves).GetFiles("*.json"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f.FullName));
                var world = doc.RootElement.GetProperty("world");
                var tick = world.GetProperty("tick").GetInt64();
                string? rival = null;
                if (world.TryGetProperty("minds", out var minds))
                    foreach (var m in minds.EnumerateArray())
                        if (m.TryGetProperty("difficulty", out var d)) rival = d.GetString();
                var over = world.TryGetProperty("winner", out var winner) && winner.ValueKind != System.Text.Json.JsonValueKind.Null;
                var kind = f.Name.StartsWith("autosave_") ? "Autosave" : f.Name.StartsWith(QuickSave) ? "Quick save"
                    : f.Name.StartsWith("crash") ? "Crash save" : "Save";
                list.Add(new Entry(f.FullName, kind, f.LastWriteTimeUtc, GameTime.DayOf(tick), tick, rival, over));
            }
            catch (Exception)
            {
                // Not a save (or half written): leave it off the list.
            }
        }
        return list.OrderByDescending(e => e.SavedUtc).ToList();
    }

    /// <summary>The newest save of any kind, for the title screen's Continue button.</summary>
    public static string? Latest(DataPaths paths)
    {
        if (!Directory.Exists(paths.Saves)) return null;
        return new DirectoryInfo(paths.Saves).GetFiles("*.json")
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }
}
