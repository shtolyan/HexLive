using System.Linq;
using System.IO;
using System.Text.Json;
using HexLive.Simulation.Persistence;
using HexLive.Simulation.Bootstrap;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Debug;
using HexLive.Simulation.Runtime;
using HexLive.Simulation.Spatial;
using NUnit.Framework;
namespace HexLive.Simulation.Tests.Behavior
{
public sealed class DoorInteractionTests
{
    private static (SimulationEngine engine,NPCState visitor,NPCState owner,WorldObjectState door) Setup()
    {
        var engine=TestWorld.CreateEngine(); var world=engine.World;
        world.Mode=GameMode.HugeIsland;
        var pair=world.Entities.Npcs.Values.Where(n=>n.Faction==Faction.Colony).OrderBy(n=>n.Id.Value).Take(2).ToArray();
        var visitor=pair[0]; var owner=pair[1];
        Assert.That(engine.ApplyManualCommand(new SetManualControlCommand(visitor.Id,true)).Accepted,Is.True);
        engine.Step();
        var hut=TestWorld.StartHut(world);
        var door=world.Entities.Objects.Values.Single(o=>o.DefinitionId==DoorTopology.DoorDefinitionId);
        hut.OwnerFaction=Faction.Colony2;
        foreach(var n in world.Entities.Npcs.Values)
        {
            PlanInterruption.TryAbort(world,n,InterruptionCause.PlayerCommand,"Test setup");
            n.Mind.CurrentGoal=GoalType.None;
            if(n.CurrentJunction is {} old) SpatialMutations.FreeJunction(world,old,n.Id);
            n.CurrentJunction=null;
            n.Tile=new TileCoord(99,99); // No unrelated resident can answer this door.
        }
        owner.Faction=Faction.Colony2;
        owner.Mind.ManualControl=false;
        owner.Traits.Add(TraitKind.Coward);
        owner.Needs.Stress=.1f;
        owner.Tile=hut.Tile;
        owner.Position=world.Junctions.Items[door.Junctions[0]].WorldPosition;
        var portal=world.Junctions.Items[door.Junctions[0]];
        var outside=portal.Neighbors.First(j=>!world.Junctions.Items[j].Blocked && !world.Junctions.Items[j].Tiles.Contains(hut.Tile));
        var junction=world.Junctions.Items[outside];
        visitor.CurrentJunction=outside; visitor.Position=junction.WorldPosition; visitor.Tile=junction.Tiles[0];
        visitor.Needs.Hunger=visitor.Needs.Thirst=0f;
        visitor.Inventory.Items.Clear(); visitor.Inventory.Items.Add(new ItemInstance("tool.axe_stone"));
        world.DoorStateVersion++;
        Assert.That(BuildingDoorRules.TryClose(world,door.Id),Is.True);
        return(engine,visitor,owner,door);
    }
    private static void Execute(WorldState world,NPCState visitor,int ticks)
    {
        var execution=new ExecutionSystem();
        for(var i=0;i<ticks;i++) { world.Tick++; execution.Run(world); }
    }

    [Test]
    public void GameplayPointerTargetsAFinishedClosedDoorWithoutCatalogInteractions()
    {
        var (engine, _, _, door) = Setup();
        Assert.That(engine.World.Content.ObjectDefinitions[door.DefinitionId].Interactions, Is.Empty);
        var leaf = WorldSnapshotExporter.Export(engine.World).Objects.Single(o => o.Id == door.Id);
        Assert.That(DoorInteractionMath.CanTargetClosedDoor(leaf), Is.True);
        leaf.ArchitectureOwnerObjectId = leaf.Id.Value;
        Assert.That(DoorInteractionMath.CanTargetClosedDoor(leaf), Is.True,
            "Standalone doors use their own id as the architecture owner.");

        leaf.IsDoorOpen = true;
        Assert.That(DoorInteractionMath.CanTargetClosedDoor(leaf), Is.False);
        leaf.IsDoorOpen = false;
        leaf.Durability = 0;
        Assert.That(DoorInteractionMath.CanTargetClosedDoor(leaf), Is.False);
        leaf.Durability = 1;
        leaf.BuildProduct = leaf.DefinitionId;
        Assert.That(DoorInteractionMath.CanTargetClosedDoor(leaf), Is.False);
        leaf.BuildProduct = string.Empty;
        leaf.ArchitectureElements[0].DemolitionPlanned = true;
        Assert.That(DoorInteractionMath.CanTargetClosedDoor(leaf), Is.False);
    }

    [Test]
    public void StrangerIsRefusedAndTrustedSleeperAnswersWithoutWaking()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        Assert.That(engine.ApplyManualCommand(new KnockDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,DoorInteractionMath.KnockTicks+1);
        Assert.That(door.IsDoorOpen,Is.False);
        Assert.That(owner.Execution.LastSocialCueKind,Is.EqualTo("DoorRefuse"));
        owner.Social.GetOrCreate(visitor.Id).Trust=.8f;
        var bed=world.Entities.Objects.Values.First(o=>ContentIds.IsBed(o.DefinitionId));
        Assert.That(BedSleep.TryEnter(world,owner,bed,world.Tick+999,null),Is.True);
        var sleepUntil=owner.Execution.EndTick;
        var oldPosition=owner.Position;
        Assert.That(engine.ApplyManualCommand(new KnockDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,DoorInteractionMath.KnockTicks+1);
        Assert.That(door.IsDoorOpen,Is.True);
        Assert.That(owner.Execution.LastSocialCueKind,Is.EqualTo("DoorWelcome"));
        Assert.That(owner.Execution.CurrentInteraction,Is.EqualTo(InteractionType.Sleep));
        Assert.That(owner.Position,Is.EqualTo(oldPosition));
        Assert.That(owner.Execution.TargetObject,Is.EqualTo(bed.Id));
        Assert.That(owner.Execution.EndTick,Is.EqualTo(sleepUntil));
        Assert.That(bed.CurrentUser,Is.EqualTo(owner.Id));
    }
    [Test]
    public void FamiliarFriendlyNeighbourOpensButComatoseResidentCannotAnswer()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        var relation=owner.Social.GetOrCreate(visitor.Id);
        relation.Familiarity=.5f; relation.Affinity=.2f;
        owner.Mind.ComaCause=ComaCause.BloodLoss;
        Assert.That(engine.ApplyManualCommand(new KnockDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,DoorInteractionMath.KnockTicks+1);
        Assert.That(door.IsDoorOpen,Is.False);
        owner.Mind.ComaCause=ComaCause.None;
        Assert.That(engine.ApplyManualCommand(new KnockDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,DoorInteractionMath.KnockTicks+1);
        Assert.That(door.IsDoorOpen,Is.True);
    }
    [Test]
    public void AxeBreakTakesFourRealTreesAndCannotBeClosed()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        owner.Tile=new TileCoord(99,99);
        var duration=DoorInteractionMath.BreakWorkTicks(world,visitor);
        var palm=world.Content.ObjectDefinitions[ContentIds.Palm].Interactions.First(i=>i.Type==InteractionType.Harvest);
        Assert.That(duration,Is.EqualTo(4*AttributeMath.WorkTicks(visitor,palm.DurationTicks,InteractionType.Harvest,GoalType.HarvestTree)));
        Assert.That(engine.ApplyManualCommand(new BreakDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,1);
        var start=world.Tick;
        Execute(world,visitor,duration-1);
        Assert.That(door.IsDoorOpen,Is.False);
        Assert.That(door.Durability,Is.GreaterThan(0f));
        Execute(world,visitor,1);
        Assert.That(world.Tick-start,Is.EqualTo(duration));
        Assert.That(door.Durability,Is.Zero);
        Assert.That(door.IsDoorOpen,Is.True);
        Assert.That(BuildingDoorRules.TryClose(world,door.Id),Is.False);
        Assert.That(world.Entities.Objects.ContainsKey(door.Id),Is.True);
        Assert.That(door.IsOccupied,Is.False);
    }
    [Test]
    public void EvenAbortedAttemptHurtsTrustAndFrightensCoward()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        Assert.That(engine.ApplyManualCommand(new BreakDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,2);
        Assert.That(owner.Social.GetOrCreate(visitor.Id).Affinity,Is.LessThan(0f));
        Assert.That(owner.Social.GetOrCreate(visitor.Id).Trust,Is.LessThan(0f));
        Assert.That(owner.Needs.Stress,Is.GreaterThanOrEqualTo(.95f));
        Assert.That(owner.Mind.CryingUntilTick,Is.GreaterThan(world.Tick));
        var damage=door.Durability;
        Assert.That(engine.ApplyManualCommand(new StopCommand(visitor.Id)).Accepted,Is.True);
        Assert.That(door.IsOccupied,Is.False);
        Execute(world,visitor,2);
        Assert.That(door.Durability,Is.EqualTo(damage));
        Assert.That(owner.Social.GetOrCreate(visitor.Id).Trust,Is.LessThan(0f));
    }
    [Test]
    public void BrokenDoorAndPartialDamageSurviveSaveWithoutReconstructingLeaf()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        owner.Tile=new TileCoord(99,99);
        door.Durability=.5f;
        Assert.That(engine.ApplyManualCommand(new BreakDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,DoorInteractionMath.BreakWorkTicks(world,visitor)/2+1);
        using var bytes=new MemoryStream();
        using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Write(world,writer);
        bytes.Position=0; var loaded=TestWorld.CreateWorld(world.Seed); loaded.Mode=world.Mode;
        using(var reader=new BinaryReader(bytes)) WorldSaveSerializer.Read(loaded,reader);
        var restored=loaded.Entities.Objects[door.Id];
        Assert.That(restored.Durability,Is.Zero);
        Assert.That(restored.IsDoorOpen,Is.True);
        Assert.That(BuildingDoorRules.TryClose(loaded,restored.Id),Is.False);
        BuildingRules.EnsureHutElements(loaded,TestWorld.StartHut(loaded),completed:true);
        Assert.That(loaded.Entities.Objects[door.Id].Durability,Is.Zero);
    }
    [Test]
    public void InProgressBreakSaveResumesItsRemainingWorkWithoutRepeatingAttempt()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        owner.Tile=new TileCoord(99,99);
        Assert.That(engine.ApplyManualCommand(new BreakDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,20);
        var damage=door.Durability;
        Assert.That(damage,Is.InRange(.01f,.99f));
        var end=visitor.Execution.EndTick;
        using var bytes=new MemoryStream();
        using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true)) WorldSaveSerializer.Write(world,writer);
        bytes.Position=0; var loaded=TestWorld.CreateWorld(world.Seed); loaded.Mode=world.Mode;
        using(var reader=new BinaryReader(bytes)) WorldSaveSerializer.Read(loaded,reader);
        var restored=loaded.Entities.Objects[door.Id]; var worker=loaded.Entities.Npcs[visitor.Id];
        Assert.That(restored.Durability,Is.EqualTo(damage));
        Assert.That(worker.Execution.EndTick,Is.EqualTo(end));
        var attempts=loaded.Events.Items.Count(e=>e.Type=="DoorBreakInStarted");
        Execute(loaded,worker,end-loaded.Tick);
        Assert.That(restored.IsDoorOpen,Is.True);
        Assert.That(restored.Durability,Is.Zero);
        Assert.That(worker.Plan.Status,Is.EqualTo(PlanStatus.Completed));
        Assert.That(loaded.Events.Items.Count(e=>e.Type=="DoorBreakInStarted"),Is.EqualTo(attempts));
    }
    [Test]
    public void ApproachCannotChooseInsideOfClosedForeignDoor()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        var hut=TestWorld.StartHut(world);
        var from=world.Junctions.Items[visitor.CurrentJunction!.Value];
        var farther=world.Junctions.Items.Values.Where(j=>!j.Blocked && !j.Tiles.Contains(hut.Tile) &&
            HexSpatialMath.Distance(j.WorldPosition,world.Junctions.Items[door.Junctions[0]].WorldPosition)>SpatialQueries.BesideReach(0f))
            .OrderBy(j=>HexSpatialMath.Distance(j.WorldPosition,from.WorldPosition)).First().Id;
        visitor.CurrentJunction=farther; visitor.Position=world.Junctions.Items[farther].WorldPosition;
        visitor.Tile=world.Junctions.Items[farther].Tiles[0];
        Assert.That(engine.ApplyManualCommand(new KnockDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        var stand=world.Junctions.Items[visitor.Plan.TargetJunctionId!.Value];
        Assert.That(stand.Tiles.Contains(hut.Tile),Is.False);
        var path=new PathfindingSystem(); path.Run(world);
        Assert.That(visitor.Movement.JunctionPath,Does.Not.Contain(door.Junctions[0]));
        Assert.That(visitor.Movement.JunctionPath,Is.Not.Empty);
        var diagnosticRoute=visitor.Movement.JunctionPath.ToArray();
        var movement=new MovementSystem(); var execution=new ExecutionSystem();
        for(var i=0;i<300 && visitor.Plan.Status==PlanStatus.Active;i++)
        { world.Tick++; movement.Run(world); execution.Run(world); }
        Assert.That(visitor.Plan.Status,Is.EqualTo(PlanStatus.Completed));
        Assert.That(visitor.Execution.LastCompletedTick,Is.GreaterThan(0));
        Assert.That(visitor.CurrentJunction,Is.EqualTo(stand.Id));
        var root=Path.GetFullPath(".");
        while(!Directory.Exists(Path.Combine(root,"Assets"))) root=Directory.GetParent(root)!.FullName;
        var output=Path.Combine(root,"Build","Diagnostics","wave2-colony","643-spatial.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output,JsonSerializer.Serialize(new {
            radius=HexSpatialMath.HexRadius, portal=door.Junctions[0].Value,
            points=world.Tiles.Items[hut.Tile].Junctions.Select(j=>new { id=j.Value,
                x=world.Junctions.Items[j].WorldPosition.X,y=world.Junctions.Items[j].WorldPosition.Y,
                blocked=world.Junctions.Items[j].Blocked,portal=j==door.Junctions[0] }),
            route=diagnosticRoute.Select(j=>new { x=world.Junctions.Items[j].WorldPosition.X,y=world.Junctions.Items[j].WorldPosition.Y })
        }));
    }
    [Test]
    public void MissingAxeIsRejectedAndLosingItAbortsWithoutLeakingDoorClaim()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        visitor.Inventory.Items.Clear();
        Assert.That(engine.ApplyManualCommand(new BreakDoorCommand(visitor.Id,door.Id)).Accepted,Is.False);
        visitor.Inventory.Items.Add(new ItemInstance("tool.axe_stone"));
        Assert.That(engine.ApplyManualCommand(new BreakDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,1);
        visitor.Inventory.Items.Clear();
        Execute(world,visitor,1);
        Assert.That(visitor.Plan.Status,Is.Not.EqualTo(PlanStatus.Active));
        Assert.That(door.IsOccupied,Is.False);
    }
    [Test]
    public void StrongOwnerOpensAndActuallyStartsRetaliation()
    {
        var(engine,visitor,owner,door)=Setup(); var world=engine.World;
        owner.Traits.Remove(TraitKind.Coward);
        owner.Inventory.Items.Add(new ItemInstance("tool.axe_stone"));
        owner.Health=1f; visitor.Health=.1f;
        Assert.That(AbuseMath.Force(world,owner),Is.GreaterThanOrEqualTo(AbuseMath.Force(world,visitor)));
        Assert.That(engine.ApplyManualCommand(new BreakDoorCommand(visitor.Id,door.Id)).Accepted,Is.True);
        Execute(world,visitor,1);
        Assert.That(door.IsDoorOpen,Is.True);
        Assert.That(owner.Mind.CurrentGoal,Is.EqualTo(GoalType.Raid));
        Assert.That(owner.Mind.RaidTargetNpcId,Is.EqualTo(visitor.Id));
        Assert.That(FactionRelations.AreHostile(world,owner,visitor),Is.True);
        Assert.That(visitor.Plan.Status,Is.EqualTo(PlanStatus.Completed));
    }
}
}
