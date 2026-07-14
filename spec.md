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
  `_grassBladesPerTile` on the renderer. **Flattening:** a lying body
  (sleeping / fainted / corpse) hides its tile's grass clump and it pops
  back after — O(lying bodies) per tick, only affected tiles toggle.
- Water renders sunken below its tile top. The sea/water tops use the imported
  **Definitive Stylized Water URP** material
  (`Resources/HexLive/Water/StylizedWaterDefinitive.mat`, from the user's
  StylizedWaterShader asset in `Assets/ThirdParty/`): depth-gradient colour,
  animated foam, fresnel, refraction. `CreateWaterMaterial` loads it first and
  falls back to the hand-written `HexLive/StylizedWater` shader. Needs the URP
  asset's Depth + Opaque textures on (PC asset already has them; the Mobile
  asset does not). **Day/night:** the water shader ignores scene lighting
  (the sea glowed at night), so `CreateWaterMaterial` hands out a runtime
  COPY of the asset (`ActiveWaterMaterial`) and `SkyDayNightController`
  scales its gradient/fresnel/foam colours with the directional light every
  frame — authored look at noon, deep moonlit blue at night (base colours
  captured once, alpha untouched, the .mat asset never dirtied).
- **Shore-only foam:** the water is depth-intersection foam, so foam appears
  wherever an opaque wall pierces the surface. Water tiles therefore build
  **no skirt** (`BuildHexPrismMesh(..., includeSkirt: false)`) — a bare
  surface, not a walled prism — so water↔water hex seams never foam. Depth
  comes from a single opaque **deep sea floor** plane (`EnsureSeaPlane`, at
  `waterY − 1.6`): open sea reads deep and foam-free, and the only shallow
  intersections left are the land tiles' cliff skirts rising through the
  surface — foam hugs the shoreline. A transparent sea-surface plane still
  extends the ocean past the playable bounds.
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
  Perlin-flickered point light that lights nearby terrain and actors. The
  fire is bound to the simulation — `SetLit(ResourceAmount > 0)` each tick,
  so it only burns while the campfire has fuel (starts cold).
- **Tool/resource/food models** are procedural low-poly
  (`LowPolyToolFactory`): axe, pickaxe, spear, bow, arrow, pot, saw,
  lighter, firewood, stone, hide, palm leaf, coconut, meat — built from
  cubes / a pyramid / a small prism, flat-shaded. Used both on the ground
  (`CreateObjectView`, before the generic primitive) and in an NPC's hand
  (`NpcActorView.SetHandProp`). No external assets, no prefab wiring.
- **AI-generated tool models** (axe/knife/pickaxe shipped) OVERRIDE the
  procedural ones: a prefab at `Resources/HexLive/Objects/<id>.prefab` is
  loaded first by both the hand and ground paths. Generate them by the FIXED
  pipeline in **`TOOL_GENERATION_SPEC.md`** (repo root) — the KEY RULE is
  *generate a HIGH-poly textured mesh (trellis-2 image-to-3D), THEN decimate
  it*; do NOT try to make an AI produce low-poly directly. That doc has the
  prompt, model, webp fix, pivot/orientation/scale conventions and wiring.
  Every agent/chat adding a tool MUST follow it so tools stay consistent.
- **Action animations** are procedural (`NpcActorView.ApplyActionPose`):
  the Animator only has locomotion + a generic crouch/sit/lay, so chopping,
  spear thrust, bow draw, eat/drink and combat swings are layered on in
  LateUpdate by rotating `rShldrBend`/`rForearmBend` in world space around
  the body's right axis. Driven by `SetInteraction` (Harvest→chop, Eat/
  Drink→hand-to-mouth, Build/Craft/Fuel→work) and `SetCombat(IsFighting,
  weapon)` which also puts the bow/spear in hand while hunting.
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

### 21.21B Hex-Step Hop (one-level climb)

Crossing to a tile one elevation level up OR down is a deliberate jump whose
timing lives in ONE place — `HexHopTuning` (Simulation/Navigation) — shared by
the sim and the presentation, so the two can never drift apart:

- v12 — CLIP ACTUALLY COMPRESSED TO THE WINDOW (the "master clock" was never
  wired). The design says the jump clip is compressed to exactly HopSeconds,
  but the animator's JumpUp/JumpDown states had `m_Speed 1` and NO speed
  parameter — the authored ~2.6s `X Bot@Jump` clip played at its own length,
  decoupled from the sim window, so it overshot HopSeconds (and with the clip's
  loop flag on, replayed — the user's "it played the jump twice"). v12 adds a
  `JumpSpeed` float param to the controller, sets both jump states'
  SpeedParameter to it, and NpcActorView sets `JumpSpeed = clipLength /
  window` at each jump (clip length cached from the runtime controller). One
  playthrough now == the hop window at any HopSeconds, loop flag irrelevant
  (the state still exits at ExitTime 0.9, now 0.9×window). Unity multiplies the
  param by global animator.speed, so fast-forward stays in sync with the arc.
- v13 — TILES[0]-CONSISTENT (fixes the border double-hop v11 introduced).
  v11 (below) fixed the wrong-hex DIVE but broke tile bookkeeping: it detected
  a wall when ANY tile of a junction differed in elevation, and committed the
  landing tile from a re-derived "nearest tile". Two regressions: (a) a junction
  on a SEAM between two elevations always borders both, so walking ALONG a seam
  fired a hop at every junction — she bounced hop-after-hop (9–15 tick
  double-hops, the user's "spрыгивает, запрыгивает"); (b) the landing tile
  often resolved to her OWN previous tile, so `npc.Tile` never updated and she
  re-armed the same hop (and the wrong tile fed the view a wrong ground Y — she
  "sank into the hex"). Root insight: normal walking assigns her tile from
  `junction.Tiles[0]`; hop detection and landing MUST use the same tile or the
  two disagree. v13: detection fires only when `Tiles[0]` steps to a different
  elevation (a drop into water counts); the hop crosses exactly ONE border,
  landing on that wall tile (`HopLandingIndex = wallIndex`, `npc.Tile` = wall
  `Tiles[0]`, `PathIndex = wallIndex+1`). Only the flight GEOMETRY keeps v11's
  tile-centre fix: fly near-centre → wall-centre (near = `Tiles[0]` of the
  junction before the wall, or npc.Tile), crossing = midpoint, ±`EdgePadding`.
  Harness: 23 hops (was 44–49 broken / 28 pre-session), 0/11 wrong-hex water
  dives, ZERO sub-25-tick bounces, every hop len 0.60.
- v11 — CORRECT DIVE GEOMETRY (superseded in part by v13 above). A wall
  junction borders SEVERAL tiles; the scan used `jn.Tiles[0]` and rebuilt the
  flight from `TileToWorld(npc.Tile)` as centre A. When the wall was a step
  ahead or the target was a DIAGONAL neighbour, that mis-placed the crossing so
  the landing fell short — a harness probe measured her landing back on land for
  10+/50 hops (e.g. stand (3,0) → target water (2,1) but HopTo landed in (3,0)).
  The fix that STUCK is the tile-CENTRE flight (crossing = centre midpoint, dir
  = near→wall, ±`EdgePadding`); v11's extra tile-reselection was reverted in v13
  because it desynced from the walk's `Tiles[0]`. Presentation also holds her
  LEVEL across the lip until
  `DownFallStartFrac` (0.5) of the flight — she only falls once past the edge,
  no more foot-scrape — plus a small `DownHopUp` pop. All knobs now live in a
  `HexTuningConfig` ScriptableObject (Resources), applied at game start and
  saved from the SwimTest inspector.
- v7 — SYMMETRIC WALL CLEARANCE (final jump geometry). The lattice is fine
  (junction spacing ~0.37 wu; boundary points are DUPLICATED either side of
  a wall), so the v6 "exclude one junction" left takeoff flush on the wall
  (measured signedFromWall 0.00) and landing 0.65 past it — an asymmetric
  shift toward the target. v7 keeps the lattice only as a DIRECTION hint and
  places takeoff/landing GEOMETRICALLY at exactly ±`EdgePadding` (0.5 wu)
  perpendicular to the wall plane (tile-centre midpoint, normal = stand→
  target axis). Measured: every hop now −0.50 / +0.50, jump length 1.15.
  The takeoff beat smoothly gathers her from where she stood (≈ the wall)
  back onto the takeoff point (a natural run-up rock, not a snap); rotation
  is locked on the flight vector the entire window (probe: rot constant, no
  spin). Survival soak PENDING (deferred at user request — jumps first).
- v10 — CONSISTENT JUMP LENGTH. Measured every hop in a harness copy of the
  SwimTest world: symmetry was perfect (−EdgePadding/+EdgePadding) BUT the
  length varied 1.0–2.0 because takeoff/landing stepped `EdgePadding/axisDot`
  along the flight (dividing by the approach angle to hold a constant
  PERPENDICULAR clearance) — an angled water dive stretched to len 2.0 (the
  user's "strange water jump"). v10 steps a FIXED `EdgePadding` ALONG the
  flight instead, so every jump is exactly `2*EdgePadding` long regardless of
  angle; the perpendicular clearance now rides the angle (0.26–0.30, natural
  — closer to the wall on an oblique approach). Default `EdgePadding` 0.5→0.3
  (len 1.0→0.6, "just over the edge" per user feedback that the jump read too
  big / too early). Slider in SwimTest.
- v9 — ONE TARGET, COMMITTED (fixes the v8 freeze/death). v8's stop-short
  redirect FOUGHT the walk: the redirect aimed at the takeoff (past her
  path junction) while the rotation/walk aimed at the junction itself, and
  because the hop-approach walked her PAST that junction without advancing
  PathIndex, the per-tick rescan measured distance-to-wall THROUGH a
  now-behind junction and flipped the target on/off with her position — she
  oscillated in place and starved (reproduced in a harness copy of the
  SwimTest world: frozen at (-4.87,0.56), rot spinning, dead by t=64). v9:
  the wall scan runs ONCE; on finding a wall it commits (`Movement.HopArmed`)
  and freezes the FIXED takeoff/landing (computed from lattice points, not
  her live position). While armed, the walk target IS the takeoff — a single
  point, so rotation + pacing + arrival never pull two ways. Arrival launches
  the hop and clears the flag; repath clears it too. Harness (SwimTest world)
  now runs the full loop: walk up → hop onto the hill → eat → hop down →
  cross the strait → drink, 4000 ticks, no freeze, survives. Compiles clean
  in the live Unity editor.
- v8 — STOP-SHORT (no gather). v7 placed takeoff correctly at −EdgePadding
  but fired the hop only when the edge junction was already her target — by
  then she was AT the wall (the fine lattice puts boundary points on it), so
  a takeoff-beat "gather" slid her ~0.5 back off the wall: the user's "walks
  into the wall, then slides back too far". v8 LOOKS AHEAD along the path for
  the nearest elevation-edge wall; while it is within ~EdgePadding+1.2 she
  walks straight toward the TAKEOFF point (−EdgePadding before the wall) and
  stops there, then crouches in place and flies — she never reaches the wall,
  so there is no slide. Probe: takeoff held constant through the takeoff
  beat, geometry −0.50/+0.50, len 1.00, rotation locked, no back-slide;
  5-day×3-seed smoke crash-free.
- v7 VIEW rewrite — VERTICAL-ONLY. Since the sim owns the root's full XZ
  (gather → straight flight → landing), the view no longer predicts XZ: the
  body follows the root's XZ exactly and adds ONLY a Y arc that turns the
  tile-crossing ground snap into a jump. Deleted the whole 3D ballistic
  prediction (root-velocity extrapolation, separate body clock, settle
  phase) — it double-computed the XZ the sim already produces and was the
  source of the residual visual offset. The arc is flat over the takeoff
  beat, eases 0→(overshoot)→1 over the flight span, pinned at 1 over the
  landing beat; reads the live root Y each frame (self-correcting, no dip/
  snap-back). Needs a live Unity session to eyeball.
- v3 — the ANIMATION is the master clock; two variables mark up the clip:
  `HopSeconds` (2.0) is the whole window (the clip is compressed to it);
  `TakeoffSeconds` (0.5) is the clip's crouch/push-off — the sim stands,
  the clip already plays; the airborne middle (`HopSeconds - Takeoff -
  Landing`) flies the padded path at constant pace; `LandingSeconds` (0.5)
  is the clip's feet-planting — the sim already stands at the landing
  point. Walking resumes when the window closes. No separate windup/landing
  idles, no view-side lead/delay pairs — sim and view read the same two
  numbers, desync is impossible by construction. Mobility/wetness factors
  don't apply mid-hop — it's a jump.
- v3.2: the hop has NO survival logic at all — no hurry threshold, no
  danger bypass (both existed briefly in §46 v3/v3.1 and were retired for
  elegance: the v3 window is short, flight moves at ~walk pace, so a girl
  jumping under attack risks only the brief takeoff/landing beats). The
  exposure cost is paid in the global balance instead: bite 0.07→0.06,
  hunger 0.013→0.012, raids →0.10; measured 6/12 (50%), dead centre.
- `LandIdleSeconds` (1.0): on reaching the edge junction the hop has landed —
  the NPC stands in plain idle (`ClimbPauseTimer`, status `Waiting`), then
  walks on.
- The snapshot exports `HopKind` ("Up"/"Down"/"") while the hop is in flight;
  the presentation plays the JumpUp/JumpDown clip compressed to the same
  `HopSeconds` and carries the body's vertical arc (slight overshoot above
  the target ledge on the way up).
- Water transitions are excluded — swim dive-in/climb-out (§40.18-B) owns
  those. Timers are transient (not persisted).

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

### 28.15E Conversation Topics & Overhead Bubbles (Iteration — Sims-style chat)

Talks were an invisible need/relationship transaction (28.15A–B): the only
readout was the turn-taking animation and a debug trace. This iteration makes
a chat *legible at a glance*, the way The Sims shows a thought bubble with an
icon and then a relationship "+/−": every talk now has a **subject** shown as
an emoji over the speaker's head, and its outcome pops a coloured
relationship change.

**Topic (`TalkTopic`, `Simulation/Social/TalkTopic.cs`).** A flat enum — the
sim only ever *picks* a subject; the emoji/colour/label live entirely in
presentation (`TalkTopicVisuals`), so retheming never touches the sim. The
pool (island-themed + relationship-coloured):

| Topic | When it's likely | Emoji |
|---|---|---|
| `SmallTalk` | default filler | 💬 |
| `Escape` | miserable (cold/rain) — "off this rock" | ⛵ |
| `Sharks` | always simmering (the water's menace) | 🦈 |
| `Dogs` | scared (high Stress) | 🐕 |
| `Weather` | rain / cold / heat | 🌧 |
| `Food` | hungry | 🥥 |
| `Fire` | cold (warmth) | 🔥 |
| `Home` | starved of company (low Social) | 🏠 |
| `Gossip` | ambient | 👀 |
| `Flirt` | mutual liking | 💗 |
| `Joke` | mutual liking | 😂 |
| `Grumble` | mutual dislike / crankiness | 😠 |

**Pick (`PickTalkTopic`, deterministic).** At talk start a weighted draw over
the pool, each weight = a base (every subject stays possible) plus context
terms read from the *pair's mean* situation — hunger, 1−Social, Stress, signed
ThermalComfort (cold/hot), rain, and mutual Affinity (like → Flirt/Joke,
dislike → Grumble). The winner is chosen by a stateless hash of
(seed, tick, pair) with an independent salt (5501) so it never correlates with
the 28.15B quarrel roll and is resume-safe. Stored on the initiator's
`Execution.CurrentTalkTopic`; cleared at completion/abort. Exported as
`NpcSnapshot.TalkTopic` ("" when idle).

**Outcome pop.** At completion the resolved signed affinity delta (+0.05 good
chat / −0.12 quarrel, per 28.15B) is stamped on **both** participants
(`Execution.LastTalkAffinityDelta` + `LastTalkResultTick`) and exported
(`TalkResultDelta`/`TalkResultTick`). The view fires the glyph once per fresh
tick: **+** / **++** green for a warmed relationship, **−** / **−−** red for a
soured one (doubled when |delta| ≥ 0.10 — "сильно/несильно"). Both housemates
get one, since both relationships moved.

**Presentation (world-space, no Canvas/TMP).** `NpcSpeechBubble` (owned by
`NpcActorView`, anchored to the head bone) draws an AI-generated empty bubble
sprite (`Resources/HexLive/UI/speech_bubble.png`) with a legacy `TextMesh`
emoji, billboarded to the camera and scale-normalised against the actor's
body scale. `HexWorldRenderer.SyncActorView` pushes `TalkTopic` every snapshot
(""→hide) and detects a new `TalkResultTick` to trigger the pop. **v1 scope:**
the bubble shows over the *initiator* only (matching the existing
initiator-only talk animation); the outcome pop shows over both. Emoji glyph
coverage depends on the platform font stack — the one piece to verify in a
build (fallback: swap `TalkTopicVisuals` glyphs for sprite icons).

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
- **Undress** (new goal + in-place `UndressItem` step): the item is
  removed from `WornItems` and **spawned back into the world at the NPC's
  current junction** — clothes migrate around the map, others can pick them
  up where they were dropped.
- **§Wardrobe-anim — two-beat timing (both 8 ticks = 2.0s).** Dress and
  undress each split at `ExecutionSystem.WardrobeHandoffFraction` (0.5), the
  instant the garment changes hands, so the view can play a gather beat and a
  garment-in-hand beat (31B.x):
  - *Dress:* beat A = gather (empty hands, the garment still lies on the
    ground); at the handoff the garment is picked up into hand; beat B = don;
    on completion `WornItems` gains it. The world object persists until
    completion (the renderer just hides its ground copy through beat B).
  - *Undress:* beat A = doff (the piece is still worn); at the handoff it is
    removed from `WornItems` into `NPCExecutionState.HeldGarment` (warmth/armor
    drop **now**, but it is **not** dropped yet); beat B = gather it up; on
    completion `HeldGarment` lands on the floor (wetness/durability preserved).
  - Exported per-NPC: `InteractionProgress` (0..1), `HeldGarmentId` (the piece
    in hand, or empty), `TargetObjectId` (for the ground-copy hide).
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

### 31A.5C Garment parameters as ScriptableObjects (Iteration 51)

The per-garment survival numbers (warmth, armor, thermal, layer, coverage,
dress duration) used to be hard-coded in `PrototypeContentCatalog` — inline
`ObjectDefinition` blocks plus an `AddImportedGarments` table. They are now a
data-driven catalog, mirroring the `HexTuningConfig` pattern (§21.21 / §49):

- **Engine-free source of truth** — `GarmentParams` (one wearable's fields) and
  the static `GarmentLibrary` live in `Simulation/Content/Garments/`. The
  library holds the built-in default table AND the *active* table the content
  catalog reads; `PrototypeContentCatalog.CreateDefaults()` now ends with
  `GarmentLibrary.AppendDefinitions(defs)`. The headless soak needs no Unity —
  it runs on the built-in defaults.
- **One ScriptableObject per item** — `GarmentDefinition` (Unity side,
  `UnityPresentation/Wearing/Garments/`) is the inspector-editable asset for one
  garment. Assets are organized into `Underwear/ Wear/ Outerwear/` folders.
- **Registry asset** — `GarmentCatalog` (a ScriptableObject list) collects every
  `GarmentDefinition` and lives in `Resources/HexLive/GarmentCatalog.asset` so
  the shipping game loads it with no scene reference.
- **Bridge** — `GarmentTuning.LoadAndApply()` (called from
  `PrototypeRuntimeBootstrap`, right after `HexTuning`, before the world is
  built) flattens the catalog into `GarmentLibrary.Override(...)`. A missing or
  empty asset is ignored → the wardrobe falls back to code defaults, never blank.
- **Materializing the assets** — the editor menu
  *HexLive → Garments → Rebuild Catalog From Defaults* creates the 43 assets and
  the catalog from `GarmentLibrary.Defaults` (Unity owns the GUIDs). *Reset
  Values From Defaults* re-stamps code values over hand-tuned assets. Rebuild is
  non-destructive — it only adds missing assets and refreshes the list.

Ids are frozen (they key the `Resources/HexLive/Wear/<id>/` art). Warmth/armor
budget unchanged from §42; the extraction is structural, values are now tuned in
the inspector.

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
| clothing.top_tropic | TopTropic (tank top, fal.ai tropical print) |
| clothing.top_tiedye | TopTiedye (tank top, fal.ai tie-dye print) |
| underwear.panty_leo | PantyLeo (panty, fal.ai leopard print) |
| underwear.panty_stars | PantyStars (panty, fal.ai stars print) |

**AI-print wardrobe experiment:** the four print items are fal.ai-generated
all-over textile patterns baked into copies of the TankTop9_20034 / Panty_11571
prefabs (both material slots re-pointed to the print material; textures/mats
live in `Assets/ImportedActors/Wear/Prints/`). Sim side: registered in
`PrototypeContentCatalog` as light summer wear (tops: Wear/Torso, +0.08
warmth; panties: Underwear/Pelvis, +0.03) and spawned as ground objects
124–127 near home. Pipeline for more: generate print → copy base prefab →
swap material GUID → drop the folder under `Resources/HexLive/Wear/<simId>/`.

### 31B.5 Renderer bridge

`HexWorldRenderer.CreateNpcView` instantiates
`Resources/HexLive/Actors/{snapshot.ActorMesh}` when present (primitive
capsule remains the fallback). A new `NpcActorView` component:

- `Construct(actorMesh)` → BodyBones.Construct (bone map + hair).
- Per snapshot: diffs `WornItems` against the currently equipped set →
  `Equip`/`TakeOff` of the mapped wear prefabs. (Since 40.10/35.5 sim
  durability DOES drive visuals — tear dissolve — and per-garment wetness
  drives the wet sheen via `Wear.SetWetness`; see those sections.)
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

### 31B.7 Wardrobe animation — two-beat dress/undress (§Wardrobe-anim)

Getting dressed/undressed is no longer an instant swap where the NPC stands
flush against the garment. `NpcActorView.SetWardrobeAction(interaction,
progress, garmentId)` (called by the renderer every snapshot, **after**
`SetInteraction`, which it overrides for `Dress`/`Undress`) plays the sim
timing (31A.5A) as two beats, split at progress 0.5:

- **Dress:** beat A = the `Gathering` clip (empty hands); at the handoff the
  garment appears in the acting hand and beat B plays the `Dressing` clip; on
  completion `SyncWorn` puts the real garment on the body. The garment lying
  on the ground is hidden by the renderer from the handoff on
  (`_wardrobeHiddenObjects`, keyed by the snapshot's `TargetObjectId`), so it
  is never visible both on the floor and in hand.
- **Undress:** beat A = the `Undressing` clip while the piece is still worn;
  at the handoff the sim moves it off the body into the hand (`HeldGarmentId`
  becomes non-empty, `SyncWorn` bares the body), beat B = the `Gathering`
  clip; on completion the sim drops it and the renderer spawns the ground
  garment.

**Hand garment prop:** a folded-cloth prop built by `GarmentDropFactory.Build`
(the real garment mesh, palm-scaled) parented to the acting hand, kept
separate from the tool `_handProp` so a wardrobe action and a held tool never
clobber each other. Handedness (40.x) follows the acting hand.

**Animator:** `Dressing` / `Undressing` bool params drive Loopy clip states
`Dress` / `Undress` (built by `HexLive ▸ Build NPC Action States`), whose base
clips are swapped at runtime from `NpcAnimSet.dress` / `.undress` via the
override controller. v1 placeholder for both is the imported
`X Bot@Dressing` / `X Bot@Undressing` take (Hostage Situation Idle) — drop
real don/doff clips into those NpcAnimSet slots to replace it.

### 31B.6 Verification

Compile-level: presentation csproj builds. Play-mode checklist (user):
three named girls render at their sim positions, walk with animation, hair
present, underwear from bootstrap visible, Dress/Undress in the sim
adds/removes garments on the body, wear conflicts swap visuals, death drops
leave the body naked. Headless soaks are unaffected (simulation untouched).

### 31B.7 Wardrobe test scene (dev tool — SHIPPED, verified)

`Assets/Scenes/WardrobeTest.unity` + `WardrobeTest/WardrobeTestBootstrap.cs`:
an isolated fitting room for tuning garment fit. One GameObject bootstraps
everything (camera/light/ground/UI); `PrototypeRuntimeBootstrap` skips the
game world when it sees the wardrobe bootstrap in the scene.

- Pick Molly / Marta / Jana; the equipped outfit carries across the switch.
- Right panel lists every wear prefab under `Resources/HexLive/Wear/**`
  grouped by sim-definition folder; click = equip (+select), click again =
  take off. Layer/slot conflicts resolve through the normal BodyBones rules.
- The girl loops sit (3.5 s) → lie+sleep (7.5 s) → get up → idle (2 s)
  through the HexNpcLocomotion params; toggleable.
- Selected garment's per-actor `WearConfig.scale` is tuned with ◀/▶ or
  the ←/→ keys (step 0.01, Shift = 0.001). Each change writes the loaded
  prefab asset and re-dresses the garment (TakeOff + Equip) — the preview
  is byte-for-byte the game's own equip path, since Construct bakes the
  scale into every stitched bone and post-hoc bone tweaks don't match.
  "Сохранить в префабы" persists via `AssetDatabase.SaveAssets` (editor
  only). New `Wear` API: `GetConfigScale` / `SetConfigScale` (adds a
  config when the actor had none).
- Camera: RMB orbit, wheel zoom, MMB pan.

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

### 33.1 / 33.2 Presentation: weapon on the back, cartoon rain (iteration 33)

Zero-simulation, presentation-only (renders live; verify in the editor):

- **33.1 Weapon slung on the back** (`NpcActorView.SetBackWeapon`): a
  carried spear/bow mounts on the upper-spine bone (Genesis3 `chestUpper`
  with fallbacks), slung diagonally, hidden while it's in the hand
  (fighting). The renderer feeds it `BackWeaponFor(npc)` from the carried
  weapon. First step toward weapons-as-equipment (§33 slot system, sim
  part pending).
- **33.2 Cartoon rain** (`HexWorldRenderer.UpdateRain`): a world-space
  droplet `ParticleSystem` (stretched billboards, ~900/s) plays over the
  island whenever `snapshot.IsRaining`, stops when it clears.

Both live with the rest of the uncommitted presentation layer (the
committed HEAD `NpcActorView` predates this infrastructure).

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
- **Campfire-as-obstacle: SHIPPED.** `campfire.spot` now carries the
  `Obstacle` tag — its anchor junction is blocked at spawn (the same
  `SetObstacleBlocking` mechanism as tree trunks; the §45 r5 bystander
  nudge relocates anyone standing there). Nobody paths through the fire
  pit. All fire interactions already stand BESIDE a blocked anchor: the
  generic object-plan branch falls back to `CollectStandableAround`, and
  DryClothes picks a passable neighbor — crafting/fueling/boiling/warming
  at the fire keep working from adjacent junctions. The `FireBurn` HP hit
  can now be wired safely in a future balance pass.

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
- **Longer actions** (things read as too fast on screen): Eat 8 → **20
  ticks** (5 s), Drink-from-bottle 6 → **16** (4 s), Talk 16 → **40**
  (10 s). Sit (70) and Sleep (100) were already unhurried; now their
  Comfort/Energy also fills gradually instead of at stand-up.
- Sickness (raw water) and the mutual social gain (Talk) still resolve
  once, at completion — only the personal-need relief is dripped.

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
  completion: Thirst **-0.6 (Raw) / -0.8 (Boiled)**, Boiled adds
  Comfort +0.05, **Raw keeps the 30 % sickness roll** (moved here from
  the old water-edge Drink), and the bottle empties.
- The dehydration emergency boost applies to BOTH GetWater and Drink, so
  a parched NPC races to fill AND to drink.

The pond/river/campfire objects now expose **FillBottle**, not Drink.
Durations: FillBottle 10 (raw) / 12 (boiled) ticks, DrinkBottle 8 ticks
— roughly the old single-drink cost split across two steps, with the
payoff that water can be carried away from the dangerous bank.

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

**Presentation (wet look — reuses the sweat tech):**

- **Body in rain**: an NPC standing outdoors while it rains gets the full
  sweat treatment — skin smoothness climbs to the wet gloss and droplet
  decals bead on the skin (renderer passes `rainWet` = raining && tile not
  Indoor into `SetBodyCondition`; it maxes with the thermal sweat drive).
- **Rain droplets on clothes too**: garments opt into a dedicated cloth
  rendering-layer bit (`Wear.ClothDecalLayer`, 1<<2); rain droplet
  projectors target skin|cloth and spray ALL zones (covered ones land on
  the garment above), while wounds/dirt/sweat stay skin-only.
- **Wet cloth**: the exporter ships per-garment `WornWetness`
  ("id\twetness"); each `Wear` lerps its smoothness toward 0.85 and darkens
  its base color as wetness rises. The dry base values are captured from
  the shared material before any tint, so when the sim dries the item
  (fire/rack/sun) the garment returns to its EXACT authored look. Wetness
  alone never swaps in the tear shader — it rides the property block only.

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
- **Natural wear rate ×2 (0.02/game-day) + early fraying**: holes start
  below durability **0.85** (was 0.6), so clothes visibly age within a week
  of wear — not only after combat damage; rags still fall apart at 0
  (`DestroyWornItems` removes the item from `WornItems`; the visual
  unequips via the SyncWorn diff — nothing drops to the ground).
- **Wet garment**: insulation degrades **gradually** — warmth ×
  `(1 − 0.9 × Wetness)`, i.e. −90% when fully soaked (was a hard cliff:
  full warmth until 0.5 then zero — the first minutes of rain changed
  nothing and the cutoff read as broken). EquipmentMath recalculates each
  slow tick, since wetness moves; armor unaffected; movement ×0.9 per wet
  *worn* item (Wetness > 0.5), floor ×0.8. `SoakedThrough` trace when a
  worn item crosses 0.5 upward.
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
- **40.2-B Ground blood stains (shipped, presentation).** A bleeding girl
  drips onto the ground: `GroundBloodStains` (spawned lazily by
  `HexWorldRenderer`) watches each NPC's snapshot `Blood` — a DOWNWARD tick
  is an unambiguous "bleeding now" signal (bandages/pills only raise it) and
  arms a ~20-tick grace window; while armed she leaves a droplet at her feet
  (±0.12 m jitter) every 6 ticks, so a walking wounded girl draws a trail.
  Each stain lands drip-small (0.09 m), spreads ease-out to a 0.26–0.46 m
  puddle over ~120 ticks, then dries: linear alpha fade to zero across
  **~3 game days (7200 ticks; `DayLengthTicks` = 2400)** — blood you walk
  past is still there tomorrow — driven by sim tick (pause/speed safe), then
  the quad is destroyed. As it dries it ALSO darkens: `_BaseColor` steps
  toward deep dried bordo over `AgeBuckets` (6) shared darker material copies
  per variant, swapped by age — a fading semi-transparent RED film over
  yellow sand read as bright "ketchup", darkening keeps old stains a dark
  dried mark. Cap 130 stains, oldest recycled (each is a
  DBuffer DecalProjector rendered every frame — a dog swarm hits the cap
  fast, so the cap bounds overdraw; size stops being rewritten once fully
  spread — the only thing that expires blood early, no external cleanup
  scripts). Cosmetic
  only — never persisted, no sim coupling. Visuals: the RVFX Blood Effects
  Pack's OWN static projector decal **materials** (`BloodFX_PBR_Projector_URP`
  shadergraph — tuned albedo power / ambient intensity / wet smoothness +
  specularity + normal, the exact look of the pack's `..._Static_Projected`
  demo prefabs), the four copied into `Resources/HexLive/BloodStainMats/` as
  `BloodStain_01..04` with fresh GUIDs and loaded via `Resources.LoadAll`.
  We copy the MATERIAL, not the pack's `..._Static_Projected` prefab: that
  prefab carries `ProjectorPrioritySetter_URP` (a per-frame `Update`) +
  `BloodModifier_URP` — 110 of those would run 110 `Update`s/frame; the bare
  projector we build + the shared material gets the identical look with zero
  per-frame script cost. This replaced the earlier runtime-built stock
  `Shader Graphs/Decal` material (which only reused the pack's loose
  textures — flatter). Rendered
  the way the pack's demo does — as downward **DecalProjectors** (the pool
  hugs sloped hex prisms, grass and feet standing in it; `renderingLayerMask`
  = everything). The projector box projects DOWNWARD ONLY — top face at the
  foot (`ProjectorHover` 0.06, `pivot.z = ProjectorDepth/2` pushes the whole
  volume below the contact point) — so it never climbs the girl's legs/body/
  clothes (the old +0.35 centered box reached ~0.8 m up her shins and sprayed
  blood on her). Rendering-layer exclusion was avoided on purpose: light
  layers are on (`m_SupportsLightLayers: 1`) and the code-created sun has no
  explicit mask, so moving the actors to a dedicated rendering layer risked
  unlighting them — the geometry fix is lighting-safe. Spread animates
  `projector.size`, drying animates
  `fadeFactor` (both projector fields, so all stains of a variant share one
  material instance). The old `Resources/HexLive/BloodStains/` +
  `BloodStainNormals/` texture folders are now unused by this system (the
  pack materials reference their source textures by GUID). NOTE: the pack
  material out-of-the-box multiplies its (red) decal texture by a grey/brown
  `_BaseColor`+`_Color` at `_ColorIntensity 0.39` → dull brown smudge on
  bright sand (a first swap looked identical to the old stock-decal version,
  which drew the SAME `BloodDecal_01..04` textures). Retinted the 4 copied
  materials to fresh wet red (`_BaseColor`/`_Color` = 0.85,0.06,0.05,
  `_ColorIntensity` 0.85) and enlarged the pool (drip 0.09 m → 0.26–0.46 m,
  `MaxAlpha` 1.0) so it reads as a clear red pool. Old brown stains linger
  until they fade (up to one game day); new bleeding draws the red ones. The
  pack's own spawner scripts (Trail/ProjectorSpawnerSystem) are NOT used —
  they run on realtime `Time.deltaTime` and would ignore sim pause/speed;
  the tick-driven lifecycle stays ours. TUNING KNOBS: `LifetimeTicks`,
  `SpreadTicks`, `DripIntervalTicks`, `PuddleScaleMin/Max`, `MaxStains`.
- **40.2-C Blood in water (shipped, presentation).** When a bleeding girl is
  IN the water her blood billows on the surface instead of pooling on the
  ground. Detection is the renderer's existing `_npcOnWater[key]`
  (`_waterCoords.Contains(npc.Tile)` — wading OR swimming); the blood-drip
  branch in `HexWorldRenderer` routes an in-water girl to a `WaterBloodStains`
  component and NOT to `GroundBloodStains`, so there is no ground puddle
  underwater. Each drip is a flat translucent scarlet **quad** (not a
  projector — independent of the custom water shader) laid on the surface,
  riding the live `WaterWave` swell each frame (`LateUpdate`). **One
  continuous billow-and-vanish over the whole lifetime:** the disc grows from
  0 to a ~3×-land 1.65–2.85 m radius while its alpha fades in lock-step (wider
  = more transparent), hitting alpha 0 exactly at full spread — then the quad
  is destroyed (no separate spread/fade phases). Hue washes scarlet → pale
  pink as it dilutes (it dilutes, it does not dry to bordo). Lifetime
  `LifetimeTicks` = **1200 (half a game day; `DayLengthTicks` = 2400) — ~4×
  faster than a land stain**. Tick-driven (pause/speed safe), cosmetic only,
  never persisted (presentation-only, like the ground stains — sim doesn't
  know about it). Disc texture + transparent URP/Unlit material are
  procedural (no imported assets). TUNING KNOBS: `LifetimeTicks`,
  `BillowScaleMin/Max`, `MaxAlpha`, `MaxStains`. FUTURE (optional): feed the
  reddening into the water shader for a true tint rather than a floating quad;
  move to the sim data model if gameplay ever needs it (e.g. shark-on-scent).
- **40.2-D Pain wince (shipped, presentation).** A bleeding girl winces in
  pain. The face mood layer (`NpcFaceAnimator`, Genesis3 `eCTRL*` blend shapes)
  gains a `SetPain(0..1)`; `HexWorldRenderer` derives pain from the freshest
  open wound — a wound bleeds only while fresh (`heal01 < 0.3`, spec 44), so
  `pain = max over wounds of clamp01((0.3 − heal01)/0.3)` — 1 on a just-taken
  wound, fading to 0 as it clots/heals. There is NO single Genesis3 pain morph,
  so the wince is composited from FACS units: `eCTRLBrowSqueeze` (brows knit),
  `eCTRLEyesSquint` L/R (eyes screwed up), `eCTRLNoseScrunch`/`NoseWrinkle`,
  `eCTRLCheekFlex` L/R, `eCTRLMouthCornerBack` L/R (teeth-bared grimace),
  `eCTRLLipsPart` (a pained gasp), plus a reinforced `eCTRLMouthFrown`; a slow
  throb (`sin(t·6)`) modulates the whole grimace so it reads as waves of pain
  ("writhing"). Pain overrides the resting smile. NEXT (as discussed): widen
  the trigger from bleeding to a general "suffering" signal (limp/`PostureHint`,
  low wellbeing) once we define what suffering is.

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
- **Shipped (visual):** `NpcActorView.SetSkinWeathering` now takes `hygiene`
  and muddies the bare-skin tint toward a dull earthy brown as it drops
  (grime overlay via the same per-submesh property block, skin-only). Driven
  by exported `Hygiene`. Bathing/waterside already restores the param.

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
- **Rate & shade:** tan builds only on bare parts under the sun, gated by
  `effectiveUv > 0.5`, which already carries the shade penalty (shaded tiles
  cut UV ×0.2), so you tan **less in shade**. Rate `0.0018/slow-tick·part`
  ≈ ~10 game days to max at open-sun exposure. Presentation tans THROUGH
  red: pale skin first flushes toward a fresh-burn red (`0.79, 0.55, 0.57`
  by TanLevel 0.35 — the retired low-HP flush color, which read exactly
  like "just caught the sun"), then deepens into the full-tan **deep
  brown** (`0.40, 0.27, 0.18` at TanLevel 1), multiplying the skin so max
  tan reads markedly dark. Acute Sunburn still layers its own red on top.

### 40.8 Visible injuries (decals/texture)
- Where a bone is hit (leg/arm/head/belly), draw a **wound** on the
  skin/clothing — texture paint or decal. Real, visible damage.
- **SHIPPED (40.8-F — blood splash VFX on fresh wounds):** the purchased
  **RVFX Blood Effects Pack** (Assets/RVFX, URP variant extracted from its
  nested `BloodEffectPack_URP.unitypackage`) is wired in two ways. (1) A
  FRESH wound (first sync where its seed appears, heal ≈ 0) fires one
  particle splash prefab (`Blood_Splash_01..03_URP`, moved under
  `Resources/HexLive/VFX` GUID-intact so material refs survive) at the
  zone's bone, oriented outward, Hierarchy-scaled to the ~0.35 actors;
  one spray per sync (a 3-gash bite = one hit), 0.4 s real-time gate for
  fast sim speeds, silent first sync (loaded wounds are history, not
  hits); the pack's KillEffect self-destroys the instance (8 s backstop).
  (2) The pack's static splatters were TRIED as the painter's underlay
  variants and reverted — on the body the original pair (the user's
  blood_splash picture + generated art) reads better; the pack textures
  live on in the ground stains (with their normal maps) and the splash
  VFX. Unused pack goodies for later: blood trails, gut chunks, UI
  blood-screen overlays.
- **v1 (whole-body flush) — RETIRED:** the old "Health < 0.6 tints the
  whole skin bruised red-purple" pass is switched off (the renderer passes
  `hurt = 0`; the `SetSkinWeathering` channel remains wired should it ever
  return). With wounds painted into the skin the marks carry the injury
  look on their own — the flush just muddied them.
- **Shipped v3 (40.8B — wounds as first-class records):** a landed
  bite/hit files `WoundState { Id, Zone, Severity, Heal01, Seed }` records
  on the NPC (`WoundMath.Inflict`; dog + shark bites only). **40.8-E
  (multi-gash):** one hit tears THREE records (same zone, distinct seeds →
  distinct painted marks), the damage split between them — total hostage
  HP, healing duration and dog balance are identical to the single-record
  era, only the visual density tripled; hits under 0.09 don't split (three
  invisible slivers would burn the cap for nothing). The debug panel's
  "+ Random wound" mirrors this (3 gashes per click). Starvation, heat,
  sunburn and sickness drain HP but create **no wound** — no phantom decals
  while starving. Each wound maps to ONE decal: spot/look deterministic
  from `Seed` (stable across frames AND save-replays — spec 41.2 replays
  the same seed), alpha fading with `Heal01`; the record disappears when
  fully closed. **Cap = 36 (raised from 12 with multi-gash — at low HP the
  body should read mauled all over, a dozen bites' worth of marks), and the
  hit past the cap never EVICTS** (dropping a
  record would strand its hostage HP — a zone could stick at 0 forever): it
  REOPENS an existing wound — same-zone if possible (the bite tears the old
  scar deeper: severity absorbs the remaining hostage + the new hit, heal
  resets, same decal spot), otherwise the most-healed wound anywhere first
  returns its held HP to its own zone, then the record is repurposed as a
  fresh wound at the new spot. **Healing is per wound and
  activity-paced**: full close ~2 game days at rest, ×2 while sleeping,
  ×0.5 while marching. **HP is held hostage**: fed-regen may only raise a
  zone to `1 − Σ severity·(1−heal)` (its `WoundMath.OpenWoundDamage`) — a
  couple of coconuts never insta-heals a mauling; each healed slice returns
  exactly its share of the zone's HP. The character panel's HP bar is
  **Fallout-style**: green fill = current health, a red right-anchored
  segment = `WoundLockedHp` (won't regen until wounds close; shrinks as
  they do), with the value reading "72% (−18)".
- **Shipped v2 (procedural decals, `SkinDecals`):** wound marks —
  scratch streaks + blood blots driven by the wound records above;
  **dust/dirt smudges** climbing legs→arms→torso→face as Hygiene
  drops; **sweat droplets** (glossy, gently shimmering) blooming on
  face/chest/arms when ThermalComfort runs hot. Each decal is a **URP
  DecalProjector** parented to the zone's bone and aimed into the limb, so
  the texture is projected onto the skin mesh and hugs its curvature (the
  Decal renderer feature is enabled on PC_Renderer/Mobile_Renderer; the
  presentation asmdef references the URP runtime). Placement is
  deterministic from npcId+zone+slot (persistent across frames). Wound
  decals use **fal.ai-generated textures** (claw gashes `wound_scratch.png`,
  splatter `blood_splat.png` in `Resources/HexLive/Decals/`), post-processed
  to red-only with soft alpha falloff (baked-in pale "skin" would clash with
  tan; wet gloss is painted into the albedo since the URP decal graph
  exposes no smoothness). Fallbacks: molly hit-system `blood_splash.png`,
  then procedural. **Dirt** is a fal.ai texture too (`dirt_dust.png`):
  macro photo of sandy powder grains + clumps on black, luminance-keyed to
  alpha with a radial edge falloff — granular speckle like the logo's
  weathered grime (the old procedural blobs read as flat paint). Tuned
  down twice after paint-bombed passes: **dark brown** (recolored from
  luminance — multiplying the beige source kept it yellow), **quiet**
  (alpha ≤ 0.45, 24 smudges — was 52 → 30, only two on the head), smaller
  patches (0.110 × height, was 0.150), an **early radial fade** (from 55%
  of the sheet) so projector box edges never show as hard seams on the
  limbs, and **volumetric** — a baked grain-relief normal map
  (`dirt_dust_n.png`) blends at 0.7 via the Albedo+Normal DBuffer. Hair renderers are pinned to the cloth layer
  explicitly (imported prefabs shipped odd masks like 257). The
  GarmentTear cloth dust was softened to match (0.45 tint pull / 0.35 max
  cover, browner `_DirtColor 0.50,0.42,0.31`) — 0.7/0.5 painted harsh
  beige speckle over dark garments. Dirt accumulates
  over ~10 game days (hygiene −0.0004/slow tick). **Sweat** is a decal too:
  a fal.ai droplet-spray photo generated on black and luminance-keyed to
  alpha (`sweat_drops.png`); the projector's fadeFactor breathes 0.75↔1.0.
  (The interim 3D-sphere droplets looked wrong and were removed.) The sheet
  was later REBUILT from its own best beads: the structureless tiny specks
  read as milky white dots on skin ("как сперма"), so the four structured
  bubbles (ring + refraction + specular notch — the "belly bubble" the user
  liked) were cropped, circularly masked and re-scattered as ~68 small
  copies (34–78 px vs the old 230 px giant) on a jittered grid + tiny
  satellites. Sweat patches also widened 8 → 13 zones entries, so full heat
  covers the body in many little glistening beads. **v3 — sweat as painted
  NORMAL-ONLY relief** (`PaintSweatIntoTexture`) — was **PARKED**: at body
  scale the bead relief read as skin pox on the face. The bead-relief tech
  is kept intact for future disease/insect-bite visuals — and its pipeline
  became the foundation of v4 below. Mechanism (for that future use): a real
  droplet is transparent, so the painter stamps the dome normal sheet
  (`sweat_drops_n`) straight into the skin's normal map — 6 patches per
  bare zone (head: 2). **Stamps are world-size-true**: UV density is
  anisotropic per tile (a leg tile packs the circumference tight and the
  length loose — square-UV stamps stretched into ribs down the thigh, and
  the dense face tile blew beads up huge), so placement measures the hit
  triangle's metres-per-UV along U and V and sizes the stamp rect per
  axis (`StampSizeFor`): every wound/bandage/bead patch lands square and
  consistent in world metres on any body part. The droplet sheet itself
  is procedural rain-glass style (analytic hemisphere normals over exact
  height fields — dense wobbly drops, run-down trails, satellites;
  matched to the user's reference). The droplet sheet normals are TRUE SPHERICAL CAPS
  (chamfer distance transform per bead + local-radius max filter, height
  = √(d(2R−d))/R, pinholes morphologically closed) — blurred-alpha
  heights gave plateaus whose edge slopes read as crater rings. Wound
  reliefs use two-scale dome heights (S ±6/−7). Strength = wetness (0.1
  buckets), zero
  colour change; the wet gloss (0.72) turns each dome into a genuine sun
  glint. The projector "rain" pass now keys off rain-only inertial wetness
  (the unified pool made sweat spawn rain rings too). Wet skin smoothness
  itself was pulled 0.85 → 0.72 (read as plastic/vinyl).
  **v4 — painted WATER DROPLETS (`PaintSweatDroplets`, SHIPPED — the live
  sweat/rain look).** Diagnosis of why nothing before read as water: (a) a
  drop is a LENS, not a gloss patch — it needs per-drop normal relief,
  near-1 smoothness UNDER THE DROP ONLY, darkened/refracted skin beneath
  and a bright meniscus rim; the uniform submesh smoothness could never
  make a discrete drop; (b) at smoothness ≈ 1 a single directional light's
  GGX lobe collapses to a sub-pixel point — the visible "glassy" shine
  comes from ENVIRONMENT specular, and the scenes ran flat ambient with
  zero reflection probes, so even correct relief looked flat; (c) the v3
  pox read came from the DENSE SPRAY sheet, not from relief per se — and
  real-size (2–4 mm) drops die in the normal-map mips anyway. Hence v4:
  - **Few LARGE drops**: one drop per stamp (`SweatDropletSheet`, a
    procedural 4×2 atlas of sphere-cap domes / hanging teardrops / runnels,
    height `h = bulge·r·√(1−(d/r)²)`, bulge 0.4 — water sits low), 8–15 mm
    world-true, 2–4 per zone scaling with the wetness pool
    (`DropletCountFor`), threshold 0.25, **face excluded**
    (`FaceDroplets = 0`) with a hard UV-size cap (`MaxDropletUvSize`) as
    the dense-face-tile guard. Limbs/torso prefer teardrop cells (V runs
    along the limb ≈ gravity).
  - **Three painted channels** per drop (`DropletStamp.shader`, stamped at
    repaint time — skin STAYS on URP Lit, respecting the shader-swap
    revert): dome relief into the normal map (v3 path); a damp halo
    (×0.9), wet darkening (×0.75), **fake refraction** (original albedo
    offset-sampled along the droplet normal XY — the Shadertoy-"Heartfelt"
    lens trick) and an additive meniscus rim baked into the albedo; and a
    **painted gloss map** — the third channel: `_MetallicGlossMap` alpha
    carries ABSOLUTE smoothness (base = the current wetness gloss, 0.95
    in drops, BlendOp Max for overlaps). URP 17 Lit multiplies map alpha
    × the `_Smoothness` scalar (verified in `LitInput.hlsl`
    `SampleMetallicSpecGloss`; map R replaces `_Metallic` — kept 0), so
    `NpcActorView` pins the property-block scalar to **1.0 on slots where
    the map is live** (`SlotHasGlossMap`) and keeps the 0.32→0.72 lerp
    elsewhere — miss a slot and the body goes vinyl (the 0.85-plastic
    scar). Droplet slots DO swap their albedo to the paint RT now (the
    price of baked refraction — same trade wounds make).
  - **Env specular for the glints**: `ProceduralSkyReflection` — a
    code-generated 64 px HDR cubemap (sky gradient + hot sun blob + dark
    ground) installed as `RenderSettings.customReflectionTexture` in
    `SkyDayNightController` and the wardrobe scene;
    `reflectionIntensity` lerps 0.25↔1 with dayAmount. The sun blob is
    static — the glint reads as "sky".
  - **Build safety**: `_METALLICSPECGLOSSMAP` is a shader_feature; no
    authored material used it, so a `SkinGlossKeepAlive.mat` ships in
    Resources to keep the variant from being stripped (editor would look
    right, device builds silently flat). NOTE: `DropletStamp.shader` is
    `Shader.Find`-loaded like NormalDecodeBlit — same build-inclusion
    caveat as that shader.
  - The v2 **projector sweat retires** (`SweatDropletProjectors = false`
    — two sweat systems would double-coat); the projector rain pass
    stays for streaks. Rain feeds the same wetness pool, so rain also
    beads drops.
  **v4.1 — dense patches (user verdict on v4.0: "одна капелька — это всё
  хуйня", the single drops were near-invisible even after live size
  tuning).** A stamp is now a PATCH of 4-9 small fully-shaded drops
  (seeded scatter per atlas cell, drops Ø 10-18 mm, patch 5-7.5 cm,
  `HeadPatchScale 0.6` on the face); ~34 patches ≈ **200 drops** at full
  sweat. Density is safe now precisely because each bead carries the
  complete water shading — the v3 pox read came from naked normal bumps.
  The **uncovered filter is dropped** for droplets (sweat everywhere;
  garment meshes occlude covered zones naturally, skin through rips
  glistens). `RefractStrength` rescaled 0.35 → 0.05 (patch-relative).
  Live-debug findings that drove this (via UnityMCP on a paused play
  session): placement/painting/MPB/gloss were all correct from the start
  — the failures were pure readability (8-15 mm ≈ 7 px specks at gameplay
  zoom; wetness-proportional fade left drops at 65% opacity; the dark tan
  `_BaseColor` tint multiplies painted highlights down; shadowed limbs
  hide albedo cues entirely; and the first "sky wash" shading flattened
  drops into pale smudges — replaced by dark lens + upper-rim crescent +
  specular dot + caustic lower rim, all baked in texture space). NOTE: the Decal
  feature runs **Albedo+Normal** (`surfaceData: 1`) — smoothness/MAOS stays
  decal-untouchable (glassy sun-glint fix), normals are used ONLY by the
  sweat material (dome normal map = volumetric beads); every other decal
  material sets `Normal_Blend 0` so blood/dirt never flatten the skin
  pores. Wet gloss is baked into the textures. Dirt is deliberately HEAVY at the bottom of the
  scale: 52 overlapping smudge decals (doubled from 26) climb the body as
  hygiene drops, and the whole-body grime tint reaches half-strength
  earthy brown at Hygiene 0 — the entire skin must read filthy, no clean
  patches.
  **Skin-only:** the exporter ships `UncoveredParts`
  (`EquipmentMath.IsPartCovered`) and decals spawn solely on bare zones;
  quads hug the skin so worn garments occlude them — never blood/sweat on
  clothing. Cleared automatically when the zone heals / skin is washed /
  the body cools. A left-side **debug panel** (`DebugControlsPanel`)
  injects a random wound (a real WoundState record) / clears wounds /
  dirties / washes / tans / untans / forces sweat on-off / toggles
  clothing visibility ("Hide clothes" — every zone counts as bare) for the
  selected NPC (or everyone) so these visuals can be exercised without
  waiting on play.
- **SHIPPED (40.8-C, shader layers instead of decals): blood & sweat on
  cloth.** The GarmentTear shader gained two artistic layers alongside the
  dust: **blood soak** — deep venous red wicking through the fabric exactly
  over the wound (localized by the SAME damage spheres that rip the cloth,
  noise-spread like real wicking; intensity = Σ unhealed wound hostage,
  fades as wounds close; stain brightness scales with the cloth's own
  luminance) — and **sweat damp** — noise patches that darken the fabric a
  touch and gloss it (smoothness +0.45×damp), driven by the thermal sweat
  level. Both ride the existing per-slot property block via
  `Wear.SetGrime(dirt, spheres, count, blood01, sweat01)`, activate the
  tear-shader swap when meaningful (blood > 0.05, sweat > 0.25), and cost
  no decal projectors.
- **SHIPPED (40.8-D — wounds painted INTO the skin texture):** the molly
  hit-placement tech, ported **collider-free** (molly's
  `MeleeOnHitNonPhysics` pattern, hardened): on a new wound the body bakes
  its pose (`BakeMesh`) and the seeded zone surface point picks the
  **closest skin triangle** (centroid distance — a ray can slip past a thin
  limb, nearest-triangle can never miss) → UV + submesh slot. Genesis3 UVs
  are UDIM-tiled (torso U∈[1,2], legs U∈[2,3]…), so the UV wraps into
  [0,1] before painting. No temp `MeshCollider`, no per-placement PhysX
  cooking. Cheaper than molly's original: triangle/UV topology is cached
  once from the baked snapshot (the Daz FBX itself is not CPU-readable;
  eyes/lashes excluded up front), vertices refresh via a reusable buffer,
  and the POINT transforms into local space with one inverse matrix
  (bake-scale detected from mesh bounds — scale-correct for the ~0.35
  actors) instead of pushing every vertex to world. Fully lazy — an NPC who
  never bleeds allocates nothing; unresolvable zones leave a dead record so
  placement never retries per-frame. The wound then composites into
  a RenderTexture copy of that slot's albedo (≤2048², explicit sRGB,
  mips regenerated after stamping, assigned to the
  per-NPC material instance) — AND into a copy of the slot's **normal
  map**: the authored normal is decoded to plain RGB
  (`NormalDecodeBlit.shader` handles DXT5nm), then each stamp's baked
  relief map (`blood_splash_n`/`blood_splat_n` bead UP, `wound_scratch_n`
  grooves IN; RGB-encoded, A = stamp alpha, linear RT — the encoding
  survives URP's `UnpackNormalmapRGorAG` since a·r = x at a = 1) blends by
  the same fading alpha, so healing flattens the relief in step with the
  color; slots without an authored normal start flat and get
  `_NORMALMAP` enabled (disabled again on full restore).
  TWO stamps per pass — the picked blood-splash
  underlay wider, the detailed gash/splat art centered on the hit. Stamp
  records (slot/uv/seed/textures) persist; **healing just repaints** the
  composite from the clean original with lower alpha (0.1 buckets) until
  the mark dissolves; bandages stamp the leaf wrap the same way. Repaints
  are event-driven (state hash), rays only on NEW wounds. Skin stays URP
  Lit — tan/sunburn tints multiply the repainted map like the original,
  clothing occludes naturally, portraits show it. `PaintWoundsIntoTexture`
  flips back to decal projectors. Sweat/dirt/rain stay projector-based.
- **SHIPPED (40.8-D v5 — volumetric wounds: wet gloss, NO relief):** the
  flat/transparent gash was three problems at once — semi-transparent
  albedo core, no specular cue, and blood color that dark tan tints
  swallowed. Fixes: (1) wounds now stamp a third channel, joining the v4
  droplet **gloss map** (`_MetallicGlossMap`, alpha = ABSOLUTE smoothness,
  NpcActorView pins `_Smoothness` to 1 on live slots): the detailed
  over-art stamps its wet-core shape (`wound_scratch_g`/`blood_splat_g`,
  `WoundWetGloss = 1.0` — with relief cut, the glint IS the volume cue;
  it must out-shine the 0.72 sweat sheen and the 0.95 droplets) via
  `WoundGlossStamp.shader` — **BlendOp Max, ColorMask A**, so overlaps
  keep the shiniest value, the base never darkens, and a healing wound
  sinks below the base and vanishes; the splash underlay stays DRY and
  now draws at 0.5 alpha (at 0.8 its pale-pink wash read as skin
  discoloration on tanned bodies). (2) The gash art is regenerated by
  `Tools/wound_art_pipeline.py` (fal.ai flux/schnell, SFX-makeup framing;
  FAL_KEY env only — never committed): chroma-key with a red-ratio gate
  (blood is R≫G+B, knuckle shadows are not), enclosed wet interiors
  hole-filled and forced **fully opaque**, baked photo highlights removed
  (the gloss channel shines dynamically now), blood brightened to survive
  the ~0.47-red tan tint, content colour bled under transparent texels
  (no white mip fringe), percentile-crop to content. (3) **Wound normal
  relief was REMOVED** (v5 revision): stamps regularly straddle UV seams
  and the relief discontinuity flared as ugly lit ridges there, while the
  perceived depth gain was marginal — wounds no longer set
  `UnderNormal`/`OverNormal`, and the `_BumpScale` boost is gone. Wound
  volume = opaque dark albedo + wet gloss. Droplet dome relief (v4) and
  the normal-paint plumbing itself are untouched; `wound_scratch_n.png`
  stays on disk unused should relief return with seam-aware art.
- **SHIPPED (40.8-D v5.1 — wound VARIETY, blood-only):** repeated hits all
  drew the same one or two shapes. Replaced the seed-parity scratch/splat
  pick with a **variant table** (`WoundVariantNames` → `_woundOver[]` /
  index-aligned `_woundGloss[]`): `WoundVariant(seed)` selects
  `seed % N`, skipping any not-yet-imported PNG (partial import paints
  fewer shapes, never crashes) and staying deterministic so a save-replay
  and a late `RefreshStampArt` agree. Ships 7 shapes: the two originals
  (`wound_scratch` claw, `blood_splat`) + 5 new `wound_gash_*`
  (slash/streak/smear/fork/torn). **All BLOOD-ONLY** — the pipeline was
  re-prompted for "wet fresh blood, torn splatter edges, no skin, no flesh"
  after flesh-baked candidates (puncture/bite/graze) were rejected: a stamp
  with baked skin tone reads wrong on other NPCs' skin (each has its own
  tan/complexion) and prosthetic-hole art looked plastic. Each variant is
  albedo + a `_g` wet-gloss twin (`Tools/wound_art_pipeline.py build
  --name <v>`); no relief (per v5). New art committed under
  `Resources/HexLive/Decals/wound_gash_*` with pre-authored metas.
- **REVERTED (skin paint-shader experiment):** swapping the bare-skin slots
  to GarmentTear (`_HolesOn = 0`) made the body flicker (Cull Off +
  AlphaTest queue on the skinned mesh) and flattened the Daz skin to a pale
  cartoon look. Skin stays on **URP Lit**; wounds/sweat droplets/bandage
  remain decal projectors + the smoothness sheen, which read better on skin.
  The shader keeps `_HolesOn`/`_BandageSpheres` (harmless, default cloth
  behavior) should a dedicated skin variant return later.

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
- **UPDATE — REAL TEARING (procedural dissolve, presentation).** The first
  `_Cutoff` pass only worked on textures that already had alpha (lace); solid
  fabric never visibly tore. Replaced with a custom URP shader
  `HexLive/GarmentTear` (`UnityPresentation/Wearing/GarmentTear.shader`):
  the tear mask is clipped against `_TearAmount`. The mask is the
  **fal.ai artistic dissolve map** (`Resources/HexLive/Decals/tear_mask.png`:
  hand-drawn-quality ragged holes/slashes, post-processed to varied gray
  depths — darker rips open FIRST, so rising wear plays a natural
  destruction sequence; mirror-composited for tileability; blurred rims so
  each hole grows along its frayed silhouette). It lives in generic UV
  space, so ONE mask serves every garment type; `Wear.ApplyTearShader`
  assigns it and flips `_TearTexOn` — without the texture the shader falls
  back to the original procedural Voronoi+noise mask. The rim bleaches into
  pale threadbare fuzz (the fabric's own hue) and worn patches fade between
  holes as tear rises. `Cull Off` + `SV_IsFrontFace` normal
  flip render the cloth's inside through the holes; skin/underlayers show
  through since the body is fully modeled beneath (layers all render).
  The ShadowCaster pass clips identically (holes don't cast solid shadows).
  `Wear.SetErosion` swaps a garment's materials to the tear shader **lazily,
  the first time wear actually bites** (durability < ~0.6) — pristine clothes
  and hair keep their original URP Lit (same-named properties `_BaseMap/
  _BaseColor/_BumpMap` carry over on swap); `_TearAmount` then rides a
  property block per garment instance. Knobs: bite threshold in
  `Wear.SetErosion`; `_TearScale` (hole density), `_TearEdgeWidth`,
  `_TearEdgeTint` (fray) in the shader defaults. Known limits: hard alpha-test
  edges (fits the low-poly style); a garment that was authored translucent
  loses its transparency once swapped (none in the current wardrobe).
  Future (§40.10-C): per-zone tear stamps into a small per-instance mask RT
  (reuse the skin-decal pattern) so a dog bite tears the exact pant leg.
- **UPDATE — §40.10-C SHIPPED (zone damage + dirt layer, combined with A).**
  Instead of a mask RT, zone damage rides **world-space damage spheres** — no
  UV mapping needed, spatial locality does the garment-coverage mapping for
  free. Each hurt body zone (health < 0.9) plants a sphere at its bone anchor
  (Head→head, Torso→abdomenUpper, Pelvis→pelvis, Arm→forearmBend,
  Leg→shin — knees/elbows, where cloth really rips), strength = 1 − health,
  refreshed every frame so spheres track the animation.
  `NpcActorView.SetBodyCondition` (which already receives zone healths +
  hygiene for skin decals) builds the spheres and calls
  `BodyBones.SetWearGrime` → every SIM garment's `Wear.SetGrime` (hair is
  never in `_wears`, stays clean). The shader takes the max of global wear
  and local sphere tear, so a dog bite rips the exact pant leg even on a
  pristine garment. **Dirt layer**: `_DirtAmount = 1 − hygiene` drives
  noise-mottled blotches (separate `_DirtScale` noise octave) that multiply
  albedo toward `_DirtColor`; coverage grows from sparse smudges to
  near-full grime as hygiene drops. The lazy shader swap now also triggers
  on dirt > 0.15 or any damage sphere. Knobs: `_DamageRadius` (rip size
  around a wound), `_DirtColor/_DirtScale`, sphere threshold + anchors in
  `NpcActorView.ZoneBoneAnchors`.
- **SHIPPED (40.10-D — wear PAINTED into garment textures).** The
  world-space sphere clip made holes **breathe with the animated bones**
  (threshold = f(positionWS) vs a bone-anchored sphere → a hole opened and
  closed as the limb swung). `GarmentWearPainter` (the skin-painter tech on
  clothes, behind `Wear.PaintWearIntoTexture`) stamps holes into a
  **per-garment copy of the artistic tear mask in UV space** — glued to the
  fabric, animation-proof: bite holes place ONCE per damage-sphere quarter
  bucket at the sphere's nearest garment triangle (molly bake + nearest
  centroid, bake-scale detected vs renderer bounds), painted near-black
  (clip open immediately — a 0.12 tear floor guarantees it); natural-wear
  holes accumulate with erosion at seeded triangles, painted at rising
  depth-grays so the artistic destruction sequence still orders them. The
  shader keeps ripping via `_TearAmount` + mask only (`_SphereTearOn 0`;
  spheres still localize blood soak). Fray rims stay shader-side (mask
  bands — UV-stable). **Dirt on cloth = the same `dirt_dust` sheet as the
  skin** stamped into a per-slot albedo copy (count/alpha from 1 − hygiene,
  seeded on-island triangles; `_DirtAmount` muted → no double filth; wash
  restores the original). All event-driven on buckets (tear 0.05, dirt
  0.1, sphere 0.25) — zero per-frame painting. **Transparent garments**
  (stockings, sheer sleeves — `_Surface 1` or Transparent queue) never
  swap to the opaque tear shader (it rendered sheer fabric solid black):
  they keep their authored shader and their holes are ERASED from the
  albedo alpha instead (`Hidden/HexLive/AlphaErase`, dst.a ×= 1−src.a,
  ColorMask A) — bite holes punch immediately, natural-wear holes as
  erosion passes their depth, and the shader's own blending renders them
  see-through. **Swap fidelity:** the tear shader honors `_BumpScale`
  (`UnpackNormalScale` — garments authored with a neutralized bump popped
  to full-strength relief the moment damage swapped the shader, "нормали
  летают") and samples `_MetallicGlossMap` (metallic R × `_Metallic`,
  smoothness A × `_Smoothness`, white default = old constants — losing
  the gloss map flattened worn leather/satin to uniform plastic). Known limit: natural-hole
  seeds use instance ids, so their spots reshuffle across a save reload
  (bite holes re-derive from wounds and stay).

### 40.11 Character UI panel
- Select an NPC → a **button** opens a panel showing: **equipment slots**
  (what's worn/held where), **clothing durability** with progress bars,
  and **bone health** (which parts are wounded). Live inspection.
- **Status effects (§48):** a row of circular buff/debuff chips on the
  selected character (bleeding, sunstroke, freezing, starving, content…),
  each with a hover tooltip explaining what it does.

### 40.12 Expand the island & scattered loot
- **Bigger / multiple islands.** Scatter **findable items** (pickaxe, saw,
  varied gear) with different models & params — crafting takes a back seat
  to exploration/finding for a while.

### 40.13 Ragdoll
- Verify ragdoll works. Use it for **faint / collapse-from-exhaustion**
  and possibly a relaxed sleep flop.
- **UPDATE — SHIPPED & VERIFIED (presentation WIP).** `NpcActorView` builds an
  11-bone physics ragdoll procedurally on the Daz Genesis3 skeleton (hip,
  chest, head, l/r shoulder+forearm, l/r thigh+shin — Rigidbody + sized
  Capsule/Sphere/Box colliders + CharacterJoints with 40° swing / ±25° twist
  limits), lazily on first use. `SetRagdoll(bool)`: active ⇒ Animator off +
  bodies dynamic (limp); inactive ⇒ bodies kinematic + Animator re-drives (the
  actor stands back up on wake). `HexWorldRenderer` routes `IsFainted` →
  `SetRagdoll(true)` (bed-sleep still uses the baked Laying clip; any other
  state ⇒ `SetRagdoll(false)`). Verified live in the editor: through the
  shipped code path a fainted actor's skeleton collapses limp to the ground and
  settles at rest (hip Y 1.3→0.01, head→0.02, rigidbodies sleeping) — visually
  a crumpled body on the terrain. Note the `??`-vs-Unity-fake-null pitfall:
  component lookups use `GetComponent(); if (x == null) Add()`, never `??`.
- **UPDATE — DEATH = RAGDOLL CORPSE (view adopts the body).** Sim logic was
  already complete since iters 15/16 and is unchanged: death spawns a
  `corpse.npc` object at the NPC's junction (`CurrentUser` = whose body,
  decay 2 days), witnesses in range grieve instantly, others on perception;
  grieving NPCs take `GoalType.Mourn` (walk to the corpse + Observe →
  "Mourned"), then `Bury` → a permanent grave. The only sim change is
  export: `ObjectSnapshot.OwnerNpcId` (= `CurrentUser`) so the view can
  match a corpse to the actor who died. View: when an NPC vanishes from the
  snapshot, `HexWorldRenderer` finds her `corpse.npc` by `OwnerNpcId` and
  ADOPTS the actor body instead of destroying it — clears laying/sit/gaze,
  destroys the old capsule-blob corpse view, and keeps the body under the
  corpse object's id. When that object leaves the snapshot (decayed or
  buried → grave), the body view is destroyed. `SetRagdoll` now also
  disables LookAtIK, and the procedural pose layers (action/thermal/
  posture) are gated off while ragdolled so nothing fights the physics.
- **UPDATE — RAGDOLL RETIRED (death/faint = lie down asleep).** In play the
  ragdoll spun on activation and fell through the terrain — hex tiles are
  collider-less meshes, so physics bodies have nothing to land on. Decision:
  no ragdoll, no extra physics. Death and faint/collapse now play the baked
  laying clip pinned to the tile's ground Y (`SetLaying(true, null,
  GroundY(tile))`) — the body quietly lies down and "sleeps" where it fell;
  the adopted-corpse lifecycle above is unchanged. `corpse.npc` and
  `grave.npc` objects render as invisible anchors (no blob, no grave
  marker); sim-side Mourn/Bury logic untouched. `SetRagdoll` stays in
  `NpcActorView` as dormant code (no callers) in case tile colliders ever
  land and it's worth revisiting.

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
   **UPDATE — SHIPPED (functional crossing).** No new goal or depletion
   economy was needed: the existing `GatherTools` goal drives it once two
   conditions hold. (a) The strait is a CHEAP crossing — `OpenStraitCorridor`
   tags its junctions into `WorldState.StraitJunctions`, and `HexPathfinder`
   charges them `StraitCost` (2×) instead of the ring's `SwimCost` (4×), so a
   route will actually take the hop; the wider ring stays 4× (a shark-risked
   last resort nobody enters). (b) The island holds an EXCLUSIVE resource —
   the ONLY `tool.pickaxe_stone` now sits on the island (the mainland scatter
   was removed), so a tool-seeking NPC must cross for it (a palm rides along
   for food/wood). Measured: 2 of 6 seeds show a real crossing (the island
   pickaxe is gathered and carried home), all 6 stay soak-green. A non-
   exclusive resource is dormant (nobody crosses — home has equivalents), and
   the cheap strait alone doesn't reshuffle the colony past the §40.18-step-3
   budget. The shark bite during the short strait hop is rare enough to keep
   `survivorsOk`. Depletion economy + LLM driver remain future enrichment; the
   core loop (cross the strait for island-exclusive loot) is live and green.
Presentation: water-swim animation, shark model + fin, island terrain —
all Unity-side, land when the editor is connected.

#### 40.18-B Swim presentation + swim test scene (shipped, tuning)
The Unity side of step 1: the swimmer LOOKS like a swimmer. Two Mixamo clips
imported through the AnimLibrary pipeline (`Swimming.fbx` → strokes,
`TreadWater.fbx` → treading idle; both loop, authored root height kept — the
view owns the plunge). Animator (`HexNpcLocomotion`): new `Swimming` bool;
`TreadWater`/`Swim` states mirror Idle/Walk gated by `Swimming` + the same
measured `Speed`; `JumpDown` exits straight into `TreadWater` when the water
flag is already up, so the dive lands treading, not standing.

Flow of one crossing (sim leads, view follows):
- **Jump in.** Stepping land→deep water is the ordinary §21.21B hex-step
  drop; the renderer sees the big Y delta and plays JumpDown, whose offset
  curve carries the body from the bank INTO the water.
- **Sink, don't snap.** A swimmer's root hangs `SwimVisuals.SinkDepth`
  (default 0.35 wu) BELOW the water surface (`HexWorldRenderer.ActorGroundY`;
  surface = tile top − 0.4·step per §31C.4). Deep water only — walkable river
  shallows still wade ankle-deep. Feet never snap onto the surface.
- **Tread a beat.** `MovementSystem` holds the swimmer still for
  `SwimEntryPauseSeconds` (default 0.75 s, `SwimEnter` trace) on entering a
  swim tile from land — reuses the climb-pause plumbing; the animator shows
  `TreadWater` because measured speed is zero.
- **Swim.** Movement resumes at `SwimSpeedFactor` (default 0.55×); the Swim
  clip plays at authored pace (the walk-cadence ground-matching is bypassed
  while swimming — sim slowness would crawl the strokes). The procedural arm
  layers (actions/thermal/posture) stand down in water.
- **Climb out.** Water→land is one elevation step up: the stock §21.21B
  windup (treading at the edge) + JumpUp hop carries her onto the bank.

Deep water = `Water && !Walkable` on both sides of the fence (sim
`MovementSystem.IsSwimTile`, view `_swimCoords` from the snapshot).

**Swim test scene** (`Scenes/SwimTest.unity`, `SwimTestBootstrap` — dev tool
like the wardrobe room): a real simulation cut down to the water-and-ledges
problem. The home island (3×3) carries a HILL tile at elevation 2 with ALL
the coconuts on top; a 2×3 deep strait (fully flooded into `SwimJunctions`
at bootstrap: the build-time ring alone can't bridge a full water tile, same
lesson as step 3) splits it from the far island (2×3) holding the only
`water.pond`. The girl wakes starving AND parched, so one lap exercises both
mechanics: hex-step jump up the hill → eat → jump down, then dive into the
strait → tread → swim → climb out → fill the bottle → drink (no `tool.bottle`
item needed — `FillBottle` charges the NPC's own `BottleWater`). When fed and
watered she is teleported home needy with the bottle emptied (else the next
"drink" is a sip on the spot, not a swim); R restarts a lap manually. The
swim knobs live on the component in the inspector, pushed into the statics
every frame: `SinkDepth`, `SwimEntryPauseSeconds`, `SwimSpeedFactor`. Jump
timing is NOT there — §21.21B centralizes it in `HexHopTuning`: the sim hops
the edge segment in `HopSeconds`, then stands `LandIdleSeconds` at the far
junction where the tile switches; the view arc (both directions) plays
inside that stand. Water transitions are excluded from sim hops — the dive
rides the same view arc over the swim-entry treading pause. No sharks — the
test runner registers the default system list, which never included
`SharkSystem`.

### 40.19 Dropped clothing = real garment lying flat (shipped)
Dropped clothing used to render as a coloured primitive cylinder. Now any
world object whose definition id has wear prefabs (`Resources/HexLive/Wear/
<simId>/` — clothing.*, underwear.*, armor.*) renders as the REAL garment
mesh lying flat on the ground, textures intact (`GarmentDropFactory`, wired
into `HexWorldRenderer.CreateObjectView` between the Objects-prefab path and
the low-poly/primitive fallbacks).

How: the wear prefab's skinned mesh renders in bind pose through a plain
MeshFilter (a T-pose shirt flattened reads as clothes laid out on the
ground). No vertex baking — the source meshes may lack Read/Write — the
whole effect is transforms: the mesh child is offset by `-bounds.center`
(the Daz bind space has its origin at the character's FEET; this recentring
is what puts the pivot at the garment's own centre so a drop sits on its
anchor instead of floating a torso away), rotated −90° about X onto its
back, and squashed to `FlattenFactor` (0.12) of its depth via the parent's
Y scale — not zero, coplanar faces would z-fight. Footwear (id contains
boot/shoe) keeps its 3D shape and just stands on the ground. Multi-piece
items (underwear = bra + panties) lie side by side, group centred. The drop
is scaled by the same actor factor the girls wear it at (`HexRadius *
NpcHeightFactor * 2.4 / 1.7`), gets a deterministic scatter yaw from the
object id, and is grounded with a 1 cm epsilon against tile z-fighting.
TUNING KNOBS: `FlattenFactor`, `PieceGapFactor` (gap between side-by-side
pieces, 0.2 of the widest).

### Implementation order (living)
Robustness first, spectacle second: 40.1 Stamina → 40.2 Blood →
40.14 tiered beds + tent → 40.3 medicine/Safety → 40.6 Hygiene →
40.7 tan/skin → 40.10 wear + 40.8 injuries + 40.9 poses (presentation,
needs live Unity) → 40.11 UI → 40.12 bigger island/loot → 40.13 ragdoll →
40.15 escape/rafts/shark → 40.5 cooperation/theft → 40.16 LLM.

## §41 Loading, Save & Offline Progression (iteration 37)

### 41.1 Loading screen (pause-until-ready)
Play mode used to drop straight into a live world (`startPaused: false`),
so the sim ticked while Unity was still spawning terrain/actors — then the
runner's accumulator "caught up" with a burst of ticks (visible stutter +
time jump). New flow: the runner starts PAUSED; a full-screen loading
overlay (UIDocument, top sorting order) drives phases:
1. **World** — bootstrap the WorldState (synchronous, cheap).
2. **Time passed** — if a save exists, restore the model blob (§41.2 v2),
   then wind forward by `offlineTicks` (§41.3), chunked ~10 ms per frame
   so the progress bar animates.
3. **Island** — let `HexWorldRenderer` build all views (terrain mesh,
   actors, wardrobe) behind the overlay for a few frames.
4. **Warm-up** — select each NPC once (builds the character panel UI,
   portrait camera + its RenderTexture, shader variants) so the FIRST real
   click has no hitch — this was the "заход в персонажа" lag: the whole
   panel tree + portrait pipeline built lazily on first selection.
5. Fade out, unpause, autosave timer starts. Camera: `NpcSelection.Select`
   (Jana) — the RTS camera's selection handler enters orbit on her, so the
   game opens looking at Jana with her panel up.
Background art: `Resources/HexLive/UI/loading_island` (fal.ai-generated,
the three girls at sunset on the low-poly island, 1920×1080). Game logo
`Resources/HexLive/UI/logo` ("HEX ISLAND SURVIVE", alpha-cut) overlays the
art top-left; no text titles are drawn by code.
Anti-burst guard (independent of loading): the tick accumulator is clamped
to `TickDeltaTime × 12` (≈3 game-seconds of backlog) — a frame hitch may
never turn into a catch-up burst; at 50× the per-frame budget (~0.8 s) is
far below the clamp, so top speed is unaffected.

### 41.2 Save format — the save IS the model (v2, iteration 39)
v1 stored `{seed, tick}` and REPLAYED the deterministic history on load.
That dies the moment non-deterministic inputs arrive — player commands and
LLM decisions (§40.16) can't be replayed from a seed — and replay time grew
with total played ticks. v2 serializes the model in full:
`WorldSaveSerializer` (Simulation/Persistence, pure C#, versioned binary)
writes every piece of state the sim can mutate — tick, environment/weather,
build project, runtime tile flags (HasFloor/Indoor) and junction
Blocked/Door sets, all world objects, all NPCs (needs, mind, plan,
execution, movement, perception, memory, social, inventory, worn items,
body/wounds), wildlife (dogs/rabbits/sharks), reservations, occupancy and
the runtime caches (verbatim, list order preserved — systems iterate
them), plus the wildlife respawn timers (moved into `WorldState` from
system-local fields, which silently reset on load). Static topology —
tiles, junction ids, adjacency, content catalog — is REBUILT from the seed
by `WorldStateFactory`, never stored; the blob is tens of KB.
File: `Application.persistentDataPath/hexlive_save.dat` — a small header
`{magic, version, seed, tick, unixSeconds, speed}` (read alone for the
menu, §41.4) followed by the blob; written to `.tmp` and swapped whole so
a crash mid-write can't eat the old save. Autosave every 60 real seconds +
on quit / play-mode exit, as before. A corrupt/mismatched save falls back
to a new world.
NOT serialized, by design: trace events (no sim effect), decision debug
scores (rewritten every pass), the junction-components cache (derived,
rebuilt on first pathfind). Dictionary ITERATION order is inherently
unsaveable (bucket/freelist internals): a restored world is content-
identical — headless-proven byte-exact on re-serialize across 4 seeds —
and simulates identically for a window (measured 0–1600 ticks), after
which enumeration-order differences make it a sibling world, not a wrong
one. Accepted: the design premise of v2 is that determinism is going away
anyway.

### 41.3 Offline progression
On load, elapsed real time becomes game time: `offlineTicks =
(now − save.unixSeconds) × 4` (1 tick = 0.25 s at 1×), capped at
**3 game days** (7200 ticks, `DayLengthTicks = 2400`) so a week away
doesn't starve the colony or stall the load. The wind starts from the
RESTORED state (v2: load first, then simulate the absence) and finishes
BEFORE any view spawns — the player returns to "time really passed":
resources regrown, needs drifted, maybe someone got bitten.

### 41.4 Main menu (iteration 38, restyled iteration 43)
The loading screen opens with a MENU over the island art, before any world
exists — a dark rounded card docked bottom-left, icon rows painted with
Painter2D (no texture assets): **«Продолжить»** (gold primary; dimmed and
unclickable without a save), **«Новая игра»** (deletes the save, rolls a
fresh seed), **«Настройки»** and **«Персонажи»** (visible placeholders,
disabled for now), **«Выйти из игры»** (`Application.Quit`, stops play mode
in-editor), then a divider and the tagline («Исследуй. Строй. Выживай.»).
The world is bootstrapped only after the choice — the runner sits
unconfigured (renderer/panels all guard on `IsReady`), so the menu costs
nothing. After the click the flow is §41.1 unchanged: restore → offline
wind → spawn → warm-up → fade → Jana.

### 41.5 Wake-up grace (iteration 38)
Waking from any Sleep (bed / leaf mat / ground) sets
`Mind.WakeGraceUntilTick = tick + 12` (~3 game-seconds). While in grace the
DecisionSystem does not score goals — the NPC simply STANDS where she slept,
coming to her senses; no instant errands off the pillow, and the presentation
GetUp clip has room to play without foot-sliding (exported as
`NpcSnapshot.IsWaking`).

## §42 Survival Realism Rebalance (iteration 38)
The colony's numbers dated from the subsistence-race era; with the player
watching one girl closely they read arcade-fast. New targets (day = 2400
ticks = 24 game hours, 100 ticks = 1 game hour):
- **Sleep**: a full recharge is a real night — bed 0.18 / leaf mat 0.15 /
  bare ground 0.12 energy per 100-tick sleep block (was 0.5/0.45/0.5), so
  0→1 takes ~6 game hours on a bed. Energy drain slowed to match a
  one-sleep-per-day budget: EnergyRate 0.015 → 0.007 per slow tick
  (~1.05 bars/day).
- **Healing**: wounds knit in DAYS, not hours — part regen 0.02 → 0.0018
  per slow tick (a 0.6 dog mauling ≈ 2.2 game days), blood refill
  0.02 → 0.005. Food no longer power-heals: eating feeds the regen
  CONDITION (fed+rested) but the rate above is the cap. First aid stays
  meaningful but not magic: bandage part-heal 0.25 → 0.15 (blood boost 0.4
  → 0.25), pill 0.2 → 0.10 (blood 0.2 → 0.15).
- **Clothing warmth** (`EquippedWarmth × 10 °C`): underwear is DECORATIVE
  warmth (0.01–0.03 — bikinis at 10 °C mean shivering), real cover carries
  the budget: tops/shirts ~0.12–0.15, dress 0.2, pants 0.25 (shorts/skirts
  0.08–0.12), boots 0.12–0.15, gloves/scarf 0.04–0.06, coat/jacket 0.4.
  A full pants+shirt+boots+coat outfit ≈ 0.9 → +9 °C; the old uniform
  0.08-for-everything wardrobe is retired.
- **UPDATE — comfort band moved to [16,22] °C** (was [12,20]; the first pass
  re-priced the wardrobe but left the band, so 10 °C naked still read
  "almost fine" on the bar and in the pressure — the exact bug it was meant
  to kill). Now: decision pressure is cold below 16 (0.025/°C, cap 0.12),
  overheating above 22; the UI signed comfort uses the SAME band (the bar
  may never say "fine" while the body freezes; -1 over a 12 °C span).
  Dress gate: effectiveTemp < 16 (was 14); undress gate: > 24 — only real
  heat strips layers, day-warmth no longer undresses what the night needs.
  **Heat can never strip an NPC naked**: `FindRemovableItem` skips the
  Underwear layer entirely (it barely warms — removing it is pure nudity,
  which was exactly the "голые при 10 °C" bug: day heat peeled everything
  including bras, and the [12,20] band called the aftermath comfortable).

### 42.A Sweat → thirst (shipped)
Overheating now costs water: in `NeedsDecaySystem` the thirst tick is
scaled by `1 + SweatThirstFactor × max(0, ThermalComfort)` — up to **+25%
thirst at heatstroke-level heat** (`SweatThirstFactor = 0.25`). Reads the
previous slow tick's signed comfort; the cold side is free (a shivering
body does not sweat). This closes the loop with the sweat VISUAL
(NpcActorView's gloss sheen at thermal/0.6): what glistens now also
drinks. The sweat multiplier rides the existing NeedsDecay trace
(`Sweat=x.xx`) — no new event type, nothing to whitelist in the harness.

Balance (the §40.18 lesson again — multi-dimensional loosening where a
single knob whack-a-moles): factor 0.5 broke seeds 777+42 on a green
HEAD, 0.25 and even 0.15 still broke 777 (dressed-for-night girls hit the
day heat and spiralled: heatstroke + faster thirst), and 0.05 alone was
green but homeopathic. The shipped config pairs **factor 0.25 with the
base `ThirstRate` eased 0.020 → 0.018**: a cool girl is slightly less
thirsty than before, a heatstroking one ~+12% net — heat matters
RELATIVE to the baseline. 6/6 soak green (12345/777/999/31337/555/42, 3
game days); the same easing also turned the WarmUp-era near-naked-start
tree from 2/6 to 6/6 green. TUNING KNOBS: `SweatThirstFactor`,
`ThirstRate`; the pair moves together — retune both or neither.

## §43 Sun & cast shadows (iteration 39)
The sim computes WHERE shadows fall, matching the rendered sun. Sun path is
a pure function of TimeOfDayNormalized (up for p in [0,0.5] = 06:00-18:00):
azimuth sweeps east -> south -> west, elevation follows sin (peak ~65 deg at
noon, ~8 deg at dawn/dusk). Every medium tick EnvironmentSystem rebuilds
`WorldState.ShadedTiles` (derived, never serialized): for each tile a ray
marches TOWARD the sun up to 5 hex steps; a blocker shades it when
`blockerElev >= myElev + k * tan(sunElev) * (stepWorld/ElevationStep)` —
tall hexes cast real directional shadows (long at dawn, none at noon for a
1-step cliff). Shade-tagged objects (palms/big trees) add +2 virtual steps
on their tile and always shade their OWN tile (canopy); Indoor tiles block
as +2 (the hut wall). `IsShaded` now reads the map, so UV exposure, tan,
sunburn and CoolOff inherit directional shade for free. Presentation: the
snapshot exports the sun azimuth/elevation so SkyDayNightController can
align the rendered light to the same path (visual shadows == sim shade).

## §44 Blood, rest & herbal bandages (iteration 40)
- **Clotting**: bleeding only while a wound is FRESH (heal01 < 0.3) — a
  closing wound stops draining blood; the first hours after a mauling stay
  deadly, the two-day tail doesn't.
- **Bed rest**: blood refill x3 while sleeping, x2 by a burning fire
  (stacking with fed) — a hurt girl who huddles and sleeps pulls through.
- **Healing herb**: `herb.bush` (new Flora spawn, 3 across the island)
  produces `resource.herb_leaf` nearby (FruitProduction pattern, max 2).
  Craft `CraftBandage` at the campfire: 2 leaves -> +1 bandage (available
  when hurt or stock < 2). Bandage use now REMEMBERS the dressed zones
  (`NPCState.BandagedZones`, cleared when the zone heals past 0.7) and the
  snapshot exports them — presentation spawns a leaf-wrap decal on the
  bandaged spot.
  - **Medkit vs herbal (the leaf wrap only means gathered plantain):** the
    two starting bandages (spec 40.3) are a pre-made medkit, NOT gathered
    leaves. `NPCNeeds.HerbalBandages` tracks how many of the pouch's bandages
    were crafted from gathered plantain; `CraftBandage` bumps it. On a
    dressing the MEDKIT bandages are spent first and leave NO leaf-wrap decal
    (the wound is patched but the plantain visual never appears for a bandage
    she didn't gather); the leaf-wrap (`BandagedZones`) is stamped ONLY when a
    herbal bandage is the one consumed — so the plantain wrap on screen always
    corresponds to leaves she actually went out and picked. Persisted in the
    save blob (BlobVersion 2).
- **Presentation (shipped)**: `herb.bush` renders as a procedural low-poly
  medicinal shrub (`LowPolyToolFactory` — splayed stems, leaf blades, pale
  bloom tips so it reads special among the greenery); `resource.herb_leaf`
  as a single leaf. The **bandage decal** (`SkinDecals`, DecalType.Bandage)
  uses a fal.ai leaf-poultice texture (`Resources/HexLive/Decals/
  bandage_wrap.png` — green leaves bound with fiber twine; procedural
  crossed-band fallback). A bandaged zone REPLACES its wound decals with
  one wrap decal (same deterministic zone placement, salt 271); when the
  sim clears the zone from `BandagedZones` the wrap disappears and any
  still-open wounds show again.
- **Water**: ThirstRate 0.020 -> 0.017 (thirst was permanently red and
  starved the blood regen's fed-condition).
- **Wound eviction payback**: when the 12-wound cap evicts the oldest
  wound, its remaining held HP returns to the zone instantly (fixes
  survivors stuck at zone 0 — the 999/31337 structureOk breaks).

## §45 Free hands — the progress drive (iteration 41)
Survival ate 100% of the day and the endgame was unreachable (30-day runs:
raft 0/20, no pickaxe, no build). When the needs are HANDLED — fed, watered,
rested, warm, no fresh danger — a +0.3 "free hands" drive lifts every
industry/progress goal (GatherStone/Wood, CraftAxe/Pickaxe, HarvestTree,
MineBoulder, Build, BuildRaft), so a stable colony automatically turns its
surplus into tools, the hut and the escape raft. Pass criterion for future
soaks: visible raft progress within 10 game days on stable seeds.

### §45 r3 — the raft actually launches (iteration 43)
r2 left the endgame formally reachable but practically locked. Instrumented
15-day soaks (gate probe sampling every 25 ticks) found three stacked locks,
each fixed in `DecisionSystem`:

1. **The fire clause was a permanent lock.** `!fuelLow` (fuel ≥ 600) was
   almost never true in the campfire era; even the softened "fire burning"
   held < 16% of sampled npc-ticks — the pit is cold most of a long run.
   The BuildRaft gate now has **no fire condition**: hunger < 0.55,
   thirst < 0.55 (matched to the free-hands drive), no danger memories,
   raft known & reachable. "Fed, watered, safe" already means the
   household can spare a pair of hands; TendFire outbids the raft when
   fuel genuinely matters.
2. **The raft had no wood supply of its own.** GatherWood only fired for
   the hearth (`fuelLow`) or the hut piece, so the coast run rode on
   leftover logs (3 deposits in 15 days). New `raftWoodDemand`: a settled
   girl (same gate needs) who knows the raft stocks **up to 3 logs**
   before the run, and the demand adds **+0.3** to GatherWood's score so
   stocking actually wins the auction.
3. **The auction weight was survival-era.** At base 0.28 BuildRaft won
   1–5 auctions in 15 days against needs hovering ~0.5–0.6. Now
   **0.4 + freeHands + 0.05 × carriedLogs** (≈ 0.85 loaded & free): when
   the gate is open the endgame outbids moderate needs; the gate itself
   guarantees she starts the trip fed, watered and safe.

**Measured (15-day, 6 seeds):** raft launched on 4/6 seeds (days 10–15);
12345 reached 9/10, 777 3/10. BuildRaft plans 11–18 per seed (was 1–5),
zero plan failures — when the goal wins, the trip works. Known remaining
weakness (out of scope here, next candidate): the colony still bleeds on
long horizons — fire alive only ~10% of the time, chronic dehydration
events, 1–2 deaths per seed by day 15 — the launch currently races the
decay rather than riding a stable surplus.

### §45 r4 — raw water eased; the sickness death-spiral broken (iteration 43)
Instrumented death-cause capture (last 3 damage events before each NpcDied)
showed the long-horizon collapse was one loop: the fire is dead ~90% of the
time (FireLit 1–6 per 15 days, FireFueled 0 — TendFire never wins the
auction against needs), so boiled water barely exists (raw:boiled drinks =
478:11 across 6 seeds), so the colony lives on the 30%/−0.15-torso raw-water
gamble at 70–105 drinks per soak — 3–5 full torsos of expected damage, and
half of all deaths were "Torso destroyed by sickness"; the other half were
starved/parched attrition with thirst pinned at 1.00 (the thirst treadmill
is 3.5–4 bottles/day at ThirstRate 0.018 — water chores already eat the
day, sickness was pure downside).

**The knob (soft param, no behaviour change):** sickness roll 30% → 15%,
torso hit 0.15 → 0.08 (expected ~0.012/drink — inside fed-regen's budget).
The gamble stays; it stopped being a death sentence for a fireless colony.

**Measured (25-day, 6 seeds):** sickness deaths ZERO; 5/6 colonies have
survivors at day 25 (was: near-total die-off by day 15–18); raft launched
4/6 (days 9–13) with 12345 — the r3 straggler — now finishing at 2/2 alive;
31337 at 9/10 with a survivor. Remaining failure mode is seed 777: wiped by
day 7.5 by hypothermia (death at ThermalComfort −0.93) + a dog mauling —
the cold/fire economy, a separate iteration (fire auction weight is
reshuffle-heavy territory, per the climb-weight lesson).

### §45 r5 — the cold layer; FULL-GAME PASSABILITY 6/6 (iteration 43)
Two sessions worked this layer concurrently in the same tree; the edits
are complementary and are recorded together here.

- **Friction fire** (this session): the freeze probe (sampling every 25
  ticks while ThermalComfort < −0.5) showed 60–75% of all freezing
  npc-ticks across ALL seeds were "dead fire + wood in hand + NO lighter"
  — one lighter per colony and a half-day burn meant the carrier was
  almost never the one freezing at the pit. TendFire no longer requires
  the lighter when ThermalComfort < −0.35 (hand-drill in genuine cold;
  the threshold sits before the −0.85 damage band so the lighter stays
  meaningful in mild weather).
- **Attrition & cold easing** (parallel chip session): starvation
  attrition 0.03/0.05 → 0.02/0.035; sickness damage floored at 0.2 torso
  (grind-not-kill — lethality needs a second stressor); hypothermia
  0.02 → 0.012 per slow tick (one bad night ~0.44, not 0.74 — dawn gives
  a chance to dress; two exposed nights still kill); fed-regen 0.0018 →
  0.0030 per slow tick and its gate hunger < 0.5 → 0.6 (the long-run
  colony hovers at ~0.5–0.6 hunger, so bodies never healed between
  episodes).
- **Emergency unload** (same chip session): getFoodAvail requires
  inventory SPACE, and the r3 raft stockpile (3 logs + leaves + tools)
  fills packs that nothing ever empties — on a 25-day soak 6 of 8
  starvation deaths died at Hunger = 1.00 with 10/10 slots and zero food
  while coconuts sat at producer cap (25–39 on the ground, 550+ rotting).
  A girl at hunger ≥ 0.8 with a full pack and no food in it now drops one
  carried resource per slow tick (wood → leaves → stone, never tools) at
  her feet. Last-resort by construction, like food-sharing/theft.
- **Boxed-in bugfix** (same chip session): an NPC standing on a junction
  that BECOMES Blocked (bed/rack obstacle spawn) kept the still-valid key,
  so pathfinding could never start and every plan read unreachable — she
  starved pinned in place (3 of 6 deaths on one soak, always at the
  campfire where furniture lands). Fix in both directions:
  `SetObstacleBlocking` now nudges bystanders (`CurrentJunction = null`,
  same as the hut-wall builder) and `ResolveCurrentJunction` re-anchors
  off a blocked junction (self-healing net for any future blocker).

**Measured (25-day, 6 seeds, live tree with friction fire + all of the
above): 6/6 colonies alive at day 25** — 12345/777/31337 at 3/3, 999/555/42
at 2/3; 3 deaths total (r4 baseline: 12 deaths, 2 full wipes). Raft
launched 4/6; 3-day gate 6/6 all-alive. The 3 remaining deaths are late
(day 14–22) chronic-attrition cases — bodies worn down over days, not any
single mechanic.

**Measured (30-day frozen-snapshot soak, 6 seeds): 6/6 launch the raft**
(555 day 3.1, 999 day 11.6, 31337 day 15.1, 12345 day 17.1, 42 day 19.7,
777 day 27.7) — every colony has survivors (12345 and 31337 and 555 at
3/3, zero deaths), 5 deaths total across all seeds (r3 baseline: 18 in
15 days). Seed 777 — the r4 hypothermia wipe — finishes 2/2 alive. The
game is now completable on every soak seed; remaining polish (boiled
water still ~5% of drinks, chronic dehydration misery, dog maulings)
is quality-of-life, not passability.

## §46 Difficulty pass — putting teeth back (iteration 44)
After r5 the game was TOO safe (6/6, three zero-death colonies). Goal from
the user: ~50% losses, deaths back in the story, "sharp" deaths (blood,
beasts, cold) rather than the grind we deliberately fixed. Measured ladder
(12 seeds — canonical 6 + 7/101/2024/4242/90210/13 — 30-40 game days,
WIN = raft launched with survivors, LOSS = colony wiped):

| knobs (cumulative) | result | reading |
|---|---|---|
| r5 baseline | 12/12 WIN, ~5 deaths | unkillable |
| bleed 0.06→0.09, dog bite 0.06→0.07 | 12/12, few deaths | girls out-fight the pair of dogs |
| + MaxDogs 2→3, respawn 3d→1.5d | 7/12 in 30d, 0 wipes | dogs STALL (danger-memory gates the raft), don't kill |
| + bite 0.09, horizon 40d | 12/12, 3 deaths | stalls finish given time; reshuffle noise |
| + attrition 0.025/0.045, sickness floor 0.2→0.1, hypothermia 0.012→0.015 | 10/12, 12 deaths, 1 wipe | deaths are back (dogs 5, bleed 2, hypo 1); most colonies lose 1-2 girls |
| + attrition 0.03/0.05 (pre-r5, CURRENT TREE) | 11/12, 14 deaths, 1 wipe | the working "drama" balance |
| experiment: 2-girl start (snapshot only, not shipped) | 11/12, 1 early wipe | redundancy is NOT what protects the colony |

**Conclusion:** the r5 safety nets made the colony a homeostat — individual
deaths no longer cascade into wipes (that cascade was exactly what r4/r5
removed). Damage knobs restore per-girl mortality (~1-2 deaths/colony/run)
but colony-level loss saturates at ~10-15%; pushing predator pressure
further first produces timeout-stalls, not drama (the 3-dog row). A true
~50% loss rate needs a swing MECHANIC (rare seeded catastrophes: night
pack raid, storm that wrecks raft progress, epidemic), not a bigger
constant — recorded here as the §46 v2 candidate.

### §46 v2 — swing catastrophes; 50% HIT (iteration 44, SHIPPED)
Two rare seeded events (pure functions of seed+day — deterministic,
save-safe, no RNG state):

- **Night raid** (`DogSystem`): each day from day 2, a 25% roll spawns a
  pack of +3 ordinary dogs at dusk (tick offset 1800). Days 0–1 are a
  grace period (a raid on an unestablished camp is a storyless coin-flip).
  Raid dogs linger until killed — they can be fought or fled. Knobs:
  `RaidChancePerDay=0.25, RaidPackSize=3, RaidDuskOffsetTicks=1800`.
- **Storm surge** (`WeatherSystem`): each day, a 12% roll washes 2 logs
  off the raft (offset 1600, on the Slow grid). Losing progress stretches
  the run → more raid rolls — the catastrophes compound. Knobs:
  `StormChancePerDay=0.12, StormRaftLogLoss=2, StormSurgeOffsetTicks=1600`.

**Measured (40-day, 12-seed soak): 6/12 WIN — exactly the target 50%.**
4 LOSS (colony wiped, all by raid packs), 2 TIMEOUT (alive but storm-set-
back, 1-3/10 raft at day 40). Winners mostly finish 3/3 ALIVE ("fought
them off and sailed") — the drama profile the user asked for: NightRaid
fires 4-8 times/run, StormSurge 1-4. Unity-target build
(HexLive.Simulation.csproj, netstandard2.1) clean. Difficulty dial for
the future: RaidChancePerDay is the primary knob (0.25 ≈ 50%; lower it
for an easier mode, raise for brutal).

## §47 The fire zone & the comfort chain (iteration 44)

### §47.1 Ember ring — the campfire is a ZONE, not a cell
User: the fire should block a hex ring around itself, not one junction,
and furniture must be built in the passable zone with an offset.
`campfire.spot` now has `ObstacleRadius = 0.8 × HexRadius`: the anchor
plus the tile's interior junction ring block (the fire hex is solid),
while the corner junctions — the lattice the camp walks and sleeps on —
stay passable. Interactions survive by construction: beside-arrival
(`CollectStandableAround`) BFS-walks through the blocked cluster to the
first standable rim, still within 1 tile of the fire (full +8° warmth).
Furniture placement (`FindSpacedFurnitureSpot`) now uses the same rim BFS
instead of the anchor's immediate neighbors, so beds/racks land just
outside the ember ring, fireside-close with a natural offset.
**Measured lesson — 1.05R is too greedy:** blocking the corner junctions
too swallowed the fireside beds; Sleep plans failed 87-224/seed, the
sleepless girls met the night raids in the open (bites ×5-10) and wins
collapsed 6/12 → 3/12. At 0.8R the ring costs ~1 seed of winrate (noise-
level) and an A/B probe (ring off) confirmed the chronic Sleep-fail
churn predates the ring — it was the bed shortage.

### §47.2 Comfort chain — why nobody built beds (user question), fixed
Diagnosis (probe: bedDeficit/leaves/chopTool sampled every 25 ticks):
1. `craftBedAvail` required a BURNING fire (the same permanent lock the
   raft and fire chains had — the pit burns 10-20% of the time);
2. `!HasReachableWithTag("Bed")` capped the colony at ONE crafted bed
   for three girls — bedless-girl Sleep churn was the loudest signal in
   every soak (787-995 plan-starts per 25 days, mostly retries);
3. palm leaves held ≥3 in **0%** of sampled npc-ticks — HarvestTree at
   score 0.3 never won the auction (lost to Dress/Socialize), so the palm
   was never chopped and the 3-leaf kit never existed.
Fixes (all availability/score, no new mechanics): `bedDeficit` = reachable
beds < living girls replaces both the one-bed cap and drives a leaf-supply
clause in `harvestTreeAvail`; the burning-fire clause dropped (weaving at
the cold pit is fine — the Craft interaction never needed the flame);
`bedChainPull` +0.25 on HarvestTree while the chain is short (deficit, no
leaves, has axe/saw) — mirrors the spec-42 cold chain.
**Measured:** beds now get built (4/12 seeds crafted 1-3 beds; a full
bed set drops Sleep fails ~190 → 11 and seed 90210 wins by day 6.7).
The chain spends real auction time in the §46 world, so the difficulty
dial moved one notch: `RaidChancePerDay 0.25 → 0.20`, final winrate
**5/12 (42%)** — inside the 40-60% target band.

### §46 v3 — pond retirement + hex-hop rebalance (iteration 45)
Retiring the two dry ponds (water now ONLY at the river) plus the §21.21B
hop ceremony re-broke the balance: 0/12 (hypothermia+starvation wipes —
in-water fill anchors soaked every water run, and the jump ceremony taxed
every seam crossing). Recovered to **5/12 (42%)** with, in order of impact:
1. `water.river` anchors moved to the dry BANK tile beside the river
   (in-water anchors = wet clothes = zero warmth = cold death spiral);
2. hop ceremony survival guards: danger in memory OR Hunger/Thirst ≥ 0.5
   skips the windup/slow-hop/land-idle framing entirely (and danger mid-hop
   aborts it) — leisure gets the pretty jump, survival gets the scramble;
3. budget re-widen: HungerRate 0.016→0.013, ThirstRate 0.018→0.014,
   BiteDamagePerPass 0.09→0.07, hypothermia part-damage 0.015→0.012,
   `RaidChancePerDay 0.20 → 0.12`.
4. §21.21B v2 — EDGE PADDING replaced the trigger radius: the hop takes off
   `EdgePadding` (0.5) before the edge junction AND lands the same padding
   past it (HopTravelRemaining countdown; a path ending at the edge lands on
   it). She never stands flush to a wall or on a drop lip.
5. §21.21B v3 — the six ceremony knobs merged into Takeoff/Landing clip
   markup (see §21.21B). The ~2x shorter total window eased the economy
   (7/12); `RaidChancePerDay` recalibrated 0.12→0.21.
6. v3.2 — hop survival logic fully removed (elegance): paid with bite 0.06,
   hunger 0.012, raids 0.10 → 6/12 (50%).
7. §21.21B v4 — circle climbing reshuffled to 4W/4L/4T on 40d; the timeouts
   were lone-survivor storm stalemates, `StormChancePerDay` 0.12→0.08, and
   on the honest 60-day horizon they RESOLVE (one late win, three deaths).
8. §21.21B v5 — the universal clamp (walking can never enter the ring)
   lengthened every cliff-side route; probe confirmed mechanics intact
   (movement/hops/goals all run; the day-0 deaths were food races, not
   freezes). Bought back with hunger 0.011, thirst 0.013 and raids
   0.10→0.08 (hypothermia-ease was tried and reshuffled NEGATIVE — reverted).
Final gate (60d, 12 seeds): **6/12 WIN / 6 LOSS / 0 TIMEOUT (50/50)**.
Soak protocol note: judge on 60d, the 40d cut reports mid-story timeouts.
Death profile: days 2-35, mixed causes (cold/starve/bleed/raids), winners
often 3/3 alive. Ladder rows measured: in-water anchors 1/12; bank anchors
3/12; +cold/raid ease 4/12; +hungry-hurry exemption 5/12; +edge padding
4/12; +raid 0.12 → 6/12. Remaining known gap:
seeds whose girls lose all axes/saws can't run the chain (chopTool 0%
in seed 42's samples) — tool scatter/recovery is a future candidate.

## §48 Status effects — the unified buff/debuff layer (iteration 46)

Everything the world does TO a survivor — the sun burning bare skin, the cold
draining her, a fresh bite bleeding her out, hunger past the emergency line,
stamina spent to the floor, comfort bottoming out — was, until now, a scatter
of bespoke per-tick branches in `SimulationSystems` writing flat fields on
`NPCNeeds`/`NPCState`, legible only as `Trace.Emit` tags. There was no single
place that answered "what conditions is this girl under right now?", and the
panel showed only the raw need meters, never a named buff or debuff.

§48 introduces a **status-effect layer**: one declarative catalog of the
buffs/debuffs a survivor can carry, and one classifier that reads her live
state and reports which are active. The player sees them as a row of circular
icon chips on the selected character, each with a hover tooltip that explains
what it does.

### §48.1 Derived, read-only — balance is untouched
The colony sits on a knife-edge (see §40 / §46 soak history): any change to a
tuned number reshuffles seeds and can tip a run. So the effect layer is
**purely derived** — it is NOT a new stored field and it never mutates
simulation state. `EffectEvaluator` only READS the existing needs/body/
environment fields and classifies them; the per-tick systems that actually
apply the consequences (bleed the blood, drain the HP, slow the walk) are
exactly as before. Adding, removing or retuning an effect changes only which
ICON shows — never an outcome. Migrating the real application logic to route
THROUGH effects (so a system reads its modifier from the effect) is a possible
v2 and is deliberately deferred, because that WOULD move numbers.

The classifier's thresholds mirror the points where the sim's own logic already
bites, so an icon appears exactly when the underlying consequence kicks in:
bleeding when a fresh wound (`Heal01 < 0.3`) sits on a part below `0.4`;
heat/cold when `|ThermalComfort| ≥ 0.85` (the HP-drain line); starving/
dehydrated off the existing hysteresis flags; winded at the `Stamina < 0.15`
floor; sunstroke while `SunExposure > 0.5` (where the sim starts docking
Comfort and burning skin — it caps at 1.0 with an HP burn then resets to 0.5,
so the useful window is the whole over-exposed band, not the momentary spike).

### §48.2 Model (Simulation/Agents/Effects/)
Pure C#, Unity-free (testable, and the sim could later read it):
- **`EffectKind`** — the enum of every effect. Grouped: injury/blood
  (`Bleeding`, `Injured`, `Hobbled`, `Bandaged`), environment (`StrongSun`
  when effective UV ≥ 0.6, `Sunstroke`,
  `Sunburnt`, `Hot`/`Heatstroke`, `Cold`/`Freezing`, `Soaked`, `Cozy` (buff —
  sat by a lit campfire, spec §49.C) — thermal is
  two-tier: mild `Hot`/`Cold` at `|ThermalComfort| ≥ 0.4`, severe HP-draining
  `Heatstroke`/`Freezing` at `≥ 0.85`), survival (`Starving`,
  `Dehydrated`, `Exhausted`, `Fainted`, `WellFed`, `Rested`), mind
  (`Stressed`, `Grieving`, `Lonely`, `Miserable`, `Content`), hygiene
  (`Filthy`). `EffectPolarity` {Buff, Debuff} colours the chip ring;
  `EffectCategory` groups/sorts.
- **`EffectDefinition` + `EffectCatalog`** — the static truth per kind:
  polarity, category, a placeholder **emoji** glyph (v1; see §48.3) and the
  `effect.<kind>.title` / `effect.<kind>.desc` localization keys. No `Color`
  here — the presentation maps polarity → ring colour.
- **`ActiveEffect { Kind, Intensity }`** — one effect acting now; `Intensity`
  (0..1) is how hard it bites (tooltip reads mild/severe, ring brightens).
- **`EffectEvaluator.Collect(npc, tick, effectiveUv, nearLitFire, results)`** —
  the classifier. Bleeding
  takes precedence over the milder `Injured`; a faint suppresses the redundant
  `Exhausted`. `Comfort` reads bipolar (low → `Miserable`, high → `Content`).
  `nearLitFire` (a campfire within warming range, same probe as the temperature
  system) raises the `Cozy` buff.

### §48.3 Icons — emoji first
Each chip is a coloured circle with a glyph inside. v1 uses **emoji**
placeholders held in the catalog (🩸 bleeding, ☀️ sunstroke, 🥶 freezing,
🍽️ starving, 😮‍💨 exhausted, 😊 content, …) — zero assets, instantly
swappable. If the runtime font lacks colour emoji the chip still reads by its
polarity-coloured ring + tooltip. Swapping a kind to a neural-generated image
later is a per-kind catalog edit; the rest of the pipeline is unchanged.

### §48.4 Bridge & UI
`WorldSnapshotExporter.ExportNpc` runs the evaluator and exports
`NpcSnapshot.Effects` as `"Kind\tintensity"` per active effect (same tab-encoded
convention as `Wounds`/`WornDurability`). The character panel (§40.11) renders
one circular chip per entry — buffs ringed green, debuffs red (brighter with
intensity) — and on hover pops a small tooltip with the localized title and the
one-line "what it does". Debuffs sort before buffs, most-intense first.

### §48.5 Not yet modelled
`Fighting` stays a dedicated badge (§40.11), not an effect chip, to avoid
duplication. (`Sick` gained a tracked duration in §49 — see below.)

## §49 Sleep, social & water overhaul (iteration 47)

A batch of quality-of-life mechanics around resting, company and drinking,
requested after watching the colony fidget. All knobs live in the static
`Spec49` class (Simulation) and are surfaced as sliders on `HexTuningConfig`
(applied at startup via `HexTuning.Apply`), so they can be tuned without a
rebuild. Verified on the 12-seed / 15-day headless soak: baseline **4W·1L·7
deaths** → full **10W·0L·3 deaths**, with the "empty get-up" churn cut **47 %**
(1660 → 882) and boiled-water share up from ~5 % to ~8 % (raise
`boilChainWeight` for more).

### §49.1 Sleep re-arm — kill the "empty get-up" churn
Ground sleep was hard-chunked into 100-tick blocks; each block *completed* into
a full stand + wake-grace + re-plan, then Sleep re-won and she lay back down —
57 % of night get-ups did nothing but re-lie. `RunGroundRest` now **re-arms the
block in place** (`ShouldKeepSleeping`) instead of standing: she sleeps the
night in one continuous lie and only truly wakes for a real, actionable need
(hunger/thirst ≥ 0.6) or a threat. Crucially **cold is NOT a wake trigger** — a
near-naked girl on a cold night sits at max thermal discomfort she can't fix, so
waking her only produced churn; the cold HP hit lands whether she's up or lying.

### §49.2 Unified sleep-comfort formula
Comfort no longer drains while asleep, and no longer rides the bed interaction's
`ComfortDelta`. Instead `NeedsDecaySystem` fills it from a single formula over a
night's sleep: **surface** (grass 0.05, leaf-mat 0.30, bed 1.0) **+ fireside**
(0.05, §49.8) **− sun** (0.15, sleeping in open daylight) **− rain** (0.15). A bed in
the rain nets ~0.70; grass nets a token 0.05 so beds are still worth building.
A worn **jacket/coat** (any torso-covering outer garment — the coat, leather or
heavy jacket) bunched under the body adds a small ground-only pad bonus (0.06),
so sleeping rough in outerwear beats bare dirt; a bed supplies its own surface
so the pad does not stack there.

### §49.3 Delayed, visible water sickness (`Sick` becomes real)
Raw-water gut-rot is no longer an instant −0.08 torso lump. A positive roll opens
a **bounded damage budget** (`Mind.SicknessDamageRemaining`, capped) paid down a
little each slow tick, plus a visible window (`Mind.SickUntilTick`) driving the
🤢 `Sick` effect and a comfort malaise. Expected total harm ≈ the old lump; the
**budget cap** is essential — without it, overlapping windows from a thirsty
colony drinking raw back-to-back ground the torso continuously (it wiped a seed
in testing). This is the one effect (§48) backed by a stored field + real DoT.

### §49.4 Social — linger longer, plus a passive "second action"
Talks run longer (`TalkDuration` 40→90) but each sates less (gains 0.40/0.25 →
0.20/0.12), so they visibly stand and chat yet still want another later. The gap
is filled by **ambient socialising**: being near an awake, settled housemate
while doing your own thing trickles a little Social (Sims-style), capped so a
real chat is still wanted. A chat **won't START** once hunger/thirst ≥
`SocializeNeedGate` (0.55) — this decouples the longer talks from dehydration
(a thirsty girl who talks instead of drinking was the soak's dominant new death).

### §49.5 Slow needs while sleeping + a real shade
A sleeping body accrues cold/heat discomfort *and* takes the thermal HP hit at
`ThermalSleepFactor` (0.5×) — the Sims-style "needs slow while asleep" that lets
the re-arm sleep-through be safe. Shade is reworked from a flat −2° cool bonus
(which chilled girls resting in shade on mild days into cold damage) into a
**heat-shield**: it only removes heat *above* the comfy band (~22°), never
chills — so a 35° day reads 28° in shade, an 18° day is unchanged.

### §49.6 Comfort-aware sleep spot + proactive boiling
`BuildGroundSleepPlan` adds a **small** comfort nudge — shade on a hot day,
fireside in the cold — dwarfed by the home-anchor + indoor-safety priority (the
historical dog-country fix), so it only re-orders nearby spots. Balance-neutral
in the soak. And when only *mildly* thirsty (< `BoilThirstCeiling` 0.6) with a
pot + a lightable fire in reach, she now **holds off the raw gamble and sets up
boiled water** (the fire chain gets a `boilChainWeight` push); urgent thirst
still takes raw. `boilChainWeight` trades boiling for stability: 0.1 (default) is
free (10W·0L), 0.2 ≈ 13 % boiled at some cost, 0.3 ≈ 20 %.

### §49.7 Wet clothes drag; being wet costs comfort
`EquipmentMath.WetMovementFactor` (already wired into the walk speed) now only
counts **real garments** (`WearLayer.Wear`/`Outerwear`) as drag — a wet
bra/panties/bikini (`Underwear`) is too light to slow you, so a girl in just
underwear (or naked) keeps full speed even soaked; pants + a vest do drag
(×`wetDragPerGarment` per wet item, floored at `wetDragFloor`). Separately, the
plain fact of being soaked shaves a little comfort each slow tick
(`wetComfortPenalty`) — and **wet underwear counts for that**: it doesn't slow
you, but it's still miserable. Soak-neutral on wipes (10W·0L).

### §49.8 Fireside comfort — the campfire is cosy
A lit campfire is now a genuine **comfort** source, not just heat. Two effects,
both keyed off the same warmth probe the temperature system uses (a burning
campfire within ~2 tiles):
- **Asleep by the fire** tops up ~5 % comfort over a night on its own
  (`SleepComfortFireBonusNight` 0.02 → **0.05**), so bedding down fireside on
  bare grass is meaningfully cosier than open ground.
- **Awake by the fire** reverses the usual waking comfort drain (`ComfortRate`)
  into a small gain (`AwakeFireComfortGain` 0.003/slow tick) — sitting fireside
  slowly *restores* comfort instead of bleeding it, so it's "a touch comfier than
  trudging about".

Both surface as the derived buff chip **`Cozy`** (§48, 🏕️, `effect.cozy.*`),
raised whenever a lit fire is in warming range. Pure additive comfort — no death
class touched; the knobs are `Spec49.SleepComfortFireBonusNight` /
`Spec49.AwakeFireComfortGain`.

## §50 Limb loss — amputation (iteration 48)

A survivor can lose an **arm or a leg** — for good. Two triggers, one hard
consequence, and the severed limb stays in the world.

### §50.1 The "severed" model
`BodyState` gains `HashSet<BodyPart> Severed` (arms/legs only — never
Head/Torso/Pelvis, which keep killing via `VitalDestroyed`). A severed zone is
pinned at 0 HP and **never regenerates**: every HP-return site (bandage/pill
first-aid, slow fed-regen, per-wound heal payback, and the at-cap wound-reopen
donor payback) skips severed zones. A **lost leg** (one or both) makes
`MobilityFactor` return a fixed `CrawlSpeedFactor` (1/3 of walking) — she crawls,
at the pace the crawl animation is authored for. A **lost arm** collapses
`StrikeFactor` below the 0.4 "mauled but present" floor — one arm gone →
×`SeveredLimbMobilityMult` (0.15), both → ×that². The derived §48 chip `Maimed`
reads `Severed` (pure classification, like Hobbled).

### §50.2 Triggers
- **Emergent (bites).** After a dog or shark bite applies its damage and files
  its wound, `AmputateSystemHelpers.TrySeverOnBite` fires iff the bitten limb is
  now at 0 HP AND either the single blow ≥ `LimbSeverThreshold` (the shark's
  0.2, a future weapon — a clean tear-off) OR a deterministic roll <
  `GrindSeverChance` (0.25 — the small dog bite that finally destroys an already-
  mauled leg rips it away). So most zeroed legs still just hobble; occasionally
  one comes off.
- **Prepared place.** `HazardSystem` (slow layer) takes a leg from any survivor
  standing on a tile that holds a `Hazard`-tagged object (`hazard.trap` — a reef/
  bear-trap a map author places), at `HazardSeverChance` (1 = on contact). Like
  the shark, a fixed dangerous spot.

### §50.3 The consequence (hard)
`Sever` dumps `LimbSeverBloodLoss` (0.4) of the Blood need instantly and files a
deep stump wound (`LimbSeverWoundSeverity` 0.35) that §44 clotting keeps bleeding
a while → a likely bleed-out spiral unless she's dressed. It never kills
outright; death, if it comes, is through blood loss over the following ticks. Her
current plan is interrupted (as a dog attack does).

### §50.4 The limb in the world
`Sever` spawns a `body.limb_severed` object at her feet (mirrors the corpse):
`CurrentUser` = whose limb (which actor mesh), `Variant` = which `BodyPart`,
`ResourceAmount` = `SeveredLimbDecayTicks` (4800 ≈ 2 days). It's tagged `Decays`,
so the generalised `CorpseSystem` rots it away on the body clock — but without a
corpse's mourn/bury interactions. Snapshot carries `ObjectSnapshot.Variant` and
`NpcSnapshot.SeveredParts`.

### §50.5 Presentation
The living body is **one skinned mesh on a shared skeleton**, so the limb can't
be deleted as a sub-object. Instead the view collapses the *distal* bone sub-tree
(`lForearmBend`/`rForearmBend` for arms, `lShin`/`rShin` for legs) to ~0 scale —
a below-elbow/below-knee cut that leaves a stub (and drops the sleeve/trouser
riding those bones) without deforming the shoulder/hip. The stump bleeds via the
normal §40.8-D wound paint on the remaining stub (the sim already filed the deep
wound there) — no special stump art (the "prosthetic-hole" look stays rejected,
§40.8-D). A **lost leg** forces `PostureHint = Crawl`, which drives a new
`Crawling` animator bool → a dedicated `Crawl` state playing the imported
`Zombie Crawl` clip (AnyState loop, cleared on death; built by
`BuildNpcActionStates`). Crawl and Limp are now distinct animator states, never
both at once; the old procedural Crawl shoulder-pose is gone. The dropped limb is
the **real geometry**: `SeveredLimbFactory` slices the owner's shared bind-pose
mesh by bone-weight to the distal chain (independent of the runtime collapse)
into a plain `MeshFilter`; when the mesh isn't Read/Write it falls back to a
primitive.

### §50.6 Tuning & balance
All knobs live in `Spec50` (mirrored in `HexTuningConfig`, §50 header): `Enabled`,
`LimbSeverThreshold`, `GrindSeverChance`, `LimbSeverBloodLoss`,
`LimbSeverWoundSeverity`, `SeveredLimbMobilityMult`, `CrawlSpeedFactor`,
`SeveredLimbDecayTicks`, `HazardSeverChance`. Amputation adds a new bleed-out
death channel on top of the
knife-edge dog balance (§46) — soak baseline vs branch and retune before shipping.
Harness must whitelist the `LimbSevered` trace event.

### §50.7 No jumping without legs — terrain goes off-limits
A survivor missing a leg (`BodyState.CanJump` false) can't hop an elevation step
or dive water — the hex-step hop (§21.21B) needs legs. `HexPathfinder.RequiresJump`
marks an edge that changes elevation (the tile stepped onto, `junction.Tiles[0]`,
matching the movement hop-arm rule); `FindPath(…, canJump)` skips those edges for
her, and a **second connectivity graph** (`WorldState.JunctionComponentsFlat`,
built by `Connectivity.RebuildFlat`, selected by the `canJump` arg on
`Reachable`/`ReachableBeside`) treats each elevation shelf and the water as its
own component. `canJump` is threaded from the NPC at the perception reachability
sites (so every goal-availability helper reading `PerceivedObject.IsReachable`
inherits it — she never *plans* a route she can't crawl) and at the path search.
A higher ledge / the water simply becomes unreachable to her, not a failed detour.


## §51 Character inventory — the backpack window (iteration 48)

Spec §51 gives the selected character a readable **inventory**. A "🎒 Backpack"
pill on the identity column of the character panel toggles a floating window
anchored just above the bar (near the portrait, so it reads as *her* things).

### §51.1 Item data — `ItemCatalog`
`Simulation/Content/ItemCatalog.cs` (Unity-free, mirrors `EffectCatalog` §48)
classifies any `ObjectDefinition` into an `ItemCategory` (Weapon, Tool,
Clothing, Armor, Food, Water, Medicine, Resource, Misc) from its tags/layer, so
new content (incl. the imported garments) is covered automatically. `ItemInfo`
carries the category, a placeholder **emoji** glyph (per-id table, else a
category default) and the Loc keys `item.<slug>.name` / `.desc`. Names/descs
fall back to the definition DisplayName + a generic `itemcat.<cat>.desc` when a
hand-authored string is absent. Real icons can replace the emoji per id later.

### §51.2 The window (CharacterPanel)
Two stacked sub-views: a **list** (Worn + Carried sections, each row = emoji
tile + localized name + category tag, fed by the snapshot's WornItems /
InventoryItems) and a **detail** item view (big glyph, name, category,
description, and derived stat rows — layer, covered zones, warmth ≈ delta×10°C,
armor percent, hunger restore, plus the worn instance's live durability/wetness).
Clicking a row opens the detail; a "‹ Back" returns to the list. The window's
bounds feed `NpcSelection.PointerOverUi`, so clicking an item never deselects the
NPC. Fully RU/EN localized; the window resets on (re)selection and on collapse.
Icons are emoji placeholders for v1 (swappable per id later).

## §52 Slot inventory, garment containers & build-sites (iteration 52)

A survival overhaul in five interlocking parts: the pack is now made of the
clothes you wear and the hands you have; clothing is a container; items have
importance; the bottle is refillable; and furniture is raised at build-sites
with a hammer. Survival pressure is eased to make room for the new busywork.

### §52.1 Slot inventory — you carry what you wear

The pack has **no capacity of its own**. It is:

```
Inventory.Capacity = IntactHands + Σ (worn garment.Capacity)
```

- **Hands** — `BodyState.IntactHands` = 2 minus severed arms (§50). Naked, that
  is the *only* capacity (2, or 1/0 for an amputee). Hands are real slots and
  the pair a two-handed weapon needs (§52.5).
- **Pockets** — every worn garment grants slots via `GarmentParams.Capacity`
  (new field, mirrored on `ObjectDefinition.InventoryCapacity` and the Unity
  `GarmentDefinition` asset). Iron rule: **panties/bra 1, top 2, pants 4,
  jacket/coat & heavy vest 6, dress 4, skirt/shorts 2, leather armor 4**;
  accessories (gloves, stockings, jewelry, boots) carry 0.
- Capacity is recomputed in `EquipmentMath.RecalculateCapacity` on every worn
  change (dress/undress/destroy/sever) and at bootstrap. The knob `SimBalance.HandSlots`
  (default 2) caps the hand contribution.
- **Personal effects** ride free of the pocket budget: the bottle (§29H) hangs
  on the body, so `InventoryState.UsedSlots`/`HasSpace` skip it — a naked girl
  still has her bottle and both hands. (`InventoryState.IsPersonalEffect`.)

A dressed girl (underwear 1 + top 2 + pants 4 + 2 hands = 9) sits near the old
flat 10; a near-naked castaway is genuinely constrained until she finds clothes
— the intended early arc.

### §52.2 Garments are containers

A worn garment's pocketed items live in the shared pack; when the garment
**leaves the body** they ride with it:

- **Undress** (`DropGarmentWithContents`) — as the piece comes off, whatever no
  longer fits the (now-smaller) pack spills *into the removed garment's*
  `WorldObjectState.Contents` (lowest-importance first). The girl remembers
  (perception) that her stone is "in those panties over there."
- **Re-dress** — donning a garment pours its `Contents` back into the pack
  (capacity just grew); any overflow drops at her feet (`StashRecovered`).
- **Destroyed clothing does NOT destroy its contents** — when a garment frays to
  rags (`DestroyWornItems`), the lost slots spill the overflow onto the ground
  (`SpillOverflow`): the bottle in the ripped panties simply falls out.
- A lost arm shrinks the pack too — `Sever` recomputes capacity and spills.

`Contents` doubles as a build-site's delivered-materials bin (§52.4) and a
fireside stockpile (§52.3).

### §52.3 Item importance & the fireside stockpile

Every item has an importance (`ItemCatalog.Importance`, by `ItemCategory`):
**Water 100, Food 95**, Medicine 80, Weapon 65, Tool 60, Armor 45, Clothing 40,
Resource 20, Misc 10. It drives *what to drop first*: overflow, combat load-drop
and haul-to-fire always shed the least-wanted item (`InventoryMath`).

**HaulToFire goal** — a full pack, in peace, sends the girl to set her lowest-value
item (importance ≤ 25) down at the hearth (`StashedAtFire`) to free a slot; it
waits there to be reclaimed by normal pickup. **Life-threatening pressure cancels
it**: a fight, fresh danger memory, Health < 0.4, or Hunger/Thirst ≥ 0.6 — you
don't tidy your pockets while something is trying to eat you. Score 0.28 (below
every need); rare under the eased-needs balance, a relief valve for real pressure.

### §52.4 Build-sites & the hammer

Placed furniture is no longer woven in one shot at the campfire. It is raised at
a **build-site** — an intent point every NPC knows from the start:

- `build.site` object carries a per-instance `BuildProduct` + material bill
  (`BillLogs/Stones/Leaves`); delivered materials accumulate in `Contents`.
  The communal **bed** is staked next to the hearth at bootstrap
  (`CreateBedSite`, bill = 2 stones) and seeded into every NPC's memory.
- **DeliverToSite / BuildFurniture** (`GoalType.BuildFurniture`, one goal): the
  gather chains (GatherStone/GatherWood) fire on the site's outstanding needs;
  a girl carrying a needed material walks to the site and deposits it
  (`SiteDelivered`, partial delivery is fine — many trips, many girls).
- **Raising needs a hammer.** Once stocked, a builder completes it if she
  carries a `tool.hammer` **or one is lying at the workbench** (the site tile or
  a neighbour) — the product spawns at the site and the site despawns
  (`FurnitureBuilt`). Two hammers spawn by the hearth at bootstrap; the hammer
  is a multi-use Tool (never consumed), grabbed via GatherTools.
- The **hut** (§35.3) is folded into the same discipline: raising a hut piece
  now also requires a hammer in hand.
- Build work is **peacetime** (like the hut/raft): it pauses under hunger/thirst
  ≥ 0.55 or fresh danger. Building is a slow surplus activity — the bed, like the
  hut, completes only when the colony has spare hands and stone, by design.
- Scope note: **bed + hut** run the build-site model in v1; tent & drying-rack
  keep their campfire craft for now (identical machinery — one table row each to
  migrate). The old `CraftBed` path is retired.

### §52.5 The hands — two-handed weapons & combat

- **Two-handed spear/bow** — wielding needs both hands: `Hunt` (and any spear
  strike) requires `IntactHands >= 2`. A one-armed survivor (§50) can no longer
  hunt.
- **Ready the weapon in a fight** — at the first dog strike, a spear-armed girl
  with two hands **drops her bulky load** (firewood/stones/leaves) to free her
  hands (`SpearReadied`) — "quick, throw down the wood and grab the spear." The
  dropped resources are recoverable after the fight.
- **The thrust bites** — a readied two-handed spear multiplies strike-back by
  `SimBalance.SpearStrikeBonus` (1.8), turning the reactive swat into a real
  jab. The bare hands / one-armed keep the plain strike.
- **Prefer empty hands** — carried tools sit stowed in pockets by default and
  are only "in hand" while actually in use; the renderer shows the active
  item and empties the hands when the job is done (presentation).

### §52.6 Eased survival (making room for the busywork)

The pack/build layer adds work, so the old food/cold panic is softened
(SimBalance + `HexTuningConfig` defaults):

| Knob | Was | Now | Effect |
|---|---|---|---|
| `HungerRate` | 0.011 | **0.0055** | eat ~half as often |
| `ThirstRate` | 0.013 | **0.010** | gentler thirst |
| `ColdPressureSlope` | 0.03 | **0.02** | less cold-anxious |
| `DressThermalThreshold` | 0.35 | **0.45** | fewer fussy wardrobe stops |
| `BottleCapacity` | 1 (implicit) | **3** | one fill = several gulps |

**Refillable bottle** — filling charges `BottleCharges = SimBalance.BottleCapacity`;
each `DrinkBottle` spends one; the bottle only empties (and warrants a refill trip)
when the last charge is gone. Far fewer water runs.

**Soak** (6 seeds, 10 game days): 6/6 green — all survive, structure/rhythm/QoL
intact. Garment-stash and spear-ready fire every seed; the bed-site delivers on
every seed and raises when surplus allows (like the hut). Deferred: dedicated
weapon slot; dynamic (non-bootstrap) site placement; tent/rack migration.

## §53 Compassion & mutual aid (iteration 49)

The survival stack now bites hard — a survivor can **starve**, **bleed**, fall
**sick** (§49) and even **lose a limb** (§50) — yet everyone was an island:
feeding, first-aid and medicine were all self-only, so a legless girl who
couldn't crawl to food, or one bleeding out beside a healthy housemate, got no
help. §53 makes the colony **care for each other**: a girl who is herself okay
walks over to the worst-off housemate and helps — feeds, dresses a wound, hands
a pill, or sits with the grieving — which bonds them both. All knobs live in the
static `Spec53` block and the `HexTuningConfig` "Сострадание (§53)" sliders, so
the headless harness can bisect it and it can be soaked without tipping a seed
(`Spec53.Enabled = false` restores pre-§53 behaviour byte-for-byte).

### §53.1 The need & the trait — сострадание
Two coupled quantities. **`NPCNeeds.Compassion`** (0..1, 1 = at peace) is the UI
bar, mirroring the Social convention (high = good). **`NPCState.CompassionTrait`**
(0..1) is the personality weight, seeded once at spawn in `[TraitMin, TraitMax]`
(deterministic on the world seed + npc id) and fixed for life. The trait scales
both how fast the need drains and the strength of the aid drive — so one girl
drops a bed build to tend a wounded housemate while another only helps when idle.

### §53.2 Decay & restore
Each slow tick, `Compassion` is **spent** by witnessing un-helped suffering
nearby: `−= CompassionRate × worstNearbySuffering × CompassionTrait` (the worse,
and the more caring the witness, the faster it drains). When no reachable
neighbour is suffering above `SufferingThreshold` it **recovers** by
`RecoverRate` toward full. Completing an aid restores `AidSelfRestore` directly.

### §53.3 Reading a neighbour's plight
The perception build tags each `PerceivedAgent` with a `Suffering` (0..1) and the
single most-urgent **helpable** `AidKind`, in urgency order: **Treat** (open
wounds / blood loss — a bleed-out clock), **Medicate** (sick, or gravely weak
with nothing to dress), **Feed** (genuinely hungry), **Console** (grieving or
breaking under stress). A helper re-assesses on arrival — she may have recovered,
worsened, or died on the way.

### §53.4 The aid behaviour
A new `GoalType.Aid` bids into the same utility auction, cloning the talk
pipeline: pick the **worst-off** reachable, unclaimed sufferer, reserve an
arm's-length approach junction, claim her with `PendingAidFrom` (she holds still
until arrival, timeout, or danger — hunger does **not** break her wait, she needs
the help), then `MoveToJunction` + an aid interaction (`FeedOther` / `TreatOther`
/ `MedicateOther` / `ConsoleOther`). On completion the **relief is applied
straight to the target** — no food or bandage is spent, so aid can never bankrupt
the knife-edge colony: Feed drops her Hunger, Treat lifts wounded parts + stops
the bleed + drops a gauze wrap, Medicate lifts Health and clears the sickness
window, Console eases Stress and shortens mourning. **Both** relationships rise by
`AidRelationshipGain` (larger than a chat's 0.05) across Affinity/Familiarity/
Trust, and the Sims-style "+/-" pop floats over both heads.

### §53.5 Weights & the self-survival gate
`aidBid = base + suffering × CompassionTrait × AidWeight + (1 − Compassion) ×
PressureWeight`. At `AidWeight = 0.85` a high-trait girl (≈1.0) facing a dying
housemate (suffering ≈ 1) bids ≈0.95 — over `CraftBed` (0.7) and the 0.15 switch
margin — while a reserved girl (≈0.35) bids ≈0.4 and helps only when otherwise
idle. Aid is **unavailable** while the helper is in her own crisis: starving,
dehydrated, `Hunger ≥ SelfHungerGate`, `Health`/`Blood` `< SelfHealthGate`,
fighting, or fleeing. Her own `StarvingBoost` (1.0) still outranks aid — a girl
dying of hunger looks after herself first.

### §53.6 Presentation / UI
A **Compassion** bar (rose, ❤ glyph) joins the CharacterPanel need rows (RU
«Сострадание»), fed by `NpcSnapshot.Compassion`. Aid emits `AidStarted` / `Aided`
traces and reuses the existing relationship-pop over both heads.

## §54 Stranded-Deep resource, processing & butchering loop (iteration 54)

Ported the Stranded-Deep gathering/crafting feel: things you chop and kill land
on the map as physical items you then process, and everything is gathered/built
from scratch (cold start). Two data-driven seams keep it cheap to extend.

### §54.1 Data seams
- **`HarvestDrop` / `Yields`** on `InteractionDefinition` (`Definitions.cs`): a
  harvest/process/butcher verb declares its output as data (`{id, count,
  scatter}`). `ExecutionSystem.ApplyHarvestYields` reads it and, for `Scatter`
  drops, spawns each item at a **distinct free junction** around the source
  (`FindScatterSpot`) — logs/leaves/sticks/meat land *around* the spot, not in a
  pocket. One handler serves palm→logs, log→sticks, carcass→meat+hide.
- **`RecipeCatalog`** (`Content/RecipeCatalog.cs`): the crafting ingredient bill
  as a `GoalType→Recipe` table (inputs, `NeedsLitFire`, `RequiresNoRack`). The
  craft-start gate (`CraftGateOk`) and effect (`ConsumeRecipeInputs`) both read
  it; outputs stay per-goal in the effect switch. Retires the duplicated
  ingredient literals that used to live in two parallel switches.

### §54.2 The wood chain (firewood retired → log + stick)
`resource.firewood` is gone, split into two materials (both tagged `Wood`, so one
`GatherWood` goal collects either):
- **`resource.log`** — the chop output. Builds/raft/premium-bed frame from logs.
  A palm drops **3 logs + 3 palm leaves**; a big tree **4 logs**; a boulder **4
  stones** — all scattered on the ground.
- **`resource.stick`** — the fuel/craft currency. Fire, axe, pickaxe, spear,
  arrows, rack, knife all cost sticks.
- **`SplitLog`** (`InteractionType.Process`): chop a **ground log** into
  `SimBalance.LogSplitYield` (4) sticks with an axe/saw — the sticks scatter.
  `forest.deadfall` sheds ready sticks (the early bootstrap shortcut).

### §54.3 Cordage & the knife
- **`plant.fibrous`** sheds `resource.fiber` (herb-bush pattern). Fiber crafts
  **`resource.rope`** (3 fiber) and **`resource.cloth`** (4 fiber) at the fire.
  Rope is now the bow's lashing; cloth the tent's panel.
- **`tool.knife`** (1 stick + 1 stone at the fire) — required to butcher. One is
  also findable in the wild to bootstrap.

### §54.4 Carcasses, butchering & cannibalism
- Every animal death (dog / rabbit-"crab") leaves a **`carcass.animal`** on the
  map (`SpawnCarcass`, `Decays` tag, rots on the `CorpseSystem` clock). Hunting
  no longer teleports loot into the pack.
- **`Butcher`** (`InteractionType.Butcher`, needs `tool.knife`) → `meat_raw` +
  `hide` scatter on the ground. Hunger-driven.
- A housemate's **`corpse.npc`** is butcherable too (cannibalism) — gated in
  `IsValidTargetFor` behind `Hunger ≥ SimBalance.CannibalizeHungerGate`, and it
  costs `CannibalismComfortPenalty` comfort (`CannibalismEnabled` toggle).

### §54.5 Meat spoilage
`MeatSpoilageSystem` (Slow layer, after `CorpseSystem`): ground `meat_raw` rots
after `MeatRawSpoilTicks`, `meat_cooked` after the longer `MeatCookedSpoilTicks`
(from its `SpawnTick`) — cooking is preservation. Carried meat is out of scope
for v1.

### §54.6 Cold start
Nothing is pre-built or handed out at home (`PrototypeWorldDefinitionFactory`
keeps only palms + deadfall). The starting **spear is retired** (crafted like any
tool); only the water bottle stays in hand. Lighter, pot and armor become
findable wilderness loot. The **hearth** stands at the generator-chosen yard spot
as an **unlit `campfire.spot`** (a cold pit) — the colony gathers sticks and
lights it themselves; the raft stays a coastal build-marker. Soak: the colony
reliably lights the fire from scratch and runs the whole chain (axe/knife/spear
crafted, trees felled, animals butchered), 6/6 seeds.

(A build-from-loose-stones campfire-site was prototyped — `build.site` with
`BuildProduct="campfire.spot"`, no hammer — but the cold/hungry start didn't
reliably prioritise hauling the stones, so the simple cold pit ships instead. The
dormant campfire-site scaffolding + `CampfireStoneBill` knob remain for a future
staged-build pass.)

### §54.7 Presentation
Procedural low-poly models (`LowPolyToolFactory`) for log, stick, fiber, rope,
cloth, knife and the animal carcass; `Process`→chop and `Butcher`→work actor
poses with the right tool in hand. The campfire is ringed with real low-poly
**stones** (`AddCampfireStoneRing`). Build-sites render their **delivered
materials piled up** (`BuildSitePile`, fed by new `ObjectSnapshot`
`BuildProduct`/`Bill*`/`Delivered*` fields) so a piece assembles from its
components. Felling a tree **tips the trunk over and leaves a stump** (`TreeFall`)
in sync with the logs hitting the ground.

### §54.8 Tuning
All knobs live in `SimBalance` (§54 block): `LogSplitYield/DurationTicks`,
`FiberPerPlant`, `RopeFiberCost`, `ClothFiberCost`, `KnifeStick/StoneCost`,
`CarcassDecayTicks`, `ButcherDurationTicks`, `CarcassMeatYield`,
`MeatRaw/CookedSpoilTicks`, `Cannibalism*`, `CampfireStoneBill`. Balance is tuned
separately — the §54 values are functional placeholders.

## §55 Rivers retired, drink from the coconut (iteration 55)

The drink mechanic is being reworked from scratch. As a first demolition
step, the old river/sea/bottle water chain is torn out and thirst is served
by cracking a coconut, Stranded-Deep style. (This is deliberately a WIP — the
full new drink logic lands in a later pass.)

### §55.1 Rivers become sea

There is no river/sea terrain type — a river was just `Water | Walkable`
(waded), sea is `Water` without `Walkable` (swum). Rivers are retired: the
seeded winding channel (`PrototypeWorldDefinitionFactory.AddSeaChannel`, was
`AddRiver`) is now carved as **unwalkable deep sea**, flush with the ocean at
elevation 0. NPCs swim it instead of wading; the one-deep swim ring
(`OpenSwimRing`) still opens the ≤2-wide channel, so it never boxes anyone in.
Sea and "water" are now the same thing. The `water.river` / `water.pond`
drink-anchors are no longer placed.

### §55.2 Water is undrinkable

Neither sea nor the former rivers can be drunk from, and boiling is retired
too. The bottle chain (`GetWater` fill at a `RawWater` bank or campfire+pot,
`Drink` from the bottle) no longer has any source — `RunDrinkBottle` /
`FillBottle` become dead code, `BottleWater` stays `None`. Raw-water sickness
is gone with it (coconut water is clean).

### §55.3 Drink from a coconut (crack → drink → eat)

The coconut is the only water source now. A **whole `food.coconut`** carries a
**`Drink`** interaction ("crack open & drink", 16 ticks, Thirst
−`SimBalance.CoconutThirst` = 0.7, Comfort +0.05) whose **Yields** transform it
into **`food.coconut_open`** — the cracked husk, dropped straight into the hand.
The opened coconut keeps the old **`Eat`** (Hunger −`CoconutHunger` = 0.6), so
one coconut = a drink *then* a meal. A whole coconut is still directly Eat-able
(the water is just wasted) so hunger never blocks on cracking first.

Goals reuse the existing two-step shape:
- **GetWater** (score = Thirst): fetch a coconut to drink — available when
  Thirst ≥ 0.35, no drinkable in hand, space free, and a `Coconut`-tagged item
  is reachable. Maps to `PickUp` (was `FillBottle`); `IsValidTargetFor` accepts
  any item with a `Drink` interaction.
- **Drink** (score = Thirst): crack a carried whole coconut in place — a
  `ConsumeInventoryItem` step with the `Drink` verb. `RunConsumeInventoryItem`
  is now verb-aware (Eat *or* Drink) and applies a completed interaction's
  `Yields` into the inventory (the husk).

Knobs: `SimBalance.CoconutThirst`. The palm still produces `food.coconut`
(`tree.palm`, interval 300, cap 2) — coconuts now feed both hunger and thirst,
so palm demand roughly doubles; watch coconut supply when the drink mechanic
is reworked.

Soak (6 seeds, 3 days): rivers gone (`walkableWater=0`, 0 river anchors),
0 bottle drinks, 7–16 coconut cracks/seed, **0 dehydration**. Residual deaths
are the pre-existing dog knife-edge (§40 / dog-fragility), not water.