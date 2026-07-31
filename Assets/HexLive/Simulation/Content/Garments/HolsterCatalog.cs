using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// Spec §52.8: a <b>holster</b> is worn GEAR, not clothing — a strap that
    /// carries the working tools. It grants <b>typed</b> weapon slots: one per
    /// listed tool id. Unlike a normal pocket
    /// (<see cref="GarmentParams.Capacity"/>), a holster slot:
    ///
    ///   • accepts ONLY its specific tool id (the axe slot holds an axe and
    ///     nothing else) and holds exactly one,
    ///   • rides <b>free of the pocket budget</b> — the tool it holds does not
    ///     count against <see cref="Agents.InventoryState.UsedSlots"/> (this is
    ///     the "future weapon slot" the retired personal-effect seam promised),
    ///   • pins its tool to a named body anchor in the prefab for the leg-slung
    ///     look (presentation fills the <c>tool.*</c> anchor children of the
    ///     worn holster prefab from the same list).
    ///
    /// Being gear also means it does not behave like cloth: it never wins or
    /// loses a wear-layer conflict (it straps over/under whatever she wears),
    /// heat never strips it (shedding it cools you by nothing), and it barely
    /// wears out (<see cref="WearMultiplier"/>). Engine-free so the headless
    /// harness resolves holstering without a Unity asset; the ids are frozen —
    /// they match the prefab anchor names.
    /// </summary>
    public static class HolsterCatalog
    {
        // garmentId → the tool ids it holsters, in prefab-anchor order.
        private static readonly Dictionary<string, string[]> Slots = new()
        {
            ["legHolster_2204"] = new[] { "tool.axe_stone", "tool.knife", "tool.hammer" },
        };

        public static bool IsHolster(string garmentId) =>
            garmentId != null && Slots.ContainsKey(garmentId);

        /// <summary>The tool ids <paramref name="garmentId"/> holsters (empty if not a holster).</summary>
        public static IReadOnlyList<string> SlotsFor(string garmentId) =>
            garmentId != null && Slots.TryGetValue(garmentId, out var s)
                ? s
                : System.Array.Empty<string>();

        /// <summary>
        /// Durability-loss scale for a worn item (1 = ordinary cloth). Gear is
        /// leather and buckles, not fabric: a holster ages ~50x slower than a
        /// shirt, both from passive wear and from bites landing on the leg it
        /// straps to. It is meant to be a keeper, not a consumable.
        /// </summary>
        public static float WearMultiplier(string garmentId) =>
            IsHolster(garmentId) ? 0.02f : 1f;
    }
}
