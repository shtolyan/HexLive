"""Author the whole skinned wolf, its roasted variant, and their inventory icons.

From the repository root: blender -b -t 4 --python Tools/blender/author_wolf_carcass.py
"""

from pathlib import Path
import sys
from math import cos, pi, sin

import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from author_animal_loot import load_palette
from author_atomic_world_props import collection, material
from author_modern_apartment import OBJECTS, ROOT, export_model, verify_export, write_unity_material

sys.path.insert(0, str(ROOT / "Tools"))
from render_item_icon import STYLES, install, render, setup_materials

STATES = {
    "resource.wolf_carcass_raw": (
        "WolfCarcassRaw", (
            ("Muscle", (.43, .118, .100), "Bark", .87),
            ("Flank", (.34, .075, .068), "Bark", .88),
            ("Fascia", (.53, .292, .211), "Sapwood", .86),
            ("Cut", (.285, .060, .058), "Bark", .91),
        ),
    ),
    "resource.wolf_carcass_cooked": (
        "WolfCarcassCooked", (
            ("Roast", (.425, .136, .040), "Bark", .86),
            ("Crust", (.175, .046, .016), "Bark", .93),
            ("Fat", (.545, .290, .105), "Sapwood", .81),
            ("Ends", (.30, .086, .033), "Bark", .89),
        ),
    ),
}

# X runs head-to-tail through the spit. Z is up; the tucked hocks rest on Z=0.
# Each body station describes centre X, centre Z, half width and half height.
TORSO = (
    (-.615, .207, .060, .065),  # Dressed neck
    (-.552, .215, .071, .078),
    (-.433, .208, .119, .118),  # Shoulders
    (-.308, .193, .176, .157),  # Deep ribcage
    (-.157, .199, .169, .151),
    (.017, .208, .124, .127),
    (.151, .216, .101, .106),  # Tucked waist
    (.254, .211, .114, .113),
    (.366, .191, .151, .136),  # Hindquarter
    (.471, .189, .118, .105),
    (.539, .190, .071, .069),
    (.581, .194, .031, .036),
)

# Near and far limbs use the same anatomy with slightly different poses. The
# first ring is inside its joint; the foot ends in a small, closed dressing cut.
FORELEG = (
    (-.367, .117, .216, .073, .077),
    (-.329, .182, .150, .079, .077),
    (-.246, .184, .066, .062, .066),
    (-.318, .181, .045, .039, .039),
    (-.410, .152, .031, .023, .022),
)
HINDLEG = (
    (.364, .103, .203, .104, .098),
    (.325, .192, .135, .094, .094),
    (.253, .168, .056, .061, .056),
    (.341, .177, .031, .034, .028),
    (.454, .165, .021, .022, .021),
)


def carcass_materials(prefix, entries, palette):
    colours = {}
    for key, rgb, island_material, roughness in entries:
        base = palette[island_material]
        colour = tuple(.88 * channel + .12 * shared for channel, shared in zip(rgb, base))
        colours[prefix + key] = (colour, roughness)
    return colours


def add_surface(vertices, faces, materials, rings, choose_material):
    """Bridge closed, consistently wound rings and cap both ends."""
    first = len(vertices)
    sides = len(rings[0])
    for ring in rings:
        vertices.extend(ring)
    faces.append(tuple(first + side for side in reversed(range(sides))))
    materials.append(3)
    for station in range(len(rings) - 1):
        for side in range(sides):
            following = (side + 1) % sides
            low = first + station * sides
            high = low + sides
            a, b, c, d = low + side, low + following, high + following, high + side
            if (station + side) % 2:
                faces.extend(((a, b, d), (b, c, d)))
            else:
                faces.extend(((a, b, c), (a, c, d)))
            materials.extend(choose_material(station, side))
    last = first + (len(rings) - 1) * sides
    faces.append(tuple(last + side for side in range(sides)))
    materials.append(3)


def torso_rings():
    rings = []
    for station, (x, z, width, height) in enumerate(TORSO):
        ring = []
        for side in range(8):
            angle = 2 * pi * side / 8
            # A small shear along the ribs produces broad irregular facets
            # without a visible sequence of geometric bands across the roast.
            shear = .014 * sin(station * 1.77 + side * .90)
            ring.append((x + shear, width * cos(angle), z + height * sin(angle)))
        rings.append(ring)
    return rings


def torso_material(station, side):
    if side in (5, 6) or (side in (4, 7) and station in (3, 5, 7)):
        return (2, 2 if station % 3 else 0)
    if side in (1, 2) and station in (2, 4, 8, 9):
        return (1, 0)
    return (0, 1 if (station + 2 * side) % 5 == 0 else 0)


def limb_rings(stations, side):
    points = [Vector((x - (.026 if side < 0 else 0), side * y, z))
              for x, y, z, _, _ in stations]
    rings = []
    for station, centre in enumerate(points):
        before = points[max(station - 1, 0)]
        after = points[min(station + 1, len(points) - 1)]
        axis = (after - before).normalized()
        across = Vector((0, side, 0)).cross(axis).normalized()
        other = axis.cross(across)
        width, depth = stations[station][3:]
        ring = []
        for corner in range(5):
            angle = 2 * pi * corner / 5
            ring.append(centre + across * (width * cos(angle)) + other * (depth * sin(angle)))
        rings.append(ring)
    return rings


def limb_material(station, side):
    if station == 3:
        return (2, 1 if side % 3 else 2)
    return (0, 1 if (station + side) % 4 == 0 else 0)


def make_carcass(identifier, colours):
    vertices, faces, slots = [], [], []
    add_surface(vertices, faces, slots, torso_rings(), torso_material)
    for side in (-1, 1):
        for limb in (FORELEG, HINDLEG):
            add_surface(vertices, faces, slots, limb_rings(limb, side), limb_material)

    # Centre the horizontal footprint and put the lowest hock on the ground.
    centre_x = (min(v[0] for v in vertices) + max(v[0] for v in vertices)) / 2
    centre_y = (min(v[1] for v in vertices) + max(v[1] for v in vertices)) / 2
    floor = min(v[2] for v in vertices)
    vertices = [(x - centre_x, y - centre_y, z - floor) for x, y, z in vertices]

    mesh = bpy.data.meshes.new(identifier)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    for name, (rgb, roughness) in colours.items():
        mesh.materials.append(material(name, (*rgb, 1), roughness))
    for face, slot in zip(mesh.polygons, slots, strict=True):
        face.material_index = slot

    owner = collection(identifier)
    owner.objects.link(bpy.data.objects.new(identifier + "_body", mesh))
    return owner


def render_icon(identifier, meshes, output):
    target = output / (identifier + ".png")
    original = STYLES["tool"]
    STYLES["tool"] = (Vector((-.21, -.56, .88)), *original[1:])
    try:
        setup_materials(meshes, "tool")
        render(str(target), meshes, "tool", 1.16, 24)
        install(str(target), identifier)
    finally:
        STYLES["tool"] = original


def main():
    output = ROOT / "Build/Diagnostics/wolf-carcass"
    output.mkdir(parents=True, exist_ok=True)
    rows = []
    for identifier, (prefix, materials) in STATES.items():
        bpy.ops.wm.read_factory_settings(use_empty=True)
        bpy.context.scene.unit_settings.system = "METRIC"
        palette = load_palette(ROOT / "Assets/HexLiveContent/source.blend")
        colours = carcass_materials(prefix, materials, palette)
        owner = make_carcass(identifier, colours)
        for name in colours:
            write_unity_material(name, directory=OBJECTS, palette=colours)
        row = export_model(identifier, owner, "carryable", True, material_directory=OBJECTS, origin="primal")
        if row["triangles"] > 400:
            raise ValueError(f"{identifier} exceeded the portable prop triangle budget")
        meshes = verify_export(row, 0, 0)
        render_icon(identifier, meshes, output)
        rows.append(row)
    if rows[0]["blenderBounds"] != rows[1]["blenderBounds"] or rows[0]["triangles"] != rows[1]["triangles"]:
        raise ValueError("Cooking must preserve the carcass fit and spit pose")


if __name__ == "__main__":
    main()
