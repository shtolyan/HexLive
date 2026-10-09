import importlib.util
import json
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('mac_release', Path(__file__).resolve().parents[1] / 'release.py')
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)


class MacReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / 'HexLive.app'
        self.package = self.app / 'Contents/Resources/Data/StreamingAssets/HexLiveUpdate'
        self.package.mkdir(parents=True)
        (self.root / 'build-manifest.json').write_text(json.dumps({'version': '1.2.3', 'variant': 'release'}))
        (self.app / 'Contents/Info.plist').write_bytes(plistlib.dumps({
            'CFBundleIdentifier': 'com.juilcylove.hexgirls', 'CFBundleShortVersionString': '1.2.3'}))
        player = self.app / 'Contents/MacOS/HexLive'
        player.parent.mkdir()
        player.write_bytes(b'player')
        self.config = {'platform': 'macos', 'architecture': 'universal',
                       'endpoint': 'https://updates.example.test/api/releases/v2/client/macos/universal/stable'}
        self.write_config()
        for arch in ('arm64', 'x64'):
            helper = self.package / ('HexLive.Updater-' + arch)
            helper.write_bytes(b'helper')
            helper.chmod(0o755)

    def write_config(self):
        (self.package / 'update-config.json').write_text(json.dumps(self.config))

    @staticmethod
    def slices(command, **kwargs):
        name = Path(command[-1]).name
        return {'HexLive': 'arm64 x86_64', 'HexLive.Updater-arm64': 'arm64', 'HexLive.Updater-x64': 'x86_64'}[name]

    def test_universal_package_is_accepted_for_both_feeds(self):
        with patch.object(r.subprocess, 'check_output', side_effect=self.slices):
            for arch in ('arm64', 'x64'):
                report, app, config = r.inspect_player(self.root, 'macos', arch)
                self.assertEqual(report['version'], '1.2.3')
                self.assertEqual(app, self.app.resolve())
                self.assertEqual(config['architecture'], 'universal')

    def test_missing_intel_helper_blocks_even_arm_feed(self):
        (self.package / 'HexLive.Updater-x64').unlink()
        with patch.object(r.subprocess, 'check_output', side_effect=self.slices):
            with self.assertRaisesRegex(ValueError, 'Updater missing'):
                r.inspect_player(self.root, 'macos', 'arm64')

    def test_wrong_helper_binary_blocks_publication(self):
        with patch.object(r.subprocess, 'check_output', return_value='x86_64'):
            with self.assertRaisesRegex(ValueError, 'Wrong updater architecture'):
                r.inspect_player(self.root, 'macos', 'arm64')

    def test_manifest_must_match_actual_app_version(self):
        (self.app / 'Contents/Info.plist').write_bytes(plistlib.dumps({
            'CFBundleIdentifier': 'com.juilcylove.hexgirls', 'CFBundleShortVersionString': '1.2.2'}))
        with patch.object(r.subprocess, 'check_output', side_effect=self.slices):
            with self.assertRaisesRegex(ValueError, 'version differs'):
                r.inspect_player(self.root, 'macos', 'arm64')

    def test_http_or_wrong_channel_endpoint_is_rejected(self):
        for endpoint in ('http://updates.example.test/macos/universal/stable',
                         'https://updates.example.test/windows/x64/stable'):
            self.config['endpoint'] = endpoint
            self.write_config()
            with self.assertRaisesRegex(ValueError, 'HTTPS update endpoint'):
                r.inspect_player(self.root, 'macos', 'arm64')

    def test_development_certificate_is_not_published(self):
        development = subprocess.CompletedProcess([], 0, '', 'Authority=Apple Development: Test')
        with patch.object(r.subprocess, 'run', return_value=development) as run:
            with self.assertRaisesRegex(ValueError, 'Developer ID'):
                r.verify_macos_distribution(self.app)
        self.assertEqual(run.call_count, 2)

    def test_missing_notarization_is_not_bypassed(self):
        signed = subprocess.CompletedProcess([], 0, '', 'Authority=Developer ID Application: Test')
        with patch.object(r.subprocess, 'run', side_effect=[signed, signed, subprocess.CalledProcessError(65, ['xcrun'])]) as run:
            with self.assertRaises(subprocess.CalledProcessError):
                r.verify_macos_distribution(self.app)
        self.assertEqual(run.call_args.args[0][1:3], ['stapler', 'validate'])


if __name__ == '__main__':
    unittest.main()
