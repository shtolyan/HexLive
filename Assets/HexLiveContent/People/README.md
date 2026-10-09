# WebGL people — preparation in progress

Source files for the new character catalogue. **Not game-ready yet.**
Do not build or publish AssetBundles without the player's separate command.

The single Blender source remains `_ArtSource/Characters/BrowserLowPoly/Primal_Wardrobe.blend`
in the original HexLive checkout. No second Blender session or working blend was created.

`Source/` contains 55 FBX exports: Marta and Kshishtof (male genitals are a
separate mesh node), 15 Primal garment fits, 32 female hair LODs and six
backpack fits. Material slots, variant texture metadata, all 172 body bone
names and 109 morph names are preserved. `source-manifest.json` records SHA256
hashes, textures, materials, mesh budgets and exact bone/morph names.
`fbx-verification.json` checks the actual binary FBX files, not only scene data.

Export and inspection scripts are in
`Assets/HexLive/UnityPresentation/Wearing/Editor/PeopleSourceTools/`.
They run inside the existing Blender scene; they never launch Unity or build bundles.

## Remaining integration work

- Unity import/axis/scale/bind-pose/morph round-trip and memory validation.
- Texture import limit 1024, alpha and normal-map settings per material role.
  Source images are retained byte-for-byte; their names are not size guarantees.
- Decide body weight reduction after animation comparison: source bodies have
  up to 11/10 influences; source exports preserve them rather than silently dropping weights.
- Game prefabs, skins/eye variants, a single female/male geometry contract,
  skinned Wear bindings, separate genitals reference and clothing visibility.
- New maps for dirt, blood, wounds, tearing and tan coverage from the new topology/UV.
- Final clothing fit review in motion, including local boot/collar intersections.
- Explicit new active catalogue, compatible old appearance IDs, a dry-run dependency audit excluding the old people catalogue.
  The player decided in the Claude chat to leave old files at their existing paths;
  do not physically move them into Legacy.
- Gameplay/portrait/lipsync checks and WebGL performance measurements.

The old live catalogue is intentionally still active until the replacement is
complete. World assets are outside this migration. Current Unity/build ownership
is coordinated with Claude in `WEBGL_ASSET_HANDOFF.md` and `.codex.md`.
