using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Config that maps NPC actions to real animation clips (replacing the old
    /// procedural shoulder poses). Clips are swapped into a small set of generic
    /// animator states via an AnimatorOverrideController at runtime, so:
    ///   • talking picks a RANDOM clip and alternates (short back-and-forth);
    ///   • death picks a RANDOM clip;
    ///   • gather/drink play their dedicated clips;
    ///   • ATTACK + armed IDLE come from the equipped weapon's entry — this is
    ///     the weapon architecture: one row per weapon (spear now, knife/etc.
    ///     later), each with its own idle and one-or-more attack moves.
    /// Assign the imported AnimLibrary clips in the inspector (Humanoid rig).
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/NPC Anim Set", fileName = "NpcAnimSet")]
    public sealed class NpcAnimSet : ScriptableObject
    {
        [Header("Общение — случайный клип, чередование по очереди")]
        [Tooltip("Talking-варианты (X Bot@Talking/Talking2/talking3/talking4). Выбирается случайный, чередуется по таймеру.")]
        public AnimationClip[] talk;

        [Header("Смерть — случайный клип")]
        [Tooltip("Death-варианты (death2/death3/Death From Back Headshot). Проигрывается один раз, замирает на последнем кадре.")]
        public AnimationClip[] death;

        [Header("Взаимодействия")]
        [Tooltip("Сбор любых предметов/воды (X Bot@Gathering Objects).")]
        public AnimationClip gather;
        [Tooltip("Питьё воды (X Bot@Drinking).")]
        public AnimationClip drink;

        [Header("Потеря ноги (§50) — заменяют стоячие клипы")]
        [Tooltip("Лежачий айдл без ноги (Prone Idle). На него подменяются Idle/Gather/Talk/Dress/Turn/Crouch у безногой.")]
        public AnimationClip proneIdle;
        [Tooltip("Ползание (Zombie Crawl). На него подменяется Walk у безногой.")]
        public AnimationClip crawl;
        [Tooltip("§Wardrobe: надевание одежды — второй такт (после газеринга, одежда в руке). Пусто = базовый клип состояния Dress.")]
        public AnimationClip dress;
        [Tooltip("§Wardrobe: снятие одежды — первый такт (перед газерингом, одежда ещё на теле). Пусто = базовый клип состояния Undress.")]
        public AnimationClip undress;

        [Header("Вооружённый idle/ходьба — если в руке инструмент/оружие (tool.*)")]
        [Tooltip("Стойка с предметом в руке (Standing Idle). Подменяет базовый Idle, пока в руке любой tool.* (топор/нож/молоток/копьё…). Пусто = обычный idle.")]
        public AnimationClip armedIdle;
        [Tooltip("Ходьба с предметом в руке (Standing Walk Forward). Подменяет базовый Walk, пока в руке любой tool.*. Пусто = обычная ходьба.")]
        public AnimationClip armedWalk;

        [Header("Оружие — idle + атака подменяются под оружие (архитектура)")]
        [Tooltip("По строке на оружие: id (tool.spear, tool.knife…), armed-idle, и один-или-несколько ударов. Копьё → Bayonet Stab сейчас; остальное добавляется строкой.")]
        public WeaponAnim[] weapons;

        /// <summary>Per-weapon animation set: armed idle + attack move(s).</summary>
        [System.Serializable]
        public sealed class WeaponAnim
        {
            [Tooltip("Инвентарный id оружия, напр. tool.spear, tool.knife.")]
            public string weaponId;
            [Tooltip("Idle-поза с оружием в руках (опционально — если пусто, обычный idle).")]
            public AnimationClip idle;
            [Tooltip("Удар(ы). Несколько = чередуются/случайны. Копьё: Bayonet Stab.")]
            public AnimationClip[] attacks;
        }

        /// <summary>The weapon row for an inventory id, or null (bare-handed).</summary>
        public WeaponAnim WeaponFor(string weaponId)
        {
            if (weapons == null || string.IsNullOrEmpty(weaponId))
            {
                return null;
            }

            foreach (var w in weapons)
            {
                if (w != null && w.weaponId == weaponId)
                {
                    return w;
                }
            }

            return null;
        }
    }
}
