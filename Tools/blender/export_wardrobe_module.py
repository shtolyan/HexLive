"""Export normalized wardrobe art with a zeroed, central-junction pivot."""

import bpy
import math
from pathlib import Path
from mathutils import Matrix

PROJECT_ROOT = Path(__file__).resolve().parents[2]
OUTPUT = str(PROJECT_ROOT / "Assets/HexLiveContent/RuntimeSource/Objects/furniture.wardrobe.fbx")
root = bpy.data.objects["HL_Wardrobe_Module"]

descendants = []
stack = list(root.children)
while stack:
    obj = stack.pop()
    descendants.append(obj)
    stack.extend(obj.children)

export_collection = bpy.data.collections.get("HL_WARDROBE_EXPORT_TEMP")
if export_collection is None:
    export_collection = bpy.data.collections.new("HL_WARDROBE_EXPORT_TEMP")
    bpy.context.scene.collection.children.link(export_collection)

for obj in list(export_collection.objects):
    bpy.data.objects.remove(obj, do_unlink=True)

export_root = bpy.data.objects.new("WardrobeRoot", None)
export_collection.objects.link(export_root)
export_root["pivot"] = "central_occupied_junction"
export_root["occupiedJunctionOffsets"] = [[0.0, -.375], [0.0, 0.0], [0.0, .375]]
export_root["allowedYawDegrees"] = [0, 60, 120, 180, 240, 300]

# Derive the export basis from the visible frame, not from the authoring root.
# Older reviews accumulated parent-inverse transforms on that Empty; the two
# structural posts remain the authoritative geometry contract.  Furniture has
# one shared placement convention: the axis spanning its occupied junctions is
# local +Y in Blender (the same convention as bed_basic_final_native).  The
# renderer may therefore apply the same six footprint yaws to every furniture
# asset without an asset-specific 30/90-degree correction.
left = bpy.data.objects["HL_Wardrobe_Native_FrameL"].matrix_world.translation
right = bpy.data.objects["HL_Wardrobe_Native_FrameR"].matrix_world.translation
foot_l = bpy.data.objects["HL_Wardrobe_Foot_L"].matrix_world.translation
foot_r = bpy.data.objects["HL_Wardrobe_Foot_R"].matrix_world.translation
pivot = (left + right) * .5
pivot.z = min(foot_l.z, foot_r.z) - .105
along = right - left
angle = math.atan2(along.y, along.x)
export_basis = Matrix.Translation(pivot) @ Matrix.Rotation(angle - math.pi * .5, 4, 'Z')
inverse = export_basis.inverted()
copies = []
for source in descendants:
    if source.type != 'MESH' or source.name.startswith("HL_Wardrobe_OccupiedJunction_"):
        continue
    # Review-only contents never belong to the furniture asset. Real garments
    # are spawned from simulation objects; hangers appear with those garments.
    lineage = []
    owner = source
    while owner is not None and owner != root:
        lineage.append(owner.name)
        owner = owner.parent
    if any(name.startswith(("HL_Wardrobe_Real_", "HL_Wardrobe_RealHanger_",
                            "HL_Wardrobe_Hanger_")) for name in lineage):
        continue
    clone = source.copy()
    clone.data = source.data.copy()
    clone.name = source.name.replace("HL_Wardrobe_", "Wardrobe_")
    export_collection.objects.link(clone)
    # FBX evaluates parent inverses as part of its axis conversion.  Assigning
    # matrix_world after parenting left the old parent inverse on the clone,
    # which doubled the building-kit offset in Unity (about 14 wu) and laid the
    # complete wardrobe on its side.  Bake the source relative to our clean
    # export root into the mesh itself and keep every exported Transform clean.
    local = inverse @ source.matrix_world
    clone.data.transform(local)
    clone.parent = export_root
    clone.matrix_parent_inverse = Matrix.Identity(4)
    clone.matrix_basis = Matrix.Identity(4)
    clone.hide_viewport = False
    clone.hide_render = False
    copies.append(clone)

# One authored hanger template belongs to the wardrobe object itself. Runtime
# garments clone this child from the already-open furniture.wardrobe bundle;
# there is no procedural Unity cylinder and no second hanger/icon bundle.
hanger_source = bpy.data.objects.get("HL_Wardrobe_RealHanger_00")
if hanger_source is None:
    raise RuntimeError("HL_Wardrobe_RealHanger_00 is required for HangerTemplate")
hanger_template = bpy.data.objects.new("HangerTemplate", None)
export_collection.objects.link(hanger_template)
hanger_template.parent = export_root
hanger_template.matrix_parent_inverse = Matrix.Identity(4)
hanger_template.matrix_basis = Matrix.Identity(4)
hanger_inverse = hanger_source.matrix_world.inverted()
hanger_descendants = []
hanger_stack = list(hanger_source.children)
while hanger_stack:
    hanger_part = hanger_stack.pop()
    hanger_descendants.append(hanger_part)
    hanger_stack.extend(hanger_part.children)
for source in hanger_descendants:
    if source.type != 'MESH':
        continue
    clone = source.copy()
    clone.data = source.data.copy()
    clone.name = source.name.replace("HL_Wardrobe_Hanger_00_", "Hanger_")
    export_collection.objects.link(clone)
    clone.data.transform(hanger_inverse @ source.matrix_world)
    clone.parent = hanger_template
    clone.matrix_parent_inverse = Matrix.Identity(4)
    clone.matrix_basis = Matrix.Identity(4)
    clone.hide_viewport = False
    clone.hide_render = False
    copies.append(clone)

# Named socket empties are exported for runtime clothing and shoe attachment.
for index in range(12):
    hanger = bpy.data.objects.get(f"HL_Wardrobe_RealHanger_{index:02}")
    if hanger is None:
        continue
    socket = bpy.data.objects.new(f"ClothingSlot_{index:02}", None)
    export_collection.objects.link(socket)
    socket.parent = export_root
    socket.matrix_parent_inverse = Matrix.Identity(4)
    socket.matrix_basis = inverse @ hanger.matrix_world
for index in range(3):
    shoe = bpy.data.objects.get(f"HL_Wardrobe_Real_CanvasSneakers_Pair_{index:02}")
    if shoe is None:
        continue
    socket = bpy.data.objects.new(f"ShoePairSlot_{index:02}", None)
    export_collection.objects.link(socket)
    socket.parent = export_root
    socket.matrix_parent_inverse = Matrix.Identity(4)
    socket.matrix_basis = inverse @ shoe.matrix_world

bpy.ops.object.select_all(action='DESELECT')
export_root.select_set(True)
for obj in copies:
    obj.select_set(True)
for obj in export_collection.objects:
    if obj.type == 'EMPTY':
        obj.select_set(True)
bpy.context.view_layer.objects.active = export_root

bpy.ops.export_scene.fbx(
    filepath=OUTPUT,
    use_selection=True,
    object_types={'EMPTY', 'MESH'},
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_ALL',
    axis_forward='-Z',
    axis_up='Y',
    add_leaf_bones=False,
    bake_anim=False,
    path_mode='AUTO',
)

# Export collection is disposable and must not pollute the authored .blend.
for obj in list(export_collection.objects):
    bpy.data.objects.remove(obj, do_unlink=True)
bpy.data.collections.remove(export_collection)
print(f"EXPORTED {OUTPUT}: {len(copies)} meshes")
