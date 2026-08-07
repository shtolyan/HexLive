#!/usr/bin/env python3
"""Offset and hand-randomize the two palm-leaf roof layers.

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
SECOND_FLOWER_ROTATION_DEGREES = 30.0
RANDOMIZATION_VERSION = 2


def baseline_matrix(obj: bpy.types.Object) -> Matrix:
    flat = obj.get("hexlive_roof_baseline_matrix")
    if flat is None or len(flat) != 16:
        raise RuntimeError(f"{obj.name}: missing 4x4 hexlive_roof_baseline_matrix")
    return Matrix([flat[row * 4 : row * 4 + 4] for row in range(4)])


def stable_seed(obj: bpy.types.Object, layer: int) -> int:
    authored_seed = int(obj.get("hexlive_roof_jitter_seed", 0))
    name_seed = sum((index + 1) * ord(character) for index, character in enumerate(obj.name))
    return 81017 + authored_seed * 37 + name_seed + layer * 1009


def randomize_leaf(obj: bpy.types.Object, layer: int) -> None:
    rng = random.Random(stable_seed(obj, layer))
    baseline = baseline_matrix(obj)

    flower_rotation = SECOND_FLOWER_ROTATION_DEGREES if layer == 2 else 0.0
    radial_jitter = rng.uniform(-5.5, 5.5)
    xy_jitter = 0.065 if layer == 2 else 0.052
    z_bias = 0.038 if layer == 2 else 0.0
    z_jitter = 0.040 if layer == 2 else 0.032

    translation = Matrix.Translation((
        rng.uniform(-xy_jitter, xy_jitter),
        rng.uniform(-xy_jitter, xy_jitter),
        z_bias + rng.uniform(-z_jitter, z_jitter),
    ))
    flower_turn = Matrix.Rotation(math.radians(flower_rotation + radial_jitter), 4, "Z")
    local_tilt = (
        Matrix.Rotation(math.radians(rng.uniform(-4.8, 4.8)), 4, "X")
        @ Matrix.Rotation(math.radians(rng.uniform(-4.8, 4.8)), 4, "Y")
        @ Matrix.Rotation(math.radians(rng.uniform(-3.8, 3.8)), 4, "Z")
    )
    scale = Matrix.Scale(rng.uniform(0.94, 1.06), 4)

    obj.matrix_world = translation @ flower_turn @ baseline @ local_tilt @ scale
    obj["hexlive_roof_flower_rotation_degrees"] = flower_rotation
    obj["hexlive_roof_radial_jitter_degrees"] = radial_jitter
    obj["hexlive_roof_randomization_version"] = RANDOMIZATION_VERSION


def main() -> None:
    source = bpy.data.collections.get(SOURCE_COLLECTION)
    if source is None:
        raise RuntimeError(f"Missing source collection: {SOURCE_COLLECTION}")

    leaves = [
        obj
        for obj in source.objects
        if obj.get("hexlive_resource_reference") == "resource.palm_leaf"
        and obj.get("hexlive_roof_layer") in (1, 2)
    ]
    layer_counts = {1: 0, 2: 0}
    for obj in leaves:
        layer = int(obj["hexlive_roof_layer"])
        randomize_leaf(obj, layer)
        layer_counts[layer] += 1

    if layer_counts != {1: 12, 2: 12}:
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
    print("SECOND_FLOWER_ROTATION_DEGREES", SECOND_FLOWER_ROTATION_DEGREES)
    print("RENDERED", HUT_PREVIEW_PATH)
    print("SAVED", BLEND_PATH)


main()
