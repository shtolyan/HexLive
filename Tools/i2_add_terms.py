#!/usr/bin/env python3
"""Залить термины I2 из TSV в Assets/Resources/I2Languages.asset.

Spec §58.3 запрещает писать строки в C#: всё, что видит игрок, — это термин в
ассете локализации. Но §136 приносит сотни коротких фраз дневника, и править
ради них YAML руками — значит гарантированно сломать отступ или потерять
строку. Копирайтинг обязан жить в текстовом файле, который можно читать
диффом.

Формат TSV: три колонки, разделитель — таб.

    ключ<TAB>English<TAB>Русский

Пустые строки и строки, начинающиеся с #, игнорируются. Внутри перевода
допустим \\n — он уедет в YAML как настоящий перенос строки.

Существующий термин ОБНОВЛЯЕТСЯ на месте (порядок терминов не меняется),
новые дописываются в конец списка. Скрипт идемпотентен: повторный запуск на
том же файле не делает ничего.

    python3 Tools/i2_add_terms.py _ArtSource/Journal/journal_lines.tsv
    python3 Tools/i2_add_terms.py --check <файл>     # ничего не писать, только отчёт
"""

import argparse
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
ASSET = REPO / "Assets" / "Resources" / "I2Languages.asset"

# Unity при пересохранении ассета убирает одинарные кавычки у простых строк
# (`- Term: menu.continue`), а сам скрипт исторически писал `- Term: 'x'`.
# Оба написания — один и тот же YAML, поэтому принимаем оба.
TERM_RE = re.compile(r"^    - Term: (?:'(?P<q>.*)'|\"(?P<dq>.*)\"|(?P<bare>\S.*?))\s*$")
# Конец списка терминов — первая строка того же уровня, что и mTerms.
END_RE = re.compile(r"^    (CaseInsensitiveTerms|OnMissingTranslation|mTerm_AppName|mLanguages):")


def quote(text):
    """YAML-строка в одинарных кавычках: удваиваем апостроф, \\n — в перенос."""
    text = text.replace("\\n", "\n")
    body = text.replace("'", "''")
    if "\n" in body:
        # I2 хранит многострочный перевод обычным блоком в кавычках.
        body = body.replace("\n", "\n\n      ")
    return "'" + body + "'"


def block(term, english, russian):
    return [
        "    - Term: '%s'" % term.replace("'", "''"),
        "      TermType: 0",
        "      Description: ",
        "      Languages:",
        "      - %s" % quote(english),
        "      - %s" % quote(russian),
        "      Flags: 0101",
        "      Languages_Touch: []",
    ]


def read_tsv(path):
    rows = []
    seen = set()
    for lineno, raw in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) != 3:
            sys.exit("%s:%d: нужно ровно 3 колонки через таб, а их %d:\n  %s"
                     % (path, lineno, len(parts), line))
        term, en, ru = (p.strip() for p in parts)
        if not term:
            sys.exit("%s:%d: пустой ключ" % (path, lineno))
        if not en or not ru:
            # §58: термин с пустой колонкой рендерится как ключ — то есть
            # выглядит как поломка. Лучше не пустить его в ассет вовсе.
            sys.exit("%s:%d: у термина '%s' пустой перевод — заполни обе колонки"
                     % (path, lineno, term))
        if term in seen:
            sys.exit("%s:%d: ключ '%s' встречается дважды" % (path, lineno, term))
        seen.add(term)
        rows.append((term, en, ru))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("tsv", type=pathlib.Path)
    parser.add_argument("--check", action="store_true",
                        help="не писать, только сказать что изменилось бы")
    args = parser.parse_args()

    if not args.tsv.exists():
        sys.exit("нет файла: %s" % args.tsv)
    if not ASSET.exists():
        sys.exit("нет ассета локализации: %s" % ASSET)

    rows = read_tsv(args.tsv)
    lines = ASSET.read_text(encoding="utf-8").splitlines()

    # Разметить, где начинается и кончается каждый существующий термин.
    spans = {}
    order = []
    current = None
    end_index = None
    for i, line in enumerate(lines):
        match = TERM_RE.match(line)
        if match:
            if current is not None:
                spans[current][1] = i
            current = (match.group("q") if match.group("q") is not None
                       else match.group("dq") if match.group("dq") is not None
                       else match.group("bare"))
            order.append(current)
            spans[current] = [i, None]
            continue
        if END_RE.match(line) and current is not None:
            spans[current][1] = i
            end_index = i
            current = None

    if end_index is None:
        sys.exit("не нашёл конец списка mTerms — формат ассета изменился, "
                 "правь скрипт, а не ассет руками")

    updated, added, unchanged = [], [], []
    # Идём с конца, чтобы правки не сдвигали индексы ещё не обработанных.
    for term, en, ru in sorted(rows, key=lambda r: -spans.get(r[0], [10**9])[0]):
        new_block = block(term, en, ru)
        if term in spans:
            start, stop = spans[term]
            if lines[start:stop] == new_block:
                unchanged.append(term)
                continue
            lines[start:stop] = new_block
            updated.append(term)
        else:
            added.append(term)

    # Новые — одним куском в конец списка, в порядке файла.
    tail = []
    for term, en, ru in rows:
        if term in added:
            tail.extend(block(term, en, ru))
    if tail:
        # end_index мог сдвинуться после правок выше — ищем заново.
        for i, line in enumerate(lines):
            if END_RE.match(line):
                end_index = i
                break
        lines[end_index:end_index] = tail

    print("термины: %d обновлено, %d добавлено, %d без изменений"
          % (len(updated), len(added), len(unchanged)))

    if args.check:
        for term in updated:
            print("  ~ %s" % term)
        for term in added:
            print("  + %s" % term)
        return

    if not updated and not added:
        return

    ASSET.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("записано: %s" % ASSET.relative_to(REPO))


if __name__ == "__main__":
    main()
