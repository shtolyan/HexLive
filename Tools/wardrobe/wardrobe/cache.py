"""What the pipeline has already done, so it does not do it twice.

Downloading is minutes and unpacking is hundreds of files; re-running a drop
should not pay for either again. Two records, both keyed by what the operator
actually gives us:

  * `downloads` — URL to the file it produced. A link cannot be checked against
    the disk without asking the host what the file is called, so the mapping is
    remembered rather than derived.
  * `installs` — archive to the wearables it put in the library, plus a few of
    the paths it wrote.

Both are re-verified against the disk before being trusted: a remembered file
that is gone is not a skip, it is a re-run. The record is a cache, not a
source of truth, so losing it costs time and nothing else.
"""
from __future__ import annotations

import json
from pathlib import Path

from . import config


def _path() -> Path:
    return config.WORK / "cache.json"


def load() -> dict:
    try:
        return json.loads(_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def save(data: dict) -> None:
    _path().parent.mkdir(parents=True, exist_ok=True)
    _path().write_text(json.dumps(data, indent=2, ensure_ascii=False),
                       encoding="utf-8")


def _section(data: dict, name: str) -> dict:
    return data.setdefault(name, {})


# --- downloads --------------------------------------------------------------

def downloaded(url: str) -> Path | None:
    """The file this URL produced last time, if it is still on disk."""
    entry = _section(load(), "downloads").get(url)
    if not entry:
        return None
    path = Path(entry["file"])
    return path if path.exists() and path.stat().st_size > 0 else None


def remember_download(url: str, path: Path) -> None:
    data = load()
    _section(data, "downloads")[url] = {"file": str(path), "name": path.name,
                                        "size": path.stat().st_size}
    save(data)


# --- installs ---------------------------------------------------------------

def installed(archive: Path) -> dict | None:
    """What this archive put in the library, if that content is still there.

    Verified by sampling the recorded paths rather than trusting the record:
    a library the operator has since cleaned out must be re-populated, and
    silently skipping that would leave `dress` loading files that do not exist.
    """
    entry = _section(load(), "installs").get(archive.name)
    if not entry:
        return None
    samples = entry.get("samples") or []
    if not samples or not all((config.DAZ_LIBRARY / s).exists() for s in samples):
        return None
    return entry


def remember_install(archive: Path, wearables: list[dict], files: int) -> None:
    data = load()
    _section(data, "installs")[archive.name] = {
        "files": files,
        "wearables": wearables,
        # A handful is enough to notice a wiped library, and keeps the record
        # small next to a product that ships nine hundred files.
        "samples": [w["relative"] for w in wearables[:5] if w.get("relative")],
    }
    save(data)


def forget(archive_or_url: str) -> bool:
    """Drop one record so the next run redoes that step."""
    data = load()
    hit = False
    for section in ("downloads", "installs"):
        if archive_or_url in _section(data, section):
            del data[section][archive_or_url]
            hit = True
    if hit:
        save(data)
    return hit
