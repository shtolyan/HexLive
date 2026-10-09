#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Re-pack a WebGL build's loose audio into FSB for the browser FMOD (§168.6).

    python3 Tools/webgl_audio_fsb.py <build-dir> --fsbankcl /path/to/fsbankcl
    python3 Tools/webgl_audio_fsb.py <build-dir> --fsbankcl ... --dry-run

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
import shlex
import shutil
import subprocess
import sys

AUDIO = {".wav", ".ogg", ".mp3"}
DEFAULT_CACHE = pathlib.Path.home() / "hex-girls" / "webgl-build" / "fsb-cache"
# Verified against the real fsbankcl before first use — see --command-template.
DEFAULT_TEMPLATE = "{fsbankcl} -format vorbis -quality {quality} -o {out} {src}"


def parse_args() -> argparse.Namespace:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("build", type=pathlib.Path, help="WebGL build directory (has index.html)")
    p.add_argument("--fsbankcl", type=pathlib.Path, required=True)
    p.add_argument("--quality", type=int, default=50, help="Vorbis quality 1..100")
    p.add_argument("--jobs", type=int, default=4)
    p.add_argument("--cache", type=pathlib.Path, default=DEFAULT_CACHE)
    p.add_argument("--command-template", default=DEFAULT_TEMPLATE,
                   help="placeholders: {fsbankcl} {quality} {out} {src}")
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


def encode(args: argparse.Namespace, src: pathlib.Path, out: pathlib.Path) -> None:
    key = hashlib.sha256(f"{sha256(src)}|{args.quality}|{args.command_template}".encode()).hexdigest()
    cached = args.cache / f"{key}.fsb"
    if not cached.exists():
        temporary = cached.with_suffix(".fsb.tmp")
        command = args.command_template.format(
            fsbankcl=shlex.quote(str(args.fsbankcl)), quality=args.quality,
            out=shlex.quote(str(temporary)), src=shlex.quote(str(src)))
        result = subprocess.run(command, shell=True, capture_output=True, text=True)
        if result.returncode != 0 or not temporary.exists():
            raise RuntimeError(f"fsbankcl failed for {src}: {result.stderr or result.stdout}")
        temporary.replace(cached)
    shutil.copyfile(cached, out)


def main() -> int:
    args = parse_args()
    root = args.build.resolve() / "StreamingAssets" / "HexLive"
    manifest_path = root / "web-audio-manifest.json"
    if not manifest_path.is_file():
        sys.exit(f"{manifest_path} missing — not a HexLive WebGL build")
    if not args.dry_run and not args.fsbankcl.is_file():
        sys.exit(f"fsbankcl not found: {args.fsbankcl}")

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
    failures = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=args.jobs) as pool:
        futures = {
            pool.submit(encode, args, root / entry, root / f"{stem}.fsb"): entry
            for stem, entry in chosen.items()
        }
        for done, future in enumerate(concurrent.futures.as_completed(futures), 1):
            try:
                future.result()
            except Exception as error:  # noqa: BLE001 - report every failed file
                failures.append(f"{futures[future]}: {error}")
            if done % 500 == 0:
                print(f"[fsb] {done}/{len(futures)}")

    if failures:
        print("\n".join(failures[:20]), file=sys.stderr)
        sys.exit(f"[fsb] {len(failures)} file(s) failed — build left unchanged except new .fsb")

    for entry in loose + dropped:
        (root / entry).unlink(missing_ok=True)
    files = sorted(kept + [f"{stem}.fsb" for stem in chosen])
    manifest_path.write_text(json.dumps({"files": files}, ensure_ascii=False, indent=0) + "\n")
    print(f"[fsb] done: {len(chosen)} .fsb, manifest {len(files)} entries")
    return 0


if __name__ == "__main__":
    sys.exit(main())
