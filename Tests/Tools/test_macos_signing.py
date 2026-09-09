import importlib.util
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('macos_signing', Path(__file__).resolve().parents[2] / 'Tools/macos_signing.py')
SIGNING = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SIGNING)


class MacSigningTests(unittest.TestCase):
    def test_configured_identity_is_not_silently_downgraded_if_signing_fails(self):
        fingerprint = 'A' * 40
        with patch.object(SIGNING, 'identity', return_value=fingerprint), \
                patch.object(SIGNING.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'codesign')) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                SIGNING.seal(Path('/test.app'))
        self.assertEqual(run.call_count, 1)
        self.assertIn(fingerprint, run.call_args.args[0])
        self.assertNotIn('-', run.call_args.args[0])

    def test_environment_overrides_local_public_fingerprint(self):
        with tempfile.TemporaryDirectory() as directory:
            config = Path(directory) / 'identity'
            config.write_text('A' * 40 + '\n')
            with patch.object(SIGNING, 'IDENTITY_FILE', config), patch.dict(SIGNING.os.environ, {'HEXLIVE_MACOS_SIGN_IDENTITY': ''}):
                self.assertEqual(SIGNING.identity(), 'A' * 40)
                with patch.dict(SIGNING.os.environ, {'HEXLIVE_MACOS_SIGN_IDENTITY': 'B' * 40}):
                    self.assertEqual(SIGNING.identity(), 'B' * 40)

    def test_invalid_identity_is_rejected_before_signing(self):
        with patch.dict(SIGNING.os.environ, {'HEXLIVE_MACOS_SIGN_IDENTITY': '--anything'}):
            with self.assertRaises(ValueError):
                SIGNING.identity()

    def test_unconfigured_optional_signing_preserves_existing_signature(self):
        with patch.object(SIGNING, 'identity', return_value=None), patch.object(SIGNING.subprocess, 'run') as run:
            SIGNING.seal(Path('/test.app'), configured_only=True)
            run.assert_not_called()

    def test_signature_must_pass_verification(self):
        with patch.object(SIGNING, 'identity', return_value='A' * 40), patch.object(SIGNING.subprocess, 'run') as run:
            SIGNING.seal(Path('/test.app'))
            self.assertEqual(run.call_count, 2)
            self.assertIn('--verify', run.call_args.args[0])
            self.assertTrue(run.call_args.kwargs['check'])


if __name__ == '__main__':
    unittest.main()
