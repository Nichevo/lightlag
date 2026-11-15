using Lightlag.Sim.Core;
using Side = Lightlag.Sim.Core.Side;

namespace Lightlag.Game;

/// <summary>
/// Answers that only change when the simulation does (a step, or an order carried out while paused): light delay,
/// whether a fleet is live or shown, signatures. Worked out once per step instead of once per frame.
/// </summary>
public partial class MapView
{
    private (long Tick, int Sub, int Commands, bool Reveal, int Fleets) _stepStamp = (-1, 0, 0, false, 0);
    private readonly Dictionary<int, bool> _liveMemo = new(), _shownMemo = new();
    private readonly Dictionary<int, double> _signatureMemo = new();
    private readonly Dictionary<(int, string), object> _memo = new();

    /// <summary>Drop the cached answers when the world has changed since they were worked out.</summary>
    private void FreshStep()
    {
        var w = Sim.World;
        var stamp = (w.Tick, w.Sub, Sim.Replay.Commands.Count, RevealAll, w.Fleets.Count);
        if (stamp == _stepStamp && !BenchProbe.OldWay) return;
        _stepStamp = stamp;
        _liveMemo.Clear();
        _shownMemo.Clear();
        _signatureMemo.Clear();
        _memo.Clear();
    }

    /// <summary>Any value worked out once per step (key: an id and what it is).</summary>
    private T PerStep<T>(int id, string what, Func<T> make)
    {
        FreshStep();
        if (_memo.TryGetValue((id, what), out var v)) return (T)v;
        var made = make()!;
        _memo[(id, what)] = made;
        return made;
    }

    private double SignatureOf(Fleet f)
    {
        FreshStep();
        if (_signatureMemo.TryGetValue(f.Id, out var s)) return s;
        return _signatureMemo[f.Id] = Sensors.Signature(Sim.World, f);
    }

    private double DelayTo(Fleet f) => PerStep(f.Id, "delay", () => Light.DelayHours(Sim.World, Side.Player, f.Position));
}
