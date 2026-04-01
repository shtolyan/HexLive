# Game Specification — Emergent NPC Simulation (Tick-Driven, Restored + Extended)

> **⚠️ IMPORTANT NOTE**
> This document was updated to introduce a tick-driven architecture, but ALL previously defined systems (AI, perception, social, content, debug, etc.) are preserved and extended — not removed.
> Tick system is now the foundation layer, not a replacement.

## 1. Core Principle

```
Simulation Core (Tick-based)
    ↓
State / Events
    ↓
Unity View
```

**Unity = View only. Simulation = Everything else.**

## 2. Tick System (FOUNDATION)

Tick = atomic simulation step

**Everything in the game evolves through ticks:**

- movement
- AI
- perception
- memory
- social
- interactions

### 2.1 Tick Layers

**Fast:**

- movement
- rotation
- execution

**Medium:**

- perception
- needs
- decisions

**Slow:**

- long-term goals
- environment

### 2.2 Rule

NOTHING depends on Unity frame updates

## 3. Spatial System (Fragment / Tile / Junction)

```
World
├── Fragment
│   ├── Tile
│   │   ├── Junction (universal spatial node)
│   │       ├── interior: 1 tile
│   │       ├── boundary edge: 2 tiles (shared)
│   │       └── boundary vertex: 3 tiles (shared)
```

Junction is the single spatial entity. There is no separate Point concept.
Each Junction has an explicit neighbor list (adjacency graph).
Boundary junctions are shared — one entity per physical position, not duplicates.

### 3.1 With Tick Integration

- movement across junctions happens per tick
- occupancy updates per tick
- reservation expiration per tick

## 4. Perception System (RESTORED + TICK-AWARE)

NPC does NOT see full world. It builds a filtered snapshot.
World → Perception → Snapshot → Decision

### 4.1 What NPC Sees

- self (needs)
- nearby tiles
- objects
- NPCs
- environment

### 4.2 Tick Integration

**Perception updates:**

- on medium ticks
- on events

## 5. Decision System (RESTORED)

NPC evaluates goals via utility.
Needs + Context + Social + Memory → Goal

### 5.1 Tick Integration

- decision NOT every tick
- runs periodically
- goal locking required

## 6. Planning System

Transforms goal → steps

**Example:**

- find object
- reserve
- move
- interact

**Tick Note**

**Planning only:**

- on goal change
- on failure

## 7. Pathfinding (Junction Graph)

Pathfinding operates on the Junction graph via BFS.
Path = `List<JunctionId>` — NPC walks junction-to-junction.
Boundary junctions naturally connect tiles (they belong to multiple tiles).
No separate tile-level or fragment-level pathfinding needed.

**Tick Note**

- path reused across ticks
- movement consumes path junction by junction

## 8. Movement System

Movement = simulation-driven
Rotate → Move → Update Position
Unity only visualizes.

## 9. Execution System

Actions progress across ticks.

## 10. Memory System (RESTORED)

**NPC has:**

- short-term memory
- long-term memory
- working memory

**Tick Note**

- decay per tick
- events create memory

## 11. Social System (RESTORED)

**NPC considers:**

- trust
- embarrassment
- presence
- hearing

**Tick Note**

- partially tick-based
- partially event-based

## 12. Content System (RESTORED)

**Objects define:**

- interactions
- affordances

NPC decides usage.

**Tick Note**

- interactions must be step-based
- effects applied over ticks

## 13. Debug System (RESTORED)

**Must show:**

- perception
- goal scores
- plan
- path
- state
- failure reasons

**Tick Note**

- All logs MUST include tick index
- [Tick 120] GoalSelected: Eat

## 14. Core Flow (IMPORTANT)

```
Tick → Perception → Decision → Plan → Movement → Execution → Memory → Repeat
```

## 15. Unity Layer

**Unity is ONLY:**

- renderer
- interpolator
- UI
- debug

## 16. ECS Philosophy

**We use:**

- entity IDs
- data components
- systems
- BUT custom implementation (not Unity ECS)

## 17. What Was Preserved

**All previously designed systems remain:**

- ✔ Perception model
- ✔ Utility AI
- ✔ Planning
- ✔ Memory system
- ✔ Social system
- ✔ Content/interaction authoring
- ✔ Debug tooling
  They are now: → tick-driven

## 18. Final Summary

We did NOT simplify the system. We:

- ✔ kept full depth
- ✔ added strict execution model
- ✔ made everything deterministic
  This is now a full simulation architecture, not just a design document

## 19. World State / Data Model

This section defines what exists in the simulation, where data lives, and what is the single source of truth.
Rule: All gameplay-relevant state lives inside the Simulation Core. Unity never owns gameplay state.

### 19.1 Source of Truth

**Single authoritative container:**

```csharp
class WorldState
{
    public int Tick;

    public FragmentMap Fragments;
    public TileMap Tiles;
    public JunctionMap Junctions;

    public EntityRepository Entities;

    public ReservationState Reservations;
    public OccupancyState Occupancy;
    public EnvironmentState Environment;

    // runtime caches (optional, non-authoritative)
    public RuntimeCaches Caches;
}
```

Only WorldState is authoritative.

### 19.2 Entities = IDs

Entities are identifiers, not objects with behavior.

```csharp
struct EntityId { public int Value; }
```

All behavior is in systems. All data is in components/state structs.

### 19.3 NPC State (v1)

```csharp
class NPCState
{
    public EntityId Id;

    // spatial
    public FragmentId Fragment;
    public TileCoord Tile;
    public JunctionId? CurrentJunction;
    public Float2 Position; // continuous world position
    public float Rotation;

    // motion
    public float MoveSpeed;
    public float TurnSpeed;

    // needs
    public NPCNeeds Needs;

    // mind
    public NPCMind Mind;            // goals, scores, locks
    public NPCPlanState Plan;       // current plan + step index
    public NPCExecutionState Exec;  // current action runtime

    // perception snapshot (cached per update)
    public PerceptionSnapshot Perception;

    // memory / social
    public MemoryState Memory;
    public SocialState Social;
}
```

**Design notes:**

- keep data flat and explicit
- avoid references to Unity objects

### 19.4 Object State (v1)

```csharp
class WorldObjectState
{
    public ObjectId Id;
    public string DefinitionId; // links to static content

    public FragmentId Fragment;
    public TileCoord Tile;
    public List<JunctionId> Junctions;

    public bool IsOccupied;
    public EntityId? CurrentUser;

    // example runtime data
    public float ResourceAmount; // e.g., food left
}
```

### 19.5 Static vs Runtime Data

**Separate clearly:**

**Static (Content):**

- ObjectDefinition
- InteractionDefinition
- Tags, roles, rules

**Runtime (WorldState):**

- occupancy
- reservations
- current users
- dynamic values (amounts, cleanliness, etc.)

Static data never changes at runtime.

### 19.6 Reservation State

```csharp
class ReservationState
{
    public Dictionary<JunctionId, ReservationRecord> Junctions;
}

class ReservationRecord
{
    public EntityId Owner;
    public int StartTick;
    public int EndTick;
}
```

### 19.7 Environment State

```csharp
class EnvironmentState
{
    public float GlobalTemperature;
    public int GlobalCrowdLevel;
}
```

Temperature is a first-class simulation value.

**Recommended extension:**

- global/base temperature
- fragment-local modifiers
- tile-local modifiers
- clothing modifiers on NPC side

**This allows the simulation to support:**

- hot/cold discomfort
- environment-driven behavior shifts
- future room climate systems

### 19.8 Runtime Caches (Non-Authoritative)

**Optional structures to speed up queries:**

```csharp
class RuntimeCaches
{
    public Dictionary<TileCoord, List<EntityId>> EntitiesByTile;
    public Dictionary<FragmentId, List<EntityId>> EntitiesByFragment;
}
```

**Rules:**

- can be rebuilt
- must not be the source of truth

### 19.9 Serialization Boundaries

**Must be serializable:**

- WorldState (core)
- NPCState
- ObjectState
- Reservations
- Tick index

**Do NOT serialize:**

- caches
- debug-only data
- derived/transient fields

### 19.10 Queries vs Direct Access

**Prefer explicit queries over scattered access:**

```csharp
IEnumerable<EntityId> GetEntitiesInTile(TileCoord tile)
WorldObjectState GetObject(ObjectId id)
```

This keeps systems decoupled from storage layout.

### 19.11 Minimal v1 Data Scope

For the first slice, include only:

- 1 NPCState
- 2–3 ObjectState (bed, food, chair)
- simple Needs
- simple Plan/Execution
- ReservationState
  Keep it small, but structurally correct.

### 19.12 Design Rules

- WorldState is the single source of truth
- Entities are IDs, not logic containers
- All gameplay data must be serializable
- Static content is separate from runtime state
- Caches are optional and non-authoritative
- Systems read/write WorldState only through well-defined paths

### 19.13 Summary

**World State answers:**

What exists right now in the simulation, in a form that can be stepped, saved, debugged, and replayed?
This is the backbone on which all tick-driven systems operate.

## 20. Spatial System (Deep)

This section formalizes the world space using a graph-based, tick-compatible model:
Fragment → Tile → Junction

**Goals:**

- deterministic navigation without NavMesh
- cheap queries for perception and planning
- precise interaction anchoring via junctions
- scalable generation (fragments)
- explicit graph connectivity for pathfinding

### 20.1 Core Structures

```csharp
struct FragmentId { public int Value; }
struct TileCoord { public int Q; public int R; } // axial coordinates for hex grid
struct JunctionId { public int Value; }

class Fragment
{
    public FragmentId Id;
    public Dictionary<TileCoord, Tile> Tiles;
    public List<FragmentLink> Links; // neighbors / portals
}

class Tile
{
    public TileCoord Coord;
    public TileFlags Flags; // walkable, blocked, indoor, etc.
    public List<JunctionId> Junctions;
    public float TemperatureModifier;
}

class Junction
{
    public JunctionId Id;
    public FragmentId Fragment;
    public Float2 WorldPosition;
    public List<TileCoord> Tiles; // 1 for interior, 2-3 for boundary
    public bool Blocked;
    public List<JunctionId> Neighbors; // explicit adjacency
}
```

**Junction is the universal spatial node.** There is no separate Point entity.
Interior junctions belong to 1 tile. Boundary junctions are shared between 2-3 tiles.
Each junction knows its neighbors via an explicit adjacency list built at world generation.

**Hex Tile Model**

- The default world tile for this project is a hexagonal tile.

**Important rule:**

- The map is built from hex tiles, not square tiles.

**This means:**

- tile adjacency should use hex neighbors
- pathfinding should operate on a hex graph
- coordinate system should use axial or cube coordinates

**Recommended v1:**

- use axial coordinates (Q, R) for simplicity
- each small room / local map chunk is a compact hex fragment

This fits the intended visual style and supports modular world construction.

### 20.2 Coordinate Model (Discrete + Continuous)

**Each entity has:**

- Discrete: FragmentId, TileCoord, JunctionId (for planning/reservation)
- Continuous: Float2 Position, float Rotation (for execution)

**Rule:**

Planning uses discrete graph; movement executes in continuous space.

### 20.3 Adjacency & Graphs

Adjacency is stored directly on Junction nodes — no separate graph structure needed.

```csharp
// Each Junction stores its neighbors:
class Junction
{
    public List<JunctionId> Neighbors; // explicit adjacency list
}
```

**Adjacency is built at world generation** using integer key offsets:
- Each junction has a deterministic (xKey, yKey) computed from tile coord + sub-grid axial position
- The 6 neighbor offsets in key-space are constant: (+1,+1), (-1,-1), (0,+2), (0,-2), (+1,-1), (-1,+1)
- Lookup neighbor keys in a global dictionary → O(n) build, works across tile boundaries

Boundary junctions naturally connect tiles — they belong to multiple tiles and have neighbors in each.

### 20.4 Occupancy & Reservation (Spatial)

Spatial ownership is first-class.

```csharp
class OccupancyState
{
    public Dictionary<JunctionId, EntityId?> JunctionOwner;
    public Dictionary<TileCoord, List<EntityId>> EntitiesInTile;
}
```

**Rules:**

- interactions require reserving junctions
- occupancy updates every tick (fast layer)
- reservation has deadlines (tick-based)
- each Junction is unique — no "linked points" grouping needed

### 20.5 Reachability Queries

Provide canonical queries (used by perception/AI):

```csharp
bool IsTileWalkable(TileCoord t);
bool IsJunctionFree(JunctionId j);
bool IsJunctionPassable(JunctionId j);
JunctionId? FindNearestJunction(Float2 worldPosition);
IReadOnlyList<JunctionId> GetPassableNeighbors(JunctionId j);
```

Implementations should be cache-friendly and avoid scanning entire world.

### 20.6 Local Neighborhood (Perception Scope)

**Perception should operate on a bounded neighborhood:**

- tiles within radius R (e.g., Manhattan or Euclidean)
- points within those tiles
- entities indexed by tile

```csharp
IEnumerable<TileCoord> GetTilesInRadius(TileCoord center, int radius);
```

This keeps perception O(local) rather than O(world).

### 20.7 Fragment Linking (Portals)

**Fragments connect via explicit links:**

```csharp
class FragmentLink
{
    public FragmentId A;
    public FragmentId B;
    public TileCoord EntryA;
    public TileCoord EntryB;
}
```

**Pathfinding:**

- high level: fragment graph
- mid level: tile graph
- low level: final point

### 20.8 Tile Micro-Grid (Optional v2)

Inside a tile, you may define micro positions (points or subcells).

**For v1:**

- rely on Points + continuous Position

**For v2:**

- add subgrid for tighter collision/formation

### 20.9 Interaction Anchors

Junctions are universal — no role system. Any junction can host any object.
Objects are placed on junctions by slot index at world generation time.
Interactions reference junctions directly via `List<JunctionId>`.

### 20.10 Movement Targets

Movement targets resolve to a JunctionId. Path = `List<JunctionId>`.

```csharp
class MovementState
{
    public List<JunctionId> JunctionPath;
    public int PathIndex;
}
```

**Execution:**

- BFS pathfind on junction graph
- walk junction-to-junction using WorldPosition
- tile transitions detected from junction.Tiles

### 20.11 Dynamic Blocking

**Tiles/points can become blocked at runtime:**

- occupancy (another NPC)
- reservation
- object state (closed door)

**Rules:**

- mark path as stale
- emit event: PathInvalidated
- replan on next decision phase (or controlled same-tick if allowed)

### 20.12 Caching Strategies

Useful caches (non-authoritative):

- entities by tile
- points by role per tile
- nearest objects by tag (local)
  All caches must be rebuildable from WorldState.

### 20.13 Editor / Generation Constraints

**Content placement must satisfy:**

- required junction anchoring exist
- points do not overlap illegally
- fragment links are consistent
- tile flags match object requirements

Validation should run at authoring time.

### 20.14 Minimal v1 Spatial Scope

- 1 Fragment
- ~10–20 Tiles (grid)
- Points for: Access, Sit, Sleep
- 4-neighbor TileGraph
- simple Occupancy + Reservation
  Keep graphs explicit and simple.

### 20.15 Design Rules

- Discrete for planning, continuous for execution
- Points are the unit of interaction, not tiles
- Occupancy and reservation are first-class
- Adjacency must be explicit (no hidden queries)
- Perception operates on local neighborhoods
- All spatial mutations occur on ticks

### 20.16 Summary

**Spatial System answers:**

Where can an entity go, where can it act, and what space is currently available or reserved?
It provides the physical backbone for navigation, interaction, and perception.

## 21. Movement, Rotation & Navigation Execution

This section defines how an entity physically progresses through the world over ticks.

**Important distinction:**

- Pathfinding decides where to go. Movement execution decides how the entity actually gets there over time.
- This system is fully simulation-driven. Unity only visualizes the already computed state.

### 21.1 Core Principle

Movement must not be treated as instant teleportation between tiles unless explicitly requested.
Instead, movement is a tick-progressed process that updates:

- position
- rotation
- tile transitions
- target reach state
- movement status

**Rule:**

The authoritative position and rotation of the entity always live in the simulation model.

### 21.2 Movement State

Each moving entity should have explicit runtime movement state.

```csharp
class MovementState
{
    public bool IsMoving;

    public List<JunctionId> JunctionPath;
    public int PathIndex;

    public Float2 DesiredDirection;
    public float DesiredRotation;

    public float MoveSpeed;
    public float TurnSpeed;

    public MovementStatus Status;
}
enum MovementStatus
{
    Idle,
    Rotating,
    Moving,
    Arrived,
    Blocked,
    Waiting,
    Invalid
}
```

This state should be visible in debug tools.

### 21.3 Planning Target vs Execution Target

There are two levels of target.
Planning Target
Chosen by the planner. Examples:

- reach tile (4, 7)
- reach bed interaction point
- reach NPC B
- Execution Target
  Currently active step for movement system. Examples:
- rotate toward next tile center
- move toward next tile center
- align to final interaction point
  This separation is useful because the execution layer may change several low-level targets while serving one high-level plan.

### 21.4 Position Model

Movement works on continuous position, but remains tied to discrete space.
Authoritative movement-related fields:

- FragmentId
- TileCoord (derived from current junction's tiles)
- JunctionId? CurrentJunction
- Float2 Position (continuous world position)

```csharp
float Rotation
```

**Rule:**

- path is discrete
- execution is continuous

This is the most practical hybrid model.

### 21.5 Rotation Execution

Rotation should progress over ticks, not snap instantly unless required.

**Example:**

```csharp
void UpdateRotation(NPCState npc, float dt)
{
    npc.Rotation = RotateTowards(
        npc.Rotation,
        npc.Movement.DesiredRotation,
        npc.Movement.TurnSpeed * dt);
}
```

**Why this matters**

**Rotation affects:**

- believable movement
- visual smoothness
- action preparation
- debug clarity

You may also require minimum facing alignment before advancing forward.

### 21.6 Forward Movement Execution

After rotation is acceptable, the entity advances toward the current target.

```csharp
void UpdateTranslation(NPCState npc, float dt)
{
    npc.Position += npc.Movement.DesiredDirection * npc.Movement.MoveSpeed * dt;
}
```

Movement should be deterministic-friendly and based on tick delta, not frame delta.

### 21.7 Movement Tick Progression

**Typical movement progression:**

- Tick 100: set target tile
- Tick 101: rotate toward target
- Tick 102: keep rotating
- Tick 103: begin moving
- Tick 104: advance position
- Tick 105: advance position
- Tick 106: cross tile threshold
- Tick 107: update TileCoord
- Tick 108: target next tile

This explicit progression is very important for debugging and state visibility.

### 21.8 Tile Threshold Crossing

The simulation must explicitly detect when the entity has entered a new tile.

**Example:**

- continuous position crosses tile boundary
- update TileCoord
- emit EnteredTile event
- refresh occupancy index
- update movement target if needed

```csharp
if (HasEnteredNewTile(npc.Position, npc.Tile))
{
    npc.Tile = ComputeTile(npc.Position);
    EmitEnteredTileEvent(npc.Id, npc.Tile);
}
```

### 21.9 Final Junction Approach

When the destination is an interaction junction, movement walks the junction path to the final node.

**Stages:**

- follow junction path step-by-step
- arrive at final junction (WorldPosition)
- align rotation if needed
- mark movement complete

NPC walks junction-to-junction using WorldPosition coordinates. Tile transitions are detected when the current junction belongs to a different tile than the previous one.

### 21.10 Arrival Rules

The movement system must define clear arrival conditions.

**Examples:**

- distance to tile center below threshold
- distance to point below threshold
- rotation alignment within angle threshold

```csharp
bool HasArrived(Vector2 pos, Vector2 target, float epsilon)
```

Arrival should never be fuzzy or hidden. It must be explicit and debuggable.

### 21.11 Path Consumption

Movement consumes the path step by step.

```csharp
if (ReachedCurrentPathNode())
{
    movement.PathIndex++;
}
```

**Rules:**

- do not rebuild path every tick
- keep using current path while valid

rebuild only when invalidated or replanning is required

### 21.12 Stopping Rules

Movement may stop for several reasons.

- Normal Stop
- destination reached
- Temporary Stop
- blocked by occupied point/tile
- waiting for target availability
- waiting for interaction window
- Forced Stop
- goal changed
- plan invalidated
- path invalidated
- external interruption
  The stop reason should always be explicit.

### 21.13 Blocking and Waiting

**The movement system must distinguish between:**

- Blocked

Cannot continue because space/path is invalid.
Waiting
Can continue later, but currently pausing.
This is important because the AI may react differently.

**Examples:**

- another NPC occupies the final point → waiting

the corridor is fully invalid or the route is gone → blocked

### 21.14 Dynamic Repathing

Movement should not constantly recalculate paths.

**Repath triggers may include:**

- current tile edge becomes blocked
- reserved target lost
- path marked stale
- destination changes

**Recommended policy:**

- mark path invalid

```csharp
continue current tick safely
```

request replan/repath on the next allowed phase
This avoids chaotic same-tick loops.

### 21.15 Occupancy Updates During Movement

Movement must cooperate with the occupancy system.

**At minimum:**

current tile membership must update as entity crosses boundaries
current point reservation must remain valid while approaching interaction
stale occupancy must never remain behind the entity
This is critical for perception, avoidance, and debugging.

### 21.16 Local Avoidance (v1 vs Later)

For v1, avoid overcomplicating movement.

**Recommended v1:**

- tile/path blocking
- reservation-based conflict prevention

simple waiting when another entity occupies critical point

**Avoid in v1:**

- advanced steering
- continuous collision avoidance
- group formation logic

These can come later.

### 21.17 Facing Requirements Before Interaction

Some interactions should require approximate facing alignment.

**Examples:**

- sit down
- talk
- use shower
- eat from object

**So movement completion may require both:**

- near target point
- facing target direction

This makes execution cleaner and animations easier.

### 21.18 Movement Events

Movement should emit structured events.

**Examples:**

- MovementStarted
- EnteredTile
- ReachedTile
- ReachedJunction
- MovementBlocked
- MovementWaiting
- MovementStopped
- MovementCompleted

**These are useful for:**

- execution system
- debug trace
- Unity presentation layer

### 21.19 Minimal Movement System Loop

**Example architecture:**

```csharp
void RunMovement(WorldState world)
{
    foreach (var npc in world.Entities.Npcs)
    {
        if (!npc.Movement.IsMoving)
            continue;
        UpdateRotation(npc, world.TickDuration);
        if (CanAdvanceForward(npc))
```

            UpdateTranslation(npc, world.TickDuration);
    
        UpdateTileTransition(npc);
        UpdateArrivalState(npc);
        UpdateMovementStatus(npc);

```csharp
    }
}
```

This is intentionally simple and suitable for v1.

### 21.20 Debug Requirements

**Movement debug should answer:**

- where is the entity now?

- what tile/path node is current?

- what point is the final target?

- is it rotating, moving, waiting, blocked, or arrived?

- why did it stop?

- when did it enter the current state?

**Recommended displayed fields:**

- current tile
- current point target
- path index / path length
- movement status
- desired rotation
- arrival threshold state

### 21.21 Minimal v1 Scope

**For the first implementation:**

- one simple tile path
- continuous movement inside world model
- smooth rotation
- tile crossing detection
- final point alignment
- waiting/blocking states
- no complex local steering

This is enough to make NPC movement feel alive while staying debuggable.

### 21.22 Design Rules

Movement is executed by the simulation, never by Unity

- Rotation and translation progress over ticks
- Discrete planning and continuous execution must stay separate
- Arrival conditions must be explicit
- Blocking and waiting are different states
- Repathing must be event-driven, not constant
- Occupancy updates must remain synchronized with movement
- Debug visibility for movement state is mandatory

### 21.23 Summary

**Movement System answers:**

How does an entity physically progress toward its destination over time, in a way that is tick-driven, precise, and debuggable?
It is the bridge between abstract navigation and actual embodied motion inside the simulation.

## 22. Perception System (Deep)

This section defines how NPCs perceive the world and construct a filtered, internal snapshot used by decision-making.

**Core idea:**

NPC does not operate on raw world state. NPC operates on a perception snapshot.

### 22.1 Perception Pipeline

```
WorldState → Spatial Query → Filtering → Perception Snapshot → Decision System
```

Perception is a data transformation, not a direct reference.

### 22.2 Perception Snapshot Structure

```csharp
class PerceptionSnapshot
{
    public SelfState Self;

    public List<PerceivedObject> Objects;
    public List<PerceivedAgent> Agents;

    public PerceivedEnvironment Environment;
}
```

### 22.3 Self Perception

```csharp
class SelfState
{
    public float Hunger;
    public float Energy;
    public float Comfort;
    public float Social;

    public TileCoord Tile;
    public FragmentId Fragment;
}
```

**Important:**

- Self perception is always available
- No filtering applied

### 22.4 Object Perception

```csharp
class PerceivedObject
{
    public ObjectId Id;

    public TileCoord Tile;
    public float Distance;

    public bool IsReachable;
    public bool IsOccupied;

    public List<InteractionType> AvailableInteractions;
}
```

### 22.5 Agent Perception

```csharp
class PerceivedAgent
{
    public EntityId Id;

    public TileCoord Tile;
    public float Distance;

    public bool CanSee;
    public bool CanHear;

    public RelationshipSummary Relationship;
}
```

### 22.6 Environment Perception

```csharp
class PerceivedEnvironment
{
    public float Temperature;
    public bool IsCrowded;
    public bool IsPrivate;

    public int NearbyAgentsCount;
}
```

Temperature must already be part of perception in v1.

**It should influence:**

- discomfort
- action desirability
- future clothing decisions

### 22.7 Spatial Filtering

Perception must be local.
GetTilesInRadius(centerTile, radius)

**Typical rules:**

- only tiles within radius
- only objects in those tiles
- only agents in those tiles

This ensures performance scalability.

### 22.8 Reachability Filtering

Not all perceived objects are usable.

**For each object:**

- check path existence
- check reservation availability

```csharp
bool IsReachable(ObjectId obj)
```

### 22.9 Perception Frequency (Tick Integration)

Perception should NOT update every tick.

**Recommended:**

- update every N ticks (medium layer)
- update immediately on critical events
- Examples of event-triggered refresh:
- target object destroyed
- reservation lost
- entity moved to new tile

### 22.10 Perception Caching

Each NPC stores its last perception snapshot.

```csharp
npc.Perception
```

**Benefits:**

- stable decision inputs
- easier debugging
- avoids recomputation

### 22.11 Known vs Visible vs Remembered

**Important conceptual split:**

- Visible
- Currently in perception snapshot
- Known
- Previously seen but still considered relevant
- Remembered
- Stored in memory system

### 22.12 Perception Invalidation

**Snapshot must be invalidated when:**

- NPC changes tile
- object state changes
- reservation state changes
- environment changes

This can trigger early refresh.

### 22.13 Perception Cost Control

Avoid full-world scans.

**Use:**

- tile-based indexing
- cached entity lists per tile
- local radius queries

### 22.14 Perception Events

**Perception may emit events:**

- NewObjectSeen
- ObjectLost
- AgentEnteredRange
- AgentLeftRange

**Useful for:**

- memory system
- social system

### 22.15 Debug Requirements

**Perception debug must show:**

- visible objects
- visible agents
- reachable vs unreachable
- environment flags

**Important:**

- Show what NPC thinks, not what actually exists.

### 22.16 Minimal v1 Scope

**For first implementation:**

- small radius perception
- objects + agents
- reachable flag
- simple environment (temperature + crowd)

### 22.17 Design Rules

- NPC never uses raw WorldState directly
- Perception is a filtered snapshot
- Perception must be local
- Perception must be cached
- Perception updates on schedule + events
- Reachability is part of perception
- Debug must reflect perception, not reality

### 22.18 Summary

**Perception System answers:**

What does the NPC believe exists around it right now, and what is actually usable?
It is the input layer of all intelligent behavior.

## 23. Decision System (Deep)

This section defines how an NPC turns its current perception, internal needs, memory, and social context into a selected goal.

**Core idea:**

Decision system does not choose animations or direct movements. It chooses intent.
That intent is expressed as a goal, which is later turned into a plan.

### 23.1 Decision Pipeline

Perception Snapshot

- + Needs
- + Memory
- + Social State
- + Environment
- + Player Influence
    → Goal Scoring
    → Goal Arbitration
    → Selected Goal
    Decision is a scoring and arbitration process, not a chain of hardcoded if/else rules.

### 23.2 Goal Model

A goal is a high-level intention.

```csharp
enum GoalType
{
    None,
    Eat,
    Sleep,
    Sit,
    Shower,
    Dress,
    Socialize,
    Observe,
    Follow,
    Idle
}
```

**A goal is not yet:**

- a route
- a target object
- a current animation
- an exact interaction point

Those come later in planning and execution.

### 23.3 Goal Score Structure

Each evaluated goal should produce a structured score result.

```csharp
class GoalScore
{
    public GoalType Goal;

    public float BaseScore;
    public float NeedModifier;
    public float MemoryModifier;
    public float SocialModifier;
    public float EnvironmentModifier;
    public float CommandModifier;

    public float FinalScore;
}
```

This is very important for debugging and explainability.

### 23.4 Inputs to Goal Scoring

Decision should combine multiple dimensions.

**Needs:**

- high hunger increases Eat
- low energy increases Sleep
- low comfort may increase Sit or Shower
- high social need may increase Socialize
- thermal discomfort increases temperature-regulating actions

**Perception:**

- no visible reachable food lowers Eat
- no reachable bed lowers Sleep
- no reachable clothing lowers Dress
- no nearby agent lowers Socialize

**Memory:**

- recently failed shower attempt reduces Shower
- recently successful social interaction boosts Socialize slightly

**Social State:**

- embarrassment lowers Shower if nearby watchers exist
- trust raises response to player-influenced goals

**Environment:**

- high heat raises desire for cool-down actions
- low temperature raises desire for warmth or clothing
- crowding lowers private actions

**Player Influence:**

- command adds weight to a compatible goal
- command may be ignored if urgency is much higher elsewhere

### 23.5 Example Scoring Formula

A simple v1 formula can be additive.

```
FinalScore = BaseScore
           + NeedModifier
           + MemoryModifier
           + SocialModifier
           + EnvironmentModifier
           + CommandModifier
```

This is intentionally simple and debuggable.
Later, it can become more sophisticated if needed.

### 23.6 Example Goal Evaluation

GoalScore EvaluateEat(NPCState npc)

```csharp
{
    var score = new GoalScore();
    score.Goal = GoalType.Eat;

    score.BaseScore = 0.1f;
    score.NeedModifier = npc.Needs.Hunger;
    score.MemoryModifier = npc.Memory.GetRecentModifier(GoalType.Eat);
    score.SocialModifier = 0f;
    score.EnvironmentModifier = 0f;
    score.CommandModifier = npc.Mind.GetCommandBoost(GoalType.Eat);

    if (!npc.Perception.Objects.Any(o => o.AvailableInteractions.Contains(InteractionType.Eat) && o.IsReachable))
        score.FinalScore = 0f;
    else
        score.FinalScore = score.BaseScore
                         + score.NeedModifier
                         + score.MemoryModifier
                         + score.SocialModifier
                         + score.EnvironmentModifier
                         + score.CommandModifier;
    return score;
}
```

**Important point:**

- perception gates possibility
- needs and modifiers shape priority

### 23.7 Goal Arbitration

Once all candidate goals are scored, the system must choose one.
GoalType SelectGoal(List<GoalScore> scores)

```csharp
{
    return scores.OrderByDescending(x => x.FinalScore).First().Goal;
}
```

However, in practice arbitration must include stabilization rules.

### 23.8 Hysteresis and Stability

Without stabilization, utility AI may jitter constantly.

**Typical problems:**

Eat wins by 0.01, then Sleep wins by 0.02 next tick, then Eat again
goal flips too often
NPC appears indecisive or broken

**Use:**

- goal lock duration
- minimum delta to switch goals
- cooldown before replanning again

**Example rules:**

keep goal for at least 5 ticks unless interrupted
require new goal to exceed current goal by threshold Δ

### 23.9 Goal Locks

```csharp
class GoalLock
{
    public GoalType Goal;
    public int StartTick;
    public int EndTick;
}
```

Goal lock is not absolute. It may be broken by higher-priority emergency conditions.

**Examples:**

- current goal = Sit
- hunger suddenly becomes critical → Eat may interrupt

### 23.10 Cooldowns

Some goals should not be re-entered immediately after failure or refusal.

**Examples:**

- failed Shower → short cooldown
- refused Socialize → cooldown before retry
- failed path → cooldown before retrying same target

```csharp
class GoalCooldown
{
    public GoalType Goal;
    public int EndTick;
}
```

This helps prevent repeated spammy attempts.

### 23.11 Hard Gating vs Soft Scoring

**Important distinction:**

**Hard Gating** — Goal is impossible right now.

- no reachable food
- no visible target NPC
- no free interaction point

In this case, final score should be 0 or goal excluded entirely.

**Soft Scoring** — Goal is possible, but less attractive.

- food is far away
- shower is socially awkward
- social interaction is mildly inconvenient

This distinction keeps decision behavior sane.

### 23.12 Player Influence on Decision

Player commands should not directly replace the AI.
They should enter scoring as modifiers.

```csharp
float GetCommandBoost(GoalType goal)
```

**Examples:**

- player says “go eat” → Eat gets a boost
- if hunger is already high, Eat likely wins
- if NPC is exhausted or embarrassed or cannot reach food, command may fail

This preserves autonomy while still making commands meaningful.

### 23.13 Social Modifiers in Decision

Social state can strongly reshape decisions.

**Examples:**

- embarrassment lowers Shower when others are nearby
- trust increases willingness to follow player-influenced goals
- affinity increases likelihood of Socialize
- low familiarity reduces some joint actions
  This ensures decision-making is not purely utilitarian in a mechanical sense.

### 23.14 Memory Modifiers in Decision

Memory should alter the attractiveness of repeating actions.

**Examples:**

recently failed Eat target → reduce similar choice for a while
repeated successful Sit → slightly increase comfort trust in similar contexts
social embarrassment event → reduce private actions in exposed spaces
This is how decision becomes adaptive over time.

### 23.15 Environment Modifiers in Decision

Environment is not only background — it changes priorities.

**Examples:**

- hot room increases Shower or cool-down behavior
- crowded room lowers private behaviors
- quiet/private area may increase Socialize or Rest
  This makes the world feel responsive.

### 23.16 Decision Frequency (Tick Integration)

Decision should not run every tick.

**Recommended:**

- run on medium schedule

run immediately on certain strong invalidations/events if needed
Examples of early re-evaluation triggers:

- path invalidated
- reservation lost
- goal completed
- target became occupied
- player command received
  Still, avoid uncontrolled same-tick oscillation.

### 23.17 Interrupt Rules

Not every new higher score should instantly interrupt the current goal.

**Interrupt only if:**

- current goal completed
- current plan invalidated

```csharp
new score exceeds current one by threshold
```

emergency need appears

**Example:**

- current goal = Sit
- hunger becomes critical → interrupt allowed
- small score change from Sit to Observe → no interrupt

### 23.18 Decision Output

The output of the decision system should be explicit.

```csharp
class DecisionResult
{
    public GoalType SelectedGoal;
    public List<GoalScore> Scores;
    public string Reason;
}
```

**This is useful for:**

- planning handoff
- debug UI
- trace logs

### 23.19 Decision Events

**Decision system may emit:**

- GoalSelected
- GoalChanged
- GoalInterrupted
- GoalRejected
- NoValidGoal

This makes decision transitions observable.

### 23.20 No Valid Goal Fallback

Sometimes every meaningful goal is gated out.
The system should handle this explicitly.

**Possible fallback goals:**

- Idle
- Observe
- Wait
- MoveToNeutralJunction

This avoids undefined states.

### 23.21 Minimal v1 Goal Set

For the first slice, keep goal vocabulary small.

**Recommended v1:**

- Eat
- Sit
- Sleep
- Idle

**Optional if ready:**

- Shower
- Socialize

This is enough to validate the architecture without overcomplicating content.

### 23.22 Debug Requirements

**Decision debug must show:**

- all candidate goals
- score breakdown per goal
- final winner
- lock/cooldown state
- current interruptibility
- last decision reason

**Example table:**

- Goal       Base  Need  Memory  Social  Env  Cmd  Final
- Eat        0.1   0.8   0.0     0.0     0.0  0.2  1.1
- Sleep      0.1   0.3   0.0     0.0     0.0  0.0  0.4
- Shower     0.1   0.4  -0.2    -0.5     0.0  0.0 -0.2
- Idle       0.0   0.0   0.0     0.0     0.0  0.0  0.0
  This must be inspectable for a selected NPC.

### 23.23 Minimal v1 Scope

**For the first implementation:**

- additive score model
- small goal set
- hard gating via perception/reachability
- simple lock and cooldown rules
- player command as score boost
- explicit DecisionResult output

That is enough to prove the full pipeline.

### 23.24 Design Rules

- Decision selects goals, not animations or direct movement
- Goals must be scored structurally, not implicitly
- Hard gating and soft scoring must remain separate
- Player influence modifies scores, not autonomy directly
  Decision must be stabilized with locks, thresholds, and cooldowns
  Decision frequency must be tick-controlled
  Every selected goal should be explainable in debug output

### 23.25 Summary

**Decision System answers:**

Given what the NPC currently perceives and remembers, what does it want to do now?
It is the core layer that turns simulation state into intention.

## 24. Planning System (Deep)

This section defines how a selected goal is transformed into a concrete, executable sequence of steps.

**Core idea:**

Decision chooses WHAT to do. Planning determines HOW to do it.

### 24.1 Planning Pipeline

```
Selected Goal → Target Selection → Plan Construction → Reservation → Validation → Plan Ready
```

### 24.2 Plan Structure

```csharp
class NPCPlanState
{
    public GoalType Goal;

    public List<PlanStep> Steps;
    public int CurrentStepIndex;

    public PlanStatus Status;
}

class PlanStep
{
    public PlanStepType Type;

    public TileCoord? TargetTile;
    public JunctionId? TargetJunction;
    public ObjectId? TargetObject;

    public int? TimeoutEndTick;
}
```

```csharp
enum PlanStepType
{
    MoveToTile,
    MoveToJunction,
    Interact,
    Wait,
}

enum PlanStatus
{
    None,
    Active,
    Completed,
    Failed,
    Invalid
}
```

### 24.3 Target Selection

Planner must select a concrete target.

**Example for Eat:**

- scan perceived objects
- filter reachable
- sort by distance / availability
- pick best candidate
- ObjectId SelectFoodTarget(NPCState npc)

### 24.4 Plan Construction Example

Goal: Eat

## 1. MoveToTile (food tile)

## 2. MoveToJunction (food access point)

## 3. Interact (eat)

### 24.5 Reservation Integration

Before committing to a plan, required points must be reserved.

```csharp
bool TryReserve(JunctionId point, EntityId npc)
If reservation fails:
try another target
```

or fail planning

### 24.6 Plan Validation

Plan must be validated before execution.

**Checks:**

- path exists
- target still valid
- reservation successful
- If invalid → planner retries or returns failure.

### 24.7 Plan Execution Flow

```
Plan Active → Execute Step 0 → Step Completed → Execute Step 1 → ... → Plan Completed
```

### 24.8 Step Completion Rules

Each step must define completion condition.

**Examples:**

- MoveToTile → reached tile
- MoveToJunction → reached point
- Interact → execution finished

### 24.9 Plan Failure

**Plan may fail due to:**

- target lost
- path invalid
- reservation lost
- timeout

**On failure:**

- mark plan invalid
- trigger replan

### 24.10 Replanning Strategy

Replan should not happen every tick.

**Triggers:**

- plan invalid
- goal changed
- target unavailable

**Policy:**

- delay replanning by cooldown
- avoid loops

### 24.11 Timeouts

Each step may have timeout.

```csharp
if (currentTick > TimeoutEndTick)
```

    step fails

### 24.12 Minimal v1 Planner

**Keep planner simple:**

- no GOAP initially
- direct mapping goal → steps
- basic target selection
- basic validation

### 24.13 Debug Requirements

**Planner debug must show:**

- current goal
- selected target
- steps
- current step index
- failure reason

### 24.14 Design Rules

- Plan is a sequence of explicit steps
- Each step must be independently validatable
- Planner must not assume success
- Reservation must happen before execution
- Replanning must be controlled, not constant

### 24.15 Summary

**Planning System answers:**

Given a goal, how exactly will the NPC achieve it step by step?
It transforms intention into an executable structure.

## 25. Pathfinding System (Deep)

This section defines how the simulation finds routes through the hierarchical spatial model.

**Core idea:**

Planning chooses the destination. Pathfinding computes the route. Movement executes the route over ticks.

### 25.1 Pathfinding Layers

**Pathfinding should be hierarchical and match the spatial model:**

- Fragment Graph
- → Tile Graph
- → Final Junction Target
- Fragment Layer

Used for large-scale routing between world regions.
Tile Layer
Used for actual local navigation inside and across fragments.
Junction Layer
Used only for final interaction anchoring, not for full v1 navigation.

### 25.2 Path Request Model

Pathfinding should use explicit requests.

```csharp
class PathRequest
{
    public FragmentId FromFragment;
    public TileCoord FromTile;

    public FragmentId ToFragment;
    public TileCoord ToTile;

    public JunctionId? FinalJunction;
}
```

This makes path generation debuggable and decoupled from movement.

### 25.3 Path Result Model

```csharp
class PathResult
{
    public bool IsValid;

    public List<FragmentId> FragmentPath;
    public List<TileCoord> TilePath;

    public JunctionId? FinalJunction;

    public PathFailureReason FailureReason;
}
enum PathFailureReason
{
    None,
    NoFragmentRoute,
    NoTileRoute,
    FinalJunctionBlocked,
    InvalidTarget,
}
```

### 25.4 High-Level Fragment Routing

When source and destination are in different fragments, use the fragment graph first.

```csharp
List<FragmentId> FindFragmentRoute(FragmentId from, FragmentId to)
```

**This gives:**

- scalability
- cleaner long-distance planning
- simpler debugging

Even if v1 starts with one fragment, this structure should exist from the start.

### 25.5 Tile Pathfinding

Tile pathfinding computes the actual navigable route.

```csharp
List<TileCoord> FindTileRoute(TileCoord from, TileCoord to)
```

**Recommended v1:**

- A*
- 4-neighbor grid
- explicit tile walkability checks

**Inputs:**

- walkable flags
- dynamic blocking
- fragment connectors
- optional path cost modifiers

### 25.6 Final Junction Resolution

Pathfinding may end at a tile, but interaction requires a point.

**Flow:**

- plan selects target object
- planner resolves required interaction point
- path result stores final point

movement refines from tile arrival to point arrival

**Important rule:**

- Point resolution is part of routing-to-interaction, not generic tile traversal.

### 25.7 Path Cost Model

For v1, keep path cost simple and explicit.

**Possible cost factors:**

- tile walkability
- blocked / reserved penalty
- door or connector transitions
- future extension: heat, crowding, danger

```csharp
float GetTileCost(TileCoord tile)
```

For the first slice, uniform cost is acceptable.

### 25.8 Reachability vs Path Existence

Reachability should be derived from pathfinding, but conceptually stay separate.
Path Exists
A route can be computed.
Reachable Now
A route exists and is currently usable for this entity in this context.

**Examples:**

path exists but final point is reserved → not reachable now
path exists but target object became invalid → not reachable now
This distinction is important for perception and decision systems.

### 25.9 Path Caching

Pathfinding should not recompute everything all the time.
Each moving NPC may keep a cached path.

```csharp
class CachedPath
{
    public PathResult Result;
    public int BuiltAtTick;
    public bool IsStale;
}
```

**Benefits:**

- lower cost
- easier debugging
- stable movement execution

### 25.10 Path Invalidation

A path may become stale or invalid during execution.

**Common causes:**

- target point reserved by someone else
- tile blocked dynamically
- target moved or disappeared
- plan changed
- fragment connection changed

**When invalidated:**

- mark path stale/invalid
- emit PathInvalidated
- request controlled replan/repath

**Important rule:**

- Never silently keep following a path that is known invalid.

### 25.11 Repath Policy

Repathing must be controlled.

**Bad behavior:**

- rebuild every tick
- instant same-tick loops

repeated path spam to same blocked target

**Recommended policy:**

- reuse valid cached path

rebuild only on explicit invalidation or destination change
optionally apply short repath cooldown
This keeps the system stable and cheap.

### 25.12 Dynamic Blocking

The pathfinder must account for dynamic conditions.

**Examples:**

- occupied narrow tile
- blocked door
- reserved final point
- temporary obstacle NPC

For v1, avoid overly smart local detours.

**Recommended v1:**

- tile blocked → repath or wait
- final point occupied → wait or replan target

### 25.13 Same-Fragment vs Cross-Fragment Pathing

**Two common cases:**

- Same Fragment
- direct tile A*
- simplest path case
- Cross Fragment
- fragment route first
- then tile path across connectors

This should be explicit in code and debug output.

### 25.14 Path Query Interfaces

Pathfinding should expose clean query functions.
PathResult BuildPath(PathRequest request);

```csharp
bool HasValidPath(EntityId entity);
bool IsPathStale(EntityId entity);
```

Avoid letting random systems manipulate tile lists directly.

### 25.15 Planner Integration

Planner should not own pathfinding internals.

**Typical relationship:**

- planner selects target tile/point/object
- pathfinder computes route
- planner validates path result
- movement consumes path result

This separation is very important for maintainability.

### 25.16 Path and Reservation Order

When both routing and reservation are needed, the order matters.

**Typical flow:**

- planner selects target
- planner resolves final point
- planner attempts reservation
- planner requests path to reserved destination

**Reason:**

path to an already-lost point is useless
reservation first reduces wasted work
However, in some cases approximate path cost may be used before reservation for ranking alternatives. That is an optimization for later.

### 25.17 Failure Handling

Pathfinding must fail explicitly, not silently.

**Examples:**

- no route to target
- connector missing
- destination invalid
- final point unavailable

**Failure result should contain:**

- reason
- target info
- tick info in logs

This is essential for debugging and planner fallback.

### 25.18 Path Events

Pathfinding should emit useful structured events.

**Examples:**

- PathBuilt
- PathInvalidated
- PathFailed
- RepathRequested
- RepathSucceeded

**These events support:**

- debug trace
- movement system
- planner feedback

### 25.19 Debug Requirements

**Path debug must show:**

- source fragment/tile
- destination fragment/tile/point
- current cached path
- current node index
- path status (valid / stale / failed)
- last invalidation reason
- whether destination is reserved/blocked

**Recommended visual debug:**

- fragment route highlight
- tile route overlay
- final point marker

### 25.20 Minimal v1 Scope

**For the first implementation:**

- one fragment supported first, multi-fragment structure kept
- A* tile pathfinding
- 4-neighbor movement
- final point refinement for interactions
- cached path on NPC
- path invalidation + repath trigger

**Avoid in v1:**

- full point-graph navigation
- complex dynamic steering
- predictive crowd routing

### 25.21 Design Rules

- Pathfinding is hierarchical: fragment → tile → point
- Pathfinding computes routes, movement executes them
- Path validity must be explicit and observable
- Cached paths should be reused until invalidated
- Repathing must be controlled, not continuous
- Reachability is not identical to mere path existence
- Final interaction precision happens at point level
- Path debug visibility is mandatory

### 25.22 Summary

**Pathfinding System answers:**

Given a destination, what valid route exists through the world right now, and is it still safe to use?
It is the routing backbone that connects planning with movement.

## 26. Execution System / Action Runtime

This section defines how a planned action is actually executed over time inside the simulation.

**Core idea:**

Planning creates an intention as steps. Execution turns the current step into a time-progressed runtime process.
Execution is the layer where "Interact", "Wait", and other active steps are advanced tick by tick, validated, completed, or failed.

### 26.1 Execution Responsibilities

**Execution system is responsible for:**

- starting the current plan step
- validating runtime preconditions
- progressing action state over ticks
- applying effects at the right moment
- completing or failing the step
- emitting execution events

**Execution system is NOT responsible for:**

- choosing goals
- building plans
- computing paths
- rendering animations

### 26.2 Execution State Model

Each NPC should have explicit runtime execution state.

```csharp
class NPCExecutionState
{
    public ExecutionStatus Status;

    public PlanStepType? ActiveStepType;
    public ObjectId? TargetObject;
    public JunctionId? TargetJunction;

    public int StartTick;
    public int? EndTick;
    public int ProgressTicks;

    public ExecutionFailureReason FailureReason;
}
enum ExecutionStatus
{
    None,
    Starting,
    Active,
    Waiting,
    Completed,
    Failed,
    Interrupted
}
```

```csharp
enum ExecutionFailureReason
{
    None,
    PreconditionsFailed,
    TargetLost,
    ReservationLost,
    Timeout,
    InterruptedByGoalChange,
    InvalidStep,
    Unknown
}
```

This state should be directly inspectable in debug.

### 26.3 Step Types and Execution Meaning

Execution semantics depend on the plan step type.
MoveToTile
Handled mostly by movement system, but execution observes completion/failure.
MoveToJunction
Handled by movement system, but execution monitors final arrival.
Interact
Handled by execution as an active runtime process.
Wait
Handled by execution through time-based pause or condition-based pause.
This means execution coordinates with movement, but does not replace it.

### 26.4 Step Lifecycle

Every executable step should have a clear lifecycle.

```
Step Pending → Starting → Active → Completed
                                  → Failed
                                  → Interrupted
```

This should be explicit and traceable.

### 26.5 Starting a Step

When the current plan step becomes active, execution must initialize runtime state.

**Example for Interact:**

- verify target object still exists

- verify target point still reserved

- verify NPC is in correct position/facing range

- initialize StartTick

- set status = Starting → Active
  If any hard precondition fails, execution should fail immediately and notify planner.

### 26.6 Runtime Preconditions

Some conditions are checked before execution starts. Others must remain valid during execution.

**Examples of runtime preconditions:**

- target object still valid
- reservation still owned by actor
- actor still at required point
- facing alignment acceptable
- environment/social condition still acceptable if required

**Important rule:**

- Preconditions are not only a planning-time concern. They may fail at runtime as the world changes.

### 26.7 Tick-Based Action Progress

Long-running actions should progress through ticks.

```csharp
void UpdateExecution(NPCState npc)
{
    npc.Exec.ProgressTicks++;
}
```

**Example:**

- Tick 300: start Eat
- Tick 301: Eat active
- Tick 302: Eat active
- Tick 303: Eat active
- Tick 304: Eat completes

This makes actions measurable, interruptible, and debuggable.

### 26.8 Instant vs Timed Actions

Execution should support both.
Instant Action
Effect applied immediately after preconditions pass.

**Examples:**

- quick Observe
- instant state toggle
- Timed Action

Action runs over several ticks before effects are finalized.

**Examples:**

- Eat
- Sleep
- Shower
- Sit for a duration

Timed actions are more useful for this simulation.

### 26.9 Action Duration Model

Use tick-based durations, not frame-based time.

```csharp
class ActionDuration
{
    public int RequiredTicks;
}
```

**Example:**

- Eat = 5 ticks
- Sit = 10 ticks
- Shower = 8 ticks

This works naturally with replay, debugging, and save/load.

### 26.10 Applying Effects

Effects should be applied in a controlled way.

**Two useful models:**

- End-Apply

Apply effect when action completes.

**Examples:**

full hunger reduction at end of Eat
Progressive Apply
Apply smaller effects during execution.

**Examples:**

- comfort increases gradually while sitting
- temperature relief gradually while showering

For v1, end-apply is simpler and easier to debug.

### 26.11 Execution Completion

A step completes when its success condition is satisfied.

**Examples:**

- Wait: required ticks elapsed

Interact: duration complete and target still valid
MoveToJunction: arrival reported by movement system

**On completion:**

- apply final effects if needed
- release any temporary execution state
- mark step completed

advance plan to next step or finish plan

### 26.12 Execution Failure

Execution may fail after it has already started.

**Common failure causes:**

- target object disappears

- target becomes invalid

- reservation lost

- actor pushed away or no longer aligned

- action exceeded timeout

- plan interrupted by stronger goal
  Execution must fail explicitly, not silently.

### 26.13 Execution Interruptions

Sometimes execution should stop even if it was progressing correctly.

**Examples:**

- emergency goal interrupts current action
- player influence or script cancels action
- social condition becomes impossible
- object enters broken/invalid state

**Interruption is different from failure:**

- failure = could not continue successfully

interruption = intentionally aborted due to higher-level change
This distinction matters for AI behavior and debugging.

### 26.14 Wait State

Execution system should support waiting as a first-class state.

**Use cases:**

- waiting for point to become free
- waiting for joint action partner
- waiting due to temporary social/environment condition

```csharp
class WaitCondition
{
    public int? EndTick;
    public string Reason;
}
```

This prevents fake failures when a short pause is appropriate.

### 26.15 Timeouts

Every long-running step should be able to fail on timeout.

```csharp
if (currentTick > step.TimeoutEndTick)
```

    FailExecution(ExecutionFailureReason.Timeout);

**Examples:**

- movement step took too long
- interaction never completed
- wait condition lasted too long

Timeouts are essential for stuck-state recovery.

### 26.16 Reservation During Execution

Execution must continuously respect reservation ownership.

**Examples:**

```csharp
if NPC reserved shower access point, it must still own it when shower starts
if reservation is lost during execution, action may fail or be interrupted
```

This ensures spatial consistency.

### 26.17 Interaction Contracts

Execution should work against generic interaction contracts, not hardcoded object subclasses.

**Example runtime request:**

```csharp
class InteractionExecutionRequest
{
    public EntityId Actor;
    public ObjectId TargetObject;
    public JunctionId TargetJunction;
    public string InteractionId;
}
```

This allows the execution layer to remain reusable across many content types.

### 26.18 Coordination with Movement

Execution and movement must cooperate, but stay separate.

**Typical flow:**

- planner step = MoveToJunction
- movement system reaches point
- execution observes arrival → marks movement step complete
- planner advances to Interact
- execution starts interaction runtime

This keeps responsibilities clean.

### 26.19 Coordination with Social / Joint Actions

Some actions may require synchronized execution with another actor.

**Examples:**

- TalkTogether
- SitTogether
- EatTogether

For v1, keep this minimal.

**Recommended v1 behavior:**

- one actor waits until partner ready

both actions start only when both preconditions hold
This can be expanded later.

### 26.20 Execution Events

Execution should emit structured events.

**Examples:**

- StepStarted
- StepCompleted
- StepFailed
- StepInterrupted
- InteractionStarted
- InteractionCompleted
- WaitStarted
- WaitEnded

**These are important for:**

- planner feedback
- memory system
- social updates
- debug trace
- Unity presentation

### 26.21 Side Effects and Event Ordering

Execution side effects should happen in a predictable order.

**Recommended order on successful completion:**

- validate completion
- apply state effects
- emit execution completion event
- release reservations if appropriate
- mark plan step complete

The order should be explicit in code and docs.

### 26.22 Debug Requirements

**Execution debug must show:**

- active step type
- execution status
- start tick
- progress ticks
- target object / point
- timeout deadline
- failure/interruption reason
- whether effects already applied

**This is necessary to answer:**

- what is the NPC doing right now?
- how long has it been doing it?
- why did it stop?

### 26.23 Minimal v1 Scope

**For the first implementation:**

- support MoveToTile / MoveToJunction completion tracking
- support Interact with fixed tick duration
- support Wait with timeout
- support explicit failure/interruption reasons
- support end-apply effects
- support execution events

**Avoid in v1:**

- highly complex synchronized multi-actor actions
- multi-phase interaction graphs
- rich animation-driven logic

### 26.24 Design Rules

- Execution progresses over ticks, not frames
- Runtime preconditions must be validated continuously where needed
- Failure and interruption are different states
- Effects must be applied in a predictable order
- Execution should work with generic interaction contracts
- Movement and execution cooperate but remain separate systems
- Timeouts are mandatory for long-running actions
- Execution state must be fully visible in debug

### 26.25 Summary

**Execution System answers:**

The NPC has a plan step. How does that step actually unfold over time, succeed, fail, or get interrupted inside the simulation?
It is the runtime layer that turns plan steps into real, time-based behavior.

## 27. Memory & Knowledge System (Deep)

This section defines how NPCs retain, forget, and use past information to influence future decisions.

**Core idea:**

Perception gives the present. Memory gives continuity over time.
Without memory, NPCs behave reactively. With memory, they behave coherently.

### 27.1 Memory Layers

Memory should be structured into layers.

- Working Memory
- Short-Term Memory
- Long-Term Memory

### 27.2 Working Memory

Temporary, execution-related data.

**Examples:**

- current target object
- current plan context
- last seen target position

**Lifetime:**

- valid only during active plan/execution
- cleared or replaced frequently

### 27.3 Short-Term Memory

Recent events and experiences.

**Examples:**

- recently failed interaction
- recently visited location
- recently seen object
- last interaction with another NPC

**Lifetime:**

- decays over ticks
- influences near-term decisions

### 27.4 Long-Term Memory

Persistent knowledge.

**Examples:**

- known food sources
- known locations
- learned preferences
- stable social relationships

**Lifetime:**

- long duration
- may persist across sessions (save/load)

### 27.5 Memory Record Structure

```csharp
class MemoryRecord
{
    public MemoryType Type;

    public EntityId? RelatedEntity;
    public ObjectId? RelatedObject;
    public TileCoord? Location;

    public float Strength;
    public int CreatedTick;
    public int LastUpdatedTick;
}
enum MemoryType
{
    InteractionSuccess,
    InteractionFailure,
    SeenObject,
    SeenAgent,
    SocialEvent,
    LocationVisited
}
```

### 27.6 Memory Strength

Memory strength represents importance or relevance.

**Rules:**

- higher strength → stronger influence on decision
- strength decays over time
- repeated events reinforce strength

```csharp
Strength -= decayRate per tick
```

### 27.7 Memory Decay (Tick-Based)

Memory decay must be processed in ticks.

```csharp
if (currentTick - record.LastUpdatedTick > threshold)
    record.Strength -= decay
When strength reaches zero:
record may be removed
```

### 27.8 Memory Formation

Memory is created from events.

**Examples:**

- interaction completed → InteractionSuccess
- interaction failed → InteractionFailure
- agent observed → SeenAgent
- object discovered → SeenObject

Execution system and perception system are primary sources of memory events.

### 27.9 Memory Reinforcement

Repeated experiences strengthen memory.

**Example:**

repeatedly eating from same object increases trust/preference

```csharp
record.Strength += reinforcementValue
```

### 27.10 Memory and Decision Integration

Memory modifies goal scores.

**Examples:**

- failed Eat recently → reduce Eat score for that object
- successful Sit → increase Sit attractiveness
- negative social memory → reduce Socialize

Memory should not override reality, but bias it.

### 27.11 Spatial Memory

NPC may remember locations.

**Examples:**

- known food location
- known resting spot

**This enables behavior like:**

go to remembered food even if not currently visible

### 27.12 Social Memory

Memory must include social interactions.

**Examples:**

- embarrassment event
- friendly interaction
- rejection

**These influence:**

- trust
- willingness to interact
- future decision modifiers

### 27.13 Clothing and Temperature Memory

NPC may also accumulate short-term experience related to temperature and clothing.

**Examples:**

- this area feels too hot
- being undressed caused discomfort

a specific clothing item reduced cold discomfort
This can later bias Dress / Undress choices.
For v1, this may remain lightweight.

### 27.14 Memory vs Perception

**Important distinction:**

- Perception
- what NPC currently sees
- Memory
- what NPC remembers
  NPC may act based on memory even if perception lacks current data.

### 27.14 Memory Query Interface

```csharp
IEnumerable<MemoryRecord> GetMemoriesByType(MemoryType type);
```

MemoryRecord GetStrongestMemory(MemoryType type);
These queries should be used by decision system.

### 27.15 Memory Capacity (Optional)

Memory may be limited.

**Policies:**

- max records per type
- drop weakest memories first

For v1, unlimited or simple cap is acceptable.

### 27.16 Memory Events

Memory system reacts to events.

**Examples:**

- OnInteractionSuccess
- OnInteractionFailure
- OnAgentSeen
- OnSocialEvent

This keeps memory consistent with world evolution.

### 27.17 Debug Requirements

**Memory debug must show:**

- active memory records
- type
- strength
- age (ticks)
- related entity/object

**Important:**

- Show why NPC remembers something, not just that it does.

### 27.18 Minimal v1 Scope

**For first implementation:**

- short-term memory
- simple strength + decay
- interaction success/failure memory
- basic influence on decision

Long-term memory can be added later.

### 27.19 Design Rules

- Memory is event-driven, not continuously recomputed
- Memory strength must decay over ticks
- Memory modifies decisions but does not replace perception
- Memory must be structured and queryable
- Memory must be debug-visible
- Memory must integrate with social and decision systems

### 27.20 Summary

**Memory System answers:**

What does the NPC remember about the past, and how does that influence what it does next?
It provides temporal continuity to behavior.

## 28. Social System (Deep)

This section defines how NPCs perceive, evaluate, and react to other agents in a socially meaningful way.

**Core idea:**

NPCs do not act in isolation. Their behavior is influenced by the presence, relationships, and expectations of others.

### 28.1 Social Model Overview

Each NPC maintains a social state relative to other agents.

```csharp
class SocialState
{
    public Dictionary<EntityId, RelationshipData> Relationships;
}
```

### 28.2 Relationship Axes

Relationships should not be a single number.

```csharp
class RelationshipData
{
    public float Trust;
    public float Familiarity;
    public float Affinity;
    public float Authority;
}
```

**Meaning:**

- Trust → willingness to follow or accept influence
- Familiarity → how well agents know each other
- Affinity → liking / emotional positivity
- Authority → perceived hierarchy or obedience

### 28.3 Social Perception

Perception must include social context.

**Each perceived agent should include:**

- distance
- visibility
- relationship summary

```csharp
class RelationshipSummary
{
    public float Trust;
    public float Affinity;
}
```

### 28.4 Presence and Observation

NPC behavior changes depending on who is nearby.

```csharp
class SocialContext
{
    public List<EntityId> NearbyAgents;
    public bool IsObserved;
    public bool IsPrivate;
}
Rules:
if others are present → behavior may change
if alone → more private actions allowed
```

### 28.5 Embarrassment System

Embarrassment is a key modifier.

**Example triggers:**

- showering while others are nearby
- failing socially
- awkward interaction

```csharp
float Embarrassment;
```

**Effects:**

- reduces likelihood of certain actions
- increases avoidance

### 28.6 Social Influence on Decision

Social context modifies goal scores.

**Examples:**

- presence reduces Shower score
- trusted NPC increases Socialize score
- disliked NPC reduces Socialize score

### 28.7 Social Interaction Types

**Basic interactions:**

- Talk
- SitTogether
- Observe
- Follow

These are implemented as interaction types in content system.

### 28.8 Joint Actions

Some actions require multiple participants.

**Example:**

- two NPCs sitting together

**Flow:**

- one NPC initiates
- target NPC evaluates request
- both reserve required points
- both execute action

For v1, keep coordination simple.

### 28.9 Social Acceptance

NPC must decide whether to accept interaction.

**Factors:**

- trust
- affinity
- current goal
- interruption rules

```csharp
bool AcceptInteraction(EntityId requester)
```

### 28.10 Social Memory Integration

Social interactions must update memory.

**Examples:**

- successful interaction → increase affinity
- rejection → decrease affinity
- embarrassment → negative memory

### 28.11 Social Cooldowns

Avoid repetitive behavior.

**Examples:**

- repeated interaction attempts
- repeated rejection

```csharp
class SocialCooldown
{
    public EntityId Target;
    public int EndTick;
}
```

### 28.12 Social Context Updates (Tick Integration)

**Social context should update:**

- periodically (medium ticks)
- on perception changes

### 28.13 Social Events

**Examples:**

- InteractionAccepted
- InteractionRejected
- RelationshipChanged
- EmbarrassmentIncreased

### 28.14 Social Debug

**Must show:**

- relationships
- embarrassment level
- nearby agents
- active social modifiers

### 28.15 Minimal v1 Scope

**For first version:**

- trust + affinity
- presence detection
- embarrassment basic system
- simple talk interaction

### 28.16 Design Rules

- Social state is per-entity pair
- Presence affects behavior
- Relationships evolve via events
- Social modifiers affect decision
- Social system must be observable

### 28.17 Summary

**Social System answers:**

How do other NPCs influence behavior, and how do relationships evolve over time?
It makes the world feel alive and reactive.

## 29. Content & Interaction Authoring System (Deep)

This section defines how world objects expose usable interactions to the simulation in a scalable, data-driven way.

**Core idea:**

Objects do not contain hardcoded AI behavior. Objects publish affordances and interaction definitions. NPC systems decide when and how to use them.
This is the layer that allows the project to grow without rewriting core AI every time a new object is added.

### 29.1 Content Layers

The content system should be separated into clear layers.

```
Object Definition → Interaction Definition → Runtime Object State → Interaction Offer → AI Consumption
```

Object Definition
Static description of an object type.
Interaction Definition
What can be done with this object.
Runtime Object State
Current state of this placed object in the world.
Interaction Offer
Resolved, usable opportunity visible to an NPC.

### 29.2 Object Definition

```csharp
class ObjectDefinition
{
    public string Id;
    public ObjectType Type;

    public List<InteractionDefinition> Interactions;
    public List<ObjectTag> Tags;
}
```

**Examples of object types:**

- Bed
- Chair
- FoodSource
- Shower
- SocialSpot
- Door
- Decoration

ObjectDefinition is static content. It should not contain runtime occupancy or current user.

### 29.3 Interaction Definition

This is the most important authoring layer.

```csharp
class InteractionDefinition
{
    public string Id;
    public InteractionType Type;

    public List<InteractionCondition> Conditions;
    public List<InteractionEffect> Effects;
    public List<JunctionRole> RequiredJunctionRoles;

    public int DurationTicks;
    public float BaseUtility;
}
```

This allows interactions to be authored as data rather than hand-coded object-specific logic.

### 29.4 Interaction Types

```csharp
enum InteractionType
{
    Eat,
    Sit,
    Sleep,
    Shower,
    Observe,
    Talk,
    Follow,
    Dress,
    Undress,
    Wait
}
```

This set should stay small in v1.

### 29.5 Affordances

An affordance is a possibility for action that an object offers.

**Examples:**

- Bed affords Sleep and LieDown
- Chair affords Sit and Observe
- FoodSource affords Eat
- Shower affords Wash / Shower
- SocialSpot affords Talk or SitTogether

**Important rule:**

- Objects publish possibilities. NPCs choose them.
- That rule keeps architecture clean.

### 29.6 Interaction Conditions

Interactions must have explicit conditions.

```csharp
class InteractionCondition
{
    public InteractionConditionType Type;
    public float Value;
}
enum InteractionConditionType
{
    RequiresObjectFree,
    RequiresReachableJunction,
    MinTrust,
    MaxEmbarrassment,
    RequiresPrivacy,
    RequiresVisibleTarget,
    RequiresReservation,
}
```

**Examples:**

- Shower requires privacy or low enough embarrassment
- Eat requires object not empty and point reachable
- Talk requires visible target agent

### 29.7 Interaction Effects

Interactions must define their outcomes.

```csharp
class InteractionEffect
{
    public EffectTargetType Target;
    public EffectType Type;
    public float Value;
}
enum EffectType
{
    HungerDelta,
    EnergyDelta,
    ComfortDelta,
    SocialDelta,
    EmbarrassmentDelta,
    TrustDelta,
    TemperatureComfortDelta,
}
```

**Examples:**

- Eat → HungerDelta -40
- Sit → ComfortDelta +10
- Shower → ComfortDelta +20, TemperatureComfortDelta +15
- Talk → SocialDelta +15
- Dress → TemperatureComfortDelta +X depending on clothing

### 29.8 Duration and Timing

Interactions must be compatible with the tick system.

**Use:**

- DurationTicks
- step-based execution
- deterministic end-apply or progressive-apply effects

**Important rule:**

- Interaction timing must be authored in ticks, not in animation seconds.
- Unity animation can follow the simulation, but must not control it.

### 29.9 Junction Anchoring and Spatial Anchors

Interactions must declare what kind of spatial anchor they require.

```csharp
enum JunctionRole
{
    Access,
    Sit,
    Sleep,
    Observe,
    SocialA,
    SocialB
}
```

**Examples:**

- Eat requires Access
- Sit requires Sit
- Sleep requires Sleep
- TalkTogether may require SocialA and SocialB

This is how content integrates directly with the spatial system.

### 29.10 Runtime Object State

Runtime state belongs to WorldState, not to ObjectDefinition.

```csharp
class WorldObjectState
{
    public ObjectId Id;
    public string DefinitionId;

    public FragmentId Fragment;
    public TileCoord Tile;
    public List<JunctionId> Points;

    public bool IsOccupied;
    public EntityId? CurrentUser;

    public float ResourceAmount;
}
```

Examples of runtime-only data:

- occupied/free
- remaining food amount
- cleanliness
- broken state
- temporary lock state

### 29.11 Interaction Offers

AI should reason about runtime-resolved offers rather than raw object definitions.

```csharp
class InteractionOffer
{
    public ObjectId ObjectId;
    public string InteractionId;

    public JunctionId TargetJunction;
    public bool IsReachable;
    public bool IsValidNow;

    public float EstimatedUtility;
}
```

This offer is what Perception and Planning can consume.

### 29.12 Offer Resolution Pipeline

WorldObjectState

- + ObjectDefinition
- + InteractionDefinition
- + Current Context
    → InteractionOffer
    Examples of context-sensitive resolution:
- object exists
- required point exists
- object is not occupied
- point is free/reserved appropriately
- social/privacy conditions compatible
  This lets AI operate on already filtered possibilities.

### 29.13 Generic Interaction Contract

Execution should work against a generic interaction contract.

```csharp
class InteractionExecutionRequest
{
    public EntityId Actor;
    public ObjectId TargetObject;
    public JunctionId TargetJunction;
    public string InteractionId;
}
```

This avoids object-specific branching inside execution code.

### 29.14 Social Interactions as Content

Social actions should also be content-driven where possible.

**Examples:**

- bench object offers SitTogether
- table offers Talk
- social spot offers Observe and Talk

This is powerful because the same interaction pipeline can support both physical and social gameplay.

### 29.15 Validation Rules

Authoring tools should validate content.

**Examples:**

- all required junction anchoring exist

interaction has at least one effect or a valid purpose

- target object type matches intended usage
- duration is non-negative
- conditions are internally consistent
  Without validation, the simulation will produce broken offers silently.

### 29.16 Static vs Runtime Separation

**Important architectural split:**

- Static Content
- object definitions
- interaction definitions
- tags
- point role requirements
- base utilities
- Runtime State
- occupancy
- reservations
- current users
- remaining resources
- dynamic context

This separation must stay strict.

### 29.17 Base Utility in Content

Each interaction may provide a content-level base utility.

**Examples:**

- Sit = 0.1
- Eat = 0.2
- Socialize = 0.15

This is not the final score. It is only the content-level base that later combines with:

- needs
- memory
- social modifiers
- environment
- player influence

### 29.18 Content Growth Strategy

**A good content system should scale like this:**

- v1
- Bed
- Chair
- FoodSource
- Shower
- SocialSpot
- ClothingItem
- v2
- multi-state objects
- broken/dirty states
- richer effects
- multi-agent coordinated interactions
- v3
- chains of interactions
- context-sensitive variants
- object-driven emergent sequences

The architecture should support this without rewriting decision/execution.

### 29.19 Minimal v1 Authoring Scope

**For the first implementation:**

- small set of ObjectDefinitions
- small set of InteractionDefinitions
- conditions
- effects
- required junction anchoring
- duration in ticks
- runtime occupancy state
- interaction offers
- basic ClothingItem definition

**Avoid in v1:**

- giant custom scripts per object
- deep inheritance trees
- animation-driven logic
- dozens of unique interaction types

### 29.20 Debug Requirements

**Content and interaction debug must show:**

- object definition id

- available interaction definitions

- currently resolved interaction offers

- why an interaction is invalid or gated

- required point role and selected point

- runtime object state (occupied, empty, etc.)
  This is very important for debugging authored content.

### 29.21 Design Rules

Objects publish affordances, not AI decisions
Interactions are authored as data, not scattered hardcoded branches

- Static content and runtime state must stay separate
- Spatial junction anchoring are part of interaction authoring
- Interaction timing is expressed in ticks
  AI should reason about offers, not raw object internals
  Validation and debug visibility are mandatory

### 29.22 Summary

Content & Interaction System answers:
How do we add new world objects and behaviors without rewriting the core AI architecture?
It is the scalability layer that turns the simulation from a prototype into a content-driven game.

## 30. Debug, Observability & Development Tooling (Deep)

This section defines the minimum and recommended tooling required to develop, inspect, debug, and iterate on the simulation quickly.

**Core idea:**

A complex simulation without observability becomes unmaintainable. Every important NPC decision should be explainable.
The debug layer is not optional support infrastructure. It is part of the core development architecture.

### 30.1 What Debug Must Answer

At any moment, the developer should be able to answer:

- State
- What is this NPC doing right now?
- What goal is active?
- What plan step is active?
- What movement or execution state is active?
- Motivation
- Why was this goal selected?
- Which goals were considered and rejected?
- Which modifiers influenced the result?
- Perception
- What does this NPC currently perceive?
- Which objects are reachable?
- Which agents are seen/heard?
  What does the NPC think the environment is like?
- Planning / Routing
- What target was selected?
- What plan was built?
- What path is cached?
- What point is reserved?
- Failure
- Why did the plan fail?
- Why did movement stop?
- Why did execution time out?
- Why was a social interaction rejected?
  If these questions cannot be answered quickly, iteration speed will collapse.

### 30.2 Debug Layers

The observability stack should be split into layers.

- World Debug
- Perception Debug
- Decision Debug
- Planning Debug
- Path Debug
- Execution Debug
- Memory Debug
- Social Debug
- Global Metrics
  These layers should be inspectable independently, but also traceable end-to-end.

### 30.3 Three Debug Modes

A practical setup should support three modes.
Lightweight Always-On
Cheap information visible most of the time.

**Examples:**

- current goal label
- current movement/execution state
- simple path line
- reserved point marker
- Selected NPC Deep Inspect

Heavy, detailed debug for one or a few selected entities.

**Examples:**

- full goal score table
- full perception snapshot
- plan steps
- memory records
- social modifiers
- execution details
- Trace / Log Mode

Chronological event stream for debugging hard bugs.

**Examples:**

- GoalSelected
- PathBuilt
- StepStarted
- ReservationLost
- InteractionCompleted

This three-layer approach is enough for v1 and scales well.

### 30.4 Tick-Centered Observability

All important debug information must be indexed by tick.

**Examples:**

- [Tick 512] PerceptionBuilt: objects=3 agents=1
- [Tick 513] GoalSelected: Eat
- [Tick 514] PlanCreated: target=FoodSource_01
- [Tick 516] PathBuilt: nodes=8
- [Tick 523] StepStarted: Interact(Eat)
- [Tick 528] StepCompleted: Interact(Eat)

**This supports:**

- replay-style reasoning
- step debugging
- bug reproduction
- comparing expected vs actual behavior

### 30.5 Selected NPC Inspector

A selected NPC should expose a compact but rich inspector.

**Recommended sections:**

- Overview
- NPC id
- current fragment/tile/point
- current goal
- current plan status
- current movement status
- current execution status
- current target object / point
- time in current state
- Perception
- visible objects
- visible agents
- reachable flags
- environment summary
- Decision
- full goal score table
- current lock / cooldown state
- last decision reason
- Plan
- plan steps
- current step index
- target object / tile / point
- reservation status
- Path
- current cached path
- current path index
- stale/invalid state
- last path failure reason
- Memory
- active records
- strength values
- age in ticks
- Social
- relationships
- embarrassment
- nearby observers
- active social modifiers
- Trace
- recent chronological events

This inspector is one of the most valuable development tools in the whole project.

### 30.6 Goal Score Debug Table

The decision system must expose score decomposition, not only the winning goal.

**Example:**

- Goal       Base  Need  Memory  Social  Env  Cmd  Final
- Eat        0.1   0.8   0.0     0.0     0.0  0.2  1.1
- Sleep      0.1   0.3   0.0     0.0     0.0  0.0  0.4
- Shower     0.1   0.4  -0.2    -0.5     0.0  0.0 -0.2
- Idle       0.0   0.0   0.0     0.0     0.0  0.0  0.0

**This should include:**

- all candidate goals
- all modifier columns
- final score
- winner highlight

Without this, utility AI becomes opaque.

### 30.7 Perception Debug View

The perception panel should show what the NPC believes, not raw world truth.

**Recommended display:**

- Self
- hunger
- energy
- comfort
- social need
- current tile/fragment
- Objects
- object id
- interaction offers
- reachable / unreachable
- occupied / free
- Agents
- agent id
- can see / can hear
- relationship summary
- distance
- Environment
- temperature

```csharp
private/public
```

crowded/not crowded

**Important rule:**

- The debug view must make it obvious when the NPC is wrong or incomplete because of limited perception.

### 30.8 Plan Debug View

**Planning debug must show:**

- selected goal
- selected target
- full ordered step list
- current step index
- step timeouts
- current step status
- last replanning reason

**Example:**

- Goal: Eat

## 1. MoveToTile (10,4)

## 2. MoveToJunction Access_1

## 3. Interact Eat

This makes it obvious where the NPC is stuck.

### 30.9 Path Debug View

Path debug should be both textual and visual.

**Recommended textual fields:**

- source fragment/tile
- destination fragment/tile/point
- path length
- current node index
- status: valid / stale / failed
- last invalidation reason

**Recommended visual fields:**

- tile path overlay
- current path node highlight
- final point marker
- blocked/reserved target markers

Pathfinding without good visualization is painful to iterate on.

### 30.10 Movement / Execution Debug View

**Movement debug must show:**

current status: Rotating / Moving / Waiting / Blocked / Arrived

- desired rotation
- current rotation
- current target tile / point
- current path index
- stop reason if any

**Execution debug must show:**

- active step type
- execution status
- progress ticks
- start tick
- timeout tick
- target object / point
- failure/interruption reason
- whether effects already applied

Together these views explain how behavior unfolds physically over time.

### 30.11 Reservation & Occupancy Debug

Spatial ownership must be visible.

**Recommended display:**

- which points are occupied
- which points are reserved
- by whom
- until which tick
- whether a reservation is stale

**Useful visual conventions:**

- occupied point = one color
- reserved point = another color
- selected owner label

This is especially important for interactions like Sit, Eat, Sleep, Shower, and social spots.

### 30.12 Failure Taxonomy

Failures must be structured.

**Examples:**

```csharp
enum FailureReason
{
    None,
    NoValidGoal,
    NoTargetFound,
    PathFailed,
    PathInvalidated,
    ReservationFailed,
    ReservationLost,
    PreconditionsFailed,
    Timeout,
    Interrupted,
    SocialRejected,
    Unknown
}
```

**Important rule:**

- Every failed plan step, movement state, or execution state should end with an explicit structured reason.
- This is essential for both logs and inspector UI.

### 30.13 Structured Trace Events

Logs should not be arbitrary text only. They should be structured records.

```csharp
class AITraceEvent
{
    public int Tick;
    public EntityId NPC;
    public string Category;
    public string Name;
    public string Message;
    public string PayloadJson;
}
```

**Useful categories:**

- Perception
- Decision
- Planning
- Pathfinding
- Movement
- Execution
- Memory
- Social

This allows filtering and tool support later.

### 30.14 Recent History Buffers

Current state alone is often not enough.
Each selected NPC should retain small recent-history buffers:

- last N perception snapshots
- last N goal selections
- last N plan changes
- last N path invalidations
- last N failures
- last N social changes
  A simple ring buffer is enough.
  This is extremely helpful for diagnosing “how did it end up here?”

### 30.15 Stuck Detection

The system should detect likely stuck cases explicitly.

**Examples:**

- same step active too long
- movement distance not changing
- path repeatedly invalidated
- same goal repeatedly selected and failed
- action timing out repeatedly

```csharp
class StuckDiagnostic
{
    public bool IsStuck;
    public string Reason;
    public int DurationTicks;
}
```

This can later feed both debug UI and automated test assertions.

### 30.16 Global Metrics

Beyond per-NPC inspection, the project should track simple simulation-wide metrics.

**Examples:**

- active NPC count
- average plan rebuilds per minute
- path failures per minute
- reservation failures per minute
- timeout count
- number of stuck NPCs
- average goal duration
- average execution duration

These are useful for spotting systemic regressions.

### 30.17 Replay / Step-Debug Mindset

Because the simulation is tick-driven, tooling should support:

- pause
- single-step tick
- multiple-step advance
- inspection after each step
  This is a major advantage of the architecture and should be used aggressively during development.
  Even a lightweight manual version in v1 will already pay off.

### 30.18 Debug Event Flow

**A strong mental model for tooling is:**

- Perception Snapshot
- → Decision Result
- → Plan
- → Path
- → Movement / Execution
- → Effects / Failure / Completion

The developer should be able to inspect this chain end to end for any selected NPC.
This is what turns the simulation from a black box into an explainable system.

### 30.19 Minimum v1 Tooling

For the first implementation, do not overbuild.
Recommended must-have tooling:

- selected NPC inspector
- goal score table
- current plan view
- path overlay
- reservation/occupancy markers
- structured trace log
- tick step / pause / fast-forward controls
- explicit failure reasons
- simple stuck detector
  This is enough to move fast without drowning in tooling work.

### 30.20 Design Rules

Every important AI decision must be explainable
Tick index must be present in all major trace/debug records
Failures must be structured, not vague
Debug must show NPC perception, not only raw world state
Selected-NPC deep inspection is more important than fancy dashboards
Reservations, paths, and plan steps must all be visible
Recent history is required for diagnosing temporal bugs
Step-debug support is a first-class feature of the architecture

### 30.21 Summary

Debug & Observability answers:
Why is this NPC doing this, why did it fail, and how exactly did it get here over time?
It is the development layer that makes the whole simulation buildable and maintainable.

## 31. Unity Integration & Presentation Layer (Deep)

This section defines how the simulation core connects to Unity for visualization, input, and UX—without breaking the authoritative tick-driven model.

**Core idea:**

Simulation owns the truth. Unity renders a projection of that truth.
Unity must never be the source of gameplay state.

### 31.1 Architectural Boundary

Simulation Core (Headless, Deterministic-Friendly)
    ↓ (Snapshots / Events)

- Presentation Layer (Unity)
- Simulation: pure data + systems, tick-driven
- Unity: views, interpolation, input, VFX, SFX

**Rule:**

No gameplay mutation from Unity-side scripts.

### 31.2 Data Flow Models

**Two complementary channels:**

- Snapshots (State Pull)
- periodic world snapshots after each tick (or N ticks)
- used for rendering and inspector UI
- Events (State Push)
- discrete events emitted by systems (e.g., StepStarted, InteractionCompleted)

used for triggering animations, sounds, UI feedback

### 31.3 Snapshot Structure (Minimal)

```csharp
class WorldSnapshot
{
    public int Tick;
    public List<NPCSnapshot> Npcs;
    public List<ObjectSnapshot> Objects;
}

class NPCSnapshot
{
    public EntityId Id;
    public Vector3 Position;
    public float Rotation;

    public TileCoord Tile;
    public JunctionId? Point;

    public GoalType CurrentGoal;
    public MovementStatus MovementStatus;
    public ExecutionStatus ExecutionStatus;
}
```

Snapshots should be lightweight and derived from WorldState.

### 31.4 Snapshot Frequency

**Options:**

- every tick (simplest)
- every N ticks (for performance)

Unity renders frames independently and interpolates between snapshots.

### 31.5 Interpolation (Client-Side)

**Unity should interpolate between two snapshots:**

```csharp
renderPos = Lerp(prev.Position, next.Position, alpha);
renderRot = Slerp(prev.Rotation, next.Rotation, alpha);
```

Where alpha is based on render time between ticks.

**Important:**

- Interpolation must not change authoritative state.

### 31.6 Entity View Binding

Each entity has a corresponding Unity view object.

```csharp
class NPCView : MonoBehaviour
{
    public EntityId Id;

    public void ApplySnapshot(NPCSnapshot s)
    {
        // store target transform, not immediate snap if interpolating
    }
}
```

**Binding strategy:**

- map EntityId → GameObject
- create/destroy views on spawn/despawn events

### 31.7 Animation Triggers via Events

Use simulation events to drive animations.

**Examples:**

- InteractionStarted(Eat) → play eating animation
- MovementStarted → locomotion blend tree
- MovementStopped → idle animation

**Rule:**

Animation is a reaction to events, not a driver of logic.

### 31.8 Input Adapter (Player → Simulation)

Unity collects input and converts it into SimulationCommand.

```csharp
void OnClickGoTo(TileCoord tile)
{
    commandQueue.Enqueue(new SimulationCommand
    {
        Type = CommandType.Move,
        TargetEntity = selectedNpc,
        Payload = tile
    });
}
```

**Important:**

- no direct mutation of NPC state from Unity
- all input goes through command queue

### 31.9 Camera & Selection

Camera and selection are purely presentation concerns.

- selecting NPC does not change simulation
- camera follows snapshot data

**Optional:**

- debug overlays attached to selected NPC

### 31.10 UI Binding

UI reads from snapshots or selected NPC inspector data.

**Examples:**

- current goal label
- needs bars
- current action
- debug panels

**Rule:**

UI reads, never writes gameplay state directly.

### 31.11 Event Bus (Presentation Side)

Unity layer may have its own event bus fed by simulation events.

```csharp
class PresentationEvent
{
    public int Tick;
    public string Type;
    public object Payload;
}
```

**Used for:**

- animation triggers
- sound effects
- UI popups

### 31.12 Time Sync

**Two time domains:**

- Simulation Time (ticks)
- Render Time (frames)

**Bridge:**

- track last and next snapshot
- compute interpolation alpha

**Avoid:**

- tying simulation step to frame rate

### 31.13 Pause / Step / Fast-Forward (UI)

**Expose controls in Unity that call simulation:**

- Pause → stop advancing ticks
- Step → advance one tick
- Fast-forward → advance multiple ticks per frame

```csharp
sim.Step();
sim.StepN(10);
```

Rendering may be simplified during fast-forward.

### 31.14 Prefabs & Authoring

Unity prefabs represent visual templates only.

- NPC prefab: mesh, animator, VFX
- Object prefab: model, particles, sounds
  Do NOT store gameplay state in prefabs.

### 31.15 Spawning & Despawning

Driven by simulation events.

- `OnEntitySpawned` → create view
- `OnEntityDespawned` → destroy view
  Views should not create entities themselves.

### 31.16 Error Handling & Desync Protection

**Unity should handle missing/late data gracefully:**

```csharp
if snapshot missing → keep last known
if entity not found → skip frame
```

**Optional later:**

sanity checks comparing snapshot vs expected ranges

### 31.17 Performance Considerations

- batch snapshot application
- avoid per-frame allocations
- reuse view objects (pooling)
- limit debug overlays in release

### 31.18 Minimal v1 Integration Scope

**For first implementation:**

- basic snapshot pipeline (every tick)
- NPCView with position/rotation
- simple interpolation
- command adapter (click → move)
- basic animation triggers from events
- selected NPC debug panel

**Avoid in v1:**

- complex networking sync
- prediction/rollback
- heavy ECS in Unity side

### 31.19 Design Rules

- Simulation is authoritative, Unity is a viewer
- No gameplay mutation from MonoBehaviours
- Use snapshots for state, events for reactions
- Interpolation is visual-only
- Input must go through command queue
- Prefabs contain visuals only
- Debug tools should read the same data as gameplay

### 31.20 Summary

**Unity Integration answers:**

How do we visualize and interact with the simulation without breaking its architecture?
It is the bridge between the deterministic world and the player experience.

## 31A. Clothing System (Model-Level Foundation)

This section introduces the minimum clothing model required for the simulation.

**Core idea:**

Clothing exists both as world content and as model-level equipment that modifies NPC state.
The Unity bone/attachment implementation may be sophisticated, but the simulation only needs the abstract gameplay layer.

### 31A.1 Clothing Item Definition

```csharp
class ClothingItemDefinition
{
    public string Id;
    public ClothingSlot Slot;
    public int Layer;
    public float WarmthValue;
}
enum ClothingSlot
{
    Head,
    Torso,
    Legs,
}
```

**Recommended current direction:**

- slot-based system
- support for layering
- warmth as a gameplay modifier

### 31A.2 Equipped Clothing State

```csharp
class ClothingState
{
    public Dictionary<ClothingSlot, List<string>> EquippedItemIdsBySlot;
}
```

**The model only needs to know:**

- what is equipped
- in which slot
- in which layer order
- what aggregate warmth/effect it gives

### 31A.3 Clothing in the World

Clothing items may exist as pickable world objects.

**Examples:**

- pants
- jacket
- hat

**These can later provide interactions:**

- Dress
- Undress
- PickUp
- Drop

### 31A.4 Clothing and Temperature

Clothing directly modifies thermal comfort.

**Example logic:**

- environment temperature creates heat/cold pressure
- equipped clothing shifts perceived thermal comfort
- NPC may decide to dress/undress accordingly

**For v1:**

a simple aggregate warmth score is enough

### 31A.5 Clothing Scope for v1

For the first vertical slice, clothing should support:

- items existing in the world
- equip into simple slots
- warmth modifier affecting temperature discomfort
- future-ready layering field, even if lightly used at first
  Detailed bone attachment and visual assembly remain on Unity side.

### 31A.6 Summary

**Clothing System answers:**

What is the NPC wearing, how does that affect temperature and comfort, and how can clothing become part of world interaction?
It creates a bridge between inventory-like systems, environmental simulation, and visual presentation.

## 32. NPC Brain & AI Architecture Layer (GOAP / LLM / Hybrid)

This section defines the meta-layer that organizes decision-making strategies and allows swapping or combining different AI approaches.

**Core idea:**

The Brain is not a system. The Brain is a composition layer that selects HOW thinking happens.

### 32.1 Brain Position in Architecture

```
Perception → Brain → Decision Model / Planning Model → Execution
```

The Brain orchestrates how decisions and plans are produced.

### 32.2 Brain Structure

```csharp
class NPCBrain
{
    public IDecisionModel Decision;
    public IPlanningModel Planner;
}
```

This allows different AI strategies to be plugged in without rewriting systems.

### 32.3 Decision Model Interface

```csharp
interface IDecisionModel
{
    DecisionResult Decide(NPCContext context);
}
```

**Implementations:**

- UtilityDecisionModel (current v1)
- RuleBasedDecisionModel
- LLMDecisionModel (future)

### 32.4 Planning Model Interface

```csharp
interface IPlanningModel
{
    NPCPlanState BuildPlan(NPCContext context, GoalType goal);
}
```

**Implementations:**

- SimplePlanner (current v1)
- GOAPPlanner
- ScriptedPlanner

### 32.5 Current v1 Brain (Simple)

Decision: Utility AI
Planning: Direct mapping (Goal → Steps)
This is stable, debuggable, and fast.

### 32.6 GOAP Integration (v2)

GOAP replaces simple planner.

```
Goal → World State → Actions (preconditions/effects) → Search (A*) → Plan
```

GOAP is powerful but more complex.

**Recommendation:**

- do not start with GOAP
- introduce after v1 is stable

### 32.7 LLM Integration (v3+)

LLM should not control simulation directly.

**Correct role:**

- high-level reasoning
- narrative behavior
- suggestion of goals or priorities

```csharp
class LLMDecisionModel : IDecisionModel
{
    public DecisionResult Decide(NPCContext context)
    {
        // interpret context → propose goal
    }
}
```

**Important rule:**

- LLM outputs must be validated and converted into structured goals.
- Never allow free-form LLM output to mutate world state.

### 32.8 Hybrid Brain Model

Recommended long-term architecture:
Utility AI (fast, reactive)

+ GOAP (structured planning)
+ LLM (high-level reasoning)

**Each layer has its role:**

- Utility → fast selection
- GOAP → structured execution
- LLM → creativity / variation

### 32.9 Brain Modes

NPCs may have different brain types.

**Examples:**

- Simple NPC → Utility only
- Advanced NPC → Utility + GOAP
- Special NPC → Utility + LLM hybrid

```csharp
enum BrainType
{
    Simple,
    Advanced,
    Narrative
}
```

### 32.10 Stability & Safety

**Brain layer must enforce safety rules:**

- no infinite loops
- no invalid goals
- no unsafe commands
- LLM-specific rules:
- validate outputs
- fallback to utility if invalid

### 32.11 Debug Requirements

**Brain debug must show:**

- active decision model
- active planning model
- raw decision result
- fallback usage
- LLM outputs (if used)

### 32.12 Minimal v1 Scope

**For first implementation:**

- NPCBrain abstraction exists
- UtilityDecisionModel
- SimplePlanner

No GOAP, no LLM yet.

### 32.13 Design Rules

- Brain orchestrates, not replaces systems
- Decision and Planning must be swappable
- LLM must be sandboxed and validated
- Keep v1 simple and deterministic
- Add complexity only after stability

### 32.14 Summary

**NPC Brain answers:**

How does this NPC think, and which decision/planning strategy does it use?
It is the abstraction layer that future-proofs the AI architecture.

## 33. First Vertical Slice / Prototype Scope

This section defines the first real playable simulation slice to implement.

**Goal:**

prove the architecture works end to end
keep scope small
validate the full AI loop on a compact map

### 33.1 Prototype World Shape

**The first prototype uses:**

- 1 fragment
- roughly 12 hex tiles
- one NPC
- a small set of objects
- basic temperature enabled
- basic clothing support enabled

This fragment acts like a tiny room / local map node.

**Important:**

- “Room” in this prototype means a small self-contained map fragment, not an abstract rectangular room system.

### 33.2 Tile Shape for Prototype

The prototype should use hexagonal tiles as the base map unit.

**Why:**

- fits intended visual direction
- naturally supports modular terrain-like construction

avoids rework later if hex is the final direction
So even the first slice should be built on hex adjacency, not square-grid temporary logic.

### 33.3 Prototype Objects

**Minimum required objects in the prototype fragment:**

- Food object (apple or simple food source)
- Chair
- Bed
- 1–3 clothing items in the world

**Optional later in this same slice:**

- Shower / wash point

### 33.4 Prototype NPC State

**The NPC should already have:**

- hunger
- energy
- comfort
- temperature discomfort / thermal state
- simple clothing state
- basic movement and facing

For the first iteration, the NPC may start with no clothing equipped.

### 33.5 Prototype AI Loop to Prove

**The first slice should prove this loop works:**

- Perceive
- → Decide
- → Plan
- → Pathfind
- → Move
- → Execute
- → Update Needs / Memory
- → Repeat

**Required concrete behaviors:**

- go eat when hungry

go sit when comfort logic prefers it
go sleep / lie when energy is low
react to temperature at least at a basic level
optionally dress if clothing logic is already connected

### 33.6 Prototype Temperature Scope

Temperature must already exist in the first slice.

**Recommended v1 behavior:**

- one environment temperature value for the fragment
- one NPC thermal discomfort value
- clothing modifies thermal comfort
- temperature can influence decision scores
  This is enough to validate the architecture without building full climate simulation yet.

### 33.7 Prototype Clothing Scope

For the first slice, clothing should be minimal but real.

**Support:**

- clothing item definitions
- clothing items placed in world
- simple equip action
- slot-based equipment in model
- warmth modifier applied to thermal comfort

**Do not overbuild yet:**

- no full inventory complexity

no advanced layering logic in gameplay rules yet
no dependency on visual rig complexity
The model should already be correct, even if visuals stay simple at first.

### 33.8 Prototype Spatial Scope

**The first slice should already validate:**

- one hex fragment
- explicit hex adjacency
- object placement on tiles
- junction anchoring for interaction
- occupancy and reservation

This ensures the final spatial model is being tested from day one.

### 33.9 Prototype Success Criteria

**The first vertical slice is successful if:**

- NPC can path across the fragment reliably
- NPC can interact with food, chair, and bed
- decision system chooses sensible goals based on needs
- execution completes actions over ticks
- debug tools explain every important choice
- temperature exists and affects behavior at least minimally
- clothing exists in the model and affects temperature

### 33.10 Recommended Implementation Order for This Slice

- Hex fragment + tile graph
- WorldState with one NPC and three core objects
- Tick loop
- Movement + pathfinding
- Perception
- Decision
- Planning + execution
- Temperature discomfort
- Clothing item + equip support
- Debug inspector + trace
- Unity view and interpolation

### 33.11 Design Rules

The first slice must use the final architectural direction, not a throwaway shortcut
Keep content tiny, but model correctness high
Temperature should exist from the first slice
Clothing should exist at least at the model level from the first slice
Hex tiles should be used immediately if they are the intended final map unit
The first prototype must validate the full loop, not just movement or just AI in isolation

### 33.12 Summary

**First Vertical Slice answers:**

What is the smallest real prototype that proves the full simulation architecture actually works together?
It is the bridge from architecture to implementation.

## 34. Vertical Slice Implementation Plan

This section breaks the first prototype into a practical engineering sequence.

**Goal:**

- avoid chaos
- preserve architecture quality
- get a playable proof quickly
- keep every stage testable in isolation

### 34.1 Core Principle

Implementation order should follow dependency order.
Do NOT start from Unity visuals. Do NOT start from clothing visuals. Do NOT start from advanced AI.
Start from simulation correctness.

**Rule:**

Every step should leave the project in a runnable, testable state.

### 34.2 Phase 0 — Project Skeleton

Create the minimal project structure.

**Recommended modules:**

- Simulation.Core
- Simulation.AI
- Simulation.Navigation
- Simulation.Content
- Simulation.Runtime
- Unity.Presentation

**Minimum tasks:**

- create assemblies / namespaces
- create WorldState
- create TickScheduler
- create SimulationCommand / SimulationEvent
- create minimal debug trace interface

**Done criteria:**

- project builds
- empty simulation can advance ticks
- tick index increments
- basic trace line can be emitted

### 34.3 Phase 1 — Hex Map Foundation

Implement the smallest real spatial model.

**Scope:**

- 1 fragment
- ~12 hex tiles
- axial coordinates (Q, R)
- explicit 6-neighbor adjacency
- tile graph queries

**Tasks:**

- TileCoord
- hex neighbor lookup
- Fragment
- Tile
- TileGraph
- object placement on tiles

**Done criteria:**

- map exists in pure simulation
- adjacency queries work
- tile reachability checks work
- simple pathable / blocked tile flags exist

### 34.4 Phase 2 — Core Entities and Objects

Add the first real simulation actors and objects.

**Scope:**

- 1 NPC
- Bed
- Chair
- Food object
- 1–3 clothing items

**Tasks:**

- NPCState
- WorldObjectState
- object definitions for bed/chair/food/clothing
- junction anchoring for interactions
- object placement in the fragment

**Done criteria:**

- world contains one NPC and all required prototype objects
- objects have valid points and definitions
- data lives entirely in simulation model

### 34.5 Phase 3 — Tick Runtime Loop

Activate the real simulation heartbeat.

**Scope:**

- fast / medium / slow scheduling
- command queue
- event queue
- tick-based runtime

**Tasks:**

```csharp
TickScheduler.Step()
```

- system ordering
- command ingestion
- event ingestion
- trace with tick indices

**Done criteria:**

- simulation runs deterministically enough for development
- commands can enter through queue
- events can be emitted and observed

### 34.6 Phase 4 — Movement and Pathfinding

Make the NPC physically capable of traversing the map.

**Scope:**

- hex tile pathfinding
- cached path
- movement state
- rotation + translation over ticks
- tile crossing updates

**Tasks:**

- PathRequest / PathResult
- A* on hex tiles
- MovementState
- movement execution loop
- arrival detection

**Done criteria:**

NPC can move from one tile to another reliably

- movement progresses over ticks
- path is visible in debug
- tile transitions update correctly

### 34.7 Phase 5 — Interaction Anchors, Occupancy, Reservation

Before real AI, spatial ownership must work.

**Scope:**

- points on objects
- occupancy tracking
- reservation system

**Tasks:**

- point definitions on bed/chair/food/clothing
- ReservationState
- point ownership updates
- reservation validity checks

**Done criteria:**

- NPC can reserve interaction points
- occupied / reserved points are distinct
- stale reservation cases are detectable

### 34.8 Phase 6 — Perception System

Now the NPC can begin to understand the local world.

**Scope:**

- local radius perception
- perceived objects
- perceived environment
- reachable flags

**Tasks:**

- PerceptionSnapshot
- local tile queries
- object filtering
- environment perception including temperature
- snapshot cache on NPC

**Done criteria:**

- NPC has a valid perception snapshot
- reachable food/chair/bed/clothing are visible in debug
- environment temperature is visible in perception

### 34.9 Phase 7 — Decision System

Now the NPC can choose goals.

**Scope:**

- utility scoring
- small goal set
- locks / cooldowns
- command influence

**Recommended first goals:**

- Eat
- Sit
- Sleep
- Dress
- Idle

**Tasks:**

- GoalType
- GoalScore
- additive scoring logic
- hard gating
- decision result output

**Done criteria:**

NPC chooses a sensible goal based on current state
goal score table is visible in debug
temperature can influence Dress / comfort-related decisions

### 34.10 Phase 8 — Planning System

Turn chosen goals into executable plans.

**Scope:**

- direct planner
- target selection
- step list creation
- validation

**Tasks:**

- NPCPlanState
- PlanStep
- goal → steps mapping
- reservation-aware target selection

**Done criteria:**

- Eat builds a valid plan
- Sit builds a valid plan
- Sleep builds a valid plan

Dress can build a simple equip plan if clothing is reachable

### 34.11 Phase 9 — Execution System

Make interactions actually complete over time.

**Scope:**

- move completion tracking
- interact runtime
- wait runtime
- duration ticks
- effect application

**Tasks:**

- NPCExecutionState
- step lifecycle
- action durations
- effect application for eat/sit/sleep/dress

**Done criteria:**

- NPC completes actions over ticks
- hunger/comfort/energy update correctly

clothing can be equipped in model state
execution failures are logged explicitly

### 34.12 Phase 10 — Temperature System

Introduce the first environmental pressure.

**Scope:**

- global fragment temperature
- NPC thermal discomfort
- clothing warmth contribution

**Tasks:**

- EnvironmentState.GlobalTemperature
- thermal discomfort calculation
- temperature-related scoring modifiers
- simple update loop

**Done criteria:**

- hot/cold state affects decision scores
- clothing changes thermal comfort

thermal state is visible in debug and perception

### 34.13 Phase 11 — Clothing Model Integration

Connect clothing as a real gameplay layer.

**Scope:**

- clothing items in world
- equip action
- slot-based equipment
- warmth aggregation

**Tasks:**

- ClothingItemDefinition
- ClothingState
- Dress interaction
- equipment slot update in model

**Done criteria:**

- NPC can equip at least one clothing item
- clothing modifies temperature comfort
- clothing state appears in debug

**Important:**

- visual bone attachment can remain minimal or mocked at this stage
- model correctness matters more than presentation here

### 34.14 Phase 12 — Memory Basics

Add the first learning loop.

**Scope:**

- short-term memory only
- interaction success/failure records
- basic strength + decay

**Tasks:**

- MemoryRecord
- memory update system
- memory-based decision modifiers

**Done criteria:**

- repeated failures influence decision
- recent success/failure visible in debug

### 34.15 Phase 13 — Debug Tooling for Slice

Before presentation polish, debugging must be usable.

**Minimum tools:**

- selected NPC inspector
- current goal panel
- plan panel
- path overlay
- reservation markers
- trace log
- tick step / pause controls

**Done criteria:**

- every major NPC decision can be inspected
- failures are explainable
- stuck states are visible

### 34.16 Phase 14 — Unity Presentation Bridge

Only after simulation works headlessly.

**Scope:**

- snapshot export
- NPC view binding
- position/rotation interpolation
- basic object views
- input adapter

**Tasks:**

- WorldSnapshot
- NPCSnapshot
- Unity-side NPCView
- click/select → command queue

**Done criteria:**

- Unity can render simulation state
- movement is visually smooth
- commands go into simulation queue
- Unity does not own gameplay logic

### 34.17 Phase 15 — Prototype Validation Scenario

Run a real vertical-slice validation scene.

**Recommended test scenario:**

- NPC starts hungry
- food is reachable
- bed and chair are present
- one clothing item is present

fragment temperature is uncomfortable enough to matter

**Observe whether NPC:**

- perceives valid opportunities
- chooses sensible goals
- moves correctly
- completes actions
- reacts to temperature and clothing logic

**Done criteria:**

- full AI loop works end to end
- no unexplained stalls
- debug tools explain all major transitions

### 34.18 What NOT to Build in the First Slice

**Do not add yet:**

- GOAP
- LLM integration
- multi-fragment procedural generation
- advanced local avoidance
- rich social behavior between multiple NPCs
- deep inventory systems
- fully featured clothing visuals
- complex camera systems

These can come later.
The first slice should prove the architecture, not the full game.

### 34.19 Recommended Milestone Order

**A condensed milestone view:**

- Project skeleton
- Hex map
- NPC + objects
- Tick runtime
- Movement + pathfinding
- Points + reservations
- Perception
- Decision
- Planning
- Execution
- Temperature
- Clothing
- Memory
- Debug tooling
- Unity presentation
- Validation scenario

### 34.20 Success Definition

**The vertical slice is successful when:**

- one NPC can live in a small hex fragment
- the NPC can perceive food, chair, bed, and clothing
- the NPC can choose meaningful goals
- the NPC can move and interact correctly
- temperature affects decision-making
- clothing exists in the model and changes comfort
- every major state transition is debuggable
- Unity only visualizes the simulation

### 34.21 Design Rules

- Build headless simulation first, presentation second
- Every phase must leave the slice runnable
- Keep content small but model correctness high
- Temperature and clothing should be present early, even if minimal
- Do not add advanced AI until the deterministic loop is proven
- Debug tooling is part of implementation, not afterthought

### 34.22 Summary

**Vertical Slice Implementation Plan answers:**

In what exact order should we build the first prototype so that the architecture stays clean and the result becomes playable as early as possible?
It is the roadmap from design to code.
