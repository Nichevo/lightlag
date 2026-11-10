namespace Lightlag.Sim.Core;

/// <summary>A ship as it was in one frame of a battle recording.</summary>
public sealed record FrameShip(int Id, double X, double Y, double Armour);

/// <summary>A missile in flight in one frame of a battle recording.</summary>
public sealed record FrameMissile(Side Side, double X, double Y);

/// <summary>Something drawn for a moment in a battle recording: a railgun volley, a missile hit, a point defence kill.</summary>
public sealed record FrameShot(EffectKind Kind, Side Side, double X0, double Y0, double X1, double Y1);

/// <summary>One frame of a battle recording: where every ship and missile was, and the shots since the last frame.</summary>
public sealed class BattleFrame
{
    public double Hour { get; set; }
    public List<FrameShip> Ships { get; set; } = new();
    public List<FrameMissile> Missiles { get; set; } = new();
    public List<FrameShot> Shots { get; set; } = new();
}

/// <summary>A ship that took part in a recorded battle (its name, class, side and fleet do not change from frame to frame).</summary>
public sealed record RosterShip(int Id, Side Side, string Name, string ClassId, string Fleet, double MaxArmour, bool Flagship);

/// <summary>
/// A moment marked on a battle's timeline: a decision a fleet made for itself (autonomous ones are what
/// the replay is for), an order arriving at light speed, a ship lost, a flagship identified.
/// </summary>
public sealed record BattleMark(double Hour, Side Side, MarkKind Kind, string Text);

public enum MarkKind { Decision, OrderArrived, ShipLost, Flagship }

/// <summary>
/// Records battles so they can be watched again from News, including far battles fought on standing
/// orders and autonomy. A frame every <see cref="BattleRecordData.FrameMinutes"/> of battle; long battles
/// thin their frames out so a recording stays small. Only the latest few battles keep their frames.
/// Recording reads the world and writes only into the battle, so it changes nothing that is played.
/// </summary>
public static class BattleRecords
{
    private static BattleRecordData D => GameData.Recording;

    /// <summary>Adds a frame if one is due (or <paramref name="force"/>, for the first and last frames).</summary>
    public static void Record(Simulation sim, Battle battle, double hour, bool force = false)
    {
        var w = sim.World;
        if (battle.FrameEveryHours <= 0) battle.FrameEveryHours = D.FrameMinutes / 60.0;
        var last = battle.Frames.Count > 0 ? battle.Frames[^1] : null;
        if (!force && last != null && hour - last.Hour < battle.FrameEveryHours - 1e-9) return;
        if (last != null && Math.Abs(last.Hour - hour) < 1e-9) battle.Frames.RemoveAt(battle.Frames.Count - 1);

        var frame = new BattleFrame { Hour = hour, Shots = battle.PendingShots.ToList() };
        battle.PendingShots.Clear();
        var near = GameData.Combat.ContactRangeMm * 1.5;
        foreach (var f in w.Fleets)
        {
            var inBattle = battle.For(f.Side).Fleets.Contains(f.Name) || Vec2.Distance(f.Position, battle.Where) <= near && battle.Roster.Any(r => r.Fleet == f.Name);
            if (!inBattle) continue;
            foreach (var s in w.Ships.Where(s => s.FleetId == f.Id))
            {
                if (battle.Roster.All(r => r.Id != s.Id))
                {
                    var flag = w.Light.On && Light.Flagship(w, s.Side)?.Id == s.Id;
                    battle.Roster.Add(new RosterShip(s.Id, s.Side, s.Name, s.ClassId, f.Name, GameData.ShipClass(s.ClassId).Armour, flag));
                }
                frame.Ships.Add(new FrameShip(s.Id, Round(s.Position.X), Round(s.Position.Y), Math.Round(s.Armour, 1)));
            }
        }
        foreach (var m in w.Missiles) frame.Missiles.Add(new FrameMissile(m.Side, Round(m.Position.X), Round(m.Position.Y)));
        battle.Frames.Add(frame);

        // A long battle: keep every other frame and record half as often.
        if (battle.Frames.Count > D.MaxFrames)
        {
            var kept = new List<BattleFrame>();
            for (var i = 0; i < battle.Frames.Count; i++)
            {
                if (i % 2 == 0 || i == battle.Frames.Count - 1) kept.Add(battle.Frames[i]);
                else if (kept.Count > 0) kept[^1].Shots.AddRange(battle.Frames[i].Shots); // shots stay in the record
            }
            battle.Frames = kept;
            battle.FrameEveryHours *= 2;
        }
        foreach (var f in battle.Frames) if (f.Shots.Count > D.MaxShotsPerFrame) f.Shots.RemoveRange(D.MaxShotsPerFrame, f.Shots.Count - D.MaxShotsPerFrame);
    }

    /// <summary>A shot or hit worth drawing in the recording of the battle being fought.</summary>
    public static void Shot(World w, CombatEffect e)
    {
        if (w.ActiveBattle is not Battle b || e.Kind == EffectKind.RailMiss || e.Kind == EffectKind.RailHit) return;
        if (b.PendingShots.Count >= D.MaxShotsPerFrame) return;
        b.PendingShots.Add(new FrameShot(e.Kind, e.Side, Round(e.From.X), Round(e.From.Y), Round(e.To.X), Round(e.To.Y)));
    }

    /// <summary>Marks a moment on the timeline of the battle a fleet is fighting in, if it is in one.</summary>
    public static void Mark(World w, Fleet f, MarkKind kind, string text)
    {
        if (w.ActiveBattle is not Battle b || !b.For(f.Side).Fleets.Contains(f.Name)) return;
        Mark(w, b, f.Side, kind, text);
    }

    public static void Mark(World w, Battle b, Side side, MarkKind kind, string text)
    {
        if (b.Marks.Count >= D.MaxMarks) return;
        b.Marks.Add(new BattleMark(w.Hour, side, kind, text));
    }

    /// <summary>Old battles drop their frames (the report numbers stay).</summary>
    public static void Trim(World w)
    {
        var keep = D.KeepBattles;
        for (var i = 0; i < w.Battles.Count - keep; i++)
        {
            w.Battles[i].Frames.Clear();
            w.Battles[i].PendingShots.Clear();
        }
    }

    /// <summary>Can this battle be watched yet by this side? Only once the light of its first frame has reached the flagship.</summary>
    public static double SeenFromHour(World w, Battle b, Side side) => b.StartHour + Light.DelayHours(w, side, b.Where);

    /// <summary>The latest hour of the battle this side has seen (its light has arrived); the whole battle without light lag.</summary>
    public static double SeenUntil(World w, Battle b, Side side)
    {
        var end = b.EndHour ?? w.Hour;
        if (!w.Light.On) return end;
        return Math.Min(end, w.Hour - Light.DelayHours(w, side, b.Where));
    }

    private static double Round(double v) => Math.Round(v, 2);
}

/// <summary>Battle recording settings (ship-classes.json, "recording").</summary>
public sealed record BattleRecordData(double FrameMinutes = 1, int MaxFrames = 240, int MaxShotsPerFrame = 40, int MaxMarks = 60, int KeepBattles = 6);
