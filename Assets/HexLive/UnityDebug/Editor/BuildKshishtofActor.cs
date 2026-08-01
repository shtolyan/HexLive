using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HexLive.UnityDebug.Editor
{
    /// <summary>
    /// §72 / §31B.3 — «умный импорт» мужского актёра Kshishtof, кодом.
    ///
    /// Префаб из molly_copy переносится как отправная точка, но игровым он не
    /// является: на нём висит чужая оснастка исходного проекта (Daz3DInstance,
    /// ActorPoint, BodyIKTargets, NavMeshAgent, камера, вложенные префабы) и
    /// НАДЕТАЯ одежда — ожерелье, боксеры, майка — тремя отдельными
    /// SkinnedMeshRenderer'ами. База обязана быть голой: одежду надевает
    /// рантайм через BodyBones.Equip.
    ///
    /// Резать это руками в YAML — верный способ тихо испортить иерархию,
    /// поэтому нормализация сделана детерминированной и повторяемой: меню можно
    /// жать сколько угодно раз, результат один и тот же.
    ///
    /// Что оставляем — ровно то, что есть на эталонном префабе Jolly:
    /// Animator (общий контроллер, root motion off), один SkinnedMeshRenderer
    /// с 17 слотами кожи, BodyBones, LookAtIK, FullBodyBipedIK.
    /// MagicaCloth сознательно НЕ вешаем: у Jolly это подпрыгивание груди.
    ///
    /// Компоненты опознаются по ПОЛНОМУ ИМЕНИ ТИПА, а не через using: иначе
    /// сборке UnityDebug пришлось бы ссылаться на FinalIK ради одной строчки
    /// отчёта, а это лишнее ребро в графе компиляции всего проекта.
    /// </summary>
    public static class BuildKshishtofActor
    {
        private const string SourcePrefab = "Assets/ImportedActors/Actors/Kshishtof/Kshishtof.prefab";
        private const string TargetPrefab = "Assets/Resources/HexLive/Actors/Kshishtof.prefab";
        private const string ControllerPath = "Assets/HexLive/UnityPresentation/Actors/HexNpcLocomotion.controller";

        // Тело. Всё остальное на префабе — одежда исходного проекта.
        private const string BodyRenderer = "Genesis3Male.Shape";

        // Что переживает срез (кроме Transform/Animator/SkinnedMeshRenderer).
        private static readonly string[] KeepTypes =
        {
            "HexLive.UnityPresentation.Wearing.BodyBones",
            "RootMotion.FinalIK.LookAtIK",
            "RootMotion.FinalIK.FullBodyBipedIK",
        };

        [MenuItem("HexLive/Actors/Build Kshishtof")]
        public static void Build()
        {
            if (Application.productName != "HexLive")
            {
                Debug.LogWarning("[Kshishtof] Не тот проект — выходим.");
                return;
            }

            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefab);
            if (source == null)
            {
                Debug.LogError($"[Kshishtof] Не найден исходный префаб: {SourcePrefab}");
                return;
            }

            var root = (GameObject)PrefabUtility.InstantiatePrefab(source);
            if (root == null)
            {
                Debug.LogError("[Kshishtof] Не удалось инстанцировать исходный префаб.");
                return;
            }

            try
            {
                PrefabUtility.UnpackPrefabInstance(
                    root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                root.name = "Kshishtof";

                var report = new List<string>();
                var genitals = FindGenitals(root, report);
                StripWornMeshes(root, genitals, report);
                StripForeignComponents(root, report);
                ReportBody(root, report);
                EnsureAnimator(root, report);
                ReportKeptRig(root, report);

                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(TargetPrefab) ?? string.Empty);
                PrefabUtility.SaveAsPrefabAsset(root, TargetPrefab, out var ok);
                AssetDatabase.SaveAssets();

                Debug.Log(ok
                    ? $"[Kshishtof] Готов: {TargetPrefab}\n" + string.Join("\n", report)
                    : $"[Kshishtof] СОХРАНИТЬ НЕ УДАЛОСЬ: {TargetPrefab}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// На что смотрит BodyBones.genitals — графт-меш §72, который обязан
        /// пережить срез. Поле приватное [SerializeField], поэтому читаем через
        /// SerializedObject, а не по имени объекта: имя у него даз-овское и
        /// меняется от экспорта к экспорту.
        /// </summary>
        private static GameObject FindGenitals(GameObject root, List<string> report)
        {
            var bones = root.GetComponentsInChildren<Component>(true)
                .FirstOrDefault(c => c != null &&
                    c.GetType().FullName == "HexLive.UnityPresentation.Wearing.BodyBones");
            if (bones == null)
            {
                return null;
            }

            var field = new SerializedObject(bones).FindProperty("genitals");
            var value = field?.objectReferenceValue as GameObject;
            report.Add(value == null
                ? "  genitals: ссылка пуста (тело останется гладким)"
                : $"  genitals: '{value.name}' — сохраняем от среза");
            return value;
        }

        /// <summary>
        /// Одежда исходного проекта. Базовый актёр голый — §31B.3.
        ///
        /// Исключение — графт гениталий: это тоже SkinnedMeshRenderer, но не
        /// одежда, а часть тела, и §72 включает/выключает его по занятости
        /// слота Pelvis. Без этой оговорки срез уносил его вместе с боксерами,
        /// и BodyBones.genitals оставался висеть на пустом обрубке.
        /// </summary>
        private static void StripWornMeshes(GameObject root, GameObject genitals, List<string> report)
        {
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true).ToArray())
            {
                if (renderer == null || renderer.gameObject.name == BodyRenderer)
                {
                    continue;
                }

                if (genitals != null &&
                    (renderer.gameObject == genitals ||
                     renderer.transform.IsChildOf(genitals.transform)))
                {
                    report.Add($"  оставлен графт: {renderer.gameObject.name}");
                    continue;
                }

                report.Add($"  срезана одежда: {renderer.gameObject.name}");
                Object.DestroyImmediate(renderer.gameObject);
            }
        }

        /// <summary>
        /// Оснастка чужого проекта. Удаляются только КОМПОНЕНТЫ, не объекты:
        /// пустой GameObject здесь — это, как правило, кость скелета, и снос
        /// «пустышек» разобрал бы актёра.
        ///
        /// Скрипты molly_copy в этом проекте не существуют вовсе, поэтому
        /// приходят как «missing script» и ловятся отдельным проходом.
        /// </summary>
        private static void StripForeignComponents(GameObject root, List<string> report)
        {
            var doomed = new List<Component>();
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null) // missing script — снимаем ниже
                {
                    continue;
                }

                if (component is Transform || component is Animator || component is SkinnedMeshRenderer)
                {
                    continue;
                }

                var fullName = component.GetType().FullName ?? string.Empty;
                if (KeepTypes.Contains(fullName))
                {
                    continue;
                }

                doomed.Add(component);
            }

            foreach (var component in doomed)
            {
                if (component == null)
                {
                    continue;
                }

                report.Add($"  снят компонент: {component.GetType().Name} на {component.gameObject.name}");
                Object.DestroyImmediate(component);
            }

            var missing = 0;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                missing += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(transform.gameObject);
            }

            if (missing > 0)
            {
                report.Add($"  снято битых скриптов (molly_copy): {missing}");
            }
        }

        private static void ReportBody(GameObject root, List<string> report)
        {
            var body = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .FirstOrDefault(r => r != null && r.gameObject.name == BodyRenderer);
            if (body == null)
            {
                Debug.LogError($"[Kshishtof] Нет рендерера тела '{BodyRenderer}'.");
                return;
            }

            // Пейнтер кожи (§40.8-G) кормит только слоты ПЕРВОГО кожаного
            // рендерера, поэтому все 17 обязаны сидеть на этом одном.
            report.Add($"  тело: {body.sharedMaterials.Length} слотов материалов на одном рендерере");
        }

        private static void EnsureAnimator(GameObject root, List<string> report)
        {
            var animator = root.GetComponent<Animator>() ?? root.AddComponent<Animator>();
            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ControllerPath);
            if (controller == null)
            {
                Debug.LogError($"[Kshishtof] Не найден контроллер {ControllerPath}");
            }
            else
            {
                animator.runtimeAnimatorController = controller;
                report.Add("  контроллер: HexNpcLocomotion (был контроллер molly_copy)");
            }

            // Позицию гонит симуляция, не корневое движение клипа.
            animator.applyRootMotion = false;
            if (animator.avatar == null || !animator.avatar.isHuman)
            {
                Debug.LogWarning("[Kshishtof] Аватар не humanoid — ретаргет клипов не заведётся.");
            }
        }

        private static void ReportKeptRig(GameObject root, List<string> report)
        {
            foreach (var kept in KeepTypes)
            {
                var found = root.GetComponentsInChildren<Component>(true)
                    .Any(c => c != null && (c.GetType().FullName ?? string.Empty) == kept);
                report.Add($"  {kept.Split('.').Last()}: {(found ? "есть" : "НЕТ")}");
            }
        }
    }
}
