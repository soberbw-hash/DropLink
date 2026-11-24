#!/usr/bin/env python3
"""Build awdld.exe and its offline tests with a MinGW-w64 toolchain.

OWL is GNU C, so the AWDL daemon cannot be built with MSVC. This script takes
an explicit MinGW bin directory (portable winlibs or a system install), finds
CMake, configures an isolated build tree, compiles, and runs the offline unit
tests. It never touches a device, driver, or network setting.

Optionally pass --wintun-dll to place a signed wintun.dll beside awdld.exe.
That only stages a file; loading it (which creates a TUN adapter) still needs
separate, explicit authorization.

Example:
    python tools/build-awdl.py --winawdl D:/work/prepared-next/winawdl \
        --mingw-bin D:/work/_toolchain/mingw64/bin \
        --wintun-dll D:/work/_toolchain/wintun/bin/amd64/wintun.dll
"""
import argparse
import hashlib
import os
import pathlib
import shutil
import subprocess
import sys

TESTS = ('host_frame_test', 'tcp_merge_test', 'frame_fixup_test',
         'service_bridge_test', 'redact_test')

# Official Wintun 0.14.1 amd64 DLL (the version awdl/win/host_if.c targets).
# SHA-256 of wintun/bin/amd64/wintun.dll from wintun-0.14.1.zip.
WINTUN_SHA256 = 'e5da8447dc2c320edc0fc52fa01885c103de8c118481f683643cacc3220dafce'


def run(args, *, cwd=None, env=None):
    print('+', ' '.join(map(str, args)), flush=True)
    subprocess.run(list(map(str, args)), cwd=cwd, env=env, check=True)


def find_cmake(explicit):
    if explicit:
        p = pathlib.Path(explicit)
        if not p.is_file():
            raise RuntimeError(f'cmake not found: {p}')
        return str(p)
    on_path = shutil.which('cmake')
    if on_path:
        return on_path
    # VS BuildTools ships CMake off PATH; probe common install locations.
    roots = [
        pathlib.Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)')),
        pathlib.Path(os.environ.get('ProgramFiles', 'C:/Program Files')),
    ]
    for root in roots:
        for base in (root / 'Microsoft Visual Studio',):
            for hit in base.rglob('CMake/bin/cmake.exe') if base.exists() else []:
                return str(hit)
    raise RuntimeError('cmake not found; pass --cmake <path>')


def clean_proxy(env):
    """MSBuild/cmake child processes choke when proxy vars are duplicated by case."""
    return {k: v for k, v in env.items()
            if k.lower() not in ('http_proxy', 'https_proxy', 'all_proxy')}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--winawdl', type=pathlib.Path, required=True,
                    help='winawdl source root (contains awdl/ and upstream/owl/)')
    ap.add_argument('--mingw-bin', type=pathlib.Path, required=True,
                    help='directory containing gcc.exe and mingw32-make.exe')
    ap.add_argument('--cmake', help='optional explicit path to cmake.exe')
    ap.add_argument('--build-dir', default='build/awdl-portable',
                    help='build directory, relative to the winawdl root')
    ap.add_argument('--wintun-dll', type=pathlib.Path,
                    help='optional signed wintun.dll (amd64) to stage beside awdld.exe')
    args = ap.parse_args()

    win = args.winawdl.resolve()
    mingw = args.mingw_bin.resolve()
    for exe in ('gcc.exe', 'mingw32-make.exe'):
        if not (mingw / exe).is_file():
            raise RuntimeError(f'{exe} not found in {mingw}')
    if not (win / 'awdl' / 'CMakeLists.txt').is_file():
        raise RuntimeError(f'awdl sources not found under {win}')
    if not (win / 'upstream' / 'owl' / 'radiotap' / 'radiotap.c').is_file():
        raise RuntimeError('OWL radiotap sources missing; init the submodule first')

    cmake = find_cmake(args.cmake)
    build = win / args.build_dir
    gcc = mingw / 'gcc.exe'
    make = mingw / 'mingw32-make.exe'
    env = clean_proxy(os.environ.copy())
    env['PATH'] = str(mingw) + os.pathsep + env.get('PATH', '')

    run([cmake, '-S', str(win / 'awdl'), '-B', str(build), '-G', 'MinGW Makefiles',
         f'-DCMAKE_C_COMPILER={gcc}', f'-DCMAKE_MAKE_PROGRAM={make}',
         '-DCMAKE_BUILD_TYPE=Release'], env=env)
    # A global DLL injector can crash cc1.exe once; a retry is safe (incremental).
    try:
        run([cmake, '--build', str(build)], env=env)
    except subprocess.CalledProcessError:
        print('build failed once; retrying incrementally (transient cc1 crash)', flush=True)
        run([cmake, '--build', str(build)], env=env)

    awdld = build / 'awdld.exe'
    if not awdld.is_file():
        raise RuntimeError('awdld.exe was not produced')
    print(f'\nawdld built: {awdld} ({awdld.stat().st_size} bytes)', flush=True)

    if args.wintun_dll:
        src = args.wintun_dll.resolve()
        if not src.is_file():
            raise RuntimeError(f'wintun.dll not found: {src}')
        digest = hashlib.sha256(src.read_bytes()).hexdigest()
        if digest != WINTUN_SHA256:
            raise RuntimeError(f'wintun.dll SHA-256 mismatch: {digest} (expected {WINTUN_SHA256})')
        shutil.copy2(src, build / 'wintun.dll')
        print(f'  staged wintun.dll (verified {digest[:16]}…). '
              'Loading it still requires explicit authorization.', flush=True)

    failed = []
    for test in TESTS:
        exe = build / f'{test}.exe'
        if not exe.is_file():
            failed.append(f'{test}: missing')
            continue
        proc = subprocess.run([str(exe)], cwd=build, env=env,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        tail = '\n'.join(proc.stdout.strip().splitlines()[-3:])
        status = 'ok' if proc.returncode == 0 else f'FAIL({proc.returncode})'
        print(f'  {test}: {status} — {tail}', flush=True)
        if proc.returncode != 0:
            failed.append(f'{test}: exit {proc.returncode}')
    if failed:
        raise RuntimeError('awdld offline tests failed: ' + '; '.join(failed))
    print('\nPASS: awdld built and all 5 offline tests passed. '
          'No device, driver or network setting was changed.', flush=True)


if __name__ == '__main__':
    try:
        main()
    except (OSError, RuntimeError, subprocess.CalledProcessError) as exc:
        print(f'awdld build failed: {exc}', file=sys.stderr)
        sys.exit(1)
