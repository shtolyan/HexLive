#!/usr/bin/env python3
"""Seal local macOS apps with a persistent identity when configured.

The configuration contains only a public certificate fingerprint, never a key.
An unavailable configured identity is a build failure, not an ad-hoc downgrade.
"""
import argparse
import os
from pathlib import Path
import plistlib
import re
import subprocess

IDENTITY_FILE = Path.home() / '.config/hexlive/macos-signing-identity'


def identity():
    value = os.environ.get('HEXLIVE_MACOS_SIGN_IDENTITY', '').strip()
    if not value and IDENTITY_FILE.exists():
        value = IDENTITY_FILE.read_text().strip()
    if value and not re.fullmatch(r'[0-9A-Fa-f]{40}', value):
        raise ValueError('macOS signing identity must be a SHA-1 certificate fingerprint')
    return value or None


def bundle_identifier(app):
    with (app / 'Contents/Info.plist').open('rb') as source:
        value = plistlib.load(source).get('CFBundleIdentifier', '')
    if not isinstance(value, str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9.-]+', value):
        raise ValueError('App must have a stable CFBundleIdentifier')
    return value


def seal(app, configured_only=False):
    signer = identity()
    if configured_only and not signer:
        return
    identifier = bundle_identifier(app)
    subprocess.run(['/usr/bin/codesign', '--force', '--deep', '--sign', signer or '-',
                    '--timestamp=none', '--preserve-metadata=identifier,entitlements,flags', str(app)], check=True)
    # .NET apphost's inherited identifier contains a build-specific UUID. The
    # outer process must use the app's stable bundle ID, not that transient ID.
    subprocess.run(['/usr/bin/codesign', '--force', '--sign', signer or '-', '--identifier', identifier,
                    '--timestamp=none', '--preserve-metadata=entitlements,flags', str(app)], check=True)
    subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('app', type=Path)
    parser.add_argument('--configured-only', action='store_true')
    args = parser.parse_args()
    seal(args.app, args.configured_only)
