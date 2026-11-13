using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using Lightlag.Sim.Core;
using HttpClient = System.Net.Http.HttpClient;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// The rival's optional advisor (see Lightlag.Sim.Core.Advisor): off by default. When on, every few weeks of game time
/// the game sends the rival's brief (only what the rival knows) to a local Ollama server and turns the answer into
/// one AdviseCommand, so the nudge is in the replay and saves. The request runs in the background; the game never
/// waits for it, and if Ollama is not running the rival simply plays on unadvised. Settings live in advisor.cfg in
/// the data folder: on/off, the server address and the model name.
/// </summary>
public static class RivalAdvisor
{
    public const string DefaultUrl = "http://localhost:11434";
    public const string DefaultModel = "llama3.2";

    public static bool On { get; private set; }
    public static string Url { get; private set; } = DefaultUrl;
    public static string Model { get; private set; } = DefaultModel;

    /// <summary>The last thing that happened, for the title screen ("asked on day 40: raid", "Ollama not reachable").</summary>
    public static string Status { get; private set; } = "";

    private static string? _file;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static Task<string?>? _asking;
    private static double _nextTryHour = -1e9;
    private static Simulation? _for;

    public static void Load(string dataDir)
    {
        _file = System.IO.Path.Combine(dataDir, "advisor.cfg");
        var cfg = new ConfigFile();
        if (cfg.Load(_file) != Error.Ok) return;
        On = (bool)cfg.GetValue("advisor", "on", false);
        Url = (string)cfg.GetValue("advisor", "url", DefaultUrl);
        Model = (string)cfg.GetValue("advisor", "model", DefaultModel);
    }

    public static void Set(bool on, string? model = null, string? url = null)
    {
        On = on;
        if (!string.IsNullOrWhiteSpace(model)) Model = model.Trim();
        if (!string.IsNullOrWhiteSpace(url)) Url = url.Trim().TrimEnd('/');
        if (_file == null) return;
        var cfg = new ConfigFile();
        cfg.SetValue("advisor", "on", On);
        cfg.SetValue("advisor", "url", Url);
        cfg.SetValue("advisor", "model", Model);
        cfg.Save(_file);
    }

    /// <summary>
    /// Called each frame while the game runs: starts a question when one is due, and hands a finished answer to the
    /// simulation as an order. Only for games against the computer rival.
    /// </summary>
    public static void Tick(Simulation sim, Main main)
    {
        if (_for != sim) { _for = sim; _asking = null; _nextTryHour = -1e9; }
        var w = sim.World;
        if (_asking is { IsCompleted: true } done)
        {
            _asking = null;
            var reply = done.IsCompletedSuccessfully ? done.Result : null;
            if (Advisor.ParseReply(reply) is var (focus, why))
            {
                sim.Issue(new AdviseCommand(Side.Rival, focus, why) { Source = "advisor" });
                Status = $"Day {w.Day}: advised the rival to {focus}";
                _nextTryHour = w.Hour + 24;
                main.Log.Info("ai", "Advisor answered", new { focus, why, model = Model });
            }
            else
            {
                Status = done.IsCompletedSuccessfully ? $"Day {w.Day}: the model's answer had no focus in it" : $"Day {w.Day}: could not reach Ollama at {Url}";
                main.Log.Warn("ai", "Advisor gave no advice", new { reply = Advisor.Clean(reply), error = done.Exception?.GetBaseException().Message, url = Url, model = Model });
                // Try again a few game days later, not every frame.
                _nextTryHour = w.Hour + 5 * 24;
            }
            return;
        }
        if (!On || _asking != null || w.Hour < _nextTryHour || Mind.Of(w, Side.Rival) == null || !Advisor.Due(w, Side.Rival)) return;
        // Don't ask in the first weeks: the rival's own plan comes first.
        if (w.Day < 20) return;
        var prompt = Advisor.Brief(w, Side.Rival);
        _nextTryHour = w.Hour + 24; // at most one question a game day, even if the answer is slow
        Status = $"Day {w.Day}: asking {Model}…";
        main.Log.Info("ai", "Asking the advisor", new { model = Model, url = Url, day = w.Day });
        _asking = Ask(Url, Model, prompt);
    }

    private static async Task<string?> Ask(string url, string model, string prompt)
    {
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["stream"] = false };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.PostAsync(url.TrimEnd('/') + "/api/generate", content).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonNode.Parse(json)?["response"]?.GetValue<string>();
    }

    /// <summary>The title-screen row: on/off, the model name and the last status.</summary>
    public static Control Picker()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        row.AddChild(Ui.Label("Rival advisor:", 15, Ui.Dim));
        var box = new CheckBox { Text = "Ollama", ButtonPressed = On, FocusMode = Control.FocusModeEnum.None,
            TooltipText = "Off by default. Every few weeks the rival asks a local language model (Ollama, on this computer) for one strategy nudge, "
                          + "using only what the rival can see. The nudge leans the rival's plans for a while; it never gives orders. The F7 mind panel shows it" };
        var model = new LineEdit { Text = Model, CustomMinimumSize = new Vector2(160, 0), TooltipText = $"The Ollama model to ask (server: {Url}). Pull it first with 'ollama pull <model>'" };
        box.Toggled += on => Set(on, model.Text);
        model.TextSubmitted += t => Set(On, t);
        model.FocusExited += () => Set(On, model.Text);
        row.AddChild(box);
        row.AddChild(Ui.Label("model", 14, Ui.Dim));
        row.AddChild(model);
        if (Status != "") row.AddChild(Ui.Label(Status, 13, Ui.Dim));
        return row;
    }
}
