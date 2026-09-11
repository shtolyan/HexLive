import importlib.util
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bug_credentials as credentials

class DeviceCredentialsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.local = Path(self.tmp.name) / 'bug-token'
        self.other = Path(self.tmp.name) / 'chosen-token'
        self.local.write_text('device_' + 'a' * 32)
        self.other.write_text('explicit_' + 'b' * 32)
        self.addCleanup(patch.stopall)
        patch.object(credentials, 'device_token_path', return_value=self.local).start()
        patch.dict(os.environ, {'HEXLIVE_BUG_TOKEN': 'wrong_' + 'c' * 32}).start()

    def test_device_file_wins_over_inherited_token(self):
        self.assertEqual(credentials.read_bug_token(), self.local.read_text())

    def test_explicit_file_wins_over_device_and_environment(self):
        self.assertEqual(credentials.read_bug_token(self.other), self.other.read_text())

    def test_missing_explicit_file_does_not_fall_back(self):
        with self.assertRaisesRegex(RuntimeError, 'no fallback'):
            credentials.read_bug_token(self.other.with_name('missing'))

    def test_missing_device_file_does_not_use_environment(self):
        self.local.unlink()
        with self.assertRaises(RuntimeError): credentials.read_bug_token()

    def test_invalid_file_never_leaks_secret_or_falls_back(self):
        invalid = 'bad secret ' + 'x' * 30
        self.other.write_text(invalid)
        with self.assertRaises(RuntimeError) as error: credentials.read_bug_token(self.other)
        self.assertNotIn(invalid, str(error.exception))

    def test_windows_utf8_bom_and_crlf(self):
        self.local.write_bytes(('\ufeff' + 'device_' + 'a' * 32 + '\r\n').encode('utf-8'))
        self.assertEqual(credentials.read_bug_token(), 'device_' + 'a' * 32)

    def test_build_configuration_replaces_stale_environment(self):
        credentials.configure_build_bug_token()
        self.assertEqual(os.environ['HEXLIVE_BUG_TOKEN'], self.local.read_text())

if __name__ == '__main__': unittest.main()
