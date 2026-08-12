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
