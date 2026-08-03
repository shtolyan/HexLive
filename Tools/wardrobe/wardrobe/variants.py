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


def _common_prefix(names: list[str]) -> str:
    """Общая приставка из ЦЕЛЫХ слов, если она есть у всех имён.

    Вендор часто повторяет название продукта в каждой вещи — «Riot Girl
    Backpack», «Riot Girl Boots», — но в пресетах материалов его не пишет:
    там просто «Backpack Denim». Без отбрасывания приставки ни одна расцветка
    Riot Girl не находила свою вещь, хотя лежала в той же папке.
    """
    if len(names) < 2:
        return ""
    words = [n.split() for n in names]
    prefix: list[str] = []
    for i in range(min(len(w) for w in words)):
        first = words[0][i]
        if any(w[i] != first for w in words):
            break
        prefix.append(first)
    # Целиком совпавшее имя приставкой не считается — иначе вещь останется без
    # имени вовсе.
    while prefix and any(len(w) <= len(prefix) for w in words):
        prefix.pop()
    return " ".join(prefix)


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

    # Каждое имя ищется и целиком, и без общей приставки продукта; список
    # отсортирован по длине, так что «CA Belt 2 Black» достаётся «CA Belt 2»,
    # а не «CA Belt».
    prefix = _common_prefix(names)
    aliases: list[tuple[str, str]] = [(n, n) for n in names]
    if prefix:
        aliases += [(n[len(prefix):].strip(), n) for n in names
                    if n.startswith(prefix) and n[len(prefix):].strip()]
    aliases.sort(key=lambda pair: len(pair[0]), reverse=True)

    found: dict[str, list[dict]] = {n: [] for n in names}
    claimed: set[Path] = set()
    for preset in sorted(folder.rglob("*.duf")):
        stem = preset.stem
        match = next(((alias, full) for alias, full in aliases if stem.startswith(alias)), None)
        if match is None:
            continue
        # Остаток режется по ТОМУ имени, которое совпало: при совпадении через
        # алиас длина полного имени другая, и название расцветки уехало бы.
        alias, owner = match
        colour = stem[len(alias):].strip(" -_") or "Default"
        # «Primal Skirt1 Iray» -> «1», а не «1 Iray»: вкус рендера мы уже выбрали
        # папкой, и в названии предмета он игроку ничего не говорит.
        for flavour in FLAVOURS:
            if colour.lower().endswith(flavour.lower()):
                colour = colour[:-len(flavour)].strip(" -_") or "Default"
                break
        maps = surfaces(preset)
        if maps:
            claimed.add(preset)
            found[owner].append({"name": colour, "surfaces": maps,
                                 "preset": preset.name})

    # Запасной путь для вендоров, которые не связывают имена вообще: у Street
    # Chic Amy вещь зовётся «Amy Boots A G3f», а её пресет — «Boots 01», и по
    # имени тут не сойтись никогда. Тогда пресет узнают по НАБОРУ ПОВЕРХНОСТЕЙ.
    #
    # Это то самое сопоставление, которое однажды поменяло местами перчатки с
    # ошейниками, поэтому оно обставлено двумя условиями: ищем только внутри
    # ОДНОГО продукта и только среди вещей, которым имя ничего не дало, и берём
    # лишь однозначное совпадение. Ничья — не выбор.
    found["__orphans__"] = [
        {"name": _colour_of(p.stem), "surfaces": surfaces(p), "preset": p.name}
        for p in sorted(folder.rglob("*.duf"))
        if p not in claimed and surfaces(p)
    ]

    return {k: v for k, v in found.items() if v}


def _colour_of(stem: str) -> str:
    for flavour in FLAVOURS:
        if stem.lower().endswith(flavour.lower()):
            return stem[:-len(flavour)].strip(" -_") or "Default"
    return stem


def adopt_orphans(harvested: dict[str, list[dict]],
                  mine: dict[str, set[str]]) -> dict[str, list[dict]]:
    """Раздать безымянные пресеты вещам — по НАБОРУ ПОВЕРХНОСТЕЙ.

    `mine` — поверхности каждой вещи, как их видит экспорт (из манифеста):
    спрашивать их у самого `.duf` вещи бесполезно, там лежит фигура, а не
    материалы.

    Это то самое сопоставление, которое однажды поменяло местами перчатки с
    ошейниками, поэтому обставлено условиями: только пресеты, которым имя
    ничего не дало, только полное вхождение в поверхности вещи и только при
    ЕДИНСТВЕННОМ подходящем. Ничья — не выбор.
    """
    orphans = harvested.pop("__orphans__", [])
    for colour in orphans:
        keys = set(colour["surfaces"])
        fits = [name for name, theirs in mine.items() if keys and keys <= theirs]
        if len(fits) == 1:
            harvested.setdefault(fits[0], []).append(colour)
    return harvested


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
        # Сначала по ФАЙЛУ: имя вещи в DAZ — это имя её .duf, а под ним и лежат
        # пресеты расцветок. Метка фигуры вендором не обязана совпадать ни с
        # тем, ни с другим («Riot Girl Backpack.duf» -> `RGBackpack`), поэтому
        # метка остаётся запасным вариантом, а не первым.
        for entry in girl.get("loaded") or []:
            stem = Path(str(entry.get("file", ""))).stem
            for name in entry.get("names") or []:
                if stem and name:
                    out.setdefault(name, stem)

        for item in girl.get("fitted") or []:
            name, label = item.get("name"), item.get("label")
            if name and label:
                out.setdefault(name, label)
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
