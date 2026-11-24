# Test-only child process; never opens a socket or touches hardware.
import json
import sys

print('not json', flush=True)
print(json.dumps({'event': 'asked', 'id': 'r1'}), flush=True)
for line in sys.stdin:
    command = json.loads(line)
    if command.get('do') == 'answer':
        print(json.dumps({'event': 'answered', 'id': command['id'], 'accept': command['accept']}), flush=True)
    elif command.get('do') == 'quit':
        break
