#!/usr/bin/env python3
"""Seal local macOS apps with a mandatory persistent signing identity.

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


def require_identity():
    signer = identity()
    if not signer:
        raise RuntimeError('Persistent macOS signing identity is required. Configure '
                           'HEXLIVE_MACOS_SIGN_IDENTITY or ~/.config/hexlive/macos-signing-identity. '
                           'See Tools/MACOS_SIGNING.md; ad-hoc publication is disabled.')
    available = subprocess.run(['/usr/bin/security', 'find-identity', '-v', '-p', 'codesigning'],
                               capture_output=True, text=True, check=True)
    fingerprints = re.findall(r'^\s*\d+\)\s+([0-9A-Fa-f]{40})\s+"', available.stdout, re.MULTILINE)
    if signer.upper() not in [value.upper() for value in fingerprints]:
        raise RuntimeError('Configured macOS signing certificate/private key is unavailable or invalid. '
                           'Check the login Keychain and Tools/MACOS_SIGNING.md; '
                           'do not replace the identity or publish ad-hoc to bypass this failure.')
    return signer


def seal(app, preserve_distribution=False, expected_identifier=None):
    identifier = bundle_identifier(app)
    if expected_identifier and identifier != expected_identifier:
        raise ValueError(f'Bundle ID changed: expected {expected_identifier}, got {identifier}')
    if preserve_distribution:
        existing = subprocess.run(['/usr/bin/codesign', '-d', '-vv', str(app)],
                                  capture_output=True, text=True, check=False)
        if any(authority in existing.stderr for authority in (
                'Authority=Developer ID Application:', 'Authority=Apple Distribution:',
                'Authority=3rd Party Mac Developer Application:')):
            subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)], check=True)
            return
    signer = require_identity()
    # Recompute local development metadata so Info.plist is bound into the
    # new signature, rather than retaining an old ad-hoc apphost's metadata.
    subprocess.run(['/usr/bin/codesign', '--force', '--deep', '--sign', signer,
                    '--timestamp=none', str(app)], check=True)
    # .NET apphost's inherited identifier contains a build-specific UUID. The
    # outer process must use the app's stable bundle ID, not that transient ID.
    subprocess.run(['/usr/bin/codesign', '--force', '--sign', signer, '--identifier', identifier,
                    '--timestamp=none', str(app)], check=True)
    subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('app', type=Path, nargs='?')
    parser.add_argument('--check-identity', action='store_true', help='Validate signing setup without modifying apps')
    parser.add_argument('--bundle-id', help='Refuse an unexpected CFBundleIdentifier')
    parser.add_argument('--preserve-distribution', action='store_true')
    args = parser.parse_args()
    if args.check_identity:
        print('Persistent macOS signing identity available: ' + require_identity())
    elif args.app is None:
        parser.error('app is required unless --check-identity is used')
    else:
        seal(args.app, args.preserve_distribution, args.bundle_id)
