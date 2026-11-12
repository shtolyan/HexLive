namespace HexLive.Simulation.Content
{

/// <summary>
/// §151.6 r2 (#732): the wooden chest is a build-menu furniture piece. It is
/// raised from sticks, boards and rope on an ordinary furniture build site and
/// stores what three backpacks hold in ordinary container cells.
/// </summary>
public static class StorageChest
{
    /// <summary>Cells of one backpack (`gear.backpack_riot`, `gear.backpack_osiris`).</summary>
    public const int BackpackCells = 9;
    public const int Capacity = 3 * BackpackCells;

    // The water collector model measures 0.93 × 0.93 × 1.05 wu. The chest keeps
    // its length, is a little narrower so it reads as a chest rather than a
    // crate, and is 40 % of its height: just above a colonist's knee
    // (~0.37 wu) and below her waist (~0.79 wu) at a 1.32 wu body.
    public const float ModelLength = .93f;
    public const float ModelWidth = .78f;
    public const float ModelHeight = .42f;
    /// <summary>Half-diagonal of the closed body (0.607 wu) plus the open lid's
    /// sweep behind its hinge.</summary>
    public const float FootprintRadius = .64f;
    /// <summary>Blocks the anchor and its six neighbours — the same seven
    /// junctions as the build-mode footprint (`BlueprintFurnitureFootprints`).</summary>
    public const float ObstacleRadius = .38f;

    // Build bill: four corner posts, eight wall/lid boards, two lashings.
    public const int BillSticks = 4;
    public const int BillBoards = 8;
    public const int BillRope = 2;

    public const string OpenInteractionId = "storage_chest.open";
    public const string CloseInteractionId = "storage_chest.close";
    private const string OpenVariant = "storage.chest.open";

    public static bool IsOpen(string variant) => variant == OpenVariant;

    public static void SetLid(WorldObjectState chest, bool open) =>
        chest.Variant = open ? OpenVariant : string.Empty;

    internal static ObjectDefinition CreateDefinition() => new()
    {
        Id = ContentIds.StorageChest,
        DisplayName = "Wooden chest",
        Tags = { "Furniture", "Container", ObjectTags.Obstacle, ObjectTags.HandBuilt },
        ObstacleRadius = ObstacleRadius,
        SolidRadius = FootprintRadius,
        Interactions =
        {
            new() { Id = "pickup." + ContentIds.StorageChest, Type = InteractionType.PickUp, DurationTicks = 4 },
            new() { Id = OpenInteractionId, Type = InteractionType.SwitchChestLid, DurationTicks = 4 },
            new() { Id = CloseInteractionId, Type = InteractionType.SwitchChestLid, DurationTicks = 4 },
        }
    };
}

}
