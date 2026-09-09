import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('studio_package', Path(__file__).parents[1] / 'package_agent_studio.py')
studio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(studio)


class StudioPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='studio-package-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.releases = self.root / 'distribution/AgentStudioReleases'
        self.releases.mkdir(parents=True)
        self.assets = self.root / 'Server/HexLive.AgentStudio/Assets'
        self.assets.mkdir(parents=True)
        (self.assets / 'agent-studio.icns').write_bytes(b'icon')
        self.binary = self.root / 'binary'
        self.binary.mkdir()
        (self.binary / 'HexLive.AgentCore.dll').write_bytes(b'test-core')

    def test_versions_are_numeric_and_independent_of_game(self):
        self.assertEqual(studio.next_version(self.releases), '0.1.1')
        for name in ['v0.1.9', 'v0.1.10', '.staging-v0.1.11-incomplete']:
            (self.releases / name).mkdir()
        self.assertEqual(studio.next_version(self.releases), '0.1.11')

    def test_relative_latest_is_replaced_but_previous_version_is_kept(self):
        first = self.releases / 'v0.1.1/Agent Studio.app'
        second = self.releases / 'v0.1.2/Agent Studio.app'
        first.mkdir(parents=True); second.mkdir(parents=True)
        latest = studio.publish_latest(self.releases, first)
        self.assertEqual(latest.readlink(), Path('AgentStudioReleases/v0.1.1/Agent Studio.app'))
        studio.publish_latest(self.releases, second)
        self.assertEqual(latest.resolve(), second)
        self.assertTrue(first.is_dir())

    def test_real_application_is_never_overwritten(self):
        latest = self.releases.parent / 'Agent Studio.app'
        latest.mkdir()
        with self.assertRaises(RuntimeError):
            studio.check_latest(self.releases)
        self.assertTrue(latest.is_dir())

    def test_failed_signature_does_not_publish_or_replace_latest(self):
        previous = self.releases / 'v0.1.1/Agent Studio.app'
        previous.mkdir(parents=True)
        latest = studio.publish_latest(self.releases, previous)
        with patch.object(studio, 'icons'), patch.object(studio, 'run', side_effect=[None, None, RuntimeError('bad signature')]):
            with self.assertRaises(RuntimeError):
                studio.package(self.root, self.binary, self.releases, '0.1.2')
        self.assertEqual(latest.resolve(), previous)
        self.assertFalse((self.releases / 'v0.1.2').exists())

    def test_missing_identity_fails_before_assets_or_staging_are_created(self):
        with patch.object(studio, 'icons') as icons, patch.object(studio, 'run', side_effect=RuntimeError('no identity')) as run:
            with self.assertRaises(RuntimeError):
                studio.package(self.root, self.binary, self.releases, '0.1.1')
        icons.assert_not_called()
        self.assertIn('--check-identity', run.call_args.args)
        self.assertEqual(list(self.releases.iterdir()), [])

    def test_success_publishes_version_manifest_and_matching_plist(self):
        with patch.object(studio, 'icons'), patch.object(studio, 'run'):
            bundle = studio.package(self.root, self.binary, self.releases, '0.1.1')
        with (bundle / 'Contents/Info.plist').open('rb') as source:
            info = studio.plistlib.load(source)
        self.assertEqual(info['CFBundleShortVersionString'], '0.1.1')
        self.assertEqual((self.releases.parent / 'Agent Studio.app').resolve(), bundle)
        self.assertTrue((bundle.parent / 'package-report.json').is_file())
        self.assertTrue((bundle.parent / 'BUILD_REPORT.md').is_file())


if __name__ == '__main__':
    unittest.main()
