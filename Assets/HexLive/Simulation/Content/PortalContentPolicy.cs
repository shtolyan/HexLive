using System;
using System.Collections.Generic;
using System.Linq;

namespace HexLive.Simulation.Content
{

/// <summary>§176: live Modern owners with an explicit apartment acquisition path.</summary>
public static class PortalContentPolicy
{
    public const string Origin = "modern";
    public const string Acquisition = "portal";

    public static IReadOnlyList<string> Objects { get; } = Array.AsReadOnly(new[]
    {
        "portal.modern_door",
        "furniture.modern_bed", ContentIds.ModernFridge, "furniture.modern_table", "furniture.modern_chair",
        "furniture.modern_sofa", "furniture.modern_tv", "furniture.modern_shelf", ContentIds.ModernWardrobe,
        ContentIds.KitchenKnife, ContentIds.BaseballBat, ContentIds.SteelHatchet, ContentIds.Flashlight,
        ContentIds.DisposableLighter, ContentIds.WindproofLighter, ContentIds.ClawHammer, ContentIds.FoldingSaw,
        ContentIds.Apple, ContentIds.Banana, ContentIds.DollarBills, ContentIds.KitchenLadle,
        ContentIds.PhotoFrame, ContentIds.BallpointPen, ContentIds.AlarmClock, ContentIds.DrinkingGlass,
        ContentIds.CeramicSaucer, ContentIds.TeddyBear,
        "food.strawberry_yogurt", "food.cheese_wedge", "food.chocolate_bar", "food.ham_sandwich",
        "food.orange", "food.canned_peaches",
    });

    public static IReadOnlyList<string> Buildings { get; } = Array.AsReadOnly(new[]
    {
        "architecture.modern_wall_cream", "architecture.modern_wall_blue", "architecture.modern_wall_green",
        "architecture.modern_floor_cream", "architecture.modern_floor_blue", "architecture.modern_floor_green",
        "architecture.modern_opening", "architecture.modern_window", "architecture.modern_exit_jamb",
        "architecture.modern_window_jamb",
    });

    public static IReadOnlyList<string> Wear => PortalWardrobeCatalog.Wear;
    public static IReadOnlyList<string> PaintMaps => PortalWardrobeCatalog.PaintMaps;

    private static readonly HashSet<string> WearIds = new(Wear, StringComparer.Ordinal);
    private static readonly HashSet<string> PaintMapIds = new(PaintMaps, StringComparer.Ordinal);

    public static bool IsPortalWear(string id) => WearIds.Contains(id);

    // Classification can identify future modern art. Approval remains an exact allowlist.
    public static bool IsModernWorldOwner(string type, string id) =>
        type == "object" && (id == "portal.modern_door" || ModernLootCatalog.IsModernProp(id)) ||
        type == "building" && id?.StartsWith("architecture.modern_", StringComparison.Ordinal) == true;

    public static bool IsApproved(string type, string id) => type switch
    {
        "object" => Objects.Contains(id, StringComparer.Ordinal),
        "building" => Buildings.Contains(id, StringComparer.Ordinal),
        "wear" => IsPortalWear(id),
        "config" => PaintMapIds.Contains(id),
        _ => false,
    };
}

}
