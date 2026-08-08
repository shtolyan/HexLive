using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>Где происходит крафт. Anywhere = на месте персонажа (спека
    /// §59: не указана станция — крафт где угодно).</summary>
    public enum CraftPlace
    {
        Anywhere = 0,
        Campfire = 1,
        Workbench = 2,
    }

    /// <summary>
    /// One ScriptableObject asset PER GEAR ITEM — weapon and tool unified,
    /// because the axe chops AND fights, the knife cuts AND stabs, the pickaxe
    /// mines AND clobbers. The asset holds EVERYTHING about the item:
    ///
    ///   • combat sheet (damage, замах/hit-delay, animation length, cooldown,
    ///     weapon priority, two-handedness),
    ///   • tool capabilities — ТИПИЗИРОВАННЫЙ массив (enum <see cref="GearCapability"/>):
    ///     никакие строки руками не пишутся; новый глагол = один член enum,
    ///   • the model — прямая ссылка на префаб (строка-путь только как
    ///     легаси-фолбэк; пусто-и-пусто = конвенция Resources/HexLive/Objects/&lt;id&gt;),
    ///   • ХВАТ В РУКЕ (бывший ItemAttachConfig — смержен сюда): локальная
    ///     поза в ладони + опциональный левый хват (Save из AxeChopTest),
    ///   • animations (attack clips, armed idle/walk, work clip),
    ///   • секция «Крафт» — ингредиенты ССЫЛКАМИ на конфиги предметов (id из
    ///     ссылки достаёт код сам; строка — фолбэк для ресурсов без ассета).
    ///
    /// Drop the asset under <c>Resources/HexLive/Gear/</c>; <see cref="GearTuning"/>
    /// loads every one at startup, overrides the sim's <see cref="GearCatalog"/>
    /// entry and registers the visuals in <see cref="GearLibrary"/>. Definition
    /// of done: a NEW weapon or tool = a new asset. No code.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Gear Config (weapon+tool)", fileName = "GearConfig")]
    public sealed class GearConfig : ScriptableObject
    {
        // The Resources folder scanned by GearTuning.LoadAndApply.
        public const string ResourceFolder = "HexLive/Gear";

        [Tooltip("Инвентарный id (tool.knife, tool.axe_stone…; пусто = кулаки).")]
        public string gearId = GearCatalog.Knife;

        [Header("Модель")]
        [Tooltip("Префаб предмета (рука+земля) — прямой ссылкой.")]
        public GameObject prefab;
        [Tooltip("ЛЕГАСИ-фолбэк: Resources-путь, если ссылка пуста. Пусто-и-пусто = конвенция HexLive/Objects/<id>.")]
        public string prefabResourcePath = "";

        [Header("Бой")]
        [Tooltip("Можно использовать как оружие. Снята — предмет НИКОГДА не достаётся в драке (бутылка, зажигалка, бинт…), какой бы приоритет ни стоял. Кулаки — оружие-фолбэк (галочка стоит, приоритет 0).")]
        public bool usableAsWeapon = true;
        [Tooltip("Урон одного попадания (до StrikeFactor бойца и брони цели).")]
        [Range(0f, 1f)] public float damage = 0.1875f;
        [Tooltip("Замах: через сколько секунд от старта анимации падает УРОН (плюс хит-реакция и кровь).")]
        [Range(0.1f, 3f)] public float hitDelaySeconds = 1.5f;
        [Tooltip("Полная длительность анимации атаки (удар + доигрыш).")]
        [Range(0.2f, 4f)] public float attackDurationSeconds = 2.0f;
        [Tooltip("Перезарядка ПОСЛЕ окончания анимации, сек.")]
        [Range(0f, 4f)] public float cooldownSeconds = 1.0f;
        [Tooltip("Приоритет выбора как оружия (0 = никогда не оружие; копьё 30 > топор 20 > нож 10 > кирка 8 > пила 7 > молоток 6).")]
        [Range(0, 100)] public int meleePriority;
        [Tooltip("Двуручное (нужны обе целые руки — копьё).")]
        public bool twoHanded;
        [Tooltip("Легаси-темп для medium-путей (защитники/предация): 1 = каждый проход.")]
        [Range(0.1f, 1f)] public float attackSpeed = 1f;
        [Tooltip("§79 ТЕМП РАБОТЫ этим инструментом: длительность работы делится на него. Считается только у того предмета, который УМЕЕТ эту работу (по способностям ниже): мачете 2 = вдвое быстрее топора, топор 1 = эталон, нож 0.75 = им рубить долго. Меньше 1 = медленнее.")]
        [Range(0.25f, 4f)] public float harvestSpeedMult = 1f;

        [Header("Инструмент — способности (enum; сим гейтится на них, не на id)")]
        [Tooltip("Что умеет предмет: Cut, Butcher, ChopWood, Mine, Hammer, Ignite, Boil, Saw, Sew, CarryWater, Dressing…")]
        public GearCapability[] capabilities;

        [Header("Крафт (выкл = предмет не крафтится, а добывается)")]
        [Tooltip("Крафтится ли предмет.")]
        public bool craftable;
        [Tooltip("Место крафта. Anywhere = на месте персонажа, без станции.")]
        public CraftPlace craftStation = CraftPlace.Anywhere;
        [Tooltip("Нужен ли ГОРЯЩИЙ костёр (форсирует станцию Campfire).")]
        public bool craftNeedsLitFire;
        [Tooltip("Ингредиенты: ссылка на конфиг предмета (id возьмётся из неё) или, для ресурсов без ассета, строковый id.")]
        public CraftIngredient[] craftIngredients;

        [System.Serializable]
        public sealed class CraftIngredient
        {
            [Tooltip("Ссылка на конфиг ингредиента — id достанется из неё.")]
            public WorldObjectConfig item;
            [Tooltip("Фолбэк: строковый id, если у ресурса ещё нет ассета.")]
            public string itemId = "";
            [Range(1, 30)] public int count = 1;

            public string ResolveId() =>
                item != null && !string.IsNullOrEmpty(item.objectId)
                    ? item.objectId
                    : itemId?.Trim() ?? "";
        }

        [Header("Хват в руке (rHand-local; тюнится в сцене AxeChopTest → Save)")]
        [Tooltip("Поза выставлена вручную. Выкл = фолбэк: AttachPoint в модели → дефолтная таблица → палм-фит.")]
        public bool handPoseAuthored;
        [Tooltip("Позиция предмета в системе координат ладони.")]
        public Vector3 handLocalPosition;
        [Tooltip("Поворот предмета (эйлеровы углы, градусы).")]
        public Vector3 handLocalEuler;
        [Tooltip("Тонкий МНОЖИТЕЛЬ масштаба поверх ObjectFit (ноль = единица).")]
        public Vector3 handLocalScale = Vector3.one;
        [Tooltip("Отдельный хват для левой руки; выкл = правый зеркалится автоматически.")]
        public bool handHasLeftOverride;
        public Vector3 leftHandLocalPosition;
        public Vector3 leftHandLocalEuler;

        [Header("Удары с индивидуальными таймингами (рукопашка: кулаки/ноги)")]
        [Tooltip("По строке на удар: клип + свой замах/доигрыш/перезарядка. Сим случайно выбирает удар на каждый обмен и играет ИМЕННО его клип. Непусто — перекрывает attackClips и плоские тайминги боя выше.")]
        public StrikeAnim[] strikes;

        [System.Serializable]
        public sealed class StrikeAnim
        {
            [Tooltip("Клип этого удара (Punch A/B, Kick A/B из AnimLibrary).")]
            public AnimationClip clip;
            [Tooltip("Замах: через сколько секунд от старта клипа падает урон.")]
            [Range(0.05f, 3f)] public float hitDelaySeconds = 0.2f;
            [Tooltip("Доигрыш ПОСЛЕ хита до конца анимации, сек.")]
            [Range(0.05f, 3f)] public float followSeconds = 0.2f;
            [Tooltip("Перезарядка после анимации, сек.")]
            [Range(0f, 4f)] public float cooldownSeconds = 0.2f;
        }

        [Header("Анимации (пусто = фолбэк: NpcAnimSet-ряд / процедурный взмах)")]
        [Tooltip("Клипы атаки этим предметом (несколько — случайный). Игнорируется, если задан список strikes.")]
        public AnimationClip[] attackClips;
        [Tooltip("Idle с предметом в руке (подменяет базовый Idle).")]
        public AnimationClip armedIdle;
        [Tooltip("Ходьба с предметом в руке (подменяет базовый Walk).")]
        public AnimationClip armedWalk;
        [Tooltip("Рабочий клип (рубка/добыча/стройка) — подменяет базовый Chop-клип.")]
        public AnimationClip workClip;

        public GearStats ToStats()
        {
            var stats = new GearStats
            {
                Id = gearId ?? string.Empty,
                Damage = damage,
                HitDelaySeconds = hitDelaySeconds,
                AttackDurationSeconds = attackDurationSeconds,
                CooldownSeconds = cooldownSeconds,
                AttackSpeed = attackSpeed,
                // Снятая галочка «оружие» = приоритет 0 — существующая
                // семантика GearStats «0 = никогда не оружие».
                MeleePriority = usableAsWeapon ? meleePriority : 0,
                TwoHanded = twoHanded,
                HarvestSpeedMult = harvestSpeedMult,
            };
            if (capabilities != null)
            {
                foreach (var capability in capabilities)
                {
                    stats.Capabilities |= capability;
                }
            }

            // Per-strike timing rows → the sim's variant sheet; the clip order
            // here IS the variant order the sim indexes into.
            if (strikes != null && strikes.Length > 0)
            {
                stats.StrikeVariants = new StrikeVariant[strikes.Length];
                for (var i = 0; i < strikes.Length; i++)
                {
                    var s = strikes[i];
                    stats.StrikeVariants[i] = new StrikeVariant
                    {
                        HitDelaySeconds = s.hitDelaySeconds,
                        AttackDurationSeconds = s.hitDelaySeconds + s.followSeconds,
                        CooldownSeconds = s.cooldownSeconds,
                    };
                }
            }

            return stats;
        }

        // The recipe declared on this item's card → RecipeCatalog (the craft
        // must have a goal-layer verb; unknown outputs are ignored there).
        public void ApplyRecipe()
        {
            if (!craftable || craftIngredients == null || craftIngredients.Length == 0)
            {
                return;
            }

            var inputs = new List<RecipeIngredient>();
            foreach (var ing in craftIngredients)
            {
                var id = ing?.ResolveId();
                if (!string.IsNullOrEmpty(id))
                {
                    inputs.Add(new RecipeIngredient(id, ing.count));
                }
            }

            if (inputs.Count > 0)
            {
                RecipeCatalog.Override(gearId, inputs.ToArray(), craftNeedsLitFire,
                    craftStation switch
                    {
                        CraftPlace.Campfire => "Campfire",
                        CraftPlace.Workbench => "Workbench",
                        _ => ""
                    });
            }
        }
    }
}
