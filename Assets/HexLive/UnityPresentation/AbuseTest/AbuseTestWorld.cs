using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.UnityPresentation.AbuseTest
{
    /// <summary>
    /// §91: арена «он и она» — маленький, но НАСТОЯЩИЙ островок.
    ///
    /// ⭐ Первая версия была стерильной лабораторией: нужды заморожены,
    /// отвлечений нет, вокруг голая земля. Она доказывала ровно ничего — в
    /// таких условиях абьюз случается неизбежно, а вопрос был не «может ли
    /// он», а «переживёт ли он конкуренцию с пеньком, кокосом и жаждой».
    /// Именно на пеньке он и залипал в настоящей игре.
    ///
    /// Поэтому здесь всё как в жизни, только тесно: нужды текут, есть где
    /// посидеть, что съесть, что выпить и что подобрать. Остров маленький
    /// НАРОЧНО — чтобы они постоянно попадались друг другу и «не встретились»
    /// перестало быть объяснением.
    ///
    /// Мир живёт в общем классе: его строит и Unity-сцена, и headless-проба.
    /// Иначе мы смотрим на два разных мира и спорим, у кого что происходит.
    /// </summary>
    public static class AbuseTestWorld
    {
        public const int GirlId = 1;   // ещё две — 2 и 3
        public const int OutsiderId = 101;

        private static NpcBootstrap Girl(int id, string name, int q, int r) =>
            new()
            {
                Id = id,
                DisplayName = name,
                ActorMesh = name,
                FragmentId = 1,
                TileQ = q,
                TileR = r,
                // Обычные живые девушки, а не манекены: им есть чем заняться и
                // есть от чего отвлечься.
                Hunger = 0.35f,
                Thirst = 0.35f,
                Energy = 0.8f,
                Comfort = 0.5f,
                Social = 0.6f,
                ThermalDiscomfort = 0.2f
            };

        public static WorldBootstrapDefinition Build(int seed = 313)
        {
            var tiles = new List<TileBootstrap>();
            for (var r = -3; r <= 3; r++)
            {
                for (var q = -4; q <= 4; q++)
                {
                    // Полоса воды по южному краю: пить, купаться, стирать —
                    // всё то, на что он отвлекался в настоящей игре.
                    var water = r == 3;
                    tiles.Add(new TileBootstrap
                    {
                        Q = q,
                        R = r,
                        Walkable = !water,
                        Water = water,
                        Elevation = water ? 0 : 1
                    });
                }
            }

            var objects = new List<ObjectBootstrap>();
            var id = 1;
            void Put(string def, int q, int r) =>
                objects.Add(new ObjectBootstrap
                {
                    Id = id++,
                    DefinitionId = def,
                    FragmentId = 1,
                    TileQ = q,
                    TileR = r
                });

            // ⭐ ПЕНЬКИ. Тот самый соблазн, на котором он завис в игре:
            // «посидеть» у одинокого человека выигрывает постоянно и длится
            // долго, а пока он сидит, аукцион не переигрывается вовсе.
            Put("sit.stump", -3, -1);
            Put("sit.stump", 2, 1);
            Put("sit.stump", 0, -2);

            // §102: ПРЕСНАЯ ВОДА. Без неё арена просто нежилая: морская полоса
            // по краю не пьётся, и все трое плюс чужак умирали от жажды к
            // четвёртому дню — а выглядело это как «абьюз почему-то перестал
            // случаться». Тест, в котором нельзя выжить, ничего не проверяет.
            Put("water.pond", 0, -1);

            // Еда и вода под ногами: голод и жажда должны быть решаемы, но
            // отнимать время.
            Put("tree.palm", -4, 0);
            Put("tree.palm", 3, -1);
            Put("food.coconut", -2, 1);
            Put("food.coconut", 1, -1);

            // Работа: камни и волокно. Ровно те цели, за которыми он уходил
            // через полкарты вместо дела.
            Put("rock.boulder", -1, 2);
            Put("rock.boulder", 3, 2);
            Put("plant.yucca", 0, 1);
            Put("plant.yucca", -3, 1);

            return new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings { Seed = seed },
                Environment = new EnvironmentBootstrap { GlobalTemperature = 18f },
                Fragments = { new FragmentBootstrap { Id = 1, Tiles = tiles } },
                Objects = objects,
                Npcs =
                {
                    // ТРОЕ девушек, как в настоящей колонии. Это не только
                    // «побольше народу»: они ходят друг к другу общаться, и
                    // подруга рядом с жертвой — вес против него. Проверяем в
                    // том числе и это.
                    Girl(GirlId, "Jana", -2, 0),
                    Girl(GirlId + 1, "Marta", -3, 0),
                    Girl(GirlId + 2, "Molly", -2, -1),
                    new NpcBootstrap
                    {
                        Id = OutsiderId,
                        DisplayName = "Kshishtof",
                        ActorMesh = "Kshishtof",
                        Faction = Faction.Outsiders,
                        FragmentId = 1,
                        TileQ = 2,
                        TileR = 0,
                        // Он тоже живой: голод и жажда текут и ДОЛЖНЫ
                        // конкурировать с желанием докопаться. Иначе тест снова
                        // ничего не проверяет.
                        Hunger = 0.35f,
                        Thirst = 0.35f,
                        Energy = 0.8f,
                        Comfort = 0.5f,
                        Social = 0f,
                        ThermalDiscomfort = 0.2f
                    }
                }
            };
        }

        /// <summary>
        /// ⭐ ВСЁ, что арена добавляет к построенному миру перед первым тиком.
        ///
        /// <para>
        /// Живёт здесь, а не у каждого запускающего, по той же причине, что и
        /// сам <see cref="Build"/>: соак, гейт и сцена Unity обязаны смотреть на
        /// ОДИН мир. Раньше это была копия в <c>Soak/Program.cs</c> и вторая в
        /// <c>AbuseTestBootstrap</c> — и они уже разошлись (соак не трогал
        /// одиночество чужака).
        /// </para>
        /// <para>
        /// Перевод часов — не правка поведения, а точка входа: отсрочка §81
        /// считается от <c>DayLengthTicks</c> (24000), и короткий прогон до неё
        /// просто не доживает.
        /// </para>
        /// </summary>
        public static void Prepare(WorldState world)
        {
            if (world == null)
            {
                return;
            }

            // Начинаем ПОСЛЕ льготных суток и в 15:00 — светло, и ждать нечего.
            world.Tick = Spec81.AbuseGraceDays * EnvironmentSystem.DayLengthTicks + 900;

            for (var i = 0; i < 3; i++)
            {
                if (world.Entities.Npcs.TryGetValue(new EntityId(GirlId + i), out var girl))
                {
                    // Нож — чтобы у неё был выбор огрызнуться, а не только сдаться.
                    girl.Inventory.Items.Add(ContentIds.Knife);
                }
            }

            if (world.Entities.Npcs.TryGetValue(new EntityId(OutsiderId), out var outsider))
            {
                outsider.Inventory.Items.Add(ContentIds.Spear);
                outsider.Inventory.Items.Add(ContentIds.Knife);
                // Он должен ХОТЕТЬ прямо сейчас: одиночество — единственная
                // незакрытая нужда во всём мире.
                outsider.Needs.Social = 0f;
            }
        }
    }
}
