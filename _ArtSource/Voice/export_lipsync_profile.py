#!/usr/bin/env python3
"""Экспорт калибровки uLipSync-профиля в JSON для офлайн-бейкера липсинка.

Разбирает Unity-YAML `Assets/Resources/HexLive/Audio/VoiceLipSyncProfile.asset`
и пишет `_ArtSource/Voice/lipsync_profile.json` — единственный источник
калибровки после удаления vendored-пакета uLipSync (spec §67.7).

Шаблон фонемы = среднее её калибровочных массивов. uLipSync при загрузке
обрезает список до последних `mfccDataCount` записей
(Profile.OnEnable → RemoveOldCalibrationData), поэтому здесь то же самое.
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ASSET = REPO / "Assets/Resources/HexLive/Audio/VoiceLipSyncProfile.asset"
OUT = Path(__file__).resolve().parent / "lipsync_profile.json"

# Порядок и маппинг на Daz-виземы обязаны совпадать с PhonemeMap в
# NpcVoiceLipSync.cs — рантайм читает виземы из .vis по индексу.
DAZ_SUFFIXES = {
    "A": "eCTRLvAA", "I": "eCTRLvIY", "U": "eCTRLvUW", "E": "eCTRLvEE",
    "O": "eCTRLvOW", "P": "eCTRLvM", "F": "eCTRLvF", "S": "eCTRLvS",
    "SH": "eCTRLvSH", "T": "eCTRLvT", "R": "eCTRLvER", "L": "eCTRLvL",
    "K": "eCTRLvK", "TH": "eCTRLvTH",
}


def parse_asset(text: str) -> dict:
    scalars = {}
    for key in ("mfccNum", "mfccDataCount", "melFilterBankChannels",
                "targetSampleRate", "sampleCount", "useStandardization",
                "compareMethod"):
        m = re.search(rf"^  {key}: (-?\d+)$", text, re.M)
        if not m:
            raise SystemExit(f"поле {key} не найдено в {ASSET}")
        scalars[key] = int(m.group(1))

    if scalars["useStandardization"] != 0:
        raise SystemExit("useStandardization != 0 — бейкер рассчитан на means=0/std=1")
    if scalars["compareMethod"] != 1:
        raise SystemExit("compareMethod != L2Norm — бейкер поддерживает только L2")

    phonemes = []
    current = None  # (name, [array, ...])
    arr = None
    for line in text.splitlines():
        m = re.match(r"^  - name: (\S+)$", line)
        if m:
            current = {"name": m.group(1), "arrays": []}
            phonemes.append(current)
            arr = None
            continue
        if re.match(r"^    - array:$", line) and current is not None:
            arr = []
            current["arrays"].append(arr)
            continue
        m = re.match(r"^      - (-?[0-9.eE+]+)$", line)
        if m and arr is not None:
            arr.append(float(m.group(1)))
    return scalars, phonemes


def main() -> None:
    scalars, phonemes = parse_asset(ASSET.read_text())
    n = scalars["mfccNum"]
    keep = scalars["mfccDataCount"]

    out_phonemes = []
    for ph in phonemes:
        arrays = ph["arrays"][-keep:]  # RemoveOldCalibrationData: последние N
        if not arrays or any(len(a) != n for a in arrays):
            raise SystemExit(f"фонема {ph['name']}: битые калибровочные массивы")
        template = [sum(a[i] for a in arrays) / len(arrays) for i in range(n)]
        suffix = DAZ_SUFFIXES.get(ph["name"])
        if suffix is None:
            raise SystemExit(f"фонема {ph['name']} не имеет Daz-виземы в маппинге")
        out_phonemes.append({
            "name": ph["name"],
            "dazSuffix": suffix,
            "mfcc": template,
        })

    if len(out_phonemes) != len(DAZ_SUFFIXES):
        raise SystemExit(
            f"фонем {len(out_phonemes)}, ожидалось {len(DAZ_SUFFIXES)}")

    profile = {
        "_source": str(ASSET.relative_to(REPO)),
        "_comment": "Калибровка липсинка (бывший uLipSync-профиль, molly_copy). "
                    "Порядок фонем = порядок визем в .vis-сайдкарах.",
        "sourceSampleRate": 44100,
        "targetSampleRate": scalars["targetSampleRate"],
        "sampleCount": scalars["sampleCount"],
        "melFilterBankChannels": scalars["melFilterBankChannels"],
        "mfccNum": scalars["mfccNum"],
        "compareMethod": "L2Norm",
        "minVolume": -3.0,
        "maxVolume": -1.0,
        "fps": 60,
        "phonemes": out_phonemes,
    }
    OUT.write_text(json.dumps(profile, indent=2, ensure_ascii=False) + "\n")
    print(f"OK: {len(out_phonemes)} фонем -> {OUT}")


if __name__ == "__main__":
    sys.exit(main())
