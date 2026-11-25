"""The built-in TLS identity, which is what makes a clean machine work.

The receiver ships an embedded Python with no site-packages and no
openssl.exe. Before x509.py existed, the first start on such a machine raised
"OpenSSL not found" and the engine exited 2. These tests fail if that
dependency ever comes back.
"""
import pathlib
import socket
import ssl
import sys
import tempfile
import threading
import unittest
from unittest import mock

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]
                       / 'tools' / 'portable' / 'backend' / 'airdrop'))

import identity  # noqa: E402
import x509  # noqa: E402


class BuiltinCertificateTests(unittest.TestCase):
    # 2048 bits, the production size: OpenSSL refuses anything smaller with
    # EE_KEY_TOO_SMALL under its default security level, which is a useful
    # reminder that the size is not a free choice.
    BITS = 2048

    @classmethod
    def setUpClass(cls):
        # One key search shared by the read-only checks: generating a 2048-bit
        # pair takes about a second, and there is no reason to pay it six times.
        cls.folder = tempfile.TemporaryDirectory()
        cls.certificate_der, cls.key_der = x509.build(bits=cls.BITS)
        cls.certificate = pathlib.Path(cls.folder.name, 'c.pem')
        cls.key = pathlib.Path(cls.folder.name, 'k.pem')
        cls.certificate.write_bytes(x509.pem('CERTIFICATE', cls.certificate_der))
        cls.key.write_bytes(x509.pem('PRIVATE KEY', cls.key_der))

    @classmethod
    def tearDownClass(cls):
        cls.folder.cleanup()

    def test_certificate_loads_and_matches_its_key(self):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        # Raises SSLError when the key does not belong to the certificate.
        context.load_cert_chain(str(self.certificate), str(self.key))

    def test_a_real_handshake_completes(self):
        server = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        server.load_cert_chain(str(self.certificate), str(self.key))
        client = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        client.check_hostname = False
        client.verify_mode = ssl.CERT_NONE

        listener = socket.socket()
        listener.bind(('127.0.0.1', 0))
        listener.listen(1)
        port = listener.getsockname()[1]
        failure = []

        def serve():
            try:
                connection, _ = listener.accept()
                with server.wrap_socket(connection, server_side=True) as tls:
                    tls.sendall(b'ok')
            except Exception as error:  # noqa: BLE001 - reported below
                failure.append(error)

        worker = threading.Thread(target=serve)
        worker.start()
        try:
            with socket.create_connection(('127.0.0.1', port)) as raw:
                with client.wrap_socket(raw) as tls:
                    self.assertEqual(tls.recv(2), b'ok')
        finally:
            worker.join(timeout=10)
            listener.close()
        self.assertEqual(failure, [])

    def test_certificate_marks_itself_a_ca(self):
        """iOS closes the connection mid-handshake without CA:TRUE.

        Checked on the DER bytes because the standard library can parse a
        certificate but not its extensions. The byte string is
        SEQUENCE { OID 2.5.29.19, BOOLEAN TRUE, OCTET STRING { SEQUENCE { BOOLEAN TRUE } } }.
        """
        self.assertIn(bytes.fromhex('300f0603551d130101ff040530030101ff'),
                      self.certificate_der)

    def test_key_usage_is_present_and_critical(self):
        # OID 2.5.29.15 (keyUsage), critical, OCTET STRING { BIT STRING 0xA4 }
        self.assertIn(bytes.fromhex('300e0603551d0f0101ff0404030202a4'),
                      self.certificate_der)

    def test_subject_key_identifier_is_present(self):
        # OID 2.5.29.14 (subjectKeyIdentifier) as a non-critical extension.
        self.assertIn(bytes.fromhex('0603551d0e'), self.certificate_der)

    def test_each_call_produces_a_different_key(self):
        second, _ = x509.build(bits=1024)
        self.assertNotEqual(self.certificate_der, second)

    def test_pem_has_the_expected_frame(self):
        text = x509.pem('CERTIFICATE', b'\x30\x00').decode('ascii')
        self.assertTrue(text.startswith('-----BEGIN CERTIFICATE-----\n'))
        self.assertTrue(text.endswith('-----END CERTIFICATE-----\n'))
        self.assertTrue(all(len(line) <= 64 for line in text.splitlines()[1:-1]))


class IdentityFallbackTests(unittest.TestCase):
    def test_creates_the_pair_without_cryptography_or_openssl(self):
        """The whole point: no external program, no third-party module."""
        with tempfile.TemporaryDirectory() as folder:
            with _builtin_only():
                certificate, key = identity.ensure_self_signed(folder)
            self.assertTrue(pathlib.Path(certificate).is_file())
            self.assertTrue(pathlib.Path(key).is_file())
            self.assertGreater(pathlib.Path(certificate).stat().st_size, 500)

    def test_second_call_reuses_the_existing_pair(self):
        with tempfile.TemporaryDirectory() as folder:
            with _builtin_only():
                first = identity.ensure_self_signed(folder)
                stamp = pathlib.Path(first[0]).stat().st_mtime_ns
                second = identity.ensure_self_signed(folder)
            self.assertEqual(first, second)
            self.assertEqual(pathlib.Path(second[0]).stat().st_mtime_ns, stamp)

    def test_the_built_pair_serves_tls(self):
        """What the receiver actually does with it at startup."""
        with tempfile.TemporaryDirectory() as folder:
            with _builtin_only():
                certificate, key = identity.ensure_self_signed(folder)
            context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            context.load_cert_chain(certificate, key)


class _builtin_only:
    """Remove both fallbacks so the built-in generator is what runs.

    Without this the test would pass on a machine that happens to have
    `cryptography` installed, and the path that has to work everywhere would
    stay untested.
    """

    def __enter__(self):
        self._patches = [
            mock.patch.object(identity, '_write_with_cryptography', return_value=False),
            mock.patch.object(identity, 'find_openssl',
                              side_effect=identity.IdentityError('not installed')),
        ]
        for patch in self._patches:
            patch.start()
        return self

    def __exit__(self, *exc):
        for patch in reversed(self._patches):
            patch.stop()
        return False


if __name__ == '__main__':
    unittest.main()
