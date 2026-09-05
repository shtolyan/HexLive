#!/usr/bin/env python3
"""Golden .vis bytes from the canonical offline DSP; no recorded voice data.

Fixtures use a deterministic integer synthetic PCM signal shared with C# tests.
Russian supplies explicit expected phonemes (the offline g2p is Latin-only).
"""
import base64
import json
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "_ArtSource/Voice"))
import bake_lipsync as reference

pcm = np.array([0 if i < 4410 or i >= 74970 else
                ((i * 97) % 20001) - 10000 for i in range(88200)], dtype=np.int16)
samples = pcm.astype(np.float32) / 32768
dsp = reference.LipSyncDsp(json.loads(reference.DEFAULT_PROFILE.read_text()))
cases = [
    ("hexkufa", "Tava miko!", reference.g2p_words("Tava miko!")),
    ("russian", "Мама, привет!", [[(5, False), (0, False), (5, False), (0, False)],
                                 [(5, False), (10, False), (1, False), (6, False), (3, False), (9, False)]]),
    ("fallback", "你好", None),
]
fixtures = []
for name, text, words in cases:
    data, algorithm = reference.bake_samples(samples, dsp, words)
    fixtures.append(dict(name=name, text=text, algorithm=algorithm,
                         visBase64=base64.b64encode(data).decode("ascii")))
target = ROOT / "Tests/HexLive.AgentHost.Tests/Fixtures/runtime-lipsync.json"
target.parent.mkdir(parents=True, exist_ok=True)
target.write_text(json.dumps(fixtures, ensure_ascii=False, indent=2) + "\n")
print(f"Exported {len(fixtures)} offline golden fixtures without audio recordings.")
