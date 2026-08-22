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

            public LargeIslandSettings(
                GameMode mode, int minQ, int maxQ, int minR, int maxR,
                int campCount, int campMinSeparationTiles, int girlsPerCamp,
                int palmTarget, int groveCount, int yuccaCount, int boulderCount,
                int looseStoneCount, int deadfallCount, int herbCount,
                bool hasCentralOutsiderCamp)
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

            AddBigElevation(fragment, seed, settings);
            var anchors = PickCampAnchors(fragment, seed, settings);
            FinalizeWater(fragment);

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
                    definition.Npcs.Add(new NpcBootstrap
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
                    });
                }
            }
        }
    }
}
