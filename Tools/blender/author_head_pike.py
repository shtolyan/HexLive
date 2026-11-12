"""Author the one-junction head pike at 1:1 scale and render its owner icon.

blender -b -t 1 --python-exit-code 1 -P Tools/blender/author_head_pike.py
"""

import argparse
import math
import os
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "Tools"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from author_modern_apartment import guid_for, write_meta, write_unity_material
from author_atomic_world_props import material
from make_modern_loot import mesh, rings
import render_item_icon as icon


ITEM = "furniture.head_pike"
OBJECTS = ROOT / "Assets/HexLiveContent/RuntimeSource/Objects"
PALETTE = ROOT / "Assets/HexLiveContent/source.blend"
MATERIALS = {
    "HeadPikeBark": ("Bark", (.74, .64, .56), .92),
    "HeadPikeSapwood": ("Heartwood", (.55, .59, .66), .90),
    "HeadPikeLashing": ("Rope", (.53, .56, .59), .96),
    "HeadPikeSkin": ("Sapwood", (.53, .43, .40), .90),
    "HeadPikeHair": ("Bark", (.20, .17, .15), .95),
}


def load_finishes(source_path):
    anchors = sorted({entry[0] for entry in MATERIALS.values()})
    with bpy.data.libraries.load(str(source_path), link=False) as (source, destination):
        if not set(anchors) <= set(source.materials):
            raise ValueError(f"Missing island materials in {source_path}")
        destination.materials = anchors

    finishes = {}
    for name, (anchor, multipliers, roughness) in MATERIALS.items():
        shader = bpy.data.materials[anchor].node_tree.nodes.get("Principled BSDF")
        anchor_color = shader.inputs["Base Color"].default_value
        color = tuple(value * weight for value, weight in zip(anchor_color, multipliers))
        material(name, (*color, 1.0), roughness)
        finishes[name] = (color, roughness)
    return finishes


def section(name, levels, finish, sides=8):
    """Horizontal rings specified by (x, y, height, half-width, half-depth)."""
    return rings(name, [(x, y, z) for x, y, z, _, _ in levels],
                 [(width, depth) for _, _, _, width, depth in levels],
                 [finish], [0] * len(levels), sides=sides, vertical=True)


def relief(name, outline, front, back, finish):
    count = len(outline)
    vertices = [(x, y, z) for y in (front, back) for x, z in outline]
    faces = [tuple(range(count)), tuple(range(2 * count - 1, count - 1, -1))]
    faces.extend((i, (i + 1) % count, (i + 1) % count + count, i + count) for i in range(count))
    return mesh(name, vertices, faces, [finish])


def make_model():
    bark, wood, rope, skin, hair = (bpy.data.materials[name] for name in MATERIALS)
    rings("Shaped wooden stake",
          [(0, 0, 0), (-.002, .003, .11), (.004, .002, .39), (.009, .004, .71),
           (.008, .005, .943), (.008, .005, 1.065)],
          [(.031, .033), (.030, .030), (.024, .027), (.029, .026), (.028, .026), (.001, .001)],
          [bark, wood], [0, 0, 0, 0, 1, 1], sides=6, vertical=True)

    for index, z in enumerate((.703, .727)):
        band = section(f"Fiber binding {index + 1}",
                       [(.009, .004, z - .010, .036, .035), (.009, .004, z + .010, .036, .035)],
                       rope, sides=6)
        pivot = Vector((.009, .004, z))
        tilt = Matrix.Translation(pivot) @ Matrix.Rotation(.12 * (-1 if index else 1), 4, "X")
        band.data.transform(tilt @ Matrix.Translation(-pivot))

    head = [section("Angular face", [
        (0, -.007, .746, .041, .042), (0, -.015, .780, .061, .069),
        (0, -.007, .815, .085, .098), (0, 0, .865, .098, .120),
        (0, .005, .922, .101, .109), (0, .015, .970, .063, .076),
    ], skin)]
    for side in (-1, 1):
        head.append(section("Ear", [
            (.089 * side, .002, .817, .010, .013), (.100 * side, 0, .846, .015, .024),
            (.096 * side, .002, .876, .008, .012),
        ], skin, sides=5))
        eye = [(.023 * side, .869), (.057 * side, .877), (.063 * side, .868), (.025 * side, .861)]
        head.append(relief("Closed eyelid", eye, -.119, -.087, hair))

    head.append(relief("Mouth", [(-.024, .799), (.019, .802), (.022, .794), (-.024, .792)], -.110, -.080, hair))
    head.append(mesh("Nose", [(-.010, -.111, .878), (.011, -.111, .874), (.015, -.111, .836),
                              (-.014, -.111, .836), (0, -.149, .842)],
                     [(0, 1, 2, 3), (0, 4, 1), (1, 4, 2), (2, 4, 3), (3, 4, 0)], [skin]))

    scalp = section("Close-cropped hair", [
        (0, .003, .881, .110, .124), (0, .006, .950, .105, .114), (0, .010, .989, .068, .078),
    ], hair)
    for vertex in list(scalp.data.vertices)[:8]:
        front = max(0.0, (.003 - vertex.co.y) / .124)
        vertex.co.z += .057 * front
    head.append(scalp)

    pivot = Vector((0, 0, .847))
    tilt = Matrix.Translation(pivot) @ Matrix.Rotation(math.radians(-8), 4, "Y")
    for part in head:
        part.data.transform(tilt @ Matrix.Translation(-pivot))

    parts = list(bpy.context.scene.objects)
    for part in parts:
        part.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    model = bpy.context.object
    model.name = model.data.name = ITEM
    with_normals = bmesh.new()
    with_normals.from_mesh(model.data)
    bmesh.ops.recalc_face_normals(with_normals, faces=list(with_normals.faces))
    with_normals.to_mesh(model.data)
    with_normals.free()
    for face in model.data.polygons:
        face.use_smooth = False
    model.data.calc_loop_triangles()
    if not 100 <= len(model.data.loop_triangles) <= 400:
        raise ValueError("Head pike exceeds the low-poly geometry budget")
    return model


def export_model(model, finishes):
    path = OBJECTS / (ITEM + ".fbx")
    bpy.ops.export_scene.fbx(
        filepath=str(path), use_selection=True, object_types={"MESH"},
        global_scale=1.0, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y", add_leaf_bones=False, bake_anim=False,
        mesh_smooth_type="FACE", path_mode="STRIP",
    )
    remaps = "  externalObjects:\n"
    for finish in model.data.materials:
        name = finish.name
        write_unity_material(name, OBJECTS, finishes)
        unity_material = OBJECTS / (name + ".mat")
        clean_lines(unity_material)
        clean_lines(Path(str(unity_material) + ".meta"))
        guid = guid_for(unity_material)
        remaps += ("  - first:\n      type: UnityEngine:Material\n      assembly: UnityEngine.CoreModule\n"
                   f"      name: {name}\n    second: {{fileID: 2100000, guid: {guid}, type: 2}}\n")
    template = (OBJECTS / "furniture.wardrobe.fbx.meta").read_text(encoding="utf-8").split("ModelImporter:", 1)[1]
    template = template.replace("  externalObjects: {}\n", remaps).replace("  importAnimation: 1", "  importAnimation: 0")
    write_meta(path, "ModelImporter:" + template)
    clean_lines(Path(str(path) + ".meta"))
    print(f"{ITEM}: {len(model.data.loop_triangles)} triangles; {tuple(model.dimensions)} wu")
    return path


def render_icon(path):
    meshes = icon.load(str(path))
    original_style = icon.STYLES["tool"]
    icon.STYLES["tool"] = (Vector((.42, -1, .23)), *original_style[1:])
    icon.setup_materials(meshes, "tool")
    render = bpy.context.scene.render
    render.threads_mode, render.threads = "FIXED", 1
    output = ROOT / "Build/Diagnostics/head-pike"
    output.mkdir(parents=True, exist_ok=True)
    png = output / (ITEM + ".png")
    icon.render(str(png), meshes, "tool", 1.10, 24)
    icon.install(str(png), ITEM)
    clean_lines(ROOT / icon.ICON_DIR / (ITEM + ".png.meta"))


def clean_lines(path):
    text = path.read_text(encoding="utf-8")
    path.write_text("\n".join(line.rstrip() for line in text.splitlines()) + "\n", encoding="utf-8", newline="\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--palette", type=Path, default=PALETTE)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    os.chdir(ROOT)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    finishes = load_finishes(args.palette.resolve())
    model = make_model()
    path = export_model(model, finishes)
    render_icon(path)


if __name__ == "__main__":
    main()
