"""Offline loopback checks; do not transmit wireless frames."""
import contextlib
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest
import uuid
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
# Import the code that ships, not a staged copy. The copy at
# .prepared/winawdl/airdrop used to be what the tests imported, and when it fell
# behind, an auto-receive priority bug stayed green locally while the shipped
# build still had it. tools/portable/data holds only *.default.json, so
# load_settings() still sees an empty context here.
AIR = pathlib.Path(os.environ.get('DROPLINK_AIRDROP_DIR', ROOT / 'tools' / 'portable' / 'backend' / 'airdrop'))
# Vendored at WinAWDL 2196fb48b97cef1ee17ee1350c442972c4efeaf0 so Heard is
# available without requiring a full prepared source tree for offline checks.
VENDOR = pathlib.Path(__file__).resolve().parent / 'support' / 'winawdl'
sys.path[:0] = [str(AIR), str(VENDOR)]
import engine
import receiver
import sender
import transfer
from test_engine import Heard


class TransferIdTests(unittest.TestCase):
    def test_first_transfers_after_engine_restart_get_distinct_history_ids(self):
        def first_transfer():
            incoming = engine.Incoming(Heard(), types.SimpleNamespace(auto_receive=False))
            return incoming._open('iPhone', None, [], 0, False).key

        self.assertNotEqual(first_transfer(), first_transfer())


class RejectedUploadTests(unittest.TestCase):
    def test_expect_and_large_bodies_are_not_drained(self):
        for headers in ({'Expect': '100-continue', 'Content-Length': '5'},
                        {'Content-Length': '65537'}, {'Content-Length': 'invalid'}):
            request = mock.Mock()
            stream = mock.Mock()
            handler = types.SimpleNamespace(headers=headers, request=request, rfile=stream, reject=mock.Mock())
            receiver.Handler.reject_unread_upload(handler, 415, 'bad type')
            handler.reject.assert_called_once_with(415, 'bad type')
            stream.read1.assert_not_called()

    def test_drain_has_deadline_and_restores_socket_timeout(self):
        request = mock.Mock()
        request.gettimeout.return_value = 17
        stream = mock.Mock()
        stream.read1.side_effect = TimeoutError()
        handler = types.SimpleNamespace(headers={'Content-Length': '5'}, request=request, rfile=stream, reject=mock.Mock())
        receiver.Handler.reject_unread_upload(handler, 401, 'not accepted')
        request.settimeout.assert_any_call(17)
        stream.read1.assert_called_once_with(5)


class ProxyModeTests(unittest.TestCase):
    def setUp(self):
        self.enterContext(mock.patch.object(engine, 'load_settings', return_value={}))
        self.enterContext(mock.patch.object(engine, 'notify_received'))
        self.enterContext(mock.patch.object(engine, 'notify_failed'))
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.dest = self.root / 'inbox'
        self.dest.mkdir()
        self.address = self.root / 'address'
        self.address.write_text('fe80::1234')
        self.udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp.bind(('127.0.0.1', 0))
        self.addCleanup(self.udp.close)
        args = engine.parse_args(['--proxy-address-file', str(self.address), '--port', '0',
                                  '--mdns-proxy-port', str(self.udp.getsockname()[1]),
                                  '--keys', str(self.root / 'keys'), '--outdir', str(self.dest)])
        self.heard = Heard()
        self.instance = engine.Engine(args, self.heard)
        self.instance.start()
        self.addCleanup(self.instance.stop)
        self.heard.wait('receiving', on=True)

    def send(self, result, file):
        def connect(connection):
            sock = socket.create_connection(('127.0.0.1', connection.port), timeout=10)
            connection.sock = connection.context.wrap_socket(sock)
        with mock.patch.object(sender.Connection, 'connect', connect):
            result.append(sender.deliver('::1', self.instance.settings.port, 0,
                          sender.client_context(str(self.root / 'keys')), 'Test sender',
                          sender.outgoing([str(file)])))

    def test_accept_delivers_complete_file_and_reject_leaves_nothing(self):
        source = self.root / 'photo.jpg'
        source.write_bytes(b'complete image bytes' * 500)
        result = []
        thread = threading.Thread(target=self.send, args=(result, source))
        thread.start()
        self.addCleanup(thread.join)
        asked = self.heard.wait('transfer_created')
        self.assertTrue(asked['requires_confirmation'])
        self.assertEqual(list(self.dest.iterdir()), [])
        self.instance.handle({'do': 'answer', 'id': asked['id'], 'accept': True})
        self.heard.wait('stored', id=asked['id'])
        thread.join(10)
        self.assertFalse(thread.is_alive())
        self.assertEqual(result, [sender.SENT])
        self.assertEqual((self.dest / 'photo.jpg').read_bytes(), source.read_bytes())
        result.clear()
        thread = threading.Thread(target=self.send, args=(result, source))
        thread.start()
        self.addCleanup(thread.join)
        asked = self.heard.wait('transfer_created')
        self.instance.handle({'do': 'answer', 'id': asked['id'], 'accept': False})
        self.heard.wait('answered', id=asked['id'], how='declined')
        thread.join(10)
        self.assertEqual(result, [sender.DECLINED])
        self.assertEqual([p.name for p in self.dest.iterdir()], ['photo.jpg'])

    def test_storage_failure_never_reports_success(self):
        source = self.root / 'note.txt'
        source.write_bytes(b'test')
        result = []
        with mock.patch.object(transfer.os, 'link', side_effect=OSError('unsupported')):
            thread = threading.Thread(target=self.send, args=(result, source))
            thread.start()
            self.addCleanup(thread.join)
            asked = self.heard.wait('transfer_created')
            self.instance.handle({'do': 'answer', 'id': asked['id'], 'accept': True})
            self.heard.wait('lost', id=asked['id'])
            thread.join(10)
        self.assertFalse(thread.is_alive())
        self.assertNotIn(sender.SENT, result)
        self.assertFalse(any(event['event'] == 'stored' for event in self.heard.events))
        self.assertEqual(list(self.dest.iterdir()), [])

    def test_live_auto_receive_requires_matching_saved_preference(self):
        with mock.patch.object(engine, 'load_settings', return_value={'auto_receive': False}):
            self.instance.handle({'do': 'set', 'auto_receive': True, 'request_id': 'a'})
            self.assertIn('auto_receive', self.heard.wait('settings', request_id='a')['refused'])
        for enabled in (True, False):
            with mock.patch.object(engine, 'load_settings', return_value={'auto_receive': enabled}):
                self.instance.handle({'do': 'set', 'auto_receive': enabled})
                self.assertEqual(self.heard.wait('settings')['refused'], {})
                self.assertEqual(self.instance.settings.ask, not enabled)

    def test_folder_change_keeps_accepted_transfer_destination(self):
        incoming = self.instance.incoming
        other = self.root / 'other'
        other.mkdir()
        source = self.root / 'photo.jpg'
        source.write_bytes(b'folder switch test')
        original = incoming.upload_started

        def change_folder(*args):
            self.instance.handle({'do': 'set', 'folder': str(other), 'request_id': 'move'})
            return original(*args)

        with mock.patch.object(incoming, '_decision', return_value='accepted'):
            results = []
            with mock.patch.object(incoming, 'upload_started', side_effect=change_folder):
                self.send(results, source)
            self.assertEqual(self.heard.wait('settings', request_id='move')['folder'], str(other))
            self.assertFalse((other / source.name).exists())
            self.send(results, source)
        self.assertEqual(results, [sender.SENT, sender.SENT])
        for directory in (self.dest, other):
            self.assertEqual((directory / source.name).read_bytes(), source.read_bytes())

    def test_proxy_rejects_disabling_confirmation(self):
        self.instance.handle({'do': 'set', 'ask': False})
        self.assertTrue(self.instance.settings.ask)
        self.assertIn('ask', self.heard.wait('settings')['refused'])

    def test_invalid_or_removed_address_stops_discovery(self):
        self.address.write_text('::1')
        self.heard.wait('link', up=False)
        self.heard.wait('receiving', on=False)
        self.assertIsNone(self.instance._found())

    def test_discovery_worker_recovers_without_restarting_listener(self):
        receiving = self.instance.receiving
        server, thread, old = receiving.server, receiving.thread, receiving.announcer
        old.stopping.set()
        self.heard.wait('receiving', on=False, reason='announcement_stopped')
        self.heard.wait('receiving', on=True)
        self.assertIs(receiving.server, server)
        self.assertIs(receiving.thread, thread)
        self.assertIsNot(receiving.announcer, old)
        self.udp.settimeout(2)
        new_port = receiving.announcer.sock.getsockname()[1]
        while True:
            packet, source = self.udp.recvfrom(9000)
            if source[1] == new_port:
                self.assertEqual(packet, receiving.announcer.service.response())
                break


class AutoReceiveTests(unittest.TestCase):
    """Auto-accept still has to announce the transfer.

    The host used to build a row only for an 'asked' event that was waiting for
    an answer, and auto-receive deliberately never sent one - so an
    auto-accepted transfer had no row at all, its progress events matched
    nothing, and the window stayed on "等待接收" while a file was arriving.
    """

    def setUp(self):
        self.enterContext(mock.patch.object(engine, 'load_settings', return_value={}))
        self.enterContext(mock.patch.object(engine, 'notify_received'))
        self.enterContext(mock.patch.object(engine, 'notify_failed'))
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.dest = self.root / 'inbox'
        self.dest.mkdir()
        self.address = self.root / 'address'
        self.address.write_text('fe80::1234')
        self.udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.udp.bind(('127.0.0.1', 0))
        self.addCleanup(self.udp.close)
        args = engine.parse_args(['--proxy-address-file', str(self.address), '--port', '0',
                                  '--mdns-proxy-port', str(self.udp.getsockname()[1]),
                                  '--keys', str(self.root / 'keys'), '--outdir', str(self.dest),
                                  '--auto-receive'])
        self.heard = Heard()
        self.instance = engine.Engine(args, self.heard)
        self.instance.start()
        self.addCleanup(self.instance.stop)
        self.heard.wait('receiving', on=True)

    def send(self, result, file):
        def connect(connection):
            sock = socket.create_connection(('127.0.0.1', connection.port), timeout=10)
            connection.sock = connection.context.wrap_socket(sock)
        with mock.patch.object(sender.Connection, 'connect', connect):
            result.append(sender.deliver('::1', self.instance.settings.port, 0,
                          sender.client_context(str(self.root / 'keys')), 'Test sender',
                          sender.outgoing([str(file)])))

    def test_auto_receive_announces_before_progress(self):
        source = self.root / 'photo.jpg'
        source.write_bytes(b'complete image bytes' * 500)
        result = []
        thread = threading.Thread(target=self.send, args=(result, source))
        thread.start()
        self.addCleanup(thread.join)

        created = self.heard.wait('transfer_created')
        self.assertFalse(created['requires_confirmation'])
        self.assertEqual(created['files'][0]['name'], 'photo.jpg')
        self.assertEqual(created['files_count'], 1)
        stored = self.heard.wait('stored', id=created['id'])
        self.assertEqual(stored['names'], ['photo.jpg'])
        thread.join(10)
        self.assertEqual(result, [sender.SENT])

        kinds = [event['event'] for event in self.heard.events]
        self.assertLess(kinds.index('transfer_created'), kinds.index('incoming'))
        self.assertLess(kinds.index('incoming'), kinds.index('stored'))
        self.assertEqual((self.dest / 'photo.jpg').read_bytes(), source.read_bytes())

    def test_paused_declines_instead_of_silently_accepting(self):
        self.instance.handle({'do': 'pause', 'on': True})
        self.assertTrue(self.heard.wait('paused')['on'])
        source = self.root / 'note.txt'
        source.write_bytes(b'paused')
        result = []
        thread = threading.Thread(target=self.send, args=(result, source))
        thread.start()
        self.addCleanup(thread.join)
        created = self.heard.wait('transfer_created')
        self.heard.wait('answered', id=created['id'], how='declined')
        thread.join(10)
        self.assertEqual(result, [sender.DECLINED])
        self.assertEqual(list(self.dest.iterdir()), [])

    def test_resume_accepts_again(self):
        self.instance.handle({'do': 'pause', 'on': True})
        self.heard.wait('paused')
        self.instance.handle({'do': 'pause', 'on': False})
        self.assertFalse(self.heard.wait('paused', on=False)['on'])
        source = self.root / 'again.txt'
        source.write_bytes(b'again')
        result = []
        thread = threading.Thread(target=self.send, args=(result, source))
        thread.start()
        self.addCleanup(thread.join)
        created = self.heard.wait('transfer_created')
        self.heard.wait('stored', id=created['id'])
        thread.join(10)
        self.assertEqual(result, [sender.SENT])


class SettingsPriorityTests(unittest.TestCase):
    """settings.json outranks the launcher environment.

    Auto-receive is a privacy-relevant switch. If a stale DROPLINK_AUTO_RECEIVE=1
    in the environment could still win, switching the switch off in the settings
    window would look like it worked and keep accepting files anyway.
    """

    def parse(self, cfg, env=None):
        with mock.patch.object(engine, 'load_settings', return_value=cfg), \
                mock.patch.dict(os.environ, env or {}, clear=False):
            if env is None:
                os.environ.pop('DROPLINK_AUTO_RECEIVE', None)
            return engine.parse_args(['--port', '0'])

    def test_settings_off_beats_environment_on(self):
        args = self.parse({'auto_receive': False}, {'DROPLINK_AUTO_RECEIVE': '1'})
        self.assertFalse(args.auto_receive)

    def test_settings_on_beats_environment_absent(self):
        args = self.parse({'auto_receive': True}, {})
        self.assertTrue(args.auto_receive)

    def test_environment_still_works_without_a_settings_file(self):
        args = self.parse({}, {'DROPLINK_AUTO_RECEIVE': '1'})
        self.assertTrue(args.auto_receive)

    def test_default_is_off(self):
        args = self.parse({}, {})
        self.assertFalse(args.auto_receive)
        self.assertTrue(args.ask)


@contextlib.contextmanager
def ordinary_folder():
    """A temp folder carrying the ACL that %TEMP% hands out.

    tempfile's own folders are created with mode 0o700, which on Windows means
    a DACL naming only the owner, SYSTEM and Administrators - the very thing
    the tests below are about. A folder made without a mode inherits
    normally."""
    path = os.path.join(tempfile.gettempdir(),
                        'droplink-acl-' + uuid.uuid4().hex[:8])
    os.mkdir(path)
    try:
        yield path
    finally:
        shutil.rmtree(path, ignore_errors=True)


def acl_of(path):
    """The DACL entries icacls prints, with the object name blanked out."""
    out = subprocess.run(['icacls', str(path)], capture_output=True, text=True,
                         encoding='mbcs', errors='replace')
    return tuple(line.strip().replace(str(path), '<object>')
                 for line in out.stdout.splitlines() if line.strip())


class StagingTests(unittest.TestCase):
    def test_hardlink_failure_does_not_create_destination(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryDirectory() as s:
            pathlib.Path(s, 'a.txt').write_bytes(b'finished')
            with mock.patch.object(transfer.os, 'link', side_effect=OSError('unsupported')):
                with self.assertRaises(OSError):
                    transfer.publish_staged(s, d, [transfer.Stored('a.txt')])
            self.assertEqual(os.listdir(d), [])

    def test_existing_file_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as d, tempfile.TemporaryDirectory() as s:
            pathlib.Path(s, 'a.txt').write_bytes(b'new')
            pathlib.Path(d, 'a.txt').write_bytes(b'old')
            result = transfer.publish_staged(s, d, [transfer.Stored('a.txt')])
            self.assertEqual(result, ['a-1.txt'])
            self.assertEqual(pathlib.Path(d, 'a.txt').read_bytes(), b'old')
            self.assertEqual(pathlib.Path(d, 'a-1.txt').read_bytes(), b'new')

    @unittest.skipUnless(os.name == 'nt', 'NTFS permissions')
    def test_published_file_gets_the_folders_permissions(self):
        """A received photo has to be openable by the person who received it.

        The staging folder used to be tempfile.mkdtemp, whose 0o700 mode on
        Windows becomes a DACL naming only its owner, SYSTEM and
        Administrators. The hard link carried that DACL to the published
        name, so a receiver running elevated produced files that only an
        elevated process could open: the notification preview showed the
        picture and Explorer answered "access denied"."""
        with ordinary_folder() as d:
            control = pathlib.Path(d, 'control.txt')
            control.write_bytes(b'x')
            staging = pathlib.Path(d, '.droplink-old')
            staging.mkdir(mode=0o700)
            pathlib.Path(staging, 'a.txt').write_bytes(b'photo')
            self.assertNotEqual(acl_of(staging / 'a.txt'), acl_of(control),
                                'a 0o700 folder is supposed to hand down its '
                                'own DACL; the premise of this test broke')
            published = transfer.publish_staged(staging, d, [transfer.Stored('a.txt')])
            self.assertEqual(acl_of(pathlib.Path(d, published[0])), acl_of(control))

    @unittest.skipUnless(os.name == 'nt', 'NTFS permissions')
    def test_staging_folder_keeps_the_folders_permissions(self):
        with ordinary_folder() as d:
            control = pathlib.Path(d, 'control.txt')
            control.write_bytes(b'x')
            with transfer.staging_folder(d) as staging:
                pathlib.Path(staging, 'a.txt').write_bytes(b'photo')
                self.assertEqual(acl_of(pathlib.Path(staging, 'a.txt')), acl_of(control))
            self.assertEqual([n for n in os.listdir(d) if n.startswith('.droplink-')], [])

    @unittest.skipUnless(os.name == 'nt', 'NTFS permissions')
    def test_repair_returns_files_published_the_old_way(self):
        with ordinary_folder() as d:
            control = pathlib.Path(d, 'control.txt')
            control.write_bytes(b'x')
            staging = pathlib.Path(d, '.droplink-old')
            staging.mkdir(mode=0o700)
            pathlib.Path(staging, 'a.txt').write_bytes(b'photo')
            transfer.mark_as_downloaded(str(staging / 'a.txt'))
            os.link(staging / 'a.txt', pathlib.Path(d, 'a.txt'))
            self.assertEqual(transfer.repair_permissions(d), 1)
            self.assertEqual(acl_of(pathlib.Path(d, 'a.txt')), acl_of(control))
            self.assertEqual(transfer.repair_permissions(d), 0,
                             'a repaired file must not be repaired again')

    @unittest.skipUnless(os.name == 'nt', 'NTFS permissions')
    def test_repair_leaves_files_it_did_not_receive_alone(self):
        with ordinary_folder() as d:
            stranger = pathlib.Path(d, 'mine.txt')
            stranger.write_bytes(b'not ours')
            before = acl_of(stranger)
            self.assertEqual(transfer.repair_permissions(d), 0)
            self.assertEqual(acl_of(stranger), before)


class ServiceIdentityTests(unittest.TestCase):
    """The receiver hides from a phone that has just received from it by
    rotating its mDNS instance ID. That only helps across restarts if the new
    ID is written down, so the rotation has to persist."""

    def test_rotation_gives_cached_entry_time_and_retires_only_the_old_ptr(self):
        mdns = receiver.mdns
        with tempfile.TemporaryDirectory() as keys, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
            udp.bind(('127.0.0.1', 0))
            udp.settimeout(3)
            old = mdns.Service(receiver.SERVICE_TYPE, '001122334455', '001122334455.local',
                               'fe80::1234', 8771, 'flags=1')
            ann = receiver.ProxyAnnouncer(old, udp.getsockname()[1], keys)
            with mock.patch.object(receiver, 'ROTATE_REQUEST', threading.Event()) as rotate:
                ann.start()
                try:
                    self.assertEqual(udp.recv(9000), old.response())
                    rotate.set()
                    deadline = time.monotonic() + 6
                    goodbye = None
                    replacement = None
                    while time.monotonic() < deadline:
                        packet = udp.recv(9000)
                        now = time.monotonic()
                        ptrs = [(value, ttl) for _, kind, ttl, value in mdns.response_records(packet)
                                if kind == mdns.TYPE_PTR]
                        if ptrs == [(old.instance_name.lower(), 0)]:
                            goodbye = now
                        active = [value for value, ttl in ptrs if ttl > 0]
                        if active and active != [old.instance_name.lower()]:
                            self.assertEqual(len(active), 1)
                            if replacement is None:
                                self.assertIsNotNone(goodbye)
                                self.assertGreaterEqual(now - goodbye, receiver.GOODBYE_GRACE_S - 0.1)
                                self.assertIn((old.instance_name.lower(), 0), ptrs)
                                replacement = active[0]
                            elif len(ptrs) == 1:
                                self.assertEqual(active, [replacement])
                                break
                    else:
                        self.fail('missing clean announcement after the handoff')
                finally:
                    ann.stop()

    def test_rotation_persists_across_a_restart(self):
        with tempfile.TemporaryDirectory() as keys:
            first = receiver.stable_service_id('fd00::1234', keys)
            rotated = receiver.rotate_service_id(keys)
            self.assertNotEqual(first, rotated,
                                'a rotation that changes nothing does not hide the receiver')
            self.assertEqual(receiver.stable_service_id('fd00::1234', keys), rotated,
                             'a restart went back to the identity the phone already cached')

    def test_rotation_is_twelve_hex_digits(self):
        with tempfile.TemporaryDirectory() as keys:
            rotated = receiver.rotate_service_id(keys)
            self.assertRegex(rotated, r'^[0-9a-f]{12}$')

    def test_stable_id_survives_a_missing_keys_folder(self):
        # First run: the folder does not exist yet. It must not raise.
        with tempfile.TemporaryDirectory() as root:
            keys = os.path.join(root, 'not-yet')
            value = receiver.stable_service_id('fd00::99', keys)
            self.assertRegex(value, r'^[0-9a-f]{12}$')
