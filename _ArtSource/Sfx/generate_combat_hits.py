#!/usr/bin/env python3
"""§104 r5 — звуки ПОПАДАНИЯ ближнего боя, через ElevenLabs sound-generation.

Зачем: до этого звука удара по человеку не существовало вовсе. `hit_flesh`
играл только на укус акулы, отрыв конечности, разделку туши и на удар девушки
ПО ВОЛКУ; человеческий бой был слышен одним «вжухом» ЗА СЕКУНДУ-ПОЛТОРЫ до
самого удара. Теперь вид играет удар по хит-штампу (NPCState.HitStampTick), и
ему нужно, чем именно.

Пишет : Assets/StreamingAssets/HexLive/Sfx/<id>_<n>.wav — WAV PCM16 mono 44.1k

  hit_punch_0..3  — глухой удар кулаком по телу
  hit_blade_0..3  — режущий удар клинком по телу

Формат — тот же, что у остальных сэмплов: FmodSfx читает их и через Studio, и
напрямую Core API, а плейлист MultiSound в populate_events.js собирается по
суффиксу _<n>, поэтому нумерация обязана быть подряд с нуля.

Ключ — env ELEVENLABS_API_KEY, в репозитории он не хранится.

Запуск:
  python3 _ArtSource/Sfx/generate_combat_hits.py            # всё, чего нет
  python3 _ArtSource/Sfx/generate_combat_hits.py --ids hit_blade
  python3 _ArtSource/Sfx/generate_combat_hits.py --dry-run  # только план
Существующие файлы пропускаются, пока не сказано --force.
"""
from __future__ import annotations

import argparse
import array
import json
import os
import pathlib
import subprocess
import sys
import wave
from urllib import request as urlrequest, error as urlerror

ROOT = pathlib.Path(__file__).resolve().parents[2]
OUTDIR = ROOT / "Assets/StreamingAssets/HexLive/Sfx"

RATE = 44100
PEAK_DBFS = -1.5          # чуть тише голоса: удар не должен перебивать реплику
TRIM_FLOOR = 0.02
MAX_SECONDS = 1.2         # удар — короткое событие; длинный хвост звучит как эхо
OUTPUT_FORMAT = "mp3_44100_128"
API = ("https://api.elevenlabs.io/v1/sound-generation"
       "?output_format=" + OUTPUT_FORMAT)

# ⭐ Просим ОДИН удар без реверба и без музыки: событие в игре мгновенное, а
# любой хвост читается как эхо помещения — на открытом острове это слышно сразу.
# duration_seconds держим коротким по той же причине.
BANKS = {
    "hit_punch": dict(
        count=4,
        duration=0.9,
        prompt=(
            "single dull bare-knuckle punch impact on a human torso, close "
            "mic, dry studio recording, thud with a soft body slap, no music, "
            "no reverb, no voice, no scream, single hit only"
        ),
    ),
    "hit_blade": dict(
        count=4,
        duration=0.9,
        prompt=(
            "single knife slash cutting into flesh, wet meaty cut with a short "
            "blade whoosh, close mic, dry studio recording, no music, no "
            "reverb, no voice, no scream, single hit only"
        ),
    ),
}


# ---------------------------------------------------------------- audio post

def decode_to_pcm(encoded: bytes) -> bytes:
    """mp3 -> raw PCM16 mono 44100 через ffmpeg (stdin/stdout, без временных файлов)."""
    proc = subprocess.run(
        ["ffmpeg", "-hide_banner", "-loglevel", "error",
         "-i", "pipe:0", "-f", "s16le", "-acodec", "pcm_s16le",
         "-ac", "1", "-ar", str(RATE), "pipe:1"],
        input=encoded, capture_output=True, check=False)
    if proc.returncode != 0 or not proc.stdout:
        raise RuntimeError(
            "ffmpeg decode failed: " + proc.stderr.decode("utf-8", "replace")[:200])
    return proc.stdout


def post_process(pcm: bytes) -> bytes:
    """Обрезать тишину, укоротить, нормализовать. Вход/выход — PCM16 mono.

    Тишину в НАЧАЛЕ режем жёстко: модель любит выдержать паузу перед ударом, а
    в игре звук должен совпасть с кадром попадания, а не прийти позже.
    """
    if not pcm:
        return pcm

    s = array.array("h")
    s.frombytes(pcm[: len(pcm) // 2 * 2])
    if sys.byteorder == "big":
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
    # 5 мс форшлага, чтобы атака не звучала срезанной, и 80 мс хвоста
    start = max(0, start - int(0.005 * RATE))
    end = min(len(s) - 1, end + int(0.080 * RATE))
    s = s[start:end + 1]

    # ⭐ МОМЕНТ УДАРА — В НАЧАЛЕ ФАЙЛА. Модель охотно дописывает перед разрезом
    # замах (whoosh), и у клинков пик уезжал на 0.11-0.34 с. В игре звук
    # запускается ровно в тик попадания, поэтому такой файл прозвучал бы ПОСЛЕ
    # того, как кровь уже брызнула, — и вся синхронность, ради которой всё это
    # делается, пропадает. Замах у нас и так есть отдельным звуком (Swing), и
    # он играет в начале анимации.
    loudest = max(range(len(s)), key=lambda i: abs(s[i]))
    lead = int(0.012 * RATE)
    if loudest > lead:
        s = s[loudest - lead:]

    max_samples = int(MAX_SECONDS * RATE)
    if len(s) > max_samples:
        s = s[:max_samples]
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


def write_wav(path: pathlib.Path, pcm: bytes) -> float:
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm)
    return len(pcm) / 2 / RATE


# ---------------------------------------------------------------- api

def synth(api_key: str, prompt: str, duration: float) -> bytes:
    body = {
        "text": prompt,
        "duration_seconds": duration,
        # 1.0 — держаться подсказки буквально: нам нужен удар, а не «атмосфера
        # драки», которую модель сочиняет при низком значении.
        "prompt_influence": 1.0,
    }
    req = urlrequest.Request(
        API,
        data=json.dumps(body).encode("utf-8"),
        headers={"xi-api-key": api_key, "Content-Type": "application/json"},
        method="POST")
    try:
        with urlrequest.urlopen(req, timeout=180) as response:
            return response.read()
    except urlerror.HTTPError as e:                      # noqa: PERF203
        raise RuntimeError(
            f"ElevenLabs {e.code}: {e.read().decode('utf-8', 'replace')[:300]}") from e


# ---------------------------------------------------------------- main

def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--ids", help="через запятую: hit_punch,hit_blade")
    ap.add_argument("--force", action="store_true", help="перегенерировать существующие")
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    wanted = [i.strip() for i in args.ids.split(",")] if args.ids else list(BANKS)
    unknown = [i for i in wanted if i not in BANKS]
    if unknown:
        print("неизвестные id: " + ", ".join(unknown), file=sys.stderr)
        return 2

    plan = []
    for sound_id in wanted:
        bank = BANKS[sound_id]
        for n in range(bank["count"]):
            path = OUTDIR / f"{sound_id}_{n}.wav"
            if path.exists() and not args.force:
                continue
            plan.append((sound_id, n, path, bank))

    if not plan:
        print("всё уже на месте (--force чтобы перегенерировать)")
        return 0

    print(f"сгенерировать {len(plan)} файл(ов) в {OUTDIR}")
    for sound_id, n, path, _ in plan:
        print(f"  {sound_id}_{n}.wav")

    if args.dry_run:
        return 0

    api_key = os.environ.get("ELEVENLABS_API_KEY")
    if not api_key:
        print("нет ELEVENLABS_API_KEY в окружении", file=sys.stderr)
        return 3

    failures = 0
    for sound_id, n, path, bank in plan:
        try:
            encoded = synth(api_key, bank["prompt"], bank["duration"])
            pcm = post_process(decode_to_pcm(encoded))
            if not pcm:
                raise RuntimeError("после обработки осталась тишина")
            seconds = write_wav(path, pcm)
            print(f"  ✓ {path.name}  {seconds:.2f} с")
        except Exception as exc:                          # noqa: BLE001
            failures += 1
            print(f"  ✗ {sound_id}_{n}: {exc}", file=sys.stderr)

    if failures:
        print(f"{failures} из {len(plan)} не получились", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
