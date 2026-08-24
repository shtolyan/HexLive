#!/usr/bin/env python3
"""Extract DAZ expression-pack dial recipes into a Unity face-expression catalog.

Expression packs like "Cute & Fun Expressions for G3F and G8F" ship NO vertex
deltas of their own: each expression is a controller dial (.dsf in Data/) whose
ERC `formulas` drive the STANDARD Genesis base-pose-head dials (eCTRLMouthSmile,
eCTRLEyesSquint-Widen, ...) with fixed weights, and the .duf preset in People/
just sets that one dial to 1. Our actors' FBX exports already carry those
standard dials as blendshapes, so an expression ports to Unity as a plain
list of (blendshape, weight%) — no new morphs, no re-export.

Usage:
    python3 Tools/daz_expression_extract.py \
        "<unpacked-product>/Data/Daz 3D/Genesis 3/Female/Morphs/<vendor>/<pack>" \
        --out Assets/HexLiveContent/RuntimeSource/FaceExpressions/CuteFun.json

Reads every *.dsf (gzipped or plain JSON) in the given morphs directory,
normalizes the target dial names to the blendshape names our G3F FBX exports
actually carry, and writes a JsonUtility-friendly catalog.

What normalization covers (all verified against Molly.new.fbx):
  * ECTRLFoo            -> eCTRLFoo            (case drift between packs)
  * ePHMFoo             -> PHMFoo              (export drops the 'e' on PHMs)
  * CTRLTongueFoo       -> eCTRLTongueFoo      (and "CTRLTongue Curl" spacing)
  * CTRLJawIn-Out       -> eCTRLJawOut-In      (same dial, file id vs label)
  * CTRLJawSide-Side    -> eCTRLJawSide-Side
  * combined shapes that are dead on our figures (the NpcFaceAnimator known
    list: EyesClosed, EyesSquint, CheekFlex, MouthCornerBack) are expanded to
    their working L/R pair at the same weight.
Bone-rotation targets (tongue01..03, tongueBase ?rotation/*) have no
blendshape equivalent and are dropped with a warning — a stuck-out tongue
loses some of its poke, nothing else.
"""

import argparse
import glob
import gzip
import json
import os
import sys
import urllib.parse

# Combined dials whose baked blendshape does nothing on our figures; the L/R
# pair works (same list NpcFaceAnimator.Construct discovered the hard way).
DEAD_COMBINED = {
    "eCTRLEyesClosed",
    "eCTRLEyesSquint",
    "eCTRLCheekFlex",
    "eCTRLMouthCornerBack",
}

RENAMES = {
    "CTRLJawIn-Out": "eCTRLJawOut-In",
    "CTRLJawSide-Side": "eCTRLJawSide-Side",
}


def normalize(name: str) -> str:
    name = name.replace(" ", "")
    if name in RENAMES:
        return RENAMES[name]
    if name.startswith("ECTRL"):
        name = "eCTRL" + name[len("ECTRL"):]
    if name.startswith("ePHM"):
        name = name[1:]
    if name.startswith("CTRLTongue"):
        name = "e" + name
    return name


def read_dsf(path: str) -> dict:
    try:
        with gzip.open(path, "rt", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, gzip.BadGzipFile):
        with open(path, "rt", encoding="utf-8") as f:
            return json.load(f)


def extract(path: str):
    doc = read_dsf(path)
    modifier = doc["modifier_library"][0]
    name = modifier["id"]
    shapes = {}   # normalized shape -> weight percent (summed: ERC adds)
    dropped = []
    for formula in modifier.get("formulas", []):
        output = urllib.parse.unquote(formula["output"].split("#")[-1])
        target, _, channel = output.partition("?")
        # push <dial>, push <const>, mult — the only shape these packs use.
        ops = [op["op"] for op in formula["operations"]]
        if ops != ["push", "push", "mult"]:
            dropped.append(f"{target} (unsupported ops {ops})")
            continue
        if channel != "value":
            dropped.append(f"{target}?{channel} (bone channel)")
            continue
        value = next(op["val"] for op in formula["operations"] if "val" in op)
        shape = normalize(target)
        targets = (
            [shape + "L", shape + "R"] if shape in DEAD_COMBINED else [shape]
        )
        for t in targets:
            shapes[t] = shapes.get(t, 0.0) + value * 100.0
    return name, shapes, dropped


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("morphs_dir", help="Data/.../Morphs/<vendor>/<pack> directory with the pack's .dsf dials")
    parser.add_argument("--out", required=True, help="catalog JSON to write (e.g. under Assets/Resources)")
    parser.add_argument("--labels", help="sidecar JSON with human labels: {'01': 'Радостный смех', ...}; "
                                         "keys are matched as a suffix of the dial name, '-Left/-Right' stripped")
    args = parser.parse_args()

    labels = {}
    if args.labels:
        with open(args.labels, encoding="utf-8") as f:
            labels = {k: v for k, v in json.load(f).items() if not k.startswith("_")}

    files = sorted(glob.glob(os.path.join(args.morphs_dir, "*.dsf")))
    if not files:
        print(f"no .dsf files in {args.morphs_dir}", file=sys.stderr)
        return 1

    entries = []
    for path in files:
        name, shapes, dropped = extract(path)
        key = name.split(" ")[-1]                       # "FXY Cute & Fun 11-Left" -> "11-Left"
        base_key = key.replace("-Left", "").replace("-Right", "")
        entries.append({
            "name": name,
            "label": labels.get(key, labels.get(base_key, "")),
            "shapes": [
                {"shape": s, "weight": round(w, 2)}
                for s, w in sorted(shapes.items())
                if abs(w) >= 0.05
            ],
        })
        for d in dropped:
            print(f"  {name}: dropped {d}")

    catalog = {"expressions": entries}
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(catalog, f, ensure_ascii=False, indent=2)
        f.write("\n")
    print(f"{len(entries)} expressions -> {args.out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
