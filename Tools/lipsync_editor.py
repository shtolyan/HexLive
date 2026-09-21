#!/usr/bin/env python3
"""Пиано-ролл липсинка (§67.7) — локальный веб-редактор таймлайнов визем.

    python3 Tools/lipsync_editor.py            # откроет http://127.0.0.1:8765

Слева список реплик, в центре пиано-ролл: 14 дорожек-визем, сегменты-«ноты»
двигаются/тянутся/переставляются между дорожками как в FruityLoops; пробел —
play/pause, клик по линейке — скраб. Save пишет ручные сегменты в
_ArtSource/Voice/lipsync_overrides.json И сразу перепекает .vis — работающая
игра подхватывает правку со следующей реплики (mtime-кэш NpcVoiceLipSync).
Вкладка «Буквы» — таблица letter_visemes.json: назначить виземы пропущенным
буквам; Save перепекает только реплики, чей g2p реально изменился (ручные
оверрайды не трогаются).
"""

from __future__ import annotations

import difflib
import json
import struct
import sys
import threading
import urllib.parse
import webbrowser
from concurrent.futures import ProcessPoolExecutor
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
VOICE_DIR = REPO / "_ArtSource/Voice"
VOICES = REPO / "Assets/StreamingAssets/HexLive/Sfx/Voices"
sys.path.insert(0, str(VOICE_DIR))

import numpy as np  # noqa: E402

import bake_lipsync as bl  # noqa: E402

PORT = 8765

_dsp = bl.LipSyncDsp(json.loads(bl.DEFAULT_PROFILE.read_text()))
_texts = bl.load_line_texts()
_lock = threading.Lock()  # запись overrides/letters + ре-бейк — по одному


def clip_list() -> list[dict]:
    overrides = bl.load_overrides()
    clips = []
    for wav in bl.voice_files(VOICES):
        key = bl.override_key(wav)
        char = wav.parent.name
        stem = wav.stem
        rest = stem[len(f"voice_{char}_"):]
        group, _, variant = rest.rpartition("_")
        lines = _texts.get(group)
        text = ""
        if lines and variant.isdigit() and int(variant) < len(lines):
            text = lines[int(variant)]
        clips.append({
            "key": key, "char": char, "group": group, "n": variant,
            "text": text, "override": key in overrides,
        })
    return clips


def wav_path(key: str) -> Path:
    p = (VOICES / key).resolve()
    if not p.is_relative_to(VOICES) or p.suffix != ".wav":
        raise ValueError(f"плохой ключ: {key}")
    # §67.17: банк сжат в Ogg; ключ пиано-ролла по-прежнему оканчивается на .wav.
    return p if p.exists() or not p.with_suffix(".ogg").exists() else p.with_suffix(".ogg")


def read_vis(vis: Path) -> tuple[int, int, int, np.ndarray]:
    blob = vis.read_bytes()
    magic, ver, nvis, fps, fc, src = struct.unpack_from("<4sHBBHI", blob)
    frames = np.frombuffer(blob[14:], dtype=np.uint8).reshape(fc, nvis + 1)
    return fps, fc, src, frames


def segments_from_frames(frames: np.ndarray, fps: int) -> list[list[int]]:
    """Кадры .vis → сегменты [[висема, startMs, endMs], …] (обратная задача:
    argmax по кадрам, сглаживание стыков не мешает — оно не меняет argmax)."""
    nvis = frames.shape[1] - 1
    ratios = frames[:, :nvis]
    active = ratios.max(axis=1) > 60  # ~0.24 — ниже только хвосты стыков
    vis = ratios.argmax(axis=1)
    segs: list[list[int]] = []
    cur = None
    for f in range(len(frames)):
        v = int(vis[f]) if active[f] else None
        if cur is not None and (v is None or v != cur[0]):
            segs.append([cur[0], round(cur[1] * 1000 / fps), round(f * 1000 / fps)])
            cur = None
        if v is not None and cur is None:
            cur = (v, f)
    if cur is not None:
        segs.append([cur[0], round(cur[1] * 1000 / fps),
                     round(len(frames) * 1000 / fps)])
    return segs


def clip_payload(key: str) -> dict:
    wav = wav_path(key)
    vis = wav.with_suffix(".vis")
    fps, fc, src, frames = read_vis(vis)
    overrides = bl.load_overrides()
    ov = overrides.get(key)
    fresh_override = ov is not None and ov.get("sourceSamples") == src
    segments = ov["segments"] if fresh_override else segments_from_frames(frames, fps)
    return {
        "key": key,
        "fps": fps,
        "durationMs": round(src * 1000 / 44100),
        "vol": [int(v) for v in frames[:, -1]],
        "segments": segments,
        "override": fresh_override,
        "visemes": bl.VISEME_NAMES,
    }


def save_clip(key: str, segments: list) -> dict:
    wav = wav_path(key)
    samples = bl.read_wav_mono16(wav)
    segments = sorted(
        ([int(v), int(s), int(e)] for v, s, e in segments if e > s),
        key=lambda x: x[1])
    with _lock:
        overrides = bl.load_overrides()
        overrides[key] = {"sourceSamples": int(samples.size),
                          "segments": segments}
        bl.DEFAULT_OVERRIDES.write_text(
            json.dumps(overrides, indent=2, ensure_ascii=False) + "\n")
        blob = bl.bake_from_segments(samples, _dsp, segments)
        wav.with_suffix(".vis").write_bytes(blob)
    return {"ok": True, "override": True}


def reset_clip(key: str) -> dict:
    wav = wav_path(key)
    with _lock:
        overrides = bl.load_overrides()
        if key in overrides:
            del overrides[key]
            bl.DEFAULT_OVERRIDES.write_text(
                json.dumps(overrides, indent=2, ensure_ascii=False) + "\n")
        samples = bl.read_wav_mono16(wav)
        words = bl.words_for_wav(wav, _texts)
        blob, algo = bl.bake_samples(samples, _dsp, words)
        wav.with_suffix(".vis").write_bytes(blob)
    return clip_payload(key) | {"algo": algo}


def letters_payload() -> dict:
    mapping = json.loads(bl.DEFAULT_LETTER_MAP.read_text())
    digraphs = set(mapping["digraphs"])
    stats: dict[str, dict] = {}
    for group, lines in _texts.items():
        for n, text in enumerate(lines):
            import re
            clean = re.sub(r"\[.*?\]", " ", text.lower())
            for word in re.split(r"[^a-z]+", clean):
                i = 0
                while i < len(word):
                    unit = word[i:i + 2] if word[i:i + 2] in digraphs else word[i]
                    i += len(unit)
                    st = stats.setdefault(unit, {"count": 0, "examples": []})
                    st["count"] += 1
                    if len(st["examples"]) < 4 and not any(
                            e["word"] == word for e in st["examples"]):
                        st["examples"].append(
                            {"word": word, "group": group, "n": n})
    units = []
    for unit, st in sorted(stats.items(), key=lambda kv: -kv[1]["count"]):
        table = mapping["digraphs"] if unit in digraphs else mapping["letters"]
        units.append({"unit": unit, "count": st["count"],
                      "viseme": table.get(unit), "examples": st["examples"]})
    return {"visemes": bl.VISEME_NAMES, "units": units,
            "digraphs": sorted(digraphs)}


def save_letters(letters: dict, digraphs: dict) -> dict:
    with _lock:
        old_map = bl.load_letter_map()
        # Мердж поверх файла: буквы, которых нет в каталоге (и потому нет в
        # таблице UI), не должны молча пропасть из маппинга.
        new_json = json.loads(bl.DEFAULT_LETTER_MAP.read_text())
        new_json["letters"].update(letters)
        new_json["digraphs"].update(digraphs)
        bl.DEFAULT_LETTER_MAP.write_text(
            json.dumps(new_json, indent=2, ensure_ascii=False) + "\n")
        bl._LETTER_MAP = None  # сброс кэша модуля
        new_map = bl.load_letter_map()

        # Перепекаем ТОЛЬКО реплики, чей g2p реально изменился.
        changed_lines = set()
        for group, lines in _texts.items():
            for n, text in enumerate(lines):
                if (bl.g2p_words(text, old_map) != bl.g2p_words(text, new_map)):
                    changed_lines.add((group, str(n)))
        overrides = bl.load_overrides()
        todo = []
        for wav in bl.voice_files(VOICES):
            char = wav.parent.name
            rest = wav.stem[len(f"voice_{char}_"):]
            group, _, variant = rest.rpartition("_")
            if (group, variant) in changed_lines and \
                    bl.override_key(wav) not in overrides:
                todo.append(wav)

        problems: list[str] = []
        for wav in todo:
            samples = bl.read_wav_mono16(wav)
            words = bl.words_for_wav(wav, _texts)
            blob, algo = bl.bake_samples(samples, _dsp, words)
            wav.with_suffix(".vis").write_bytes(blob)
            if words and algo != "align":
                problems.append(f"{wav.name}: align не сошёлся")
    return {"ok": True, "rebaked": len(todo), "problems": problems[:20]}


# ---------------------------------------------------------------- аналитика
# Сверка запечённого таймлайна с ожидаемой последовательностью букв (g2p)
# и с акустикой. Ищет ровно те болезни, что видны глазом в пиано-ролле:
# потерянные буквы, гласные, зажатые в минимальную длительность, сегменты,
# чьи кадры спектрально не похожи на назначенную букву, и буквы в тишине.

ANALYSIS_FILE = VOICE_DIR / "lipsync_analysis.json"
_VOWEL_SET = {0, 1, 2, 3, 4}
_MIN_VOWEL_MS = 55     # ≈ _VOWEL_MIN (3 кадра) — «гласная в минимуме»
_QUIET = 0.08          # ниже — кадр считается тишиной
_AGREE_TOP = 4         # назначенная висема в топ-4 акустики = «похожа»


def _analyze_one(key: str) -> dict:
    wav = VOICES / key
    fps, fc, src, frames = read_vis(wav.with_suffix(".vis"))
    overrides = bl.load_overrides()
    ov = overrides.get(key)
    manual = ov is not None and ov.get("sourceSamples") == src

    words = bl.words_for_wav(wav, _texts) or []
    expected = [u for word in words for u in word]  # [(vis, long)]

    samples = bl.read_wav_mono16(wav)
    padded = np.concatenate(
        [np.zeros(_dsp.window_samples, dtype=np.float32), samples])
    dists = np.full((fc, len(bl.VISEME_NAMES)), np.nan)
    vols = np.zeros(fc)
    for t in range(fc - 1):
        pos = min(int(round(t / fps * 44100)), samples.size)
        d, vol = _dsp.frame_features(padded[pos:pos + _dsp.window_samples])
        vols[t] = vol
        if d is not None:
            dists[t] = d

    if manual:
        segments = ov["segments"]
    else:
        # Точный путь Витерби, не реконструкция из сглаженного .vis —
        # иначе буквы у границ клипа «теряются» в измерении, а не в бейке.
        segments = []
        if words and np.isfinite(dists).any():
            std = np.nanstd(dists)
            norm = (dists - np.nanmean(dists)) / (std if std > 1e-9 else 1.0)
            states = bl.build_states(words)
            path = bl.viterbi_align(norm, vols, states)
            if path is not None:
                cur, start = path[0], 0
                for t in range(1, fc):
                    if path[t] != cur:
                        if states[cur].vis != bl.SIL:
                            segments.append([states[cur].vis,
                                             round(start * 1000 / fps),
                                             round(t * 1000 / fps)])
                        cur, start = path[t], t
                if states[cur].vis != bl.SIL:
                    segments.append([states[cur].vis,
                                     round(start * 1000 / fps),
                                     round(fc * 1000 / fps)])
        if not segments:
            segments = segments_from_frames(frames, fps)

    # Потерянные буквы: LCS ожидаемой последовательности с фактической.
    exp_vis = [u[0] for u in expected]
    seg_vis = [s[0] for s in segments]
    sm = difflib.SequenceMatcher(a=exp_vis, b=seg_vis, autojunk=False)
    matched_exp: set[int] = set()
    matched_seg: set[int] = set()
    for blk in sm.get_matching_blocks():
        for k in range(blk.size):
            matched_exp.add(blk.a + k)
            matched_seg.add(blk.b + k)
    missing = [bl.VISEME_NAMES[exp_vis[i]]
               for i in range(len(exp_vis)) if i not in matched_exp]
    missing_vowels = [m for m in missing if m in ("A", "I", "U", "E", "O")]

    # Пер-сегментные метрики.
    vowels_min = 0
    bad_segs = []
    silent_segs = 0
    for si, (vis, s_ms, e_ms) in enumerate(segments):
        f0 = max(0, int(s_ms * fps / 1000))
        f1 = min(fc - 1, max(f0 + 1, int(round(e_ms * fps / 1000))))
        seg_vols = vols[f0:f1]
        if vis in _VOWEL_SET and si in matched_seg and e_ms - s_ms <= _MIN_VOWEL_MS:
            vowels_min += 1
        if seg_vols.size and float(seg_vols.mean()) < _QUIET:
            silent_segs += 1
            continue
        # MFCC-шаблоны — слабое свидетельство (потому длительности и несут
        # половину выравнивания), так что жалуемся только на «СОВСЕМ не
        # похожа»: ни одного кадра в топ-4 на заметном отрезке.
        voiced = [f for f in range(f0, f1)
                  if vols[f] > _QUIET and np.isfinite(dists[f]).all()]
        if len(voiced) >= 5:
            ranks = [int(np.argsort(dists[f]).tolist().index(vis))
                     for f in voiced]
            agree = sum(r < _AGREE_TOP for r in ranks) / len(ranks)
            if agree == 0.0:
                bad_segs.append(
                    {"vis": bl.VISEME_NAMES[vis], "startMs": s_ms,
                     "agree": round(agree, 2)})

    score = (3 * len(missing_vowels) + 2 * (len(missing) - len(missing_vowels))
             + vowels_min + 2 * len(bad_segs) + 2 * silent_segs)
    return {
        "key": key, "manual": manual, "score": score,
        "missing": missing, "vowelsMin": vowels_min,
        "badSegs": bad_segs[:6], "silentSegs": silent_segs,
        "units": len(expected), "segs": len(segments),
    }


def run_analysis() -> dict:
    keys = [bl.override_key(w) for w in bl.voice_files(VOICES)]
    with ProcessPoolExecutor() as pool:
        rows = list(pool.map(_analyze_one, keys, chunksize=16))
    rows.sort(key=lambda r: -r["score"])
    clean = sum(1 for r in rows if r["score"] == 0)
    result = {
        "total": len(rows), "clean": clean,
        "flagged": len(rows) - clean,
        "rows": [r for r in rows if r["score"] > 0],
    }
    ANALYSIS_FILE.write_text(
        json.dumps(result, ensure_ascii=False) + "\n")
    return result


def cached_analysis() -> dict:
    if ANALYSIS_FILE.exists():
        return json.loads(ANALYSIS_FILE.read_text())
    return {"total": 0, "clean": 0, "flagged": 0, "rows": []}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # тихий сервер
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        q = urllib.parse.parse_qs(url.query)
        try:
            if url.path == "/":
                body = PAGE.encode()
                self.send_response(200)
                self.send_header("Content-Type", "text/html; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            elif url.path == "/api/clips":
                self._json(clip_list())
            elif url.path == "/api/clip":
                self._json(clip_payload(q["key"][0]))
            elif url.path == "/api/letters":
                self._json(letters_payload())
            elif url.path == "/api/analysis":
                self._json(cached_analysis())
            elif url.path == "/audio":
                audio = wav_path(q["key"][0])
                data = audio.read_bytes()
                self.send_response(200)
                self.send_header("Content-Type",
                                 "audio/ogg" if audio.suffix == ".ogg" else "audio/wav")
                self.send_header("Content-Length", str(len(data)))
                self.send_header("Cache-Control", "no-store")
                self.end_headers()
                self.wfile.write(data)
            else:
                self._json({"error": "not found"}, 404)
        except BrokenPipeError:
            pass
        except Exception as e:  # noqa: BLE001
            self._json({"error": str(e)}, 500)

    def do_POST(self):
        url = urllib.parse.urlparse(self.path)
        try:
            n = int(self.headers.get("Content-Length", 0))
            payload = json.loads(self.rfile.read(n) or b"{}")
            if url.path == "/api/clip":
                self._json(save_clip(payload["key"], payload["segments"]))
            elif url.path == "/api/clip/reset":
                self._json(reset_clip(payload["key"]))
            elif url.path == "/api/letters":
                self._json(save_letters(payload["letters"], payload["digraphs"]))
            elif url.path == "/api/analyze":
                with _lock:
                    self._json(run_analysis())
            else:
                self._json({"error": "not found"}, 404)
        except Exception as e:  # noqa: BLE001
            self._json({"error": str(e)}, 500)


PAGE = r"""<!doctype html>
<meta charset="utf-8">
<title>Липсинк — пиано-ролл</title>
<style>
:root { --bg:#15171b; --panel:#1d2026; --panel2:#242832; --ink:#e9e5da;
  --dim:#8f95a3; --acc:#e0a458; --acc2:#5fa8a0; --grid:#2c313c; --sel:#f0c078; }
* { box-sizing:border-box; }
html,body { height:100%; }
body { margin:0; background:var(--bg); color:var(--ink);
  font:14px/1.45 system-ui,-apple-system,sans-serif; display:flex; }
#side { width:300px; flex:0 0 auto; border-right:1px solid var(--grid);
  display:flex; flex-direction:column; }
#side header { padding:10px; display:flex; gap:8px; }
#search { flex:1; background:var(--panel2); color:var(--ink);
  border:1px solid var(--grid); border-radius:6px; padding:6px 9px; }
#tabs { display:flex; gap:4px; padding:0 10px 8px; }
#tabs button { flex:1; background:var(--panel2); color:var(--dim);
  border:1px solid var(--grid); border-radius:6px; padding:5px; cursor:pointer; }
#tabs button.on { color:var(--acc); border-color:var(--acc); }
#clips { overflow-y:auto; flex:1; }
.clip { padding:6px 12px; cursor:pointer; border-bottom:1px solid var(--grid); }
.clip:hover { background:var(--panel); }
.clip.on { background:var(--panel2); border-left:3px solid var(--acc); }
.clip .k { font:11px ui-monospace,Menlo,monospace; color:var(--dim); }
.clip .t { font-size:12.5px; white-space:nowrap; overflow:hidden;
  text-overflow:ellipsis; }
.clip .ov { color:var(--acc); font-size:11px; }
#main { flex:1; display:flex; flex-direction:column; min-width:0; }
#bar { display:flex; align-items:center; gap:10px; padding:10px 14px;
  border-bottom:1px solid var(--grid); flex-wrap:wrap; }
#bar button { background:var(--panel2); color:var(--ink);
  border:1px solid var(--grid); border-radius:6px; padding:6px 14px;
  cursor:pointer; }
#bar button:hover { border-color:var(--acc); }
#play { width:52px; font-size:15px; color:var(--acc); }
#title { font:600 13px ui-monospace,Menlo,monospace; color:var(--dim); }
#linetext { font-weight:600; }
#status { margin-left:auto; color:var(--acc2); font-size:12.5px; }
#rollwrap { flex:1; position:relative; overflow:hidden; padding:8px 14px 14px; }
#roll { width:100%; height:100%; display:block; border-radius:8px;
  background:var(--panel); cursor:default; }
#letters { display:none; overflow:auto; padding:16px; }
#letters table { border-collapse:collapse; }
#letters td, #letters th { padding:6px 12px; border-bottom:1px solid var(--grid);
  text-align:left; }
#letters .unit { font:600 16px ui-monospace,Menlo,monospace; }
#letters .miss { color:#e07068; font-weight:600; }
#letters select { background:var(--panel2); color:var(--ink);
  border:1px solid var(--grid); border-radius:5px; padding:4px 6px; }
#letters .ex { color:var(--dim); font-size:12.5px; }
#analytics { display:none; overflow:auto; padding:16px; }
#analytics table { border-collapse:collapse; width:100%; }
#analytics td, #analytics th { padding:6px 10px;
  border-bottom:1px solid var(--grid); text-align:left; font-size:13px; }
#analytics tr[data-key] { cursor:pointer; }
#analytics tr[data-key]:hover { background:var(--panel); }
.chip { display:inline-block; background:var(--panel2); border:1px solid var(--grid);
  border-radius:10px; padding:1px 8px; margin:1px 3px 1px 0; font-size:11.5px; }
.chip.bad { color:#e07068; border-color:#5a3532; }
.chip.warn { color:var(--acc); }
.hint { color:var(--dim); font-size:12px; padding:0 14px 8px; }
</style>
<div id="side">
  <header><input id="search" placeholder="поиск: molly sad…"></header>
  <div id="tabs">
    <button id="tabRoll" class="on">Реплики</button>
    <button id="tabLetters">Буквы</button>
    <button id="tabAn">Аналитика</button>
  </div>
  <div id="clips"></div>
</div>
<div id="main">
  <div id="bar">
    <button id="play">▶</button>
    <div><div id="title">—</div><div id="linetext"></div></div>
    <button id="save">Сохранить</button>
    <button id="reset">Сбросить в авто</button>
    <span id="status"></span>
  </div>
  <div class="hint">пробел — play/pause · клик по линейке — перемотка ·
    тянуть ноту — двигать (вверх/вниз = другая буква) · края — длительность ·
    двойной клик — новая нота · Delete — удалить · ⌘S — сохранить</div>
  <div id="rollwrap"><canvas id="roll"></canvas></div>
  <div id="letters"></div>
  <div id="analytics"></div>
</div>
<audio id="au"></audio>
<script>
const $ = s => document.querySelector(s);
const css = n => getComputedStyle(document.documentElement).getPropertyValue(n).trim();
let CLIPS = [], clip = null, segs = [], sel = -1, dirty = false;
let VIS = [];
const au = $('#au');

const LANE_L = 46, RULER = 26, VOL_H = 40;

function status(msg, ok=true) {
  const el = $('#status');
  el.textContent = msg; el.style.color = ok ? css('--acc2') : '#e07068';
  if (msg) setTimeout(() => { if (el.textContent === msg) el.textContent = ''; }, 4000);
}

async function loadClips() {
  CLIPS = await (await fetch('/api/clips')).json();
  renderClipList();
}
function renderClipList() {
  const f = $('#search').value.toLowerCase().split(/\s+/).filter(Boolean);
  const root = $('#clips'); root.innerHTML = '';
  for (const c of CLIPS) {
    const hay = (c.key + ' ' + c.text).toLowerCase();
    if (f.some(w => !hay.includes(w))) continue;
    const div = document.createElement('div');
    div.className = 'clip' + (clip && clip.key === c.key ? ' on' : '');
    div.innerHTML = `<div class="k">${c.key}${c.override ? ' <span class="ov">✎ ручной</span>' : ''}</div>
      <div class="t">${c.text || '—'}</div>`;
    div.onclick = () => openClip(c.key);
    root.appendChild(div);
  }
}
$('#search').oninput = renderClipList;

async function openClip(key) {
  if (dirty && !confirm('Есть несохранённые правки — бросить?')) return;
  clip = await (await fetch('/api/clip?key=' + encodeURIComponent(key))).json();
  VIS = clip.visemes;
  segs = clip.segments.map(s => s.slice());
  sel = -1; dirty = false;
  au.src = '/audio?key=' + encodeURIComponent(key) + '&t=' + Date.now();
  au.load();
  const meta = CLIPS.find(c => c.key === key);
  $('#title').textContent = key + (clip.override ? '  ✎ ручной' : '');
  $('#linetext').textContent = meta ? meta.text : '';
  renderClipList(); draw();
}

// ---------- пиано-ролл ----------
const roll = $('#roll');
function geom() {
  const r = roll.getBoundingClientRect();
  const laneH = (r.height - RULER - VOL_H) / 14;
  return { w: r.width, h: r.height, laneH,
    x0: LANE_L, x1: r.width - 8,
    msToX: ms => LANE_L + (r.width - 8 - LANE_L) * ms / clip.durationMs,
    xToMs: x => Math.max(0, Math.min(clip.durationMs,
      (x - LANE_L) / (r.width - 8 - LANE_L) * clip.durationMs)),
    laneY: v => RULER + v * laneH,
    yToLane: y => Math.max(0, Math.min(13, Math.floor((y - RULER) / laneH))) };
}
function draw() {
  const dpr = devicePixelRatio, r = roll.getBoundingClientRect();
  roll.width = r.width * dpr; roll.height = r.height * dpr;
  const g = roll.getContext('2d'); g.scale(dpr, dpr);
  g.clearRect(0, 0, r.width, r.height);
  if (!clip) return;
  const G = geom();
  // дорожки
  for (let v = 0; v < 14; v++) {
    g.fillStyle = v % 2 ? css('--panel') : css('--panel2');
    g.fillRect(G.x0, G.laneY(v), G.x1 - G.x0, G.laneH);
    g.fillStyle = css('--dim');
    g.font = '11px ui-monospace,Menlo,monospace';
    g.fillText(VIS[v], 10, G.laneY(v) + G.laneH * 0.65);
  }
  // линейка: деления по 250 мс
  g.fillStyle = css('--dim'); g.font = '10px ui-monospace,Menlo,monospace';
  for (let ms = 0; ms <= clip.durationMs; ms += 250) {
    const x = G.msToX(ms);
    g.fillRect(x, RULER - 6, 1, 6);
    if (ms % 500 === 0) g.fillText((ms/1000).toFixed(1), x + 2, RULER - 8);
  }
  // громкость
  g.strokeStyle = css('--acc2'); g.lineWidth = 1.5; g.beginPath();
  const volY = r.height - VOL_H;
  clip.vol.forEach((v, f) => {
    const x = G.msToX(f * 1000 / clip.fps);
    const y = r.height - 4 - (VOL_H - 8) * v / 255;
    f ? g.lineTo(x, y) : g.moveTo(x, y);
  });
  g.stroke();
  g.fillStyle = css('--dim'); g.fillText('громкость', 10, volY + 12);
  // сегменты
  segs.forEach((s, i) => {
    const x = G.msToX(s[1]), w = Math.max(3, G.msToX(s[2]) - x);
    g.fillStyle = i === sel ? css('--sel') : css('--acc');
    g.globalAlpha = i === sel ? 1 : 0.85;
    g.beginPath(); g.roundRect(x, G.laneY(s[0]) + 2, w, G.laneH - 4, 3); g.fill();
    g.globalAlpha = 1;
    if (w > 18) {
      g.fillStyle = '#1a1206'; g.font = 'bold 10px ui-monospace,Menlo,monospace';
      g.fillText(VIS[s[0]], x + 4, G.laneY(s[0]) + G.laneH * 0.65);
    }
  });
  // плейхед
  const x = G.msToX(au.currentTime * 1000);
  g.strokeStyle = css('--ink'); g.lineWidth = 1.5;
  g.beginPath(); g.moveTo(x, 0); g.lineTo(x, r.height); g.stroke();
}

let drag = null; // {mode:'move'|'l'|'r'|'scrub', i, dx}
const SNAP = ms => Math.round(ms * 60 / 1000) * 1000 / 60;
function hit(mx, my) {
  const G = geom();
  for (let i = segs.length - 1; i >= 0; i--) {
    const s = segs[i], x = G.msToX(s[1]), x2 = G.msToX(s[2]);
    const y = G.laneY(s[0]);
    if (my >= y && my <= y + G.laneH && mx >= x - 4 && mx <= x2 + 4) {
      if (mx < x + 5) return { mode: 'l', i };
      if (mx > x2 - 5) return { mode: 'r', i };
      return { mode: 'move', i };
    }
  }
  return null;
}
roll.onpointerdown = e => {
  if (!clip) return;
  const rect = roll.getBoundingClientRect();
  const mx = e.clientX - rect.left, my = e.clientY - rect.top;
  const G = geom();
  if (my < RULER || my > rect.height - VOL_H) {
    drag = { mode: 'scrub' };
    au.currentTime = G.xToMs(mx) / 1000; draw();
    roll.setPointerCapture(e.pointerId);
    return;
  }
  const h = hit(mx, my);
  if (h) {
    sel = h.i;
    drag = { ...h, offMs: G.xToMs(mx) - segs[h.i][1],
             lane0: segs[h.i][0], my0: my };
    roll.setPointerCapture(e.pointerId);
  } else { sel = -1; }
  draw();
};
roll.onpointermove = e => {
  const rect = roll.getBoundingClientRect();
  const mx = e.clientX - rect.left, my = e.clientY - rect.top;
  const G = geom();
  if (!drag) {
    const h = hit(mx, my);
    roll.style.cursor = h ? (h.mode === 'move' ? 'grab' : 'ew-resize')
      : (my < RULER || my > rect.height - VOL_H ? 'pointer' : 'default');
    return;
  }
  if (drag.mode === 'scrub') { au.currentTime = G.xToMs(mx) / 1000; draw(); return; }
  const s = segs[drag.i], ms = G.xToMs(mx);
  if (drag.mode === 'move') {
    const len = s[2] - s[1];
    s[1] = SNAP(Math.max(0, Math.min(clip.durationMs - len, ms - drag.offMs)));
    s[2] = s[1] + len;
    s[0] = G.yToLane(my);
  } else if (drag.mode === 'l') {
    s[1] = SNAP(Math.min(s[2] - 1000/60, ms));
  } else {
    s[2] = SNAP(Math.max(s[1] + 1000/60, ms));
  }
  dirty = true; draw();
};
roll.onpointerup = () => { drag = null; };
roll.ondblclick = e => {
  if (!clip) return;
  const rect = roll.getBoundingClientRect();
  const mx = e.clientX - rect.left, my = e.clientY - rect.top;
  if (my < RULER || my > rect.height - VOL_H || hit(mx, my)) return;
  const G = geom();
  const start = SNAP(G.xToMs(mx));
  segs.push([G.yToLane(my), start, Math.min(clip.durationMs, start + 120)]);
  sel = segs.length - 1; dirty = true; draw();
};
addEventListener('keydown', e => {
  if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
  if (e.code === 'Space') { e.preventDefault(); togglePlay(); }
  if ((e.key === 'Delete' || e.key === 'Backspace') && sel >= 0) {
    segs.splice(sel, 1); sel = -1; dirty = true; draw();
  }
  if ((e.metaKey || e.ctrlKey) && e.key === 's') { e.preventDefault(); saveClip(); }
});
function togglePlay() {
  if (!clip) return;
  if (au.paused) { au.play(); } else { au.pause(); }
}
au.onplay = au.onpause = () => { $('#play').textContent = au.paused ? '▶' : '⏸'; };
$('#play').onclick = togglePlay;
(function tick(){ if (clip) draw(); requestAnimationFrame(tick); })();
addEventListener('resize', draw);

async function saveClip() {
  if (!clip) return;
  const res = await (await fetch('/api/clip', { method: 'POST',
    body: JSON.stringify({ key: clip.key, segments: segs }) })).json();
  if (res.ok) {
    dirty = false; clip.override = true;
    const meta = CLIPS.find(c => c.key === clip.key);
    if (meta) meta.override = true;
    $('#title').textContent = clip.key + '  ✎ ручной';
    renderClipList();
    status('Сохранено — игра подхватит со следующей реплики');
  } else status('Ошибка: ' + (res.error || '?'), false);
}
$('#save').onclick = saveClip;
$('#reset').onclick = async () => {
  if (!clip || !confirm('Убрать ручную правку и вернуть авто-выравнивание?')) return;
  const fresh = await (await fetch('/api/clip/reset', { method: 'POST',
    body: JSON.stringify({ key: clip.key }) })).json();
  clip = fresh; segs = clip.segments.map(s => s.slice()); sel = -1; dirty = false;
  const meta = CLIPS.find(c => c.key === clip.key);
  if (meta) meta.override = false;
  $('#title').textContent = clip.key;
  renderClipList(); draw();
  status('Возвращено авто-выравнивание (' + (fresh.algo || 'align') + ')');
};

// ---------- вкладки ----------
let lettersData = null;
$('#tabRoll').onclick = () => switchTab('roll');
$('#tabLetters').onclick = () => switchTab('letters');
$('#tabAn').onclick = () => switchTab('an');
function switchTab(tab) {
  $('#tabRoll').classList.toggle('on', tab === 'roll');
  $('#tabLetters').classList.toggle('on', tab === 'letters');
  $('#tabAn').classList.toggle('on', tab === 'an');
  $('#rollwrap').style.display = tab === 'roll' ? '' : 'none';
  document.querySelector('.hint').style.display = tab === 'roll' ? '' : 'none';
  $('#letters').style.display = tab === 'letters' ? 'block' : 'none';
  $('#analytics').style.display = tab === 'an' ? 'block' : 'none';
  if (tab === 'letters') loadLetters();
  if (tab === 'an') loadAnalysis(false);
}

// ---------- вкладка «Аналитика» ----------
async function loadAnalysis(rerun) {
  const root = $('#analytics');
  if (rerun) root.innerHTML = '<p>Гоняю анализ по всем репликам… (≈минута)</p>';
  const data = await (await fetch(rerun ? '/api/analyze' : '/api/analysis',
    rerun ? { method: 'POST', body: '{}' } : undefined)).json();
  if (!data.total) {
    root.innerHTML = `<p>Анализ ещё не гонялся.</p>
      <p><button id="runAn">Прогнать анализ (≈минута)</button></p>`;
    $('#runAn').onclick = () => loadAnalysis(true);
    return;
  }
  const chips = r => [
    ...r.missing.map(m => `<span class="chip bad">потеряна ${m}</span>`),
    r.vowelsMin ? `<span class="chip warn">гласных в минимуме: ${r.vowelsMin}</span>` : '',
    ...r.badSegs.map(b => `<span class="chip warn">${b.vis}@${(b.startMs/1000).toFixed(2)}s не похожа (${Math.round(b.agree*100)}%)</span>`),
    r.silentSegs ? `<span class="chip">букв в тишине: ${r.silentSegs}</span>` : '',
  ].filter(Boolean).join('');
  root.innerHTML = `<p>Всего ${data.total} · чистых <b style="color:var(--acc2)">${data.clean}</b> ·
      с флагами <b style="color:var(--acc)">${data.flagged}</b>
      &nbsp;<button id="runAn">Перегнать анализ</button></p>
    <table><tr><th>балл</th><th>реплика</th><th>что не так</th></tr>` +
    data.rows.map(r => `<tr data-key="${r.key}">
      <td>${r.score}</td>
      <td class="k">${r.key}${r.manual ? ' ✎' : ''}<br>
        <span class="ex">${(CLIPS.find(c => c.key === r.key) || {}).text || ''}</span></td>
      <td>${chips(r)}</td></tr>`).join('') + '</table>';
  $('#runAn').onclick = () => loadAnalysis(true);
  root.querySelectorAll('tr[data-key]').forEach(tr => tr.onclick = () => {
    switchTab('roll'); openClip(tr.dataset.key);
  });
}
async function loadLetters() {
  lettersData = await (await fetch('/api/letters')).json();
  const root = $('#letters');
  const opts = v => ['<option value="skip"' + (v === 'skip' ? ' selected' : '') + '>— не влияет —</option>']
    .concat(lettersData.visemes.map(name =>
      `<option${name === v ? ' selected' : ''}>${name}</option>`)).join('');
  root.innerHTML = `<table><tr><th>буква</th><th>визема рта</th>
    <th>встреч</th><th>примеры (клик — послушать)</th></tr>` +
    lettersData.units.map(u => `<tr>
      <td class="unit${u.viseme == null ? ' miss' : ''}">${u.unit}${u.viseme == null ? ' ⚠' : ''}</td>
      <td><select data-unit="${u.unit}">${u.viseme == null ? '<option selected disabled>назначь…</option>' : ''}${opts(u.viseme)}</select></td>
      <td>${u.count}</td>
      <td class="ex">${u.examples.map(e =>
        `<a href="#" data-g="${e.group}" data-n="${e.n}">${e.word}</a>`).join('  ')}</td>
    </tr>`).join('') + '</table>' +
    `<p><button id="saveLetters">Сохранить и перепечь затронутые</button>
     <span id="lettersStatus" style="color:var(--acc2)"></span></p>
     <p class="ex">Ручные (✎) реплики не перепекаются — их таймлайн твой.</p>`;
  root.querySelectorAll('a[data-g]').forEach(a => a.onclick = ev => {
    ev.preventDefault();
    const key = CLIPS.find(c => c.group === a.dataset.g && c.n === a.dataset.n);
    if (key) { au.src = '/audio?key=' + encodeURIComponent(key.key); au.play(); }
  });
  $('#saveLetters').onclick = async () => {
    const letters = {}, digraphs = {};
    root.querySelectorAll('select[data-unit]').forEach(s => {
      if (s.selectedOptions[0].disabled) return; // так и не назначена
      const t = lettersData.digraphs.includes(s.dataset.unit) ? digraphs : letters;
      t[s.dataset.unit] = s.value;
    });
    $('#lettersStatus').textContent = 'перепекаю…';
    const res = await (await fetch('/api/letters', { method: 'POST',
      body: JSON.stringify({ letters, digraphs }) })).json();
    $('#lettersStatus').textContent = res.ok
      ? `готово: перепечено ${res.rebaked} реплик` : 'ошибка: ' + res.error;
  };
}

loadClips();
</script>
"""


def main() -> None:
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    url = f"http://127.0.0.1:{PORT}/"
    print(f"Пиано-ролл липсинка: {url}  (Ctrl+C — выход)")
    threading.Timer(0.4, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
