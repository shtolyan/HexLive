using System;
using System.Linq;
using HexLive.Simulation.Agents;
using HexLive.Simulation.AI;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;
using HexLive.Simulation.Common;
using HexLive.Simulation.Navigation;
namespace HexLive.Simulation.Runtime
{
/// <summary>§129.5: a voice can answer through the leaf; hands cannot.</summary>
public static class DoorInteractionMath
{
    public const int KnockTicks = 8;
    public const int TreeWorkCount = 4;
    public const int HearingRadiusTiles = 1;
    public const float TrustToOpen = .4f;
    public const float FamiliarityToOpen = .35f;
    public const int StressPulseTicks = 16;
    internal static ItemInstance Tool(NPCState npc) =>
        GearCatalog.BestToolFor(npc.Inventory.Items,GearCapability.ChopWood,npc.Body.IntactHands);
    internal static bool TryReserveApproach(WorldState world,NPCState npc,WorldObjectState door,out JunctionId stand)
    {
        stand=default;
        if(npc.CurrentJunction is not {} from) return false;
        var rim=world.Caches.ObjectApproachJunctionsScratch;
        SpatialQueries.CollectStandableAround(world,door.Junctions[0],rim,96,
            SpatialQueries.BesideReach(0f),door,InteractionReach.RimMode);
        var actors=PathfindingSystem.OtherActorJunctions(world,npc);
        var forbidden=DoorTopology.ForbiddenFor(world,npc.Faction);
        foreach(var candidate in rim.OrderBy(j=>HexSpatialMath.Distance(npc.Position,world.Junctions.Items[j].WorldPosition)).ThenBy(j=>j.Value))
        {
            if(candidate==door.Junctions[0] || actors.Contains(candidate)) continue;
            if(candidate==from) { stand=from; return true; }
            if(!SpatialQueries.IsJunctionFree(world,candidate) ||
                HexPathfinder.FindPath(world,from,candidate,actors,canJump:PlanningSystem.CanUseCriticalTraversal(npc),
                    hardAvoid:forbidden).Count==0) continue;
            if(!SpatialMutations.TryReserveJunction(world,candidate,npc.Id,world.Tick,Spec121.ManualReserveTicks)) continue;
            stand=candidate; return true;
        }
        return false;
    }
    public static int BreakWorkTicks(WorldState world, NPCState npc)
    {
        var tool=Tool(npc);
        var tree=world.Content.ObjectDefinitions[ContentIds.Palm].Interactions.First(i=>i.Type==InteractionType.Harvest);
        var speed=tool==null?1f:GearCatalog.For(tool.DefinitionId).HarvestSpeedMult;
        return TreeWorkCount*AttributeMath.WorkTicks(npc,GearCatalog.ScaleTicks(tree.DurationTicks,speed),
            InteractionType.Harvest,GoalType.HarvestTree);
    }
    private static NPCState[] Residents(WorldState world, WorldObjectState door, NPCState visitor)
    {
        var faction=BuildingDoorRules.OwnerFaction(world,door);
        return world.Entities.Npcs.Values.Where(n=>n.Id!=visitor.Id && n.Health>0f &&
            n.Faction==faction &&
            !n.IsUnconscious(world.Tick) && !n.IsBeingCarried &&
            HexSpatialMath.HexDistance(n.Tile,door.Tile)<=HearingRadiusTiles).OrderBy(n=>n.Id.Value).ToArray();
    }
    internal static void Knock(WorldState world,NPCState visitor,WorldObjectState door)
    {
        SocialCueSignals.Stamp(world,visitor,"DoorKnock",null);
        Trace.Emit(world,visitor.Id,"DoorKnocked",$"Door={door.Id.Value}");
        var residents=Residents(world,door,visitor);
        var owner=residents.OrderByDescending(n=>n.Social.GetOrCreate(visitor.Id).Trust).FirstOrDefault();
        if(owner==null) return;
        var relation=owner.Social.GetOrCreate(visitor.Id);
        var accept=!FactionRelations.AreHostile(world,owner,visitor) &&
            (owner.Faction==visitor.Faction || relation.Trust>=TrustToOpen ||
             (relation.Familiarity>=FamiliarityToOpen && relation.Affinity>0f));
        if(accept) accept=BuildingDoorRules.TryOpen(world,door.Id);
        SocialCueSignals.Stamp(world,owner,accept?"DoorWelcome":"DoorRefuse",visitor.Id,allowSleeping:true);
        if(accept) Trace.Emit(world,owner.Id,"DoorOpenedForGuest",$"Target=NPC{visitor.Id.Value} Door={door.Id.Value}");
        else Trace.Emit(world,owner.Id,"DoorRefusedGuest",$"Target=NPC{visitor.Id.Value} Door={door.Id.Value}");
    }
    // The journal needs a real person, never an object id guessed as an NPC.
    // In an empty house there is no mirror author.
    internal static string ResidentJournalTarget(WorldState world,NPCState visitor,WorldObjectState door)
    {
        var resident=Residents(world,door,visitor).FirstOrDefault();
        return resident==null?string.Empty:$" Target=NPC{resident.Id.Value}";
    }
    internal static void BreakAttempt(WorldState world,NPCState visitor,WorldObjectState door)
    {
        Trace.Emit(world,visitor.Id,"DoorBreakInStarted",$"Door={door.Id.Value}"+ResidentJournalTarget(world,visitor,door));
        var residents=Residents(world,door,visitor);
        foreach(var owner in residents)
        {
            var relation=owner.Social.GetOrCreate(visitor.Id);
            relation.Affinity=Math.Max(-1f,relation.Affinity-.2f);
            relation.Trust=Math.Max(-1f,relation.Trust-.15f);
            relation.LastInteractionTick=world.Tick;
            owner.Needs.Stress=Math.Min(1f,owner.Needs.Stress+.3f);
        }
        var defender=residents.FirstOrDefault(n=>!ManualControlMath.IsManual(n) && !n.IsFighting &&
            !n.Traits.Has(TraitKind.Coward) && AbuseMath.Force(world,n)>=AbuseMath.Force(world,visitor));
        if(defender!=null)
        {
            defender.Social.GetOrCreate(visitor.Id).Affinity=Math.Min(-.5f,defender.Social.GetOrCreate(visitor.Id).Affinity);
            BuildingDoorRules.TryOpen(world,door.Id);
            SocialCueSignals.Stamp(world,defender,"DoorDefend",visitor.Id,allowSleeping:true);
            RaidSystem.StartRaidOn(world,defender,visitor,"DoorBreakIn");
            return;
        }
        foreach(var owner in residents)
        {
            SocialCueSignals.Stamp(world,owner,"DoorFear",visitor.Id,allowSleeping:true);
            if(!owner.Traits.Has(TraitKind.Coward) && owner.Needs.Stress<.65f) continue;
            owner.Needs.Stress=Math.Max(.95f,owner.Needs.Stress);
            if(owner.Mind.CryingUntilTick>world.Tick || owner.IsFighting) continue;
            var until=world.Tick+SimBalance.CryingBreakdownTicks;
            PlanInterruption.TryAbort(world,owner,InterruptionCause.Crying,"Door break-in fear");
            owner.Mind.CurrentGoal=GoalType.None;
            ExecutionSystem.TryLieDownForCrying(world,owner,until);
            owner.Mind.CryingUntilTick=until;
            Trace.Emit(world,owner.Id,"CryingBreakdown",$"Reason=DoorBreakIn Target=NPC{visitor.Id.Value}");
        }
    }
    internal static void StressPulse(WorldState world,NPCState visitor,WorldObjectState door)
    {
        foreach(var owner in Residents(world,door,visitor))
        {
            owner.Needs.Stress=Math.Min(1f,owner.Needs.Stress+.03f);
            var relation=owner.Social.GetOrCreate(visitor.Id);
            relation.Affinity=Math.Max(-1f,relation.Affinity-.01f);
            relation.LastInteractionTick=world.Tick;
        }
    }
}
}
