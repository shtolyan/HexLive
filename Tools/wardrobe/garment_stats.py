"""Раздать статы всем 685 вещам каталога по классу вещи.

Правило одно: **никогда не понижать**. Для каждой вещи берётся max(текущее,
пол своего класса), поэтому вручную вытюненная вещь остаётся как есть, а
нулевая получает свой минимум. Обычная одежда даёт понемногу, броня — заметно.

Правится ДВА места, и оба обязательны:
  * `GarmentLibrary.cs` — кодовые умолчания; отсюда берутся карманы и пол
    (`GarmentTuning.BackfillCapacity` подставляет их, когда в ассете 0);
  * `Assets/.../Garments/Assets/**.asset` — тюнинг, который в игре
    ПЕРЕКРЫВАЕТ умолчания и уезжает в `SimData/simdata.json`.

    python Tools/wardrobe/garment_stats.py          # вхолостую
    python Tools/wardrobe/garment_stats.py --apply
"""
from __future__ import annotations

import glob
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

ROOT = Path(__file__).resolve().parents[2]
LIBRARY = ROOT / "Assets/HexLive/Simulation/Content/Garments/GarmentLibrary.cs"
ASSETS = ROOT / "Assets/HexLive/UnityPresentation/Wearing/Garments/Assets"

# Порядок ВАЖЕН: побеждает первое совпадение. Бельё стоит раньше топов, потому
# что `underwear.swim_top` — это бельё, а не футболка; чулки раньше штанов,
# потому что `tights` иначе уехали бы в джинсы.
POLICY: list[tuple[str, float, float, int]] = [
    # ключи (регексп)                                  тепло броня карманы
    (r"backpack",                                       0.02, 0.00, 8),
    (r"pouch",                                          0.02, 0.00, 2),
    (r"armor\.|armguard|greave|armwrap|harness|"
     r"vest|corset|cuffs|breastplate",                  0.08, 0.15, 1),
    (r"jacket|coat|hoodie|parka",                       0.28, 0.06, 4),
    (r"sweater|keikogi",                                0.20, 0.02, 2),
    (r"stocking|sock|tights",                           0.04, 0.00, 0),
    (r"bodysuit|babydoll|lingerie",                     0.04, 0.00, 1),
    (r"briefs|panty|thong|bra_|bra$|bikini|swim",       0.02, 0.00, 1),
    (r"jeans|pants|legging|trousers",                   0.12, 0.05, 4),
    (r"shorts",                                         0.05, 0.02, 2),
    (r"skirt|tutu",                                     0.06, 0.01, 2),
    (r"dress|outfit|suit",                              0.10, 0.01, 2),
    (r"boots",                                          0.14, 0.10, 0),
    (r"sneaker|slipon|shoe|footwear",                   0.08, 0.05, 0),
    (r"sandal|heel|pumps|flipflop",                     0.02, 0.02, 0),
    (r"glove|sleeve",                                   0.03, 0.05, 0),
    (r"belt|strap",                                     0.01, 0.04, 2),
    (r"top|tank|halter|blouse|shirt|tee",               0.06, 0.01, 2),
    (r"cap|hat|headband|scarf|bowtie|collar|bandana",   0.04, 0.01, 0),
]

# Украшения и очки не дают ничего — и это решение, а не забытая строка:
# статы висят на ПОКРЫТЫХ зонах, а кулон не покрывает ничего.
NOTHING = re.compile(r"glasses|necklace|pendant|bracelet|earring|jewel", re.I)


def classify(item_id: str) -> tuple[float, float, int] | None:
    if NOTHING.search(item_id):
        return None
    lowered = item_id.lower()
    for pattern, warmth, armor, capacity in POLICY:
        if re.search(pattern, lowered):
            return warmth, armor, capacity
    return None


def main() -> int:
    apply = "--apply" in sys.argv
    text = LIBRARY.read_text(encoding="utf-8")

    row = re.compile(
        r'(new\("(?P<id>[^"]+)",\s*"[^"]*",\s*WearLayer\.\w+,\s*)'
        r'(?P<w>[\d.]+)f,\s*(?P<a>[\d.]+)f,\s*(?P<t>-?[\d.]+)f,\s*dress,\s*(?P<c>\d+)')

    changed_rows = 0
    floors: dict[str, tuple[float, float, int]] = {}
    unclassified: list[str] = []

    def fix(match: re.Match) -> str:
        nonlocal changed_rows
        item = match.group("id")
        rule = classify(item)
        if rule is None:
            unclassified.append(item)
            return match.group(0)

        warmth = max(float(match.group("w")), rule[0])
        armor = max(float(match.group("a")), rule[1])
        capacity = max(int(match.group("c")), rule[2])
        floors[item] = (warmth, armor, capacity)

        if (warmth, armor, capacity) != (float(match.group("w")), float(match.group("a")), int(match.group("c"))):
            changed_rows += 1

        return (f'{match.group(1)}{warmth:.2f}f, {armor:.2f}f, '
                f'{match.group("t")}f, dress, {capacity}')

    patched = row.sub(fix, text)
    print(f"GarmentLibrary.cs: строк тронуто {changed_rows}, "
          f"без класса {len(unclassified)}")
    if unclassified:
        print("   без класса:", ", ".join(sorted(set(unclassified))[:20]))

    changed_assets = 0
    for path in glob.glob(str(ASSETS / "**/*.asset"), recursive=True):
        body = Path(path).read_text(encoding="utf-8")
        found = re.search(r"^  id: (.*)$", body, re.M)
        if not found or found.group(1).strip() not in floors:
            continue

        warmth, armor, capacity = floors[found.group(1).strip()]
        updated = body
        updated = re.sub(r"^  warmth: [\d.]+$", f"  warmth: {warmth:g}", updated, flags=re.M)
        updated = re.sub(r"^  armor: [\d.]+$", f"  armor: {armor:g}", updated, flags=re.M)
        updated = re.sub(r"^  capacity: \d+$", f"  capacity: {capacity}", updated, flags=re.M)
        if updated == body:
            continue

        changed_assets += 1
        if apply:
            Path(path).write_text(updated, encoding="utf-8")

    print(f"ассетов тронуто: {changed_assets}")
    if apply:
        LIBRARY.write_text(patched, encoding="utf-8")
        print("записано.")
    else:
        print("вхолостую — ничего не записано (--apply, чтобы записать).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
