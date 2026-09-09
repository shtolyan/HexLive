# Original save fixture for bug #351

Run with Unity closed, Python 3.12+ (`pgrep` available) and .NET 9:

```sh
python3 Tools/bug351_legacy_export.py \
  --source '/path/to/original/world.dat' \
  --output '/private/tmp/bug351-snapshots'
```

The source must have SHA-256
`d403257bd7965bc172199fdf45f034e7e843fdc2397de58bfd095bd6ee74890f`.
The output directory must be new or empty. The tool reads the source and verifies
its hash again after export. It never writes a save, changes a blob version, or
adds legacy compatibility to the current game.

The wrapper extracts only the simulation, its standalone project and simdata
from fixed commit `2236e4a97a72f125d4cb889a398558666fc533d3`:

```sh
git archive 2236e4a97a72f125d4cb889a398558666fc533d3 \
  Assets/HexLive/Simulation HexLive.Simulation.Standalone.csproj SimData/simdata.json
```

It builds these unmodified historical sources in a temporary directory, then
builds `Program.cs` against that assembly through `HintPath`. There are no added
packages. The temporary source/build files are removed when the tool exits.

The canonical factory reconstructs Feud seed `-12054716`. The canonical v64
`WorldSaveSerializer.Read` consumes the whole original blob at tick `5812`.
The historical `WorldSnapshotExporter` produces three JSON files:

- Saved NPC2 assigned to bed1072 with `Sleep/InProgress` and unconscious/Faint
  state; production displays `FallenIdle` using the shared sleep pose. There
  are no scenario changes or simulation ticks in this baseline.
- Controlled NPC1 on saved bed1071.
- Controlled NPC1 on saved bed1072.

Each controlled case starts with an independent restore. Historical
`SetManualControlCommand` and `StopCommand` release the actor and any existing
bed occupant; historical `BedSleep.TryEnter` prepares the sleep state. No pose
or bed geometry is written manually. These are controlled presentation cases,
not a claim that NPC1 slept on both beds in the original play session.

`manifest.json` records source, reader commit, assembly/serializer/simdata
hashes, outer/blob versions, seed/tick, exact preparation and each JSON hash.
Snapshots use standard public property names and numeric enums. Saved files
and generated JSON are local evidence and must not be committed.

Pass the output directory to the Unity saved-hut fixture with
`-hexlive-bug351-snapshots <output>` and the original file with
`-hexlive-bug351-save <source>`.
