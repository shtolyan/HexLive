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

    // §176.3 / bug #743: uniforms, combat gear and costume armour never reach
    // an apartment. Model families cover their colourways. This is NON_CIVILIAN
    // from Tools/portal_wardrobe.py plus the island-only FCO Gloves Male.
    private static readonly HashSet<string> NonCivilianModels = new(StringComparer.Ordinal)
    {
        "FAO Harness Male", "FCO Belt Male", "FCO Boots Male", "FCO Gloves Male", "FCO Knee Straps Male",
        "FCO Legs Straps Male", "FCO Pants Male", "FCO Waist Strappy Male", "TonnyFlash",
        "clothing.belt_holster", "clothing.belt_stars", "clothing.boots_alloy",
        "clothing.boots_osiris", "clothing.cap_stars", "clothing.greaves_tod",
        "clothing.helmet_knight", "clothing.helmet_m1", "clothing.helmet_space",
        "clothing.helmet_tactical", "clothing.helmet_tactical_headset", "clothing.helmet_vietnam",
        "clothing.jacket_ranger", "clothing.pants_ranger", "clothing.pants_stars",
        "clothing.shorts_osiris", "clothing.top_alloy", "clothing.top_osiris", "clothing.top_stars",
        "clothing.vest_ranger", "gear.backpack_osiris", "gear.beltpouch_ranger",
    };

    /// <summary>
    /// §176.3 / bug #743: an ordinary island garment that also belongs in an
    /// apartment wardrobe. Castaway hide and fur (the primal families) stay on
    /// the island, as do the non-civilian models above.
    /// </summary>
    public static bool IsCivilianIslandWear(string id, string prototypeId)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var model = string.IsNullOrEmpty(prototypeId) ? id : prototypeId;
        return !NonCivilianModels.Contains(id) && !NonCivilianModels.Contains(model) &&
            !IsCastawayHide(id) && !IsCastawayHide(model);
    }

    private static bool IsCastawayHide(string id) =>
        id.IndexOf("_primal", StringComparison.Ordinal) >= 0 ||
        id.StartsWith("clothing.collar_fur", StringComparison.Ordinal);

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
