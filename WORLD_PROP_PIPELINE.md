# World props: Blender → Unity → Player

`GLB`/`glTF` is an editable art-source format only. It may live under
`Assets/_AiGen` or `Assets/ArtSource`, but never under `Assets/Resources` and
never as a direct or transitive dependency of an asset in `Resources` or a
native-only Addressables group. Unity's glTF `ScriptedImporter` sub-assets can
exist in Editor and disappear from Player while leaving a non-null prefab root.

The canonical inventory is `Tools/world_prop_manifest.json`. Every shipped prop
declares its source, runtime-native asset, minimum renderer count, required
material slots and alpha contract. Rejected experiments are not manifest rows.

## Export

1. Author the approved model in Blender; keep GLB only as source.
2. Add one `mode: native` manifest row. Use `existing-native` for an approved
   FBX already authored natively and `native-assembly` for a runtime assembly
   made exclusively from native pieces.
3. Run Blender, not ordinary Python:

   `blender --background --python Tools/bake_world_prop_fbx.py -- --repo <repo> --id <manifest-id>`

   The exporter preserves mesh and empty stage groups, writes FBX under
   `Resources/HexLive/Objects`, imports it back in Blender and refuses changed
   mesh count, empty geometry or changed bounds. `--id` is mandatory and may be
   repeated: a broad invocation can never silently overwrite every approved
   native asset, including protected palm/rock mirrors.
4. Runtime ids resolve only through `WorldPropResources`; assembled furniture
   resolves through `BedAssembly`. A prefab wrapper around the GLB is not a
   runtime artifact.

Opaque leaf silhouettes must be real geometry. Textured cards must declare
`alpha: clip` or `blend`, retain their texture dependency and use a compatible
Player shader. An opaque rectangle is never an acceptable leaf fallback.

## Gates

`WorldPropBuildGate` runs before every Player build and is also available from
`HexLive/Content/Validate Player-Safe Resources`. It rejects:

- `.glb`/`.gltf` anywhere under Resources;
- direct or transitive glTF dependencies of any Resources asset;
- a missing manifest runtime FBX;
- fewer renderers than declared or missing declared material slots;
- glTF dependencies in native-only Addressables roots;
- a prosthetic catalog other than eight native FBX files.

`BedAssembly` additionally verifies structural contracts at runtime. The drying
rack, for example, is valid only with four renderable stick pieces and four
renderable lashings; one surviving rope renderer cannot mask stripped stands.

## Prosthetics

The eight fitted prostheses are already Player-safe native FBX files under
`Assets/HexLiveContent/Prosthetics`. They are external Addressables entries, not
Resources assets, and contain no GLB/glTF importer dependency. The manifest and
build gate keep the count at eight and scan their transitive dependencies.
