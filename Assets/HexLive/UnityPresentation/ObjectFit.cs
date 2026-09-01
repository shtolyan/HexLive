using UnityEngine;
using HexLive.UnityPresentation.Spatial;

namespace HexLive.UnityPresentation
{
    /// <summary>
    /// Single source of truth for how big a world object renders — used by BOTH
    /// the ground renderer (<c>HexWorldRenderer.FitObjectPrefab</c>) and the
    /// in-hand prop (<c>NpcActorView.SetHandProp</c>) so a tool / coconut is the
    /// SAME physical size in the hand and on the ground.
    ///
    /// Size = a per-category fraction of <see cref="SimulationUnityMapper.HexRadius"/>,
    /// applied to the object's measured max dimension. Change a number here and it
    /// moves everywhere (ground + hand) at once. The gear asset's hand scale is a
    /// per-item fine MULTIPLIER on top of this (default 1), not the absolute size.
    /// </summary>
    public static class ObjectFit
    {
        /// Spec §54.2: target length for a dropped log or stick, in HexRadius
        /// units. The approved standing palm is authored independently at 1:1.
        public const float PalmSegmentLength = 0.7f;

        /// World-space target for the object's measured dimension.
        public static float TargetWorldSize(string definitionId)
        {
            var r = SimulationUnityMapper.HexRadius;
            // §54.2: PalmTreeFactory returns before generic fitting; palm_final
            // keeps its authored 1:1 dimensions.
            if (definitionId.Contains("tree")) return r * 2.2f;
            if (definitionId.Contains("bed")) return r * 0.95f;
            // A meat chunk reads bigger than a coconut half — 1.5× the standard
            // food size. Ground, hand and the roasting spit all share this.
            if (definitionId == "food.meat_raw" || definitionId == "food.meat_cooked") return r * 0.18f;
            if (definitionId.StartsWith("food.")) return r * 0.12f;
            // Spec §54.2: a log/stick is a full palm-trunk segment long (big, like
            // Stranded Deep) — measured by its long axis; the crown and leaf are
            // sized to sit with the palm.
            if (definitionId == "resource.log" || definitionId == "resource.stick") return r * PalmSegmentLength;
            // §119.1: доска — распущенное бревно (saw.log даёт две штуки), а не
            // ручной инструмент. На общей ручке для resource.* (0.216) она лежала
            // в траве щепкой втрое короче бревна: модель рисовалась, но игрок её
            // не находил. 0.5 — заметно меньше бревна и всё же доска.
            if (definitionId == "resource.board") return r * 0.5f;
            if (definitionId == "resource.palm_crown") return r * 0.7f;
            if (definitionId == "resource.palm_leaf") return r * 0.55f;
            // The spear is a long two-handed weapon — much longer than a hand tool.
            if (definitionId == "tool.spear") return r * 0.9f;
            // A rope coil is a small bundle — slightly smaller than a coconut
            // half (food.* renders at 0.12), not tool-sized.
            if (definitionId == "resource.rope") return r * 0.10f;
            // A lighter is a tiny pocket object — 1/3 of the standard tool size,
            // applied to BOTH ground and hand (0.216 / 3).
            if (definitionId == "tool.lighter") return r * 0.072f;
            // A one-litre bottle is shorter than a hand tool. Keep this in the
            // shared fit table so the ground and hand cannot drift apart again.
            if (definitionId == "tool.bottle") return r * 0.18f;
            // Tools & resources: 0.216 = the standard hand/ground tool size
            // (was 0.18; +20% after in-hand testing, applied to BOTH paths).
            if (definitionId.StartsWith("tool.") || definitionId.StartsWith("resource.")) return r * 0.216f;
            // Small carried items (item.bandage…) are pocket-sized, like food.
            // Without this they fell to the 0.6 default and a bandage roll
            // rendered campfire-big on the ground and in hand.
            if (definitionId.StartsWith("item.")) return r * 0.12f;
            // Bug #341: med.splint is a small carried medical prop. The med.* id
            // used to miss every category and fall through to the 0.6 default,
            // making the one-metre source mesh five times too large everywhere.
            if (definitionId == "med.splint") return r * 0.12f;
            if (definitionId == "campfire.spot") return r * 0.55f;
            if (definitionId == "grave.npc") return r * 0.35f;
            if (definitionId == "rock.boulder") return r * 0.45f;
            // §35.5B/§54.15: station.drying_rack and station.water_collector are
            // NOT sized here — drying_rack_final / water_collector_final are
            // authored 1:1 like the beds and rendered via BedAssembly, whose
            // renderer branch returns before FitObjectPrefab ever runs. Adding
            // a factor here would be dead code today and a double-scale the
            // day that branch changes.
            if (definitionId == "forest.deadfall" ||
                definitionId == "construction.site") return r * 0.7f;
            return r * 0.6f;
        }

        /// Which bounds dimension is normalized for a given category.
        public static float MeasureCurrent(string definitionId, Bounds b)
        {
            if (definitionId.Contains("tree") || definitionId == "grave.npc") return b.size.y;
            if (definitionId.Contains("bed") || definitionId == "campfire.spot" ||
                definitionId == "rock.boulder" || definitionId == "forest.deadfall" ||
                definitionId == "construction.site")
                return Mathf.Max(b.size.x, b.size.z);
            return Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)); // food / tool / resource / default
        }

        /// <summary>
        /// A Renderer component alone does not prove that a prefab can draw.
        /// Scripted-importer sub-assets may resolve to null in a Player while
        /// leaving the serialized MeshRenderer hierarchy intact.
        /// </summary>
        public static bool HasRenderableGeometry(GameObject go)
        {
            var meshFilters = go.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < meshFilters.Length; i++)
            {
                var mesh = meshFilters[i].sharedMesh;
                if (mesh != null && mesh.vertexCount > 0)
                {
                    return true;
                }
            }

            var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < skinned.Length; i++)
            {
                var mesh = skinned[i].sharedMesh;
                if (mesh != null && mesh.vertexCount > 0)
                {
                    return true;
                }
            }

            var sprites = go.GetComponentsInChildren<SpriteRenderer>(true);
            for (var i = 0; i < sprites.Length; i++)
            {
                if (sprites[i].sprite != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// Combined world-space bounds of every renderer under <paramref name="go"/>.
        public static bool WorldBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return false;
            bounds = rs[0].bounds;
            for (var i = 1; i < rs.Length; i++) bounds.Encapsulate(rs[i].bounds);
            return true;
        }

        /// Uniform factor to MULTIPLY the object's localScale by so its measured
        /// world dimension equals the category target. 1 if it has no renderers.
        /// Works both at world scale (ground) and under a scaled bone (hand) —
        /// renderer.bounds is world-space, so the result targets a world size.
        public static float FitScaleFactor(GameObject go, string definitionId)
        {
            if (!TryMeasuredSize(go, definitionId, out var current) || current <= 0.0001f)
            {
                return 1f;
            }

            return TargetWorldSize(definitionId) / current;
        }

        /// <summary>
        /// ⭐ #136: размер предмета НЕ ЗАВИСИТ ОТ ТОГО, КАК ОН ПОВЁРНУТ.
        /// <para>
        /// Здесь стоял мировой AABB (<see cref="WorldBounds"/>), а он у длинного
        /// предмета тем короче, чем косее предмет висит: у стержня длины L,
        /// повёрнутого на угол α, наибольшая сторона коробки ≈ L·cos α. Подгонка
        /// делит цель на эту укороченную меру — и предмет РАСТЁТ ровно во столько
        /// раз, во сколько его наклонили. За спиной копьё висит почти вертикально
        /// (наклон 18°, cos ≈ 0.95), а в руке лежит наискось — отсюда жалоба
        /// игрока «в руке намного больше, чем за спиной» на ОДИН И ТОТ ЖЕ
        /// предмет с одной и той же целью размера.
        /// </para>
        /// <para>
        /// Поэтому меряем в СОБСТВЕННОМ пространстве предмета и переводим в мир
        /// масштабом: длина копья — свойство копья, а не его позы. Прежний
        /// порядок «сначала поворот, потом подгонка» это лечить не мог — он
        /// делал ошибку лишь одинаковой на каждом кадре.
        /// </para>
        /// </summary>
        public static bool TryMeasuredSize(GameObject go, string definitionId, out float size)
        {
            size = 0f;
            var toLocal = go.transform.worldToLocalMatrix;
            var has = false;
            var local = new Bounds();

            // ⚠️ Меряем ИСХОДНЫЕ меши, а не Renderer.bounds: последний уже
            // мировой AABB, то есть уже раздут поворотом. Привести его в
            // локальное пространство мало — раздутие переехало бы вместе с ним.
            void Accumulate(Mesh mesh, Transform owner)
            {
                if (mesh == null || mesh.vertexCount == 0 || owner == null) return;
                var matrix = toLocal * owner.localToWorldMatrix;
                var meshBounds = mesh.bounds;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = meshBounds.center + Vector3.Scale(
                        meshBounds.extents,
                        new Vector3(
                            (corner & 1) == 0 ? -1f : 1f,
                            (corner & 2) == 0 ? -1f : 1f,
                            (corner & 4) == 0 ? -1f : 1f));
                    var localPoint = matrix.MultiplyPoint3x4(point);
                    if (!has)
                    {
                        local = new Bounds(localPoint, Vector3.zero);
                        has = true;
                    }
                    else
                    {
                        local.Encapsulate(localPoint);
                    }
                }
            }

            var filters = go.GetComponentsInChildren<MeshFilter>(true);
            for (var i = 0; i < filters.Length; i++)
            {
                Accumulate(filters[i].sharedMesh, filters[i].transform);
            }

            var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var i = 0; i < skinned.Length; i++)
            {
                Accumulate(skinned[i].sharedMesh, skinned[i].transform);
            }

            // Спрайты и прочее без меша меряем как раньше — по мировой коробке:
            // у плоского спрайта поворот длину не искажает.
            if (!has)
            {
                if (!WorldBounds(go, out var fallback)) return false;
                size = MeasureCurrent(definitionId, fallback);
                return true;
            }

            // Локальный размер → мировой: масштабом самого предмета вместе с
            // костью, на которой он висит.
            var lossy = go.transform.lossyScale;
            var worldSize = new Bounds(
                Vector3.zero,
                new Vector3(
                    local.size.x * Mathf.Abs(lossy.x),
                    local.size.y * Mathf.Abs(lossy.y),
                    local.size.z * Mathf.Abs(lossy.z)));
            size = MeasureCurrent(definitionId, worldSize);
            return true;
        }
    }
}
