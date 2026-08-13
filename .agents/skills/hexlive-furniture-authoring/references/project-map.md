# Project map

Use these as routing hints; inspect current code because names can evolve.

| Concern | Canonical location |
|---|---|
| Project rules and spatial drawing requirement | `AGENTS.md`, `CLAUDE.md` |
| Behavior and coordinate contract | `Spec/<N>.md` (`§N` → `Spec/N.md`; index in `spec.md`) |
| Hex radius and world conversion | `Assets/HexLive/Simulation/Spatial/HexSpatialMath.cs` |
| Junction templates and 0.375-wu grid | `Assets/HexLive/Simulation/Spatial/HexPointLayout.cs` |
| Hut production layout data | `Assets/HexLive/Simulation/Runtime/BuildingRules.cs` |
| Production furniture spawning/repair | `Assets/HexLive/Simulation/Bootstrap/BuildingBootstrap.cs` |
| Simulation-to-Unity yaw conversion | `Assets/HexLive/UnityPresentation/Spatial/SimulationUnityMapper.cs` |
| Production object rendering | `Assets/HexLive/UnityPresentation/Rendering/HexWorldRenderer.cs` |
| Furniture factory precedent | `Assets/HexLive/UnityPresentation/Environment/BedAssembly.cs` |
| Hut layout draft UI | `Assets/HexLive/UnityPresentation/HutTest/HutLayoutDesigner.cs` |
| Hut production acceptance fixture | `Assets/HexLive/UnityPresentation/HutTest/HutTestBootstrap.cs` |
| Building art master | `Assets/ArtSource/Building/hexlive_building_kit.blend` |
| Player-packed furniture FBXs | `Assets/Resources/HexLive/Objects/` |
| Blender authoring/export scripts | `Tools/blender/` |

## Data ownership

- Blender owns artistic geometry, materials, construction piece hierarchy, clean asset axes, and floor pivot.
- Simulation/blueprint data owns junction anchors, occupied/blocked nodes, wall side, allowed yaw, availability, and save/load state.
- Presentation owns only conversion into Unity coordinates and visual state. It must not invent a second layout.
- `HutLayoutDesigner` `PlayerPrefs` key `HexLive.HutLayoutDraft.v1` owns an editable draft. It is not the ordinary game's source of truth until explicitly promoted to committed layout data.
- The player's explicit Save button writes `[HutDesigner][SAVED]` plus JSON to Unity's `Editor.log`. Use `scripts/extract_latest_hut_layout.py` and promote only the element the player just approved.

## Current reference assets

- `bed_basic_final_native.fbx` demonstrates the established primary elongated axis and the identity runtime-wrapper pattern. Its legacy imported root scale is not a template for new exports; new code-facing FBX roots must be clean.
- `furniture.wardrobe.fbx` is the normalized standing-furniture example: three-node axis along Blender local `+Y`, floor pivot in the middle, and six footprint yaws without asset-specific rotation code.
