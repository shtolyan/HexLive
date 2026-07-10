# Iteration 23 — Actor Visualization & Wardrobe (Marta, Molly, Jolie)

Spec: §31B (new section, written first), §19.3 amendment (DisplayName /
ActorMesh). Simulation logic untouched — soaks stay green by construction.

## Source

`/Volumes/ORICO/molly_copy` — Daz3D Genesis3Female actors with a
bone-stitching wear system (Wear prefab + BodyBones + ParentConnection).
NPC identities: **Marta** → Marta mesh, **Molly** → Molly mesh, **Jolie** →
Jana mesh (no Jolie actor exists; the name is the colony's).

## What was transferred (the "smart import")

- **206 assets, ~731 MB** under `Assets/ImportedActors/` — the exact GUID
  dependency closure of the three bodies, three hairs, and six clothing
  items, computed by script (guid scan over prefab/mat/meta), not by folder
  copying. All original .meta files ship alongside → every cross-reference
  survives. Zero unresolved GUIDs verified post-copy.
- **FinalIK** (`Assets/RootMotion`, 2.9 MB, user decision mid-iteration):
  kept wholesale with new `RootMotion` / `RootMotion.Editor` asmdefs;
  presentation asmdef references it.
- **Wardrobe subset** (spec 31B.4): Panty_31415 + Bra_20266 →
  `underwear.cloth`; SkinnyJeans_24487 → `clothing.leather_pants`;
  Jacket_7653 → `clothing.coat`; S3D_DdlSlc_Top → `armor.leather`;
  jacket_8867 → `armor.heavy`. Prefabs moved (GUID-preserving) to
  `Assets/Resources/HexLive/Wear/<simDefinitionId>/`.
- **Animation**: only `DefaultAvatar@Idle_Neutral` + `@WalkForward_NtrlFaceFwd`
  FBX clips (instead of 233 MB packs) + a hand-authored 2-state
  `HexNpcLocomotion.controller` (Speed float), shared by all three girls via
  humanoid retargeting.
- **Excluded**: all adult content, male gear, DynamicBone, combat/AI/camera/
  Firebase scripts, WearData ScriptableObject layer (redundant for us).

## Script adaptation (not copying)

`Assets/HexLive/UnityPresentation/Wearing/`:
- `Wear` and `BodyBones` reimplemented with identical serialized layouts;
  their .meta files **claim the original GUIDs** (`7a660832…`, `eaba56e3…`)
  so all copied prefabs bind to the adapted classes with zero YAML edits.
  `ActorName` enum order preserved (serialized as int). Gender checks,
  events, genitals toggling (always off now), Actor/Localize/VisualScripting
  coupling — removed.
- `ParentConnection` — runtime-only, fresh GUID.
- `NpcActorView` — snapshot bridge: WornItems diff → Equip/TakeOff (a sim
  item may map to several garments), Speed animator param from
  MovementStatus, and the ported LookAtIK gaze (solver.target + weights,
  smooth ease; talkers look at partners, walkers down the path).
- `ActorWardrobe` — Resources-convention catalog, no inspector wiring.

## Naked-base prefab surgery

Stripped copies authored by script into `Resources/HexLive/Actors/`:
Marta 404→370 docs, Jana 439→378, Molly 363→358. Kept: full skeleton, body
SkinnedMeshRenderers, Animator (repointed to our controller), BodyBones,
**LookAtIK + FullBodyBipedIK** (user decision — eyes and posture stay
alive). Removed: 30+ gameplay components (combat/AI/weapon stack, Daz glue,
face blendshape scripts, physics, NavMeshAgent) and camera/anchor children
(DialogueCamera, Points, TonyAnchor, …). Prefab YAML surgery removes
documents, scrubs m_Component/m_Children lists, and validates that no
dangling GUID remains.

## Renderer bridge

`HexWorldRenderer.CreateNpcView` instantiates the actor prefab by
`snapshot.ActorMesh` (primitive capsule = fallback), normalizes scale to the
hex metric (1.7 m source height → NPC capsule height × 2.4), and per
snapshot syncs wardrobe/walk/gaze via `NpcActorView`. Added the previously
missing **stale NPC view sweep** — a dead girl's walking view is destroyed
when her corpse object takes over. Debug panel shows display names.

## Verification

- HexLive.Simulation / UnityPresentation (incl. FinalIK sources) /
  UnityDebug all compile; harness soak seed 12345 still OK.
- GUID resolution check over all new/moved prefabs: 0 unresolved.
- Play-mode checklist (needs the Unity editor, spec 31B.6): girls render at
  sim positions, walk animation, hair spawns, dress/undress visuals follow
  the sim, gaze during talks, corpse leaves a naked-base cleanup.

## Performance hardening (first play-mode profile)

The first profile (15 FPS, 897 ms main thread) indicted the debug layer,
not the actors (`NpcActorView.Update` = 0.00 ms):

1. **Full world snapshot exported per consumer per frame** — renderer,
   debug panel, and point overlay each called `WorldSnapshotExporter.Export`
   every frame (~180k GC allocs/frame combined). Fixed: the runner caches
   the snapshot per simulation tick (spec 31.17).
2. **~14k junction marker spheres ≈ 10M triangles** — markers are now
   debug-only behind the renderer's `_showJunctionMarkers` flag (default
   off).
3. **Point overlay refreshed 14k UI badges + GL edges per frame** — now
   refreshes only on tick change / camera move, culls badges and edges to
   `OverlayRange` (14 units) around the camera focus, hard cap 700 badges.

## Known follow-ups

- Unity will regenerate .csproj files and folder .metas on first open.
- Actor scale factor (2.4×) may need eyeball tuning.
- FBBIK idles unused until a mechanic needs it (kept per user decision).
- Wet/durability visuals, face animation, bow/tool props in hands — later.
