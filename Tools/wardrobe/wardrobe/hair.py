"""Варианты причёсок: тот же приём, что у расцветок одежды, но проще.

Причёска — не гардероб (spec §31B.4B): у неё нет слота, слоя и строки каталога,
одна штука меняется через `BodyBones.SetHair`. Поэтому вариант причёски НЕ
становится отдельным предметом: это тот же меш и те же материалы, у которых
подменена одна карта.

⭐ Отсюда главное свойство: **материал варианта — копия настроенного материала
прототипа**, в котором заменён только `_BaseMap`. Значит подобранные руками
пороги прозрачности (0.42 у большинства, 0.12 у Bendine, 0.08 у Chunky) и
прозрачный режим у шапочек и кожи головы переносятся на все варианты САМИ.
Переносить настройки отдельной работой не нужно — и нельзя: они разойдутся.

Проверено на данных: у наборов outoftouch каждый цвет назначает девять
диффузных карт на те же девять поверхностей и больше ничего не трогает. У SWAM
попадаются пресеты, меняющие только оттенок (вишенки Yumi Hair) — они тоже
поддержаны, как у одежды.
"""
from __future__ import annotations

import json
from pathlib import Path
from urllib.parse import unquote

from . import config, textures, variants

# Причёска в проекте -> папка её набора в библиотеке DAZ. Сопоставлено по КЛЮЧУ
# МЕША через ссылку на `data/<вендор>/<продукт>` внутри пресетов, а не по
# названию: названия расходятся (Hair07 живёт в «Ladina Hair», Neu09Hair — в
# «Yumi Hair»), и совпадение имён тут ничего не значит.
#
# Часть пресетов лежит в папке Genesis 8. Это не помеха: меш у нас свой, а
# пресет материалов поколения не знает — он назначает картинки на имена
# поверхностей, и у одного продукта они совпадают.
PRODUCTS = {
    "AdellHair": "People/Genesis 3 Female/Hair/SWAM/Adell Hair",
    "AsukaHair": "People/Genesis 8 Female/Hair/SWAM/Asuka - Space Buns/Hair",
    "BendineHair": "People/Genesis 8 Female/Hair/OOT Bendine Hair",
    "Bob3Hair": "People/Genesis 3 Female/Hair/Goldtassel/Bobbity Bo Hair",
    "ChunkyHair": "People/Genesis 3 Female/Hair/OOT Chunky Pigtails",
    "EilisHair": "People/Genesis 8 Female/Hair/Eilis Hair",
    "Hair07": "People/Genesis 8 Female/Hair/SWAM/Ladina Hair",
    "JenniferHair": "People/Genesis 3 Female/Hair/Kool/Jennifer Hair",
    "LeonyPonytail": "People/Genesis 3 Female/Hair/OOT Leony Ponytail Hair",
    "LoonaHair": "People/Genesis 3 Female/Hair/SWAM/Loona Hair",
    "Neu09Hair": "People/Genesis 3 Female/Hair/SWAM/Yumi Hair",
    "TootsieRollHair": "People/Genesis 3 Female/Hair/Goldtassel/Classic Roll Hair",
}

HAIR_ROOT = config.ASSETS / "ImportedActors" / "Hair"
MANIFEST = config.ASSETS / "Editor" / "HairVariants.json"

# Служебные пресеты, а не цвета: «Apply First» переводит причёску на нужный
# шейдер, «!HIDE» прячет часть, «Posing Mat» — вспомогательный материал.
#
# ⚠️ Знак «!» вендоры ставят НЕ ТОЛЬКО в начало имени: «Bendine Hair !Apply
# First», «Cherry !HIDE». Проверка «начинается с !» пропускала их, и служебные
# пресеты доезжали цветами.
_JUNK_WORDS = ("apply first", "hide", "show", "posing", "iray", "3delight")


def _is_junk(name: str) -> bool:
    low = name.strip().lower()
    return "!" in low or any(w in low for w in _JUNK_WORDS)


def own_materials(hair: str) -> dict[str, str]:
    """Поверхность -> имя файла карты, как она стоит у прототипа сейчас."""
    out: dict[str, str] = {}
    folder = HAIR_ROOT / hair / "Materials"
    if not folder.exists():
        return out
    for mat in folder.glob("*.mat"):
        text = mat.read_text(encoding="utf-8", errors="ignore")
        for line in text.splitlines():
            if "m_Texture:" in line and "guid:" in line:
                out[mat.stem] = "<есть>"
                break
    return out


def harvest(hair: str) -> list[dict]:
    """Цвета одной причёски: [{name, textures:[{source,texture}], colors:[...]}]."""
    product = config.DAZ_LIBRARY / PRODUCTS[hair]
    if not product.exists():
        return []

    surfaces = {m.stem for m in (HAIR_ROOT / hair / "Materials").glob("*.mat")}
    found: list[dict] = []
    for preset in sorted(product.rglob("*.duf")):
        if _is_junk(preset.stem):
            continue
        maps = {s: Path(v.replace("\\", "/")).name
                for s, v in variants.surfaces(preset).items() if s in surfaces}
        tints = {s: t for s, t in variants.tints(preset).items() if s in surfaces}
        if not maps and not tints:
            continue
        found.append({
            "name": preset.stem,
            "textures": [{"source": s, "texture": t} for s, t in sorted(maps.items())],
            "colors": [{"source": s, "color": c} for s, c in sorted(tints.items())],
            "_files": maps,
            # Откуда пресет — нужно только чтобы выбрать вкус отрисовки при
            # дублях по имени; в манифест не попадает.
            "preset_dir": str(preset.parent).lower(),
        })

    # ⚠️ Один цвет часто лежит ДВАЖДЫ — в двух вкусах отрисовки (Iray и
    # 3Delight) или в папках двух поколений. Имя у них одно, поэтому в проекте
    # они дали бы одну папку, но манифест бы врал числом: у Eilis 48 записей
    # при 25 настоящих цветах. Iray предпочтительнее, как и у одежды.
    by_name: dict[str, dict] = {}
    for colour in found:
        old = by_name.get(colour["name"])
        if old is None or ("iray" in colour["preset_dir"] and "iray" not in old["preset_dir"]):
            by_name[colour["name"]] = colour

    # Тот же отсев, что у одежды: сравнивается РЕЗУЛЬТАТ наложения на прототип,
    # иначе два пресета с одинаковыми картами доедут двумя одинаковыми цветами.
    return variants.dedupe(sorted(by_name.values(), key=lambda c: c["name"]))


def opacity_maps(hair: str) -> dict[str, str]:
    """Поверхность -> карта ПРОЗРАЧНОСТИ этой причёски.

    ⭐ Пресеты цвета её НЕ несут: они меняют только диффуз, блеск и
    подповерхностное рассеяние. Прозрачность ставится один раз служебным
    пресетом («Chunky Pigtails !Apply First») и одна на все цвета.

    Без неё вариант выглядит сломанным, хотя материал верен: прозрачный режим
    включён, а прозрачничать нечем — альфа сплошная, и шапочка садится на голову
    чёрной полосой. Именно так и вышло на первом прогоне.
    """
    product = config.DAZ_LIBRARY / PRODUCTS[hair]
    found: dict[str, str] = {}
    for preset in sorted(product.rglob("*.duf")):
        doc = variants._load(preset)
        if not doc:
            continue
        for entry in doc.get("scene", {}).get("animations", []) or []:
            url = unquote(str(entry.get("url", "")))
            if "#materials/" not in url or "cutout" not in url.lower():
                continue
            keys = entry.get("keys") or []
            value = keys[0][1] if keys and len(keys[0]) > 1 else None
            if not isinstance(value, str) or "/" not in value.replace("\\", "/"):
                continue
            surface = url.split("#materials/", 1)[1].split(":", 1)[0]
            found.setdefault(surface, Path(value.replace("\\", "/")).name)

    # Второй источник — сами прототипы. Их картинки уже склеены и несут пару в
    # имени: `08OOTChunkyCap__OOTUtilityChunkyCapT.png`. Служебный пресет
    # выставляет прозрачность не всем поверхностям (у Chunky пяти из девяти), а
    # у прототипа она есть у каждой, потому что он собран и проверен глазами.
    for mat in (HAIR_ROOT / hair / "Materials").glob("*.mat"):
        if mat.stem in found:
            continue
        text = mat.read_text(encoding="utf-8", errors="ignore")
        for meta in (HAIR_ROOT / hair / "Textures").glob("*.meta"):
            guid = _guid(meta)
            # `meta.stem` — это «Цвет__Маска.png» целиком, поэтому расширение
            # снимается отдельно: иначе получается «Маска.png.png».
            name = Path(meta.stem)
            if guid and guid in text and "__" in name.stem:
                found[mat.stem] = name.stem.rsplit("__", 1)[1] + name.suffix
                break
    return found


def _guid(meta: Path) -> str | None:
    for line in meta.read_text(encoding="utf-8", errors="ignore").splitlines():
        if line.startswith("guid: "):
            return line.split(" ", 1)[1].strip()
    return None


def _find_mask(name: str) -> Path | None:
    """Найти карту прозрачности, не полагаясь на расширение.

    Имя маски мы узнаём двумя путями — из служебного пресета и из имени
    склеенной картинки прототипа, — и расширение в них расходится: у Chunky
    маска шапочки лежит как `.jpg`, а в имени склейки стоит `.png`.
    """
    direct = textures._resolve(name)
    if direct is not None:
        return direct
    stem = Path(name).stem
    for candidate in config.DAZ_TEXTURES.rglob(stem + ".*"):
        if candidate.suffix.lower() in (".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"):
            return candidate
    return None


def stage(hair: str, colours: list[dict]) -> dict:
    """Положить картинки цветов в папку причёски, вернуть отчёт.

    Диффуз СКЛЕИВАЕТСЯ с картой прозрачности в один PNG — ровно так же, как
    лежат картинки прототипов (`08OOTChunkyCap__OOTUtilityChunkyCapT.png`).
    Имена в манифесте после этого указывают на склейку, а не на исходный jpg.
    """
    out_dir = HAIR_ROOT / hair / "Textures"
    masks = opacity_maps(hair)
    written, missing = [], []
    for colour in colours:
        colour.pop("preset_dir", None)   # служебное, в манифест не идёт
        files = colour.pop("_files", {})
        renamed: dict[str, str] = {}
        for surface, name in sorted(files.items()):
            source = textures._resolve(name)
            if source is None:
                missing.append(f"{colour['name']}/{surface}: {name}")
                continue

            mask = _find_mask(masks[surface]) if surface in masks else None
            if mask is not None:
                target = out_dir / f"{source.stem}_{mask.stem}.png"
                if not target.exists():
                    textures._save_cutout(source, mask, target)
                    written.append(target.name)
            else:
                target = out_dir / source.name
                if not target.exists():
                    textures._save_plain(source, target)
                    written.append(target.name)
            renamed[surface] = target.name

        colour["textures"] = [{"source": s, "texture": t}
                              for s, t in sorted(renamed.items())]
    return {"written": written, "missing": missing}


def build() -> dict:
    """Собрать варианты всех причёсок и записать манифест для экстрактора."""
    data, report = {}, {"hair": [], "problems": []}
    for hair in sorted(PRODUCTS):
        if not (HAIR_ROOT / hair).exists():
            report["problems"].append(f"нет причёски в проекте: {hair}")
            continue
        colours = harvest(hair)
        staged = stage(hair, colours)
        report["problems"] += staged["missing"]
        data[hair] = colours
        report["hair"].append({"name": hair, "colours": len(colours),
                               "textures": len(staged["written"])})
    # Форма ровно та, которую читает `JsonUtility` на стороне Unity: объект с
    # одним массивом. Словарь «причёска -> цвета» он не умеет, а переделывать
    # его на месте регулярками — способ получить обрыв на вложенном массиве,
    # что однажды и вышло.
    MANIFEST.parent.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps(
        {"hairs": [{"name": k, "colours": v} for k, v in sorted(data.items())]},
        ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    report["manifest"] = str(MANIFEST)
    report["total"] = sum(h["colours"] for h in report["hair"])
    report["ok"] = not report["problems"]
    return report
