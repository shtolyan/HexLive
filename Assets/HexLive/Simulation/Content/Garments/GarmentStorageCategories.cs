namespace HexLive.Simulation.Content
{
    /// <summary>
    /// Shared physical-storage lookup. Wardrobe presentation and simulation
    /// consume the explicit category carried by catalog metadata; body slots
    /// remain only the rules for equipping an item.
    /// </summary>
    public static class GarmentStorageCategories
    {
        public static bool IsFootwear(string definitionId)
            => CategoryFor(definitionId) == GarmentCategory.Footwear;

        public static bool IsPairedGloves(string definitionId)
            => CategoryFor(definitionId) == GarmentCategory.Gloves;

        public static GarmentCategory CategoryFor(string definitionId)
        {
            foreach (var garment in GarmentLibrary.Active)
            {
                if (garment != null && garment.Id == definitionId) return garment.Category;
            }
            return GarmentCategory.Unclassified;
        }
    }
}
