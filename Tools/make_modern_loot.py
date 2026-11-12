"""Build the apartment loot as flat-shaded native FBX and render inventory icons.

blender -b -t 1 -P Tools/make_modern_loot.py -- [--icons] [--decor] [--item ID]
The game sets hand and ground sizes through ObjectFit.
"""

import math
import os
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Assets/HexLiveContent/source.blend"
OBJECTS = ROOT / "Assets/HexLiveContent/RuntimeSource/Objects"
BUILD = ROOT / "Build/Diagnostics/portal-loot"


def palette():
    anchors = ("Sapwood", "Heartwood", "Bark", "LeafGreen", "Prosthetic_Metal", "Prosthetic_MetalDark")
    with bpy.data.libraries.load(str(SOURCE), link=False) as (source, destination):
        assert set(anchors) <= set(source.materials)
        destination.materials = list(anchors)


def shade(anchor, name, weights, roughness=0.7):
    base = bpy.data.materials[anchor]
    mat = base.copy()
    mat.name = "Modern_" + name
    bsdf = next(node for node in mat.node_tree.nodes if node.type == "BSDF_PRINCIPLED")
    rgb = tuple(min(0.45, c * weight) for c, weight in zip(bsdf.inputs["Base Color"].default_value, weights))
    bsdf.inputs["Base Color"].default_value = (*rgb, 1)
    bsdf.inputs["Roughness"].default_value = roughness
    mat.diffuse_color = (*rgb, 1)
    return mat


def mesh(name, vertices, faces, materials, indices=None):
    data = bpy.data.meshes.new(name)
    data.from_pydata(vertices, [], faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    for material in materials:
        data.materials.append(material)
    for index, face in enumerate(data.polygons):
        face.use_smooth = False
        if indices is not None:
            face.material_index = indices[index]
    return obj


def prism(name, outline, low, high, material):
    count = len(outline)
    vertices = [(x, y, z) for z in (low, high) for x, y in outline]
    faces = [tuple(range(count - 1, -1, -1)), tuple(range(count, 2 * count))]
    for i in range(count):
        j = (i + 1) % count
        faces.append((i, j, j + count, i + count))
    return mesh(name, vertices, faces, [material])


def rings(name, centers, radii, materials, levels, sides=8, vertical=False):
    vertices = []
    for (x, y, z), (wide, deep) in zip(centers, radii):
        for i in range(sides):
            a = math.tau * i / sides
            u, v = wide * math.cos(a), deep * math.sin(a)
            vertices.append((x + u, y + v, z) if vertical else (x + u, y, z - v))

    faces = [tuple(range(sides - 1, -1, -1))]
    indices = [levels[0]]
    for ring in range(len(centers) - 1):
        for i in range(sides):
            j = (i + 1) % sides
            a, b = ring * sides, (ring + 1) * sides
            faces.append((a + i, a + j, b + j, b + i))
            indices.append(levels[ring])
    start = (len(centers) - 1) * sides
    faces.append(tuple(range(start, start + sides)))
    indices.append(levels[-1])
    return mesh(name, vertices, faces, materials, indices)


def box(name, x, y, z, width, length, depth, material):
    left, right = x - width / 2, x + width / 2
    bottom, top = y - length / 2, y + length / 2
    return prism(name, [(left, bottom), (right, bottom), (right, top), (left, top)],
                 z - depth / 2, z + depth / 2, material)


def seat_grip(butt, turn=False):
    """Move a held tool into the shipped tools' mesh frame (TOOL_GENERATION_SPEC §6).

    The GearConfig hand pose discards the FBX root rotation, so it sees the
    authored Blender axes with X negated: the grip END must sit at the origin
    and the handle must run along +Y. `turn` spins the part 180 degrees about
    the handle, which moves a -X edge to +X (Unity -X: the palm side of the
    reference knife grip). Bugs #758/#759: pivots at the bolster/mid-handle put
    the hand on the blade and left the handle sticking out of the fist.
    """
    frame = Matrix.Translation((0, -butt, 0))
    if turn:
        frame = Matrix.Rotation(math.pi, 4, "Y") @ frame
    for part in bpy.context.scene.objects:
        if part.type == "MESH":
            part.data.transform(frame)


def kitchen_knife():
    steel = shade("Prosthetic_Metal", "KnifeSteel", (0.73, 0.8, 0.93), 0.45)
    edge = shade("Prosthetic_Metal", "KnifeEdge", (1.1, 1.05, 1.07), 0.38)
    grip = shade("Prosthetic_MetalDark", "KnifeGrip", (0.25, 0.31, 0.40))
    prism("Blade", [(-.115, .038), (.070, .038), (.079, .431), (.058, .596),
                    (-.010, .540), (-.089, .445), (-.116, .244)], -.018, .018, steel)
    prism("Ground edge", [(-.118, .049), (-.103, .060), (-.102, .246), (-.074, .446),
                          (-.011, .525), (.058, .596), (-.008, .548), (-.092, .448), (-.120, .243)],
          -.019, .019, edge)
    prism("Handle", [(-.057, -.285), (.056, -.285), (.069, -.241), (.068, .056),
                     (.046, .088), (-.047, .083), (-.067, -.244)], -.045, .045, grip)
    for y in (-.215, -.102, .005):
        rings("Rivet", [(0, y, -.046), (0, y, .046)], [(.012, .012)] * 2,
              [edge], [0, 0], sides=6, vertical=True)
    # Same frame as tool.knife: handle end at the pivot, edge toward the palm.
    seat_grip(-.285, turn=True)


def baseball_bat():
    barrel = shade("Sapwood", "BatWood", (0.48, 0.43, 0.36))
    end = shade("Heartwood", "BatEnd", (0.43, 0.41, 0.35))
    grip = shade("Bark", "BatGrip", (0.34, 0.36, 0.48))
    band = shade("LeafGreen", "BatBand", (0.42, 0.52, 0.71))
    profile = [(-.218, .039), (-.203, .054), (-.181, .055), (-.163, .029),
               (-.085, .028), (.073, .029), (.084, .030), (.175, .032),
               (.377, .047), (.595, .075), (.745, .079), (.789, .066)]
    rings("Tapered bat", [(0, y, 0) for y, _ in profile], [(r, r) for _, r in profile],
          [barrel, end, grip, band], [1, 1, 1, 2, 2, 3, 0, 0, 0, 0, 1, 1])


def apple():
    red = shade("Heartwood", "AppleRed", (.54, .12, .10))
    shoulder = shade("Heartwood", "AppleShoulder", (.56, .25, .11))
    leaf = shade("LeafGreen", "FruitLeaf", (.77, .85, .72))
    stalk = shade("Bark", "FruitStem", (.56, .51, .45))
    rings("Apple skin", [(0, 0, z) for z in (.0, .063, .196, .355, .444, .446)],
          [(.077, .068), (.176, .160), (.228, .211), (.231, .220), (.153, .151), (.063, .065)],
          [red, shoulder], [0, 0, 0, 1, 1, 0], sides=10, vertical=True)
    rings("Stem", [(0, 0, .42), (.035, 0, .524)], [(.025, .020), (.017, .016)],
          [stalk], [0, 0], sides=6, vertical=True)
    mesh("Folded leaf", [(0, .004, .488), (.113, -.068, .507), (.227, -.024, .501),
                         (.117, .057, .495), (.104, 0, .531)],
         [(0, 1, 4), (1, 2, 4), (2, 3, 4), (3, 0, 4), (3, 2, 1, 0)], [leaf])


def banana():
    peel = shade("Sapwood", "BananaPeel", (.55, .62, .13))
    tip = shade("Bark", "BananaTip", (.52, .52, .29))
    green = shade("LeafGreen", "BananaStem", (.62, .73, .28))
    points = [(.190, -.397, .049), (.131, -.359, .035), (.008, -.257, 0), (-.080, -.111, 0),
              (-.087, .061, .013), (-.026, .220, .051), (.096, .323, .098), (.175, .371, .128)]
    sizes = [(.025, .026), (.049, .053), (.075, .084), (.086, .097),
             (.084, .096), (.073, .078), (.050, .052), (.022, .023)]
    rings("Curved peel", points, sizes, [peel, tip, green], [1, 0, 0, 0, 0, 0, 2, 2], sides=7)


def dollar_bills():
    paper = shade("LeafGreen", "CashPaper", (2.45, 2.12, 2.05))
    ink = shade("LeafGreen", "CashInk", (.32, .45, .39))
    band = shade("Sapwood", "CashBand", (.64, .64, .69))
    for i in range(3):
        box("Banknotes", (.018 if i % 2 else 0), -.008 * i, .010 * i, .75, .33, .013, paper)
    for y in (-.126, .094):
        box("Printed frame", 0, y, .028, .66, .018, .004, ink)
    for x in (-.321, .321):
        box("Printed border", x, -.016, .028, .018, .23, .004, ink)
    box("Wrapper", -.018, -.016, .034, .164, .347, .024, band)
    for x in (-.225, .203):
        box("Ink lines", x, -.032, .028, .095, .019, .004, ink)
        box("Ink lines", x, .025, .028, .095, .019, .004, ink)


def ladle():
    steel = shade("Prosthetic_Metal", "LadleSteel", (.75, .82, .91), 0.43)
    dark = shade("Prosthetic_MetalDark", "LadleHandle", (.43, .52, .63))
    rings("Handle", [(0, y, z) for y, z in [(-.35, .11), (-.28, .11), (.14, .09), (.18, .045)]],
          [(.019, .019), (.031, .024), (.027, .022), (.031, .018)], [dark, steel], [0, 0, 1, 1], sides=6)
    # Outer bottom, outer lip, inner lip and inner bottom make a shallow bowl.
    radii = [(.046, .053), (.104, .113), (.139, .153), (.122, .134), (.045, .055)]
    levels = [(-.022, .335), (.0, .335), (.047, .335), (.049, .335), (.004, .335)]
    rings("Scoop", [(0, y, z) for z, y in levels], radii, [steel], [0] * 5, sides=10, vertical=True)


def photo_frame():
    wood = shade("Heartwood", "FrameWalnut", (.36, .28, .24))
    bevel = shade("Sapwood", "FrameBevel", (.46, .39, .34))
    sky = shade("Prosthetic_Metal", "FrameSky", (.43, .71, .86))
    ridge = shade("LeafGreen", "FrameRidge", (.48, .60, .57))
    meadow = shade("LeafGreen", "FrameMeadow", (.85, .97, .81))
    for x in (-.285, .285):
        box("Stile", x, 0, .250, .057, .058, .50, wood)
        box("Stile edge", x, -.030, .250, .025, .008, .482, bevel)
    for z in (.028, .472):
        box("Rail", 0, 0, z, .52, .058, .056, wood)
        box("Rail edge", 0, -.030, z, .52, .008, .023, bevel)
    box("Photograph", 0, -.014, .25, .518, .030, .396, sky)
    mesh("Distant mountains", [(-.256, -.030, .053), (.257, -.030, .053), (.257, -.030, .208),
         (.159, -.030, .325), (.046, -.030, .216), (-.106, -.030, .363), (-.256, -.030, .196)],
         [(0, 1, 2, 3, 4, 5, 6)], [ridge])
    mesh("Foreground", [(-.256, -.032, .053), (.257, -.032, .053), (.257, -.032, .124),
         (.035, -.032, .184), (-.256, -.032, .144)], [(0, 1, 2, 3, 4)], [meadow])
    mesh("Easel", [(x, y, z) for x in (-.055, .055)
                   for y, z in ((.019, .355), (.181, 0), (.214, 0), (.044, .349))],
         [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)], [wood])


def ballpoint_pen():
    barrel = shade("Prosthetic_Metal", "PenBlue", (.16, .39, .84))
    cap = shade("Prosthetic_MetalDark", "PenCap", (.23, .34, .66))
    clip = shade("Prosthetic_Metal", "PenClip", (.82, .88, .95))
    profile = [(-.371, .008), (-.342, .017), (-.273, .032), (.151, .032),
               (.164, .038), (.298, .038), (.334, .027), (.342, .013)]
    rings("Retractable pen", [(0, y, .038) for y, _ in profile], [(r, r) for _, r in profile],
          [barrel, cap, clip], [2, 2, 0, 2, 1, 1, 1, 1], sides=6)
    box("Pocket clip", 0, .235, .078, .029, .175, .013, clip)
    box("Clip foot", 0, .152, .069, .030, .031, .025, clip)


def alarm_clock():
    case = shade("Heartwood", "ClockRed", (.57, .16, .14))
    brass = shade("Sapwood", "ClockBrass", (.74, .62, .34))
    face = shade("Prosthetic_Metal", "ClockFace", (.72, .72, .57))
    ink = shade("Prosthetic_MetalDark", "ClockInk", (.25, .27, .28))
    rings("Bezel", [(0, y, .29) for y in (-.105, -.088, .046, .089)],
          [(.188, .188), (.225, .225), (.225, .225), (.195, .195)],
          [case, brass], [1, 0, 0, 0], sides=12)
    rings("Dial", [(0, y, .29) for y in (-.108, -.105)], [(.179, .179)] * 2, [face], [0, 0], sides=12)
    for x in (-.145, .145):
        box("Foot", x, .006, .048, .054, .115, .096, brass)
        rings("Bell", [(x, 0, z) for z in (.494, .516, .557, .571)],
              [(.091, .066), (.094, .071), (.071, .055), (.028, .028)], [case, brass], [1, 0, 0, 0],
              sides=6, vertical=True)
    box("Bell striker", 0, 0, .542, .099, .032, .032, brass)

    def dial_mark(angle, start, end, width):
        normal = (math.cos(angle), math.sin(angle))
        tangent = (-normal[1], normal[0])
        points = [(normal[0] * radial + tangent[0] * lateral, -.110,
                   .29 + normal[1] * radial + tangent[1] * lateral)
                  for radial, lateral in ((start, -width), (end, -width), (end, width), (start, width))]
        mesh("Dial mark", points, [(0, 1, 2, 3)], [ink])

    for step in range(12):
        dial_mark(step * math.tau / 12, .139, .159, .005)
    dial_mark(math.radians(154), -.014, .085, .010)
    dial_mark(math.radians(37), -.022, .121, .007)


def drinking_glass():
    teal = shade("Prosthetic_Metal", "GlassTeal", (.28, .66, .67), .35)
    rim = shade("Prosthetic_Metal", "GlassRim", (.66, .87, .86), .32)
    inner = shade("Prosthetic_Metal", "GlassInner", (.34, .54, .56), .46)
    profile = [(0, .103), (.034, .125), (.385, .154), (.402, .149),
               (.402, .126), (.373, .125), (.057, .092)]
    rings("Hollow glass", [(0, 0, z) for z, _ in profile], [(r, r) for _, r in profile],
          [teal, rim, inner], [0, 0, 0, 1, 1, 2, 2], sides=10, vertical=True)


def ceramic_saucer():
    cream = shade("Sapwood", "SaucerIvory", (.53, .57, .59), .51)
    glaze = shade("Prosthetic_Metal", "SaucerGlaze", (.40, .62, .71), .49)
    profile = [(0, .16), (.028, .18), (.074, .342), (.106, .355), (.112, .335),
               (.105, .319), (.091, .285), (.058, .207), (.058, .150), (.051, .138)]
    rings("Turned saucer", [(0, 0, z) for z, _ in profile], [(r, r) for _, r in profile],
          [cream, glaze], [0, 0, 0, 0, 1, 0, 0, 1, 0, 0], sides=12, vertical=True)


def clothes_hanger():
    wood = shade("Heartwood", "HangerWood", (.49, .38, .30))
    steel = shade("Prosthetic_Metal", "HangerHook", (.79, .83, .91), .45)
    shoulder = [(-.364, -.121), (-.346, -.155), (-.303, -.158), (-.139, -.085),
                (0, -.020), (.139, -.085), (.303, -.158), (.346, -.155), (.364, -.121),
                (.333, -.101), (.177, -.030), (.034, .038), (-.034, .038), (-.177, -.030), (-.333, -.101)]
    prism("Shoulder rail", shoulder, 0, .034, wood)
    box("Trouser bar", 0, -.137, .014, .679, .027, .027, wood)
    arc = [(.022 + .065 * math.cos(math.radians(a)), .140 + .065 * math.sin(math.radians(a)))
           for a in (-106, -140, -175, -210, -245, -280, -315, -350)]
    arc = [(0, .024), (0, .070)] + arc
    for (x, y), (nx, ny) in zip(arc, arc[1:]):
        length = math.hypot(nx - x, ny - y)
        dx, dy = -(ny - y) * .011 / length, (nx - x) * .011 / length
        prism("Formed hook", [(x + dx, y + dy), (x - dx, y - dy),
                              (nx - dx, ny - dy), (nx + dx, ny + dy)], .009, .029, steel)


ITEMS = {
    # 212 = the old 32-degree icon roll plus the 180 that seat_grip turned.
    "tool.kitchen_knife": (kitchen_knife, 1.14, 212),
    "tool.baseball_bat": (baseball_bat, 1.08, 0),
    "food.apple": (apple, 1.28, 0),
    "food.banana": (banana, 1.14, 22),
    "item.dollar_bills": (dollar_bills, 1.15, 27),
    "item.kitchen_ladle": (ladle, 1.12, 18),
}

DECOR_ITEMS = {
    "item.photo_frame": (photo_frame, 1.20, 0),
    "item.ballpoint_pen": (ballpoint_pen, 1.10, 26),
    "item.alarm_clock": (alarm_clock, 1.18, 0),
    "item.drinking_glass": (drinking_glass, 1.22, 0),
    "item.ceramic_saucer": (ceramic_saucer, 1.15, 16),
    "item.clothes_hanger": (clothes_hanger, 1.13, 24),
}


def export(item, author):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    palette()
    author()
    parts = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    for part in parts:
        part.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    obj = bpy.context.object
    obj.name = obj.data.name = item
    obj.data.calc_loop_triangles()
    count = len(obj.data.loop_triangles)
    assert 20 <= count <= 400, (item, count)
    bpy.ops.export_scene.fbx(filepath=str(OBJECTS / (item + ".fbx")), use_selection=True,
                             axis_forward="-Z", axis_up="Y", object_types={"MESH"},
                             mesh_smooth_type="FACE", path_mode="STRIP", add_leaf_bones=False,
                             bake_anim=False)
    print(f"MODERN_LOOT {item}: {count} triangles, {tuple(obj.dimensions)}", flush=True)


def render_icons(items):
    sys.path.insert(0, str(ROOT / "Tools"))
    import render_item_icon as icon

    for item, (_, fit, tilt) in items.items():
        source = OBJECTS / (item + ".fbx")
        png = BUILD / (item + ".png")
        meshes = icon.load(str(source))
        icon.tilt(meshes, tilt)
        icon.setup_materials(meshes, "tool")
        render = bpy.context.scene.render
        render.threads_mode, render.threads = "FIXED", 1
        icon.render(str(png), meshes, "tool", fit, 24)
        icon.install(str(png), item)


def main():
    os.chdir(ROOT)
    BUILD.mkdir(parents=True, exist_ok=True)
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    items = DECOR_ITEMS if "--decor" in arguments else ITEMS | DECOR_ITEMS
    selected = [arguments[index + 1] for index, argument in enumerate(arguments) if argument == "--item"]
    if selected:
        items = {item: items[item] for item in selected}
    for item, (author, _, _) in items.items():
        export(item, author)
    if "--icons" in arguments:
        render_icons(items)


if __name__ == "__main__":
    main()
