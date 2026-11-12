using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior;

/// <summary>
/// #762: the apartment bed is a bed. A colonist sleeps on it exactly like on
/// bed.basic (same order, same anchor and heading, same rescue), and it
/// restores energy and health 1.5x as fast as the primitive bed. The factor
/// is data on the definition (<see cref="ObjectDefinition.BedRestMultiplier"/>).
/// </summary>
public sealed class ModernBedSleepTests
{
    [Test]
    public void TheModernBedIsABedWithASleepInteractionAndARestFactor()
    {
        var world = TestWorld.CreateWorld(76201);
        var modern = world.Content.ObjectDefinitions[ContentIds.ModernBed];
        var basic = world.Content.ObjectDefinitions[ContentIds.BedBasic];
        Assert.Multiple(() =>
        {
            Assert.That(ContentIds.IsBed(ContentIds.ModernBed), Is.True);
            Assert.That(modern.HasTag(ObjectTags.Bed), Is.True);
            Assert.That(modern.HasTag(ObjectTags.Obstacle), Is.True);
            Assert.That(modern.ObstacleRadius, Is.EqualTo(basic.ObstacleRadius));
            Assert.That(modern.Interactions.Single(i => i.Type == InteractionType.Sleep).DurationTicks,
                Is.EqualTo(basic.Interactions.Single(i => i.Type == InteractionType.Sleep).DurationTicks));
            Assert.That(modern.Interactions.Any(i => i.Type == InteractionType.PickUp), Is.True);
            Assert.That(basic.BedRestMultiplier, Is.EqualTo(1f));
            Assert.That(modern.BedRestMultiplier, Is.EqualTo(ModernLootCatalog.BedRestMultiplier));
            Assert.That(ModernLootCatalog.BedRestMultiplier, Is.EqualTo(1.5f));
        });
    }

    [Test]
    public void AnOrderedColonistLiesDownOnTheModernBedExactlyLikeOnBedBasic()
    {
        var (basicNpc, basicBed) = OrderSleep(ContentIds.BedBasic);
        var (modernNpc, modernBed) = OrderSleep(ContentIds.ModernBed);
        Assert.Multiple(() =>
        {
            Assert.That(modernNpc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep));
            Assert.That(modernNpc.Execution.TargetObject, Is.EqualTo(modernBed.Id));
            Assert.That(modernBed.IsOccupied && modernBed.CurrentUser == modernNpc.Id, Is.True);
            // Same anchor, same yaw, same footprint: the same pose.
            Assert.That(modernBed.Junctions[0], Is.EqualTo(basicBed.Junctions[0]));
            Assert.That(modernNpc.Position.X, Is.EqualTo(basicNpc.Position.X).Within(.0001f));
            Assert.That(modernNpc.Position.Y, Is.EqualTo(basicNpc.Position.Y).Within(.0001f));
            Assert.That(modernNpc.RotationDegrees, Is.EqualTo(basicNpc.RotationDegrees).Within(.01f));
            Assert.That(modernNpc.Tile, Is.EqualTo(basicNpc.Tile));
        });
    }

    private static (NPCState Npc, WorldObjectState Bed) OrderSleep(string bedId)
    {
        var engine = TestWorld.CreateEngine(76202);
        var world = engine.World;
        foreach (var fire in world.Entities.Objects.Values.Where(o => o.DefinitionId == "campfire.spot"))
            fire.ResourceAmount = 0;
        var npc = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).First();
        npc.Mind.ManualControl = true;
        npc.Needs.Energy = .4f;
        npc.Needs.Hunger = 0f;
        npc.Needs.Thirst = 0f;
        npc.Mind.AdrenalineUntilTick = 0;
        npc.Perception.Hostiles.Clear();
        npc.Perception.Mobs.Clear();
        npc.Memory.Dangers.Clear();
        var bed = PlaceNear(world, npc, bedId, yawStep: 2);
        engine.Step(); // topology/connectivity catch up with the new footprint
        TestContext.Out.WriteLine($"{bedId}: npc junction={npc.CurrentJunction?.Value} tile={npc.Tile} bed tile={bed.Tile}");

        var admission = engine.ApplyManualCommand(new InteractCommand(npc.Id, bed.Id, InteractionType.Sleep));
        Assert.That(admission.Accepted, Is.True, $"{bedId}: {admission.Reason}");
        for (var tick = 0; tick < 400 && npc.Execution.CurrentInteraction != InteractionType.Sleep; tick++)
        {
            npc.Perception.Hostiles.Clear();
            npc.Memory.Dangers.Clear();
            engine.Step();
        }

        Assert.That(npc.Execution.CurrentInteraction, Is.EqualTo(InteractionType.Sleep), $"{bedId}: never lay down");
        TestContext.Out.WriteLine($"{bedId}: pos=({npc.Position.X:0.000},{npc.Position.Y:0.000}) " +
            $"heading={npc.RotationDegrees:0} bed={bed.Id.Value} anchor={bed.Junctions[0].Value}");
        return (npc, bed);
    }

    /// <summary>The same deterministic spot for either bed: the first free
    /// tile centre near the colonist whose footprint validates for bed.basic.</summary>
    private static WorldObjectState PlaceNear(WorldState world, NPCState npc, string bedId, int yawStep)
    {
        foreach (var tile in world.Tiles.Items.Keys
                     .Where(t => HexSpatialMath.HexDistance(t, npc.Tile) is >= 1 and <= 3)
                     .OrderBy(t => HexSpatialMath.HexDistance(t, npc.Tile)).ThenBy(t => t.Q).ThenBy(t => t.R))
        {
            if (world.Tiles.Items[tile].Flags.HasFlag(TileFlags.Indoor)) continue;
            var centre = HexSpatialMath.TileToWorld(tile);
            var anchor = world.Tiles.Items[tile].Junctions.Select(id => world.Junctions.Items[id])
                .FirstOrDefault(j => HexSpatialMath.Distance(j.WorldPosition, centre) < .01f &&
                    FurniturePlacement.Validate(world, ContentIds.BedBasic, j, yawStep) == null);
            if (anchor == null) continue;
            var bed = WorldObjectMutations.SpawnObject(world, bedId, anchor.Fragment, tile, anchor.Id);
            bed.RotationDegrees = StructurePlacement.QuantizeHexYaw(yawStep * 60f);
            bed.Variant = bedId == ContentIds.ModernBed
                ? PortalWorld.PlacedFurnitureVariant : PortableFurniture.BasicPlacedVariant;
            bed.OwnerFaction = npc.Faction;
            PortableFurniture.ApplyPlacedFootprint(world, bed);
            return bed;
        }

        Assert.Fail("no spot for a bed");
        return null;
    }

    [Test]
    public void TheModernBedRestoresEnergyAndHealthOneAndAHalfTimesAsFast()
    {
        var basic = SleepFor(ContentIds.BedBasic);
        var modern = SleepFor(ContentIds.ModernBed);
        TestContext.Out.WriteLine($"energy per slow tick: basic {basic.Energy:0.000000} modern {modern.Energy:0.000000}");
        TestContext.Out.WriteLine($"torso regen over the run: basic {basic.Health:0.000000} modern {modern.Health:0.000000}");
        TestContext.Out.WriteLine($"rest heal multiplier: basic {basic.Heal} modern {modern.Heal}");
        Assert.Multiple(() =>
        {
            Assert.That(basic.Energy, Is.EqualTo(SimBalance.SleepEnergyBaseBonus + SimBalance.SleepEnergyBasicBedBonus)
                .Within(1e-6f), "bed.basic keeps its 4-hour clock");
            Assert.That(modern.Energy / basic.Energy, Is.EqualTo(1.5f).Within(1e-4f));
            Assert.That(basic.Health, Is.GreaterThan(0f));
            Assert.That(modern.Health / basic.Health, Is.EqualTo(1.5f).Within(1e-3f));
            Assert.That(modern.Heal / basic.Heal, Is.EqualTo(1.5f).Within(1e-6f));
            Assert.That(modern.Comfort, Is.EqualTo(basic.Comfort).Within(1e-6f), "comfort is a bed's, not 1.5x");
        });
    }

    private static (float Energy, float Health, float Heal, float Comfort) SleepFor(string bedId)
    {
        var world = TestWorld.CreateWorld(76203);
        world.Environment.Phase = DayPhase.Night;
        world.Environment.IsRaining = false;
        foreach (var fire in world.Entities.Objects.Values.Where(o => o.DefinitionId == "campfire.spot"))
            fire.ResourceAmount = 0;
        var npc = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony && n.Health > 0f)
            .OrderBy(n => n.Id.Value).First();
        var bed = PlaceNear(world, npc, bedId, yawStep: 0);
        Assert.That(BedSleep.TryEnter(world, npc, bed, world.Tick + 100000, npc.CurrentJunction), Is.True, bedId);
        npc.Needs.Hunger = .1f;
        npc.Needs.Thirst = .1f;
        npc.Needs.ThermalComfort = 0f;
        npc.Needs.ThermalDiscomfort = 0f;
        npc.Needs.Comfort = .2f;
        npc.WornItems.Clear();
        npc.Body.Parts[BodyPart.Torso] = .5f;
        npc.Health = npc.Body.Mean();

        var decay = new NeedsDecaySystem();
        npc.Needs.Energy = .2f;
        decay.Run(world);
        var energy = npc.Needs.Energy - .2f;
        var comfort = npc.Needs.Comfort - .2f;
        npc.Needs.Energy = .2f;
        var torso = npc.Body.Parts[BodyPart.Torso];
        for (var i = 0; i < 20; i++)
        {
            npc.Needs.Energy = .2f;
            npc.Needs.Hunger = .1f;
            npc.Needs.Thirst = .1f;
            decay.Run(world);
        }

        return (energy, npc.Body.Parts[BodyPart.Torso] - torso,
            KenshiMedicalMath.RestHealMultiplier(world, npc), comfort);
    }
}
