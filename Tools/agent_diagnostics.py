#!/usr/bin/env python3
"""Read-only summary of Agent Studio diagnostic JSONL (no credentials required)."""
import argparse
import collections
import json
from pathlib import Path


def summarize(directory, turn=None):
    rows, invalid = [], 0
    for path in sorted(Path(directory).glob('events*.jsonl')):
        with path.open(encoding='utf-8') as stream:
            for line in stream:
                try:
                    row = json.loads(line)
                    if not isinstance(row, dict) or row.get('schemaVersion') != 1:
                        invalid += 1
                        continue
                    if turn is None or row.get('turnId') == turn:
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
    for r in rows:
        output.append(f"{r.get('utc')} npc={r.get('npcId')} turn={r.get('turnId')} "
                      f"{r.get('kind')} {r.get('tool', '')} {r.get('result', '')}")
    return '\n'.join(output)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', help='StateDirectory/diagnostics')
    parser.add_argument('--turn', help='hashed turnId from an event')
    arguments = parser.parse_args()
    print(summarize(arguments.directory, arguments.turn))
