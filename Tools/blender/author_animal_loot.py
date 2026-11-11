"""Author #694 world props and render icons from their actual exported FBXs."""

import argparse
import json
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector
from mathutils.geometry import delaunay_2d_cdt, tessellate_polygon

sys.path.insert(0, str(Path(__file__).resolve().parent))
from author_atomic_world_props import collection, material
from author_modern_apartment import OBJECTS, ROOT, bake_parts, export_model, verify_export, write_unity_material

sys.path.insert(0, str(ROOT / "Tools"))
from render_item_icon import STYLES, install, render, setup_materials

HIDE = "resource.hide_wolf"
RAW = "food.crab_raw"
COOKED = "food.crab_cooked"
OUTPUT = ROOT / "Build/Diagnostics/animal-loot"

# From the snout around the right ear, paws and tail. The left edge has
# slightly different cuts so the laid-out hide has a handmade outline.
HIDE_EDGE = (
    (0, .61), (.061, .599), (.090, .546), (.107, .493), (.148, .509),
    (.181, .612), (.214, .531), (.192, .447), (.165, .347), (.216, .290),
    (.335, .303), (.418, .363), (.482, .348), (.511, .318), (.508, .269),
    (.467, .228), (.398, .231), (.303, .172), (.239, .132), (.223, .040),
    (.221, -.112), (.251, -.221), (.345, -.266), (.452, -.272),
    (.504, -.316), (.490, -.362), (.452, -.385), (.369, -.362),
    (.282, -.318), (.219, -.333), (.181, -.414), (.104, -.443),
    (.112, -.513), (.122, -.591), (.099, -.677), (.054, -.749), (0, -.778),
)
FUR_POINTS = (
    (0, .573), (-.043, .531), (.039, .528), (-.114, .477), (.12, .472),
    (0, .416), (-.13, .363), (.12, .355), (-.271, .268), (.269, .253),
    (-.413, .277), (.421, .283), (-.121, .236), (.092, .237), (0, .284),
    (-.164, .117), (-.065, .132), (.052, .142), (.165, .118),
    (-.151, -.021), (-.073, -.004), (.022, .011), (.129, -.027),
    (-.165, -.148), (-.071, -.171), (.056, -.139), (.163, -.143),
    (-.242, -.245), (.245, -.245), (-.402, -.327), (.397, -.314),
    (-.107, -.291), (.012, -.295), (.112, -.273),
    (-.102, -.374), (.07, -.391), (.006, -.476), (.035, -.586), (0, -.704),
)
CRAB_BUDGET = {"Eyes": 24, "Shell": 134, "ShellLight": 180, "Underside": 58}
CRAB_COLOURS = {
    RAW: {"Shell": (.36, .091, .051), "ShellLight": (.52, .17, .086),
          "Underside": (.51, .34, .205), "Eyes": (.039, .025, .019)},
    COOKED: {"Shell": (.56, .061, .024), "ShellLight": (.74, .185, .047),
             "Underside": (.67, .36, .15), "Eyes": (.058, .028, .015)},
}


def load_palette(path):
    with bpy.data.libraries.load(str(path)) as (source, loaded):
        loaded.materials = ["Bark", "Sapwood"]
    result = {}
    for value in loaded.materials:
        shader = value.node_tree.nodes.get("Principled BSDF") if value.node_tree else None
        result[value.name] = tuple((shader.inputs["Base Color"].default_value if shader else value.diffuse_color)[:3])
    return result


def hide_materials(palette):
    bark, sapwood = palette["Bark"], palette["Sapwood"]
    return {
        "WolfFurSilver": (tuple(.23 + .085 * channel for channel in sapwood), .96),
        "WolfFurGuard": ((.15, .152, .159), .96),
        "WolfFurBack": ((.077, .081, .087), .97),
        "WolfFurLeather": (tuple(.54 * channel for channel in bark), .94),
    }


def triangulate(loop):
    # Blender 5 returns indices; earlier builds return the original vectors.
    return (tuple(point if isinstance(point, int) else loop.index(point) for point in tri)
            for tri in tessellate_polygon([loop]))


def make_hide(palette):
    edge = [(x, y) for x, y in HIDE_EDGE]
    edge.extend((-x * (1 + .019 * ((i % 3) - 1)), y + .009 * ((i % 5) - 2))
                for i, (x, y) in reversed(list(enumerate(HIDE_EDGE[1:-1], 1))))
    edge.reverse()
    boundary = [(i, (i + 1) % len(edge)) for i in range(len(edge))]
    points, _, triangles, sources, _, _ = delaunay_2d_cdt(
        [Vector(point) for point in (*edge, *FUR_POINTS)], boundary, [], 1, .00001)
    source_to_mesh = {original: i for i, originals in enumerate(sources) for original in originals}
    top = [Vector((x, y + .08, .016 + .022 * (1 - min(abs(x) / .52, 1)) +
                   .004 * (i % 3))) for i, (x, y) in enumerate(points)]
    bottom = [Vector((x, y + .08, 0)) for x, y in edge]
    # Fill small triangles inside the cut contour so grey guard hairs read
    # without long, dark strips crossing the entire pelt.
    faces = list(triangles)
    assignments = []
    for index, face in enumerate(faces):
        x, y = sum((points[i] for i in face), Vector((0, 0))) / len(face)
        dark_back = abs(x) < .13 and -.37 < y < .39 and index % 4 != 0
        nose = abs(x) < .068 and y > .553
        assignments.append(2 if dark_back or nose else 1 if index % 3 else 0)
    n = len(top)
    for start, end in boundary:
        faces.append((n + start, n + end, source_to_mesh[end], source_to_mesh[start]))
        assignments.append(3)
    for triangle in triangulate(bottom):
        faces.append(tuple(n + i for i in reversed(triangle)))
        assignments.append(3)
    mesh = bpy.data.meshes.new(HIDE)
    mesh.from_pydata(top + bottom, [], faces)
    mesh.update()
    obj = bpy.data.objects.new(HIDE + "_fur", mesh)
    owner = collection(HIDE)
    owner.objects.link(obj)
    for name, (rgb, roughness) in hide_materials(palette).items():
        mesh.materials.append(material(name, (*rgb, 1), roughness))
    for face, slot in zip(mesh.polygons, assignments):
        face.material_index = slot
    return owner


def make_crab(identifier, source, palette):
    bpy.ops.import_scene.fbx(filepath=str(source), use_anim=False)
    imported = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    owner = collection(identifier)
    colours = {}
    for obj in imported:
        key = obj.data.materials[0].name.split(".")[-1]
        obj.data.calc_loop_triangles()
        bpy.context.view_layer.objects.active = obj
        modifier = obj.modifiers.new("whole crab LOD", "DECIMATE")
        modifier.ratio = CRAB_BUDGET[key] / len(obj.data.loop_triangles)
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        name = "WholeCrab" + ("Raw" if identifier == RAW else "Cooked") + key
        rgb = tuple(.91 * value + .09 * ground for value, ground in
                    zip(CRAB_COLOURS[identifier][key], palette["Bark"]))
        colours[name] = (rgb, .91 if identifier == RAW else .83)
        obj.data.materials[0] = material(name, (*rgb, 1), colours[name][1])
        owner.objects.link(obj)
    mesh = bake_parts(identifier + "_whole", owner.objects)
    for obj in imported:
        bpy.data.objects.remove(obj, do_unlink=True)
    lo = Vector(min(vertex.co[i] for vertex in mesh.data.vertices) for i in range(3))
    hi = Vector(max(vertex.co[i] for vertex in mesh.data.vertices) for i in range(3))
    mesh.data.transform(Matrix.Translation((-(lo.x + hi.x) / 2, -(lo.y + hi.y) / 2, -lo.z)))
    owner.objects.link(mesh)
    return owner, colours


def render_icon(row, meshes):
    identifier = row["id"]
    target = OUTPUT / (identifier + ".png")
    original = STYLES["tool"]
    view = Vector((.13, -.26, 1) if identifier == HIDE else (.10, .52, .92))
    STYLES["tool"] = (view, *original[1:])
    try:
        bpy.context.scene.render.threads_mode = "FIXED"
        bpy.context.scene.render.threads = 1
        setup_materials(meshes, "tool")
        render(str(target), meshes, "tool", 1.17, 20)
        install(str(target), identifier)
    finally:
        STYLES["tool"] = original


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--palette", type=Path, default=ROOT / "Assets/HexLiveContent/source.blend")
    parser.add_argument("--crab", type=Path, default=ROOT / "Assets/HexLiveContent/RuntimeSource/Animals/crab.fbx")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    OUTPUT.mkdir(parents=True, exist_ok=True)
    rows = []
    for identifier in (HIDE, RAW, COOKED):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.context.scene.unit_settings.system = "METRIC"
        palette = load_palette(args.palette)
        owner, colours = (make_hide(palette), hide_materials(palette)) if identifier == HIDE else (
            make_crab(identifier, args.crab, palette))
        for name in colours:
            write_unity_material(name, directory=OBJECTS, palette=colours)
        row = export_model(identifier, owner, "carryable", True, material_directory=OBJECTS, origin="primal")
        if row["triangles"] > 400:
            raise ValueError(f"{identifier} exceeded the portable prop triangle budget")
        meshes = verify_export(row, 0, 0)
        render_icon(row, meshes)
        rows.append(row)
    if rows[1]["blenderBounds"] != rows[2]["blenderBounds"] or rows[1]["triangles"] != rows[2]["triangles"]:
        raise ValueError("The crab changed geometry between raw and cooked")
    (OUTPUT / "authoring.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
