"""Names a received photo lands under.

An iPhone names a photo after its own asset key. That is not a name: the
receive folder fills up with lines like
``C8F41611-1285-46BD-885D-D27C8B6C9ED6.PNG`` and the photo the user just sent
reads as a photo that never arrived. A bare asset key becomes a date and a
time; every other name is left exactly as the sender wrote it, because that
name may be the only thing the sender recognises on the other end.
"""
import os
import pathlib
import sys
import time
import unittest

sys.path.insert(0, os.environ.get('DROPLINK_AIRDROP_DIR', str(pathlib.Path(__file__).resolve().parents[1] / '.prepared/winawdl/airdrop')))
import transfer

# Built from the calendar rather than a raw epoch, so the expectation stays
# right in any time zone.
WHEN = time.localtime(time.mktime((2026, 11, 25, 2, 18, 32, 0, 0, -1)))


class ReadableNameTests(unittest.TestCase):
    def test_asset_key_becomes_a_photo_with_a_time(self):
        name = transfer.readable_name(
            'C8F41611-1285-46BD-885D-D27C8B6C9ED6.PNG', WHEN)
        self.assertEqual(name, '照片 2026-11-25 02.18.32.png')

    def test_asset_key_of_a_video_says_video(self):
        name = transfer.readable_name(
            '81FA1FEE-A0D6-4CB0-B930-D25D8B52E7F6.MOV', WHEN)
        self.assertEqual(name, '视频 2026-11-25 02.18.32.mov')

    def test_a_real_name_is_never_touched(self):
        for name in ('IMG_4147.jpg', '假期 第一版.HEIC', 'holiday.png'):
            self.assertEqual(transfer.readable_name(name, WHEN), name)

    def test_a_name_that_merely_starts_with_a_key_is_kept(self):
        # The phone only ever sends the key alone. Anything longer carries
        # something the sender typed, and that is not ours to discard.
        name = 'C8F41611-1285-46BD-885D-D27C8B6C9ED6 备份.PNG'
        self.assertEqual(transfer.readable_name(name, WHEN), name)

    def test_extensionless_key_is_kept(self):
        self.assertEqual(
            transfer.readable_name('C8F41611-1285-46BD-885D-D27C8B6C9ED6', WHEN),
            'C8F41611-1285-46BD-885D-D27C8B6C9ED6')


if __name__ == '__main__':
    unittest.main()
