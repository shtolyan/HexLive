"""Build a clean game-faithful hut review around the exact wardrobe mount nodes."""

import bpy
import math
from mathutils import Matrix, Vector


COL = bpy.data.collections["HL_WARDROBE_REVIEW"]
HEX = Vector((20.0, 0.0, 0.0))
MOUNT_CENTER = Vector((20.649519, -1.125, 0.0))
TANGENT = Vector((math.cos(math.radians(30)), math.sin(math.radians(30)), 0.0))
NORMAL = Vector((-TANGENT.y, TANGENT.x, 0.0))


def remove_prefix(prefix):
    for obj in list(bpy.data.objects):
        if obj.name.startswith(prefix):
            bpy.data.objects.remove(obj, do_unlink=True)


# Production hut FBX contains its real modular walls, windows, door and floor.
remove_prefix("HL_GameHut_")
before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath="/Volumes/ORICO/HexLive/Assets/HexLiveContent/RuntimeSource/Objects/building.hut_1hex.fbx")
imported = [obj for obj in bpy.data.objects if obj not in before]
parented = {child for obj in imported for child in obj.children}
roots = [obj for obj in imported if obj not in parented]
hut_root = bpy.data.objects.new("HL_GameHut_ROOT", None)
COL.objects.link(hut_root)
hut_root.location = (20, 0, 0)
hut_root["source"] = "Resources/HexLive/Objects/building.hut_1hex.fbx"
for obj in roots:
    world = obj.matrix_world.copy(); obj.parent = hut_root; obj.matrix_world = world
for obj in imported:
    obj.name = "HL_GameHut_" + obj.name
    for old in list(obj.users_collection): old.objects.unlink(obj)
    COL.objects.link(obj)

# Hide roof pieces for an interior-review image; geometry remains in file.
for obj in imported:
    if "roof" in obj.name.lower() or "leaf" in obj.name.lower() and obj.matrix_world.translation.z > 1.1:
        obj.hide_render = True

# Remove the incomplete separately cloned door; production hut owns the door.
remove_prefix("HL_GameDoor_Bay07_")

# Rebuild wardrobe placement from ONE rigid affine transform, based on its
# pre-snap authored centre. This avoids scaling independent world pivots twice.
authored_center = Vector((20.02, -1.02, 0.0))
angle = math.radians(30)
rotation = Matrix.Rotation(angle, 4, 'Z')
scale = Matrix.Identity(4); scale[0][0] = .375 / .44
transform = (Matrix.Translation(MOUNT_CENTER) @ rotation @ scale @
             Matrix.Translation(-authored_center))

# Recreate all wardrobe content from the deterministic scripts, then transform
# every top-level module by the exact same matrix.
exec(open("/Volumes/ORICO/HexLive/Tools/blender/import_real_wardrobe_garments.py").read(), globals())
exec(open("/Volumes/ORICO/HexLive/Tools/blender/rebuild_wardrobe_from_bed_sticks.py").read(), globals())
exec(open("/Volumes/ORICO/HexLive/Tools/blender/restore_manual_wardrobe_hangers.py").read(), globals())

def is_wardrobe_top(obj):
    return obj.parent is None and (obj.name == "HL_Wardrobe_ROOT" or
        obj.name.startswith("HL_Wardrobe_Real_") or
        obj.name.startswith("HL_Wardrobe_RealHanger_") or
        obj.name.startswith("HL_Wardrobe_Slot_"))

for obj in list(COL.objects):
    if is_wardrobe_top(obj):
        obj.matrix_world = transform @ obj.matrix_world

root = bpy.data.objects.get("HL_Wardrobe_ROOT")
if root:
    root["mountNodes"] = "edge t=.25/.50/.75"
    root["mountEdgeYaw"] = 30.0

# Remove old proxies and irrelevant off-hex review debris from render.
for obj in COL.objects:
    if obj.name.startswith("HL_Wardrobe_Footprint_") or obj.name == "HL_Wardrobe_Interaction":
        obj.hide_render = True
    if obj.name.startswith("HL_Wardrobe_HexEdge_"):
        obj.hide_render = True

bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")
