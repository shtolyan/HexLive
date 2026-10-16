"""One-time migration of shipped construction artwork to an editable stage library.

Run in background Blender with --factory-startup. Creates a new .blend only;
never overwrites a source or exports runtime FBX. Re-running after artist edits
is deliberately refused. Each asset has a scene at its real origin, plus an
overview of collection instances. Stage and unit transforms are normalized
without moving a single visible vertex.
"""
import importlib.util
import json
import math
from pathlib import Path

import bmesh
import bpy
from mathutils import Matrix, Vector, Quaternion

REPO = Path(__file__).resolve().parents[2]
MANIFEST = REPO / "Tools/construction_stage_manifest.json"
OUTPUT = REPO / "_ArtSource/Construction/hexlive_construction_stages.blend"
TOKENS = {"resource.log": "log", "resource.stick": "stick", "resource.board": "board",
          "resource.rope": "rope", "resource.palm_leaf": "palm_leaf", "resource.stone": "stone"}
LABELS = {
    "architecture.wall.wood": "Стена", "architecture.window.wood": "Окно",
    "architecture.door.wood": "Дверь", "architecture.support.wood": "Опора",
    "architecture.floor.board": "Пол", "architecture.roof.palm": "Крыша",
    "architecture.roof.palm.flat": "Крыша плоская", "furniture.hearth": "Домашний очаг",
    "bed.basic": "Кровать", "campfire.spot": "Уличный костёр",
    "station.drying_rack": "Сушилка", "station.water_collector": "Сборщик воды",
    "station.workbench": "Верстак", "furniture.wardrobe": "Гардероб",
}


def empty(name, parent, collection):
    obj = bpy.data.objects.new(name, None)
    collection.objects.link(obj)
    obj.parent = parent
    obj.empty_display_size = .07
    return obj


def parent_keep_world(obj, parent):
    bpy.context.view_layer.update()
    world = obj.matrix_world.copy()
    obj.parent = parent
    obj.matrix_parent_inverse = Matrix.Identity(4)
    obj.matrix_world = world


def bounds(objects):
    points = [o.matrix_world @ v.co for o in objects if o.type == "MESH"
              for v in o.data.vertices]
    return ([min(p[i] for p in points) for i in range(3)],
            [max(p[i] for p in points) for i in range(3)])


def stage_index(obj):
    if obj.name.isdigit():
        return int(obj.name)
    if obj.name.startswith("BuildStage_"):
        return int(obj.name.split("_")[1].split("__")[0].split(".")[0])
    return None


def normalize_import(objects):
    # Store matrices first: rewriting a parent must not transform its children.
    matrices = {o: o.matrix_world.copy() for o in objects}
    for obj in objects:
        if obj.type == "MESH":
            obj.data = obj.data.copy()
            obj.data.transform(matrices[obj])
            obj.parent = None
            obj.matrix_world = Matrix.Identity(4)
    # Reparent meshes to their old owner with the new identity world basis.
    # Caller owns the imported hierarchy before this function returns.
    return matrices


def split_board(source, stage, collection, start_index):
    """Four genuinely separate boards with the exact old shelf silhouette."""
    low = min(v.co.y for v in source.data.vertices)
    high = max(v.co.y for v in source.data.vertices)
    for index in range(4):
        mesh = source.data.copy()
        bm = bmesh.new()
        bm.from_mesh(mesh)
        for boundary, outer in ((low + (high-low)*index/4, False),
                                (low + (high-low)*(index+1)/4, True)):
            result = bmesh.ops.bisect_plane(
                bm, geom=list(bm.verts)+list(bm.edges)+list(bm.faces), dist=1e-7,
                plane_co=(0, boundary, 0), plane_no=(0, 1, 0),
                clear_inner=not outer, clear_outer=outer,
            )
            edges = [edge for edge in result["geom_cut"]
                     if isinstance(edge, bmesh.types.BMEdge) and edge.is_boundary]
            if edges:
                bmesh.ops.holes_fill(bm, edges=edges, sides=0)
        bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
        bm.to_mesh(mesh)
        bm.free()
        # Split boards are hewn low-poly pieces. Smooth normals averaged with
        # the new cut caps would make every segment look inflated.
        for polygon in mesh.polygons:
            polygon.use_smooth = False
        obj = bpy.data.objects.new(f"board_unit_{start_index+index:02d}", mesh)
        collection.objects.link(obj)
        obj.parent = stage
        obj["hexlive_construction_role"] = "unit"
        obj["hexlive_export_name"] = obj.name
    bpy.data.objects.remove(source, do_unlink=True)


def prepare_wardrobe(root, imported, stages, collection):
    objects = {o.name: o for o in imported}
    for index, name in enumerate(("Wardrobe_Native_FrameL", "Wardrobe_Native_FrameR",
                                  "Wardrobe_Native_TopRail", "Wardrobe_Native_HangRail")):
        unit = empty(f"stick_unit_{index:02d}", stages[1], collection)
        parent_keep_world(objects[name], unit)
        if index < 2:
            parent_keep_world(objects["Wardrobe_Foot_" + ("L" if index == 0 else "R")], unit)
    for index, obj in enumerate(sorted(
            (o for o in imported if o.name.startswith("Wardrobe_ShoeShelf_")),
            key=lambda o: o.name)):
        imported.remove(obj)
        split_board(obj, stages[2], collection, index * 4)
    rope = empty("rope_unit_00", stages[3], collection)
    for obj in imported:
        if obj.name.startswith("Wardrobe_Native_") and "Rope_" in obj.name:
            parent_keep_world(obj, rope)
    # Socket/template names stay exact and direct: production reads them.
    for obj in list(imported):
        if obj.type == "EMPTY" and (obj.name.startswith(("ClothingSlot_", "ShoePairSlot_"))
                                    or obj.name == "HangerTemplate"):
            parent_keep_world(obj, root)
            obj["hexlive_construction_role"] = "helper"
            if obj.name == "HangerTemplate":
                obj.hide_render = True
                for part in obj.children_recursive:
                    part.hide_render = True
                    part.hide_set(True)
    root["hexlive_stage_notes"] = "4 frame units (feet included), 8 shelf planks, 1 compound rope"


def prepare(definition, number):
    scene = bpy.data.scenes.new(f"{number:02d} {LABELS[definition['id']]}")
    bpy.context.window.scene = scene
    collection = bpy.data.collections.new(definition["id"])
    scene.collection.children.link(collection)
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(REPO / definition["export"]))
    imported = list(set(bpy.data.objects)-before)
    bpy.context.view_layer.update()
    original_bounds = bounds(imported)
    parents = {o: o.parent for o in imported}
    original_stages = {stage_index(o): o for o in imported if stage_index(o)}
    matrices = normalize_import(imported)
    # Empties keep their original world matrix, descendants are then restored
    # under them. This preserves the door hinge and all garment/sleep sockets.
    for obj in imported:
        if obj.type == "EMPTY":
            obj.parent = None
            obj.matrix_world = matrices[obj]
    def depth(obj):
        count, parent = 0, parents[obj]
        while parent in parents:
            count += 1
            parent = parents[parent]
        return count
    for obj in sorted(imported, key=depth):
        obj.parent = parents[obj]
        obj.matrix_parent_inverse = Matrix.Identity(4)
        obj.matrix_world = Matrix.Identity(4) if obj.type == "MESH" else matrices[obj]
        bpy.context.view_layer.update()
    for obj in imported:
        for owner in list(obj.users_collection):
            owner.objects.unlink(obj)
        collection.objects.link(obj)
        obj["hexlive_export_name"] = obj.name

    root = empty(definition["id"], None, collection)
    root["hexlive_definition_id"] = definition["id"]
    root["hexlive_export_name"] = definition["id"]
    root["hexlive_source_bounds"] = json.dumps(original_bounds)
    stages = {}
    for expected in definition["stages"]:
        index = expected["index"]
        name = f"BuildStage_{index}__{TOKENS[expected['material']]}"
        stage = empty(name, root, collection)
        stage["hexlive_stage_index"] = index
        stage["hexlive_material_id"] = expected["material"]
        stage["hexlive_export_name"] = name
        stages[index] = stage

    if definition["id"] == "furniture.wardrobe":
        prepare_wardrobe(root, imported, stages, collection)
    elif definition["id"] == "station.drying_rack":
        for obj in imported:
            index = 1 if obj.name.split(".")[0] in ("stick_00", "stick_01") else (
                2 if obj.name.startswith("stick_") else 3)
            parent_keep_world(obj, stages[index])
    else:
        for index, old in original_stages.items():
            for child in list(old.children):
                parent_keep_world(child, stages[index])
        for obj in imported:
            if obj.type == "EMPTY" and not obj.children and obj not in original_stages.values():
                if obj.name.startswith(("point", "WC_point", "fire_point")):
                    parent_keep_world(obj, root)
                    obj["hexlive_construction_role"] = "helper"

    # Remove empty former import wrappers and stage parents, never art nodes.
    owned = set(root.children_recursive)
    for obj in imported:
        if obj.name not in bpy.data.objects:
            continue
        if obj not in owned:
            if obj.type == "MESH" or any(c.type == "MESH" for c in obj.children_recursive):
                raise RuntimeError(f"Unassigned artwork in {definition['id']}: {obj.name}")
            bpy.data.objects.remove(obj, do_unlink=True)
    bpy.context.view_layer.update()
    final_bounds = bounds(root.children_recursive)
    delta = max(abs(original_bounds[a][b]-final_bounds[a][b])
                for a in range(2) for b in range(3))
    if delta > .00001:
        raise RuntimeError(f"Geometry moved: {definition['id']}, delta={delta}")
    for stage in stages.values():
        for child in stage.children:
            if "_deco_" not in child.name and not child.name.startswith("HL_Door_State_"):
                child["hexlive_construction_role"] = "unit"
    scene["hexlive_definition_id"] = definition["id"]
    scene["hexlive_repository"] = str(REPO)
    scene.world = bpy.data.worlds.get("StageWorld") or bpy.data.worlds.new("StageWorld")
    scene.world.color = (.08, .08, .08)
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.display.shading.show_cavity = True
    scene.display.shading.cavity_type = "BOTH"
    scene.display.shading.background_type = "WORLD"
    bpy.ops.object.select_all(action="DESELECT")
    root.select_set(True)
    bpy.context.view_layer.objects.active = root
    print("PREPARED", definition["id"], "bounds_delta", delta,
          "stages", [(i, len(s.children)) for i, s in stages.items()])
    return scene, collection, root


def main():
    if OUTPUT.exists():
        raise RuntimeError("Workspace already exists; never regenerate over artist edits: " + str(OUTPUT))
    manifest = json.loads(MANIFEST.read_text())
    bpy.ops.wm.read_factory_settings(use_empty=True)
    initial = bpy.context.scene
    prepared = [prepare(d, i+1) for i, d in enumerate(manifest["definitions"])]
    overview = initial
    overview.name = "00 Все объекты"
    overview["hexlive_repository"] = str(REPO)
    for i, (scene, collection, root) in enumerate(prepared):
        instance = bpy.data.objects.new(LABELS[root["hexlive_definition_id"]], None)
        overview.collection.objects.link(instance)
        instance.instance_type = "COLLECTION"
        instance.instance_collection = collection
        instance.location = ((i%4)*3.5, (i//4)*3.5, 0)
    readme = bpy.data.texts.new("НАЧНИ ЗДЕСЬ.txt")
    readme.write("Все 14 объектов уже разложены по стадиям.\n"
                 "Выбери сцену в верхнем меню Scene. Каждый объект стоит в своём нуле.\n"
                 "В Outliner раскрой parent → BuildStage → children.\n"
                 "Перетаскивай children между стадиями с сохранением трансформации.\n"
                 "N → HexLive Build: назначение, preview, проверка и экспорт.\n"
                 "Один прямой child стадии = одна единица ресурса.\n"
                 "Сокеты, шарнир двери и HangerTemplate не удалять.\n"
                 "Сохраняй этот .blend перед экспортом. Полная инструкция: Docs/ConstructionStagesAuthoring.md\n")
    # Embed the panel launcher so this file is usable without installing an add-on.
    launcher = bpy.data.texts.new("ОТКРЫТЬ ПАНЕЛЬ.py")
    launcher.write("from pathlib import Path\nimport bpy\n"
                   "root=next(p for p in Path(bpy.data.filepath).resolve().parents if (p/'Tools/blender/construction_stage_authoring.py').is_file())\n"
                   "p=root/'Tools/blender/construction_stage_authoring.py'\n"
                   "exec(compile(p.read_text(), str(p), 'exec'), {'__name__':'__main__','__file__':str(p)})\n")
    # Start on the wardrobe, with orbit centred on the actual object.
    bpy.context.window.scene = prepared[-1][0]
    for screen in bpy.data.screens:
        for area in screen.areas:
            if area.type == "VIEW_3D":
                space = area.spaces.active
                space.shading.color_type = "MATERIAL"
                space.overlay.show_relationship_lines = False
                space.region_3d.view_location = (0, 0, .75)
                space.region_3d.view_distance = 3.3
                space.region_3d.view_rotation = Quaternion((.83, .48, .12, .25)).normalized()
                space.region_3d.view_perspective = "PERSP"
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.file.pack_all()
    bpy.ops.wm.save_as_mainfile(filepath=str(OUTPUT), compress=True)
    print("STAGE_WORKSPACE", OUTPUT)


if __name__ == "__main__":
    main()
