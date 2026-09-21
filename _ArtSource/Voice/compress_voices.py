#!/usr/bin/env python3
"""§67.17 — сжать голосовые банки: WAV PCM16 -> Ogg Vorbis (oggenc -q5).

Несжатый банк весил 938 МБ (5.3 МБ на минуту речи), а липсинк давно не читает
звук в игре — губы идут по запечённому .vis, рантайм сверяет только длину.
Vorbis сохраняет длину сэмпл-в-сэмпл, поэтому .vis остаётся действительным и
перепекать ничего не нужно. .meta переезжает вместе с файлом (guid тот же).

Идемпотентен: трогает только оставшиеся .wav. generate_voices.py сам кладёт
новые дубли уже сжатыми — этот скрипт нужен для банка, сгенерированного раньше.

    python3 _ArtSource/Voice/compress_voices.py            # оба банка
    python3 _ArtSource/Voice/compress_voices.py --dry-run
Нужен oggenc: brew install vorbis-tools (ffmpeg собран без libvorbis).
"""
from __future__ import annotations

import argparse
import pathlib
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor

import bake_lipsync
from generate_voices import LOCALIZED_OUTDIR, OGG_QUALITY, OUTDIR


def compress(wav: pathlib.Path) -> tuple[int, int]:
    ogg = wav.with_suffix(".ogg")
    before = wav.stat().st_size
    subprocess.run(["oggenc", "-Q", "-q", OGG_QUALITY, "-o", str(ogg), str(wav)], check=True)
    if bake_lipsync.voice_frames(ogg) != bake_lipsync.voice_frames(wav):
        ogg.unlink()
        raise RuntimeError(f"{wav.name}: кодировщик изменил длину")
    meta = pathlib.Path(str(wav) + ".meta")
    if meta.exists():
        meta.rename(str(ogg) + ".meta")
    wav.unlink()
    return before, ogg.stat().st_size


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    wavs = [w for root in (OUTDIR, LOCALIZED_OUTDIR) if root.exists()
            for w in sorted(root.rglob("voice_*.wav"))]
    print(f"к сжатию: {len(wavs)}")
    if args.dry_run or not wavs:
        return 0
    with ThreadPoolExecutor() as pool:
        sizes = list(pool.map(compress, wavs))
    before, after = sum(b for b, _ in sizes), sum(a for _, a in sizes)
    print(f"{before / 1048576:.0f} МБ -> {after / 1048576:.0f} МБ (в {before / after:.1f} раза)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
