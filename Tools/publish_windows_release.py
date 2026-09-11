#!/usr/bin/env python3
"""§156 publish a completed Windows Player release through the SSH-only path."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shlex
import subprocess
import sys

DEFAULT_HOST = "hexlive-server"
REMOTE_ROOT = "/var/lib/hexlive/player-releases/windows"


def run(command: list[str]) -> None:
    completed = subprocess.run(command, check=False)
    if completed.returncode:
        raise RuntimeError(f"command failed ({completed.returncode}): {command[0]}")


def digest(path: Path) -> str:
    value = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            value.update(chunk)
    return value.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("release", type=Path, help="v<version> directory")
    parser.add_argument("--host", default=DEFAULT_HOST)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    release = args.release.resolve()
    manifest_path = release / "build-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    info = manifest["playerRelease"]
    archive = release / info["archiveFileName"]
    sha = info["archiveSha256"].lower()
    if archive.stat().st_size != int(info["archiveSize"]) or digest(archive) != sha:
        raise RuntimeError("archive size/SHA does not match build-manifest.json")
    if args.dry_run:
        print(f"Would publish {archive} as {sha} to {args.host}:{REMOTE_ROOT}")
        return 0

    remote_archive = f"/tmp/hexlive-player-{sha}.part"
    remote_manifest = f"/tmp/hexlive-windows-latest-{sha}.json"
    run(["scp", str(archive), f"{args.host}:{remote_archive}"])
    run(["scp", str(manifest_path), f"{args.host}:{remote_manifest}"])
    q = shlex.quote
    command = (
        "set -eu; "
        f"test \"$(sha256sum {q(remote_archive)} | cut -d' ' -f1)\" = {q(sha)}; "
        f"install -d -m 0755 {q(REMOTE_ROOT + '/blobs')}; "
        f"if test ! -e {q(REMOTE_ROOT + '/blobs/' + sha)}; then "
        f"install -m 0644 {q(remote_archive)} {q(REMOTE_ROOT + '/blobs/' + sha)}; fi; "
        f"install -m 0644 {q(remote_manifest)} {q(REMOTE_ROOT + '/latest.json.tmp')}; "
        f"mv -Tf {q(REMOTE_ROOT + '/latest.json.tmp')} {q(REMOTE_ROOT + '/latest.json')}; "
        f"rm -f {q(remote_archive)} {q(remote_manifest)}"
    )
    run(["ssh", "-o", "BatchMode=yes", args.host, command])
    print(f"Published Windows Player {manifest['version']} ({sha})")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, KeyError, ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
