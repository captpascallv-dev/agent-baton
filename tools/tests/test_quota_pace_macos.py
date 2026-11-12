import importlib.util
import json
import io
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / 'quota_pace.py'
spec = importlib.util.spec_from_file_location('quota_pace', SCRIPT)
pace = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pace)

class QuotaPaceTests(unittest.TestCase):
    def test_mac_default(self):
        with mock.patch.object(sys, 'platform', 'darwin'):
            self.assertEqual(pace.default_snapshot(), str(Path.home() / 'Library/Application Support/CodexUsageHUD/quota-snapshot.json'))

    def test_other_default_unchanged(self):
        with mock.patch.object(sys, 'platform', 'win32'):
            self.assertEqual(pace.default_snapshot(), os.path.expandvars(r'%LOCALAPPDATA%\CodexUsageHUD\quota-snapshot.json'))

    def test_schema_one_and_unknown(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'quota-snapshot.json'
            path.write_text(json.dumps({'schema':1,'slots':[{'slot':'codex_primary','windows':[{'pool':'codex_primary_window','remaining_percent':73.5,'resets_at_utc':None},{'pool':'codex_weekly','remaining_percent':None,'resets_at_utc':None}]}]}))
            output = io.BytesIO()
            # main writes UTF-8 to stdout.buffer. Mock the platform and argv in-process
            # so this macOS contract is tested identically on Windows and Linux hosts.
            stdout = mock.Mock(buffer=output)
            with mock.patch.object(sys, 'platform', 'darwin'), \
                 mock.patch.object(sys, 'argv', [str(SCRIPT), str(path)]), \
                 mock.patch.object(sys, 'stdout', stdout):
                pace.main()
            text = output.getvalue().decode('utf-8')
            self.assertIn('73.5%', text)
            self.assertIn('left unknown', text)

if __name__=='__main__': unittest.main()
