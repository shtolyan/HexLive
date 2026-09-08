using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{
    // §146.4/§146.7: «Выживание на большом острове». Остров ~2.5× Feud (первый
    // шаг ревизии 1 — рост до ~6× это ревизия 2, после перф-замера), три
    // лагеря-соседки по 2 девушки, ни одной постройки, ни одного чужака.
    // Лес — рощами, по 1-3 пальмы на гекс на НЕсмежных якорных узлах, чтобы
    // гекс оставался проходимым; посев рассчитан от билла дома Hut1Hex
    // (16-29 пальм и ~36 юкк на дом).
    public static partial class PrototypeWorldDefinitionFactory
    {
        // §146.2: ревизия генератора. Пишется в блоб сейва и сверяется при
        // загрузке: рост карты меняет число, и сейв от старой геометрии
        // отклоняется честно, а не портится молча.
        // Ревизия 2: рост 46×36 → 72×56 после перф-гейта ревизии 1
        // (2.5×: 203 тик/с ≈ 4.9 мс/тик, запас до 4 Гц ~50×).
        public const int BigIslandWorldGenRevision = 2;

        // Границы ревизии 2: 72×56 = 4032 тайла ≈ 6× Feud (29×23 = 667).
        public const int BigMinQ = -34;
        public const int BigMaxQ = 37;
        public const int BigMinR = -26;
        public const int BigMaxR = 29;

        // §146.4: лагеря разнесены так, чтобы диски InCamp (радиус 6, §72.13)
        // не могли пересечься даже с запасом на прибрежные обходы.
        public const int BigCampCount = 3;
        public const int BigCampMinSeparationTiles = 22;

        // §146.7: лес на десять домов Hut1Hex (16-29 пальм и ~36 юкк на дом)
        // плюс живой лес после стройки; остальной посев — по площади.
        internal const int BigPalmTarget = 240;
        private const int BigGroveCount = 24;
        private const int BigYuccaCount = 360;
        private const int BigBoulderCount = 95;
        private const int BigLooseStoneCount = 220;
        private const int BigDeadfallCount = 24;
        private const int BigHerbCount = 18;

        // §146.9: «Огромный остров» — ровно следующий append-only режим.
        // 102×80 = 8160 тайлов, то есть 2.02× карты BigIsland (4032 тайла).
        // Шесть лагерей получают по одной девушке, а центральная стоянка
        // чужаков включает обычные §72.14 волны раз в три дня.
        public const int HugeIslandWorldGenRevision = 1;
        public const int HugeMinQ = -49;
        public const int HugeMaxQ = 52;
        public const int HugeMinR = -38;
        public const int HugeMaxR = 41;
        public const int HugeCampCount = 6;
        public const int HugeCampMinSeparationTiles = 22;
        internal const int HugePalmTarget = 480;

        // §157.1: «Острова» — шесть ячеек рецепта HugeIsland сеткой 3×2 встык,
        // в морской рамке. Каждая ячейка — тот же остров, что и Huge: тот же
        // размер, тот же шум в мировых координатах, тот же спад. Спад берётся
        // МАКСИМУМОМ по шести эллипсам: вне своей ячейки чужой спад равен
        // нулю, поэтому острова не сливаются в один и не тонут друг под
        // другом. 314×168 = 52 752 тайла.
        public const int IslandsWorldGenRevision = 1;
        public const int IslandColumns = 3;
        public const int IslandRows = 2;
        public const int IslandCount = IslandColumns * IslandRows;
        public const int IslandCellQ = HugeMaxQ - HugeMinQ + 1;   // 102
        public const int IslandCellR = HugeMaxR - HugeMinR + 1;   // 80
        public const int IslandsSeaMargin = 4;
        public const int IslandsMinQ = -(IslandColumns * IslandCellQ) / 2 - IslandsSeaMargin;
        public const int IslandsMaxQ = IslandsMinQ + IslandColumns * IslandCellQ + 2 * IslandsSeaMargin - 1;
        public const int IslandsMinR = -(IslandRows * IslandCellR) / 2 - IslandsSeaMargin;
        public const int IslandsMaxR = IslandsMinR + IslandRows * IslandCellR + 2 * IslandsSeaMargin - 1;

        // §157.3: полуширина брода в тайлах (1 → брод шириной 3).
        public const int IslandFordHalfWidth = 1;

        // §157.2: якорь лагеря не ближе стольких тайлов к воде — лагерь не на
        // пляже, и плато радиуса 3 не поднимает море в сушу.
        public const int IslandCampCoastClearance = 4;

        /// <summary>§157.1: ячейка сетки, в которой живёт один остров.</summary>
        internal readonly struct IslandCell
        {
            public readonly int Index;
            public readonly int MinQ;
            public readonly int MaxQ;
            public readonly int MinR;
            public readonly int MaxR;

            public IslandCell(int index, int minQ, int maxQ, int minR, int maxR)
            {
                Index = index;
                MinQ = minQ;
                MaxQ = maxQ;
                MinR = minR;
                MaxR = maxR;
            }

            public TileCoord Center => new TileCoord((MinQ + MaxQ) / 2, (MinR + MaxR) / 2);

            public bool Contains(int q, int r) => q >= MinQ && q <= MaxQ && r >= MinR && r <= MaxR;
        }

        internal static readonly IslandCell[] IslandCells = BuildIslandCells();

        private static IslandCell[] BuildIslandCells()
        {
            var cells = new IslandCell[IslandCount];
            for (var j = 0; j < IslandRows; j++)
            {
                for (var i = 0; i < IslandColumns; i++)
                {
                    var minQ = IslandsMinQ + IslandsSeaMargin + i * IslandCellQ;
                    var minR = IslandsMinR + IslandsSeaMargin + j * IslandCellR;
                    cells[j * IslandColumns + i] = new IslandCell(
                        j * IslandColumns + i, minQ, minQ + IslandCellQ - 1, minR, minR + IslandCellR - 1);
                }
            }

            return cells;
        }

        /// <summary>§157: индекс ячейки-острова по тайлу; −1 вне ячеек (морская рамка).</summary>
        public static int IslandCellIndex(TileCoord tile)
        {
            foreach (var cell in IslandCells)
            {
                if (cell.Contains(tile.Q, tile.R))
                {
                    return cell.Index;
                }
            }

            return -1;
        }

        private readonly struct LargeIslandSettings
        {
            public readonly GameMode Mode;
            public readonly int MinQ;
            public readonly int MaxQ;
            public readonly int MinR;
            public readonly int MaxR;
            public readonly int CampCount;
            public readonly int CampMinSeparationTiles;
            public readonly int GirlsPerCamp;
            public readonly int PalmTarget;
            public readonly int GroveCount;
            public readonly int YuccaCount;
            public readonly int BoulderCount;
            public readonly int LooseStoneCount;
            public readonly int DeadfallCount;
            public readonly int HerbCount;
            public readonly bool HasCentralOutsiderCamp;

            // §157: ячейки островов; null = один массив суши (Big/Huge/Maniac).
            public readonly IslandCell[] Cells;

            public LargeIslandSettings(
                GameMode mode, int minQ, int maxQ, int minR, int maxR,
                int campCount, int campMinSeparationTiles, int girlsPerCamp,
                int palmTarget, int groveCount, int yuccaCount, int boulderCount,
                int looseStoneCount, int deadfallCount, int herbCount,
                bool hasCentralOutsiderCamp, IslandCell[] cells = null)
            {
                Mode = mode;
                MinQ = minQ;
                MaxQ = maxQ;
                MinR = minR;
                MaxR = maxR;
                CampCount = campCount;
                CampMinSeparationTiles = campMinSeparationTiles;
                GirlsPerCamp = girlsPerCamp;
                PalmTarget = palmTarget;
                GroveCount = groveCount;
                YuccaCount = yuccaCount;
                BoulderCount = boulderCount;
                LooseStoneCount = looseStoneCount;
                DeadfallCount = deadfallCount;
                HerbCount = herbCount;
                HasCentralOutsiderCamp = hasCentralOutsiderCamp;
                Cells = cells;
            }
        }

        private static readonly LargeIslandSettings BigIslandSettings = new(
            GameMode.BigIsland, BigMinQ, BigMaxQ, BigMinR, BigMaxR,
            BigCampCount, BigCampMinSeparationTiles, 2,
            BigPalmTarget, BigGroveCount, BigYuccaCount, BigBoulderCount,
            BigLooseStoneCount, BigDeadfallCount, BigHerbCount,
            hasCentralOutsiderCamp: false);

        private static readonly LargeIslandSettings HugeIslandSettings = new(
            GameMode.HugeIsland, HugeMinQ, HugeMaxQ, HugeMinR, HugeMaxR,
            HugeCampCount, HugeCampMinSeparationTiles, 1,
            HugePalmTarget, 48, 720, 190, 440, 48, 36,
            hasCentralOutsiderCamp: true);

        // §146.11: same authored island recipe as HugeIsland. Mode remains its
        // own append-only world identity; only the Colony starter differs.
        private static readonly LargeIslandSettings ManiacSettings = new(
            GameMode.Maniac, HugeMinQ, HugeMaxQ, HugeMinR, HugeMaxR,
            HugeCampCount, HugeCampMinSeparationTiles, 1,
            HugePalmTarget, 48, 720, 190, 440, 48, 36,
            hasCentralOutsiderCamp: true);

        // Якорные узлы для пальм на одном гексе: интерьерные слоты 13 (2,-1),
        // 30 (-1,2), 10 (-1,-1) — треугольник ~1.12 wu стороной. Диск блокировки
        // пальмы 0.45 wu (~7 узлов из 61), три таких не сливаются, и между
        // ними и ободом гекса всегда остаётся проход.
        private static readonly int[][] PalmSlotSets =
        {
            new[] { 13 },
            new[] { 13, 30 },
            new[] { 13, 30, 10 },
        };

        internal static WorldBootstrapDefinition CreateBigIsland(int seed)
        {
            return CreateLargeIsland(seed, BigIslandSettings);
        }

        internal static WorldBootstrapDefinition CreateHugeIsland(int seed)
        {
            return CreateLargeIsland(seed, HugeIslandSettings);
        }

        internal static WorldBootstrapDefinition CreateManiac(int seed)
        {
            return CreateLargeIsland(seed, ManiacSettings);
        }

        // §157: посев ×6 от Huge — шесть островов того же рецепта; стоянки
        // чужаков нет (§157.7 — они приходят с моря).
        private static readonly LargeIslandSettings IslandsSettings = new(
            GameMode.Islands, IslandsMinQ, IslandsMaxQ, IslandsMinR, IslandsMaxR,
            IslandCount, HugeCampMinSeparationTiles, 1,
            HugePalmTarget * IslandCount, 48 * IslandCount, 720 * IslandCount, 190 * IslandCount,
            440 * IslandCount, 48 * IslandCount, 36 * IslandCount,
            hasCentralOutsiderCamp: false, cells: IslandCells);

        internal static WorldBootstrapDefinition CreateIslands(int seed)
        {
            return CreateLargeIsland(seed, IslandsSettings);
        }

        /// <summary>
        /// §156.9: тот же рецепт «Огромного острова», растянутый по ПЛОЩАДИ в
        /// <paramref name="areaScale"/> раз. Мир для ЗАМЕРА, а не режим игры: у
        /// него нет ни идентичности в сейве, ни пункта меню, ни своей ревизии
        /// worldgen — он живёт ровно столько, сколько идёт прогон.
        /// <para>
        /// Ради этого §156 и существует: шесть лагерей по одной девушке остаются
        /// прежними, а карта растёт, — то есть колония занимает всё меньшую её
        /// долю. Содержимое (пальмы, юкка, камни, валежник) масштабируется вместе
        /// с площадью, иначе большой остров оказался бы просто пустым и мерил бы
        /// не ту игру. Расстояние между лагерями тоже растёт: разъехавшиеся
        /// лагеря будят БОЛЬШЕ карты, чем сгрудившиеся, и это честный, а не
        /// удобный, случай для механики.
        /// </para>
        /// </summary>
        public static WorldBootstrapDefinition CreateScaledHugeIsland(int seed, int areaScale)
        {
            if (areaScale < 1)
            {
                areaScale = 1;
            }

            var side = System.MathF.Sqrt(areaScale);
            int Grow(int min, int max, out int grownMin)
            {
                var span = (int)System.MathF.Round((max - min + 1) * side);
                grownMin = min - (span - (max - min + 1)) / 2;
                return grownMin + span - 1;
            }

            var maxQ = Grow(HugeMinQ, HugeMaxQ, out var minQ);
            var maxR = Grow(HugeMinR, HugeMaxR, out var minR);
            int Scale(int count) => (int)System.MathF.Round(count * (float)areaScale);

            var settings = new LargeIslandSettings(
                GameMode.HugeIsland, minQ, maxQ, minR, maxR,
                HugeCampCount,
                (int)System.MathF.Round(HugeCampMinSeparationTiles * side),
                girlsPerCamp: 1,
                Scale(HugePalmTarget), Scale(48), Scale(720), Scale(190),
                Scale(440), Scale(48), Scale(36),
                hasCentralOutsiderCamp: true);
            return CreateLargeIsland(seed, settings);
        }

        private static WorldBootstrapDefinition CreateLargeIsland(
            int seed, LargeIslandSettings settings)
        {
            var definition = new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings
                {
                    TickDeltaTime = 0.25f,
                    MediumTickInterval = 4,
                    SlowTickInterval = 16,
                    Seed = seed,
                    Mode = settings.Mode,
                    SpawnCompletedTestHut = false
                },
                Environment = new EnvironmentBootstrap
                {
                    GlobalTemperature = 9f
                },
                Fragments = { new FragmentBootstrap { Id = 1 } }
            };

            var fragment = definition.Fragments[0];
            for (var q = settings.MinQ; q <= settings.MaxQ; q++)
            {
                for (var r = settings.MinR; r <= settings.MaxR; r++)
                {
                    fragment.Tiles.Add(Tile(q, r));
                }
            }

            List<TileCoord> anchors;
            if (settings.Cells is null)
            {
                AddBigElevation(fragment, seed, settings);
                anchors = PickCampAnchors(fragment, seed, settings);
                FinalizeWater(fragment);
            }
            else
            {
                // §157: шесть островов. Броды режутся ПОСЛЕ воды и ДО лесов и
                // россыпи — Plantable и пулы россыпи исключают Water, и брод не
                // должен зарасти пальмами.
                AddIslandsElevation(fragment, seed, settings);
                anchors = PickIslandCampAnchors(fragment, seed, settings);
                FinalizeWater(fragment);
                CarveIslandFords(fragment, settings);
            }

            var resourceAnchors = new List<TileCoord>(anchors);
            TileCoord? outsiderCamp = null;
            if (settings.HasCentralOutsiderCamp)
            {
                outsiderCamp = PickCentralOutsiderCamp(fragment, anchors, seed);
                if (outsiderCamp is { } camp)
                {
                    MarkCampSanctuary(fragment, camp);
                    resourceAnchors.Add(camp);
                }
            }

            AddBigForests(definition, fragment, seed, resourceAnchors, settings);
            AddBigScatter(definition, fragment, seed, resourceAnchors, settings);
            AddBigColonists(definition, anchors, settings.GirlsPerCamp);

            for (var i = 0; i < anchors.Count; i++)
            {
                definition.FactionHomes.Add(new FactionHomeBootstrap
                {
                    Faction = CampFaction(i),
                    TileQ = anchors[i].Q,
                    TileR = anchors[i].R
                });
            }

            if (outsiderCamp is { } outsider)
            {
                definition.FactionHomes.Add(new FactionHomeBootstrap
                {
                    Faction = Agents.Faction.Outsiders,
                    TileQ = outsider.Q,
                    TileR = outsider.R
                });
                AddHugeOpeningOutsider(definition, outsider);
            }

            return definition;
        }

        internal static Agents.Faction CampFaction(int campIndex) => campIndex switch
        {
            0 => Agents.Faction.Colony,
            1 => Agents.Faction.Colony2,
            2 => Agents.Faction.Colony3,
            3 => Agents.Faction.Colony4,
            4 => Agents.Faction.Colony5,
            _ => Agents.Faction.Colony6,
        };

        // §146.9: стоянка чужаков читается как центр острова, а не «ещё один
        // седьмой береговой лагерь». Берём ближайшую к геометрическому центру
        // низину, но не ближе десяти гексов к любой девушке; из равных первых
        // 24 точек выбираем по сиду, чтобы миры не складывались в один штамп.
        private static TileCoord? PickCentralOutsiderCamp(
            FragmentBootstrap fragment, List<TileCoord> girlCamps, int seed)
        {
            var center = new TileCoord(
                (HugeMinQ + HugeMaxQ) / 2,
                (HugeMinR + HugeMaxR) / 2);
            var candidates = new List<(TileCoord tile, int centerDistance)>();
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Water || !tile.Walkable || tile.Blocked ||
                    tile.Elevation < 1 || tile.Elevation > 2)
                {
                    continue;
                }

                var coord = new TileCoord(tile.Q, tile.R);
                var clear = true;
                foreach (var camp in girlCamps)
                {
                    if (HexSpatialMath.HexDistance(coord, camp) < 10)
                    {
                        clear = false;
                        break;
                    }
                }

                if (clear)
                {
                    candidates.Add((coord, HexSpatialMath.HexDistance(coord, center)));
                }
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            candidates.Sort((a, b) =>
            {
                var byDistance = a.centerDistance.CompareTo(b.centerDistance);
                if (byDistance != 0) return byDistance;
                var byQ = a.tile.Q.CompareTo(b.tile.Q);
                return byQ != 0 ? byQ : a.tile.R.CompareTo(b.tile.R);
            });
            var pool = System.Math.Min(24, candidates.Count);
            var pick = (int)(MathUtil.Hash01(seed, pool, 146, 14641) * pool);
            return candidates[System.Math.Min(pool - 1, pick)].tile;
        }

        private static void MarkCampSanctuary(FragmentBootstrap fragment, TileCoord camp)
        {
            var byCoord = new Dictionary<(int, int), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            if (byCoord.TryGetValue((camp.Q, camp.R), out var center))
            {
                center.Indoor = true;
            }

            foreach (var direction in HexDirection.All)
            {
                if (byCoord.TryGetValue((camp.Q + direction.DQ, camp.R + direction.DR),
                        out var around) &&
                    around.Walkable && !around.Water && around.Elevation >= 1)
                {
                    around.Indoor = true;
                }
            }
        }

        private static void AddHugeOpeningOutsider(
            WorldBootstrapDefinition definition, TileCoord camp)
        {
            if (!HexLive.Simulation.Runtime.Spec72.Enabled ||
                HexLive.Simulation.Runtime.Spec72.OutsiderCount <= 0)
            {
                return;
            }

            definition.Npcs.Add(new NpcBootstrap
            {
                Id = 101,
                DisplayName = "Kshishtof",
                ActorMesh = "Kshishtof",
                Faction = Agents.Faction.Outsiders,
                FragmentId = 1,
                TileQ = camp.Q,
                TileR = camp.R,
                Hunger = 0.50f,
                Thirst = 0.45f,
                Energy = 0.70f,
                Comfort = 0.40f,
                Social = 0.30f,
                ThermalDiscomfort = 0.50f,
                Attributes =
                {
                    [Agents.AttributeKind.Strength] = HexLive.Simulation.Runtime.Spec72.OutsiderStrength,
                    [Agents.AttributeKind.Agility] = HexLive.Simulation.Runtime.Spec72.OutsiderAgility,
                    [Agents.AttributeKind.Endurance] = HexLive.Simulation.Runtime.Spec72.OutsiderEndurance,
                    [Agents.AttributeKind.Toughness] = HexLive.Simulation.Runtime.Spec72.OutsiderToughness,
                    [Agents.AttributeKind.Hardiness] = HexLive.Simulation.Runtime.Spec72.OutsiderHardiness,
                    [Agents.AttributeKind.Wits] = HexLive.Simulation.Runtime.Spec72.OutsiderWits,
                    [Agents.AttributeKind.Perception] = HexLive.Simulation.Runtime.Spec72.OutsiderPerception,
                },
                Traits = new List<string>
                {
                    Agents.TraitKind.Abuser.ToString(),
                    Agents.TraitKind.Slob.ToString()
                }
            });
        }

        // Тот же язык рельефа, что у Feud (шум × спад), с двумя отличиями:
        // спад эллиптический (карта шире, чем выше — круговой спад топил бы
        // север и юг), и добавлена низкочастотная октава — на большой карте
        // двухоктавный шум читается как рябь без крупного рельефа.
        private static void AddBigElevation(
            FragmentBootstrap fragment, int seed, LargeIslandSettings settings)
        {
            var center = HexSpatialMath.TileToWorld(new TileCoord(
                (settings.MinQ + settings.MaxQ) / 2,
                (settings.MinR + settings.MaxR) / 2));
            var eastEdge = HexSpatialMath.TileToWorld(new TileCoord(
                settings.MaxQ, (settings.MinR + settings.MaxR) / 2));
            var southEdge = HexSpatialMath.TileToWorld(new TileCoord(
                (settings.MinQ + settings.MaxQ) / 2, settings.MaxR));
            var halfX = System.Math.Abs(eastEdge.X - center.X);
            var halfY = System.Math.Abs(southEdge.Y - center.Y);

            foreach (var tile in fragment.Tiles)
            {
                var world = HexSpatialMath.TileToWorld(new TileCoord(tile.Q, tile.R));
                var nx = (world.X - center.X) / halfX;
                var ny = (world.Y - center.Y) / halfY;
                var dist = System.MathF.Sqrt(nx * nx + ny * ny);
                var falloff = MathUtil.Clamp01(1f - dist * dist * 1.15f);

                var noise = ValueNoise(seed + 31, world.X * 0.05f, world.Y * 0.05f) * 0.30f +
                            ValueNoise(seed, world.X * 0.13f, world.Y * 0.13f) * 0.40f +
                            ValueNoise(seed + 17, world.X * 0.34f, world.Y * 0.34f) * 0.30f;
                var height = (noise * noise * 1.4f + 0.3f) * falloff;

                var elevation = (int)System.MathF.Round(height * 6f);
                elevation = System.Math.Min(5, elevation);

                if (tile.Q <= settings.MinQ || tile.Q >= settings.MaxQ ||
                    tile.R <= settings.MinR || tile.R >= settings.MaxR)
                {
                    elevation = 0;
                }

                tile.Elevation = elevation;
            }
        }

        // §157.1: рельеф шести островов. Тело — AddBigElevation с одной
        // заменой: спад считается максимумом по эллипсам всех ячеек. Внутри
        // своей ячейки это ровно спад Huge, а чужие ячейки дают ноль, так что
        // ни один остров не тонет под соседом и не сливается с ним.
        private static void AddIslandsElevation(
            FragmentBootstrap fragment, int seed, LargeIslandSettings settings)
        {
            var cells = settings.Cells;
            var centers = new Float2[cells.Length];
            var halfX = new float[cells.Length];
            var halfY = new float[cells.Length];
            for (var k = 0; k < cells.Length; k++)
            {
                var cell = cells[k];
                var midQ = (cell.MinQ + cell.MaxQ) / 2;
                var midR = (cell.MinR + cell.MaxR) / 2;
                centers[k] = HexSpatialMath.TileToWorld(new TileCoord(midQ, midR));
                var eastEdge = HexSpatialMath.TileToWorld(new TileCoord(cell.MaxQ, midR));
                var southEdge = HexSpatialMath.TileToWorld(new TileCoord(midQ, cell.MaxR));
                halfX[k] = System.Math.Abs(eastEdge.X - centers[k].X);
                halfY[k] = System.Math.Abs(southEdge.Y - centers[k].Y);
            }

            foreach (var tile in fragment.Tiles)
            {
                var world = HexSpatialMath.TileToWorld(new TileCoord(tile.Q, tile.R));
                var falloff = 0f;
                for (var k = 0; k < cells.Length; k++)
                {
                    var nx = (world.X - centers[k].X) / halfX[k];
                    var ny = (world.Y - centers[k].Y) / halfY[k];
                    var dist = System.MathF.Sqrt(nx * nx + ny * ny);
                    falloff = System.MathF.Max(falloff, MathUtil.Clamp01(1f - dist * dist * 1.15f));
                }

                var noise = ValueNoise(seed + 31, world.X * 0.05f, world.Y * 0.05f) * 0.30f +
                            ValueNoise(seed, world.X * 0.13f, world.Y * 0.13f) * 0.40f +
                            ValueNoise(seed + 17, world.X * 0.34f, world.Y * 0.34f) * 0.30f;
                var height = (noise * noise * 1.4f + 0.3f) * falloff;

                var elevation = (int)System.MathF.Round(height * 6f);
                elevation = System.Math.Min(5, elevation);

                if (tile.Q <= settings.MinQ || tile.Q >= settings.MaxQ ||
                    tile.R <= settings.MinR || tile.R >= settings.MaxR)
                {
                    elevation = 0;
                }

                tile.Elevation = elevation;
            }
        }

        // Компонента по шести соседям от стартового тайла. Если старт не
        // проходит предикат (аналитически невозможно для центра ячейки, но
        // защитно), стартуем с ближайшего подходящего по (дистанция, Q, R).
        private static HashSet<(int, int)> FloodFrom(
            Dictionary<(int, int), TileBootstrap> byCoord, TileCoord start,
            System.Func<TileBootstrap, bool> inside)
        {
            var origin = (start.Q, start.R);
            if (!byCoord.TryGetValue(origin, out var startTile) || !inside(startTile))
            {
                var best = int.MaxValue;
                TileCoord? nearest = null;
                foreach (var pair in byCoord)
                {
                    if (!inside(pair.Value)) continue;
                    var coord = new TileCoord(pair.Key.Item1, pair.Key.Item2);
                    var d = HexSpatialMath.HexDistance(coord, start);
                    if (d < best ||
                        (d == best && nearest is { } n &&
                         (coord.Q < n.Q || (coord.Q == n.Q && coord.R < n.R))))
                    {
                        best = d;
                        nearest = coord;
                    }
                }

                if (nearest is not { } found)
                {
                    return new HashSet<(int, int)>();
                }

                origin = (found.Q, found.R);
            }

            var component = new HashSet<(int, int)> { origin };
            var queue = new Queue<(int, int)>();
            queue.Enqueue(origin);
            while (queue.Count > 0)
            {
                var (cq, cr) = queue.Dequeue();
                foreach (var dir in HexDirection.All)
                {
                    var next = (cq + dir.DQ, cr + dir.DR);
                    if (component.Contains(next) ||
                        !byCoord.TryGetValue(next, out var neighbor) ||
                        !inside(neighbor))
                    {
                        continue;
                    }

                    component.Add(next);
                    queue.Enqueue(next);
                }
            }

            return component;
        }

        // Весь диск радиуса radius вокруг (q, r) лежит в множестве.
        private static bool DiskInside(HashSet<(int, int)> set, int q, int r, int radius)
        {
            for (var dq = -radius; dq <= radius; dq++)
            {
                var lo = System.Math.Max(-radius, -dq - radius);
                var hi = System.Math.Min(radius, -dq + radius);
                for (var dr = lo; dr <= hi; dr++)
                {
                    if (!set.Contains((q + dq, r + dr)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        // §157.2: по одному якорю на остров. Компонента острова — та, что
        // содержит центр его ячейки (в центре спад равен единице, суша там
        // всегда). Кандидат — тайл этой компоненты высотой 1..2, у которого весь
        // диск радиуса IslandCampCoastClearance тоже на суше этой компоненты:
        // лагерь не на пляже, и плато не поднимает море. Порядок обхода HashSet
        // недетерминирован, поэтому кандидаты сначала собираются, потом
        // сортируются.
        private static List<TileCoord> PickIslandCampAnchors(
            FragmentBootstrap fragment, int seed, LargeIslandSettings settings)
        {
            var byCoord = new Dictionary<(int, int), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            static bool IsLand(TileBootstrap t) => t.Elevation >= 1;

            var anchors = new List<TileCoord>(settings.Cells.Length);
            foreach (var cell in settings.Cells)
            {
                var island = FloodFrom(byCoord, cell.Center, IsLand);
                var eligible = new List<TileCoord>();
                foreach (var (q, r) in island)
                {
                    var tile = byCoord[(q, r)];
                    if (tile.Elevation < 1 || tile.Elevation > 2 ||
                        !DiskInside(island, q, r, IslandCampCoastClearance))
                    {
                        continue;
                    }

                    eligible.Add(new TileCoord(q, r));
                }

                eligible.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
                if (eligible.Count == 0)
                {
                    anchors.Add(cell.Center); // патологический сид: центр ячейки
                    continue;
                }

                anchors.Add(eligible[(int)(MathUtil.Hash01(seed, eligible.Count, cell.Index, 15701) *
                    eligible.Count) % eligible.Count]);
            }

            // Плато лагеря — то же правило, что у PickCampAnchors: радиус 3,
            // высота 1..2, внешняя кромка карты остаётся морем.
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Q <= settings.MinQ || tile.Q >= settings.MaxQ ||
                    tile.R <= settings.MinR || tile.R >= settings.MaxR)
                {
                    continue;
                }

                foreach (var anchor in anchors)
                {
                    if (HexSpatialMath.HexDistance(new TileCoord(tile.Q, tile.R), anchor) <= 3)
                    {
                        tile.Elevation = System.Math.Max(1, System.Math.Min(2, tile.Elevation));
                        break;
                    }
                }
            }

            return anchors;
        }

        // Прямая по гексам: линейная интерполяция в кубических координатах с
        // кубическим округлением. Детерминирована.
        private static List<TileCoord> HexLine(TileCoord a, TileCoord b)
        {
            var n = HexSpatialMath.HexDistance(a, b);
            var line = new List<TileCoord>(n + 1);
            for (var s = 0; s <= n; s++)
            {
                var t = n == 0 ? 0f : s / (float)n;
                var x = a.Q + (b.Q - a.Q) * t;
                var z = a.R + (b.R - a.R) * t;
                var y = -x - z;
                var rx = System.MathF.Round(x);
                var ry = System.MathF.Round(y);
                var rz = System.MathF.Round(z);
                var dx = System.MathF.Abs(rx - x);
                var dy = System.MathF.Abs(ry - y);
                var dz = System.MathF.Abs(rz - z);
                if (dx > dy && dx > dz)
                {
                    rx = -ry - rz;
                }
                else if (dy > dz)
                {
                    ry = -rx - rz;
                }
                else
                {
                    rz = -rx - ry;
                }

                line.Add(new TileCoord((int)rx, (int)rz));
            }

            return line;
        }

        // §157.3: броды между соседними по сетке островами. Между ближайшей
        // парой пляжных тайлов двух островов режется прямая шириной
        // 2·IslandFordHalfWidth+1: вода остаётся водой, но становится ходибельной
        // (Water && Walkable, Elevation 0 — отмель, по правилу 20.16 вся вода на
        // одном уровне). Суша, касающаяся брода, клампится к высоте 1: сход в
        // брод — шов, а не утёс. Зовётся ПОСЛЕ FinalizeWater.
        private static void CarveIslandFords(FragmentBootstrap fragment, LargeIslandSettings settings)
        {
            var byCoord = new Dictionary<(int, int), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            static bool IsDry(TileBootstrap t) => !t.Water;

            var cells = settings.Cells;
            var coasts = new List<TileCoord>[cells.Length];
            for (var k = 0; k < cells.Length; k++)
            {
                var island = FloodFrom(byCoord, cells[k].Center, IsDry);
                var coast = new List<TileCoord>();
                foreach (var (q, r) in island)
                {
                    if (byCoord[(q, r)].Elevation != 1)
                    {
                        continue;
                    }

                    var touchesWater = false;
                    foreach (var dir in HexDirection.All)
                    {
                        if (byCoord.TryGetValue((q + dir.DQ, r + dir.DR), out var n) && n.Water)
                        {
                            touchesWater = true;
                            break;
                        }
                    }

                    if (touchesWater)
                    {
                        coast.Add(new TileCoord(q, r));
                    }
                }

                coast.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
                coasts[k] = coast;
            }

            var edges = new List<(int a, int b)>();
            for (var j = 0; j < IslandRows; j++)
            {
                for (var i = 0; i < IslandColumns; i++)
                {
                    var k = j * IslandColumns + i;
                    if (i + 1 < IslandColumns) edges.Add((k, k + 1));
                    if (j + 1 < IslandRows) edges.Add((k, k + IslandColumns));
                }
            }

            var fordTiles = new List<TileBootstrap>();
            foreach (var (a, b) in edges)
            {
                TileCoord? from = null;
                TileCoord? to = null;
                var best = int.MaxValue;
                foreach (var pa in coasts[a])
                {
                    foreach (var pb in coasts[b])
                    {
                        var d = HexSpatialMath.HexDistance(pa, pb);
                        if (d < best) // строгое «<» + сортированный обход = детерминизм
                        {
                            best = d;
                            from = pa;
                            to = pb;
                        }
                    }
                }

                if (from is not { } start || to is not { } end)
                {
                    continue; // защитно: у острова нет пляжа высоты 1
                }

                foreach (var step in HexLine(start, end))
                {
                    for (var dq = -IslandFordHalfWidth; dq <= IslandFordHalfWidth; dq++)
                    {
                        var lo = System.Math.Max(-IslandFordHalfWidth, -dq - IslandFordHalfWidth);
                        var hi = System.Math.Min(IslandFordHalfWidth, -dq + IslandFordHalfWidth);
                        for (var dr = lo; dr <= hi; dr++)
                        {
                            if (byCoord.TryGetValue((step.Q + dq, step.R + dr), out var tile) && tile.Water)
                            {
                                tile.Walkable = true;
                                tile.Elevation = 0;
                                fordTiles.Add(tile);
                            }
                        }
                    }
                }
            }

            foreach (var ford in fordTiles)
            {
                foreach (var dir in HexDirection.All)
                {
                    if (byCoord.TryGetValue((ford.Q + dir.DQ, ford.R + dir.DR), out var n) &&
                        !n.Water && n.Elevation > 1)
                    {
                        n.Elevation = 1;
                    }
                }
            }
        }

        // Вода назначается ПОСЛЕ выбора лагерей: плато лагеря (кламп 1..2 в
        // радиусе 3) может поднять прибрежное мелководье в сушу, и флаги
        // обязаны считаться от итоговой высоты.
        private static void FinalizeWater(FragmentBootstrap fragment)
        {
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Elevation <= 0)
                {
                    tile.Elevation = 0;
                    tile.Water = true;
                    tile.Walkable = false;
                }
            }
        }

        // §146.4: рецепт §72.9, обобщённый с одного лагеря на три: материк —
        // крупнейшая связная суша; кандидаты — низина 1-2 вдали от кромки
        // карты; первый якорь — сидированный выбор, следующие — из «дальней
        // трети» по минимальной дистанции до уже выбранных, с детерминированным
        // ослаблением порога на патологических сидах.
        private static List<TileCoord> PickCampAnchors(
            FragmentBootstrap fragment, int seed, LargeIslandSettings settings)
        {
            var byCoord = new Dictionary<(int, int), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            static bool IsLand(TileBootstrap t) => t.Elevation >= 1;

            // Крупнейший связный кусок суши. Компоненты обходим от тайлов в
            // порядке списка (детерминированный порядок заполнения карты).
            var visited = new HashSet<(int, int)>();
            var mainland = new HashSet<(int, int)>();
            foreach (var tile in fragment.Tiles)
            {
                var start = (tile.Q, tile.R);
                if (!IsLand(tile) || visited.Contains(start))
                {
                    continue;
                }

                var component = new HashSet<(int, int)> { start };
                var queue = new Queue<(int, int)>();
                queue.Enqueue(start);
                visited.Add(start);
                while (queue.Count > 0)
                {
                    var (cq, cr) = queue.Dequeue();
                    foreach (var dir in HexDirection.All)
                    {
                        var next = (cq + dir.DQ, cr + dir.DR);
                        if (visited.Contains(next) ||
                            !byCoord.TryGetValue(next, out var neighbor) ||
                            !IsLand(neighbor))
                        {
                            continue;
                        }

                        visited.Add(next);
                        component.Add(next);
                        queue.Enqueue(next);
                    }
                }

                if (component.Count > mainland.Count)
                {
                    mainland = component;
                }
            }

            var eligible = new List<TileCoord>();
            foreach (var tile in fragment.Tiles)
            {
                if (!mainland.Contains((tile.Q, tile.R)) ||
                    tile.Elevation < 1 || tile.Elevation > 2 ||
                    tile.Q < settings.MinQ + 5 || tile.Q > settings.MaxQ - 5 ||
                    tile.R < settings.MinR + 5 || tile.R > settings.MaxR - 5)
                {
                    continue;
                }

                eligible.Add(new TileCoord(tile.Q, tile.R));
            }

            eligible.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));

            var anchors = new List<TileCoord>();
            if (eligible.Count == 0)
            {
                // Патологический сид: садимся на любой материк, что есть.
                foreach (var (q, r) in mainland)
                {
                    anchors.Add(new TileCoord(q, r));
                    break;
                }
            }
            else
            {
                var first = eligible[(int)(MathUtil.Hash01(seed, eligible.Count, 146, 14603) *
                    eligible.Count) % eligible.Count];
                anchors.Add(first);
            }

            for (var campIndex = 1; campIndex < settings.CampCount; campIndex++)
            {
                TileCoord? picked = null;
                for (var separation = settings.CampMinSeparationTiles;
                     separation >= 6 && picked == null;
                     separation -= 2)
                {
                    var fits = new List<(TileCoord tile, int minDist)>();
                    var farthest = 0;
                    foreach (var candidate in eligible)
                    {
                        var minDist = int.MaxValue;
                        foreach (var anchor in anchors)
                        {
                            minDist = System.Math.Min(minDist,
                                HexSpatialMath.HexDistance(candidate, anchor));
                        }

                        if (minDist >= separation)
                        {
                            fits.Add((candidate, minDist));
                            farthest = System.Math.Max(farthest, minDist);
                        }
                    }

                    if (fits.Count == 0)
                    {
                        continue;
                    }

                    // Дальняя треть по минимальной дистанции — «другой конец
                    // острова», но с выбором, а не в одну точку (§72.9).
                    var floor = separation + (farthest - separation) * 2 / 3;
                    var far = new List<TileCoord>();
                    foreach (var (candidate, minDist) in fits)
                    {
                        if (minDist >= floor)
                        {
                            far.Add(candidate);
                        }
                    }

                    if (far.Count == 0)
                    {
                        foreach (var (candidate, _) in fits)
                        {
                            far.Add(candidate);
                        }
                    }

                    picked = far[(int)(MathUtil.Hash01(seed, far.Count, campIndex, 14605) *
                        far.Count) % far.Count];
                }

                if (picked is { } chosen)
                {
                    anchors.Add(chosen);
                }
            }

            // Плато лагеря (правило дома Feud, по одному на лагерь): в радиусе
            // 3 от якоря — высота 1..2. Поднимает прибрежное море в сушу и
            // срезает утёсы, чтобы лагерь никогда не начинался на скале.
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Q <= settings.MinQ || tile.Q >= settings.MaxQ ||
                    tile.R <= settings.MinR || tile.R >= settings.MaxR)
                {
                    continue; // внешняя кромка карты остаётся морем
                }

                foreach (var anchor in anchors)
                {
                    if (HexSpatialMath.HexDistance(new TileCoord(tile.Q, tile.R), anchor) <= 3)
                    {
                        tile.Elevation = System.Math.Max(1, System.Math.Min(2, tile.Elevation));
                        break;
                    }
                }
            }

            return anchors;
        }

        // §146.7: лес рощами. Центры вдали от лагерей, плотность падает от
        // центра рощи: кольцо 0 — 2-3 пальмы, кольцо 1 — 1-2, кольцо 2 — 0-1.
        // Ролл ключуется координатой тайла, а не индексом рощи, поэтому
        // пересечение двух рощ не удваивает лес и не зависит от порядка.
        private static void AddBigForests(
            WorldBootstrapDefinition definition, FragmentBootstrap fragment, int seed,
            List<TileCoord> anchors, LargeIslandSettings settings)
        {
            var byCoord = new Dictionary<(int, int), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            var centers = PickGroveCenters(fragment, seed, anchors, settings);
            var planted = new HashSet<(int, int)>();
            var nextId = 200;
            var palms = 0;

            void Plant(TileBootstrap tile, int count)
            {
                var slots = PalmSlotSets[System.Math.Min(count, 3) - 1];
                foreach (var slot in slots)
                {
                    definition.Objects.Add(Object(nextId++, "tree.palm", 1, tile.Q, tile.R, slot));
                    palms++;
                }

                planted.Add((tile.Q, tile.R));
            }

            bool Plantable(TileBootstrap tile)
            {
                if (tile.Water || !tile.Walkable || tile.Blocked || tile.Indoor ||
                    tile.Elevation < 1 || planted.Contains((tile.Q, tile.R)))
                {
                    return false;
                }

                // §146.7: поляна лагеря — дерево в HexClaimRadius делает гекс
                // нестроябельным, а гекс чертежа выбирается в 2-3 от костра.
                foreach (var anchor in anchors)
                {
                    if (HexSpatialMath.HexDistance(new TileCoord(tile.Q, tile.R), anchor) <= 2)
                    {
                        return false;
                    }
                }

                return true;
            }

            foreach (var grove in centers)
            {
                for (var dq = -2; dq <= 2; dq++)
                {
                    for (var dr = -2; dr <= 2; dr++)
                    {
                        var coord = new TileCoord(grove.Q + dq, grove.R + dr);
                        var ring = HexSpatialMath.HexDistance(coord, grove);
                        if (ring > 2 || !byCoord.TryGetValue((coord.Q, coord.R), out var tile) ||
                            !Plantable(tile))
                        {
                            continue;
                        }

                        var roll = MathUtil.Hash01(seed, coord.Q, coord.R, 14611);
                        switch (ring)
                        {
                            case 0:
                                Plant(tile, roll < 0.5f ? 3 : 2);
                                break;
                            case 1 when roll < 0.60f:
                                Plant(tile, roll < 0.21f ? 2 : 1);
                                break;
                            case 2 when roll < 0.25f:
                                Plant(tile, 1);
                                break;
                        }
                    }
                }
            }

            // Добор одиночками до цели §146.7 — рощи покрывают большую часть,
            // но их выход зависит от сида, а цель — контракт гейта.
            var loose = new List<TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                if (Plantable(tile))
                {
                    loose.Add(tile);
                }
            }

            loose.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
            var attempt = 0;
            while (palms < settings.PalmTarget && loose.Count > 0)
            {
                var pick = (int)(MathUtil.Hash01(seed, loose.Count, attempt++, 14613) * loose.Count);
                pick = System.Math.Min(pick, loose.Count - 1);
                var tile = loose[pick];
                loose.RemoveAt(pick);
                if (Plantable(tile))
                {
                    Plant(tile, 1);
                }
            }
        }

        private static List<TileCoord> PickGroveCenters(
            FragmentBootstrap fragment, int seed, List<TileCoord> anchors,
            LargeIslandSettings settings)
        {
            var candidates = new List<TileCoord>();
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Water || !tile.Walkable || tile.Elevation < 1 || tile.Elevation > 3 ||
                    tile.Q < settings.MinQ + 3 || tile.Q > settings.MaxQ - 3 ||
                    tile.R < settings.MinR + 3 || tile.R > settings.MaxR - 3)
                {
                    continue;
                }

                var coord = new TileCoord(tile.Q, tile.R);
                var nearCamp = false;
                foreach (var anchor in anchors)
                {
                    if (HexSpatialMath.HexDistance(coord, anchor) < 4)
                    {
                        nearCamp = true;
                        break;
                    }
                }

                if (!nearCamp)
                {
                    candidates.Add(coord);
                }
            }

            candidates.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));

            var centers = new List<TileCoord>();
            var attempt = 0;
            while (centers.Count < settings.GroveCount && candidates.Count > 0 &&
                   attempt < settings.GroveCount * 8)
            {
                var pick = (int)(MathUtil.Hash01(seed, candidates.Count, attempt++, 14607) *
                    candidates.Count);
                pick = System.Math.Min(pick, candidates.Count - 1);
                var center = candidates[pick];
                candidates.RemoveAt(pick);

                // Центры не ближе 4 друг к другу: между рощами остаются чистые
                // коридоры, лес никогда не смыкается в стену (§146.7).
                var tooClose = false;
                foreach (var existing in centers)
                {
                    if (HexSpatialMath.HexDistance(center, existing) < 4)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    centers.Add(center);
                }
            }

            return centers;
        }

        // §146.7: остальной посев. Обломки-препятствия (валуны, юкка, кусты,
        // deadfall) держатся в ≥3 тайлах от якорей — поляна и гекс чертежа
        // должны оставаться строябельными; свободные камни и инструменты не
        // блокируют узлы и ложатся где угодно.
        private static void AddBigScatter(
            WorldBootstrapDefinition definition, FragmentBootstrap fragment, int seed,
            List<TileCoord> anchors, LargeIslandSettings settings)
        {
            var taken = new HashSet<(int, int)>();
            foreach (var existing in definition.Objects)
            {
                taken.Add((existing.TileQ, existing.TileR));
            }

            var open = new List<(int q, int r)>();      // свободная суша вдали от лагерей
            var nearCampByAnchor = new List<List<(int q, int r)>>();
            for (var i = 0; i < anchors.Count; i++)
            {
                nearCampByAnchor.Add(new List<(int, int)>());
            }

            foreach (var tile in fragment.Tiles)
            {
                if (tile.Water || !tile.Walkable || tile.Blocked || tile.Indoor ||
                    tile.Elevation < 1 || taken.Contains((tile.Q, tile.R)))
                {
                    continue;
                }

                var coord = new TileCoord(tile.Q, tile.R);
                var minCamp = int.MaxValue;
                var nearest = -1;
                for (var i = 0; i < anchors.Count; i++)
                {
                    var d = HexSpatialMath.HexDistance(coord, anchors[i]);
                    if (d < minCamp)
                    {
                        minCamp = d;
                        nearest = i;
                    }
                }

                if (minCamp >= 3)
                {
                    open.Add((tile.Q, tile.R));
                }

                if (nearest >= 0 && minCamp >= 1 && minCamp <= 4)
                {
                    nearCampByAnchor[nearest].Add((tile.Q, tile.R));
                }
            }

            open.Sort();
            foreach (var list in nearCampByAnchor)
            {
                list.Sort();
            }

            var nextId = 2000;
            void Place(List<(int q, int r)> pool, string definitionId, int count, int salt, int slot)
            {
                for (var i = 0; i < count && pool.Count > 0; i++)
                {
                    var pick = (int)(MathUtil.Hash01(seed, nextId, i, salt) * pool.Count);
                    pick = System.Math.Min(pick, pool.Count - 1);
                    var (q, r) = pool[pick];
                    pool.RemoveAt(pick);
                    definition.Objects.Add(Object(nextId++, definitionId, 1, q, r, slot));
                }
            }

            Place(open, "rock.boulder", settings.BoulderCount, 14615, 1);
            Place(open, "resource.stone", settings.LooseStoneCount, 14617, 2);
            Place(open, "plant.yucca", settings.YuccaCount, 14619, 1);
            Place(open, "forest.deadfall", settings.DeadfallCount, 14621, 2);
            Place(open, "herb.bush", settings.HerbCount, 14623, 1);

            // Дикие запасные инструменты — глушь вознаграждает разведку (§40.12).
            Place(open, "tool.pickaxe_stone", 2, 14625, 2);
            Place(open, "tool.saw", 1, 14627, 2);
            Place(open, "tool.hammer", 1, 14629, 2);

            // §55.4 (bug #317): запасные бутылки. Свою девушки носят со старта;
            // эти ждут потерявшую (или пришедшую с пустыми руками §72) — пара в
            // глуши плюс по одной у каждого лагеря, тем же паттерном, что
            // инструменты выше и ниже.
            Place(open, "tool.bottle", 2, 14639, 2);

            // §146.7: стартовый инструмент каждого лагеря лежит у самого лагеря.
            // Пила ОБЯЗАТЕЛЬНА — без Saw-капабилити доскам дома неоткуда
            // взяться; молоток поднимает постройку, нож валит юкку и разделывает,
            // зажигалка зажигает первый костёр.
            for (var i = 0; i < nearCampByAnchor.Count; i++)
            {
                var pool = nearCampByAnchor[i];
                Place(pool, "tool.saw", 1, 14631 + i * 10, 2);
                Place(pool, "tool.hammer", 1, 14633 + i * 10, 2);
                Place(pool, "tool.knife", 1, 14635 + i * 10, 2);
                Place(pool, "tool.lighter", 1, 14637 + i * 10, 2);
                Place(pool, "tool.bottle", 1, 14643 + i * 10, 2); // §55.4 (bug #317)
            }
        }

        // §146.4/§146.9: заданное режимом число девушек на лагерь — якорь и
        // кольцо-1. Профили нужд
        // рассинхронизированы ГЛОБАЛЬНЫМ индексом (формулы §33.4 из Feud):
        // шесть девушек не встанут в очередь к одной нужде даже в разных
        // лагерях, а прибытия §132 доведут состав со временем.
        private static void AddBigColonists(
            WorldBootstrapDefinition definition, List<TileCoord> anchors, int girlsPerCamp)
        {
            for (var campIndex = 0; campIndex < anchors.Count; campIndex++)
            {
                var anchor = anchors[campIndex];
                var seats = new List<TileCoord> { anchor };
                foreach (var dir in HexDirection.All)
                {
                    seats.Add(new TileCoord(anchor.Q + dir.DQ, anchor.R + dir.DR));
                }

                for (var j = 0; j < girlsPerCamp; j++)
                {
                    var i = campIndex * girlsPerCamp + j;
                    var npc = new NpcBootstrap
                    {
                        // 1,2 / 11,12 / 21,22 — десятка на лагерь: прибытия и
                        // чужие id (101+, 1000+, 2000+) не пересекаются никогда.
                        Id = campIndex * 10 + j + 1,
                        Faction = CampFaction(campIndex),
                        FragmentId = 1,
                        TileQ = seats[j].Q,
                        TileR = seats[j].R,
                        Hunger = 0.70f - 0.10f * (i % 4),
                        Thirst = 0.40f + 0.07f * (i % 3),
                        Energy = 0.45f + 0.10f * (i % 4),
                        Comfort = 0.35f + 0.07f * (i % 3),
                        Social = 0.45f + 0.09f * (i % 3),
                        ThermalDiscomfort = 0.60f - 0.09f * (i % 3),
                    };

                    // §146.11: normalized 1.0 is displayed as 10/10. Only id=1
                    // belongs to the player-controlled Colony camp.
                    if (definition.Simulation.Mode == GameMode.Maniac &&
                        campIndex == 0 && j == 0)
                    {
                        npc.Attributes[Agents.AttributeKind.Strength] = 1f;
                        npc.Attributes[Agents.AttributeKind.Agility] = 1f;
                        npc.Attributes[Agents.AttributeKind.Endurance] = 1f;
                        npc.Attributes[Agents.AttributeKind.Toughness] = 1f;
                        npc.Attributes[Agents.AttributeKind.Hardiness] = 1f;
                        npc.Attributes[Agents.AttributeKind.Wits] = 1f;
                        npc.Attributes[Agents.AttributeKind.Perception] = 1f;
                    }

                    definition.Npcs.Add(npc);
                }
            }
        }
    }
}
