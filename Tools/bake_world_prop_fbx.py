"""Bake GLB world props to Unity-native FBX files under Resources.

Run with Blender (not ordinary Python):
  blender --background --python Tools/bake_world_prop_fbx.py -- --repo /path/to/HexLive

The FBX mirrors are intentional Player inputs. Unity's glTF ScriptedImporter
creates valid Editor sub-assets for the source GLBs, but those sub-assets are
not retained as prefab dependencies in a macOS Player build.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys

import bpy
from mathutils import Vector


RESOURCE_GLB_SOURCES = ("item.bandage", "tool.bottle", "tool.machete", "tool.saw")

LEGACY_GLB_WRAPPERS = (
    "bed_basic_final", "bed_leaf_final", "campfire_final", "food.meat_cooked",
    "food.meat_raw", "palm_final", "palm_frond", "resource.hide",
    "resource.palm_leaf", "resource.stone", "rock.boulder", "tool.lighter",
    "water_collector_final",
)


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


STAGED_BILL_KEYS = ("logs", "sticks", "rope", "leaves", "stones")


def _reparent_preserving_world(obj, parent) -> None:
    matrix_world = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_world = matrix_world


def _new_empty(name: str, parent):
    obj = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    return obj


def normalize_campfire_stages() -> None:
    """Restore the logical resource pieces from the former Unity prefab.

    The source GLB groups render meshes by art role (Sticks/Spit/StoneRing),
    while BedAssembly and BuildSiteMath consume 9+2+1 sticks, two rope
    lashings and eighteen individual stones.  The old GLB-backed prefab added
    those logical wrappers by hand; native FBX baking must reproduce them or
    the Player sees the whole campfire from the first delivered stick.
    """
    roots = [obj for obj in bpy.context.scene.objects if obj.parent is None]
    if len(roots) != 1:
        raise RuntimeError(f"campfire source must have one root, got {len(roots)}")
    root = roots[0]
    by_name = {obj.name: obj for obj in bpy.context.scene.objects}
    required_groups = {"Sticks", "Spit", "StoneRing"}
    missing = required_groups - by_name.keys()
    if missing:
        raise RuntimeError(f"campfire source misses groups: {sorted(missing)}")

    stages = {number: _new_empty(str(number), root) for number in range(1, 6)}

    base_sticks = sorted(by_name["Sticks"].children, key=lambda obj: obj.name)
    if len(base_sticks) != 9 or any(not obj.name.startswith("stick_") for obj in base_sticks):
        raise RuntimeError("campfire stage 1 must contain exactly 9 stick_* meshes")
    for obj in base_sticks:
        _reparent_preserving_world(obj, stages[1])

    def compound(name: str, stage: int, children: tuple[str, ...]) -> None:
        wrapper = _new_empty(name, stages[stage])
        for child_name in children:
            child = by_name.get(child_name)
            if child is None:
                raise RuntimeError(f"campfire source misses {child_name}")
            _reparent_preserving_world(child, wrapper)

    compound("stick_post_a", 2, ("spit_post_L", "spit_fork_L"))
    compound("stick_post_b", 2, ("spit_post_R", "spit_fork_R"))
    compound("stick_bar", 3, ("spit_bar",))
    compound("rope_a", 4, tuple(f"rope_L_loop{i}" for i in range(3)))
    compound("rope_b", 4, tuple(f"rope_R_loop{i}" for i in range(3)))

    stones = sorted(by_name["StoneRing"].children, key=lambda obj: obj.name)
    if len(stones) != 18:
        raise RuntimeError(f"campfire stage 5 must contain 18 stones, got {len(stones)}")
    for index, obj in enumerate(stones):
        obj.name = f"stone_{index:02d}"
        _reparent_preserving_world(obj, stages[5])

    for group_name in required_groups:
        group = by_name[group_name]
        if group.children:
            raise RuntimeError(f"campfire normalization left children under {group_name}")
        bpy.data.objects.remove(group, do_unlink=True)


def normalize_for_runtime(entry_id: str) -> None:
    if entry_id == "campfire.spot":
        normalize_campfire_stages()
    elif entry_id == "tool.machete":
        normalize_machete()


def normalize_machete() -> None:
    """Keep only the approved retopologised blade from the authored scene.

    The GLB also contains the source scene's two-metre Cube, Camera and Light.
    Unity's ObjectFit measures every renderer, so that stray cube became the
    machete's bounds: the real 1 m blade was shrunk and offset in the hand.
    The lowpoly mesh is already authored in the canonical tool frame (+Z in
    Blender -> +Y in Unity, grip base at zero), therefore no transform is
    required here; removing the scene furniture preserves its approved pivot.
    """
    approved = bpy.data.objects.get("lowpoly")
    if approved is None or approved.type != "MESH":
        raise RuntimeError("tool.machete source misses approved lowpoly mesh")
    for obj in list(bpy.context.scene.objects):
        if obj != approved:
            bpy.data.objects.remove(obj, do_unlink=True)


def _is_logical_piece(name: str) -> bool:
    return any(name.startswith(prefix) for prefix in
               ("log_", "stick_", "rope_", "leaf_", "stone_"))


def _logical_pieces(root) -> list:
    pieces = []
    for child in root.children:
        if _is_logical_piece(child.name):
            pieces.append(child)
        else:
            pieces.extend(_logical_pieces(child))
    return pieces


def validate_stage_contract(entry: dict, context: str) -> None:
    expected = entry.get("stagedBill")
    if not expected:
        return

    stage_groups = [obj for obj in bpy.context.scene.objects if obj.name.isdigit()]
    search_roots = sorted(stage_groups, key=lambda obj: int(obj.name)) or [
        obj for obj in bpy.context.scene.objects if obj.parent is None
    ]
    pieces = [piece for root in search_roots for piece in _logical_pieces(root)]
    actual = {key: 0 for key in STAGED_BILL_KEYS}
    for piece in pieces:
        name = piece.name
        if name.startswith("log_"): actual["logs"] += 1
        elif name.startswith("stick_"): actual["sticks"] += 1
        elif name.startswith("rope_"): actual["rope"] += 1
        elif name.startswith("leaf_"): actual["leaves"] += 1
        elif name.startswith("stone_"): actual["stones"] += 1
    normalized_expected = {key: int(expected.get(key, 0)) for key in STAGED_BILL_KEYS}
    if actual != normalized_expected:
        raise RuntimeError(
            f"{entry['id']} staged bill mismatch after {context}: "
            f"expected {normalized_expected}, got {actual}")


def bake(entry: dict, source: Path, destination: Path) -> None:
    reset_scene()
    bpy.ops.import_scene.gltf(filepath=str(source))
    normalize_for_runtime(entry["id"])
    source_meshes, source_vertices, source_min, source_max = mesh_stats()
    if source_meshes == 0 or source_vertices == 0:
        raise RuntimeError(f"{source} imported without mesh data")
    validate_stage_contract(entry, "source normalization")

    destination.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=str(destination),
        # Empty stage groups ("1".."5") are part of BedAssembly's runtime
        # contract, so preserve them together with the meshes.
        object_types={"MESH", "EMPTY"},
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
        # Player assets must be self-contained. A glTF source often keeps its
        # albedo only as a packed image; AUTO writes an empty filesystem path
        # into FBX and Unity then imports an untextured material (bug #66).
        path_mode="COPY",
        embed_textures=True,
    )

    # A successful export call is not sufficient: read the FBX back and prove
    # that its mesh payload survived the round-trip before replacing a build input.
    reset_scene()
    bpy.ops.import_scene.fbx(filepath=str(destination))
    baked_meshes, baked_vertices, baked_min, baked_max = mesh_stats()
    if baked_meshes == 0 or baked_vertices == 0:
        raise RuntimeError(f"{destination} round-tripped without mesh data")
    if entry.get("requireTextures"):
        textured_materials = [
            material for material in bpy.data.materials
            if material.node_tree and any(
                node.type == "TEX_IMAGE" and node.image is not None and
                node.image.size[0] > 0 and node.image.size[1] > 0
                for node in material.node_tree.nodes
            )
        ]
        if not textured_materials:
            raise RuntimeError(f"{destination} round-tripped without texture data")
    for source_value, baked_value in zip(source_min + source_max, baked_min + baked_max):
        if abs(source_value - baked_value) > 0.0001:
            raise RuntimeError(
                f"{destination} changed bounds: {source_min}..{source_max} -> "
                f"{baked_min}..{baked_max}"
            )
    validate_stage_contract(entry, "FBX round-trip")

    print(
        f"[world-prop-fbx] {source.name} -> {destination.name}: "
        f"{source_meshes} mesh(es), {source_vertices} source vertices, "
        f"{baked_vertices} baked vertices, bounds {baked_min}..{baked_max}"
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", type=Path, required=True)
    parser.add_argument(
        "--id", action="append", dest="ids", required=True,
        help="Explicit manifest id to rebake; repeat for multiple approved targets.")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else [])

    asset_dir = args.repo.resolve() / "Assets"
    art_source = asset_dir / "ArtSource" / "WorldProps"
    art_source.mkdir(parents=True, exist_ok=True)
    resource_objects = asset_dir / "Resources" / "HexLive" / "Objects"
    for stem in RESOURCE_GLB_SOURCES:
        old = resource_objects / f"{stem}.glb"
        new = art_source / old.name
        if old.exists() and not new.exists():
            old.replace(new)
            old_meta = Path(f"{old}.meta")
            if old_meta.exists():
                old_meta.replace(Path(f"{new}.meta"))

    legacy = asset_dir / "ArtSource" / "LegacyGlbWrappers"
    legacy.mkdir(parents=True, exist_ok=True)
    for stem in LEGACY_GLB_WRAPPERS:
        old = resource_objects / f"{stem}.prefab"
        new = legacy / old.name
        if old.exists() and not new.exists():
            old.replace(new)
            old_meta = Path(f"{old}.meta")
            if old_meta.exists():
                old_meta.replace(Path(f"{new}.meta"))

    destination_dir = (
        args.repo.resolve() / "Assets" / "Resources" / "HexLive" / "Objects"
    )
    manifest = json.loads((args.repo.resolve() / "Tools" / "world_prop_manifest.json").read_text())
    requested = set(args.ids)
    known = {entry["id"] for entry in manifest["entries"] if entry["mode"] == "native"}
    unknown = requested - known
    if unknown:
        raise RuntimeError(f"Unknown or non-bake manifest ids: {sorted(unknown)}")
    for entry in manifest["entries"]:
        if entry["mode"] != "native" or entry["id"] not in requested:
            continue
        bake(entry, asset_dir / entry["source"],
             destination_dir / entry.get("baked", entry["native"]))


if __name__ == "__main__":
    main()
