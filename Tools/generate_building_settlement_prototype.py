#!/usr/bin/env python3
"""Build and render the Blender settlement-placement prototype.

Run inside the already-open HexLive Blender file.  Placement decisions come
from Tools/settlement_placement.py; this file only turns the result into art
and debug geometry.
"""

from __future__ import annotations

import bpy
import json
import math
from mathutils import Matrix, Vector
import os
import random
import sys


REPO_ROOT = "/Volumes/ORICO/HexLive"
OUTPUT_DIR = os.path.join(REPO_ROOT, "Assets/ArtSource/Building")
BLEND_PATH = os.path.join(OUTPUT_DIR, "hexlive_building_kit.blend")
BEAUTY_PATH = os.path.join(OUTPUT_DIR, "hexlive_building_settlement_preview.png")
DEBUG_PATH = os.path.join(OUTPUT_DIR, "hexlive_building_placement_debug.png")
SEED = 7319
MAP_RADIUS = 7
HEX_RADIUS = 1.5
REQUESTS = ("longhouse_2hex", "bend_3hex", "hut_1hex", "longhouse_2hex", "hut_1hex")

if REPO_ROOT not in sys.path:
    sys.path.insert(0, REPO_ROOT)

from Tools.settlement_placement import (  # noqa: E402
    GeneratedMap,
    Placement,
    as_dict,
    distance,
    generate_map,
    place_settlement,
)


def material(name, color, roughness=0.9, emission_strength=0.0):
    found = bpy.data.materials.get(name)
    if found is None:
        found = bpy.data.materials.new(name)
    found.diffuse_color = color
    found.use_nodes = True
    principled = next((node for node in found.node_tree.nodes if node.type == "BSDF_PRINCIPLED"), None)
    if principled:
        principled.inputs["Base Color"].default_value = color
        principled.inputs["Roughness"].default_value = roughness
        emission = principled.inputs.get("Emission Color") or principled.inputs.get("Emission")
        emission_input = principled.inputs.get("Emission Strength")
        if emission and emission_strength > 0.0:
            emission.default_value = color
        if emission_input:
            emission_input.default_value = emission_strength
    return found


def remove_previous():
    generated_collections = [collection for collection in bpy.data.collections if collection.name.startswith("HL_SETTLEMENT_")]
    generated_objects = set()
    for collection in generated_collections:
        generated_objects.update(collection.all_objects)
    for obj in generated_objects:
        bpy.data.objects.remove(obj, do_unlink=True)
    scene = bpy.data.scenes.get("HexBuildingArt_Settlement")
    if scene is not None:
        if bpy.context.window.scene == scene:
            bpy.context.window.scene = bpy.data.scenes.get("HexBuildingArt_Hut") or next(iter(bpy.data.scenes))
        bpy.data.scenes.remove(scene)
    for collection in generated_collections:
        if collection.name in bpy.data.collections:
            bpy.data.collections.remove(collection)
    for obj in list(bpy.data.objects):
        if obj.name.startswith("HL_Settlement_") and len(obj.users_collection) == 0:
            bpy.data.objects.remove(obj, do_unlink=True)


def axial_to_world(cell):
    q, r = cell
    return Vector((math.sqrt(3.0) * HEX_RADIUS * (q + r * 0.5), 1.5 * HEX_RADIUS * r, 0.0))


def cell_name(cell):
    def token(value):
        return f"p{value}" if value >= 0 else f"m{abs(value)}"
    return token(cell[0]) + "_" + token(cell[1])


def move_to_collection(obj, collection):
    for owner in list(obj.users_collection):
        owner.objects.unlink(obj)
    collection.objects.link(obj)


def create_hex_mesh(name, mat):
    radius = 1.43
    z_bottom = -0.16
    z_top = -0.075
    vertices = []
    for z in (z_bottom, z_top):
        for index in range(6):
            angle = math.radians(-90 + index * 60)
            vertices.append((math.cos(angle) * radius, math.sin(angle) * radius, z))
    faces = [tuple(range(5, -1, -1)), tuple(range(6, 12))]
    for index in range(6):
        faces.append((index, (index + 1) % 6, 6 + (index + 1) % 6, 6 + index))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(mat)
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def primitive_cylinder(name, collection, location, radius, depth, mat, vertices=7):
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=depth, location=location)
    obj = bpy.context.object
    obj.name = name
    move_to_collection(obj, collection)
    obj.data.materials.append(mat)
    for polygon in obj.data.polygons:
        polygon.use_smooth = False
    return obj


def primitive_cone(name, collection, location, radius, depth, mat):
    bpy.ops.mesh.primitive_cone_add(vertices=7, radius1=radius, radius2=0.02, depth=depth, location=location)
    obj = bpy.context.object
    obj.name = name
    move_to_collection(obj, collection)
    obj.data.materials.append(mat)
    return obj


def stick_between(name, collection, start, end, scale_radius=0.75):
    source = bpy.data.objects.get("stick_workbench_00")
    if source is None:
        raise RuntimeError("Missing stick_workbench_00")
    start = Vector(start)
    end = Vector(end)
    direction = end - start
    obj = bpy.data.objects.new(name, source.data)
    collection.objects.link(obj)
    obj.location = (start + end) * 0.5
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = direction.to_track_quat("X", "Z")
    obj.scale = (direction.length / 0.975, scale_radius, scale_radius)
    return obj


def add_text(name, body, location, collection, size=0.52):
    curve = bpy.data.curves.new(name + "_Curve", "FONT")
    curve.body = body
    curve.align_x = "CENTER"
    curve.align_y = "CENTER"
    curve.size = size
    curve.extrude = 0.008
    curve.materials.append(LABEL)
    obj = bpy.data.objects.new(name, curve)
    collection.objects.link(obj)
    obj.location = location
    return obj


def add_tree(cell, index, collection):
    rng = random.Random(SEED * 17 + index * 131)
    center = axial_to_world(cell)
    center.x += rng.uniform(-0.30, 0.30)
    center.y += rng.uniform(-0.30, 0.30)
    height = rng.uniform(1.15, 1.72)
    primitive_cylinder(
        f"HL_Settlement_Tree_{index:02d}_Trunk",
        collection,
        (center.x, center.y, height * 0.42),
        rng.uniform(0.10, 0.15),
        height * 0.84,
        TREE_TRUNK,
        7,
    )
    leaf_source = bpy.data.objects.get("Leaf")
    if leaf_source is None:
        raise RuntimeError("Missing Leaf source")
    for frond_index in range(5):
        angle = math.tau * frond_index / 5.0 + rng.uniform(-0.18, 0.18)
        leaf = bpy.data.objects.new(f"HL_Settlement_Tree_{index:02d}_Leaf_{frond_index}", leaf_source.data)
        collection.objects.link(leaf)
        leaf.location = (center.x, center.y, height * 0.86 + rng.uniform(-0.04, 0.08))
        leaf.rotation_euler = (
            math.radians(rng.uniform(-5.0, 5.0)),
            math.radians(rng.uniform(-11.0, 3.0)),
            angle,
        )
        scale = rng.uniform(0.52, 0.72)
        leaf.scale = (scale, scale * rng.uniform(0.92, 1.06), scale)


def add_rock(cell, index, collection):
    rng = random.Random(SEED * 23 + index * 197)
    center = axial_to_world(cell)
    stone_source = bpy.data.objects.get("stone_00")
    if stone_source is None:
        raise RuntimeError("Missing stone_00 source")
    for stone_index in range(rng.randint(2, 4)):
        angle = rng.uniform(0.0, math.tau)
        radius = rng.uniform(0.0, 0.34)
        stone = bpy.data.objects.new(f"HL_Settlement_Rock_{index:02d}_{stone_index}", stone_source.data)
        collection.objects.link(stone)
        stone.location = (
            center.x + math.cos(angle) * radius,
            center.y + math.sin(angle) * radius,
            0.12 + rng.uniform(-0.01, 0.06),
        )
        stone.rotation_euler = (rng.uniform(-0.2, 0.2), rng.uniform(-0.2, 0.2), rng.uniform(0.0, math.tau))
        scale = rng.uniform(2.2, 3.8)
        stone.scale = (scale, scale * rng.uniform(0.75, 1.12), scale * rng.uniform(0.65, 1.05))


def add_campfire(collection):
    center = axial_to_world((0, 0))
    stone_source = bpy.data.objects.get("stone_00")
    for index in range(10):
        angle = math.tau * index / 10.0
        stone = bpy.data.objects.new(f"HL_Settlement_CampStone_{index:02d}", stone_source.data)
        collection.objects.link(stone)
        stone.location = (center.x + math.cos(angle) * 0.40, center.y + math.sin(angle) * 0.40, 0.13)
        stone.rotation_euler[2] = angle
        stone.scale = (1.45, 1.10, 1.15)
    stick_between("HL_Settlement_CampLog_A", collection, (-0.30, -0.18, 0.19), (0.30, 0.18, 0.19), 0.70)
    stick_between("HL_Settlement_CampLog_B", collection, (-0.30, 0.18, 0.21), (0.30, -0.18, 0.21), 0.70)
    primitive_cone("HL_Settlement_FlameOuter", collection, (0.0, 0.0, 0.43), 0.19, 0.48, FLAME_OUTER)
    primitive_cone("HL_Settlement_FlameCore", collection, (0.03, -0.02, 0.39), 0.10, 0.31, FLAME_CORE)
    light_data = bpy.data.lights.new("HL_Settlement_CampLight", "POINT")
    light_data.energy = 180.0
    light_data.color = (1.0, 0.22, 0.035)
    light_data.shadow_soft_size = 1.4
    light = bpy.data.objects.new("HL_Settlement_CampLight", light_data)
    collection.objects.link(light)
    light.location = (0.0, 0.0, 0.62)


def hex_vertices(center):
    return [
        Vector((center.x + math.cos(math.radians(-90 + 60 * index)) * HEX_RADIUS,
                center.y + math.sin(math.radians(-90 + 60 * index)) * HEX_RADIUS,
                0.0))
        for index in range(6)
    ]


def perimeter_nodes(center):
    corners = hex_vertices(center)
    result = []
    for index, corner in enumerate(corners):
        result.append(corner)
        result.append((corner + corners[(index + 1) % 6]) * 0.5)
    return result


def point_key(point):
    return round(float(point.x), 4), round(float(point.y), 4)


def edge_key(start, end):
    return tuple(sorted((point_key(start), point_key(end))))


def analyze_boundary(centers):
    occurrences = {}
    for hex_index, center in enumerate(centers):
        nodes = perimeter_nodes(center)
        for bay_index, start in enumerate(nodes):
            end = nodes[(bay_index + 1) % 12]
            occurrences.setdefault(edge_key(start, end), []).append({
                "hex_index": hex_index,
                "bay_index": bay_index,
                "start": start,
                "end": end,
                "mid": (start + end) * 0.5,
            })
    boundary = [items[0] for items in occurrences.values() if len(items) == 1]
    shared = [items for items in occurrences.values() if len(items) == 2]
    return boundary, shared


def source_segment(bay_index):
    nodes = perimeter_nodes(Vector((0.0, 0.0, 0.0)))
    return nodes[bay_index], nodes[(bay_index + 1) % 12]


def map_segment(source_start, source_end, target_start, target_end):
    source_mid = (source_start + source_end) * 0.5
    target_mid = (target_start + target_end) * 0.5
    source_angle = math.atan2((source_end - source_start).y, (source_end - source_start).x)
    target_angle = math.atan2((target_end - target_start).y, (target_end - target_start).x)
    return Matrix.Translation(target_mid) @ Matrix.Rotation(target_angle - source_angle, 4, "Z") @ Matrix.Translation(-source_mid)


def copy_prefixed(source_collection, prefix, target_collection, transform, name_prefix):
    for source in source_collection.objects:
        if not source.name.startswith(prefix):
            continue
        obj = source.copy()
        obj.data = source.data
        obj.name = f"{name_prefix}_{source.name}"
        obj.hide_render = False
        obj.hide_viewport = False
        obj.hide_set(False)
        target_collection.objects.link(obj)
        obj.matrix_world = transform @ source.matrix_world


def build_house(placement: Placement, collection):
    source_hut = bpy.data.collections.get("HL_BUILDING_HUT_1HEX")
    centers = [axial_to_world(cell) for cell in placement.footprint]
    boundary, shared = analyze_boundary(centers)
    prefix = f"HL_Settlement_B{placement.building_id:02d}"
    rng = random.Random(SEED + placement.building_id * 1009)

    for hex_index, center in enumerate(centers):
        transform = Matrix.Translation(center)
        copy_prefixed(source_hut, "HL_Hex_R1.5_Exact", collection, transform, f"{prefix}_H{hex_index}")
        copy_prefixed(source_hut, "HL_Floor_", collection, transform, f"{prefix}_H{hex_index}")
        copy_prefixed(source_hut, "HL_Roof_", collection, transform, f"{prefix}_H{hex_index}")

    camp = axial_to_world((0, 0))
    door_index = min(range(len(boundary)), key=lambda index: (boundary[index]["mid"] - camp).length)
    window_candidates = [index for index in range(len(boundary)) if index != door_index]
    rng.shuffle(window_candidates)
    window_indices = set(window_candidates[:max(2, len(centers) + 1)])
    kept_nodes = {}

    for boundary_index, entry in enumerate(boundary):
        start = entry["start"]
        end = entry["end"]
        if boundary_index == door_index:
            kind, source_bay, source_prefix = "Door", 0, "HL_Bay_00_Door"
        elif boundary_index in window_indices:
            kind, source_bay, source_prefix = "Window", 3, "HL_Bay_03_Window"
        else:
            kind, source_bay, source_prefix = "Solid", 2, "HL_Bay_02_Solid"
        source_start, source_end = source_segment(source_bay)
        transform = map_segment(source_start, source_end, start, end)
        copy_prefixed(source_hut, source_prefix, collection, transform, f"{prefix}_B{boundary_index:02d}_{kind}")
        kept_nodes[point_key(start)] = start
        kept_nodes[point_key(end)] = end

    source_post_point = perimeter_nodes(Vector((0.0, 0.0, 0.0)))[0]
    for post_index, point in enumerate(kept_nodes.values()):
        transform = Matrix.Translation(point) @ Matrix.Rotation(rng.uniform(-0.035, 0.035), 4, "Z") @ Matrix.Translation(-source_post_point)
        copy_prefixed(source_hut, "HL_Post_00_", collection, transform, f"{prefix}_P{post_index:02d}")

    root = bpy.data.objects.new(prefix + "_Root", None)
    collection.objects.link(root)
    root["hexlive_definition_id"] = placement.definition_id
    root["hexlive_building_id"] = placement.building_id
    root["hexlive_anchor_qr"] = placement.anchor
    root["hexlive_rotation"] = placement.rotation
    root["hexlive_footprint"] = json.dumps(placement.footprint)
    root["hexlive_door_cell"] = placement.door_cell
    root["hexlive_door_outside"] = placement.door_outside
    root["hexlive_path_length"] = len(placement.path_to_campfire)
    root["hexlive_placement_score"] = placement.score
    root["hexlive_shared_sides"] = len(shared) // 2


def add_polyline(name, cells, collection, mat, z=0.10, bevel=0.055):
    points = [axial_to_world(cell) + Vector((0.0, 0.0, z)) for cell in cells]
    if len(points) < 2:
        return
    curve = bpy.data.curves.new(name + "_Curve", "CURVE")
    curve.dimensions = "3D"
    curve.resolution_u = 1
    curve.bevel_depth = bevel
    curve.bevel_resolution = 1
    curve.materials.append(mat)
    spline = curve.splines.new("POLY")
    spline.points.add(len(points) - 1)
    for target, point in zip(spline.points, points):
        target.co = (*point, 1.0)
    obj = bpy.data.objects.new(name, curve)
    collection.objects.link(obj)


def add_hex_outline(name, cell, collection, mat, z=0.11):
    center = axial_to_world(cell)
    points = hex_vertices(center)
    curve = bpy.data.curves.new(name + "_Curve", "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = 0.035
    curve.bevel_resolution = 0
    curve.materials.append(mat)
    spline = curve.splines.new("POLY")
    spline.points.add(6)
    for target, point in zip(spline.points, points + [points[0]]):
        target.co = (point.x, point.y, z, 1.0)
    obj = bpy.data.objects.new(name, curve)
    collection.objects.link(obj)


def add_worn_path(placement, collection):
    for path_index, cell in enumerate(placement.path_to_campfire[:-1]):
        center = axial_to_world(cell)
        rng = random.Random(SEED + placement.building_id * 911 + path_index * 31)
        primitive_cylinder(
            f"HL_Settlement_B{placement.building_id:02d}_PathPatch_{path_index}",
            collection,
            (center.x + rng.uniform(-0.18, 0.18), center.y + rng.uniform(-0.18, 0.18), -0.055),
            rng.uniform(0.24, 0.36),
            0.028,
            PATH_GROUND,
            7,
        )


def area_light(name, collection, location, energy, size, color, target=(0.0, 0.0, 0.0)):
    data = bpy.data.lights.new(name, "AREA")
    data.energy = energy
    data.size = size
    data.color = color
    obj = bpy.data.objects.new(name, data)
    collection.objects.link(obj)
    obj.location = location
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()
    return obj


remove_previous()
generated_map: GeneratedMap = generate_map(SEED, MAP_RADIUS)
placements: list[Placement] = place_settlement(generated_map, REQUESTS, SEED)

scene = bpy.data.scenes.new("HexBuildingArt_Settlement")
bpy.context.window.scene = scene
scene.unit_settings.system = "METRIC"
scene.unit_settings.scale_length = 1.0
root = bpy.data.collections.new("HL_SETTLEMENT_ROOT")
terrain_collection = bpy.data.collections.new("HL_SETTLEMENT_TERRAIN")
nature_collection = bpy.data.collections.new("HL_SETTLEMENT_NATURE")
camp_collection = bpy.data.collections.new("HL_SETTLEMENT_CAMPFIRE")
building_collection = bpy.data.collections.new("HL_SETTLEMENT_BUILDINGS")
path_collection = bpy.data.collections.new("HL_SETTLEMENT_PATHS")
debug_collection = bpy.data.collections.new("HL_SETTLEMENT_DEBUG")
presentation = bpy.data.collections.new("HL_SETTLEMENT_PRESENTATION")
scene.collection.children.link(root)
for collection in (terrain_collection, nature_collection, camp_collection, building_collection, path_collection, debug_collection, presentation):
    root.children.link(collection)

GROUND = material("HL_Settlement_Ground", (0.19, 0.27, 0.14, 1.0), 1.0)
GROUND_ALT = material("HL_Settlement_GroundAlt", (0.23, 0.31, 0.16, 1.0), 1.0)
WATER = material("HL_Settlement_Water", (0.045, 0.23, 0.31, 1.0), 0.48)
TREE_TRUNK = material("HL_Settlement_TreeTrunk", (0.27, 0.13, 0.052, 1.0), 0.98)
PATH_GROUND = material("HL_Settlement_PathGround", (0.39, 0.28, 0.13, 1.0), 1.0)
DEBUG_PATH_MAT = material("HL_Settlement_DebugPath", (1.0, 0.51, 0.06, 1.0), 0.65, 1.0)
DEBUG_FOOTPRINT = material("HL_Settlement_DebugFootprint", (0.04, 0.77, 0.86, 1.0), 0.62, 0.7)
DEBUG_DOOR = material("HL_Settlement_DebugDoor", (1.0, 0.16, 0.025, 1.0), 0.58, 1.2)
FLAME_OUTER = material("HL_Settlement_FlameOuter", (1.0, 0.10, 0.005, 1.0), 0.4, 5.0)
FLAME_CORE = material("HL_Settlement_FlameCore", (1.0, 0.78, 0.08, 1.0), 0.35, 8.0)
LABEL = material("HL_Settlement_Label", (0.94, 0.79, 0.42, 1.0), 0.82)
STUDIO_GROUND = material("HL_Settlement_StudioGround", (0.045, 0.065, 0.048, 1.0), 1.0)

tile_meshes = {
    "ground_a": create_hex_mesh("HL_Settlement_GroundTileMeshA", GROUND),
    "ground_b": create_hex_mesh("HL_Settlement_GroundTileMeshB", GROUND_ALT),
    "water": create_hex_mesh("HL_Settlement_WaterTileMesh", WATER),
}
for cell, terrain in sorted(generated_map.terrain.items()):
    if terrain == "water":
        mesh = tile_meshes["water"]
    else:
        mesh = tile_meshes["ground_a" if (cell[0] - cell[1] + SEED) % 3 else "ground_b"]
    tile = bpy.data.objects.new(f"HL_Settlement_Tile_{cell_name(cell)}", mesh)
    terrain_collection.objects.link(tile)
    tile.location = axial_to_world(cell)
    tile["hexlive_axial_q"] = cell[0]
    tile["hexlive_axial_r"] = cell[1]
    tile["hexlive_terrain"] = terrain

for prop_index, (cell, kind) in enumerate(sorted(generated_map.props.items())):
    if kind == "tree":
        add_tree(cell, prop_index, nature_collection)
    else:
        add_rock(cell, prop_index, nature_collection)

add_campfire(camp_collection)
for placement in placements:
    add_worn_path(placement, path_collection)
    build_house(placement, building_collection)
    add_polyline(
        f"HL_Settlement_B{placement.building_id:02d}_DebugPath",
        placement.path_to_campfire,
        debug_collection,
        DEBUG_PATH_MAT,
    )
    for cell in placement.footprint:
        add_hex_outline(
            f"HL_Settlement_B{placement.building_id:02d}_Footprint_{cell_name(cell)}",
            cell,
            debug_collection,
            DEBUG_FOOTPRINT,
        )
    door_world = axial_to_world(placement.door_outside)
    primitive_cylinder(
        f"HL_Settlement_B{placement.building_id:02d}_DoorMarker",
        debug_collection,
        (door_world.x, door_world.y, 0.12),
        0.16,
        0.10,
        DEBUG_DOOR,
        8,
    )
    footprint_world = [axial_to_world(cell) for cell in placement.footprint]
    centroid = sum(footprint_world, Vector()) / len(footprint_world)
    add_text(
        f"HL_Settlement_B{placement.building_id:02d}_DebugLabel",
        f"B{placement.building_id} · {len(placement.footprint)} HEX",
        (centroid.x, centroid.y, 2.42),
        debug_collection,
        0.31,
    )

bpy.ops.mesh.primitive_plane_add(size=52.0, location=(0.0, 0.0, -0.24))
ground_plane = bpy.context.object
ground_plane.name = "HL_Settlement_StudioGround"
ground_plane.data.materials.append(STUDIO_GROUND)
move_to_collection(ground_plane, presentation)

add_text("HL_Settlement_Title", "SETTLEMENT PLACEMENT · SEED 7319", (0.0, -18.1, 0.02), presentation, 0.72)
add_text("HL_Settlement_Subtitle", "5 HOUSES · FOOTPRINT CLEAR · DOOR PATH TO FIRE", (0.0, -19.0, 0.02), presentation, 0.34)

beauty_data = bpy.data.cameras.new("HL_Settlement_BeautyCamera")
beauty_data.type = "ORTHO"
beauty_data.ortho_scale = 36.0
beauty = bpy.data.objects.new("HL_Settlement_BeautyCamera", beauty_data)
presentation.objects.link(beauty)
beauty.location = (25.0, -31.0, 27.0)
beauty.rotation_euler = (Vector((0.0, 0.0, 0.55)) - beauty.location).to_track_quat("-Z", "Y").to_euler()

debug_data = bpy.data.cameras.new("HL_Settlement_DebugCamera")
debug_data.type = "ORTHO"
debug_data.ortho_scale = 39.0
debug_camera = bpy.data.objects.new("HL_Settlement_DebugCamera", debug_data)
presentation.objects.link(debug_camera)
debug_camera.location = (0.0, 0.0, 44.0)
debug_camera.rotation_euler = (Vector((0.0, 0.0, 0.0)) - debug_camera.location).to_track_quat("-Z", "Y").to_euler()

area_light("HL_Settlement_Key", presentation, (-14.0, -18.0, 28.0), 3400, 12.0, (1.0, 0.76, 0.50))
area_light("HL_Settlement_Fill", presentation, (18.0, -3.0, 22.0), 2700, 11.0, (0.50, 0.70, 1.0))
area_light("HL_Settlement_Rim", presentation, (0.0, 22.0, 24.0), 2850, 10.0, (0.72, 0.94, 0.62))

world = bpy.data.worlds.new("HL_Settlement_World")
world.use_nodes = True
world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.018, 0.026, 0.020, 1.0)
world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.48
scene.world = world
scene.render.engine = "BLENDER_EEVEE"
scene.eevee.use_gtao = True
scene.eevee.gtao_distance = 5
scene.eevee.gtao_factor = 1.45
scene.eevee.use_soft_shadows = True
scene.render.resolution_percentage = 100
scene.render.image_settings.file_format = "PNG"
scene.view_settings.view_transform = "Filmic"
try:
    scene.view_settings.look = "Medium High Contrast"
except Exception:
    pass

debug_collection.hide_render = True
scene.camera = beauty
scene.render.resolution_x = 2000
scene.render.resolution_y = 1250
scene.render.filepath = BEAUTY_PATH
bpy.ops.render.render(write_still=True)

debug_collection.hide_render = False
roof_objects = [obj for obj in building_collection.objects if "_HL_Roof_" in obj.name]
for obj in roof_objects:
    obj.hide_render = True
scene.camera = debug_camera
scene.render.resolution_x = 1700
scene.render.resolution_y = 1700
scene.render.filepath = DEBUG_PATH
bpy.ops.render.render(write_still=True)
for obj in roof_objects:
    obj.hide_render = False

scene.camera = beauty
debug_collection.hide_render = False
scene["hexlive_seed"] = SEED
scene["hexlive_map_radius"] = MAP_RADIUS
scene["hexlive_algorithm"] = "rings + footprint occupancy + entrance BFS + spread score"
scene["hexlive_placement_json"] = json.dumps(as_dict(generated_map, placements), separators=(",", ":"))

previous_save_versions = bpy.context.preferences.filepaths.save_version
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)
bpy.context.preferences.filepaths.save_version = previous_save_versions

print("SETTLEMENT_PLACED", json.dumps({
    "seed": SEED,
    "houses": len(placements),
    "footprints": [placement.footprint for placement in placements],
    "paths": [len(placement.path_to_campfire) for placement in placements],
}, ensure_ascii=False))
print("RENDERED", BEAUTY_PATH)
print("RENDERED", DEBUG_PATH)
print("SAVED", BLEND_PATH)
