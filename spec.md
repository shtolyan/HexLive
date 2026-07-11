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
    public string DisplayName;   // iteration 23: Marta / Molly / Jolie
    public string ActorMesh;     // iteration 23: which visual body renders her

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

    // carrying (v1 minimal, see 29B)
    public InventoryState Inventory; // few slots of definition ids
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

    // production runtime data (only used by producers, see 29A)
    public int NextProductionTick;
    public List<ObjectId> ProducedItems;
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

### 19.3C Body, Parts & Wounds (Iteration 12)

Ported from the molly_copy reference project (BoneHealthSystem + Wearing):
bones simplified to **7 body parts**, each with its own health.

```csharp
enum BodyPart { Head, Torso, Pelvis, ArmL, ArmR, LegL, LegR }

class BodyState
{
    public Dictionary<BodyPart, float> Parts; // each 0..1, starts 1.0
}
```

| Rule | Value |
|---|---|
| Vital parts | Head, Torso — destroyed (0) → **death**, regardless of overall HP |
| Overall Health | mean of the 7 parts (feeds existing flee/regen/snapshot logic) |
| Bite target | seeded-weighted random part: legs 30 %+30 %, arms 12.5 %+12.5 %, torso 10 %, pelvis 3 %, head 2 % (dogs bite low) |
| Bite damage | 0.2 × (1 − armor covering that part) to the hit part |
| Limping | movement speed × (0.4 + 0.6 × mean leg health) — a mauled leg means hobbling home |
| Weak arms | strike-back × (0.4 + 0.6 × mean arm health) |
| Regeneration | each part +0.02 per slow tick while Hunger < 0.5 |
| Death causes | vital part destroyed (traced with the part) OR overall health depleted |

### 19.7A Day/Night Cycle v1 (Iteration 6)

The environment gains a tick-derived clock; temperature and behavior follow
the day rhythm.

| Parameter | Value |
|---|---|
| Day length | 2400 ticks (10 min real time at 0.25 s/tick) |
| Phases (quarters) | Morning 06–12, Day 12–18, Evening 18–24, Night 00–06 |
| Simulation start (tick 0) | 06:00, Morning |
| Temperature | 12 ± 6 °C sinusoid; warmest 15:00 (18°), coldest 03:00 (6°) |
| Discomfort threshold | unchanged (12°): days are comfortable, nights are cold |
| Fruit production | Morning + Day only (29A); at dawn an overdue timer fires immediately — "morning apples" |
| Sleep environment bonus (23.4) | +0.25 at Night, +0.10 at Evening via EnvironmentModifier |

```csharp
class EnvironmentState
{
    // ... existing fields ...
    public float TimeOfDayNormalized; // 0..1, 0 = 06:00
    public DayPhase Phase;            // Morning, Day, Evening, Night
}
```

An `EnvironmentSystem` (Slow layer, ordered before needs/temperature) derives
the clock from the tick and updates `GlobalTemperature`; a `PhaseChanged`
trace marks transitions. Expected emergent rhythm: gather food and socialize
in daylight, dress for the cold evening, sleep through the night.

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

> **SEAMLESS WORLD REWORK (Iteration 17, 2026-07).** The world is
> **seamless**: one continuous hex field in a single axial coordinate
> space. The Fragment/FragmentLink model below is **retired as a gameplay
> concept** — there are no portals, transitions, or per-fragment scopes.
> `FragmentId` survives in code as a technical constant (always 1) until a
> cleanup pass removes it; future scaling is by chunked *streaming* of one
> continuous space, never by teleport links. All sections mentioning
> fragment transitions (20.x FragmentLink, 19.x Fragment scope) are void.


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

### 20.16 Tile Elevation & The Island (iteration 27)

The world is an island: sea on every side, beaches, grassy plains, brown
hills, and mountains — some climbable by natural ramps, some sheer.

**Elevation model**

- `Tile.Elevation`: int 0..5. 0 = sea floor, 1-2 = lowland, 3 = hills,
  4-5 = mountains. Bootstrap carries it (`TileBootstrap.Elevation`).
- Elevation is SIMULATION state: it decides passability. Rendering maps it
  to visual height (0.55 world units per level) and biome colors — sand at
  the waterline (existing adjacency rule), grass lowland, brown-green
  hills, brown rock peaks.
- The 0.55 step is deliberately **sitting height**: the actors stand
  ~1.7 units tall, so one step (0.55) is just above chair-seat height
  (~0.45) and taller than a shin (~0.44) — legs dangle off a 1-level
  ledge without touching the ground (§29G), approachable from below and
  from above. Two steps (1.10) are chest-high: a cliff, blocked.

**Seeded generation** (PrototypeWorldDefinitionFactory)

- Deterministic value noise (Hash01 lattice + bilinear interpolation, two
  octaves at world-space frequencies ~0.13/0.3) times a radial island
  falloff from the map center.
- `height <= 0` -> **sea**: Water flag, `Walkable = false` (open sea is
  not shallows — nobody swims out of the world; the river and pond remain
  walkable Water). The island mask guarantees a full sea ring inside the
  map bounds.
- The **home plateau**: tiles within 3 of the home center and of the hut
  site clamp to elevation 1-2 and never become sea — the colony never
  spawns on a cliff.
- **All water shares one level**: river tiles carve to elevation 0 — the
  same as the sea — so the river meets the coast flush (no stepped water
  surfaces). Banks clamp to elevation 1: a 0->1 step to the waterline is
  a walkable slope (drinkable) and, per §29G, a scenic ledge to sit on
  with legs over the water; >1 would be a cliff wall.

**Cliffs & ramps**

- A boundary junction whose owning LAND tiles differ by **more than 1
  level** is `Blocked` at bootstrap — cliffs are real obstacles, exactly
  the mechanism walls and tree trunks already use (connectivity, pathing
  and the debug overlay inherit it for free).
- A 1-level difference is a walkable slope: where the noise is gentle the
  hillside naturally forms ramp paths ("stairs") to the top; where it is
  steep, peaks stay unreachable on purpose.
- Soak invariant: the home connectivity component must cover >= 50 % of
  land junctions (an island with SOME unreachable crags is desired; a
  shattered one is a generation bug).

**Rendering**

- Hexes render as **solid prisms** (`BuildHexPrismMesh`): a flat top at
  `0.2 + Elevation x 0.55` plus a six-quad perimeter skirt dropping to a
  shared base (`TerrainBaseY = -2.5`), so a raised tile is a rock column
  that visually meets its lower neighbours instead of a floating cap. The
  mesh has two submeshes — top (biome material) and skirt (cliff material).
- Style is **flat low-poly, untextured** — deliberately no noise textures
  (they showed tiling seams and clashed with the cartoon look). Each facet
  is a flat colour: `BiomeColor` gives the per-biome base
  (`grass`, `grass_dry`, `hill`, `rock`, `mountain`, `sand`, `cliff`),
  `Jitter` applies a small stable per-tile brightness wobble (keyed off the
  tile coord) for a hand-placed patchwork, and `GetFlatMaterial` caches one
  double-sided (`_Cull = 0`) URP/Lit material per quantised colour so the
  SRP batcher still groups most tiles.
- Grass tiles grow **low-poly tufts** (`BuildGrassClump`): a per-tile
  combined mesh of solid-green triangle blades (crossed pairs, slight lean),
  deterministically scattered from the tile coord, one shared flat green
  material — no texture, no alpha. Gated by `_grassDetail` /
  `_grassBladesPerTile` on the renderer.
- Water renders sunken below its tile top (existing rule); its prism skirt
  uses the sand material as a riverbed bank. A large sea-colored backdrop
  plane extends the ocean beyond the playable bounds. The sea/water tops use
  a stylized cartoon water shader (`HexLive/StylizedWater`): gentle vertex
  waves, fresnel deep→shallow two-tone, drifting glints, soft specular.
- **Day/night** (`SkyDayNightController`, installed by the bootstrap) reads
  `Environment.TimeOfDayNormalized` (0 = 06:00) and swings one directional
  light along an arc: it is the **sun** by day (warm→white, bright) and the
  **moon** by night (cool, dim), overhead at noon/midnight. Ambient light
  lerps night↔day. A custom skybox (`HexLive/StylizedSky`) renders a
  day/night gradient, a sun disc + glow, a moon disc + glow, a warm sunset
  band, and a twinkling star field; the controller feeds it sun/moon
  directions and the day amount each frame.
- **Campfires burn** (`CampfireEffect` on `campfire.spot` views): rising
  flame + ember particle systems (additive soft puffs) and a warm,
  Perlin-flickered point light that lights nearby terrain and actors.
- Every movable and object view takes its Y from its tile's top (the
  renderer's pose interpolation smooths level changes).

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

**Iteration 5 note (2026-07):** implemented for objects with hex-distance
radius **2 tiles**; objects beyond the radius are supplied to the same
perception list from spatial memory (27.18A) flagged `FromMemory`.
**Agents remain globally perceived in v1** — the prototype world is one
shared home and its yard; cohabitant awareness is assumed. Agent-radius and
agent memory return together with bigger worlds.

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
    Eat,      // consume food the NPC already has (inventory)
    GetFood,  // acquire food from the world (pick up produce)
    Sleep,
    Sit,
    Shower,
    Dress,
    Socialize,
    Explore,  // wander to a random far junction (iteration 9, see 29C.5)
    Observe,
    Follow,
    Idle
}
```

Acquisition and consumption are separate goals: `GetFood` ends with food in
inventory, `Eat` ends with a need satisfied. Keeping them apart keeps scoring,
gating, and debug output honest (see 23.21, 29B).

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

**Worked example (v1 Starving status, needs on 0..1 scale):**

- enter Starving when Hunger >= 0.85
- clear Starving only when Hunger < 0.60

The wide 0.85/0.60 gap is deliberate hysteresis: one apple (HungerDelta -0.60
since iteration 6) taken at the 0.85 threshold lands at ~0.25 and safely
clears the status, so the NPC cannot flicker in and out of Starving.

**v1 goal switching rules (iteration 3):**

| Parameter | Value |
|---|---|
| Switch delta with an active plan | new score must exceed current by 0.15 |
| Switch with no active plan | free (delta 0) |
| Goal lock duration | 24 ticks (6 s) from goal adoption |
| Lock override (emergency) | new score must exceed current by 0.5 |

The Starving boost (+1.0, see 23.17) intentionally crosses the 0.5 lock
override, so emergencies always break through. `Idle` and `None` are never
locked and never require a delta to leave.

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

**v1 rules (iteration 3):** a lock is placed whenever a non-Idle goal is
adopted (`EndTick = now + 24`). While locked, a competing goal takes over only
if its score exceeds the current goal's score by 0.5 (only the Starving boost
crosses this in v1). After the lock expires the normal 0.15 switch delta from
23.8 applies. Stale locks (for a goal no longer current) are ignored.

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

**v1 rules (iteration 3):** planning failures (`PlanFailed` — no suitable
object, `ReservationFailed` — junction taken by another NPC) put the failed
goal on a 40-tick (10 s) cooldown. Goals on cooldown are hard-gated in
scoring (FinalScore = 0), so the NPC picks the next-best activity instead of
hammering the same target. Expired cooldowns are pruned each decision pass.

A failure also clears the goal's own lock (23.9): a failed goal must not be
defended by its lock, or the hold rule would pin the zero-scored goal and
planning would retry the same target every pass until the lock expired.

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

**Concrete v1 emergency rule (Starving):**

| Parameter | Value |
|---|---|
| Enter Starving | Hunger >= 0.85 |
| Clear Starving (hysteresis, see 23.8) | Hunger < 0.60 |
| While Starving | +1.0 EmergencyModifier to Eat and GetFood FinalScore |

The +1.0 boost dominates any normal score (regular max is 0.1 + need = 1.1 vs
boosted 2.1 minimum when available), so food goals always win while Starving.

**Interrupt semantics (mandatory cleanup):** when a new goal replaces an
active plan — emergency or otherwise — the simulation must abort cleanly
before replanning:

- if an interaction is in progress, clear the target object's
  `IsOccupied` / `CurrentUser`
- free the occupied junction and release the plan's junction reservation
- reset execution state, invalidate the plan, clear the movement path
- emit `GoalInterrupted`

Skipping any of these leaks occupancy/reservations permanently.
Starving is a status only: needs still clamp at their bounds and there is no
death or damage in v1.

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

- Eat (consume from inventory)
- GetFood (pick up food from the world, iteration 2)
- Sit
- Sleep
- Socialize (talk to another NPC, iteration 4 — see 28.15A)
- Idle

**Optional if ready:**

- Shower

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

**v1 candidate filter (iteration 3):** a perceived object qualifies only if it
is reachable, offers the goal's interaction, and is not occupied by another
NPC (occupied-by-self counts as available — an NPC mid-interaction must not
lose its own target). This is the RequiresObjectFree condition from 29.6
applied at selection time; junction reservation remains the race arbiter.

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

### 27.18A Spatial Object Memory v1 (Iteration 5)

Implemented subset: **SeenObject spatial memory** (27.11) — enough to make
limited perception (22.7) livable. Interaction success/failure records and
the float strength model are deferred; v1 uses a TTL instead.

| Parameter | Value |
|---|---|
| Perception radius (objects) | hex distance <= 2 tiles |
| Memory record | per-object: definition id, tile, junction, LastSeenTick, IsPermanent |
| Storage | dictionary keyed by ObjectId (O(1) upsert on every sighting) |
| TTL for discovered (runtime) objects | 2400 ticks (10 min) since last seen |
| Seeded home knowledge | all bootstrap objects, `IsPermanent`, never expires |

**Rules:**

- **Formation/reinforcement (27.8/27.9):** every sighting upserts the record
  and refreshes `LastSeenTick`.
- **Negative evidence:** if a remembered object's tile is inside the current
  perception radius but the object no longer exists, the record is removed
  (`MemoryForgotten` trace). Belief survives only where the NPC cannot see.
- **Stale-memory discovery:** walking to a remembered object that turns out
  to be gone fails the plan, removes the record, and re-decides — the
  emergent "go check, discover it's gone" behavior.
- **Memory vs perception (27.14):** memory entries enter the same perceived
  list flagged `FromMemory`, with occupancy assumed free. Because memory can
  be wrong, execution must guard interaction start against an actually
  occupied object (fail + cooldown instead of stomping the occupant).
- **Foraging (27.11):** when GetFood finds no known food item, the NPC walks
  to the nearest known producer (`Produce != null`, e.g. an apple tree) —
  a move-only plan with no interaction. Arriving brings dropped fruit into
  perception radius. If already at the producer and no fruit is visible,
  GetFood takes a normal 40-tick cooldown — the NPC "waits by the tree".

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

**Iteration 4 implementation note (2026-07):** implemented — relationship
dictionary (Trust/Familiarity/Affinity per pair, Authority deferred), agent
perception, Social need decay, and the direct Talk interaction below.
**Embarrassment is consciously deferred**: no privacy-sensitive action
(Shower etc.) exists yet, so the mechanic would have no trigger; it returns
together with the Shower object.

### 28.15A Talk v1 — Concrete Rules (Iteration 4)

| Parameter | Value |
|---|---|
| Social need decay | -0.008 per slow tick (higher Social = better) |
| Talk duration | 16 ticks (4 s) |
| Social gain | initiator +0.40, listener +0.25 |
| Relationship gain (both directions) | Familiarity +0.05, Affinity +0.05 |
| Talk range at start | 4 × hex radius (world distance) |
| Affinity decision feedback | Socialize SocialModifier = 0.1 × best target Affinity |
| Failure / rejection | goal cooldown 40 ticks (23.10 rules apply) |

**Flow (28.8 "keep coordination simple"):**

- Socialize goal is available when a reachable, non-busy, **stationary**
  agent is perceived (busy = mid-interaction other than Talk). Walking agents
  are not talk targets — v1 explicitly avoids moving-target chases; the wide
  talk range absorbs small drift between planning and arrival.
- **Invitation handshake (28.8 made concrete):** when the initiator's plan is
  built it *claims* the target (`PendingTalkFrom = initiator`). A claimed NPC
  accepts by waiting in place: it aborts its own active plan (a "polite
  interrupt"), holds goal None, and does not initiate talks itself. The wait
  breaks on: initiator no longer targeting it (self-healed every decision
  pass), the waiter entering Starving, or a 120-tick timeout
  (`TalkWaitTimeout` trace).
- Plan: reserve a free passable neighbor junction of the target's current
  junction → MoveToJunction → Interact(Talk) with a *target agent* instead of
  a target object (`NPCPlanState.TargetAgentId`).
- **Partner choice (28.6, iteration 8):** among available targets the NPC
  prefers the one it likes most (highest affinity); distance is only a
  tie-break. Friendship therefore self-selects — and a disliked housemate
  gets approached only via the loneliness override.
- **In-flight goal protection:** while a talk plan is active (walking or
  talking), the per-tick availability scan must not zero out the Socialize
  score — transient target movement is not "target gone"; the plan validates
  its target at arrival. Without this the initiator's own plan gets
  interrupted by score collapse every time the target shifts.
- At arrival the initiator validates: target still within talk range, not
  busy, not Starving, not walking. Any failure → `InteractionRejected` /
  out-of-range trace, plan aborted, Socialize on cooldown.
- At completion both sides receive Social and relationship gains and
  `TalkCompleted` / `RelationshipChanged` events are emitted.
- Accepted v1 simplification: a listener who walks away mid-talk still grants
  full effects (talks are 4 s; not worth partial-effect machinery yet).

### 28.15B Talk Outcomes & Conflict v1 (Iteration 7)

Talks no longer always succeed: relationships gain real dynamics — quarrels,
resentment, avoidance, and reconciliation. Affinity becomes signed
(**-1..+1**, was 0..1); Familiarity stays 0..1 (you know your enemies too).
Trust still does not evolve in v1.

**Talk outcome roll (deterministic):** at talk completion a quarrel chance is
computed and resolved with a stateless hash of (tick, initiator id,
listener id) — no RNG state, resume-safe.

| Parameter | Value |
|---|---|
| Quarrel base chance | 0.10 |
| Irritability term | +0.35 × mean of both participants' max(Hunger, 1-Energy) |
| Friendship protection | -0.25 × max(0, mean mutual Affinity) |
| Hostility spice | +0.10 × max(0, -mean mutual Affinity) |
| Clamp | 0.05 .. 0.60 |

Retuned after the first soaks: the crankier-participant max made nearly every
talk risky (41 % quarrels → net-negative talk economics → guaranteed
hostility), and symmetric affinity influence made reconciliation hopeless
(mutual -1.0 pushed the chance to the old 0.75 cap). With the mean + split
terms, a calm neutral talk quarrels ~20 % (slightly net-positive), friends
are protected, and a reconciliation talk succeeds ~3 times out of 4 —
relationships can genuinely travel both directions.

| Outcome | Effects (both directions unless noted) |
|---|---|
| Success | Social +0.40/+0.25; Familiarity +0.05; Affinity +0.05 |
| Quarrel | Social +0.15/+0.10 (contact, but draining); Familiarity +0.05; Affinity -0.12; Embarrassment +0.30 both; initiator gets a Socialize cooldown (23.10) |

**Refusal by dislike (28.9/28.10):** the listener refuses when its affinity
toward the initiator is below **-0.25** — unless the listener itself is
lonely (Social < **0.25**), which overrides the grudge ("reconciliation
talks"). A rejected initiator loses **-0.05** affinity toward the rejector —
**but only for personal refusals** (dislike). Neutral refusals (busy,
starving, walking) cost nothing: taking offense at "sorry, busy" turned out
to be the engine of an irreversible hostility spiral (157 rejections × -0.05
dwarfed every possible gain in the first soak run).

**Grudges fade (asymmetric):** affinity drifts toward 0 — by **0.003 per
slow tick** (~0.45/day) on the negative side, but only **0.001 per slow
tick** (~0.15/day) on the positive side. Without any drift a -1.0 lock-in is
permanent (the refusal gate blocks the very talks that could heal it); with
*symmetric* drift friendships could never warm past ~+0.2 because the decay
outran talk gains. Asymmetry matches life: resentment cools in a day or two,
while friendship needs only light upkeep. A deep grudge (-1.0) becomes
talkable (-0.25) in under 2 days.

**Resentment from contention:** arriving at an object occupied by another
NPC (`InteractionBlocked`) costs **-0.08** affinity toward the occupant —
scarcity breeds friction organically.

**Embarrassment (28.5):** quarrels raise Embarrassment by 0.30; it decays
0.02 per slow tick; the Socialize score takes
`SocialModifier = 0.1 × best target affinity - 0.3 × Embarrassment`, so a
freshly quarreled NPC keeps to itself for a while, and disliked targets
lower the urge (28.6).

**Expected dynamics:** cranky talks sour relationships; soured relationships
raise quarrel chance and refusals; avoidance starves the Social need; the
loneliness override forces a reconciliation attempt; a calm successful talk
heals. Relationships oscillate instead of saturating at eternal friendship.

### 28.15C Death, Grief & Remembrance (Iteration 15)

A housemate's death must not pass unnoticed.

**Corpse:** death spawns `corpse.npc` at the death site (in addition to the
dropped items, 31A.5A). `CurrentUser` stores *whose* corpse it is (the field
is unused on corpses otherwise). The body decays away after **4800 ticks**
(2 days, `CorpseSystem` on the ResourceAmount-as-timer pattern of 29E.3).

**Learning of death:**

- **Witnessing** — any living NPC within 6 tiles at the moment of death
  grieves immediately.
- **Discovery** — perceiving a corpse not yet grieved for triggers grief on
  sight (per-NPC `GrievedCorpses` set prevents re-triggering).

**Grief (scaled by the relationship):**

| Effect | Value |
|---|---|
| Social need | -max(0.15, 0.3 + 0.3 × affinity toward the deceased) — friends hurt hard, even enemies' deaths disturb |
| Comfort | -0.2 |
| Mourning period | 2400 ticks (1 day): Socialize score takes -0.2 (withdrawal) |
| Fear | the death site enters danger memory (29C.4A) — explore/food avoid it |

**Mourning (closure):** while grieving, the `Mourn` goal (score 0.7 —
the ritual must survive the long walk to the body against everyday needs;
0.5 kept getting interrupted mid-pilgrimage until the corpse rotted)
targets the corpse with the existing `Observe` interaction (16 ticks).
Completing it ends the mourning period early ("простился") with a small
Comfort recovery (+0.1). If the corpse decays before anyone comes, grief
simply times out.

### 28.15D Burial & Graves (Iteration 16)

The death arc closes: a body can be laid to rest, and fear becomes memory.

**Burial:**

| Rule | Value |
|---|---|
| `Bury` goal | any housemate who perceives/remembers a corpse; score 0.6 (below Mourn 0.7 — rite first, burial after) |
| Interaction | `Bury` on the corpse, 20 ticks, no tool needed in v1 |
| Result | corpse despawns → **`grave.npc` spawns at the same junction** (permanent, never decays; `CurrentUser` still records who lies there) |
| Closure | the burier's mourning ends; Comfort +0.15 |
| Sanctification | the death tile is removed from **every living NPC's** danger memory — the place is no longer frightening, it is sacred |

**Grave visits (remembrance):** the `Mourn` goal widens — when *not*
grieving but lonely (Social < 0.35) and a grave is known, it targets the
grave (Observe, 12 ticks, score 0.3): Comfort +0.1, **Social +0.15** — the
dead keep a lasting social presence; lonely housemates come to talk to the
grave.

**Loot, not inheritance:** the deceased's dropped gear is simply
**ownerless** — ordinary world items at the death site that anyone may pick
up, wear, or use through the normal PickUp/Dress goals. There is no
ownership or inheritance concept; the grave's `CurrentUser` records only
*who lies there* (identity for grief), never possession of the items around
it.

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
    PickUp, // take a world item into inventory (iteration 2; naming per 31A.3)
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

    // production runtime data (producers only, see 29A)
    public int NextProductionTick;
    public List<ObjectId> ProducedItems;
}
```

Examples of runtime-only data:

- occupied/free
- remaining food amount
- cleanliness
- broken state
- temporary lock state
- production timer and produced item ids (flora, see 29A)

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

## 29A. Flora & Produce System (v1 Minimal)

Added in iteration 2 (2026-07). Some world objects produce other world objects
over time: an apple tree periodically drops apples on nearby tiles. This is
the first system that creates and destroys world objects at runtime, so it
also defines the runtime object lifecycle rules.

### 29A.1 Producer Definition

Production is authored as optional static content on ObjectDefinition:

```csharp
class ObjectDefinition
{
    // ... existing fields ...
    public ProduceDefinition? Produce; // null for non-producers
}

class ProduceDefinition
{
    public string ProducedDefinitionId; // e.g. "food.apple"
    public int IntervalTicks;           // v1: 100 (25 s; was 160 before the
                                        // daylight-only rule of 19.7A halved
                                        // the production window)
    public int MaxConcurrent;           // v1: 3
    public int MaxDistanceTiles;        // v1: 1 (tree tile + direct neighbors)
}
```

A producer needs no interactions of its own — the tree itself is not eaten or
harvested in v1; only its dropped produce is.

### 29A.2 Production Rules

Production runs as a Slow-layer system (after needs/temperature):

- production runs only during daylight (Morning/Day phases, 19.7A); at night
  the timer is left untouched, so an overdue producer fires immediately at
  dawn ("morning apples")
- for each object whose definition has `Produce != null`:
  - if `world.Tick < NextProductionTick` → skip
  - on firing, set `NextProductionTick = Tick + IntervalTicks` immediately,
    so a failed drop also waits a full interval (no per-tick retry spam)
  - prune `ProducedItems` of ids no longer present in the world
    (eaten/picked produce frees cap slots)
  - if `ProducedItems.Count >= MaxConcurrent` → skip, trace
    `ProduceSkipped(CapReached)`
  - otherwise pick a drop spot and spawn the produced object, record its id
    in `ProducedItems`, trace `ObjectSpawned`

**Drop spot selection (deterministic):** candidate tiles are the producer's
tile plus its neighbors within `MaxDistanceTiles`, filtered to existing,
walkable, non-blocked tiles. Within a tile, pick the first junction (by slot
order) that is not blocked, not occupied, and not already the anchor of
another object on that tile. If no candidate exists → skip, trace
`ProduceSkipped(NoFreeSpot)`, retry next interval.

**Accepted limitation:** produce may land on a junction that is currently
unreachable for an NPC. Perception marks it `IsReachable = false`, it is never
targeted, and it still counts against `MaxConcurrent` — bounded garbage, not a
leak.

### 29A.3 Runtime Object Lifecycle

Iteration 2 introduces object spawn/despawn at runtime. Rules:

- **ID allocation:** hand-authored bootstrap objects keep ids below 1000;
  runtime-spawned objects take ids from `WorldState.NextRuntimeObjectId`
  (monotonic counter starting at 1000). No id reuse.
- **Symmetry:** spawn adds the object to the entity repository and every
  spatial cache (objects-by-tile); despawn removes it from exactly the same
  structures. No other system may add/remove objects directly.
- **Ownership:** despawn must not touch junction reservations — those are
  owned by NPC plans and are released by plan invalidation/interrupt cleanup
  (see 23.17, 26.16).
- **Observability:** spawn and despawn emit `ObjectSpawned` /
  `ObjectDespawned` trace events (view layer contract: 31.15).
- **Dangling targets:** any plan targeting a despawned object must fail
  safely: the execution layer detects the missing target, releases the plan's
  junction reservation, invalidates the plan, and lets the next decision pass
  re-decide.

### 29A.4 v1 Content

- `tree.apple`: no interactions, tag `Flora`,
  `Produce = { "food.apple", 100, 4, 1 }` (retuned in iteration 6 for
  daylight-only production: the dark half of the day adds ~+1.5 Hunger with
  zero supply, so nights are survived on ground stock. Interval 160→100,
  concurrent cap 3→4 for a deeper dusk stock, and the apple itself feeds more:
  Eat HungerDelta -0.45→-0.60)
- `food.apple` (existing): Eat (8 ticks, HungerDelta -0.45,
  ComfortDelta +0.05) plus new PickUp (see 29B)

## 29B. Inventory & Item Carrying (v1 Minimal)

Added in iteration 2 (2026-07). The vertical slice scope (33.7, 34.18)
deliberately excluded "deep inventory systems"; this section pulls forward
only the minimal carrying model needed for the survival loop:
pick food up → carry it → eat it.

### 29B.1 Inventory State

```csharp
class InventoryState
{
    public List<string> Items; // definition ids, no per-item runtime state
    public int Capacity;       // v1: 2
}
```

Items are plain definition-id strings. A carried item has no world position,
no junctions, no occupancy — it exists only in the list.

### 29B.2 PickUp Contract

`PickUp` is a normal object interaction (29.3) on the world item:

- plan shape: MoveToJunction(item junction) → Interact(PickUp)
- duration: 4 ticks (1 s); no need deltas
- on completion: the world object despawns (29A.3) and its definition id is
  appended to the actor's inventory
- hard-gated (23.11) when the inventory is full or already contains food —
  v1 NPCs do not stockpile
- **restraint gate (iteration 6):** also hard-gated below Hunger 0.35 — NPCs
  do not harvest food they do not need. Without this they vacuum every apple
  the moment it drops, the ground stock is empty by dusk, and the
  production-free night (19.7A) becomes a nightly famine.

Goal mapping: `GetFood → PickUp` on the nearest reachable object offering it.

### 29B.3 Eating From Inventory

`Eat` becomes an inventory-consumption goal:

- availability = inventory contains an item whose definition has an Eat
  interaction (perceived world food no longer gates Eat)
- plan shape: a single in-place `ConsumeInventoryItem` step — no target
  object, no junction reservation, executable anywhere
- execution runs the item's own Eat InteractionDefinition (duration and
  effects come from content, e.g. apple: 8 ticks, Hunger -0.45), then removes
  the item from the inventory; trace `ItemConsumed`

### 29B.4 Out of Scope in v1

- Drop / transfer / theft
- stacking, item counts, weight
- per-item runtime state (freshness, durability)
- inventory UI beyond a debug readout
- clothing equip flow migration (31A keeps its own equip model for now)

## 29C. Wildlife, Health & Combat (v1 Minimal — Iteration 9)

Added 2026-07. The world gains real stakes: health, hostile wildlife (dogs),
armor, and death. Also introduces the **world seed** — runs are deterministic
per seed, but different seeds produce different lives.

### 29C.1 World Seed & Randomness

`WorldState.Seed` comes from bootstrap (`Simulation.Seed`). All chance rolls
(talk outcomes 28.15B, dog behavior, explore urges) are stateless hashes that
mix the seed, so a run is exactly reproducible for a given seed and diverges
across seeds. The presentation layer may randomize the seed per session; the
simulation itself stays deterministic.

### 29C.2 Health

| Parameter | Value |
|---|---|
| `NPCState.Health` | 0..1, starts 1.0 |
| Regeneration | +0.02 per slow tick while Hunger < 0.5 ("eat and rest to heal") |
| **Starvation / dehydration** | while Hunger >= 0.95 OR Thirst >= 0.95, every body part loses **-0.03 per slow tick** (both at once: -0.05); Health follows the body mean, a destroyed vital ends it (`StarvedToDeath` trace) |
| Death | Health <= 0 → NPC is removed from the world (`NpcDied` trace) |

**Why the drain matters (iteration 30 bug fix):** before this, an NPC whose
needs maxed out (food/water unreachable) simply hung forever — Health never
fell, so it never died and never freed its slot. A survival sim must have
consequences: unmet critical needs cost HP until the body gives out. The
0.95 gate sits well above the 0.85 "starving" appraisal, so a healthy colony
that briefly spikes hunger loses nothing and recovers; only a genuinely stuck
agent drains to death (~200 s of continuous starvation).

Death cleanup must be total: entity repository, tile caches/occupancy, all
junction reservations/occupancy owned by the NPC, and any world object it was
using. Other NPCs' relationships and invitations self-heal (28.8 rules).

### 29C.3 Dogs

Dogs are lightweight creatures (`DogState`), not NPCs — a three-state machine
(Roam / Chase / Fight), no needs, plans, or perception pipeline.

| Parameter | Value |
|---|---|
| Population | max 2 alive; initial spawn at world start, respawn check every 3 days |
| Spawn placement | random wilderness junction, hex distance >= 5 from every NPC |
| Roam | ~20 % chance per medium tick to hop to a random passable neighbor junction |
| Aggro | NPC within hex distance 2 → chase |
| Chase | one junction hop per medium tick (slower than a walking NPC) |
| Attack range | same or adjacent junction |
| Dog HP | 0.9; NPC strike-back 0.15 per medium tick → dog dies in ~6 s |
| Dog damage | 0.08 × (1 − EquippedArmor) per medium tick |

**Intended balance:** one dog costs an unarmored NPC ~0.5 Health — a scary
but survivable fight followed by days of healing. Two dogs at once out-damage
the kill rate — near-certain death. Armor tilts both fights.

**Combat is reactive in v1:** a fought NPC has its plan aborted and is held
in place (`IsFighting`); it strikes back automatically at one adjacent dog.
No flee, no planned hunting, no fear memory yet — those are the next layers.
Fighting NPCs count as busy for social purposes.

### 29C.4 Armor & Overheating

`InteractionEffects` gains `ArmorDelta` → `NPCState.EquippedArmor` (0..1,
fraction of incoming damage absorbed). Armor pieces are Dress-able clothing
scattered in the wilderness — exploration pays.

Warmth stops being a pure good. Effective temperature, graded pressure:

```
effectiveTemp = GlobalTemperature + EquippedWarmth * 10
cold (effectiveTemp < 12): ThermalDiscomfort += min(0.09, (12 - effectiveTemp) * 0.02)
hot  (effectiveTemp > 20): ThermalDiscomfort += min(0.09, (effectiveTemp - 20) * 0.02)
else: ThermalDiscomfort -= 0.03
```

A coat (+0.4 warmth = +4°) turns the coldest night (6°) into a mild 10°
(pressure 0.04 instead of the naked 0.09 cap); the same coat pushes the 18°
afternoon to 22° (pressure 0.04), and coat + leather armor (+6°) to 24°
(0.08) — an overdressed NPC overheats at midday and pays for protection with
discomfort.

**Equip rules (v1, from the first soak):**

- Warmth and armor apply as **max(current, item)**, not a sum — repeated
  dressing must not stack a coat into a furnace and 1.0 armor (the first
  soak produced invulnerable, permanently overheated NPCs).
- `Dress` is hard-gated below ThermalDiscomfort 0.3 **and** when the
  effective temperature is not on the cold side (>= 14): an overheated NPC
  reaching for more clothes was a self-reinforcing doom loop.

**v1 limitation:** there is no Undress action yet — dressing decisions are
one-way within a run; threat-aware armor decisions (don armor because dogs
are near) are deferred until fear/memory of wildlife exists.

### 29C.4A Fear, Flight & Sanctuary (Iteration 10)

Combat stops being a stand-and-trade: NPCs assess, flee, remember, and
prepare.

**Sanctuary:** dogs never enter Indoor tiles — roaming, chasing, and
spawning all skip indoor junctions. Home is safe; "run home" is a real
strategy, not a metaphor.

**Flight:**

| Parameter | Value |
|---|---|
| Flee trigger | Health < 0.5 OR >= 2 dogs adjacent |
| Flee behavior | abort plan → `Flee` goal: move-only plan to the nearest reachable indoor junction |
| While fleeing | no strike-back; bites still land (escape has a price) |
| Escape works because | NPC walks ~1.5× dog hop speed; dogs lose targets beyond aggro+3 |
| Flee ends | on arrival (plan completes → normal life resumes) |

The decision system holds the `Flee` goal unconditionally while its plan is
active — nothing outbids running for your life.

**Danger memory (27 integration):** every aggro/first bite records
`{tile, tick}` in `MemoryState.Dangers` (deduped by tile, TTL 2400 ticks =
1 day, cap 8). Effects:

- **Explore avoidance:** wander destinations within 3 tiles of a fresh
  danger are filtered out — "меня там покусали, обойду".
- **Food avoidance (iteration 11 hard lesson):** GetFood pickup targets and
  forage destinations within 2 tiles of a fresh danger are skipped too —
  **unless the NPC is Starving** (desperation overrides caution, consistent
  with 23.17 emergencies). Without this, a dog pair camping a fruit tree
  became a death trap: hungry NPCs walked to the nearest tree one after
  another and died at the same spot — one even looting the previous
  victim's armor on the way — a full colony wipe on day 4.
- **Threat-aware dressing (the 29C.4 deferral, now real):** with any fresh
  danger memory and EquippedArmor < 0.3, `Dress` becomes available even in
  warm weather, scored at need >= 0.6, and the planner prefers the
  highest-ArmorDelta reachable item instead of the nearest — the NPC
  consciously puts on armor *because the world got dangerous*, accepting the
  overheating cost (29C.4).

### 29C.5 Explore Goal (23.2 addition)

`Explore` sends an idle NPC to a random reachable junction 3–8 tiles away
(move-only plan). Score = 0.1 base + 0.05 need + a seeded jitter of 0..0.2
that changes every 160 ticks — wandering urges vary run to run (retuned from
0.1 + 0..0.2 @ 80 ticks: the first soak produced 16 trips/NPC/day and hungry,
twitchy wanderers; a few trips per day is the intent). Explore loses
to any pressing need but beats Idle roughly half the time, so calm NPCs
drift outward, discover armor, trees — and dogs. Discoveries persist via
spatial memory (27.18A).

## 29E. Thirst, Water & Fire (Iteration 13)

The survival chain deepens: drink or suffer, and boiled water is worth the
logistics of fire.

### 29E.1 Thirst Need

| Parameter | Value |
|---|---|
| `NPCNeeds.Thirst` | 0..1, higher = worse (like Hunger); starts per bootstrap |
| Decay | +0.025 per slow tick |
| Restraint gate | Drink goal gated below Thirst 0.35 (29B.2 pattern) |
| Dehydrated status | enter >= 0.85, clear < 0.60; +1.0 boost to Drink (23.17 pattern) |
| Death | none in v1 — misery only, like hunger |

### 29E.2 Water Sources

- **Pond** (`water.pond`, tag RawWater, 2 in the wilderness): `Drink`
  interaction, 10 ticks, Thirst -0.6 — and a **30 % sickness roll** (seeded
  hash): Torso -0.15, Comfort -0.2, `GotSick` trace. Raw water is a gamble.
- **Lit campfire + pot**: `Drink` (boiled), 12 ticks, Thirst -0.8,
  Comfort +0.05, no risk. Requires the campfire lit and `tool.pot` in the
  drinker's inventory. The planner prefers boiled over raw whenever it is
  actually available.

### 29E.3 Campfire & Fuel

- `campfire.spot` (fixed, in the yard). **Fuel = `ResourceAmount`** in
  ticks; lit = fuel > 0. A `FireSystem` (Slow) burns 16 ticks of fuel per
  slow tick and traces `FireOut` at zero.
- `Fuel` interaction (8 ticks): consumes one `resource.firewood` from the
  actor's inventory, +1200 ticks of fuel (half a day per log).
  **Lighting a dead fire requires `tool.lighter` in inventory**; topping up
  a burning fire does not.
- Firewood: pickable logs scattered at bootstrap plus two
  `forest.deadfall` producers (Produce = firewood, interval 300, cap 3 —
  the 29A pipeline reused verbatim). Renewable but effortful.
- Tools (`tool.lighter`, `tool.pot`, tag Tool): pickable, multi-use, never
  consumed; placed near the home at bootstrap ("basics are found quickly").
- Inventory capacity grows 2 → 4 (food + lighter + pot + a log).

### 29E.4 Goal Chain (emergent, no multi-step planner)

Four single-step goals chain through world state, not through plans:

| Goal | Available when | Need value |
|---|---|---|
| Drink | (lit campfire && pot) OR pond known; Thirst >= 0.35 | Thirst |
| GatherTools | lighter or pot missing && a Tool item known reachable && space | 0.25 + 0.2 × Thirst |
| GatherWood | no log carried && wood known && campfire fuel < 600 && space | 0.2 + 0.3 × Thirst |
| TendFire | log carried && campfire fuel < 600 && (fuel > 0 OR lighter carried) | 0.25 + 0.3 × Thirst |

Target selection is tag-filtered per goal (GetFood → Food, GatherWood →
Firewood, GatherTools → Tool), so food pickups and wood pickups never cross.
The expected life: early days of risky pond water and occasional sickness,
then tools get collected, the fire gets kept, and boiled water becomes the
norm — with relapses to the pond when the fire dies far from a log.

## 29F. Hunting & Crafting (Iteration 14)

Meat closes the survival chain: prey → weapon → hunt → cook → eat, plus the
first clothing craft. The campfire doubles as the workbench.

### 29F.1 Rabbits

Prey wildlife (`RabbitState`, mirrors 29C.3 dogs but flees):

| Parameter | Value |
|---|---|
| Population | max 4; respawn check every 2400 ticks (rabbits breed fast — retuned from 3/3600 after a 1-kill-per-10-days first soak); spawn >= 3 tiles from NPCs, never indoors |
| Idle | grazes; 30 % chance per medium tick of a random hop |
| Flight | NPC within 2 tiles → hops to the neighbor junction farthest from the nearest NPC |
| Spooked | after a missed kill attempt: immune + panicked for 150 ticks |

NPCs perceive rabbits by direct proximity scan (hex distance <= 4) — no
rabbit memory in v1. (Radius 5-6 was tried in the crab era to raise
encounter rates: the longer chases dragged hunters into dog country and
seeds wiped — hunts stay rare-but-real at 4.)

### 29F.2 Hunting

- `Hunt` goal: needs a **spear carried**, a visible non-spooked rabbit,
  Hunger >= 0.3, and no raw meat already in hand (a carried coconut does
  NOT block the hunt — meat is also hide, and hide is pants and a bow;
  with the old any-food gate hunters basically never had an open window
  and the whole leather/bow tier lay dormant). Hunting is for the
  PECKISH: available at **0.3 <= Hunger < 0.55** — a truly hungry NPC
  takes the sure meal, not a chase with a 50 % roll. Inside the window
  the score **0.3 + 0.5 × Hunger** genuinely outbids GetFood (= Hunger);
  the availability window, not the curve, protects mealtimes. (History:
  0.15 + 0.5 × Hunger was strictly dominated — zero hunts across four
  seeds; an uncapped 0.3 + 0.5 × Hunger caused starving storms.)
- The plan is a move-only chase to the rabbit's junction; the rabbit flees;
  re-planning each arrival produces a genuine pursuit (NPC walk speed beats
  hop speed).
- Kill resolution is automatic on adjacency (RabbitSystem): seeded roll,
  **50 % kill / 50 % miss**. Miss → rabbit spooked, hunter gets a Hunt
  cooldown. Kill → rabbit despawns; **1 raw meat + 1 hide auto-loot** into
  the hunter's inventory (overflow drops at feet).

### 29F.3 Crafting at the Campfire

`Craft` interaction on `campfire.spot` (12 ticks); the recipe is selected by
the goal; ingredients validated at interaction start:

| Goal | Ingredients | Output | Extra requirement |
|---|---|---|---|
| CraftSpear | 1 firewood | `tool.spear` (inventory) | none (whittling) |
| CookMeat | 1 raw meat | `food.meat_cooked` (inventory) | fire lit |
| CraftLeather | 1 hide (retuned from 2: loot splits across hunters, two-on-one-NPC never happened in soaks) | `clothing.leather_pants` — **worn immediately** | none |

- **Raw meat is inedible** — `food.meat_raw` has no Eat interaction at all;
  the only path to calories is through the fire.
- `food.meat_cooked`: Eat, Hunger -0.9 — a real meal versus the apple's -0.6.
- `clothing.leather_pants`: Wear layer, covers Pelvis + LegL + LegR, warmth
  0.2, **armor 0.2 — the first leg protection** (dogs bite legs 60 % of the
  time, 19.3C).
- Goal scores: CookMeat 0.3 + 0.4 × Hunger; CraftSpear 0.2 + 0.2 × Hunger
  (gated on not owning one); CraftLeather 0.35 flat (gated on a hide in hand and
  not already wearing pants).
- Inventory capacity 4 → **7** (food, lighter, pot, log, spear, meat, hide).

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

Target contract — driven by simulation events:

- `OnEntitySpawned` → create view
- `OnEntityDespawned` → destroy view
  Views should not create entities themselves.

**v1 implementation (iteration 2): snapshot diff.** The renderer creates a
view for every snapshot id it does not know yet, and destroys views whose id
is missing from the current snapshot. This is equivalent for a small world and
avoids an event-delivery channel; the event-driven contract above remains the
later target. The simulation still emits `ObjectSpawned` / `ObjectDespawned`
trace events for observability (see 29A.3).

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

Iteration 23 hardening (first real profile, 285 tiles / ~14k junctions):

- **Snapshot export is cached per simulation tick** in the runner —
  consumers (renderer, debug panel, point overlay) may poll every frame
  but the world is serialized at most once per tick. Exporting the full
  snapshot per consumer per frame cost ~180k GC allocations/frame.
- **Junction markers are debug-only** (~14k sphere primitives = ~10M
  triangles): off by default, toggleable via the renderer's
  `_showJunctionMarkers` flag.
- **The point overlay culls by camera distance** (badges and GL edges
  within `DebugOverlayRange` of the camera focus only, hard badge cap)
  and refreshes badges only when the snapshot tick changes.

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

### 31A.5A Wearables as Items (Iteration 11)

Clothing stops being an infinite prop and becomes a real, exclusive item.

**Model:**

- `NPCState.WornItems` — list of worn definition ids. Equipment values are
  recomputed from it: `EquippedWarmth / EquippedArmor = max over worn items`
  (consistent with 29C.4's no-stacking rule).
- **Dress consumes the world object** (like PickUp): the garment despawns
  into `WornItems` — only one NPC can wear the coat. Wearables become
  contended resources like beds (24.3 filtering + reservations apply).
- **Undress** (new goal + in-place `UndressItem` step, 6 ticks): the item is
  removed from `WornItems` and **spawned back into the world at the NPC's
  current junction** — clothes migrate around the map, others can pick them
  up where they were dropped.
- **Death drops everything worn** at the death site (29C.2 cleanup): killed
  in the wild wearing heavy armor → the armor lies where the dogs won,
  retrievable by whoever dares.

**Undress decision (the 29C.4 tradeoff made complete):**

| Rule | Value |
|---|---|
| Trigger | effectiveTemp > 20 AND ThermalDiscomfort >= 0.4 |
| Removal candidate | the warmest worn item that is *safe to remove* |
| Safe to remove | item has no armor, OR no fresh danger memory (29C.4A) |
| If nothing is safe to remove | keep sweating — protection beats comfort under threat |

An NPC that armored up after a dog attack overheats at noon; once the danger
memory fades (1 day), it finally takes the armor off — and until then it
visibly pays for safety.

### 31A.5B Layered Wear (Iteration 12, molly_copy port)

Clothing gains the molly three-layer model, adapted to the headless sim
(WearSlots simplified to the 7 body parts of 19.3C):

```csharp
enum WearLayer { Underwear, Wear, Outerwear }

class ObjectDefinition
{
    // ... existing ...
    public WearLayer? Layer;        // null = not wearable
    public List<BodyPart> Covers;   // zones this garment occupies/protects
}
```

**Rules (mirroring molly BodyBones.Equip):**

- One item per (layer, body part). Dressing over an occupied (layer, part)
  **takes the old item off** — it drops at the NPC's feet like Undress.
- **Warmth = clamp01(sum over worn items)** — layering finally stacks
  (underwear 0.05 + coat 0.4 + heavy armor 0.25 = a genuinely hot outfit).
- **Armor is per part = max over items covering that part** — leather on the
  torso does nothing for a bitten leg. Protection now has anatomy.
- Every NPC starts wearing `underwear.cloth` (Underwear, Torso+Pelvis,
  warmth 0.05) — three instances exist, one per NPC, never contended.
- v1 content: coat = Wear layer, covers Torso+Arms (warmth 0.4);
  leather armor = Outerwear, Torso (armor 0.3, warmth 0.15);
  heavy armor = Outerwear, Torso+Pelvis (armor 0.5, warmth 0.25).
  Note: no garment covers legs yet — dogs bite low, and legs are naked
  until leather crafting arrives (iteration 14 roadmap).

### 31A.6 Summary

**Clothing System answers:**

What is the NPC wearing, how does that affect temperature and comfort, and how can clothing become part of world interaction?
It creates a bridge between inventory-like systems, environmental simulation, and visual presentation.

## 31B. Actor Visualization & Wardrobe (iteration 23)

The colony gets faces. Three Daz3D Genesis3Female actors are transferred
from the sibling project `/Volumes/ORICO/molly_copy` and bound to the three
simulated NPCs. The simulation stays byte-identical — this section is pure
presentation; `DisplayName`/`ActorMesh` on NPCState (19.3) are inert labels
the simulation never branches on.

### 31B.1 Identity

| NPC | DisplayName | ActorMesh (visual body) | Hair (auto-spawned) |
|---|---|---|---|
| 1 | Marta | Marta (`Daz3D/Martanaked`) | LowPonytail |
| 2 | Molly | Molly (`MollyMesh.mesh`, 385 MB standalone) | ShilohHair |
| 3 | Jolie | Jana (`Daz3D/Jana`) | JelikaHair_32434 |

Jolie wears Jana's body — the actor set has no Jolie; the name belongs to
the colony, the mesh to the source project.

### 31B.2 Source wear architecture (adopted)

The molly_copy wearing system is adopted with its serialization intact:

- A **wear item** is a prefab: root `Wear` MonoBehaviour + its own bone
  hierarchy under `hip` + one SkinnedMeshRenderer. `Wear.Construct()`
  parents every garment bone onto the matching body bone (by name) via a
  runtime `ParentConnection`, and swaps the renderer's mesh per actor from
  the serialized `WearConfig { actorName, scale, mesh }` list — one prefab
  fits every girl.
- **BodyBones** on the actor: bone-name map built from `hip`, an empty
  `Wear` child as attach container, a `hair` Wear prefab auto-spawned on
  Construct, and `Equip`/`TakeOff` with per-slot layer bookkeeping
  (underwear auto-hides under outer garments — same three layers as 31A.5B).

### 31B.3 Transfer rules (the "smart import")

- **GUID preservation**: every copied asset ships with its original .meta —
  all cross-references (meshes, materials, textures, prefab hair refs)
  survive verbatim. The GUID dependency closure is computed by script, not
  copied by folder guess.
- **Script adaptation, not copying**: only `Wear` and `BodyBones` are
  reimplemented (namespace `HexLive.UnityPresentation.Wearing`), stripped
  of Actor/FinalIK/localization/combat coupling, but keeping the exact
  serialized field names AND claiming the ORIGINAL script GUIDs
  (`7a660832…` / `eaba56e3…`) in their .meta — copied wear prefabs bind to
  the adapted classes with zero YAML edits. The `ActorName` enum order
  (Molly, Jolly, Marta, Tonny, Kshishtof, Jana, Masha, Rita) is preserved —
  `WearConfig.actorName` is serialized as an int. `ParentConnection` is
  runtime-added (never serialized) and needs no GUID claim.
- **Naked base prefabs**: stripped copies of the actor prefabs are authored
  under `Assets/Resources/HexLive/Actors/<ActorMesh>.prefab`. Kept: the
  full skeleton, body SkinnedMeshRenderer(s), Animator (repointed to our
  controller), adapted BodyBones, the empty `Wear` container. Removed: all
  gameplay/IK/camera/face MonoBehaviours (Actor, FinalIK, Daz3DInstance,
  BlendShape*, Agent/combat stack, NavMeshAgent, Rigidbody, colliders) and
  non-rig children (cameras, anchor points, `Points`). No clothing is baked
  into the prefab — the base is naked; even hair arrives at runtime.
- **Wardrobe subset**: only female, non-explicit items that map onto
  simulation wearables; the source's WearData ScriptableObjects and 1.2 GB
  library are NOT copied wholesale.
- **Excluded on principle**: all adult content (SEX* prefabs, related
  animation clips and items), male FCO_*/FAO_* gear, DynamicBone,
  combat/AI/camera/Firebase scripts.
- **FinalIK is transferred** (Assets/RootMotion, ~3 MB, user decision):
  `LookAtIK` and `FullBodyBipedIK` components stay on the actor prefabs
  with their serialized bone references intact — the girls keep living
  eyes and posture. The gaze mechanic is ported: the view layer drives
  `lookAt.solver.target` + weight profile (weight/body/head/eyes/clamp,
  molly_copy's LookAtConfig pattern) — talking NPCs look at each other,
  walkers glance along their path; weights ease in/out smoothly.

### 31B.4 Sim-to-visual wardrobe mapping

`clothing definition id -> wear prefab(s)` by Resources convention:
`Assets/Resources/HexLive/Wear/<definitionId>/` contains one or more wear
prefabs, all equipped/removed together as the visual of that sim item.

| Sim item | Visual prefab(s) |
|---|---|
| underwear.cloth | Panty_31415 + Bra_20266 |
| clothing.leather_pants | SkinnyJeans_24487 |
| clothing.coat | Jacket_7653 |
| armor.leather | DdlSlc_Top |
| armor.heavy | jacket_8867 |

### 31B.5 Renderer bridge

`HexWorldRenderer.CreateNpcView` instantiates
`Resources/HexLive/Actors/{snapshot.ActorMesh}` when present (primitive
capsule remains the fallback). A new `NpcActorView` component:

- `Construct(actorMesh)` → BodyBones.Construct (bone map + hair).
- Per snapshot: diffs `WornItems` against the currently equipped set →
  `Equip`/`TakeOff` of the mapped wear prefabs. Sim wetness/durability do
  not change visuals in v1.
- Gaze (LookAtIK): during a Talk interaction the view targets the partner's
  head; while walking, a point ahead on the path; otherwise the weight eases
  to 0 (animation-neutral idle gaze).
- Drives the Animator **from measured view motion**, not simulation
  status: the view samples its own root displacement and yaw delta each
  frame — feet move exactly when the body visibly moves (status-driven
  animation lagged a tick + damping: sliding starts, treadmill stops).
  Controller states: Idle / Walk / TurnLeft / TurnRight (clips
  `@Idle_Neutral`, `@WalkForward_NtrlFaceFwd`, `@TurnOnSpot` Left/Right A);
  params `Speed` and `TurnDirection` (−1/0/+1 from yaw rate when standing).
  One controller shared by all three girls via humanoid retargeting.
- `FullBodyBipedIK` ships **disabled** on the prefabs: its effector targets
  lived on components we strip, and an unfed solver freezes the pose (the
  "Marta slides in one pose" bug). It stays dormant until a mechanic feeds
  it; LookAtIK remains active.
- Positions/rotation stay owned by the existing renderer lerp — **the
  simulation is the only mover**. Root motion is disabled on the actor
  prefabs (the locomotion clips carry it) AND at runtime, and the actor
  body child is re-pinned to its view root every LateUpdate: without this
  the Animator and the renderer both moved transforms and the body drifted
  off its root on turns.
- Actor scale is normalized to the hex metric via a uniform view-scale
  factor.

### 31B.6 Verification

Compile-level: presentation csproj builds. Play-mode checklist (user):
three named girls render at their sim positions, walk with animation, hair
present, underwear from bootstrap visible, Dress/Undress in the sim
adds/removes garments on the body, wear conflicts swap visuals, death drops
leave the body naked. Headless soaks are unaffected (simulation untouched).

## 31C. World Object Visualization & Tropical Reskin (iteration 24)

The island stops being abstract: beds are beds, palms are palms, water has
depth, and the fauna/flora match the tropics. Part presentation, part small
simulation amendments — each amendment is listed here and in its deep
section.

### 31C.1 Simulation amendments

- **Coconuts, not apples** (29A): the home produce trees are palms;
  `food.apple` -> `food.coconut` (same Eat -0.6 / PickUp), `tree.apple`
  retired — `tree.palm` gains `Produce { food.coconut, 100, 4, 1 }`.
  Richer coconut mechanics (cracking them open) — a later iteration.
- **Crabs, not rabbits** (29F.2): the huntable animal is a crab; it spawns
  within 2 tiles of Water tiles (river bank), hops slower (they scuttle),
  and the trace/metric vocabulary follows (CrabSpawned/CrabKilled/…).
  Loot unchanged (meat + hide) — call the hide chitin when it matters.
- **Obstacle anchors** (20.15/24.3): `tree.big`, `tree.palm`, and
  `rock.boulder` **block their anchor junction** (spawn/bootstrap sets
  Blocked + TopologyVersion++; felling/mining unblocks the same way). NPCs
  no longer walk through trunks. Interaction plans with a blocked-anchor
  target approach a free passable **neighbor** junction instead — the
  execution range check is junction-adjacent by construction.
- **Produce rot** (29A): a dropped fruit that nobody picks up despawns
  after 2400 ticks (`ProduceRotted` trace) — unreachable-junction drops no
  longer litter the world forever.

### 31C.2 Bed & the lying pose

`bed_b.prefab` transfers from molly_copy (GUID closure, scripts stripped —
its AnimationLevelItem pattern is *adapted*, not copied: attach point +
override idle clip). Our version: the bed object view exposes an
`ObjectAttachPoint` (the source prefab's `point` child); while an NPC's
current interaction is `Sleep` and her execution target is that bed, the
actor view snaps to the attach point and plays the **Laying** animator
state (`Laying Breathless.fbx` idle). On wake the view releases back to
the renderer's pose flow. Same pattern reserved for Sit later.

### 31C.3 Object prefab convention

`HexWorldRenderer.CreateObjectView` first tries
`Resources/HexLive/Objects/<definitionId>.prefab`; the primitive composite
remains the fallback. Delivered prefabs: `bed.basic` (bed_b), `tree.palm`
(downloaded CC0 model), `food.coconut`. Everything else stays primitive
until it earns art.

### 31C.4 Water depth & sand

Water tiles render **sunken and translucent**: the hex top drops ~40 % of
tile height below ground level with an alpha-blended blue material — an
NPC wading the shallows visibly sinks to the ankles (the simulation
already walks through Water tiles; this is pure visual depth). Walkable
tiles adjacent to water render sand-colored — the river gets banks.

### 31C.5 Post-play fixes (user report)

- **Bed teleport**: the transferred bed prefab carried a baked scene offset
  ({-20.6, 0, 3.5}) in its root — the visual (and its lying attach point)
  rendered 20 units from the logical anchor, so a sleeper "vanished into a
  red sphere" across the map. Root zeroed; additionally the actor view
  refuses an attach point farther than 3 body heights (teleport guard).
- **Legible fallbacks**: the catch-all red sphere is gone — water spots are
  flat blue discs, the campfire an ember-orange disc, stones gray, tools
  and resources brown boxes, graves dark slabs, corpses lying capsules;
  the unknown-object default is a neutral gray sphere.
- **Mutual obstacles (spec 24.3 amendment)**: NPC pathfinding avoids
  junctions currently occupied by a standing housemate (falls back to the
  direct path when fully enclosed — never hard-stuck); a mover whose next
  step is occupied waits up to 40 ticks, then re-paths around. Nobody
  shares a standing spot or ghosts through a neighbor.
- **Talk spacing & facing (spec 28.8 amendment)**: the initiator approaches
  a free junction ~0.9 hex radius from the partner (an arm's-length circle,
  not the adjacent sub-grid point), and when the talk starts both
  participants turn to face each other (sim rotation; the view's gaze
  already followed).

### 31C.7 Water etiquette & solid furniture (iteration 26)

- **Animals never enter water** (29C/29F amendment): dog and crab movement
  (roam, chase, flee, hop) rejects junctions whose tiles are entirely
  Water. Shore junctions (mixed land+water) stay walkable — crabs skitter
  along the very bank but do not swim. Crab spawn obeys the same rule.
- **Drinking happens from the bank** (29E amendment): when an interaction
  target's anchor junction lies on a Water tile, the plan approaches the
  nearest free passable junction on DRY land instead — the girl stands on
  the bank and draws river water from the neighbor tile. Reuses the
  obstacle "stand beside" mechanism.
- **The bed is solid** (24.3 amendment): `ObjectDefinition.ObstacleRadius`
  (world units) blocks every junction within that radius of the anchor —
  not just the anchor itself. `bed.basic` gets Obstacle + radius **0.3 x
  hex radius** — the bedroll core; the first soak at 0.45 blocked so much
  of the small home interior that traffic starved the colony (starving
  38 -> 22 on the control run). Nobody paths through the bed and the
  sleeper approaches from beside it. Blocked junctions are recorded
  per object (`WorldObjectState.BlockedJunctions`) so despawn unblocks
  exactly what the object blocked — overlapping obstacles and walls stay
  intact.

### 29G Ground Rest & The Crafted Bed (iteration 28)

The land itself is furniture — worse than the real thing, but always there.

**Sitting anywhere**

- The Sit goal no longer requires a chair: chairs are preferred candidates;
  with none reachable the girl sits on the ground at a free junction.
- **Ledge sitting**: junctions on a boundary where adjacent tiles differ by
  >= 1 elevation are scenic spots — sitting there means legs dangling over
  the edge. Ground-sit planning prefers a ledge within 4 tiles.
- Comfort: chair +0.4 / ledge +0.25 / plain ground +0.15 (Energy +0.05 on
  the ground vs +0.1 on a chair). Duration 70 ticks and the post-sit
  cooldown are shared with 31C.7A.
- At sit start she faces the lower side of the ledge (sim rotation).
- **Sitting is leisure, not survival**: the Sit score is (1-Comfort) x 0.5
  — an idle-time filler that never outbids fire and food chores (at full
  weight Sit >= 0.4 by construction of its own gate and it starved the
  economy) — and Sit is unavailable during sleep hours (the Sit->Sleep
  churn was 47 interrupts/soak).

**Lying on the grass**

- Sleep works with no bed, but only for the tired or after dark:
  availability = Energy < 0.45 OR Evening/Night phase. Unconditional
  availability turned naps into a universal time sink (73/soak, the fire
  never lit).
- The girl lies at the CENTER of a free hexagon — walkable, not water, no
  objects, no other claimant. The spot is anchored to HOME (the campfire
  tile, search radius 6), and **indoor floor beats proximity**: outdoor
  night camps wherever the dark caught her got the colony mauled by dogs
  (sanctuary rule 29C.4A protects indoor sleepers).
- Ground sleep: 100 ticks, Energy +0.5, Comfort 0 (bed: +0.5 / +0.2) — a
  night is a night; the bed's edge is comfort, not energy (+0.35 ground
  energy created a poverty trap: 160 naps/soak and no time to live). The
  sleeping metabolism (31C.7A) applies to both.
- **Lying claims the body's footprint**: junctions within 0.5 x hex radius
  of the lying spot register in `NPCState.ClaimedJunctions` for the
  duration — housemates path around a sleeper (the same avoidance that
  respects standing NPCs). Claims release on completion, interruption, or
  death.

**The bed must be earned**

- Bootstrap beds are GONE — the colony's first nights are on the grass.
- `CraftBed` goal (score 0.6): 2 logs + 3 palm leaves at the campfire ->
  a bedroll placed on a free junction near the fire (the CraftRack
  placement pattern). Available when no bed is known reachable, the
  materials are carried AND the fire is burning (fuel > 0) — the hearth
  outranks the mattress (a bed crafted from the last logs left seed 777
  fireless for the whole soak). A palm chop yields exactly the bed kit
  (2 logs + 3 leaves); the 0.6 score lets the bed outbid TendFire
  (<= 0.55) in that window — at 0.35 the kit's logs were always eaten by
  the hearth. The hut-completion bed reward stays.

### 35.7 Crafted furniture no longer heaps (iteration 33)

The camp piled up: campfire, bed and rack all landed on the fire's
immediate neighbour, stacked together. Now crafted furniture (bed, rack)
places via a small spaced search: **pass 1** a free fireside junction that
isn't within a tile of an existing bed/rack; **pass 2** a junction 2 tiles
out that's clear; **pass 3** any free fireside junction (a heap beats
homelessness). Furniture still hugs the fire — the economy is too tight for
long trips to a distant bed (2-4 tiles out starved seed 12345) — but the
pieces no longer sit on top of each other. The campfire itself is not
avoided (you want the bed *near* the hearth, just not stacked on the rack).

### 29F.4 Coconuts trimmed, a spear in every hand (iteration 32)

The colony did nothing but eat coconuts — hunting and the whole meat/hide/
leather-armor tier lay dormant. Rebalanced so the wilds matter:

- **Everyone bootstraps with a `tool.spear`** (like the bottle: a weapon
  always to hand — foreshadows the weapon-slot equipment of a later pass).
  This is what actually turns hunting on: NPCs no longer need to craft a
  spear first, so a crab in sight becomes a hunt from day one.
- **Coconuts trimmed**: palm `MaxConcurrent` 5 → **4**. A gentle cut — the
  colony stays fed (crabs hug the far river and can't fully replace fruit,
  so a harsh cut just starved everyone and starved the fire of wood-gathering
  effort), but coconuts no longer blanket the map.
- **Hunt window widened** 0.55 → **0.8** hunger: with fruit a little
  scarcer, hunger climbs past 0.55 often; hunting must stay available as the
  real meat/hide source rather than ceding to a hungrier GetFood.
- Result: hunts happen (kills, then `clothing.leather_pants` sewn from
  hides → armor against the dogs), the food table no longer monotone.
- Deeper scarcity (making crabs a true food pillar) waits on the crabs
  themselves becoming catchable near home — their river habitat + the tight
  perception radius make them opportunistic prey for now.

### 29C.10 Signed thermal comfort & the burning fire (iteration 31)

The temperature axis becomes a **signed "chocolate" scale** for the UI, and
weather gains real stakes.

- `NPCNeeds.ThermalComfort` in [-1, +1]: **0 = ideal**, negative = too cold
  (snowflake, left), positive = too hot (sun, right). Computed each slow
  tick from the effective temperature: 0 inside the ideal [12,20] band,
  scaling to ±1 over ~15 degrees beyond it. This is the instantaneous
  reading the UI shows.
- The old unsigned `ThermalDiscomfort` (0..1) stays as the accumulating
  NEED the decision layer scores (Dress when cold, CoolOff/Undress when
  hot — direction still comes from the effective temperature). Its pressure
  now tracks the discomfort magnitude.
- **HP at the extremes**: while |ThermalComfort| >= 0.85 (un-fled freezing
  or heatstroke, and not standing in water) every body part loses 0.02 per
  slow tick — `Hypothermia` / `Heatstroke` traces; a destroyed vital ends
  it. Weather can now kill the unprepared. (Dormant in the current mild
  climate — a safety net for genuine cold snaps / heat waves.)
- **The campfire warms the DISPLAY**: a LIT campfire radiates warmth to
  tiles within 2 (≈ +8 at 1 tile, +4 at 2), folded into the signed
  ThermalComfort the UI shows — the player watches the dial pull toward
  "ideal" by the fire on a cold night. This iteration keeps it DISPLAY
  only: feeding the fire's warmth into the decision-driving discomfort NEED
  reshuffled the dog-fragile colony (everyone comfortable → nobody
  dresses/cools → repositioned into wipes). Likewise the "standing in the
  fire burns you" HP hit is **deferred**: NPCs constantly path across the
  central fire tile, so any hit whittles them down. Both land properly in
  the **campfire-as-obstacle** pass (approach from the edge, never stand on
  the flames) — `onFire`/`FireBurn` are already computed and traced,
  waiting to be wired once that holds.

### 29C.9 Gradual needs & longer actions (iteration 30)

Sims-style: a need fills **visibly, tick by tick, across the action**, not
in one jump when it ends. The interaction's total effect is divided into
`duration` equal shares; each in-progress tick applies one share, the last
share lands at completion, so the sum is exactly the authored effect.

- Applies to every need-bearing interaction through the shared
  `ApplyEffectsScaled(npc, effects, 1/duration)` path: object interactions
  (Sit on a chair, Sleep in a bed — Comfort/Energy), Eat-from-inventory
  (Hunger), Drink-from-bottle (Thirst/Comfort), and ground rest
  (Comfort/Energy). Work interactions (Harvest/Fuel/Craft/Build/PickUp)
  carry no need effect, so their share is zero — harmless.
- **Longer actions** (things read as too fast on screen): Eat 8 -> **20
  ticks** (5 s), Drink-from-bottle 6 -> **16** (4 s), Talk 16 -> **40**
  (10 s). Sit (70) and Sleep (100) were already unhurried; now their
  Comfort/Energy also fills gradually instead of at stand-up.
- Sickness (raw water) and the mutual social gain (Talk) still resolve
  once, at completion — only the personal-need relief is dripped.
- **Decision stability:** gradual needs drain the executing goal's own
  score mid-action, so the decision layer must NOT re-decide while an
  action is InProgress (a guard holds the goal until it completes) — else
  the goal flips every tick and interrupts explode. Emergencies are
  unaffected: Flee is set reactively by the fear path, and
  starvation/dehydration interrupt on the next decision after the (short,
  <= 100-tick) action ends.

### 29H The Water Bottle (iteration 29)

Drinking is no longer a bare interaction at the water's edge — everyone
carries a bottle, fills it at a source, and drinks from it. A two-step
chain that mirrors GetFood -> Eat.

**The bottle**

- Every NPC bootstraps with a personal `tool.bottle` (a real inventory
  item, so it shows in the panel and in hand). It is never consumed and
  never dropped on death — a personal effect, always there.
- The bottle holds one of `WaterKind` = **None / Raw / Boiled**, tracked
  on `NPCState.BottleWater` (one bottle per NPC, so the fill state lives
  on the NPC, not the item instance).

**Fill then drink**

- **GetWater** goal (score = Thirst, same as GetFood = Hunger): available
  when Thirst >= 0.35 and the bottle is empty and a source is reachable.
  Plan = walk to the best source and **FillBottle** (a new interaction
  type) there. Source preference reuses the old drink selection: a lit
  campfire with a pot fills **Boiled**; otherwise a pond/river bank fills
  **Raw**. Filling quenches nothing — it only charges the bottle. The
  bank-standing / reach-beside pathing is exactly the old Drink's.
- **Drink** goal (score = Thirst): available when Thirst >= 0.35 and the
  bottle is NOT empty. Plan = a single in-place `DrinkBottle` step (no
  target object, no reservation — like Eat-from-inventory). On
  completion: Thirst **-0.7 (Raw) / -0.85 (Boiled)**, Boiled adds
  Comfort +0.05, **Raw keeps the 30 % sickness roll** (moved here from
  the old water-edge Drink), and the bottle empties.
- The dehydration emergency boost applies to BOTH GetWater and Drink, so
  a parched NPC races to fill AND to drink.

The pond/river/campfire objects now expose **FillBottle**, not Drink.
Durations: FillBottle 6 (raw) / 8 (boiled) ticks, DrinkBottle 6 ticks
— together about one old single-drink cost, with the relief bumped so
the two-step throughput matches (the first soak's 10/12 + 8 at -0.6/-0.8
nearly doubled the ticks-per-thirst and spiked dehydration). The payoff:
water can be carried away from the dangerous bank.

### 31C.7A Unhurried sitting (iteration 26 addendum)

The 12-tick (3 s) Sit read as fidgeting once the sit animation landed.
Rebalanced as a proper breather:

- Sit duration 12 -> **70 ticks** (~17.5 s), Comfort +0.4, Energy +0.1
  (one long rest instead of many micro-sits).
- Availability gate: Comfort < 0.6 **and Hunger/Thirst < 0.6** — you sit
  because you need it, and never settle into a chair on an empty stomach
  (the first soak showed long sits crowding out meals on seed 777).
- **Post-sit cooldown 240 ticks** (~1 game-hour): after a good rest she
  gets on with her day instead of chaining sits.
- **Well-rested wanderlust** (29C.5 amendment): Explore gains +0.15 when the campfire is fuelled and
  every need is comfortable (Hunger/Thirst < 0.5, Energy > 0.5,
  Comfort > 0.35 — with beds earned rather than given (29G) comfort is a
  luxury; the old 0.5 bar made wanderlust unreachable) —
  long rests created genuinely idle NPCs who then never left the yard;
  a settled colony strolls instead of standing by the fire.
- **Sleeping metabolism**: while the current interaction is Sleep, Hunger
  and Thirst accumulate at **x0.4** — a sleeping body burns less; without
  this, hour-long sleep blocks guaranteed a starving wake-up every night
  (starving churn x3).
- **Sleep is likewise unhurried** (same addendum): bed Sleep 20 -> **100
  ticks** (~25 s, one game-hour), Energy +0.5, Comfort +0.2 — four-five
  real sleep blocks per night instead of eight catnaps (160 starved the
  colony overnight: hunger spiked mid-block, starving x10). No post-sleep
  cooldown: waking briefly and turning over is how nights work.

### 31C.6 Full model pass & interaction poses (iteration 25)

- **Every world object renders a real model** (Kenney Survival Kit + Food
  Kit + Nature Kit, all CC0, licenses in ImportedActors/Kenney): big tree,
  deadfall log, boulder, stone, firewood, palm leaf clump, campfire pit,
  coconut, raw/cooked meat, stone axe, stone pickaxe, pot (bucket), grave
  signpost, construction floor frame, drying rack (fence), and the bed is
  now a survival **bedroll** (the sci-fi bed retired). Auto-fit sizes per
  category (tools 0.18 R, campfire 0.55 R, boulder 0.45 R).
- **Water anchors have no gizmo**: water.pond/water.river interaction
  objects render as empty anchors — the river/pond tiles ARE the visual;
  NPCs walk to the bank and drink (user decision; the sim interaction
  objects stay, only their visuals are gone).
- **Interaction poses** (animator params Working/Sitting + states
  Crouch / Sit; clips: Polygonmaker crouch_inplace, Mixamo Sitting):
  PickUp/Harvest/Build/Craft/Fuel/Bury/Hang -> crouch; Sit -> sit. Laying
  keeps priority over both.
- **Hand props**: while eating the food model sits in the right hand
  (rHand bone via BodyBones), while chopping/mining — the axe/pickaxe,
  while drinking boiled water — the pot; auto-scaled to palm size by
  bounds. No prop when nothing fits.

### 31C.5A Verification

Sim side: 2-seed soak (coconut chain = old apple metrics, crabs spawn/get
hunted near water, no pathing through blocked anchors, rot counter > 0,
structure checks green). Presentation: play-mode checklist — sleeping NPC
lies on the bed, palms/coconuts/bed render from prefabs, water shows
depth, sand banks visible, no one clips through trunks.

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

**Iteration 2 note (2026-07):** the static food object is replaced by apple
trees (29A) that drop apples outdoors; food now enters the world only through
production, and the NPC carries it via the minimal inventory (29B).

### 33.4 Prototype NPC State

**The NPC should already have:**

- hunger
- energy
- comfort
- temperature discomfort / thermal state
- simple clothing state
- basic movement and facing

For the first iteration, the NPC may start with no clothing equipped.

**Iteration 3 note (2026-07):** the prototype runs **two NPCs** with different
starting need profiles. Single-instance objects (bed, chair, coat) become
contended: occupied objects are filtered at target selection (24.3), junction
reservations arbitrate races, and failed attempts go on goal cooldown (23.10).

**Iteration 8 note (2026-07):** **three NPCs**. Bed scarcity is deliberate
(2 beds : 3 sleepers): nightly contention feeds the resentment mechanic
(28.15B) and differentiates pairwise relationships. Talk partners are chosen
by affinity (28.6), not proximity, so triangles form: coalitions, cool-offs,
and reconciliations between different pairs at different times.

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

**Iteration 6 note (2026-07):** the single temperature value now follows the
day/night sinusoid (19.7A). Seasons and local (fragment/tile) modifiers
remain deferred.

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

**Iteration 2 note (2026-07):** a deliberately *minimal* inventory (2 slots,
pickup + consume only — see 29B) was pulled forward to enable the survival
loop. "Deep inventory systems" (stacking, transfer, UI, item state) remain
deferred.
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


## 35. Open World Survival Expansion (Iterations 17-22 Master Plan)

Direction set 2026-07-10: a seamless world with real building, weather, sun,
rivers, wet and wearing-out clothing, and ranged weapons. Research note:
hex-native construction follows the tile/edge/corner model (Red Blob Games)
— **walls live on hex edges**, which maps 1:1 onto the existing
edge-junction blocking (`blockedSlots`); building is therefore native to
the current spatial system, not bolted on.

### 35.1 Iteration 17 — Seamless World Foundation

- World grows to a ~285-tile continuous field (q -8..10, r -6..8); the
  hand-authored home stays where it is, wilderness generated around it.
- **River**: a seeded winding line of `Water` tiles (new TileFlags.Water):
  walkable shallows, drinkable (RawWater), never buildable; cooling and
  wetness arrive with 35.4/35.5.
- **Terrain props** (seeded placement): boulders (`rock.boulder`), small
  stones (`resource.stone`, pickable), big shade trees (`tree.big`), palms
  (`tree.palm`). Inert until their iterations.
- **Performance**: perception reachability switches from per-object BFS to
  an O(1) **connected-component cache** (recomputed when topology changes —
  i.e., when walls are built); path BFS remains only for actual movement.

### 35.2 Iteration 18 — Tools & Harvesting (implemented)

- **Logs are unified with `resource.firewood`** — one wood resource serves
  fire and construction; deadfall stays the renewable source, felling the
  bulk source.
- **Stone axe** (craft: 1 log + 1 stone), **stone pickaxe** (1 log +
  2 stones) at the campfire workbench (Craft recipes keyed by goal, 29F.3
  pattern); **saw** is findable wilderness loot (tag Tool — the existing
  GatherTools goal collects it; the goal's gate widens from lighter/pot to
  "any reachable Tool not carried", which also recovers dropped gear).
- **`Harvest` interaction** on harvestable objects: big tree **80 ticks
  (20 s)**, palm **60**, boulder **80** (iteration 29 — felling a whole
  tree and breaking rock should read as real labor, not a flick; the
  earlier 40/30/40 were near-instant on screen). Trees need axe OR saw
  (saw halves the duration — a sawn palm is 30 ticks); boulders need the
  pickaxe. Completion consumes the object and loots:
  big tree → 4 logs; palm → 2 logs + **3 palm leaves**
  (`resource.palm_leaf`); boulder → 4 stones. Shade dies with the tree
  (real tradeoff from 35.4 on).
- **Goal chain** (29E.4 pattern): GatherStone (stones needed for missing
  tools, score 0.25) → CraftAxe (0.3) / CraftPickaxe (0.25) →
  HarvestTree (0.35 when the fire is starving and no ground wood remains,
  or 0.25 to stock palm leaves) / MineBoulder (0.25 when the pickaxe owner
  carries < 2 stones). Apple trees are never harvest targets.
- Inventory capacity 7 → **10** (the tool belt era).

### 35.3 Iteration 19 — Building (hex-edge construction, implemented)

- **One communal project** (v1): a 1-tile wilderness hut, site seeded
  5-7 tiles from home on a walkable non-water tile whose 6 neighbors all
  exist; the **door edge faces home**. A `construction.site` anchor object
  is spawned there so the whole plan/reserve/interact machinery applies
  unchanged; NPCs know it from the start (seeded memory).
- **Build pieces**, one `Build` interaction (30 ticks) each, sequenced
  floor → 5 walls → door:
  - **Floor + roof**: 1 log + 2 palm leaves; sets `TileFlags.HasFloor`.
  - **Wall segment** (per edge): 1 log + 1 stone; blocks ALL junctions
    shared with that neighbor (the existing blockedSlots mechanism) and
    bumps `TopologyVersion` — the connectivity cache rebuilds from real
    construction for the first time.
  - **Door** (home-facing edge): 2 logs; blocks all shared junctions
    except one, which is marked `Junction.Door` — passable to humans,
    **never used by animals** (dog/rabbit movement skips Door junctions).
    The doorway junction must be a **mid-edge** junction — shared by
    exactly the site tile and the door neighbor (`Tiles.Count == 2`) and
    not already blocked. Corner junctions are shared with the adjacent
    wall edges and get blocked when those walls go up; marking one of
    them as the door seals the hut with the builder inside (found by the
    iteration 20 soak: a trapped NPC slept in the sealed hut for days
    while starving — everything outside was unreachable). Walls in turn
    never block a junction already marked `Door`. Soak invariant: after
    completion the hut interior must share a connectivity component with
    the outside world.
- **Completion**: all pieces done → the tile flips `Indoor` (sanctuary +
  the new **+4° indoor warmth** in the temperature model), a `bed.basic`
  spawns inside (a wilderness bed — the reward), the construction site is
  removed, `HutCompleted` traced.
- **Material drivers** extend the 29E.4 chains: GatherWood/GatherStone
  also trigger when the pending build piece needs logs/stones; palm leaves
  come from the 35.2 palm-chopping stock. `Build` (score 0.4) fires when
  the NPC carries the full bill for the pending piece.
- **Construction is peacetime work** (first-soak lesson: logs feed walls
  AND the hearth, and haulers starved — 37 starving / 47 dehydrated
  episodes): the build chain pauses entirely while the builder is hungry
  (>= 0.5) or thirsty (>= 0.5), or while the campfire is low on fuel. The
  hearth outranks the walls.
- Map-edge safety: `FindNearestJunction` now skips blocked junctions, and
  any NPC standing on a junction as it is walled re-resolves to the
  nearest passable one.

### 35.4 Iteration 20 — Sun, UV & Shade (implemented)

- **UV index**: `UvIndex = 0.9 × sin(π × dayProgress / 0.5)` over the
  daylight half (06:00-18:00), 0 otherwise — peaks 0.9 at midday; rain
  (35.5) will halve it.
- **Exposure & sunburn** (needs at least one *uncovered* body part —
  31A.5B coverage reuse; the head is never covered until hats exist):
  - effective UV: Indoor or Water tile → 0; within 1 tile of a Shade
    object (big tree / palm) → ×0.2; else full.
  - while effective UV > 0.5: `SunExposure += (uv - 0.5) × 0.3` per slow
    tick (~1.3 game-hours of peak sun to burn — the first soak showed
    0.15 never crossed 1.0 in ten days: exposure peaked at 0.99),
    plus Comfort -0.02 once exposure passes 0.5 (the sizzle is felt
    before the burn).
  - at SunExposure >= 1.0: **sunburn** — a seeded-random uncovered part
    takes 0.08 damage (head burns can kill via 19.3C vitals — sunstroke),
    Comfort -0.15, exposure resets to 0.5, `Sunburn` trace.
  - recovery: exposure -0.05 per protected/night slow tick (a burn risk
    lingers through a short shade break; 0.1 erased it too fast).
- **Shade & cooling**: shade lowers effective temperature by 2°; standing
  on a Water tile (the river) by 3° and blocks UV — the river is the
  midday refuge the user asked for.
- **CoolOff goal** (score 0.1 + 0.5 × max(Thermal, SunExposure − 0.4),
  when effective temp > 20 with Thermal >= 0.35 **or** SunExposure >= 0.6
  — heat and sunburn risk are separate reasons to seek cover; the first
  soak showed the heat-only gate never trips in this climate): move-only
  trip to the nearest Shade or Water spot; standing there cools and
  shields, and exposure recovery drains the urge so the NPC naturally
  resumes work. Chopping palms for leaves (35.2) now visibly costs the
  colony its parasols.

### 35.5 Iteration 21 — Weather, Rain & Wet Clothes (implemented)

- **Weather system** (WeatherSystem, slow layer): seeded rain fronts,
  scheduled **per day** — a per-slow-tick Bernoulli roll turned out badly
  mixed on the 16-tick stride (seed 777 rolled zero fronts in ten days).
  For day `d = tick / 2400`: it is a rain day iff
  `Hash01(seed, d, 17, 3301) < 0.45`; the front starts at
  `d×2400 + Hash01(seed, d, 18, 3301) × 2100` and lasts
  `300 + 600 × Hash01(seed, d, 19, 3302)` ticks. The whole schedule is a
  pure function of (seed, tick) — no weather state machine; the system
  just derives `IsRaining` each slow tick and traces the
  `RainStarted`/`RainStopped` transitions.
- **Rain effects**: effective UV ×0.5 (applied to `UvIndex` at source),
  global temperature −3° while raining. Rain wets every wearable outdoors:
  worn and carried items of NPCs on non-Indoor tiles, and wearable ground
  objects on non-Indoor tiles (a coat dropped in the rain soaks — and so
  does one hung on the rack; nobody dries laundry in a downpour).
- **Item instances**: `ItemInstance { DefinitionId, Wetness 0..1,
  Durability 0..1 }` (Agents/InventoryState.cs). `NPCState.WornItems` and
  `InventoryState.Items` become `List<ItemInstance>`; ground objects carry
  `WorldObjectState.Wetness` so wetness survives drop → pickup → dress
  round-trips (Durability mechanics arrive in 35.6; the field rides along
  at 1.0). Non-wearables (tools, logs, food) share the type; their wetness
  is tracked but has no effect in v1.
- **Wetting rates** (per slow tick): rain outdoors +0.04 (soaked in ~12
  slow ticks); standing on a Water tile +0.15 (the river drenches).
- **Wet garment** (Wetness > 0.5): contributes **zero warmth**
  (EquipmentMath recalculates each slow tick, since wetness now moves),
  armor unaffected; movement ×0.9 per wet *worn* item, floor ×0.8.
  `SoakedThrough` trace when a worn item crosses 0.5 upward.
- **Drying** (every slow tick an item is not being wetted, all locations —
  worn, carried, ground): `Wetness -= 0.02 × rate`, where rate is the best
  of: ×3 in direct sun (effective UV > 0.3 at the holder's/item's tile,
  shade/indoor rules from 35.4 apply), ×4 within 1 tile of a lit campfire,
  ×5 hanging on the drying rack, else ×1. Multipliers do not stack.
- **Drying rack**: `station.drying_rack` object; `CraftRack` goal (score
  0.3 + 0.2 when raining or any worn item wet; the first soak scored it
  0.1+0.3 and it lost all 811 available ticks to the busy goal field —
  infrastructure competes like CraftLeather 0.35, not like filler;
  requires 2 logs carried, none exists yet) — crafted at the campfire and placed on a free
  neighbor junction. "Holds one item" v1: hanging = the NPC undresses the
  wettest worn garment and it spawns as a ground object **at the rack's
  junction**; a wearable at the rack junction dries ×5. Hung clothes are
  ownerless loot (28.15D rule) — anyone cold just dresses from the rack.
- **DryClothes goal** (score 0.15 + 0.4 × max worn wetness; available when
  a worn item has Wetness > 0.5, it is not raining, and a free rack or lit
  campfire is known reachable): if the rack junction is free → walk there
  and hang the wettest item (`ItemHung` trace); else a move-only trip to
  the lit campfire — standing there dries the whole outfit at ×4.
  Cooldown 40 ticks on completion/failure.
- Deliberate v1 simplifications: rain has no sound/visual sim side; no
  puddles; rabbits/dogs ignore rain; boiled-water pots don't collect rain.

### 35.6 Iteration 22 — Durability & Bow (implemented)

- **Passive wear** (MoistureSystem — the per-item condition pass): every
  *worn* garment loses `0.01 / 150` durability per slow tick (= 0.01 per
  worn game-day; ~100 quiet days per garment). Carried, hung, and ground
  items do not wear; tools do not wear in v1.
- **Damage wear**: every dog bite costs each garment covering the bitten
  part **0.05** durability — armor absorbs health damage but the cloth
  gets chewed either way. Dog fights, not time, are what actually kill
  clothes on the soak horizon.
- **Destruction**: at durability <= 0 the worn item is removed outright —
  `ItemDestroyed` trace, Comfort -0.1, equipment recalculated. Nothing
  drops: it is rags. Crafting keeps the colony clothed — the economy loops.
- **Bow**: `tool.bow` — `CraftBow` goal (score 0.3): 2 logs + 1 hide at
  the campfire; available when no housemate need is more pressing and the
  crafter has no bow yet. CraftLeather (0.35) outranks it for the first
  hide — pants before weaponry.
- **Arrows**: `resource.arrow` — `CraftArrows` goal (score 0.3): 1 log ->
  **3 arrows**; available with a bow and an empty quiver (arrow count 0).
- **Ranged hunting** (RabbitSystem resolution, before the spear branch): a
  hunter with bow + arrow shoots at hex distance <= 3 — no adjacency
  chase needed. Per shot: 1 arrow consumed; hit chance **0.6** -> kill +
  loot (meat + hide) and a **40 %** roll to recover the arrow from the
  carcass (`ArrowRecovered`); a miss spooks the rabbit (existing spook +
  Hunt cooldown) and the arrow is lost in the grass. `BowShot` trace.
  Hunt availability: spear **or** (bow and >= 1 arrow).
- **Shooting dogs** (DogSystem): while a dog is *chasing*, any OTHER NPC
  (not the chase target, not fighting) with bow + arrow within 3 tiles
  fires once per slow pass: hit chance **0.5**, damage **0.35** (two hits
  drop a 0.9-hp dog); arrows are never recovered from dogs. The fear arc
  gains an answer: a fleeing housemate can be covered from the treeline.
  `DogShot` trace; dog death reuses the existing DogKilled path.

Each iteration keeps the spec-first discipline: numbers land in the
relevant deep sections (20, 29C-F, 31A) as they are implemented; this
master plan is the map, not the law.

---

## §40 Survivor Arc — Roadmap (captured 2026-07-11)

A large design brainstorm from the user, recorded verbatim-in-intent so
nothing is lost. Items are implemented iteration-by-iteration (spec-first,
2-seed+ soak, commit); anything not yet built lives here as the plan. The
colony is currently dog-fragile, so each behavioural change is a balancing
pass — order chosen to add robustness before difficulty.

### 40.1 Stamina (new core need)
- A derived reserve: high when **fed, rested, comfortable** (formed from
  Hunger/Energy/Comfort). **Every action spends stamina** (harvest, craft,
  build, hunt, gather…). Depleted stamina → the NPC must **rest** (sit,
  lie, sleep) or **eat** to recover; idling recovers it slowly, resting
  fast. When it hits zero: **panting** ("фух-фух") animation, a strong
  urge to sit/lie down and recover.
- Extreme depletion (plus hunger/stress) can cause **unconsciousness**:
  the NPC ragdolls, falls, and lies for a while before getting up.
- **Shipped v1:** stamina drains −0.05/slow-tick while working/moving,
  recovers +0.06 resting (sit/sleep) and +0.015 idle, under a ceiling set
  by Hunger/Energy/Comfort — verified to dip near the floor under sustained
  work in soak. Soft: it does not gate actions (that collapses the
  economy), only nudges the rest goals and colours the UI. The **panting**
  cue is exported as a derived `Winded` flag (`Stamina < 0.15`) for the
  presentation breath/pose; the ragdoll-faint on extreme depletion is the
  existing `FaintedUntilTick` path (§40.13), surfaced as `IsFainted`.

### 40.2 Blood & bleeding
- Wounds cause **gradual blood loss**. Low blood → death if untended.
  Blood **regenerates like HP** over time (and via food/rest). Bandages/
  medicine speed it and stop bleeding.

### 40.3 Medicine & stockpiling (Safety goal)
- New consumables: **bandages, pills** — treat wounds / stop bleeding /
  restore HP. New **Safety goal**: keep a reserve of food, water, medicine,
  supplies. NPCs stockpile against scarcity.
- **Shipped v1 (medicine):** each NPC carries a small reserve of bandages
  (`Needs.Bandages`, auto-dressed at `Blood < 0.35`, §40.2) and **pills**
  (`Needs.Pills`, start 1). Pills are the last-resort backup to bandages:
  when a part is wounded (`worst < 0.4`) and no bandage fires yet `Health`
  has fallen near death (`< 0.3`), a pill is spent — the wounded parts and
  HP recover a step. Fires only at the brink, so it can pull a dying NPC
  back without touching the healthy colony's deterministic dog-dance
  (robustness-positive, like the bandage). Both reserves are in the snapshot
  for the future character panel. The full **Safety goal** (actively
  gathering a reserve into a chest, §40.4) stays deferred — a new gathering
  goal reshuffles the fragile colony, so it waits on a surplus economy.

### 40.4 Harder gathering, more food, storage
- Reduce the deficit (more total food) but make **acquiring it harder**
  (further, gated, riskier). Chests / storage places to stockpile
  resources & food (foreshadowed by the Safety goal).

### 40.5 Emergent cooperation & theft
- NPCs **help each other** with food/supplies. When truly starving they
  make **hard choices** — even **stealing** from a housemate. Social
  fabric under scarcity.
- **Shipped v1 (food-sharing):** the first cooperation seed. When an NPC is
  starving at the death-brink (`Hunger ≥ 0.95`, the same gate that begins
  starvation damage) and an adjacent housemate is well-fed (`Hunger < 0.4`),
  not fighting or fleeing, and carrying spare food, that neighbour **hands
  over one food item** — the starving NPC's hunger drops a meal's worth and
  the damage that tick is averted. Deliberately a **passive last-resort**:
  no new goal, no reroute, no pathing change — it fires only when a fed
  carrier already stands beside the dying one, so like the pill (§40.3) it
  saves a life without disturbing the healthy colony's deterministic routine
  (robustness-positive; all 6 soak seeds green). Active giving (walking food
  to the hungry), theft, and generosity shaped by relationships stay
  deferred — those add competing goals and reroutes that reshuffle the
  fragile dog-dance.
- **Shipped v1 (theft):** the flip side. If an NPC is still starving at the
  death-brink after nobody shared, and an adjacent housemate is *carrying*
  food, the starving one **takes it** — the victim loses the meal (a hard
  choice under scarcity). Mirrors food-sharing (same passive last-resort
  adjacency check, no goal/reroute) but takes regardless of the victim's own
  state. Verified: all 6 soak seeds green with deaths identical to baseline
  — it's dormant in the standard soaks (a housemate carrying a spare meal
  next to a starving one is a rare coincidence, like sharing) but present and
  non-destabilizing when it does fire. NOTE: an earlier *inference* that
  theft would destabilize was wrong — measured, it's safe like sharing,
  because it places nothing and fires only on the rare brink-adjacency.

### 40.6 Hygiene & dirt
- New need **Hygiene**: NPCs get **dirty** over time (visual grime + the
  param). Restore by **bathing** — swim in water / stand under a
  **waterfall** / shower. Find/borrow swim & shower animations (the
  molly_copy repo has a shower animation).

### 40.7 Sunburn → tan (skin system)
- Skin **reddens where clothing doesn't cover**, sharply along garment
  edges (a t-shirt's neckline burns in a clean line). Fresh burn = bright
  red; over time it **fades to a dark tan**. Torn clothing lets those
  patches tan too. New param **skin protection**; UV/heat damage tied to
  it. Strong overheat → **heatstroke / sunstroke** damage and possible
  faint. Implement by painting the skin texture progressively (burn→tan)
  masked by coverage.
- **Shipped v1 (sim data model):** two skin channels are now tracked and
  exported so the skin painter has both when Unity connects.
  `Needs.TanLevel` (permanent, never fades) already builds from sun on bare
  parts. New `Needs.Sunburn` is the **acute redness**: it rises ~2.5× faster
  than tan under strong sun on uncovered parts, then heals when out of the
  sun (or fully covered), and as it heals ~40 % of the healed amount settles
  into permanent `TanLevel` — bright red first, browning into tan, exactly
  the described fade. Purely cosmetic: it gates no action and changes no
  survival outcome (so it can't disturb the fragile colony). Presentation
  reads it as the red channel over the tan, masked by garment coverage.

### 40.8 Visible injuries (decals/texture)
- Where a bone is hit (leg/arm/head/belly), draw a **wound** on the
  skin/clothing — texture paint or decal. Real, visible damage.

### 40.9 Injury-driven locomotion & poses
- **Limp** when a leg is hurt; **crawl** when both legs are down; a hurt
  **arm hangs** ragdoll-limp; hands **clutch head/belly** when those parts
  are hit. Ties bone health → animation.
- **Shipped v1 (sim hint):** the snapshot exports a single authoritative
  `PostureHint` per NPC, derived from body damage + faint, so the
  presentation pose layer has one unambiguous signal instead of
  re-interpreting raw bone health. Priority: `Faint` > `Crawl` (both legs
  < 0.4) > `Limp` (one leg) > `ArmHang` (an arm) > `HeadClutch` (head) >
  `Upright`. Export-only — no simulation logic changes. The poses
  themselves are presentation (Unity) and land when the pose layer reads
  this hint.

### 40.10 Clothing wear (verify + visual)
- Verify durability actually works; **worn-out clothing turns to trash**
  and is discarded. **Visual tearing**: garments get progressively ragged
  (torn tights = transparent alpha-cut texture). Research alpha-cutout /
  texture-erosion tech; drive rip amount by durability.

### 40.11 Character UI panel
- Select an NPC → a **button** opens a panel showing: **equipment slots**
  (what's worn/held where), **clothing durability** with progress bars,
  and **bone health** (which parts are wounded). Live inspection.

### 40.12 Expand the island & scattered loot
- **Bigger / multiple islands.** Scatter **findable items** (pickaxe, saw,
  varied gear) with different models & params — crafting takes a back seat
  to exploration/finding for a while.

### 40.13 Ragdoll
- Verify ragdoll works. Use it for **faint / collapse-from-exhaustion**
  and possibly a relaxed sleep flop.

### 40.14 Tent (sun shelter) & tiered beds
- **Tent**: a min-1-hex **angled canopy** that shades one person from the
  sun — one (or two) hex edges become an impassable roof. A **bed can go
  under it**. Needs a nice model (leaves + sticks).
- **Tiered beds**: the current bedroll becomes **tier 2** (high comfort,
  expensive). Add a **simple leaf sleeping-mat** — cheap, only a little
  better than bare grass.

### 40.15 Global goal — escape the island
- Beyond "survive": **leave the island.** Build a **raft (with a motor)**
  and sail away, hopping between **multiple islands** (resources run out;
  move on). Swimming risks a **shark** mob (attacks/kills mid-swim); a raft
  is the safe crossing. Endgame in the spirit of survival games (build up,
  then depart). NPCs must **organize together** for it.

### 40.16 LLM assist
- Wire an **LLM** to suggest **joint plans** when the colony is in dire
  straits — high-level cooperative strategy the hand-written AI can't
  reach. (Spec has earlier hooks for this; formalize the trigger + I/O.)
- **Shipped v1 (trigger + I/O contract):** `AI.DireStraits.Assess(world)`
  detects a colony-wide crisis (≥ 2 living NPCs, and ≥ half of them,
  starving/parched or badly wounded at once). On the rising edge,
  `NeedsDecaySystem` consults `DireStraits.Advisor` (an `IJointPlanAdvisor`)
  and emits a `DireStraits` trace with the `DireStraitsContext`
  (starving/wounded/living counts). The default advisor is a **null-object**
  — the seam is **inert** (no behaviour change, colony soak unaffected) until
  a host swaps in an LLM-backed advisor and acts on the returned plan. This
  is exactly the "formalize the trigger + I/O" step; the live LLM call and
  plan-application are the deferred v2.

### 40.17 Climb — weighted pathfinding (implementation plan, deferred)
The user wants big elevation steps to be **climbable "seams"** that are 2×
the cost of flat walking, so a route **prefers the flat detour** but will
climb when climbing is genuinely shorter. Current status: `HexPathfinder`
is a uniform-cost **BFS** (`Navigation/HexPathfinder.cs`) and there is **no
seam marker** — junctions carry only their tiles' `Elevation`, no per-edge
climb flag. A naive elevation-diff heuristic mis-tags seams and reshuffles
the fragile dog-dance, so this is a **focused iteration**, not a drive-by.
Turnkey steps for that pass:
1. **Tag seams at world-gen.** When wiring junction neighbours in
   `PrototypeWorldDefinitionFactory` / the junction builder, mark an edge as
   a climb-seam when the two junctions' representative `Elevation` differs by
   ≥ 1 step. Store it as a `HashSet<(JunctionId,JunctionId)>` climb-edge set
   on `WorldState` (symmetric) or a per-neighbour parallel list.
2. **Convert BFS → uniform-cost search.** Swap the `Queue` for a
   `PriorityQueue<JunctionId,long>` keyed by `gScore*BIG + insertionSeq`.
   With every edge cost 1 this is byte-identical to today's BFS (the seq
   tiebreak preserves FIFO) — verify a soak is unchanged before adding
   weight (safe substrate first).
3. **Weight the seams.** Edge cost = 2 when the edge is in the climb-seam
   set, else 1. Re-soak all 6 seeds; expect route shifts on hillsides —
   rebalance (dog spawns / home layout) if a seed tips, per the fragility
   discipline.
4. **Presentation.** The climb animation (hand-over-hand up the seam) lands
   in Unity when connected; the seam set also tells the renderer where to
   play it. Blocked on Unity like the rest of §40's visual layer.

**MEASURED CONCLUSION (steps 1–2 shipped; 3 blocked by design, not tuning).**
Steps 1–2 are done and green: seams are tagged (`WorldState.ClimbSeams`,
exported as `IsClimbSeam` with presentation dots), and `HexPathfinder` is a
Unity-safe uniform-cost search (`SortedDictionary`, byte-identical at cost 1).
Step 3 (the actual weight) is **proven un-addable by parameter tuning** —
**8+ soak tests, all break seeds, broken set shifts whack-a-mole**:
seam-cost 2× / 1.5× / 1.2× (each tips 1–3 seeds); a movement-speed 0.5× over
seams (the user's literal "2× longer to climb", NOT a reroute — still tips 2
seeds, so it's timing not routing); dog-rebalance rounds (softer bite / lower
aggro / both — 4/6 at best, shifting seeds); and fewer dogs (`MaxDogs 1` +
weight — 3/6, WORSE). **Root cause:** the 3-NPC colony survives the
deterministic dog-dance with *zero margin*, so ANY perturbation to NPC
movement/timing — however small or rarely-triggered — reshuffles which
encounters occur and tips it; even *reducing* difficulty reshuffles chaotically.
**Therefore the climb weight (and §40.18's second island) cannot ship by
tuning.** They need one of: (a) the colony made robust *by construction* —
survive dog fights with margin so which-fight-happens stops mattering (a
deliberate softening of the knife-edge tension — a design choice about the
game's feel), or (b) a redesigned, less timing-chaotic dog-encounter model.
Both are design decisions, not knob-turns. Until one is chosen, `SeamCost`
stays == `FlatCost` (uniform, green) and the weight is measured-dormant like
the escape raft. See [[project_dog_fragility_balance]] for the full test log.

**UPDATE — SHIPPED (1c0e67b).** The "blocked by design" conclusion above was
premature. The fix was to address BOTH coupled dimensions at once, exactly
as diagnosed: (1) a **food-exempt** climb weight — `FindPath(..., weightClimb)`,
false when `Hunger/Thirst >= 0.5`, so hungry NPCs keep short food/water
routes (no starvation); (2) a **dog margin** — `BiteDamagePerPass 0.2→0.10`,
enough combat slack to absorb the reroute's dog-dance reshuffle. Together:
6/6 green, `SeamCost = 12` (1.2×) active for comfortable NPCs, dogs still
lethal (5 deaths/6 seeds vs 7). The margin threshold is sharp (0.10 works,
0.11→5/6). TRADEOFF: dogs do half per-bite damage — a deliberate, modest
softening (the "accept multi-round balancing" the goal allowed); set
`BiteDamagePerPass` back to `0.2` to restore the harder game (the weight
then goes dormant). §40.18's second island reaches 5/6 on this softened
baseline (only one seed's fire/water soft-gate resists) — close, a focused
placement pass away, no longer a hard blocker.

### 40.18 Islands, swimming & shark — implementation plan (arc)
The escape endgame (§40.15) and bigger-world (§40.12) share one dependency
chain: **traversable water → swimming → shark → island-hopping**. No piece
delivers value alone (a shark with nothing to bite is dead code), so this is
a deliberate multi-iteration arc, sequenced so each step soaks green before
the next. It WILL reshuffle the dog-dance (water becomes walkable, the map
grows) — budget multi-round rebalancing per [[project_dog_fragility_balance]].
1. **Swim tiles.** Add a `Swimmable` tile flag distinct from the current
   impassable `Water`. Deep sea stays blocked; a shallow crossing between
   land masses is Swimmable. Pathfinder: entering a Swimmable junction costs
   ~4× a land step (slow, deferred like the climb weight §40.17 so it's a
   last-resort route) and drains Stamina/adds a `Swimming` state. This is the
   destabilizing foundation — soak + rebalance dog spawns before proceeding.
2. **Shark mob.** A `Shark` wildlife entity (like the dog/crab) that patrols
   Swimmable/Water tiles only — it never occupies land junctions, so it's
   inert to the land colony (the same reason the water-bound crab pathing is
   safe). It attacks any NPC in the `Swimming` state (bite → Blood/HP), and
   is absent from land. Ship first as a dormant patroller (no swimmers yet →
   verifiably deaths==baseline, like the null advisor §40.16), then wire the
   bite once swimming exists.
3. **Second island.** Extend world-gen with a second land mass across a
   Swimmable strait, seeded with fresh loot (a pickaxe/saw already scatter,
   §40.12) and its own resources. Connectivity cache must span both.
   **UPDATE — SHIPPED (reachable island).** A second land mass sits in the SE
   sea at (9,4)/(9,5) (`PrototypeWorldDefinitionFactory` forces those tiles to
   elevation 1). `WorldStateFactory.OpenStraitCorridor` floods the SE strait
   box (all-water junctions with tiles in q7–10,r2–6) into `SwimJunctions`, so
   the island joins the mainland's connected component (BFS-measured: 9841
   junctions, i.e. the whole map). NOTE: the one-deep swim ring alone can't
   bridge a full water tile (its interior junctions stay all-water/blocked) —
   the corridor flood is required; island terrain alone leaves a 163-junction
   isolated blob. The prior "far shore is topologically isolated" conclusion
   was a probe artifact (`npc.CurrentJunction` is null at bootstrap), not real.
   REBALANCE COST (per the standing "accept multi-round balancing" directive):
   the far-corner terrain change reshuffles the knife-edge dog-dance to 1/6 on
   its own, so the colony's perturbation budget had to be widened to absorb it
   — `BiteDamagePerPass` 0.10→0.07, `HungerRate` 0.02→0.016, `ThirstRate`
   0.025→0.020. This is the ONLY config that soaks 6/6: tightening any of the
   three whack-a-moles (0.09/0.019/0.024 → 4/6 breaking 31337/555; 0.08/0.017/
   0.022 → 4/6 breaking 12345/555 — different seeds each step). Same lesson as
   the climb weight §40.17: multi-dimensional loosening converges where single
   knobs oscillate. The island is REACHABLE but not yet a crossing DESTINATION
   (no island-exclusive resource wired → NPCs have no incentive to swim there);
   that is step 4. Revert the three constants + the two bootstrap edits to undo.
4. **Island-hopping goal.** Resources deplete on the home island → a
   high-level goal to cross (swim = shark risk, or the raft §40.15 = safe
   crossing) to the next island. Ties the escape raft's payoff to a real
   destination. LLM joint-planning (§40.16) is the natural driver.
Presentation: water-swim animation, shark model + fin, island terrain —
all Unity-side, land when the editor is connected.

### Implementation order (living)
Robustness first, spectacle second: 40.1 Stamina → 40.2 Blood →
40.14 tiered beds + tent → 40.3 medicine/Safety → 40.6 Hygiene →
40.7 tan/skin → 40.10 wear + 40.8 injuries + 40.9 poses (presentation,
needs live Unity) → 40.11 UI → 40.12 bigger island/loot → 40.13 ragdoll →
40.15 escape/rafts/shark → 40.5 cooperation/theft → 40.16 LLM.
