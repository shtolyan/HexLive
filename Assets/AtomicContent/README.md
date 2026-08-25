# Atomic content authoring descriptors

`Tools/content.py build` resolves `wear`, `actor`, `hair` and `prosthetic`
directly from their stable source conventions. Hair colour materials become
entries of that same hair bundle; prosthetic source names are derived from ids
such as `leg.wood.l`. Their owner icon remains mandatory. Other content
families use one descriptor per logical object:

```text
Assets/AtomicContent/<type>/<id>.json
```

That file is the private authoring recipe for one object, not a runtime catalog
or a content-release manifest. Building it never reads sibling descriptors.

For the initial population of an empty server, `Tools/content.py build-all`
discovers these same independent recipes plus convention-based runtime assets.
The active wear inventory comes from the simulation's independent object
definitions; authoring-only `GarmentCatalog`/`ActorAppearanceCatalog` assets are
never published and are not runtime dependencies.
It still calls the classic bundle pipeline once per logical object, checks zero
external dependencies for every result, and emits ordinary per-object
`candidate.json` files. `publish-all` only batches transport and process startup;
the server promotes each candidate under its own lock/revision. The generated
inventory is transient build output, never a client catalog or release id.

Both `build` and `build-all` refuse to start while any tracked Git LFS asset is
still a pointer stub. Hydrate the checkout with `git lfs checkout` (and
`git lfs pull` if the object is not local) before building; a 130-byte pointer
must never become a valid-looking immutable content blob. Generated metadata
and bootstrap icon importer GUIDs are derived from `type/id`, so identical
inputs produce the same bundle SHA and a repeated publish is a server no-op.

```text
python3 Tools/content.py build-all --platform StandaloneOSX --output <osx-root>
python3 Tools/content.py build-all --platform StandaloneWindows64 --output <windows-root>
python3 Tools/content.py publish-all --input <osx-root> --input <windows-root> \
  --host hexlive-server --required-platform StandaloneOSX \
  --required-platform StandaloneWindows64
```

Remote promotion runs the installed server CLI as the unprivileged `hexlive`
account (override with `--remote-user`) and keeps `/var/lib/hexlive/assets`
writable by that service account.

When another platform is bootstrapped later, publish that platform with
`--retain-current-variants`. The server retains only verified existing variants
and refuses the merge if metadata changed, so a Windows bootstrap can add its
payloads without deleting macOS while a real semantic object update still has
to publish all supported platforms together.

```json
{
  "main": "Assets/HexLiveContent/Objects/axe_stone.prefab",
  "displayName": "Stone Axe",
  "metadata": {
    "slot": "hand",
    "category": "tool",
    "legacyResourcePath": "HexLive/Objects/tool.axe_stone"
  },
  "entries": []
}
```

The descriptor itself is embedded as the bundle's `metadata` entry. Visual
gameplay objects (`wear`, `actor`, `hair`, `prosthetic`, `object`, `building`,
`mob`) always resolve `Assets/HexLiveContent/Icons/<id>.png` when `icon` is not
explicitly overridden. A missing owner icon is a hard build failure. The icon
is embedded in this exact object's bundle and is never assigned to an icon
group, catalog, or standalone bundle (§152.2).

The one-time bootstrap inventory may embed the bootstrap placeholder in an
icon-bearing owner that predates icon authoring; it marks that object's metadata
with `iconPlaceholder=true`. This is still an entry of that exact owner bundle,
never a shared icon payload, and a later real icon update revises only that
object. Ordinary single-object publication keeps the hard owner-icon gate.

Additional roots still belong to this one bundle. Hair descriptors, for
example, embed colour materials with stable entry names:

```json
"entries": [
  {"name": "colour/black/Hair", "asset": "Assets/ImportedActors/Hair/X/Materials/black/Hair.mat"}
],
"metadata": {
  "colours": [
    {"id": "black", "surfaces": ["Hair"]}
  ]
}
```
