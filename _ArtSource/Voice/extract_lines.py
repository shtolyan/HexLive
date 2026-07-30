#!/usr/bin/env python3
"""§67.10: HEXKUFA_LANGUAGE.md §7 -> hexkufa_lines.json (the doc stays the source of truth).

Parses the catalog tables: a "#### A1 · `sad_hunger` · P1 — …" header starts a
group, and every following "| реплика | русский |" row is one variant.
"""
import json, re, sys, pathlib

DOC = pathlib.Path("HEXKUFA_LANGUAGE.md")
OUT = pathlib.Path("_ArtSource/Voice/hexkufa_lines.json")

hdr = re.compile(r"^####\s+(\S+)\s+·\s+`([a-z0-9_]+)`\s+·\s+(P\d)\s+—\s+(.*)$")
row = re.compile(r"^\|\s*(.+?)\s*\|\s*(.+?)\s*\|\s*$")

groups, cur = [], None
for line in DOC.read_text(encoding="utf-8").splitlines():
    m = hdr.match(line)
    if m:
        cur = {"code": m.group(1), "id": m.group(2), "priority": m.group(3),
               "hook": m.group(4), "lines": []}
        groups.append(cur)
        continue
    if cur is None:
        continue
    if line.startswith("### ") or line.startswith("## "):
        cur = None
        continue
    r = row.match(line)
    if r:
        text, gloss = r.group(1).strip("`"), r.group(2).strip("`")
        if text in ("Реплика", "---") or set(text) <= set("-| "):
            continue
        cur["lines"].append({"text": text, "ru": gloss})

bad = [g["id"] for g in groups if len(g["lines"]) != 3]
json.dump({"language": "hexkufa", "groups": groups}, OUT.open("w"), ensure_ascii=False, indent=1)
print(f"groups={len(groups)} lines={sum(len(g['lines']) for g in groups)}")
if bad:
    print("WARN groups without exactly 3 lines:", bad, file=sys.stderr)
