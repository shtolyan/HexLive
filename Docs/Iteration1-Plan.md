# Iteration 1 Plan

## Core Constraint

The simulation core is a standalone C# domain layer.

Mandatory rules:
- All simulation logic lives outside Unity-specific code.
- The core must compile in a separate assembly definition.
- The core must not reference `UnityEngine`, `UnityEditor`, or any Unity package API.
- The core must use plain C# types and project-owned math/value types where needed.
- Unity is only a presentation, input, and debug host around the simulation.

Planned assembly split:
- `HexLive.Simulation`
- `HexLive.UnityPresentation`
- `HexLive.UnityDebug`

Dependency rules:
- `HexLive.Simulation` depends on nothing Unity-specific.
- `HexLive.UnityPresentation` can depend on `HexLive.Simulation` and Unity assemblies.
- `HexLive.UnityDebug` can depend on `HexLive.Simulation`, `HexLive.UnityPresentation`, and Unity assemblies.

## First Iteration Target

Build the first vertical slice on the final architecture direction:
- 1 fragment
- 16 hex tiles
- 13 points per hex
- 1 NPC
- objects: food, chair, bed, clothing
- global temperature in the fragment
- tick-driven simulation with pause, step, and speed control
- Unity renders the state and exposes debug controls

## Architecture Layers

### Simulation

Scope:
- World state
- Tick loop
- Spatial model
- Pathfinding
- Movement execution
- Perception
- Decision
- Planning
- Execution
- Temperature
- Clothing model
- Reservation and occupancy
- Events and commands

Constraints:
- No MonoBehaviours
- No ScriptableObjects
- No Unity math types
- No Unity serialization dependency

### Unity Presentation

Scope:
- Scene bootstrap
- Hex mesh generation
- Point markers
- Capsule NPC view
- Primitive object views
- Snapshot polling / binding
- Tick controls UI
- Debug overlays and inspector panel

### Bootstrap Data

Scope:
- Static world JSON
- Static content JSON
- Deterministic startup data

## Implementation Order

1. Create folder layout and assembly definitions with strict dependency boundaries.
2. Create simulation model types and stub systems for the full spec surface.
3. Create deterministic JSON bootstrap for the prototype world.
4. Implement 16-tile hex fragment generation and explicit adjacency.
5. Implement 13-point layout inside each hex tile.
6. Implement occupancy, reservation, and spatial queries.
7. Implement tick runtime with pause, step, and speed scaling.
8. Implement pathfinding and tick-based movement execution.
9. Implement content definitions and interaction execution for food, chair, bed, and clothing.
10. Implement perception, utility decision, and direct planner.
11. Implement debug trace and debug panel.
12. Implement Unity rendering and snapshot binding.
13. Validate the full loop end to end.

## Point Layout Decision

For v1, each hex tile uses 13 fixed points:
- 1 center point
- 6 inner ring points
- 6 outer ring points

Rules:
- same deterministic layout for every tile
- points are evenly distributed
- points can be visualized in Unity for debugging
- objects bind to points, not arbitrary transforms

## World Slice Decision

Prototype world for iteration 1:
- 16 tiles
- some walkable, some blocked
- at least one route requiring obstacle avoidance
- enough free points for multi-object tiles

## Debug Decision

Debug panel is part of the first iteration, not postponed.

Minimum debug outputs:
- current tick
- simulation speed
- pause / step controls
- selected NPC state
- needs and temperature state
- current goal
- current plan
- movement state
- current interaction
- recent trace events

## Success Criteria

Iteration 1 is complete when:
- simulation runs from a JSON-defined world
- Unity only displays and controls the simulation
- NPC can select a goal, build a plan, navigate around blocked tiles, reserve a point, use an object, and update needs over ticks
- tick speed can be changed at runtime
- debug panel explains the active state and recent decisions
