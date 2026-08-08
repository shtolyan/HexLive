using System.IO;
using System.Linq;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Runtime;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
    public sealed class TemperatureInertiaTests
    {
        [Test]
        public void ColdBody_WarmsQuicklyAtFire_ThenDriftsBackToColdEnvironment()
        {
            var body = -0.80f;
            body = TemperatureSystem.MoveBodyTowards(
                body, 0f, TemperatureSystem.ActiveReliefPerSlowTick);
            Assert.That(body, Is.EqualTo(-0.70f).Within(0.0001f));

            body = TemperatureSystem.MoveBodyTowards(
                body, -1f, TemperatureSystem.BodyDriftPerSlowTick);
            Assert.That(body, Is.EqualTo(-0.74f).Within(0.0001f));
        }

        [Test]
        public void HotBody_CoolsQuicklyInWater_ThenDriftsBackToHotEnvironment()
        {
            var body = 0.70f;
            body = TemperatureSystem.MoveBodyTowards(
                body, 0f, TemperatureSystem.ActiveReliefPerSlowTick);
            Assert.That(body, Is.EqualTo(0.60f).Within(0.0001f));

            body = TemperatureSystem.MoveBodyTowards(
                body, 1f, TemperatureSystem.BodyDriftPerSlowTick);
            Assert.That(body, Is.EqualTo(0.64f).Within(0.0001f));
        }

        [TestCase(4f, -1f)]
        [TestCase(16f, 0f)]
        [TestCase(19f, 0f)]
        [TestCase(22f, 0f)]
        [TestCase(34f, 1f)]
        public void EnvironmentMapping_UsesSignedBodyScale(float temperature, float expected)
        {
            Assert.That(TemperatureSystem.SignedFromTemperature(temperature),
                Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void SaveLoadMidTransition_PreservesBodyTemperature()
        {
            var world = TestWorld.CreateWorld(72031);
            var npc = world.Entities.Npcs.Values.First();
            npc.Needs.ThermalComfort = -0.43f;

            using var blob = new MemoryStream();
            using (var writer = new BinaryWriter(blob, System.Text.Encoding.UTF8, true))
            {
                WorldSaveSerializer.Write(world, writer);
            }

            blob.Position = 0;
            var loaded = TestWorld.CreateWorld(72031);
            using (var reader = new BinaryReader(blob, System.Text.Encoding.UTF8, true))
            {
                WorldSaveSerializer.Read(loaded, reader);
            }

            Assert.That(loaded.Entities.Npcs[npc.Id].Needs.ThermalComfort,
                Is.EqualTo(-0.43f).Within(0.0001f));
        }
    }
}
