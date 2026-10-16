"""Regression check against the artist's pre-review snapshot (no Unity needed)."""
import json
from pathlib import Path
import sys

import bpy

REPO = Path(__file__).resolve().parents[2]
DIRECTORY = REPO / "Build/ConstructionReview-20261016"


def snapshot():
    result = {}
    for definition in ("furniture.wardrobe", "bed.basic"):
        root = next(o for o in bpy.data.objects if o.get("hexlive_definition_id") == definition)
        nodes = [o for o in root.children_recursive if
                 str(o.get("hexlive_export_name", o.name)).startswith(("HangerTemplate", "ClothingSlot_", "point"))]
        for node in nodes:
            name = str(node.get("hexlive_export_name", node.name))
            if name.startswith("point"):
                name = "point"
            matrices = [v for row in node.matrix_world for v in row]
            points = sorted(tuple(o.matrix_world @ v.co) for o in [node, *node.children_recursive]
                            if o.type == "MESH" for v in o.data.vertices)
            result[definition+"/"+name] = matrices+[v for point in points for v in point]
    return result


def main():
    args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
    destination = Path(args[0]) if args else REPO/"_ArtSource/Construction/hexlive_construction_stages.blend"
    bpy.ops.wm.open_mainfile(filepath=str(DIRECTORY/"before-review.blend"))
    expected = snapshot()
    bpy.ops.wm.open_mainfile(filepath=str(destination))
    actual = snapshot()
    if expected.keys() != actual.keys():
        raise RuntimeError("Approved attachment set changed")
    error = 0
    for name, values in expected.items():
        if len(values) != len(actual[name]):
            raise RuntimeError("Approved attachment geometry changed: "+name)
        distance = max(abs(a-b) for a, b in zip(values, actual[name]))
        error = max(error, distance)
        if distance > 1e-6:
            raise RuntimeError(f"Approved attachment moved: {name}, error={distance}")
    result = {"attachments": len(expected), "maxErrorWu": error,
              "baseline": "before-review.blend", "source": str(destination)}
    (DIRECTORY/"approved-attachments-check.json").write_text(json.dumps(result, indent=2)+"\n")
    print("APPROVED_ATTACHMENTS_UNCHANGED", result)


if __name__ == "__main__":
    main()
