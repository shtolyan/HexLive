"""Bake GLB world props to Unity-native FBX files under Resources.

Run with Blender (not ordinary Python):
  blender --background --python Tools/bake_world_prop_fbx.py -- --repo /path/to/HexLive

The FBX mirrors are intentional Player inputs. Unity's glTF ScriptedImporter
creates valid Editor sub-assets for the source GLBs, but those sub-assets are
not retained as prefab dependencies in a macOS Player build.
"""

from __future__ import annotations

import argparse
from pathlib import Path
import sys

import bpy
from mathutils import Vector


PROP_FILES = {
    "palm_final.glb": "palm_final_native.fbx",
    "leaf_final.glb": "palm_frond_native.fbx",
    "rock_boulder.glb": "rock_boulder_native.fbx",
    "stone_single.glb": "stone_single_native.fbx",
}


def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)


def mesh_stats() -> tuple[int, int, tuple[float, float, float], tuple[float, float, float]]:
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    points = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
    if not points:
        return 0, 0, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0)
    minimum = tuple(min(point[axis] for point in points) for axis in range(3))
    maximum = tuple(max(point[axis] for point in points) for axis in range(3))
    return len(meshes), sum(len(obj.data.vertices) for obj in meshes), minimum, maximum


def bake(source: Path, destination: Path) -> None:
    reset_scene()
    bpy.ops.import_scene.gltf(filepath=str(source))
    source_meshes, source_vertices, source_min, source_max = mesh_stats()
    if source_meshes == 0 or source_vertices == 0:
        raise RuntimeError(f"{source} imported without mesh data")

    destination.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(destination),
        object_types={"MESH"},
        use_selection=False,
        global_scale=1.0,
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z",
        axis_up="Y",
        use_mesh_modifiers=True,
        use_triangles=True,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="AUTO",
        embed_textures=False,
    )

    # A successful export call is not sufficient: read the FBX back and prove
    # that its mesh payload survived the round-trip before replacing a build input.
    reset_scene()
    bpy.ops.import_scene.fbx(filepath=str(destination))
    baked_meshes, baked_vertices, baked_min, baked_max = mesh_stats()
    if baked_meshes == 0 or baked_vertices == 0:
        raise RuntimeError(f"{destination} round-tripped without mesh data")
    for source_value, baked_value in zip(source_min + source_max, baked_min + baked_max):
        if abs(source_value - baked_value) > 0.0001:
            raise RuntimeError(
                f"{destination} changed bounds: {source_min}..{source_max} -> "
                f"{baked_min}..{baked_max}"
            )

    print(
        f"[world-prop-fbx] {source.name} -> {destination.name}: "
        f"{source_meshes} mesh(es), {source_vertices} source vertices, "
        f"{baked_vertices} baked vertices, bounds {baked_min}..{baked_max}"
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else [])

    source_dir = args.repo.resolve() / "Assets" / "_AiGen"
    destination_dir = (
        args.repo.resolve() / "Assets" / "Resources" / "HexLive" / "Objects"
    )
    for source_name, destination_name in PROP_FILES.items():
        bake(source_dir / source_name, destination_dir / destination_name)


if __name__ == "__main__":
    main()
