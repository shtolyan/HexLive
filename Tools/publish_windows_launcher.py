#!/usr/bin/env python3
"""§156 atomically publish the stable initial-download HexLiveInstaller.exe."""

from __future__ import annotations

import argparse
from pathlib import Path
import shlex
import subprocess
import sys

REMOTE_ROOT = "/var/lib/hexlive/player-releases/windows"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("installer", type=Path)
    parser.add_argument("--host", default="hexlive-server")
    args = parser.parse_args()
    installer = args.installer.resolve()
    if not installer.is_file() or installer.name.lower() != "hexliveinstaller.exe":
        raise RuntimeError("expected a published HexLiveInstaller.exe")
    remote = f"/tmp/hexlive-installer-{installer.stat().st_size}.part"
    subprocess.run(["scp", str(installer), f"{args.host}:{remote}"], check=True)
    q = shlex.quote
    command = (
        f"set -eu; install -d -m 0755 {q(REMOTE_ROOT)}; "
        f"install -m 0755 {q(remote)} {q(REMOTE_ROOT + '/HexLiveInstaller.exe.tmp')}; "
        f"mv -Tf {q(REMOTE_ROOT + '/HexLiveInstaller.exe.tmp')} {q(REMOTE_ROOT + '/HexLiveInstaller.exe')}; "
        f"rm -f {q(remote)}"
    )
    subprocess.run(["ssh", "-o", "BatchMode=yes", args.host, command], check=True)
    print("Published /api/releases/v1/windows/installer")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, subprocess.CalledProcessError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
