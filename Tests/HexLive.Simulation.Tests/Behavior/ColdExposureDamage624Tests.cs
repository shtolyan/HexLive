using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
    public sealed class ColdExposureDamage624Tests
    {
        [TestCase(-1f, false, 0.5f)]
        [TestCase(-1f, true, 0.5f)]
        [TestCase(1f, false, 1f)]
        [TestCase(1f, true, 1f)]
        public void ExposureUsesHalfColdDamageAndPreservesHeatAndSleep(float signed, bool sleeping, float factor)
        {
            var world = TestWorld.CreateWorld(624);
            var npc = world.Entities.Npcs.Values.First();
            var tile = world.Tiles.Items.First(t => t.Value.Flags == TileFlags.Walkable).Key;
            npc.Tile = tile;
            world.Environment.GlobalTemperature = signed < 0f ? -20f : 60f;
            world.Environment.UvIndex = 0f;
            foreach (var obj in world.Entities.Objects.Values) obj.ResourceAmount = 0f;
            npc.Needs.ThermalComfort = signed;
            npc.EquippedWarmth = 0f;
            foreach (var part in npc.Body.Parts.Keys.ToArray()) npc.Body.Parts[part] = 1f;
            npc.Health = 1f;
            npc.Execution.CurrentInteraction = sleeping ? InteractionType.Sleep : null;

            new TemperatureSystem().Run(world);

            var expected = SimBalance.ThermalHpHit * factor * (sleeping ? Spec49.ThermalSleepFactor : 1f);
            Assert.That(1f - npc.Body.Parts[BodyPart.Head], Is.EqualTo(expected).Within(0.000001f));
            Assert.That(1f - npc.Body.Parts[BodyPart.LegL], Is.EqualTo(expected).Within(0.000001f));
        }
    }
}
