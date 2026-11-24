"""Build a real Windows .lnk so the desktop entry points at the exe itself.

COM (WScript.Shell) is not available in this environment, so the Shell Link
binary format is written directly. Only the fields Explorer actually needs are
filled: LinkInfo with a local base path plus a working directory. The target's
own icon is used (no IconLocation), which is what makes the shortcut look like
the app instead of like a document.
"""
import struct
import sys

HEADER_SIZE = 0x4C
CLSID = bytes.fromhex('011402000000 0000c0000000000000 46'.replace(' ', ''))
FLAG_HAS_LINK_INFO = 0x02
FLAG_HAS_WORKING_DIR = 0x10
FLAG_IS_UNICODE = 0x80


def utf16_counted(text):
    raw = text.encode('utf-16-le')
    return struct.pack('<H', len(raw) // 2) + raw


def build(target, working_dir, serial=0):
    target_ansi = target.encode('gbk') + b'\x00'
    # VolumeID: size, DRIVE_FIXED, serial, label offset, then an empty label.
    # The serial is not decoration: a LinkInfo that claims a volume it cannot
    # match is resolved leniently by Explorer but rejected outright by strict
    # readers, so the real one is passed in.
    volume = struct.pack('<IIII', 17, 3, serial, 0x10) + b'\x00'

    link_info_header = 0x1C
    local_offset = link_info_header + len(volume)
    suffix_offset = local_offset + len(target_ansi)
    size = suffix_offset + 1

    link_info = struct.pack('<IIIIIII', size, link_info_header, 1,
                            link_info_header, local_offset, 0, suffix_offset)
    link_info += volume + target_ansi + b'\x00'

    flags = FLAG_HAS_LINK_INFO | FLAG_HAS_WORKING_DIR | FLAG_IS_UNICODE
    header = struct.pack('<I', HEADER_SIZE) + CLSID
    header += struct.pack('<I', flags)
    header += struct.pack('<I', 0x20)          # FILE_ATTRIBUTE_ARCHIVE
    header += struct.pack('<QQQ', 0, 0, 0)     # creation / access / write time
    header += struct.pack('<I', 0)             # file size
    header += struct.pack('<i', 0)             # icon index
    header += struct.pack('<I', 1)             # SW_SHOWNORMAL
    header += struct.pack('<H', 0)             # hotkey
    header += struct.pack('<H', 0)             # reserved
    header += struct.pack('<II', 0, 0)         # reserved
    assert len(header) == HEADER_SIZE, len(header)

    data = header + link_info + utf16_counted(working_dir)
    if len(data) % 2:
        data += b'\x00'
    return data


if __name__ == '__main__':
    target, workdir, out = sys.argv[1], sys.argv[2], sys.argv[3]
    serial = int(sys.argv[4], 16) if len(sys.argv) > 4 else 0
    blob = build(target, workdir, serial)
    with open(out, 'wb') as handle:
        handle.write(blob)
    print('wrote %s (%d bytes)' % (out, len(blob)))
