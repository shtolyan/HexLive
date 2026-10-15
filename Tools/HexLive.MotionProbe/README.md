# Motion diagnostics

This probe builds only a local .NET diagnostic executable, never a Unity
Player or server release. It does not write a save, issue player orders or
deploy anything. Keep captures in ignored `Build/`, outside commits and tracker
attachments; motion data is not a recording of animation or frame rate.

```powershell
$env:DOTNET_ROLL_FORWARD = 'Major' # only needed if .NET 9 runtime is unavailable
dotnet run --project Tools/HexLive.MotionProbe --configuration Release -- watch Build/motion.jsonl
dotnet run --project Tools/HexLive.MotionProbe --configuration Release -- replay Build/captured-world Build/replay.jsonl 1000000001 6000
dotnet run --project Tools/HexLive.MotionProbe --configuration Release -- route Build/captured-world Build/route.jsonl 1000000001 303214,303219,299378,299359,299352
```

`watch` opens the canonical Singapore `/watch` stream anonymously for 20 seconds;
it requires `ControlEnabled=false` and no assigned characters. It sends no
command frames. Output contains IDs, positions, headings, goals and movement
states, not credentials, conversation text or accounts.

`replay` requires an authorized, read-only capture of **the same world's**
`world.sav`, `creation.xml` and `simdata.json` in the supplied directory. It
validates the server HXLS v2 header, seed, mode, byte length and restored tick,
then uses `WorldCreation.Definition`, `WorldStateFactory`,
`WorldSaveSerializer` and the normal simulation registry. Record the printed
save SHA, topology checksum and source commit separately. Local code may differ
from the deployed binary: a replay does not prove live reproduction.

`route` uses the same topology and tuning but places the chosen character at the
first node **in local memory**, then runs only `MovementSystem` for up to 80 ticks.
Nodes must be adjacent and the start must belong to exactly one tile. This holds
the measured route constant while diagnosing movement independently of AI
planning; it does not replay the exact historical character state.

JSONL contains path indices, hop endpoints/tiles, turn and recovery timers.
The companion `.geometry.json` contains the traced nodes and nearby bed, stump
and rack anchors. Large heading changes can be valid new-plan turns: do not
label the summary counter as a bug count. A sleeping live sample cannot confirm
that a walking defect is fixed.
