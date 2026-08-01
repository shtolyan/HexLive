# HexLive — agent guide

Survival-colony sim (Unity, URP). The canonical design/behaviour spec is
**`spec.md`** (repo root) — keep it in sync with code (spec-first).

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

## Generating inventory ICONS (the pictures in the item list)

**Follow `ICON_GENERATION_SPEC.md`** — a different pipeline from the models
above: no AI at all, just a fixed ¾ Cycles camera over the real game mesh, so
every icon in the list matches. `Tools/unity_mesh_to_obj.py` (garment `.mesh` →
OBJ) + `Tools/render_item_icon.py` (OBJ **or** GLB → 512×512 sprite, and
`--install <itemId>` writes the `.meta` too). **First check whether a finished
icon already exists in `/Volumes/ORICO/molly_copy/Assets/Wear/<item>/` — the
whole garment set came from there.**

## Conventions

- Art style is **flat low-poly / faceted / cartoon** — no noise/procedural textures.
- **Localization (spec §58): never author strings in C#.** All player-facing
  strings are I2 Localization terms in `Assets/Resources/I2Languages.asset`
  (EN + RU columns); `Loc.cs` is only a facade over `LocalizationManager` and
  must stay table-free. New string = new term in the asset, read via
  `Loc.Get("area.key")`.
- Unity work goes through the UnityMCP bridge; it drops on domain reload / when the
  editor is unfocused — re-pin the instance and retry. Guard mutations with
  `if (Application.productName != "HexLive") return;` (a second project may share the bridge).

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

Scripts (`FMODStudio/Scripts/`): `populate_events.js` rebuilds the sfx/ambience
events (scans `Sfx` **flat**), `sync_voices.js` rebuilds the voice events (scans
`Sfx/Voices/<char>/` **recursively**, purges stale `voice_*` events + assets
first). Run them headless — and note the gotcha:

```bash
script -q /dev/null "/Applications/FMOD Studio.app/Contents/MacOS/fmodstudiocl" \
  -script "$PWD/FMODStudio/Scripts/sync_voices.js" "$PWD/FMODStudio/HexLive/HexLive.fspro"
script -q /dev/null "/Applications/FMOD Studio.app/Contents/MacOS/fmodstudiocl" \
  -build "$PWD/FMODStudio/HexLive/HexLive.fspro"
```

`fmodstudiocl` **must** run under a pseudo-tty (`script -q /dev/null …`) —
without one it dies with the cryptic «The files a, tty do not exist».
A first build may print transient `FSBank error (7)` lines: delete
`Build/Desktop/*.bank` and rebuild — a clean build must end with 0 errors.

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

## Headless simulation probes (no Unity)

For AI/GOAP/simulation checks, do not start Unity just to run ticks. Build the
simulation assembly first:

```bash
dotnet build HexLive.Simulation.csproj --no-restore
```

Then make a temporary console probe under `/private/tmp` targeting `net9.0` and
reference the built DLL directly:

```xml
<Reference Include="HexLive.Simulation">
  <HintPath>/Volumes/ORICO/HexLive/Temp/Bin/Debug/HexLive.Simulation/HexLive.Simulation.dll</HintPath>
</Reference>
```

Use `WorldBootstrapDefinition` + `WorldStateFactory().Create(def)` to build a
small hand-authored world, then create a `SimulationEngine` and register the same
core systems as `SimulationRunnerBehaviour.RegisterDefaultSystems`:
`PathfindingSystem`, `MovementSystem`, `ExecutionSystem`, `PerceptionSystem`,
`DecisionSystem`, `PlanningSystem` first; add slow/world systems only when the
probe needs them. Step with `engine.Step()`. Read traces from
`world.Events.Items` (not `world.Events` directly).

This works for focused checks like “without a knife, does a hungry NPC craft a
tool before planning coconut food/water?” and avoids re-discovering the Unity
runtime path every session.

**Sim data for headless runs (spec §59.3 — MANDATORY):** the tuned
ScriptableObject catalogs (mobs, gear, world objects, recipes) are exported to
`SimData/simdata.json` via the Unity menu **HexLive ▸ Export Sim Data (JSON)**.
A probe's FIRST line MUST be
`HexLive.Simulation.Content.SimDataFile.Require("/Volumes/ORICO/HexLive/SimData/simdata.json")`
— it THROWS if the export is missing. Running on code defaults is FORBIDDEN
(silent drift between the tuned game and the harness). Re-export after tuning
assets; the file is committed, so it is normally present. A `net471` temp console will fail on this machine
because ordinary .NET Framework reference assemblies are missing, even though
the Unity `.csproj` itself builds.
