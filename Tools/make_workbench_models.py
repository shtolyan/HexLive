"""Build the §119 board and workbench from HexLive's native wood primitives.

Run headlessly with the repository's installed Blender:

    /Applications/Blender.app/Contents/MacOS/Blender \
        --factory-startup --background --python Tools/make_workbench_models.py

The source scene is saved beside the authored assets and both runtime models
are exported as native FBX.  The workbench reuses the shipped stick mesh for
all six structural members; only ``resource.board`` is a new primitive.
"""

from __future__ import annotations

import math
from pathlib import Path

import bpy
from mathutils import Vector


PROJECT_ROOT = Path(__file__).resolve().parents[1]
OBJECT_ROOT = PROJECT_ROOT / "Assets/Resources/HexLive/Objects"
SOURCE_ROOT = PROJECT_ROOT / "Assets/ArtSource/Crafting"
STICK_FBX = OBJECT_ROOT / "resource.stick.fbx"
BOARD_FBX = OBJECT_ROOT / "resource.board.fbx"
WORKBENCH_FBX = OBJECT_ROOT / "station.workbench.fbx"
BLEND_SOURCE = SOURCE_ROOT / "hexlive_crafting.blend"
PREVIEW_PATH = SOURCE_ROOT / "workbench_preview.png"

# Spec §119.1.  Keep these numbers in lockstep with CraftStationLayout.
BOARD_LENGTH = 0.975
BOARD_WIDTH = 0.180
BOARD_THICKNESS = 0.055
TABLE_WIDTH = 0.980
TABLE_DEPTH = 0.760
TABLE_HEIGHT = 0.780
OBSTACLE_RADIUS = 0.620
CRAFT_STAND_DISTANCE = 0.750
INGREDIENT_FIT = 0.160
OUTPUT_FIT = 0.240

INGREDIENT_SLOTS = (
    (-0.30, -0.22, TABLE_HEIGHT + 0.025),
    (0.00, -0.22, TABLE_HEIGHT + 0.025),
    (0.30, -0.22, TABLE_HEIGHT + 0.025),
    (-0.30, 0.22, TABLE_HEIGHT + 0.025),
    (0.00, 0.22, TABLE_HEIGHT + 0.025),
    (0.30, 0.22, TABLE_HEIGHT + 0.025),
)


def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    SOURCE_ROOT.mkdir(parents=True, exist_ok=True)
    OBJECT_ROOT.mkdir(parents=True, exist_ok=True)


def material(name: str, color: tuple[float, float, float, float], roughness: float):
    found = bpy.data.materials.get(name)
    if found is not None:
        return found
    made = bpy.data.materials.new(name)
    made.diffuse_color = color
    made.use_nodes = True
    principled = next(
        (node for node in made.node_tree.nodes if node.type == "BSDF_PRINCIPLED"),
        None,
    )
    if principled is not None:
        principled.inputs["Base Color"].default_value = color
        principled.inputs["Roughness"].default_value = roughness
    return made


BOARD_TOP = None
BOARD_SIDE = None
BOARD_END = None
ROPE = None


def create_materials() -> None:
    global BOARD_TOP, BOARD_SIDE, BOARD_END, ROPE
    BOARD_TOP = material("Sapwood", (0.64, 0.39, 0.17, 1.0), 0.86)
    BOARD_SIDE = material("Bark", (0.27, 0.13, 0.052, 1.0), 0.94)
    BOARD_END = material("Heartwood", (0.48, 0.25, 0.095, 1.0), 0.90)
    ROPE = material("Rope", (0.64, 0.53, 0.32, 1.0), 0.98)


def flat(obj: bpy.types.Object) -> None:
    if obj.type != "MESH":
        return
    for polygon in obj.data.polygons:
        polygon.use_smooth = False


def root(name: str, collection: bpy.types.Collection) -> bpy.types.Object:
    made = bpy.data.objects.new(name, None)
    collection.objects.link(made)
    made.empty_display_type = "PLAIN_AXES"
    made.empty_display_size = 0.06
    return made


def marker(
    name: str,
    parent: bpy.types.Object,
    collection: bpy.types.Collection,
    location: tuple[float, float, float],
    role: str,
) -> bpy.types.Object:
    made = bpy.data.objects.new(name, None)
    collection.objects.link(made)
    made.parent = parent
    made.location = location
    made.empty_display_type = "SPHERE"
    made.empty_display_size = 0.025
    made["hexlive_marker"] = role
    return made


def create_board_mesh(name: str) -> bpy.types.Mesh:
    bpy.ops.mesh.primitive_cube_add(size=1.0)
    obj = bpy.context.object
    obj.name = name
    obj.dimensions = (BOARD_LENGTH, BOARD_WIDTH, BOARD_THICKNESS)
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)

    bevel = obj.modifiers.new("HandHewnFacet", "BEVEL")
    bevel.width = 0.012
    bevel.segments = 1
    bevel.affect = "EDGES"
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=bevel.name)

    obj.data.materials.append(BOARD_TOP)
    obj.data.materials.append(BOARD_SIDE)
    obj.data.materials.append(BOARD_END)
    for polygon in obj.data.polygons:
        normal = polygon.normal
        if abs(normal.z) > 0.55:
            polygon.material_index = 0
        elif abs(normal.x) > 0.55:
            polygon.material_index = 2
        else:
            polygon.material_index = 1
        polygon.use_smooth = False

    mesh = obj.data
    mesh.name = "Board_HandHewn_Mesh"
    bpy.data.objects.remove(obj, do_unlink=True)
    return mesh


def instantiate_mesh(
    name: str,
    mesh: bpy.types.Mesh,
    parent: bpy.types.Object,
    collection: bpy.types.Collection,
    location: tuple[float, float, float],
    scale: tuple[float, float, float] = (1.0, 1.0, 1.0),
) -> bpy.types.Object:
    made = bpy.data.objects.new(name, mesh)
    collection.objects.link(made)
    made.parent = parent
    made.location = location
    made.scale = scale
    flat(made)
    return made


def load_stick_mesh() -> bpy.types.Mesh:
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(STICK_FBX), use_anim=False)
    imported = [obj for obj in bpy.data.objects if obj not in before]
    source = next(obj for obj in imported if obj.type == "MESH")
    mesh = source.data.copy()
    mesh.name = "HexLive_StickNative_Mesh"
    for obj in imported:
        bpy.data.objects.remove(obj, do_unlink=True)
    return mesh


def align_between(
    obj: bpy.types.Object,
    start: tuple[float, float, float],
    end: tuple[float, float, float],
) -> None:
    start_v = Vector(start)
    end_v = Vector(end)
    direction = end_v - start_v
    obj.location = (start_v + end_v) * 0.5
    # The shipped stick's long axis is local +X.
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = direction.to_track_quat("X", "Z")
    obj.scale = (direction.length / BOARD_LENGTH, 1.0, 1.0)


def torus(
    name: str,
    parent: bpy.types.Object,
    collection: bpy.types.Collection,
    location: tuple[float, float, float],
    rotation: tuple[float, float, float],
    major_radius: float,
    minor_radius: float = 0.009,
) -> bpy.types.Object:
    bpy.ops.mesh.primitive_torus_add(
        major_radius=major_radius,
        minor_radius=minor_radius,
        major_segments=12,
        minor_segments=4,
        location=location,
        rotation=rotation,
    )
    made = bpy.context.object
    made.name = name
    made.data.name = f"{name}_Mesh"
    made.data.materials.append(ROPE)
    flat(made)
    for owner in list(made.users_collection):
        owner.objects.unlink(made)
    collection.objects.link(made)
    made.parent = parent
    return made


def build_board_asset(board_mesh: bpy.types.Mesh) -> bpy.types.Object:
    collection = bpy.data.collections.new("BOARD_EXPORT")
    bpy.context.scene.collection.children.link(collection)
    board_root = root("resource.board", collection)
    board_root["hexlive_definition_id"] = "resource.board"
    board_root["hexlive_dimensions_wu"] = (
        BOARD_LENGTH,
        BOARD_WIDTH,
        BOARD_THICKNESS,
    )
    instantiate_mesh(
        "Board",
        board_mesh,
        board_root,
        collection,
        (0.0, 0.0, BOARD_THICKNESS * 0.5),
    )
    return board_root


def build_workbench_asset(
    board_mesh: bpy.types.Mesh,
    stick_mesh: bpy.types.Mesh,
) -> bpy.types.Object:
    collection = bpy.data.collections.new("WORKBENCH_EXPORT")
    bpy.context.scene.collection.children.link(collection)
    bench = root("station.workbench", collection)
    bench["hexlive_definition_id"] = "station.workbench"
    bench["hexlive_dimensions_wu"] = (TABLE_WIDTH, TABLE_DEPTH, TABLE_HEIGHT)
    bench["hexlive_obstacle_radius_wu"] = OBSTACLE_RADIUS
    bench["hexlive_bill_boards"] = 4
    bench["hexlive_bill_sticks"] = 6
    bench["hexlive_bill_rope"] = 2

    # Four authored boards form the 0.76-wu top exactly including three gaps.
    gap = (TABLE_DEPTH - 4.0 * BOARD_WIDTH) / 3.0
    first_y = -TABLE_DEPTH * 0.5 + BOARD_WIDTH * 0.5
    for index in range(4):
        y = first_y + index * (BOARD_WIDTH + gap)
        instantiate_mesh(
            f"Stage3_Board_{index + 1}",
            board_mesh,
            bench,
            collection,
            (0.0, y, TABLE_HEIGHT - BOARD_THICKNESS * 0.5),
            (TABLE_WIDTH / BOARD_LENGTH, 1.0, 1.0),
        )

    # Stage 1: four slightly splayed native sticks.
    leg_top_z = TABLE_HEIGHT - BOARD_THICKNESS
    leg_bottom_z = 0.02
    leg_points = (
        (-0.39, -0.285, -0.43, -0.32),
        (0.39, -0.285, 0.43, -0.32),
        (-0.39, 0.285, -0.43, 0.32),
        (0.39, 0.285, 0.43, 0.32),
    )
    for index, (top_x, top_y, bottom_x, bottom_y) in enumerate(leg_points):
        leg = instantiate_mesh(
            f"Stage1_Leg_{index + 1}", stick_mesh, bench, collection, (0.0, 0.0, 0.0)
        )
        align_between(leg, (bottom_x, bottom_y, leg_bottom_z), (top_x, top_y, leg_top_z))

    # Stage 2: two long-side cross braces, again using the native stick mesh.
    brace_specs = (
        ((-0.43, -0.32, 0.29), (0.43, -0.32, 0.29)),
        ((-0.43, 0.32, 0.29), (0.43, 0.32, 0.29)),
    )
    for index, (start, end) in enumerate(brace_specs):
        brace = instantiate_mesh(
            f"Stage2_Brace_{index + 1}", stick_mesh, bench, collection, (0.0, 0.0, 0.0)
        )
        align_between(brace, start, end)

    # Stage 4: one rope resource is represented by a paired visible binding.
    for side, y in (("Front", -0.302), ("Back", 0.302)):
        for x in (-0.39, 0.39):
            torus(
                f"Stage4_Rope{side}_{'L' if x < 0 else 'R'}",
                bench,
                collection,
                (x, y, 0.685),
                (math.pi * 0.5, 0.0, 0.0),
                0.056,
            )

    marker(
        "CraftStand",
        bench,
        collection,
        (0.0, -CRAFT_STAND_DISTANCE, 0.0),
        "craft_stand",
    )
    marker(
        "CraftOutput",
        bench,
        collection,
        (0.0, 0.0, TABLE_HEIGHT + 0.025),
        "craft_output",
    )
    marker(
        "CraftProgressAnchor",
        bench,
        collection,
        (0.0, 0.0, 1.12),
        "craft_progress",
    )
    for index, location in enumerate(INGREDIENT_SLOTS):
        made = marker(
            f"CraftIngredient{index + 1:02d}",
            bench,
            collection,
            location,
            "craft_ingredient",
        )
        made["hexlive_fit_max_wu"] = INGREDIENT_FIT

    bench["hexlive_output_fit_max_wu"] = OUTPUT_FIT
    return bench


def hierarchy(parent: bpy.types.Object) -> list[bpy.types.Object]:
    found = [parent]
    stack = list(parent.children)
    while stack:
        item = stack.pop()
        found.append(item)
        stack.extend(item.children)
    return found


def export_fbx(parent: bpy.types.Object, path: Path) -> None:
    bpy.ops.object.select_all(action="DESELECT")
    for item in hierarchy(parent):
        item.hide_set(False)
        item.select_set(True)
    bpy.context.view_layer.objects.active = parent
    bpy.ops.export_scene.fbx(
        filepath=str(path),
        use_selection=True,
        object_types={"EMPTY", "MESH"},
        use_custom_props=True,
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        add_leaf_bones=False,
        bake_anim=False,
        axis_forward="-Z",
        axis_up="Y",
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_UNITS",
        path_mode="AUTO",
    )


def add_preview(workbench: bpy.types.Object) -> None:
    camera_data = bpy.data.cameras.new("WorkbenchPreviewCamera")
    camera = bpy.data.objects.new("WorkbenchPreviewCamera", camera_data)
    bpy.context.scene.collection.objects.link(camera)
    camera.location = (1.8, -2.2, 1.65)
    camera.rotation_euler = ((Vector((0.0, 0.0, 0.48)) - camera.location).to_track_quat("-Z", "Y").to_euler())
    camera.data.lens = 62
    bpy.context.scene.camera = camera

    for name, location, energy, size in (
        ("WorkbenchKey", (-2.2, -2.5, 3.2), 650.0, 3.0),
        ("WorkbenchFill", (2.4, -0.8, 1.8), 300.0, 2.2),
    ):
        light_data = bpy.data.lights.new(name, "AREA")
        light_data.energy = energy
        light_data.size = size
        light = bpy.data.objects.new(name, light_data)
        bpy.context.scene.collection.objects.link(light)
        light.location = location
        light.rotation_euler = ((Vector((0.0, 0.0, 0.45)) - light.location).to_track_quat("-Z", "Y").to_euler())

    # Workbench renderer is deterministic and does not require a GPU context in
    # background mode (the old local Blender build crashes in headless Eevee).
    bpy.context.scene.render.engine = "BLENDER_WORKBENCH"
    bpy.context.scene.display.shading.light = "STUDIO"
    bpy.context.scene.display.shading.show_shadows = True
    bpy.context.scene.display.shading.show_cavity = True
    bpy.context.scene.display.shading.cavity_type = "WORLD"
    bpy.context.scene.render.resolution_x = 720
    bpy.context.scene.render.resolution_y = 720
    bpy.context.scene.render.resolution_percentage = 100
    bpy.context.scene.render.image_settings.file_format = "PNG"
    bpy.context.scene.render.film_transparent = True
    bpy.context.scene.render.filepath = str(PREVIEW_PATH)
    bpy.ops.render.render(write_still=True)


def main() -> None:
    reset_scene()
    create_materials()
    board_mesh = create_board_mesh("BoardTemplate")
    stick_mesh = load_stick_mesh()
    board = build_board_asset(board_mesh)
    workbench = build_workbench_asset(board_mesh, stick_mesh)
    export_fbx(board, BOARD_FBX)
    export_fbx(workbench, WORKBENCH_FBX)
    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND_SOURCE))
    add_preview(workbench)
    print(
        "HEXLIVE_WORKBENCH "
        f"board={BOARD_FBX} workbench={WORKBENCH_FBX} source={BLEND_SOURCE} "
        f"size={TABLE_WIDTH:.3f}x{TABLE_DEPTH:.3f}x{TABLE_HEIGHT:.3f} "
        f"obstacle={OBSTACLE_RADIUS:.3f} stand={CRAFT_STAND_DISTANCE:.3f}"
    )


if __name__ == "__main__":
    main()
