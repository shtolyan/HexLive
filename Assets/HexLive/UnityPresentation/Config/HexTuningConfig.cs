using HexLive.Simulation.Navigation;
using HexLive.Simulation.Runtime;
using UnityEngine;

namespace HexLive.UnityPresentation.Config
{
    /// <summary>
    /// Движение и «ощущение» воды: тайминг/геометрия прыжка (HexHopTuning),
    /// плавание и волны. Игровой БАЛАНС здесь больше не живёт — он разъехался
    /// по тематическим конфигам в Resources/HexLive/Balance (Character /
    /// ResourceLoop / Social / Threat, см. BalanceTuning). Прыжковые поля
    /// зеркалятся в HexHopTuning, две симуляционные ручки плавания — в
    /// MovementSystem (через [MirrorField]); ЧИСТО презентационные поля
    /// (глубина погружения, волны, посадка на кромку) помечены [MirrorIgnore] —
    /// их толкает рукописная часть HexTuning.Apply. SwimTest-сцена сохраняет
    /// сюда же кнопкой «Сохранить настройки».
    /// </summary>
    [CreateAssetMenu(menuName = "HexLive/Tuning Config", fileName = "HexTuningConfig")]
    [MirrorTarget(typeof(HexHopTuning))]
    public sealed class HexTuningConfig : ScriptableObject
    {
        [Header("Прыжок — тайминг")]
        [Tooltip("ВСЁ окно прыжка ВВЕРХ: толчок + полёт + приземление. Клип сжимается ровно в это время, сек.")]
        [Range(0.5f, 5f)] public float hopSeconds = 2f;
        [Tooltip("Окно прыжка ВНИЗ (спрыгивание) — обычно меньше, чтобы было быстрее. Тайминги толчка/посадки масштабируются пропорционально. Сек.")]
        [Range(0.2f, 5f)] public float downHopSeconds = 2f;
        [Tooltip("ТОЛЧОК: сколько в начале клипа занимает присед/замах — тело стоит, анимация уже играет, сек.")]
        [MirrorField(typeof(HexHopTuning), "TakeoffSeconds")]
        [Range(0f, 2f)] public float hopTakeoffSeconds = 0.5f;
        [Tooltip("ПРИЗЕМЛЕНИЕ: сколько в конце клипа занимает посадка ног — тело уже в точке, стоит, сек.")]
        [MirrorField(typeof(HexHopTuning), "LandingSeconds")]
        [Range(0f, 2f)] public float hopLandingSeconds = 0.5f;

        [Header("Прыжок — геометрия")]
        [Tooltip("ОТСТУП от стены (мировые единицы): взлетает за столько ДО стены и приземляется за столько ПОСЛЕ — симметрично. Больше = длиннее прыжок.")]
        [MirrorField(typeof(HexHopTuning), "EdgePadding")]
        [Range(0.1f, 1.5f)] public float hopEdgePadding = 0.3f;
        [Tooltip("СПРЫГИВАНИЕ: на сколько подпрыгивает ВВЕРХ с края перед падением (клиренс ног над кромкой). 0 = сразу вниз.")]
        [MirrorField(typeof(HexHopTuning), "DownHopUp")]
        [Range(0f, 0.8f)] public float hopDownUp = 0.2f;
        [Tooltip("СПРЫГИВАНИЕ: доля полёта, до которой она летит РОВНО и не падает. 0.5 = падает только перелетев кромку. Меньше = падает раньше (может задеть край).")]
        [MirrorField(typeof(HexHopTuning), "DownFallStartFrac")]
        [Range(0f, 0.95f)] public float hopDownFallStartFrac = 0.5f;
        [Tooltip("НЫРОК: на сколько уходит ПОД уровень плавания в нижней точке плюха, потом выныривает.")]
        [Range(0f, 1.5f)] public float divePlungeDepth = 0.35f;

        // Эти два — НАСТОЯЩИЕ симуляционные ручки (SwimSpeedFactor меняет тайминг
        // пути), а не презентационные, как утверждал старый комментарий. Они жили
        // под [MirrorIgnore] и применялись руками, из-за чего не попадали ни в
        // BalanceReflection, ни в SimData/simdata.json — headless-прогон и сервер
        // молча брали дефолты кода вместо настроенных значений (§59.3).
        // Теперь мапятся штатно; MovementSystem внесён в BalanceReflection.BalanceClasses.
        [Header("Вода — симуляция")]
        [Tooltip("Пауза после прыжка в воду: сколько секунд барахтается на месте (tread), прежде чем поплыть.")]
        [MirrorField(typeof(MovementSystem), "SwimEntryPauseSeconds")]
        [Range(0f, 15f)] public float swimEntryPauseSeconds = 0.75f;
        [Tooltip("Множитель скорости движения в глубокой воде (1 — как пешком).")]
        [MirrorField(typeof(MovementSystem), "SwimSpeedFactor")]
        [Range(0.1f, 1.5f)] public float swimSpeedFactor = 0.6f;

        [Header("Сидение на краю (ledge)")]
        [Tooltip("Прямой сдвиг попы по Y на краю — применяется ВСЕГДА (минус = ниже к земле; на верхней кромке подъём-на-ступень = 0).")]
        [MirrorIgnore]
        [Range(-1f, 1f)] public float ledgeSeatLift = 0.4f;
        [Tooltip("Сдвиг НАЗАД на кромку (к верхнему тайлу): чтобы подъём приходился на землю, а не висел над обрывом. Больше = глубже на край.")]
        [MirrorIgnore]
        [Range(0f, 1f)] public float ledgeSeatBack = 0.45f;

        [Header("Вода — визуал")]
        [Tooltip("К какой высоте берега (в ЦЕЛЫХ ступенях) поднимается море, прежде чем утонуть на waterSurfaceDrop. Вода-тайлы в симуляции на уровне 0, а берег — на 1; поэтому без этого подъёма вода стояла на ступень ниже кромки. 1 = у самых пляжей (уровень 1).")]
        [MirrorIgnore]
        [Range(0f, 3f)] public float waterShoreLevel = 1f;
        [Tooltip("УРОВЕНЬ ВОДЫ: на какую долю ступени поверхность утоплена ниже БЕРЕГА (waterShoreLevel). Меньше = вода выше. 0.1 — у самой кромки; от неё считаются и плавание, и нырок, и вылезание. Меши строятся на старте сцены — менять до запуска.")]
        [MirrorIgnore]
        [Range(0.02f, 0.6f)] public float waterSurfaceDrop = 0.1f;
        [Tooltip("На сколько корень актёра проваливается НИЖЕ поверхности воды. 0 — ноги на поверхности.")]
        [MirrorIgnore]
        [Range(-0.5f, 1.5f)] public float sinkDepth = 0.6f;
        [Tooltip("Глубина, с которой начинается бредущая походка (wade) перед полноценным плаванием.")]
        [MirrorIgnore]
        [Range(0f, 1f)] public float wadeDepth = 0.2f;
        [Tooltip("Высота тела в воде (для tread и гребков). Мировые единицы, + = вверх.")]
        [MirrorIgnore]
        [Range(-1f, 1f)] public float swimBodyLift = 0.45f;
        [Tooltip("Амплитуда волны — высота гребня. ОДНА на всё: и меш воды, и качание пловца.")]
        [MirrorIgnore]
        [Range(0f, 1f)] public float waveAmplitude = 0.1f;
        [Tooltip("Частота волны (рад/юнит). Меньше — длиннее и плавнее волна.")]
        [MirrorIgnore]
        [Range(0.05f, 3f)] public float waveFrequency = 1.4f;
        [Tooltip("Скорость бега волны по поверхности.")]
        [MirrorIgnore]
        [Range(0f, 6f)] public float waveSpeed = 2f;
    }
}
