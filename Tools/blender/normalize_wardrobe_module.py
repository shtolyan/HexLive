"""Normalize the authored wardrobe into one rigid, code-ready furniture module."""

import bpy
import math
from mathutils import Matrix, Vector


COL = bpy.data.collections["HL_WARDROBE_REVIEW"]
OLD_ROOT = bpy.data.objects.get("HL_Wardrobe_ROOT")
PLACEMENT = bpy.data.objects.get("HL_Wardrobe_PlacementRoot")

# Current visible frame midpoint. It becomes the furniture pivot on floor level.
left = bpy.data.objects["HL_Wardrobe_Native_FrameL"].matrix_world.translation
right = bpy.data.objects["HL_Wardrobe_Native_FrameR"].matrix_world.translation
pivot = (left + right) * .5
pivot.z = min(
    bpy.data.objects["HL_Wardrobe_Foot_L"].matrix_world.translation.z,
    bpy.data.objects["HL_Wardrobe_Foot_R"].matrix_world.translation.z,
) - .105

module = bpy.data.objects.get("HL_Wardrobe_Module")
if module is None:
    module = bpy.data.objects.new("HL_Wardrobe_Module", None)
    COL.objects.link(module)
else:
    # Preserve descendants before resetting a previously authored module.
    for child in list(module.children):
        world = child.matrix_world.copy()
        child.parent = None
        child.matrix_parent_inverse = Matrix.Identity(4)
        child.matrix_basis = world

module.location = pivot
module.rotation_mode = 'XYZ'
module.rotation_euler = (0.0, 0.0, math.radians(30.0))
module.scale = (1.0, 1.0, 1.0)


def belongs_to_module(obj):
    name = obj.name
    if name in {"HL_Wardrobe_ROOT", "HL_Wardrobe_Contents"}:
        return True
    prefixes = (
        "HL_Wardrobe_Native_", "HL_Wardrobe_Foot_", "HL_Wardrobe_Hanger_",
        "HL_Wardrobe_RealHanger_", "HL_Wardrobe_Real_", "HL_Wardrobe_Slot_",
        "HL_Wardrobe_ShoeShelf_", "HL_Wardrobe_Stage_",
    )
    return name.startswith(prefixes)


# Find only top nodes of wardrobe content, then parent them all under one root
# while preserving their current approved visible positions.
candidates = {obj for obj in bpy.data.objects if belongs_to_module(obj)}
tops = []
for obj in candidates:
    parent = obj.parent
    if parent not in candidates:
        tops.append(obj)

for obj in tops:
    if obj == module:
        continue
    world = obj.matrix_world.copy()
    obj.parent = module
    # Keep a conventional clean hierarchy.  Assigning matrix_world directly
    # after reparenting preserves Blender's implicit parent inverse; repeating
    # normalization then applies the module translation again and drifts the
    # complete wardrobe far away from its pivot.
    obj.matrix_parent_inverse = Matrix.Identity(4)
    obj.matrix_basis = module.matrix_world.inverted() @ world

# The old review scene hid several pieces independently. A reusable furniture
# module owns its visibility at the root, so every descendant must render.
stack = list(module.children)
while stack:
    obj = stack.pop()
    obj.hide_viewport = False
    obj.hide_render = False
    stack.extend(obj.children)

# Obsolete authoring scenery must never travel with the furniture module.
for obj in list(PLACEMENT.children) if PLACEMENT else []:
    if obj not in tops:
        obj.hide_viewport = True
        obj.hide_render = True

# Code-facing placement contract matches bed.basic: local +Y spans the occupied
# junctions and the central occupied junction is the pivot. Exactly three nodes
# are occupied. The review root may be rotated in the art scene; the exporter
# bakes it into this clean furniture basis.
module["furnitureId"] = "furniture.wardrobe"
module["pivot"] = "central_occupied_junction"
module["occupiedAxis"] = "+Y (same as bed.basic length axis)"
module["roomSideAxis"] = "+X"
module["allowedYawDegrees"] = [0, 60, 120, 180, 240, 300]
module["occupiedJunctionOffsets"] = [[0.0, -.375], [0.0, 0.0], [0.0, .375]]
module["occupiedJunctionCount"] = 3
module["clothingSlots"] = 12
module["shoePairSlots"] = 3

# Replace the confusing six-node marker set with the exact three occupied
# junctions attached to the module itself.
for obj in list(bpy.data.objects):
    if obj.name.startswith("HL_Wardrobe_Footprint_") or obj.name == "HL_Wardrobe_Interaction":
        bpy.data.objects.remove(obj, do_unlink=True)

mat = bpy.data.materials.get("HL_FootprintBlocked") or bpy.data.materials.new("HL_FootprintBlocked")
mat.diffuse_color = (.9, .04, .04, 1.0)
for index, y in enumerate((-.375, 0.0, .375)):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=.045)
    marker = bpy.context.object
    marker.name = f"HL_Wardrobe_OccupiedJunction_{index}"
    for owner in list(marker.users_collection):
        owner.objects.unlink(marker)
    COL.objects.link(marker)
    marker.parent = module
    marker.location = (0.0, y, .03)
    marker.data.materials.append(mat)
    marker.hide_render = False

# Root is what artists and exporters select/rotate.
bpy.ops.object.select_all(action='DESELECT')
module.select_set(True)
bpy.context.view_layer.objects.active = module

bpy.context.scene["wardrobeModuleRoot"] = module.name
bpy.context.scene["wardrobeOccupiedJunctionCount"] = 3
bpy.context.scene["wardrobeYawStepDegrees"] = 60
bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")
