using System.Collections.Generic;

namespace HexLive.Simulation.Content
{
    /// <summary>
    /// Spec §42 / §31A.5B: the engine-free parameter bundle for one wearable.
    /// This is the plain-C# mirror of the Unity-side GarmentDefinition asset —
    /// the simulation assembly cannot see ScriptableObject (noEngineReferences),
    /// so the presentation layer converts each asset into one of these and
    /// pushes it into <see cref="GarmentLibrary"/> at startup (exactly how the
    /// HexTuningConfig asset feeds HexHopTuning / Spec49 statics).
    ///
    /// From these fields <see cref="GarmentLibrary.AppendDefinitions"/> builds
    /// the runtime <see cref="ObjectDefinition"/> (a "Dress" interaction whose
    /// effects carry warmth / armor / thermal), so nothing downstream changes.
    /// </summary>
    public sealed class GarmentParams
    {
        public GarmentParams(
            string id,
            string displayName,
            WearLayer layer,
            float warmth,
            float armor,
            float thermalDelta,
            int dressDurationTicks,
            int capacity,
            params BodyPart[] covers)
            : this(id, displayName, layer, warmth, armor, thermalDelta,
                   dressDurationTicks, capacity, GarmentSex.Any, covers)
        {
        }

        public GarmentParams(
            string id,
            string displayName,
            WearLayer layer,
            float warmth,
            float armor,
            float thermalDelta,
            int dressDurationTicks,
            int capacity,
            GarmentSex sex,
            params BodyPart[] covers)
        {
            Sex = sex;
            Id = id;
            DisplayName = displayName;
            Layer = layer;
            Warmth = warmth;
            Armor = armor;
            ThermalDelta = thermalDelta;
            DressDurationTicks = dressDurationTicks;
            Capacity = capacity;
            Covers = new List<BodyPart>(covers);
        }

        // Stable content id — matches the Resources/HexLive/Wear/<id>/ folder so
        // the visual still loads. Never rename without moving the art.
        public string Id { get; }

        public string DisplayName { get; }

        // §84: на кого сшито. Меш каждой вещи вылеплен под конкретное тело, и
        // женская вещь на мужском теле рисуется вывернутым мешем — поэтому
        // запрет живёт в СИМУЛЯЦИИ, а не в виде: чужую одежду не надо
        // отрисовывать правильнее, её не надо даже рассматривать.
        //
        // Any — вещь без пола (верёвка на поясе, подсумок): её носят все.
        public GarmentSex Sex { get; }

        public WearLayer Layer { get; }

        // Body zones this garment covers (armor & warmth apply per covered part).
        public List<BodyPart> Covers { get; }

        // Spec §42: EquippedWarmth x10 = °C added. Underwear is a whisper
        // (0.01-0.06); real cover carries the budget; a coat is the big spender.
        public float Warmth { get; }

        // Spec §29C.4: fraction of a bite/hit absorbed on the parts it covers.
        public float Armor { get; }

        // Spec §29C.4: overheating discomfort in the sun — warmer/heavier gear
        // reads as a negative thermal pull (it makes you hot).
        public float ThermalDelta { get; }

        // Length of the dress/undress animation window in ticks (2.0s @ 8).
        public int DressDurationTicks { get; }

        // Spec §52: inventory slots this garment grants while worn. The pack has
        // no capacity of its own — the body carries 2 hand slots and every worn
        // piece adds its pockets. Iron rule: panties/bra 1, top 2, pants 4,
        // jacket/vest 6; accessories (gloves, stockings, jewelry) grant 0.
        public int Capacity { get; }
    }
}
