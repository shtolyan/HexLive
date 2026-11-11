"""Export and verify the authored wooden chest. Run with Blender in background mode."""

import argparse
import json
import math
from pathlib import Path
import re
import sys

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from author_atomic_world_props import collection, material
from author_modern_apartment import (IMPORTER, OBJECTS, ROOT, bake_parts, block, guid_for,
                                     render_icon, write_meta, write_unity_material)

ID = "furniture.storage_chest"
PIVOT = "HL_Door_Pivot"
CLOSED = "HL_Door_State_Closed"
OPEN = "HL_Door_State_Open"
PALETTE = {
    "ChestSapwood": ("Sapwood", .59, .86),
    "ChestHeartwood": ("Heartwood", .62, .91),
    "ChestBark": ("Bark", .72, .94),
    "ChestRope": ("Rope", .69, .97),
}
DIAGNOSTICS = ROOT / "Build/Diagnostics/storage-chest"


def geometry_constants():
    source = (ROOT / "Assets/HexLive/Simulation/Content/StorageChest.cs").read_text(encoding="utf-8")
    return tuple(float(re.search(rf"const float {name} = ([.\d]+)f", source)[1])
                 for name in ("ModelWidth", "ModelLength", "FootprintRadius"))


def load_palette(path):
    with bpy.data.libraries.load(str(path)) as (source, target):
        target.materials = [row[0] for row in PALETTE.values()]
        if any(name not in source.materials for name in target.materials):
            raise ValueError("The shared palette does not contain the chest materials")
    shades = {}
    for name, (shared, factor, roughness) in PALETTE.items():
        source = bpy.data.materials[shared]
        shader = source.node_tree.nodes.get("Principled BSDF") if source.use_nodes else None
        color = shader.inputs["Base Color"].default_value if shader else source.diffuse_color
        shades[name] = (tuple(min(.42, channel * factor) for channel in color[:3]), roughness)
    return shades


def empty(name, parent=None, location=(0, 0, 0), rotation=(0, 0, 0)):
    obj = bpy.data.objects.new(name, None)
    bpy.context.scene.collection.objects.link(obj)
    obj.parent = parent
    obj.location = location
    obj.rotation_euler = rotation
    return obj


def author(mats, width, length):
    wood, dark, bark, rope = (mats[name] for name in PALETTE)
    body = collection("Chest body source")
    lid = collection("Chest lid source")
    half_width, half_length = width / 2, length / 2
    block(body, "bottom", (0, 0, .032), (width - .044, length - .048, .042), dark)
    for side in (-1, 1):
        x, y = side * (half_width - .023), side * (half_length - .023)
        for row, z in enumerate((.108, .216)):
            finish = (wood, dark)[row]
            block(body, f"long_plank_{side}_{row}", (x, 0, z), (.038, length - .048, .103), finish)
            block(body, f"end_plank_{side}_{row}", (0, y, z), (width - .064, .036, .103), finish)
        block(body, f"long_rim_{side}", (x, 0, .275), (.040, length - .054, .030), dark)
        block(body, f"end_rim_{side}", (0, y, .275), (width - .070, .038, .030), dark)
        for end in (-1, 1):
            block(body, f"corner_{side}_{end}", (x, end * (half_length - .024), .145),
                  (.046, .048, .290), bark)
        block(body, f"hinge_lower_{side}", (-half_width + .012, side * .192, .251),
              (.018, .033, .057), rope)

    for i in range(3):
        board = width / 3
        block(lid, f"lid_plank_{i}", ((i - 1) * board, 0, .313),
              (board - .003, length, .039), (dark, wood, wood)[i])
    for side in (-1, 1):
        block(lid, f"lid_batten_{side}", (0, side * .195, .337),
              (width - .045, .033, .020), bark)
        block(lid, f"hinge_upper_{side}", (-half_width + .013, side * .192, .292),
              (.021, .033, .038), rope)
        block(lid, f"pull_foot_{side}", (half_width - .019, side * .043, .308),
              (.038, .014, .017), rope)
    block(lid, "rope_pull", (half_width - .007, 0, .306), (.014, .086, .014), rope)

    root = empty(ID)
    base_mesh = bake_parts("ChestBody", body.objects)
    base_mesh.parent = root
    hinge = Vector((-half_width + .040, 0, .292))
    pivot = empty(PIVOT, root, hinge)
    lid_mesh = bake_parts("ChestLid", lid.objects)
    lid_mesh.data.transform(Matrix.Translation(-hinge))
    lid_mesh.parent = pivot
    closed = empty(CLOSED, root, hinge)
    opened = empty(OPEN, root, hinge, (0, math.radians(-85), 0))
    return root, base_mesh, lid_mesh, pivot, closed, opened


def export(objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    path = OBJECTS / (ID + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=str(path), use_selection=True, object_types={"MESH", "EMPTY"},
        global_scale=1.0, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y", add_leaf_bones=False, bake_anim=False,
        use_mesh_modifiers=True, mesh_smooth_type="FACE", path_mode="STRIP",
    )
    template = IMPORTER.read_text(encoding="utf-8").split("ModelImporter:", 1)[1]
    remaps = "  externalObjects:\n" + "".join(
        "  - first:\n      type: UnityEngine:Material\n      assembly: UnityEngine.CoreModule\n"
        f"      name: {name}\n    second: {{fileID: 2100000, guid: {guid_for(OBJECTS / (name + '.mat'))}, type: 2}}\n"
        for name in PALETTE
    )
    template = (template.replace("  externalObjects: {}\n", remaps)
                .replace("  importAnimation: 1", "  importAnimation: 0")
                .replace("  preserveHierarchy: 0", "  preserveHierarchy: 1"))
    write_meta(path, "ModelImporter:" + template)
    return path


def verify(path, width, length, radius):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=str(path))
    root, pivot, closed, opened = [bpy.data.objects[name] for name in (ID, PIVOT, CLOSED, OPEN)]
    base_mesh, lid_mesh = [bpy.data.objects[name] for name in ("ChestBody", "ChestLid")]
    if ([obj for obj in bpy.context.scene.objects if obj.parent is None] != [root] or
            any(abs(float(v)) > .0001 for v in (*root.location, *root.rotation_euler)) or
            any(abs(v - 1) > .0001 for v in root.scale) or
            base_mesh.parent != root or lid_mesh.parent != pivot or
            any(node.parent != root for node in (pivot, closed, opened))):
        raise ValueError("Chest FBX did not retain the floor pivot and hinge hierarchy")
    if any((node.location - pivot.location).length > .0001 for node in (closed, opened)):
        raise ValueError("The lid poses do not share the hinge")
    if abs(pivot.rotation_euler.to_quaternion().rotation_difference(
            closed.rotation_euler.to_quaternion()).angle) > .0001:
        raise ValueError("The imported chest is not closed by default")

    meshes = [base_mesh, lid_mesh]
    points = [obj.matrix_world @ vertex.co for obj in meshes for vertex in obj.data.vertices]
    lower = [min(p[i] for p in points) for i in range(3)]
    upper = [max(p[i] for p in points) for i in range(3)]
    if (abs(lower[2]) > .0001 or abs(upper[0] - lower[0] - width) > .0001 or
            abs(upper[1] - lower[1] - length) > .0001 or
            max(math.hypot(p.x, p.y) for p in points) > radius):
        raise ValueError("The imported chest lies outside its authored closed footprint")
    for mesh in meshes:
        mesh.data.calc_loop_triangles()
    triangles = sum(len(mesh.data.loop_triangles) for mesh in meshes)
    names = {slot.material.name for mesh in meshes for slot in mesh.material_slots}
    if triangles > 400 or names != PALETTE.keys():
        raise ValueError("The chest exceeded its triangle budget or lost a material")

    pivot.rotation_euler = opened.rotation_euler
    bpy.context.view_layer.update()
    open_points = [lid_mesh.matrix_world @ vertex.co for vertex in lid_mesh.data.vertices]
    open_height = max(p.z for p in open_points)
    open_radius = max(math.hypot(p.x, p.y) for p in open_points)
    if open_height < .65 or open_radius > radius:
        raise ValueError(f"Open lid: height {open_height:.4f}, radius {open_radius:.4f}, "
                         f"pose {tuple(opened.rotation_euler)}, expected radius <= {radius}")
    pivot.rotation_euler = closed.rotation_euler
    bpy.context.view_layer.update()
    return meshes, {"id": ID, "triangles": triangles, "materials": sorted(names),
                    "bounds": {"min": lower, "max": upper}, "openHeight": open_height}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--palette", type=Path, default=ROOT / "Assets/HexLiveContent/source.blend")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    width, length, radius = geometry_constants()
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    shades = load_palette(args.palette)
    mats = {name: material(name, (*rgb, 1), roughness) for name, (rgb, roughness) in shades.items()}
    for name in mats:
        write_unity_material(name, directory=OBJECTS, palette=shades)
    path = export(author(mats, width, length))
    meshes, receipt = verify(path, width, length, radius)
    render_icon({"id": ID}, meshes)
    DIAGNOSTICS.mkdir(parents=True, exist_ok=True)
    (DIAGNOSTICS / "authoring.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt))


if __name__ == "__main__":
    main()
