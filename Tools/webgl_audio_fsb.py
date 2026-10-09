#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Re-pack a WebGL build's loose audio into FSB for the browser FMOD (§168.6).

    python3 Tools/webgl_audio_fsb.py <build-dir>
    python3 Tools/webgl_audio_fsb.py <build-dir> --dry-run

The encoder is hexfsb (Tools/webgl/hexfsb.c) over libfsbank from the FMOD
Engine SDK 2.03 for macOS — that SDK has no fsbankcl. Build it once with
Tools/webgl/build_hexfsb.sh <sdk-copy>; the default --hexfsb points at
~/hex-girls/webgl-build/fmod-2.03.15/bin/hexfsb.

Why: the FMOD build Unity links for WebGL is the reduced one — it reads only
FSB containers. Loose .mp3, .ogg and even .wav handed to createSound answer
ERR_FORMAT (measured on all three, 9.10.2026), so music, voices, loops and the
SFX fallback were silent in the browser.

What it does, on the BUILD OUTPUT only (the repository and StreamingAssets in
Assets stay untouched — desktop keeps its plain files):
1. read StreamingAssets/HexLive/web-audio-manifest.json written by
   HexLiveWebGLPlayerBuild;
2. encode every audio entry into `<same stem>.fsb` with one Vorbis subsound,
   through a content cache keyed by SHA-256 of the source + settings, so a
   rebuild re-encodes only changed files;
3. delete the loose originals from the build, keep `.vis` lipsync sidecars
   (Vorbis keeps the exact sample count), and rewrite the manifest.

FmodSfx in the web build opens `.fsb`, plays subsound 0 and releases the
container with the sound. A stem that exists both as .mp3 and the build's
generated .ogg (music) is encoded once, from the .ogg.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile

AUDIO = {".wav", ".ogg", ".mp3"}
DEFAULT_CACHE = pathlib.Path.home() / "hex-girls" / "webgl-build" / "fsb-cache"
DEFAULT_HEXFSB = pathlib.Path.home() / "hex-girls" / "webgl-build" / "fmod-2.03.15" / "bin" / "hexfsb"
# Part of the cache key: a change of encoder or settings re-encodes everything.
ENCODER_TAG = "hexfsb/fsbank-2.03.15/vorbis"


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("build", type=pathlib.Path, help="WebGL build directory (has index.html)")
    p.add_argument("--hexfsb", type=pathlib.Path, default=DEFAULT_HEXFSB)
    p.add_argument("--quality", type=int, default=50, help="Vorbis quality 1..100")
    p.add_argument("--jobs", type=int, default=6, help="parallel hexfsb processes")
    p.add_argument("--cache", type=pathlib.Path, default=DEFAULT_CACHE)
    p.add_argument("--exclude-prefix", action="append", default=None,
                   help="manifest prefix dropped from the web build entirely; default: "
                        "Sfx/Voices/ (hexkufa — frozen, §67.16; the game never plays it)")
    p.add_argument("--dry-run", action="store_true")
    return p.parse_args()


def sha256(path: pathlib.Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def pick_sources(entries: list[str]) -> dict[str, str]:
    """stem path (no extension) -> chosen source entry; .ogg beats .mp3 for one stem."""
    chosen: dict[str, str] = {}
    for entry in entries:
        suffix = pathlib.PurePosixPath(entry).suffix.lower()
        if suffix not in AUDIO:
            continue
        stem = entry[: -len(suffix)]
        current = chosen.get(stem)
        if current is None or suffix == ".ogg":
            chosen[stem] = entry
    return chosen


def cache_key(args: argparse.Namespace, src: pathlib.Path) -> str:
    return hashlib.sha256(f"{sha256(src)}|{args.quality}|{ENCODER_TAG}".encode()).hexdigest()


def encode_batch(args: argparse.Namespace, jobs: list[tuple[pathlib.Path, pathlib.Path]]) -> list[str]:
    """One hexfsb process for a slice of (source, cached .fsb) pairs; returns failures."""
    pending = {cached.with_suffix(".fsb.tmp"): cached for _, cached in jobs}
    lines = "".join(f"{src}\t{cached.with_suffix('.fsb.tmp')}\n" for src, cached in jobs)
    with tempfile.TemporaryDirectory(prefix="hexfsb-") as scratch:
        result = subprocess.run([str(args.hexfsb), "--quality", str(args.quality), "--cache", scratch],
                                input=lines, capture_output=True, text=True)
    failures = []
    for line in result.stdout.splitlines():
        status, _, rest = line.partition("\t")
        out = pathlib.Path(rest.split("\t")[0])
        if status == "ok" and out.exists():
            out.replace(pending.pop(out))
        else:
            failures.append(line)
    if pending and not failures:
        failures.append(f"hexfsb exited {result.returncode}: {result.stderr.strip()[:500]}")
    return failures


def main() -> int:
    args = parse_args()
    root = args.build.resolve() / "StreamingAssets" / "HexLive"
    manifest_path = root / "web-audio-manifest.json"
    if not manifest_path.is_file():
        sys.exit(f"{manifest_path} missing — not a HexLive WebGL build")
    if not args.dry_run and not args.hexfsb.is_file():
        sys.exit(f"hexfsb not found: {args.hexfsb} — Tools/webgl/build_hexfsb.sh <sdk-copy>")

    entries = json.loads(manifest_path.read_text())["files"]
    excluded_prefixes = args.exclude_prefix if args.exclude_prefix is not None else ["Sfx/Voices/"]
    dropped = [e for e in entries if any(e.startswith(prefix) for prefix in excluded_prefixes)]
    entries = [e for e in entries if e not in set(dropped)]
    print(f"[fsb] dropped {len(dropped)} entries under {excluded_prefixes}")
    chosen = pick_sources(entries)
    loose = [e for e in entries if pathlib.PurePosixPath(e).suffix.lower() in AUDIO]
    kept = [e for e in entries if pathlib.PurePosixPath(e).suffix.lower() not in AUDIO]
    print(f"[fsb] {len(chosen)} sounds from {len(loose)} loose files; {len(kept)} sidecars kept")
    if args.dry_run:
        for stem, entry in list(chosen.items())[:10]:
            print(f"  {entry} -> {stem}.fsb")
        return 0

    args.cache.mkdir(parents=True, exist_ok=True)
    cached = {stem: args.cache / f"{cache_key(args, root / entry)}.fsb" for stem, entry in chosen.items()}
    # One job per cache entry: identical files share it and must not race on one .tmp.
    todo = sorted({path: root / chosen[stem] for stem, path in cached.items() if not path.exists()}.items())
    todo = [(src, path) for path, src in todo]
    print(f"[fsb] {len(chosen) - len(todo)} cached, {len(todo)} to encode")
    failures = []
    if todo:
        slices = [todo[i::args.jobs] for i in range(args.jobs) if todo[i::args.jobs]]
        with concurrent.futures.ThreadPoolExecutor(max_workers=len(slices)) as pool:
            for result in pool.map(lambda part: encode_batch(args, part), slices):
                failures.extend(result)

    if failures:
        print("\n".join(failures[:20]), file=sys.stderr)
        sys.exit(f"[fsb] {len(failures)} file(s) failed — build left unchanged")

    for stem, path in cached.items():
        shutil.copyfile(path, root / f"{stem}.fsb")
    for entry in loose + dropped:
        (root / entry).unlink(missing_ok=True)
    files = sorted(kept + [f"{stem}.fsb" for stem in chosen])
    manifest_path.write_text(json.dumps({"files": files}, ensure_ascii=False, indent=0) + "\n")
    print(f"[fsb] done: {len(chosen)} .fsb, manifest {len(files)} entries")
    return 0


if __name__ == "__main__":
    sys.exit(main())
