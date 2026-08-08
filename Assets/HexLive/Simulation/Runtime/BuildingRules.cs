namespace HexLive.Simulation.Runtime
{

/// <summary>
/// Shared simulation/presentation contract for the first architectural module.
/// A leaf resource represents one tied roof bundle, not one rendered frond;
/// twelve bundles reveal the dense one-hundred-forty-four-frond roof.
/// </summary>
public static class BuildingRules
{
    public const string HutHearthVariant = "hut-hearth";
    public const int StageCount = 3;

    public const int FrameSticks = 28;
    public const int FrameBoards = 6;

    public const int EnclosureSticks = 6;
    public const int EnclosureBoards = 25;
    public const int EnclosureRope = 4;

    public const int RoofLeaves = 12;

    public const int TotalSticks = FrameSticks + EnclosureSticks;
    public const int TotalBoards = FrameBoards + EnclosureBoards;
    public const int TotalRope = EnclosureRope;
    public const int TotalLeaves = RoofLeaves;

    public static int CompletedStages(int sticks, int boards, int rope, int leaves)
    {
        if (sticks < FrameSticks || boards < FrameBoards) return 0;
        if (sticks < TotalSticks || boards < TotalBoards || rope < TotalRope) return 1;
        return leaves < TotalLeaves ? 2 : 3;
    }

    public static float FrameProgress(int sticks, int boards) => Average(
        Ratio(sticks, FrameSticks), Ratio(boards, FrameBoards));

    public static float EnclosureProgress(int sticks, int boards, int rope)
    {
        if (sticks < FrameSticks || boards < FrameBoards) return 0f;
        return Average(
            Ratio(sticks - FrameSticks, EnclosureSticks),
            Ratio(boards - FrameBoards, EnclosureBoards),
            Ratio(rope, EnclosureRope));
    }

    public static float RoofProgress(int sticks, int boards, int rope, int leaves)
    {
        if (sticks < TotalSticks || boards < TotalBoards || rope < TotalRope) return 0f;
        return Ratio(leaves, RoofLeaves);
    }

    private static float Ratio(int value, int required) => required <= 0
        ? 1f
        : System.Math.Max(0f, System.Math.Min(1f, value / (float)required));

    private static float Average(params float[] values)
    {
        var total = 0f;
        foreach (var value in values) total += value;
        return values.Length == 0 ? 1f : total / values.Length;
    }
}

}
