namespace Lightlag.Sim;

/// <summary>
/// Where the game writes things. Everything lives under one data folder, kept apart from the
/// build folders so updating or rolling back a build never touches saves, logs or reports.
/// </summary>
public sealed class DataPaths
{
    public DataPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }
    public string Logs => Path.Combine(Root, "logs");
    public string Saves => Path.Combine(Root, "saves");
    public string Reports => Path.Combine(Root, "reports");
    public string Crashes => Path.Combine(Root, "crashes");
    public string Nightly => Path.Combine(Root, "nightly");

    public DataPaths EnsureCreated()
    {
        foreach (var dir in new[] { Root, Logs, Saves, Reports, Crashes, Nightly })
            Directory.CreateDirectory(dir);
        return this;
    }

    /// <summary>A timestamped folder name that sorts by time, e.g. 2026-11-09_083959_crash.</summary>
    public static string StampedName(DateTime utc, string suffix) =>
        $"{utc:yyyy-MM-dd_HHmmss}_{suffix}";
}
