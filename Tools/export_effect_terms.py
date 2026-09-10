#!/usr/bin/env python3
"""Export I2 effect/need terms used by headless MCP; author translations only in I2 (§58)."""
import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Assets/Resources/I2Languages.asset"
OUTPUT = ROOT / "Server/HexLive.Server/Mcp/effect-terms.json"
TERM = re.compile(r"^    - Term: (.+)$", re.MULTILINE)


def scalar(value):
    # Unity wraps quoted YAML scalars at physical line boundaries. Fold those
    # boundaries, retaining paragraph breaks; reject block scalars explicitly.
    lines = value.strip().splitlines()
    if not lines:
        return ""
    if lines[0] in ("|", ">", "|-", ">-", "|+", ">+"):
        raise ValueError("Block I2 scalars require explicit parser support")
    folded = lines[0].strip()
    blank = 0
    for line in lines[1:]:
        line = line.strip()
        if not line:
            blank += 1
            continue
        if folded.startswith('"') and folded.endswith("\\"):
            folded = folded[:-1] + line
        else:
            folded += ("\n" * blank if blank else " ") + line
        blank = 0
    if folded.startswith('"'):
        return json.loads(folded.replace("\n", "\\n"))
    if folded.startswith("'"):
        if not folded.endswith("'"):
            raise ValueError("Unterminated quoted I2 scalar")
        return folded[1:-1].replace("''", "'")
    return folded


def required_keys():
    effects = ROOT / "Assets/HexLive/Simulation/Agents/Effects"
    catalog = (effects / "EffectDefinition.cs").read_text(encoding="utf-8-sig")
    kinds = re.findall(r"Add\(EffectKind\.(\w+),", catalog)
    keys = {f"effect.{kind.lower()}.{part}" for kind in kinds for part in ("title", "desc")}
    evaluator = (effects / "EffectEvaluator.cs").read_text(encoding="utf-8-sig")
    keys.update(re.findall(r'"(effect\.[a-z.]+)"', evaluator))
    model = (effects / "ActiveEffect.cs").read_text(encoding="utf-8-sig")
    needs = re.search(r"enum NeedKind\s*\{([^}]+)\}", model)[1]
    keys.update("need." + need.lower() for need in re.findall(r"\b[A-Z]\w*\b", needs))
    return keys


def export(source=SOURCE):
    text = source.read_text(encoding="utf-8-sig")
    languages = re.search(r"^    mLanguages:\s*\n(.*?)(?=^    \w|\Z)", text, re.MULTILINE | re.DOTALL)
    codes = [] if languages is None else re.findall(r"^      Code: (.+)$", languages[1], re.MULTILINE)
    if [scalar(code) for code in codes] != ["en", "ru"]:
        raise ValueError("Expected canonical I2 language order en, ru")
    required = required_keys()
    terms = list(TERM.finditer(text))
    result = {}
    for index, match in enumerate(terms):
        key = scalar(match[1])
        if key not in required:
            continue
        block = text[match.end():terms[index + 1].start() if index + 1 < len(terms) else len(text)]
        section = re.search(r"^      Languages:\s*\n(.*?)(?=^      (?:Flags|Languages_Touch):)", block, re.MULTILINE | re.DOTALL)
        if section is None:
            raise ValueError(f"Missing Languages for {key}")
        values = re.findall(r"^      - (.*?)(?=^      - |\Z)", section[1], re.MULTILINE | re.DOTALL)
        if len(values) != 2:
            raise ValueError(f"Expected two translations for {key}")
        english, russian = map(scalar, values)
        if not english or key in result:
            raise ValueError(f"Missing English or duplicate term: {key}")
        result[key] = {"en": english, "ru": russian or english}
    if result.keys() != required:
        raise ValueError("Missing I2 terms: " + ", ".join(sorted(required - result.keys())))
    return json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    expected = export()
    if args.check:
        if not OUTPUT.exists() or OUTPUT.read_text(encoding="utf-8") != expected:
            raise SystemExit("Effect terms are stale; run python3 Tools/export_effect_terms.py")
        print("Effect terms match canonical I2 translations")
    else:
        OUTPUT.write_text(expected, encoding="utf-8")
        print(f"Exported {len(json.loads(expected))} canonical effect/need terms")


if __name__ == "__main__":
    main()
