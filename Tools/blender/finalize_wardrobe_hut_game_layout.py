"""Exact junction snap plus production beds and approved bay-7 door for hut review."""

import bpy
import math
from mathutils import Matrix, Vector


COL = bpy.data.collections["HL_WARDROBE_REVIEW"]
CENTER = Vector((20.649519, -1.125, 0.0))
TANGENT = Vector((math.cos(math.radians(30)), math.sin(math.radians(30)), 0.0))
NORMAL = Vector((-TANGENT.y, TANGENT.x, 0.0))
WIDTH_SCALE = .375 / .44


def wardrobe_top_root(obj):
    if obj.parent is not None:
        return False
    return (obj.name == "HL_Wardrobe_ROOT" or
            obj.name.startswith("HL_Wardrobe_Real_") or
            obj.name.startswith("HL_Wardrobe_RealHanger_") or
            obj.name.startswith("HL_Wardrobe_Slot_"))


# Compress the entire authored module only along its wall tangent. The two
# frame posts now land exactly at the t=.25 and t=.75 junctions, with centre at
# t=.50. Contents preserve their relative slot ordering.
for obj in list(COL.objects):
    if not wardrobe_top_root(obj):
        continue
    delta = obj.matrix_world.translation - CENTER
    along, across = delta.dot(TANGENT), delta.dot(NORMAL)
    new_position = CENTER + TANGENT * (along * WIDTH_SCALE) + NORMAL * across
    world = obj.matrix_world.copy()
    world.translation = new_position
    obj.matrix_world = world
root = bpy.data.objects.get("HL_Wardrobe_ROOT")
if root:
    root.scale.x *= WIDTH_SCALE
    root["mountJunctions"] = ["edge.t25", "edge.t50", "edge.t75"]


def remove_prefix(prefix):
    for obj in list(bpy.data.objects):
        if obj.name.startswith(prefix):
            bpy.data.objects.remove(obj, do_unlink=True)


# Remove proxy beds and previous production review instances.
remove_prefix("HL_Wardrobe_BedA")
remove_prefix("HL_Wardrobe_BedB")
remove_prefix("HL_GameBed_")


def import_game_bed(label, x, y, yaw):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath="/Volumes/ORICO/HexLive/Assets/HexLiveContent/RuntimeSource/Objects/bed_basic_final_native.fbx")
    imported = [obj for obj in bpy.data.objects if obj not in before]
    parented = {child for obj in imported for child in obj.children}
    roots = [obj for obj in imported if obj not in parented]
    assembly = bpy.data.objects.new(f"HL_GameBed_{label}", None)
    COL.objects.link(assembly)
    assembly.location = (x, y, .14)
    assembly.rotation_euler[2] = math.radians(yaw)
    assembly["source"] = "Resources/HexLive/Objects/bed_basic_final_native.fbx"
    assembly["buildingRulesYaw"] = yaw
    for obj in roots:
        # Rehome all imported roots under one deterministic placement pivot.
        world = obj.matrix_world.copy()
        obj.parent = assembly
        obj.matrix_world = world
    for obj in imported:
        obj.name = f"HL_GameBed_{label}_{obj.name}"
        for old in list(obj.users_collection):
            old.objects.unlink(obj)
        COL.objects.link(obj)
    return assembly


# Exact BuildingRules local positions/yaws, translated to this review hex.
import_game_bed("A", 20.0 - .974279, 0.0, 0.0)
import_game_bed("B", 20.0 + .487139, .84375, 60.0)


# Door art is authored as bay 0 on the lower-right half. Bay 7 is the opposite
# edge's second half: rotate the complete module 180 degrees about hex centre.
remove_prefix("HL_GameDoor_Bay07_")
door_sources = [obj for obj in bpy.data.objects if obj.name.startswith("HL_Bay_00_Door_")]
rot180 = Matrix.Translation((20, 0, 0)) @ Matrix.Rotation(math.pi, 4, 'Z')
for source in door_sources:
    clone = source.copy()
    if source.data:
        clone.data = source.data.copy()
    clone.name = "HL_GameDoor_Bay07_" + source.name.removeprefix("HL_Bay_00_Door_")
    COL.objects.link(clone)
    clone.matrix_world = rot180 @ source.matrix_world
    clone["buildingElement"] = "door.7"

# Hide the old proxy markers from beauty render but keep them selectable for
# layout inspection; their viewport state stays visible.
for obj in COL.objects:
    if obj.name.startswith("HL_Wardrobe_Footprint_") or obj.name == "HL_Wardrobe_Interaction":
        obj.hide_render = True

bpy.context.scene["hutReviewUsesProductionBeds"] = True
bpy.context.scene["hutReviewDoorBay"] = 7
bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")
