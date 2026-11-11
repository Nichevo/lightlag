using Lightlag.Sim.Logging;

namespace Lightlag.Sim.Core;

// ---- Data (economy.json, "teams") ------------------------------------------------------------

/// <summary>
/// Research team rules. A side starts with one general team and empty slots up to Slots. Speed on an
/// idea is the team's skill in the idea's field times a size factor: (scientists / baseScientists)^0.75,
/// so twice the scientists gives about 1.7x the speed. Finishing an idea raises the team's skill in its
/// field by skillPerIdea up to maxSkill; skills a team doesn't keep fade back towards 1 by fadePer30Days.
/// A side surveys a body by holding orbit there for surveyHours the first time, and the survey takes
/// surveyCut off one random idea it hasn't researched.
/// </summary>
public sealed record TeamsData(
    int Slots = 3,
    double StartScientists = 20,
    double FoundScientists = 12,
    double FoundCost = 120,
    double FoundDays = 20,
    double FoundSkill = 2.0,
    double TrainScientists = 4,
    double TrainCost = 30,
    double TrainDays = 10,
    double BaseScientists = 20,
    double SkillPerIdea = 0.125,
    double MaxSkill = 2.5,
    double FadePer30Days = 0.125,
    double SplitShare = 0.5,
    double MinScientists = 6,
    double SurveyHours = 24,
    double SurveyCut = 0.25);

// ---- State ----------------------------------------------------------------------------------

/// <summary>A research team: its size, its skill in each field (1 = normal), and the fields it keeps.</summary>
public sealed class ResearchTeam
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double Scientists { get; set; }

    /// <summary>Skill per field, in Research.Fields order. 1 is normal; skills not kept fade back to 1.</summary>
    public double[] Skill { get; set; } = { 1, 1, 1 };

    /// <summary>The 1-2 fields the team keeps (they don't fade). Empty: it keeps its best field.</summary>
    public List<string> Kept { get; set; } = new();

    /// <summary>A newly founded team starts work at this hour.</summary>
    public double ReadyHour { get; set; }

    /// <summary>Scientists in training join at this hour (null: none training).</summary>
    public double? TrainingUntilHour { get; set; }
    public double TrainingScientists { get; set; }

    /// <summary>Ideas this team has finished.</summary>
    public int Ideas { get; set; }
}

// ---- Rules ----------------------------------------------------------------------------------

public static class Research
{
    public static TeamsData T => GameData.Economy.Teams ?? new TeamsData();

    public static readonly string[] Fields = { "drives", "weapons", "sensors" };
    public static readonly string[] FieldNames = { "Drives and ships", "Weapons and defence", "Sensors and industry" };
    private static readonly string[] TeamNames = { "First team", "Second team", "Third team", "Fourth team", "Fifth team" };

    public static int FieldIndex(string field) => Array.IndexOf(Fields, field);
    public static string FieldName(string field) => FieldIndex(field) is var i and >= 0 ? FieldNames[i] : field;

    public static ResearchTeam? Team(World w, Side side, int id) => Economy.Of(w, side).Teams.Find(t => t.Id == id);

    /// <summary>Speed from size: (scientists / base)^0.75, so twice the size is about 1.7x as fast.</summary>
    public static double SizeFactor(double scientists)
    {
        var r = Math.Max(0, scientists) / T.BaseScientists;
        var root = Math.Sqrt(r);
        return root * Math.Sqrt(root);
    }

    /// <summary>How many base days of an idea in this field the team gets through per day.</summary>
    public static double Speed(ResearchTeam team, string field)
    {
        var i = FieldIndex(field);
        return (i >= 0 ? team.Skill[i] : 1) * SizeFactor(team.Scientists);
    }

    /// <summary>The fields the team keeps: its chosen ones, or its best field if none is chosen.</summary>
    public static IEnumerable<int> KeptFields(ResearchTeam team)
    {
        if (team.Kept.Count > 0) return team.Kept.Select(FieldIndex).Where(i => i >= 0);
        var best = 0;
        for (var i = 1; i < team.Skill.Length; i++) if (team.Skill[i] > team.Skill[best] + 1e-9) best = i;
        return new[] { best };
    }

    public static bool Ready(World w, ResearchTeam team) => w.Hour >= team.ReadyHour;

    /// <summary>The research orders a team is working through, first one first.</summary>
    public static IEnumerable<WorkOrder> QueueOf(World w, Side side, int teamId) =>
        w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Research && o.TeamId == teamId);

    /// <summary>Hours of base work for an idea, after any survey discovery cut.</summary>
    public static double BaseHours(World w, Side side, string id) =>
        GameData.Research(id).Days * 24 * (Economy.Of(w, side).Cuts.Contains(id) ? 1 - T.SurveyCut : 1);

    /// <summary>Days until this order is done, counting the orders ahead of it in its team's queue.</summary>
    public static double DaysUntilDone(World w, WorkOrder order)
    {
        if (order.TeamId is not int id || Team(w, order.Side, id) is not ResearchTeam team) return double.PositiveInfinity;
        var hours = Math.Max(0, team.ReadyHour - w.Hour);
        foreach (var o in QueueOf(w, order.Side, id))
        {
            hours += o.HoursLeft / Math.Max(1e-6, Speed(team, GameData.Research(o.Key).Field));
            if (o == order) break;
        }
        return hours / 24;
    }

    /// <summary>The team a new idea goes to when none is named: the ready team that would finish it soonest.</summary>
    public static ResearchTeam? BestTeamFor(World w, Side side, string id)
    {
        var field = GameData.Research(id).Field;
        ResearchTeam? best = null;
        var bestHours = double.PositiveInfinity;
        foreach (var t in Economy.Of(w, side).Teams)
        {
            var hours = Math.Max(0, t.ReadyHour - w.Hour) + QueueOf(w, side, t.Id).Sum(o => o.HoursLeft / Math.Max(1e-6, Speed(t, GameData.Research(o.Key).Field)))
                        + BaseHours(w, side, id) / Math.Max(1e-6, Speed(t, field));
            if (hours < bestHours - 1e-9) { best = t; bestHours = hours; }
        }
        return best;
    }

    /// <summary>Why this idea can't be researched yet (a missing idea it needs), or null.</summary>
    public static string? Missing(World w, Side side, string id)
    {
        var state = Economy.Of(w, side);
        foreach (var need in GameData.Research(id).Needs ?? new List<string>())
            if (!state.Researched.Contains(need) && !w.Orders.Any(o => o.Side == side && o.Kind == OrderKind.Research && o.Key == need))
                return $"Needs {GameData.Research(need).Name}";
        return null;
    }

    private static ResearchTeam NewTeam(World w, Side side, double scientists)
    {
        var state = Economy.Of(w, side);
        var team = new ResearchTeam
        {
            Id = w.NextId++, Name = TeamNames[Math.Min(state.TeamsFounded, TeamNames.Length - 1)], Scientists = scientists, ReadyHour = w.Hour,
        };
        state.TeamsFounded++;
        state.Teams.Add(team);
        return team;
    }

    /// <summary>Each side's first, general team (also for games saved before teams existed).</summary>
    public static void EnsureTeams(World w)
    {
        foreach (var side in new[] { Side.Player, Side.Rival })
            if (Economy.Of(w, side).Teams.Count == 0) NewTeam(w, side, T.StartScientists);
    }

    /// <summary>One game hour of research for a side: each ready team works on the first idea in its queue.</summary>
    public static void Work(Simulation sim, Side side)
    {
        var w = sim.World;
        var state = Economy.Of(w, side);
        // Orders with no team (or a team that is gone) go to the first team.
        foreach (var o in w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Research
                                              && (o.TeamId is not int id || Team(w, side, id) == null)))
            o.TeamId = state.Teams[0].Id;

        foreach (var team in state.Teams.ToList())
        {
            if (team.TrainingUntilHour is double done && w.Hour >= done)
            {
                team.Scientists += team.TrainingScientists;
                sim.Log.Info("research", $"{side} {team.Name} grows", new { side = side.ToString(), team = team.Id, added = team.TrainingScientists, team.Scientists });
                team.TrainingUntilHour = null;
                team.TrainingScientists = 0;
            }
            Fade(team);
            if (!Ready(w, team)) continue;
            var order = QueueOf(w, side, team.Id).FirstOrDefault();
            if (order == null) continue;
            if (!order.Paid)
            {
                var cost = Economy.CostOf(order);
                if (state.Industry < cost) continue;
                state.Industry -= cost;
                state.Totals.IndustrySpent += cost;
                order.Paid = true;
                sim.Log.Info("economy", $"{side} starts {Economy.NameOf(order)}", new { side = side.ToString(), order.Id, order.Key, cost, team = team.Id });
            }
            order.HoursLeft -= GameTime.HoursPerTick * Speed(team, GameData.Research(order.Key).Field);
            if (order.HoursLeft > 1e-9) continue;
            w.Orders.Remove(order);
            Learn(sim, side, order.Key, team);
        }
    }

    private static void Fade(ResearchTeam team)
    {
        var kept = KeptFields(team).ToList();
        var perHour = T.FadePer30Days / (30 * 24);
        for (var i = 0; i < team.Skill.Length; i++)
            if (!kept.Contains(i) && team.Skill[i] > 1) team.Skill[i] = Math.Max(1, team.Skill[i] - perHour);
    }

    private static void Learn(Simulation sim, Side side, string id, ResearchTeam team)
    {
        var w = sim.World;
        var data = GameData.Research(id);
        Economy.Of(w, side).Researched.Add(id);
        var i = FieldIndex(data.Field);
        if (i >= 0) team.Skill[i] = Math.Min(T.MaxSkill, team.Skill[i] + T.SkillPerIdea);
        team.Ideas++;
        Tech.OnLearn(w, side, id);
        sim.Log.Info("economy", $"{side} finishes research: {data.Name}", new { side = side.ToString(), id, team = team.Id });
    }

    /// <summary>
    /// The first time a side holds orbit at a body for SurveyHours, it surveys it: the find takes
    /// SurveyCut off one idea it hasn't researched (picked at random).
    /// </summary>
    public static void Surveys(Simulation sim)
    {
        var w = sim.World;
        foreach (var b in w.Bodies)
            foreach (var side in new[] { Side.Player, Side.Rival })
            {
                var i = (int)side;
                if (b.Surveyed[i] || !w.Fleets.Any(f => f.Side == side && Economy.InOrbitAt(f, b))) continue;
                b.SurveyHours[i] += GameTime.HoursPerTick;
                if (b.SurveyHours[i] < T.SurveyHours) continue;
                b.Surveyed[i] = true;
                var state = Economy.Of(w, side);
                var open = GameData.Economy.Research.Where(r => !state.Researched.Contains(r.Id) && !state.Cuts.Contains(r.Id)).ToList();
                if (open.Count == 0)
                {
                    sim.Log.Info("research", $"{side} surveys {b.Name}: nothing new", new { side = side.ToString(), bodyId = b.Id });
                    continue;
                }
                var pick = open[sim.Rng.Range(0, open.Count)];
                state.Cuts.Add(pick.Id);
                state.Finds.Add(new SurveyFind(b.Id, pick.Id, w.Hour));
                foreach (var o in w.Orders.Where(o => o.Side == side && o.Kind == OrderKind.Research && o.Key == pick.Id))
                {
                    o.HoursLeft = Math.Max(0, o.HoursLeft - o.TotalHours * T.SurveyCut);
                    o.TotalHours *= 1 - T.SurveyCut;
                }
                sim.Log.Info("research", $"{side} surveys {b.Name}: a find speeds up {pick.Name}", new { side = side.ToString(), bodyId = b.Id, idea = pick.Id });
            }
    }

    /// <summary>Why a new team can't be founded now, or null.</summary>
    public static string? CanFound(World w, Side side, string field)
    {
        if (!w.Loop) return "There is no research in the battle sandbox";
        if (FieldIndex(field) < 0) return $"Unknown field '{field}'";
        var state = Economy.Of(w, side);
        if (state.Teams.Count >= T.Slots) return "All team slots are full";
        if (state.Industry < T.FoundCost) return $"Needs {T.FoundCost:0} Industry";
        return null;
    }

    public static string? CanTrain(World w, Side side, int teamId)
    {
        if (!w.Loop) return "There is no research in the battle sandbox";
        if (Team(w, side, teamId) is not ResearchTeam team) return "No such team";
        if (team.TrainingUntilHour != null) return "Already training";
        if (Economy.Of(w, side).Industry < T.TrainCost) return $"Needs {T.TrainCost:0} Industry";
        return null;
    }

    public static string? CanSplit(World w, Side side, int teamId, string field)
    {
        if (!w.Loop) return "There is no research in the battle sandbox";
        if (FieldIndex(field) < 0) return $"Unknown field '{field}'";
        if (Team(w, side, teamId) is not ResearchTeam team) return "No such team";
        if (Economy.Of(w, side).Teams.Count >= T.Slots) return "No empty team slot";
        if (team.Scientists < T.MinScientists * 2) return $"Needs at least {T.MinScientists * 2:0} scientists";
        return null;
    }

    internal static void Found(Simulation sim, Side side, string field)
    {
        var w = sim.World;
        Economy.Of(w, side).Industry -= T.FoundCost;
        Economy.Of(w, side).Totals.IndustrySpent += T.FoundCost;
        var team = NewTeam(w, side, T.FoundScientists);
        team.ReadyHour = w.Hour + T.FoundDays * 24;
        team.Skill[FieldIndex(field)] = T.FoundSkill;
        team.Kept.Add(field);
        sim.Log.Info("research", $"{side} founds {team.Name}", new { side = side.ToString(), team = team.Id, field, ready = team.ReadyHour });
    }

    internal static void Train(Simulation sim, Side side, int teamId)
    {
        var w = sim.World;
        var team = Team(w, side, teamId)!;
        Economy.Of(w, side).Industry -= T.TrainCost;
        Economy.Of(w, side).Totals.IndustrySpent += T.TrainCost;
        team.TrainingUntilHour = w.Hour + T.TrainDays * 24;
        team.TrainingScientists = T.TrainScientists;
        sim.Log.Info("research", $"{side} trains scientists for {team.Name}", new { side = side.ToString(), team = team.Id, until = team.TrainingUntilHour });
    }

    internal static void Split(Simulation sim, Side side, int teamId, string field)
    {
        var w = sim.World;
        var from = Team(w, side, teamId)!;
        var moving = Math.Floor(from.Scientists / 2);
        from.Scientists -= moving;
        var team = NewTeam(w, side, moving);
        for (var i = 0; i < team.Skill.Length; i++) team.Skill[i] = 1 + (from.Skill[i] - 1) * T.SplitShare;
        team.Kept.Add(field);
        sim.Log.Info("research", $"{side} splits {team.Name} off {from.Name}", new { side = side.ToString(), from = from.Id, team = team.Id, field, moving });
    }
}

/// <summary>A survey find: the body, the idea it sped up, and when.</summary>
public sealed record SurveyFind(int BodyId, string Idea, double Hour);

// ---- Commands -------------------------------------------------------------------------------

/// <summary>Found a new research team in an empty slot, with a speciality that starts high.</summary>
public sealed record FoundTeamCommand(Side Side, string Field) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (Research.CanFound(world, Side, Field) is string problem)
        {
            sim.Log.Warn("research", $"Can't found a team: {problem}", new { side = Side.ToString(), Field, Source });
            return;
        }
        Research.Found(sim, Side, Field);
    }
}

/// <summary>Train more scientists for a team: paid now, they join after the training time.</summary>
public sealed record TrainTeamCommand(Side Side, int TeamId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (Research.CanTrain(world, Side, TeamId) is string problem)
        {
            sim.Log.Warn("research", $"Can't train: {problem}", new { side = Side.ToString(), TeamId, Source });
            return;
        }
        Research.Train(sim, Side, TeamId);
    }
}

/// <summary>Split half a team off into an empty slot; the new team keeps the field named.</summary>
public sealed record SplitTeamCommand(Side Side, int TeamId, string Field) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        if (Research.CanSplit(world, Side, TeamId, Field) is string problem)
        {
            sim.Log.Warn("research", $"Can't split the team: {problem}", new { side = Side.ToString(), TeamId, Field, Source });
            return;
        }
        Research.Split(sim, Side, TeamId, Field);
    }
}

/// <summary>Choose the 1-2 fields a team keeps (the others fade). An empty list keeps its best field.</summary>
public sealed record SetTeamKeptCommand(Side Side, int TeamId, List<string> Fields) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var team = Research.Team(world, Side, TeamId);
        var fields = Fields.Distinct().ToList();
        if (team == null || fields.Count > 2 || fields.Any(f => Research.FieldIndex(f) < 0))
        {
            sim.Log.Warn("research", "Bad kept fields", new { side = Side.ToString(), TeamId, fields = string.Join(",", Fields), Source });
            return;
        }
        team.Kept = fields;
        sim.Log.Info("research", $"{Side} {team.Name} keeps new fields", new { side = Side.ToString(), TeamId, fields = string.Join(",", fields), Source });
    }
}

/// <summary>Move a research order to another team's queue (at the end). Progress made so far is kept.</summary>
public sealed record SetOrderTeamCommand(Side Side, int OrderId, int TeamId) : GameCommand
{
    public override (Side Side, Vec2 At)? Destination(World world) => null; // a home order: at once, even while paused

    public override void Apply(World world, Simulation sim)
    {
        var order = world.Orders.Find(o => o.Id == OrderId && o.Side == Side && o.Kind == OrderKind.Research);
        if (order == null || Research.Team(world, Side, TeamId) == null)
        {
            sim.Log.Warn("research", "Team choice for a missing order or team", new { side = Side.ToString(), OrderId, TeamId, Source });
            return;
        }
        world.Orders.Remove(order);
        order.TeamId = TeamId;
        world.Orders.Add(order);
        sim.Log.Info("research", "Research order moves team", new { side = Side.ToString(), OrderId, TeamId, Source });
    }
}
