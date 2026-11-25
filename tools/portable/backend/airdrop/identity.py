# SPDX-License-Identifier: MIT
"""The TLS identity this receiver presents: a self-signed certificate, which
is all a sender needs when the receiver is visible to everyone."""
import datetime
import logging
import os
import shutil
import subprocess
import time

import x509

CERTIFICATE = 'certificate.self-signed.pem'
KEY = 'key.self-signed.pem'
VALID_DAYS = '365'
# Passed explicitly because some Windows builds of OpenSSL point at a default
# configuration file that does not exist. Senders do not look at the name.
# The BasicConstraints/KeyUsage block matters as much as the key itself: OpenSSL
# 3.x does not mark a `req -x509` certificate as a CA unless it is told to, and
# an iOS sender answers a certificate without CA:TRUE by closing the TCP
# connection mid-handshake (ssl: UNEXPECTED_EOF_WHILE_READING). The in-process
# builder above already adds BasicConstraints(ca=True, critical=True); this
# keeps the openssl fallback identical instead of subtly weaker.
REQUEST_CONFIG = '''[req]
distinguished_name = subject
prompt = no
x509_extensions = v3
[subject]
CN = AirDrop receiver
[v3]
basicConstraints = critical,CA:TRUE
keyUsage = critical,digitalSignature,keyEncipherment,keyCertSign
subjectKeyIdentifier = hash
'''
# Git for Windows ships OpenSSL without putting it on PATH.
GIT_OPENSSL = (('mingw64', 'bin', 'openssl.exe'), ('usr', 'bin', 'openssl.exe'))


class IdentityError(Exception):
    pass


def default_keys_dir():
    base = os.environ.get('LOCALAPPDATA') or os.path.expanduser('~')
    return os.path.join(base, 'winawdl', 'keys')


def find_openssl():
    found = shutil.which('openssl')
    if found:
        return found
    git = shutil.which('git')
    if git:
        root = os.path.dirname(os.path.dirname(os.path.realpath(git)))
        for parts in GIT_OPENSSL:
            candidate = os.path.join(root, *parts)
            if os.path.isfile(candidate):
                return candidate
    raise IdentityError('OpenSSL not found: put openssl on PATH or install Git for Windows')


def _write_with_cryptography(certificate, key):
    """Create the pair in-process, so a clean Windows machine needs no
    openssl.exe (or Git for Windows) to start the receiver. Returns False when
    the module is not installed, so the caller can fall back to the CLI."""
    try:
        from cryptography import x509
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import rsa
        from cryptography.x509.oid import NameOID
    except ImportError:
        return False
    private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'AirDrop receiver')])
    now = datetime.datetime.now(datetime.timezone.utc)
    certificate_object = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(private.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - datetime.timedelta(days=1))
        .not_valid_after(now + datetime.timedelta(days=int(VALID_DAYS)))
        .add_extension(x509.BasicConstraints(ca=True, path_length=None),
                       critical=True)
        .sign(private, hashes.SHA256()))
    # Write both halves through temporary names so a crash cannot leave a
    # certificate without its key (or the reverse) next to each other.
    _write_pair(certificate, key,
                certificate_object.public_bytes(serialization.Encoding.PEM),
                private.private_bytes(serialization.Encoding.PEM,
                                      serialization.PrivateFormat.PKCS8,
                                      serialization.NoEncryption()))
    return True


def _write_with_builtin(certificate, key):
    """Create the pair with x509.py, which needs nothing but the standard
    library.

    This is the path that makes a clean Windows machine work at all. The
    receiver ships an embedded Python with no site-packages, so there is no
    `cryptography`; and a machine that has never installed Git for Windows has
    no openssl.exe either. Requiring either one to receive a photo is how a
    portable package ends up "not working on this computer" for reasons the
    person holding it cannot possibly diagnose.

    Takes about a second: an RSA-2048 key search plus one signature.
    """
    started = time.monotonic()
    try:
        certificate_der, key_der = x509.build(common_name='AirDrop receiver',
                                              days=int(VALID_DAYS))
    except Exception as error:  # noqa: BLE001 - any failure means "fall through"
        logging.warning('built-in certificate generation failed: %s', error)
        return False
    _write_pair(certificate, key, x509.pem('CERTIFICATE', certificate_der),
                x509.pem('PRIVATE KEY', key_der))
    logging.info('created a self-signed identity in %.1f s (built-in)',
                 time.monotonic() - started)
    return True


def _write_pair(certificate, key, certificate_data, key_data):
    """Write both halves through temporary names.

    A crash between the two writes must not leave a certificate without its
    key next to it: the next start would see both files present, return the
    paths, and then fail the TLS handshake with no explanation.
    """
    for path, data in ((key, key_data), (certificate, certificate_data)):
        temporary = path + '.tmp'
        with open(temporary, 'wb') as out:
            out.write(data)
            out.flush()
            os.fsync(out.fileno())
        os.replace(temporary, path)


def ensure_self_signed(keys_dir):
    """Return the certificate and key paths, creating the pair on first use."""
    certificate = os.path.join(keys_dir, CERTIFICATE)
    key = os.path.join(keys_dir, KEY)
    if os.path.isfile(certificate) and os.path.isfile(key):
        return certificate, key
    os.makedirs(keys_dir, exist_ok=True)
    if _write_with_cryptography(certificate, key):
        return certificate, key
    if _write_with_builtin(certificate, key):
        return certificate, key
    # Last resort only. Every machine reaches one of the two paths above, so
    # this exists for the case where both were somehow broken, not as the
    # normal way to get a certificate.
    config = os.path.join(keys_dir, 'request.cnf')
    with open(config, 'w', encoding='ascii') as out:
        out.write(REQUEST_CONFIG)
    try:
        run = subprocess.run(
            [find_openssl(), 'req', '-config', config, '-newkey', 'rsa:2048',
             '-nodes', '-x509', '-days', VALID_DAYS, '-keyout', key,
             '-out', certificate],
            capture_output=True, text=True)
    finally:
        os.unlink(config)
    if run.returncode != 0:
        raise IdentityError(f'openssl could not create a certificate: {run.stderr.strip()}')
    return certificate, key
