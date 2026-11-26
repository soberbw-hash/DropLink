#!/usr/bin/env python3
"""Stage a pristine engine copy for the offline test suites.

The tests run the real engine, and the engine reads ``../../data/settings.json``
relative to its own folder. Pointing the tests at a live installation therefore
picks up that user's settings (for example auto-receive, which disables the
confirmation flag the tests assert). Staging the engine under
``.prepared/winawdl/airdrop`` gives it an empty settings context again.

Usage:
    python tools/prepare-test-engine.py --from "D:/DropLink-stage/backend/airdrop"
    python tools/prepare-test-engine.py --engine-only tools/portable/backend/airdrop/engine.py
"""
import argparse
import pathlib
import shutil
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
STAGE = REPO / '.prepared' / 'winawdl' / 'airdrop'
MARKER = 'identity.py'
SHIPPED = REPO / 'tools' / 'portable' / 'backend' / 'airdrop' / 'engine.py'


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--from', dest='source', type=pathlib.Path, required=False,
                        help='assembled backend/airdrop folder to copy')
    parser.add_argument('--engine-only', dest='engine', type=pathlib.Path,
                        help='replace only engine.py in the staged copy (default: the '
                             'shipped one under tools/portable/backend/airdrop)')
    parser.add_argument('--clean', action='store_true', help='remove the staged copy and exit')
    args = parser.parse_args()

    if args.clean:
        if STAGE.exists():
            shutil.rmtree(STAGE)
            print(f'removed: {STAGE}')
        return 0

    if args.engine or not args.source:
        # The staged tree only exists to give the real engine an empty settings
        # context. Its engine.py has to be the one that ships, otherwise the
        # offline tests happily pass against code nobody runs.
        engine = (args.engine or SHIPPED).resolve()
        if not engine.is_file():
            print(f'FAIL: no engine at {engine}')
            return 1
        if not (STAGE / MARKER).is_file():
            print(f'FAIL: {STAGE} is not staged yet; run --from <install>/backend/airdrop first')
            return 1
        shutil.copyfile(engine, STAGE / 'engine.py')
        print(f'OK synced engine.py -> {STAGE / "engine.py"}')
        return 0

    source = args.source.resolve()
    if not (source / MARKER).is_file():
        print(f'FAIL: {source} is not a complete engine folder (missing {MARKER})')
        print('Pass the assembled backend/airdrop from a portable install.')
        return 1

    if STAGE.exists():
        shutil.rmtree(STAGE)
    STAGE.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, STAGE)

    # A stale settings file two levels up would leak into the engine again.
    leaked = STAGE.parent.parent / 'data' / 'settings.json'
    if leaked.exists():
        leaked.unlink()
        print(f'removed ambient settings: {leaked}')

    files = sorted(p.name for p in STAGE.iterdir() if p.is_file())
    print(f'OK staged {len(files)} file(s) at {STAGE}')
    print('engine = ' + str(STAGE / 'engine.py'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
