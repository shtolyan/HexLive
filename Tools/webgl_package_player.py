#!/usr/bin/env python3
"""Package an existing WebGL Player; never builds Unity or publishes anything.

Published FSB reuse excludes only StreamingAssets/HexLive, never Studio banks.
Every Studio bank must match the committed build source, including LFS SHA.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import subprocess
import tarfile

from webgl_verify_audio_reuse import committed_digest

BANK_DIRECTORY = 'StreamingAssets/FMODBanks'
REQUIRED_BANKS = ('Master.bank', 'Master.strings.bank')


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(chunk)
    return h.hexdigest()


def verify_banks(build, checkout, source_commit):
    directory = build / BANK_DIRECTORY
    for name in REQUIRED_BANKS:
        if not (directory / name).is_file():
            raise ValueError('MissingStudioBank: ' + name)
    hashes = {}
    for bank in sorted(directory.rglob('*.bank')):
        if bank.is_symlink() or not bank.resolve().is_relative_to(build):
            raise ValueError('UnsafeStudioBank: ' + bank.name)
        with bank.open('rb') as stream:
            header = stream.read(12)
        if len(header) != 12 or header[:4] != b'RIFF' or header[8:] != b'FEV ' or bank.stat().st_size != int.from_bytes(header[4:8], 'little') + 8:
            raise ValueError('InvalidStudioBank: ' + bank.name)
        relative = bank.relative_to(build).as_posix()
        blob = subprocess.check_output(['git', '-C', str(checkout), 'show', source_commit + ':Assets/' + relative], stderr=subprocess.DEVNULL)
        actual = digest(bank)
        if actual != committed_digest(blob):
            raise ValueError('StudioBankSourceMismatch: ' + bank.name)
        hashes[relative] = actual
    return hashes


def inspect_player(build, checkout, source_commit, reuse_published_audio):
    build, checkout = build.resolve(), checkout.resolve()
    commit = subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', '--verify', '--end-of-options', source_commit + '^{commit}'], stderr=subprocess.DEVNULL).decode().strip()
    for name in ('index.html', 'server-directory.json'):
        if not (build / name).is_file():
            raise ValueError('MissingPlayerFile: ' + name)
    for suffix in ('.loader.js', '.data.br', '.framework.js.br', '.wasm.br'):
        if not any((build / 'Build').glob('*' + suffix)):
            raise ValueError('MissingPlayerBuildFile: ' + suffix)
    banks = verify_banks(build, checkout, commit)
    if reuse_published_audio:
        marker = json.loads((build / 'audio-reuse-required.json').read_text(encoding='utf-8-sig'))
        if not isinstance(marker, dict) or marker.get('requiresVerifiedPublishedFsb') is not True:
            raise ValueError('UnverifiedAudioReuseMode')
    files = {}
    for path in sorted(build.rglob('*')):
        relative = path.relative_to(build)
        if path.is_symlink():
            raise ValueError('SymlinkInPlayer: ' + relative.as_posix())
        if not path.is_file() or path.suffix == '.meta' or any('BurstDebugInformation_DoNotShip' in part for part in relative.parts):
            continue
        if reuse_published_audio and relative.parts[:2] == ('StreamingAssets', 'HexLive'):
            continue
        files[relative.as_posix()] = digest(path)
    if not banks.keys() <= files.keys():
        raise ValueError('StudioBanksExcludedFromPackage')
    return dict(passed=True, sourceCommit=commit, audioReuseRequired=reuse_published_audio,
                files=files, studioBankHashes=banks)


def verify_archive(archive, proof):
    with tarfile.open(archive) as source:
        members = source.getmembers()
        names = [m.name for m in members]
        if not set(proof['studioBankHashes']) <= set(names):
            raise ValueError('StudioBanksExcludedFromArchive')
        if len(names) != len(set(names)) or set(names) != set(proof['files']):
            raise ValueError('ArchiveFileSetMismatch')
        for member in members:
            name = PurePosixPath(member.name)
            if not member.isfile() or name.is_absolute() or '..' in name.parts or '\\' in member.name:
                raise ValueError('UnsafeArchiveMember')
            h = hashlib.sha256()
            with source.extractfile(member) as stream:
                for chunk in iter(lambda: stream.read(1024 * 1024), b''):
                    h.update(chunk)
            if h.hexdigest() != proof['files'][member.name]:
                raise ValueError('ArchiveHashMismatch: ' + member.name)


def package(build, output, proof):
    # Exclusive creation preserves previously verified packages.
    with output.open('xb') as target, tarfile.open(fileobj=target, mode='w:gz') as archive:
        for relative in proof['files']:
            path = build / relative
            info = archive.gettarinfo(str(path), relative)
            info.mode = 0o644
            with path.open('rb') as stream:
                archive.addfile(info, stream)
    verify_archive(output, proof)
    return dict(proof, sha256=digest(output))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('build', type=Path)
    parser.add_argument('--checkout', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--source-commit', required=True)
    parser.add_argument('--reuse-published-audio', action='store_true')
    parser.add_argument('--protocol', type=int, required=True)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--verify-only', action='store_true')
    mode.add_argument('--check-archive', type=Path)
    mode.add_argument('--output', type=Path)
    parser.add_argument('--receipt', type=Path)
    args = parser.parse_args()
    if args.protocol < 1:
        parser.error('--protocol must be positive')
    try:
        proof = inspect_player(args.build, args.checkout, args.source_commit, args.reuse_published_audio)
        proof['protocol'] = args.protocol
        if args.check_archive:
            verify_archive(args.check_archive, proof)
        if args.output:
            proof = package(args.build, args.output, proof)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        proof = dict(passed=False, error=str(error))
    if args.receipt:
        args.receipt.write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(proof, indent=2))
    return 0 if proof['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
