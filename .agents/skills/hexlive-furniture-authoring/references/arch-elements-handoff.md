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

| Element | Stage units (sticks / boards-stones-leaves / rope) |
|---|---|
| `architecture.wall.wood` | 4 / 3 / 1 |
| `architecture.window.wood` | 4 / 3 / 1 |
| `architecture.door.wood` | 5 / 2 / 1 |
| `architecture.support.wood` | 2 / 0 / 1 |
| `architecture.floor.board` | 2 / 3 / 1 |
| `architecture.roof.palm` | 2 / 3 / 1 |
| `furniture.hearth` | 8 / 12 / 1 |

Numbers that were verified, not eyeballed:

- Face winding: 0 of 6136 faces inverted (was 4842, 78.9%).
- Floor/roof sector orientation error: 1.7° (was 179.6°).
- Hearth ring: 0 open joints of 12; extent 0.334 wu against the 0.375 lattice.
- Floor frame under the deck: beams 0.0461/0.0457, lashings 0.0441, deck
  underside 0.0465.
- Roof tiling: outer eave 0.000, centre peak +0.133…+0.178, eave height 2.271.
- Wardrobe footprint vs model: 0.0° (was 60°). Bed flush to wall: 0.0000.
- Corner supports: pair within 6–25° of radial (was unrotated/random).

## Open

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
4. **Production hut still uses the old monolith.** The game scene renders
   `building.hut_1hex.fbx` through `HutAssembly` with the 34/31/12/4 bill. Moving
   it onto these six elements and recomputing the bill is a separate change.
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
