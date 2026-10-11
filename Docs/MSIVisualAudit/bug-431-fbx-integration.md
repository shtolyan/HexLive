# Flashback #431: hair source restoration

The existing People source tree now contains 16 styles / 32 FBXs with original
SourceUV, strand alpha, scalp/cap geometry and authored skin weights. The old
opaque EMIT RGB atlas and smart-project UV had discarded cutout information;
changing material transparency flags could not recover it.

The verified Primal_Wardrobe.blend SHA256 is
568b871a7c839e140de973da1e670fe1ab7e00cceabab8083e0267b6bd7b5661.
It was read without saving. Candidates retain source UV and corner normals.
Bendine/Chunky use regional strand selection to preserve scalp coverage and tails;
both LODs share the union of weighted bones and ancestors. Export sorts faces by
material and removes unused slots to match Unity's first-use material order.

PeopleHairSourcePostprocessor verifies each FBX hash against its adjacent
.hair-bindings.json, then restores named influences lost by native FBX import.
The native importer dropped small weights even with minBoneWeight=0; independent
Blender re-import preserved them. The production audit verifies positions, UV,
named weights and per-material triangle counts without patching audit clones.

PeopleAssetPreparation.ImportHairAndPrepare imports and prepares hair only.
It preserves authored colour, UV transform, texture and cutoff. Nine existing
GUID-resolved grayscale masks restore 26 material slots on Jelika, LowPonytail
and Onyx. Their red channel, not opaque alpha, becomes the colour texture alpha.
Source cutoff is retained (Leony 0.12); LowPonytail strands use reviewed 0.08
because legacy 0.42 erased nearly all of this dark mask. Tint alpha is 1 for
separate-mask materials. No invented legacy alpha-strength formula is applied.

## Validation on MSI, Unity 6000.4.5f1

- Production FBX parity: 32/32, zero unmatched vertices, no diagnostic weight
  restoration. Maximum position error 0.020 mm; weight error 1.1920929e-7.
- Prepared prefab audit: 16/16, shared skeleton, material/submesh contracts,
  no legacy ImportedActors dependency, 304 finite run-animation samples.
- 144 native images: four rest views per LOD and one run view per style.
  All 128 static views were reviewed after opacity/cutoff changes.
- Actual camera/LODGroup test: 48/48 near/mid/far cases across 16 styles.
  Diagnostic green/red materials identify LOD0/LOD1; far views are culled.
- SpecStructureGateTests: 6/6 passed on installed .NET 10 with process-only
  DOTNET_ROLL_FORWARD=Major. An earlier sandbox testhost connection timed out;
  the successful run is HairSpecTestsVerified, not that failed attempt.

Evidence under Build/MSIVisualAudit: HairExport/unity-production-fbx-audit.json,
PreparedHair/audit.json, HairLodCamera/audit.json, PreparedHair/prepared-contact-1.jpg
through -4.jpg, unity-hair-integration.log, unity-hair-opacity-audit.log,
unity-hair-lod-camera-audit.log, HairSpecTestsVerified/*.trx.

## Integration and remaining player test

Mac must review the shared PeopleAssetPreparation.cs change before integration.
Hair runtime owners (PortraitStage, CharacterPanel, NpcActorView, NpcFaceAnimator,
HexWorldRenderer) are unchanged. Existing unrelated local edits are excluded.
The committed FBXs, binding sidecars, manifest, textures, materials and prefabs
are the prepared source set; Mac does not need to rerun Blender for integration.
For regeneration, export_hair_candidates.py requires the pinned candidate JSONs
and selected-integration-inputs.json from the accompanying source-input archive.
Do not run install_hair_source.py over dirty targets: it deliberately refuses.

The image audit explicitly selects one LOD and draws baked meshes; it does not
exercise runtime property blocks. The separate camera test checks automatic LOD
selection with diagnostic colours, not final shader cost. Coarse low-poly edges,
Onyx's visible LOD silhouette change, draw calls, live colour variation and browser
performance still require the coordinated WebGL build/player test. No WebGL build
or publication was performed on MSI; source manifest game-ready flags stay false.

Source-input/evidence archive: hv001-source-inputs-and-evidence.zip (28,901,195 bytes).
SHA256: 6a4f81f28e3913a1d06281da9af085ab257dc7616245931e1d8cee3489ee7ad5.
It contains 32 pinned candidate JSONs, selected-integration-inputs.json, final
captures, parity/animation/LOD reports and audit helpers. It is not a checkout
or a production build. Helpers are diagnostic CLI entry points, not runtime code.
