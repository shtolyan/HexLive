"""Stage: снять предмет со всех мест, где он зарегистрирован.

Предмет живёт в СЕМИ местах, и забытое место не молчит — оно врёт. Строка в
`GarmentLibrary` без префаба даёт предмет-призрак, ассет определения без строки
библиотеки побеждает при сборке каталога и приезжает пустым (§9), а термин без
предмета просто копится.

Поэтому удаление — отдельный шаг, а не «поправь в трёх файлах»:

  1. запись в манифесте заходa (сама вещь или её расцветка);
  2. строка `GarmentLibrary.BuildDefaults`;
  3. строка `WearSlotCatalog.Slots`;
  4. термины `item.<slug>.name` / `.desc` в I2;
  5. ассет определения `Garments/Assets/<Слой>/<slug>.asset` (+ `.meta`);
  6. префаб `Resources/HexLive/Wear/<simId>/` (у расцветки его нет — она
     берёт геометрию прототипа) и папка материалов расцветки;
  7. иконка `Resources/HexLive/UI/Items/<simId>.png` (+ `.meta`).

Каталог и SimData НЕ трогаются руками: их пересобирают меню Unity, и это
единственный способ не разойтись с тюнингом.
"""
from __future__ import annotations

import io
import re
import shutil
from pathlib import Path

from . import config, manifest, register

DEFINITIONS = (config.ASSETS / "HexLive" / "UnityPresentation" / "Wearing"
               / "Garments" / "Assets")
PREFABS = config.ASSETS / "Resources" / "HexLive" / "Wear"
ICONS = config.ASSETS / "Resources" / "HexLive" / "UI" / "Items"


def _read(path: Path) -> str:
    return io.open(path, encoding="utf-8", newline="").read()


def _write(path: Path, text: str) -> None:
    io.open(path, "w", encoding="utf-8", newline="").write(text)


def _drop_lines(path: Path, matches) -> int:
    """Убрать из файла строки, на которые отвечает `matches`."""
    if not path.exists():
        return 0
    text = _read(path)
    nl = "\r\n" if "\r\n" in text[:4000] else "\n"
    kept = [line for line in text.split(nl) if not matches(line)]
    removed = len(text.split(nl)) - len(kept)
    if removed:
        _write(path, nl.join(kept))
    return removed


def _drop_terms(item_id: str) -> int:
    """Термины лежат блоком в семь строк — режется блок, а не строка."""
    path = register.I2_ASSET
    text = _read(path)
    nl = "\r\n" if "\r\n" in text[:4000] else "\n"
    removed = 0
    for suffix in ("name", "desc"):
        term = f"item.{register.slug(item_id)}.{suffix}"
        head = f"    - Term: '{term}'{nl}"
        start = text.find(head)
        if start < 0:
            continue
        nxt = text.find("    - Term: '", start + len(head))
        end = nxt if nxt > 0 else len(text)
        text = text[:start] + text[end:]
        removed += 1
    if removed:
        _write(path, text)
    return removed


def _rm(path: Path) -> bool:
    meta = path.with_suffix(path.suffix + ".meta")
    hit = path.exists()
    if path.is_dir():
        shutil.rmtree(path)
    elif hit:
        path.unlink()
    if meta.exists():
        meta.unlink()
    return hit


def _with_colourways(data: dict, item_ids: list[str]) -> list[str]:
    """Дополнить список расцветками тех вещей, которые снимают целиком.

    ⚠️ Расцветка берёт у прототипа ГЕОМЕТРИЮ (`PrototypeId`), поэтому снять
    вещь и оставить её расцветки — значит оставить 92 предмета, ссылающихся на
    ничто: в манифесте они исчезнут вместе с вещью, а строки библиотеки, слоты,
    термины и ассеты определений останутся сиротами. Порядок важен: расцветки
    удаляются ПЕРЕД прототипом, пока он ещё в манифесте и по нему можно собрать
    их имена.
    """
    out: list[str] = []
    for item_id in item_ids:
        garment = next((g for g in data.get("garments") or []
                        if g["simId"] == item_id), None)
        if garment:
            out += [f'{item_id}_{register.variant_slug(v["name"])}'
                    for v in garment.get("variants") or []]
        out.append(item_id)
    return out


def remove(drop: str, item_ids: list[str]) -> dict:
    """Снять предметы заходa `drop`. Работает и с вещью, и с её расцветкой."""
    data = manifest.load(drop)
    report: dict = {"drop": drop, "items": [], "problems": []}

    asked = list(item_ids)
    item_ids = _with_colourways(data, item_ids)
    report["expanded"] = [i for i in item_ids if i not in asked]

    for item_id in item_ids:
        where: list[str] = []

        # 1. Манифест: вещь целиком или одна её расцветка.
        garments = data.get("garments") or []
        before = len(garments)
        whole = next((g for g in garments if g["simId"] == item_id), None)
        data["garments"] = [g for g in garments if g["simId"] != item_id]
        if len(data["garments"]) != before:
            where.append("манифест: вещь")
            # Арт вещи, которую сняли целиком, больше никому не нужен: меши,
            # материалы и текстуры остались бы мёртвым весом в проекте.
            if whole and whole.get("folder") and _rm(config.WEAR_IMPORT / whole["folder"]):
                where.append(f'арт-папка {whole["folder"]}')
        else:
            for g in data["garments"]:
                kept = [v for v in (g.get("variants") or [])
                        if f'{g["simId"]}_{register.variant_slug(v["name"])}' != item_id]
                if len(kept) != len(g.get("variants") or []):
                    gone = [v for v in g["variants"] if v not in kept]
                    g["variants"] = kept
                    where.append(f'манифест: расцветка вещи {g["simId"]}')
                    for v in gone:
                        folder = (config.WEAR_IMPORT / g["folder"] / "Materials"
                                  / re.sub(r'[<>:"/\\|?*]', "_", v["name"]))
                        if _rm(folder):
                            where.append("материалы расцветки")

        # 2-3. Строки библиотеки и слотов.
        if _drop_lines(register.GARMENT_LIBRARY, lambda s: f'new("{item_id}"' in s):
            where.append("GarmentLibrary")
        if _drop_lines(register.SLOT_CATALOG, lambda s: f'["{item_id}"]' in s):
            where.append("WearSlotCatalog")

        # 4. Термины.
        if _drop_terms(item_id):
            where.append("термины I2")

        # 5. Ассет определения — слой заранее неизвестен, поэтому ищем везде.
        for layer in DEFINITIONS.iterdir() if DEFINITIONS.exists() else []:
            if _rm(layer / f"{register.slug(item_id)}.asset"):
                where.append(f"определение ({layer.name})")

        # 6-7. Префаб и иконка.
        if _rm(PREFABS / item_id):
            where.append("префаб")
        if _rm(ICONS / f"{item_id}.png"):
            where.append("иконка")

        if not where:
            report["problems"].append(f"{item_id}: не нашёлся нигде")
        report["items"].append({"id": item_id, "removed_from": where})

    manifest.save(data, drop)
    report["ok"] = not report["problems"]
    report["next"] = ("пересоберите каталог и SimData через меню Unity — руками "
                      "их править нельзя")
    return report
