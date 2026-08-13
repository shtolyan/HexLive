"""Restore the earlier simple hanger module and prepare it for manual placement."""

import bpy
from mathutils import Vector


COL = bpy.data.collections["HL_WARDROBE_REVIEW"]
SAPWOOD = bpy.data.materials.get("HL_BedExact_Heartwood")
RX, RY, RZ = 20.02, -1.02, 0.14
SLOT_COUNT, SLOT_PITCH = 12, 0.065


def remove_tree(obj):
    for child in list(obj.children):
        remove_tree(child)
    bpy.data.objects.remove(obj, do_unlink=True)


for name in [o.name for o in COL.objects if o.name.startswith("HL_Wardrobe_RealHanger_")
             or o.name.startswith("HL_Wardrobe_Hanger_")]:
    obj = bpy.data.objects.get(name)
    if obj:
        remove_tree(obj)


def twig(name, a, b, radius, parent):
    a, b = Vector(a), Vector(b)
    d = b - a
    bpy.ops.mesh.primitive_cylinder_add(vertices=7, radius=radius,
                                       depth=d.length, location=(a + b) * .5)
    obj = bpy.context.object
    obj.name = name
    for old in list(obj.users_collection):
        old.objects.unlink(obj)
    COL.objects.link(obj)
    obj.rotation_mode = 'QUATERNION'
    obj.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(d.normalized())
    obj.data.materials.append(SAPWOOD)
    obj.parent = parent


for index in range(SLOT_COUNT):
    slot_x = (index - (SLOT_COUNT - 1) * .5) * SLOT_PITCH
    # Origin is exactly the rail contact. Moving/rotating this one root keeps
    # every child together and makes manual placement predictable.
    root = bpy.data.objects.new(f"HL_Wardrobe_RealHanger_{index:02}", None)
    COL.objects.link(root)
    root.location = (20.0 + slot_x, RY - .015, RZ + 1.25)
    root.empty_display_type = 'SPHERE'
    root.empty_display_size = .015
    root["pivot"] = "rail_contact"
    root["manualEditable"] = True
    apex = (0, 0, -.075)
    left, right = (0, -.12, -.18), (0, .12, -.18)
    twig(f"HL_Wardrobe_Hanger_{index:02}_L", apex, left, .010, root)
    twig(f"HL_Wardrobe_Hanger_{index:02}_R", apex, right, .010, root)
    twig(f"HL_Wardrobe_Hanger_{index:02}_Base", left, right, .008, root)
    # Earlier compact hook, without the regressed oversized loop.
    hook = (apex, (0, 0, 0), (0, .035, .045), (0, .075, .015))
    for part in range(3):
        twig(f"HL_Wardrobe_Hanger_{index:02}_Hook{part}", hook[part], hook[part+1], .008, root)

# Make the wardrobe the editing focus and select only the twelve movable roots.
bpy.ops.object.select_all(action='DESELECT')
for index in range(SLOT_COUNT):
    bpy.data.objects[f"HL_Wardrobe_RealHanger_{index:02}"].select_set(True)
bpy.context.view_layer.objects.active = bpy.data.objects["HL_Wardrobe_RealHanger_00"]
bpy.context.scene.cursor.location = (20.0, RY, .85)
bpy.context.scene["wardrobeManualEditCenter"] = [20.0, RY, .85]
bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")
