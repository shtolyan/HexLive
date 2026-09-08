#!/usr/bin/env python3
"""Package an already compiled macOS Agent Studio; does not compile or launch agents."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import tempfile


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
    args = parser.parse_args()
    assets = repo / 'Server/HexLive.AgentStudio/Assets'
    icons(assets)
    if args.icons_only:
        return
    if not (args.binary_dir / 'HexLive.AgentStudio').is_file():
        raise SystemExit('Build Agent Studio first; this tool never compiles automatically.')
    destination = repo / 'Build/AgentStudio' / datetime.now(timezone.utc).strftime('preview-%Y%m%d-%H%M%S')
    bundle = destination / 'Agent Studio.app'
    contents = bundle / 'Contents'
    contents.mkdir(parents=True)
    shutil.copytree(args.binary_dir, contents / 'MacOS')
    resources = contents / 'Resources'; resources.mkdir()
    shutil.copy2(assets / 'agent-studio.icns', resources / 'AgentStudio.icns')
    with (contents / 'Info.plist').open('wb') as output:
        plistlib.dump({'CFBundleName': 'Agent Studio', 'CFBundleDisplayName': 'Agent Studio',
                      'CFBundleIdentifier': 'com.hexlive.agentstudio', 'CFBundleExecutable': 'HexLive.AgentStudio',
                      'CFBundlePackageType': 'APPL', 'CFBundleIconFile': 'AgentStudio.icns',
                      'CFBundleShortVersionString': '0.1.0', 'CFBundleVersion': '1',
                      'NSHighResolutionCapable': True, 'LSMinimumSystemVersion': '12.0'}, output)
    run('codesign', '--force', '--deep', '--sign', '-', bundle)
    run('codesign', '--verify', '--deep', '--strict', bundle)
    (destination / 'package-report.json').write_text(json.dumps({
        'kind': 'framework-dependent-preview', 'requiresInstalledDotnet': '9',
        'binarySource': str(args.binary_dir), 'compiledByThisTool': False,
        'note': 'Existing successful binary packaged with icon; not a completed Agent Studio release.'
    }, indent=2))
    print(bundle)


if __name__ == '__main__':
    main()
