#!/usr/bin/env python3
"""Build a dense four-layer palm roof and hand-randomize its top leaves.

Run this script inside the already-open HexLive Blender file through the
Blender MCP bridge.  Every run starts from the authored baseline matrices, so
the operation is deterministic and does not accumulate transform drift.
"""

from __future__ import annotations

import math
import os
import random

import bpy
from mathutils import Matrix


REPO_ROOT = "/Volumes/ORICO/HexLive"
OUTPUT_DIR = os.path.join(REPO_ROOT, "Assets/ArtSource/Building")
BLEND_PATH = os.path.join(OUTPUT_DIR, "hexlive_building_kit.blend")
HUT_PREVIEW_PATH = os.path.join(OUTPUT_DIR, "hexlive_building_hut_preview.png")
SOURCE_COLLECTION = "HL_BUILDING_HUT_1HEX"
HUT_SCENE = "HexBuildingArt_Hut"
FLOWER_ROTATIONS_DEGREES = {1: 0.0, 2: 30.0, 3: 15.0, 4: 45.0}
UNDERLAYER_PREFIX = "HL_Roof_palm_leaf_under_"
WOVEN_MAT_PREFIX = "HL_Roof_woven_mat_"
RANDOMIZATION_VERSION = 3


def baseline_matrix(obj: bpy.types.Object) -> Matrix:
    flat = obj.get("hexlive_roof_baseline_matrix")
    if flat is None or len(flat) != 16:
        raise RuntimeError(f"{obj.name}: missing 4x4 hexlive_roof_baseline_matrix")
    return Matrix([flat[row * 4 : row * 4 + 4] for row in range(4)])


def stable_seed(obj: bpy.types.Object, layer: int) -> int:
    authored_seed = int(obj.get("hexlive_roof_jitter_seed", 0))
    name_seed = sum((index + 1) * ord(character) for index, character in enumerate(obj.name))
    return 81017 + authored_seed * 37 + name_seed + layer * 1009


def rebuild_dense_underlayers(source: bpy.types.Collection) -> None:
    for obj in list(source.objects):
        if obj.name.startswith(UNDERLAYER_PREFIX) or obj.get("hexlive_roof_layer") in (3, 4):
            bpy.data.objects.remove(obj, do_unlink=True)

    for layer in (3, 4):
        for patch in range(6):
            for half in range(2):
                authored = source.objects.get(f"HL_Roof_palm_leaf_{patch}_{half}")
                if authored is None:
                    raise RuntimeError(f"Missing authored leaf for patch={patch}, half={half}")
                obj = authored.copy()
                obj.data = authored.data
                obj.name = f"{UNDERLAYER_PREFIX}{layer}_{patch}_{half}"
                obj.hide_render = False
                obj.hide_viewport = False
                obj.hide_set(False)
                source.objects.link(obj)
                obj["hexlive_roof_layer"] = layer
                obj["hexlive_roof_patch"] = patch
                obj["hexlive_roof_half"] = half
                obj["hexlive_roof_jitter_seed"] = int(authored.get("hexlive_roof_jitter_seed", 0)) + layer * 211
                obj["hexlive_roof_visual_role"] = "dense_underlayer"
                obj["hexlive_resource_visual_fraction"] = 0.5


def woven_mat_materials() -> list[bpy.types.Material]:
    colors = (
        (0.15, 0.31, 0.105, 1.0),
        (0.19, 0.38, 0.125, 1.0),
        (0.23, 0.43, 0.145, 1.0),
    )
    result = []
    for index, color in enumerate(colors):
        name = f"HL_Roof_PalmMat_{index}"
        mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
        mat.diffuse_color = color
        mat.use_nodes = True
        principled = next(
            (node for node in mat.node_tree.nodes if node.type == "BSDF_PRINCIPLED"),
            None,
        )
        if principled is not None:
            principled.inputs["Base Color"].default_value = color
            principled.inputs["Roughness"].default_value = 0.96
        result.append(mat)
    return result


def rebuild_woven_underlay(source: bpy.types.Collection) -> None:
    for obj in list(source.objects):
        if obj.name.startswith(WOVEN_MAT_PREFIX):
            mesh = obj.data
            bpy.data.objects.remove(obj, do_unlink=True)
            if mesh is not None and mesh.users == 0:
                bpy.data.meshes.remove(mesh)

    materials = woven_mat_materials()
    radius = 1.62
    apex_z = 2.675
    eave_z = 2.070
    row_t = 0.54
    for patch in range(6):
        left_angle = math.radians(-90.0 + patch * 60.0)
        right_angle = math.radians(-30.0 + patch * 60.0)
        left = (math.cos(left_angle) * radius, math.sin(left_angle) * radius, eave_z)
        right = (math.cos(right_angle) * radius, math.sin(right_angle) * radius, eave_z)
        apex = (0.0, 0.0, apex_z)
        inner_left = tuple(apex[axis] + (left[axis] - apex[axis]) * row_t for axis in range(3))
        inner_right = tuple(apex[axis] + (right[axis] - apex[axis]) * row_t for axis in range(3))
        outer_middle = tuple((left[axis] + right[axis]) * 0.5 for axis in range(3))
        vertices = (apex, inner_left, inner_right, left, outer_middle, right)
        faces = ((0, 1, 2), (1, 3, 4, 2), (2, 4, 5))
        mesh = bpy.data.meshes.new(f"{WOVEN_MAT_PREFIX}{patch}_Mesh")
        mesh.from_pydata(vertices, [], faces)
        for mat in materials:
            mesh.materials.append(mat)
        for face_index, polygon in enumerate(mesh.polygons):
            polygon.use_smooth = False
            polygon.material_index = (patch + face_index) % len(materials)
        obj = bpy.data.objects.new(f"{WOVEN_MAT_PREFIX}{patch}", mesh)
        source.objects.link(obj)
        obj["hexlive_roof_patch"] = patch
        obj["hexlive_roof_visual_role"] = "woven_underlay"
        obj["hexlive_resource_reference"] = "resource.palm_leaf"
        obj["hexlive_resource_visual_fraction"] = 0.0


def randomize_leaf(obj: bpy.types.Object, layer: int) -> None:
    rng = random.Random(stable_seed(obj, layer))
    baseline = baseline_matrix(obj)

    flower_rotation = FLOWER_ROTATIONS_DEGREES[layer]
    is_underlayer = layer in (3, 4)
    radial_jitter = rng.uniform(-3.0, 3.0) if is_underlayer else rng.uniform(-5.5, 5.5)
    if layer == 1:
        xy_jitter, z_bias, z_jitter = 0.052, 0.0, 0.032
    elif layer == 2:
        xy_jitter, z_bias, z_jitter = 0.065, 0.038, 0.040
    elif layer == 3:
        xy_jitter, z_bias, z_jitter = 0.032, -0.055, 0.018
    else:
        xy_jitter, z_bias, z_jitter = 0.036, -0.028, 0.018

    translation = Matrix.Translation((
        rng.uniform(-xy_jitter, xy_jitter),
        rng.uniform(-xy_jitter, xy_jitter),
        z_bias + rng.uniform(-z_jitter, z_jitter),
    ))
    flower_turn = Matrix.Rotation(math.radians(flower_rotation + radial_jitter), 4, "Z")
    tilt_degrees = 2.4 if is_underlayer else 4.8
    twist_degrees = 2.2 if is_underlayer else 3.8
    local_tilt = (
        Matrix.Rotation(math.radians(rng.uniform(-tilt_degrees, tilt_degrees)), 4, "X")
        @ Matrix.Rotation(math.radians(rng.uniform(-tilt_degrees, tilt_degrees)), 4, "Y")
        @ Matrix.Rotation(math.radians(rng.uniform(-twist_degrees, twist_degrees)), 4, "Z")
    )
    scale_range = (1.055, 1.11) if is_underlayer else (0.94, 1.06)
    scale = Matrix.Scale(rng.uniform(*scale_range), 4)

    obj.matrix_world = translation @ flower_turn @ baseline @ local_tilt @ scale
    obj["hexlive_roof_flower_rotation_degrees"] = flower_rotation
    obj["hexlive_roof_radial_jitter_degrees"] = radial_jitter
    obj["hexlive_roof_randomization_version"] = RANDOMIZATION_VERSION


def main() -> None:
    source = bpy.data.collections.get(SOURCE_COLLECTION)
    if source is None:
        raise RuntimeError(f"Missing source collection: {SOURCE_COLLECTION}")

    rebuild_dense_underlayers(source)
    rebuild_woven_underlay(source)
    leaves = [
        obj
        for obj in source.objects
        if obj.get("hexlive_resource_reference") == "resource.palm_leaf"
        and obj.get("hexlive_roof_layer") in FLOWER_ROTATIONS_DEGREES
    ]
    layer_counts = {layer: 0 for layer in FLOWER_ROTATIONS_DEGREES}
    for obj in leaves:
        layer = int(obj["hexlive_roof_layer"])
        randomize_leaf(obj, layer)
        layer_counts[layer] += 1

    if layer_counts != {1: 12, 2: 12, 3: 12, 4: 12}:
        raise RuntimeError(f"Expected 12 leaves per layer, got {layer_counts}")

    scene = bpy.data.scenes.get(HUT_SCENE)
    if scene is None:
        raise RuntimeError(f"Missing preview scene: {HUT_SCENE}")
    bpy.context.window.scene = scene
    scene.render.filepath = HUT_PREVIEW_PATH
    bpy.ops.render.render(write_still=True)

    previous_save_versions = bpy.context.preferences.filepaths.save_version
    bpy.context.preferences.filepaths.save_version = 0
    try:
        bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)
    finally:
        bpy.context.preferences.filepaths.save_version = previous_save_versions

    print("ROOF_LAYER_COUNTS", layer_counts)
    print("FLOWER_ROTATIONS_DEGREES", FLOWER_ROTATIONS_DEGREES)
    print("RENDERED", HUT_PREVIEW_PATH)
    print("SAVED", BLEND_PATH)


main()
