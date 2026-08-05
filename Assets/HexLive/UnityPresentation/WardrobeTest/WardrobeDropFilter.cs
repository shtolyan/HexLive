#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HexLive.UnityPresentation.WardrobeTest
{
    /// <summary>
    /// Which garments the wardrobe browser leaves out: the ones already checked.
    /// </summary>
    /// <remarks>
    /// The wardrobe is imported in batches, and every batch is reviewed in this
    /// scene before the next one is fetched. Once a batch has passed, its things
    /// only make the list longer — the whole point of the pause is to look at
    /// what is NEW. So a finished batch is hidden here rather than deleted:
    /// nothing is lost, the plan is one flag away from bringing it back, and
    /// the game itself is untouched (this is browser-only, editor-only).
    ///
    /// The plan lives beside the drop manifests it names — `import-plan.json` in
    /// `Assets/Editor/WearDrops` — because a plan under `Temp/` is gitignored,
    /// and a checklist nobody else can see is not a checklist.
    /// </remarks>
    internal static class WardrobeDropFilter
    {
        private const string DropRoot = "Assets/Editor/WearDrops";
        private const string PlanPath = DropRoot + "/import-plan.json";

        private static HashSet<string> _hidden;

        /// <summary>Prototype ids the browser should skip.</summary>
        internal static HashSet<string> Hidden
        {
            get
            {
                if (_hidden != null)
                {
                    return _hidden;
                }

                _hidden = new HashSet<string>();
                if (!File.Exists(PlanPath))
                {
                    return _hidden;
                }

                Plan plan;
                try
                {
                    plan = JsonUtility.FromJson<Plan>(File.ReadAllText(PlanPath));
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Wardrobe] не читается план импорта: {e.Message}");
                    return _hidden;
                }

                if (plan?.batches == null)
                {
                    return _hidden;
                }

                // ⭐ Прятать отсмотренное имеет смысл, только пока есть
                // НЕОТСМОТРЕННОЕ. Когда закрыты все заходы — а это и есть конец
                // импорта — правило «скрыть готовые» прячет гардероб целиком, и
                // человек открывает сцену, в которой нет ни одной вещи. Тогда
                // фильтр отключается сам: смотреть заново на всё лучше, чем
                // смотреть на пустоту.
                var unreviewed = false;
                foreach (var batch in plan.batches)
                {
                    if (batch != null && !batch.done && !batch.excluded)
                    {
                        unreviewed = true;
                        break;
                    }
                }

                if (!unreviewed)
                {
                    Debug.Log("[Wardrobe] все заходы импорта закрыты — фильтр «скрыть отсмотренное» " +
                              "выключен, показан весь гардероб.");
                    return _hidden;
                }

                foreach (var batch in plan.batches)
                {
                    if (batch == null || !batch.done || string.IsNullOrEmpty(batch.drop))
                    {
                        continue;
                    }

                    var manifest = $"{DropRoot}/{batch.drop}.json";
                    if (!File.Exists(manifest))
                    {
                        Debug.LogWarning(
                            $"[Wardrobe] заход {batch.batch} помечен готовым, но манифеста «{batch.drop}» нет");
                        continue;
                    }

                    var drop = JsonUtility.FromJson<Drop>(File.ReadAllText(manifest));
                    if (drop?.garments == null)
                    {
                        continue;
                    }

                    foreach (var garment in drop.garments)
                    {
                        if (garment != null && !string.IsNullOrEmpty(garment.simId))
                        {
                            _hidden.Add(garment.simId);
                        }
                    }
                }

                return _hidden;
            }
        }

        /// <summary>Drop the cache — the plan changed under us.</summary>
        internal static void Forget() => _hidden = null;

        // JsonUtility wants concrete fields; anything else in the files is ignored.
        [System.Serializable]
        private sealed class Plan
        {
            public Batch[] batches;
        }

        [System.Serializable]
        private sealed class Batch
        {
            public int batch;
            public bool done;
            public bool excluded;
            public string drop;
        }

        [System.Serializable]
        private sealed class Drop
        {
            public Garment[] garments;
        }

        [System.Serializable]
        private sealed class Garment
        {
            public string simId;
        }
    }
}
#endif
