#nullable enable
using System.Collections;
using UnityEngine;

namespace HexLive.UnityPresentation.Wearing
{
    /// <summary>
    /// Постоянный хозяин корутин, которые ГРУЗЯТ КОНТЕНТ.
    ///
    /// Баг #182: причёска ехала корутиной на самой актрисе, а Unity убивает
    /// корутины ВЫКЛЮЧЕННОГО объекта — молча, без исключения. Культинг
    /// восприятия гасит девушек соседних лагерей (§146), и у двух из шести
    /// корутина умерла между <c>ContentQueue.Begin</c> и <c>End</c>: очередь
    /// навсегда осталась с двумя незакрытыми задачами, а
    /// <c>_pendingHairLoads</c> — с единицей. Занавес ждал их вечно.
    ///
    /// Правило, которое из этого следует: <b>учёт загрузки не может жить на
    /// объекте, который кто-то вправе выключить</b>. Материалы и префабы
    /// приезжают одинаково нужные и погасшей, и видимой актрисе — их дорога
    /// не должна зависеть от того, смотрит ли на неё игрок.
    /// </summary>
    public sealed class ContentCoroutines : MonoBehaviour
    {
        private static ContentCoroutines? _instance;

        public static Coroutine Run(IEnumerator routine)
        {
            if (_instance == null)
            {
                var host = new GameObject("ContentCoroutines") { hideFlags = HideFlags.DontSave };
                _instance = host.AddComponent<ContentCoroutines>();
                DontDestroyOnLoad(host);
            }

            return _instance.StartCoroutine(routine);
        }

        // Enter Play Mode без domain reload сохраняет статику, а хозяин с
        // прошлой сессии уже уничтожен.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _instance = null;
    }
}
