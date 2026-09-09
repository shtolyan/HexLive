import importlib.util
from pathlib import Path
import plistlib
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mac_build", ROOT / "Tools" / "build_release.py")
BUILD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILD)


class MicrophonePackagingTests(unittest.TestCase):
    def test_optimized_local_player_uses_configured_identity_and_preserves_distribution(self):
        with patch.object(BUILD, 'remove_macos_metadata', return_value=0), \
                patch.object(BUILD, 'verify_code_signature', return_value=(True, 'valid')), \
                patch.object(BUILD.subprocess, 'run') as run:
            BUILD.ensure_development_signature(Path('/test.app'), release=True)
            command = run.call_args.args[0]
            self.assertIn('--configured-only', command)
            self.assertIn('--preserve-distribution', command)

    def test_invalid_release_is_not_downgraded_to_ad_hoc(self):
        with patch.object(BUILD, 'remove_macos_metadata', return_value=0), \
                patch.object(BUILD, 'verify_code_signature', return_value=(False, 'invalid')), \
                patch.object(BUILD.subprocess, 'run') as run:
            with self.assertRaises(RuntimeError):
                BUILD.ensure_development_signature(Path('/test.app'), release=True)
            self.assertEqual(run.call_count, 1)

    def test_localized_permission_preserves_other_plist_values_and_is_idempotent(self):
        with tempfile.TemporaryDirectory() as folder:
            app = Path(folder) / "HexLive.app"
            info_path = app / "Contents" / "Info.plist"
            info_path.parent.mkdir(parents=True)
            original = {"CFBundleIdentifier": "test.hexlive", "CFBundleLocalizations": ["fr"]}
            info_path.write_bytes(plistlib.dumps(original))
            BUILD.configure_microphone_permission(app)
            info = plistlib.loads(info_path.read_bytes())
            self.assertEqual(info["CFBundleIdentifier"], "test.hexlive")
            self.assertEqual(info["CFBundleLocalizations"], ["fr", "en", "ru"])
            self.assertTrue(info["NSMicrophoneUsageDescription"])
            ru = app / "Contents" / "Resources" / "ru.lproj" / "InfoPlist.strings"
            values = plistlib.loads(ru.read_bytes())
            self.assertIn("микрофон", values["NSMicrophoneUsageDescription"])
            values["CFBundleDisplayName"] = "Сохранить название"
            ru.write_bytes(plistlib.dumps(values, sort_keys=False))
            BUILD.configure_microphone_permission(app)
            self.assertEqual(plistlib.loads(ru.read_bytes())["CFBundleDisplayName"], "Сохранить название")
            first = (info_path.read_bytes(), ru.read_bytes())
            BUILD.configure_microphone_permission(app)
            self.assertEqual(first, (info_path.read_bytes(), ru.read_bytes()))

    def test_missing_translation_fails_before_modifying_app(self):
        with tempfile.TemporaryDirectory() as folder:
            term = Path(folder) / "terms.tsv"
            term.write_text(BUILD.MICROPHONE_TERM + "\tOnly English\t\n")
            with self.assertRaises(RuntimeError):
                BUILD.configure_microphone_permission(Path(folder) / "Missing.app", term)


if __name__ == "__main__":
    unittest.main()
