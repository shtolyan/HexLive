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
        "furniture.modern_sofa", "furniture.modern_tv", "furniture.modern_shelf",
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

    public static IReadOnlyList<string> Wear { get; } = Array.AsReadOnly(new[]
    {
        "clothing.shorts_classic", "clothing.halter_vamp", "clothing.croptop_fit",
    });

    // These six owners match the approved fit artifacts for the three base garments.
    public static IReadOnlyList<string> PaintMaps { get; } = Array.AsReadOnly(new[]
    {
        "paintmaps.garment_7d6cad5d36b3b05cbb5a7eeb44441552_6b8e58100bebef1d3ebf4e8a_marta_4246",
        "paintmaps.garment_7d6cad5d36b3b05cbb5a7eeb44441552_6b8e58100bebef1d3ebf4e8a_kshishtof_4246",
        "paintmaps.garment_09ed605d5ca9ce194bb16e5b7659ed70_46302d2607153dc1a50379b2_marta_5237",
        "paintmaps.garment_09ed605d5ca9ce194bb16e5b7659ed70_46302d2607153dc1a50379b2_kshishtof_5237",
        "paintmaps.garment_ef9e673e27cf381b9e7c49c154ff158b_77a59b76230ea0db48c55dcf_marta_1607",
        "paintmaps.garment_ef9e673e27cf381b9e7c49c154ff158b_77a59b76230ea0db48c55dcf_kshishtof_1607",
    });

    public static bool IsPortalWear(string id) => Wear.Contains(id, StringComparer.Ordinal);

    // Classification can identify future modern art. Approval remains an exact allowlist.
    public static bool IsModernWorldOwner(string type, string id) =>
        type == "object" && (id == "portal.modern_door" || ModernLootCatalog.IsModernProp(id)) ||
        type == "building" && id?.StartsWith("architecture.modern_", StringComparison.Ordinal) == true;

    public static bool IsApproved(string type, string id) => type switch
    {
        "object" => Objects.Contains(id, StringComparer.Ordinal),
        "building" => Buildings.Contains(id, StringComparer.Ordinal),
        "wear" => IsPortalWear(id),
        "config" => PaintMaps.Contains(id, StringComparer.Ordinal),
        _ => false,
    };
}

}
