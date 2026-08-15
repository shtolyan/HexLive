# HexLive balance handbook

This directory documents the game-designer-facing balance workflow. The
canonical behaviour is still `spec.md`; this page answers the operational
question: **where do I turn a number, and what must travel with it?**

## The three layers

1. `Assets/Resources/HexLive/Balance/*.asset` — complete, inspector-friendly
   defaults used by the Unity game. Each public field maps to exactly one
   simulation static through `SimConfigMirror`; coverage gates reject missing
   or duplicate mappings.
2. `SimData/simdata.json` — the exported effective balance and catalogs used by
   tests, soaks and the headless server. After changing an asset, run
   **HexLive ▸ Export Sim Data (JSON)**. Headless runs refuse stale schema.
3. `HexLiveContent/balance.json` beside a built Player — optional, partial,
   highest-priority override for a local build. It changes registered balance
   numbers without rebuilding the application. Relaunch the Player (or return
   to the menu so the scene boots again) after replacing the file.

An explicit launch argument overrides the conventional path:

```text
-hexlive-balance /absolute/path/to/balance.json
```

The file is a flat JSON object. Keys are the exact names from the `balance`
section of `SimData/simdata.json`; it may contain one knob or hundreds. The
loader validates the whole file before applying anything: an unknown key,
wrong type or fractional integer rejects the entire deployment and leaves the
asset defaults active. The Unity console records either `Applied N external
override(s)` or a rejection reason.

Remote games deliberately ignore the viewer's local final values: the server's
`--simdata` is authoritative and its handshake overwrites client-side tuning
before world generation. To tune a remote world, export/deploy the server's
simdata and restart that world process.

## Population knobs (§132)

| Key | Default | Meaning |
|---|---:|---|
| `WorldBalance.MaxLivingNpcs` | 10 | Hard ceiling across both living rosters. Corpses do not consume seats. |
| `WorldBalance.MaxColonyNpcs` | 5 | Player-camp quota. |
| `WorldBalance.MaxOutsiderNpcs` | 5 | Hostile-camp quota, including the opening outsider. |
| `WorldBalance.ColonyArrivalIntervalDays` | 7 | One new woman on calendar days 7, 14, 21…; `0` disables. |
| `Spec72.RaidWaveIntervalDays` | 3 | One hostile arrival opportunity on days 3, 6, 9…; `0` disables later waves. |

A full camp consumes that date. If somebody dies the next day, the game waits
for the next scheduled boundary rather than releasing a hidden backlog. The
global cap and the relevant faction quota must both have room.

Start with [`population.example.json`](population.example.json), rename/copy it
to `HexLiveContent/balance.json`, and keep only the knobs needed for that
deployment.

## Sleep recovery knobs (§54.11 r2)

| Key | Default | Meaning |
|---|---:|---|
| `SimBalance.SleepEnergyBaseBonus` | 0.002 | Net energy restored per slow tick on the ground. At the current 16-tick slow interval, 0.002 means 0→100% in 8 game hours. |
| `SimBalance.SleepEnergyBasicBedBonus` | 0.002 | Extra recovery on `bed.basic`. Base + bed = 0.004, so 0→100% takes 4 game hours. |
| `SimBalance.SleepEnergyFireBonus` | 0 | Optional extra recovery near fire. Keep at 0 when the 4 h / 8 h timing must stay exact; fire still supplies warmth and comfort. |
| `SimBalance.GroundSleepEnergy` | 0 | Legacy timed-interaction recovery channel. Keep at 0 to avoid double recovery. |
| `SimBalance.BedEnergy` | 0 | Legacy bed-interaction recovery channel. Keep at 0 to avoid double recovery. |

The exact hour formulas are `groundHours = 0.016 / baseBonus` and
`bedHours = 0.016 / (baseBonus + bedBonus)`. Sleeping already advances hunger
and thirst at 0.1× the awake rate; these energy knobs do not alter metabolism.

Start with [`sleep.example.json`](sleep.example.json) for the current 4 h / 8 h
experiment. It is intentionally complete: leaving both legacy channels at zero
is part of the timing contract.
