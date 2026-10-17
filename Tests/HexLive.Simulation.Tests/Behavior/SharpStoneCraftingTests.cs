using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
    public sealed class SharpStoneCraftingTests
    {
        [SetUp]
        public void SetUp()
        {
            GearCatalog.ResetToDefaults();
            RecipeCatalog.ResetToDefaults();
        }

        [TearDown]
        public void TearDown()
        {
            GearCatalog.ResetToDefaults();
            RecipeCatalog.ResetToDefaults();
        }

        [Test]
        public void SharpStone_CutsYuccaAndDressesAnimalsButDoesNotOpenCoconutsSplitLogsOrButcherHumans()
        {
            var sharp = GearCatalog.For(ContentIds.SharpStone);
            Assert.Multiple(() =>
            {
                Assert.That(sharp.Has(GearCapability.CutPlant), Is.True);
                Assert.That(sharp.Has(GearCapability.ButcherAnimal), Is.True);
                Assert.That(sharp.Has(GearCapability.Butcher), Is.False);
                Assert.That(sharp.Has(GearCapability.Cut), Is.False);
                Assert.That(sharp.Has(GearCapability.ChopWood), Is.False);
                Assert.That(sharp.Has(GearCapability.OpenCoconut), Is.False);
            });

            var definitions = PrototypeContentCatalog.CreateDefaults();
            var yucca = definitions["plant.yucca"].Interactions.Single(i => i.Id == "cut.yucca");
            var animal = definitions["carcass.animal"].Interactions.Single(i => i.Id == "butcher.carcass");
            var human = definitions["corpse.npc"].Interactions.Single(i => i.Id == "butcher.body");
            var coconut = definitions[ContentIds.Coconut].Interactions.Single(i => i.Id == "pierce.coconut");
            var log = definitions[ContentIds.Log].Interactions.Single(i => i.Id == "split.log");
            var palm = definitions["tree.palm"].Interactions.Single(i => i.Id == "chop.palm");
            var boulder = definitions["rock.boulder"].Interactions.Single(i => i.Id == "mine.boulder");
            Assert.That(yucca.RequiredCapabilities,
                Is.EquivalentTo(new[] { GearCapability.Cut, GearCapability.CutPlant }));
            Assert.That(coconut.RequiredCapabilities,
                Is.EqualTo(new[] { GearCapability.OpenCoconut }));
            Assert.That(animal.RequiredCapabilities,
                Is.EqualTo(new[] { GearCapability.ButcherAnimal }));
            Assert.That(human.RequiredCapabilities,
                Is.EqualTo(new[] { GearCapability.Butcher }));

            var carried = new[] { ContentIds.SharpStone };
            Assert.Multiple(() =>
            {
                Assert.That(ActionRequirements.HasTool(carried, yucca.RequiredCapabilities, 2), Is.True);
                Assert.That(ActionRequirements.HasTool(carried, animal.RequiredCapabilities, 2), Is.True);
                Assert.That(ActionRequirements.HasTool(carried, human.RequiredCapabilities, 2), Is.False);
                Assert.That(ActionRequirements.HasTool(carried, coconut.RequiredCapabilities, 2), Is.False);
                Assert.That(ActionRequirements.HasTool(carried, log.RequiredCapabilities, 2), Is.False);
                Assert.That(ActionRequirements.HasTool(carried,
                    new[] { GearCatalog.RequiredCapabilities(palm, definitions["tree.palm"]) }, 2), Is.False);
                Assert.That(ActionRequirements.HasTool(carried,
                    new[] { GearCatalog.RequiredCapabilities(boulder, definitions["rock.boulder"]) }, 2), Is.False);
            });

            var npc = new NPCState { Id = new EntityId(172) };
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.SharpStone));
            Assert.That(DecisionSystem.HasCoconutBlade(npc), Is.False);
            npc.Inventory.Items.Add(new ItemInstance(ContentIds.Knife));
            Assert.That(DecisionSystem.HasCoconutBlade(npc), Is.True);
        }

        [Test]
        public void AutonomousRecipes_UseTheAuthoredSurvivalChain()
        {
            AssertBill(GoalType.CraftSharpStone, (ContentIds.Stone, 2));
            AssertBill(GoalType.CraftKnife,
                (ContentIds.Stick, 1), (ContentIds.SharpStone, 1), (ContentIds.Rope, 1));
            AssertBill(GoalType.CraftSpear,
                (ContentIds.SharpStone, 1), (ContentIds.Rope, 1));
            AssertBill(GoalType.CraftAxe,
                (ContentIds.Stick, 1), (ContentIds.Stone, 1), (ContentIds.Rope, 1));
            AssertBill(GoalType.CraftBandage,
                (ContentIds.HerbLeaf, 2), (ContentIds.Cloth, 1));
        }

        [Test]
        public void ExportedData_ContainsCraftableHammerAndSharpStone()
        {
            SimDataFile.Require(RepoPaths.SimData);

            Assert.That(RecipeCatalog.ByOutput.ContainsKey(ContentIds.Hammer), Is.True);
            Assert.That(RecipeCatalog.ByOutput[ContentIds.Hammer].Inputs
                .Select(input => (input.Id, input.Count)), Is.EquivalentTo(new[]
                {
                    (ContentIds.Stick, 1), (ContentIds.Stone, 1), (ContentIds.Rope, 1)
                }));
            Assert.That(RecipeCatalog.ByOutput.ContainsKey(ContentIds.SharpStone), Is.True);
            Assert.That(GearCatalog.For(ContentIds.SharpStone).Has(GearCapability.CutPlant), Is.True);
            Assert.That(GearCatalog.For(ContentIds.SharpStone).Has(GearCapability.ButcherAnimal), Is.True);
        }

        private static void AssertBill(GoalType goal, params (string Id, int Count)[] expected)
        {
            Assert.That(RecipeCatalog.ByGoal.ContainsKey(goal), Is.True);
            Assert.That(RecipeCatalog.ByGoal[goal].Inputs.Select(input => (input.Id, input.Count)),
                Is.EquivalentTo(expected));
        }
    }
}
