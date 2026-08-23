#!/usr/bin/env python3
"""§67.10 — synthesise the hexkufa voice banks with ElevenLabs.

Reads  : _ArtSource/Voice/hexkufa_lines.json   (built from HEXKUFA_LANGUAGE.md §7)
         _ArtSource/Voice/voices.json          (voice ids + per-character manner)
         env ELEVENLABS_API_KEY                (never stored in the repo)
Writes : Assets/StreamingAssets/HexLive/Sfx/Voices/<char>/
             voice_<char>_<emotion>[_<slug>]_<n>.wav   — WAV PCM16 mono 44.1k

Hard requirements from the game (spec §67.6/§67.7):
  * WAV PCM16 mono 44100 Hz — uLipSync parses the PCM by hand; mp3/ogg = shut mouth
  * silence trimmed, peak normalised to -1 dBFS, length capped at 4.2 s
  * >= 3 variants per group, so a repeated situation does not repeat a sound

Usage:
  python3 _ArtSource/Voice/generate_voices.py                # everything (216 x 5)
  python3 _ArtSource/Voice/generate_voices.py --priority P1   # the playable core
  python3 _ArtSource/Voice/generate_voices.py --chars molly,jana --groups sad_hunger
  python3 _ArtSource/Voice/generate_voices.py --dry-run       # plan only, no API calls
Existing files are skipped unless --force.
"""
from __future__ import annotations

import argparse
import array
import hashlib
import json
import os
import pathlib
import re
import struct
import subprocess
import sys
import time
import wave
from concurrent.futures import ThreadPoolExecutor
from urllib import request as urlrequest, error as urlerror

import bake_lipsync  # §67.7: рядом с каждым WAV печём .vis-таймлайн визем

ROOT = pathlib.Path(__file__).resolve().parents[2]
LINES = ROOT / "_ArtSource/Voice/hexkufa_lines.json"
VOICES = ROOT / "_ArtSource/Voice/voices.json"
OUTDIR = ROOT / "Assets/StreamingAssets/HexLive/Sfx/Voices"

RATE = 44100
MAX_SECONDS = 4.2
PEAK_DBFS = -1.0
TRIM_FLOOR = 0.012          # relative amplitude counted as silence
# Raw pcm_44100 needs an ElevenLabs Pro plan (403 on ours), so we ask for the
# 44.1 kHz mp3 — same sample rate, one lossy hop — and decode it locally with
# ffmpeg into the PCM16 mono 44.1k WAV the game requires (§67.7 lipsync).
OUTPUT_FORMAT = "mp3_44100_128"
API = "https://api.elevenlabs.io/v1/text-to-speech/{vid}?output_format=" + OUTPUT_FORMAT


# ---------------------------------------------------------------- text shaping

VOWEL_RUN = re.compile(r"([aeiouAEIOU])\1{1,}")


def apply_manner(text: str, stretch: float) -> str:
    """HEXKUFA_LANGUAGE.md §5: the words are shared, the manner is not.
    Molly draws vowels out, Marta clips them. Only the DECORATIVE repeats move —
    the word itself is never rewritten, so the catalog stays one catalog."""
    if abs(stretch - 1.0) < 0.01:
        return text

    def repl(m: re.Match) -> str:
        run = len(m.group(0))
        n = max(2, min(8, round(run * stretch)))
        return m.group(1) * n

    return VOWEL_RUN.sub(repl, text)


def strip_tags(text: str) -> str:
    """eleven_v3 reads "[sad]" as a delivery tag; v2 would read it aloud."""
    return re.sub(r"\s*\[[^\]]*\]\s*", " ", text).strip()


def shorten_delivery(text: str, level: int) -> str:
    """Make the SAME line render faster, without rewriting the words.
    v3 ignores voice_settings.speed, so the only levers are the decorative
    vowel runs and the "..." pauses (v3 renders an ellipsis as a real breath).
    level 1 = collapse ellipses to commas; level 2 = also drop runs to 2."""
    if level >= 1:
        text = text.replace("...", ",").replace("..", ",")
        text = re.sub(r",\s*,+", ",", text).replace(" ,", ",")
    if level >= 2:
        text = VOWEL_RUN.sub(lambda m: m.group(1) * 2, text)
    if level >= 3:
        # A two-word tag ("[sad, shivering]") makes v3 add breath and pauses;
        # keeping only the first word gives the same colour, faster.
        text = re.sub(r"^\[([^,\]]+)[^\]]*\]", r"[\1]", text)
    return text.strip()


# ---------------------------------------------------------------- audio post

def decode_to_pcm(encoded: bytes) -> bytes:
    """mp3 bytes -> raw PCM16 mono 44100 via ffmpeg (stdin/stdout, no temp files)."""
    proc = subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error",
         "-i", "pipe:0", "-f", "s16le", "-acodec", "pcm_s16le",
         "-ac", "1", "-ar", str(RATE), "pipe:1"],
        input=encoded, capture_output=True, check=False)
    if proc.returncode != 0 or not proc.stdout:
        raise RuntimeError(f"ffmpeg decode failed: {proc.stderr.decode('utf-8', 'replace')[:200]}")
    return proc.stdout


def post_process(pcm: bytes) -> bytes:
    """Trim silence, cap length, normalise. Input/output: 16-bit mono PCM.
    Pure array/struct — the stdlib audioop module is gone in Python 3.13."""
    if not pcm:
        return pcm

    s = array.array("h")
    s.frombytes(pcm[: len(pcm) // 2 * 2])
    if sys.byteorder == "big":       # ElevenLabs PCM is little-endian
        s.byteswap()

    peak = max((abs(v) for v in s), default=0)
    if peak == 0:
        return b""

    floor = peak * TRIM_FLOOR
    start, end = 0, len(s) - 1
    while start < len(s) and abs(s[start]) < floor:
        start += 1
    while end > start and abs(s[end]) < floor:
        end -= 1
    # keep a 15 ms lead-in / 60 ms tail so plosives and breath survive
    start = max(0, start - int(0.015 * RATE))
    end = min(len(s) - 1, end + int(0.060 * RATE))
    s = s[start : end + 1]

    max_samples = int(MAX_SECONDS * RATE)
    if len(s) > max_samples:
        s = s[:max_samples]
        # fade the last 40 ms so a hard cut doesn't click
        fade = min(int(0.040 * RATE), len(s))
        for i in range(fade):
            idx = len(s) - fade + i
            s[idx] = int(s[idx] * (1.0 - i / fade))

    target = 32767 * (10 ** (PEAK_DBFS / 20.0))
    peak = max((abs(v) for v in s), default=0)
    if peak > 0:
        gain = target / peak
        for i in range(len(s)):
            s[i] = max(-32768, min(32767, int(s[i] * gain)))

    if sys.byteorder == "big":
        s.byteswap()
    return s.tobytes()


def wav_seconds(path: pathlib.Path) -> float:
    try:
        with wave.open(str(path)) as w:
            return w.getnframes() / float(w.getframerate())
    except Exception:  # noqa: BLE001 - unreadable file counts as "regenerate"
        return 0.0


def write_wav(path: pathlib.Path, pcm: bytes) -> float:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm)
    return len(pcm) / 2 / RATE


def ensure_unity_meta(path: pathlib.Path) -> None:
    """StreamingAssets still need committed Unity metas. Use a stable guid so
    headless generation and an open Editor cannot race to invent two ids."""
    if not path.exists():
        return
    meta = pathlib.Path(str(path) + ".meta")
    if meta.exists():
        return
    relative = path.relative_to(ROOT).as_posix()
    guid = hashlib.sha256(("hexlive-voice-meta:" + relative).encode()).hexdigest()[:32]
    meta.write_text(
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "DefaultImporter:\n"
        "  externalObjects: {}\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n",
        encoding="utf-8")


# ---------------------------------------------------------------- api

def synth(api_key: str, voice_id: str, text: str, cfg: dict, model: str, seed: int) -> bytes:
    body = {
        "text": text,
        "model_id": model,
        "voice_settings": {
            "stability": cfg.get("stability", 0.45),
            "similarity_boost": cfg.get("similarity_boost", 0.78),
            "use_speaker_boost": True,
        },
        "seed": seed,
    }
    req = urlrequest.Request(
        API.format(vid=voice_id),
        data=json.dumps(body).encode("utf-8"),
        headers={"xi-api-key": api_key, "Content-Type": "application/json"},
        method="POST",
    )
    with urlrequest.urlopen(req, timeout=120) as resp:
        return resp.read()


def synth_with_fallback(api_key, voice_id, text, cfg, models, seed, log):
    """Try eleven_v3 (audio tags) first; fall back to v2 with the tags stripped."""
    last = None
    for i, model in enumerate(models):
        payload = text if i == 0 else strip_tags(text)
        for attempt in range(3):
            try:
                return synth(api_key, voice_id, payload, cfg, model, seed), model
            except urlerror.HTTPError as e:
                detail = e.read().decode("utf-8", "replace")[:300]
                last = f"{model} HTTP {e.code}: {detail}"
                if e.code in (429, 500, 502, 503):
                    time.sleep(2 + attempt * 3)
                    continue
                break
            except Exception as e:  # noqa: BLE001 - network flake
                last = f"{model}: {e}"
                time.sleep(2 + attempt * 3)
        log(f"    model {model} failed -> {last}")
    raise RuntimeError(last or "synthesis failed")


# ---------------------------------------------------------------- main

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--priority", default=None, help="only groups of this priority (P1/P2/P3)")
    ap.add_argument("--chars", default=None, help="comma-separated subset of characters")
    ap.add_argument("--groups", default=None, help="comma-separated subset of group ids")
    ap.add_argument("--force", action="store_true", help="re-generate existing files")
    ap.add_argument("--dry-run", action="store_true")
    # Starter tier allows 3 concurrent requests; 4 earns HTTP 429.
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--fix-capped", action="store_true",
                    help="re-render only the takes that hit the 4.2 s cap "
                         "(they are cut mid-word) with a faster delivery")
    args = ap.parse_args()

    api_key = os.environ.get("ELEVENLABS_API_KEY", "").strip()
    if not api_key and not args.dry_run:
        print("ELEVENLABS_API_KEY is not set", file=sys.stderr)
        return 2

    catalog = json.loads(LINES.read_text(encoding="utf-8"))
    voices = json.loads(VOICES.read_text(encoding="utf-8"))
    models = [voices.get("model", "eleven_v3"), voices.get("model_fallback", "eleven_multilingual_v2")]

    chars = {k: v for k, v in voices["characters"].items()
             if not args.chars or k in args.chars.split(",")}
    groups = [g for g in catalog["groups"]
              if (not args.priority or g["priority"] == args.priority)
              and (not args.groups or g["id"] in args.groups.split(","))]

    jobs = []
    targets = []
    for char, cfg in chars.items():
        for g in groups:
            for n, line in enumerate(g["lines"]):
                path = OUTDIR / char / f"voice_{char}_{g['id']}_{n}.wav"
                targets.append(path)
                if args.fix_capped:
                    # Only the takes that ran into the cap — they end mid-word.
                    if not path.exists() or wav_seconds(path) < MAX_SECONDS - 0.01:
                        continue
                elif path.exists() and not args.force:
                    continue
                jobs.append((char, cfg, g, n, line, path))

    print(f"characters={len(chars)} groups={len(groups)} files to make={len(jobs)}")
    if args.dry_run:
        for char, _cfg, g, n, line, path in jobs[:10]:
            print(f"  {path.relative_to(ROOT)}  <-  {line['text']}")
        print("  …" if len(jobs) > 10 else "")
        return 0

    lock_out = []
    def log(msg):
        lock_out.append(msg)
        print(msg, flush=True)

    failures, made = [], []

    def run(job):
        char, cfg, g, n, line, path = job
        stretch = cfg.get("vowel_stretch", 1.0)
        base_seed = cfg.get("seed", 1) + n
        # Escalating attempts: a plain take, then progressively faster delivery
        # with a fresh seed. Only used by --fix-capped; the first attempt IS the
        # normal path, so a fresh bank is generated exactly as before.
        attempts = [(line["text"], stretch, base_seed)] if not args.fix_capped else [
            (shorten_delivery(line["text"], 1), stretch * 0.7, base_seed + 500),
            (shorten_delivery(line["text"], 1), stretch * 0.5, base_seed + 900),
            (shorten_delivery(line["text"], 2), 0.0, base_seed + 1300),
            (shorten_delivery(line["text"], 3), 0.0, base_seed + 1700),
        ]

        best = None
        last_error = None
        for raw, stretch_i, seed in attempts:
            text = apply_manner(raw, stretch_i) if stretch_i else raw
            try:
                encoded, model = synth_with_fallback(
                    api_key, cfg["voice_id"], text, cfg, models, seed, log)
                pcm = post_process(decode_to_pcm(encoded))
                if len(pcm) < int(0.25 * RATE) * 2:
                    raise RuntimeError(f"too short/silent after trim ({len(pcm)} bytes)")
                secs = len(pcm) / 2 / RATE
                if best is None or secs < best[0]:
                    best = (secs, pcm, model)
                # Under the cap with room to spare = a clean, unclipped take.
                if secs < MAX_SECONDS - 0.05:
                    break
            except Exception as e:  # noqa: BLE001
                last_error = e

        if best is None:
            failures.append((path.name, str(last_error)))
            log(f"  FAIL {path.name}: {last_error}")
            return

        secs = write_wav(path, best[1])
        # Липсинк-таймлайн (.vis) обязан обновляться вместе с WAV — рантайм
        # сверяет sourceSamples и молча выключит губы при рассинхроне.
        bake_lipsync.bake_file(path)
        made.append((path, secs, best[2]))
        flag = " STILL CAPPED" if secs >= MAX_SECONDS - 0.01 else ""
        log(f"  ok {path.name} {secs:.2f}s [{best[2]}]{flag}")

    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        list(pool.map(run, jobs))

    for path in targets:
        ensure_unity_meta(path)
        ensure_unity_meta(path.with_suffix(".vis"))

    print(f"\nmade={len(made)} failed={len(failures)}")
    if made:
        longest = max(made, key=lambda m: m[1])
        print(f"longest: {longest[0].name} {longest[1]:.2f}s")
    for name, err in failures[:20]:
        print(f"  FAILED {name}: {err}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
