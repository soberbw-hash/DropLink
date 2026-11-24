"""Wording of the completion toast, tested without a desktop.

Two things are pinned here. First, the line carries only the file name: the
notification header already says DropLink, so "DropLink 收到 " in front of the
name spent a third of the line saying nothing. Second, the name has to stay on
one line - the shell wraps a long <text> instead of clipping it, and a wrapped
file name reads as two unrelated strings. Truncation keeps the extension and
cuts the stem, because "…D25D8B52E7F6.PNG" is unreadable while
"81FA1FEE-A0D6-4CB0….PNG" still identifies the file.
"""
import os
import pathlib
import sys
import unittest

sys.path.insert(0, os.environ.get('DROPLINK_AIRDROP_DIR', str(pathlib.Path(__file__).resolve().parents[1] / '.prepared/winawdl/airdrop')))
import engine


class ToastTextTests(unittest.TestCase):
    def test_single_file_is_the_name_alone(self):
        title, body = engine.toast_text(['IMG_0001.PNG'])
        self.assertEqual(title, 'IMG_0001.PNG')
        self.assertEqual(body, '', 'the file name already says the format')

    def test_single_extensionless_file_still_one_line(self):
        # Nothing to report: there is no format to name.
        title, body = engine.toast_text(['holiday'])
        self.assertEqual(title, 'holiday')
        self.assertEqual(body, '')

    def test_two_files_are_summarised_on_one_line(self):
        title, body = engine.toast_text(['IMG_0001.PNG', 'IMG_0002.PNG'])
        self.assertEqual(title, '2 个文件 · IMG_0001.PNG')
        self.assertEqual(body, '')

    def test_long_list_still_one_line(self):
        names = ['a%d.PNG' % n for n in range(1, 6)]
        title, body = engine.toast_text(names)
        self.assertEqual(title, '5 个文件 · a1.PNG')
        self.assertEqual(body, '')

    def test_count_prefix_and_long_name_still_fit_one_line(self):
        name = '81FA1FEE-A0D6-4CB0-B930-D25D8B52E7F6.PNG'
        title, _body = engine.toast_text([name, 'b.PNG'])
        self.assertLessEqual(engine._toast_width(title), engine.TOAST_LINE_WIDTH)
        self.assertTrue(title.endswith('.PNG'), title)

    def test_live_photo_reports_the_format_it_will_keep(self):
        # A Live Photo arrives as a *.pvt folder plus the still copy the
        # receiver makes, so the extension alone does not explain the result.
        # The folder itself is a container and must not be listed as a format.
        # The line says what else is there - it must not promise that this
        # machine can open it, because HEIC/HEVC codecs are not always present.
        title, body = engine.toast_text(['IMG_0001.pvt', 'IMG_0001.HEIC'])
        self.assertEqual(title, '2 个文件 · IMG_0001.pvt')
        self.assertEqual(body, '另有：HEIC')
        self.assertNotIn('可查看', body)

    def test_format_line_can_be_switched_off(self):
        _title, body = engine.toast_text(['IMG_0001.pvt', 'IMG_0001.HEIC'],
                                         show_format=False)
        self.assertEqual(body, '')

    def test_nothing_received_still_reads_as_a_sentence(self):
        title, body = engine.toast_text([])
        self.assertEqual(title, '已保存到接收文件夹')
        self.assertEqual(body, '')

    def test_stored_objects_are_accepted(self):
        # names may be Stored str subclasses, not plain str.
        class Stored(str):
            pass
        title, body = engine.toast_text([Stored('IMG_0001.PNG')])
        self.assertEqual(title, 'IMG_0001.PNG')
        self.assertEqual(body, '')


class FitNameTests(unittest.TestCase):
    def test_short_name_is_untouched(self):
        self.assertEqual(engine.fit_name('IMG_0001.JPG'), 'IMG_0001.JPG')

    def test_long_uuid_name_keeps_its_extension(self):
        name = '81FA1FEE-A0D6-4CB0-B930-D25D8B52E7F6.PNG'
        fitted = engine.fit_name(name)
        self.assertTrue(fitted.endswith('.PNG'), fitted)
        self.assertIn('…', fitted)
        self.assertLessEqual(engine._toast_width(fitted), engine.TOAST_LINE_WIDTH)
        # The beginning is what identifies the file; the tail is what gets cut.
        self.assertTrue(fitted.startswith('81FA1FEE-A0D6'), fitted)

    def test_long_chinese_name_keeps_its_extension(self):
        name = '这是一个非常非常长的中文文件名用来测试截断效果和宽度计算的照片.jpg'
        fitted = engine.fit_name(name)
        self.assertTrue(fitted.endswith('.jpg'), fitted)
        self.assertLessEqual(engine._toast_width(fitted), engine.TOAST_LINE_WIDTH)

    def test_name_without_extension_is_still_cut(self):
        fitted = engine.fit_name('a' * 80)
        self.assertTrue(fitted.endswith('…'), fitted)
        self.assertLessEqual(engine._toast_width(fitted), engine.TOAST_LINE_WIDTH)

    def test_width_counts_cjk_as_two_columns(self):
        self.assertEqual(engine._toast_width('ab'), 2)
        self.assertEqual(engine._toast_width('照片'), 4)

    def test_live_photo_says_what_it_is(self):
        # One photo on the phone, two files on the PC. "2 个文件" tells the user
        # nothing about why, and the video is the half that will not open.
        still, video = 'IMG_4147.jpg', 'IMG_4147.MOV'
        title, body = engine.toast_text([still, video], True, [[still, video]])
        self.assertEqual(title, '实况图 · IMG_4147.jpg')
        self.assertEqual(body, '视频 IMG_4147.MOV')

    def test_live_photo_name_still_fits_one_line(self):
        long = '这是一个非常非常长的实况图名字用来测试宽度计算.jpg'
        still, video = long, long.replace('.jpg', '.MOV')
        title, body = engine.toast_text([still, video], True, [[still, video]])
        for line in (title, body):
            self.assertLessEqual(engine._toast_width(line), engine.TOAST_LINE_WIDTH)


if __name__ == '__main__':
    unittest.main()
