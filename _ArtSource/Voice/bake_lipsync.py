#!/usr/bin/env python3
"""Офлайн-бейк таймлайнов визем (.vis) из голосовых WAV — spec §67.7.

Точный порт DSP-цепочки uLipSync (LipSyncJob/Algorithm), которой раньше
рантайм считал MFCC на каждый кадр. Теперь анализ выполняется один раз,
здесь, а игра лишь сэмплирует готовый таймлайн по позиции FMOD-канала.

⭐ Квирки оригинала воспроизведены СОЗНАТЕЛЬНО — они и есть звучание
   откалиброванного профиля, «чинить» их нельзя:
   - «фильтр НЧ» ПРИБАВЛЯЕТСЯ к сигналу (out = x + FIR(x)), причинный
     (Algorithm.cs:119);
   - даунсэмпл 44100→16000 — nearest-floor: лерп вырожден багом
     `i1 = min(i0, len-1)` (Algorithm.cs:171);
   - нормализация пика — ПОСЛЕ окна Хэмминга (LipSyncJob.cs:48-50);
   - в тишине ratios нулевые, рот закрывает volume.

Формат .vis (little-endian), кладётся рядом с WAV:
    u32  magic 'HXLS'
    u16  version = 1
    u8   visemeCount = 14 (порядок фонем = lipsync_profile.json = PhonemeMap)
    u8   fps = 60
    u16  frameCount
    u32  sourceSamples (сэмплов в data-чанке WAV — проверка свежести)
    затем frameCount кадров по (visemeCount + 1) байт:
    ratio каждой виземы 0..255, последним — volume 0..255.
Первый и последний кадры гарантированно нулевые (рот закрыт до и после).

CLI:
    python3 _ArtSource/Voice/bake_lipsync.py            # весь банк, инкрементально
    python3 _ArtSource/Voice/bake_lipsync.py --force    # пересобрать всё
    python3 _ArtSource/Voice/bake_lipsync.py path/to/voice_x_y_0.wav
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
import wave
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[2]
DEFAULT_PROFILE = Path(__file__).resolve().parent / "lipsync_profile.json"
DEFAULT_VOICES_DIR = REPO / "Assets/StreamingAssets/HexLive/Sfx/Voices"

MAGIC = b"HXLS"
VERSION = 1
EPSILON = 1.1920929e-07  # Unity.Mathematics math.EPSILON


class LipSyncDsp:
    """Порт LipSyncJob.Execute: окно PCM 44.1k → (ratios[14], volume)."""

    def __init__(self, profile: dict):
        self.src_rate = int(profile["sourceSampleRate"])
        self.target_rate = int(profile["targetSampleRate"])
        self.sample_count = int(profile["sampleCount"])
        self.mel_channels = int(profile["melFilterBankChannels"])
        self.mfcc_num = int(profile["mfccNum"])
        self.min_volume = float(profile["minVolume"])
        self.max_volume = float(profile["maxVolume"])
        self.fps = int(profile["fps"])
        self.phoneme_names = [p["name"] for p in profile["phonemes"]]
        self.templates = np.array(
            [p["mfcc"] for p in profile["phonemes"]], dtype=np.float64)

        # inputSampleCount (uLipSync.cs:75-83): окно в исходной частоте.
        ratio = self.src_rate / self.target_rate
        self.window_samples = int(np.ceil(self.sample_count * ratio))

        # FIR-ядро «фильтра» (Algorithm.LowPassFilter, float32-арифметика).
        cutoff = np.float32((self.target_rate / 2 - 500) / self.src_rate)
        rng = np.float32(500 / self.src_rate)
        n = int(round(np.float32(3.1) / rng))
        if (n + 1) % 2 == 0:
            n += 1
        i = np.arange(n, dtype=np.float32)
        x = i - np.float32((n - 1) / 2.0)
        ang = np.float32(2 * np.pi) * cutoff * x
        self.fir = (2 * cutoff * np.sin(ang) / ang).astype(np.float64)

        # Индексы nearest-floor даунсэмпла (DownSample2 с вырожденным лерпом).
        df = self.src_rate / self.target_rate
        out_len = int(round(self.window_samples / df))
        self.ds_index = np.floor(df * np.arange(out_len)).astype(np.int64)

        # Окно Хэмминга по длине ПОСЛЕ даунсэмпла.
        m = np.arange(out_len, dtype=np.float64)
        self.hamming = 0.54 - 0.46 * np.cos(2 * np.pi * m / (out_len - 1))

        # Мел-банк (Algorithm.MelFilterBank): матрица весов 26×(N/2+1).
        f_max = self.target_rate / 2
        mel_max = 1127.0 * np.log(1.0 + f_max / 700.0)
        n_max = out_len // 2
        dfreq = f_max / n_max
        d_mel = mel_max / (self.mel_channels + 1)
        weights = np.zeros((self.mel_channels, n_max + 1), dtype=np.float64)
        to_hz = lambda mel: 700.0 * (np.exp(mel / 1127.0) - 1.0)
        for ch in range(self.mel_channels):
            f_begin = to_hz(d_mel * ch)
            f_center = to_hz(d_mel * (ch + 1))
            f_end = to_hz(d_mel * (ch + 2))
            i_begin = int(np.ceil(f_begin / dfreq))
            i_center = int(round(f_center / dfreq))
            i_end = int(np.floor(f_end / dfreq))
            for k in range(i_begin + 1, i_end + 1):
                f = dfreq * k
                a = ((f - f_begin) / (f_center - f_begin) if k < i_center
                     else (f_end - f) / (f_end - f_center))
                weights[ch, k] = a / ((f_end - f_begin) * 0.5)
        self.mel_weights = weights

        # DCT-II без нормировки (Algorithm.DCT), берём строки 1..mfccNum.
        j = np.arange(self.mel_channels, dtype=np.float64)
        rows = np.arange(1, self.mfcc_num + 1, dtype=np.float64)
        self.dct = np.cos(
            np.outer(rows, (j + 0.5)) * (np.pi / self.mel_channels))

    def analyze_window(self, window: np.ndarray) -> tuple[np.ndarray, float]:
        """window: float32[window_samples] @44.1k → (ratios[14], volume 0..1)."""
        rms = float(np.sqrt(np.mean(window.astype(np.float64) ** 2)))

        peak = float(np.max(np.abs(window))) if window.size else 0.0
        if peak < EPSILON:
            # Нулевое окно: в C# log10(0) каскадом обнуляет и ratios, и volume.
            return np.zeros(len(self.templates)), 0.0

        x = window.astype(np.float64)
        # «LPF»: сигнал + причинная свёртка (data[i] += Σ b[j]·tmp[i-j]).
        x = x + np.convolve(x, self.fir)[: x.size]
        x = x[self.ds_index]                     # nearest-floor 44100→16000
        y = np.empty_like(x)                     # pre-emphasis 0.97
        y[0] = x[0]
        y[1:] = x[1:] - 0.97 * x[:-1]
        y *= self.hamming                        # окно
        peak = np.max(np.abs(y))                 # normalize ПОСЛЕ окна
        if peak >= EPSILON:
            y = y / peak
        spectrum = np.abs(np.fft.rfft(y))        # magnitude, бины 0..N/2
        mel = self.mel_weights @ spectrum
        with np.errstate(divide="ignore"):
            mel_db = 10.0 * np.log10(mel)
        if not np.all(np.isfinite(mel_db)):
            # Пустой мел-канал → в C# те же -inf/NaN гасят скоры в нули.
            return np.zeros(len(self.templates)), 0.0
        mfcc = self.dct @ mel_db

        # L2-скоринг (means=0, std=1): d = sqrt(mean((mfcc-tmpl)^2)).
        d = np.sqrt(np.mean((mfcc - self.templates) ** 2, axis=1))
        scores = np.power(10.0, -d)
        total = scores.sum()
        ratios = scores / total if total > 0 else np.zeros_like(scores)

        # Volume-таргет — формула uLipSyncBlendShape.UpdateVolume.
        if rms <= 0.0:
            volume = 0.0
        else:
            volume = (np.log10(rms) - self.min_volume) / max(
                self.max_volume - self.min_volume, 1e-4)
            volume = float(np.clip(volume, 0.0, 1.0))
        return ratios, volume


def read_wav_mono16(path: Path) -> np.ndarray:
    with wave.open(str(path), "rb") as w:
        if (w.getnchannels(), w.getsampwidth(), w.getframerate()) != (1, 2, 44100):
            raise ValueError(
                f"{path.name}: ожидался WAV PCM16 mono 44.1k, получено "
                f"{w.getnchannels()}ch {w.getsampwidth()*8}bit {w.getframerate()}Hz")
        raw = w.readframes(w.getnframes())
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0


def bake_samples(samples: np.ndarray, dsp: LipSyncDsp) -> bytes:
    """PCM → готовое содержимое .vis."""
    n_samples = samples.size
    duration = n_samples / dsp.src_rate
    frame_count = int(np.ceil(duration * dsp.fps)) + 1
    padded = np.concatenate(
        [np.zeros(dsp.window_samples, dtype=np.float32), samples])

    viseme_count = len(dsp.templates)
    frames = bytearray()
    for t in range(frame_count):
        pos = min(int(round(t / dsp.fps * dsp.src_rate)), n_samples)
        window = padded[pos : pos + dsp.window_samples]
        if t == frame_count - 1:
            ratios, volume = np.zeros(viseme_count), 0.0  # рот закрыт в конце
        else:
            ratios, volume = dsp.analyze_window(window)
        frames += bytes(int(round(r * 255)) for r in ratios)
        frames.append(int(round(volume * 255)))

    header = struct.pack(
        "<4sHBBHI", MAGIC, VERSION, viseme_count, dsp.fps,
        frame_count, n_samples)
    return header + bytes(frames)


def validate(blob: bytes, path: Path, dsp: LipSyncDsp) -> list[str]:
    """Дешёвые инварианты бейка; список нарушений (пустой = ок)."""
    problems = []
    magic, version, vis, fps, frame_count, src_samples = struct.unpack_from(
        "<4sHBBHI", blob)
    stride = vis + 1
    frames = np.frombuffer(blob[14:], dtype=np.uint8).reshape(frame_count, stride)

    if frames[0].any():
        problems.append("первый кадр не нулевой")
    if frames[-1].any():
        problems.append("последний кадр не нулевой")
    ratio_sums = frames[:, :vis].astype(np.int32).sum(axis=1)
    bad = ~((ratio_sums == 0) | (np.abs(ratio_sums - 255) <= vis))
    if bad.any():
        problems.append(f"сумма ratio вне нормы в {int(bad.sum())} кадрах")
    expected = int(np.ceil(src_samples / dsp.src_rate * fps)) + 1
    if frame_count != expected:
        problems.append(f"frameCount {frame_count} != ожидаемых {expected}")
    duration = src_samples / dsp.src_rate
    if duration > 0.5:
        voiced = float((frames[:, vis] > 0).mean())
        if voiced < 0.2:
            problems.append(f"озвучено лишь {voiced:.0%} кадров — декод-бага?")
    return [f"{path.name}: {p}" for p in problems]


def vis_path_for(wav: Path) -> Path:
    return wav.with_suffix(".vis")


def is_up_to_date(wav: Path, vis: Path) -> bool:
    if not vis.exists() or vis.stat().st_mtime < wav.stat().st_mtime:
        return False
    try:
        with vis.open("rb") as f:
            head = f.read(14)
        magic, version, _, _, _, src_samples = struct.unpack("<4sHBBHI", head)
        with wave.open(str(wav), "rb") as w:
            return (magic == MAGIC and version == VERSION
                    and src_samples == w.getnframes())
    except Exception:
        return False


_WORKER_DSP: LipSyncDsp | None = None


def _init_worker(profile: dict) -> None:
    global _WORKER_DSP
    _WORKER_DSP = LipSyncDsp(profile)


def _bake_one(wav_str: str) -> tuple[str, list[str]]:
    wav = Path(wav_str)
    dsp = _WORKER_DSP
    assert dsp is not None
    samples = read_wav_mono16(wav)
    blob = bake_samples(samples, dsp)
    problems = validate(blob, wav, dsp)
    vis_path_for(wav).write_bytes(blob)
    return wav.name, problems


_DSP_CACHE: dict[str, LipSyncDsp] = {}


def bake_file(wav: Path, profile_path: Path = DEFAULT_PROFILE) -> Path:
    """Запечь один WAV (используется generate_voices.py). Возвращает путь .vis."""
    key = str(profile_path)
    dsp = _DSP_CACHE.get(key)
    if dsp is None:
        dsp = _DSP_CACHE[key] = LipSyncDsp(json.loads(Path(profile_path).read_text()))
    samples = read_wav_mono16(Path(wav))
    blob = bake_samples(samples, dsp)
    problems = validate(blob, Path(wav), dsp)
    for p in problems:
        print(f"  ⚠ {p}", file=sys.stderr)
    out = vis_path_for(Path(wav))
    out.write_bytes(blob)
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("paths", nargs="*", type=Path,
                    help="WAV-файлы или директории (дефолт — весь банк голосов)")
    ap.add_argument("--profile", type=Path, default=DEFAULT_PROFILE)
    ap.add_argument("--force", action="store_true",
                    help="пересобрать даже актуальные сайдкары")
    ap.add_argument("--jobs", type=int, default=0,
                    help="процессов (0 = по числу ядер)")
    args = ap.parse_args()

    profile = json.loads(args.profile.read_text())
    dsp = LipSyncDsp(profile)

    roots = args.paths or [DEFAULT_VOICES_DIR]
    wavs: list[Path] = []
    for root in roots:
        if root.is_dir():
            wavs += sorted(root.rglob("voice_*.wav"))
        elif root.suffix.lower() == ".wav":
            wavs.append(root)
        else:
            ap.error(f"не WAV и не директория: {root}")
    if not wavs:
        print("WAV-файлов не найдено", file=sys.stderr)
        return 1

    todo = [w for w in wavs
            if args.force or not is_up_to_date(w, vis_path_for(w))]
    print(f"файлов: {len(wavs)}, к запеканию: {len(todo)}")
    if not todo:
        return 0

    all_problems: list[str] = []
    workers = args.jobs if args.jobs > 0 else None
    if len(todo) == 1:
        _init_worker(profile)
        done = [_bake_one(str(todo[0]))]
    else:
        with ProcessPoolExecutor(
                max_workers=workers, initializer=_init_worker,
                initargs=(profile,)) as pool:
            done = list(pool.map(_bake_one, [str(w) for w in todo],
                                 chunksize=8))
    for _, problems in done:
        all_problems += problems

    print(f"запечено: {len(done)}")
    if all_problems:
        print(f"⚠ нарушений инвариантов: {len(all_problems)}", file=sys.stderr)
        for p in all_problems[:40]:
            print(f"  ⚠ {p}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
