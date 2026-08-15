#!/usr/bin/env python3
"""Fill `metallic`/`smoothness` in the wear drop manifests by surface class.

Why this exists
---------------
DAZ carries NO usable gloss data. Measured over the whole library
(5350 `.duf`, 22595 material zones): `Metallic Weight` is 0.0 in every zone
that has it, `Glossy Roughness` is 0.0 in all but 20, and every other
shine channel (`Glossy Reflectivity`, `Glossy Layered Weight`, `Glossy Weight`,
`Top Coat Weight`, `Refraction Weight`, `Dual Lobe Specular Weight`,
`Base Mixing`) is a single constant across the entire library, varying
*within a file* in 0 files out of 5350. So the flat look is not export loss —
the numbers were never authored. They have to be invented, once, consistently.

The values below are the MEDIANS of the thirty materials tuned by hand on
AnarchyCorset / AnarchyStuddedCollar / VampHalter / LuxuryBikiniBriefs /
WildBra. This generalizes that taste to the rest of the wardrobe instead of
re-dragging a slider 600 times.

Cloth is deliberately left at 0/0: it was never hand-tuned because matte is
already right for it. Anything this script cannot confidently classify is
left untouched and reported, so nothing is silently guessed.

Usage
-----
    python3 Tools/wardrobe/surface_pbr.py --dry-run     # plan only
    python3 Tools/wardrobe/surface_pbr.py --check       # validate vs hand-tuned
    python3 Tools/wardrobe/surface_pbr.py --apply       # write manifests

After --apply, re-run the wear extraction so the .mat files are rebuilt.
"""
from __future__ import annotations

import argparse
import collections
import glob
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DROPS = os.path.join(REPO, "Assets", "Editor", "WearDrops")

# Calibrated on the hand-tuned thirty: median metallic / median smoothness.
#   metal   0.657 / 0.649      leather 0.335 / 0.330
# Rounded to two places; the source spread was ±0.25, so precision beyond
# that would be false.
CLASSES = {
    "metal":   (0.66, 0.65),
    "leather": (0.34, 0.33),
    # Left explicitly at matte. Present so `--check` can assert that a cloth
    # surface is classified as cloth rather than falling through to unknown.
    "cloth":   (0.0, 0.0),
}

# Ordered: the first pattern that matches wins, so specific beats generic.
# Patterns match the DAZ surface name (manifest `source`), lowercased.
RULES = [
    # --- metal ---------------------------------------------------------
    ("metal", r"\b(metal|metals|metall?ic)\b"),
    ("metal", r"(rivet|stud|grommet|eyelet|buckle|bucklet|clasp|chain|zipper|"
              r"zip|hardware|hasp|snap|clip|d.?ring|o.?ring)"),
    ("metal", r"\b(ring|rings)\b"),
    ("metal", r"(gold|golden|silver|brass|bronze|chrome|steel|iron|copper)"),
    ("metal", r"\b(tip|tips|frame|blade|spike|spikes|plate|armor|armour)\b"),
    ("metal", r"\b(button|buttons)\b"),

    # --- leather / straps ----------------------------------------------
    ("leather", r"(leather|suede)"),
    ("leather", r"\b(strap|straps|strap_outer|strap_inner|strap_edge)\b"),
    ("leather", r"(corset|harness|holster|sheath|scabbard|bandolier)"),
    ("leather", r"\b(belt|belts|collar|halter|cuff|cuffs|tab|tabs)\b"),
    ("leather", r"\b(boot|boots|shoe|shoes|sandal|sandals)\b"),

    # --- cloth (explicitly matte; no change, but classified) ------------
    ("cloth", r"(fabric|cloth|cotton|denim|linen|silk|wool|knit|mesh|lace|"
              r"velvet|satin)"),
    ("cloth", r"\b(shirt|tshirt|t_shirt|tee|top|blouse|turtleneck|sweater|"
              r"jacket|hood|hoodie)\b"),
    ("cloth", r"\b(pant|pants|short|shorts|skirt|dress|legging|leggings)\b"),
    ("cloth", r"\b(panty|panties|bra|brief|briefs|thong|underwear|lingerie)\b"),
    ("cloth", r"\b(sleeve|sleeves|sock|socks|stocking|stockings|glove|gloves)\b"),
    ("cloth", r"\b(waistband|band|bands|ruffle|trim|hem|stitch|stitches|"
              r"seam|cord|lace|laces|knot|string|ribbon|bow)\b"),
    ("cloth", r"\b(sole|in_sole|insole|under_sole|mid_sole|midsole|outsole|"
              r"heel|tread)\b"),

    # --- substring tier -------------------------------------------------
    # Vendors glue words together (`M1Buttons`, `AmyBoots_HeelTip`,
    # `BottomTrim`, `fur_plain`), which no word-boundary pattern can reach.
    # Only tokens that cannot plausibly appear inside an unrelated garment
    # surface name go here — `ring` and `tip` are deliberately NOT in this
    # tier, since `string` and `multiple` would match them.
    ("metal", r"(button|buckl|rivet|eyelet|grommet|zipper|chain)"),
    ("cloth", r"(sole|heel|boot|trim|fur|ruffle|stitch|lace|seam|hem)"),
    ("leather", r"(strap|collar|cuff|belt)"),
]

COMPILED = [(cls, re.compile(pat)) for cls, pat in RULES]

# Names that carry no meaning at all — never guess from these.
OPAQUE = re.compile(r"^(mat\d*|material[\s._-]*\d*|surface\d*|default\d*|"
                    r"lambert\d*|phong\d*|blinn\d*|\d+)$")


def classify(surface: str) -> str | None:
    """-> 'metal' | 'leather' | 'cloth' | None (unknown, leave alone)."""
    s = (surface or "").strip().lower()
    if not s or OPAQUE.match(s):
        return None
    # `_` is a word character, so \bstraps\b would miss `front_straps` — the
    # exact miss that showed up on three of the hand-tuned fifteen. Split the
    # separators into spaces so word-boundary patterns see the parts.
    s = re.sub(r"[_\-.]+", " ", s)
    for cls, rx in COMPILED:
        if rx.search(s):
            return cls
    return None


def manifests() -> list[str]:
    out = []
    for p in sorted(glob.glob(os.path.join(DROPS, "*.json"))):
        base = os.path.basename(p)
        if base.startswith("_") or base == "import-plan.json":
            continue
        out.append(p)
    return out


def write_manifest(path: str, doc) -> None:
    """Rewrite a drop manifest byte-compatibly with how it is authored.

    Two-space indent, CRLF and literal UTF-8 Cyrillic, matching the existing
    files exactly. Getting any of these wrong reformats the whole file and
    buries a 22-value change under 17k lines of diff. The trailing newline is
    per-file, not a convention: `jmrluxdevi.json` has one, `anarchy.json` does
    not, so it is read off the file being replaced rather than assumed.
    """
    try:
        had_newline = open(path, "rb").read().endswith(b"\n")
    except OSError:
        had_newline = False
    text = json.dumps(doc, indent=2, ensure_ascii=False)
    with open(path, "w", encoding="utf-8", newline="\r\n") as fh:
        fh.write(text)
        if had_newline:
            fh.write("\n")


def iter_materials(doc):
    for g in doc.get("garments", []):
        for m in g.get("materials", []):
            yield g, m


MAT_ROOT = os.path.join(REPO, "Assets", "ImportedActors", "Wear")
_MET_RX = re.compile(r"- _Metallic: ([\d.]+)")
_SMO_RX = re.compile(r"- _Smoothness: ([\d.]+)")


def read_mat(folder: str, source: str):
    """-> (metallic, smoothness) from the built .mat, or None if absent/zero.

    The extractor rebuilds every .mat from the manifest, so a value tuned by
    hand in the Inspector survives only until the next extraction. This lifts
    those values back into the manifest, which is the actual source of truth.
    """
    base = os.path.join(MAT_ROOT, folder, "Materials")
    if not os.path.isdir(base):
        return None
    # colourway subfolders repeat the same surface; the top-level file is the
    # prototype the variants are generated from.
    for cand in (os.path.join(base, source + ".mat"),):
        if os.path.isfile(cand):
            txt = open(cand, encoding="utf-8", errors="replace").read()
            m, s = _MET_RX.search(txt), _SMO_RX.search(txt)
            mv = float(m.group(1)) if m else 0.0
            sv = float(s.group(1)) if s else 0.0
            if mv != 0.0 or sv != 0.0:
                return mv, sv
    return None


def adopt(apply_changes: bool) -> int:
    """Pull hand-tuned .mat values back into the manifests."""
    found = 0
    files = 0
    for path in manifests():
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)
        touched = False
        for garment, mat in iter_materials(doc):
            got = read_mat(garment.get("folder", ""), mat.get("source", ""))
            if not got:
                continue
            mv, sv = got
            if (float(mat.get("metallic", 0) or 0) == mv
                    and float(mat.get("smoothness", 0) or 0) == sv):
                continue
            print(f"  adopt {garment.get('folder')}/{mat.get('source')}: "
                  f"metallic={mv} smoothness={sv}")
            mat["metallic"] = mv
            mat["smoothness"] = sv
            touched = True
            found += 1
        if touched and apply_changes:
            write_manifest(path, doc)
            files += 1
    print(f"\nhand-tuned materials adopted: {found}")
    if apply_changes:
        print(f"manifests rewritten: {files}")
    else:
        print("(dry run — nothing written; add --apply to write)")
    return 0


def run(apply_changes: bool) -> int:
    stats = collections.Counter()
    unknown = collections.Counter()
    changed_files = 0

    for path in manifests():
        with open(path, encoding="utf-8") as fh:
            doc = json.load(fh)

        touched = False
        for garment, mat in iter_materials(doc):
            cls = classify(mat.get("source", ""))
            stats[cls or "UNKNOWN"] += 1
            if cls is None:
                unknown[mat.get("source", "?")] += 1
                continue
            met, smo = CLASSES[cls]
            # Never clobber a value someone already set on purpose.
            cur_m = float(mat.get("metallic", 0.0) or 0.0)
            cur_s = float(mat.get("smoothness", 0.0) or 0.0)
            if cur_m != 0.0 or cur_s != 0.0:
                stats["kept-existing"] += 1
                continue
            if met == 0.0 and smo == 0.0:
                continue  # cloth: already correct, no write
            mat["metallic"] = met
            mat["smoothness"] = smo
            touched = True
            stats["written"] += 1

        if touched and apply_changes:
            write_manifest(path, doc)
            changed_files += 1

    print("=== classification ===")
    for k in ("metal", "leather", "cloth", "UNKNOWN", "kept-existing", "written"):
        if stats[k]:
            print(f"  {k:14s} {stats[k]}")
    print(f"\n=== unclassified surface names: {len(unknown)} distinct ===")
    for name, n in unknown.most_common(30):
        print(f"  {n:3d}  {name}")
    if apply_changes:
        print(f"\nmanifests rewritten: {changed_files}")
        print("Now re-run the wear extraction to rebuild the .mat files.")
    else:
        print("\n(dry run — nothing written; pass --apply to write)")
    return 0


# --- validation against the hand-tuned thirty ---------------------------
HAND_TUNED = {
    # surface name -> (metallic, smoothness) the player chose
    "buckles": (0.807, 0.736), "bucklet": (0.296, 0.284), "corset": (0.444, 0.235),
    "eyelets": (0.603, 0.524), "front_straps": (0.210, 0.311), "rings": (0.754, 0.666),
    "rivets": (0.544, 0.715), "shldr_straps": (0.520, 0.338), "side_straps": (0.665, 0.333),
    "straps": (0.520, 0.240), "collar": (0.363, 0.502), "tabs": (0.307, 0.387),
    "tip": (0.802, 0.824), "halter": (0.248, 0.327), "golden": (0.563, 0.649),
}
EXPECTED = {
    "buckles": "metal", "bucklet": "metal", "eyelets": "metal", "rings": "metal",
    "rivets": "metal", "tip": "metal", "golden": "metal",
    "corset": "leather", "front_straps": "leather", "shldr_straps": "leather",
    "side_straps": "leather", "straps": "leather", "collar": "leather",
    "tabs": "leather", "halter": "leather",
}


def check() -> int:
    print("=== classifier vs the hand-tuned thirty ===")
    bad = 0
    for name, want in EXPECTED.items():
        got = classify(name)
        mark = "ok " if got == want else "FAIL"
        if got != want:
            bad += 1
        hm, hs = HAND_TUNED[name]
        cm, cs = CLASSES.get(got, (0.0, 0.0))
        print(f"  {mark} {name:14s} want={want:8s} got={str(got):8s} "
              f"hand=({hm:.3f},{hs:.3f}) class=({cm:.2f},{cs:.2f}) "
              f"delta=({cm-hm:+.3f},{cs-hs:+.3f})")
    print(f"\nmisclassified: {bad}/{len(EXPECTED)}")
    return 1 if bad else 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    g = ap.add_mutually_exclusive_group()
    g.add_argument("--apply", action="store_true", help="write the manifests")
    g.add_argument("--dry-run", action="store_true", help="plan only (default)")
    g.add_argument("--check", action="store_true",
                   help="validate the classifier against the hand-tuned thirty")
    # Not in the exclusive group: --adopt combines with --apply to write.
    ap.add_argument("--adopt", action="store_true",
                    help="lift hand-tuned .mat values into the manifests "
                         "(run this BEFORE --apply so they are not overwritten)")
    a = ap.parse_args()
    if a.check:
        return check()
    if a.adopt:
        return adopt(apply_changes=a.apply)
    return run(apply_changes=a.apply)


if __name__ == "__main__":
    sys.exit(main())
