#!/usr/bin/env python3
"""Read-only macOS release readiness check; never builds, signs, uploads or deploys."""
import argparse
import concurrent.futures
import json
from pathlib import Path
import subprocess
import urllib.error
import urllib.request

import release


def check_credentials(path, reader=False):
    info = json.loads(Path(path).read_text())
    if reader:
        if info.get('type') != 'service_account' or not all(info.get(k) for k in ('client_email', 'private_key', 'token_uri')):
            raise ValueError('Expected Google service-account reader credentials')
    elif not all(info.get(k) for k in ('client_id', 'client_secret', 'refresh_token')):
        raise ValueError('Expected Google publisher credentials from release.py authorize')
    return 'Local credential file is present; Google access still needs a live sync/publish check'


def check_feed(url):
    try:
        with urllib.request.urlopen(url, timeout=15) as response:
            if len(response.read(200001)) > 200000: raise ValueError('Oversized metadata response')
            return {'check': url, 'status': 'ok', 'detail': f'HTTP {response.status} (availability only)'}
    except urllib.error.HTTPError as error:
        return {'check': url, 'status': 'blocked', 'detail': f'HTTP {error.code}; no usable published feed' }
    except (OSError, ValueError):
        return {'check': url, 'status': 'blocked', 'detail': 'HTTPS request failed; check DNS, certificate and service'}


def inspect(args):
    checks = []
    def record(name, action):
        try:
            detail = action()
            checks.append({'check': name, 'status': 'ok', 'detail': detail or 'Passed'})
        except FileNotFoundError:
            checks.append({'check': name, 'status': 'blocked', 'detail': 'Required file or tool is missing'})
        except subprocess.CalledProcessError as error:
            checks.append({'check': name, 'status': 'blocked', 'detail': f'{Path(error.cmd[0]).name} exited {error.returncode}'})
        except json.JSONDecodeError:
            checks.append({'check': name, 'status': 'blocked', 'detail': 'Invalid JSON configuration'})
        except ValueError as error:
            checks.append({'check': name, 'status': 'blocked', 'detail': str(error)})
        except (OSError, KeyError, ImportError) as error:
            # Never expose a JSON document or HTTP response containing credentials.
            checks.append({'check': name, 'status': 'blocked', 'detail': type(error).__name__})

    folder = args.release.resolve()
    for arch in ('arm64', 'x64'):
        record('Player and updater: ' + arch, lambda arch=arch: (release.inspect_player(folder, 'macos', arch), 'Package matches platform/version')[1])
    record('Developer ID, signature, notarization and Gatekeeper', lambda: release.verify_macos_distribution(folder / 'HexLive.app'))
    record('Google Drive publisher credentials', lambda: check_credentials(args.credentials))
    record('Google Drive reader credentials', lambda: check_credentials(args.reader_credentials, reader=True))

    def check_release_key():
        from cryptography.hazmat.primitives import serialization
        from cryptography.hazmat.primitives.asymmetric import rsa
        key = serialization.load_pem_private_key(args.signing_key.read_bytes(), password=None)
        public = serialization.load_pem_public_key((release.ROOT / 'Tools/updates/release-public.pem').read_bytes())
        if not isinstance(key, rsa.RSAPrivateKey) or key.key_size < 3072 or key.public_key().public_numbers() != public.public_numbers():
            raise ValueError('Release signing key does not match client trust')
        return 'RSA release key matches the public trust key'
    record('Release metadata signing key', check_release_key)
    if not args.offline:
        config = json.loads((release.ROOT / 'Tools/updates/trust.json').read_text())
        urls = [config['endpoint'] + '/macos/' + arch + '/stable/latest' for arch in ('arm64', 'x64')]
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
            checks.extend(pool.map(check_feed, urls))
    return checks


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--release', type=Path, default=(Path.home() / 'hex-girls/HexLive.app').resolve().parent)
    config = Path.home() / '.config/hexlive/releases'
    parser.add_argument('--credentials', type=Path, default=config / 'publisher-credentials.json')
    parser.add_argument('--reader-credentials', type=Path, default=config / 'reader-credentials.json', help='Local reader credential file, if configured on this builder')
    parser.add_argument('--signing-key', type=Path, default=config / 'signing-key.pem')
    parser.add_argument('--offline', action='store_true', help='Skip HTTPS feed checks')
    args = parser.parse_args()
    checks = inspect(args)
    ready = all(c['status'] == 'ok' for c in checks)
    print(json.dumps({'ready': ready, 'release': str(args.release.resolve()), 'checks': checks}, indent=2, ensure_ascii=False))
    return 0 if ready else 1


if __name__ == '__main__':
    raise SystemExit(main())
