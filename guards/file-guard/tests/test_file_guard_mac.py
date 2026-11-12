"""macOS fixture-only policy and native-trash checks."""
import errno
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import file_guard as guard

@unittest.skipUnless(sys.platform == 'darwin', 'macOS policy')
class MacGuardTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix='file-guard-mac-')).resolve()
        self.config = self.root / 'config.json'
        self.protected = self.root / 'protected'
        self.protected.mkdir()
        self.config.write_text(json.dumps({'protected_roots':[str(self.protected)]}))
        self.env = patch.dict(os.environ, {'FILE_GUARD_CONFIG':str(self.config)})
        self.env.start()
    def tearDown(self):
        self.env.stop()
        # Fixtures only; never remove permanently. Leave them if trash fails.
        guard.subprocess.run(["/usr/bin/trash", "-s", str(self.root)], check=True, capture_output=True)
    def target(self, name='fixture.txt'):
        p = self.root / name
        p.write_text('file guard fixture')
        return p
    def test_plain_absolute(self):
        p=self.target()
        self.assertEqual(guard.path_policy(str(p)), (None,str(p)))
    def test_dynamic_relative_and_roots(self):
        for value in ['relative', str(self.root/'$bad'), str(self.root/'*'), '/', str(self.root), str(self.protected), str(self.root)+'/../target']:
            with self.subTest(value=value):
                self.assertIsNotNone(guard.path_policy(value)[0])
    def test_symlink_and_ancestor(self):
        p=self.target(); alias=self.root/'link'; alias.symlink_to(p)
        self.assertEqual(guard.path_policy(str(alias))[0]['code'],'DELETE_TARGET_NOT_CANONICAL')
        d=self.root/'dir'; d.mkdir(); link=self.root/'dir-link'; link.symlink_to(d)
        self.assertEqual(guard.path_policy(str(link/'missing'))[0]['code'],'DELETE_TARGET_NOT_CANONICAL')
    def test_finder_alias_refused(self):
        p=self.target(); info=b'\0'*8+b'\x80\x00'+b'\0'*22
        with patch.object(guard,'mac_finder_info',return_value=info):
            self.assertEqual(guard.path_policy(str(p))[0]['code'],'DELETE_TARGET_NOT_CANONICAL')
    def test_xattr_error_fails_closed(self):
        p=self.target()
        with patch.object(guard,'mac_finder_info',side_effect=OSError(errno.EACCES,'denied')):
            self.assertEqual(guard.path_policy(str(p))[0]['code'],'DELETE_TARGET_UNRESOLVABLE')
    def test_mount_root_refused(self):
        p=self.target()
        with patch.object(guard.os.path, "ismount", return_value=True):
            self.assertEqual(guard.path_policy(str(p))[0]["code"], "PROTECTED_TARGET")
    def test_batch_preflight(self):
        p=self.target()
        with patch.object(guard.subprocess,'run') as run:
            result,status=guard.recycle([str(p),'relative'])
            self.assertEqual(status,2); run.assert_not_called(); self.assertTrue(p.exists())
    def test_failed_trash_no_fallback(self):
        p=self.target()
        with patch.object(guard.subprocess,'run',side_effect=OSError('cross volume failure')):
            result,status=guard.recycle([str(p)])
            self.assertEqual(status,3); self.assertTrue(p.exists())
    def test_native_trash_fixture(self):
        p=self.target('native-trash-fixture.txt')
        result,status=guard.recycle([str(p)])
        self.assertEqual(status,0,result); self.assertFalse(p.exists())
    def test_command_and_move(self):
        p=self.target()
        self.assertEqual(guard.inspect({'command':'rm '+str(p),'cwd':str(self.root),'shell':'bash'})['code'],'USE_RECYCLER')
        self.assertEqual(guard.moving(['relative'],str(self.root/'dest'),str(self.root))['decision'],'deny')
    def test_preimage(self):
        p=self.target()
        result,status=guard.preimage([str(p)],str(self.root/'copies'))
        self.assertEqual(status,0,result); self.assertTrue(p.exists())

if __name__ == '__main__': unittest.main()
