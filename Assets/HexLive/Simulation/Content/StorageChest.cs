namespace HexLive.Simulation.Content
{

/// <summary>§151.6: the primitive chest uses ordinary container cells and object state.</summary>
public static class StorageChest
{
    public const int Capacity = 3;
    public const float FootprintRadius = .42f;
    public const float ModelWidth = .50f;
    public const float ModelLength = .66f;
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
        Tags = { "Furniture", "Container" },
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
