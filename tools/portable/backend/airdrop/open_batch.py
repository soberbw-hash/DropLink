"""Open a completed batch from DropLink's local history in Windows Explorer."""
import base64
import ctypes
import json
import os
from pathlib import Path
import sys


def batch_target(history_file, batch):
    with history_file.open(encoding='utf-8-sig') as handle:
        rows = json.load(handle)
    row = next((item for item in rows if item.get('Id') == batch), None)
    if not row or not row.get('Folder'):
        return None
    folder = Path(row['Folder'])
    if not folder.is_absolute() or not folder.is_dir():
        return None
    root = folder.resolve()
    existing = None
    for name in row.get('Files', []):
        item = folder / name
        if not item.resolve().is_relative_to(root) or not item.exists():
            continue
        if existing is not None:
            return folder, None
        existing = item
    return folder, existing


def reveal(item):
    ole = ctypes.OleDLL('ole32')
    shell = ctypes.OleDLL('shell32')
    cleanup = ctypes.WinDLL('ole32')
    cleanup.CoTaskMemFree.argtypes = [ctypes.c_void_p]
    cleanup.CoTaskMemFree.restype = None
    cleanup.CoUninitialize.restype = None
    ole.CoInitialize(None)
    try:
        pidl = ctypes.c_void_p()
        shell.SHParseDisplayName(ctypes.c_wchar_p(str(item)), None, ctypes.byref(pidl), 0, None)
        try:
            shell.SHOpenFolderAndSelectItems(pidl, 0, None, 0)
        finally:
            cleanup.CoTaskMemFree(pidl)
    finally:
        cleanup.CoUninitialize()


def main(encoded):
    batch = base64.urlsafe_b64decode(encoded + '=' * (-len(encoded) % 4)).decode('utf-8')
    target = batch_target(Path(__file__).resolve().parents[2] / 'data' / 'history.json', batch)
    if target is None:
        return 1  # Older notifications can fall back to the configured folder.
    folder, item = target
    if item is None:
        os.startfile(folder)
    else:
        reveal(item)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main(sys.argv[1]))
    except (OSError, ValueError, TypeError, KeyError, IndexError):
        sys.exit(1)
