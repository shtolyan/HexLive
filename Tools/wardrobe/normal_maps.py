#!/usr/bin/env python3
"""Import the normal maps DAZ ships but the wear extraction never copied.

Measured before writing this: `_BumpMap` is bound on 12 of 2577 built wear
materials (0.5%), while the DAZ library binds a `Normal Map` channel in 2156
material zones. The maps exist; nothing carried them across.

Linking is done by DIFFUSE TEXTURE FILENAME, not by surface name. The manifest
already records the diffuse each surface uses (`texture`), and the DAZ preset
binds the normal map on the same zone as that diffuse — so the join is exact.
Matching on surface names instead would be guesswork: WARDROBE_SPEC.md warns
that a garment is identified by its mesh key and never by its display name,
and the same trap applies to surfaces (`metal` appears in dozens of products).

Usage
-----
    python3 Tools/wardrobe/normal_maps.py                # coverage report only
    python3 Tools/wardrobe/normal_maps.py --apply        # copy + write manifests
"""
from __future__ import annotations

import argparse
import collections
import glob
import gzip
import json
import os
import shutil
import sys
import urllib.parse

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DROPS = os.path.join(REPO, "Assets", "Editor", "WearDrops")
WEAR = os.path.join(REPO, "Assets", "ImportedActors", "Wear")
LIB = r"C:\Users\shtolyan\Documents\DAZ 3D\Studio\My Library"

IMG_EXT = (".jpg", ".jpeg", ".png", ".tif", ".tiff")


def load_duf(path):
    raw = open(path, "rb").read()
    if raw[:2] == b"\x1f\x8b":
        raw = gzip.decompress(raw)
    return json.loads(raw.decode("utf-8", "replace"))


def resolve(url: str) -> str | None:
    """DAZ stores '/Runtime/Textures/Vendor/Prod%20Name/x.jpg' — library-relative."""
    if not url:
        return None
    p = urllib.parse.unquote(url).lstrip("/\\").replace("/", os.sep)
    full = os.path.join(LIB, p)
    return full if os.path.isfile(full) else None


def channels(container):
    out = {}
    for named in ("diffuse", "bump"):
        blk = container.get(named)
        if isinstance(blk, dict) and "channel" in blk:
            c = blk["channel"]
            if c.get("image_file"):
                out[named] = c["image_file"]
    for ex in container.get("extra", []):
        for ch in ex.get("channels", []):
            c = ch.get("channel", {})
            if c.get("id") and c.get("image_file"):
                out[c["id"]] = c["image_file"]
    return out


def build_index():
    """-> {diffuse_basename_lower: normal_map_abs_path}

    Later presets do not overwrite an earlier hit: the first preset that binds
    a normal map beside a given diffuse wins, and colourways of one garment all
    reuse the same normal map anyway.
    """
    idx = {}
    scanned = 0
    for root, _dirs, fs in os.walk(LIB):
        for f in fs:
            if not f.lower().endswith(".duf"):
                continue
            p = os.path.join(root, f)
            try:
                d = load_duf(p)
            except Exception:
                continue
            scanned += 1
            base = {ml.get("id"): channels(ml) for ml in d.get("material_library", [])}
            mats = d.get("scene", {}).get("materials", [])
            entries = []
            if mats:
                for m in mats:
                    k = (m.get("url") or "").lstrip("#")
                    merged = dict(base.get(k, {}))
                    merged.update(channels(m))
                    entries.append(merged)
            else:
                entries = list(base.values())
            for ch in entries:
                dif = ch.get("Diffuse Color") or ch.get("diffuse")
                nrm = ch.get("Normal Map")
                if not dif or not nrm:
                    continue
                key = os.path.basename(urllib.parse.unquote(dif)).lower()
                if key in idx:
                    continue
                got = resolve(nrm)
                if got:
                    idx[key] = got
    return idx, scanned


def manifests():
    out = []
    for p in sorted(glob.glob(os.path.join(DROPS, "*.json"))):
        b = os.path.basename(p)
        if b.startswith("_") or b == "import-plan.json":
            continue
        out.append(p)
    return out


def run(apply_changes: bool) -> int:
    print("indexing DAZ presets ...", flush=True)
    idx, scanned = build_index()
    print(f"  presets scanned: {scanned}")
    print(f"  diffuse->normal pairs: {len(idx)}\n")

    hits = 0
    misses = collections.Counter()
    copied = 0
    per_garment = collections.Counter()
    changed = 0

    for path in manifests():
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        touched = False
        for g in doc.get("garments", []):
            folder = g.get("folder") or ""
            for m in g.get("materials", []):
                tex = m.get("texture")
                if not tex:
                    continue
                src = idx.get(os.path.basename(tex).lower())
                if not src:
                    misses[os.path.basename(tex)] += 1
                    continue
                hits += 1
                per_garment[folder] += 1
                dest_dir = os.path.join(WEAR, folder, "Textures")
                nm = os.path.basename(src)
                if apply_changes:
                    os.makedirs(dest_dir, exist_ok=True)
                    dest = os.path.join(dest_dir, nm)
                    if not os.path.isfile(dest):
                        shutil.copy2(src, dest)
                        copied += 1
                if m.get("normal") != nm:
                    m["normal"] = nm
                    touched = True
        if touched and apply_changes:
            try:
                had_nl = open(path, "rb").read().endswith(b"\n")
            except OSError:
                had_nl = False
            text = json.dumps(doc, indent=2, ensure_ascii=False)
            with open(path, "w", encoding="utf-8", newline="\r\n") as fh:
                fh.write(text)
                if had_nl:
                    fh.write("\n")
            changed += 1

    print(f"materials with a normal map found: {hits}")
    print(f"garments covered: {len(per_garment)}")
    for k, v in per_garment.most_common(30):
        print(f"    {v:3d}  {k}")
    print(f"\nmaterials with no normal map in DAZ: {sum(misses.values())}")
    if apply_changes:
        print(f"\ntextures copied: {copied}\nmanifests rewritten: {changed}")
        print("Re-run the wear extraction to rebuild the .mat files.")
    else:
        print("\n(report only — pass --apply to copy and write)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true",
                    help="copy the maps and record them in the manifests")
    a = ap.parse_args()
    return run(a.apply)


if __name__ == "__main__":
    sys.exit(main())
