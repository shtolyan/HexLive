# Bug #395 — Windows content delivery verification

Bug #395 reports that Windows still shows old palm/stump art and an oversized
crab. The report has no captured server context. The current `ServerBook`
record for player version `0.1.105` points at the NYC content API
`https://163-245-204-96.sslip.io/api/assets/v1`; that endpoint is the concrete
delivery target for this verification, not proof of which record the reporter
selected.

## Source and candidate evidence

The first Win64 candidate batch under `Build/bug395-validation` proved that the
four canonical owners build as self-contained payloads and can coexist in one
load session. Production factory validation exposed an FBX basis mismatch in
the first stump candidate; it was rebuilt after the source correction. The
other three payloads were not rebuilt.

| Owner | Approved source evidence | First Win64 candidate |
|---|---|---|
| `crab` | config footprint `0.16`, visual yaw `-90` | `e4da3dcb487e157b708181b088d62c08e018f734ddc4fbb19f09c5d84fa9fa82`, 96,059 bytes |
| `stump.palm` | basis-normalized FBX `4af88a1e55566fb3589cc2ca6df2b663ea8c5842cd6e1b4476d8cdb39f76c7b1` | `ca2a27f0773455cd1ad5e769b1e5d5fe586f97ce2e7a243c8637ab20f4ba6fa6`, 65,260 bytes |
| `resource.palm_crown` | FBX `1a287bc99922baedff50adc3718e3945d29761945996c39ea6c8e845eb2f710a` | `fefb578e759a0292f40dc5962b5afefd83905bb90b4745b1ae9eb2bf7e8de12b`, 153,179 bytes |
| `resource.palm_crown_small` | FBX `f364eaafadd4da6b242f93c521de35b93d6002e615fc695947ae82280b416e44` | `2a954a668df480fc60f80b11bc10d1002eefbc558b8608e4865ee46c107fbafa`, 78,638 bytes |

The portable candidate package was also read back through
`Tools/content.py merge_candidates`; all four relative `stagedPath`, payload
SHA-256, byte size, and `StandaloneWindows64` checks passed. A final package
will replace the superseded stump payload after the factory proof passes.

## Stump basis correction

The approved stump mesh imported with an owner-root X rotation of approximately
`-90°`. Consequently, `StumpFactory` local scale `(0.85, 1, 0.85)` acted on
world X/Y and produced bounds `(0.509291, 0.255000, 0.630000)` instead of
preserving height. Enabling Unity's `bakeAxisConversion` only changed that root
to approximately `+90°`, so the `.meta` experiment was reverted unchanged.

The authoring exporter now bakes the Blender-to-Unity space only for
`stump.palm`. A headless Blender import comparison in
`Build/bug395-validation/stump-basis-comparison.json` passed all four gates at
`1e-6`: world-space vertices, triangles, per-face material assignments, and
bounds are unchanged. The source remains a 204-vertex, 120-polygon mesh with
`Bark`, `Sapwood`, and `Heartwood`, and bounds `(0.599166, 0.630000, 0.300000)`
in Blender's imported coordinate convention.

Unity reimport then reported owner-root rotation `(0, 0, 0, 1)`, identity axes,
and source bounds `(0.599166, 0.300000, 0.630000)`. The final safe production
factory proof (`RuntimeFactoryProof-v3.result.json`) loaded all four candidate
records through `ContentAssetService` / `ContentPrefabCache` and passed. It
measured crab `maxXZ = 0.24000001` and stump bounds
`(0.5092908, 0.3000000, 0.5355000)`; both crown geometry fingerprints and
materials matched their approved sources. Cleanup restored the static service
state, removed every temporary object and bundle, left the original scene
clean, and the Unity Console contained zero errors. The five generated PNGs
show the factory output and a common-scale overview.

## Delivery and rollback

Publication is intentionally pending. The four current Windows payloads were
downloaded from the public HTTPS blob endpoint and their SHA-256 and sizes were
verified; their current object records are stored beside them as monotonic
rollback inputs. Each object is published separately with
`Tools/content.py publish --retain-current-variants`; this preserves the current
macOS variant only when the post-publish metadata check confirms exact equality
of its prior SHA-256 and size. The four commands are not one transaction.

NYC SSH access and host trust have not been authorized in this task. No SSH
alias, key, `known_hosts` entry, remote upload, or registry mutation is part of
this commit. The bug remains `in_progress` until the player explicitly permits
that destination access. After delivery, the agent must verify each new Windows
blob and the unchanged macOS SHA/size through HTTPS before setting
`ready_for_test`; the player's visual acceptance then decides `fixed` versus
`rework`.
