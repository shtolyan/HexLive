# Iteration 25 — Full Model Pass & Interaction Animations

Spec: §31C.6 (written with the change). Simulation untouched — presentation
only; soaks unaffected by construction.

## Models (Kenney Survival Kit + Food Kit + Nature Kit, CC0)

Downloaded from kenney.nl (licenses in `ImportedActors/Kenney/`), mapped by
the Resources convention (`Resources/HexLive/Objects/<definitionId>.fbx`):

| Sim object | Model |
|---|---|
| tree.big (green cylinders — gone) | nature `tree_default` |
| tree.palm | nature `tree_palmDetailedTall` (iter 24) |
| forest.deadfall | survival `tree-log` |
| rock.boulder / resource.stone | survival `rock-b` / `resource-stone` |
| resource.firewood / palm_leaf | survival `resource-wood` / `grass` |
| campfire.spot | survival `campfire-pit` |
| food.coconut / meat_raw / meat_cooked | food `coconut` / `meat-patty` / `meat-cooked` |
| tool.axe_stone / pickaxe_stone / pot | survival `tool-axe` / `tool-pickaxe` / `bucket` |
| grave.npc | survival `signpost-single` |
| construction.site / station.drying_rack | survival `structure-floor` / `fence` |
| bed.basic | survival **bedroll** (sci-fi bed retired) |

Auto-fit gained per-category targets (tools/resources 0.18 R, campfire
0.55 R, boulder 0.45 R, grave by height 0.35 R, deadfall/rack/site 0.7 R).
`colormap.png` ships next to the models for Kenney material lookup.

## Water: no gizmo (user decision)

`water.pond` / `water.river` interaction objects now render as **empty
anchors** — the sunken translucent river/pond tiles are the only visual;
girls walk to the bank and drink like in Stranded-style games. Sim objects
unchanged (they remain the Drink anchors).

## Interaction poses & hand props

- Clips reused from molly_copy (with their .meta/humanoid configs):
  Polygonmaker `crouch_inplace`, Mixamo `Sitting`.
- Controller: new bools `Working` / `Sitting`, states Crouch / Sit
  (AnyState entry, exit to Idle; Laying keeps priority).
- `NpcActorView.SetInteraction`: PickUp/Harvest/Build/Craft/Fuel/Bury/Hang
  → crouch; Sit → sit.
- **Hand props** attach to the `rHand` bone: eating → the food's own model
  (coconut/meat), chopping/mining → axe/pickaxe, drinking boiled → pot;
  bounds-normalized to palm size, destroyed on interaction change.

## Live-editor fixes (first MCP session, play-mode paused)

Diagnosed with the Unity MCP server on the user's paused play session:

1. **Laying/Sit states had NULL motions** — the hand-authored controller
   referenced `fileID 7400000` inside the new FBXes, but their clips carry
   different ids. The "laying" girl therefore played an empty state:
   upright default pose sunk to the attach height ("вошла в кровать и
   пропала"). Fixed by assigning clips through the AnimatorController API
   (by asset reference, immune to fileID guessing).
2. **Humanoid discarded the lying orientation** — a lying pose lives in the
   clip's root rotation, which humanoid import drops unless baked. Enabled
   Bake Into Pose (rotation + height, keep original) for Laying/Sitting —
   the girl now visibly lies on the bedroll, head on the pillow (verified
   by in-editor render).
3. Hygiene: CopyFromOther clips had NULL source avatars — DefaultAvatar@
   clips now point at DefaultAvatar.fbx's avatar, crouch builds its own.
   Pose sanity checked live: idle 0.81 / crouch 0.40 / sit 0.42 /
   laying 0.40 (bounds heights).

## Notes

- No good semantic "eat" clip exists in the source packs (mocap library is
  CMU-numbered); the hand-held food + idle reads well enough until a
  proper clip is found.
- Kenney FBX may need a material touch-up in the editor (colormap
  assignment) if models import gray.
- Spaceship bed dependencies remain in ImportedActors (unused, ~24 files) —
  clean up in a housekeeping pass if disk matters.
