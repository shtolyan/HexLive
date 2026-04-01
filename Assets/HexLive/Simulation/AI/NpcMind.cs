using System.Collections.Generic;

namespace HexLive.Simulation.AI
{

public sealed class NPCMind
{
    public GoalType CurrentGoal { get; set; } = GoalType.None;

    public GoalLock? GoalLock { get; set; }

    public List<GoalCooldown> Cooldowns { get; } = new();

    public List<GoalScore> LastScores { get; } = new();

    public DecisionResult LastDecision { get; set; } = new();
}

public enum GoalType
{
    None,
    Eat,
    Sleep,
    Sit,
    Dress,
    Idle
}

public sealed class GoalScore
{
    public GoalType Goal { get; set; }

    public float BaseScore { get; set; }

    public float NeedModifier { get; set; }

    public float MemoryModifier { get; set; }

    public float SocialModifier { get; set; }

    public float EnvironmentModifier { get; set; }

    public float CommandModifier { get; set; }

    public float FinalScore { get; set; }
}

public sealed class GoalLock
{
    public GoalType Goal { get; set; }

    public int StartTick { get; set; }

    public int EndTick { get; set; }
}

public sealed class GoalCooldown
{
    public GoalType Goal { get; set; }

    public int EndTick { get; set; }
}

public sealed class DecisionResult
{
    public GoalType SelectedGoal { get; set; }

    public List<GoalScore> Scores { get; } = new();

    public string Reason { get; set; } = string.Empty;
}

}
