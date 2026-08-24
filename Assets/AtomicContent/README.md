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
