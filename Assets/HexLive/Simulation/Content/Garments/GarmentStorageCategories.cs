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

        /// <summary>
        /// A garment stored as a compact left/right pair: explicit gloves, or
        /// any authored wearable whose fine slots are confined to the arms.
        /// The slot rule deliberately excludes ids and coarse protection zones,
        /// so new cuffs, detached sleeves and arm guards inherit it automatically.
        /// </summary>
        public static bool IsPairedHandwear(string definitionId)
        {
            if (CategoryFor(definitionId) == GarmentCategory.Gloves) return true;

            var slots = WearSlotCatalog.For(definitionId);
            if (slots.Count == 0) return false;
            for (var index = 0; index < slots.Count; index++)
            {
                if (!IsArmSlot(slots[index])) return false;
            }

            return true;
        }

        public static GarmentCategory CategoryFor(string definitionId)
        {
            foreach (var garment in GarmentLibrary.Active)
            {
                if (garment != null && garment.Id == definitionId) return garment.Category;
            }
            return GarmentCategory.Unclassified;
        }

        private static bool IsArmSlot(WearSlot slot) => slot is
            WearSlot.ShoulderR or WearSlot.ShoulderL or
            WearSlot.ForearmR or WearSlot.ForearmL or
            WearSlot.WristR or WearSlot.WristL or
            WearSlot.HandR or WearSlot.HandL;
    }
}
