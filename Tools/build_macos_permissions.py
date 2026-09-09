#!/usr/bin/env python3
"""Build the universal macOS privacy bridge; no Unity or recording involved."""
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]


def build():
    output = ROOT / 'Assets/Plugins/macOS/HexLivePermissions.bundle'
    output.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(['/usr/bin/xcrun', 'clang', '-bundle', '-fobjc-arc', '-fblocks',
                    '-arch', 'arm64', '-arch', 'x86_64', '-mmacosx-version-min=11.0',
                    '-framework', 'AVFoundation', '-framework', 'Foundation',
                    str(ROOT / 'Tools/macos/HexLivePermissions.m'), '-o', str(output)], check=True)
    subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', str(output)], check=True)


if __name__ == '__main__':
    build()
