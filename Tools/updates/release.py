#!/usr/bin/env python3
"""§166: explicit Google Drive publication and verified cache synchronization."""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import plistlib
import re
import shutil
import subprocess
import tempfile
import time
import zipfile
from datetime import datetime, timezone
from urllib.parse import urlsplit

API = 'https://www.googleapis.com/drive/v3/files'
UPLOAD = 'https://www.googleapis.com/upload/drive/v3/files'
ROOT = Path(__file__).resolve().parents[2]
CLIENT_FOLDER = '1jRbkDUlaHsyInES8in-Wxf8zKujUFwaa'


def atomic(path, data):
    path = Path(path); path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name(path.name + '.tmp')
    with tmp.open('wb') as out:
        out.write(data); out.flush(); os.fsync(out.fileno())
    tmp.replace(path)


def digest(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as src:
        for chunk in iter(lambda: src.read(1024 * 1024), b''): h.update(chunk)
    return h.hexdigest()


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r'\d+\.\d+\.\d+', value): raise ValueError('Invalid version')
    return tuple(map(int, value.split('.')))


def validate(m):
    if (m['schemaVersion'] != 1 or m['product'] != 'client' or m['channel'] != 'stable'
        or (m['platform'], m['architecture']) not in [('windows', 'x64'), ('macos', 'x64'), ('macos', 'arm64')]
        or not re.fullmatch('[a-f0-9]{64}', m['sha256']) or m['size'] <= 0 or m['unpackedSize'] <= 0
        or not re.fullmatch('[A-Za-z0-9_-]+', m['driveFileId']) or version(m['minimumVersion']) > version(m['version'])):
        raise ValueError('Invalid manifest')
    datetime.fromisoformat(m['publishedUtc'])


def verify(envelope, public_key):
    from cryptography.hazmat.primitives import serialization, hashes
    from cryptography.hazmat.primitives.asymmetric import padding, rsa
    key = serialization.load_pem_public_key(Path(public_key).read_bytes())
    if not isinstance(key, rsa.RSAPublicKey) or key.key_size < 3072: raise ValueError('RSA-3072+ required')
    payload = base64.b64decode(envelope['payload'], validate=True)
    if len(payload) > 98304: raise ValueError('Oversized manifest')
    key.verify(base64.b64decode(envelope['signature'], validate=True), payload, padding.PKCS1v15(), hashes.SHA256())
    manifest = json.loads(payload); validate(manifest)
    return manifest


def session(credentials_path):
    from google.auth.transport.requests import AuthorizedSession
    from google.oauth2 import service_account
    from google.oauth2.credentials import Credentials
    info = json.loads(Path(credentials_path).read_text())
    if info.get('type') == 'service_account':
        credentials = service_account.Credentials.from_service_account_info(info, scopes=['https://www.googleapis.com/auth/drive.readonly'])
    else: credentials = Credentials.from_authorized_user_info(info)
    return AuthorizedSession(credentials)


def files(http, folder, name):
    if not re.fullmatch('[A-Za-z0-9_-]+', folder): raise ValueError('Invalid folder ID')
    q = f"'{folder}' in parents and name = '{name}' and trashed = false"
    response = http.get(API, params={'q': q, 'fields': 'files(id,name,size,sha256Checksum),nextPageToken'}, timeout=30)
    response.raise_for_status(); result = response.json()
    if result.get('nextPageToken'): raise ValueError('Too many matching files')
    return result['files']


def upload(http, path, folder, name, existing=None):
    # Resumable media upload streams from disk; metadata and archive never enter command arguments.
    method = http.patch if existing else http.post
    response = method(UPLOAD + ('/' + existing if existing else ''), params={'uploadType': 'resumable', 'fields': 'id,size,sha256Checksum'},
        json={'name': name, **({} if existing else {'parents': [folder]})},
        headers={'X-Upload-Content-Length': str(Path(path).stat().st_size)}, timeout=30)
    response.raise_for_status()
    with Path(path).open('rb') as body:
        result = http.put(response.headers['Location'], data=body, timeout=(30, 1800))
    result.raise_for_status(); metadata = result.json()
    if metadata.get('sha256Checksum') != digest(path) or int(metadata['size']) != Path(path).stat().st_size:
        raise ValueError('Drive readback checksum mismatch')
    return metadata['id']


def inspect_player(release, platform, architecture):
    """Check the actual package before opening credentials or uploading anything."""
    release = Path(release).resolve()
    report = json.loads((release / 'build-manifest.json').read_text())
    if report['variant'] != 'release': raise ValueError('Public stable requires a release build')
    version(report['version'])
    if (platform, architecture) not in [('macos', 'arm64'), ('macos', 'x64'), ('windows', 'x64')]:
        raise ValueError('Unsupported release platform/architecture')
    app = release / ('HexLive.app' if platform == 'macos' else 'HexLive')
    if not app.is_dir(): raise ValueError('Missing completed player')
    package = app / ('Contents/Resources/Data/StreamingAssets/HexLiveUpdate' if platform == 'macos' else 'HexLive_Data/StreamingAssets/HexLiveUpdate')
    config = json.loads((package / 'update-config.json').read_text())
    if config['platform'] != platform or config['architecture'] not in (architecture, 'universal' if platform == 'macos' else architecture):
        raise ValueError('Build platform mismatch')
    endpoint = urlsplit(config['endpoint'])
    if (endpoint.scheme != 'https' or not endpoint.hostname or endpoint.username or endpoint.password or
        endpoint.query or endpoint.fragment or not endpoint.path.endswith(f'/{platform}/{config["architecture"]}/stable')):
        raise ValueError('Invalid HTTPS update endpoint')
    architectures = ['arm64', 'x64'] if config['architecture'] == 'universal' else [architecture]
    executable = app / ('Contents/MacOS/HexLive' if platform == 'macos' else 'HexLive.exe')
    if not executable.is_file(): raise ValueError('Player executable missing')
    for arch in architectures:
        name = 'HexLive.Updater.exe' if platform == 'windows' else 'HexLive.Updater' + ('-' + arch if len(architectures) == 2 else '')
        helper = package / name
        if not helper.is_file() or helper.stat().st_size == 0: raise ValueError('Updater missing: ' + name)
        if platform == 'macos':
            if not os.access(helper, os.X_OK): raise ValueError('Updater is not executable: ' + name)
            slices = subprocess.check_output(['/usr/bin/lipo', '-archs', str(helper)], text=True).split()
            if ('arm64' if arch == 'arm64' else 'x86_64') not in slices: raise ValueError('Wrong updater architecture: ' + name)
    if platform == 'macos':
        info = plistlib.loads((app / 'Contents/Info.plist').read_bytes())
        if info.get('CFBundleIdentifier') != 'com.juilcylove.hexgirls': raise ValueError('Unexpected macOS bundle ID')
        if info.get('CFBundleShortVersionString') != report['version']: raise ValueError('Player version differs from manifest')
        slices = subprocess.check_output(['/usr/bin/lipo', '-archs', str(executable)], text=True).split()
        if any(('arm64' if arch == 'arm64' else 'x86_64') not in slices for arch in architectures):
            raise ValueError('Player architectures differ from updater configuration')
    return report, app, config


def verify_macos_distribution(app):
    subprocess.run(['codesign', '--verify', '--deep', '--strict', str(app)], capture_output=True, text=True, check=True)
    details = subprocess.run(['codesign', '-d', '--verbose=4', str(app)], capture_output=True, text=True, check=True)
    if 'Authority=Developer ID Application:' not in details.stderr: raise ValueError('Public Mac requires configured Developer ID signing')
    subprocess.run(['xcrun', 'stapler', 'validate', str(app)], capture_output=True, text=True, check=True)
    subprocess.run(['spctl', '--assess', '--type', 'execute', str(app)], capture_output=True, text=True, check=True)


def publish(args):
    from cryptography.hazmat.primitives import serialization, hashes
    from cryptography.hazmat.primitives.asymmetric import padding, rsa
    release = args.release.resolve()
    report, app, config = inspect_player(release, args.platform, args.architecture)
    version(args.minimum_version)
    if version(args.minimum_version) > version(report['version']): raise ValueError('Minimum version exceeds release')
    if args.platform == 'macos': verify_macos_distribution(app)
    key = serialization.load_pem_private_key(args.signing_key.read_bytes(), password=None)
    if not isinstance(key, rsa.RSAPrivateKey) or key.key_size < 3072: raise ValueError('RSA-3072+ required')
    numbers = key.public_key().public_numbers()
    if (int.from_bytes(base64.b64decode(config['modulus']), 'big') != numbers.n or
        int.from_bytes(base64.b64decode(config['exponent']), 'big') != numbers.e): raise ValueError('Player trust key mismatch')
    http = session(args.credentials)
    latest_name = f'{args.platform}-{args.architecture}-stable.json'
    latest = files(http, args.folder, latest_name)
    if len(latest) > 1: raise ValueError('Ambiguous latest file')
    if latest:
        old = http.get(API + '/' + latest[0]['id'], params={'alt':'media'}, timeout=30); old.raise_for_status()
        old_envelope = old.json()
        old_bytes = base64.b64decode(old_envelope['payload'], validate=True)
        key.public_key().verify(base64.b64decode(old_envelope['signature'], validate=True), old_bytes, padding.PKCS1v15(), hashes.SHA256())
        old_payload = json.loads(old_bytes); validate(old_payload)
        if version(report['version']) <= version(old_payload['version']): raise ValueError('Refusing version downgrade or replacement')
    with tempfile.TemporaryDirectory(prefix='hexlive-publish-') as temp:
        archive = Path(temp) / 'player.zip'
        if args.platform == 'macos': subprocess.run(['ditto', '-c', '-k', '--keepParent', str(app), str(archive)], check=True)
        else: shutil.make_archive(str(archive.with_suffix('')), 'zip', release, app.name)
        with zipfile.ZipFile(archive) as zipped: unpacked = sum(e.file_size for e in zipped.infolist())
        sha = digest(archive); name = f'{args.platform}-{args.architecture}-{report["version"]}-{sha}.zip'
        previous = files(http, args.folder, name)
        if len(previous) > 1: raise ValueError('Ambiguous archive')
        if previous:
            if previous[0].get('sha256Checksum') != sha: raise ValueError('Existing archive mismatch')
            file_id = previous[0]['id']
        else: file_id = upload(http, archive, args.folder, name)
        manifest = dict(schemaVersion=1, product='client', channel='stable', platform=args.platform, architecture=args.architecture,
            version=report['version'], minimumVersion=args.minimum_version, publishedUtc=datetime.now(timezone.utc).isoformat(),
            notesEn=args.notes_en.read_text(), notesRu=args.notes_ru.read_text(), driveFileId=file_id,
            sha256=sha, size=archive.stat().st_size, unpackedSize=unpacked)
        validate(manifest)
        payload = json.dumps(manifest, ensure_ascii=False, separators=(',', ':')).encode()
        envelope = dict(payload=base64.b64encode(payload).decode(), signature=base64.b64encode(key.sign(payload, padding.PKCS1v15(), hashes.SHA256())).decode())
        metadata = Path(temp) / latest_name; metadata.write_text(json.dumps(envelope))
        identity = upload(http, metadata, args.folder, latest_name, latest[0]['id'] if latest else None)
        print(json.dumps({'version':manifest['version'], 'sha256':sha, 'manifestFileId':identity}))


def sync_once(args, http):
    for platform, architecture in [('windows','x64'),('macos','arm64'),('macos','x64')]:
        remote = files(http, args.folder, f'{platform}-{architecture}-stable.json')
        if not remote: continue
        if len(remote) != 1: raise ValueError('Ambiguous release metadata')
        response = http.get(API + '/' + remote[0]['id'], params={'alt':'media'}, timeout=30)
        response.raise_for_status()
        if len(response.content) > 200000: raise ValueError('Oversized envelope')
        envelope = response.json(); manifest = verify(envelope, args.public_key)
        if (manifest['platform'],manifest['architecture']) != (platform,architecture): raise ValueError('Wrong feed platform')
        target = args.cache / platform / architecture
        latest = target / 'latest.json'
        if latest.exists():
            prior = verify(json.loads(latest.read_text()), args.public_key)
            if version(manifest['version']) < version(prior['version']): raise ValueError('Release downgrade rejected')
            if manifest['version'] == prior['version'] and manifest['sha256'] != prior['sha256']: raise ValueError('Immutable release changed')
        blob = target / 'blobs' / manifest['sha256']; blob.parent.mkdir(parents=True, exist_ok=True)
        if not blob.exists():
            if shutil.disk_usage(blob.parent).free < manifest['size'] + 64*1024*1024: raise OSError('Cache disk full')
            part = blob.with_suffix('.part')
            offset = part.stat().st_size if part.exists() else 0
            if offset > manifest['size']: part.unlink(); offset = 0
            if offset < manifest['size']:
                response = http.get(API + '/' + manifest['driveFileId'], params={'alt':'media'},
                    headers={'Range': f'bytes={offset}-'} if offset else {}, stream=True, timeout=(30, 120))
                response.raise_for_status()
                if response.status_code == 206:
                    if response.headers.get('Content-Range') != f'bytes {offset}-{manifest["size"]-1}/{manifest["size"]}': raise ValueError('Bad content range')
                else: offset = 0
                with part.open('ab' if offset else 'wb') as output:
                    for chunk in response.iter_content(1024*1024):
                        offset += len(chunk)
                        if offset > manifest['size']: raise ValueError('Oversized archive')
                        output.write(chunk)
                    output.flush(); os.fsync(output.fileno())
            if part.stat().st_size != manifest['size'] or digest(part) != manifest['sha256']:
                part.unlink(); raise ValueError('Archive verification failed')
            part.replace(blob)
        if blob.stat().st_size != manifest['size'] or digest(blob) != manifest['sha256']: raise ValueError('Corrupt cache archive')
        atomic(latest, json.dumps(envelope).encode())
    atomic(args.cache / 'sync-status.json', json.dumps({'lastSuccessUtc':datetime.now(timezone.utc).isoformat()}).encode())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    auth = sub.add_parser('authorize'); auth.add_argument('--client-secret', type=Path, required=True); auth.add_argument('--output', type=Path, required=True)
    pub = sub.add_parser('publish'); pub.add_argument('release', type=Path); pub.add_argument('--platform', choices=['macos','windows'], required=True)
    pub.add_argument('--architecture', choices=['arm64','x64'], required=True); pub.add_argument('--minimum-version', required=True)
    pub.add_argument('--signing-key', type=Path, required=True); pub.add_argument('--notes-en', type=Path, required=True); pub.add_argument('--notes-ru', type=Path, required=True)
    sync = sub.add_parser('sync'); sync.add_argument('--cache', type=Path, required=True); sync.add_argument('--public-key', type=Path, required=True); sync.add_argument('--once', action='store_true')
    for cmd in (pub, sync): cmd.add_argument('--credentials', type=Path, required=True); cmd.add_argument('--folder', default=CLIENT_FOLDER)
    args = parser.parse_args()
    if args.command == 'authorize':
        from google_auth_oauthlib.flow import InstalledAppFlow
        if args.output.exists(): raise ValueError('Credential file already exists')
        credentials = InstalledAppFlow.from_client_secrets_file(str(args.client_secret), scopes=['https://www.googleapis.com/auth/drive']).run_local_server(port=0)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        fd = os.open(args.output, os.O_WRONLY|os.O_CREAT|os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as output: output.write(credentials.to_json())
    elif args.command == 'publish': publish(args)
    else:
        import fcntl
        args.cache.mkdir(parents=True, exist_ok=True)
        lock = (args.cache / '.sync.lock').open('a')
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        http = session(args.credentials)
        while True:
            try: sync_once(args, http)
            except Exception as exc:
                # Do not print HTTP responses/credentials. The last good release remains untouched.
                print(datetime.now(timezone.utc).isoformat(), 'Sync failed:', type(exc).__name__, flush=True)
                if args.once: raise
            if args.once: break
            time.sleep(60)

if __name__ == '__main__': main()
