using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// The single saved home for the values we used to hand-tune as code
    /// constants (hop timing/geometry, swim & water feel). The game applies
    /// this asset at startup (HexTuning.LoadAndApply); the SwimTest scene's
    /// "Сохранить настройки" button writes the live-tuned values back here.
    /// One asset lives in Resources/HexLive so runtime code can load it.
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Tuning Config", fileName = "HexTuningConfig")]
    public sealed class HexTuningConfig : ScriptableObject
    {
        [Header("Прыжок — тайминг")]
        [Tooltip("ВСЁ окно прыжка: толчок + полёт + приземление. Клип сжимается ровно в это время, сек.")]
        [Range(0.5f, 5f)] public float hopSeconds = 2f;
        [Tooltip("ТОЛЧОК: сколько в начале клипа занимает присед/замах — тело стоит, анимация уже играет, сек.")]
        [Range(0f, 2f)] public float hopTakeoffSeconds = 0.5f;
        [Tooltip("ПРИЗЕМЛЕНИЕ: сколько в конце клипа занимает посадка ног — тело уже в точке, стоит, сек.")]
        [Range(0f, 2f)] public float hopLandingSeconds = 0.5f;

        [Header("Прыжок — геометрия")]
        [Tooltip("ОТСТУП от стены (мировые единицы): взлетает за столько ДО стены и приземляется за столько ПОСЛЕ — симметрично. Больше = длиннее прыжок.")]
        [Range(0.1f, 1.5f)] public float hopEdgePadding = 0.3f;
        [Tooltip("СПРЫГИВАНИЕ: на сколько подпрыгивает ВВЕРХ с края перед падением (клиренс ног над кромкой). 0 = сразу вниз.")]
        [Range(0f, 0.8f)] public float hopDownUp = 0.2f;
        [Tooltip("СПРЫГИВАНИЕ: доля полёта, до которой она летит РОВНО и не падает. 0.5 = падает только перелетев кромку. Меньше = падает раньше (может задеть край).")]
        [Range(0f, 0.95f)] public float hopDownFallStartFrac = 0.5f;
        [Tooltip("НЫРОК: на сколько уходит ПОД уровень плавания в нижней точке плюха, потом выныривает.")]
        [Range(0f, 1.5f)] public float divePlungeDepth = 0.35f;

        [Header("Вода — симуляция")]
        [Tooltip("Пауза после прыжка в воду: сколько секунд барахтается на месте (tread), прежде чем поплыть.")]
        [Range(0f, 15f)] public float swimEntryPauseSeconds = 0.75f;
        [Tooltip("Множитель скорости движения в глубокой воде (1 — как пешком).")]
        [Range(0.1f, 1.5f)] public float swimSpeedFactor = 0.6f;

        [Header("Сидение на краю (ledge)")]
        [Tooltip("Подъём попы на верхнюю ступень, когда сидит на краю (мировые единицы, ступень ~0.55 высотой).")]
        [Range(0f, 1f)] public float ledgeSeatLift = 0.4f;
        [Tooltip("Сдвиг НАЗАД на кромку (к верхнему тайлу): чтобы подъём приходился на землю, а не висел над обрывом. Больше = глубже на край.")]
        [Range(0f, 1f)] public float ledgeSeatBack = 0.45f;

        [Header("Вода — визуал")]
        [Tooltip("На сколько корень актёра проваливается НИЖЕ поверхности воды. 0 — ноги на поверхности.")]
        [Range(-0.5f, 1.5f)] public float sinkDepth = 0.6f;
        [Tooltip("Глубина, с которой начинается бредущая походка (wade) перед полноценным плаванием.")]
        [Range(0f, 1f)] public float wadeDepth = 0.2f;
        [Tooltip("Высота тела в воде (для tread и гребков). Мировые единицы, + = вверх.")]
        [Range(-1f, 1f)] public float swimBodyLift = 0.45f;
        [Tooltip("Амплитуда волны — высота гребня. ОДНА на всё: и меш воды, и качание пловца.")]
        [Range(0f, 1f)] public float waveAmplitude = 0.1f;
        [Tooltip("Частота волны (рад/юнит). Меньше — длиннее и плавнее волна.")]
        [Range(0.05f, 3f)] public float waveFrequency = 1.4f;
        [Tooltip("Скорость бега волны по поверхности.")]
        [Range(0f, 6f)] public float waveSpeed = 2f;
    }
}
