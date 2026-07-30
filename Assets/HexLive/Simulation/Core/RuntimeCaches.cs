using System.Collections.Generic;
using HexLive.Simulation.Common;

namespace HexLive.Simulation.Core
{

public sealed class RuntimeCaches
{
    public Dictionary<TileCoord, List<EntityId>> EntitiesByTile { get; } = new();

    public Dictionary<FragmentId, List<EntityId>> EntitiesByFragment { get; } = new();

    public Dictionary<TileCoord, List<ObjectId>> ObjectsByTile { get; } = new();

    // Spec 29C.3 (chase-path fix): junctions a ground mob may never STEP on —
    // indoor (sanctuary), doors, all-water. Chase pathfinding feeds these into
    // FindPath's avoid set so a dog plans routes it can actually walk; without
    // this it planned the girls' shortest path THROUGH the hut, refused the
    // first indoor step every pass, and stood frozen mid-camp forever (seed
    // 521091321 day 43, dog 13). Rebuilt lazily when TopologyVersion moves
    // (build completions bump it when walls/doors/blocks change).
    public HashSet<JunctionId> MobForbiddenJunctions { get; } = new();

    public int MobForbiddenBuiltVersion { get; set; }
}

}
