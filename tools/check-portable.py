#!/usr/bin/env python3
"""Read-only release checklist for an assembled DropLink portable folder."""
import argparse
import json
import pathlib
import sys

BINARIES = [
    'app/DropLink.exe',
    # Published next to the exe by tools/publish.ps1. The .ico is what a
    # shortcut needs, and the PNG is the design file; losing either means the
    # icon can only be recovered from version control.
    'app/DropLink.ico',
    'app/icon-source.png',
    'backend/airbridge_radio.exe',
    'backend/radio_pipe_bridge.exe',
    'backend/awdl_netd.exe',
    'backend/awdld.exe',
    'backend/python/python.exe',
]

# Modules that come from the pinned upstream source, not from this repository.
# The repo only carries the patched engine.py/receiver.py/transfer.py overlay, so
# a source-only check cannot require them; an assembled package must have them or
# the engine cannot even import (identity.py is the first failure).
UPSTREAM_ENGINE_MODULES = [
    'backend/airdrop/cpio.py',
    'backend/airdrop/identity.py',
    'backend/airdrop/mdns.py',
    'backend/airdrop/netif.py',
    'backend/airdrop/redact.py',
    'backend/airdrop/sender.py',
]

REQUIRED = [
    'Start-DropLink.vbs',
    '启动 DropLink.cmd',
    '_工具/设置 DropLink.cmd',
    '_工具/测试 DropLink.cmd',
    '_工具/性能基准.cmd',
    'backend/airdrop/engine.py',
    'backend/airdrop/receiver.py',
    'backend/airdrop/transfer.py',
    'backend/airdrop/notify.ps1',
    'backend/airdrop/toast-action.ps1',
    'backend/airdrop/settings-tool.ps1',
    'backend/airdrop/settings-gui.ps1',
    'backend/airdrop/test-card.ps1',
    'backend/airdrop/benchmark.ps1',
    'data/settings.default.json',
    'data/hardware.default.json',
] + BINARIES


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('portable', type=pathlib.Path, help='assembled portable folder')
    parser.add_argument('--source-only', action='store_true',
                        help='check the repository skeleton; binaries are expected to be built later')
    args = parser.parse_args()
    root = args.portable.resolve()
    required = [item for item in REQUIRED if not (args.source_only and item in BINARIES)]
    if not args.source_only:
        required += UPSTREAM_ENGINE_MODULES
    errors = []
    warnings = []
    if not root.is_dir():
        print(f'FAIL: not a directory: {root}')
        return 1
    for relative in required:
        path = root / relative
        if not path.is_file():
            errors.append(f'missing: {relative}')
        elif path.stat().st_size == 0:
            errors.append(f'empty: {relative}')
    for relative in ('data/settings.json', 'data/hardware.json'):
        path = root / relative
        if not path.is_file():
            warnings.append(f'not initialized yet: {relative}')
            continue
        try:
            value = json.loads(path.read_text(encoding='utf-8'))
        except (OSError, ValueError) as error:
            errors.append(f'{relative}: invalid JSON: {error}')
            continue
        if not isinstance(value, dict):
            errors.append(f'{relative}: top level must be an object')
    exe = root / 'app' / 'DropLink.exe'
    if not args.source_only and exe.is_file() and exe.stat().st_size < 1024 * 1024:
        errors.append('app/DropLink.exe looks too small; publish may be incomplete')
    for relative in ('data/received', 'data/logs', 'data/keys'):
        if not (root / relative).is_dir():
            warnings.append(f'not created yet: {relative}')
    for warning in warnings:
        print(f'WARN: {warning}')
    for error in errors:
        print(f'FAIL: {error}')
    if errors:
        return 1
    print(f'PASS: portable package at {root}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
