#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Разрез spec.md на Spec/<N>.md — по файлу на раздел §N.

Скрипт одноразовый по назначению, но коммитится: он И ЕСТЬ аудит миграции.
Режим `--verify` доказывает, что при разрезе не потерялось ни байта, и его
можно прогнать в любой момент против любого дореформенного коммита.

⭐ ПРАВИЛО РАЗРЕЗА. Заголовок `## …` начинает НОВЫЙ раздел, только если его
номер БОЛЬШЕ номера текущего раздела. Резать по «любому ##» нельзя: внутри
§24 и §30 живут примеры плана — `## 1. MoveToTile`, `## 2. MoveToJunction`,
`## 3. Interact`, — и наивный сплиттер порезал бы файл по ним, растащив два
раздела на восемь огрызков. Правило монотонности отбрасывает ровно эти шесть
строк и ничего больше (проверено на живом файле).

Две аномалии правило ловит верно, но решение по ним — человеческое, поэтому
они заданы явными списками ниже, а не подгонкой регулярки. Список — это
документ: он объясняет ПОЧЕМУ, чего регулярка объяснить не может.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
SPEC = REPO / "spec.md"
SPEC_DIR = REPO / "Spec"
ORDER = SPEC_DIR / "ORDER.txt"
PREAMBLE = SPEC_DIR / "preamble.md"

# `## §135 Заголовок`, `## 21. Заголовок`, `## 29A. Заголовок`, `## §93-94 Заголовок`
HEADING = re.compile(r"^## (?:§)?(\d+)([A-Z]?)(?:-(\d+))?[.\s]")

# Заголовок, который ОБЯЗАН начать раздел, хотя его номер не больше текущего.
#
# §75 «Состав» физически лежит в файле ПОСЛЕ §75A «Симпатия» — порядок в
# документе такой исторически. Без этой строки правило монотонности сложило бы
# §75 внутрь Spec/75A.md, и ссылки на §75 из кода уехали бы не в тот файл.
FORCE_NEW_SECTION = {
    "## §75 Состав — две ручки, а не два списка (iteration 75)",
}

# Заголовок, который НЕ начинает раздел, хотя выглядит как заголовок.
#
# §84 заведён в спеке ДВАЖДЫ: «Юка рубится как дерево» и «Одежда знает, на кого
# сшита». Это ровно та болезнь, которую лечит Tools/spec_new.py. Обе части
# кладутся в Spec/84.md: перенумерация исключена (24 ссылки на §84 из кода
# сегодня и так неоднозначны, а после слияния все резолвятся в один файл).
FORCE_NOT_SECTION = {
    "## §84 Одежда знает, на кого сшита (iteration 84)",
}


def sort_key(num: str, suffix: str) -> tuple[int, str]:
    """29 < 29A < 29B < 30 — буквенный суффикс сортируется после голого номера."""
    return (int(num), suffix)


def parse(text: str) -> tuple[str, list[tuple[str, str, str]], dict[str, str]]:
    """Разбирает spec.md. Возвращает (преамбула, [(ключ, заголовок, тело)], указатели).

    Тело нарезано по строкам ВЕРБАТИМ, вместе с концами строк и хвостовыми
    пустыми строками, — склейка кусков обязана дать исходный файл байт в байт.

    Указатели — это второй номер сдвоенного заголовка (§93-94, §100-101).
    Без них правило «§N → Spec/N.md» имело бы дыры ровно там, где документ и
    так путаный: код ссылается на §94, а файла 94.md не существует.
    """
    lines = text.splitlines(keepends=True)

    starts: list[tuple[int, str]] = []  # (индекс строки, ключ раздела)
    pointers: dict[str, str] = {}       # ключ-указатель -> ключ настоящего раздела
    current: tuple[int, str] | None = None
    for i, line in enumerate(lines):
        m = HEADING.match(line)
        if not m:
            continue
        stripped = line.rstrip("\n")
        num, suffix, upto = m.group(1), m.group(2), m.group(3)
        key = num + suffix
        candidate = sort_key(num, suffix)

        if stripped in FORCE_NOT_SECTION:
            continue
        if stripped in FORCE_NEW_SECTION:
            pass
        elif current is not None and candidate <= current:
            continue  # пример внутри раздела, а не раздел

        starts.append((i, key))
        current = candidate
        if upto:
            for n in range(int(num) + 1, int(upto) + 1):
                pointers[str(n)] = key

    if not starts:
        raise SystemExit("не найдено ни одного раздела — правило разреза сломалось")

    preamble = "".join(lines[: starts[0][0]])

    sections: list[tuple[str, str, str]] = []
    for n, (start, key) in enumerate(starts):
        end = starts[n + 1][0] if n + 1 < len(starts) else len(lines)
        body = "".join(lines[start:end])
        sections.append((key, lines[start].rstrip("\n"), body))
    return preamble, sections, pointers


def do_split(dry_run: bool) -> int:
    text = SPEC.read_text(encoding="utf-8")
    preamble, sections, pointers = parse(text)

    seen: dict[str, str] = {}
    for key, heading, _ in sections:
        if key in seen:
            raise SystemExit(
                f"§{key} встретился дважды как самостоятельный раздел:\n"
                f"  {seen[key]}\n  {heading}\n"
                "Реши руками: либо в FORCE_NOT_SECTION (склеить в один файл), "
                "либо дать одному из них свободный номер."
            )
        seen[key] = heading

    print(f"разделов: {len(sections)}, преамбула: {len(preamble.splitlines())} строк, "
          f"указателей: {len(pointers)}")
    if dry_run:
        for key, heading, body in sections:
            print(f"  Spec/{key}.md  {len(body.splitlines()):5d} строк  {heading[:60]}")
        for key, target in sorted(pointers.items(), key=lambda kv: int(kv[0])):
            print(f"  Spec/{key}.md  указатель -> {target}.md")
        return 0

    SPEC_DIR.mkdir(exist_ok=True)
    PREAMBLE.write_text(preamble, encoding="utf-8")
    for key, _, body in sections:
        (SPEC_DIR / f"{key}.md").write_text(body, encoding="utf-8")
    for key, target in pointers.items():
        # Указателей НЕТ в ORDER.txt — склейка их не видит, доказательство
        # побайтового равенства от них не зависит.
        (SPEC_DIR / f"{key}.md").write_text(
            f"§{key} описан вместе с §{target} — см. [{target}.md]({target}.md).\n",
            encoding="utf-8",
        )
    ORDER.write_text(
        "# Порядок разделов в документе. Он НЕ всегда возрастающий: §75A лежит\n"
        "# перед §75 — так сложилось исторически, и склейка обязана его повторять.\n"
        "# Новую строку сюда дописывает Tools/spec_new.py.\n"
        + "".join(f"{key}\n" for key, _, _ in sections),
        encoding="utf-8",
    )
    print(f"записано: Spec/*.md ({len(sections)} разделов + {len(pointers)} указателя), "
          "Spec/ORDER.txt, Spec/preamble.md")
    return 0


def read_order() -> list[str]:
    return [
        line.strip()
        for line in ORDER.read_text(encoding="utf-8").splitlines()
        if line.strip() and not line.startswith("#")
    ]


def rejoin() -> str:
    parts = [PREAMBLE.read_text(encoding="utf-8")]
    for key in read_order():
        parts.append((SPEC_DIR / f"{key}.md").read_text(encoding="utf-8"))
    return "".join(parts)


def do_verify(ref: str) -> int:
    """Склейка Spec/ обязана совпасть с дореформенным spec.md БАЙТ В БАЙТ."""
    original = subprocess.run(
        ["git", "-C", str(REPO), "show", f"{ref}:spec.md"],
        capture_output=True, check=True,
    ).stdout.decode("utf-8")
    joined = rejoin()

    if joined == original:
        print(f"✅ склейка Spec/ идентична {ref}:spec.md "
              f"({len(original.splitlines())} строк, {len(original.encode())} байт)")
        return 0

    print(f"❌ склейка РАСХОДИТСЯ с {ref}:spec.md")
    print(f"   было {len(original.splitlines())} строк / {len(original.encode())} байт")
    print(f"   стало {len(joined.splitlines())} строк / {len(joined.encode())} байт")
    import difflib
    diff = list(difflib.unified_diff(
        original.splitlines(keepends=True), joined.splitlines(keepends=True),
        fromfile=f"{ref}:spec.md", tofile="склейка Spec/", n=1))
    sys.stdout.writelines(diff[:200])
    if len(diff) > 200:
        print(f"... ещё {len(diff) - 200} строк диффа")
    return 1


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--verify", metavar="REF",
                   help="склеить Spec/ и сравнить с <REF>:spec.md байт в байт")
    p.add_argument("--dry-run", action="store_true",
                   help="показать разбиение, ничего не записывая")
    a = p.parse_args()
    if a.verify:
        return do_verify(a.verify)
    return do_split(a.dry_run)


if __name__ == "__main__":
    sys.exit(main())
