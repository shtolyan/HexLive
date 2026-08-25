#nullable enable
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime.Blueprints;
using HexLive.Simulation.Spatial;
using HexLive.UnityPresentation.Bootstrap;
using HexLive.UnityPresentation.Rendering;
using HexLive.UnityPresentation.Spatial;
using UnityEngine;

namespace HexLive.UnityPresentation.Environment
{
    /// <summary>
    /// §120.7 / §120.1: колышки размеченных стройплощадок в ИГРОВОМ мире.
    ///
    /// Пустой <c>build.site</c> не рисует ничего (выученное правило «пустой
    /// BuildProduct → невидимый якорь»), поэтому только колышки делают свежую
    /// разметку видимой. Правило игрока: колышки бесплатны, появляются вместе
    /// с разметкой и исчезают, как только в постройку доставлен ПЕРВЫЙ
    /// ингредиент, — дальше рост показывают сами доставленные детали.
    ///
    /// Раскладка точек — тот же чистый <see cref="BlueprintPlanningMarkers"/>,
    /// что у конструктора: дом ставит колышки по элементам утверждённого
    /// плана, одиночное изделие — четыре угла ориентированного footprint.
    /// Derived presentation: не мир, не сейв, каждые полсекунды выводится
    /// заново из snapshot.
    /// </summary>
    public sealed class BuildSiteStakeRenderer : MonoBehaviour
    {
        private const float RefreshInterval = 0.5f;
        private const float StakeHeight = 0.22f;
        private const float StakeRadius = 0.028f;

        // Копия констант ForFurniture: рамка чуть шире footprint, но не уже
        // минимального прямоугольника, чтобы колышки точечного изделия
        // (костра) не слипались в один пучок.
        private const float FurniturePadding = 0.055f;
        private const float MinimumFurnitureHalfExtent = 0.18f;

        private SimulationRunnerBehaviour? _runner;
        private HexWorldRenderer? _worldRenderer;
        private Transform? _root;
        private Material? _stakeMaterial;
        private Mesh? _stakeMesh;
        private float _nextRefresh;

        // siteId → корень его колышков; пересобирается только при изменении
        // подписи (тайл/поворот/продукт) или пропаже права на колышки.
        private readonly Dictionary<int, (string Signature, GameObject Root)> _stakes = new();
        private readonly HashSet<int> _seen = new();
        private readonly Dictionary<int, List<ObjectSnapshot>> _modulesByOwner = new();

        public void Construct(SimulationRunnerBehaviour runner, HexWorldRenderer worldRenderer)
        {
            _runner = runner;
            _worldRenderer = worldRenderer;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            Refresh();
        }

        private void Refresh()
        {
            if (_runner == null || !_runner.IsReady || _worldRenderer == null)
            {
                ClearAll();
                return;
            }

            var snapshot = _runner.CreateSnapshot();
            if (snapshot == null)
            {
                ClearAll();
                return;
            }

            // §120.8: модули plan-площадки — самостоятельные объекты; и колышки,
            // и правило «первый ингредиент» читают ИХ, а не committed-план —
            // поэтому произвольный чертёж обслуживается тем же кодом.
            _modulesByOwner.Clear();
            foreach (var obj in snapshot.Objects)
            {
                if (obj.ArchitectureOwnerObjectId is not { } ownerId ||
                    obj.ArchitectureElements.Count != 1)
                {
                    continue;
                }

                if (!_modulesByOwner.TryGetValue(ownerId, out var list))
                {
                    list = new List<ObjectSnapshot>();
                    _modulesByOwner[ownerId] = list;
                }

                list.Add(obj);
            }

            _seen.Clear();
            foreach (var obj in snapshot.Objects)
            {
                if (string.IsNullOrEmpty(obj.BuildProduct)) continue;
                _modulesByOwner.TryGetValue(obj.Id.Value, out var modules);
                if (!WantsStakes(obj, modules)) continue;

                _seen.Add(obj.Id.Value);
                var signature = $"{obj.Tile.Q}:{obj.Tile.R}:{obj.RotationDegrees:0}:{obj.BuildProduct}" +
                                $":{(modules?.Count ?? 0)}";
                if (_stakes.TryGetValue(obj.Id.Value, out var existing) &&
                    existing.Signature == signature)
                {
                    continue;
                }

                if (existing.Root != null) Destroy(existing.Root);
                _stakes[obj.Id.Value] = (signature, BuildStakes(obj, modules));
            }

            // Площадка достроилась, получила первый материал или исчезла.
            List<int>? stale = null;
            foreach (var pair in _stakes)
            {
                if (_seen.Contains(pair.Key)) continue;
                stale ??= new List<int>();
                stale.Add(pair.Key);
            }

            if (stale == null) return;
            foreach (var id in stale)
            {
                if (_stakes[id].Root != null) Destroy(_stakes[id].Root);
                _stakes.Remove(id);
            }
        }

        /// <summary>Колышки живут, пока в постройке нет ни одного ингредиента.
        /// У plan-площадки материалы копятся В МОДУЛЯХ — сумма идёт по ним.
        /// В открытом режиме стройки колышки подменяет полный призрак.</summary>
        private static bool WantsStakes(ObjectSnapshot site, List<ObjectSnapshot> modules)
        {
            if (UI.BuildModePanel.IsOpen) return false;
            return !BuildSiteStakeVisibility.HasPhysicalProgress(site, modules);
        }

        private GameObject BuildStakes(ObjectSnapshot site, List<ObjectSnapshot> modules)
        {
            EnsureRoot();
            var root = new GameObject($"Build stakes (site {site.Id.Value} {site.BuildProduct})");
            root.transform.SetParent(_root, false);

            var groundY = _worldRenderer!.GroundTopY(site.Tile);
            var anchor = _worldRenderer.ObjectAnchorPosition(site);
            foreach (var point in StakePoints(site, anchor, modules))
            {
                AddStake(root.transform, new Vector3(point.X, groundY, point.Y));
            }

            // Bug #198: these stakes are the only visible geometry of a fresh
            // site, but until now they lived outside HexWorldRenderer's object
            // view and therefore could neither highlight nor receive a click.
            // Register the derived marker as a proxy for the authoritative
            // build.site; the normal object menu then supplies the catalogued
            // Build interaction and sends the real site ObjectId to the sim.
            root.AddComponent<Views.WorldObjectView>()
                .Init(site.Id.Value, site.DefinitionId);

            return root;
        }

        private static IEnumerable<Float2> StakePoints(
            ObjectSnapshot site, Float2 anchor, List<ObjectSnapshot> modules)
        {
            if (site.BuildProduct == ContentIds.HutPlan)
            {
                return PlanStakePoints(site, anchor, modules);
            }

            return FurnitureStakePoints(site, anchor);
        }

        /// <summary>Дом: колышек на месте каждого запланированного модуля.
        /// Геометрия читается из САМИХ модульных объектов (LocalX/LocalZ,
        /// повёрнутые площадкой) — поэтому произвольный чертёж §120.8 размечен
        /// так же честно, как committed-план, без чтения чертежа клиентом.</summary>
        private static IEnumerable<Float2> PlanStakePoints(
            ObjectSnapshot site, Float2 anchor, List<ObjectSnapshot> modules)
        {
            if (modules == null) yield break;
            var radians = site.RotationDegrees * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            var unique = new HashSet<(int, int)>();
            foreach (var module in modules)
            {
                var element = module.ArchitectureElements[0];
                var world = new Float2(
                    anchor.X + element.LocalX * cos - element.LocalZ * sin,
                    anchor.Y + element.LocalX * sin + element.LocalZ * cos);
                if (!unique.Add(((int)(world.X * 1000f), (int)(world.Y * 1000f)))) continue;
                yield return world;
            }
        }

        /// <summary>Одиночное изделие: четыре угла ориентированного footprint —
        /// та же рамка, что у <see cref="BlueprintPlanningMarkers.ForFurniture"/>,
        /// только заякоренная на мировую точку площадки.</summary>
        private static IEnumerable<Float2> FurnitureStakePoints(ObjectSnapshot site, Float2 anchor)
        {
            float minX = 0f, maxX = 0f, minY = 0f, maxY = 0f;
            var any = false;
            foreach (var offset in BlueprintFurnitureFootprints.LocalOffsets(site.BuildProduct))
            {
                var local = BlueprintGeometry.JunctionToWorld(offset);
                if (!any)
                {
                    minX = maxX = local.X;
                    minY = maxY = local.Y;
                    any = true;
                    continue;
                }

                minX = Mathf.Min(minX, local.X);
                maxX = Mathf.Max(maxX, local.X);
                minY = Mathf.Min(minY, local.Y);
                maxY = Mathf.Max(maxY, local.Y);
            }

            var centreX = any ? (minX + maxX) * 0.5f : 0f;
            var centreY = any ? (minY + maxY) * 0.5f : 0f;
            var halfX = Mathf.Max(MinimumFurnitureHalfExtent,
                any ? (maxX - minX) * 0.5f + FurniturePadding : 0f);
            var halfY = Mathf.Max(MinimumFurnitureHalfExtent,
                any ? (maxY - minY) * 0.5f + FurniturePadding : 0f);
            var radians = site.RotationDegrees * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            var corners = new[]
            {
                new Float2(centreX - halfX, centreY - halfY),
                new Float2(centreX + halfX, centreY - halfY),
                new Float2(centreX + halfX, centreY + halfY),
                new Float2(centreX - halfX, centreY + halfY)
            };
            foreach (var corner in corners)
            {
                yield return new Float2(
                    anchor.X + corner.X * cos - corner.Y * sin,
                    anchor.Y + corner.X * sin + corner.Y * cos);
            }
        }

        private void AddStake(Transform parent, Vector3 groundPosition)
        {
            var stake = new GameObject("Stake");
            stake.transform.SetParent(parent, false);
            stake.transform.position = groundPosition + Vector3.up * (StakeHeight * 0.5f);
            // Лёгкий детерминированный наклон от позиции — вбитая в землю
            // палочка, а не строй одинаковых столбиков.
            var hash = Mathf.Abs(groundPosition.x * 73.13f + groundPosition.z * 41.7f);
            stake.transform.localRotation = Quaternion.Euler(
                (hash % 7f) - 3f, hash * 37f % 360f, (hash * 1.7f % 7f) - 3f);
            // Меш примитивного цилиндра — 2 wu высотой и 1 wu толщиной.
            stake.transform.localScale = new Vector3(
                StakeRadius * 2f, StakeHeight * 0.5f, StakeRadius * 2f);

            var filter = stake.AddComponent<MeshFilter>();
            filter.sharedMesh = StakeMesh();
            var renderer = stake.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = StakeMaterial();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private Mesh StakeMesh()
        {
            if (_stakeMesh != null) return _stakeMesh;
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _stakeMesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            Destroy(primitive);
            return _stakeMesh;
        }

        private Material StakeMaterial()
        {
            if (_stakeMaterial != null) return _stakeMaterial;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _stakeMaterial = new Material(shader) { name = "BuildSiteStake" };
            var wood = new Color(0.55f, 0.42f, 0.27f);
            _stakeMaterial.color = wood;
            if (_stakeMaterial.HasProperty("_BaseColor"))
                _stakeMaterial.SetColor("_BaseColor", wood);
            if (_stakeMaterial.HasProperty("_Smoothness"))
                _stakeMaterial.SetFloat("_Smoothness", 0f);
            return _stakeMaterial;
        }

        private void EnsureRoot()
        {
            if (_root != null) return;
            var root = new GameObject("Build site stakes");
            root.transform.SetParent(transform, false);
            _root = root.transform;
        }

        private void ClearAll()
        {
            if (_stakes.Count == 0) return;
            foreach (var pair in _stakes)
            {
                if (pair.Value.Root != null) Destroy(pair.Value.Root);
            }

            _stakes.Clear();
        }

        private void OnDestroy()
        {
            if (_stakeMaterial != null) Destroy(_stakeMaterial);
        }
    }
}
