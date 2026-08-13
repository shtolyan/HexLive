#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Генератор корневого spec.md — оглавления над Spec/<N>.md.

Смысл файла: спека целиком — это ~400k токенов, её не может прочитать ни один
агент. Оглавление на ~150 строк читается целиком и стоит ~3k токенов, а дальше
агент открывает ровно те один-два раздела, которые ему нужны.

Генератор намеренно ТУПОЙ и детерминированный: только заголовки и размеры,
никаких выжимок из текста. Гейт SpecStructureGate сверяет результат с файлом
в репозитории посимвольно, и любая недетерминированность красила бы его.

    python3 Tools/spec_index.py            # перезаписать spec.md
    python3 Tools/spec_index.py --check    # только сверить (для гейта)
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
SPEC = REPO / "spec.md"
SPEC_DIR = REPO / "Spec"
ORDER = SPEC_DIR / "ORDER.txt"
PREAMBLE = SPEC_DIR / "preamble.md"

# `## §93-94 Заголовок` -> label «93-94», title «Заголовок»
HEADING = re.compile(r"^## (?:§)?(\d+[A-Z]?(?:-\d+)?)[.\s]+(.*)$")

NAV = """\
> ⭐ **Это ГЕНЕРИРУЕМОЕ оглавление. Руками не править.**
> Текст спеки живёт в `Spec/<N>.md` — по файлу на раздел.
> Перегенерировать: `python3 Tools/spec_index.py`
>
> **`§N` → `Spec/N.md`, механически, без поиска.** Увидел `§105.14` в
> комментарии C# — открывай `Spec/105.md`. Подпункты (`§54.14`, `§21.21B`)
> живут внутри файла своего раздела.
>
> **Новый раздел заводится ТОЛЬКО через `python3 Tools/spec_new.py "Название"`** —
> он выдаёт следующий свободный номер. Не выбирай номер глазами: §84 заведён
> в спеке дважды именно потому, что свободный номер выбирали вручную.
>
> Номера §N не меняются НИКОГДА: на них 3773 ссылки из C#, 364 из
> Tests/Server/Tools и 1275 внутренних. Дыры в нумерации (87, 88, 90, 92,
> 95, 96, 98, 103) — это история, а не ошибка.
"""

GROUPS = [
    ("Обзор — исторический слой (§1-§18)", lambda n: n <= 18),
    ("Глубокая архитектура (§19-§35)", lambda n: 19 <= n <= 39),
    ("Итерации (§40+)", lambda n: n >= 40),
]


def read_order() -> list[str]:
    return [
        line.strip()
        for line in ORDER.read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.startswith("#")
    ]


def render() -> str:
    out = [PREAMBLE.read_text(encoding="utf-8").rstrip("\n"), "", NAV]

    entries = []
    for key in read_order():
        path = SPEC_DIR / f"{key}.md"
        text = path.read_text(encoding="utf-8")
        first = text.splitlines()[0] if text else ""
        m = HEADING.match(first)
        if not m:
            raise SystemExit(f"Spec/{key}.md начинается не с заголовка раздела: {first!r}")
        label, title = m.group(1), m.group(2).strip()
        entries.append((key, label, title))

    for caption, belongs in GROUPS:
        rows = [e for e in entries if belongs(int(re.match(r"\d+", e[0]).group()))]
        if not rows:
            continue
        out += ["", f"## {caption}", "", "| § | Раздел |", "|---|---|"]
        for key, label, title in rows:
            out.append(f"| [§{label}](Spec/{key}.md) | {title.replace('|', '\\|')} |")

    # Размеров разделов здесь намеренно НЕТ. С ними оглавление устаревало бы от
    # каждой правки текста, и гейт требовал бы регенерации на каждый абзац —
    # ровно та трения, из-за которой инструмент начинают обходить. Без них
    # индекс меняется только когда раздел добавили, убрали или переименовали.
    out += ["", f"*Разделов: {len(entries)}.*", ""]
    return "\n".join(out)


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--check", action="store_true",
                   help="сверить spec.md с генерацией, не записывая")
    a = p.parse_args()

    fresh = render()
    if a.check:
        current = SPEC.read_text(encoding="utf-8") if SPEC.exists() else ""
        if current == fresh:
            print("✅ spec.md актуален")
            return 0
        print("❌ spec.md разошёлся с Spec/ — перегенерируй: python3 Tools/spec_index.py")
        import difflib
        sys.stdout.writelines(list(difflib.unified_diff(
            current.splitlines(keepends=True), fresh.splitlines(keepends=True),
            fromfile="spec.md", tofile="генерация", n=1))[:60])
        return 1

    SPEC.write_text(fresh, encoding="utf-8")
    print(f"spec.md перезаписан: {fresh.count(chr(10))} строк оглавления "
          f"над {len(read_order())} разделами")
    return 0


if __name__ == "__main__":
    sys.exit(main())
