"""Replace wardrobe peg-like lashings and shelf props with readable rope coils."""

import bpy


COLLECTION = "HL_WARDROBE_REVIEW"
PREFIX = "HL_Wardrobe_"
RX, RY, RZ = 20.02, -1.02, 0.14

col = bpy.data.collections[COLLECTION]
root = bpy.data.objects.get(PREFIX + "ROOT")
rope_mat = bpy.data.materials.get("Rope")


def remove_matching(parts):
    for obj in list(col.objects):
        if any(part in obj.name for part in parts):
            bpy.data.objects.remove(obj, do_unlink=True)


def rope_wrap(name, location, major, minor):
    bpy.ops.mesh.primitive_torus_add(
        major_segments=12,
        minor_segments=5,
        location=location,
        major_radius=major,
        minor_radius=minor,
    )
    obj = bpy.context.object
    obj.name = PREFIX + name
    for old in list(obj.users_collection):
        old.objects.unlink(obj)
    col.objects.link(obj)
    obj.data.materials.append(rope_mat)
    obj.parent = root
    return obj


remove_matching(("Lashing_", "ShelfSupport_", "TopBinding_", "ShelfBinding_"))

for x in (-0.44, 0.44):
    # Three irregular cord turns around each post/top-rail junction.
    for index, z in enumerate((RZ + 1.405, RZ + 1.430, RZ + 1.455)):
        coil = rope_wrap(f"TopBinding_{x}_{index}", (RX + x, RY, z), 0.046, 0.009)
        coil.rotation_euler[2] = (index - 1) * 0.08

    # The shelf is tied directly to both posts; no little support sticks.
    for index, z in enumerate((RZ + 0.120, RZ + 0.145)):
        coil = rope_wrap(f"ShelfBinding_{x}_{index}", (RX + x, RY, z), 0.043, 0.008)
        coil.rotation_euler[2] = (index * 2 - 1) * 0.06

bpy.context.scene["wardrobeJoinery"] = "rope coils only; no peg lashings or shelf support sticks"
bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")
