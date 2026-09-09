#!/usr/bin/env python3
"""Package an already compiled macOS Agent Studio; does not compile or launch agents."""
import argparse
from datetime import datetime, timezone
import fcntl
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import struct
import subprocess
import sys
import tempfile

DEFAULT_RELEASES = Path.home() / 'hex-girls' / 'AgentStudioReleases'


def next_version(releases):
    versions = [(0, 1, 0)]
    if releases.exists():
        for path in releases.iterdir():
            match = re.fullmatch(r'v(\d+)\.(\d+)\.(\d+)', path.name)
            if match:
                versions.append(tuple(map(int, match.groups())))
    major, minor, patch = max(versions)
    return f'{major}.{minor}.{patch + 1}'


def check_latest(releases):
    latest = releases.parent / 'Agent Studio.app'
    if latest.exists() and not latest.is_symlink():
        raise RuntimeError(f'Refusing to overwrite a real application: {latest}')
    return latest


def publish_latest(releases, bundle):
    latest = check_latest(releases)
    temporary = latest.parent / f'.Agent Studio.app.tmp-{os.getpid()}'
    # Never remove an unknown pre-existing path to make publication succeed.
    temporary.symlink_to(os.path.relpath(bundle, latest.parent), target_is_directory=True)
    try:
        os.replace(temporary, latest)
    finally:
        if temporary.is_symlink():
            temporary.unlink()
    return latest


def package(repo, binary_dir, releases, version):
    assets = repo / 'Server/HexLive.AgentStudio/Assets'
    icons(assets)
    destination = releases / f'v{version}'
    if destination.exists():
        raise RuntimeError(f'Refusing to overwrite version: {destination}')
    staging = Path(tempfile.mkdtemp(prefix=f'.staging-v{version}-', dir=releases))
    bundle = staging / 'Agent Studio.app'
    contents = bundle / 'Contents'
    contents.mkdir(parents=True)
    shutil.copytree(binary_dir, contents / 'MacOS')
    resources = contents / 'Resources'; resources.mkdir()
    shutil.copy2(assets / 'agent-studio.icns', resources / 'AgentStudio.icns')
    with (contents / 'Info.plist').open('wb') as output:
        plistlib.dump({'CFBundleName': 'Agent Studio', 'CFBundleDisplayName': 'Agent Studio',
                      'CFBundleIdentifier': 'com.hexlive.agentstudio', 'CFBundleExecutable': 'HexLive.AgentStudio',
                      'CFBundlePackageType': 'APPL', 'CFBundleIconFile': 'AgentStudio.icns',
                      'CFBundleShortVersionString': version, 'CFBundleVersion': version,
                      'NSHighResolutionCapable': True, 'LSMinimumSystemVersion': '12.0'}, output)
    run(sys.executable, repo / 'Tools/macos_signing.py', bundle)
    run('codesign', '--verify', '--deep', '--strict', bundle)
    report = {'version': version, 'packagedAtUtc': datetime.now(timezone.utc).isoformat(),
        'kind': 'framework-dependent-preview', 'requiresInstalledDotnet': '9',
        'binarySource': str(binary_dir), 'compiledByThisTool': False,
        'coreSha256': hashlib.sha256((contents / 'MacOS/HexLive.AgentCore.dll').read_bytes()).hexdigest(),
        'note': 'Existing successful binary packaged with icon; not a completed Agent Studio release.'}
    (staging / 'package-report.json').write_text(json.dumps(report, indent=2) + '\n')
    (staging / 'BUILD_REPORT.md').write_text(
        f'# Agent Studio {version}\n\n'
        f'- Packaged: {report["packagedAtUtc"]}\n'
        f'- Requires installed .NET 9; framework-dependent preview.\n'
        f'- Binary source: `{binary_dir}`\n'
        f'- Core SHA-256: `{report["coreSha256"]}`\n'
        '- Signature verified before publication. This command does not compile or launch agents.\n')
    staging.rename(destination)
    artifact = destination / 'Agent Studio.app'
    latest = publish_latest(releases, artifact)
    print(f'Agent Studio {version}: {artifact}')
    print(f'Latest: {latest}')
    return artifact


def run(*args):
    subprocess.run([str(x) for x in args], check=True, stdout=subprocess.DEVNULL)


def icons(assets):
    with tempfile.TemporaryDirectory(prefix='agent-studio-icons-') as temporary:
        root = Path(temporary)
        iconset = root / 'AgentStudio.iconset'; iconset.mkdir()
        source = assets / 'agent-studio.png'
        pngs = {}
        for size in (16, 32, 48, 64, 128, 256, 512, 1024):
            target = root / f'{size}.png'
            run('sips', '-z', size, size, source, '--out', target)
            pngs[size] = target
        for size in (16, 32, 128, 256, 512):
            shutil.copy2(pngs[size], iconset / f'icon_{size}x{size}.png')
            shutil.copy2(pngs[size * 2], iconset / f'icon_{size}x{size}@2x.png')
        run('iconutil', '-c', 'icns', iconset, '-o', assets / 'agent-studio.icns')
        sizes = (16, 32, 48, 256)
        payloads = [pngs[size].read_bytes() for size in sizes]
        header = struct.pack('<HHH', 0, 1, len(sizes))
        offset = 6 + 16 * len(sizes)
        entries = bytearray()
        for size, payload in zip(sizes, payloads):
            entries.extend(struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(payload), offset))
            offset += len(payload)
        (assets / 'agent-studio.ico').write_bytes(header + entries + b''.join(payloads))


def main():
    repo = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--icons-only', action='store_true')
    parser.add_argument('--binary-dir', type=Path, default=repo / 'Build/dotnet/bin/HexLive.AgentStudio/Release/net9.0')
    parser.add_argument('--output-root', type=Path, default=DEFAULT_RELEASES,
                        help='Versioned releases directory; latest link is created in its parent, beside HexLive.app')
    parser.add_argument('--dry-run', action='store_true', help='Show next version and paths without writing or packaging')
    args = parser.parse_args()
    assets = repo / 'Server/HexLive.AgentStudio/Assets'
    if args.icons_only:
        if not args.dry_run:
            icons(assets)
        return
    if not (args.binary_dir / 'HexLive.AgentStudio').is_file():
        raise SystemExit('Build Agent Studio first; this tool never compiles automatically.')
    releases = args.output_root.expanduser().absolute()
    latest = check_latest(releases)
    if args.dry_run:
        print(releases / f'v{next_version(releases)}' / 'Agent Studio.app')
        print(f'Latest: {latest}')
        return
    releases.mkdir(parents=True, exist_ok=True)
    with (releases / '.package.lock').open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise SystemExit('Another Agent Studio publisher is active; retry after it finishes.')
        package(repo, args.binary_dir.resolve(), releases, next_version(releases))


if __name__ == '__main__':
    main()
