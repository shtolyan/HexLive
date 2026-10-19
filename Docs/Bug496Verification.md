# #496 — reproducible native allocation comparison

The first clean Release run of `50ec382c7ecee64038cc61bb8f5ce6ab96e5b803`
passed Voice68/68 and Server515/515. Simulation finished2023passed,
1failed,6existing skips. The failure was
`WarmAllocatorWorkIsIndependentOfDistantObjectCount`:6168B before versus0B
after adding1000 distant objects. The actual ClipWindow gate passed in29.879s.
The red full-suite logs and TRX files are retained locally under
`Build/MSIRelease/20261019-master-release/first-full-50ec-*`.

MSI has.NET10.0.10 and6.0.26 runtimes, with no.NET9 runtime. The net9-target
native tests use process-only Major roll-forward. Four isolated diagnostic
processes ran the **unchanged** allocation test:

| JIT configuration | before / after time | allocation before / after |
|---|---|---|
| default, run1 |228.115 /26.329ms |0 /0B |
| default, run2 |229.768 /27.783ms |0 /0B |
| single tier, run1 |19.970 /21.950ms |0 /0B |
| single tier, run2 |20.407 /23.450ms |0 /0B |

These observations show a startup-sensitive measurement and do not establish
an extra allocation caused by distant objects. We do not infer the precise
allocation site from these counters alone. Raw results: `allocation-ab.json`
and its four TRX files in the same local release directory.

`TieredCompilation=false` is now pinned **only** in
`Tests/HexLive.Simulation.Tests/HexLive.Simulation.Tests.csproj`, so both sides
of an allocation comparison run under one JIT strategy. This is the supported
[Microsoft runtime configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation#tiered-compilation).
`GroundItemPlacementTests`, its exact byte/candidate/inspection assertions,
runtime placement code and test filters are unchanged. The setting does not
configure Unity, the game server, other test executables or the operating system.

The generated test runtimeconfig contains `System.Runtime.TieredCompilation:false`.
The existing allocation test passes1/1 with that project configuration and no
tiering environment override. This focused result alone is not release admission:
the complete clean-commit Voice/Server/Simulation suites must pass before the
authorized Singapore activation, with results in `RELEASE_COORDINATION_SVETA.md`.
