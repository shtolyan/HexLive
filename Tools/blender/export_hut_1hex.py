"""Export the approved one-hex hut collection from the open Blender file.

Run inside Blender (normally through Blender MCP). The source .blend remains
the artistic master; Unity receives a small, axis-stable FBX containing only
the three build stages and no settlement/presentation objects.
"""

import json
import os

import bpy
from mathutils import Matrix


SOURCE_FILE = "hexlive_building_kit.blend"
SOURCE_COLLECTION = "HL_BUILDING_HUT_1HEX"


if os.path.basename(bpy.data.filepath) != SOURCE_FILE:
    raise RuntimeError("Open the HexLive building kit before exporting: " + bpy.data.filepath)

repo_root = os.path.abspath(os.path.join(os.path.dirname(bpy.data.filepath), "../../.."))
output_path = os.path.join(
    repo_root, "Assets", "Resources", "HexLive", "Objects", "building.hut_1hex.fbx"
)
hut = bpy.data.collections.get(SOURCE_COLLECTION)
if hut is None:
    raise RuntimeError(f"Missing Blender collection {SOURCE_COLLECTION}")

# Repair suffixes left by an interrupted old-style temporary export.
for obj in list(hut.all_objects):
    if obj.name.endswith(".001"):
        base_name = obj.name[:-4]
        if bpy.data.objects.get(base_name) is None:
            obj.name = base_name

# The floor underbeams are already authored along local Y, inside Blender's
# XY floor plane. An earlier export forced +90 degrees around X and turned all
# three into 2.24-wu vertical spikes through the finished floor. Keep the
# authored horizontal orientation explicitly so reruns are idempotent.
for index in range(3):
    name = f"HL_Floor_underbeam_{index}"
    obj = bpy.data.objects.get(name)
    if obj is None:
        raise RuntimeError("Missing " + name)
    obj.rotation_mode = "XYZ"
    obj.rotation_euler = (0.0, 0.0, 0.0)

bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)

temp_collection = bpy.data.collections.new("__HL_EXPORT_HUT_1HEX_TEMP")
bpy.context.scene.collection.children.link(temp_collection)
stage_roots = {}
for stage_name in ("1", "2", "3"):
    empty = bpy.data.objects.new(f"BuildStage_{stage_name}", None)
    temp_collection.objects.link(empty)
    stage_roots[stage_name] = empty


def stage_for(name):
    if "Roof_palm_leaf" in name or "Roof_woven_mat" in name:
        return "3"
    if "Bay" in name or "Roof_rafter" in name or "Roof_apex_lashing" in name:
        return "2"
    return "1"


exported = []
stage_counts = {"1": 0, "2": 0, "3": 0}
for source in hut.all_objects:
    if source.type != "MESH" or source.name == "HL_Hex_R1.5_Exact":
        continue
    duplicate = source.copy()
    duplicate.data = source.data.copy()
    duplicate.animation_data_clear()
    duplicate.name = source.name + "__EXPORT"
    # Bake source world transforms into private meshes. Unity receives identity
    # children and never has to reinterpret Blender pivots or unapplied scale.
    duplicate.data.transform(source.matrix_world)
    duplicate.matrix_world = Matrix.Identity(4)
    stage_name = stage_for(source.name)
    duplicate.parent = stage_roots[stage_name]
    temp_collection.objects.link(duplicate)
    exported.append(duplicate)
    stage_counts[stage_name] += 1

for obj in bpy.context.selected_objects:
    obj.select_set(False)
for obj in [*stage_roots.values(), *exported]:
    obj.select_set(True)
bpy.context.view_layer.objects.active = stage_roots["1"]

os.makedirs(os.path.dirname(output_path), exist_ok=True)
bpy.ops.export_scene.fbx(
    filepath=output_path,
    use_selection=True,
    object_types={"EMPTY", "MESH"},
    apply_unit_scale=True,
    apply_scale_options="FBX_SCALE_ALL",
    use_space_transform=True,
    bake_space_transform=False,
    axis_forward="-Z",
    axis_up="Y",
    add_leaf_bones=False,
    mesh_smooth_type="FACE",
    use_mesh_modifiers=True,
    use_triangles=False,
    use_tspace=False,
    use_custom_props=False,
    bake_anim=False,
    path_mode="AUTO",
    embed_textures=False,
)

for duplicate in exported:
    mesh = duplicate.data
    bpy.data.objects.remove(duplicate, do_unlink=True)
    if mesh.users == 0:
        bpy.data.meshes.remove(mesh)
for empty in stage_roots.values():
    bpy.data.objects.remove(empty, do_unlink=True)
bpy.data.collections.remove(temp_collection)

print(json.dumps({
    "source": bpy.data.filepath,
    "fbx": output_path,
    "fbx_bytes": os.path.getsize(output_path),
    "stage_counts": stage_counts,
}, ensure_ascii=False, indent=2))
