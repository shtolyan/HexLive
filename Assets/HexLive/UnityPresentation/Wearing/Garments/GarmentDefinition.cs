using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing.Garments
{
    /// <summary>
    /// Spec §42: one wearable, one asset. This is the inspector-editable home
    /// for a garment's survival parameters (warmth / armor / thermal / coverage)
    /// — the ScriptableObject twin of the engine-free <see cref="GarmentParams"/>.
    /// The <see cref="GarmentCatalog"/> collects every one of these; at startup
    /// GarmentTuning converts them and pushes the table into GarmentLibrary, so
    /// the plain-C# simulation reads tuned values without ever seeing Unity.
    ///
    /// The <see cref="id"/> is the content key — it must match the garment's
    /// Resources/HexLive/Wear/&lt;id&gt;/ art folder, so don't rename it here.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Garment", fileName = "Garment")]
    public sealed class GarmentDefinition : ScriptableObject
    {
        [Header("Идентификация")]
        [Tooltip("Content-id вещи. ДОЛЖЕН совпадать с папкой арта Resources/HexLive/Wear/<id>. Не переименовывать.")]
        public string id = string.Empty;
        [Tooltip("Отображаемое имя (запасное, если нет строки в локализации).")]
        public string displayName = string.Empty;

        [Header("Слой и покрытие")]
        [Tooltip("Слой одежды: Underwear (на тело) / Wear (основной) / Outerwear (верхний). Внутри слоя вещи вытесняют друг друга по зоне.")]
        public WearLayer layer = WearLayer.Wear;
        [Tooltip("Зоны тела, которые вещь закрывает. Тепло и броня действуют на покрытые части (§29C.4).")]
        public List<BodyPart> covers = new();

        [Header("Параметры выживания")]
        [Tooltip("Тепло. EquippedWarmth×10 = +°C. Бельё — шёпот (0.01–0.06); основной слой несёт бюджет; пальто — главный источник (~0.4). Комплект топ+штаны+ботинки ≈ 0.5.")]
        [Range(0f, 1f)] public float warmth = 0.1f;
        [Tooltip("Броня: доля укуса/удара, поглощённая на покрытых зонах (§29C.4). Только кожа/ботинки/тяжёлая броня. 0 — обычная ткань.")]
        [Range(0f, 1f)] public float armor = 0f;
        [Tooltip("Перегрев на солнце: чем теплее/тяжелее вещь, тем сильнее минус (дискомфорт в жару). 0 — лёгкая летняя.")]
        [Range(-1f, 0f)] public float thermalDelta = 0f;

        [Header("Анимация")]
        [Tooltip("Длина окна одевания/снятия в тиках (8 ≈ 2.0 сек).")]
        [Range(1, 40)] public int dressDurationTicks = 8;

        [Header("Инвентарь (§52)")]
        [Tooltip("Сколько слотов инвентаря даёт эта вещь, пока надета. Правило: трусы/лифчик 1, топ 2, штаны 4, куртка/тяжёлый жилет 6, платье 4, юбка/шорты 2. Аксессуары (перчатки, чулки, украшения) — 0. Инвентарь = 2 слота в руках + карманы всей надетой одежды.")]
        [Range(0, 10)] public int capacity = 0;

        // Convert to the engine-free bundle the simulation consumes.
        public GarmentParams ToParams()
        {
            var parts = covers != null ? covers.ToArray() : System.Array.Empty<BodyPart>();
            return new GarmentParams(
                id,
                string.IsNullOrEmpty(displayName) ? id : displayName,
                layer,
                warmth,
                armor,
                thermalDelta,
                dressDurationTicks,
                capacity,
                parts);
        }
    }
}
