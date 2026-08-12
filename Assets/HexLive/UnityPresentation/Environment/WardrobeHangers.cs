using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §133: точки, на которых висит одежда в домашнем гардеробе — тот же
    /// контракт, что у <see cref="DryingRackHangers"/>: единственный источник
    /// правды для рендера и для будущего префаба.
    ///
    /// <para>
    /// ⭐ ВРЕМЕННО: модели гардероба ещё нет, поэтому все восемь мест сходятся в
    /// ОДНУ точку и вещи ложатся стопкой с небольшим шагом по высоте (просьба
    /// игрока: «пока объект не смоделирован, пускай просто складываются
    /// стопкой»). Когда приедет модель с плечиками, здесь меняются только
    /// координаты — ни симуляция, ни рендер про это не знают.
    /// </para>
    /// </summary>
    public static class WardrobeHangers
    {
        /// Высота нижней вещи в стопке (world units, пол = 0).
        public const float StackBaseY = 0.42f;

        /// Шаг по высоте между вещами в стопке.
        public const float StackStepY = 0.055f;

        /// Стопка стоит чуть впереди корпуса, чтобы не тонуть в будущей модели.
        public const float StackForwardZ = 0.10f;

        /// Сколько мест в гардеробе (зеркало Spec133.WardrobeCapacity).
        public const int SlotCount = 8;

        /// Место i-й вещи (ранг по id объекта). Свёрнуто по модулю, чтобы
        /// переполнение никогда не бросало исключение — просто удвоит полку.
        public static Vector3 Slot(int index)
        {
            var wrapped = ((index % SlotCount) + SlotCount) % SlotCount;
            return new Vector3(0f, StackBaseY + wrapped * StackStepY, StackForwardZ);
        }
    }
}
