using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Spatial
{
    /// <summary>
    /// §127 (черновик): подбор ТОЧЕК для парной романтической анимации.
    /// Якорь размещения — ЕЁ узлы: анимация привязана к кромке гекса (стена
    /// или ступенька), и героиня может занять ЛЮБОЙ свободный узел вдоль этой
    /// кромки — ближней интерьерной линии из четырёх узлов, параллельной ребру
    /// (0.325 wu от грани). Поворот — часть кандидата: в стену (Wall) или
    /// спиной к ступеньке (Sitting). ЕГО стартовый узел вычисляется из
    /// авторского §127-офсета пары: оба сначала ВСТАЮТ на свои узлы штатной
    /// ходьбой, затем включается анимация — он соскальзывает с узла в точный
    /// офсет (HisAnimWorld), она остаётся на своём узле.
    /// Куда это подключается в игре (триггер/последствия) — ещё не решено.
    /// </summary>
    public static class RomancePlacement
    {
        /// <summary>Какая кромка нужна анимации (перепад высоты соседа).</summary>
        public enum EdgeRequirement
        {
            Flat,    // сосед вровень (diff 0)
            StepUp,  // ступенька вверх (diff +1) — «присесть спиной»
            Wall,    // обрыв вверх (diff >= +2) — «руки в стену»
        }

        /// <summary>Правило позы: кромка + куда смотреть + его офсет
        /// в ЕЁ системе (X = вправо, Y = вперёд), из RomancePoseCatalog.</summary>
        public readonly struct PoseRule
        {
            public PoseRule(EdgeRequirement edge, bool faceIntoEdge, Float2 maleLocalOffset)
            {
                Edge = edge;
                FaceIntoEdge = faceIntoEdge;
                MaleLocalOffset = maleLocalOffset;
            }

            public EdgeRequirement Edge { get; }
            public bool FaceIntoEdge { get; }
            public Float2 MaleLocalOffset { get; }
        }

        /// <summary>Готовый кандидат: узлы обоих + её поворот.</summary>
        public readonly struct Placement
        {
            public Placement(TileCoord herTile, AxialPoint herSub, Float2 herWorld, float herFacingDeg,
                TileCoord hisTile, AxialPoint hisSub, Float2 hisWorld, Float2 hisAnimWorld, int edgeDirIndex)
            {
                HerTile = herTile;
                HerSub = herSub;
                HerWorld = herWorld;
                HerFacingDeg = herFacingDeg;
                HisTile = hisTile;
                HisSub = hisSub;
                HisWorld = hisWorld;
                HisAnimWorld = hisAnimWorld;
                EdgeDirIndex = edgeDirIndex;
            }

            public TileCoord HerTile { get; }
            public AxialPoint HerSub { get; }
            public Float2 HerWorld { get; }
            /// <summary>Сим-градусы (0° = +X); кратные 60° = перпендикуляр ребра.</summary>
            public float HerFacingDeg { get; }
            public TileCoord HisTile { get; }
            public AxialPoint HisSub { get; }
            /// <summary>Узел, НА который он приходит ходьбой.</summary>
            public Float2 HisWorld { get; }
            /// <summary>Точная точка анимации, куда он соскальзывает с узла.</summary>
            public Float2 HisAnimWorld { get; }
            /// <summary>0..5 — ребро кромки; сим-угол нормали = 60°·k.</summary>
            public int EdgeDirIndex { get; }
        }

        /// <summary>Соседи по рёбрам в порядке углов нормали: 0°, 60°, … 300°.</summary>
        public static readonly (int dq, int dr)[] EdgeNeighbors =
        {
            (1, 0), (0, 1), (-1, 1), (-1, 0), (0, -1), (1, -1),
        };

        // Ближняя к ребру интерьерная линия: проекция оффсета узла на нормаль
        // ребра равна максимуму 0.974 wu (следующая линия — 0.65, порог между).
        private const float NearEdgeDotThreshold = 0.9f;

        /// <summary>
        /// Все кандидаты позы вокруг тайла. elevation отдаёт высоту тайла или
        /// null (нет тайла/вода); isNodeFree — занятость узла (оба узла должны
        /// быть свободны; его узел может совпасть с её — пара, не соседи).
        /// </summary>
        public static List<Placement> FindCandidates(
            TileCoord anchor,
            PoseRule rule,
            Func<TileCoord, int?> elevation,
            Func<TileCoord, AxialPoint, bool> isNodeFree)
        {
            var results = new List<Placement>();
            var anchorElevation = elevation(anchor);
            if (anchorElevation == null) return results;

            for (var k = 0; k < EdgeNeighbors.Length; k++)
            {
                var (dq, dr) = EdgeNeighbors[k];
                var neighbor = new TileCoord(anchor.Q + dq, anchor.R + dr);
                var neighborElevation = elevation(neighbor);
                if (neighborElevation == null) continue;

                var diff = neighborElevation.Value - anchorElevation.Value;
                var kind = diff >= 2 ? EdgeRequirement.Wall
                    : diff == 1 ? EdgeRequirement.StepUp
                    : diff == 0 ? EdgeRequirement.Flat
                    : (EdgeRequirement?)null; // ступенька ВНИЗ — не кромка для позы
                if (kind != rule.Edge) continue;

                var normalDeg = 60f * k;
                var facing = rule.FaceIntoEdge ? normalDeg : normalDeg + 180f;
                var normalX = MathF.Cos(normalDeg * (MathF.PI / 180f));
                var normalY = MathF.Sin(normalDeg * (MathF.PI / 180f));

                foreach (var template in HexPointLayout.GetInteriorTemplates())
                {
                    var dot = template.Offset.X * normalX + template.Offset.Y * normalY;
                    if (dot < NearEdgeDotThreshold) continue; // не ближняя линия
                    if (!isNodeFree(anchor, template.SubAxial)) continue;

                    var herWorld = HexSpatialMath.PointToWorld(anchor, template.Offset);
                    var hisAnimWorld = MaleAnimPosition(herWorld, facing, rule.MaleLocalOffset);
                    if (!TryFindStartNode(anchor, hisAnimWorld, elevation, isNodeFree,
                            out var hisTile, out var hisSub, out var hisWorld))
                        continue;

                    results.Add(new Placement(anchor, template.SubAxial, herWorld, facing,
                        hisTile, hisSub, hisWorld, hisAnimWorld, k));
                }
            }

            return results;
        }

        /// <summary>Его точка анимации: её мир + офсет в её системе
        /// (X = вправо, Y = вперёд; вправо = поворот forward на −90°).</summary>
        public static Float2 MaleAnimPosition(Float2 herWorld, float herFacingDeg, Float2 maleLocalOffset)
        {
            var rad = herFacingDeg * (MathF.PI / 180f);
            var fx = MathF.Cos(rad);
            var fy = MathF.Sin(rad);
            // right = forward, повёрнутый на −90° в сим-плоскости (проверено на
            // сохранённых сетах: взгляд 180°, локальный (−0.04, −0.34) → мир (+0.34, −0.04)).
            var rx = fy;
            var ry = -fx;
            return new Float2(
                herWorld.X + rx * maleLocalOffset.X + fx * maleLocalOffset.Y,
                herWorld.Y + ry * maleLocalOffset.X + fy * maleLocalOffset.Y);
        }

        // Его стартовый узел: ближайший к точке анимации СВОБОДНЫЙ узел якорного
        // тайла или соседей (интерьер + граничное кольцо, дедуп по ключу).
        private static bool TryFindStartNode(
            TileCoord anchor,
            Float2 target,
            Func<TileCoord, int?> elevation,
            Func<TileCoord, AxialPoint, bool> isNodeFree,
            out TileCoord bestTile,
            out AxialPoint bestSub,
            out Float2 bestWorld)
        {
            var foundTile = anchor;
            var foundSub = default(AxialPoint);
            var foundWorld = default(Float2);
            var bestDistance = float.MaxValue;
            var seen = new HashSet<(int, int)>();

            var tiles = new List<TileCoord>(7) { anchor };
            foreach (var (dq, dr) in EdgeNeighbors)
                tiles.Add(new TileCoord(anchor.Q + dq, anchor.R + dr));

            foreach (var tile in tiles)
            {
                if (elevation(tile) == null) continue;
                foreach (var templates in new[]
                             { HexPointLayout.GetInteriorTemplates(), HexPointLayout.GetBoundaryTemplates() })
                {
                    foreach (var template in templates)
                    {
                        var key = HexPointLayout.GetJunctionKeyPair(tile, template.SubAxial);
                        if (!seen.Add(key)) continue;
                        if (!isNodeFree(tile, template.SubAxial)) continue;

                        var world = HexSpatialMath.PointToWorld(tile, template.Offset);
                        var distance = HexSpatialMath.Distance(world, target);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            foundTile = tile;
                            foundSub = template.SubAxial;
                            foundWorld = world;
                        }
                    }
                }
            }

            bestTile = foundTile;
            bestSub = foundSub;
            bestWorld = foundWorld;
            return bestDistance < float.MaxValue;
        }
    }
}
