import contextlib
import hashlib
import io
import json
from pathlib import Path
import struct
import sys
import tarfile
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import webgl_package_player as player


class PlayerPackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='webgl-player-package-test-',
            dir=Path(__file__).resolve().parents[2] / 'Build')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.build = self.root / 'player'
        self.commit = 'a' * 40
        self.banks = {}
        for name in player.REQUIRED_BANKS:
            data = b'RIFF' + struct.pack('<I', 12) + b'FEV fixture!'
            self.write(player.BANK_DIRECTORY + '/' + name, data)
            self.banks['Assets/' + player.BANK_DIRECTORY + '/' + name] = data
        for name in ('index.html', 'server-directory.json', 'Build/player.loader.js',
                     'Build/player.data.br', 'Build/player.framework.js.br', 'Build/player.wasm.br',
                     'StreamingAssets/another-runtime-config.json', 'TemplateData/style.css'):
            self.write(name, b'fixture')
        self.write('audio-reuse-required.json', json.dumps({'requiresVerifiedPublishedFsb': True}).encode())
        self.write('StreamingAssets/HexLive/Sfx/test.wav', b'raw source must not enter reuse package')
        self.write('StreamingAssets/HexLive/Sfx/test.fsb', b'previously published audio handled by stage')
        self.write('player_BurstDebugInformation_DoNotShip/data.txt', b'debug')
        self.write('StreamingAssets/FMODBanks/Master.bank.meta', b'meta')
        mocked = patch.object(player.subprocess, 'check_output', side_effect=self.git)
        mocked.start(); self.addCleanup(mocked.stop)

    def write(self, name, data):
        path = self.build / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)

    def git(self, args, **kwargs):
        if 'rev-parse' in args: return (self.commit + '\n').encode()
        return self.banks[args[-1].split(':', 1)[1]]

    def inspect(self):
        return player.inspect_player(self.build, self.root, self.commit, True)

    def test_reuse_archive_keeps_both_studio_banks_and_other_runtime_files(self):
        proof = self.inspect()
        output = self.root / 'player.tar.gz'
        packaged = player.package(self.build, output, proof)
        self.assertEqual(packaged['sha256'], player.digest(output))
        with tarfile.open(output) as archive:
            names = archive.getnames()
        self.assertTrue(set(proof['studioBankHashes']) <= set(names))
        self.assertIn('StreamingAssets/another-runtime-config.json', names)
        self.assertFalse(any('HexLive/Sfx' in n or 'DoNotShip' in n or n.endswith('.meta') for n in names))

    def test_missing_either_bank_fails_closed(self):
        for name in player.REQUIRED_BANKS:
            with self.subTest(name=name):
                path = self.build / player.BANK_DIRECTORY / name
                data = path.read_bytes(); path.unlink()
                with self.assertRaisesRegex(ValueError, 'MissingStudioBank'): self.inspect()
                path.write_bytes(data)

    def test_empty_html_lfs_pointer_and_truncated_bank_are_rejected(self):
        for data in (b'', b'<html>404</html>', b'version https://git-lfs.github.com/spec/v1\n', b'RIFF\xff\xff\xff\xffFEV '):
            with self.subTest(data=data):
                self.write(player.BANK_DIRECTORY + '/Master.bank', data)
                with self.assertRaisesRegex(ValueError, 'InvalidStudioBank'): self.inspect()

    def test_different_bank_source_commit_is_rejected(self):
        self.banks['Assets/' + player.BANK_DIRECTORY + '/Master.bank'] += b'changed'
        with self.assertRaisesRegex(ValueError, 'StudioBankSourceMismatch'): self.inspect()

    def test_hydrated_lfs_bank_matches_committed_oid(self):
        for name, data in list(self.banks.items()):
            self.banks[name] = b'version https://git-lfs.github.com/spec/v1\n' + b'oid sha256:' + hashlib.sha256(data).hexdigest().encode() + b'\nsize ' + str(len(data)).encode() + b'\n'
        self.assertEqual(len(self.inspect()['studioBankHashes']), 2)

    def test_historical_omit_all_streaming_assets_archive_is_rejected(self):
        proof = self.inspect(); output = self.root / 'bad.tar.gz'
        with tarfile.open(output, 'w:gz') as archive:
            for name in proof['files']:
                if not name.startswith('StreamingAssets/'):
                    archive.add(self.build / name, arcname=name)
        with self.assertRaisesRegex(ValueError, 'StudioBanksExcludedFromArchive'):
            player.verify_archive(output, proof)

    def test_existing_package_is_never_overwritten(self):
        output = self.root / 'kept.tar.gz'; output.write_bytes(b'keep')
        with self.assertRaises(FileExistsError): player.package(self.build, output, self.inspect())
        self.assertEqual(output.read_bytes(), b'keep')

    def test_full_audio_mode_keeps_audio_tree(self):
        proof = player.inspect_player(self.build, self.root, self.commit, False)
        self.assertIn('StreamingAssets/HexLive/Sfx/test.fsb', proof['files'])

    def test_failed_check_replaces_a_stale_pass_receipt(self):
        receipt = self.root / 'receipt.json'; receipt.write_text('{"passed":true}')
        (self.build / player.BANK_DIRECTORY / 'Master.bank').unlink()
        argv = ['package', str(self.build), '--source-commit', self.commit,
                '--protocol', '25', '--verify-only', '--receipt', str(receipt)]
        with patch.object(sys, 'argv', argv), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(player.main(), 1)
        self.assertFalse(json.loads(receipt.read_text())['passed'])


if __name__ == '__main__':
    unittest.main()
