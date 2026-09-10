import importlib.util
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('macos_signing', Path(__file__).parents[1] / 'macos_signing.py')
signing = importlib.util.module_from_spec(spec)
spec.loader.exec_module(signing)


class SigningAccessTests(unittest.TestCase):
    def test_listed_identity_with_denied_key_fails_before_publication(self):
        paths = []
        def denied(args, **kwargs):
            paths.append(Path(args[-1]))
            return subprocess.CompletedProcess(args, 1, '', 'errSecInternalComponent')
        with patch.object(signing, 'require_identity', return_value='A' * 40), patch.object(signing.subprocess, 'run', side_effect=denied):
            with self.assertRaisesRegex(RuntimeError, 'private-key signing probe failed'):
                signing.check_signing_access()
        self.assertEqual(len(paths), 1)
        self.assertFalse(paths[0].exists())

    def test_same_identity_is_used_and_probe_is_verified_then_removed(self):
        with patch.object(signing, 'require_identity', return_value='B' * 40), patch.object(signing.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, '', '')) as run:
            self.assertEqual(signing.check_signing_access(), 'B' * 40)
        first, second = run.call_args_list
        self.assertIn('B' * 40, first.args[0])
        self.assertIn('--verify', second.args[0])
        self.assertEqual(first.args[0][-1], second.args[0][-1])
        self.assertFalse(Path(first.args[0][-1]).exists())

    def test_keychain_prompt_cannot_wait_indefinitely(self):
        with patch.object(signing, 'require_identity', return_value='C' * 40), patch.object(signing.subprocess, 'run', side_effect=subprocess.TimeoutExpired('codesign', 30)):
            with self.assertRaisesRegex(RuntimeError, 'timed out'):
                signing.check_signing_access()
