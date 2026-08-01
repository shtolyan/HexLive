using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;

namespace HexLive.UnityPresentation.AbuseTest
{
    /// <summary>
    /// §87: арена «он и она». Крошечный плоский островок, один чужак и одна
    /// девушка в двух шагах друг от друга — и больше НИЧЕГО: ни зверей, ни
    /// стройки, ни голода.
    ///
    /// ⭐ Мир описан ЗДЕСЬ, в общем классе, а не в бутстрапе сцены, потому что
    /// его строит и Unity-сцена, и headless-проба. Иначе мы с пользователем
    /// смотрели бы на два разных мира и спорили, у кого что происходит, — а
    /// именно из-за этого арена и понадобилась.
    /// </summary>
    public static class AbuseTestWorld
    {
        public const int GirlId = 1;
        public const int OutsiderId = 101;

        public static WorldBootstrapDefinition Build(int seed = 313)
        {
            var tiles = new List<TileBootstrap>();
            for (var r = -4; r <= 4; r++)
            {
                for (var q = -5; q <= 5; q++)
                {
                    tiles.Add(new TileBootstrap
                    {
                        Q = q,
                        R = r,
                        Walkable = true,
                        Water = false,
                        Elevation = 1
                    });
                }
            }

            return new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings { Seed = seed },
                Environment = new EnvironmentBootstrap { GlobalTemperature = 18f },
                Fragments = { new FragmentBootstrap { Id = 1, Tiles = tiles } },
                Npcs =
                {
                    new NpcBootstrap
                    {
                        Id = GirlId,
                        DisplayName = "Jana",
                        ActorMesh = "Jana",
                        FragmentId = 1,
                        TileQ = -1,
                        TileR = 0,
                        // Все нужды закрыты: ей не за чем уходить, и ничто не
                        // конкурирует со сценой.
                        Hunger = 0.2f,
                        Thirst = 0.2f,
                        Energy = 0.95f,
                        Comfort = 0.9f,
                        Social = 0.9f,
                        ThermalDiscomfort = 0.1f
                    },
                    new NpcBootstrap
                    {
                        Id = OutsiderId,
                        DisplayName = "Kshishtof",
                        ActorMesh = "Kshishtof",
                        Faction = Faction.Outsiders,
                        FragmentId = 1,
                        TileQ = 1,
                        TileR = 0,
                        // Сыт, напоен, выспан — и ПОЛНОСТЬЮ одинок. Единственная
                        // незакрытая нужда во всём мире, чтобы в аукционе не
                        // осталось ни одного конкурента.
                        Hunger = 0.2f,
                        Thirst = 0.2f,
                        Energy = 0.95f,
                        Comfort = 0.9f,
                        Social = 0f,
                        ThermalDiscomfort = 0.1f
                    }
                }
            };
        }
    }
}
