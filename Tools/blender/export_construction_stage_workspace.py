"""Validate/export artist-edited stage parents from the shared .blend library.

blender _ArtSource/Construction/hexlive_construction_stages.blend -b \
  --python Tools/blender/export_construction_stage_workspace.py -- --audit-source

Use --definition bed.basic to export one object, or --all for all objects.
--export-dir /tmp/... validates exports without changing Unity build inputs.
Without an export argument this tool only validates the editable source.
"""
import importlib.util
from datetime import datetime, timezone
import hashlib
import json
import shutil
import sys
import uuid
from pathlib import Path

import bpy
from mathutils import Matrix, Vector
from mathutils.kdtree import KDTree

REPO = Path(__file__).resolve().parents[2]
SOURCE = REPO / "_ArtSource/Construction/hexlive_construction_stages.blend"
spec = importlib.util.spec_from_file_location(
    "hexlive_stage_authoring", REPO / "Tools/blender/construction_stage_authoring.py")
authoring = importlib.util.module_from_spec(spec)
spec.loader.exec_module(authoring)


def find_root(definition_id):
    roots = [obj for obj in bpy.data.objects
             if obj.get("hexlive_definition_id") == definition_id]
    if len(roots) != 1:
        raise RuntimeError(f"Expected one authoring root for {definition_id}, got {len(roots)}")
    return roots[0]


def validate(definition):
    root = find_root(definition["id"])
    issues = authoring.validate_root(root, definition)
    if any(abs(root.matrix_world[i][j] - Matrix.Identity(4)[i][j]) > 1e-5
           for i in range(4) for j in range(4)):
        issues.append("Authoring root must stay at the asset origin with identity transform")
    for index, stage in authoring.stages_below(root):
        if any(abs(stage.matrix_local[i][j]-Matrix.Identity(4)[i][j]) > 1e-5
               for i in range(4) for j in range(4)):
            issues.append(f"Stage {index} must have identity transform for drag reparenting")
    export_names = [stage_name(obj) for obj in [root, *root.children_recursive]]
    duplicate_names = sorted(name for name in set(export_names) if export_names.count(name)>1)
    if duplicate_names:
        issues.append("Duplicate exported attachment/part names: " + ", ".join(duplicate_names))
    required = definition.get("requiredNodes", [])
    missing = [name for name in required if name not in export_names]
    if missing:
        issues.append("Missing required sockets/parts: " + ", ".join(missing))
    if issues:
        raise RuntimeError(definition["id"] + ": " + "; ".join(issues))
    return root


def stage_name(obj):
    if obj.get("hexlive_stage_index"):
        token = authoring.MATERIAL_TOKEN[obj["hexlive_material_id"]]
        return f"BuildStage_{obj['hexlive_stage_index']}__{token}"
    return str(obj.get("hexlive_export_name", obj.name))


def export_definition(definition, output=None):
    root = validate(definition)
    scene = next(s for s in bpy.data.scenes if s.get("hexlive_definition_id") == definition["id"])
    old_scene = bpy.context.window.scene
    bpy.context.window.scene = scene
    destination = Path(output) if output else REPO / definition["export"]
    destination.parent.mkdir(parents=True, exist_ok=True)
    source_objects = [root, *root.children_recursive]
    source_matrices = {obj: obj.matrix_world.copy() for obj in source_objects}
    # Capture action paths before parking source names: stage discovery uses
    # those canonical names and must not run against temporary export names.
    def node_path(node):
        parts = []
        while node is not None:
            parts.append(stage_name(node))
            if node == root:
                break
            node = node.parent
        return "/".join(reversed(parts))
    actions = [{"index": index+1, "stage": stage, "resource": material,
                "count": 1, "nodePath": node_path(unit)}
               for index, (stage, material, unit) in enumerate(
                   authoring.unit_sequence(root, definition))]
    # FBX preserves exact node names. Park all conflicting names for the short
    # lifetime of the export copies, so Blender cannot append .001 to sockets.
    desired_names = {obj: stage_name(obj) for obj in source_objects}
    parked = {}
    for name in set(desired_names.values()):
        existing = bpy.data.objects.get(name)
        if existing:
            parked[existing] = existing.name
            existing.name = "__stage_export_source__" + existing.name
    temp = bpy.data.collections.new("__stage_export__")
    scene.collection.children.link(temp)
    copies = {}
    try:
        for source in source_objects:
            clone = source.copy()
            clone.name = desired_names[source]
            clone.data = source.data
            temp.objects.link(clone)
            copies[source] = clone
        for source, clone in copies.items():
            clone.parent = copies.get(source.parent)
            clone.matrix_parent_inverse = Matrix.Identity(4)
            clone.matrix_world = source_matrices[source]
            clone.hide_viewport = False
            clone.hide_render = False
            clone.hide_set(False)
        bpy.context.view_layer.update()
        bpy.ops.object.select_all(action="DESELECT")
        for clone in copies.values():
            clone.select_set(True)
        bpy.context.view_layer.objects.active = copies[root]
        bpy.ops.export_scene.fbx(
            filepath=str(destination), use_selection=True, object_types={"EMPTY", "MESH"},
            apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
            axis_forward="-Z", axis_up="Y", use_space_transform=True,
            bake_space_transform=False, add_leaf_bones=False, mesh_smooth_type="FACE",
            use_mesh_modifiers=True, bake_anim=False, path_mode="AUTO", embed_textures=False,
        )
        # One action = one paid unit. This data is also usable by the future
        # NPC work animation; it never requires reverse-engineering renderer bounds.
        receipt = {"version": 1, "definitionId": definition["id"],
                   "modelSha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
                   "basis": {"up": "+Z", "forward": "+Y", "units": "wu",
                             "fbxForward": "-Z", "fbxUp": "Y"},
                   "actions": actions}
        receipt_path = destination.with_suffix(".construction.json")
        receipt_path.write_text(
            json.dumps(receipt, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
        if destination.is_absolute() and (REPO/"Assets") in destination.parents:
            meta = Path(str(receipt_path)+".meta")
            if not meta.exists():
                meta.write_text("fileFormatVersion: 2\nguid: "+uuid.uuid4().hex+"\n", encoding="utf-8")
    finally:
        for clone in copies.values():
            bpy.data.objects.remove(clone, do_unlink=True)
        bpy.data.collections.remove(temp)
        for obj, name in parked.items():
            obj.name = name
        bpy.context.window.scene = old_scene
    print("STAGE_EXPORTED", definition["id"], destination)
    return destination


def geometry(objects):
    return [obj.matrix_world @ vertex.co for obj in objects if obj.type == "MESH"
            for vertex in obj.data.vertices]


def verify_exports(definitions, directory):
    # Capture every source before resetting Blender for independent FBX reads.
    expected = {}
    marker_names = ("ClothingSlot_", "ShoePairSlot_", "DryingSlot_", "ShoeShelfSurface", "HangerTemplate", "point", "fire_point",
                    "HL_Door_Pivot", "HL_Door_State_")
    for definition in definitions:
        root = validate(definition)
        objects = [root, *root.children_recursive]
        expected[definition["id"]] = (
            geometry(objects),
            {stage_name(o): o.matrix_world.copy() for o in objects
             if stage_name(o).startswith(marker_names)},
        )
    for definition in definitions:
        points, markers = expected[definition["id"]]
        path = directory / Path(definition["export"]).name
        receipt = json.loads(path.with_suffix(".construction.json").read_text())
        if receipt.get("modelSha256") != hashlib.sha256(path.read_bytes()).hexdigest():
            raise RuntimeError(definition["id"]+": stale export receipt")
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.ops.import_scene.fbx(filepath=str(path))
        objects = list(bpy.context.scene.objects)
        tree = KDTree(len(points))
        for i, point in enumerate(points):
            tree.insert(point, i)
        tree.balance()
        actual = geometry(objects)
        if len(points) != len(actual):
            raise RuntimeError(f"{definition['id']}: FBX vertex count changed")
        reverse = KDTree(len(actual))
        for i, point in enumerate(actual):
            reverse.insert(point, i)
        reverse.balance()
        error = max(max(tree.find(point)[2] for point in actual),
                    max(reverse.find(point)[2] for point in points))
        if error > 1e-5:
            raise RuntimeError(f"{definition['id']}: FBX geometry moved by {error}")
        for name, matrix in markers.items():
            node = bpy.data.objects.get(name)
            if node is None:
                raise RuntimeError(f"{definition['id']}: missing marker {name}")
            if max(abs(matrix[i][j]-node.matrix_world[i][j])
                   for i in range(4) for j in range(4)) > 1e-5:
                raise RuntimeError(f"{definition['id']}: marker moved: {name}")
        wrapper = bpy.data.objects.new("__verify__", None)
        for obj in objects:
            if obj.parent is None:
                world = obj.matrix_world.copy()
                obj.parent = wrapper
                obj.matrix_world = world
        issues = authoring.validate_root(wrapper, definition)
        if issues:
            raise RuntimeError(definition["id"] + ": " + "; ".join(issues))
        sequence = authoring.unit_sequence(wrapper, definition)
        if len(sequence) != len(receipt["actions"]):
            raise RuntimeError(definition["id"]+": action count differs from FBX units")
        for index, ((stage, material, unit), action) in enumerate(zip(sequence, receipt["actions"])):
            names = []
            node = unit
            while node is not None and node != wrapper:
                names.append(node.name)
                node = node.parent
            if (action["index"] != index+1 or action["count"] != 1 or
                    action["stage"] != stage or action["resource"] != material or
                    action["nodePath"] != "/".join(reversed(names))):
                raise RuntimeError(f"{definition['id']}: action {index+1} does not match imported hierarchy")
        print("STAGE_ROUNDTRIP_OK", definition["id"], "vertices", len(points),
              "max_error", error, "markers", len(markers))


def main():
    definitions = json.loads((REPO/"Tools/construction_stage_manifest.json").read_text())["definitions"]
    args = sys.argv[sys.argv.index("--")+1:] if "--" in sys.argv else []
    if Path(bpy.data.filepath).resolve() != SOURCE:
        if "--candidate" not in args or not ("--export-dir" in args or "--verify-export-dir" in args):
            raise RuntimeError("Open the editable construction-stage .blend: " + str(SOURCE))
    requested = args[args.index("--definition")+1] if "--definition" in args else None
    selected = [d for d in definitions if requested is None or d["id"] == requested]
    if not selected:
        raise RuntimeError("Unknown definition: " + str(requested))
    if "--verify-export-dir" in args:
        directory = Path(args[args.index("--verify-export-dir")+1])
        verify_exports(selected, directory)
        return
    for definition in selected:
        root = validate(definition)
        print("STAGE_SOURCE_OK", definition["id"],
              [(i, len(authoring.resource_units(stage)))
               for i, stage in authoring.stages_below(root)])
    if "--all" in args or requested:
        export_dir = Path(args[args.index("--export-dir")+1]) if "--export-dir" in args else None
        if export_dir is None:
            backup = REPO/"Build/ConstructionExportBackups"/datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
            backup.mkdir(parents=True)
            for definition in selected:
                original = REPO/definition["export"]
                for source in (original, Path(str(original)+".meta"), original.with_suffix(".construction.json")):
                    if source.is_file():
                        shutil.copy2(source, backup/source.name)
            print("CONSTRUCTION_EXPORT_BACKUP", backup)
        for definition in selected:
            output = export_dir / Path(definition["export"]).name if export_dir else None
            export_definition(definition, output)
    print("STAGE_SOURCE_AUDIT", len(selected), "objects, 0 errors")


if __name__ == "__main__":
    main()
