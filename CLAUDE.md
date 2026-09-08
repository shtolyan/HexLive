# HexLive — agent guide

Survival-colony sim (Unity, URP). The canonical design/behaviour spec lives in
**`Spec/<N>.md` — one file per section** — and must stay in sync with code
(spec-first).

## ⭐ Спека: `§N` → `Spec/N.md`, без поиска

- **Ссылка резолвится в путь механически.** Увидел `§105.14` в комментарии C# —
  открывай `Spec/105.md`. Подпункты (`§54.14`, `§21.21B`) живут внутри файла
  своего раздела. Не грепай — открывай.
- **`spec.md` в корне — ГЕНЕРИРУЕМОЕ оглавление** (169 строк, ~14 КБ). Его
  можно и нужно прочитать целиком; раньше это было невозможно — файл весил
  1.5 МБ ≈ 400k токенов, и «spec-first» на практике выродилось в слепой grep.
  Руками не править: `python3 Tools/spec_index.py`.
- ⭐ **Новый раздел — ТОЛЬКО `python3 Tools/spec_new.py "Название"`.** Он выдаёт
  следующий свободный номер (max+1), создаёт файл и дописывает `Spec/ORDER.txt`.
  Не выбирай номер глазами: именно так §84 оказался занят двумя разными темами.
  Дыры (87, 88, 90, 92, 95, 96, 98, 103) НЕ переиспользуются — код на них уже
  ссылается, и занять такой номер значит сделать ссылки не мёртвыми, а лживыми.
- **Номера `§N` не меняются никогда**: на них 3773 ссылки из C#, 364 из
  Tests/Server/Tools, 187 из других `.md` и 1275 внутренних. Переименовать
  заголовок раздела можно, сменить его номер — нет.
- Структуру стережёт `SpecStructureGate` в `dotnet test`: раздел вне
  оглавления, два раздела в одном файле, ссылка `§N` в никуда. Девять висячих
  ссылок уже известны и записаны в `known_missing_spec_sections.txt` — это
  долг (крупнейший, §103, обещан двадцатью файлами ядра ИИ), а не разрешение.
- `python3 Tools/spec_split.py --verify` печатает весь дифф текста спеки с
  момента разреза. Расхождение там — норма, а не поломка.

## ⭐ Пространственное = НАРИСУЙ (правило от игрока)

Любой разговор или правка про геометрию мира — джанкшены, гекс-сетка,
obstacle-блокировки, зоны падения/спавна, пути и подходы, шеренги лежания,
станции взаимодействия, радиусы (`ObstacleRadius`/`SolidRadius`/reach) —
сопровождается **картинкой-схемой** (SVG-виджет/диаграмма), а при изменении
поведения — картинкой «как станет». Требования, чтобы схема не врала:

- Геометрию **считать из реальных констант кода**, не рисовать на глаз:
  `HexPointLayout` (интерьер r=3 → 37 узлов, кольцо r=4 → 24 на рёбрах),
  `HexSpatialMath.HexRadius = 1.5 wu`, шаг суб-сетки 0.375 wu, шеренга/тело
  из `Spec49`, радиусы из каталога. Посчитать точки скриптом — минута,
  и картинка становится документом, а не иллюстрацией.
- Показывать штатные элементы: узлы сетки (тускло), блокированные (красным),
  занятые/исключённые кольца (оранжевым), зону действия (синим), маршрут
  стрелками; легенда по-русски, подписи короткие.
- Числа из картинки (радиусы в wu, счёт узлов) повторить словами в ответе —
  текст остаётся, когда виджет уже не виден.

## ⭐ Server bug tracker (spec §114)

The player files bugs from inside the game into the central server SQLite store.
When the user says «разбери баги» / «посмотри баг-трекер», use the project skill
`.agents/skills/hexlive-bug-tracker/SKILL.md`; do not read the retired reports
array in `BUGS.json`.

- **Your queue** = every report with `status` `"created"` or `"rework"`.
  Before work begins, immediately set it to `"in_progress"` and append a
  Russian agent comment. Each report carries a `context` line
  (`seed=… tick=… npc=…`) captured at submit time — use it to reproduce.
- After implementation, set `status` to `"ready_for_test"` and **append** a
  Russian comment describing the work and that it is ready for testing. The
  player alone decides `ready_for_test → fixed`, `ready_for_test → rework`, or
  archive; an agent must never confirm a fix or archive it.
- Keep `assignedAgent` and a compact Russian `agentHandoff` on the report.
  Rework retains them and is sent to its still-live owner first; otherwise the
  replacement agent must resume from the stored handoff and comments. Before a
  report becomes `ready_for_test`, create an atomic commit
  `fix(bug-<id>): <summary>` with trailer `Bug: #<id>` and append its full SHA
  to `fixCommits`. Never replace earlier SHAs; `fixCommit` is legacy-only.
  Append it with `bugs.py update <id> --fix-commits <sha>` from the checkout
  that holds the commit: the same call uploads the commit's patch (§114.4c),
  which is what the player expands in the card — the server has no git.
  Never include unrelated dirty paths in that commit.
- The lifecycle is `created → in_progress → ready_for_test → fixed`, with
  `ready_for_test → rework → in_progress` on a failed test. Archive is a
  history flag for a confirmed fix. Permanent deletion is an explicit player
  or administrator action, never part of the agent workflow.
- The game polls `/api/bugs/v1/reports` and sends mutations to the same API;
  the web admin at `/admin/bugs` is another surface over the same rows.
- `BUGS.json` is retained only for the Unity MCP lease. Its historical reports
  are imported into SQLite once and must not be edited or reintroduced.

Code: `Server/HexLive.Server/Bugs/` (SQLite + API + admin),
`UnityPresentation/UI/BugReportStore.cs` (HTTP client),
`UI/BugReportPanel.cs` (window), button in `DebugControlsPanel`.
Основной текст существующего отчёта игрок может отредактировать из карточки;
эта операция меняет только `text`, не пересоздаёт отчёт и не затрагивает его
контекст, workflow, комментарии, версии, коммиты или архивный флаг.

### ⭐ Unity MCP: single-owner lease in BUGS.json

Only one agent may use Unity MCP at a time. Before **any** Unity MCP call —
including instance discovery, resource reads, console inspection, screenshots,
tests and read-only probes — acquire the top-level `unityMcpLease` atomically:

```bash
python3 Tools/unity_mcp_lease.py acquire --agent <stable-agent-task-name> --task "<short purpose>"
```

Success writes `status:"busy"`, `ownerAgent`, `task`, `acquiredUtc` and
`heartbeatUtc` into `BUGS.json`; only that owner may call the bridge. Refresh a
long operation with `heartbeat --agent <name>` and run `release --agent <name>`
immediately after the final call, after failure, or before waiting for the user.
An MCP command that timed out may still be running, so keep ownership through
the required artefact polling and release only when that polling is finished.

If another owner is busy, do not make a discovery/probe call, do not hand-edit,
release, or steal the lease. Use `status`, contact `ownerAgent` through the
orchestrator, and wait for `free`; only the owner or explicit player direction
may clear an abandoned lease. Direct JSON editing is not acquisition — the CLI
serializes competing free→busy transitions with a file lock. Work that never
calls Unity MCP does not take the lease.

## Versioned player builds

The build agent uses one entry point **per platform** — macOS and Windows are
two scripts, not one script with a flag, because almost nothing about the
outside of a build is portable (prefs live in `defaults` vs the registry,
symlink vs junction, codesign vs nothing):

```bash
python3 Tools/build_release.py            # macOS  → HexLive.app
python Tools/build_release_windows.py     # Windows → HexLive/HexLive.exe
```

Both share the version reservation, the server bug snapshot and stamping, and
the report/manifest shape. `HexLiveReleaseBuilder` exposes `BuildMacOS` and
`BuildWindows` over one `Run(BuildTarget)`; each refuses to run unless Unity was
launched with the matching `-buildTarget`.

The Windows script differs where the platform forces it:

- It is run on Windows against `StandaloneWindows64`; do not launch it from
  macOS and do not switch a shared checkout back and forth merely to obtain a
  Player. Platform content is a separate `Tools/content.py build[-all]` job and
  is never an implicit phase of either Player build.
- Auto Refresh is forced on through `HKCU\Software\Unity Technologies\Unity
  Editor 5.x` (value names are hashed, so they are matched by prefix) and
  restored afterwards — otherwise `CompileControl` leaves batchmode running
  `-executeMethod` against stale assemblies.
- The «latest player» pointer is an **NTFS junction** (`mklink /J`), which needs
  no admin rights. There is no adjacent content junction or catalog.
- A `Temp/UnityLockfile` left by a batch run that exited non-zero is cleared
  automatically: the file is only treated as a live editor when `Unity.exe` is
  actually in the process table. Do not read the file alone as proof.
- Publication retries the staging→`v<version>` rename: Windows refuses to
  rename a directory while any file under it is open, and a `tail -f` on
  `unity-build.log` is enough to lose a finished build.
- The pre-build bug snapshot uses the Windows-provided `curl.exe`/Schannel
  directly against the HTTPS production API, without an HTTP proxy. Certificate
  verification is mandatory; never add `-k`/`--insecure`. The build token is
  discovered from the private repository skill first and the per-user config
  second, so a fresh trusted checkout needs no manual token setup.

It builds the macOS development player into
`~/hex-girls/Releases/v<version>/` on the internal disk, next to
`BUILD_REPORT.md`, `build-manifest.json`, and the Unity log. The report lists
the Git commits since the last successful scripted build, every dirty path that
was also compiled, and a start-of-build server API snapshot: reports ready for
testing in this exact build, late-ready reports that did not make the snapshot,
and open/in-progress work. `--dry-run` prints the same plan without launching
Unity or changing the version; `--release` makes a non-development player.
After publication, `~/hex-girls/HexLive.app` is atomically retargeted to the
latest versioned Player; older releases and their reports remain untouched.

The script must refuse to run while `Temp/UnityLockfile` exists. Do not remove a
live lock or launch a second Editor. Player builds deliberately do not run
Addressables or atomic content builds, do not require content output, and do not
create `HexLiveContent` links. The release manifest records the Asset API
contract instead: endpoint from `-hexlive-assets` or the ws/wss server address,
no catalog in Player, and no bundles beside Player. Icons are entries of their
owning object bundles (§152.2), never a shared icon payload.

After `BuildPipeline` succeeds, the command-line entry point explicitly runs
the idempotent version finalizer instead of trusting Unity 6 to rediscover the
postprocess half of the combined pre/post callback. The Python publisher then
refuses success while a pending version remains or any start-snapshot bug lacks
`readyForTestInVersion`. It also performs strict deep signature verification:
development builds with Unity/FMOD nested-signature drift are re-signed ad-hoc
and verified again; `--release` never replaces a distribution signature and
fails publication instead.

`CompileControl` normally keeps both Unity Auto Refresh prefs at zero and holds
`AssetDatabase.DisallowAutoRefresh()`. Before the separate batchmode process,
the script backs up `kAutoRefreshMode` and `kAutoRefresh`, temporarily sets both
to one so Unity must import and compile current sources before `-executeMethod`,
then restores their exact previous values in `finally`. The backup lives in
`Library/HexLiveBuildAutoRefreshBackup.json`; a later run restores it first if
the previous process was interrupted. Never publish a build from stale Editor
assemblies merely because the interactive Editor normally compiles manually.

## Generating tool / weapon models (axe, knife, pickaxe, hammer, spear…)

**Follow `TOOL_GENERATION_SPEC.md` exactly.** Every tool must be produced the
same way so they stay consistent.

**⭐ KEY RULE: generate a HIGH-poly textured mesh, THEN decimate it. Never try to
make an AI output "low-poly" directly** — it produces mush. The flow is:

1. **flat-shaded faceted IMAGE** (fal.ai flux, our low-poly prompt) →
2. **high-poly textured GLB** (Artificial Studio `trellis-2` image-to-3D, ~490 K tris — keeps facets) →
3. **fix WebP textures** in the GLB (glTFast can't read `EXT_texture_webp`) →
4. **import** to Unity → **decimate** (UnityMeshSimplifier, ~20-30 K tris) →
5. **reorient/pivot** to the conventions (handle +Y, grip base at pivot, working edge +Z) — do NOT scale to a size; `ObjectFit` normalizes hand+ground size at runtime (tools → `0.216×HexRadius`) →
6. **flat URP/Lit material** + extracted albedo →
7. **prefab** at `Resources/HexLive/Objects/<id>.prefab` (hand + ground load it automatically; back up any old `.fbx`) + the hand pose in the tool's `GearConfig` asset («Хват в руке», tuned via AxeChopTest → Save).

Shipped tools: `tool.axe_stone`, `tool.knife`, `tool.pickaxe_stone`. Any `tool.*`
in hand auto-gets the armed idle/walk clips; axe/pickaxe also get the Chop
animation. Tune the in-hand pose in the `AxeChopTest` scene (`toolId` field → Play → Save).

When Unity is NOT running, steps 4-8 collapse into headless Blender and the
model ships as a plain `.glb` in `Resources/HexLive/Objects/<id>.glb` — see
**`TOOL_GENERATION_SPEC.md` §3b** (`tool.machete`, `tool.bottle`, `tool.saw`,
`item.bandage` went that way).

## Heeled shoes — the raised heel

**Follow `HEEL_POSE_SPEC.md`.** A heeled shoe in DAZ ships a foot POSE next to
the mesh (heel up, toes bent back) and the shoe is modelled around it; Unity has
no equivalent, so without it the girl stands flat inside the shoe and her foot
pokes through the sole. It is two rotations and a lift, carried in the drop
manifest as `heelPose` and applied by `BodyBones.LateUpdate` — **a new pair of
heels needs no C# at all**. The pose numbers come out of the product's own
`*FootPose*.duf`; the rotation AXIS is data, not a constant, because an FBX
import can permute a bone's local axes — verify in play before believing it.

## Generating inventory ICONS (the pictures in the item list)

**Follow `ICON_GENERATION_SPEC.md`** — a different pipeline from the models
above: no AI at all, just a fixed ¾ Cycles camera over the real game mesh, so
every icon in the list matches. `Tools/unity_mesh_to_obj.py` (garment `.mesh` →
OBJ) + `Tools/render_item_icon.py` (OBJ **or** GLB → 512×512 sprite, and
`--install <itemId>` writes the `.meta` too). **First check whether a finished
icon already exists in `/Volumes/ORICO/molly_copy/Assets/Wear/<item>/` — the
whole garment set came from there.**

## Conventions

### Carryable resources, items and tools

Before adding or fixing any `resource.*` / `item.*` / `tool.*` prop — its model,
its size on the ground and in the hand, or its inventory icon — read and follow
`.agents/skills/hexlive-resource-authoring/SKILL.md`. A carryable thing is six
artefacts (id, catalog entry, tuning asset, model, `ObjectFit` size, icon **plus
its Addressables entry**), and every one of them fails silently on its own.

### Furniture art and placement

Before changing a furniture Blender source/FBX, pivot, axes, footprint, wall
alignment, six-way rotation, construction hierarchy, or test-versus-production
placement, read and follow
`.agents/skills/hexlive-furniture-authoring/SKILL.md`. Normalize crooked axes in
the source/exported asset; never add a per-model runtime angle. Placement lives
in committed simulation/blueprint data, and fixtures must use the same factory
and loading path as the ordinary game.

- **Desktop Player display contract:** every macOS/desktop Player launch must
  request `1920×1080` with `FullScreenMode.ExclusiveFullScreen` before the first
  scene renders. Keep `ProjectSettings` defaults at the same Full HD values,
  with macOS Retina support disabled; do not rely on Unity's persisted
  resolution from an older build.
- Art style is **flat low-poly / faceted / cartoon** — no noise/procedural textures.
- **Localization (spec §58): never author strings in C#.** All player-facing
  strings are I2 Localization terms in `Assets/Resources/I2Languages.asset`
  (EN + RU columns); `Loc.cs` is only a facade over `LocalizationManager` and
  must stay table-free. New string = new term in the asset, read via
  `Loc.Get("area.key")`.
- Unity work goes through the UnityMCP bridge; it drops on domain reload / when the
  editor is unfocused — re-pin the instance and retry. Guard mutations with
  `if (Application.productName != "HexLive") return;` (a second project may share the bridge).
- **Codex only:** never launch Unity, Unity batchmode, `BuildPipeline`, or a
  command-line project/assembly build while the user's Unity Editor is open.
  The user explicitly permits checking whether Unity Editor is closed before
  an authorized build (2026-09-08). Check process presence without printing
  process arguments or secrets; if no Editor is running, proceed without
  repeatedly asking for confirmation. A failed process check is not proof
  that Unity is closed. Never close their Editor or start a second Editor
  automatically. If it is open, use the single-owner UnityMCP workflow or
  ask the user to close it before building. This restriction does not apply to Claude.
- **Never `EditorUtility.DisplayDialog` for a result — log it.** A modal box owns
  Unity's main thread, and the bridge runs on that thread, so an "OK" nobody is
  there to click freezes every command until a human comes back. Menu items on the
  automated path (SimData export, tuning validation) log instead. The exception is a
  genuine confirmation before something destructive (`Reset Values From Defaults`),
  which must stay modal and must stay off the automated path.
- The bridge caps a command at **30 s**. Anything longer — a compile, the wear
  extraction, a paint-map rebake — reports "Command processing timed out" and
  *finishes anyway*. Never treat that message as failure: poll for the artefact the
  command was supposed to produce.

### Wardrobe — ⭐ read `WARDROBE_SPEC.md` before touching any of it

**`WARDROBE_SPEC.md` (repo root) is the whole pipeline**: the flow end to end,
the WardrobeTest panel the human reviews in, the comment loop, where every file
lives, and — most of the value — a list of the traps that already broke things
silently (welds, colourways, heel poses, textures, the bridge, memory). Five
drops and ~260 garments went through it; every warning in it is a bug that
shipped once.

The pipeline lives in `Tools/wardrobe` (see also `HEEL_POSE_SPEC.md` and
`ICON_GENERATION_SPEC.md`). Three mistakes cost real time and are worth not
repeating:

- **A garment is identified by its DAZ MESH KEY, never by its name.**
  `armor.leather` and `S3D_DdlSlc_Top` are the same top; matching on the display
  name finds nothing, or worse, finds the wrong piece. Tripped over three times.
- **Verify a rebuild by CONTENT, not by file timestamps.** Unity rewrites a mesh
  asset even when nothing about it changed, so "the file is newer" proves only
  that the menu ran. Compare what is inside — vertex count, channel layout, the
  actual bytes — or the report will be confidently wrong.
- **Refresh assets BEFORE running the extractor.** Otherwise it faithfully builds
  prefabs from a stale FBX import and everything looks like it worked.

Two more that read as bugs and are not: `blender -b` is forbidden only for the
MCP bridge (the add-on needs an event loop) — for a plain script it is the
normal mode, which is what `ICON_GENERATION_SPEC.md` uses; and a `.duf` that
keys the foot rotations to zero is a RESET, not a pose (`HEEL_POSE_SPEC.md` §2).

**A DAZ modal box stalls the whole run**, because DazScript executes on the Qt
main thread the dialog owns — one missing texture and the script server answers
STUDIO_BUSY until a human clicks OK. `Tools/wardrobe/daz_dialog_watchdog.ps1`
(wired into the `dress` stage via `wardrobe/watchdog.py`) closes the boxes it
positively recognises, through Win32, with no screenshots and no moving the
mouse. Two things keep it honest and must stay: a **deny list that overrides
the allow list** — DAZ also asks "overwrite this file?" and we write FBX files —
and a **log of every dismissal**, or missing content stops being visible and
resurfaces as white shoes. Note DAZ is Qt, so its buttons have no window of
their own: the dialog is closed with `WM_CLOSE` (what the X does), not by
clicking OK. It relieves the stall; it does not install the missing content.

## ⭐ ALL SOUND GOES THROUGH FMOD — and through BOTH of its halves

**Never** add `AudioSource` / `AudioClip` / `PlayOneShot` — Unity audio is
disabled in this project (that is also why uLipSync needed the
`overrideSampleRate` patch, spec §67.7). Every sound plays through FMOD.

FMOD lives here in **two halves, and they must be kept in sync by hand**:

| | Runtime (what the game actually plays) | FMOD Studio project |
|---|---|---|
| Where | `Assets/StreamingAssets/HexLive/Sfx/**` | `FMODStudio/HexLive/` (+ built `Build/Desktop/Master.bank`) |
| How | `FmodSfx` → **Studio events** (`CreateInstance`/`start`); Core API from files is the fallback, and the only path for voices | events 1:1 by `FmodSfx.Sfx.*` id, MultiSound playlists, spatialiser |
| Banks | **LOADED** — integration upgraded to **2.03.14**, `BankLoadType: All`, banks staged in `Assets/StreamingAssets/FMODBanks` | source of truth for volume/effects/distances |

**Mix and tune in FMOD Studio, not in code** (§67.12). Playback goes through
Studio events (`event:/SFX|Ambience|Voices/<id>`), so a fader you move in
Studio is what the game plays — and Live Update is on, so you can attach to a
running game and tune live. `FmodSfx.Defs` volumes are now only the FALLBACK
path used when an event is missing (unbuilt bank / brand-new id). Voices stay
on the Core API on purpose: the §67.7 lipsync needs the concrete file and
playback position, which an event hides.

**Music is the third branch (spec §70).** `MusicDirector` + the music section of
`FmodSfx`: files in `Assets/StreamingAssets/HexLive/Music/<id>.(ogg|mp3|wav)`,
played as a Core-API **stream** — a 5-minute track can't be a sample, and it has
to sound in the MAIN MENU, i.e. before the world (and `Prewarm`) exists. **New
track = new file, no code and no Studio rebuild** (`menu_*` = menu playlist,
everything else = in-game). It is still mixed by Studio: the channel plays into
a `HexLiveMusic` group parented to the Studio master bus, so the master fader
and the Game-view Mute button apply. Volumes/gaps/fades are the constants at the
top of `MusicDirector` — the one place music is tuned.

Version numbers read as decimal-in-hex: `FMOD.VERSION.number` `0x00020314` =
2.03.14. FMOD Studio (the app) and FMOD for Unity (the package in
`Assets/Plugins/FMOD`) are **separate downloads** — check the package, not the
app, before blaming a version mismatch.

**«Only the voices are audible» = the Game view «Mute Audio» button is ON.**
It mutes the FMOD Studio master BUS, so every *event* goes silent, while raw
Core-API channels (the §67.6 voice lines) keep playing — an exact split that
looks like a code bug and is not one. Check `EditorUtility.audioMasterMute`
first. Two more traps found the hard way while chasing it:
- After swapping the integration you MUST fully quit Unity — native plugins are
  never unloaded, so the old library stays resident (`ERR_HEADER_MISMATCH`).
- Do NOT touch `RuntimeManager` from edit mode (an MCP probe counts): it logs
  «RuntimeManager accessed outside of runtime» and leaves a zombie manager whose
  `Update` never runs in the following play session — events then start and die
  at timeline 0 with no errors, which looks exactly like a broken bank. Verify
  suspicions in a FRESH play session, or against the standalone bank probe.

### ⭐ A NEW VOICE LINE NEEDS NO FMOD WORK AT ALL — AND NO UNITY EITHER

**Do not touch FMOD Studio when adding hexkufa lines.** Voices never play
through Studio events: `FmodSfx.EventPathFor` hard-forces the event path off for
every id starting with `voice_` (the §67.7 lipsync samples the `.vis` sidecar
of the concrete file and needs the channel position as its clock — an event
hides both) and reads the WAV straight off disk via the Core API, discovering
it by scanning `Sfx/Voices/**` at load. So the whole flow is:

```bash
# 1. add the group to HEXKUFA_LANGUAGE.md §7 (and any new word to §3)
python3 _ArtSource/Voice/extract_lines.py               # doc -> hexkufa_lines.json
python3 _ArtSource/Voice/generate_voices.py --groups <id>   # -> 5 voices x 3 wavs + .vis
```

That is the whole job — the line is audible AND lip-synced on the next Play.
`--dry-run` plans without spending API calls; `STILL CAPPED` in the output means
the take hit the 4.2 s cap and was **cut mid-word** — shorten the line in the
doc and re-run with `--force`, do not ship it.

**Lipsync is baked, not analyzed (spec §67.7 + `HEXKUFA_LANGUAGE.md` §9.2).**
Next to every WAV lives a `.vis` viseme timeline; `generate_voices.py` bakes it
automatically (known text → g2p → Viterbi alignment over the audio). Commit the
`.vis` + its `.meta` together with the WAV. If a WAV was regenerated any other
way, rebake: `python3 _ArtSource/Voice/bake_lipsync.py <file-or-dir>`
(incremental; `--force` = whole bank, ~a minute). A stale sidecar is safe but
mute: the runtime compares lengths and logs `stale .vis … rebake voices` once.
An `align не сошёлся` warning means the filename does not match a catalog line —
fix the name, don't ship the acoustic fallback. Articulation tuning lives ONLY
in `bake_lipsync.py` knobs (`_ALIGN_GAIN`, duration caps) + rebake — never in C#.

### The Studio scripts are a rebuild, not a sync — reach for them rarely

`FMODStudio/Scripts/`: `populate_events.js` rebuilds sfx/ambience events (scans
`Sfx` **flat**), `sync_voices.js` tops up the voice events (scans
`Sfx/Voices/<char>/` **recursively**). Three traps, all paid for the hard way:

- **`populate_events.js` deletes events it did not make.** Run order is always
  `populate_events` → `sync_voices`, never the reverse.
- **`sync_voices.js` used to purge all 252 voice events and 756 assets on every
  run** — minutes of work and ~1000 rewritten files in git for nothing. It is
  incremental now (existing events are kept); full rebuild lives behind the
  `PURGE_ALL` constant at the top of the script and is only for a schema change
  (spatialiser, distances, playlist shape).
- **Paths come from the OPENED PROJECT, never hardcoded.** `sync_voices.js`
  used to point at `/Volumes/ORICO/HexLive` literally, so running it from a git
  worktree silently rebuilt the bank from ANOTHER checkout's files: freshly
  generated lines were missing and the fifth voice (`kshishtof`) had no events
  at all. It now derives the repo root from `studio.project.filePath`.

Run them headless — and note the gotcha:

```bash
script -q /dev/null "/Applications/FMOD Studio.app/Contents/MacOS/fmodstudiocl" \
  -script "$PWD/FMODStudio/Scripts/sync_voices.js" "$PWD/FMODStudio/HexLive/HexLive.fspro"
script -q /dev/null "/Applications/FMOD Studio.app/Contents/MacOS/fmodstudiocl" \
  -build "$PWD/FMODStudio/HexLive/HexLive.fspro"
```

`fmodstudiocl` **must** run under a pseudo-tty (`script -q /dev/null …`) —
without one it dies with the cryptic «The files a, tty do not exist». It also
writes its own log (`FMODStudio/Scripts/sync_voices.log`) **under the root it
resolved**, so if a run seems to have done nothing, check whose log you are
reading before re-running. A first build may print transient `FSBank error (7)`
lines: delete `Build/Desktop/*.bank` and rebuild — a clean build must end with
0 errors.

## Blender through MCP — it must be RUNNING, with a GUI

The `blender` MCP server ([ahujasid/blender-mcp](https://github.com/ahujasid/blender-mcp))
is only a pipe: it talks to an **add-on living inside a running Blender** over TCP
`localhost:9876`. No Blender on that port = every tool fails. Starting Blender is the
agent's job, not the user's.

**⭐ KEY RULE: `blender --background` (`-b`) does NOT work — ever.** The add-on
dispatches commands onto the main thread via `bpy.app.timers`, which never tick without
an event loop, so it refuses outright:

```
BlenderMCP: cannot start server in background mode (blender -b) - commands would never execute
```

There is no headless path on Windows (`xvfb-run` is Linux-only). A real window must open.

Launch it and wait for the port — Windows box, Blender 5.2.0 LTS is portable at
`%LOCALAPPDATA%\Programs\Blender` (NOT Program Files — it is a no-admin portable install):

```powershell
$root = "$env:LOCALAPPDATA\Programs\Blender"
Start-Process "$root\blender.exe" -ArgumentList "--python","`"$root\start_mcp_server.py`""
# then poll until New-Object Net.Sockets.TcpClient can Connect("localhost",9876) — ~3 s
```

`start_mcp_server.py` sits next to `blender.exe` and just runs
`bpy.ops.blendermcp.start_server()` off a 2-second timer (the operator needs a ready
context). Launching plain `blender.exe` starts NO server — then it is
`View3D → N panel → BlenderMCP → Connect to MCP server` by hand.

Two things that save a wasted restart:

- **Order does not matter.** The MCP server retries per call, so starting Blender
  *after* the Claude session is fine — no session restart needed. The startup log line
  «Could not connect to Blender on startup» is expected noise, not a fault.
- The failure is this exact string, and it means only «Blender is not up»:
  `Error getting scene info: Could not connect to Blender. Make sure the Blender addon is running.`

Telemetry is deliberately off (`BLENDER_MCP_DISABLE_TELEMETRY=true` in the server's
`env`) — otherwise the add-on uploads prompts and scene data to a third-party Supabase.
The `user_prompt` argument every tool asks for feeds that; it is inert while the flag is
set, so pass anything short.

## ⭐ «В игре выглядит плохо» — сначала ЗАМЕР, потом гипотеза

Урок целого дня отладки (spec §109.16): один симптом («скользит / крутится /
проваливается») трижды получил правдоподобный, подтверждённый чтением кода и
НЕВЕРНЫЙ диагноз. Каждый круг стоил игроку сборки билда. Порядок, который
работает:

1. **Воспроизвести ТЕКУЩИЙ мир — это значит прочитать ТЕКУЩИЙ СЕЙВ**, а не
   крутить worldgen с тем же сидом (сейв стоит на своём тике, мир уже другой):
   `~/Library/Application Support/DefaultCompany/HexLive/saves/<world-id>/world.dat`
   (`<world-id>` выбран в PlayerPrefs `HexLive.LocalWorldId`, либо берётся
   новейший `world.json`), заголовок v3 32 байта (`HXLV`, version, seed, tick,
   unixSeconds, speed, mode; legacy v2 — 28 байт без mode), дальше
   `WorldSaveSerializer.Read` поверх `PrototypeWorldDefinitionFactory.Create(seed)`.
   Дальше — потиковая печать того, на что жалуются: позиция, угол, желаемый
   угол, статус движения, шаг пути. Форма беды видна за минуту.
2. **Определить СЛОЙ до правки.** Симптом и причина обычно в разных: тело
   двигал сим — врал вид; позу ломал вид — обвинялся аватар. Вторая половина
   кадра берётся через мост в редакторе **на паузе** (`execute_code`):
   `transform.position`, кости (`GetBoneTransform(Hips/LeftFoot)`),
   `GetCurrentAnimatorStateInfo`, `IsInTransition`, веса эффекторов IK. Это
   один вызов и полная картина.
3. **Правка вида без единого кадра проверки — ставка, а не работа.** Снап
   позиции и `CrossFade` в этот день были приняты «по логике» и сломали прыжки
   и позы. Не подтвердил наблюдением — не коммить.
4. **Команду аниматора нельзя ставить в покадровый расчёт без фронта** —
   `CrossFade` каждый кадр = вечный переход с весом ~0.04, аниматор не покидает
   состояние, ретаргет недосмешанной позы роняет тело под пол. Только по ребру
   или переходом в самом контроллере.
5. **Симптом «стало так же» — повод мерить, а не чинить дальше.**

Метрики этого дня, которым место в гейтах: «стоящий вне своего гекса», «смена
тайла у стоящего со сменой высоты», «реверсы направления на NPC-день», «пара
без обоснования», «телепорт без ходьбы». Диагноз архитектурной причины и план
рефакторинга — `POSITIONING_REFACTOR.md`.

## Headless checks (no Unity) — gates, soaks, golden traces

**Start here, not with a scratchpad probe.** The harness used to be rewritten from
scratch every session, and its bugs were reborn with it: wrong checkout, stale
simdata, a hand-copied system list. It is committed now, in two projects that live
OUTSIDE `Assets/` (so they may carry `PackageReference`, which the simulation
assembly may not):

```bash
dotnet test Tests/HexLive.Simulation.Tests
```

Seven gates, none of which a human can hold in their head:

| Gate | Catches |
|---|---|
| `EventWhitelistGate` | a `GameEventTypes` name nothing emits — comas and cooking were silently missing from history and audio for years |
| `TraceEmitLint` | an event `Type` composed at runtime (§63's `"ClothesWashed underwear.bra …"`), which makes the type set infinite and ungreppable |
| `SystemRegistryGate` | a system implemented but never registered (`SharkSystem`), and any reordering — registration order is execution order and is load-bearing |
| `GoalTypeCoverageGate` | a `GoalType` nobody scores and nobody assigns; dead ordinals are declared, not pretended away |
| `HardcodedIdLint` | a NEW hardcoded content id in `Runtime/Systems` (ratchet over `known_hardcoded_ids.txt`) |
| `WireCoverageGate` | a `WorldSnapshot` field the codec forgot — every property is stamped non-default and round-tripped |
| `BalanceParityGate` | a tuning knob missing from `simdata.json`, i.e. the "the inspector dial silently does nothing" trap |

Soaks and trace recording are one binary (`hexsoak` — both are "step and listen",
and splitting them would mean a third copy of the harness):

```bash
dotnet run --project Tests/HexLive.Simulation.Soak -- --seed 12345 --ticks 12000
```

**When something is stuck, ask it directly:**

```bash
dotnet run --project Tests/HexLive.Simulation.Soak -- --seed 12345 --ticks 12000 --explain-stuck 5
```

`StuckDiagnosticSystem` (spec §30.15) watches for the four shapes of "not getting
anywhere" — `IdleWithGoal` (the §102 signature: goal set, no interaction, not
moving), `StepOverrun`, `GoallessCrisis`, `PositionFrozen` — and `--explain-stuck`
prints the flight-recorder tail beside each one, i.e. what that colonist was doing
BEFORE she froze. Watch the `Reason=`; the per-NPC ring (spec §30.14) is what makes
the tail survive, since the colony-wide ring only holds ~11 ticks.

⭐ **Смотри на ХУДШИЙ шаг, не на тик/с** (spec §158.1): отчёт печатает
«худший шаг N мс (тик T), за бюджетом 250 мс: k, дольше секунды: m» и
«связность целиком: n перестроек». Средний тик 10 мс на «Островах» прятал
тики по 2.8 с — именно они рвали связь у игрока. На пути тика нет права на
обход `world.Junctions.Items.Values`/`Tiles.Items.Values`: проходимость
пишется только через `WorldTopology` (гейт `TopologyWriteLint`), кэши на
`TopologyVersion` догоняют журнал через `WorldTopology.CatchUp`, кандидаты
ищутся через `LocalSearch` кольцами тайлов.

`-h` lists the rest. It reports the spec §30.16 metrics: goal churn per NPC-day
(both raw field changes and "dropped one job for another", which is the number
§35.4a means), median/mean goal dwell, plan-failure rate, and **stuck NPC-ticks** —
goal set, no interaction running, not moving, the exact §102 signature that emitted
nothing at all for 2872 consecutive ticks.

**Before and after any refactor, prove behaviour did not move:**

```bash
Tools/golden_trace.sh HEAD --preset scores
```

It builds and runs `<base-ref>` in a throwaway `git worktree` (never `git stash` —
that touches your working state), records the decision trace on fixed seeds from
both sides, and diffs them as text. ⭐ **Float operation order IS behaviour**: the
same sequence is bit-identical, so rewriting `a + b + c` as `a + (b + c)` shows up
as a diff. That is the point — in a world where every roll is a hash of the seed,
that reordering changed the game. A diff means "accept it consciously and write it
into spec.md", never "ignore it". `--preset scores` includes `GoalScored`, i.e. the
exact float output of every scoring block; `decisions` is the cheap one.

### When the committed tools cannot ask your question

Only then write a throwaway probe. Build the simulation assembly first:

```bash
dotnet build HexLive.Simulation.Standalone.csproj
```

**Use `HexLive.Simulation.Standalone.csproj`, not `HexLive.Simulation.csproj`.**
The latter is generated by Unity, is gitignored (`*.csproj`), targets net471, and
does not exist at all in a git worktree or when the editor has never been opened —
the headless path cannot depend on it. The Standalone one is committed, targets
`net9.0`, and compiles exactly the same files the asmdef does. Keep it free of
`PackageReference`: anything added there compiles here and breaks in Unity.

The DLL lands in `Build/dotnet/bin/HexLive.Simulation/Debug/net9.0/` **inside the
checkout you built from**. Then make a temporary console probe under `/private/tmp`
targeting `net9.0` and reference it:

```xml
<Reference Include="HexLive.Simulation">
  <HintPath>$(RepoRoot)/Build/dotnet/bin/HexLive.Simulation/Debug/net9.0/HexLive.Simulation.dll</HintPath>
</Reference>
```

⚠️ Resolve `$(RepoRoot)` to **the checkout you are actually working in** — pass it
on the command line, or paste the absolute path. When working in a git worktree
(`.claude/worktrees/<name>/`), that is the worktree, NOT `/Volumes/ORICO/HexLive`.
Pointing a probe at the main checkout while editing a worktree measures the wrong
code and reads as "my change did nothing"; the same goes for `SimData/simdata.json`
below.

Order of magnitude, so you know what a soak costs: a full 24-system engine on the
prototype world (285 tiles, ~14 k junctions, 4 NPCs) runs **~1.2-1.5 ms/tick** —
about 170-210× faster than the 4 Hz the game plays at.

Use `WorldBootstrapDefinition` + `WorldStateFactory().Create(def)` to build a
small hand-authored world, then create a `SimulationEngine` and register the
default systems with `SimulationSystemRegistry.RegisterDefaults(engine)` — the
same call the Unity runner makes, so a probe can never drift from the game. For a
narrower probe, register by hand instead: `PathfindingSystem`, `MovementSystem`,
`ExecutionSystem`, `PerceptionSystem`, `DecisionSystem`, `PlanningSystem` first,
adding slow/world systems only when the probe needs them. Step with
`engine.Step()`. Read traces from `world.Events.Items` (not `world.Events`
directly); each event carries a monotonic `Seq`, so track a watermark rather than
comparing object identity — the ring trims at 2048 and with verbose trace on that
is roughly every 11 ticks.

This works for focused checks like “without a knife, does a hungry NPC craft a
tool before planning coconut food/water?” and avoids re-discovering the Unity
runtime path every session. `Tests/HexLive.Simulation.Soak/Program.cs` already
does the bootstrap-and-drain dance correctly — copy from there rather than from
memory.

⚠️ **A soak answers a different question than an arena.** «How many times in eight
days» is not «why is there none right now» (§102.7): the arena answers in twenty
seconds, the soak takes half an hour and misses. Measure «did it reach the act»,
not «was there an opportunity».

**Sim data for headless runs (spec §59.3 — MANDATORY):** the tuned
ScriptableObject catalogs (mobs, gear, world objects, recipes) are exported to
`SimData/simdata.json` via the Unity menu **HexLive ▸ Export Sim Data (JSON)**.
A probe's FIRST line MUST be
`HexLive.Simulation.Content.SimDataFile.Require("<your-checkout>/SimData/simdata.json")`
— again, the checkout you are working in, worktree included —
and it THROWS if the export is missing. Running on code defaults is FORBIDDEN
(silent drift between the tuned game and the harness). Re-export after tuning
assets; the file is committed, so it is normally present. A `net471` temp console will fail on this machine
because ordinary .NET Framework reference assemblies are missing, even though
the Unity `.csproj` itself builds.

## Running the world on a server (local ⇄ remote is ONE switch)

The colony can live in a headless process and have Unity watch it. Local play is
untouched and stays the default — **the game must always run with no server
anywhere**, so this is two backends behind one seam, not a migration.

```bash
dotnet run --project Server/HexLive.Server -- --port 5123
```

Then start the game with `-hexlive-server ws://localhost:5123/watch`. `--help`
lists the rest (`--seed`, `--save`, `--autosave`, `--simdata`, `--debug-details`).

### Updating/restarting the local server without losing password or save

The server state is not just the executable. The save file, admin account,
player token, persistent player-to-character assignments (`hexlive-players.json`),
MCP token, simdata export, and log must travel as one directory.
For the local player machine the canonical runtime directory is:

```bash
/Users/shtolyan/hex-girls/server
```

Use absolute paths when replacing the server process:

```bash
cd /Users/shtolyan/hex-girls/server
./bin/HexLive.Server \
  --port 5123 \
  --save /Users/shtolyan/hex-girls/server/bigisland-30430.sav \
  --simdata /Users/shtolyan/hex-girls/server/simdata.json \
  --control
```

Never launch a player-facing server with a save in `/tmp`, from a build output
directory, or from an arbitrary current working directory. The admin password
hash lives beside the save as `hexlive-admin.txt`; changing the save directory
changes the admin account file. A server update must therefore:

1. Find the currently running command (`lsof -nP -iTCP:5123 -sTCP:LISTEN` and
   `ps -p <pid> -o command`).
2. Preserve the same `--save` directory unless the player explicitly asks for a
   different world.
3. Verify the target directory contains the expected `hexlive-admin.txt` and
   `simdata.json`.
4. Start the replacement with absolute `--save` and `--simdata` paths.
5. Read the first server log lines and confirm they say:
   `save file <expected path>` and `admin account <same directory>/hexlive-admin.txt`.

The binary must also protect the operator: if the target save already exists,
startup reads its header and continues that save's seed and world mode. Command
line `--seed`/`--mode` are only fresh-world defaults. If an existing save cannot
be identified safely, the server must refuse to start rather than creating a new
world that autosave could write over the old one.

Presentation talks ONLY to `ISimulationSource` (`UnityPresentation/Bootstrap/`).
Behind it sits an `ISimulationBackend`:

| | |
|---|---|
| `LocalEngineBackend` | the default — our own engine, exactly as before |
| `LoopbackBackend` | local engine, but read back through the wire codec (`-hexlive-loopback`) |
| `RemoteSocketBackend` | WebSocket to the server |

**Do not reach for `SimulationRunnerBehaviour.Engine` in shipped presentation.**
It is null in every non-local mode and exists only for the dev/test scenes
(AmputationTest, BedBuildTest, SwimTest, WolfFightTest), which mutate throwaway
worlds directly. Everything else goes through the snapshot, `DrainEvents`, or
`TryGetObjectDefinition`.

**The wire lives in the SIMULATION assembly** (`Simulation/Wire/`) so both ends
link the same code and the format cannot fork. Two rules follow:
- Add a field to `WorldSnapshot` ⇒ add it to `WorldSnapshotCodec`. A forgotten
  field is silent (a colonist just renders without her wound), so it is guarded
  by a reflection coverage test that round-trips every property — and by
  `-hexlive-loopback`, which surfaces it on the first Play with no server needed.
- The ~14 000 junctions never travel. The client rebuilds them from the seed (the
  same trick loading a save already uses) and the handshake carries a topology
  checksum; a mismatch aborts the connection rather than rendering a subtly
  different island. The handshake also ships the server's `simdata.json`, so the
  client never relies on its own assets matching.

**Events are filtered by type before they leave the server.** The simulation emits
~244 types and ~206 events a tick, but the client reads exactly one whitelist —
`Simulation/Runtime/GameEventTypes.cs` — for the colony history, sound and speech.
Measured over 4 000 ticks: **823 713 events (90 MB) in, 281 (26 KB) out — 0.03%,
a 3 571× cut**, taking the event channel from ~92 KB/s to ~0.03 KB/s. What survives
is what you would expect (`TreeChopped`, `TalkStarted`, `RelationshipChanged`,
`FireLit`); what goes is `PerceivedObject` (695 k of the 824 k on its own) and
`GoalScored`.

Two rules when touching that list:
- **Filter by TYPE only — never rewrite `Message`.** It is parsed, not just shown:
  `SoundManager.TryMobPosFromMessage` digs the wolf id out of `"Dog={id} …"`, the
  ONLY position source for `DogAggro`/`DogKilled` (system events, no `EntityId`),
  and `GameHistoryFormatter` parses `Kind=`, `Topic=`, `Cause=[…]`, `->NPC`.
- **Every name in the list must be a type something actually emits.** Two were not,
  for a long time: the list waited for `Collapsed` while the coma path emits
  `FellAsleepExhausted`/`FaintedBloodLoss`, and for `MeatCooked` while the fire
  emits `MeatRoasted` — so comas and cooking never reached history or audio at all,
  in local play too. Re-run the whitelist drift probe after editing.

**The snapshot frame carries only what moves.** Neither tiles (285) nor junctions
(~14 000) travel: both are pure worldgen output and the receiver regenerates them
from the seed, which the handshake's topology checksum proves matched. Object
records drop their build-site block (twelve ints, zero on ~215 of 218 objects)
behind a flags byte, and definition ids travel as an index into a table both ends
derive from the same content catalog (`Wire/DefinitionIdTable.cs`).

⚠️ **`DefinitionIdTable.Build(world.Content)` must run before the first frame is
encoded or decoded** — on the server after worldgen, on the client after it
applies the server's simdata. Skip it and objects still arrive, just nameless, and
the renderer draws grey spheres: a silent failure, so assert it.

**Frames are deltas.** One full frame on connect, then only what moved:
`Wire/SnapshotDelta.cs` encodes, `Wire/SnapshotDeltaReader.cs` applies. Measured
end to end: **202 → 10 KB/s** per viewer.

The one rule that makes it safe: **the delta encoder never looks at a field.** It
asks the ordinary record writer — the same one a keyframe uses — for an entity's
bytes and compares them to what it sent last time. Equal means unchanged. So a
field can only be forgotten in one place, and that place is already watched by the
reflection coverage gate. A hand-written "did this change?" check would be a second
place to forget it, and a forgotten field in a delta does not corrupt one frame the
way it would in a full snapshot — it corrupts the mirror until the next keyframe.

Consequences worth knowing before touching it:
- **Entity lists are in ascending-id order, both ends, always** (`SortById` in the
  exporter). Without it a dictionary reshuffle would resend the world and any
  checksum would flap for a non-bug.
- **The roster comes from the snapshot**, as a set difference against the baseline
  — never from `ObjectSpawned`/`ObjectDespawned` events. That ring trims every ~11
  ticks, and a missed removal is a ghost object that never goes away.
- **A delta whose baseline is not the mirror's tick is refused, not applied.** The
  client answers with `RequestKeyframe`. Same on queue overflow: drop everything
  and restart from a full frame rather than losing a link in the chain.
- Verified by a soak that, every tick, applies the delta to a mirror and compares
  the FULL encoding of both, byte for byte — across three seeds, with a chaos mode
  that despawns objects and mangles needs so the run covers what organic play might
  not reach in 800 ticks.

NPCs are now most of a delta frame: they are replaced whole, and a walking
colonist's position changes every tick. Splitting the NPC record into field groups
is the next lever.

**Hardware, measured, not estimated:** ~40 MB RSS idle, ~46 MB with a viewer
attached, ~4% of one core (~8-9 ms per tick, 4 ticks/s). A 1-vCPU / 2 GB VPS
hosts this many times over. Note the per-tick cost is ~8 ms on the server but
~1.2 ms in a back-to-back headless probe — same code, cold cache versus hot.
Plan with the 8 ms figure; the probe number is not what a 4 Hz world experiences.

### Admin panel

`http://<host>:<port>/admin` — pause/resume, operator fast-forward, start a new
world, save-and-shutdown, password and recovery email.

- **First run prints a one-time password to the console.** There is no default
  password to leak or forget to change; the panel then nags until it is replaced
  and a recovery email is set.
- Credentials live in `hexlive-admin.txt` beside the save (mode 0600): PBKDF2-SHA256,
  210k iterations, per-account salt. The password itself is never stored.
- Forgot-password mails a single-use 30-minute link via SMTP
  (`HEXLIVE_SMTP_HOST/_PORT/_USER/_PASSWORD/_FROM`). **With no SMTP configured
  the link is printed to the server console instead** — deliberate, since console
  access already means operator access, and better than a reset that silently
  does nothing.
- ⚠️ **Plain HTTP sends the password in the clear.** The panel says so on the page
  whenever it is reached from anywhere but localhost. Put it behind Caddy/nginx
  with TLS before exposing it.

**Fast-forward is operator-only.** A viewer asking for 50× is clamped to 1× by
`WorldHost.SetSpeed`; the panel goes through `SetSpeedAsOperator`, which is not
clamped (capped at 200×). A shared world wound forward burns colony days for
everyone watching and multiplies every viewer's stream — so the Unity speed bar
greys out >1× on a remote link, and the server enforces it regardless.

## ⭐ Обновление production-сервера 62.146.235.120

Это канонический runbook для уже развёрнутого VPS. Любой агент обновляет его
одинаково; импровизированный `dotnet run` в `/root`, новый каталог с сейвом или
ручной запуск вне systemd — не обновление production.

### Доступ и неизменяемый runtime-контракт

- SSH-алиас: `hexlive-server` (`root@62.146.235.120`). В общей среде агентов он
  записан в `~/.ssh/config`, отдельный ключ лежит в
  `~/.ssh/hexlive_62_146_235_120_ed25519` с mode 0600. Проверка всегда
  беспарольная: `ssh -o BatchMode=yes hexlive-server true`. Если она не прошла,
  остановиться и попросить игрока восстановить ключ; не искать и не сохранять
  root-пароль в репозитории, shell history, логе или чате.
- systemd unit: `hexlive.service`; Kestrel origin: `127.0.0.1:5123`;
  публичный TLS viewer: `wss://vmi3529459.contaboserver.net/watch`;
  Asset API: `https://vmi3529459.contaboserver.net/api/assets/v1`. Caddy
  (`caddy.service`, canonical source `Server/Caddyfile`, deployed path
  `/etc/caddy/Caddyfile`) завершает TLS и проксирует весь host в origin.
- Версионные бинарники: `/opt/hexlive/releases/<full-git-sha>`; активная версия —
  атомарный symlink `/opt/hexlive/current`.
- **Вся постоянная жизнь сервера** находится в `/var/lib/hexlive`: `world.sav`,
  `simdata.json`, `hexlive-admin.txt`, `hexlive-player.txt` и
  `hexlive-players.json`. Никогда не удалять каталог, не менять `--save`, не
  создавать новый admin account/token и не подменять seed/mode при обновлении.
  Локальная копия player token для клиента лежит в
  `~/.config/hexlive/servers/62.146.235.120/player-token` с mode 0600; её
  содержимое не печатать.

### 1. Preflight

Разворачивается **полный SHA коммита**, а не незафиксированное рабочее дерево.
Не делать `git pull`, commit или reset без отдельной просьбы игрока. Живые
изменения `BUGS.json` сохранять и не включать в релиз. Сначала записать SHA и
состояние production:

```bash
git rev-parse HEAD
git status --short
ssh -o BatchMode=yes hexlive-server \
  'systemctl is-active hexlive.service; readlink -f /opt/hexlive/current; \
   stat -c "%a %U:%G %n" /var/lib/hexlive /var/lib/hexlive/world.sav \
   /var/lib/hexlive/simdata.json /var/lib/hexlive/hexlive-admin.txt \
   /var/lib/hexlive/hexlive-player.txt; curl --fail --silent http://127.0.0.1:5123/'
```

Ожидаются `active`, release под `/opt/hexlive/releases/`, каталог state 0750 и
секреты/сейв 0600 пользователя `hexlive`. До сборки соблюсти общее правило выше:
Unity Editor должен быть закрыт. Собрать из чистого временного `git archive`,
чтобы не компилировать случайные dirty-файлы и не писать build output в рабочую
копию. В одной shell-сессии:

```bash
deploy_sha="$(git rev-parse HEAD)"
deploy_tmp="$(mktemp -d /private/tmp/hexlive-server.XXXXXX)"
trap 'rm -rf "$deploy_tmp"' EXIT
mkdir "$deploy_tmp/src"
git archive -o "$deploy_tmp/source.tar" "$deploy_sha" \
  HexLive.Simulation.Standalone.csproj Server \
  Tests/HexLive.Server.Tests Tests/HexLive.Simulation.Tests \
  Assets/HexLive/Simulation \
  Assets/HexLive/UnityPresentation/AbuseTest/AbuseTestWorld.cs \
  Assets/HexLive/UnityPresentation/Wearing/PresentationSpeed.cs \
  SimData Spec spec.md Tools
tar -xf "$deploy_tmp/source.tar" -C "$deploy_tmp/src"
cd "$deploy_tmp/src"
```

Обязательные проверки из этого temp checkout:

```bash
dotnet test Tests/HexLive.Server.Tests --configuration Release --nologo
dotnet test Tests/HexLive.Simulation.Tests --configuration Release --nologo
```

Обе должны пройти. Известный красный общий suite не замалчивать: развёртывание
такого коммита допустимо только по явному указанию игрока, с перечислением
падающих тестов в отчёте; серверные тесты не пропускаются никогда.

### 2. Публикация и загрузка

Внутри временного архива выполнить self-contained publish именно для Linux x64:

```bash
dotnet publish Server/HexLive.Server/HexLive.Server.csproj \
  --configuration Release --runtime linux-x64 --self-contained true \
  --nologo --disable-build-servers --output "$deploy_tmp/publish"
install -m 0644 SimData/simdata.json "$deploy_tmp/publish/simdata.json"
deploy_archive="$deploy_tmp/hexlive-server-$deploy_sha.tar.gz"
COPYFILE_DISABLE=1 tar -czf "$deploy_archive" -C "$deploy_tmp/publish" .
deploy_archive_sha="$(shasum -a 256 "$deploy_archive" | awk '{print $1}')"
printf '%s  %s\n' "$deploy_archive_sha" "$deploy_archive"
scp "$deploy_archive" hexlive-server:/tmp/
```

На сервере сверить `$deploy_archive_sha` **до** распаковки; локальные переменные
сами через SSH не переносятся, поэтому передать точные значения аргументами или
вставить уже вычисленные строки — не посылать на remote shell буквальные
placeholders. Распаковать сначала в новый staging-каталог и только готовый
каталог переименовать в `/opt/hexlive/releases/<FULL_SHA>`. Старые releases не
удалять — они являются rollback. Бинарники принадлежат `root:root` и не пишут в
свой release. Скопировать новый `simdata.json` в
`/var/lib/hexlive/simdata.json` как `hexlive:hexlive` mode 0600, предварительно
сохранив rollback-копию текущего.

Перед переключением запомнить точную цель `/opt/hexlive/current` и SHA-256
`hexlive-admin.txt`/`hexlive-player.txt` (сравнивать хеши, не выводить секреты).
Новый symlink создавать рядом и заменять через `mv -Tf`, затем:

```bash
systemctl restart hexlive.service
systemctl is-active hexlive.service
journalctl -u hexlive.service -n 80 --no-pager
```

В журнале не показывать рамки первого запуска с паролем/token. Проверить строки
`save file /var/lib/hexlive/world.sav` и
`admin account /var/lib/hexlive/hexlive-admin.txt`, отсутствие `[fatal]`, а также
что новый `[world] seed ... tick ...` продолжает прежний мир. Если новый процесс
не стал healthy за 30 секунд, **сразу** вернуть прежний symlink и прежний
`simdata.json`, запустить `systemctl restart hexlive.service`, проверить старую
версию и только затем диагностировать новую.

### 3. Проверка снаружи и завершение

```bash
curl --fail --silent --show-error --max-time 10 \
  https://vmi3529459.contaboserver.net/
curl --http1.1 --silent --output /dev/null --write-out '%{http_code}\n' \
  --max-time 3 -H 'Connection: Upgrade' -H 'Upgrade: websocket' \
  -H 'Sec-WebSocket-Version: 13' \
  -H 'Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==' \
  https://vmi3529459.contaboserver.net/watch
curl --fail --silent --show-error --max-time 10 \
  https://vmi3529459.contaboserver.net/api/assets/v1/index/StandaloneOSX/unity6000-content1 \
  --output /dev/null
```

Вторая команда должна напечатать `101`; timeout после upgrade допустим, потому
что WebSocket остаётся открытым. Затем сделать один контролируемый restart и
доказать, что tick продолжился, `world.sav` остался 0600 `hexlive:hexlive`, а
хеши admin account и player token не изменились. Только после этого удалить
точный загруженный файл из `/tmp`; локальный `mktemp` удалить через trap. В
финальном отчёте указать полный deployed SHA, старую и новую цель symlink,
результаты тестов, HTTP/WS-проверок и restart/save-проверки.

`/admin`, player control, viewer и Asset API снаружи используются только через
HTTPS/WSS host выше. Порт 5123 остаётся Kestrel origin и временным legacy-входом
на период миграции клиентов; новый Player нормализует прежний IP-адрес в WSS до
подключения. Не возвращать в release-инструкции публичный `http/ws` endpoint.
