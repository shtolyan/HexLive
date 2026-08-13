"""Replace wardrobe cylinders with the exact Stick mesh used by the canonical bed."""

import bpy
from mathutils import Vector


SOURCE = "/Volumes/ORICO/HexLive/Assets/HexLiveContent/source.blend"
COL = bpy.data.collections["HL_WARDROBE_REVIEW"]

with bpy.data.libraries.load(SOURCE, link=False) as (src, dst):
    dst.objects = [name for name in src.objects if name in ("Stick", "rope_00")]

stick_source = next(obj for obj in dst.objects if obj and obj.name.startswith("Stick"))
rope_source = next(obj for obj in dst.objects if obj and obj.name.startswith("rope_00"))
stick_length = stick_source.dimensions.x


def canonical_material(name, colour, roughness=.90):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    mat.diffuse_color = (*colour, 1.0)
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*colour, 1.0)
    bsdf.inputs["Roughness"].default_value = roughness
    return mat


# Exact Principled values read from source.blend's canonical bed Stick.
BED_BARK = canonical_material("HL_BedExact_Bark", (.40, .28, .17))
BED_HEARTWOOD = canonical_material("HL_BedExact_Heartwood", (.88, .55, .26))
BED_ROPE = canonical_material("HL_BedExact_Rope", (.48, .32, .14), .86)


def remove_tokens(tokens):
    for obj in list(COL.objects):
        if any(token in obj.name for token in tokens):
            bpy.data.objects.remove(obj, do_unlink=True)


def native_stick(name, a, b, thickness=1.0, parent=None):
    a, b = Vector(a), Vector(b)
    direction = b - a
    obj = bpy.data.objects.new(name, stick_source.data.copy())
    COL.objects.link(obj)
    obj.location = (a + b) * 0.5
    obj.rotation_mode = 'QUATERNION'
    obj.rotation_quaternion = Vector((1, 0, 0)).rotation_difference(direction.normalized())
    obj.scale = (direction.length / stick_length, thickness, thickness)
    obj.data.materials.clear()
    obj.data.materials.append(BED_BARK)
    obj.data.materials.append(BED_HEARTWOOD)
    # The source mesh has real end-cap polygons, but its imported assignment
    # was lost (all faces pointed at Bark). Recover caps geometrically on the
    # native X axis so every stretched stick retains a visible wood cut.
    for polygon in obj.data.polygons:
        polygon.material_index = 1 if abs(polygon.normal.x) >= .82 else 0
    obj.parent = parent
    obj["source"] = "Assets/HexLiveContent/source.blend::Stick"
    return obj


def native_rope(name, location, scale=1.0, parent=None):
    obj = bpy.data.objects.new(name, rope_source.data.copy())
    COL.objects.link(obj)
    obj.location = location
    obj.scale = (scale, scale, scale)
    obj.data.materials.clear()
    obj.data.materials.append(BED_ROPE)
    obj.parent = parent
    obj["source"] = "Assets/HexLiveContent/source.blend::rope_00"
    return obj


remove_tokens(("FramePost", "TopRail", "HangRail", "RealHanger", "TopBinding", "ShelfBinding",
               "HL_Wardrobe_Native_", "HL_Wardrobe_Hanger_", "HL_Wardrobe_Foot_"))
root = bpy.data.objects.get("HL_Wardrobe_ROOT")
rx, ry, rz = 20.02, -1.02, 0.14

native_stick("HL_Wardrobe_Native_FrameL", (rx-.44, ry, rz), (rx-.44, ry, rz+1.46), 1.05, root)
native_stick("HL_Wardrobe_Native_FrameR", (rx+.44, ry, rz), (rx+.44, ry, rz+1.46), 1.05, root)
native_stick("HL_Wardrobe_Native_TopRail", (rx-.48, ry, rz+1.46), (rx+.48, ry, rz+1.46), 1.0, root)
# Rail physically reaches both posts instead of ending in mid-air.
native_stick("HL_Wardrobe_Native_HangRail", (rx-.455, ry-.015, rz+1.25), (rx+.455, ry-.015, rz+1.25), .72, root)

# Two transverse feet make the freestanding construction legible and stable.
for side, x in (("L", rx-.44), ("R", rx+.44)):
    native_stick(f"HL_Wardrobe_Foot_{side}", (x, ry-.22, rz+.035),
                 (x, ry+.16, rz+.035), .72, root)

for obj in COL.objects:
    if obj.type == 'MESH' and "ShoeShelf" in obj.name:
        obj.data.materials.clear()
        obj.data.materials.append(BED_HEARTWOOD)

# The exact bed rope knot at structural joints; no procedural white torus.
for x in (-.44, .44):
    # Exact bed knot meshes, layered and slightly offset so bindings remain
    # readable around the top beam, hanger rail, shelf and transverse feet.
    for turn, dz in enumerate((-.022, .0, .022)):
        native_rope(f"HL_Wardrobe_Native_TopRope_{x}_{turn}",
                    (rx+x, ry, rz+1.43+dz), .46, root)
    for turn, dz in enumerate((-.012, .012)):
        native_rope(f"HL_Wardrobe_Native_RailRope_{x}_{turn}",
                    (rx+x, ry-.012, rz+1.25+dz), .40, root)
        native_rope(f"HL_Wardrobe_Native_ShelfRope_{x}_{turn}",
                    (rx+x, ry, rz+.145+dz), .38, root)
        native_rope(f"HL_Wardrobe_Native_FootRope_{x}_{turn}",
                    (rx+x, ry, rz+.045+dz), .36, root)

# Twelve identical code-ready hanger modules from the same bed stick mesh.
for index in range(12):
    x = 20.0 + (index - 5.5) * .065
    hanger = bpy.data.objects.new(f"HL_Wardrobe_RealHanger_{index:02}", None)
    COL.objects.link(hanger)
    # Contact pivot lies on the rail centre. The hook wraps over it; the
    # shoulder triangle hangs below, with no floating gap.
    hanger.location = (x, ry-.015, rz+1.25)
    hanger["pivot"] = "rail_contact"
    hanger["widthWu"] = .24
    apex=(0,.0,-.072); left=(0,-.12,-.18); right=(0,.12,-.18)
    native_stick(f"HL_Wardrobe_Hanger_{index:02}_L", apex, left, .28, hanger)
    native_stick(f"HL_Wardrobe_Hanger_{index:02}_R", apex, right, .28, hanger)
    native_stick(f"HL_Wardrobe_Hanger_{index:02}_Base", left, right, .23, hanger)
    # Short hook assembled from the same twig profile.
    hook=(apex,(0,-.012,-.012),(0,-.012,.045),(0,.038,.070),(0,.078,.035),(0,.070,-.005))
    for part in range(len(hook)-1):
        native_stick(f"HL_Wardrobe_Hanger_{index:02}_Hook{part}", hook[part], hook[part+1], .22, hanger)

for obj in dst.objects:
    if obj:
        bpy.data.objects.remove(obj, do_unlink=True)

bpy.context.scene["wardrobeStructureSource"] = "canonical bed Stick + rope_00 meshes"
bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")
