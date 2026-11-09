import argparse
import hashlib
import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('content_cleanup', Path(__file__).resolve().parents[1] / 'content.py')
content = importlib.util.module_from_spec(spec)
spec.loader.exec_module(content)


class ContentStagingCleanupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        payload = self.base / 'authored.bundle'
        payload.write_bytes(b'authored bytes remain outside staging')
        self.candidate = {'type': 'object', 'id': 'tool.test', 'variants': [{
            'platform': 'WebGL', 'runtimeProfile': 'unity6000-content1',
            'stagedPath': str(payload), 'sha256': hashlib.sha256(payload.read_bytes()).hexdigest(),
            'size': payload.stat().st_size, 'attachments': []}]}
        self.args = argparse.Namespace(asset_root=self.base / 'assets', server_dll=str(self.base / 'server.dll'),
            retain_current_variants=False, host='test-host', remote_root='/var/lib/hexlive/assets', remote_user='hexlive')
        Path(self.args.server_dll).touch()

    def test_success_removes_only_owned_local_transport_and_keeps_source_and_registry(self):
        record = self.args.asset_root / 'records' / 'current.json'
        record.parent.mkdir(parents=True)
        record.write_text('current pointer')
        with patch.object(content.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0)):
            self.assertEqual(content.publish_local(self.candidate, self.args), 0)
        self.assertEqual(list((self.args.asset_root / 'staging').iterdir()), [])
        self.assertEqual(record.read_text(), 'current pointer')
        self.assertTrue((self.base / 'authored.bundle').is_file())

    def test_failure_keeps_local_staged_bytes_for_reproduction(self):
        with patch.object(content.subprocess, 'run', return_value=subprocess.CompletedProcess([], 3)):
            self.assertEqual(content.publish_local(self.candidate, self.args), 3)
        stages = list((self.args.asset_root / 'staging').iterdir())
        self.assertEqual(len(stages), 1)
        staged = stages[0] / self.candidate['variants'][0]['sha256']
        self.assertEqual(staged.read_bytes(), (self.base / 'authored.bundle').read_bytes())

    def remote(self, code):
        with patch.object(content, 'prepare_remote_stage'), patch.object(content, 'upload_remote_blob'), \
             patch.object(content, 'run_checked') as checked, \
             patch.object(content.subprocess, 'run', return_value=subprocess.CompletedProcess([], code)):
            self.assertEqual(content.publish_remote(self.candidate, self.args), code)
            return [call.args[0] for call in checked.call_args_list if call.args[0][:3] == ['ssh', 'test-host', 'rm']]

    def test_remote_success_removes_only_the_uuid_transport_subdirectory(self):
        removes = self.remote(0)
        self.assertEqual(len(removes), 1)
        self.assertEqual(removes[0][3:5], ['-rf', '--'])
        target = removes[0][-1]
        self.assertTrue(target.startswith('/var/lib/hexlive/assets/staging/'))
        self.assertRegex(target.rsplit('/', 1)[1], r'^[a-f0-9]{32}$')

    def test_remote_failure_does_not_remove_transport(self):
        self.assertEqual(self.remote(1), [])


if __name__ == '__main__':
    unittest.main()
