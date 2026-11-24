"""Offline loopback checks; do not transmit wireless frames."""
import os
import pathlib
import socket
import sys
import tempfile
import threading
import unittest
from unittest import mock

ROOT = pathlib.Path(__file__).resolve().parents[1]
AIR = pathlib.Path(os.environ.get('DROPLINK_AIRDROP_DIR', ROOT / '.prepared/winawdl/airdrop'))
sys.path[:0] = [str(AIR), str(AIR / 'tests')]
import engine
import sender
import transfer
from test_engine import Heard


class ProxyModeTests(unittest.TestCase):
    def setUp(self):
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
        asked = self.heard.wait('asked')
        self.assertTrue(asked['waits'])
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
        asked = self.heard.wait('asked')
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
            asked = self.heard.wait('asked')
            self.instance.handle({'do': 'answer', 'id': asked['id'], 'accept': True})
            self.heard.wait('lost', id=asked['id'])
            thread.join(10)
        self.assertFalse(thread.is_alive())
        self.assertNotIn(sender.SENT, result)
        self.assertFalse(any(event['event'] == 'stored' for event in self.heard.events))
        self.assertEqual(list(self.dest.iterdir()), [])

    def test_proxy_rejects_disabling_confirmation(self):
        self.instance.handle({'do': 'set', 'ask': False})
        self.assertTrue(self.instance.settings.ask)
        self.assertIn('ask', self.heard.wait('settings')['refused'])

    def test_invalid_or_removed_address_stops_discovery(self):
        self.address.write_text('::1')
        self.heard.wait('link', up=False)
        self.heard.wait('receiving', on=False)
        self.assertIsNone(self.instance._found())


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
