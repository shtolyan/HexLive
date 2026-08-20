"""Export the §120 architecture LEGO elements from the open building kit.

Run inside Blender (normally through Blender MCP). Five FBX files land in
Assets/Resources/HexLive/Objects, one per constructor element definition:

    architecture.wall.wood.fbx      <- HL_ARCH_WALL
    architecture.window.wood.fbx    <- HL_ARCH_WINDOW
    architecture.door.wood.fbx      <- HL_ARCH_DOOR
    architecture.support.wood.fbx   <- HL_ARCH_SUPPORT
    architecture.floor.board.fbx    <- HL_ARCH_FLOOR

Element contract (mirrors building.hut_1hex.fbx):
  * BuildStage_1/2/3 empties at the root; every direct mesh child of a stage
    is ONE delivered resource unit (sticks -> stage 1, boards -> stage 2,
    rope -> stage 3). Objects whose name contains "_deco_" are hardware that
    appears together with its stage and is not a resource unit.
  * The door leaf lives under HL_Door_Pivot with HL_Door_State_Closed/Open
    sibling markers, same as the hut door.
  * Local frame: section length runs along Blender +Y (Unity +Z after the
    stock X=-90 import), the element centre is the origin, Z is up.
"""

import json
import os

import bpy
from mathutils import Matrix

SOURCE_FILE = "hexlive_building_kit.blend"
COLLECTION = "HL_ARCH_ELEMENTS"

ELEMENTS = {
    "HL_ARCH_WALL": "architecture.wall.wood",
    "HL_ARCH_WINDOW": "architecture.window.wood",
    "HL_ARCH_DOOR": "architecture.door.wood",
    "HL_ARCH_SUPPORT": "architecture.support.wood",
    "HL_ARCH_FLOOR": "architecture.floor.board",
    "HL_ARCH_ROOF": "architecture.roof.palm",
    "HL_ARCH_ROOF_FLAT": "architecture.roof.palm.flat",
    "HL_ARCH_HEARTH": "furniture.hearth",
}

# A targeted deterministic repair should not rewrite seven unrelated binary
# FBX files merely because Blender's exporter ran again. Empty means the normal
# full export; a definition id exports exactly that one element.
requested_element = os.environ.get("HEXLIVE_ARCH_ELEMENT", "").strip()
if requested_element:
    selected = {root: definition for root, definition in ELEMENTS.items()
                if definition == requested_element}
    if not selected:
        raise RuntimeError("Unknown HEXLIVE_ARCH_ELEMENT " + requested_element)
    ELEMENTS = selected

# Empties that must survive export as direct children of the element root, not
# as stage members: the renderer looks the hearth flame up by this exact name.
ROOT_MARKERS = ("fire_point",)

DOOR_PIVOT_CHILD_MARKERS = ("HL_Door_State_Closed", "HL_Door_State_Open")


if os.path.basename(bpy.data.filepath) != SOURCE_FILE:
    raise RuntimeError("Open the HexLive building kit before exporting: " + bpy.data.filepath)

repo_root = os.path.abspath(os.path.join(os.path.dirname(bpy.data.filepath), "../../.."))
objects_dir = os.path.join(repo_root, "Assets", "Resources", "HexLive", "Objects")
os.makedirs(objects_dir, exist_ok=True)

collection = bpy.data.collections.get(COLLECTION)
if collection is None:
    raise RuntimeError("Missing Blender collection " + COLLECTION)


def stage_index_of(obj):
    node = obj
    while node is not None:
        if "BuildStage_1" in node.name:
            return "1"
        if "BuildStage_2" in node.name:
            return "2"
        if "BuildStage_3" in node.name:
            return "3"
        node = node.parent
    return None


def is_door_leaf_member(obj):
    """The leaf hangs under the pivot. The source pivot is parked as
    __src_HL_Door_Pivot during export, so accept both spellings or the leaf
    silently lands on the stage instead and the door stops swinging."""
    node = obj
    while node is not None:
        if node.name.startswith("HL_Door_Pivot") or node.name.startswith("__src_HL_Door_Pivot"):
            return True
        node = node.parent
    return False


report = {}
for root_name, definition_id in ELEMENTS.items():
    root = bpy.data.objects.get(root_name)
    if root is None:
        raise RuntimeError("Missing element root " + root_name)
    to_origin = Matrix.Translation(-root.matrix_world.translation)

    temp = bpy.data.collections.new("__HL_ARCH_EXPORT_TEMP")
    bpy.context.scene.collection.children.link(temp)
    stage_roots = {}
    for stage in ("1", "2", "3"):
        # Steal exact names from any source empties for the export duration.
        holder = bpy.data.objects.get(f"BuildStage_{stage}")
        if holder is not None:
            holder.name = f"__src_BuildStage_{stage}"
        empty = bpy.data.objects.new(f"BuildStage_{stage}", None)
        temp.objects.link(empty)
        stage_roots[stage] = empty

    door_pivot = door_closed = door_open = None
    if root_name == "HL_ARCH_DOOR":
        source_pivot = next(o for o in root.children_recursive
                            if o.name.startswith("HL_Door_Pivot"))
        pivot_world = to_origin @ source_pivot.matrix_world
        # Park the source out of the way or Blender hands the export copy
        # "HL_Door_Pivot.001", and the exact name is a runtime contract.
        source_pivot_name = source_pivot.name
        source_pivot.name = "__src_" + source_pivot_name
        for marker in DOOR_PIVOT_CHILD_MARKERS:
            src = bpy.data.objects.get(marker)
            if src is not None:
                src.name = "__src_" + marker
        door_pivot = bpy.data.objects.new("HL_Door_Pivot", None)
        door_closed = bpy.data.objects.new("HL_Door_State_Closed", None)
        door_open = bpy.data.objects.new("HL_Door_State_Open", None)
        for src_name, empty in (("__src_HL_Door_State_Closed", door_closed),
                                ("__src_HL_Door_State_Open", door_open)):
            src = bpy.data.objects.get(src_name)
            if src is None:
                raise RuntimeError("Missing door state marker " + src_name)
            empty.matrix_world = to_origin @ src.matrix_world
        for empty in (door_pivot, door_closed, door_open):
            temp.objects.link(empty)
            empty.parent = stage_roots["2"]
        door_pivot.matrix_world = pivot_world

    root_markers = []
    for marker_name in ROOT_MARKERS:
        source = next((o for o in root.children_recursive
                       if o.name.split(".")[0] == marker_name), None)
        if source is None:
            continue
        pose = to_origin @ source.matrix_world
        source.name = "__src_" + source.name
        marker = bpy.data.objects.new(marker_name, None)
        temp.objects.link(marker)
        marker.matrix_world = pose
        root_markers.append(marker)

    exported = []
    renamed_sources = []
    stage_counts = {"1": 0, "2": 0, "3": 0}
    for source in [o for o in root.children_recursive if o.type == "MESH"]:
        stage = stage_index_of(source)
        if stage is None:
            raise RuntimeError(f"{source.name} is outside every BuildStage")
        duplicate = source.copy()
        duplicate.data = source.data.copy()
        duplicate.animation_data_clear()
        # Ship the AUTHORED name, not a suffixed one: presentation matches some
        # pieces by exact node name (CampfireSpitMeat looks for "stick_bar", and
        # a "stick_bar__EXPORT" silently loses every meat slot). The source is
        # parked under __src_ for the duration so Blender cannot add ".001".
        authored_name = source.name
        source.name = "__src_" + authored_name
        renamed_sources.append((source, authored_name))
        duplicate.name = authored_name
        if root_name == "HL_ARCH_DOOR" and is_door_leaf_member(source):
            duplicate.data.transform(door_pivot.matrix_world.inverted()
                                     @ to_origin @ source.matrix_world)
            duplicate.parent = door_pivot
        else:
            duplicate.data.transform(to_origin @ source.matrix_world)
            duplicate.parent = stage_roots[stage]
        duplicate.matrix_parent_inverse = Matrix.Identity(4)
        duplicate.matrix_basis = Matrix.Identity(4)
        temp.objects.link(duplicate)
        exported.append(duplicate)
        if "_deco_" not in source.name:
            stage_counts[stage] += 1

    for obj in bpy.context.selected_objects:
        obj.select_set(False)
    selection = [*stage_roots.values(), *exported, *root_markers]
    if door_pivot is not None:
        selection += [door_pivot, door_closed, door_open]
    for obj in selection:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = stage_roots["1"]

    output_path = os.path.join(objects_dir, definition_id + ".fbx")
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
    for source, authored_name in renamed_sources:
        source.name = authored_name
    extra = [door_pivot, door_closed, door_open] if door_pivot is not None else []
    for empty in [*extra, *stage_roots.values(), *root_markers]:
        bpy.data.objects.remove(empty, do_unlink=True)
    bpy.data.collections.remove(temp)
    for stage in ("1", "2", "3"):
        holder = bpy.data.objects.get(f"__src_BuildStage_{stage}")
        if holder is not None:
            holder.name = f"BuildStage_{stage}"
    if root_name == "HL_ARCH_DOOR":
        parked = bpy.data.objects.get("__src_" + source_pivot_name)
        if parked is not None:
            parked.name = source_pivot_name
    for marker in DOOR_PIVOT_CHILD_MARKERS + ROOT_MARKERS:
        src = bpy.data.objects.get("__src_" + marker)
        if src is not None:
            src.name = marker

    report[definition_id] = {
        "fbx": output_path,
        "bytes": os.path.getsize(output_path),
        "stage_counts": stage_counts,
    }

print(json.dumps(report, ensure_ascii=False, indent=2))
