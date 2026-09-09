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
    def setUp(self):
        identifier = patch.object(SIGNING, 'bundle_identifier', return_value='com.hexlive.test')
        identifier.start()
        self.addCleanup(identifier.stop)

    def test_configured_identity_is_not_silently_downgraded_if_signing_fails(self):
        fingerprint = 'A' * 40
        with patch.object(SIGNING, 'require_identity', return_value=fingerprint), \
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

    def test_missing_configuration_fails_without_touching_app(self):
        with patch.object(SIGNING, 'identity', return_value=None), patch.object(SIGNING.subprocess, 'run') as run:
            with self.assertRaisesRegex(RuntimeError, 'required'):
                SIGNING.seal(Path('/test.app'))
            run.assert_not_called()

    def test_missing_or_invalid_private_key_fails_preflight(self):
        result = subprocess.CompletedProcess([], 0, '0 valid identities found', '')
        with patch.object(SIGNING, 'identity', return_value='A' * 40), \
                patch.object(SIGNING.subprocess, 'run', return_value=result) as run:
            with self.assertRaisesRegex(RuntimeError, 'unavailable or invalid'):
                SIGNING.require_identity()
        self.assertEqual(run.call_count, 1)
        self.assertIn('find-identity', run.call_args.args[0])

    def test_configured_identity_must_be_in_valid_identity_inventory(self):
        fingerprint = 'A' * 40
        result = subprocess.CompletedProcess([], 0, f'  1) {fingerprint} "Apple Development: Test"\n', '')
        with patch.object(SIGNING, 'identity', return_value=fingerprint.lower()), \
                patch.object(SIGNING.subprocess, 'run', return_value=result):
            self.assertEqual(SIGNING.require_identity(), fingerprint.lower())

    def test_changed_bundle_id_is_rejected_before_any_signing(self):
        with patch.object(SIGNING.subprocess, 'run') as run:
            with self.assertRaisesRegex(ValueError, 'Bundle ID changed'):
                SIGNING.seal(Path('/test.app'), expected_identifier='com.hexlive.other')
        run.assert_not_called()

    def test_signature_must_pass_verification(self):
        with patch.object(SIGNING, 'require_identity', return_value='A' * 40), patch.object(SIGNING.subprocess, 'run') as run:
            SIGNING.seal(Path('/test.app'))
            self.assertEqual(run.call_count, 3)
            self.assertIn('--verify', run.call_args.args[0])
            self.assertTrue(run.call_args.kwargs['check'])
            outer = run.call_args_list[1].args[0]
            self.assertIn('--identifier', outer)
            self.assertIn('com.hexlive.test', outer)
            self.assertNotIn('--deep', outer)

    def test_existing_distribution_certificate_is_not_replaced(self):
        result = subprocess.CompletedProcess([], 0, '', 'Authority=Developer ID Application: Distribution Owner')
        with patch.object(SIGNING, 'identity', return_value='A' * 40), \
                patch.object(SIGNING.subprocess, 'run', return_value=result) as run:
            SIGNING.seal(Path('/test.app'), preserve_distribution=True)
            self.assertEqual(run.call_count, 2)
            self.assertIn('--verify', run.call_args.args[0])
            for call in run.call_args_list:
                self.assertNotIn('--sign', call.args[0])

    def test_invalid_distribution_signature_is_not_preserved_or_replaced(self):
        result = subprocess.CompletedProcess([], 0, '', 'Authority=Developer ID Application: Test')
        with patch.object(SIGNING.subprocess, 'run', side_effect=[result, subprocess.CalledProcessError(1, 'codesign')]) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                SIGNING.seal(Path('/test.app'), preserve_distribution=True)
        self.assertEqual(run.call_count, 2)


if __name__ == '__main__':
    unittest.main()
