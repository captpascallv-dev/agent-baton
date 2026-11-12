import importlib.util
import pathlib
import sys
import unittest

spec = importlib.util.spec_from_file_location('guard_test_runs', pathlib.Path(__file__).parents[1] / 'guard_test_runs.py')
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)

@unittest.skipUnless(sys.platform == 'darwin', 'macOS command spellings')
class MacCommands(unittest.TestCase):
    def test_block(self):
        for command in ['pytest -q', '/usr/local/bin/pytest -q', 'env PYTHONPATH=src pytest -q', 'env PYTEST_ADDOPTS="-q --maxfail=1" pytest', '/usr/local/bin/node --test', 'python3 -m unittest discover', 'npm run test:live', 'cd /tmp; pytest -q']:
            with self.subTest(command=command): self.assertTrue(g.needs_cap(command))
    def test_multiline(self):
        for command in [
            'echo setup\npytest -q',
            'echo setup\n/usr/local/bin/pytest -q',
            'echo setup # preparation\npytest -q',
            '# preparation only\n/usr/local/bin/pytest -q',
        ]:
            with self.subTest(command=command):
                self.assertTrue(g.needs_cap(command))

    def test_allow(self):
        for command in ['echo pytest', 'python3 guards/capped_run.py --mem-gb 1 --timeout 60 -- pytest -q', 'git status']:
            with self.subTest(command=command): self.assertFalse(g.needs_cap(command))

if __name__ == '__main__': unittest.main()
