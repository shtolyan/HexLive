# Text-only VoiceBrain evaluation

Runs the actual `BrainTurn`, `CharacterView`, streaming client, and isolated
conversation memory/relationships. No ElevenLabs credential is accepted or used.
HTTP is restricted to OpenAI Chat Completions, with at most 80 attempts and 750
completion tokens per request. **Run only when the user authorizes paid model tests.**
Provider credentials are read from the process environment, never written to reports:
`OPENAI_API_KEY`, `VOICEBRAIN_MODEL`, `VOICEBRAIN_REASONING`. Do not print or commit them.

Input is a private JSON object with `characters`, containing sanitized
`describe_colonist` snapshots. Do not export old conversation archives, credentials,
`legacyAgentState`, or unneeded personal data. Capture the intended NPC explicitly.

```powershell
dotnet publish Tools/HexLive.VoiceBrainEval -c Release -r linux-x64 --self-contained true -o Build/VoiceBrainEval
# On the intended diagnostic machine, with its configured provider credential:
./Build/VoiceBrainEval/HexLive.VoiceBrainEval input.json private-output-directory 123
```

`--projection-only` adds no provider calls and needs no key. The paid run repeats
the five original grounding cases, then checks inspection→action, resources,
harvesting with/without a tool, water vessel collection/placement/empty state,
construction delivery/finishing without a hammer, visible prey/danger without a
weapon, vanished targets, queued versus completed work, and crafting.

The gateway records intended actions and either rejects execution explicitly or
returns an explicitly queued diagnostic receipt. **It never sends orders to a
game server.** Scenarios use independent neutral test relationships and isolated
memory, not the player's real relationship history. Controlled objects/animals
are synthetic data, not spawned production entities.

`results.json` records every answer, intended call, evaluated assembly SHA,
model, usage, configured-tariff cost estimate, and limits. Reports are private
artifacts, not source or evidence archives for upload. Keep the runner output in
an ignored directory; do not overwrite an earlier run to cherry-pick results.
Exit zero proves execution completed, not that all model decisions are correct:
review factual accuracy, refusal reasons, and intentions for every case. Run
server/simulation tests separately for physical outcomes and admission checks.
This does not replace an authenticated live phone test after an authorized release.

For a focused follow-up, `HEXLIVE_EVAL_SCENARIOS` selects comma-separated case ids;
`HEXLIVE_EVAL_MAX_REQUESTS` lowers the remaining budget (1..80). Preserve the first
run and its failures; use a new output directory and count paid attempts across
runs. Do not reset the budget to silently spend more on retries.
