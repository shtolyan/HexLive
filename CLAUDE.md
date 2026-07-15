# HexLive — agent guide

Survival-colony sim (Unity, URP). The canonical design/behaviour spec is
**`spec.md`** (repo root) — keep it in sync with code (spec-first).

## Generating tool / weapon models (axe, knife, pickaxe, hammer, spear…)

**Follow `TOOL_GENERATION_SPEC.md` exactly.** Every tool must be produced the
same way so they stay consistent.

**⭐ KEY RULE: generate a HIGH-poly textured mesh, THEN decimate it. Never try to
make an AI output "low-poly" directly** — it produces mush. The flow is:

1. **flat-shaded faceted IMAGE** (fal.ai flux, our low-poly prompt) →
2. **high-poly textured GLB** (Artificial Studio `trellis-2` image-to-3D, ~490 K tris — keeps facets) →
3. **fix WebP textures** in the GLB (glTFast can't read `EXT_texture_webp`) →
4. **import** to Unity → **decimate** (UnityMeshSimplifier, ~20-30 K tris) →
5. **reorient/pivot** to the conventions (handle +Y, grip base at pivot, working edge +Z) — do NOT scale to a size; `ObjectFit` normalizes hand+ground size at runtime (tools → `0.216×HexRadius`) →
6. **flat URP/Lit material** + extracted albedo →
7. **prefab** at `Resources/HexLive/Objects/<id>.prefab` (hand + ground load it automatically; back up any old `.fbx`) + an `ItemAttachConfig` entry.

Shipped tools: `tool.axe_stone`, `tool.knife`, `tool.pickaxe_stone`. Any `tool.*`
in hand auto-gets the armed idle/walk clips; axe/pickaxe also get the Chop
animation. Tune the in-hand pose in the `AxeChopTest` scene (`toolId` field → Play → Save).

## Conventions

- Art style is **flat low-poly / faceted / cartoon** — no noise/procedural textures.
- Unity work goes through the UnityMCP bridge; it drops on domain reload / when the
  editor is unfocused — re-pin the instance and retry. Guard mutations with
  `if (Application.productName != "HexLive") return;` (a second project may share the bridge).

## Headless simulation probes (no Unity)

For AI/GOAP/simulation checks, do not start Unity just to run ticks. Build the
simulation assembly first:

```bash
dotnet build HexLive.Simulation.csproj --no-restore
```

Then make a temporary console probe under `/private/tmp` targeting `net9.0` and
reference the built DLL directly:

```xml
<Reference Include="HexLive.Simulation">
  <HintPath>/Volumes/ORICO/HexLive/Temp/Bin/Debug/HexLive.Simulation/HexLive.Simulation.dll</HintPath>
</Reference>
```

Use `WorldBootstrapDefinition` + `WorldStateFactory().Create(def)` to build a
small hand-authored world, then create a `SimulationEngine` and register the same
core systems as `SimulationRunnerBehaviour.RegisterDefaultSystems`:
`PathfindingSystem`, `MovementSystem`, `ExecutionSystem`, `PerceptionSystem`,
`DecisionSystem`, `PlanningSystem` first; add slow/world systems only when the
probe needs them. Step with `engine.Step()`. Read traces from
`world.Events.Items` (not `world.Events` directly).

This works for focused checks like “without a knife, does a hungry NPC craft a
tool before planning coconut food/water?” and avoids re-discovering the Unity
runtime path every session. A `net471` temp console will fail on this machine
because ordinary .NET Framework reference assemblies are missing, even though
the Unity `.csproj` itself builds.
