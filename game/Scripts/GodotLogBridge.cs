using Godot;
using Lightlag.Sim.Logging;

namespace Lightlag.Game;

/// <summary>
/// Copies Godot's own errors and warnings (including C# exceptions that Godot catches in callbacks)
/// into the game log, so nothing that goes wrong is only visible in the editor.
/// </summary>
public partial class GodotLogBridge : Logger
{
    private readonly GameLog _log;

    public GodotLogBridge() : this(GameLog.Memory()) { }

    public GodotLogBridge(GameLog log) => _log = log;

    public override void _LogError(string function, string file, int line, string code, string rationale,
                                   bool editorNotify, int errorType, Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        var level = errorType == (int)ErrorType.Warning ? LogLevel.Warn : LogLevel.Error;
        var message = string.IsNullOrEmpty(rationale) ? code : rationale;
        _log.Write(level, "godot", message, new { function, file, line, code });
    }

    public override void _LogMessage(string message, bool error)
    {
        if (error) _log.Error("godot", message.TrimEnd());
    }
}
