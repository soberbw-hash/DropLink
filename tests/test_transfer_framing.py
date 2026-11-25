"""What the receiver says when a request body is not framed the way it declared.

A field report of "invalid or overlong chunk-size line" told nobody anything: a
sender that closed mid-body, a body that arrived without its chunk framing and
genuine garbage all produced that one sentence, and each needs a different fix.
These pin the three apart, and pin that the bytes themselves reach the log.
"""
import io
import os
import pathlib
import sys
import unittest

sys.path.insert(0, os.environ.get('DROPLINK_AIRDROP_DIR', str(pathlib.Path(__file__).resolve().parents[1] / 'tools/portable/backend/airdrop')))
import transfer

CHUNKED = {'Transfer-Encoding': 'chunked'}


def read(data, headers=None):
    merged = dict(CHUNKED)
    if headers:
        merged.update(headers)
    return transfer.read_small_body(io.BytesIO(data), merged)


class FramingTests(unittest.TestCase):
    def test_a_well_formed_body_still_reads(self):
        self.assertEqual(read(b'5\r\nhello\r\n0\r\n\r\n'), b'hello')

    def test_one_chunk_per_byte_is_still_well_formed(self):
        self.assertEqual(read(b'1\r\na\r\n1\r\nb\r\n0\r\n\r\n'), b'ab')

    def test_an_empty_body_is_not_an_error(self):
        self.assertEqual(read(b'0\r\n\r\n'), b'')

    def test_a_closed_connection_is_reported_as_a_closed_connection(self):
        # The phone shows "waiting" forever when this happens, so the log has to
        # say the sender left rather than blame the parser.
        with self.assertRaises(transfer.RequestBodyError) as caught:
            read(b'')
        message = str(caught.exception)
        self.assertIn('closed the connection', message)
        self.assertNotIn('chunk-size', message)

    def test_a_body_without_framing_says_so_and_shows_the_bytes(self):
        # What a de-chunking intermediary leaves behind: real content, with the
        # header that described the framing still in place. A short body trips
        # the "no line break" check, a longer one the length limit - either way
        # the message has to show the bytes and name the likely cause.
        with self.assertRaises(transfer.RequestBodyError) as caught:
            read(b'bplist00\xd1\x01\x02' + b'\x00' * 200)
        message = str(caught.exception)
        self.assertIn('62 70 6c 69 73 74 30 30', message)  # bplist00
        self.assertIn('| bplist00', message)
        self.assertIn('raw binary plist arrived while the request declared chunked', message)

    def test_a_short_unframed_body_names_the_missing_line_break(self):
        with self.assertRaises(transfer.RequestBodyError) as caught:
            read(b'bplist00\xd1\x01\x02')
        self.assertIn('not chunked framing', str(caught.exception))

    def test_garbage_reaches_the_message_instead_of_being_summarised(self):
        with self.assertRaises(transfer.RequestBodyError) as caught:
            read(b'GARBAGE!!!!!!\r\n')
        self.assertIn('47 41 52 42 41 47 45', message := str(caught.exception))
        self.assertIn('GARBAGE', message)

    def test_a_truncated_chunk_says_truncated(self):
        with self.assertRaises(transfer.RequestBodyError) as caught:
            read(b'5\r\nhel')
        self.assertIn('truncated', str(caught.exception))


if __name__ == '__main__':
    unittest.main()
