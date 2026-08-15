using System;
using System.Collections.Generic;
using System.Linq;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// The two player-facing Build/Buy workspaces. A catalog entry belongs to
    /// exactly one workspace, so architecture and furniture never compete for
    /// pointer input or draw both of their grids at once (§120).
    /// </summary>
    public enum BuildCatalogMode
    {
        Construction,
        Furniture
    }

    public enum BuildCatalogPlacementKind
    {
        FloorRegion,
        WallLine,
        Window,
        Door,
        Support,
        Roof,
        Furniture
    }

    /// <summary>
    /// Planning markers are on by default for every buildable definition.
    /// Content may opt out without adding an id-specific presentation branch.
    /// </summary>
    public enum BuildMarkerPolicy
    {
        Default = 0,
        Hidden = 1
    }

    public static class BuildCatalogCategories
    {
        public const string Floor = "floor";
        public const string Walls = "walls";
        public const string Openings = "openings";
        public const string Roof = "roof";
        public const string Comfort = "comfort";
        public const string Heating = "heating";
        public const string Storage = "storage";
        public const string Workstations = "workstations";
        public const string Outdoor = "outdoor";
    }

    /// <summary>
    /// Data-only catalog metadata. It deliberately does not carry a prefab,
    /// Unity type or model correction: the game and HutTest resolve the same
    /// definitionId through the ordinary production factories.
    /// </summary>
    public sealed class BuildCatalogEntryDefinition
    {
        public string DefinitionId { get; }
        public BuildCatalogMode Mode { get; }
        public string CategoryId { get; }
        public BuildCatalogPlacementKind PlacementKind { get; }
        public string NameTerm { get; }
        public string DescriptionTerm { get; }
        public string FallbackGlyph { get; }
        public int SortOrder { get; }
        public bool RequiresCompletedFloor { get; }
        public BuildMarkerPolicy MarkerPolicy { get; }

        public BuildCatalogEntryDefinition(
            string definitionId,
            BuildCatalogMode mode,
            string categoryId,
            BuildCatalogPlacementKind placementKind,
            string nameTerm,
            string descriptionTerm,
            string fallbackGlyph,
            int sortOrder,
            bool requiresCompletedFloor = false,
            BuildMarkerPolicy markerPolicy = BuildMarkerPolicy.Default)
        {
            DefinitionId = definitionId ?? throw new ArgumentNullException(nameof(definitionId));
            Mode = mode;
            CategoryId = categoryId ?? throw new ArgumentNullException(nameof(categoryId));
            PlacementKind = placementKind;
            NameTerm = nameTerm ?? throw new ArgumentNullException(nameof(nameTerm));
            DescriptionTerm = descriptionTerm ?? throw new ArgumentNullException(nameof(descriptionTerm));
            FallbackGlyph = fallbackGlyph ?? string.Empty;
            SortOrder = sortOrder;
            RequiresCompletedFloor = requiresCompletedFloor;
            MarkerPolicy = markerPolicy;
        }

        public bool ShowPlanningMarkers => MarkerPolicy != BuildMarkerPolicy.Hidden;
    }

    /// <summary>
    /// One authoritative list for every object the colony can plan and build:
    /// architectural LEGO, indoor furniture, workstations and outdoor sites.
    /// Adding another buildable object means adding one data row here rather
    /// than another Tool enum member and renderer switch (§120).
    /// </summary>
    public static class BuildCatalogDefinition
    {
        private static readonly BuildCatalogEntryDefinition[] EntriesInternal =
        {
            new("architecture.floor.board", BuildCatalogMode.Construction,
                BuildCatalogCategories.Floor, BuildCatalogPlacementKind.FloorRegion,
                "blueprint.catalog.floor.name", "blueprint.catalog.floor.description", "⬡", 10),
            new("architecture.wall.wood", BuildCatalogMode.Construction,
                BuildCatalogCategories.Walls, BuildCatalogPlacementKind.WallLine,
                "blueprint.catalog.wall.name", "blueprint.catalog.wall.description", "▥", 20),
            new("architecture.window.wood", BuildCatalogMode.Construction,
                BuildCatalogCategories.Openings, BuildCatalogPlacementKind.Window,
                "blueprint.catalog.window.name", "blueprint.catalog.window.description", "▣", 30),
            new("architecture.door.wood", BuildCatalogMode.Construction,
                BuildCatalogCategories.Openings, BuildCatalogPlacementKind.Door,
                "blueprint.catalog.door.name", "blueprint.catalog.door.description", "⌑", 40),
            new("architecture.support.wood", BuildCatalogMode.Construction,
                BuildCatalogCategories.Roof, BuildCatalogPlacementKind.Support,
                "blueprint.catalog.support.name", "blueprint.catalog.support.description", "┃", 50),
            new("architecture.roof.palm", BuildCatalogMode.Construction,
                BuildCatalogCategories.Roof, BuildCatalogPlacementKind.Roof,
                "blueprint.catalog.roof.name", "blueprint.catalog.roof.description", "⌃", 60),

            new(ContentIds.BedBasic, BuildCatalogMode.Furniture,
                BuildCatalogCategories.Comfort, BuildCatalogPlacementKind.Furniture,
                "blueprint.catalog.bed.name", "blueprint.catalog.bed.description", "▰", 100,
                requiresCompletedFloor: true),
            new("furniture.hearth", BuildCatalogMode.Furniture,
                BuildCatalogCategories.Heating, BuildCatalogPlacementKind.Furniture,
                "blueprint.catalog.hearth.name", "blueprint.catalog.hearth.description", "♨", 110,
                requiresCompletedFloor: true),
            new(ContentIds.Wardrobe, BuildCatalogMode.Furniture,
                BuildCatalogCategories.Storage, BuildCatalogPlacementKind.Furniture,
                "blueprint.catalog.wardrobe.name", "blueprint.catalog.wardrobe.description", "╫", 120,
                requiresCompletedFloor: true),
            new(ContentIds.Workbench, BuildCatalogMode.Furniture,
                BuildCatalogCategories.Workstations, BuildCatalogPlacementKind.Furniture,
                "blueprint.catalog.workbench.name", "blueprint.catalog.workbench.description", "⚒", 130,
                requiresCompletedFloor: true),

            new(ContentIds.Campfire, BuildCatalogMode.Furniture,
                BuildCatalogCategories.Outdoor, BuildCatalogPlacementKind.Furniture,
                "blueprint.catalog.campfire.name", "blueprint.catalog.campfire.description", "♨", 200),
            new(ContentIds.DryingRack, BuildCatalogMode.Furniture,
                BuildCatalogCategories.Outdoor, BuildCatalogPlacementKind.Furniture,
                "blueprint.catalog.drying_rack.name", "blueprint.catalog.drying_rack.description", "╥", 210),
            new(ContentIds.WaterCollector, BuildCatalogMode.Furniture,
                BuildCatalogCategories.Outdoor, BuildCatalogPlacementKind.Furniture,
                "blueprint.catalog.water_collector.name", "blueprint.catalog.water_collector.description", "▽", 220)
        };

        private static readonly Dictionary<string, BuildCatalogEntryDefinition> ById =
            EntriesInternal.ToDictionary(entry => entry.DefinitionId, StringComparer.Ordinal);

        public static IReadOnlyList<BuildCatalogEntryDefinition> All => EntriesInternal;

        public static IEnumerable<BuildCatalogEntryDefinition> ForMode(BuildCatalogMode mode) =>
            EntriesInternal.Where(entry => entry.Mode == mode).OrderBy(entry => entry.SortOrder);

        public static IEnumerable<BuildCatalogEntryDefinition> ForCategory(
            BuildCatalogMode mode, string categoryId) =>
            ForMode(mode).Where(entry => string.Equals(entry.CategoryId, categoryId, StringComparison.Ordinal));

        public static bool TryGet(string definitionId, out BuildCatalogEntryDefinition definition) =>
            ById.TryGetValue(definitionId ?? string.Empty, out definition);
    }
}
