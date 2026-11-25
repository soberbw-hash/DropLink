# SPDX-License-Identifier: MIT
# Adapted from bin/airdrop-serve.py in omdrop, Copyright (c) 2026 Brent Kearney.
"""Bounded reading, decoding and storing of what an AirDrop sender uploads.
The bytes come from whoever is in radio range, so every step has a ceiling."""
import contextlib
import io
import logging
import os
import re
import shutil
import socket
import tempfile
import threading
import time
import unicodedata
import uuid
import zlib
from urllib.parse import urlsplit

import cpio

KIB = 1024
MIB = 1024 * KIB
GIB = 1024 * MIB

# How long a body may arrive with nothing at all before the upload is dropped.
#
# 30 s was too eager for AWDL. The log of 2026-11-25 01:47 has a real 27 MB
# video that got to 6.08 MB and then went quiet for 30 s; the receiver answered
# 408 and the phone showed nothing. Channel hopping on a link with two peers is
# exactly the situation where a sender pauses for tens of seconds and then
# carries on, and killing it turns a slow transfer into a failed one. The rate
# floor below still catches a body that is alive but crawling.
READ_IDLE_TIMEOUT_SECONDS = 90
# How long to wait for the marker that ends a body once the archive in it is
# complete. An iPhone sends none after a video or a Live Photo.
END_MARKER_WAIT_SECONDS = 2
INITIAL_READ_SECONDS = 60
MIN_READ_BYTES_PER_SECOND = 64 * KIB
MAX_METADATA_BYTES = 1 * MIB
MAX_ARCHIVE_MEMBERS = 512
MIN_FREE_BYTES = 1 * GIB
DISK_CHECK_INTERVAL = 16 * MIB
IO_CHUNK_BYTES = 64 * KIB
# What this side cuts an upload into. A receiver has to take whatever size a
# sender chose, so a small one is the safe one.
DVZIP_BLOCK_BYTES = 16 * KIB
DVZIP_STORED = 0x80000000
# Photos and videos do not shrink, so a higher level only costs time.
DVZIP_LEVEL = 1
MAX_CHUNK_LINE_BYTES = 128
MAX_TRAILER_BYTES = 8 * KIB
MAX_CONCURRENT_UPLOADS = 2
UPLOAD_SLOTS = threading.BoundedSemaphore(MAX_CONCURRENT_UPLOADS)
MAX_NAME_UNITS = 255
MAX_NAME_ATTEMPTS = 10000
LIVE_PHOTO_FOLDER_SUFFIX = '.pvt'
LIVE_PHOTO_METADATA = 'metadata.plist'
LIVE_PHOTO_VIDEO_SUFFIX = '.mov'

# Separators, what Windows forbids (a colon selects an alternate data stream),
# control characters, and bidi overrides: "gpj.<U+202E>exe" shows as "exe.jpg".
UNSAFE_CHAR = re.compile(
    r'[/\\\x00<>:"|?*]|[‪-‮⁦-⁩​-‏﻿]|[\x01-\x1f\x7f-\x9f]')
# Windows resolves these to devices whatever extension follows.
DEVICE_NAME = re.compile(r'^(con|prn|aux|nul|com[0-9¹²³]|lpt[0-9¹²³])$',
                         re.IGNORECASE)
# Zone 3 is "Internet": Windows then treats the file like a browser download.
ZONE_MARK = b'[ZoneTransfer]\r\nZoneId=3\r\n'
# A line break inside a stored link would add keys to the shortcut file, such
# as an icon path on a remote share.
LINK_BREAKING_CHAR = re.compile(r'[\x00-\x20\x7f-\x9f]')


class RequestBodyError(ValueError):
    status = 400


class BodyTooLarge(RequestBodyError):
    status = 413


class UploadLimitError(ValueError):
    pass


class ReceiveBudget:
    """Byte budget for one upload, based on current free output-disk space."""

    def __init__(self, dest, percent):
        self.free_bytes = shutil.disk_usage(dest).free
        percent_bytes = self.free_bytes * percent // 100
        reservable = max(0, self.free_bytes - MIN_FREE_BYTES)
        self.byte_limit = min(percent_bytes, reservable)
        self.percent = percent
        if self.byte_limit <= 0:
            raise UploadLimitError(
                f'not enough free space while preserving {MIN_FREE_BYTES} bytes')


class ReadRateTimeout(socket.timeout):
    """The body is still arriving, but slower than the minimum read rate."""


class ReadDeadline:
    """Require sustained progress as well as an idle-socket timeout."""

    def __init__(self):
        self.started = time.monotonic()
        self.transferred = 0
        self.idle_seconds = READ_IDLE_TIMEOUT_SECONDS
        # What the reader was about to read, for stall_report.
        self.awaiting = 'the first byte'
        self.chunks = 0
        # True while the next thing due is a chunk header: every chunk so far
        # arrived whole, so the body may be all there bar its end marker.
        self.between_chunks = False

    def note(self, size):
        self.transferred += size
        allowed = INITIAL_READ_SECONDS + (
            self.transferred / MIN_READ_BYTES_PER_SECOND)
        if time.monotonic() > self.started + allowed:
            raise ReadRateTimeout('request body did not meet minimum read rate')

    def stall_report(self, error, expected):
        """How far a timed-out body got: a stopped transfer and a slow one
        call for different fixes."""
        why = (f'slower than {MIN_READ_BYTES_PER_SECOND} bytes/s'
               if isinstance(error, ReadRateTimeout)
               else f'nothing arrived for {self.idle_seconds} s')
        of = f' of {expected}' if expected is not None else ''
        return (f'request body read timed out, {why}: {self.transferred}{of} '
                f'bytes in {time.monotonic() - self.started:.1f} s, then '
                f'waiting for {self.awaiting} after {self.chunks} chunks')


class LimitedWriter:
    """Count writes, enforce a byte ceiling, and preserve free disk space on
    the volume holding `disk`; None skips that check for in-memory targets."""

    def __init__(self, target, limit, label, limit_error=UploadLimitError,
                 disk=None):
        self.target = target
        self.limit = limit
        self.label = label
        self.limit_error = limit_error
        self.disk = disk
        self.total = 0
        self._until_disk_check = 0

    @property
    def remaining(self):
        return self.limit - self.total

    def write(self, data):
        if not data:
            return 0
        size = len(data)
        if size > self.remaining:
            raise self.limit_error(
                f'{self.label} exceeds {self.limit}-byte limit')
        if self.disk and size >= self._until_disk_check:
            if shutil.disk_usage(self.disk).free < MIN_FREE_BYTES + DISK_CHECK_INTERVAL:
                raise UploadLimitError(
                    f'{self.label} stopped to preserve {MIN_FREE_BYTES} bytes free')
            self._until_disk_check = DISK_CHECK_INTERVAL
        written = self.target.write(data)
        if written != size:
            raise OSError(f'short write for {self.label}: {written} of {size} bytes')
        self.total += written
        self._until_disk_check -= written
        return written


def body_plan(headers, limit):
    """Validate framing before sending 100 Continue or reading any body."""
    transfer = headers.get('Transfer-Encoding', '')
    if transfer:
        encodings = [part.strip().lower() for part in transfer.split(',')]
        if encodings != ['chunked']:
            raise RequestBodyError(f'unsupported Transfer-Encoding: {transfer!r}')
        return 'chunked', None
    value = headers.get('Content-Length')
    if value is None:
        raise RequestBodyError('request body needs Content-Length or chunked encoding')
    try:
        length = int(value)
    except (TypeError, ValueError):
        raise RequestBodyError(f'invalid Content-Length: {value!r}') from None
    if length < 0:
        raise RequestBodyError(f'invalid Content-Length: {value!r}')
    if length > limit:
        raise BodyTooLarge(f'request body exceeds {limit}-byte limit')
    return 'length', length


def _copy_exact(source, writer, size, truncated, deadline):
    # read1 returns what has arrived instead of waiting for a full chunk, so a
    # stalled body is counted to the byte.
    read = getattr(source, 'read1', source.read)
    left = size
    while left:
        deadline.awaiting = f'the last {left} of {size} bytes'
        data = read(min(left, IO_CHUNK_BYTES))
        if not data:
            raise RequestBodyError(truncated)
        deadline.note(len(data))
        writer.write(data)
        left -= len(data)


def _preview(data, limit=48):
    """The first bytes of something that was not what was expected.

    A field report of "invalid chunk-size line" says nothing about whether the
    sender stopped talking or sent a different framing altogether. Both look
    identical in a log message that only names the failure, and they need
    opposite fixes, so the bytes go in the message.
    """
    shown = data[:limit]
    text = ''.join(chr(byte) if 32 <= byte < 127 else '.' for byte in shown)
    tail = '...' if len(data) > limit else ''
    return f'{shown.hex(" ")}{tail} | {text}{tail}'


def _framing_hint(line):
    """Name the cause when the bytes identify it, instead of only the symptom."""
    if line.startswith(b'bplist'):
        # What a de-chunking intermediary leaves behind: the real body, with the
        # header that described the framing still in place.
        return ' - a raw binary plist arrived while the request declared chunked encoding'
    return ''


def _read_chunked(rfile, writer, deadline):
    while True:
        deadline.awaiting = 'a chunk header'
        deadline.between_chunks = deadline.chunks > 0
        line = rfile.readline(MAX_CHUNK_LINE_BYTES + 1)
        deadline.between_chunks = False
        deadline.note(len(line))
        if not line:
            # A closed connection is not a malformed one: the sender gave up
            # before or during the body, and the phone shows "waiting" because
            # it never got an answer. Saying so is the whole difference between
            # chasing a parser bug and chasing the link.
            raise RequestBodyError(
                'sender closed the connection before the body arrived: '
                f'{deadline.transferred} bytes read, then waiting for '
                f'{deadline.awaiting} after {deadline.chunks} chunk(s)')
        if len(line) > MAX_CHUNK_LINE_BYTES:
            raise RequestBodyError(
                f'chunk-size line longer than {MAX_CHUNK_LINE_BYTES} bytes: '
                + _preview(line) + _framing_hint(line))
        if not line.endswith(b'\n'):
            raise RequestBodyError(
                'body is not chunked framing (no line break in the first '
                f'{len(line)} bytes): ' + _preview(line) + _framing_hint(line))
        token = line.split(b';', 1)[0].strip()
        try:
            size = int(token, 16)
        except ValueError:
            raise RequestBodyError(
                f'invalid chunk size: {_preview(line)}' + _framing_hint(line)) from None
        if size < 0:
            raise RequestBodyError('negative chunk size')
        if size == 0:
            trailers = 0
            while True:
                deadline.awaiting = 'the line that ends the body'
                line = rfile.readline(MAX_CHUNK_LINE_BYTES + 1)
                deadline.note(len(line))
                trailers += len(line)
                if not line:
                    raise RequestBodyError(
                        'sender closed the connection inside the body: '
                        f'{deadline.transferred} bytes read, waiting for '
                        f'{deadline.awaiting}')
                if len(line) > MAX_CHUNK_LINE_BYTES or trailers > MAX_TRAILER_BYTES:
                    raise RequestBodyError(
                        'chunk trailers are too long: ' + _preview(line))
                if line in (b'\r\n', b'\n'):
                    return
        if size > writer.remaining:
            raise BodyTooLarge(
                f'request body exceeds {writer.limit}-byte limit')
        _copy_exact(rfile, writer, size, 'truncated chunk data', deadline)
        deadline.awaiting = 'the line break after a chunk'
        ending = rfile.read(2)
        deadline.note(len(ending))
        if ending != b'\r\n':
            raise RequestBodyError('chunk data missing CRLF')
        deadline.chunks += 1


def read_request_body(rfile, headers, target, limit, *, disk=None, plan=None,
                      deadline=None):
    """Stream one framed request body into target under byte and time limits."""
    mode, length = plan or body_plan(headers, limit)
    deadline = deadline or ReadDeadline()
    writer = LimitedWriter(target, limit, 'request body', BodyTooLarge, disk)
    if mode == 'chunked':
        _read_chunked(rfile, writer, deadline)
    else:
        _copy_exact(rfile, writer, length, 'truncated request body', deadline)
    return writer.total


def read_small_body(rfile, headers, limit=MAX_METADATA_BYTES):
    body = io.BytesIO()
    read_request_body(rfile, headers, body, limit)
    return body.getvalue()


def _inflate(decoder, data, writer):
    while data:
        output = decoder.decompress(
            data, min(IO_CHUNK_BYTES, writer.remaining + 1))
        writer.write(output)
        if decoder.unconsumed_tail:
            data = decoder.unconsumed_tail
        else:
            return


def _decode_compressed_block(source, size, writer):
    decoder = zlib.decompressobj()
    left = size
    while left:
        data = source.read(min(left, IO_CHUNK_BYTES))
        if not data:
            raise ValueError(f'truncated compressed block: {left} bytes missing')
        left -= len(data)
        _inflate(decoder, data, writer)
        if decoder.unused_data:
            raise ValueError('compressed block has trailing data')
    writer.write(decoder.flush())
    if not decoder.eof:
        raise ValueError('truncated compressed block')


def outline(value):
    """A plist value with each run of bytes replaced by its length."""
    if isinstance(value, bytes):
        return f'<{len(value)} bytes>'
    if isinstance(value, dict):
        return {key: outline(item) for key, item in value.items()}
    if isinstance(value, list):
        return [outline(item) for item in value]
    return value


def _dvzip_block(data):
    packed = zlib.compress(data, DVZIP_LEVEL)
    if len(packed) < len(data):
        return len(packed).to_bytes(4, 'big') + packed
    return (len(data) | DVZIP_STORED).to_bytes(4, 'big') + data


def encode_dvzip(pieces):
    """Yield the stream in `pieces` as DVZip blocks, each compressed only if
    that makes it smaller."""
    held = bytearray()
    for piece in pieces:
        held += piece
        while len(held) >= DVZIP_BLOCK_BYTES:
            yield _dvzip_block(bytes(held[:DVZIP_BLOCK_BYTES]))
            del held[:DVZIP_BLOCK_BYTES]
    if held:
        yield _dvzip_block(bytes(held))


def decode_dvzip(source, target, byte_limit, disk):
    """Stream a gzip or block-framed DVZip body into a bounded CPIO stream."""
    writer = LimitedWriter(target, byte_limit, 'decompressed archive', disk=disk)
    source.seek(0)
    magic = source.read(2)
    source.seek(0)
    if magic == b'\x1f\x8b':
        decoder = zlib.decompressobj(zlib.MAX_WBITS | 16)
        while True:
            data = source.read(IO_CHUNK_BYTES)
            if not data:
                break
            _inflate(decoder, data, writer)
            if decoder.unused_data:
                raise ValueError('gzip body has trailing data')
        writer.write(decoder.flush())
        if not decoder.eof:
            raise ValueError('truncated gzip body')
        return writer.total

    # DVZip framing: a 4-byte big-endian length per block, whose top bit marks
    # a block stored as is; the others are zlib streams.
    while True:
        header = source.read(4)
        if not header:
            return writer.total
        if len(header) != 4:
            raise ValueError(f'truncated DVZip block header: {len(header)} bytes')
        value = int.from_bytes(header, 'big')
        size = value & 0x7fffffff
        if value & 0x80000000:
            left = size
            while left:
                data = source.read(min(left, IO_CHUNK_BYTES))
                if not data:
                    raise ValueError(f'truncated stored block: {left} bytes missing')
                writer.write(data)
                left -= len(data)
        else:
            _decode_compressed_block(source, size, writer)


class ArchiveEndWatcher:
    """Decodes an upload as it arrives, keeping none of it, to tell when its
    archive is complete. A body it cannot follow is left to the idle timeout."""

    DVZIP_HEADER_BYTES = 4

    def __init__(self):
        self._scanner = cpio.TrailerScanner()
        self._gave_up = False
        self._feed = self._first_bytes
        self._held = b''
        self._inflater = None
        self._block_left = 0
        self.decoded = 0  # bytes of the archive seen so far

    @property
    def gave_up(self):
        return self._gave_up

    def feed(self, wire):
        """Take the next bytes of the body; True once the archive is whole."""
        if self._gave_up:
            return False
        try:
            self._feed(self._held + wire)
        except (ValueError, zlib.error):
            self._gave_up = True
            return False
        return self._block_left == 0 and self._scanner.complete

    def _first_bytes(self, data):
        if len(data) < 2:
            self._held = data
            return
        self._held = b''
        if data[:2] == b'\x1f\x8b':
            self._inflater = zlib.decompressobj(zlib.MAX_WBITS | 16)
            self._feed = self._inflate
        else:
            self._feed = self._blocks
        self._feed(data)

    def _inflate(self, data):
        while data:
            plain = self._inflater.decompress(data, IO_CHUNK_BYTES)
            self.decoded += len(plain)
            self._scanner.feed(plain)
            data = self._inflater.unconsumed_tail

    def _blocks(self, data):
        view = memoryview(data)
        while len(view):
            if self._block_left == 0:
                if len(view) < self.DVZIP_HEADER_BYTES:
                    self._held = bytes(view)
                    return
                header = int.from_bytes(view[:self.DVZIP_HEADER_BYTES], 'big')
                view = view[self.DVZIP_HEADER_BYTES:]
                self._block_left = header & 0x7fffffff
                stored = header & 0x80000000
                self._inflater = None if stored else zlib.decompressobj()
            piece = view[:self._block_left]
            view = view[len(piece):]
            self._block_left -= len(piece)
            if self._inflater:
                self._inflate(bytes(piece))
            else:
                self.decoded += len(piece)
                self._scanner.feed(piece)
        self._held = b''


class EndWatchingFile:
    """Writes an upload to `target` and calls `on_complete`, once, when what
    has been written holds a complete archive. `on_progress` is told how many
    bytes of the archive have arrived, or of the body if that cannot be read."""

    def __init__(self, target, on_complete, on_progress=None):
        self._target = target
        self._on_complete = on_complete
        self._on_progress = on_progress
        self._watcher = ArchiveEndWatcher()
        self._complete = False
        self._wire = 0

    def write(self, data):
        written = self._target.write(data)
        self._wire += len(data)
        if not self._complete and self._watcher.feed(data):
            self._complete = True
            self._on_complete()
        if self._on_progress:
            self._on_progress(self._wire if self._watcher.gave_up else self._watcher.decoded)
        return written


def safe_name(name, fallback):
    """Return one non-hidden filesystem component from sender-controlled text."""
    name = unicodedata.normalize('NFC', name.replace('\\', '/').rsplit('/', 1)[-1])
    # Windows drops trailing dots and spaces itself, which would let two
    # different names land on one file.
    name = UNSAFE_CHAR.sub('', name).strip().lstrip('.').rstrip('. ')
    stem, ext = os.path.splitext(name)
    if DEVICE_NAME.match(stem.split('.', 1)[0]):
        stem = '_' + stem
    while len((stem + ext).encode('utf-16-le')) // 2 > MAX_NAME_UNITS and stem:
        stem = stem[:-1]
    return (stem + ext) or fallback


def open_new(dest, name):
    """Create dest/name atomically without clobbering an existing file."""
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, 'O_BINARY', 0)
    for i in range(MAX_NAME_ATTEMPTS):
        cand = numbered(name, i)
        try:
            fd = os.open(os.path.join(dest, cand), flags, 0o644)
        except FileExistsError:
            continue
        return cand, os.fdopen(fd, 'wb')
    raise FileExistsError(name)


def mark_as_downloaded(path):
    """Tag a stored file as coming from an untrusted zone. Only NTFS has the
    stream this lives in; elsewhere there is nothing to tag."""
    if os.name != 'nt':
        return
    try:
        with open(path + ':Zone.Identifier', 'wb') as mark:
            mark.write(ZONE_MARK)
    except OSError:
        pass


if os.name == 'nt':
    import ctypes
    from ctypes import wintypes

    _SE_FILE_OBJECT = 1
    _DACL_SECURITY_INFORMATION = 0x00000004
    _UNPROTECTED_DACL_SECURITY_INFORMATION = 0x20000000
    _ACL_REVISION = 2
    _ACL_SIZE_INFORMATION = 2
    _INHERITED_ACE = 0x10

    class _AclSize(ctypes.Structure):
        _fields_ = [('AceCount', wintypes.DWORD),
                    ('AclBytesInUse', wintypes.DWORD),
                    ('AclBytesFree', wintypes.DWORD)]

    class _AceHead(ctypes.Structure):
        _fields_ = [('AceType', ctypes.c_ubyte),
                    ('AceFlags', ctypes.c_ubyte),
                    ('AceSize', wintypes.WORD)]

    _advapi32 = ctypes.WinDLL('advapi32', use_last_error=True)
    _kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    _advapi32.SetNamedSecurityInfoW.restype = wintypes.DWORD
    _advapi32.SetNamedSecurityInfoW.argtypes = [
        wintypes.LPWSTR, ctypes.c_int, wintypes.DWORD, ctypes.c_void_p,
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p]
    _advapi32.GetNamedSecurityInfoW.restype = wintypes.DWORD
    _advapi32.GetNamedSecurityInfoW.argtypes = [
        wintypes.LPWSTR, ctypes.c_int, wintypes.DWORD,
        ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p),
        ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_void_p),
        ctypes.POINTER(ctypes.c_void_p)]
    _advapi32.GetSecurityDescriptorDacl.restype = wintypes.BOOL
    _advapi32.GetSecurityDescriptorDacl.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(wintypes.BOOL),
        ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(wintypes.BOOL)]
    _advapi32.GetAclInformation.restype = wintypes.BOOL
    _advapi32.GetAclInformation.argtypes = [
        ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.c_int]
    _advapi32.GetAce.restype = wintypes.BOOL
    _advapi32.GetAce.argtypes = [
        ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(ctypes.c_void_p)]
    _advapi32.InitializeAcl.restype = wintypes.BOOL
    _advapi32.InitializeAcl.argtypes = [
        ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD]
    _kernel32.LocalFree.argtypes = [ctypes.c_void_p]


def _dacl_from(path):
    """The DACL Windows stored on `path`, or None if it cannot be read."""
    descriptor = ctypes.c_void_p()
    if _advapi32.GetNamedSecurityInfoW(
            path, _SE_FILE_OBJECT, _DACL_SECURITY_INFORMATION,
            None, None, None, None, ctypes.byref(descriptor)) != 0:
        return None, None
    present = wintypes.BOOL()
    acl = ctypes.c_void_p()
    defaulted = wintypes.BOOL()
    if not _advapi32.GetSecurityDescriptorDacl(
            descriptor, ctypes.byref(present), ctypes.byref(acl),
            ctypes.byref(defaulted)):
        _kernel32.LocalFree(descriptor)
        return None, None
    return descriptor, (acl if present.value else None)


def _inherits_anything(path):
    """True when some entry in the DACL was inherited from the folder."""
    descriptor, acl = _dacl_from(path)
    if descriptor is None:
        return True  # cannot tell; leave the file alone
    try:
        if not acl:
            return False
        size = _AclSize()
        if not _advapi32.GetAclInformation(
                acl, ctypes.byref(size), ctypes.sizeof(size),
                _ACL_SIZE_INFORMATION):
            return True
        for index in range(size.AceCount):
            entry = ctypes.c_void_p()
            if not _advapi32.GetAce(acl, index, ctypes.byref(entry)):
                continue
            head = ctypes.cast(entry, ctypes.POINTER(_AceHead)).contents
            if head.AceFlags & _INHERITED_ACE:
                return True
        return False
    finally:
        _kernel32.LocalFree(descriptor)


def inherit_permissions(path):
    """Give `path` back the permissions of the folder it sits in.

    Windows hands a file the DACL of whatever folder created it, and a folder
    made with mode 0o700 gets a DACL naming only its owner, SYSTEM and
    Administrators. A hard link carries that DACL to the published name, so a
    receiver running elevated published photos that only an elevated process
    could open: the notification preview showed the picture and Explorer told
    the person who received it that access was denied.

    Setting an empty, unprotected DACL makes Windows merge in exactly the
    entries the folder inherits, which is what a file created there normally
    gets. Nothing is granted that the folder does not already grant.
    """
    if os.name != 'nt':
        return True
    empty = ctypes.create_string_buffer(1024)
    if not _advapi32.InitializeAcl(empty, len(empty), _ACL_REVISION):
        return False
    return _advapi32.SetNamedSecurityInfoW(
        path, _SE_FILE_OBJECT,
        _DACL_SECURITY_INFORMATION | _UNPROTECTED_DACL_SECURITY_INFORMATION,
        None, None, empty, None) == 0


def _is_received(path):
    """True for a file this receiver wrote: they all carry the zone mark."""
    try:
        with open(path + ':Zone.Identifier', 'rb') as mark:
            return mark.read(len(ZONE_MARK)) == ZONE_MARK
    except OSError:
        return False


def repair_permissions(dest):
    """Fix files received before published names inherited the folder's ACL.

    Only files carrying this receiver's own mark are touched, and only when
    nothing in their DACL was inherited: the receive folder can be pointed at
    Downloads, and other files there are not ours to rewrite.
    """
    repaired = 0
    try:
        entries = list(os.scandir(dest))
    except OSError:
        return 0
    for entry in entries:
        try:
            if not entry.is_file() or not _is_received(entry.path):
                continue
            if _inherits_anything(entry.path):
                continue
            if inherit_permissions(entry.path):
                repaired += 1
        except OSError:
            continue
    return repaired


class ExtractionBudget:
    def __init__(self, byte_limit):
        self.byte_limit = byte_limit
        self.members = 0
        self.output_bytes = 0

    def count_member(self):
        self.members += 1
        if self.members > MAX_ARCHIVE_MEMBERS:
            raise UploadLimitError(
                f'archive exceeds {MAX_ARCHIVE_MEMBERS}-member limit')


class ExtractedFileWriter(LimitedWriter):
    def __init__(self, target, budget, name, disk):
        super().__init__(target, budget.byte_limit, f'file {name!r}', disk=disk)
        self.budget = budget

    def write(self, data):
        if self.budget.output_bytes + len(data) > self.budget.byte_limit:
            raise UploadLimitError(
                f'archive output exceeds {self.budget.byte_limit}-byte limit')
        written = super().write(data)
        self.budget.output_bytes += written
        return written


def live_photo_folder(pathname):
    """The folder a Live Photo's member sits in, or None for any other file:
    a Live Photo arrives as a folder named *.pvt."""
    for part in pathname.split('/')[:-1]:
        if part.lower().endswith(LIVE_PHOTO_FOLDER_SUFFIX):
            return part
    return None


class Stored(str):
    """The name a file was stored under. `live_photo` is the folder the two
    files of a Live Photo came in, and None for any other file."""

    def __new__(cls, name, live_photo=None):
        stored = super().__new__(cls, name)
        stored.live_photo = live_photo
        return stored


def live_photos(names):
    """[still, video] for each Live Photo whose two files are among these
    stored names. Two files that only share a stem are not one."""
    folders = {}
    for name in names:
        folder = getattr(name, 'live_photo', None)
        if folder:
            folders.setdefault(folder, []).append(name)
    pairs = []
    for files in folders.values():
        videos = [name for name in files if name.lower().endswith(LIVE_PHOTO_VIDEO_SUFFIX)]
        stills = [name for name in files if name not in videos]
        if len(videos) == 1 and len(stills) == 1:
            pairs.append([stills[0], videos[0]])
    return pairs


def free_number(dest, stem):
    """The first number under which no file in dest has this stem, whatever
    its extension; 0 stands for the bare stem."""
    taken = {os.path.splitext(name)[0].casefold() for name in os.listdir(dest)}
    for number in range(MAX_NAME_ATTEMPTS):
        if numbered(stem, number).casefold() not in taken:
            return number
    raise FileExistsError(stem)


def numbered(name, number):
    stem, ext = os.path.splitext(name)
    return name if number == 0 else f'{stem}-{number}{ext}'


# An iPhone names a photo after its own asset key:
# "C8F41611-1285-46BD-885D-D27C8B6C9ED6.PNG". That is a database id, not a
# name. A receive folder full of them cannot be read at a glance, and a photo
# nobody recognises looks like a photo that never arrived.
ASSET_KEY = re.compile(
    r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$',
    re.IGNORECASE)
VIDEO_SUFFIXES = ('.mov', '.mp4', '.m4v', '.avi')


def readable_name(name, when=None):
    """Turn a bare asset key into "照片 2026-11-25 02.18.32.png".

    Only names that are nothing but an asset key are touched: anything the
    sender actually named keeps its name, because that name may be the only
    thing the sender recognises on the other side.
    """
    stem, ext = os.path.splitext(name)
    if not ext or not ASSET_KEY.match(stem):
        return name
    kind = '视频' if ext.lower() in VIDEO_SUFFIXES else '照片'
    stamp = time.strftime(' %Y-%m-%d %H.%M.%S', when or time.localtime())
    return kind + stamp + ext.lower()


def _extract(archive, dest, budget, created):
    written = []
    live_photo_numbers = {}
    for entry in cpio.read_entries(archive):
        budget.count_member()
        logging.debug('archive member %r, %d bytes, %s', entry.pathname, entry.size,
                      'a file' if entry.isfile else 'not a file')
        # Archive paths are split on "/" alone: a name may itself hold
        # backslashes. AppleDouble companions ("._name") carry Finder metadata
        # only, and a Live Photo's metadata only pairs its two files for Photos.
        leaf = entry.pathname.rsplit('/', 1)[-1]
        live_photo = live_photo_folder(entry.pathname)
        if (not entry.isfile or leaf.startswith('._')
                or (live_photo and leaf == LIVE_PHOTO_METADATA)):
            continue
        if budget.output_bytes + entry.size > budget.byte_limit:
            raise UploadLimitError(
                f'archive declares more than {budget.byte_limit} output bytes')
        wanted = safe_name(entry.pathname, 'file')
        if live_photo:
            # The still and the video share a stem, and keep sharing it when
            # an earlier file has taken the name of one of them.
            if live_photo not in live_photo_numbers:
                live_photo_numbers[live_photo] = free_number(
                    dest, os.path.splitext(wanted)[0])
            wanted = numbered(wanted, live_photo_numbers[live_photo])
        else:
            # Never for a Live Photo: its two members must keep one stem, and
            # two calls to readable_name can straddle a second boundary.
            wanted = readable_name(wanted)
        name, output = open_new(dest, wanted)
        created.append(name)
        with output:
            sink = ExtractedFileWriter(output, budget, name, dest)
            for block in entry.blocks():
                sink.write(block)
        mark_as_downloaded(os.path.join(dest, name))
        written.append(Stored(name, live_photo))
    return written


def publish_staged(staging, dest, names):
    """Publish complete files with no-replace hard links on the same volume.

    Filesystems without hard-link support fail closed. A multi-file upload is
    not a single atomic transaction; files already linked stay if a later link
    fails, and the caller must report failure rather than success.
    """
    published = []
    groups = {}
    for item in names:
        name = str(item)
        source = os.path.join(staging, name)
        # Flush completed file data before its final name is exposed.
        with open(source, 'rb+') as complete:
            os.fsync(complete.fileno())
        group = getattr(item, 'live_photo', None)
        if group is not None:
            if group not in groups:
                groups[group] = free_number(dest, os.path.splitext(name)[0])
            name = numbered(name, groups[group])
        for number in range(MAX_NAME_ATTEMPTS):
            candidate = numbered(name, number)
            published_path = os.path.join(dest, candidate)
            try:
                # link() atomically creates a name and fails if it exists.
                os.link(source, published_path)
            except FileExistsError:
                continue
            # The published name shares the staging file's security
            # descriptor, so whatever the staging folder handed down lands on
            # a file the user is about to open. Make sure that is the receive
            # folder's own set of permissions.
            if not inherit_permissions(published_path):
                logging.warning('upload: %r published with its staging '
                                'permissions, not the folder\'s', candidate)
            published.append(Stored(candidate, group))
            break
        else:
            raise FileExistsError(name)
    return published


@contextlib.contextmanager
def staging_folder(dest):
    """A scratch folder inside dest that keeps dest's own permissions.

    tempfile.mkdtemp is the obvious choice and the wrong one here. Since
    Python 3.12 a 0o700 folder on Windows gets a DACL naming only its owner,
    SYSTEM and Administrators, and a hard link carries that DACL to the
    published file. The receiver runs elevated, so its owner is the
    Administrators group: the file arrived, the notification preview showed
    it, and the person double-clicking it in Explorer was told access was
    denied.
    """
    path = os.path.join(dest, '.droplink-' + uuid.uuid4().hex)
    # No mode: a mode would become a DACL. Without one the folder inherits
    # the receive folder's entries, and so does everything created in it.
    os.mkdir(path)
    try:
        yield path
    finally:
        shutil.rmtree(path, ignore_errors=True)


def store_upload(raw, dest, tid, receive_budget):
    """Decode and validate the full archive before publishing complete files."""
    with staging_folder(dest) as staging:
        with tempfile.TemporaryFile(dir=staging) as archive:
            raw.seek(0)
            logging.debug('upload %s: body starts with %s', tid, raw.read(8).hex())
            decoded = decode_dvzip(raw, archive, receive_budget.byte_limit, dest)
            raw.seek(0)
            raw.truncate(0)
            archive.seek(0)
            logging.info('upload %s: decoded to %d bytes', tid, decoded)
            names = _extract(archive, staging,
                             ExtractionBudget(receive_budget.byte_limit), [])
        return publish_staged(staging, dest, names)


def is_web_link(item):
    return (isinstance(item, str) and item.startswith(('http://', 'https://'))
            and not LINK_BREAKING_CHAR.search(item))


def store_link(url, dest):
    """Write a received URL into dest as an inert .url shortcut and return the
    stored name. The URL is written as a value and never opened here."""
    parts = urlsplit(url)
    stem = '-'.join(p for p in (parts.netloc, parts.path.rstrip('/').rsplit('/', 1)[-1]) if p)
    name, f = open_new(dest, safe_name(stem, 'link') + '.url')
    with f:
        f.write(f'[InternetShortcut]\r\nURL={url}\r\n'.encode())
    mark_as_downloaded(os.path.join(dest, name))
    return name
