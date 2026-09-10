#!/usr/bin/env python3
"""Read-only summary of Agent Studio diagnostic JSONL (no credentials required)."""
import argparse
import collections
import json
from pathlib import Path


def summarize(directory, turn=None, plan=None):
    rows, invalid = [], 0
    for path in sorted(Path(directory).glob('events*.jsonl')):
        with path.open(encoding='utf-8') as stream:
            for line in stream:
                try:
                    row = json.loads(line)
                    if not isinstance(row, dict) or row.get('schemaVersion') != 1:
                        invalid += 1
                        continue
                    execution = row.get('execution') or {}
                    if (turn is None or row.get('turnId') == turn) and (plan is None or execution.get('planId') == plan):
                        rows.append(row)
                except json.JSONDecodeError:
                    invalid += 1
    rows.sort(key=lambda r: (r.get('utc', ''), r.get('sessionId', ''), r.get('sequence', 0)))
    counts = collections.Counter(r.get('kind', 'unknown') for r in rows)
    output = [f'Events: {len(rows)}; incomplete/unsupported lines: {invalid}',
              'Counts: ' + ', '.join(f'{k}={v}' for k, v in sorted(counts.items()))]
    durations = [r['elapsedMs'] for r in rows if r.get('kind') == 'model.completed' and isinstance(r.get('elapsedMs'), (int, float))]
    if durations:
        output.append(f'Model duration: total={sum(durations)} ms; max={max(durations)} ms')
    requests = [r for r in rows if r.get('kind') in ('model.request.completed', 'model.request.failed')]
    if requests:
        totals = []
        for key in ('inputTokens', 'outputTokens'):
            values = [(r.get('usage') or {}).get(key) for r in requests]
            known = [v for v in values if isinstance(v, int) and not isinstance(v, bool) and v >= 0]
            totals.append(f'{key}: known={sum(known)}, unavailable={len(values) - len(known)}/{len(values)} requests')
        output.append('Usage: ' + '; '.join(totals))
    for r in rows:
        execution = r.get('execution') or {}
        observation = r.get('observation') or {}
        details = ''
        if execution:
            details += (f" plan={execution.get('planId')} step={execution.get('stepId')}"
                        f" command={execution.get('CommandSequence')} reason={execution.get('reason', '')}")
        if observation:
            details += (f" energy={observation.get('Energy')} stamina={observation.get('Stamina')}"
                        f" freeSlots={observation.get('FreeSlots')}")
        output.append(f"{r.get('utc')} npc={r.get('npcId')} turn={r.get('turnId')} "
                      f"{r.get('kind')} {r.get('tool', '')} {r.get('result', '')}{details}")
    return '\n'.join(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', help='StateDirectory/diagnostics')
    parser.add_argument('--turn', help='hashed turnId from an event')
    parser.add_argument('--plan', help='hashed execution.planId from an event')
    arguments = parser.parse_args()
    print(summarize(arguments.directory, arguments.turn, arguments.plan))
