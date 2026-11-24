"""Regression check against the pinned, patched WinAWDL source tree."""
import os
import pathlib
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.environ.get('DROPLINK_AIRDROP_DIR', str(pathlib.Path(__file__).resolve().parents[1] / '.prepared/winawdl/airdrop')))
import engine


class EngineDirectoryTests(unittest.TestCase):
    def test_explicit_output_does_not_validate_unused_default(self):
        original = engine.receiver.valid_dir
        def reject_unused_default(value):
            if value == '~/Downloads':
                raise AssertionError('unused default must not be validated')
            return original(value)
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.object(engine.receiver, 'valid_dir', side_effect=reject_unused_default):
                args = engine.parse_args(['--outdir', directory])
            self.assertEqual(pathlib.Path(args.dest), pathlib.Path(directory))
            self.assertTrue(args.ask, 'incoming files must require confirmation by default')

    def test_missing_explicit_output_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            missing = str(pathlib.Path(directory) / 'missing')
            with self.assertRaises(SystemExit):
                engine.parse_args(['--outdir', missing])


if __name__ == '__main__':
    unittest.main()
