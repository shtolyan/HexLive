using System.Collections.Generic;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;

namespace HexLive.UnityPresentation.MealPreparationTest
{
    public static class MealPreparationTestWorld
    {
        private const int Seed = 20261113;
        private const int Radius = 4;
        private const string Chair = "chair.basic";
        private static readonly TileCoord FireTile = new(1, 0);

        public static WorldBootstrapDefinition CreateDefinition(bool emptyHands)
        {
            var definition = new WorldBootstrapDefinition
            {
                Simulation = new SimulationBootstrapSettings { Seed = Seed },
                Environment = new EnvironmentBootstrap { GlobalTemperature = 23f },
                FactionHomes = { new FactionHomeBootstrap { StakeCampfireSite = false } }
            };

            var fragment = new FragmentBootstrap { Id = 1 };
            for (var q = -Radius; q <= Radius; q++)
            for (var r = -Radius; r <= Radius; r++)
            {
                if (System.Math.Abs(q + r) > Radius) continue;
                var water = r >= 3;
                fragment.Tiles.Add(new TileBootstrap
                {
                    Q = q, R = r, Elevation = water ? 0 : 1, Walkable = !water, Water = water
                });
            }
            definition.Fragments.Add(fragment);

            definition.Npcs.Add(Actor(1, "Marta", 0, -1));
            if (!emptyHands)
            {
                definition.Npcs.Add(Actor(2, "Jana", -1, 0));
                definition.Npcs.Add(Actor(3, "Kshishtof", 1, -1));
                definition.Npcs.Add(Actor(4, "Tonny", 0, 1));
            }
            return definition;
        }

        public static WorldState CreateWorld(WorldBootstrapDefinition definition, bool emptyHands)
        {
            var world = new WorldStateFactory().Create(definition);
            foreach (var npc in world.Entities.Npcs.Values)
            {
                npc.Inventory.Items.Clear();
            }

            Place(world, ContentIds.Palm, new(-1, -1));
            Place(world, ContentIds.Palm, new(2, -2));
            Place(world, ContentIds.Yucca, new(0, -2));
            Scatter(world, new(-1, -2), ContentIds.Stone, ContentIds.Stone, ContentIds.Stick);
            Scatter(world, new(1, -2), ContentIds.Coconut, ContentIds.Coconut, ContentIds.Stick, ContentIds.Stone);
            if (emptyHands) return world;

            Place(world, ContentIds.PalmStump, new(-2, 0));
            Place(world, ContentIds.PalmStump, new(-2, 1));
            Place(world, ContentIds.Workbench, new(-2, -1));
            Place(world, ContentIds.ModernTable, new(0, 0));
            Place(world, Chair, new(0, -1), 7);
            Place(world, Chair, new(0, 1), 19);
            Place(world, ContentIds.BedBasic, new(-1, 2));

            var fire = Place(world, ContentIds.Campfire, FireTile);
            AddInstalled(fire, ContentIds.Stick, SimBalance.CampfireBillSticks);
            AddInstalled(fire, ContentIds.Rope, SimBalance.CampfireBillRope);
            AddInstalled(fire, ContentIds.Stone, SimBalance.CampfireBillStones);
            fire.ResourceAmount = 2 * SimBalance.MeatRoastDurationTicks;
            fire.Contents.Add(new ItemInstance(ContentIds.MeatRaw));

            var wolf = Place(world, ContentIds.CarcassAnimal, new(2, 0));
            wolf.Variant = MobIds.Dog;
            wolf.ResourceAmount = SimBalance.CarcassDecayTicks;
            Scatter(world, new(2, 1), ContentIds.WolfCarcassRaw, ContentIds.WolfCarcassCooked,
                ContentIds.MeatRoasted, ContentIds.CrabRaw, ContentIds.CrabRoasted);

            Scatter(world, new(-3, 1), ContentIds.Knife, ContentIds.Knife, ContentIds.KitchenKnife,
                ContentIds.SharpStone, ContentIds.AxeStone, ContentIds.Firestarter, ContentIds.Rope, ContentIds.Log);
            Scatter(world, new(-2, 2), ContentIds.CoconutOpen, ContentIds.MeatRaw,
                HumanMeatCatalog.ArmCooked, HumanMeatCatalog.LegCooked, HumanMeatCatalog.TorsoCooked);
            Place(world, ContentIds.MeatCooked, new(-2, 2), 18).ResourceAmount = FoodMath.MeatPortionGrams;
            Scatter(world, new(2, -1), ContentIds.Apple, ContentIds.Banana, ContentIds.Orange,
                ContentIds.CheeseWedge, ContentIds.HamSandwich, ContentIds.ChocolateBar,
                ContentIds.StrawberryYogurt, ContentIds.CannedPeaches);
            Scatter(world, new(1, 1), ContentIds.Bandage, ContentIds.Bandage, ContentIds.WolfHide);
            Place(world, ContentIds.CoconutPierced, new(1, 1), 13).WaterKind = WaterKind.Coconut;
            FillWater(Place(world, ContentIds.Bottle, new(1, 1), 15));
            FillWater(Place(world, ContentIds.LeatherFlask, new(1, 1), 17));
            return world;
        }

        private static NpcBootstrap Actor(int id, string actor, int q, int r) => new()
        {
            Id = id, DisplayName = actor, ActorMesh = actor, UseAuthoredAppearance = true, Faction = Faction.Colony, FragmentId = 1,
            TileQ = q, TileR = r, Hunger = 0.45f, Thirst = 0.45f, Energy = 0.9f, Social = 0.6f,
            Comfort = 0.55f, Traits = new List<string>()
        };

        private static void Scatter(WorldState world, TileCoord tile, params string[] ids)
        {
            for (var i = 0; i < ids.Length; i++)
                Place(world, ids[i], tile, i * 3);
        }

        private static WorldObjectState Place(WorldState world, string id, TileCoord tile, int slot = 0) =>
            WorldObjectMutations.SpawnObject(world, id, new FragmentId(1), tile,
                world.Tiles.Items[tile].Junctions[slot]);

        private static void AddInstalled(WorldObjectState fire, string material, int count)
        {
            for (var i = 0; i < count; i++)
                fire.Contents.Add(new ItemInstance(material));
        }

        private static void FillWater(WorldObjectState vessel)
        {
            vessel.ResourceAmount = 4;
            vessel.WaterKind = WaterKind.Rain;
            vessel.LastAddedWaterKind = WaterKind.Rain;
        }
    }
}
