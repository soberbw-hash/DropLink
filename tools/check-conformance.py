#!/usr/bin/env python3
"""Check DropLink IPC event vectors in tests/conformance/ipc-events.jsonl."""
import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
VECTORS = ROOT / 'tests' / 'conformance' / 'ipc-events.jsonl'


def check(vector):
    name = vector.get('name', '<unnamed>')
    events = vector.get('events')
    if not isinstance(events, list) or not events:
        raise ValueError(f'{name}: events must be a non-empty list')
    seen = []
    required = vector.get('required', {})
    for index, event in enumerate(events):
        if not isinstance(event, dict) or 'event' not in event:
            raise ValueError(f'{name}: event {index} has no event name')
        kind = event['event']
        seen.append(kind)
        for field in required.get(kind, []):
            if field not in event:
                raise ValueError(f'{name}: event {index} ({kind}) missing {field}')
    expected = vector.get('expect', [])
    if seen != expected:
        raise ValueError(f'{name}: order {seen} != expected {expected}')
    for forbidden in vector.get('forbid', []):
        if forbidden in seen:
            raise ValueError(f'{name}: forbidden event {forbidden} appeared')
    return name


def main():
    if not VECTORS.exists():
        raise SystemExit(f'missing vectors: {VECTORS}')
    count = 0
    with VECTORS.open(encoding='utf-8') as handle:
        for line_number, line in enumerate(handle, 1):
            if not line.strip():
                continue
            try:
                vector = json.loads(line)
            except json.JSONDecodeError as exc:
                raise SystemExit(f'bad JSON at line {line_number}: {exc}') from exc
            print('PASS:', check(vector))
            count += 1
    print(f'PASS: {count} conformance vector(s)')


if __name__ == '__main__':
    try:
        main()
    except ValueError as exc:
        print('FAIL:', exc, file=sys.stderr)
        raise SystemExit(1)
