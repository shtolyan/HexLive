"""Author the one-stage indoor hearth and a grounded hut review in Blender.

Run inside the already-open Blender that owns hexlive_building_kit.blend.
The export collection is local-space, rests on Z=0, and contains no cooking
spit.  The review composition uses the measured hut floor top (0.1075 wu).
"""

import math
import os
import random

import bpy
from mathutils import Vector


EXPORT = "HL_INDOOR_HEARTH_EXPORT"
REVIEW = "HL_INDOOR_HEARTH_REVIEW"
PREFIX = "HL_IndoorHearth_"
REVIEW_X = 14.0
FLOOR_TOP = 0.075 + 0.065 * 0.5
PREVIEW = "/private/tmp/hexlive_indoor_hearth_review.png"


def collection(name):
    found = bpy.data.collections.get(name)
    if found is None:
        found = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(found)
    return found


def clear_collection(coll):
    for obj in list(coll.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def material(name, color, emission=0.0, roughness=0.82):
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
    mat.diffuse_color = (*color, 1.0)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get("Principled BSDF")
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*color, 1.0)
        bsdf.inputs["Roughness"].default_value = roughness
        if "Emission Color" in bsdf.inputs:
            bsdf.inputs["Emission Color"].default_value = (*color, 1.0)
            bsdf.inputs["Emission Strength"].default_value = emission
        elif "Emission" in bsdf.inputs:
            bsdf.inputs["Emission"].default_value = (*color, 1.0)
    return mat


def move_to(obj, coll):
    for old in list(obj.users_collection):
        old.objects.unlink(obj)
    coll.objects.link(obj)
    return obj


def assign(obj, mat):
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    return obj


def cylinder(name, radius, depth, loc, mat, vertices=10, rotation=(0, 0, 0), coll=None):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices, radius=radius, depth=depth, location=loc, rotation=rotation
    )
    obj = bpy.context.object
    obj.name = name
    obj.data.name = name + "_mesh"
    assign(obj, mat)
    move_to(obj, coll)
    return obj


def ico(name, radius, loc, scale, rotation, mat, coll):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=radius, location=loc)
    obj = bpy.context.object
    obj.name = name
    obj.data.name = name + "_mesh"
    obj.scale = scale
    obj.rotation_euler = rotation
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    assign(obj, mat)
    move_to(obj, coll)
    return obj


def beam(name, start, end, radius, mat, coll, vertices=8):
    start, end = Vector(start), Vector(end)
    delta = end - start
    obj = cylinder(name, radius, delta.length, (start + end) * 0.5, mat, vertices, coll=coll)
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = Vector((0, 0, 1)).rotation_difference(delta.normalized())
    obj.rotation_mode = "XYZ"
    return obj


def flame(name, loc, radius, height, mat, tilt, coll):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=1.0, location=loc)
    obj = bpy.context.object
    obj.name = name
    obj.data.name = name + "_mesh"
    obj.scale = (radius, radius * 0.78, height)
    obj.rotation_euler = tilt
    # Pull the top into a faceted tongue rather than using a smooth blob.
    for vert in obj.data.vertices:
        if vert.co.z > 0.35:
            factor = max(0.18, 1.0 - (vert.co.z - 0.35) * 0.70)
            vert.co.x *= factor
            vert.co.y *= factor
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    assign(obj, mat)
    move_to(obj, coll)
    return obj


def author_export():
    coll = collection(EXPORT)
    clear_collection(coll)
    rng = random.Random(1308)

    stone = material("HearthStone", (0.30, 0.275, 0.235))
    stone_light = material("HearthStoneLight", (0.43, 0.39, 0.32))
    ash = material("HearthAsh", (0.12, 0.105, 0.09))
    ember = material("HearthEmber", (0.48, 0.075, 0.018), emission=1.8)
    bark = bpy.data.materials.get("Bark") or material("Bark", (0.23, 0.11, 0.045))
    heartwood = bpy.data.materials.get("Heartwood") or material("Heartwood", (0.50, 0.25, 0.08))
    flame_outer = material("HearthFlameOuter", (1.0, 0.22, 0.025), emission=3.2, roughness=0.4)
    flame_inner = material("HearthFlameInner", (1.0, 0.72, 0.12), emission=4.5, roughness=0.35)

    root = bpy.data.objects.new(PREFIX + "root", None)
    coll.objects.link(root)
    root["hexlive_definition"] = "furniture.hearth_indoor"
    root["build_stages"] = 1
    root["warms_only"] = True
    root["can_cook"] = False
    root["occupied_hexes"] = 1
    root["footprint_diameter_wu"] = 1.0
    root["floor_contact_z"] = 0.0

    # The stone rim reaches almost 1.0 wu across: roughly twice the footprint
    # area of the old ~0.72-wu hearth, while remaining a one-hex object.
    base = cylinder(PREFIX + "stage_01_stone_plinth", 0.43, 0.105, (0, 0, 0.0525), stone,
                    vertices=12, coll=coll)
    base.scale.x = 1.04
    base.scale.y = 0.98
    base.parent = root
    ash_bed = cylinder(PREFIX + "stage_01_ash_bed", 0.31, 0.035, (0, 0, 0.119), ash,
                       vertices=12, coll=coll)
    ash_bed.parent = root

    for i in range(12):
        angle = 2 * math.pi * i / 12
        radial = 0.355 + rng.uniform(-0.012, 0.012)
        obj = ico(
            f"{PREFIX}stage_01_rim_stone_{i:02d}",
            0.115 + rng.uniform(-0.008, 0.009),
            (math.cos(angle) * radial, math.sin(angle) * radial, 0.155 + rng.uniform(-0.005, 0.006)),
            (1.12 + rng.uniform(-0.08, 0.08), 0.76 + rng.uniform(-0.06, 0.06), 0.62 + rng.uniform(-0.05, 0.05)),
            (rng.uniform(-0.16, 0.16), rng.uniform(-0.16, 0.16), angle + rng.uniform(-0.2, 0.2)),
            stone_light if i in (1, 5, 8) else stone,
            coll,
        )
        obj.parent = root

    log_specs = [
        ((-0.25, -0.18, 0.205), (0.25, 0.18, 0.205), bark),
        ((-0.25, 0.18, 0.218), (0.25, -0.18, 0.218), heartwood),
        ((-0.20, -0.03, 0.265), (0.20, 0.03, 0.265), bark),
    ]
    for i, (start, end, mat) in enumerate(log_specs):
        obj = beam(f"{PREFIX}stage_01_charred_log_{i:02d}", start, end, 0.044, mat, coll)
        obj.parent = root

    for i, (x, y, z, scale) in enumerate([
        (-0.12, -0.04, 0.282, (1.0, .75, .55)),
        (0.10, 0.05, 0.286, (.85, .70, .50)),
        (0.00, -0.12, 0.278, (.75, .60, .45)),
    ]):
        obj = ico(f"{PREFIX}stage_01_ember_{i:02d}", 0.075, (x, y, z), scale,
                  (0, 0, rng.uniform(0, math.pi)), ember, coll)
        obj.parent = root

    outer = flame(PREFIX + "stage_01_flame_outer", (0.01, 0.005, 0.43), 0.19, 0.31,
                  flame_outer, (0.08, -0.05, -0.12), coll)
    inner = flame(PREFIX + "stage_01_flame_inner", (-0.035, -0.015, 0.405), 0.105, 0.225,
                  flame_inner, (-0.06, 0.08, 0.15), coll)
    outer.parent = root
    inner.parent = root

    fire_point = bpy.data.objects.new(PREFIX + "fire_point", None)
    coll.objects.link(fire_point)
    fire_point.location = (0, 0, 0.31)
    fire_point.parent = root
    return coll


def linked_instance(name, source, location, coll, rotation_z=0.0):
    obj = bpy.data.objects.new(name, None)
    obj.instance_type = "COLLECTION"
    obj.instance_collection = source
    obj.location = location
    obj.rotation_euler.z = rotation_z
    coll.objects.link(obj)
    return obj


def duplicate_hut_cutaway(source, coll):
    """Linked-mesh review copy: no roof and no two camera-facing wall edges."""
    excluded_prefixes = (
        "HL_Roof_",
        "HL_Bay_00_", "HL_Bay_01_", "HL_Bay_02_", "HL_Bay_03_",
        "HL_Bay_10_", "HL_Bay_11_",
        "HL_Post_00_", "HL_Post_01_", "HL_Post_02_", "HL_Post_03_", "HL_Post_11_",
    )
    for original in source.objects:
        if original.name.startswith(excluded_prefixes):
            continue
        if original.type not in {"MESH", "EMPTY"}:
            continue
        copy = original.copy()
        if original.data is not None:
            copy.data = original.data
        copy.name = PREFIX + "ReviewHut_" + original.name
        copy.matrix_world = original.matrix_world.copy()
        copy.location.x += REVIEW_X
        coll.objects.link(copy)


def duplicate_finished_bed(source, coll, x_offset):
    """Flatten a finished linked-mesh copy without changing staged hide flags."""
    source_bottom = min(
        (original.matrix_world @ Vector(corner)).z
        for original in source.objects if original.type == "MESH"
        for corner in original.bound_box
    )
    grounded_z = FLOOR_TOP - source_bottom
    for original in source.objects:
        if original.type != "MESH":
            continue
        copy = original.copy()
        copy.data = original.data
        copy.name = PREFIX + "ReviewBed_" + ("L_" if x_offset < 0 else "R_") + original.name
        copy.parent = None
        copy.matrix_world = original.matrix_world.copy()
        copy.location.x += REVIEW_X + x_offset
        copy.location.z += grounded_z
        copy.hide_render = False
        copy.hide_viewport = False
        coll.objects.link(copy)


def author_review(export):
    review = collection(REVIEW)
    clear_collection(review)

    hut = bpy.data.collections.get("HL_BUILDING_HUT_1HEX")
    bed = bpy.data.collections.get("HL_HEX_FIT_BED_EXPORT")
    if hut:
        duplicate_hut_cutaway(hut, review)
    if bed:
        # Same anchors as the game after BedWallSnugOffset: ±0.8955 wu.
        duplicate_finished_bed(bed, review, -0.8955)
        duplicate_finished_bed(bed, review, 0.8955)
    # Hearth stays toward the rear like BuildingBootstrap's -forward*0.55;
    # its visual -0.20 offset is folded into this authored review location.
    linked_instance(PREFIX + "Review_Hearth", export, (REVIEW_X, 0.72, FLOOR_TOP), review)

    # Warm practical light, deliberately separate from export geometry.
    light_data = bpy.data.lights.new(PREFIX + "Review_FireLight", "POINT")
    light_data.energy = 240
    light_data.color = (1.0, 0.28, 0.07)
    light_data.shadow_soft_size = 1.0
    light = bpy.data.objects.new(PREFIX + "Review_FireLight", light_data)
    light.location = (REVIEW_X, 0.72, FLOOR_TOP + 0.48)
    review.objects.link(light)

    target = bpy.data.objects.new(PREFIX + "Review_Target", None)
    target.location = (REVIEW_X, 0.12, 0.75)
    review.objects.link(target)
    cam_data = bpy.data.cameras.new(PREFIX + "Review_Camera")
    cam_data.lens = 48
    cam = bpy.data.objects.new(PREFIX + "Review_Camera", cam_data)
    cam.location = (REVIEW_X + 3.7, -5.2, 3.0)
    review.objects.link(cam)
    constraint = cam.constraints.new("TRACK_TO")
    constraint.target = target
    constraint.track_axis = "TRACK_NEGATIVE_Z"
    constraint.up_axis = "UP_Y"
    bpy.context.scene.camera = cam

    key_data = bpy.data.lights.new(PREFIX + "Review_Key", "AREA")
    key_data.energy = 800
    key_data.shape = "DISK"
    key_data.size = 5.0
    key = bpy.data.objects.new(PREFIX + "Review_Key", key_data)
    key.location = (REVIEW_X - 4.0, -4.5, 7.0)
    review.objects.link(key)


def render_and_save():
    scene = bpy.context.scene
    try:
        scene.render.engine = "BLENDER_EEVEE_NEXT"
    except TypeError:
        scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 1200
    scene.render.resolution_y = 900
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = PREVIEW
    scene.render.film_transparent = False
    scene.world.color = (0.035, 0.045, 0.035)
    bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath)
    bpy.ops.render.render(write_still=True)
    print({"blend": bpy.data.filepath, "preview": PREVIEW, "floor_top": FLOOR_TOP})


if __name__ == "__main__":
    export_collection = author_export()
    author_review(export_collection)
    render_and_save()
