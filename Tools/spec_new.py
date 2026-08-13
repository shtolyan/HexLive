#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Заводит новый раздел спеки под следующим свободным номером.

    python3 Tools/spec_new.py "Волк уносит добычу"
    python3 Tools/spec_new.py --dry-run          # только показать номер

Зачем скрипт вместо «посмотреть глазами»: чтобы узнать свободный номер в старой
одноблочной спеке, надо было просмотреть 22 тысячи строк. Никто не смотрел —
и §84 в итоге заведён ДВАЖДЫ («Юка рубится как дерево» и «Одежда знает, на кого
сшита»), а между §86 и §89 зияет дыра. Здесь номер выдаётся за миллисекунду.

⭐ Номер всегда max+1, дыры НЕ переиспользуются. Дыра — не свободное место:
код уже ссылается на §87, §88, §90, §92, §95, §96, §98 и §103, просто текста
этих разделов в спеке нет. Занять такой номер новой темой значит сделать
существующие ссылки не мёртвыми, а ЛЖИВЫМИ — это хуже.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
SPEC_DIR = REPO / "Spec"
ORDER = SPEC_DIR / "ORDER.txt"

SKELETON = """\
## §{n} {title}

<!-- Почему это существует: одна-две фразы про проблему, а не про решение. -->

### §{n}.1 <первый подпункт>

TODO
"""


def next_number() -> int:
    """max+1 по ВСЕМ файлам Spec/, включая указатели (§94, §101) — их номера заняты."""
    used = [
        int(m.group(1))
        for p in SPEC_DIR.glob("*.md")
        if (m := re.fullmatch(r"(\d+)[A-Z]?", p.stem))
    ]
    if not used:
        raise SystemExit("Spec/ пуст — это не похоже на рабочий репозиторий")
    return max(used) + 1


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("title", nargs="?", help="заголовок раздела, по-русски")
    p.add_argument("--dry-run", action="store_true", help="только показать номер")
    a = p.parse_args()

    n = next_number()
    if a.dry_run or not a.title:
        print(f"следующий свободный номер: §{n}  ->  Spec/{n}.md")
        return 0

    path = SPEC_DIR / f"{n}.md"
    if path.exists():
        raise SystemExit(f"{path} уже существует — гонка с соседней сессией, перезапусти")

    title = a.title.strip()
    if "iteration" not in title.lower():
        title = f"{title} (iteration {n})"   # так озаглавлены все разделы с §41
    path.write_text(SKELETON.format(n=n, title=title), encoding="utf-8")

    with ORDER.open("a", encoding="utf-8") as fh:
        fh.write(f"{n}\n")

    subprocess.run([sys.executable, str(REPO / "Tools" / "spec_index.py")], check=True)
    print(f"\n§{n} заведён: {path.relative_to(REPO)}")
    print(f"  ссылайся из кода как §{n}; подпункты — §{n}.1, §{n}.2 внутри этого файла")
    return 0


if __name__ == "__main__":
    sys.exit(main())
