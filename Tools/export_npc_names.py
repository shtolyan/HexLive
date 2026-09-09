#!/usr/bin/env python3
"""Export canonical I2 NPC names for the headless MCP server (§58.7)."""
import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Assets/Resources/I2Languages.asset"
OUTPUT = ROOT / "Server/HexLive.Server/Mcp/npc-names.json"
TERM = re.compile(r"^    - Term: (.+)$", re.MULTILINE)


def scalar(value):
    value = value.strip()
    if value.startswith('"'):
        # Name translations are single-line scalars; Unity uses JSON-compatible
        # double-quoted Unicode escapes when serializing them.
        return json.loads(value)
    if value.startswith("'"):
        if not value.endswith("'"):
            raise ValueError("Multiline I2 NPC names are not supported")
        return value[1:-1].replace("''", "'")
    return value


def export(source):
    text = source.read_text(encoding="utf-8")
    languages = re.search(r"^    mLanguages:\s*\n(.*?)(?=^    \w|\Z)", text, re.MULTILINE | re.DOTALL)
    codes = [] if languages is None else [
        scalar(value) for value in re.findall(r"^      Code: (.+)$", languages.group(1), re.MULTILINE)
    ]
    if codes != ["en", "ru"]:
        raise ValueError(f"Expected canonical I2 language order ['en', 'ru'], got {codes!r}")
    terms = list(TERM.finditer(text))
    names = {}
    for index, term in enumerate(terms):
        key = scalar(term.group(1))
        if not re.fullmatch(r"npc\.[^.]+\.name", key):
            continue
        block = text[term.end():terms[index + 1].start() if index + 1 < len(terms) else len(text)]
        translations = re.search(r"^      Languages:\s*\n      - (.*)\n      - (.*)(?:\n|$)", block, re.MULTILINE)
        if translations is None:
            raise ValueError(f"Expected two single-line translations for {key}")
        english, russian = (scalar(value) for value in translations.groups())
        if not english:
            raise ValueError(f"Missing English name for {key}")
        name_id = key[4:-5].lower()
        if name_id in names:
            raise ValueError(f"Duplicate NPC name {name_id}")
        names[name_id] = {"en": english, "ru": russian or english}
    if not names:
        raise ValueError("No NPC names found in I2")
    return json.dumps(names, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    expected = export(SOURCE)
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != expected:
            raise SystemExit("NPC names export is stale; run python3 Tools/export_npc_names.py")
        print("NPC names match canonical I2 translations")
    else:
        OUTPUT.write_text(expected, encoding="utf-8")
        print(f"Exported {len(json.loads(expected))} NPC names to {OUTPUT.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
