import bpy
from mathutils import Vector, Matrix


EXPORT = "HL_HEX_FIT_BED_EXPORT"
PRESENTATION = "HL_HEX_FIT_BED_PRESENTATION"
SCENE_NAME = "HexFitBed_Review"
SOURCE_ROOT = "BED2_root"
ART_W = .57
ART_L = 1.42
APOTHEM = 1.5 * (3 ** .5) / 2
INNER_X = APOTHEM / 2
CENTER_X = (APOTHEM + INNER_X) / 2


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
            low.x = min(low.x, point.x); low.y = min(low.y, point.y); low.z = min(low.z, point.z)
            high.x = max(high.x, point.x); high.y = max(high.y, point.y); high.z = max(high.z, point.z)
    return low, high


def clear_collection(collection):
    for obj in list(collection.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def copy_tree(source, parent, collection, prefix=""):
    copied = source.copy()
    if source.data is not None:
        copied.data = source.data.copy()
    copied.name = prefix + source.name
    collection.objects.link(copied)
    copied.parent = parent
    copied.matrix_parent_inverse = Matrix.Identity(4)
    copied.matrix_local = source.matrix_local.copy()
    for child in source.children:
        copy_tree(child, copied, collection, prefix)
    return copied


def duplicate_tree(source, parent, collection, prefix):
    copied = source.copy()
    if source.data is not None:
        copied.data = source.data.copy()
    copied.name = prefix + source.name
    collection.objects.link(copied)
    copied.parent = parent
    copied.matrix_parent_inverse = Matrix.Identity(4)
    copied.matrix_local = source.matrix_local.copy()
    copied.hide_viewport = False
    copied.hide_render = source.name == "point"
    for child in source.children:
        duplicate_tree(child, copied, collection, prefix)
    return copied


source = bpy.data.objects.get(SOURCE_ROOT)
export = bpy.data.collections.get(EXPORT)
presentation = bpy.data.collections.get(PRESENTATION)
review = bpy.data.scenes.get(SCENE_NAME)
if source is None or export is None or presentation is None or review is None:
    raise RuntimeError("Original BED2_root or HexFitBed review data is missing")

low, high = root_bounds(source)
size = high - low
scale_x = ART_W / size.x
scale_y = ART_L / size.y
scale_z = min(scale_x, scale_y)
source_center = (low + high) * .5

# Remove only the generated bed models; exact hex/grid/reservation markers stay.
clear_collection(export)
for obj in list(presentation.objects):
    if obj.name.startswith(("L_", "R_")):
        bpy.data.objects.remove(obj, do_unlink=True)

master = bpy.data.objects.new("HexFitBed_root", None)
export.objects.link(master)
master.scale = (scale_x, scale_y, scale_z)
master.location = (-source_center.x * scale_x, -source_center.y * scale_y,
                   .04 - low.z * scale_z)
for child in source.children:
    copy_tree(child, master, export)

# A hidden sleep marker at the surface center, separate from construction pieces.
point = bpy.data.objects.new("point", None)
point.empty_display_type = "SPHERE"
point.empty_display_size = .035
point.location = (0, 0, .04 + (high.z-low.z)*scale_z*.72)
point.parent = master
export.objects.link(point)

for side, prefix in ((-1, "L_"), (1, "R_")):
    root = bpy.data.objects.new(prefix + "HexFitBed_root", None)
    presentation.objects.link(root)
    root.location = (side * CENTER_X - source_center.x * scale_x,
                     -source_center.y * scale_y,
                     .04 - low.z * scale_z)
    root.scale = master.scale.copy()
    for child in master.children:
        if child.name == "point":
            continue
        duplicate_tree(child, root, presentation, prefix)

review["style_source"] = "BED2_root: 4 log + 5 stick + 10 rope + 50 loose leaf"
review["has_pillow"] = False
review["art_width_wu"] = ART_W
review["art_length_wu"] = ART_L
review["reserved_junctions_per_bed"] = 14

bpy.context.window.scene = review
bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)
print("RESTYLED_FROM", SOURCE_ROOT)
print("SOURCE_BOUNDS", tuple(round(v, 4) for v in size))
print("SCALE", round(scale_x, 5), round(scale_y, 5), round(scale_z, 5))
print("PIECES", len(descendants(source)), "NO_PILLOW", True)
