"""Replace wardrobe review proxies with the same bind-pose meshes used by GarmentDropFactory."""

import bpy
import math
from pathlib import Path
from mathutils import Vector


ROOT = Path("/Volumes/ORICO/HexLive")
OBJ_DIR = ROOT / "Assets/ArtSource/Building/_ArtSource/wardrobe_game_garments"
COLLECTION_NAME = "HL_WARDROBE_REVIEW"


GARMENT_CYCLE = (
    ("ClassicTop", "classic_top.obj", (0.36, 0.16, 0.10, 1.0)),
    ("FighterPants", "fighter_pants.obj", (0.16, 0.13, 0.10, 1.0)),
    ("SweetyBabydoll", "sweety_babydoll.obj", (0.42, 0.18, 0.21, 1.0)),
)
SLOT_COUNT, SLOT_PITCH = 12, 0.065
SAMPLES = tuple(
    (*GARMENT_CYCLE[i % 3][:2], (i - 5.5) * SLOT_PITCH, 1.255,
     GARMENT_CYCLE[i % 3][2]) for i in range(SLOT_COUNT)
)


def collection():
    return bpy.data.collections.get(COLLECTION_NAME)


def remove_proxies():
    col = collection()
    def remove_tree(obj):
        for child in list(obj.children):
            remove_tree(child)
        bpy.data.objects.remove(obj, do_unlink=True)
    names = [obj.name for obj in col.objects]
    for name in names:
        obj = bpy.data.objects.get(name)
        if obj is not None and (name.startswith("HL_Wardrobe_Garment_") or
                name.startswith("HL_Wardrobe_Shoe_") or
                name.startswith("HL_Wardrobe_Hanger_") or
                name.startswith("HL_Wardrobe_RealHanger_") or
                name.startswith("HL_Wardrobe_Real_")):
            remove_tree(obj)


def material(name, colour):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = colour
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = colour
    bsdf.inputs["Roughness"].default_value = 0.72
    return mat


def move_to_collection(obj):
    col = collection()
    for old in list(obj.users_collection):
        old.objects.unlink(obj)
    col.objects.link(obj)


def import_obj(path):
    before = set(bpy.data.objects)
    bpy.ops.wm.obj_import(filepath=str(path), forward_axis='NEGATIVE_Z_FORWARD', up_axis='Y_UP')
    return [o for o in bpy.data.objects if o not in before and o.type == 'MESH']


def combined_bounds(objects):
    pts = [obj.matrix_world @ Vector(corner) for obj in objects for corner in obj.bound_box]
    lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
    hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
    return lo, hi


def twig_between(name, a, b, radius, parent):
    a, b = Vector(a), Vector(b)
    direction = b - a
    bpy.ops.mesh.primitive_cylinder_add(vertices=7, radius=radius,
                                       depth=direction.length, location=(a + b) * 0.5)
    obj = bpy.context.object
    obj.name = name
    move_to_collection(obj)
    obj.rotation_mode = 'QUATERNION'
    obj.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(direction.normalized())
    obj.data.materials.append(material("Sapwood", (0.52, 0.27, 0.10, 1.0)))
    obj.parent = parent


def add_real_hanger(slot_x, index):
    # Universal runtime module. Root/pivot is the exact contact point on the
    # rail; every twig is authored in LOCAL coordinates. The rail runs along X,
    # so the hanger lies in Y/Z and needs no corrective Euler rotation.
    root = bpy.data.objects.new(f"HL_Wardrobe_RealHanger_{index:02}", None)
    collection().objects.link(root)
    root.location = (20.0 + slot_x, -1.035, 1.39)
    root["hangerWidthWu"] = 0.24
    root["pivot"] = "rail_contact"
    apex = (0.0, 0.0, -0.075)
    left = (0.0, -0.12, -0.18)
    right = (0.0, 0.12, -0.18)
    twig_between(f"HL_Wardrobe_RealHanger_{index:02}_ShoulderA", apex, left, 0.010, root)
    twig_between(f"HL_Wardrobe_RealHanger_{index:02}_ShoulderB", apex, right, 0.010, root)
    twig_between(f"HL_Wardrobe_RealHanger_{index:02}_Base", left, right, 0.008, root)
    # Hook curves over the rail in the SAME Y/Z plane, never along the rail.
    hook = [apex, (0.0, 0.0, 0.0), (0.0, 0.035, 0.045), (0.0, 0.075, 0.015)]
    for part in range(len(hook) - 1):
        twig_between(f"HL_Wardrobe_RealHanger_{index:02}_Hook{part}",
                     hook[part], hook[part + 1], 0.008, root)


def add_hanging_sample(label, filename, slot_x, rail_z, colour):
    objects = import_obj(OBJ_DIR / filename)
    root = bpy.data.objects.new(f"HL_Wardrobe_Real_{label}_{slot_x:+.3f}", None)
    collection().objects.link(root)
    root["definitionSource"] = f"HexLiveContent/Wear/{label}"
    root["runtimeFactory"] = "GarmentDropFactory.BuildHanging"
    root["hangerAttachMode"] = "bounds_top_to_shoulder_line"
    root["hangerSlotPitchWu"] = SLOT_PITCH
    root["hangerYawDegrees"] = 90.0
    for obj in objects:
        move_to_collection(obj)
        obj.parent = root
        # OBJ+MTL keeps the same game albedo selected by the sample manifest.
        # Only textureless source garments receive a neutral fallback material.
        if not obj.data.materials or not any(m and m.use_nodes and
                any(n.type == 'TEX_IMAGE' and n.image for n in m.node_tree.nodes)
                for m in obj.data.materials):
            obj.data.materials.clear()
            obj.data.materials.append(material(f"HL_GameGarment_{label}", colour))

    # Runtime BuildHanging contract: upright bind pose, centred on mesh bounds,
    # cloth compressed only front-to-back. Scale to the approved hut kit.
    lo, hi = combined_bounds(objects)
    centre = (lo + hi) * 0.5
    height = max(hi.z - lo.z, 0.001)
    # Preserve the visual hierarchy of the real items: the ring top/bra is
    # compact, the babydoll medium, and trousers longest.
    target_h = {"ClassicTop": 0.24, "FighterPants": 0.56, "SweetyBabydoll": 0.34}[label]
    scale = target_h / height
    root.scale = (scale, scale * 0.12, scale)
    # After a +90° Z turn: source X becomes world Y, compressed source Y
    # becomes -world X. Centre analytically so every garment shares the same
    # slot pivot and its highest vertex rests just below the hanger shoulders.
    root.location = (20.0 + slot_x + centre.y * scale * 0.12,
                     -1.035 - centre.x * scale,
                     rail_z - hi.z * scale)
    # GarmentDropFactory gives us an upright bind-pose garment. On this rack
    # the rail runs along X, therefore the hanger/garment plane must be turned
    # perpendicular to it (plus only a tiny hand-hung variation).
    jitter = {"ClassicTop": -5.0, "FighterPants": 2.0, "SweetyBabydoll": 6.0}[label]
    root.rotation_euler[2] = math.radians(90.0 + jitter)


def add_footwear_pair(filename, pair_index, pair_x):
    objects = import_obj(OBJ_DIR / filename)
    root = bpy.data.objects.new(f"HL_Wardrobe_Real_CanvasSneakers_Pair_{pair_index:02}", None)
    collection().objects.link(root)
    root["definitionSource"] = "clothing.sneakers_canvas"
    root["runtimeFactory"] = "GarmentDropFactory.BuildHanging (footwear stays 3D)"
    for obj in objects:
        move_to_collection(obj)
        obj.parent = root
        if not obj.data.materials or not any(m and m.use_nodes and
                any(n.type == 'TEX_IMAGE' and n.image for n in m.node_tree.nodes)
                for m in obj.data.materials):
            obj.data.materials.clear()
            obj.data.materials.append(material("HL_GameGarment_CanvasSneakers", (0.18, 0.15, 0.12, 1.0)))
    lo, hi = combined_bounds(objects)
    width = max(hi.x - lo.x, 0.001)
    # Keep the actual game's paired footwear mesh, only normalize each pair to
    # a shelf module width. Pair packing below derives count from this width.
    target_pair_width = 0.245
    scale = target_pair_width / width
    root.scale = (scale, scale, scale)
    root.location = (20.0 + pair_x - ((lo.x + hi.x) * 0.5) * scale,
                     -1.065 - ((lo.y + hi.y) * 0.5) * scale,
                     0.315 - lo.z * scale)
    root["pairSlotWidthWu"] = target_pair_width
    root["pairGapWu"] = 0.018


remove_proxies()
for index, sample in enumerate(SAMPLES):
    add_real_hanger(sample[2], index)
    add_hanging_sample(*sample)
# Pack complete pairs over the shelf's 0.88 wu usable width. With a real pair
# module of .245 and .018 clearance this yields three pairs, centred.
PAIR_WIDTH, PAIR_GAP, SHELF_USABLE = 0.245, 0.018, 0.88
PAIR_COUNT = int((SHELF_USABLE + PAIR_GAP) // (PAIR_WIDTH + PAIR_GAP))
PAIR_PITCH = PAIR_WIDTH + PAIR_GAP
for pair_index in range(PAIR_COUNT):
    pair_x = (pair_index - (PAIR_COUNT - 1) * 0.5) * PAIR_PITCH
    add_footwear_pair("canvas_sneakers.obj", pair_index, pair_x)
bpy.context.scene["wardrobeShoePairCount"] = PAIR_COUNT
bpy.context.scene["wardrobeContentSource"] = "GarmentDropFactory.BuildHanging / real game meshes"
bpy.ops.wm.save_as_mainfile(filepath=str(ROOT / "Assets/ArtSource/Building/hexlive_building_kit.blend"))
