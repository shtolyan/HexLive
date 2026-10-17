"""Generate deterministic low-poly seed props for §173.

Run with the project's Blender:

    /Applications/Blender.app/Contents/MacOS/Blender -b \
        -P Tools/make_plant_seed_models.py

The herb seed is a small tan three-pod cluster derived from the canonical
``Rope`` palette. Yucca seeds are a dark, flat four-seed fan derived from
``StoneGrey``. Runtime ObjectFit owns their final world size.
"""

from __future__ import annotations

import math
import os

import bpy


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_BLEND = os.path.join(REPO, "Assets/HexLiveContent/source.blend")
OUTPUT_DIR = os.path.join(REPO, "Assets/HexLiveContent/RuntimeSource/Objects")


def principled(material):
    return next(node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED")


def palette_copy(source_name, output_name, multiplier, warmth=0.0):
    with bpy.data.libraries.load(SOURCE_BLEND, link=False) as (source, target):
        assert source_name in source.materials, f"source.blend has no {source_name}"
        target.materials = [source_name]

    material = bpy.data.materials[source_name]
    material.name = output_name
    material.use_nodes = True
    shader = principled(material)
    base = tuple(shader.inputs["Base Color"].default_value[:3])
    shader.inputs["Base Color"].default_value = (
        min(1.0, base[0] * multiplier + warmth),
        min(1.0, base[1] * multiplier + warmth * 0.65),
        min(1.0, base[2] * multiplier),
        1.0,
    )
    shader.inputs["Roughness"].default_value = 0.92
    return material


def add_seed(name, location, scale, rotation, material):
    bpy.ops.mesh.primitive_ico_sphere_add(
        subdivisions=1, radius=1.0, location=location, rotation=rotation)
    seed = bpy.context.object
    seed.name = name
    seed.scale = scale
    seed.data.materials.append(material)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    for polygon in seed.data.polygons:
        polygon.use_smooth = False
    return seed


def export_joined(definition_id, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    result = bpy.context.object
    result.name = definition_id
    result.data.name = definition_id
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    path = os.path.join(OUTPUT_DIR, definition_id + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        apply_unit_scale=True,
        object_types={"MESH"},
        mesh_smooth_type="FACE",
        use_mesh_modifiers=False,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="STRIP",
        axis_forward="-Z",
        axis_up="Y",
    )
    triangles = sum(len(p.vertices) - 2 for p in result.data.polygons)
    print(
        definition_id,
        "dims=", tuple(round(v, 4) for v in result.dimensions),
        "verts=", len(result.data.vertices),
        "tris=", triangles,
    )
    print("WROTE", path)


def build_herb_seed():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    # The shared icon rig adds roughly two stops to upward faces; 0.42 keeps
    # the pods warm straw-brown without clipping them into a white pebble.
    material = palette_copy("Rope", "PlantSeed_Herb", 0.42, warmth=0.008)
    seeds = [
        add_seed("HerbPod_A", (-0.14, 0.02, 0.00), (0.18, 0.07, 0.07),
                 (0.0, math.radians(12), math.radians(-18)), material),
        add_seed("HerbPod_B", (0.02, 0.035, 0.035), (0.20, 0.075, 0.065),
                 (math.radians(8), math.radians(-4), math.radians(6)), material),
        add_seed("HerbPod_C", (0.16, 0.02, -0.025), (0.17, 0.065, 0.06),
                 (math.radians(-6), math.radians(5), math.radians(20)), material),
    ]
    export_joined("resource.herb_seed", seeds)


def build_yucca_seed():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    material = palette_copy("StoneGrey", "PlantSeed_Yucca", 0.28)
    seeds = []
    for index, (x, z, yaw) in enumerate((
        (-0.13, 0.00, -24), (-0.035, 0.045, -8),
        (0.065, 0.025, 12), (0.155, -0.015, 28),
    )):
        seeds.append(add_seed(
            f"YuccaSeed_{index}", (x, 0.025 + index * 0.003, z),
            (0.13, 0.025, 0.095),
            (0.0, math.radians(yaw), math.radians(yaw * 0.18)), material))
    export_joined("resource.yucca_seed", seeds)


build_herb_seed()
build_yucca_seed()
