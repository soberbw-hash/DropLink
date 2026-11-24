"""Wording of the completion toast, tested without a desktop.

The toast used to repeat what the file name already said ("电脑可查看：PNG"
under "IMG_0001.PNG"), so a normal transfer produced three lines of text and
a picture. These checks pin the shape that replaced it: one line whenever one
line is enough, and a second line only when it carries new information.
"""
import os
import pathlib
import sys
import unittest

sys.path.insert(0, os.environ.get('DROPLINK_AIRDROP_DIR', str(pathlib.Path(__file__).resolve().parents[1] / '.prepared/winawdl/airdrop')))
import engine


class ToastTextTests(unittest.TestCase):
    def test_single_file_is_one_line(self):
        title, body = engine.toast_text(['IMG_0001.PNG'])
        self.assertEqual(title, 'DropLink 收到 IMG_0001.PNG')
        self.assertEqual(body, '', 'the file name already says the format')

    def test_single_extensionless_file_still_one_line(self):
        # Nothing to report: there is no format to name.
        title, body = engine.toast_text(['holiday'])
        self.assertEqual(title, 'DropLink 收到 holiday')
        self.assertEqual(body, '')

    def test_two_files_are_listed(self):
        title, body = engine.toast_text(['IMG_0001.PNG', 'IMG_0002.PNG'])
        self.assertEqual(title, 'DropLink 收到 2 个文件')
        self.assertEqual(body, 'IMG_0001.PNG、IMG_0002.PNG')

    def test_long_list_is_truncated_with_a_suffix(self):
        names = ['a%d.PNG' % n for n in range(1, 6)]
        title, body = engine.toast_text(names)
        self.assertEqual(title, 'DropLink 收到 5 个文件')
        self.assertEqual(body, 'a1.PNG、a2.PNG、a3.PNG 等')

    def test_live_photo_reports_the_format_it_will_keep(self):
        # A Live Photo arrives as a *.pvt folder plus the still copy the
        # receiver makes, so the extension alone does not explain the result.
        # The folder itself is a container and must not be listed as a format.
        title, body = engine.toast_text(['IMG_0001.pvt', 'IMG_0001.HEIC'])
        self.assertEqual(title, 'DropLink 收到 2 个文件')
        self.assertEqual(body, 'IMG_0001.pvt、IMG_0001.HEIC\n电脑可查看：HEIC')

    def test_format_line_can_be_switched_off(self):
        _title, body = engine.toast_text(['IMG_0001.pvt', 'IMG_0001.HEIC'],
                                         show_format=False)
        self.assertNotIn('电脑可查看', body)

    def test_nothing_received_still_reads_as_a_sentence(self):
        title, body = engine.toast_text([])
        self.assertEqual(title, 'DropLink 收到文件')
        self.assertEqual(body, '已保存到接收文件夹')

    def test_stored_objects_are_accepted(self):
        # names may be Stored str subclasses, not plain str.
        class Stored(str):
            pass
        title, body = engine.toast_text([Stored('IMG_0001.PNG')])
        self.assertEqual(title, 'DropLink 收到 IMG_0001.PNG')
        self.assertEqual(body, '')


if __name__ == '__main__':
    unittest.main()
