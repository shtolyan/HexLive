# Atomic content: macOS/Windows lockstep runbook

This runbook is the operational companion to §152. Both platform variants of
an object must be built from the same Git commit. Do not maintain a handwritten
inventory, rename authoring ids, or inject platform-specific placeholder art.

## 0. Two-machine handoff gate

The release owner announces one immutable build SHA. macOS and Windows must
check out that exact SHA, not merely the same moving branch name. Do not start a
new queue while the integration branch is still advancing. Once the SHA is
announced:

```text
git fetch origin
git switch --detach <announced-build-sha>
git lfs pull
git lfs checkout
git rev-parse HEAD
```

Both builders send the resulting full SHA to each other before invoking Unity.
If either value differs, stop. Candidate directories produced from an older
HEAD, a partial handwritten queue, or a local uncommitted fix are not merged
with this bootstrap and must be kept outside the publish input.

The answers to the current Windows handoff are part of this contract:

- `FCO * Male`, `FAO Harness Male`, `TonnyFlash`, and DAZ hair colour names with
  spaces are valid legacy ids/entries. Do not rename or escape them.
- All active male raid garments are normal publication objects. Empty
  `clothing.shirt_naughty*` and extracted Alloy residues are not active objects
  and must not be force-built.
- Owner icons are embedded in the owner's bundle. When real art is absent both
  platforms omit `iconAsset` and the Player uses its stable emoji; neither
  machine supplies a platform-local placeholder `--icon`.
- RuntimeSource objects are discovered by `build-all`; no handwritten
  descriptor batch is exchanged between machines.
- `build-all` owns the complete audio and `config/simdata` inventory too. Do not
  run a second platform-specific sound or simdata recipe.
- The only acceptable bootstrap result is the complete 2,710-object inventory
  and digest in §2 with zero failed objects. The earlier 272/289-object Windows
  partial queues are diagnostic output, not publishable candidates.
- Publication targets only the isolated staging root/port in §4. Production is
  unchanged until a cold-cache Player completes the end-to-end acceptance.

## 1. Synchronize the source

The integration branch is `codex/content-release-fixes`.

```text
git fetch origin
git switch codex/content-release-fixes
git pull --ff-only origin codex/content-release-fixes
git lfs pull
git lfs checkout
git rev-parse HEAD
```

The branch is used to fetch the announced commit; §0's detached exact-SHA
checkout is the build state. The macOS and Windows builders must exchange the
final `git rev-parse HEAD` value before starting. If either checkout has source
changes, commit them on a separate branch and merge first; candidate outputs
are not a substitute for a shared source commit.

## 2. Identity and authoring rules

- Current `type/id` validation deliberately allows ASCII spaces:
  `^[A-Za-z0-9][A-Za-z0-9._ -]{0,127}$`. Existing ids such as
  `FCO Pants Male`, `FAO Harness Male`, and `TonnyFlash` are published without
  renaming or escaping. Save ids and simulation ids therefore remain stable.
- Bundle entry names allow letters, digits, `.`, `_`, `-`, `/`, and ASCII
  spaces. Hair entries such as `colour/Adell Col 01/Base` are valid. Do not
  rename DAZ material directories or colour ids.
- `Tools/content.py build-all` is the only bootstrap inventory enumerator. It
  discovers the active wear inventory from `GarmentLibrary.Defaults`, actors,
  hair, prosthetics, objects, buildings, mobs and generic `RuntimeSource`
  assets. Do not enumerate only `GarmentDefinition` files and do not create
  ~195 handwritten descriptors for convention-based runtime assets.
- Missing real owner icons are omitted and marked `iconFallback=emoji`; this is
  identical on both platforms. Never pass a hand-made transparent or placeholder
  `--icon`. A later real icon is committed once and republishes only its owner
  object. Already-published `iconPlaceholder=true` records remain compatible:
  the client ignores their generated diamond and shows the emoji.
- Authoring-only garment definitions that are absent from
  `GarmentLibrary.Defaults` or have no single prefab under their `ArtId` are not
  publication candidates. Do not force-build empty `clothing.shirt_naughty*`
  or extracted Alloy variant residues. The active base objects
  `clothing.skirt_alloy` and `clothing.top_alloy` are discovered normally.
- All current male raid garments, including `FCO Boots Male`,
  `FCO Knee Straps Male`, `FCO Legs Straps Male`, and
  `FCO Waist Strappy Male`, have committed `GarmentDefinition`, art, and owner
  icon assets on the integration branch and must be built.

The bootstrap inventory expected at the time of this runbook is 2,710 objects:

```text
actor=5 audio=1128 building=7 config=754 hair=16 mob=1 object=37
prosthetic=8 vfx=69 wear=685
assetBundle=1581 file=1129
```

For a canonical sorted list containing one `type/id` per line, both machines
can compare this command's SHA-256 after building:

```text
find <output> -name candidate.json -type f -print0 \
  | xargs -0 jq -r '.type + "/" + .id' \
  | LC_ALL=C sort | shasum -a 256
```

For the inventory above the digest is
`de2ec59dae44c2f531189cca3aa679cc225bff88897d8d71c0bf9766ffa7a7ce`.
Windows may use an equivalent PowerShell/Python sort; compare UTF-8 lines and
ordinal code-point order.

## 3. Build one complete platform inventory

macOS:

```text
python3 Tools/content.py build-all \
  --platform StandaloneOSX \
  --output <osx-output>
```

Windows (use the Python launcher installed on that machine):

```text
py -3 Tools/content.py build-all \
  --platform StandaloneWindows64 \
  --output <windows-output>
```

`build-all` invokes the classic Unity AssetBundle pipeline independently for
each logical object and then adds raw audio/config candidates. Raw audio bytes
are platform-independent but still receive both platform variants; blob
storage deduplicates their identical SHA-256 values.

Before transport, both outputs must report zero failed objects, the same
`type/id` set, the expected platform/profile, zero external bundle dependencies,
valid payload hashes, and no Git LFS pointer payloads.

## 4. Publish only to isolated staging

Production `/var/lib/hexlive/assets` and port 5123 are out of scope until the
Player end-to-end test passes. The current isolated target is:

```text
service:     hexlive-staging.service
port:        5124
asset root:  /var/lib/hexlive-staging/assets
server CLI:  /opt/hexlive-staging/current/HexLive.Server
```

macOS is published first. A later Windows bootstrap retains the verified macOS
variant and adds Windows under a new monotonically increasing object revision:

```text
py -3 Tools/content.py publish-all \
  --input <windows-output> \
  --host hexlive-server \
  --required-platform StandaloneWindows64 \
  --remote-root /var/lib/hexlive-staging/assets \
  --remote-user hexlive \
  --server-dll /opt/hexlive-staging/current/HexLive.Server \
  --retain-current-variants
```

If the Windows machine has no approved staging SSH key, archive the complete
`<windows-output>` directory and transfer it to the release owner; do not add a
public upload endpoint and do not point `content.py` at production.

`config/simdata` is emitted by `build-all` from the same committed source. It is
published like every other atomic object; the server-side simulation file used
by a running world remains a separate deployment concern.
