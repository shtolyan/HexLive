using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Content;
using HexLive.Simulation.Debug;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

public sealed class FullHygieneBodySoilTests
{
    [Test]
    public void FullHygieneHidesOldBloodSoilButKeepsRealWounds()
    {
        var world = TestWorld.CreateWorld(185);
        var npc = world.Entities.Npcs.Values.First();
        var leg = npc.Body.Condition(BodyPart.LegL);
        leg.BloodSoil = 0.8f;
        npc.Wounds.Add(new WoundState
        {
            Id = npc.NextWoundId++,
            Zone = BodyPart.LegL,
            Severity = 0.3f,
            Heal01 = 0.25f,
            Clot01 = 1f,
            Stabilized = true,
            Seed = 185
        });

        npc.Needs.Hygiene = 1f;
        var clean = WorldSnapshotExporter.Export(world).Npcs
            .Single(candidate => candidate.Id == npc.Id);

        Assert.Multiple(() =>
        {
            Assert.That(clean.BodyPartConditions
                .Single(part => part.Part == BodyPart.LegL).BloodSoil, Is.Zero);
            Assert.That(clean.OpenWounds, Has.Count.EqualTo(1),
                "Full hygiene removes cosmetic soil, never the real wound layer.");
        });

        npc.Needs.Hygiene = 0.99f;
        var dirty = WorldSnapshotExporter.Export(world).Npcs
            .Single(candidate => candidate.Id == npc.Id);
        Assert.That(dirty.BodyPartConditions
            .Single(part => part.Part == BodyPart.LegL).BloodSoil,
            Is.EqualTo(0.8f).Within(0.0001f));
    }
}
