using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HexLive.Simulation.Agents;
using HexLive.Simulation.Common;
using HexLive.Simulation.Content;
using HexLive.Simulation.Core;
using HexLive.Simulation.Spatial;

namespace HexLive.Simulation.Runtime
{
// §54/§123: local, definition-aware drop admission. No world-object scan.
internal static class GroundItemPlacement
{
    internal readonly struct SearchMetrics
    {
        public readonly int ObjectsInspected, CandidatesChecked;
        public SearchMetrics(int objectsInspected,int candidatesChecked)
        {ObjectsInspected=objectsInspected;CandidatesChecked=candidatesChecked;}
    }
    private sealed class Scratch
    {
        public readonly List<Candidate> Candidates = new();
        public readonly List<WorldObjectState> Objects = new();
        public readonly HashSet<JunctionId> SeenJunctions = new(), Sockets = new();
        public readonly HashSet<ObjectId> SeenObjects = new();
        public readonly List<Reservation> Reserved = new();
        public int ObjectsInspected, CandidatesChecked;
    }
    private readonly struct Candidate
    {
        public readonly JunctionId Id; public readonly TileCoord Tile; public readonly Float2 Position; public readonly float Distance;
        public Candidate(JunctionId id,TileCoord tile,Float2 position,float distance)
        {Id=id;Tile=tile;Position=position;Distance=distance;}
    }
    private readonly struct Reservation
    {
        public readonly JunctionId Junction; public readonly GroundPileBounds Bounds; public readonly ItemInstance Item;
        public Reservation(JunctionId junction,GroundPileBounds bounds,ItemInstance item)
        {Junction=junction;Bounds=bounds;Item=item;}
    }
    // Weak per-world scratch: no static world retention, no per-drop lists.
    private static readonly ConditionalWeakTable<WorldState,ConcurrentBag<Scratch>> Work = new();
    private static readonly Comparison<Candidate> CompareCandidates=(a,b) =>
    {var d=a.Distance.CompareTo(b.Distance);return d!=0?d:a.Id.Value.CompareTo(b.Id.Value);};

    private static Scratch Rent(WorldState world) => Work.GetOrCreateValue(world).TryTake(out var scratch) ? scratch : new Scratch();
    private static void Return(WorldState world,Scratch scratch)
    {
        scratch.Candidates.Clear();scratch.Objects.Clear();scratch.SeenJunctions.Clear();
        scratch.SeenObjects.Clear();scratch.Sockets.Clear();scratch.Reserved.Clear();
        Work.GetOrCreateValue(world).Add(scratch);
    }
    internal static bool TryFind(WorldState world,NPCState npc,ItemInstance item,
        out TileCoord tile,out JunctionId junction,out string reason)
    {
        return TryFind(world,npc,item,out tile,out junction,out reason,out _);
    }
    internal static bool TryFind(WorldState world,NPCState npc,ItemInstance item,
        out TileCoord tile,out JunctionId junction,out string reason,out SearchMetrics metrics)
    {
        var scratch=Rent(world);
        try
        {
            var found=Find(world,npc,item,npc.Tile,npc.Position,scratch,out tile,out junction,out _,out reason);
            metrics=new(scratch.ObjectsInspected,scratch.CandidatesChecked);
            return found;
        }
        finally { Return(world,scratch); }
    }
    internal static bool CanPlaceBatch(WorldState world,NPCState npc,IReadOnlyList<ItemInstance> items)
        => CanPlaceBatch(world,npc,items,out _);
    internal static bool CanPlaceBatch(WorldState world,NPCState npc,IReadOnlyList<ItemInstance> items,out string reason)
    {
        var scratch=Rent(world);reason="";
        try
        {
            for(var i=0;i<items.Count;i++)
            {
                var item=items[i];
                if(!Find(world,npc,item,npc.Tile,npc.Position,scratch,out _,out var junction,out var bounds,out reason)) return false;
                scratch.Reserved.Add(new(junction,bounds,item));
            }
            return true;
        }
        finally { Return(world,scratch); }
    }
    internal static bool TryFindNear(WorldState world,NPCState npc,ItemInstance item,TileCoord origin,Float2 position,
        out TileCoord tile,out JunctionId junction,out string reason)
    {
        var scratch=Rent(world);
        try { return Find(world,npc,item,origin,position,scratch,out tile,out junction,out _,out reason); }
        finally { Return(world,scratch); }
    }
    private static bool Find(WorldState world,NPCState npc,ItemInstance item,TileCoord origin,Float2 position,
        Scratch scratch,out TileCoord tile,out JunctionId junction,out GroundPileBounds resultBounds,out string reason)
    {
        tile=origin;junction=default;resultBounds=default;reason="";
        scratch.ObjectsInspected=0;scratch.CandidatesChecked=0;
        if(!world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId,out var incomingDefinition) ||
            !GroundPileCatalog.TryGet(item.DefinitionId,incomingDefinition.Layer!=null,out var profile))
        {reason="UnknownGroundGeometry:"+item.DefinitionId;return false;}
        reason="NoDropSpot";
        var capacity=GroundPileCatalog.Capacity(item.DefinitionId);
        var footprint=Footprint(profile,capacity);
        scratch.Candidates.Clear();scratch.Objects.Clear();scratch.SeenJunctions.Clear();
        scratch.SeenObjects.Clear();scratch.Sockets.Clear();scratch.ObjectsInspected=0;
        // Candidate ring is the existing harvest/drop search radius (one tile).
        for(var dq=-1;dq<=1;dq++) for(var dr=Math.Max(-1,-dq-1);dr<=Math.Min(1,-dq+1);dr++)
        {
            var coord=new TileCoord(origin.Q+dq,origin.R+dr);
            if(!world.Tiles.Items.TryGetValue(coord,out var cell) ||
                (cell.Flags & TileFlags.Walkable)==0 || (cell.Flags & TileFlags.Blocked)!=0) continue;
            foreach(var id in cell.Junctions)
            {
                if(!scratch.SeenJunctions.Add(id) || !world.Junctions.Items.TryGetValue(id,out var node) ||
                    node.Fragment!=npc.Fragment || !SpatialQueries.IsJunctionPassable(world,id)) continue;
                if(world.Occupancy.JunctionOwner.TryGetValue(id,out var owner) && owner!=null && owner!=npc.Id) continue;
                var dx=node.WorldPosition.X-position.X;var dz=node.WorldPosition.Y-position.Y;
                scratch.Candidates.Add(new(id,coord,node.WorldPosition,dx*dx+dz*dz));
            }
        }
        scratch.Candidates.Sort(CompareCandidates);
        // Pad the fixed candidate ring by both shared geometry extents. This
        // includes a wide neighbouring pile whose anchor lies outside that ring.
        var rings=2+(int)Math.Ceiling((footprint.RadiusXZ+GroundPileCatalog.MaximumRadiusXZ)/
            (HexSpatialMath.HexRadius*HexSpatialMath.HexRowStepFactor));
        for(var dq=-rings;dq<=rings;dq++) for(var dr=Math.Max(-rings,-dq-rings);dr<=Math.Min(rings,-dq+rings);dr++)
        {
            var coord=new TileCoord(origin.Q+dq,origin.R+dr);
            if(!world.Caches.ObjectsByTile.TryGetValue(coord,out var ids)) continue;
            foreach(var id in ids)
            {
                if(!scratch.SeenObjects.Add(id) || !world.Entities.Objects.TryGetValue(id,out var obj) || obj.Junctions.Count==0) continue;
                scratch.ObjectsInspected++;
                if(IsSocket(obj.DefinitionId)) scratch.Sockets.Add(obj.Junctions[0]);
                else scratch.Objects.Add(obj);
            }
        }
        foreach(var candidate in scratch.Candidates)
        {
            scratch.CandidatesChecked++;
            if(scratch.Sockets.Contains(candidate.Id)) continue;
            var bounds=footprint.At(new(candidate.Position.X,0,candidate.Position.Y));
            var count=0;var valid=true;
            foreach(var obj in scratch.Objects)
            {
                if(scratch.Sockets.Contains(obj.Junctions[0]) || !world.Content.ObjectDefinitions.TryGetValue(obj.DefinitionId,out var def) || !IsLoose(def)) continue;
                if(!GroundPileCatalog.TryGet(obj.DefinitionId,def.Layer!=null,out var neighbourProfile)) {reason="UnknownGroundGeometry:"+obj.DefinitionId;return false;}
                if(obj.Junctions[0]==candidate.Id)
                {
                    if(capacity==1 || !Compatible(item,obj) || ++count>=capacity) {valid=false;break;}
                    continue;
                }
                if(!world.Junctions.Items.TryGetValue(obj.Junctions[0],out var anchor)) continue;
                var neighbour=Footprint(neighbourProfile,GroundPileCatalog.Capacity(obj.DefinitionId))
                    .At(new(anchor.WorldPosition.X,0,anchor.WorldPosition.Y));
                if(bounds.OverlapsXZ(neighbour)) {valid=false;break;}
            }
            if(!valid) continue;
            foreach(var reservation in scratch.Reserved)
            {
                if(reservation.Junction==candidate.Id)
                {
                    if(capacity==1 || !Compatible(item,reservation.Item) || ++count>=capacity) {valid=false;break;}
                }
                else if(bounds.OverlapsXZ(reservation.Bounds)) {valid=false;break;}
            }
            if(!valid) continue;
            tile=candidate.Tile;junction=candidate.Id;resultBounds=bounds;reason="";return true;
        }
        return false;
    }
    private static GroundPileBounds Footprint(GroundPileProfile profile,int capacity)
    {
        var bound=profile.FullBounds;
        if(!profile.Garment) return bound;
        // Any existing deterministic garment yaw fits this measured circle.
        var radius=bound.RadiusXZ;
        return new(-radius,bound.MinY,-radius,radius,bound.MaxY,radius);
    }
    internal static bool IsSocket(string id) => id==ContentIds.DryingRack || id==ContentIds.Wardrobe || id==ContentIds.WaterCollector;
    internal static bool IsLoose(ObjectDefinition def)
    {
        // Campfire PickUp is intercepted as take.from.spit, never a loose fire.
        if(def.Id=="campfire.spot") return false;
        if(def.Layer!=null || GroundPileCatalog.TryGet(def.Id,false,out _)) return true;
        foreach(var interaction in def.Interactions) if(interaction.Type==InteractionType.PickUp) return true;
        return false;
    }
    // Inventory stacks share DefinitionId only; world objects retain individual state.
    private static bool Compatible(ItemInstance a,WorldObjectState b) => a.DefinitionId==b.DefinitionId;
    private static bool Compatible(ItemInstance a,ItemInstance b) => a.DefinitionId==b.DefinitionId;
}
}
