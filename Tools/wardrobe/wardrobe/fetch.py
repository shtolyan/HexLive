"""Stage: download the archives a drop is made of.

Several links usually arrive together (a product is often split into parts, or
ships a G3 and a G8 package), so this reports per file as it goes — that
progress is what the Telegram bot relays.

Google Drive needs its own client: for anything past ~100 MB it answers the
plain download URL with an HTML virus-scan interstitial instead of the file,
and the confirmation dance changes often enough that `gdown` is worth the
dependency. Everything else is a straight streamed GET.
"""
from __future__ import annotations

import re
import time
from pathlib import Path
from urllib.parse import unquote, urlparse

import requests

from . import cache, config

_GDRIVE_ID = [
    re.compile(r"drive\.google\.com/file/d/([A-Za-z0-9_-]{20,})"),
    re.compile(r"drive\.google\.com/open\?id=([A-Za-z0-9_-]{20,})"),
    re.compile(r"drive\.google\.com/uc\?id=([A-Za-z0-9_-]{20,})"),
    re.compile(r"[?&]id=([A-Za-z0-9_-]{20,})"),
]

_ARCHIVE_SUFFIXES = (".zip", ".rar", ".7z")


def drive_id(url: str) -> str | None:
    for pattern in _GDRIVE_ID:
        found = pattern.search(url)
        if found:
            return found.group(1)
    return None


def _name_from_headers(response: requests.Response, url: str) -> str:
    disposition = response.headers.get("content-disposition", "")
    found = re.search(r'filename\*?=(?:UTF-8\'\')?"?([^";]+)"?', disposition)
    if found:
        return unquote(found.group(1))
    name = Path(urlparse(url).path).name
    return unquote(name) or "download.bin"


def _download_direct(url: str, into: Path) -> Path:
    with requests.get(url, stream=True, timeout=60,
                      headers={"User-Agent": "Mozilla/5.0"}) as response:
        response.raise_for_status()
        target = into / _name_from_headers(response, url)
        with open(target, "wb") as handle:
            for chunk in response.iter_content(chunk_size=1 << 20):
                handle.write(chunk)
    return target


def _download_drive(file_id: str, into: Path) -> Path:
    import gdown  # imported lazily so a direct-link run needs no Drive support

    out = gdown.download(id=file_id, output=str(into) + "/", quiet=True)
    if not out:
        raise RuntimeError(
            "Google Drive не отдал файл. Ссылка точно открыта «для всех, у кого есть ссылка»?")
    return Path(out)


# MediaFire serves an HTML landing page; the real file lives behind a button
# whose href points at a per-request `download####.mediafire.com` host. Newer
# pages hide it in a data attribute instead, so both are tried.
_MEDIAFIRE_HREF = [
    re.compile(r'id="downloadButton"[^>]*\shref="([^"]+)"'),
    re.compile(r'href="(https://download[0-9]*\.mediafire\.com/[^"]+)"'),
    re.compile(r'data-scrambled-url="([A-Za-z0-9+/=]+)"'),
]


def _download_mediafire(url: str, into: Path) -> Path:
    session = requests.Session()
    session.headers["User-Agent"] = "Mozilla/5.0"
    page = session.get(url, timeout=60)
    page.raise_for_status()

    direct = None
    for pattern in _MEDIAFIRE_HREF:
        found = pattern.search(page.text)
        if not found:
            continue
        candidate = found.group(1)
        if pattern.pattern.startswith("data-scrambled"):
            import base64
            candidate = base64.b64decode(candidate).decode("utf-8", "replace")
        if candidate.startswith("http"):
            direct = candidate
            break

    if direct is None:
        raise RuntimeError(
            "на странице MediaFire не нашлась прямая ссылка — вероятно, изменилась вёрстка "
            "или файл требует капчу")

    with session.get(direct, stream=True, timeout=120) as response:
        response.raise_for_status()
        target = into / _name_from_headers(response, direct)
        with open(target, "wb") as handle:
            for chunk in response.iter_content(chunk_size=1 << 20):
                handle.write(chunk)
    return target


def fetch_one(url: str, into: Path) -> dict:
    into.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    file_id = drive_id(url)
    host = urlparse(url).netloc.lower()

    if file_id:
        path = _download_drive(file_id, into)
    elif "mediafire.com" in host:
        path = _download_mediafire(url, into)
    else:
        path = _download_direct(url, into)

    size = path.stat().st_size
    return {
        "url": url,
        "file": str(path),
        "name": path.name,
        "size_mb": round(size / 1024 / 1024, 1),
        "seconds": round(time.monotonic() - started, 1),
        "source": "google drive" if file_id else
                  ("mediafire" if "mediafire.com" in host else "http"),
        "archive": path.suffix.lower() in _ARCHIVE_SUFFIXES,
    }


def fetch(urls: list[str], into: Path | None = None,
          progress=lambda _: None, force: bool = False) -> dict:
    into = into or config.DOWNLOADS
    report: dict = {"files": [], "errors": []}

    for url in urls:
        url = url.strip()
        if not force and (known := cache.downloaded(url)) is not None:
            size = round(known.stat().st_size / 1024 / 1024, 1)
            progress(f"   ⏭ {known.name} — уже скачан ({size} МБ), пропускаю")
            report["files"].append({
                "url": url, "file": str(known), "name": known.name,
                "size_mb": size, "seconds": 0.0, "source": "кэш",
                "archive": known.suffix.lower() in _ARCHIVE_SUFFIXES,
                "cached": True,
            })
            continue
        try:
            entry = fetch_one(url, into)
        except Exception as e:  # noqa: BLE001 — the report is the error channel
            report["errors"].append(f"{url}: {e}")
            continue
        cache.remember_download(url, Path(entry["file"]))
        progress(f"   ✔ {entry['name']} — {entry['size_mb']} МБ за {entry['seconds']} с")
        report["files"].append(entry)
        if not entry["archive"]:
            report["errors"].append(
                f"{entry['name']}: это не архив ({entry['file']}) — распаковывать нечего")

    report["archives"] = [f["file"] for f in report["files"] if f["archive"]]
    report["ok"] = bool(report["archives"]) and not report["errors"]
    return report
