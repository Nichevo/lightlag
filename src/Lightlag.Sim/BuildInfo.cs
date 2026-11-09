using System.Text.Json;

namespace Lightlag.Sim;

/// <summary>
/// Which build is running. CI writes build_info.json next to the game; local dev builds say "dev".
/// Every log line, crash report and replay carries this so a report can be matched to its build.
/// </summary>
public sealed record BuildInfo(string Version, string Commit, string BuiltAt)
{
    public static BuildInfo Dev { get; } = new("dev", "local", "");

    public override string ToString() => Commit == "local" ? Version : $"{Version} ({Commit})";

    public static BuildInfo FromJson(string json)
    {
        var info = JsonSerializer.Deserialize<BuildInfo>(json, Json.Options);
        return info ?? Dev;
    }

    /// <summary>Looks for build_info.json in the given folders, first match wins.</summary>
    public static BuildInfo Load(params string[] folders)
    {
        foreach (var folder in folders)
        {
            var path = Path.Combine(folder, "build_info.json");
            try
            {
                if (File.Exists(path)) return FromJson(File.ReadAllText(path));
            }
            catch (Exception)
            {
                // A broken build_info.json must never stop the game from starting.
            }
        }
        return Dev;
    }
}
