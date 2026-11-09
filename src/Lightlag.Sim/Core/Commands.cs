using System.Text.Json.Serialization;

namespace Lightlag.Sim.Core;

/// <summary>
/// Everything a player (or AI) does to the game goes through a command. Commands are recorded with
/// the tick they took effect, which is what a replay is: the seed plus this list.
/// Never change the world from the UI directly; add a command instead.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(MoveShipCommand), "moveShip")]
[JsonDerivedType(typeof(StopShipCommand), "stopShip")]
[JsonDerivedType(typeof(DebugCrashCommand), "debugCrash")]
[JsonDerivedType(typeof(DebugBreakRuleCommand), "debugBreakRule")]
public abstract record GameCommand
{
    /// <summary>Who issued it, e.g. "player", "ai:rival", "headless".</summary>
    public string Source { get; init; } = "player";

    public abstract void Apply(World world, Simulation sim);
}

public sealed record MoveShipCommand(int ShipId, double X, double Y) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        var ship = world.FindShip(ShipId);
        if (ship == null)
        {
            sim.Log.Warn("sim", "Move order for a ship that does not exist", new { ShipId });
            return;
        }
        ship.Destination = new Vec2(X, Y);
        sim.Log.Debug("sim", $"{ship.Name} ordered to {ship.Destination}", new { ShipId, X, Y, Source });
    }
}

public sealed record StopShipCommand(int ShipId) : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        var ship = world.FindShip(ShipId);
        if (ship != null) ship.Destination = null;
    }
}

/// <summary>Throws inside the simulation on purpose, to test that crash reports and replays work.</summary>
public sealed record DebugCrashCommand(string Reason) : GameCommand
{
    public override void Apply(World world, Simulation sim) =>
        throw new InvalidOperationException($"Test crash on purpose: {Reason}");
}

/// <summary>Breaks the game state on purpose, to test that rule checks catch it.</summary>
public sealed record DebugBreakRuleCommand : GameCommand
{
    public override void Apply(World world, Simulation sim)
    {
        if (world.Ships.Count == 0) return;
        world.Ships[0].HoldBodyId = null;
        world.Ships[0].Position = new Vec2(double.NaN, 0);
    }
}
