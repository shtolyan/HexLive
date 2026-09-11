"""Device-local Flashback credentials shared by the CLI and player builders.

Never read a repository credential or silently select a token from the shell.
A missing/invalid explicitly selected file must fail instead of changing identity.
"""
from pathlib import Path
import os


def device_token_path() -> Path:
    return Path.home() / ".config" / "hexlive" / "bug-token"


def read_bug_token(token_file=None) -> str:
    path = Path(token_file).expanduser() if token_file is not None else device_token_path()
    try:
        secret = path.read_text(encoding="utf-8-sig").strip()
    except (OSError, UnicodeError):
        raise RuntimeError(f"Cannot read device bug token: {path}. Configure this device's key; no fallback was used.") from None
    if not 24 <= len(secret) <= 4096 or any(ch.isspace() for ch in secret):
        raise RuntimeError(f"Invalid device bug token file: {path}. No fallback was used.")
    return secret


def configure_build_bug_token() -> None:
    # Build snapshot helpers use this process-local value; inherited values never win.
    os.environ["HEXLIVE_BUG_TOKEN"] = read_bug_token()
