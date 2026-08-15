# §120 architecture elements — state and open work

Written 2026-08-15. Read `SKILL.md` first; this file is only what is DONE, what
is OPEN, and where the next agent should start.

## Where everything lives

| Thing | Path |
|---|---|
| Geometry (primitives) | `Tools/blender/arch_elements_lib.py` |
| Build every element | `Tools/blender/build_arch_elements.py` |
| Export one FBX per id | `Tools/blender/export_arch_elements.py` |
| Models | `Assets/Resources/HexLive/Objects/architecture.*.fbx`, `furniture.hearth.fbx` |
| Visual factory | `UnityPresentation/Environment/BlueprintArchitectureFactory.cs` |
| Constructor preview | `UnityPresentation/HutTest/BlueprintEditor/BlueprintPreviewRenderer.cs` |
| Footprints | `Simulation/Runtime/Blueprints/BlueprintFurnitureFootprints.cs` |
| Validation | `Simulation/Runtime/Blueprints/BlueprintValidator.cs` |

Rebuild and export from the open kit:

```python
exec(open("Tools/blender/build_arch_elements.py").read())
exec(open("Tools/blender/export_arch_elements.py").read())
```

## Done and measured

⭐ **Re-measure these after every kit rebuild, out of the FBX, never off this
table.** The rope column was wrong for months (it said 1 everywhere, because a
lashing was once one joined object) and the roof row went stale the moment the
flat panels became real palm fronds. A module billed for fewer units than the
model has can never reveal its last pieces, yet still reports Complete. Counted
2026-08-15 by parsing the FBX `Models` + `Connections`, skipping `_deco_`
hardware and the transparent `HL_Door_Pivot` container:

| Element | Stage units (sticks / boards-stones-leaves / rope) |
|---|---|
| `architecture.wall.wood` | 4 / 3 / **4** |
| `architecture.window.wood` | 4 / 3 / **4** |
| `architecture.door.wood` | 5 / 2 / **4** |
| `architecture.support.wood` | 2 / 0 / **3** |
| `architecture.floor.board` | 2 / 3 / **3** |
| `architecture.roof.palm` | **3 / 27 / 2** |
| `architecture.roof.palm.flat` | **3 / 27 / 2** |
| `furniture.hearth` | 8 / 12 / 1 (not re-measured) |

The player's committed plan therefore bills **161 sticks, 101 boards, 167 rope,
270 leaves** — 699 hauled items, of which the roof thatch alone is 270. That is
a balance decision the player has to see, not a number to quietly shrink.

Numbers that were verified, not eyeballed:

- Face winding: 0 of 6136 faces inverted (was 4842, 78.9%).
- Floor/roof sector orientation error: 1.7° (was 179.6°).
- Hearth ring: 0 open joints of 12; extent 0.334 wu against the 0.375 lattice.
- Floor frame under the deck: beams 0.0461/0.0457, lashings 0.0441, deck
  underside 0.0465.
- Roof tiling: outer eave 0.000, centre peak +0.133…+0.178, eave height 2.271.
- Wardrobe footprint vs model: 0.0° (was 60°). Bed flush to wall: 0.0000.
- Corner supports: pair within 6–25° of radial (was unrotated/random).
- Roof support rule: a sector needs BOTH corners of its own outer edge (was
  three of the parent hex's six corners). On the original six posts that allows
  8 sectors of 10 instead of 6, and the two refusals are honest — one edge post
  really is missing, and the message names it.
- `RoofHex` roofs a whole hex in one all-or-nothing transaction; repeating it
  changes nothing. Nothing stands at the hex centre: six panels meet there.
- Furniture overlay draws the r=4 boundary ring as well as the r=3 interior set,
  deduplicated by JunctionKey: 169 points on a three-hex room versus 111.
- Seam posts: on the player's own saved draft (24 sections, 7 supports) the ship-
  ping build left 5 bare joints, 6 doubled ones and 20 rope rings hanging in mid
  air. After `BlueprintGeometry.AssignSeamPosts`: 25 joints, exactly one post
  pair on each, zero orphan rings, and the door keeps its authored hinge. Two
  separate defects, both measured, not guessed:
  * the pair stands on the model's local −Z (authored +Y, the 0.5 wu seam) which
    is the segment's **A**, while the drop test asked about **B** — and
    `BuildSegmentKey` SORTS its two nodes, so no fixed end can ever be right
    around a ring;
  * the lashings were matched by `_rope`, but the export names them
    `WALL_bind_0..3` / `WIN_bind_*` / `DOOR_bind_*`, so nothing was ever deleted.
- Furniture now needs floor under every occupied junction even on an empty
  blueprint, and the furniture overlay only dots junctions that sit on a built
  floor sector. The old `floors.Length > 0` escape let a bed be dropped on the
  grass beside the hut, with green dots inviting it.

## Open

0. **Rain falls through the roof.** Measured: the scene has 25 colliders and
   NONE on tiles or roofs — `HexWorldRenderer.UpdateRain` kills drops with a
   single infinite `Planes` collider at ground level, so nothing can stop them
   higher up. The fix is a change of collision scheme, not a one-liner:
   add a `RainBlocker` layer (only layer 8 `SmallProps` is currently defined),
   switch the system to `collision.type = World` with `collidesWith` = that
   layer, give each finished roof sector a collider on it, and replace the
   ground plane with one large thin ground box collider on the same layer so
   the splash sub-emitter still fires. Verify with rain on (`R` in HutTest)
   from inside a finished hut.

1. **Room stretching does not fill floor or roof.** Dragging a room leaves one
   sector instead of the full set. Start in `BlueprintEditorCommands`
   (`CreateRoom` / `ResizeRoom`): check whether a `FloorSector` AND a
   `RoofSector` element is added for every sector the room gains, and whether
   `ResizeRoom` removes them symmetrically.
2. **Roof refused where three supports exist.** The rule is
   `BlueprintValidator.ValidateRoofs`: any 3 of the 6 corners of THAT hex must
   carry a Support element, and corner nodes are shared between hexes so one
   support counts for both. Do not guess which rule fires — print the
   `BlueprintValidationResult` messages for the rejected candidate first.
3. **Roof art.** The player wants it thicker (it currently shows through),
   overhanging past the walls, and thatched with the palm fronds instead of flat
   panels. Geometry lives in the `HL_ARCH_ROOF` block of
   `build_arch_elements.py`; keep the tiling rule (slope inside the hex).
4. **Production hut still uses the old monolith — the bridge is HALF BUILT.**
   The player wants his own constructor draft to be raised by colonists, not the
   canonical hut. What is measured and true:
   * A build-site's whole geometry already lives per element
     (`ArchitectureElementState.LocalX/LocalZ/LocalYaw` + the four Required*
     counts) and is already serialised and wired. Only the GENERATOR of that
     list is hard-coded: `BuildingRules.HutDefinitions()`, twelve bays, windows
     nailed to bays 2/3/10/11.
   * **DONE:** `BlueprintBuildingPlan` (Simulation/Runtime/Blueprints) converts a
     `BuildingBlueprintDraft` into exactly that module list. Measured on the
     player's saved draft: anchor hex (0,0), footprint {(-1,1),(0,0),(0,1)},
     51 modules (7 support / 10 floor / 16 wall / 7 window / 1 door / 10 roof),
     bill **151 sticks, 101 boards, 51 rope, 30 leaves**, 51/51 unique slot keys,
     deterministic. It reuses `AssignSeamPosts`, so a section's post stands on
     the same seam in the world as in the preview.
   * **LEFT:**
     a. `BuildingRules` must take the module list from the SITE instead of always
        calling `HutDefinitions()` — `ResolveHutElements`, `SyncHutElements` and
        `RefreshHutElementGeometry` all re-derive it globally. The site's own
        elements already carry it; read those.
     b. Stake the site: `BuildingBootstrap.CreateHutSite` (which has **zero
        callers** today) needs a plan-aware sibling that sets `Bill*` from
        `BlueprintBuildingPlan.Bill` and spawns the modules.
     c. Multi-hex footprint: `CompleteHut` sets `HasFloor|Indoor` on ONE tile and
        derives one portal edge. A three-hex plan needs `Footprint(draft)`.
     d. Presentation draws nothing from `ArchitectureElements` at all — grep is
        empty. `HutAssembly` renders the monolith FBX. The renderer for a plan is
        `BlueprintArchitectureFactory` plus `ApplyStageProgress`, which is
        written, correct, and has **no callers** — a fair sign the previous
        session was walking to exactly this bridge and stopped.
   * Committing the player's draft: it lives at
     `~/Library/Application Support/JuicyLove/HexLive/HexLive/BlueprintDrafts/hut_constructor_autosave.json`.
     The old promotion path (`scripts/extract_latest_hut_layout.py`, scraping
     `[HutDesigner][SAVED]` out of `Editor.log`) is FURNITURE-ONLY and stale —
     the constructor writes proper JSON now. Freeze the JSON as a committed
     asset instead of scraping a log.

5. **`BuildHutTest` scene.** `Assets/HexLive/UnityPresentation/BuildHutTest/BuildHutTestWorld.cs`
   builds the sandbox world: hex flower (radius 1 play area, radius 2 padding —
   `BlockEdgeJunctions` seals a bare flower's own rim), three colonists, an
   unbuilt hearth via `FactionHomeBootstrap.StakeCampfireSite`, and every
   material scattered from ring 1 outwards so nothing lands inside the house.
   The scene itself is not authored yet. The recipe, measured: the real game is
   assembled at runtime by `PrototypeRuntimeBootstrap` in ANY scene and switches
   itself off when a `SimulationRunnerBehaviour` already exists — which is what
   every existing test scene does, and why none of them has the game's menus or
   cameras. So the scene must be a copy of `Main.unity` with ONLY a marker that
   overrides the world definition and creates no runner. `PrototypeWorldDefinitionFactory.Create`
   has no override hook yet; add one. Watch `LoadingScreen` — it enables autosave
   and will overwrite `hexlive_save.dat`.
   Also true and worth not rediscovering: `tool.bottle` is `MaxCarriedInstances = 1`
   and the water lives on the NPC (`NpcState.BottleWater`), so a heap of bottles
   is NOT a water supply — a `water.pond` is. Meat placed through `ObjectBootstrap`
   never spoils, because `AddObject` leaves `SpawnTick = 0` and
   `MeatSpoilageSystem` skips those.
5. **`SimData/simdata.json` was not re-exported.** The hut hearth bill is `const`
   so `BalanceKnobHygieneGate` stays green, but if any of those numbers becomes a
   `public static` knob it must be exported through
   **HexLive ▸ Export Sim Data (JSON)** first.

## Habits that paid off here

- Measure before diagnosing, and measure in the object's own space. Two separate
  wrong diagnoses this session came from comparing coordinates across spaces.
- Reproduce the complaint as a NUMBER (open joints, degrees of error, faces
  inverted). Every fix above was confirmed by the number moving.
- Keep the Unity MCP lease (`Tools/unity_mcp_lease.py`) and release it before
  waiting on the player.
