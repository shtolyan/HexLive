# Bug 432: reuse authored skin normal detail

The prepared bodies share a new UV atlas, while the original Unity materials
sample separate face, torso, arm and leg maps. Copying those texture references
onto the new atlas samples the wrong body regions. The committed garment fix
and tangent fix remain dependencies; this change supplies the missing skin maps.

The source is the existing `Assets/ImportedActors/Daz3D` FBX/material/texture
content. Some active materials use height textures converted by Unity, rather
than the separately supplied `*_N` images. `PeopleSkinNormalExport.Export`
decodes the textures actually assigned to those materials, including their
authored `_BumpScale`, through Unity's normal unpacking. Source paths and SHA256
hashes are recorded alongside the intermediate EXRs. No external blend is needed.

`Tools/remap_skin_normals.py` rasterizes the prepared mesh UVs and finds the
corresponding source surface. It transfers only the texture perturbation,
parallel-transported to the destination normal and expressed in the actual
Unity tangent basis. Source mip filtering follows the destination pixel footprint.
It deliberately does not bake differences between the two body shapes: an earlier
experimental surface bake introduced nail/mouth artifacts and was rejected.

Reproduction, from this checkout with ordinary Unity and Blender:

1. Unity `-batchmode -quit -executeMethod HexLive.UnityPresentation.Wearing.Editor.PeopleSkinNormalExport.Export`.
2. Blender `--background --disable-autoexec --python-exit-code 1 --python Tools/remap_skin_normals.py -- --source-dir Build/PeopleSkinNormalSource --output Build/PeopleSkinNormals`.
3. Unity `-batchmode -quit -executeMethod HexLive.UnityPresentation.Wearing.Editor.PeopleSkinNormalExport.Install`.

Supply the existing project's `-projectPath` and separate `-logFile` to Unity.
Run only when another editor is not using the project. The installer updates the
two skin normal textures, base/complexion material references and source manifest.
Future `PeopleAssetPreparation` updates existing complexion normal references.
Colour properties remain separate from the normal-map change.

Native export, remap and installation completed with exit 0 on MSI. The first
installation attempt failed compilation on `Environment.NewLine`; qualification
with `System.Environment` fixed it before any installation occurred.

Transfer metrics (`Build/PeopleSkinNormals/projection.json`):

| Actor | Covered samples | Source distance p95 / maximum, cm | Singular tangent samples | Negative tangent Z |
| --- | ---: | ---: | ---: | ---: |
| Marta | 452354 | 0.3455 / 0.8094 | 7 | 0 |
| Kshishtof | 481980 | 0.0911 / 0.6490 | 6 | 0 |

The few singular interpolated tangent samples fall back to flat normals.
Both atlases are 1024 pixels, linear NormalMap imports with mipmaps. Material
strength is 1 because the original per-region strengths are already encoded.
This is a filtered atlas representation, not pixel-identical high-resolution art.

Local Unity 6000.4.5f1 verification passed all seven focused PlayMode tests
(two skin atlas/material tests, two tangent tests, three bandage tests), with
exit 0. Results: `Build/MSIVisualAudit/TestResults/skin-normals-bandage-playmode.xml`.
These tests check material/import settings, tangent validity and painter output;
they do not establish published-game visual quality.

The installed-material comparison produced 32 source/target, normal/flat views
under identical neutral material and lighting settings, exit 0:
`Build/MSIVisualAudit/SkinNormals/Preview-Installed`. Inspected head, hand and
torso views show subtle authored detail without the rejected surface-bake pits.
Low-poly silhouette/shape differences remain and are not normal-map detail.

Mac must integrate all #432 commits and regenerate actor content with
the skin textures and material dependencies. Published-game/WebGL checks under
side lighting, skin seams, animation and bandage overlays remain required.
No publication or final live QA is implied by local checks.
