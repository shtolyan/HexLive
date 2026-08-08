"""Export the approved one-hex hut collection from the open Blender file.

Run inside Blender (normally through Blender MCP). The source .blend remains
the artistic master; Unity receives a small, axis-stable FBX containing only
the three build stages and no settlement/presentation objects.
"""

import json
import os

import bpy
import math
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

# Window rails were authored in QUATERNION mode. Looking at rotation_euler made
# them appear to be zeroed, while the real (0.5, 0.5, 0.5, 0.5) quaternion laid
# them horizontally through the wall. The rail mesh itself is authored along
# local X; rotate that axis onto Blender Z, which imports as Unity Y. Rails are vertical
# architectural members, so make that invariant explicit for the whole kit,
# including future duplicated house modules. This is idempotent.
for obj in bpy.data.objects:
    if obj.type != "MESH" or "Window_window_rail" not in obj.name:
        continue
    location = obj.matrix_world.translation.copy()
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = (math.cos(math.pi * 0.25), 0.0, math.sin(math.pi * 0.25), 0.0)
    obj.location = location

# The door is authored as separate boards. Normalize it to one clear artistic
# open angle around the existing hinge before saving/exporting. Deriving the
# current angle from the outer-board centres makes reruns idempotent.
door_prefix = "HL_Bay_00_Door_"
door_hinge_source = bpy.data.objects.get(door_prefix + "door_transform")
door_top = bpy.data.objects.get(door_prefix + "board_top")
door_outer_a = bpy.data.objects.get(door_prefix + "door_board_0")
door_outer_b = bpy.data.objects.get(door_prefix + "door_board_2")
door_leaf_names = (
    door_prefix + "door_board_0",
    door_prefix + "door_board_1",
    door_prefix + "door_board_2",
    door_prefix + "door_brace",
    door_prefix + "handle",
)
if door_hinge_source is None or door_top is None or door_outer_a is None or door_outer_b is None:
    raise RuntimeError("Incomplete authored hut door")

door_hinge = door_hinge_source.matrix_world.translation.copy()
wall_angle = door_top.matrix_world.to_euler("XYZ").z
open_angle = wall_angle + math.radians(72.0)
leaf_vector = door_outer_b.matrix_world.translation - door_outer_a.matrix_world.translation
current_angle = math.atan2(leaf_vector.y, leaf_vector.x)
delta = open_angle - current_angle
rotate_about_hinge = (
    Matrix.Translation(door_hinge) @
    Matrix.Rotation(delta, 4, "Z") @
    Matrix.Translation(-door_hinge)
)
for name in door_leaf_names:
    obj = bpy.data.objects.get(name)
    if obj is None:
        raise RuntimeError("Missing " + name)
    obj.matrix_world = rotate_about_hinge @ obj.matrix_world

bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)

temp_collection = bpy.data.collections.new("__HL_EXPORT_HUT_1HEX_TEMP")
bpy.context.scene.collection.children.link(temp_collection)
stage_roots = {}
for stage_name in ("1", "2", "3"):
    empty = bpy.data.objects.new(f"BuildStage_{stage_name}", None)
    temp_collection.objects.link(empty)
    stage_roots[stage_name] = empty

# Exported semantic transforms: Unity keeps the leaf under DoorPivot and reads
# the two sibling state markers. No mesh-specific hand rotation is required in
# presentation code, and Open/Close can interpolate one transform.
door_pivot = bpy.data.objects.new("HL_Door_Pivot", None)
door_closed = bpy.data.objects.new("HL_Door_State_Closed", None)
door_open = bpy.data.objects.new("HL_Door_State_Open", None)
for empty in (door_pivot, door_closed, door_open):
    temp_collection.objects.link(empty)
    empty.parent = stage_roots["2"]
door_pivot.matrix_world = Matrix.Translation(door_hinge) @ Matrix.Rotation(open_angle, 4, "Z")
door_closed.matrix_world = Matrix.Translation(door_hinge) @ Matrix.Rotation(wall_angle, 4, "Z")
door_open.matrix_world = door_pivot.matrix_world.copy()


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
    if source.name in door_leaf_names:
        duplicate.data.transform(door_pivot.matrix_world.inverted() @ source.matrix_world)
    else:
        duplicate.data.transform(source.matrix_world)
    stage_name = stage_for(source.name)
    duplicate.parent = door_pivot if source.name in door_leaf_names else stage_roots[stage_name]
    duplicate.matrix_parent_inverse = Matrix.Identity(4)
    duplicate.matrix_basis = Matrix.Identity(4)
    temp_collection.objects.link(duplicate)
    exported.append(duplicate)
    stage_counts[stage_name] += 1

for obj in bpy.context.selected_objects:
    obj.select_set(False)
for obj in [*stage_roots.values(), door_pivot, door_closed, door_open, *exported]:
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
for empty in [door_pivot, door_closed, door_open, *stage_roots.values()]:
    bpy.data.objects.remove(empty, do_unlink=True)
bpy.data.collections.remove(temp_collection)

print(json.dumps({
    "source": bpy.data.filepath,
    "fbx": output_path,
    "fbx_bytes": os.path.getsize(output_path),
    "stage_counts": stage_counts,
}, ensure_ascii=False, indent=2))
