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
// Read-only local planning view. Uses exactly the same admission as Drop;
// a suggested approach is not a route, a reservation, or a future guarantee.
public static class GroundItemPlacementPreview
{
    public const int ApproachRadiusTiles = 2;
    public const int MaxApproachRadiusTiles = 6;

    public static bool TryFindApproach(WorldState world, NPCState npc, ItemInstance item,
        out bool canDropHere, out Float2 approach, out JunctionId dropJunction, out int checkedOrigins,
        int approachRadiusTiles = ApproachRadiusTiles)
    {
        if (approachRadiusTiles < 1 || approachRadiusTiles > MaxApproachRadiusTiles)
            throw new ArgumentOutOfRangeException(nameof(approachRadiusTiles));
        approach = npc.Position;
        checkedOrigins = 1;
        canDropHere = GroundItemPlacement.TryFind(world, npc, item, out _, out dropJunction, out _);
        if (canDropHere) return true;
        var routeStart = GroundItemPlacement.RouteOrigin(world, npc);
        if (routeStart == null)
        {
            dropJunction = default;
            return false;
        }
        for (var ring = 1; ring <= approachRadiusTiles; ring++)
        for (var dq = -ring; dq <= ring; dq++)
        for (var dr = Math.Max(-ring, -dq - ring); dr <= Math.Min(ring, -dq + ring); dr++)
        {
            var tile = new TileCoord(npc.Tile.Q + dq, npc.Tile.R + dr);
            if (HexSpatialMath.HexDistance(npc.Tile, tile) != ring ||
                !SpatialQueries.IsTileWalkable(world, tile)) continue;
            JunctionId? origin = null;
            var closest = float.MaxValue;
            foreach (var id in world.Tiles.Items[tile].Junctions)
            {
                var node = world.Junctions.Items[id];
                // A shared boundary node can resolve to the other tile after
                // move_to. That would change Drop's one-tile search ring and
                // repeatedly suggest the same unusable coordinate.
                if (node.Tiles.Count != 1 || !node.Tiles[0].Equals(tile) ||
                    node.Fragment != npc.Fragment || !SpatialQueries.IsJunctionPassable(world, id) ||
                    !SpatialQueries.IsJunctionFree(world, id) ||
                    !Connectivity.ReachableWithoutTrace(world, routeStart.Value, id, npc.Body.CanJump)) continue;
                var dx = node.WorldPosition.X - npc.Position.X;
                var dy = node.WorldPosition.Y - npc.Position.Y;
                var distance = dx * dx + dy * dy;
                if (distance < closest || distance == closest && (origin == null || id.Value < origin.Value.Value))
                { origin = id; closest = distance; }
            }
            if (origin == null) continue;
            var position = world.Junctions.Items[origin.Value].WorldPosition;
            checkedOrigins++;
            if (!GroundItemPlacement.TryFindNear(world, npc, item, tile, position,
                    out _, out dropJunction, out _, routeStart: origin)) continue;
            approach = position;
            return true;
        }
        dropJunction = default;
        return false;
    }
}

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
        public readonly int LeafCount; public readonly float LeafSeparation;
        public Candidate(JunctionId id,TileCoord tile,Float2 position,float distance,int leafCount=0,float leafSeparation=0)
        {Id=id;Tile=tile;Position=position;Distance=distance;LeafCount=leafCount;LeafSeparation=leafSeparation;}
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
    private static readonly Comparison<Candidate> CompareLeafCandidates=(a,b) =>
    {
        var count=a.LeafCount.CompareTo(b.LeafCount);
        if(count!=0) return count;
        var separation=b.LeafSeparation.CompareTo(a.LeafSeparation);
        return separation!=0?separation:CompareCandidates(a,b);
    };

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
        out TileCoord tile,out JunctionId junction,out string reason,bool scatter=false,
        JunctionId? routeStart=null)
    {
        var scratch=Rent(world);
        try { return Find(world,npc,item,origin,position,scratch,out tile,out junction,out _,out reason,scatter,routeStart); }
        finally { Return(world,scratch); }
    }
    private static bool Find(WorldState world,NPCState npc,ItemInstance item,TileCoord origin,Float2 position,
        Scratch scratch,out TileCoord tile,out JunctionId junction,out GroundPileBounds resultBounds,out string reason,
        bool scatter=false,JunctionId? routeStart=null)
    {
        tile=origin;junction=default;resultBounds=default;reason="";
        scratch.ObjectsInspected=0;scratch.CandidatesChecked=0;
        routeStart ??= RouteOrigin(world,npc);
        if(routeStart==null) {reason="NoRouteOrigin";return false;}
        if(!world.Content.ObjectDefinitions.TryGetValue(item.DefinitionId,out var incomingDefinition) ||
            !GroundPileCatalog.TryGet(item.DefinitionId,incomingDefinition.Layer!=null,out var profile))
        {reason="UnknownGroundGeometry:"+item.DefinitionId;return false;}
        reason="NoDropSpot";
        var capacity=scatter?1:GroundPileCatalog.Capacity(item.DefinitionId);
        var footprint=Footprint(profile,capacity,scatter);
        var compactLeaves=scatter && item.DefinitionId==ContentIds.PalmLeaf;
        if(compactLeaves) { origin=HexSpatialMath.WorldToTile(position); tile=origin; }
        scratch.Candidates.Clear();scratch.Objects.Clear();scratch.SeenJunctions.Clear();
        scratch.SeenObjects.Clear();scratch.Sockets.Clear();scratch.ObjectsInspected=0;
        // §54.21: crown leaves spread evenly inside their source hex; logs and other
        // harvests still need separate ground spots. Inventory admission is unchanged.
        var searchRadius=compactLeaves?0:scatter?3:1;
        for(var dq=-searchRadius;dq<=searchRadius;dq++) for(var dr=Math.Max(-searchRadius,-dq-searchRadius);dr<=Math.Min(searchRadius,-dq+searchRadius);dr++)
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
                var support=scatter?HexSpatialMath.WorldToTile(node.WorldPosition):coord;
                if(scatter && !SpatialQueries.IsTileWalkable(world,support)) continue;
                if(compactLeaves && (!support.Equals(origin) || !FitsInsideHex(node.WorldPosition,origin,profile.Single.RadiusXZ))) continue;
                scratch.Candidates.Add(new(id,support,node.WorldPosition,dx*dx+dz*dz));
            }
        }
        scratch.Candidates.Sort(CompareCandidates);
        // Pad the fixed candidate ring by both shared geometry extents. This
        // includes a wide neighbouring pile whose anchor lies outside that ring.
        var rings=searchRadius+1+(int)Math.Ceiling((footprint.RadiusXZ+GroundPileCatalog.MaximumRadiusXZ)/
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
        if(compactLeaves)
        {
            // Balance all admissible anchors before adding another thin layer.
            // Among equally filled anchors choose the one farthest from existing
            // leaves, so even the eight-leaf yield covers the source hex.
            // Score from saved objects, not transient batch/rank state: collecting
            // a leaf or loading a save must not move any surviving leaf.
            for(var i=0;i<scratch.Candidates.Count;i++)
            {
                var candidate=scratch.Candidates[i];var count=0;var separation=float.MaxValue;
                foreach(var obj in scratch.Objects)
                {
                    if(!obj.IsHarvestScatter || obj.DefinitionId!=ContentIds.PalmLeaf || !obj.Tile.Equals(origin) ||
                        !world.Junctions.Items.TryGetValue(obj.Junctions[0],out var anchor)) continue;
                    if(obj.Junctions[0]==candidate.Id) count++;
                    var dx=candidate.Position.X-anchor.WorldPosition.X;var dz=candidate.Position.Y-anchor.WorldPosition.Y;
                    separation=Math.Min(separation,dx*dx+dz*dz);
                }
                scratch.Candidates[i]=new(candidate.Id,candidate.Tile,candidate.Position,candidate.Distance,count,separation);
            }
            scratch.Candidates.Sort(CompareLeafCandidates);
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
                // Separate item instances remain independently pickable; only
                // harvest leaves may overlap each other, never an inventory pile.
                if(compactLeaves && obj.IsHarvestScatter && obj.DefinitionId==ContentIds.PalmLeaf) continue;
                if(!GroundPileCatalog.TryGet(obj.DefinitionId,def.Layer!=null,out var neighbourProfile)) {reason="UnknownGroundGeometry:"+obj.DefinitionId;return false;}
                if(obj.Junctions[0]==candidate.Id)
                {
                    if(capacity==1 || obj.IsHarvestScatter || !Compatible(item,obj) || ++count>=capacity) {valid=false;break;}
                    continue;
                }
                if(!world.Junctions.Items.TryGetValue(obj.Junctions[0],out var anchor)) continue;
                var neighbour=Footprint(neighbourProfile,GroundPileCatalog.Capacity(obj.DefinitionId),obj.IsHarvestScatter)
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
            // A locally free point may belong to a sealed component (bug #477:
            // three old palms left their centre free but unreachable). The item
            // must land in the actor's actual route component, including when a
            // harvest scatters farther than the ordinary one-tile drop.
            if(!routeStart.Value.Equals(candidate.Id) &&
               !Connectivity.ReachableWithoutTrace(world,routeStart.Value,candidate.Id,npc.Body.CanJump)) continue;
            tile=candidate.Tile;junction=candidate.Id;resultBounds=bounds;reason="";return true;
        }
        return false;
    }
    private static bool FitsInsideHex(Float2 point,TileCoord tile,float radius)
    {
        var center=HexSpatialMath.TileToWorld(tile);
        var x=point.X-center.X;var z=point.Y-center.Y;
        var limit=HexSpatialMath.HexApothem-radius-.01f;
        // Distance to each pair of sides of the pointy-top hex. The measured
        // bounding circle contains the whole leaf for every allowed yaw.
        return Math.Abs(x)<=limit && Math.Abs(.5f*x+HexSpatialMath.Sqrt3*.5f*z)<=limit &&
            Math.Abs(.5f*x-HexSpatialMath.Sqrt3*.5f*z)<=limit;
    }

    internal static JunctionId? RouteOrigin(WorldState world,NPCState npc)
    {
        if(npc.CurrentJunction is { } current &&
            world.Junctions.Items.TryGetValue(current,out var node) &&
            node.Fragment==npc.Fragment && SpatialQueries.IsJunctionPassable(world,current))
            return current;

        // A new arrival or a just-displaced actor can temporarily have only
        // Position/Tile. Resolve from the local tile instead of silently
        // disabling the route guard; no world-wide junction scan is needed.
        if(!world.Tiles.Items.TryGetValue(npc.Tile,out var tile)) return null;
        JunctionId? nearest=null;
        var best=float.MaxValue;
        foreach(var id in tile.Junctions)
        {
            if(!world.Junctions.Items.TryGetValue(id,out var candidate) ||
                candidate.Fragment!=npc.Fragment || !SpatialQueries.IsJunctionPassable(world,id)) continue;
            var dx=candidate.WorldPosition.X-npc.Position.X;
            var dy=candidate.WorldPosition.Y-npc.Position.Y;
            var distance=dx*dx+dy*dy;
            if(distance<best || distance==best && (nearest==null || id.Value<nearest.Value.Value))
            {best=distance;nearest=id;}
        }
        return nearest;
    }
    private static GroundPileBounds Footprint(GroundPileProfile profile,int capacity,bool scatter=false)
    {
        var bound=scatter?profile.Single:profile.FullBounds;
        if(!profile.Garment && !scatter) return bound;
        // Deterministic garment/scatter yaw fits this measured circle.
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
