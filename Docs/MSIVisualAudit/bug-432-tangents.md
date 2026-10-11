# Bug 432: restore the body tangent basis

The prepared Marta and Kshishtof meshes had normals and UVs but no tangents.
URP skin and painted bandage normal maps therefore lacked the mesh's tangent
basis. This is independent of the missing skin normal textures, which remain
an open part of #432.

`PeopleSourceModelPostprocessor` now calculates tangents on the final meshes
after splitting the skin, eye and mouth submeshes. Its import version is bumped
so existing cached models are regenerated. Import-time MikkTSpace was rejected:
the native audit detected changed geometry, which would invalidate paint maps.
The importer remains on `None`; the postprocessor supplies the tangents without
changing vertex/UV/index identity.

Native Unity 6000.4.5f1 audit (exit 0) preserved these SHA256 hashes of vertices,
UVs and triangle indices before/after tangent calculation:

| Body | Vertices / final tangents | Geometry hash |
| --- | --- | --- |
| Marta | 4410 / 4410 | `1A44E670F1E171BE7845B837451DE0B86B99194C292B86865AD2F7860DC2BA28` |
| Kshishtof | 8957 / 8957 | `DC4CED9C91F0A1FA737C1AC395793FDB3E23878E785626C3B82321F8DF1D06B4` |

The same native preview rendered opposite tangent-X normals with identical Z,
using the actual body meshes and URP material. Both the missing-tangent control
and repaired meshes changed lighting, so the control is **not** evidence that
all prior normal lighting was absent. The repaired meshes now have their actual
UV-derived tangent basis; the regression test checks unit length, orthogonality
to normals and handedness at every vertex. Render evidence is under
`Build/MSIVisualAudit/SkinNormals/TangentAudit`, with log
`Build/MSIVisualAudit/unity-skin-tangents.log`.

The focused graphics PlayMode run passed all five tests (zero failures/skips,
Unity exit 0): two imported-body tangent tests plus the three existing bandage
atlas/painter tests. Result: `Build/MSIVisualAudit/TestResults/tangents-bandage-playmode.xml`.

This is also a dependency for #433 bandage relief. Its earlier tests verified
atlas placement and normal-texture output, not correct visible relief. Mac must
integrate this change with the bandage fix and rebuild actor content from the
reimported meshes. Shared published-game lighting, animation and WebGL QA remain
required; this change does not publish or certify the release.
