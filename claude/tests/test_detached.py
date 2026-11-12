"""Offline POSIX detach regressions. Never invokes a model or reads credentials.

Run: PWSH_PATH=/path/to/pwsh python3 -m unittest discover -s claude/tests -p test_detached.py -v
The capped_run case is macOS-only because that guard does not support Linux.
"""
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
import uuid

REPO = Path(__file__).resolve().parents[2]
PWSH = os.environ.get('PWSH_PATH') or shutil.which('pwsh')


@unittest.skipUnless(os.name == 'posix' and PWSH, 'POSIX and PowerShell required')
class DetachedDispatchTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix='baton detach 空间 '))
        self.marker = uuid.uuid4().hex
        (self.root / 'test-owner').write_text(self.marker)
        self.sandbox = self.root / 'claude tree 中文'
        shutil.copytree(REPO / 'claude' / 'bin', self.sandbox / 'bin')
        if os.environ.get('DISPATCH_SCRIPT'):
            shutil.copy2(os.environ['DISPATCH_SCRIPT'], self.sandbox / 'bin' / 'Dispatch.ps1')
        self.workspace = self.root / 'workspace 空 间'
        self.workspace.mkdir()
        self.outbox = self.root / 'outbox 交付'
        self.card = self.root / 'task card 中文.md'
        self.card.write_text('Offline dummy task only.', encoding='utf-8')
        fake = self.root / 'fake executor 中文.py'
        fake.write_text('import json, pathlib, sys, time\ntime.sleep(1)\n'
                        'p = pathlib.Path(sys.argv[1]); p.mkdir(exist_ok=True)\n'
                        'deadline=time.monotonic()+20\n'
                        'while not (p / "release").exists():\n'
                        ' if time.monotonic()>deadline: sys.exit(9)\n'
                        ' time.sleep(.05)\n'
                        '(p / "READY.json").write_text(json.dumps({"status":"ok"}))\n', encoding='utf-8')
        self.config = self.root / 'executors 中文.json'
        self.config.write_text(json.dumps({'executors': {'fake': {
            'command': sys.executable, 'args': [str(fake), '{outbox}'], 'stdin_card': False}}}), encoding='utf-8')
        self.job = self.sandbox / 'jobs' / 'offline-detach'
        self.env = dict(os.environ)
        # Dispatch may resolve its child shell by PATH even when PWSH_PATH is explicit.
        self.env['PATH'] = str(Path(PWSH).resolve().parent) + os.pathsep + self.env.get('PATH', '')
        self.initiators = []

    def command(self):
        return [PWSH, '-NoProfile', '-File', str(self.sandbox / 'bin' / 'Dispatch.ps1'),
                '-Executor', 'fake', '-Workspace', str(self.workspace), '-Outbox', str(self.outbox),
                '-PromptFile', str(self.card), '-Task', '离线 detach 测试', '-JobId', 'offline-detach',
                '-Config', str(self.config), '-TimeoutMinutes', '1']

    def wait_for(self, predicate, seconds=20):
        end = time.monotonic() + seconds
        while time.monotonic() < end:
            if predicate():
                return
            time.sleep(.05)
        self.fail('Timed out waiting for offline fixture; artifacts: ' + str(self.root))

    def assert_delivery(self):
        self.wait_for(lambda: (self.job / 'job.json').exists())
        metadata = json.loads((self.job / 'job.json').read_text(encoding='utf-8-sig'))
        self.assertFalse((self.outbox / 'READY.json').exists(), 'Fixture delivered before caller cleanup')
        (self.outbox / 'release').write_text('caller cleanup completed')
        result = subprocess.run([sys.executable, str(self.sandbox / 'bin' / 'wait_delivery.py'),
            '--job-dir', str(self.outbox), '--process-id', str(metadata['host_pid']),
            '--timeout-minutes', '0.3', '--interval-seconds', '1'],
            env=self.env, capture_output=True, text=True, timeout=25)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.wait_for(lambda: (self.job / 'terminal.json').exists())
        owner = json.loads((self.job / 'owner.json').read_text(encoding='utf-8-sig'))
        terminal = json.loads((self.job / 'terminal.json').read_text(encoding='utf-8-sig'))
        self.assertEqual(metadata['host_pid'], owner['pid'])
        self.assertEqual(owner['pid'], terminal['pid'])
        self.assertEqual(terminal['exit_code'], 0)
        self.assertEqual((self.job / 'exit_code.txt').read_text(encoding='utf-8-sig').strip(), '0')
        self.assertTrue((self.outbox / 'READY.json').is_file())

    @unittest.skipUnless(sys.platform == 'darwin', 'capped_run POSIX backend is macOS-only')
    def test_dispatch_only_capped_run_spaces_unicode(self):
        # Intentionally cap ONLY Dispatch: a correctly detached executor must outlive it.
        result = subprocess.run([sys.executable, str(REPO / 'guards' / 'capped_run.py'),
            '--mem-gb', '1', '--timeout', '30', '--log', str(self.root / 'dispatch cap.log'),
            '--', *self.command()], env=self.env, capture_output=True, text=True, timeout=40)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('DISPATCHED', (self.root / 'dispatch cap.log').read_text(errors='replace'))
        self.assert_delivery()

    def test_killed_initiating_session_does_not_kill_executor(self):
        log = self.root / 'initiator 空间.log'
        with log.open('wb') as stream:
            proc = subprocess.Popen(['/bin/sh', '-c', '"$@"; sleep 30', 'offline-initiator',
                                     *self.command()], env=self.env, stdin=subprocess.DEVNULL,
                                    stdout=stream, stderr=stream, start_new_session=True)
        self.initiators.append(proc)
        self.wait_for(lambda: 'DISPATCHED' in log.read_text(errors='replace'))
        # The shell stays alive after dispatch so this is its recorded private session.
        self.assertIsNone(proc.poll())
        os.killpg(proc.pid, signal.SIGTERM)
        proc.wait(timeout=5)
        self.assert_delivery()

    def tearDown(self):
        # Bound cleanup to private initiating sessions and command lines bearing this
        # unique fixture root. Never signal a PID learned from an untrusted artifact.
        for proc in self.initiators:
            if proc.poll() is None:
                try:
                    os.killpg(proc.pid, signal.SIGTERM)
                    proc.wait(timeout=2)
                except subprocess.TimeoutExpired:
                    os.killpg(proc.pid, signal.SIGKILL)
                    proc.wait(timeout=2)
                except ProcessLookupError:
                    pass
        for sig in (signal.SIGTERM, signal.SIGKILL):
            rows = subprocess.run(['/bin/ps', '-axo', 'pid=,command='], capture_output=True,
                                  text=True, check=True, timeout=5).stdout.splitlines()
            for row in rows:
                parts = row.strip().split(None, 1)
                if len(parts) == 2 and str(self.root) in parts[1]:
                    pid = int(parts[0])
                    if pid != os.getpid():
                        try:
                            os.kill(pid, sig)
                        except ProcessLookupError:
                            pass
            time.sleep(.2)
        self.assertEqual((self.root / 'test-owner').read_text(), self.marker)
        if sys.platform == 'darwin':
            # Native Trash CLI; no UI scripting or permanent deletion.
            subprocess.run(['/usr/bin/trash', '-s', str(self.root)],
                           check=True, capture_output=True, text=True, timeout=15)
        else:
            shutil.rmtree(self.root)


if __name__ == '__main__':
    unittest.main()
