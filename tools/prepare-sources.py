#!/usr/bin/env python3
"""Prepare pinned source trees for inspection/build. Never touches hardware."""
import argparse
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
REPOS = {
    'dropbridge': ('https://github.com/ndpyzwy-0w0/DropBridge.git', '8fbcee72569de2bf3fc8782164996dcc84f9f0c1'),
    'winawdl': ('https://github.com/raph559/winawdl.git', '2196fb48b97cef1ee17ee1350c442972c4efeaf0'),
    'devourer': ('https://github.com/OpenIPC/devourer.git', '34a9e6f95c08da6cb7c4e621e070058e2cc5e779'),
}
OWL = 'da255a70f221784c836d943dd3f243bc798f223b'

def run(*args, cwd=None):
    print('+', ' '.join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), cwd=cwd, check=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=pathlib.Path, help='new, non-existent directory')
    args = parser.parse_args()
    dest = args.destination.resolve()
    dest.mkdir(parents=True, exist_ok=False)  # Never overwrite or reuse any existing tree.
    for name, (url, revision) in REPOS.items():
        repo = dest / name
        run('git', 'clone', '--no-checkout', url, repo)
        run('git', '-C', repo, 'checkout', '--detach', revision)
        actual = subprocess.check_output(['git', '-C', str(repo), 'rev-parse', 'HEAD'], text=True).strip()
        if actual != revision:
            raise RuntimeError(f'{name}: incorrect revision {actual}')
    bridge, win, dev = (dest / x for x in ('dropbridge', 'winawdl', 'devourer'))
    run('git', '-C', win, 'submodule', 'update', '--init', '--recursive', 'upstream/owl')
    owl = win / 'upstream/owl'
    actual = subprocess.check_output(['git', '-C', str(owl), 'rev-parse', 'HEAD'], text=True).strip()
    if actual != OWL:
        raise RuntimeError(f'OWL gitlink changed: {actual}')
    # The identity patch is already entirely included in awdl-ipc-host.patch.
    # Check its reverse afterwards, exactly as upstream's idempotent script does.
    for repo, patch in ((win, bridge / 'patches/awdl-ipc-host.patch'),
                        (owl, bridge / 'patches/owl-airbridge.patch'),
                        (dev, bridge / 'patches/devourer-airbridge.patch'),
                        (win, ROOT / 'patches/engine-default-directory.patch'),
                        (win, ROOT / 'patches/atomic-file-publish.patch'),
                        (win, ROOT / 'patches/engine-proxy-mode.patch')):
        run('git', '-C', repo, 'apply', '--check', patch)
        run('git', '-C', repo, 'apply', patch)
    run('git', '-C', win, 'apply', '--reverse', '--check', bridge / 'patches/airdrop-identity.patch')
    print('Prepared source trees. No device, driver or network settings were changed.')

if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, subprocess.CalledProcessError) as exc:
        print(f'Preparation failed: {exc}. Inspect the destination; it was not removed.', file=sys.stderr)
        sys.exit(1)
