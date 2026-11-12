"""Author the apartment's portable tools and plush bear.

blender -b -t 1 -P Tools/make_apartment_tools.py -- [--icons] [--item ID]
Ground and hand fit are set by the game. The flashlight marker pair preserves
the authored beam axis through the Blender, FBX and Unity transforms.
"""

import math
import os
from pathlib import Path
import re
import sys
import uuid

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
import bmesh

from make_modern_loot import BUILD, OBJECTS, ROOT, box, mesh, palette, prism, rings, seat_grip, shade
import render_item_icon as icon


OUT = BUILD / "useful"
MODEL_TEMPLATE = OBJECTS / "tool.kitchen_knife.fbx.meta"


def profile(name, outline, low, high, material):
    area = sum(x * ny - nx * y for (x, y), (nx, ny) in zip(outline, outline[1:] + outline[:1]))
    return prism(name, outline if area > 0 else list(reversed(outline)), low, high, material)


def face_along_z():
    # The grip remains on +Y. Convert the side profile's -X working edge to +Z.
    rotation = Matrix.Rotation(math.pi / 2, 4, "Y")
    for part in bpy.context.scene.objects:
        part.data.transform(rotation)


def wedge_head(name, stations, materials, levels):
    """Loft a forged axe head along its length (profile -X = bit).

    Each station is (top, middle, bottom, half-thickness): three side-profile
    points that share one cheek thickness. A real head is a WEDGE — thick at
    the eye, thinning through the cheeks to a ground bevel — which a constant
    prism cannot show; #761 read the old 11%-thick slab as "not a real axe".
    """
    vertices, faces, indices = [], [], []
    for points, half in ((s[:3], s[3]) for s in stations):
        for x, y in points:
            vertices += [(x, y, half), (x, y, -half)]
    for i, level in zip(range(len(stations) - 1), levels):
        a, b = 6 * i, 6 * (i + 1)
        for k in (0, 2):                          # top-middle, middle-bottom
            faces += [(a + k, b + k, b + k + 2, a + k + 2),            # +Z cheek
                      (a + k + 1, a + k + 3, b + k + 3, b + k + 1)]    # -Z cheek
            indices += [level, level]
        faces += [(a, a + 1, b + 1, b), (a + 4, b + 4, b + 5, a + 5)]  # top, bottom
        indices += [level, level]
    last = 6 * (len(stations) - 1)
    faces += [(0, 2, 4, 5, 3, 1), (last, last + 1, last + 3, last + 5, last + 4, last + 2)]
    indices += [levels[0], levels[-1]]
    head = mesh(name, vertices, faces, materials, indices)
    solid = bmesh.new()
    solid.from_mesh(head.data)
    bmesh.ops.recalc_face_normals(solid, faces=solid.faces)  # Unity culls back faces
    solid.to_mesh(head.data)
    solid.free()
    return head


def steel_hatchet():
    # Material creation order is the manifest's slot order: Handle, Grip, Steel, Edge.
    shaft = shade("Heartwood", "HatchetHandle", (.78, .62, .38), .75)
    grip = shade("Bark", "HatchetGrip", (.28, .37, .43))
    steel = shade("Prosthetic_Metal", "HatchetSteel", (.71, .84, 1.06), .48)
    edge = shade("Prosthetic_Metal", "HatchetEdge", (1.32, 1.34, 1.35), .38)
    # Proportions of a ~38 cm forged hatchet, as fractions of its length:
    # oval handle 8 x 6 %, swelling at the butt into a knob that hooks toward
    # the bit; head 36 % from poll to edge, 7 % thick at the eye, 25 % bit.
    # Grip end at the origin, handle along +Y (TOOL_GENERATION_SPEC §6).
    rings("Curved handle",
          [(-.026, 0, 0), (-.024, .022, 0), (-.012, .070, 0), (-.002, .150, 0),
           (.006, .330, 0), (.006, .560, 0), (0, .790, 0), (0, .975, 0)],
          [(.036, .027), (.048, .034), (.039, .029), (.035, .026),
           (.039, .028), (.032, .024), (.036, .026), (.033, .025)],
          [shaft, grip], [1, 1, 1, 1, 0, 0, 0, 0], sides=8)
    wedge_head("Forged head", [
        ((.072, .936), (.077, .876), (.072, .818), .029),       # flat poll
        ((.040, .950), (.040, .876), (.040, .810), .034),       # eye
        ((-.040, .953), (-.040, .874), (-.040, .806), .034),
        ((-.105, .956), (-.108, .868), (-.105, .794), .022),    # cheek, hollow beard line
        ((-.175, .963), (-.183, .862), (-.175, .764), .013),
        ((-.232, .974), (-.250, .859), (-.234, .732), .007),    # bevel shoulder
        ((-.258, .986), (-.291, .858), (-.262, .714), .0015),   # flared convex edge, bearded heel
    ], [steel, edge], [0, 0, 0, 0, 0, 1])
    face_along_z()


def flashlight():
    body = shade("Prosthetic_MetalDark", "FlashlightBody", (.33, .57, .77))
    grip = shade("Prosthetic_MetalDark", "FlashlightGrip", (.14, .22, .27))
    collar = shade("Prosthetic_Metal", "FlashlightCollar", (.48, .70, .84), .44)
    bezel = shade("Prosthetic_MetalDark", "FlashlightBezel", (.29, .30, .32))
    lens = shade("Prosthetic_Metal", "FlashlightLens", (.65, 1.31, 1.42), .36)
    switch = shade("Sapwood", "FlashlightSwitch", (.96, .44, .10))
    y = (-.43, -.409, -.36, -.325, .14, .188, .25, .321, .537, .57)
    sizes = (.072, .097, .097, .085, .085, .103, .117, .145, .145, .125)
    rings("Anodized body", [(0, v, 0) for v in y], [(size, size) for size in sizes],
          [body, grip, collar, bezel, lens], [1, 1, 2, 0, 0, 2, 0, 0, 3, 4], sides=12)
    for v in (-.29, -.228, -.166, -.104, -.042):
        rings("Grip rib", [(0, v - .007, 0), (0, v + .007, 0)], [(.090, .090)] * 2,
              [grip], [0, 0], sides=8)
    box("Push button", 0, .106, .090, .074, .093, .021, switch)


def disposable_lighter():
    plastic = shade("LeafGreen", "DisposableLighterCase", (.23, 1.23, .85), .62)
    guard = shade("Prosthetic_Metal", "DisposableLighterGuard", (1.23, 1.26, 1.29), .42)
    lever = shade("Heartwood", "DisposableLighterLever", (1.28, .15, .10))
    wheel = shade("Prosthetic_MetalDark", "DisposableLighterWheel", (.42, .43, .47))
    profile("Plastic body", [(-.176, -.486), (.176, -.486), (.205, -.428), (.205, .241),
                             (.168, .277), (-.175, .277), (-.204, .239), (-.204, -.427)],
            -.105, .105, plastic)
    profile("Silver flame guard", [(-.194, .238), (.061, .238), (.061, .479), (.036, .509),
                                   (-.149, .509), (-.194, .472)], -.104, .104, guard)
    box("Ignition lever", .131, .332, .005, .131, .157, .170, lever)
    box("Spark wheel", .097, .424, .012, .100, .060, .156, wheel)
    for y in (.313, .370, .427):
        box("Vent", -.111, y, .106, .075, .020, .006, wheel)


def windproof_lighter():
    shell = shade("Prosthetic_Metal", "WindproofLighterBrass", (1.35, .92, .48), .45)
    lid = shade("Prosthetic_Metal", "WindproofLighterLid", (1.17, .85, .47), .54)
    guard = shade("Prosthetic_MetalDark", "WindproofLighterChimney", (.55, .59, .62), .48)
    slots = shade("Prosthetic_MetalDark", "WindproofLighterSlots", (.18, .19, .22))
    profile("Brass case", [(-.256, -.417), (.256, -.417), (.282, -.392), (.282, .254),
                           (-.282, .254), (-.282, -.392)], -.115, .115, shell)
    profile("Open hinged lid", [(-.279, .205), (-.334, .200), (-.564, .349), (-.577, .400),
                                 (-.547, .457), (-.492, .464), (-.269, .303)], -.113, .113, lid)
    box("Hinge", -.288, .254, 0, .060, .072, .233, guard)
    profile("Perforated chimney", [(-.166, .239), (.064, .239), (.064, .515), (.047, .551),
                                    (-.142, .551), (-.166, .520)], -.085, .085, guard)
    box("Spark wheel", .142, .379, 0, .117, .126, .153, slots)
    for x in (-.120, -.040, .040):
        for y in (.338, .432):
            box("Air hole", x, y, .087, .036, .044, .006, slots)


def claw_hammer():
    head = shade("Prosthetic_Metal", "ClawHammerSteel", (.83, .93, 1.14), .48)
    face = shade("Prosthetic_Metal", "ClawHammerFace", (1.32, 1.38, 1.39), .39)
    shaft = shade("Sapwood", "ClawHammerHandle", (.57, .45, .31), .73)
    grip = shade("Bark", "ClawHammerGrip", (.31, .37, .42))
    # #754: 13 % wide read as a club; a claw hammer handle is ~10 x 7.5 %.
    slim = .8
    rings("Fiberglass handle",
          [(0, -.239, 0), (0, -.220, 0), (0, .103, 0), (0, .129, 0), (0, .660, 0)],
          [(w * slim, d * slim) for w, d in
           ((.049, .037), (.062, .042), (.064, .042), (.053, .037), (.036, .033))],
          [shaft, grip], [1, 1, 1, 0, 0], sides=8)
    profile("Hammer head", [(-.173, .559), (-.088, .566), (-.035, .584), (.072, .560),
                           (.187, .600), (.209, .672), (.166, .713), (-.079, .719),
                           (-.133, .729), (-.184, .726)], -.067, .067, head)
    profile("Impact face", [(-.243, .560), (-.178, .550), (-.163, .568), (-.164, .718),
                            (-.179, .736), (-.242, .726)], -.084, .084, face)
    for near, far in ((-.070, -.013), (.013, .070)):
        profile("Curved claw", [(.065, .578), (.149, .560), (.245, .510), (.277, .466),
                                (.288, .477), (.258, .542), (.185, .619), (.104, .655)],
                near, far, head)
    face_along_z()
    # #754: grip end at the pivot, like tool.hammer; the hand pose holds it there.
    seat_grip(-.239)


def folding_saw():
    steel = shade("Prosthetic_Metal", "FoldingSawBlade", (.91, .97, 1.06), .48)
    grip = shade("Sapwood", "FoldingSawHandle", (1.24, .48, .12))
    insert = shade("Prosthetic_MetalDark", "FoldingSawGrip", (.27, .31, .33))
    pin = shade("Prosthetic_Metal", "FoldingSawPin", (1.13, 1.18, 1.19), .46)
    teeth = [(-.105, .084)]
    for i in range(12):
        y = .113 + .042 * i
        teeth.extend([(-.106 + i * .0023, y), (-.141 + i * .0023, y + .027)])
    profile("Toothed blade", teeth + [(-.060, .649), (.004, .636), (.034, .608), (.044, .075)],
            -.020, .020, steel)
    profile("Safety orange grip", [(-.061, -.428), (.044, -.423), (.082, -.379), (.074, .046),
                                  (.053, .108), (-.068, .112), (-.118, .062), (-.110, -.377)],
            -.052, .052, grip)
    profile("Recessed grip insert", [(-.075, -.371), (.047, -.362), (.050, -.066),
                                     (.028, -.040), (-.069, -.047), (-.086, -.077)],
            .051, .058, insert)
    rings("Pivot rivet", [(-.023, .057, -.058), (-.023, .057, .058)], [(.033, .033)] * 2,
          [pin], [0, 0], sides=8, vertical=True)
    face_along_z()


def teddy_bear():
    fur = shade("Heartwood", "TeddyFur", (1.02, .47, .30))
    felt = shade("Sapwood", "TeddyFelt", (1.04, .76, .57))
    stitching = shade("Bark", "TeddyStitching", (.29, .30, .34))
    heart = shade("Heartwood", "TeddyHeart", (1.58, .12, .26))

    def plush(name, center, size, finish, shape="body"):
        x, y, z = center
        w, d, h = size
        tiers = [(-1., .26), (-.58, .82), (.12, 1.), (.70, .74), (1., .24)]
        sides = 7
        if shape == "limb":
            tiers = [(-1., .28), (0., 1.), (1., .29)]
            sides = 5
        rings(name, [(x, y, z + h * height) for height, _ in tiers],
              [(w * f, d * f) for _, f in tiers], [finish], [0] * len(tiers), sides=sides, vertical=True)

    def felt_patch(name, center, width, height, finish):
        x, y, z = center
        vertices = [(x + width * math.cos(math.tau * i / 7), y, z + height * math.sin(math.tau * i / 7))
                    for i in range(7)]
        mesh(name, vertices, [tuple(range(7))], [finish])

    plush("Seated body", (0, .027, .329), (.251, .191, .260), fur)
    plush("Round head", (0, -.016, .670), (.229, .182, .199), fur)
    for side in (-1, 1):
        plush("Rounded ear", (side * .162, .010, .805), (.088, .065, .087), fur, "limb")
        felt_patch("Felt ear lining", (side * .166, -.050, .822), .046, .045, felt)
        plush("Paw", (side * .207, -.116, .365), (.076, .121, .118), fur, "limb")
        plush("Foot", (side * .128, -.096, .103), (.101, .152, .101), fur, "limb")
        felt_patch("Stitched eye", (side * .083, -.184, .710), .018, .021, stitching)
    plush("Soft muzzle", (0, -.167, .627), (.099, .064, .055), felt, "limb")
    felt_patch("Stitched nose", (0, -.221, .650), .025, .014, stitching)
    # A soft heart points down and sits between both forepaws.
    patch = profile("Heart cushion", [(0, .225), (.118, .313), (.165, .383), (.156, .438),
                                     (.110, .477), (.051, .468), (0, .421), (-.051, .468),
                                     (-.110, .477), (-.156, .438), (-.165, .383), (-.118, .313)],
                    .168, .235, heart)
    patch.data.transform(Matrix.Rotation(math.pi / 2, 4, "X"))


ITEMS = {
    "tool.steel_hatchet": (steel_hatchet, 1.10, (.435, (1.02, -.82, .41))),
    "tool.flashlight": (flashlight, 1.10, (.24, (.74, 1., .46))),
    "tool.disposable_lighter": (disposable_lighter, 1.13, (.085, (.64, 1., .64))),
    "tool.windproof_lighter": (windproof_lighter, 1.29, (.080, (.70, 1., .63))),
    "tool.claw_hammer": (claw_hammer, 1.10, (.34, (1.02, -.82, .41))),
    "tool.folding_saw": (folding_saw, 1.10, (.46, (1.07, -.88, .36))),
    "item.teddy_bear": (teddy_bear, 1.20, (.24, (.45, -1., .36))),
}


def export(item, author):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    palette()
    author()
    meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    for part in meshes:
        part.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.join()
    model = bpy.context.object
    model.name = model.data.name = item
    model.data.calc_loop_triangles()
    triangles = len(model.data.loop_triangles)
    if not 20 <= triangles <= 400:
        raise ValueError(f"{item} has {triangles} triangles")

    if item == "tool.flashlight":
        for name, y in (("FlashlightLens", .57), ("FlashlightTail", -.43)):
            marker = bpy.data.objects.new(name, None)
            bpy.context.collection.objects.link(marker)
            marker.parent = model
            marker.location.y = y
            marker.select_set(True)

    path = OBJECTS / (item + ".fbx")
    bpy.ops.export_scene.fbx(filepath=str(path), use_selection=True, axis_forward="-Z", axis_up="Y",
                             object_types={"MESH", "EMPTY"}, mesh_smooth_type="FACE",
                             path_mode="STRIP", add_leaf_bones=False, bake_anim=False)
    meta = Path(str(path) + ".meta")
    if not meta.exists():
        template = MODEL_TEMPLATE.read_text(encoding="utf-8")
        guid = uuid.uuid5(uuid.NAMESPACE_URL, "hexlive:" + path.relative_to(ROOT).as_posix()).hex
        template = re.sub(r"^guid: [0-9a-f]{32}$", "guid: " + guid, template, flags=re.M)
        if item == "tool.flashlight":
            template = template.replace("preserveHierarchy: 0", "preserveHierarchy: 1")
        meta.write_text(template, encoding="utf-8", newline="\n")
    materials = [material.name for material in model.data.materials]
    print(f"APARTMENT_TOOL {item}: {triangles} triangles, {tuple(model.dimensions)}, {materials}", flush=True)


def render_icons(items):
    original = icon.STYLES["tool"]
    for item, (_, fit, (_, view)) in items.items():
        meshes = icon.load(str(OBJECTS / (item + ".fbx")))
        icon.STYLES["tool"] = (Vector(view), *original[1:])
        icon.setup_materials(meshes, "tool")
        render = bpy.context.scene.render
        render.threads_mode, render.threads = "FIXED", 1
        png = OUT / (item + ".png")
        icon.render(str(png), meshes, "tool", fit, 16)
        icon.install(str(png), item)
    icon.STYLES["tool"] = original


def main():
    os.chdir(ROOT)
    OUT.mkdir(parents=True, exist_ok=True)
    arguments = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    selected = [arguments[i + 1] for i, flag in enumerate(arguments) if flag == "--item"]
    items = {item: ITEMS[item] for item in selected} if selected else ITEMS
    for item, (author, _, _) in items.items():
        export(item, author)
    if "--icons" in arguments:
        render_icons(items)


if __name__ == "__main__":
    main()
