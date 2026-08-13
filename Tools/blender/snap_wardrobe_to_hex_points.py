"""Snap the wardrobe to a pointy-top hex wall and visualize its junction footprint."""

import bpy
import math
from mathutils import Matrix, Vector


COL = bpy.data.collections["HL_WARDROBE_REVIEW"]
OLD_CENTER = Vector((20.02, -1.02, 0.0))
HEX_CENTER = Vector((20.0, 0.0, 0.0))
RADIUS = 1.5
JUNCTION_STEP = 0.375
YAW_DEGREES = 30.0

# Lower-right pointy-top edge: bottom vertex -> lower-right vertex.
edge_a = HEX_CENTER + Vector((0.0, -RADIUS, 0.0))
edge_b = HEX_CENTER + Vector((math.sqrt(3) * RADIUS * .5, -RADIUS * .5, 0.0))
edge_mid = (edge_a + edge_b) * .5


def is_wardrobe_root(obj):
    if obj.parent is not None:
        return False
    return (obj.name == "HL_Wardrobe_ROOT" or
            obj.name.startswith("HL_Wardrobe_Real_") or
            obj.name.startswith("HL_Wardrobe_RealHanger_") or
            obj.name.startswith("HL_Wardrobe_Slot_"))


# Delete old layout markers.
for obj in list(COL.objects):
    if obj.name.startswith("HL_Wardrobe_Footprint_") or obj.name == "HL_Wardrobe_Interaction":
        bpy.data.objects.remove(obj, do_unlink=True)

# Move the complete authored module rigidly; children retain their local layout.
angle = math.radians(YAW_DEGREES)
transform = (Matrix.Translation(edge_mid - OLD_CENTER) @
             Matrix.Translation(OLD_CENTER) @
             Matrix.Rotation(angle, 4, 'Z') @
             Matrix.Translation(-OLD_CENTER))
for obj in list(COL.objects):
    if is_wardrobe_root(obj):
        obj.matrix_world = transform @ obj.matrix_world


def marker(name, location, colour, size=.032):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=size, location=location)
    obj = bpy.context.object
    obj.name = name
    for old in list(obj.users_collection):
        old.objects.unlink(obj)
    COL.objects.link(obj)
    mat_name = "HL_FootprintBlocked" if colour[0] > colour[1] else "HL_FootprintInteract"
    mat = bpy.data.materials.get(mat_name) or bpy.data.materials.new(mat_name)
    mat.diffuse_color = (*colour, 1.0)
    mat.use_nodes = True
    mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*colour, 1.0)
    mat.node_tree.nodes["Principled BSDF"].inputs["Emission"].default_value = (*colour, 1.0)
    mat.node_tree.nodes["Principled BSDF"].inputs["Emission Strength"].default_value = .35
    obj.data.materials.append(mat)
    return obj


tangent = (edge_b - edge_a).normalized()
inward = (HEX_CENTER - edge_mid).normalized()

# The wardrobe occupies exactly three wall junctions t=.25/.50/.75.
occupied = []
for column, t in enumerate((.25, .50, .75)):
    p = edge_a.lerp(edge_b, t)
    p.z = .175
    occupied.append(p)
    marker(f"HL_Wardrobe_Footprint_C{column}", p, (.85, .08, .08), .032)

# Use the central node one more row inward as the approach/interaction point.
interaction = edge_mid + inward * (math.sqrt(3) * JUNCTION_STEP)
interaction.z = .175
marker("HL_Wardrobe_Interaction", interaction, (.10, .75, .36), .043)

scene = bpy.context.scene
scene["wardrobeHexEdge"] = "lower_right"
scene["wardrobeYawDegrees"] = YAW_DEGREES
scene["wardrobeAllowedYawDegrees"] = [0, 60, 120, 180, 240, 300]
scene["wardrobeOccupiedJunctionCount"] = 3
scene["wardrobeInteractionJunctionCount"] = 1
scene["wardrobeJunctionStepWu"] = JUNCTION_STEP

# Select the whole rack root for immediate Frame Selected/manual orbit.
bpy.ops.object.select_all(action='DESELECT')
root = bpy.data.objects.get("HL_Wardrobe_ROOT")
if root:
    root.select_set(True)
    bpy.context.view_layer.objects.active = root

bpy.ops.wm.save_as_mainfile(filepath="/Volumes/ORICO/HexLive/Assets/ArtSource/Building/hexlive_building_kit.blend")
