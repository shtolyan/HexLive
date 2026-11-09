"""Check the browser reporter contract without creating bug reports.

curl uses the platform TLS trust store. Credentials enter curl over stdin only.
An optional synthetic WAV creates one expiring transcription draft, never a bug.
"""
import argparse
import json
from pathlib import Path
import subprocess
import time
from urllib.parse import urlsplit

ORIGINS = (
    "https://play.62-146-235-120.sslip.io",
    "https://163-245-204-96.sslip.io",
)


def quote(value):
    return '"' + str(value).replace('\\', '\\\\').replace('"', '\\"').replace('\r', '\\r').replace('\n', '\\n') + '"'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, default=Path.home() / '.config/hexlive/reporting.json')
    parser.add_argument('--origin', action='append', help='Exact browser origin; defaults to both published game origins')
    parser.add_argument('--wav', type=Path, help='Optional synthetic mono PCM16/16kHz fixture for real STT verification')
    args = parser.parse_args()
    cfg = json.loads(args.config.read_text(encoding='utf-8-sig'))
    api = cfg['api'].rstrip('/')
    parts = urlsplit(api)
    if parts.scheme != 'https' or parts.username or parts.password or parts.query or parts.fragment or parts.path != '/api/bugs/v1':
        raise SystemExit('Expected an HTTPS Flashback API URL')
    if cfg.get('project') != 'hexlive' or not cfg.get('token'):
        raise SystemExit('Expected the device-local hexlive reporter capability')
    authority = parts.scheme + '://' + parts.netloc

    def request(origin, path, method='GET', body=None, content_type='application/json', preflight=False):
        lines = ['url = ' + quote(authority + path), 'request = ' + quote(method),
                 'header = ' + quote('Origin: ' + origin)]
        if preflight:
            lines += ['header = "Access-Control-Request-Method: POST"',
                      'header = "Access-Control-Request-Headers: authorization,content-type,x-flashback-project"']
        else:
            lines += ['header = ' + quote('Authorization: Bearer ' + cfg['token']),
                      'header = "X-Flashback-Project: hexlive"']
        if body is not None:
            lines += ['header = ' + quote('Content-Type: ' + content_type),
                      ('data-binary = ' + quote('@' + str(body.resolve()))) if isinstance(body, Path) else 'data = ' + quote(body)]
        result = subprocess.run(['curl', '--silent', '--show-error', '--include', '--max-time', '30',
                                 '--write-out', '\n%{http_code}', '--config', '-'],
                                input='\n'.join(lines) + '\n', capture_output=True, text=True, encoding='utf-8')
        if result.returncode:
            raise RuntimeError('HTTPS transport failed: ' + result.stderr.strip())
        raw, _, status = result.stdout.rpartition('\n')
        headers, _, payload = raw.rpartition('\n\n')
        try:
            payload = json.loads(payload) if payload else {}
        except ValueError:
            payload = {'error': 'Non-JSON response'}
        return int(status), payload, headers.lower()

    def expect(origin, path, status, **kwargs):
        actual, payload, headers = request(origin, path, **kwargs)
        if actual != status:
            raise RuntimeError(f'{origin} {path}: expected HTTP {status}, got {actual}: {str(payload)[:500]}')
        if origin in origins and f'access-control-allow-origin: {origin}'.lower() not in headers:
            raise RuntimeError('Missing browser CORS response for ' + origin)
        return payload, headers

    origins = args.origin or list(ORIGINS)
    for origin in origins:
        identity, _ = expect(origin, '/api/auth/me', 200)
        if identity.get('reporterProject') != 'hexlive' or identity.get('isAdmin') or identity.get('allowedStatuses') != ['created']:
            raise RuntimeError('Expected a write-only hexlive reporter, never an agent/admin key')
        _, headers = expect(origin, '/api/bugs/v1/transcriptions', 204, method='OPTIONS', preflight=True)
        if not all(x in headers for x in ('authorization', 'content-type', 'x-flashback-project')):
            raise RuntimeError('Missing CORS preflight request headers')
        expect(origin, '/api/bugs/v1/transcriptions', 400, method='POST', body='bad-wav', content_type='audio/wav')
        expect(origin, '/api/bugs/v1/reports', 400, method='POST', body='{}')
        expect(origin, '/api/bugs/v1/reports', 403)
        print(f'PASS {origin}: identity, preflight, WAV/report validation, reports unreadable', flush=True)
    expect('https://untrusted.invalid', '/api/auth/me', 403)
    print('PASS unlisted origin remains forbidden', flush=True)
    if args.wav:
        draft, _ = expect(origins[0], '/api/bugs/v1/transcriptions', 202, method='POST', body=args.wav, content_type='audio/wav')
        job_id = draft.get('id')
        if not job_id:
            raise RuntimeError('Missing transcription draft receipt')
        deadline = time.monotonic() + 300
        while time.monotonic() < deadline:
            draft, _ = expect(origins[0], '/api/bugs/v1/transcriptions/' + job_id, 200)
            if draft.get('status') == 'failed':
                raise RuntimeError('Transcription failed: ' + str(draft.get('error')))
            if draft.get('status') == 'complete':
                if not draft.get('text', '').strip():
                    raise RuntimeError('Empty transcription')
                print('PASS real transcription complete; draft=' + job_id + '; characters=' + str(len(draft['text'])), flush=True)
                break
            time.sleep(1)
        else:
            raise RuntimeError('Transcription exceeded 300 seconds')
    print('No bug reports created.', flush=True)


if __name__ == '__main__':
    main()
