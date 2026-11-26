"""File selection for completed-transfer notifications."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools/portable/backend/airdrop'))
from open_batch import batch_target


class OpenBatchTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.folder = Path(temp.name) / 'received'
        self.folder.mkdir()
        self.history = self.folder.parent / 'history.json'

    def record(self, names):
        row = {'Id': 'r1', 'Folder': str(self.folder), 'Files': names}
        self.history.write_text(json.dumps([row]), encoding='utf-8-sig')

    def test_single_remaining_file_is_selected(self):
        path = self.folder / '照片.jpg'
        path.touch()
        self.record(['removed.jpg', path.name])
        self.assertEqual(batch_target(self.history, 'r1'), (self.folder, path))

    def test_multi_file_batch_opens_folder(self):
        for name in ['photo.jpg', 'photo.mov']:
            (self.folder / name).touch()
        self.record(['photo.jpg', 'photo.mov'])
        self.assertEqual(batch_target(self.history, 'r1'), (self.folder, None))

    def test_outside_history_folder_is_never_selected(self):
        self.record(['../history.json'])
        self.assertEqual(batch_target(self.history, 'r1'), (self.folder, None))
        self.assertIsNone(batch_target(self.history, 'unknown'))
