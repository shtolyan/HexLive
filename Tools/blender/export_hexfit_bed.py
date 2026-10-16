"""Export the canonical staged ``bed.basic`` from the building-kit source.

The authored source is ``L_HexFitBed_root`` in ``hexlive_building_kit.blend``.
That is the approved hex-fitted artwork used by the current runtime asset (the
older ``BED2_root`` has since diverged in proportions).  The source may
already contain canonical ``BuildStage_*`` parents (the preferred interactive
workflow), or it may still be the historical flat 69-piece hierarchy.  The
flat fallback is grouped by the established piece prefixes without modifying
or saving the .blend file.

Run with the building kit open, for example::

    blender Assets/ArtSource/Building/hexlive_building_kit.blend --background \
      --python Tools/blender/export_hexfit_bed.py

Set ``HEXLIVE_BED_OUTPUT`` to validate into a temporary path without replacing
the runtime asset.
"""

import os
import re
from pathlib import Path

import bpy
from mathutils import Vector


SOURCE_FILE = "hexlive_building_kit.blend"
SOURCE_ROOT = "L_HexFitBed_root"
STAGE_RE = re.compile(r"^BuildStage_(\d+)(?:__(.+))?$")
STAGES = (
    (1, "log", "log_", 4),
    (2, "stick", "stick_", 5),
    (3, "rope", "rope_", 10),
    (4, "palm_leaf", "leaf_", 50),
)


def descendants(root):
    return list(root.children_recursive)


def root_bounds(root):
    inverse = root.matrix_world.inverted()
    low = Vector((1e9, 1e9, 1e9))
    high = Vector((-1e9, -1e9, -1e9))
    for obj in descendants(root):
        if obj.type != "MESH":
            continue
        matrix = inverse @ obj.matrix_world
        for corner in obj.bound_box:
            point = matrix @ Vector(corner)
            low.x = min(low.x, point.x)
            low.y = min(low.y, point.y)
            low.z = min(low.z, point.z)
            high.x = max(high.x, point.x)
            high.y = max(high.y, point.y)
            high.z = max(high.z, point.z)
    return low, high


def export_name(name):
    return name[2:] if name.startswith("L_") else name


def copy_tree(source, parent, collection):
    copied = source.copy()
    if source.data is not None:
        copied.data = source.data.copy()
    copied.name = export_name(source.name)
    collection.objects.link(copied)
    copied.parent = parent
    copied.matrix_local = source.matrix_local.copy()
    copied.hide_viewport = False
    copied.hide_render = False
    for child in source.children:
        copy_tree(child, copied, collection)
    return copied


def authored_stages(source_root):
    result = {}
    for child in source_root.children:
        match = STAGE_RE.match(child.name)
        if match:
            result[int(match.group(1))] = child
        elif child.name.isdigit():
            result[int(child.name)] = child
    return result


def source_units(source_root, index, prefix, explicit):
    if index in explicit:
        return list(explicit[index].children)
    return [
        child for child in source_root.children
        if export_name(child.name).startswith(prefix)
    ]


def remove_temp(collection):
    for obj in list(collection.objects):
        mesh = obj.data if obj.type == "MESH" else None
        bpy.data.objects.remove(obj, do_unlink=True)
        if mesh is not None and mesh.users == 0:
            bpy.data.meshes.remove(mesh)
    bpy.data.collections.remove(collection)


if Path(bpy.data.filepath).name != SOURCE_FILE:
    raise RuntimeError("Open the HexLive building kit before exporting: " + bpy.data.filepath)

source_root = bpy.data.objects.get(SOURCE_ROOT)
if source_root is None:
    raise RuntimeError("Missing source root: " + SOURCE_ROOT)

repo = Path(bpy.data.filepath).resolve().parents[3]
default_output = (
    repo / "Assets" / "HexLiveContent" / "RuntimeSource" / "Objects"
    / "bed_basic_final_native.fbx"
)
output = Path(os.environ.get("HEXLIVE_BED_OUTPUT", str(default_output))).resolve()
output.parent.mkdir(parents=True, exist_ok=True)

low, high = root_bounds(source_root)
scale_x, scale_y, scale_z = source_root.scale

temp = bpy.data.collections.new("__HL_BED_STAGE_EXPORT__")
bpy.context.scene.collection.children.link(temp)
root = bpy.data.objects.new("HexFitBed_root", None)
temp.objects.link(root)
root.location = (
    -((low.x + high.x) * 0.5) * scale_x,
    -((low.y + high.y) * 0.5) * scale_y,
    0.04 - low.z * scale_z,
)
root.scale = (scale_x, scale_y, scale_z)

explicit = authored_stages(source_root)
counts = {}
for index, token, prefix, expected in STAGES:
    stage = bpy.data.objects.new(f"BuildStage_{index}__{token}", None)
    temp.objects.link(stage)
    stage.parent = root
    units = source_units(source_root, index, prefix, explicit)
    counts[index] = len(units)
    if len(units) != expected:
        remove_temp(temp)
        raise RuntimeError(
            f"bed.basic stage {index} ({token}) has {len(units)} units; expected {expected}"
        )
    for unit in units:
        copy_tree(unit, stage, temp)

# Authored height only. BedAssembly creates the runtime `point` with canonical
# orientation, using this marker solely to read the vertical offset.
point = bpy.data.objects.new("point", None)
temp.objects.link(point)
point.parent = root
point.empty_display_type = "SPHERE"
point.empty_display_size = 0.035
point.location = (0.0, 0.0, 0.54)

bpy.ops.object.select_all(action="DESELECT")
selection = [root, point, *root.children]
for stage in root.children:
    selection.extend(stage.children_recursive)
for obj in selection:
    obj.select_set(True)
bpy.context.view_layer.objects.active = root

bpy.ops.export_scene.fbx(
    filepath=str(output),
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
    bake_anim=False,
    path_mode="AUTO",
    embed_textures=False,
)

print("BED_STAGE_EXPORT", output)
print("BED_STAGE_COUNTS", counts)
remove_temp(temp)
