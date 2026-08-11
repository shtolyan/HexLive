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
DEFAULT_LINES = Path(__file__).resolve().parent / "hexkufa_lines.json"
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

    def frame_features(self, window: np.ndarray) -> tuple[np.ndarray | None, float]:
        """window @44.1k → (L2-дистанции до 14 шаблонов | None для тишины,
        volume 0..1). Общий низ для обоих алгоритмов бейка."""
        rms = float(np.sqrt(np.mean(window.astype(np.float64) ** 2)))
        if rms <= 0.0:
            volume = 0.0
        else:
            volume = (np.log10(rms) - self.min_volume) / max(
                self.max_volume - self.min_volume, 1e-4)
            volume = float(np.clip(volume, 0.0, 1.0))

        peak = float(np.max(np.abs(window))) if window.size else 0.0
        if peak < EPSILON:
            return None, 0.0
        d = self._mfcc_distances(window)
        if d is None:
            return None, 0.0
        return d, volume

    def analyze_window(self, window: np.ndarray) -> tuple[np.ndarray, float]:
        """window: float32[window_samples] @44.1k → (ratios[14], volume 0..1).
        Алгоритм «acoustic» — порт рантайм-классификации uLipSync."""
        d, volume = self.frame_features(window)
        if d is None:
            # Нулевое окно: в C# log10(0) каскадом обнуляет и ratios, и volume.
            return np.zeros(len(self.templates)), 0.0
        scores = np.power(10.0, -d)
        total = scores.sum()
        ratios = scores / total if total > 0 else np.zeros_like(scores)
        return ratios, volume

    def _mfcc_distances(self, window: np.ndarray) -> np.ndarray | None:
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
            return None
        mfcc = self.dct @ mel_db

        # L2-дистанции (means=0, std=1): d = sqrt(mean((mfcc-tmpl)^2)).
        return np.sqrt(np.mean((mfcc - self.templates) ** 2, axis=1))


# ---------------------------------------------------------------- align
# Алгоритм «align» (дефолт): буквы реплики ИЗВЕСТНЫ (хекскуфа, §67.9),
# распознавать нечего — нужно лишь расставить их по времени. g2p переводит
# текст в последовательность визем, Витерби выравнивает её по аудио
# (эмиссии = те же MFCC-дистанции + громкость для тишины). В отличие от
# покадровой классификации (алгоритм «acoustic», бывший uLipSync), даёт
# чёткие сегменты в правильном порядке вместо каши «всё по 20%».

SIL = -1

# Порядок визем = порядок фонем профиля = индексы в .vis = PhonemeMap рантайма.
VISEME_NAMES = ["A", "I", "U", "E", "O", "P", "F", "S", "SH", "T", "R", "L",
                "K", "TH"]

# Маппинг букв живёт в letter_visemes.json (его редактирует
# Tools/lipsync_editor.py). "skip" = буква не формирует рот.
DEFAULT_LETTER_MAP = Path(__file__).resolve().parent / "letter_visemes.json"

# Неизвестные буквы, встреченные при g2p за этот запуск, — для сводки.
UNKNOWN_LETTERS: set[str] = set()


def load_letter_map(path: Path = DEFAULT_LETTER_MAP) -> tuple[dict, dict]:
    """letter_visemes.json → ({буква: индекс|None}, {диграф: индекс|None})."""
    data = json.loads(Path(path).read_text())

    def to_index(name: str) -> int | None:
        if name == "skip":
            return None
        return VISEME_NAMES.index(name)

    letters = {k: to_index(v) for k, v in data["letters"].items()}
    digraphs = {k: to_index(v) for k, v in data["digraphs"].items()}
    return letters, digraphs


_LETTER_MAP: tuple[dict, dict] | None = None


def _letter_map() -> tuple[dict, dict]:
    global _LETTER_MAP
    if _LETTER_MAP is None:
        _LETTER_MAP = load_letter_map()
    return _LETTER_MAP

_VOWELS = frozenset({0, 1, 2, 3, 4})  # A I U E O


def g2p_words(text: str,
              letter_map: tuple[dict, dict] | None = None
              ) -> list[list[tuple[int, bool]]]:
    """Текст реплики → слова как последовательности (индекс виземы, long).
    Повторы буквы схлопываются в один юнит; повтор ≥3 (долгота «Piiiisko»,
    manner-растяжки) помечается long — такому юниту не ограничиваем
    длительность. Поэтому точная строка синтеза (vowel_stretch,
    --fix-capped) не важна."""
    import re
    letters, digraphs = letter_map if letter_map is not None else _letter_map()
    clean = re.sub(r"\[.*?\]", " ", text.lower())
    words: list[list[tuple[int, bool]]] = []
    for raw in re.split(r"[^a-z]+", clean):
        if not raw:
            continue
        units: list[tuple[int, bool]] = []
        i = 0
        while i < len(raw):
            two = raw[i : i + 2]
            if two in digraphs:
                vis = digraphs[two]
                i += 2
            elif raw[i] in letters:
                vis = letters[raw[i]]
                i += 1
            else:
                # Буквы нет в letter_visemes.json — рот её не сыграет.
                UNKNOWN_LETTERS.add(raw[i])
                vis = None
                i += 1
            if vis is None:
                continue
            if units and units[-1][0] == vis:
                units[-1] = (vis, True)  # повтор буквы = долгий юнит
            else:
                units.append((vis, False))
        if units:
            words.append(units)
    return words


class _State:
    __slots__ = ("vis", "optional", "min_frames", "max_frames")

    def __init__(self, vis: int, optional: bool, min_frames: int,
                 max_frames: int | None):
        self.vis = vis
        self.optional = optional
        self.min_frames = min_frames
        self.max_frames = max_frames  # None = не ограничена


# Ручки выравнивания. Дистанции стандартизуются по файлу, поэтому масштаб
# стабилен: 0 = средняя похожесть, ±1 = сигма.
_SIL_LOUD_COST = 3.0      # тишина на громком кадре — дорого
_PHONE_QUIET_COST = 2.5   # фонема на тихом кадре — дорого
_QUIET_VOL = 0.10         # ниже этой громкости кадр «тихий»
_ABANDON_COST = 4.0       # штраф за каждый недопетый юнит (обрезанные WAV)

# Длительности в кадрах 60 fps (кадр ≈ 16.7 мс). Шаблоны MFCC — слабое
# свидетельство, поэтому длительности несут половину работы: смычные
# коротки, фрикативы среднее, гласные — минимум 67 мс и без потолка
# (долготу решает Витерби по остатку слова).
_VOWEL_MIN = 4
_CONSONANT_MIN = 2
# Громкий кадр — почти наверняка гласная: скидка эмиссии гласных состояний
# пропорционально громкости. Без неё согласные (шаблоны слабые) перетягивали
# время, и гласные массово сидели в минимальной длительности — ровно то
# «теряются гласные», что видно глазом в пиано-ролле.
_VOWEL_LOUD_BONUS = 0.6
_CONSONANT_MAX = {
    5: 8, 9: 8, 12: 8,        # P T K — смычные/носовые, ≤133 мс
    10: 10, 11: 10,           # R L — сонорные, ≤167 мс
    6: 12, 7: 12, 8: 12, 13: 12,  # F S SH TH — фрикативы, ≤200 мс
}


def build_states(words: list[list[tuple[int, bool]]]) -> list[_State]:
    """Слова → цепочка состояний: [SIL?] w1 [SIL?] w2 … [SIL?].
    Гласные и долгие юниты без верхней границы; согласные ограничены —
    иначе «m» (закрытые губы) присваивает себе соседние гласные."""
    states: list[_State] = [_State(SIL, True, 1, None)]
    for word in words:
        for vis, is_long in word:
            if vis in _VOWELS:
                states.append(_State(vis, False, _VOWEL_MIN, None))
            elif is_long:
                states.append(_State(vis, False, _CONSONANT_MIN, None))
            else:
                states.append(_State(
                    vis, False, _CONSONANT_MIN, _CONSONANT_MAX[vis]))
        states.append(_State(SIL, True, 1, None))  # межсловные паузы
    return states


def viterbi_align(dists: np.ndarray, vols: np.ndarray,
                  states: list[_State]) -> list[int] | None:
    """dists[f,14] (стандартизованные, NaN на тихих кадрах), vols[f] →
    индекс состояния на кадр. None = выравнивание не сошлось."""
    frames = len(vols)
    n_states = len(states)

    # Суб-состояния кодируют длительность: цепочка длиной max (или min для
    # неограниченных, у которых последний суб-стейт зациклен). Выход к
    # следующему состоянию разрешён с позиции ≥ min-1.
    sub_state: list[int] = []
    sub_pos: list[int] = []
    sub_entry: list[int] = []
    for s, st in enumerate(states):
        sub_entry.append(len(sub_state))
        chain = st.min_frames if st.max_frames is None else st.max_frames
        for p in range(chain):
            sub_state.append(s)
            sub_pos.append(p)
    n_sub = len(sub_state)

    # Эмиссии по состояниям (одинаковы для всех суб-позиций).
    emit_state = np.zeros((frames, n_states))
    quiet = _PHONE_QUIET_COST * np.clip((_QUIET_VOL - vols) / _QUIET_VOL, 0.0, 1.0)
    for s, st in enumerate(states):
        if st.vis == SIL:
            emit_state[:, s] = _SIL_LOUD_COST * vols
        else:
            base = np.where(np.isnan(dists[:, st.vis]), 2.0, dists[:, st.vis])
            emit_state[:, s] = base + quiet
            if st.vis in _VOWELS:
                emit_state[:, s] -= _VOWEL_LOUD_BONUS * vols
    emit = emit_state[:, sub_state]

    # Переходы: из суб-стейта j — остаться (только последний суб-стейт
    # неограниченного состояния), шаг по цепочке, выход к входам следующих
    # состояний (через optional-пропуски).
    next_entries: list[list[int]] = []
    for s in range(n_states):
        outs = []
        t = s + 1
        while t < n_states:
            outs.append(sub_entry[t])
            if not states[t].optional:
                break
            t += 1
        next_entries.append(outs)

    can_stay = np.zeros(n_sub, dtype=bool)
    step_next: list[int | None] = [None] * n_sub
    exits: list[list[int]] = [[] for _ in range(n_sub)]
    for j in range(n_sub):
        s = sub_state[j]
        st = states[s]
        chain = st.min_frames if st.max_frames is None else st.max_frames
        last = sub_pos[j] == chain - 1
        if last and st.max_frames is None:
            can_stay[j] = True
        if not last:
            step_next[j] = j + 1
        if sub_pos[j] >= st.min_frames - 1:
            exits[j] = next_entries[s]

    INF = 1e18
    cost = np.full(n_sub, INF)
    back: list[np.ndarray] = []

    starts = [sub_entry[0]]
    t = 1
    while t < n_states and states[t - 1].optional:
        starts.append(sub_entry[t])
        t += 1
    for j in starts:
        cost[j] = emit[0, j]

    for f in range(1, frames):
        new = np.full(n_sub, INF)
        arg = np.full(n_sub, -1, dtype=np.int64)
        for j in range(n_sub):
            c = cost[j]
            if c >= INF:
                continue
            if can_stay[j] and c < new[j]:
                new[j] = c
                arg[j] = j
            k = step_next[j]
            if k is not None and c < new[k]:
                new[k] = c
                arg[k] = j
            for k in exits[j]:
                if c < new[k]:
                    new[k] = c
                    arg[k] = j
        new += emit[f]
        cost = new
        back.append(arg)

    # Финал: конец цепочки или ранний обрыв со штрафом за недопетое.
    best_j, best_cost = -1, INF
    for j in range(n_sub):
        if cost[j] >= INF:
            continue
        s = sub_state[j]
        remaining = sum(
            1 for t in range(s + 1, n_states) if not states[t].optional)
        if states[s].vis != SIL and sub_pos[j] < states[s].min_frames - 1:
            remaining += 1  # оборвались, не допев фонему
        total = cost[j] + _ABANDON_COST * remaining
        if total < best_cost:
            best_cost, best_j = total, j

    if best_j < 0:
        return None

    path = [best_j]
    for arg in reversed(back):
        prev = arg[path[-1]]
        if prev < 0:
            return None
        path.append(int(prev))
    path.reverse()
    return [sub_state[j] for j in path]


# Ручные правки таймлайнов из пиано-ролла (Tools/lipsync_editor.py).
# Ключ — "<char>/<file>.wav", значение — {"sourceSamples": N,
# "segments": [[висема, startMs, endMs], ...]}. Бейкер уважает оверрайд,
# пока WAV не перегенерирован (sourceSamples совпадает) — иначе правка
# устарела: предупреждаем и выравниваем заново.
DEFAULT_OVERRIDES = Path(__file__).resolve().parent / "lipsync_overrides.json"


def load_overrides(path: Path = DEFAULT_OVERRIDES) -> dict:
    p = Path(path)
    return json.loads(p.read_text()) if p.exists() else {}


def override_key(wav: Path) -> str:
    return f"{wav.parent.name}/{wav.name}"


def _volume_curve(samples: np.ndarray, dsp: LipSyncDsp,
                  frame_count: int) -> np.ndarray:
    """Кривая volume 0..1 по кадрам — формула uLipSyncBlendShape по rms."""
    sq = np.concatenate([np.zeros(dsp.window_samples),
                         samples.astype(np.float64) ** 2])
    cum = np.concatenate([[0.0], np.cumsum(sq)])
    vols = np.zeros(frame_count)
    w = dsp.window_samples
    n = samples.size
    for t in range(frame_count - 1):
        pos = min(int(round(t / dsp.fps * dsp.src_rate)), n)
        rms = float(np.sqrt((cum[pos + w] - cum[pos]) / w))
        if rms > 0.0:
            v = (np.log10(rms) - dsp.min_volume) / max(
                dsp.max_volume - dsp.min_volume, 1e-4)
            vols[t] = float(np.clip(v, 0.0, 1.0))
    return vols


def bake_from_segments(samples: np.ndarray, dsp: LipSyncDsp,
                       segments: list) -> bytes:
    """Ручные сегменты [[висема, startMs, endMs], …] → содержимое .vis."""
    n_samples = samples.size
    frame_count = int(np.ceil(n_samples / dsp.src_rate * dsp.fps)) + 1
    viseme_count = len(dsp.templates)
    vols = _volume_curve(samples, dsp, frame_count)

    ratios = np.zeros((frame_count, viseme_count))
    for vis, start_ms, end_ms in segments:
        f0 = max(0, int(round(start_ms * dsp.fps / 1000.0)))
        f1 = min(frame_count - 1, int(round(end_ms * dsp.fps / 1000.0)))
        if 0 <= vis < viseme_count and f1 > f0:
            ratios[f0:f1] = 0.0
            ratios[f0:f1, vis] = _ALIGN_GAIN

    sm = ratios.copy()
    sm[1:-1] = 0.25 * ratios[:-2] + 0.5 * ratios[1:-1] + 0.25 * ratios[2:]
    ratios = sm
    ratios[0] = 0.0
    ratios[-1] = 0.0
    vols[-1] = 0.0

    frames = bytearray()
    for t in range(frame_count):
        frames += bytes(int(round(r * 255)) for r in ratios[t])
        frames.append(int(round(float(vols[t]) * 255)))
    header = struct.pack("<4sHBBHI", MAGIC, VERSION, viseme_count, dsp.fps,
                         frame_count, n_samples)
    return header + bytes(frames)


def load_line_texts(path: Path = DEFAULT_LINES) -> dict[str, list[str]]:
    """hexkufa_lines.json → {group_id: [text варианта 0, 1, 2]}."""
    data = json.loads(Path(path).read_text())
    return {g["id"]: [ln["text"] for ln in g["lines"]] for g in data["groups"]}


def words_for_wav(wav: Path, texts: dict[str, list[str]]) -> list[list[int]] | None:
    """voice_<char>_<group>_<n>.wav → виземы известного текста (None = нет)."""
    char = wav.parent.name.lower()
    stem = wav.stem
    prefix = f"voice_{char}_"
    if not stem.startswith(prefix) or "_" not in stem[len(prefix):]:
        return None
    rest = stem[len(prefix):]
    group, _, variant = rest.rpartition("_")
    if not variant.isdigit():
        return None
    lines = texts.get(group)
    if lines is None or int(variant) >= len(lines):
        return None
    words = g2p_words(lines[int(variant)])
    return words or None


def read_wav_mono16(path: Path) -> np.ndarray:
    with wave.open(str(path), "rb") as w:
        if (w.getnchannels(), w.getsampwidth(), w.getframerate()) != (1, 2, 44100):
            raise ValueError(
                f"{path.name}: ожидался WAV PCM16 mono 44.1k, получено "
                f"{w.getnchannels()}ch {w.getsampwidth()*8}bit {w.getframerate()}Hz")
        raw = w.readframes(w.getnframes())
    return np.frombuffer(raw, dtype=np.int16).astype(np.float32) / 32768.0


# Пиковая сила виземы при выравнивании: полные 100 у Daz-визем — гротеск,
# 0.8 читается как выразительная, но живая артикуляция.
_ALIGN_GAIN = 0.8


def bake_samples(samples: np.ndarray, dsp: LipSyncDsp,
                 words: list[list[int]] | None = None) -> tuple[bytes, str]:
    """PCM (+известные слова) → (содержимое .vis, использованный алгоритм)."""
    n_samples = samples.size
    duration = n_samples / dsp.src_rate
    frame_count = int(np.ceil(duration * dsp.fps)) + 1
    padded = np.concatenate(
        [np.zeros(dsp.window_samples, dtype=np.float32), samples])

    viseme_count = len(dsp.templates)
    dists = np.full((frame_count, viseme_count), np.nan)
    vols = np.zeros(frame_count)
    for t in range(frame_count - 1):
        pos = min(int(round(t / dsp.fps * dsp.src_rate)), n_samples)
        window = padded[pos : pos + dsp.window_samples]
        d, vol = dsp.frame_features(window)
        vols[t] = vol
        if d is not None:
            dists[t] = d

    ratios = np.zeros((frame_count, viseme_count))
    algo = "acoustic"
    if words:
        finite = np.isfinite(dists)
        if finite.any():
            std = np.nanstd(dists)
            norm = (dists - np.nanmean(dists)) / (std if std > 1e-9 else 1.0)
            states = build_states(words)
            path = viterbi_align(norm, vols, states)
            if path is not None:
                for t in range(frame_count - 1):
                    vis = states[path[t]].vis
                    if vis != SIL:
                        ratios[t, vis] = _ALIGN_GAIN
                # Мягкий стык сегментов: треугольник 1 кадр по времени.
                sm = ratios.copy()
                sm[1:-1] = 0.25 * ratios[:-2] + 0.5 * ratios[1:-1] + 0.25 * ratios[2:]
                ratios = sm
                algo = "align"

    if algo == "acoustic":
        # Фолбэк/легаси: покадровая классификация (бывший uLipSync).
        with np.errstate(invalid="ignore"):
            scores = np.power(10.0, -dists)
        scores = np.where(np.isfinite(scores), scores, 0.0)
        sums = scores.sum(axis=1, keepdims=True)
        np.divide(scores, sums, out=ratios, where=sums > 0)

    ratios[0] = 0.0
    ratios[-1] = 0.0          # рот закрыт в начале и в конце
    vols[-1] = 0.0

    frames = bytearray()
    for t in range(frame_count):
        frames += bytes(int(round(r * 255)) for r in ratios[t])
        frames.append(int(round(float(vols[t]) * 255)))

    header = struct.pack(
        "<4sHBBHI", MAGIC, VERSION, viseme_count, dsp.fps,
        frame_count, n_samples)
    return header + bytes(frames), algo


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
    # acoustic: суммы ~255; align: пик 0.8·255 и спады на стыках — важно
    # лишь, чтобы сумма кадра не превышала «одну визему целиком».
    ratio_sums = frames[:, :vis].astype(np.int32).sum(axis=1)
    bad = ratio_sums > 255 + vis
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
_WORKER_TEXTS: dict[str, list[str]] | None = None
_WORKER_OVERRIDES: dict | None = None


def _init_worker(profile: dict, texts: dict[str, list[str]] | None,
                 overrides: dict | None = None) -> None:
    global _WORKER_DSP, _WORKER_TEXTS, _WORKER_OVERRIDES
    _WORKER_DSP = LipSyncDsp(profile)
    _WORKER_TEXTS = texts
    _WORKER_OVERRIDES = overrides


def _bake_one(wav_str: str) -> tuple[str, str, list[str]]:
    wav = Path(wav_str)
    dsp = _WORKER_DSP
    assert dsp is not None
    samples = read_wav_mono16(wav)
    problems: list[str] = []

    ov = (_WORKER_OVERRIDES or {}).get(override_key(wav))
    if ov is not None:
        if ov.get("sourceSamples") == samples.size:
            blob = bake_from_segments(samples, dsp, ov["segments"])
            problems += validate(blob, wav, dsp)
            vis_path_for(wav).write_bytes(blob)
            return wav.name, "manual", problems
        problems.append(
            f"{wav.name}: оверрайд устарел (WAV перегенерирован) — выравниваю заново")

    words = words_for_wav(wav, _WORKER_TEXTS) if _WORKER_TEXTS else None
    blob, algo = bake_samples(samples, dsp, words)
    problems += validate(blob, wav, dsp)
    if _WORKER_TEXTS is not None and algo != "align":
        problems.append(f"{wav.name}: align не сошёлся — запечён acoustic")
    vis_path_for(wav).write_bytes(blob)
    return wav.name, algo, problems


_DSP_CACHE: dict[str, LipSyncDsp] = {}
_TEXTS_CACHE: dict[str, dict[str, list[str]]] = {}


def bake_file(wav: Path, profile_path: Path = DEFAULT_PROFILE) -> Path:
    """Запечь один WAV (используется generate_voices.py). Возвращает путь .vis.
    Оверрайд пиано-ролла для СВЕЖЕ-перегенерированного WAV всегда устаревает
    (sourceSamples не совпадёт), так что тут он честно игнорируется."""
    key = str(profile_path)
    dsp = _DSP_CACHE.get(key)
    if dsp is None:
        dsp = _DSP_CACHE[key] = LipSyncDsp(json.loads(Path(profile_path).read_text()))
    lkey = str(DEFAULT_LINES)
    texts = _TEXTS_CACHE.get(lkey)
    if texts is None and DEFAULT_LINES.exists():
        texts = _TEXTS_CACHE[lkey] = load_line_texts()
    wav = Path(wav)
    samples = read_wav_mono16(wav)
    problems: list[str] = []

    ov = load_overrides().get(override_key(wav))
    if ov is not None and ov.get("sourceSamples") == samples.size:
        blob = bake_from_segments(samples, dsp, ov["segments"])
        algo = "manual"
    else:
        if ov is not None:
            problems.append(f"{wav.name}: оверрайд устарел — выравниваю заново")
        words = words_for_wav(wav, texts) if texts else None
        blob, algo = bake_samples(samples, dsp, words)
        if words and algo != "align":
            problems.append(f"{wav.name}: align не сошёлся — запечён acoustic")

    problems += validate(blob, wav, dsp)
    for p in problems:
        print(f"  ⚠ {p}", file=sys.stderr)
    out = vis_path_for(wav)
    out.write_bytes(blob)
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("paths", nargs="*", type=Path,
                    help="WAV-файлы или директории (дефолт — весь банк голосов)")
    ap.add_argument("--profile", type=Path, default=DEFAULT_PROFILE)
    ap.add_argument("--algo", choices=("align", "acoustic"), default="align",
                    help="align (дефолт) = Витерби по известному тексту; "
                         "acoustic = легаси-классификация uLipSync")
    ap.add_argument("--force", action="store_true",
                    help="пересобрать даже актуальные сайдкары")
    ap.add_argument("--jobs", type=int, default=0,
                    help="процессов (0 = по числу ядер)")
    args = ap.parse_args()

    profile = json.loads(args.profile.read_text())
    dsp = LipSyncDsp(profile)
    texts = load_line_texts() if args.algo == "align" else None
    overrides = load_overrides()

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
        _init_worker(profile, texts, overrides)
        done = [_bake_one(str(todo[0]))]
    else:
        with ProcessPoolExecutor(
                max_workers=workers, initializer=_init_worker,
                initargs=(profile, texts, overrides)) as pool:
            done = list(pool.map(_bake_one, [str(w) for w in todo],
                                 chunksize=8))
    algos: dict[str, int] = {}
    for _, algo, problems in done:
        algos[algo] = algos.get(algo, 0) + 1
        all_problems += problems

    print(f"запечено: {len(done)} ({', '.join(f'{k}={v}' for k, v in sorted(algos.items()))})")
    if all_problems:
        print(f"⚠ нарушений инвариантов: {len(all_problems)}", file=sys.stderr)
        for p in all_problems[:40]:
            print(f"  ⚠ {p}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
