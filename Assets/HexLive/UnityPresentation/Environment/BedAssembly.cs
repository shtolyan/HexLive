#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Content;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// Spec §54.2: toggle-assembles a bed from the assembled prefab's OWN piece
    /// children (<c>log_*</c> / <c>stick_*</c> / <c>rope_*</c> / <c>leaf_*</c>).
    /// The canonical bed_basic_final prefab, authored in Blender at
    /// 1:1) renders BOTH the finished bed (all pieces on) and the progressive
    /// build-site (the first N pieces of each material on, per delivered count) —
    /// so the mat visibly grows into exactly the finished piece as resources are
    /// hauled in, with no separate slot table to keep in sync.
    /// </summary>
    public sealed class BedAssembly : MonoBehaviour
    {
        // Tuned by the player in BedSleepPoseTest: authored 0.24 with slider
        // -0.30 = -0.06. This is the animated BODY ROOT, not the bed surface;
        // the Sleep clip's baked body offset then rests the body on the leaves.
        // Measured against the shipped bed.basic: root sits at floor+0.1075,
        // leaf surface is ~0.27 wu above it, while the sleeping skin extends
        // ~0.10-0.13 wu below the animated body root. +0.35 places the skin on
        // the leaves; the former -0.06 marker was below even the bed logs.
        public const float SleepRootLocalY = 0.37f;
        // The authored mattress stopped short of its production junction footprint,
        // leaving an obvious empty strip at the feet. Keep width/height unchanged
        // and extend only the bed-local longitudinal axis to nearly meet its nodes.
        public const float BedWidthScale = 1.139507f; // 0.570000 -> 0.649519 wu
        public const float BedLengthScale = 1.056338f; // 1.420000 -> 1.500000 wu
        private readonly List<GameObject> _logs = new();
        private readonly List<GameObject> _sticks = new();
        private readonly List<GameObject> _ropes = new();
        private readonly List<GameObject> _leaves = new();
        private readonly List<GameObject> _stones = new(); // §54.14: campfire ring
        private readonly List<GameObject> _boards = new(); // §119: workbench braces/top
        private bool _scanned;

        private void Scan()
        {
            if (_scanned)
            {
                return;
            }

            _scanned = true;

            // §54.12 / bug #60: pieces live either in a flat model or inside
            // numbered STAGE groups ("1".."5"). Native FBX import may insert
            // one or more model-root transforms above those nodes, so scanning
            // only direct children makes every renderer stay active and the
            // structure appears complete before a single resource is hauled.
            // Find stages recursively, then find logical pieces recursively
            // inside each stage. A named piece is a boundary: compound pieces
            // (a forked post or rope lashing) toggle as one delivered resource.
            var stages = new List<Transform>();
            CollectStageGroups(transform, stages);
            if (stages.Count == 0)
            {
                var flat = new List<Transform>();
                CollectLogicalPieces(transform, flat);
                AddSorted(flat);
                return;
            }

            stages.Sort((a, b) => int.Parse(a.name).CompareTo(int.Parse(b.name)));
            foreach (var stage in stages)
            {
                var pieces = new List<Transform>();
                CollectLogicalPieces(stage, pieces);
                AddSorted(pieces);
            }
        }

        private static void CollectStageGroups(Transform root, List<Transform> stages)
        {
            foreach (Transform child in root)
            {
                if (int.TryParse(child.name, out _))
                {
                    stages.Add(child);
                    continue;
                }

                CollectStageGroups(child, stages);
            }
        }

        private static void CollectLogicalPieces(Transform root, List<Transform> pieces)
        {
            foreach (Transform child in root)
            {
                if (IsLogicalPiece(child.name))
                {
                    pieces.Add(child);
                    continue;
                }

                CollectLogicalPieces(child, pieces);
            }
        }

        private void AddSorted(List<Transform> pieces)
        {
            pieces.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            foreach (var piece in pieces)
            {
                AddPiece(piece);
            }
        }

        private static bool IsLogicalPiece(string name) =>
            name.StartsWith("log_") || name.StartsWith("stick_") ||
            name.StartsWith("rope_") || name.StartsWith("leaf_") ||
            name.StartsWith("stone_") || name.StartsWith("board_");

        private void AddPiece(Transform t)
        {
            var n = t.name;
            if (n.StartsWith("log_")) _logs.Add(t.gameObject);
            else if (n.StartsWith("stick_")) _sticks.Add(t.gameObject);
            else if (n.StartsWith("rope_")) _ropes.Add(t.gameObject);
            else if (n.StartsWith("leaf_")) _leaves.Add(t.gameObject);
            else if (n.StartsWith("stone_")) _stones.Add(t.gameObject);
            else if (n.StartsWith("board_")) _boards.Add(t.gameObject);
        }

        /// Show the whole bed (every piece on).
        public void ApplyAll()
        {
            Scan();
            Apply(_logs.Count, _sticks.Count, _ropes.Count, _leaves.Count, _stones.Count,
                _boards.Count);
        }

        /// Show only the delivered pieces: the first N of each material on, rest off.
        public void Apply(int logs, int sticks, int ropes, int leaves, int stones = 0,
            int boards = 0)
        {
            Scan();
            Toggle(_logs, logs);
            Toggle(_sticks, sticks);
            Toggle(_ropes, ropes);
            Toggle(_leaves, leaves);
            Toggle(_stones, stones);
            Toggle(_boards, boards);
        }

        /// <summary>
        /// The canonical bed is erected as a frame, not as four independent
        /// resource piles. Delivered later materials stay hidden until the
        /// piece below them is complete: logs → slats → lashings → leaves.
        /// </summary>
        public void ApplyBedStages(int logs, int sticks, int ropes, int leaves)
        {
            Scan();
            var frameReady = logs >= _logs.Count;
            var slatsReady = frameReady && sticks >= _sticks.Count;
            var lashingsReady = slatsReady && ropes >= _ropes.Count;

            Toggle(_logs, logs);
            Toggle(_sticks, frameReady ? sticks : 0);
            Toggle(_ropes, slatsReady ? ropes : 0);
            Toggle(_leaves, lashingsReady ? leaves : 0);
            Toggle(_stones, 0);
            Toggle(_boards, 0);
        }

        private static void Toggle(List<GameObject> list, int on)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var piece = list[i];
                if (piece == null) // destroyed externally — don't stall the render loop
                {
                    continue;
                }

                var active = i < on;
                if (piece.activeSelf != active)
                {
                    piece.SetActive(active);
                }
            }
        }

        // ── Factory ────────────────────────────────────────────────────────────
        // Which assembled prefab renders each staged product.
        private static string PrefabPath(string product) => "HexLive/Objects/" + product switch
        {
            "bed.basic" => "bed_basic_final_native",
            // drying rack is assembled directly from approved native
            // resource.stick + rope_lashing assets above.
            "station.drying_rack" => "drying_rack_final",
            "campfire.spot" => "campfire_final_native",
            "station.water_collector" => "water_collector_final_native",
            ContentIds.Workbench => "station.workbench",
            _ => product
        };

        /// §35.5B/§54.14: every product rendered by a staged assembled prefab —
        /// the beds, the drying rack, the campfire and the water collector share
        /// the grow-in-place build-site view.
        public static bool IsAssembled(string product) =>
            product is "bed.basic" or "station.drying_rack"
                or "campfire.spot" or "station.water_collector" or ContentIds.Workbench;

        private static GameObject? Instantiate(string product, out BedAssembly asm)
        {
            asm = null!;
            if (product == "station.drying_rack")
            {
                var rack = BuildNativeDryingRack();
                if (HasExpectedDryingRack(rack))
                {
                    asm = rack.GetComponent<BedAssembly>() ?? rack.AddComponent<BedAssembly>();
                    return rack;
                }
                DestroyRuntimeObject(rack);
            }

            // Never fall back to drying_rack_final: its four nested stands are
            // legacy stick_final→GLB wrappers. If the native assembly contract
            // fails, use the emergency primitive assembly below instead.
            var prefab = product == "station.drying_rack"
                ? null
                : HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>(PrefabPath(product));
            GameObject? go = null;
            if (prefab != null)
            {
                go = Object.Instantiate(prefab);
                // A Resources prefab that points at a glTF ScriptedImporter can
                // survive a Player build as an empty hierarchy.  A non-null
                // wrapper is not proof that its mesh sub-assets were packed.
                if (!ObjectFit.HasRenderableGeometry(go))
                {
                    DestroyRuntimeObject(go);
                    go = null;
                }
                else
                {
                    // Simulation yaw belongs to an identity presentation root.
                    // Native FBX roots carry Blender's X=-90° import rotation;
                    // assigning yaw directly to them makes a bed stand upright.
                    var model = go;
                    go = new GameObject($"{product} (native assembly)");
                    model.name = $"{product} model";
                    model.transform.SetParent(go.transform, false);
                }
            }

            go ??= BuildSafeFallback(product);
            if (product == ContentIds.BedBasic)
                go.transform.localScale = new Vector3(BedWidthScale, 1f, BedLengthScale);
            asm = go.GetComponent<BedAssembly>() ?? go.AddComponent<BedAssembly>();
            return go;
        }

        private static GameObject BuildNativeDryingRack()
        {
            var root = new GameObject("drying_rack_final (native assembly)");
            var stick = WorldPropResources.Load("resource.stick");
            var rope = HexLive.UnityPresentation.Content.AtomicResources.Load<GameObject>("HexLive/Objects/rope_lashing");
            if (stick == null || rope == null) return root;

            AddNative(root.transform, stick, "stick_00", new Vector3(-0.44f, 0.3675f, 0f),
                Quaternion.Euler(0f, 0f, 90f));
            AddNative(root.transform, stick, "stick_01", new Vector3(0.44f, 0.3675f, 0f),
                Quaternion.Euler(0f, 0f, 90f));
            AddNative(root.transform, stick, "stick_02", new Vector3(0f, 0.78f, 0.055f),
                Quaternion.identity);
            AddNative(root.transform, stick, "stick_03", new Vector3(0f, 0.50f, -0.055f),
                Quaternion.identity);

            var joints = new[]
            {
                new Vector3(-0.44f, 0.78f, 0.055f), new Vector3(0.44f, 0.78f, 0.055f),
                new Vector3(-0.44f, 0.50f, -0.055f), new Vector3(0.44f, 0.50f, -0.055f)
            };
            for (var i = 0; i < joints.Length; i++)
            {
                AddNative(root.transform, rope, $"rope_{i:00}", joints[i], Quaternion.identity);
            }
            return root;
        }

        private static void AddNative(Transform parent, GameObject prefab, string name,
            Vector3 position, Quaternion rotation)
        {
            var piece = Object.Instantiate(prefab, parent);
            piece.name = name;
            piece.transform.localPosition = position;
            piece.transform.localRotation = rotation;
            foreach (var collider in piece.GetComponentsInChildren<Collider>())
                DestroyRuntimeObject(collider);
        }

        private static bool HasExpectedDryingRack(GameObject rack)
        {
            var sticks = 0;
            var ropes = 0;
            foreach (Transform child in rack.transform)
            {
                if (!ObjectFit.HasRenderableGeometry(child.gameObject)) continue;
                if (child.name.StartsWith("stick_")) sticks++;
                if (child.name.StartsWith("rope_")) ropes++;
            }
            return sticks == 4 && ropes == 4;
        }

        /// <summary>
        /// Build-safe last resort for every assembled product. These pieces use
        /// Unity meshes and URP materials only, so a stripped glTF dependency can
        /// never turn a real simulation object into an invisible empty wrapper.
        /// Names deliberately follow the normal log_/stick_/rope_/leaf_/stone_
        /// contract, therefore partial sites and finished structures share the
        /// exact same staging code.
        /// </summary>
        private static GameObject BuildSafeFallback(string product)
        {
            var root = new GameObject($"{product} (build-safe fallback)");
            switch (product)
            {
                case "campfire.spot":
                    for (var i = 0; i < 9; i++)
                    {
                        var angle = i * 30f;
                        AddPiece(root.transform, $"stick_{i:00}", PrimitiveType.Cylinder,
                            new Vector3(0.035f, 0.34f, 0.035f),
                            new Vector3(0f, 0.08f + (i % 2) * 0.035f, 0f),
                            new Vector3(90f, angle, 0f), new Color(0.48f, 0.27f, 0.14f));
                    }
                    for (var i = 0; i < 18; i++)
                    {
                        var radians = i * Mathf.PI * 2f / 18f;
                        AddPiece(root.transform, $"stone_{i:00}", PrimitiveType.Sphere,
                            new Vector3(0.16f, 0.10f, 0.13f),
                            new Vector3(Mathf.Cos(radians) * 0.46f, 0.07f,
                                Mathf.Sin(radians) * 0.46f),
                            new Vector3(0f, i * 37f, 0f), new Color(0.43f, 0.45f, 0.54f));
                    }
                    // Bill contract: 9 hearth sticks + 2 posts + 1 crossbar.
                    AddPiece(root.transform, "stick_spit_post_l", PrimitiveType.Cylinder,
                        new Vector3(0.025f, 0.34f, 0.025f), new Vector3(-0.52f, 0.34f, 0f),
                        new Vector3(0f, 0f, -10f), new Color(0.38f, 0.20f, 0.10f));
                    AddPiece(root.transform, "stick_spit_post_r", PrimitiveType.Cylinder,
                        new Vector3(0.025f, 0.34f, 0.025f), new Vector3(0.52f, 0.34f, 0f),
                        new Vector3(0f, 0f, 10f), new Color(0.38f, 0.20f, 0.10f));
                    AddPiece(root.transform, "stick_bar", PrimitiveType.Cylinder,
                        new Vector3(0.025f, 0.75f, 0.025f), new Vector3(0f, 0.67f, 0f),
                        new Vector3(0f, 0f, 90f), new Color(0.38f, 0.20f, 0.10f));
                    AddPiece(root.transform, "rope_spit_l", PrimitiveType.Sphere,
                        Vector3.one * 0.07f, new Vector3(-0.52f, 0.58f, 0f),
                        Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
                    AddPiece(root.transform, "rope_spit_r", PrimitiveType.Sphere,
                        Vector3.one * 0.07f, new Vector3(0.52f, 0.58f, 0f),
                        Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
                    break;

                case "station.drying_rack":
                    AddFrame(root.transform, 0.75f, 0.85f, 0.05f);
                    break;

                case "station.water_collector":
                    AddFrame(root.transform, 0.62f, 0.72f, 0.045f);
                    AddPiece(root.transform, "leaf_funnel", PrimitiveType.Cylinder,
                        new Vector3(0.42f, 0.08f, 0.42f), new Vector3(0f, 0.58f, 0f),
                        Vector3.zero, new Color(0.25f, 0.53f, 0.23f));
                    break;

                case ContentIds.Workbench:
                    AddWorkbenchFallback(root.transform);
                    break;

                default: // leaf/basic beds
                    for (var i = 0; i < 4; i++)
                    {
                        AddPiece(root.transform, $"log_{i:00}", PrimitiveType.Cylinder,
                            new Vector3(0.08f, 0.72f, 0.08f),
                            new Vector3((i < 2 ? -0.48f : 0.48f), 0.10f,
                                (i % 2 == 0 ? -0.62f : 0.62f)),
                            new Vector3(90f, 0f, 0f), new Color(0.43f, 0.24f, 0.12f));
                    }
                    for (var i = 0; i < 12; i++)
                    {
                        AddPiece(root.transform, $"leaf_{i:00}", PrimitiveType.Cube,
                            new Vector3(0.16f, 0.025f, 1.05f),
                            new Vector3(-0.44f + i * 0.08f, 0.18f, 0f),
                            new Vector3(0f, (i % 2 == 0 ? -5f : 5f), 0f),
                            new Color(0.28f, 0.54f, 0.23f));
                    }
                    break;
            }
            return root;
        }

        private static void AddFrame(Transform root, float halfWidth, float height, float radius)
        {
            var wood = new Color(0.43f, 0.24f, 0.12f);
            AddPiece(root, "log_post_l", PrimitiveType.Cylinder,
                new Vector3(radius, height * 0.5f, radius), new Vector3(-halfWidth, height * 0.5f, 0f),
                Vector3.zero, wood);
            AddPiece(root, "log_post_r", PrimitiveType.Cylinder,
                new Vector3(radius, height * 0.5f, radius), new Vector3(halfWidth, height * 0.5f, 0f),
                Vector3.zero, wood);
            AddPiece(root, "log_crossbar", PrimitiveType.Cylinder,
                new Vector3(radius, halfWidth, radius), new Vector3(0f, height, 0f),
                new Vector3(0f, 0f, 90f), wood);
            AddPiece(root, "rope_lashing_l", PrimitiveType.Sphere,
                Vector3.one * radius * 2.4f, new Vector3(-halfWidth, height, 0f),
                Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
            AddPiece(root, "rope_lashing_r", PrimitiveType.Sphere,
                Vector3.one * radius * 2.4f, new Vector3(halfWidth, height, 0f),
                Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
        }

        private static void AddWorkbenchFallback(Transform root)
        {
            var wood = new Color(0.43f, 0.27f, 0.14f);
            var board = new Color(0.58f, 0.43f, 0.25f);
            var x = 0.42f;
            var z = 0.30f;
            for (var i = 0; i < 4; i++)
            {
                AddPiece(root, $"stick_workbench_{i:00}", PrimitiveType.Cube,
                    new Vector3(0.055f, 0.39f, 0.055f),
                    new Vector3(i % 2 == 0 ? -x : x, 0.39f, i < 2 ? -z : z),
                    Vector3.zero, wood);
            }
            AddPiece(root, "stick_workbench_04", PrimitiveType.Cube,
                new Vector3(0.44f, 0.045f, 0.045f), new Vector3(0f, 0.28f, -z),
                Vector3.zero, wood);
            AddPiece(root, "stick_workbench_05", PrimitiveType.Cube,
                new Vector3(0.44f, 0.045f, 0.045f), new Vector3(0f, 0.28f, z),
                Vector3.zero, wood);
            AddPiece(root, "board_workbench_brace_left", PrimitiveType.Cube,
                new Vector3(0.035f, 0.32f, 0.055f), new Vector3(-x, 0.42f, 0f),
                new Vector3(42f, 0f, 0f), board);
            AddPiece(root, "board_workbench_brace_right", PrimitiveType.Cube,
                new Vector3(0.035f, 0.32f, 0.055f), new Vector3(x, 0.42f, 0f),
                new Vector3(-42f, 0f, 0f), board);
            AddPiece(root, "rope_workbench_00", PrimitiveType.Sphere,
                new Vector3(0.07f, 0.04f, 0.07f), new Vector3(-x, 0.72f, 0f),
                Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
            AddPiece(root, "rope_workbench_01", PrimitiveType.Sphere,
                new Vector3(0.07f, 0.04f, 0.07f), new Vector3(x, 0.72f, 0f),
                Vector3.zero, new Color(0.72f, 0.58f, 0.34f));
            for (var i = 0; i < 4; i++)
            {
                AddPiece(root, $"board_workbench_{i:00}", PrimitiveType.Cube,
                    new Vector3(0.49f, 0.025f, 0.09f),
                    new Vector3(0f, 0.755f, -0.27f + i * 0.18f), Vector3.zero, board);
            }
        }

        private static void AddPiece(Transform root, string name, PrimitiveType type,
            Vector3 scale, Vector3 position, Vector3 rotation, Color color)
        {
            var piece = GameObject.CreatePrimitive(type);
            piece.name = name;
            piece.transform.SetParent(root, false);
            piece.transform.localScale = scale;
            piece.transform.localPosition = position;
            piece.transform.localEulerAngles = rotation;
            foreach (var collider in piece.GetComponents<Collider>())
            {
                DestroyRuntimeObject(collider);
            }
            var renderer = piece.GetComponent<Renderer>();
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (renderer != null && shader != null)
            {
                var material = new Material(shader) { color = color };
                if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                material.SetFloat("_Smoothness", 0.08f);
                renderer.sharedMaterial = material;
            }
        }

        // The Player uses deferred destruction; the editor-side world-prop
        // gate instantiates the same assemblies outside play mode and must
        // clean them synchronously without Unity's Destroy-in-edit-mode error.
        private static void DestroyRuntimeObject(Object value)
        {
            if (Application.isPlaying) Object.Destroy(value);
            else Object.DestroyImmediate(value);
        }

        /// A finished bed — every piece visible. Absolute-sized (1:1), so the caller
        /// must NOT run FitObjectPrefab on it.
        public static GameObject? BuildFinished(string product)
        {
            var go = Instantiate(product, out var asm);
            asm?.ApplyAll();
            EnsureSleepPoint(go, product);
            return go;
        }

        /// Авторский маркер сна: `point`, а также блендеровские дубликаты
        /// `point.001` / `point_01`. Отдельный метод, потому что тот же вопрос
        /// задают рендер и оба тестовых стенда — три редакции одного правила
        /// разошлись бы на первой правке модели.
        internal static bool IsSleepMarker(string name) =>
            name.StartsWith("point", System.StringComparison.Ordinal) &&
            (name.Length == 5 || !char.IsLetter(name[5]));

        private static void EnsureSleepPoint(GameObject? root, string product)
        {
            // ⭐ Сравнение по КАНОНИЧЕСКОМУ идентификатору, а не по сырому.
            //
            // Кровать в мире может нести легаси-идентификатор (`bed.leaf`,
            // `building.hut_bed`) — это read-only псевдонимы сейва, и грузиться
            // они обязаны как `bed.basic`. Рендер отдаёт сюда СЫРОЙ
            // `worldObject.DefinitionId` (HexWorldRenderer:3135), поэтому
            // точное сравнение молча пропускало такую кровать, и она
            // оставалась без маркера сна. Дальше FindBedAttachPoint честно
            // откатывался к границам рендерера — а у листовой кровати верх
            // границ это КОНЧИК САМОГО ВЫСОКОГО ЛИСТА, плюс ещё SleepBodyLift
            // 0.12: девушка висела над постелью на всю длину листьев (баг
            // #114, скриншот сцены HutTest). Комментарий ниже обещает ровно
            // обратное — «чтобы высота сна никогда не откатывалась к границам
            // рендерера», — и обещание держалось только для кроватей, попавших
            // сюда уже каноническими (HutFurnitureFactory передаёт BedBasic
            // явно, потому и встроенная кровать хижины была в порядке).
            if (root == null || ContentIds.Canonicalize(product) != ContentIds.BedBasic)
            {
                return;
            }

            // ⭐ У АВТОРСКОГО МАРКЕРА БЕРЁТСЯ ТОЛЬКО ВЫСОТА. НИКОГДА — ПОВОРОТ.
            //
            // Маркер — это не просто «где лежать»: NpcActorView берёт у него
            // ЦЕЛИКОМ и позицию по XZ, и ОРИЕНТАЦИЮ тела
            // (`_bodyRoot.rotation = _layingAttach.rotation`). Пустышка, которую
            // создаёт код ниже, имеет единичный локальный поворот, поэтому тело
            // наследует ориентацию самой кровати — это и есть работающее
            // поведение. А авторский `point.001` — блендеровский Null, несущий
            // конверсию осей экспорта; отдать его читателю как есть значит
            // отдать телу чужой поворот, и девушка ложится развёрнутой.
            // Проверено игроком: подмена узла «стала только хуже».
            //
            // Поэтому авторский узел здесь — ИСТОЧНИК ОДНОГО ЧИСЛА, высоты. Она
            // снимается как мировая разница по Y с корнем сборки, то есть в тех
            // же единицах, в которых её задаёт константа, и подставляется в ту
            // же самую пустышку. Ни один поворот при этом не меняется.
            //
            // Числа из ассета (Blender Z-вверх, Z над корнем кровати): рама
            // 0.138, слеги и вязки 0.314, листья 0.256…0.489 при среднем 0.386,
            // маркер 0.540, константа 0.370. Модель считает, код читает.
            var lift = SleepRootLocalY;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (child == root.transform || !IsSleepMarker(child.name))
                {
                    continue;
                }

                lift = child.position.y - root.transform.position.y;
                // Убрать с дороги: читатели (рендер, гейт HutTest,
                // BedSleepPoseTest) ищут имя `point`, и найти они должны
                // пустышку с правильной ориентацией, а не этот узел.
                child.name = "point_authored_height";
                break;
            }

            // Native FBX files deliberately contain only renderable construction
            // pieces. Add the same authored sleep marker the former prefab-based
            // beds used, so sleeping height never falls back to renderer bounds.
            // The FBX root carries Blender's X=-90° axis conversion, therefore
            // world-up must be converted into that root's local space.
            var point = new GameObject("point");
            point.transform.SetParent(root.transform, false);
            point.transform.localPosition = root.transform.InverseTransformVector(
                Vector3.up * lift);
        }

        /// A build-site in progress — only the delivered pieces of each material.
        public static GameObject? BuildPartial(
            string product, int logs, int sticks, int ropes, int leaves, int stones = 0,
            int boards = 0)
        {
            var go = Instantiate(product, out var asm);
            if (product == ContentIds.BedBasic)
            {
                asm?.ApplyBedStages(logs, sticks, ropes, leaves);
            }
            else
            {
                asm?.Apply(logs, sticks, ropes, leaves, stones, boards);
            }
            return go;
        }
    }
}
