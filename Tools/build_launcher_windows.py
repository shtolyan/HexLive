#!/usr/bin/env python3
"""§156 publish the self-contained launcher without committing the player token."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "Launcher" / "HexLive.Launcher" / "HexLive.Launcher.csproj"
DEFAULT_TOKEN = Path.home() / ".config" / "hexlive" / "servers" / "62.146.235.120" / "player-token"
DEFAULT_OUTPUT = Path.home() / "hex-girls" / "Launcher"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--token-file", type=Path, default=DEFAULT_TOKEN)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--sign-thumbprint", default=os.environ.get("HEXLIVE_SIGN_THUMBPRINT", ""))
    args = parser.parse_args()
    token = args.token_file.expanduser().read_text(encoding="utf-8").strip()
    if not token.startswith("hexplay_"):
        raise RuntimeError("player token file is missing or malformed")
    output = args.output.expanduser().resolve()
    output.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    environment["HexLivePlayerToken"] = token
    command = ["dotnet", "publish", str(PROJECT), "-c", "Release", "-r", "win-x64",
               "--self-contained", "true", "--nologo", "-o", str(output)]
    completed = subprocess.run(command, cwd=ROOT, env=environment, check=False)
    if completed.returncode:
        return completed.returncode
    executable = output / "HexLiveInstaller.exe"
    if not executable.is_file():
        raise RuntimeError(f"publish did not create {executable}")
    if args.sign_thumbprint:
        signtool = shutil.which("signtool")
        if not signtool:
            raise RuntimeError("HEXLIVE_SIGN_THUMBPRINT was set but signtool was not found")
        subprocess.run([signtool, "sign", "/sha1", args.sign_thumbprint, "/fd", "SHA256",
                        "/tr", "http://timestamp.digicert.com", "/td", "SHA256", str(executable)], check=True)
    else:
        print("Launcher published unsigned (signing hook is ready).")
    print(executable)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError) as error:
        print(f"ERROR: {error}", file=sys.stderr)
        raise SystemExit(1)
