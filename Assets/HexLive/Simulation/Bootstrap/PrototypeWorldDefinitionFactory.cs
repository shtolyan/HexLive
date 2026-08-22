using System;
using System.Collections.Generic;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Bootstrap
{
    public static partial class PrototypeWorldDefinitionFactory
    {
        // §73: границы карты. Были зашиты числами в четырёх местах, из-за чего
        // «расширить остров» означало найти и согласовать их все, включая те,
        // что задают пролив и второй островок у восточного края. Теперь край
        // один, и всё, что должно жить НА краю, считается от него.
        //
        // Радиальный спад в AddIslandElevation нормируется на MaxQ, поэтому
        // раздвигание границ растит сушу наружу, а не топит её: дом колонии
        // остаётся там же, вокруг него просто становится больше острова.
        public const int MinQ = -13;
        public const int MaxQ = 15;
        public const int MinR = -10;
        public const int MaxR = 12;

        /// <summary>
        /// Swaps the map without touching anything else about the game.
        ///
        /// The real game is assembled at runtime by PrototypeRuntimeBootstrap in
        /// whatever scene is loaded, and it switches itself off as soon as a
        /// SimulationRunnerBehaviour already exists — which is exactly what
        /// every dev scene does, and exactly why none of them has the menus,
        /// the loading screen, the RTS camera or the HUD. A scene that wants
        /// the WHOLE game on a different map therefore must not build a runner;
        /// it sets this instead, from Awake, and lets the ordinary bootstrap do
        /// the rest. LoadingScreen is the only reader that matters and it runs
        /// long after Awake.
        ///
        /// Null means the shipped island. Clear it when the scene unloads: it is
        /// static, and a stale override would follow the player into a new game.
        /// </summary>
        public static Func<int, WorldBootstrapDefinition> Override;

        // §146.1/§146.11: one entry point for every append-only scenario. The
        // Feud body below is the shipped island, byte-for-byte; large-island
        // variants live in their own shared partial.
        // The dev-scene Override wins regardless of mode — those scenes are
        // deliberately mode-less.
        public static WorldBootstrapDefinition Create(
            int seed = 12345, GameMode mode = GameMode.Feud)
        {
            var over = Override;
            if (over != null) return over(seed);

            return mode switch
            {
                GameMode.BigIsland => CreateBigIsland(seed),
                GameMode.HugeIsland => CreateHugeIsland(seed),
                GameMode.Maniac => CreateManiac(seed),
                _ => CreateFeud(seed)
            };
        }

        private static WorldBootstrapDefinition CreateFeud(
            int seed, bool includeOutsiders = true)
        {
            var definition = new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings
                {
                    TickDeltaTime = 0.25f,
                    MediumTickInterval = 4,
                    SlowTickInterval = 16,
                    Seed = seed,
                    Mode = GameMode.Feud,
                    SpawnCompletedTestHut = true
                },
                Environment = new EnvironmentBootstrap
                {
                    GlobalTemperature = 9f
                },
                Fragments =
                {
                    new FragmentBootstrap
                    {
                        Id = 1,
                        Tiles = new List<TileBootstrap>
                        {
                            Tile(0, 0, indoor: true),
                            // Long wall through tile (1,0): blocks right side
                            Tile(1, 0, indoor: true, blockedSlots: new[] { 6, 7, 13, 14, 20, 21 }),
                            Tile(2, 0, indoor: true),
                            Tile(3, 0, walkable: false, blocked: true),
                            // Vertical wall in tile (0,1)
                            Tile(0, 1, indoor: true, blockedSlots: new[] { 5, 11, 18, 25, 31 }),
                            // L-shaped wall in tile (1,1)
                            Tile(1, 1, indoor: true, blockedSlots: new[] { 15, 16, 17, 18, 25, 31 }),
                            // Diagonal wall in tile (2,1)
                            Tile(2, 1, indoor: true, blockedSlots: new[] { 9, 10, 17, 24, 27 }),
                            // Partial wall in tile (3,1)
                            Tile(3, 1, indoor: true, blockedSlots: new[] { 16, 17, 23, 24 }),
                            // Corridor wall in tile (-1,2)
                            Tile(-1, 2, indoor: true, blockedSlots: new[] { 12, 13, 19, 20 }),
                            Tile(0, 2, indoor: true),
                            Tile(1, 2, walkable: false, blocked: true),
                            // Wall in tile (2,2)
                            Tile(2, 2, indoor: true, blockedSlots: new[] { 11, 18, 25 }),
                            Tile(-1, 3, indoor: true),
                            Tile(0, 3, indoor: true),
                            // Small barrier in tile (1,3)
                            Tile(1, 3, indoor: true, blockedSlots: new[] { 17, 18, 19, 24, 25 }),
                            Tile(2, 3, indoor: true),

                            // Outdoor yard wrapping the west and south edges of the
                            // house, plus an east pocket. Food only grows out here,
                            // forcing the house <-> yard survival loop (iteration 2).
                            Tile(-1, 0),
                            Tile(-2, 1),
                            Tile(-1, 1),
                            Tile(-2, 2),
                            Tile(-2, 3),
                            Tile(-2, 4),
                            Tile(-1, 4),
                            Tile(0, 4),
                            Tile(1, 4),
                            Tile(2, 4),
                            Tile(3, 2),
                            Tile(4, 1)
                        }
                    }
                },
                Objects =
                {
                    // Spec §54 COLD START: nothing is pre-built or handed out at
                    // home. The yard keeps only the natural resources — palms
                    // (food/wood/leaves) and deadfall (ready sticks). The hearth is
                    // a build-site the colony raises from stones (CreateCampfireSite),
                    // the raft is a coastal build-marker, and every tool/garment is
                    // gathered, found in the wild, or crafted. Retired from the
                    // yard: the finished campfire, lighter, pot, starting wood,
                    // chair, coat, armor and the wardrobe (pot/lighter/armor are
                    // now findable wilderness loot — see AddNaturalFeatures).
                    Object(104, "tree.palm", 1, -2, 2, 1),
                    Object(105, "tree.palm", 1, 1, 4, 1),
                    // §54.2: only big palms now (small palm retired).
                    Object(107, "tree.palm", 1, 3, 2, 1),
                    Object(108, "tree.palm", 1, 6, 1, 1),
                    Object(122, "forest.deadfall", 1, 6, 3, 2),
                    Object(123, "forest.deadfall", 1, -4, 4, 2)
                },
                // §74: the four are no longer a fixed cast. Name, body mesh,
                // material set, hairstyle and voice are left BLANK on purpose —
                // WorldStateFactory.AssignAppearance rolls each one from the
                // world seed, so a new island brings new women. What stays
                // authored is what the simulation actually reads: the tiles
                // they wash ashore on and the desynchronized need profiles
                // below, so the four never queue for the same need at once.
                Npcs = { }
            };

            AddColonists(definition);
            AddWilderness(definition.Fragments[0]);
            AddIslandElevation(definition.Fragments[0], seed);
            AddSeaChannel(definition.Fragments[0], seed);
            AddNaturalFeatures(definition, seed);
            if (includeOutsiders)
            {
                AddOutsiderCamp(definition, seed);
            }
            return definition;
        }

        // §75: состав колонии генерируется от WorldBalance.ColonistCount, а не
        // задан списком строк. Дом авторский и конечный, поэтому мест ровно
        // столько, сколько размечено — больше народу просто некуда посадить.
        //
        // Профили нужд разведены по индексу СПЕЦИАЛЬНО: если все стартуют
        // одинаковыми, они синхронно захотят пить, синхронно пойдут к воде и
        // синхронно встанут в очередь за одной кружкой (spec 33.4).
        private static void AddColonists(WorldBootstrapDefinition definition)
        {
            var spots = new[]
            {
                (0, 0), (2, 0), (0, 3), (2, 3), (2, 1), (-1, 3), (0, 2), (3, 1),
            };

            var count = System.Math.Max(0,
                System.Math.Min(
                    HexLive.Simulation.Runtime.WorldBalance.ColonistCount,
                    System.Math.Min(
                        spots.Length,
                        System.Math.Min(
                            HexLive.Simulation.Runtime.WorldBalance.MaxColonyNpcs,
                            HexLive.Simulation.Runtime.WorldBalance.MaxLivingNpcs))));
            for (var i = 0; i < count; i++)
            {
                var (q, r) = spots[i];
                // Смещения подобраны так, чтобы соседние по индексу профили не
                // совпадали ни по одной нужде.
                definition.Npcs.Add(new NpcBootstrap
                {
                    Id = i + 1,
                    FragmentId = 1,
                    TileQ = q,
                    TileR = r,
                    Hunger = 0.70f - 0.10f * (i % 4),
                    Thirst = 0.40f + 0.07f * (i % 3),
                    Energy = 0.45f + 0.10f * (i % 4),
                    Comfort = 0.35f + 0.07f * (i % 3),
                    Social = 0.45f + 0.09f * (i % 3),
                    ThermalDiscomfort = 0.60f - 0.09f * (i % 3),
                });
            }
        }

        // §72: the hostile survivor and his camp on the far side of the island.
        //
        // The camp tile is COMPUTED, never hard-coded: the island's shape is
        // seeded noise (AddIslandElevation), so any fixed coordinate is open sea
        // on some fraction of seeds. We take the farthest walkable lowland tile
        // from the colony hearth — "the other end of the island" on every seed,
        // deterministically.
        private static void AddOutsiderCamp(WorldBootstrapDefinition definition, int seed)
        {
            var colonyHome = new TileCoord(0, 4);
            var fragment = definition.Fragments[0];

            // Стоянка обязана быть на ТОЙ ЖЕ СУШЕ, что и колония. Без этого
            // «самый дальний проходимый тайл» — это крошечный островок у края
            // карты (шум высот их щедро сеет), и чужак оказывался заперт на двух
            // гексах посреди моря: ни дойти до девушек, ни выжить.
            var mainland = new HashSet<(int, int)>();
            var byCoord = new Dictionary<(int, int), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            static bool IsLand(TileBootstrap t) =>
                t.Walkable && !t.Water && !t.Blocked && t.Elevation >= 1;

            if (byCoord.TryGetValue((colonyHome.Q, colonyHome.R), out var start) && IsLand(start))
            {
                var queue = new Queue<(int, int)>();
                queue.Enqueue((colonyHome.Q, colonyHome.R));
                mainland.Add((colonyHome.Q, colonyHome.R));
                while (queue.Count > 0)
                {
                    var (cq, cr) = queue.Dequeue();
                    foreach (var dir in HexDirection.All)
                    {
                        var next = (cq + dir.DQ, cr + dir.DR);
                        if (mainland.Contains(next) ||
                            !byCoord.TryGetValue(next, out var neighbor) ||
                            !IsLand(neighbor))
                        {
                            continue;
                        }

                        mainland.Add(next);
                        queue.Enqueue(next);
                    }
                }
            }

            // Дальше — как раньше: дальняя треть, выбор по сиду. «Самый дальний»
            // детерминирован и на любом сиде упирался бы в один угол карты.
            var candidates = new List<TileBootstrap>();
            var farthest = 0;
            foreach (var tile in fragment.Tiles)
            {
                // Низина 1-2 — та же полоса, к которой прижат дом колонии, так
                // что его стоянка не окажется ни на скале, ни в прибое.
                if (!IsLand(tile) || tile.Elevation > 2 ||
                    !mainland.Contains((tile.Q, tile.R)))
                {
                    continue;
                }

                var distance = HexSpatialMath.HexDistance(
                    new TileCoord(tile.Q, tile.R), colonyHome);
                if (distance >= HexLive.Simulation.Runtime.Spec72.OutsiderCampMinDistanceTiles)
                {
                    candidates.Add(tile);
                    farthest = System.Math.Max(farthest, distance);
                }
            }

            if (candidates.Count == 0)
            {
                return; // патологический сид — оставляем мир одностановищным
            }

            // Дальняя треть диапазона: заведомо «другой конец острова», но с
            // выбором, а не в одну точку.
            var floor = HexLive.Simulation.Runtime.Spec72.OutsiderCampMinDistanceTiles +
                (farthest - HexLive.Simulation.Runtime.Spec72.OutsiderCampMinDistanceTiles) * 2 / 3;
            var far = new List<TileBootstrap>();
            foreach (var tile in candidates)
            {
                if (HexSpatialMath.HexDistance(new TileCoord(tile.Q, tile.R), colonyHome) >= floor)
                {
                    far.Add(tile);
                }
            }

            if (far.Count == 0)
            {
                far = candidates;
            }

            // Сортируем по координате, а не полагаемся на порядок списка тайлов:
            // выбор обязан зависеть только от сида, иначе мир перестанет быть
            // воспроизводимым.
            far.Sort((a, b) => a.Q != b.Q ? a.Q.CompareTo(b.Q) : a.R.CompareTo(b.R));
            var best = far[(int)(MathUtil.Hash01(seed, far.Count, 72, 7201) * far.Count) % far.Count];

            var camp = new TileCoord(best.Q, best.R);

            // §72.12: ЛОГОВО. Санктуарий в этом мире — это буквально indoor-тайл
            // (MobSystem.IsNpcInSanctuary), и у колонии он есть, потому что её
            // дом размечен вручную: собаки бросают погоню у двери (§29C.4A).
            // Стоянка чужака вычисляется в дикой земле, indoor-тайлов там нет —
            // то есть девушкам всегда есть куда нырнуть, а ему некуда НИКОГДА.
            // Отсюда и его смерти от волков на 0.3-й день.
            //
            // Даём ему тот же механизм, а не особое правило: якорь и кольцо
            // вокруг него становятся indoor. Вода и скалы пропускаются — логово
            // должно быть проходимой сушей.
            best.Indoor = true;
            foreach (var dir in HexDirection.All)
            {
                if (byCoord.TryGetValue((camp.Q + dir.DQ, camp.R + dir.DR), out var around) &&
                    IsLand(around))
                {
                    around.Indoor = true;
                }
            }

            if (HexLive.Simulation.Runtime.Spec72.Enabled)
            {
                definition.FactionHomes.Add(new FactionHomeBootstrap
                {
                    Faction = Agents.Faction.Colony,
                    TileQ = colonyHome.Q,
                    TileR = colonyHome.R
                });
                definition.FactionHomes.Add(new FactionHomeBootstrap
                {
                    Faction = Agents.Faction.Outsiders,
                    TileQ = camp.Q,
                    TileR = camp.R
                });
            }

            // §132: оба потолка действуют уже на стартовый ростер, а не только на
            // будущие волны. Заданный в конфиге перекомплект не создаёт мир,
            // который уже в нулевом тике нарушает обещанный максимум.
            var remainingWorldSeats = System.Math.Max(0,
                HexLive.Simulation.Runtime.WorldBalance.MaxLivingNpcs - definition.Npcs.Count);
            var outsiders = System.Math.Max(0,
                System.Math.Min(
                    HexLive.Simulation.Runtime.Spec72.OutsiderCount,
                    System.Math.Min(
                        HexLive.Simulation.Runtime.WorldBalance.MaxOutsiderNpcs,
                        remainingWorldSeats)));
            if (outsiders == 0)
            {
                return;
            }

            // Идентификаторы чужаков начинаются с 101, а не продолжают ряд
            // девушек: состав колонии теперь переменной длины, и «следующий
            // свободный номер» разъезжался бы при каждой смене ColonistCount.
            // Дырка в нумерации ничему не мешает — id идёт в хеши числом, а не
            // индексом, — зато чужаки всегда вставляются ПОСЛЕ колонии, и
            // порядок обхода словаря у неё остаётся прежним.
            var seats = new List<TileCoord> { camp };
            foreach (var dir in HexDirection.All)
            {
                var around = new TileCoord(camp.Q + dir.DQ, camp.R + dir.DR);
                if (byCoord.TryGetValue((around.Q, around.R), out var seat) && IsLand(seat))
                {
                    seats.Add(around);
                }
            }

            for (var i = 0; i < outsiders && i < seats.Count; i++)
            {
                definition.Npcs.Add(new NpcBootstrap
                {
                    Id = 101 + i,
                    // Имя и тело только у первого — остальные пока безымянные:
                    // §74 раскатывает облик от сида, а мужской набор внешностей
                    // ещё не заведён, так что все они получат тело Кшиштофа.
                    DisplayName = i == 0 ? "Kshishtof" : string.Empty,
                    // Должно разбираться в ActorName, иначе вид молча подставит
                    // тело МАРТЫ — а это читается как сломанный импорт, не как
                    // опечатка.
                    ActorMesh = "Kshishtof",
                    Faction = Agents.Faction.Outsiders,
                    FragmentId = 1,
                    TileQ = seats[i].Q,
                    TileR = seats[i].R,
                    // Сошёл на берег как все: выспавшийся, голодный, жаждущий.
                    // Ничего ему не дарят — нож и копьё он приносит с собой.
                    Hunger = 0.50f - 0.06f * (i % 3),
                    Thirst = 0.45f + 0.06f * (i % 3),
                    Energy = 0.70f - 0.08f * (i % 3),
                    Comfort = 0.40f,
                    Social = 0.30f,
                    ThermalDiscomfort = 0.50f,
                    // §76: его тело АВТОРСКОЕ, а не выпавшее. Девушки катятся от
                    // сида по бюджету §76.2 — у каждого мира свои; он один и тот
                    // же всегда, потому что противник, который на половине сидов
                    // выпадает хилым, читается как поломка, а не как разнообразие.
                    // Идёт обычным путём переопределений (§74: заполненное —
                    // намерение автора), а не особой веткой внутри ролла.
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
                    // §126: его характер — тоже АВТОРСКИЙ, не выпавший. Гнобит
                    // (§81) и не моется (§89) он не потому, что он «чужой» —
                    // фракция теперь отвечает только на вопрос, кто кому враг, —
                    // а потому, что он вот такой человек. Список задан явно,
                    // значит ролл §126 для него не бросается вовсе.
                    Traits = new System.Collections.Generic.List<string>
                    {
                        Agents.TraitKind.Abuser.ToString(),
                        Agents.TraitKind.Slob.ToString()
                    },
                });
            }
        }

        // Spec 20.16: the world is an island — seeded value noise times a
        // radial falloff carves sea, lowland, hills and mountains. The home
        // plateau is clamped so the colony never spawns on a cliff.
        private static void AddIslandElevation(FragmentBootstrap fragment, int seed)
        {
            // Центр спада — дом; край берётся из границ карты, иначе новые
            // тайлы окажутся за пределами спада и утонут все разом.
            var center = HexSpatialMath.TileToWorld(new TileCoord(1, 1));
            var edge = HexSpatialMath.TileToWorld(new TileCoord(MaxQ, 1));
            var maxDist = System.Math.Abs(edge.X - center.X);

            var home = new TileCoord(0, 2);
            var hutSite = new TileCoord(5, -4);

            foreach (var tile in fragment.Tiles)
            {
                var coord = new TileCoord(tile.Q, tile.R);
                var world = HexSpatialMath.TileToWorld(coord);
                var dx = world.X - center.X;
                var dy = world.Y - center.Y;
                var dist = System.MathF.Sqrt(dx * dx + dy * dy) / maxDist;

                // Radial island falloff: 1 at the center, 0 past ~0.95.
                var falloff = MathUtil.Clamp01(1f - dist * dist * 1.15f);

                var noise = ValueNoise(seed, world.X * 0.13f, world.Y * 0.13f) * 0.55f +
                            ValueNoise(seed + 17, world.X * 0.34f, world.Y * 0.34f) * 0.45f;
                // Sharpen: squaring pushes midtones down, peaks stand out
                // and neighboring quantization steps jump 2+ (real cliffs).
                var height = (noise * noise * 1.4f + 0.3f) * falloff;

                var elevation = (int)System.MathF.Round(height * 6f);
                elevation = System.Math.Min(5, elevation);

                // The map's outer ring is always open sea — no straight-cut
                // coastline at the world bounds.
                if (tile.Q <= MinQ || tile.Q >= MaxQ || tile.R <= MinR || tile.R >= MaxR)
                {
                    elevation = 0;
                }

                // Home plateau: never sea, never cliffside.
                var nearHome = HexSpatialMath.HexDistance(coord, home) <= 3 ||
                               HexSpatialMath.HexDistance(coord, hutSite) <= 3;
                if (nearHome)
                {
                    elevation = System.Math.Max(1, System.Math.Min(2, elevation));
                }

                // Spec 40.18: a small second island in the SE sea, reachable via
                // the swim strait opened in OpenStraitCorridor.
                if (coord.Q == MaxQ - 1 && (coord.R == MaxR - 4 || coord.R == MaxR - 3))
                {
                    elevation = System.Math.Max(elevation, 1);
                }

                if (elevation <= 0)
                {
                    // Open sea: visible water, but nobody swims off the island.
                    tile.Elevation = 0;
                    tile.Water = true;
                    tile.Walkable = false;
                }
                else
                {
                    tile.Elevation = elevation;
                }
            }
        }

        // Deterministic value noise: Hash01 lattice + bilinear interpolation.
        private static float ValueNoise(int seed, float x, float y)
        {
            var x0 = (int)System.MathF.Floor(x);
            var y0 = (int)System.MathF.Floor(y);
            var tx = x - x0;
            var ty = y - y0;
            var sx = tx * tx * (3f - 2f * tx);
            var sy = ty * ty * (3f - 2f * ty);

            var v00 = MathUtil.Hash01(seed, x0, y0, 7001);
            var v10 = MathUtil.Hash01(seed, x0 + 1, y0, 7001);
            var v01 = MathUtil.Hash01(seed, x0, y0 + 1, 7001);
            var v11 = MathUtil.Hash01(seed, x0 + 1, y0 + 1, 7001);

            var a = v00 + (v10 - v00) * sx;
            var b = v01 + (v11 - v01) * sx;
            return a + (b - a) * sy;
        }

        // Spec 29C: wilderness — a walkable outdoor region around the
        // hand-authored home, filled programmatically. Room to roam, room
        // for dogs to spawn far from anyone.
        private static void AddWilderness(FragmentBootstrap fragment)
        {
            var existing = new HashSet<(int q, int r)>();
            foreach (var tile in fragment.Tiles)
            {
                existing.Add((tile.Q, tile.R));
            }

            for (var q = MinQ; q <= MaxQ; q++)
            {
                for (var r = MinR; r <= MaxR; r++)
                {
                    if (existing.Contains((q, r)))
                    {
                        continue;
                    }

                    fragment.Tiles.Add(Tile(q, r));
                }
            }
        }

        // §55: rivers are retired — what was the winding river is now an ordinary
        // sea inlet. The same seeded channel is carved as UNWALKABLE deep water
        // (sea = Water without Walkable), so NPCs swim it rather than wade, and
        // it is no longer drinkable. The one-deep swim ring (WorldStateFactory)
        // still opens this ≤2-wide channel so it never boxes anyone in.
        private static void AddSeaChannel(FragmentBootstrap fragment, int seed)
        {
            var byCoord = new Dictionary<(int q, int r), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                byCoord[(tile.Q, tile.R)] = tile;
            }

            var wander = 0;
            for (var r = MinR; r <= MaxR; r++)
            {
                var roll = MathUtil.Hash01(seed, r, 0, 1201);
                wander += roll < 0.33f ? -1 : roll > 0.66f ? 1 : 0;
                wander = System.Math.Max(-2, System.Math.Min(2, wander));

                // Keep world-x roughly constant: q + r/2 ~ 8 + wander.
                var q = (MaxQ - 2) + wander - (r + 600) / 2 + 300;
                foreach (var dq in MathUtil.Hash01(seed, r, 1, 1201) < 0.4f ? new[] { 0, 1 } : new[] { 0 })
                {
                    if (byCoord.TryGetValue((q + dq, r), out var tile) &&
                        !tile.Indoor && !tile.Blocked)
                    {
                        tile.Water = true;
                        tile.Walkable = false; // §55: deep sea — swum, not waded, and undrinkable
                        tile.Elevation = 0;    // spec 20.16: ALL water shares one level — flush with the sea
                        tile.BlockedSlots.Clear();
                    }
                }
            }
        }

        // Spec 35.1: seeded terrain props — inert until iterations 18/20.
        private static void AddNaturalFeatures(WorldBootstrapDefinition definition, int seed)
        {
            var fragment = definition.Fragments[0];
            var taken = new HashSet<(int q, int r)>();
            foreach (var existing in definition.Objects)
            {
                taken.Add((existing.TileQ, existing.TileR));
            }

            var candidates = new List<(int q, int r)>();
            foreach (var tile in fragment.Tiles)
            {
                if (tile.Walkable && !tile.Blocked && !tile.Indoor && !tile.Water &&
                    !taken.Contains((tile.Q, tile.R)))
                {
                    candidates.Add((tile.Q, tile.R));
                }
            }

            var nextId = 124;
            void Place(string definitionId, int count, int salt, int slot)
            {
                for (var i = 0; i < count && candidates.Count > 0; i++)
                {
                    var pick = (int)(MathUtil.Hash01(seed, nextId, i, salt) * candidates.Count);
                    pick = System.Math.Min(pick, candidates.Count - 1);
                    var (q, r) = candidates[pick];
                    candidates.RemoveAt(pick);
                    definition.Objects.Add(Object(nextId++, definitionId, 1, q, r, slot));
                }
            }

            // More stone in the world: the campfire's stone ring alone wants 18,
            // and stones are also eaten by the axe/pickaxe/knife recipes — the old
            // 8 boulders + 12 loose ran the hearth short. Boulders yield 5 each
            // (needs a pickaxe), loose stones are free to pick up.
            Place("rock.boulder", 24, 331, 1);
            Place("resource.stone", 48, 443, 2);
            // §54.2: the old big tree is RETIRED, and the small palm too — only the
            // big palm (3 logs + crown) spawns now.
            Place("tree.palm", 6, 661, 1);        // big palms (3 logs)
            Place("tree.palm", 6, 557, 1);        // (was small palms — now big)
            Place("tool.saw", 1, 773, 2); // spec 35.2: findable wilderness loot
            // Spec 40.12: more scattered gear — the wilds reward exploring, and
            // a found tool saves a craft. GatherTools already collects any
            // reachable Tool not carried.
            Place("tool.saw", 1, 991, 2);
            Place("herb.bush", 3, 1213, 1); // spec 44: healing herb
            Place("plant.yucca", 18, 1327, 1); // spec §54: yucca — cut for fiber (rope/cloth); consumed, so seed plenty (beds need 8 rope = 8 fiber each)
            Place("tool.knife", 1, 1451, 2); // spec §54: one findable knife bootstraps butchering
            Place("tool.hammer", 2, 1489, 1); // spec §54.2: findable hammers raise the bed build-sites
            // Spec §54 cold start: the home conveniences are no longer handed
            // out — the lighter (a spark) is findable wilderness loot instead,
            // so the wilds still reward exploring. (The pot stood next to it
            // until §55.2 retired boiling and left it a prop with no verb.)
            Place("tool.lighter", 1, 1663, 1);

            // §119 test hook: exactly two arms and two legs, one wooden and one
            // mechanical of each. Place() removes every chosen dry/free tile,
            // so all four are distinct and seed-deterministic on NEW maps only.
            if (HexLive.Simulation.Runtime.Spec119.TestProstheticMapDrops)
            {
                Place(ContentIds.WoodenArm, 1, 11901, 2);
                Place(ContentIds.MechanicalArm, 1, 11902, 2);
                Place(ContentIds.WoodenLeg, 1, 11903, 2);
                Place(ContentIds.MechanicalLeg, 1, 11904, 2);
            }

            // Spec 40.18 step 4: the ONLY pickaxe sits on the second island —
            // an island-exclusive tool a GatherTools NPC must cross the strait
            // for (plus a palm for food/wood), reached via the cheap strait.
            definition.Objects.Add(Object(nextId++, "tree.palm", 1, 9, 4, 1));
            definition.Objects.Add(Object(nextId++, "tool.pickaxe_stone", 1, 9, 5, 2));

            // §55: river drink-anchors retired — rivers are gone and water is no
            // longer drinkable. Thirst is quenched by cracking a coconut instead
            // (see food.coconut's Drink interaction).

            // Spec 40.15: the escape raft on the coast — a walkable, non-water
            // tile beside the sea, nearest to home (the way off the island).
            // §40.15 r2: the raft is DISABLED pending its redesign
            // (SimBalance.RaftEnabled). The goal gates were already off, but the
            // coastal vessel.raft OBJECT was still seeded — so every new world
            // spawned a log-raft prop nobody could use. Skip the whole block.
            if (!HexLive.Simulation.Runtime.SimBalance.RaftEnabled)
            {
                return;
            }

            var raftCoords = new Dictionary<(int q, int r), TileBootstrap>();
            foreach (var tile in fragment.Tiles)
            {
                raftCoords[(tile.Q, tile.R)] = tile;
            }

            var home = new TileCoord(0, 2);
            TileBootstrap raftTile = null;
            var raftBest = int.MaxValue;
            foreach (var tile in fragment.Tiles)
            {
                if (!tile.Walkable || tile.Water || tile.Indoor)
                {
                    continue;
                }

                var beside = false;
                foreach (var dir in HexDirection.All)
                {
                    if (raftCoords.TryGetValue((tile.Q + dir.DQ, tile.R + dir.DR), out var n) && n.Water)
                    {
                        beside = true;
                        break;
                    }
                }

                if (!beside)
                {
                    continue;
                }

                var d = HexSpatialMath.HexDistance(new TileCoord(tile.Q, tile.R), home);
                if (d < raftBest)
                {
                    raftBest = d;
                    raftTile = tile;
                }
            }

            if (raftTile is not null)
            {
                definition.Objects.Add(Object(nextId++, "vessel.raft", 1, raftTile.Q, raftTile.R, 1));
            }
        }

        private static TileBootstrap Tile(int q, int r, bool walkable = true, bool indoor = false, bool blocked = false, params int[] blockedSlots)
        {
            return new TileBootstrap
            {
                Q = q,
                R = r,
                Walkable = walkable,
                Indoor = indoor,
                Blocked = blocked,
                BlockedSlots = new List<int>(blockedSlots)
            };
        }


        private static ObjectBootstrap Object(int id, string definitionId, int fragmentId, int tileQ, int tileR, params int[] junctionSlots)
        {
            return new ObjectBootstrap
            {
                Id = id,
                DefinitionId = definitionId,
                FragmentId = fragmentId,
                TileQ = tileQ,
                TileR = tileR,
                JunctionSlots = new List<int>(junctionSlots)
            };
        }
    }
}
