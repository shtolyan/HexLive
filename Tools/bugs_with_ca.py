#!/usr/bin/env python3
"""Run the project Flashback client with an explicit, verified CA bundle.

Example: python Tools/bugs_with_ca.py --ca-file <device-local.pem> whoami
The bundle and credentials stay outside Git. No global SSL state is changed.
"""
import argparse
import json
from pathlib import Path
import runpy
import ssl
import sys
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--ca-file", required=True)
    options, remaining = parser.parse_known_args()
    try:
        context = ssl.create_default_context(cafile=str(Path(options.ca_file).expanduser()))
    except (OSError, ssl.SSLError) as error:
        raise SystemExit(f"Cannot load CA file: {error}. No fallback was used.") from None
    if not context.check_hostname or context.verify_mode != ssl.CERT_REQUIRED:
        raise SystemExit("Verified TLS context is required.")

    path = Path(__file__).resolve().parents[1] / ".agents/skills/hexlive-bug-tracker/scripts/bugs.py"
    client = runpy.run_path(str(path))

    def call_with_ca(args, method, endpoint, payload=None, auth=False, api=None):
        data = None if payload is None else json.dumps(payload, ensure_ascii=False).encode()
        headers = {"Accept": "application/json", "Authorization": "Bearer " + client["token"](args)}
        if data is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(
            (api or args.api).rstrip("/") + endpoint, data=data, headers=headers, method=method
        )
        try:
            with urllib.request.urlopen(request, timeout=15, context=context) as response:
                body = response.read().decode()
                return None if not body else json.loads(body)
        except urllib.error.HTTPError as error:
            raise SystemExit(f"HTTP {error.code}: {error.read().decode(errors='replace')}") from None

    # Replace only this loaded client's transport. Keep its CLI, identity reader,
    # revision handling and commit uploads; never patch urllib or ssl globally.
    client["main"].__globals__["call"] = call_with_ca
    sys.argv = [str(path), *remaining]
    client["main"]()


if __name__ == "__main__":
    main()
