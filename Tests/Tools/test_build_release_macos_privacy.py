import importlib.util
from pathlib import Path
import plistlib
import tempfile
import subprocess
from types import SimpleNamespace
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("mac_build", ROOT / "Tools" / "build_release.py")
BUILD = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(BUILD)
HUT_SPEC = importlib.util.spec_from_file_location('hut_build', ROOT / 'Tools/build_hut_test.py')
HUT = importlib.util.module_from_spec(HUT_SPEC)
with patch.dict('sys.modules', {'build_release': BUILD}):
    HUT_SPEC.loader.exec_module(HUT)


class MicrophonePackagingTests(unittest.TestCase):
    def test_test_client_also_requires_identity_before_unity(self):
        with patch.object(BUILD, 'find_unity') as unity, \
                patch.object(HUT.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'preflight')) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                HUT.main()
        unity.assert_not_called()
        self.assertIn('--check-identity', run.call_args.args[0])

    def test_optimized_local_player_uses_configured_identity_and_preserves_distribution(self):
        with patch.object(BUILD, 'remove_macos_metadata', return_value=0), \
                patch.object(BUILD, 'verify_code_signature', return_value=(True, 'valid')), \
                patch.object(BUILD.subprocess, 'run') as run:
            BUILD.ensure_development_signature(Path('/test.app'), release=True)
            command = run.call_args.args[0]
            self.assertIn('--bundle-id', command)
            self.assertIn('com.juilcylove.hexgirls', command)
            self.assertIn('--preserve-distribution', command)

    def test_invalid_signature_is_never_downgraded_in_either_build_variant(self):
        for release in (False, True):
            with self.subTest(release=release), patch.object(BUILD, 'remove_macos_metadata', return_value=0), \
                patch.object(BUILD, 'verify_code_signature', return_value=(False, 'invalid')), \
                patch.object(BUILD.subprocess, 'run') as run:
                with self.assertRaises(RuntimeError):
                    BUILD.ensure_development_signature(Path('/test.app'), release=release)
                self.assertEqual(run.call_count, 1)

    def test_missing_identity_stops_before_unity_is_selected_or_started(self):
        args = SimpleNamespace(output_root=Path('/unused'), release=True, dry_run=False)
        with patch.object(BUILD, 'parse_args', return_value=args), \
                patch.dict(BUILD.os.environ, {'HEXLIVE_BUG_TOKEN': 'test-only'}), \
                patch.object(BUILD, 'planned_version', return_value=('0.1.1', 'test')), \
                patch.object(BUILD, 'load_last_success'), patch.object(BUILD, 'collect_git'), \
                patch.object(BUILD, 'read_bug_tracker'), patch.object(BUILD, 'print_plan'), \
                patch.object(BUILD, 'find_unity') as unity, \
                patch.object(BUILD.subprocess, 'run', side_effect=subprocess.CalledProcessError(1, 'preflight')) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                BUILD.main()
        unity.assert_not_called()
        self.assertEqual(run.call_count, 1)
        self.assertIn('--check-identity', run.call_args.args[0])

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
