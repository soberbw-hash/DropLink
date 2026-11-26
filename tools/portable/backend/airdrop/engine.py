# SPDX-License-Identifier: MIT
"""The AirDrop receiver and sender as one process for a program to drive: it
reads one JSON command a line and writes one JSON event a line, and its log
goes to the error stream. It receives for as long as the AWDL adapter is up,
and looks for receivers only while told to, because looking wakes every Apple
device in Bluetooth range.

Commands: {"do": "look", "on": true}, {"do": "send", "id": ..., "peer": ...,
"files": [...]}, {"do": "cancel", "id": ...}, {"do": "answer", "id": ...,
"accept": true}, {"do": "set", "name": ..., "folder": ..., "ask": ...},
{"do": "quit"}. The end of the input is a quit as well."""
import argparse
import contextlib
import errno
import http.client
import ipaddress
import os
import json
import logging
import secrets
import socket
import subprocess
import sys
import threading
import time
import unicodedata

import identity
import mdns
import netif
import receiver
import sender
import transfer

PROTOCOL = 1
LINK_POLL_SECONDS = 1
# A receiver that has not been announced for this long has left.
PEER_SILENCE_SECONDS = 20
# How often a receiver that gave no name yet is asked again, and how often
# one given on the command line is checked.
PROBE_PAUSE_SECONDS = 2
# One that will not talk to this computer is asked again less often: only
# its owner opening it to everyone changes the answer.
UNWILLING_PAUSE_SECONDS = 10
PROGRESS_GAP_SECONDS = 0.1
ASK_POLL_SECONDS = 0.25
# A sender that neither leaves nor gets an answer is given up on after this.
ASK_LONGEST_SECONDS = 300
# An accepted sender that uploads nothing is given up on after this.
UPLOAD_DUE_SECONDS = 30
BIND_RETRY_SECONDS = 5
LOOPBACK = ('::1', 0)


class Events:
    """Writes events for the driving program. `closed` is set when it has
    stopped reading."""

    def __init__(self, stream):
        self._stream = stream
        self._lock = threading.Lock()
        self.closed = threading.Event()

    def say(self, event, **fields):
        line = json.dumps({'event': event, **fields}, ensure_ascii=False)
        with self._lock:
            try:
                self._stream.write(line + '\n')
                self._stream.flush()
            except (OSError, ValueError):
                self.closed.set()


class Settings:
    """What the receiver reads on every request, so a change applies to the
    next one."""

    def __init__(self, args):
        self.name = args.name
        self.model = args.model
        self.dest = args.dest
        self.ask = args.ask
        self.port = args.port
        self.flags = args.flags
        self.max_receive_percent = args.max_receive_percent
        self.auto_receive = bool(getattr(args, 'auto_receive', False))
        # Paused means "decline what arrives" without tearing the radio down.
        # Rebuilding the chain costs 40-60 s, so stopping the session to pause
        # would make resuming feel broken.
        self.paused = False
        self.redact = False
        self.interface_index = 0

    def public(self):
        return {'name': self.name, 'folder': self.dest, 'ask': self.ask}


class Accepted:
    """One incoming transfer from its ask to its last file."""

    def __init__(self, key, total):
        self.key = key
        self.total = total
        self.folder = None
        self.uploading = False
        self.said = 0.0


STILL_SUFFIXES = ('.jpg', '.jpeg', '.png', '.heic', '.tif', '.tiff', '.bmp', '.webp')


def make_still_copies(folder, names):
    """Copy the still image of each received Live Photo (*.pvt) into the
    receive folder under a normal image name, so Windows shows a plain photo
    instead of a hidden .pvt folder. Best-effort; never raises."""
    try:
        cfg = load_settings()
        if not cfg.get('live_photo_still_copy', True):
            return []
        pairs = transfer.live_photos(names)
        made = []
        for still, _video in pairs:
            # still may be 'IMG_0001.pvt/IMG_0001.HEIC' (a Stored str).
            src = os.path.join(folder, str(still))
            if not os.path.exists(src):
                continue
            # Basename of the still, flattened out of the .pvt folder.
            leaf = os.path.basename(str(still))
            stem, ext = os.path.splitext(leaf)
            if ext.lower() not in STILL_SUFFIXES:
                continue
            dest = os.path.join(folder, leaf)
            if os.path.abspath(src) == os.path.abspath(dest) or os.path.exists(dest):
                continue
            # Avoid clobbering an existing unrelated file.
            base, n = stem, 1
            while os.path.exists(dest):
                dest = os.path.join(folder, '%s_%d%s' % (base, n, ext))
                n += 1
            import shutil
            shutil.copy2(src, dest)
            made.append(os.path.basename(dest))
            logging.info('live photo still copy -> %s', dest)
        return made
    except Exception as error:
        logging.warning('live photo still copy failed: %s', error)
        return []


# How much text fits on one toast line. Measured against the real toast on
# Windows 11 at 100%: about 38 columns, where a CJK character costs two.
TOAST_LINE_WIDTH = 38


def _toast_width(text):
    return sum(2 if unicodedata.east_asian_width(ch) in ('W', 'F') else 1
               for ch in text)


def fit_name(name, limit=TOAST_LINE_WIDTH):
    """Shorten a file name to one toast line, keeping the extension intact.

    The shell wraps a long <text> element instead of clipping it, and a wrapped
    file name reads as two unrelated lines. Cutting the *stem* keeps the useful
    part - "81FA1FEE-A0D6-4CB0-B930-D25D8B52….PNG" still says what the file is,
    while "…D25D8B52E7F6.PNG" says nothing at all.
    """
    name = str(name)
    if _toast_width(name) <= limit:
        return name
    stem, ext = os.path.splitext(name)
    room = limit - _toast_width(ext) - 1  # one column for the ellipsis
    if room <= 0:
        return '…' + ext
    kept = ''
    used = 0
    for ch in stem:
        width = 2 if unicodedata.east_asian_width(ch) in ('W', 'F') else 1
        if used + width > room:
            break
        kept += ch
        used += width
    return kept + '…' + ext


def toast_text(names, show_format=True, live=None):
    """Title and optional second line for the completion toast.

    The notification already carries the app name in its header, so starting the
    line with "DropLink 收到" only ate the space the file name needed. The name
    itself is the whole message: one line, extension preserved.

    A second line is kept only when it adds something a file name cannot show -
    a Live Photo's *.pvt container, where the still copy is what the PC can open.

    Kept as a plain function so the wording can be tested without a desktop.
    """
    names = [str(n) for n in names]
    if live:
        # A Live Photo is one photo on the phone and two files here. Saying
        # "2 个文件" is not an explanation, and the video is the half that
        # will not open on a PC - which reads as "the file is broken".
        still, video = str(live[0][0]), str(live[0][1])
        prefix = '实况图 · '
        title = prefix + fit_name(os.path.basename(still),
                                  TOAST_LINE_WIDTH - _toast_width(prefix))
        tail = '视频 '
        return title, tail + fit_name(os.path.basename(video),
                                      TOAST_LINE_WIDTH - _toast_width(tail))
    count = len(names)
    if count == 1:
        title = fit_name(names[0])
        body = ''
    elif count > 1:
        # The count prefix eats into the same line, so the name gets what is
        # left of it - otherwise "2 个文件 · <38 columns>" wraps and the whole
        # point of the one-line rule is lost.
        prefix = '%d 个文件 · ' % count
        title = prefix + fit_name(names[0], TOAST_LINE_WIDTH - _toast_width(prefix))
        body = ''
    else:
        title = '已保存到接收文件夹'
        body = ''

    if show_format and names:
        try:
            opaque = [n for n in names
                      if os.path.splitext(n)[1].lower() in ('', '.pvt')]
            # *.pvt is a container, not something the PC can open, so it is
            # never reported as a format. What matters is the still copy.
            suffixes = {os.path.splitext(n)[1].lstrip('.').upper() for n in names
                        if os.path.splitext(n)[1].lower() != '.pvt'}
            suffixes = {s for s in suffixes if s}
            if opaque and suffixes:
                # Not "电脑可查看": a file extension is not proof that this
                # machine has the codec. HEIC needs the HEIF extension and HEVC
                # needs a codec pack, neither of which ships with Windows. Say
                # what else is there, do not promise it will open.
                line = '另有：' + ' '.join(sorted(suffixes))
                body = (body + '\n' + line) if body else line
        except Exception:
            pass
    return title, body


def notify_received(folder, names, live=None, batch=None):
    """Best-effort Windows toast on completion; never blocks or breaks reception."""
    def _run():
        try:
            import base64
            script = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'notify.ps1')
            if not os.path.exists(script):
                return
            title, label = toast_text(names, load_settings().get(
                'show_format_in_notification', True), live)
            def b64(text):
                # PowerShell drops an empty argument outright, which would make
                # -MessageB64 / -ImageB64 bind to nothing and kill the whole
                # toast. '~' is the agreed stand-in for "empty".
                return base64.b64encode(str(text).encode('utf-8')).decode('ascii') or '~'
            # Clipboard payload: the received files themselves, one full path
            # per line. The toast button puts those files on the clipboard, so
            # pasting into a chat or an editor yields the photo - copying a bare
            # file name is not "copy to clipboard" in any sense the user means.
            clip_text = '\n'.join(os.path.join(folder, str(n)) for n in names) if names else ''
            # Which file the "open folder" button should select. Without it the
            # folder opens on a list of UUID names and the photo reads as lost.
            first_name = str(names[0]) if names else ''
            # Inline preview: first received image that actually exists on
            # disk. Stored() names may be plain strings or Stored objects.
            image_suffixes = ('.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp', '.heic', '.tif', '.tiff')
            preview = ''
            for n in names:
                filename = getattr(n, 'name', None) or str(n)
                if filename.lower().endswith(image_suffixes):
                    candidate = os.path.join(folder, filename)
                    if os.path.exists(candidate):
                        preview = candidate
                        break
            args = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                    '-File', script,
                    '-FolderB64', b64(folder),
                    '-TitleB64', b64(title),
                    '-MessageB64', b64(label),
                    '-ClipB64', b64(clip_text),
                    '-OpenLabelB64', b64('打开接收文件夹'),
                    '-ClipLabelB64', b64('复制到剪贴板'),
                    '-ImageB64', b64(preview),
                    '-SelectB64', b64(first_name),
                    # The button payload is a history id, not a path: a protocol
                    # URI is world-invokable, so it must not be able to aim
                    # Explorer at any folder it likes.
                    '-BatchB64', b64(batch or '')]
            # CREATE_NO_WINDOW: the engine is started without a console, so a
            # console child would otherwise get a fresh, *visible* one - a black
            # PowerShell box popping up right after a file arrives.
            creation = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
            subprocess.run(args, timeout=20, stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, check=False,
                           creationflags=creation)
        except Exception as error:
            logging.warning('toast notification failed: %s', error)
    threading.Thread(target=_run, name='toast', daemon=True).start()


def failure_toast_text(reason):
    if str(reason or '').startswith('storage failed:'):
        return '文件保存失败', '请检查保存位置后重试。'
    return '传输未完成', '请在 iPhone 上重新发送。'


def notify_failed(reason):
    """Keep technical detail in the lost event and receiver log."""
    def _run():
        try:
            import base64
            script = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'notify.ps1')
            if not os.path.exists(script):
                return
            def b64(text):
                return base64.b64encode(str(text).encode('utf-8')).decode('ascii') or '~'
            title, message = failure_toast_text(reason)
            args = ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                    '-File', script,
                    '-FolderB64', b64('~'),
                    '-TitleB64', b64(title),
                    '-MessageB64', b64(message),
                    '-NoActions']
            creation = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
            subprocess.run(args, timeout=20, stdout=subprocess.DEVNULL,
                           stderr=subprocess.DEVNULL, check=False,
                           creationflags=creation)
        except Exception as error:
            logging.warning('failure toast failed: %s', error)
    threading.Thread(target=_run, name='toast-failed', daemon=True).start()


def announced_files(ask):
    files = []
    for entry in ask.get('Files') or []:
        size = entry.get('FileSize')
        files.append({
            'name': transfer.safe_name(str(entry.get('FileName') or ''), 'file'),
            'size': size if isinstance(size, int) and size >= 0 else None,
            'type': str(entry.get('FileType') or ''),
        })
    return files


class Incoming(receiver.Watcher):
    """Turns what the receiver sees into events, and holds a sender's ask
    until the driving program answers it."""

    def __init__(self, events, settings):
        self.events = events
        self.settings = settings
        self._lock = threading.Lock()
        self._waiting = {}

    def _open(self, sender_name, model, files, links, waits):
        with self._lock:
            # History and notification buttons outlive the receiver process.
            key = 'r' + secrets.token_hex(16)
            if waits:
                # Before the request is said: its answer may come back at once.
                self._waiting[key] = {'made': threading.Event(), 'accept': False}
        sizes = [file['size'] for file in files]
        total = sum(sizes) if sizes and None not in sizes else None
        # Every transfer is announced, in both modes. It used to be silent in
        # auto-receive mode, and the host only created a row for an 'asked' that
        # was waiting for an answer - so an auto-accepted transfer had no row at
        # all and its progress events were dropped on the floor.
        self.events.say('transfer_created', id=key, sender=sender_name, model=model,
                        files=files, links=links, total=total,
                        files_count=len(files), requires_confirmation=bool(waits),
                        mode='confirm' if waits else 'auto')
        if getattr(self.settings, 'auto_receive', False):
            logging.info('auto-accepted transfer from %s: %s', sender_name,
                         ', '.join(f['name'] for f in files))
        return Accepted(key, total)

    def asked(self, ask, address, left):
        kinds = ask.get('TransferType') or {}
        links = sum(1 for item in ask.get('Items') or [] if transfer.is_web_link(item)) \
            if 'links' in kinds else 0
        if getattr(self.settings, 'paused', False):
            # Paused is a real refusal, not a silent drop: the sender sees the
            # transfer declined and the host gets an honest 'answered' event.
            accepted = self._open(ask.get('SenderComputerName'), ask.get('SenderModelName'),
                                  announced_files(ask), links, False)
            self.events.say('answered', id=accepted.key, how='declined')
            return None
        waits = self.settings.ask and not getattr(self.settings, 'auto_receive', False)
        accepted = self._open(ask.get('SenderComputerName'), ask.get('SenderModelName'),
                              announced_files(ask), links, waits)
        how = self._decision(accepted.key, left) if waits else 'accepted'
        self.events.say('answered', id=accepted.key, how=how)
        if how != 'accepted':
            return None
        accepted.folder = self.settings.dest
        if not links:
            due = threading.Timer(UPLOAD_DUE_SECONDS, self._overdue, [accepted])
            due.daemon = True
            due.start()
        return accepted

    def _decision(self, key, left):
        with self._lock:
            slot = self._waiting[key]
        try:
            deadline = time.monotonic() + ASK_LONGEST_SECONDS
            while time.monotonic() < deadline:
                if slot['made'].wait(ASK_POLL_SECONDS):
                    return 'accepted' if slot['accept'] else 'declined'
                if left():
                    return 'left'
            return 'expired'
        finally:
            with self._lock:
                del self._waiting[key]

    def answer(self, key, accept):
        with self._lock:
            slot = self._waiting.get(key)
        if slot:
            slot['accept'] = accept
            slot['made'].set()
        return bool(slot)

    def _overdue(self, accepted):
        if not accepted.uploading:
            accepted.uploading = True
            self.events.say('lost', id=accepted.key, reason='nothing arrived')

    def upload_started(self, accepted, tid, total):
        if accepted is None:
            # Asking first means nothing comes in that was not asked about.
            if self.settings.ask:
                return None
            accepted = self._open(None, None, [], 0, False)
            self.events.say('answered', id=accepted.key, how='accepted')
        elif accepted.uploading:
            return None
        accepted.uploading = True
        accepted.total = total or accepted.total
        if accepted.folder is None:
            accepted.folder = self.settings.dest
        self.events.say('incoming', id=accepted.key, done=0, total=accepted.total)
        return accepted

    def upload_progress(self, token, done):
        now = time.monotonic()
        if now - token.said < PROGRESS_GAP_SECONDS:
            return
        token.said = now
        # The archive is a little longer than the files in it.
        shown = min(done, token.total) if token.total else done
        self.events.say('incoming', id=token.key, done=shown, total=token.total)

    def stored(self, token, names):
        folder = token.folder or self.settings.dest
        live = transfer.live_photos(names)
        self.events.say('stored', id=token.key, folder=folder,
                        names=names, live=live)
        if getattr(self.settings, 'auto_receive', False):
            extra = make_still_copies(folder, names)
            shown = [str(n) for n in names] + extra
            notify_received(folder, shown, live, batch=token.key)

    def upload_failed(self, token, reason):
        self.events.say('lost', id=token.key, reason=reason)
        # The window is usually in the tray, and a failed upload used to leave
        # nothing behind but a log line and a row in a list nobody was looking
        # at. "It just stopped" was the whole story the user got.
        notify_failed(reason)


class Receiving:
    """The receiver and its announcement, for as long as the link is up."""

    def __init__(self, events, settings, watcher, context, loopback, proxy=False, keys=None, proxy_port=15353):
        self.events = events
        self.settings = settings
        self.watcher = watcher
        self.context = context
        self.loopback = loopback
        self.proxy = proxy
        self.keys = keys
        self.proxy_port = proxy_port
        self.server = None
        self.thread = None
        self.announcer = None
        self._said = None

    def _say(self, on, reason=None):
        if (on, reason) != self._said:
            self._said = (on, reason)
            # In the log too, not only on the IPC stream. A receiver that stops
            # because the link went away leaves a user staring at a phone that
            # "does nothing", and until now the only trace was an event the UI
            # happened to be listening for.
            logging.info('receiver %s%s', 'up' if on else 'down',
                         '' if reason is None else f': {reason}')
            self.events.say('receiving', on=on, reason=reason, port=self.settings.port)

    def start(self, index, address):
        """Whether the receiver runs now. A port held by another program is
        reported and tried again by the caller."""
        if self.server:
            if self.thread.is_alive():
                return self.ensure_announcement(index, address)
            self.stop('listener_stopped')
        self.settings.interface_index = index
        try:
            server = receiver.Receiver(self.settings.port, self.settings, self.watcher, proxy=self.proxy)
        except OSError as error:
            held = error.errno == errno.EADDRINUSE
            self._say(False, 'port_held' if held else str(error))
            return False
        self.settings.port = server.server_address[1]
        server.socket = self.context.wrap_socket(server.socket, server_side=True,
                                                 do_handshake_on_connect=False)
        self.server = server
        self.thread = threading.Thread(target=server.serve_forever, name='receiver',
                                       daemon=True)
        self.thread.start()
        logging.info('listening as %s on port %d into %s', self.settings.name,
                     self.settings.port, self.settings.dest)
        return self.ensure_announcement(index, address)

    def ensure_announcement(self, index, address):
        if self.proxy or not self.loopback:
            if self.announcer is not None:
                if self.announcer.thread.is_alive():
                    return True
                self._say(False, 'announcement_stopped')
                self.announcer.stop()
                self.announcer = None
            service_id = (receiver.stable_service_id(address, self.keys)
                          if self.proxy else secrets.token_hex(6))
            try:
                service = mdns.Service(receiver.SERVICE_TYPE, service_id, f'{service_id}.local',
                                       address, self.settings.port, f'flags={self.settings.flags}')
                announcer = (receiver.ProxyAnnouncer(service, self.proxy_port, self.keys)
                             if self.proxy else mdns.Announcer(service, index))
                announcer.start()
                self.announcer = announcer
            except OSError as error:
                self._say(False, str(error))
                return False
        self._say(True)
        return True

    def stop(self, reason=None):
        if self.announcer:
            self.announcer.stop()
            self.announcer = None
        if self.server:
            self.server.shutdown()
            self.thread.join()
            self.server.reset_sessions()
            self.server.server_close()
            self.server = None
            self._say(False, reason)


class Peer:
    def __init__(self, address, port):
        self.address = address
        self.port = port
        self.name = None
        self.model = None
        self.probing = False
        self.next_probe = 0.0
        self.shy = False  # answers only devices it knows


class Looking:
    """Finds receivers while told to: wakes them over Bluetooth, hears them
    announce themselves and asks each for its name."""

    def __init__(self, events, context, fixed):
        self.events = events
        self.context = context
        self.fixed = fixed  # receivers given on the command line, for a test
        self.wanted = threading.Event()
        self._lock = threading.Lock()
        self._peers = {}
        self._thread = None
        self._scope = None
        self._stop = threading.Event()
        self._shy_said = 0

    def peer(self, key):
        with self._lock:
            peer = self._peers.get(key)
            return peer if peer and peer.name else None

    def link(self, scope):
        """Called with the adapter's index when the link comes up, and with
        None when it goes down."""
        self._halt()
        self._scope = scope
        if scope is not None and self.wanted.is_set():
            self._begin()

    def look(self, on):
        if on == self.wanted.is_set():
            return
        if on:
            self.wanted.set()
            if self._scope is not None:
                self._begin()
        else:
            self.wanted.clear()
            self._halt()

    def _begin(self):
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, args=(self._scope, self._stop),
                                        name='looking', daemon=True)
        self._thread.start()

    def _halt(self):
        if not self._thread:
            return
        self._stop.set()
        self._thread.join()
        self._thread = None
        with self._lock:
            named = [key for key, peer in self._peers.items() if peer.name]
            self._peers.clear()
        for key in named:
            self.events.say('peer_gone', id=key)
        self._say_shy(0)
        self.events.say('looking', on=False)

    def _run(self, scope, stop):
        if self.fixed is not None:
            self.events.say('looking', on=True, beacon=False, why='test receivers')
            while not stop.wait(mdns.QUERY_PERIOD_S):
                self._reconcile(self.fixed, scope, stop)
            return
        try:
            browser = mdns.Browser(receiver.SERVICE_TYPE, scope)
        except OSError as error:
            logging.warning('cannot look for receivers: %s', error)
            self.events.say('looking', on=False, why=str(error))
            return
        try:
            with sender.Beacon() as beacon:
                self.events.say('looking', on=True, beacon=beacon.on, why=beacon.said)
                while not stop.is_set():
                    browser.poll()
                    self._reconcile(browser.instances.devices(PEER_SILENCE_SECONDS),
                                    scope, stop)
        finally:
            browser.close()

    def _reconcile(self, located, scope, stop):
        now = time.monotonic()
        gone, probes = [], []
        with self._lock:
            for key in [key for key in self._peers if key not in located]:
                if self._peers.pop(key).name:
                    gone.append(key)
            for key, (address, port) in located.items():
                peer = self._peers.setdefault(key, Peer(address, port))
                peer.address, peer.port = address, port
                if not peer.name and not peer.probing and now >= peer.next_probe:
                    peer.probing = True
                    probes.append((key, peer))
            shy = sum(1 for peer in self._peers.values() if peer.shy)
        for key in gone:
            self.events.say('peer_gone', id=key)
        for key, peer in probes:
            threading.Thread(target=self._probe, args=(key, peer, scope, stop),
                             name='probe', daemon=True).start()
        self._say_shy(shy)

    def _say_shy(self, shy):
        if shy != self._shy_said:
            self._shy_said = shy
            self.events.say('unwilling', count=shy)

    def _probe(self, key, peer, scope, stop):
        try:
            found = sender.receiver_identity(peer.address, peer.port, scope, self.context)
        except sender.Unwilling as error:
            logging.info('a receiver at %s refuses this computer: %s', peer.address, error)
            found = None
        except sender.Unreachable as error:
            logging.info('a receiver at %s does not answer yet: %s', peer.address, error)
            found = False
        with self._lock:
            peer.probing = False
            # None is a receiver that keeps its name for those it knows, or
            # would not have our certificate at all.
            peer.shy = found is None
            peer.next_probe = time.monotonic() + (
                UNWILLING_PAUSE_SECONDS if peer.shy else PROBE_PAUSE_SECONDS)
            still_there = self._peers.get(key) is peer and not stop.is_set()
            if found and still_there:
                peer.name, peer.model = found
        if found and still_there:
            self.events.say('peer', id=key, name=peer.name, model=peer.model)


class SendWatch(sender.Watch):
    def __init__(self, events, key, total):
        self.events = events
        self.key = key
        self.total = total
        self.cancelled = False
        self.was_accepted = False
        self._connection = None
        self._said = 0.0

    def state(self, state, **more):
        self.events.say('send', id=self.key, state=state, total=self.total, **more)

    @property
    def reached(self):
        return self._connection is not None

    def connected(self, connection):
        self._connection = connection
        if self.cancelled:
            self.cancel()

    def accepted(self, total):
        self.was_accepted = True
        self.state('sending', done=0)

    def progress(self, done):
        now = time.monotonic()
        if now - self._said >= PROGRESS_GAP_SECONDS:
            self._said = now
            self.state('sending', done=min(done, self.total))

    def uploaded(self):
        self.state('finishing', done=self.total)

    def cancel(self):
        self.cancelled = True
        connection = self._connection
        if connection and connection.sock:
            with contextlib.suppress(OSError):
                connection.sock.shutdown(socket.SHUT_RDWR)


class Sending:
    def __init__(self, events, settings, looking, context):
        self.events = events
        self.settings = settings
        self.looking = looking
        self.context = context
        self._lock = threading.Lock()
        self._watches = {}

    def start(self, key, peer_key, paths, scope):
        def failed(reason, detail=None):
            self.events.say('send', id=key, state='failed', reason=reason, detail=detail)

        peer = self.looking.peer(peer_key)
        if not peer or scope is None:
            failed('gone')
            return
        try:
            files = sender.outgoing(paths)
        except sender.Unsendable as error:
            failed(error.kind, str(error))
            return
        except OSError as error:
            failed('unreadable', str(error))
            return
        watch = SendWatch(self.events, key, sender.archive_bytes(files))
        with self._lock:
            self._watches[key] = watch
        threading.Thread(target=self._run, args=(watch, peer, files, scope),
                         name='send', daemon=True).start()

    def cancel(self, key):
        with self._lock:
            watch = self._watches.get(key)
        if watch:
            watch.cancel()

    def _run(self, watch, peer, files, scope):
        watch.state('asking')
        try:
            outcome = sender.deliver(peer.address, peer.port, scope, self.context,
                                     self.settings.name, files, watch)
            watch.state(outcome, done=watch.total if outcome == sender.SENT else None)
        except TimeoutError:
            watch.state('failed', reason='stalled' if watch.was_accepted else 'no_answer')
        except (OSError, http.client.HTTPException, ValueError) as error:
            if watch.cancelled:
                watch.state('cancelled')
            else:
                watch.state('failed', reason='broken' if watch.reached else 'unreachable',
                            detail=str(error) or type(error).__name__)
        finally:
            with self._lock:
                del self._watches[watch.key]


class Engine:
    def __init__(self, args, events):
        self.events = events
        self.settings = Settings(args)
        self.alias = args.iface
        self.loopback = args.loopback
        self.proxy_address_file = args.proxy_address_file
        self.context = receiver.tls_context(args.keys)
        self.incoming = Incoming(events, self.settings)
        self.receiving = Receiving(events, self.settings, self.incoming, self.context,
                                   args.loopback, bool(args.proxy_address_file),
                                   args.keys, args.mdns_proxy_port)
        self.looking = Looking(events, sender.client_context(args.keys),
                               dict(args.peer) if args.loopback else None)
        self.sending = Sending(events, self.settings, self.looking, self.looking.context)
        self.stopping = threading.Event()
        self.scope = None
        self._lock = threading.Lock()
        self._link_thread = threading.Thread(target=self._watch_link, name='link', daemon=True)

    def start(self):
        self.events.say('ready', protocol=PROTOCOL, **self.settings.public())
        self._link_thread.start()

    def _found(self):
        if self.proxy_address_file:
            try:
                with open(self.proxy_address_file, encoding='ascii') as handle:
                    value = handle.read(128).strip()
                address = ipaddress.IPv6Address(value)
                return (0, str(address)) if address.is_link_local else None
            except (OSError, ValueError, UnicodeError):
                return None
        if self.loopback:
            return LOOPBACK[1], LOOPBACK[0]
        index = netif.interface_index(self.alias)
        address = netif.link_local_address(index) if index else None
        return (index, address) if address else None

    def _watch_link(self):
        current = None
        next_bind = 0.0
        while not self.stopping.is_set():
            found = self._found()
            if found != current:
                # The address file is what netd writes and what the phone is
                # told; when it changes or disappears the receiver is torn down
                # and every attempt from the phone hits a closed port with
                # nothing in the log to say why.
                logging.info('link %s', f'up at {found[1]}' if found else 'down')
                with self._lock:
                    self.receiving.stop()
                    self.scope = found[0] if found else None
                    self.looking.link(self.scope)
                current = found
                next_bind = 0.0
                self.events.say('link', up=bool(found))
            if found and time.monotonic() >= next_bind:
                next_bind = time.monotonic() + BIND_RETRY_SECONDS
                self.receiving.start(*found)
            self.stopping.wait(LINK_POLL_SECONDS)

    def stop(self):
        self.stopping.set()
        self._link_thread.join()
        with self._lock:
            self.looking.look(False)
            self.receiving.stop()

    def set(self, command):
        refused = {}
        if 'name' in command:
            try:
                self.settings.name = receiver.valid_name(str(command['name']).strip())
            except argparse.ArgumentTypeError as error:
                refused['name'] = str(error)
        if 'folder' in command:
            try:
                self.settings.dest = receiver.valid_dir(str(command['folder']))
            except argparse.ArgumentTypeError as error:
                refused['folder'] = str(error)
        if 'ask' in command:
            if self.proxy_address_file and command['ask'] is not True:
                refused['ask'] = 'confirmation required in proxy mode'
            else:
                self.settings.ask = bool(command['ask'])
        if 'auto_receive' in command:
            value = command['auto_receive']
            if type(value) is not bool or load_settings().get('auto_receive') is not value:
                refused['auto_receive'] = 'saved preference does not match'
            else:
                self.settings.ask = not value
                self.settings.auto_receive = value
        self.events.say('settings', request_id=command.get('request_id'), refused=refused,
                        **self.settings.public())

    def handle(self, command):
        """Carry out one command; False for the one that ends the engine."""
        do = command.get('do')
        if do == 'quit':
            return False
        if self.proxy_address_file and do in ('look', 'send'):
            self.events.say('error', reason='sending is unavailable in proxy mode')
            return True
        if do == 'look':
            with self._lock:
                self.looking.look(bool(command.get('on')))
        elif do == 'send':
            self.sending.start(str(command.get('id')), command.get('peer'),
                               [str(path) for path in command.get('files') or []], self.scope)
        elif do == 'cancel':
            self.sending.cancel(str(command.get('id')))
        elif do == 'answer':
            self.incoming.answer(str(command.get('id')), command.get('accept') is True)
        elif do == 'pause':
            # Declining new requests only. A transfer that was already accepted
            # runs to the end - cutting it off would lose the user's files.
            self.settings.paused = command.get('on') is True
            logging.info('receiving %s', 'paused' if self.settings.paused else 'resumed')
            self.events.say('paused', on=self.settings.paused)
        elif do == 'set':
            self.set(command)
        else:
            self.events.say('error', reason=f'unknown command {do!r}')
        return True


def fixed_peer(text):
    """A receiver to offer without looking: "[address]:port"."""
    address, _, port = text.rpartition(':')
    try:
        return f'{text}.fixed', (address.strip('[]'), int(port))
    except ValueError:
        raise argparse.ArgumentTypeError('"[address]:port"') from None


SETTINGS_PATH = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)), '..', '..', 'data', 'settings.json'))


def load_settings():
    """Read data\\settings.json; any problem yields empty settings."""
    try:
        with open(SETTINGS_PATH, encoding='utf-8') as handle:
            value = json.load(handle)
        return value if isinstance(value, dict) else {}
    except (OSError, ValueError):
        return {}


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    parser.add_argument('--name', type=receiver.valid_name,
                        default=socket.gethostname().split('.')[0])
    parser.add_argument('--model', type=receiver.valid_model, default=receiver.DEFAULT_MODEL)
    parser.add_argument('--outdir', dest='dest', type=receiver.valid_dir,
                        default='~/Downloads')
    parser.add_argument('--take-all', dest='ask', action='store_false',
                        help='store what anyone sends without asking')
    parser.add_argument('--max-receive-percent', type=receiver.valid_percent,
                        default=receiver.DEFAULT_MAX_RECEIVE_PERCENT)
    parser.add_argument('--iface', default=receiver.DEFAULT_INTERFACE)
    parser.add_argument('--port', type=int, default=receiver.DEFAULT_PORT)
    parser.add_argument('--flags', type=lambda text: int(text, 0), default=receiver.DEFAULT_FLAGS)
    parser.add_argument('--keys', default=identity.default_keys_dir())
    parser.add_argument('--loopback', action='store_true',
                        help='for a test without a radio: receive on this computer '
                             'alone and offer the receivers given with --peer')
    parser.add_argument('--peer', type=fixed_peer, action='append', default=[])
    parser.add_argument('--proxy-address-file', default=None,
                        help='experimental local awdl_netd receiver proxy')
    parser.add_argument('--mdns-proxy-port', type=int, default=15353)
    parser.add_argument('--trace', action='store_true')
    parser.add_argument('--auto-receive', action='store_true',
                        help='accept incoming transfers silently (no confirmation event)')
    args = parser.parse_args(argv)
    # Persistent settings from data\settings.json. This is what the settings
    # panel edits, so name/auto-receive survive elevation and redeployment.
    cfg = load_settings()
    if cfg.get('device_name'):
        try:
            args.name = receiver.valid_name(str(cfg['device_name']).strip())
        except argparse.ArgumentTypeError:
            pass
    if 'auto_receive' in cfg:
        # settings.json is the single source of truth. The settings window writes
        # it, so a stale environment variable must never turn auto-receive back on
        # after the user switched it off.
        args.auto_receive = bool(cfg['auto_receive'])
    elif os.environ.get('DROPLINK_AUTO_RECEIVE') == '1':
        # Fallback for older launchers that predate the settings file only.
        args.auto_receive = True
    if args.auto_receive:
        args.ask = False
    # The stock proxy-mode guard forbids --take-all for safety. Auto-receive is
    # an explicit opt-in through the launcher, so allow it here only.
    if args.proxy_address_file and (args.loopback or (not args.ask and not args.auto_receive) or args.peer):
        parser.error('proxy mode requires confirmation and cannot be combined with loopback or peers')
    if not 1 <= args.mdns_proxy_port <= 65535:
        parser.error('invalid mDNS proxy port')
    return args


def main(argv=None):
    args = parse_args(argv)
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        stream.reconfigure(encoding='utf-8')
    logging.basicConfig(level=logging.DEBUG if args.trace else logging.INFO,
                        stream=sys.stderr, format='%(asctime)s %(levelname)s %(message)s')
    # Optional on-disk copy of the diagnostics. The WPF host drains stderr
    # into memory and discards it, so without this there is no record to
    # inspect when AirDrop discovery fails on a real iPhone. Opt-in via
    # DROPLINK_LOG_DIR; failure to write never affects reception.
    log_dir = os.environ.get('DROPLINK_LOG_DIR')
    if log_dir:
        try:
            os.makedirs(log_dir, exist_ok=True)
            handler = logging.FileHandler(os.path.join(log_dir, 'engine.log'),
                                          encoding='utf-8', mode='a')
            handler.setFormatter(logging.Formatter(
                '%(asctime)s %(levelname)s %(message)s'))
            logging.getLogger().addHandler(handler)
            logging.info('engine log -> %s', os.path.join(log_dir, 'engine.log'))
        except OSError:
            pass
    # Files received while the staging folder still handed down its
    # owner-only DACL are on disk and unreadable to the person who received
    # them. This process is the one with the rights to fix them, and it would
    # otherwise sit quiet until a link appears - so do it now, not on link up.
    repaired = transfer.repair_permissions(args.dest)
    if repaired:
        logging.info('gave %d previously received file(s) back their folder '
                     'permissions', repaired)
    events = Events(sys.stdout)
    try:
        engine = Engine(args, events)
    except identity.IdentityError as error:
        events.say('fatal', reason='no_certificate', detail=str(error))
        return 2
    engine.start()
    try:
        for line in sys.stdin:
            if not line.strip():
                continue
            try:
                command = json.loads(line)
                if not isinstance(command, dict):
                    raise ValueError
            except ValueError:
                events.say('error', reason='a command is one JSON object a line')
                continue
            if not engine.handle(command) or events.closed.is_set():
                break
    except KeyboardInterrupt:
        pass
    finally:
        engine.stop()
    return 0


if __name__ == '__main__':
    sys.exit(main())
