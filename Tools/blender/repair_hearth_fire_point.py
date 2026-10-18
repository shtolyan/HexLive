"""Repair #491 in the existing stage workspace; export separately with its exporter.

blender _ArtSource/Construction/hexlive_construction_stages.blend -b --python \
  Tools/blender/repair_hearth_fire_point.py
"""
import importlib.util
import json
import math
from pathlib import Path

import bpy
from mathutils import Vector

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "_ArtSource/Construction/hexlive_construction_stages.blend"
assert Path(bpy.data.filepath).resolve() == SOURCE, "Open the existing stage workspace"
spec = importlib.util.spec_from_file_location("stage_export", REPO / "Tools/blender/export_construction_stage_workspace.py")
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)
definition = next(d for d in json.loads((REPO / "Tools/construction_stage_manifest.json").read_text())["definitions"]
                  if d["id"] == "furniture.hearth")
root = exporter.find_root(definition["id"])
bpy.context.window.scene = next(s for s in bpy.data.scenes if s.get("hexlive_definition_id") == definition["id"])
bpy.context.view_layer.update()
marker = next(o for o in root.children_recursive if exporter.stage_name(o) == "fire_point")
assert marker.parent == root
before = root.matrix_world.inverted() @ marker.matrix_world.translation
target = Vector(definition["requiredMarkerPoses"][0]["position"])
assert min((before - Vector((-16, 0, 0))).length, (before - target).length) < 1e-5, "Unexpected source; inspect before repairing"
matrices = {o: o.matrix_world.copy() for o in bpy.data.objects if o != marker}
marker.location = target
marker.rotation_euler = (math.pi / 2, 0, 0)
bpy.context.view_layer.update()
exporter.validate(definition)
for obj, matrix in matrices.items():
    assert max(abs(matrix[i][j] - obj.matrix_world[i][j]) for i in range(4) for j in range(4)) < 1e-5, obj.name
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=str(SOURCE))
print("HEARTH_FIRE_POINT_REPAIRED", list(before), "->", list(target), "other object matrices unchanged")
