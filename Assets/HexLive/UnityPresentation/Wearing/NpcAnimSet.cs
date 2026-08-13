using System.Collections.Generic;
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
        [Tooltip("Лежачий айдл без ноги (X Bot@Fallen Idle — упавшая, но живая и в сознании; прежний LayingBelly читался как сон). На него подменяются Idle/Gather/Talk/Dress/Turn/Crouch у безногой.")]
        public AnimationClip proneIdle;
        [Tooltip("Ползание (X Bot@Crawling). На него подменяются все gait-слоты у лежащей.")]
        public AnimationClip crawl;
        [Tooltip("§Wardrobe: надевание одежды — второй такт (после газеринга, одежда в руке). Пусто = базовый клип состояния Dress.")]
        public AnimationClip dress;
        [Tooltip("§Wardrobe: снятие одежды — первый такт (перед газерингом, одежда ещё на теле). Пусто = базовый клип состояния Undress.")]
        public AnimationClip undress;

        [Header("§81 Безделье — случайная сценка, когда делать нечего")]
        [Tooltip("Клипы «просто существует»: сальса, приседания, джампинг-джеки, шарит по карманам. " +
                 "Выбирается случайный и играется РЕДКО — это приправа, а не занятие.")]
        public AnimationClip[] idleFidgets;

        [Header("§81 Такты сцены абьюза")]
        [Tooltip("Отшатнулась, когда на неё наехали (X Bot@Rejected).")]
        public AnimationClip rejected;
        [Tooltip("Плачет (X Bot@Crying) — такт «она плачет» и такт «сдалась».")]
        public AnimationClip crying;
        [Tooltip("Грустная походка (X Bot@Sad Walk). Подменяет обычный шаг на несколько минут после сцены.")]
        public AnimationClip sadWalk;

        [Header("§105 Сон — поза на каждую девушку своя")]
        [Tooltip("Позы сна (Sleep, Sleeping Idle). Вариант выбирается по id колонистки и держится всю жизнь: " +
                 "четыре тела в одинаковой позе у костра читались как копипаста. " +
                 "Пусто = у всех авторский клип состояния Sleep. " +
                 "Заполняется меню HexLive ▸ Actors ▸ Assign Sleep Poses.")]
        public AnimationClip[] sleep;

        [Header("Вооружённый idle/ходьба — если в руке инструмент/оружие (tool.*)")]
        [Tooltip("Стойка с предметом в руке (Standing Idle). Подменяет базовый Idle, пока в руке любой tool.* (топор/нож/молоток/копьё…). Пусто = обычный idle.")]
        public AnimationClip armedIdle;
        [Tooltip("Ходьба с предметом в руке (Standing Walk Forward). Подменяет базовый Walk, пока в руке любой tool.*. Пусто = обычная ходьба.")]
        public AnimationClip armedWalk;

        [Header("§78 Мужская локомоция — своя походка мужскому телу")]
        [Tooltip("Комплект локомоции для мужчин (Kshishtof/Tonny): стойка, шаг, трусца, бег, сидение. " +
                 "Подменяет базовые клипы контроллера, авторски женские. Пусто = мужчина ходит как девушки. " +
                 "Заполняется меню HexLive ▸ Actors ▸ Build Male Locomotion.")]
        public LocomotionSet male;

        /// <summary>
        /// One body's locomotion takes. The four moving slots mirror the
        /// animator's own structure — Idle plus the three §71 GaitBlend slots
        /// (walk / slow run / run) — and sit is the Sit state's clip. Anything
        /// left empty falls through to the controller's authored take, so a
        /// half-filled set is a valid state, not a broken one.
        /// </summary>
        [System.Serializable]
        public sealed class LocomotionSet
        {
            [Tooltip("Стойка на месте (Mixamo Breathing Idle).")]
            public AnimationClip idle;
            [Tooltip("Шаг — слот 0 блендера походки (Mixamo Walking).")]
            public AnimationClip walk;
            [Tooltip("Трусца — слот 0.5 блендера походки (Mixamo Running slow).")]
            public AnimationClip slowRun;
            [Tooltip("Бег — слот 1 блендера походки (Mixamo Running).")]
            public AnimationClip run;
            [Tooltip("Сидение — на пеньке и на краю гекса это один и тот же клип (Mixamo Sitting).")]
            public AnimationClip sit;
        }

        [Header("§71 Калибровка шага — сколько земли покрывает КАЖДЫЙ клип")]
        [Tooltip("По строке на клип локомоции: сколько ростов тела в секунду он проходит на авторской " +
                 "скорости 1×. Базовый женский шаг ≈ 0.76, трусца ≈ 1.52, бег ≈ 2.58. " +
                 "Вид делит фактическую скорость на это число и получает темп проигрывания — поэтому " +
                 "ноги стоят на земле на ЛЮБОЙ скорости. Клипа нет в таблице = берётся значение слота " +
                 "по умолчанию, то есть поведение до §71.5. Заполняется в сцене LocomotionTest.")]
        public ClipStride[] strides;

        /// <summary>
        /// §71.5: how much ground one clip covers at 1× playback, in body
        /// heights per second. Before this the whole rig had ONE such number
        /// (the base walk's 0.76) plus two relative cadences for the run slots
        /// — so every clip swapped into the walk slot (the sad walk, the armed
        /// walk, the male set, the §50 crawl) was played as if it had the base
        /// walk's stride, and its feet slid by exactly the ratio between them.
        /// </summary>
        [System.Serializable]
        public sealed class ClipStride
        {
            [Tooltip("Клип локомоции (шаг/трусца/бег/грустный шаг/ходьба с инструментом/ползание).")]
            public AnimationClip clip;
            [Tooltip("Ростов тела в секунду на скорости проигрывания 1×.")]
            [Range(0.05f, 6f)] public float bodyHeightsPerSec = 0.76f;
        }

        private Dictionary<AnimationClip, float> _strideCache;

        /// <summary>§71.5: this clip's authored ground pace, or <paramref name="fallback"/>
        /// when the table says nothing about it (which reproduces the single-constant
        /// behaviour it replaced).</summary>
        public float StrideFor(AnimationClip clip, float fallback)
        {
            if (clip == null)
            {
                return fallback;
            }

            if (_strideCache == null)
            {
                RebuildStrideCache();
            }

            return _strideCache.TryGetValue(clip, out var bhps) && bhps > 0.001f ? bhps : fallback;
        }

        /// <summary>Re-read <see cref="strides"/> into the lookup. The tuning scene edits
        /// the array live, so it needs a way to say "I changed it".</summary>
        public void RebuildStrideCache()
        {
            _strideCache ??= new Dictionary<AnimationClip, float>();
            _strideCache.Clear();
            if (strides == null)
            {
                return;
            }

            foreach (var s in strides)
            {
                if (s != null && s.clip != null)
                {
                    _strideCache[s.clip] = s.bodyHeightsPerSec;
                }
            }
        }

        /// <summary>Write one clip's calibration, adding the row if it is new — the
        /// tuning scene's slider, and the thing its Save button persists.</summary>
        public void SetStride(AnimationClip clip, float bodyHeightsPerSec)
        {
            if (clip == null)
            {
                return;
            }

            if (strides != null)
            {
                foreach (var s in strides)
                {
                    if (s != null && s.clip == clip)
                    {
                        s.bodyHeightsPerSec = bodyHeightsPerSec;
                        RebuildStrideCache();
                        return;
                    }
                }
            }

            var grown = new ClipStride[(strides?.Length ?? 0) + 1];
            for (var i = 0; i < grown.Length - 1; i++)
            {
                grown[i] = strides[i];
            }

            grown[^1] = new ClipStride { clip = clip, bodyHeightsPerSec = bodyHeightsPerSec };
            strides = grown;
            RebuildStrideCache();
        }

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
