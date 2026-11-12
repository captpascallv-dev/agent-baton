"""Small real macOS smoke tests; never allocate large amounts of memory."""
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

RUNNER = Path(__file__).resolve().parents[1] / 'capped_run.py'
spec = importlib.util.spec_from_file_location('capped_run', RUNNER)
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)

@unittest.skipUnless(sys.platform == 'darwin', 'macOS runner')
class MacRunnerTests(unittest.TestCase):
    def invoke(self, source, mem='0.2', timeout='5'):
        with tempfile.TemporaryDirectory() as tmp:
            result = subprocess.run([sys.executable, str(RUNNER), '--mem-gb', mem,
                '--timeout', timeout, '--log', str(Path(tmp) / 'test.log'), '--',
                sys.executable, '-c', source], capture_output=True, text=True, timeout=15)
            return result

    def test_child_exit(self):
        result = self.invoke('print("hello"); raise SystemExit(7)')
        self.assertEqual(result.returncode, 7, result.stdout + result.stderr)
        self.assertIn('hello', result.stdout)

    def test_timeout(self):
        result = self.invoke('import time; time.sleep(10)', timeout='1')
        self.assertEqual(result.returncode, 124, result.stdout + result.stderr)

    def test_memory_sample(self):
        # The normal interpreter footprint exceeds 1 MiB. No pressure test.
        result = self.invoke('import time; time.sleep(10)', mem='0.001')
        self.assertEqual(result.returncode, 137, result.stdout + result.stderr)

    def test_missing_command(self):
        with tempfile.TemporaryDirectory() as tmp:
            result = subprocess.run([sys.executable, str(RUNNER), '--log',
                str(Path(tmp) / 'test.log'), '--', '/nonexistent-capped-run-command'],
                capture_output=True, text=True, timeout=15)
        self.assertEqual(result.returncode, 125, result.stdout + result.stderr)

    def test_tree_accounting(self):
        output = '10 1 10 100\n11 10 10 200\n12 11 12 300\n99 1 99 900\n'
        with mock.patch.object(runner.subprocess, 'run', return_value=mock.Mock(stdout=output)):
            self.assertEqual(runner.process_tree_rss(10), 600 * 1024)

    def test_sampling_failure(self):
        import argparse
        with tempfile.TemporaryDirectory() as tmp:
            args = argparse.Namespace(cwd=tmp, timeout=5, mem_gb=0.2, tail=0)
            with mock.patch.object(runner, "process_tree_rss", side_effect=OSError("sample unavailable")):
                code = runner.run_macos(args, [sys.executable, "-c", "import time;time.sleep(10)"],
                    200 * 1024 * 1024, str(Path(tmp) / "test.log"))
            self.assertEqual(code, 125)

    def test_timeout_group_child_killed(self):
        self.check_group_child_killed(True)

    def test_leftover_group_child_killed(self):
        self.check_group_child_killed(False)

    def check_group_child_killed(self, timeout):
        with tempfile.TemporaryDirectory() as tmp:
            pid_file = Path(tmp) / 'pid'
            source = ('import subprocess,sys,time; from pathlib import Path; '
                      'p=subprocess.Popen([sys.executable,"-c","import time;time.sleep(20)"]); '
                      f'Path({str(pid_file)!r}).write_text(str(p.pid)); time.sleep({10 if timeout else 0.2})')
            result = self.invoke(source, timeout="1" if timeout else "5")
            self.assertEqual(result.returncode, 124 if timeout else 0, result.stdout + result.stderr)
            pid = pid_file.read_text()
            for _ in range(30):
                state = subprocess.run(['/bin/ps', '-p', pid, '-o', 'stat='],
                    capture_output=True, text=True).stdout.strip()
                if not state or state.startswith('Z'):
                    break
                time.sleep(0.1)
            self.assertTrue(not state or state.startswith('Z'), state)

if __name__ == '__main__':
    unittest.main()
