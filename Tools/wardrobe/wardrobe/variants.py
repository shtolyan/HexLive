"""Собрать ВСЕ расцветки продукта, не примеряя ни одной лишний раз.

Расцветка не требует примерки. Геометрия у всех вариантов одна и та же —
пресет материалов меняет лишь то, какая картинка назначена на какую
поверхность. Поэтому DAZ одевает вещь ОДИН раз (это даёт меш, скелет и UV), а
все расцветки вычитываются из `.duf` файлами, без студии.

Для Complete Anarchy это 18 примерок вместо 1 404 (18 вещей × 78 расцветок).

Что такое пресет внутри: JSON (иногда gzip) с `image_library` — списком
картинок — и `scene.materials`, где каждая поверхность ссылается на картинку.
Достаточно диффуза: наш стиль плоский, URP/Lit читает только albedo.

Как расцветка привязывается к вещи: по имени файла. Вендор называет пресеты
«<вещь> <цвет>» — «CA Belt 2 Black», «CA Blouse Bra Purple». Имя вещи известно
из её собственного .duf, так что остаток строки и есть название расцветки.
Совпадение по САМОМУ ДЛИННОМУ префиксу, иначе «CA Belt 2 Black» уедет к «CA
Belt».
"""
from __future__ import annotations

import gzip
import json
import re
from pathlib import Path
from urllib.parse import unquote

# Оба «вкуса» пресетов описывают одни и те же расцветки; Iray современнее и
# чаще несёт полный набор карт, поэтому он предпочтительнее, а 3Delight —
# запасной вариант для продуктов, где Iray-версии нет.
FLAVOURS = ("Iray", "3Delight")

DIFFUSE = ("diffuse color", "base color", "diffuse")


def _load(path: Path) -> dict | None:
    try:
        raw = path.read_bytes()
    except OSError:
        return None
    if raw[:2] == b"\x1f\x8b":
        try:
            raw = gzip.decompress(raw)
        except OSError:
            return None
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None


def surfaces(preset: Path) -> dict[str, str]:
    """Поверхность -> файл диффузной карты, как его называет пресет.

    Читается блок `scene.animations` — тот же, в котором лежит поза каблуков
    (см. heels.py). Пресет записывает НАЗНАЧЕНИЯ как список адресов:

        name://@selection#materials/belt:?diffuse/image_file  ->  ".../OABelt_Blk.jpg"

    то есть имя поверхности стоит между `#materials/` и `:`, а нужный канал —
    `diffuse/image_file`.

    Искать картинку в `material_library.diffuse` бесполезно: там лежит только
    ЦВЕТ (`value`), а карта приходит отдельной строкой назначения — на этом
    разбор сначала дал ноль расцветок при тринадцати картинках в файле.

    Прочие каналы (`Bump Strength`, `Displacement Strength`) отбрасываются: наш
    стиль плоский, URP/Lit читает только albedo.
    """
    doc = _load(preset)
    if not doc:
        return {}

    out: dict[str, str] = {}
    for entry in doc.get("scene", {}).get("animations", []) or []:
        url = unquote(str(entry.get("url", "")))
        if "#materials/" not in url or not url.endswith("diffuse/image_file"):
            continue
        surface = url.split("#materials/", 1)[1].split(":", 1)[0]
        keys = entry.get("keys") or []
        if keys and len(keys[0]) > 1 and keys[0][1]:
            out[surface] = unquote(str(keys[0][1]))
    return out


def garment_names(product: Path) -> list[str]:
    """Имена вещей продукта — по их собственным .duf в корне."""
    return sorted((p.stem for p in product.glob("*.duf")), key=len, reverse=True)


def harvest(product: Path) -> dict[str, list[dict]]:
    """Вещь -> список её расцветок [{name, surfaces}], без запуска DAZ."""
    names = garment_names(product)
    if not names:
        return {}

    folder = None
    for flavour in FLAVOURS:
        candidate = product / "Materials" / flavour
        if candidate.is_dir():
            folder = candidate
            break
    if folder is None:
        folder = product / "Materials"
    if not folder.exists():
        return {}

    found: dict[str, list[dict]] = {n: [] for n in names}
    for preset in sorted(folder.rglob("*.duf")):
        stem = preset.stem
        # Самый длинный подходящий префикс — иначе «CA Belt 2 Black»
        # притянется к «CA Belt».
        owner = next((n for n in names if stem.startswith(n)), None)
        if owner is None:
            continue
        colour = stem[len(owner):].strip(" -_") or "Default"
        maps = surfaces(preset)
        if maps:
            found[owner].append({"name": colour, "surfaces": maps,
                                 "preset": preset.name})

    return {k: v for k, v in found.items() if v}


def owners(dress_report: dict) -> dict[str, str]:
    """Ключ меша -> имя вещи в DAZ, из отчёта одевания.

    Это ЕДИНСТВЕННАЯ честная связь, и она уже записана: одевая девушку, DAZ
    сообщает, какая фигура получилась из какого файла — `{"label": "CA Blouse",
    "name": "TA_Shirt_13442"}`. Имена не совпадают и совпадать не должны (см.
    правило про DAZ MESH KEY в CLAUDE.md), поэтому угадывать нечего.

    Угадывание пробовали, и оно провалилось ровно так, как и должно было:
    сопоставление по набору ПОВЕРХНОСТЕЙ поменяло местами длинные перчатки с
    ремешковыми (у обеих одна поверхность `glove`), оба ошейника, а блузку
    отдало ремню. Расцветки при этом выглядели правдоподобно — просто чужими.
    """
    out: dict[str, str] = {}
    for girl in dress_report.get("girls") or []:
        for item in girl.get("fitted") or []:
            name, label = item.get("name"), item.get("label")
            if name and label:
                out[name] = label
    return out


def colours_for(harvested: dict[str, list[dict]], label: str) -> list[dict]:
    """Расцветки вещи по её метке из отчёта одевания.

    Через нормализацию, потому что DAZ отдаёт метку фигуры уже причёсанной:
    пресеты лежат под «CA Cap», а в сцене вещь зовётся «CA_Cap». Одно
    подчёркивание — и кепка осталась без всех четырёх своих расцветок.
    """
    if not label:
        return []
    want = re.sub(r"[\s_]+", " ", label).strip().lower()
    for item, colours in harvested.items():
        if re.sub(r"[\s_]+", " ", item).strip().lower() == want:
            return colours
    return []


def dedupe(colours: list[dict], prototype: dict[str, str] | None = None) -> list[dict]:
    """Убрать расцветки, которые ничем не отличаются.

    Вендор кладёт пресет на каждую комбинацию, и после сужения до поверхностей
    ЭТОГО меша разные пресеты часто дают одно и то же назначение картинок —
    у топа три расцветки сошлись на `TAShirt.jpg`. Такая «расцветка» — лишний
    предмет в списке, лишняя строка библиотеки и лишние термины перевода.

    `prototype` — карты самой вещи, как она приехала из DAZ. Совпавшая с ними
    расцветка не нужна тем более: прототип уже стоит в списке первым, и именно
    поэтому первый и последний варианты выглядели одинаково.
    """
    base = dict(prototype or {})
    seen: set[tuple] = {tuple(sorted(base.items()))} if base else set()

    out = []
    for colour in sorted(colours, key=lambda c: c["name"]):
        # Сравнивается РЕЗУЛЬТАТ наложения, а не собственные карты расцветки:
        # пресет переписывает лишь часть поверхностей, остальные остаются от
        # прототипа. Расцветка топа «White» называет `shirt: TAShirt.jpg` — ровно
        # то, что у прототипа и так стоит, — и по своим картам выглядит новой, а
        # на девушке неотличима. Собственные карты сравнивать бессмысленно.
        painted = dict(base)
        painted.update({t["source"]: t["texture"] for t in colour["textures"]})
        key = tuple(sorted(painted.items()))
        if key in seen:
            continue
        seen.add(key)
        out.append(colour)
    return out
