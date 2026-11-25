# SPDX-License-Identifier: MIT
"""Build a self-signed TLS identity using nothing but the standard library.

The receiver ships an embedded Python with no site-packages, so there is no
`cryptography` to import, and a clean Windows machine has no openssl.exe -
requiring Git for Windows to start an AirDrop receiver is not acceptable.
Before this module existed the first start on such a machine raised
"OpenSSL not found", the engine exited 2, and the app could only show a
failed setup step. That was the single most common "the package does not
work on this computer" report.

Only two things are needed: an RSA-2048 key pair, and a v3 certificate that
is its own CA. iOS closes the TLS connection mid-handshake
(ssl: UNEXPECTED_EOF_WHILE_READING) when the receiver's certificate lacks
BasicConstraints CA:TRUE, so that extension is not optional.

Everything here is DER assembled by hand. It is deliberately small and
readable rather than general: one key type, one signature algorithm, one
shape of certificate.
"""
import base64
import datetime
import hashlib
import secrets

# --------------------------------------------------------------------------
# DER primitives
# --------------------------------------------------------------------------


def _length(size):
    if size < 0x80:
        return bytes([size])
    body = size.to_bytes((size.bit_length() + 7) // 8, 'big')
    return bytes([0x80 | len(body)]) + body


def _tlv(tag, body):
    return bytes([tag]) + _length(len(body)) + body


def _sequence(*items):
    return _tlv(0x30, b''.join(items))


def _set(*items):
    return _tlv(0x31, b''.join(items))


def _integer(value):
    if value == 0:
        return b'\x02\x01\x00'
    body = value.to_bytes((value.bit_length() + 7) // 8, 'big')
    # DER integers are signed, so a leading bit of 1 has to be padded.
    if body[0] & 0x80:
        body = b'\x00' + body
    return _tlv(0x02, body)


def _oid(dotted):
    parts = [int(piece) for piece in dotted.split('.')]
    if len(parts) < 2:
        raise ValueError('an OID needs at least two arcs')
    body = bytearray([parts[0] * 40 + parts[1]])
    for part in parts[2:]:
        chunk = bytearray([part & 0x7F])
        part >>= 7
        while part:
            chunk.append((part & 0x7F) | 0x80)
            part >>= 7
        body += bytes(reversed(chunk))
    return _tlv(0x06, bytes(body))


def _octet_string(body):
    return _tlv(0x04, body)


def _bit_string(body, unused=0):
    return _tlv(0x03, bytes([unused]) + body)


def _boolean(value):
    return _tlv(0x01, b'\xff' if value else b'\x00')


def _null():
    return b'\x05\x00'


def _utc_time(moment):
    """UTCTime, which is what every TLS stack expects before 2050."""
    return _tlv(0x17, moment.strftime('%y%m%d%H%M%SZ').encode('ascii'))


def _utf8(text):
    return _tlv(0x0C, text.encode('utf-8'))


# --------------------------------------------------------------------------
# RSA key pair
# --------------------------------------------------------------------------

# Trial division by the primes below 2000 rejects most candidates before the
# far more expensive Miller-Rabin step. Without it a 1024-bit prime search
# spends most of its time on numbers that fail on 3.
_SMALL_PRIMES = []
for _candidate in range(2, 2000):
    for _divisor in _SMALL_PRIMES:
        if _divisor * _divisor > _candidate:
            break
        if _candidate % _divisor == 0:
            break
    else:
        _SMALL_PRIMES.append(_candidate)
del _candidate, _divisor


def _is_probable_prime(number, rounds=24):
    """Miller-Rabin. 24 rounds leaves a false positive below 2^-48.

    A wrong "prime" here produces a key that fails to sign, which is caught
    the moment the certificate is built - it cannot silently weaken anything.
    """
    if number < 2:
        return False
    for small in _SMALL_PRIMES:
        if number % small == 0:
            return number == small
    odd = number - 1
    twos = 0
    while odd % 2 == 0:
        odd //= 2
        twos += 1
    for _ in range(rounds):
        base = secrets.randbelow(number - 3) + 2
        value = pow(base, odd, number)
        if value in (1, number - 1):
            continue
        for _ in range(twos - 1):
            value = pow(value, 2, number)
            if value == number - 1:
                break
        else:
            return False
    return True


def _prime(bits):
    while True:
        candidate = secrets.randbits(bits)
        # Top two bits set: the product of two such primes has exactly `bits`
        # bits, which keeps the modulus the size the sender expects.
        candidate |= (1 << (bits - 1)) | (1 << (bits - 2)) | 1
        if _is_probable_prime(candidate):
            return candidate


def _gcd(left, right):
    while right:
        left, right = right, left % right
    return left


def key_pair(bits=2048):
    """A fresh RSA key pair as plain integers."""
    exponent = 65537
    while True:
        p = _prime(bits // 2)
        q = _prime(bits // 2)
        if p == q:
            continue
        # e must be invertible modulo both p-1 and q-1.
        if (p - 1) % exponent == 0 or (q - 1) % exponent == 0:
            continue
        modulus = p * q
        if modulus.bit_length() != bits:
            continue
        break
    if p < q:
        p, q = q, p
    # d is the inverse modulo lcm(p-1, q-1), which is the smaller and equally
    # valid private exponent.
    carmichael = (p - 1) // _gcd(p - 1, q - 1) * (q - 1)
    return {
        'n': modulus,
        'e': exponent,
        'd': pow(exponent, -1, carmichael),
        'p': p,
        'q': q,
    }


# --------------------------------------------------------------------------
# Signing (PKCS#1 v1.5 with SHA-256)
# --------------------------------------------------------------------------

# DigestInfo prefix for SHA-256, from RFC 8017 section 9.2 note 1.
_SHA256_PREFIX = bytes.fromhex('3031300d060960864801650304020105000420')


def _sign(private, message):
    size = (private['n'].bit_length() + 7) // 8
    digest_info = _SHA256_PREFIX + hashlib.sha256(message).digest()
    if size < len(digest_info) + 11:
        raise ValueError('the RSA modulus is too small for a SHA-256 signature')
    padding = b'\xff' * (size - len(digest_info) - 3)
    block = b'\x00\x01' + padding + b'\x00' + digest_info
    return pow(int.from_bytes(block, 'big'), private['d'], private['n']).to_bytes(size, 'big')


# --------------------------------------------------------------------------
# Certificate
# --------------------------------------------------------------------------

_RSA_ENCRYPTION = '1.2.840.113549.1.1.1'
_SHA256_WITH_RSA = '1.2.840.113549.1.1.11'
_COMMON_NAME = '2.5.4.3'
_BASIC_CONSTRAINTS = '2.5.29.19'
_KEY_USAGE = '2.5.29.15'
_SUBJECT_KEY_ID = '2.5.29.14'


def _name(common_name):
    return _sequence(_set(_sequence(_oid(_COMMON_NAME), _utf8(common_name))))


def _algorithm():
    return _sequence(_oid(_SHA256_WITH_RSA), _null())


def _extension(oid, critical, value):
    parts = [_oid(oid)]
    if critical:
        parts.append(_boolean(True))
    parts.append(_octet_string(value))
    return _sequence(*parts)


def _public_key_bytes(key):
    return _sequence(_integer(key['n']), _integer(key['e']))


def build(common_name='AirDrop receiver', days=365, bits=2048):
    """Return (certificate_der, private_key_pkcs8_der)."""
    key = key_pair(bits)
    public = _public_key_bytes(key)
    subject = _name(common_name)

    # KeyUsage: digitalSignature (bit 0), keyEncipherment (bit 2) and
    # keyCertSign (bit 5). Bits are numbered from the most significant, so the
    # byte is 0b10100100 and the last two bits are unused.
    key_usage = _bit_string(bytes([0xA4]), 2)
    # subjectKeyIdentifier is the SHA-1 of the public key BIT STRING body.
    key_id = _octet_string(hashlib.sha1(public).digest())

    extensions = (
        _extension(_BASIC_CONSTRAINTS, True, _sequence(_boolean(True))),
        _extension(_KEY_USAGE, True, key_usage),
        _extension(_SUBJECT_KEY_ID, False, key_id),
    )

    now = datetime.datetime.now(datetime.timezone.utc)
    not_before = now - datetime.timedelta(days=1)
    not_after = now + datetime.timedelta(days=days)

    # A serial number must be a positive integer; clearing the top bit keeps
    # it under 2^127 and therefore never needs the leading zero pad.
    serial = secrets.randbits(127) | 1

    body = _sequence(
        _tlv(0xA0, _integer(2)),          # version [0] EXPLICIT INTEGER v3
        _integer(serial),
        _algorithm(),
        subject,                          # issuer == subject: self-signed
        _sequence(_utc_time(not_before), _utc_time(not_after)),
        subject,
        _sequence(_sequence(_oid(_RSA_ENCRYPTION), _null()), _bit_string(public)),
        _tlv(0xA3, _sequence(*extensions)),  # extensions [3] EXPLICIT
    )
    certificate = _sequence(body, _algorithm(), _bit_string(_sign(key, body)))

    # RSAPrivateKey, wrapped in the PKCS#8 PrivateKeyInfo the PEM label
    # "PRIVATE KEY" promises. Some TLS libraries accept the bare PKCS#1 form
    # under a "RSA PRIVATE KEY" label, but PKCS#8 is the interoperable one.
    private = _sequence(
        _integer(0),
        _integer(key['n']),
        _integer(key['e']),
        _integer(key['d']),
        _integer(key['p']),
        _integer(key['q']),
        _integer(key['d'] % (key['p'] - 1)),
        _integer(key['d'] % (key['q'] - 1)),
        _integer(pow(key['q'], -1, key['p'])),
    )
    pkcs8 = _sequence(
        _integer(0),
        _sequence(_oid(_RSA_ENCRYPTION), _null()),
        _octet_string(private),
    )
    return certificate, pkcs8


def pem(label, der):
    body = base64.b64encode(der).decode('ascii')
    lines = [body[start:start + 64] for start in range(0, len(body), 64)]
    return ('-----BEGIN %s-----\n' % label + '\n'.join(lines) +
            '\n-----END %s-----\n' % label).encode('ascii')
