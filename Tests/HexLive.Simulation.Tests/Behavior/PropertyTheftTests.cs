using System.IO;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Wire;
using NUnit.Framework;

namespace HexLive.Simulation.Tests.Behavior
{
public sealed class PropertyTheftTests
{
    [TestCase(2, false, true)]
    [TestCase(1, false, false)]
    [TestCase(2, true, false)]
    [TestCase(null, false, false)]
    public void PersonalOwnershipAndConsentPrecedeCampAndDistance(int? owner, bool consent, bool theft)
    {
        Assert.That(TheftMath.IsUnauthorizedUse(1, Faction.Colony, owner,
            Faction.Colony, consent, false), Is.EqualTo(theft));
        if (owner.HasValue)
            Assert.That(TheftMath.IsUnauthorizedUse(1, Faction.Colony, owner,
                null, consent, true), Is.EqualTo(theft));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SuccessfulDressNotifiesExactlyOnceAndConsumesAnyConsent(bool granted)
    {
        var world = TestWorld.CreateWorld();
        var pair = world.Entities.Npcs.Values.Where(n => n.Faction == Faction.Colony).Take(2).ToArray();
        var actor = pair[0]; var owner = pair[1];
        actor.Mind.OutfitLocked = false;
        actor.Mind.ManualControl = true; actor.Plan.Goal = GoalType.PlayerOrder;
        var obj = WorldObjectMutations.SpawnObject(world, "underwear.bra_riot", actor.Fragment,
            actor.Tile, world.Tiles.Items[actor.Tile].Junctions[0]);
        obj.Owner = owner.Id;
        owner.Perception.Agents.Add(new PerceivedAgent { Id = actor.Id });
        if (granted) actor.Mind.WearGrants.Add(new WearGrant { Item = obj.Id, Owner = owner.Id, ExpiresTick = world.Tick + 1 });
        var definition = world.Content.ObjectDefinitions[obj.DefinitionId];
        Assert.That(ExecutionSystem.CompleteDress(world, actor, obj, definition,
            definition.Interactions.First(i => i.Type == InteractionType.Dress), ""), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(world.Events.Items.Count(e => e.Type == "ItemStolen"), Is.EqualTo(granted ? 0 : 1));
            Assert.That(owner.Social.GetOrCreate(actor.Id).Affinity, Is.EqualTo(granted ? 0f : -TheftMath.WitnessAffinityHit).Within(.0001f));
            Assert.That(actor.Mind.WearGrants, Is.Empty);
        });
    }

    [Test]
    public void BlockedDressDoesNotReportTheft()
    {
        var world = TestWorld.CreateWorld(); var actor = world.Entities.Npcs.Values.First();
        actor.Mind.OutfitLocked = true;
        var obj = world.Entities.Objects.Values.First(o => world.Content.ObjectDefinitions[o.DefinitionId].Layer != null);
        obj.Owner = world.Entities.Npcs.Values.First(n => n.Id != actor.Id).Id;
        var definition = world.Content.ObjectDefinitions[obj.DefinitionId];
        Assert.That(ExecutionSystem.CompleteDress(world, actor, obj, definition,
            definition.Interactions.First(i => i.Type == InteractionType.Dress), ""), Is.False);
        Assert.That(world.Events.Items.Count(e => e.Type == "ItemStolen"), Is.Zero);
    }

    [Test]
    public void PropertyAndConsentTravelInKeyframeAndDeltaThenClearIndependentlyOfCorpseOwner()
    {
        var sent = new WorldSnapshot { Tick = 1 };
        var obj = new ObjectSnapshot { Id = new ObjectId(17), DefinitionId = "resource.stick",
            OwnerNpcId = 99, PropertyOwnerNpcId = 12, PropertyOwnerFaction = Faction.Outsiders };
        var actor = new NpcSnapshot { Id = new EntityId(1), Faction = Faction.Colony };
        actor.WearGrantObjectIds.Add(17); sent.Objects.Add(obj); sent.Npcs.Add(actor);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        WorldSnapshotCodec.Write(sent, writer, includeDebugDetails: false); stream.Position = 0;
        var mirror = new WorldSnapshot(); using var reader = new BinaryReader(stream);
        WorldSnapshotCodec.Read(reader, mirror);
        Assert.That(mirror.Objects[0].OwnerNpcId, Is.EqualTo(99));
        Assert.That(mirror.Objects[0].PropertyOwnerNpcId, Is.EqualTo(12));
        Assert.That(mirror.Objects[0].PropertyOwnerFaction, Is.EqualTo(Faction.Outsiders));
        Assert.That(TheftMath.IsTheft(mirror, mirror.Npcs[0], mirror.Objects[0], InteractionType.Dress), Is.False);
        var encoder = new SnapshotDeltaEncoder();
        Apply(encoder.Encode(sent, includeDebugDetails: false), mirror, -1);
        sent.Tick = 2; obj.PropertyOwnerNpcId = null; obj.PropertyOwnerFaction = null;
        actor.WearGrantObjectIds.Clear();
        Apply(encoder.Encode(sent, includeDebugDetails: false), mirror, 1);
        Assert.That(mirror.Objects[0].PropertyOwnerNpcId, Is.Null);
        Assert.That(mirror.Objects[0].PropertyOwnerFaction, Is.Null);
        Assert.That(mirror.Objects[0].OwnerNpcId, Is.EqualTo(99));
        Assert.That(mirror.Npcs[0].WearGrantObjectIds, Is.Empty);
    }

    [Test]
    public void ExportUsesRealPropertyOwnerAndOnlyLiveWearGrants()
    {
        var world = TestWorld.CreateWorld(); var actor = world.Entities.Npcs.Values.First();
        var owner = world.Entities.Npcs.Values.First(n => n.Id != actor.Id);
        var obj = world.Entities.Objects.Values.First();
        obj.Owner = owner.Id; obj.CurrentUser = actor.Id; obj.OwnerFaction = owner.Faction;
        actor.Mind.WearGrants.Add(new WearGrant { Item = obj.Id, ExpiresTick = world.Tick });
        actor.Mind.WearGrants.Add(new WearGrant { Item = new ObjectId(-1), ExpiresTick = world.Tick - 1 });
        var snapshot = WorldSnapshotExporter.Export(world);
        var exported = snapshot.Objects.Single(o => o.Id == obj.Id);
        Assert.That(exported.OwnerNpcId, Is.EqualTo(actor.Id.Value));
        Assert.That(exported.PropertyOwnerNpcId, Is.EqualTo(owner.Id.Value));
        Assert.That(snapshot.Npcs.Single(n => n.Id == actor.Id).WearGrantObjectIds, Is.EqualTo(new[] { obj.Id.Value }));
        obj.Owner = null; obj.OwnerFaction = null;
        Assert.That(WorldSnapshotExporter.Export(world).Objects.Single(o => o.Id == obj.Id).PropertyOwnerNpcId, Is.Null);
    }

    private static void Apply(byte[] bytes, WorldSnapshot target, int tick)
    {
        using var stream = new MemoryStream(bytes); using var reader = new BinaryReader(stream);
        SnapshotDeltaReader.Apply(reader, target, tick);
    }
}
}
