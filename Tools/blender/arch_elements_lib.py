"""Deterministic authoring of the §120 architecture LEGO elements.

Run `build_arch_elements.py` inside the open HexLive building kit; this module
holds the geometry. Everything is generated from fixed seeds, so the collection
can be rebuilt byte-for-byte after a Blender crash, and the art is reviewable as
code instead of as hand-moved objects.

Element contract (see Spec/120.md and export_arch_elements.py):
  * BuildStage_1/2/3 empties; every direct mesh child of a stage is ONE
    delivered resource — sticks, then boards, then rope.
  * Section length runs along +Y, Z is up, the origin is the section centre on
    the terrain plane. The deck top of a floor sector is +0.107475 wu.
  * The boundary motif is shared by wall, window and door: a pair of thin bowed
    sticks front and back, overlapping at mid height, sitting EXACTLY on the
    0.5-wu section seam so the vertical joint between neighbouring sections is
    covered. Boards deliberately keep their small horizontal seams — those read
    as hewn planking — but they span the full section so no vertical slot opens.
"""

import math
import random

import bmesh
import bpy
from mathutils import Euler, Vector as V

COLLECTION = "HL_ARCH_ELEMENTS"

SECTION = 0.5                 # one wall section, §120 build lattice
SEAM = SECTION * 0.5          # boundary sticks live on the joint
SLAB_LENGTH = SECTION + 0.006  # boards overlap the joint a hair
TOTAL_HEIGHT = 2.30
SPLICE_OVERLAP = 0.40
STICK_R = 0.036
STICK_X = 0.082               # front/back offset of the pair
DECK_TOP = 0.107475           # HutAssembly.FloorSurfaceLift
TAN30 = 0.5773502691896257

PALETTE = {
    "ARCH_Sapwood": (0.720, 0.490, 0.235),
    "ARCH_SapwoodLight": (0.840, 0.630, 0.340),
    "ARCH_Heartwood": (0.560, 0.300, 0.110),
    "ARCH_Bark": (0.300, 0.170, 0.075),
    "ARCH_BarkLight": (0.430, 0.270, 0.130),
    "ARCH_Rope": (0.740, 0.620, 0.390),
    # Hearth palette, matched to the outdoor campfire so both fires read as the
    # same colony craft (HearthStone / HearthStoneLight / HL_Hearth_Ash / Ember).
    "ARCH_Stone": (0.300, 0.275, 0.235),
    "ARCH_StoneLight": (0.430, 0.390, 0.320),
    "ARCH_Ash": (0.105, 0.090, 0.074),
    "ARCH_Ember": (0.400, 0.025, 0.006),
    "ARCH_Charcoal": (0.090, 0.075, 0.062),
}

EMISSIVE = {"ARCH_Ember"}


# --------------------------------------------------------------------------- #
# scene plumbing
# --------------------------------------------------------------------------- #

def ensure_materials():
    for name, (r, g, b) in PALETTE.items():
        material = bpy.data.materials.get(name) or bpy.data.materials.new(name)
        material.use_nodes = True
        bsdf = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Base Color"].default_value = (r, g, b, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.85
        if "Specular IOR Level" in bsdf.inputs:
            bsdf.inputs["Specular IOR Level"].default_value = 0.1
        if name in EMISSIVE and "Emission Color" in bsdf.inputs:
            bsdf.inputs["Emission Color"].default_value = (r, g, b, 1.0)
            bsdf.inputs["Emission Strength"].default_value = 1.6


def mat(name):
    return bpy.data.materials[name]


def ensure_collection():
    collection = bpy.data.collections.get(COLLECTION)
    if collection is None:
        collection = bpy.data.collections.new(COLLECTION)
    scene = bpy.context.scene
    if collection.name not in [c.name for c in scene.collection.children]:
        scene.collection.children.link(collection)
    return collection


def clear_collection():
    collection = ensure_collection()
    for obj in list(collection.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    return collection


def new_obj(name, mesh, parent=None, loc=(0, 0, 0), rot=(0, 0, 0)):
    obj = bpy.data.objects.new(name, mesh)
    ensure_collection().objects.link(obj)
    if parent:
        obj.parent = parent
    obj.location = loc
    obj.rotation_mode = "XYZ"
    obj.rotation_euler = Euler(rot)
    return obj


def new_empty(name, parent=None, loc=(0, 0, 0), rot=(0, 0, 0)):
    empty = bpy.data.objects.new(name, None)
    empty.empty_display_size = 0.12
    ensure_collection().objects.link(empty)
    if parent:
        empty.parent = parent
    empty.location = loc
    empty.rotation_mode = "XYZ"
    empty.rotation_euler = Euler(rot)
    return empty


def element_root(name, x):
    root = new_empty(name, loc=(x, 0, 0))
    stages = [new_empty(f"BuildStage_{i}", root) for i in (1, 2, 3)]
    return root, stages


def join(objects, name):
    """Merge several meshes into ONE delivered resource."""
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    merged = objects[0]
    merged.name = name
    merged.data.name = name
    return merged


# --------------------------------------------------------------------------- #
# primitives
# --------------------------------------------------------------------------- #

def bowed_stick(name, length, radius, rng, bow=0.008, facets=7):
    """Faceted log along +Z with a gentle bow; end caps show the heartwood."""
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    offset = rng.uniform(0, math.pi)
    rings = []
    for z, bx, scale in ((0.0, 0.0, 1.0),
                         (length * 0.5, bow, rng.uniform(0.96, 1.0)),
                         (length, 0.0, rng.uniform(0.84, 0.92))):
        ring = []
        for k in range(facets):
            a = offset + 2 * math.pi * k / facets
            r = radius * scale * rng.uniform(0.92, 1.06)
            ring.append(bm.verts.new((math.cos(a) * r + bx, math.sin(a) * r, z)))
        rings.append(ring)
    for seg in range(len(rings) - 1):
        lower, upper = rings[seg], rings[seg + 1]
        for a in range(facets):
            b = (a + 1) % facets
            bm.faces.new((lower[a], lower[b], upper[b], upper[a]))
    for ring, flip in ((rings[0], True), (rings[-1], False)):
        face = bm.faces.new(ring if flip else list(reversed(ring)))
        face.material_index = 1
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(mat("ARCH_BarkLight"))
    mesh.materials.append(mat("ARCH_Heartwood"))
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def plank_slab(name, length, height, thick, rng, tones, planks=2, overlap=0.013):
    """One board RESOURCE: `planks` hewn boards stacked in Z, SHIPLAPPED — each
    board overlaps the one below and is a touch thinner, so the seam still reads
    as a dark step line but daylight cannot come through it. Length runs along
    Y, origin at the slab bottom centre; the board spans the whole section, so
    no vertical slot opens at the joint either."""
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    materials = []
    indices = {}

    def material_index(material_name):
        if material_name not in indices:
            indices[material_name] = len(materials)
            materials.append(mat(material_name))
        return indices[material_name]

    plank_height = (height + overlap * (planks - 1)) / planks
    chamfer = min(0.030, plank_height * 0.22)
    for index in range(planks):
        z0 = index * (plank_height - overlap)
        tone = material_index(tones[index % len(tones)])
        heart = material_index("ARCH_Heartwood")
        # Alternating thickness makes the lap read as a shadowed step line
        # instead of a hole: the seam stays visible, daylight does not.
        half = thick * 0.5 * (1.0 if index % 2 == 0 else 0.82)
        profile = [(-half + 0.012, z0), (-half, z0 + chamfer),
                   (-half, z0 + plank_height - chamfer), (-half + 0.012, z0 + plank_height),
                   (half - 0.012, z0 + plank_height), (half, z0 + plank_height - chamfer),
                   (half, z0 + chamfer), (half - 0.012, z0)]
        rings = []
        for side, y in enumerate((-length * 0.5, length * 0.5)):
            scale = 1.0 if side == 0 else rng.uniform(0.985, 1.0)
            jitter_x = rng.uniform(-0.005, 0.005)
            jitter_z = rng.uniform(-0.004, 0.004)
            centre = z0 + plank_height * 0.5
            rings.append([bm.verts.new((x * scale + jitter_x, y,
                                        (z - centre) * scale + centre + jitter_z))
                          for x, z in profile])
        count = len(profile)
        for a in range(count):
            b = (a + 1) % count
            face = bm.faces.new((rings[0][a], rings[0][b], rings[1][b], rings[1][a]))
            face.material_index = tone
        for ring, flip in ((rings[0], True), (rings[1], False)):
            face = bm.faces.new(ring if flip else list(reversed(ring)))
            face.material_index = heart
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    for material in materials:
        mesh.materials.append(material)
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def pair_lashing(name, rng, half_x, half_y, sep_axis="x", turns=3,
                 tube=0.0072, frap=True, clear=0.004):
    """Lashing authored in its FINAL orientation for a pair of VERTICAL sticks:
    turns lie in the X-Y plane and stack along Z, so the rope really goes around
    the sticks. half_x/half_y come from the measured stick bundle at that height
    (see fit_rope), never from a guess. `frap` adds the perpendicular frapping
    turns that cinch the wrap between the two sticks."""
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    rx = half_x + tube + clear
    ry = half_y + tube + clear
    pitch = tube * 2.5
    stack_half = (turns - 1) * pitch * 0.5

    def tube_loop(points, radius):
        previous = None
        first = None
        count = len(points)
        for k in range(count):
            p0, p1 = points[k], points[(k + 1) % count]
            d = (p1 - p0).normalized()
            up = V((0, 0, 1)) if abs(d.z) < 0.9 else V((1, 0, 0))
            side = d.cross(up).normalized()
            upward = side.cross(d).normalized()
            quad = [bm.verts.new(p0 + side * radius + upward * radius),
                    bm.verts.new(p0 - side * radius + upward * radius),
                    bm.verts.new(p0 - side * radius - upward * radius),
                    bm.verts.new(p0 + side * radius - upward * radius)]
            if previous:
                for e in range(4):
                    bm.faces.new((previous[e], previous[(e + 1) % 4],
                                  quad[(e + 1) % 4], quad[e]))
            else:
                first = quad
            previous = quad
        for e in range(4):
            bm.faces.new((previous[e], previous[(e + 1) % 4], first[(e + 1) % 4], first[e]))

    segments = 14
    for turn in range(turns):
        z0 = -stack_half + turn * pitch
        points = []
        for k in range(segments):
            a = 2 * math.pi * k / segments
            jitter = rng.uniform(-0.0007, 0.0007)
            points.append(V((math.cos(a) * (rx + jitter),
                             math.sin(a) * (ry + jitter),
                             z0 + (a / (2 * math.pi)) * pitch * 0.55)))
        tube_loop(points, tube)

    if frap:
        for side in (-1, 1):
            points = []
            for k in range(12):
                a = 2 * math.pi * k / 12
                if sep_axis == "x":
                    points.append(V((side * tube * 1.35,
                                     math.cos(a) * (ry + tube * 1.25),
                                     math.sin(a) * (stack_half + tube * 2.0))))
                else:
                    points.append(V((math.cos(a) * (rx + tube * 1.25),
                                     side * tube * 1.35,
                                     math.sin(a) * (stack_half + tube * 2.0))))
            tube_loop(points, tube * 0.85)

    knot = bmesh.ops.create_icosphere(bm, subdivisions=1, radius=tube * 1.6)
    bmesh.ops.translate(bm, verts=knot["verts"], vec=(
        rx * 0.62 if sep_axis == "y" else 0.0,
        ry * 0.62 if sep_axis == "x" else 0.0,
        stack_half + tube * 1.2))
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(mat("ARCH_Rope"))
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def faceted_stone(name, rng, radius, flatten=0.62, material="ARCH_Stone"):
    """A colony stone: low-poly boulder with jittered facets, flat shaded.
    Same read as the outdoor campfire ring, one stone per delivered resource."""
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=1, radius=radius)
    for vert in bm.verts:
        vert.co.x *= rng.uniform(0.82, 1.18)
        vert.co.y *= rng.uniform(0.82, 1.18)
        vert.co.z *= flatten * rng.uniform(0.86, 1.14)
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(mat(material))
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def disc(name, radius, height, sides, material, rng=None, dip=0.0):
    """Flat-shaded ash bed / coal pan; `dip` sinks the centre into a bowl."""
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    rim = []
    for k in range(sides):
        a = 2 * math.pi * k / sides
        jitter = rng.uniform(0.94, 1.06) if rng else 1.0
        rim.append(bm.verts.new((math.cos(a) * radius * jitter,
                                 math.sin(a) * radius * jitter, height)))
    centre_top = bm.verts.new((0, 0, height - dip))
    floor = bm.verts.new((0, 0, 0.0))
    for k in range(sides):
        b = (k + 1) % sides
        bm.faces.new((rim[k], rim[b], centre_top))
        bm.faces.new((rim[b], rim[k], floor))
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(mat(material))
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def axis_lashing(name, rng, axis_radius, squash=1.0, loops=3, tube=0.0062):
    """Slim wrap around a single horizontal joist (axis along X)."""
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    radius = axis_radius + tube + 0.004
    for index in range(loops):
        x0 = (index - (loops - 1) / 2) * tube * 2.4
        points = []
        for k in range(12):
            a = 2 * math.pi * k / 12
            jitter = rng.uniform(-0.0008, 0.0008)
            points.append(V((x0, math.cos(a) * (radius + jitter),
                             math.sin(a) * (radius + jitter) * squash)))
        previous = None
        first = None
        for k in range(12):
            p0, p1 = points[k], points[(k + 1) % 12]
            d = (p1 - p0).normalized()
            side = d.cross(V((1, 0, 0))).normalized()
            upward = side.cross(d).normalized()
            quad = [bm.verts.new(p0 + side * tube + upward * tube),
                    bm.verts.new(p0 - side * tube + upward * tube),
                    bm.verts.new(p0 - side * tube - upward * tube),
                    bm.verts.new(p0 + side * tube - upward * tube)]
            if previous:
                for e in range(4):
                    bm.faces.new((previous[e], previous[(e + 1) % 4],
                                  quad[(e + 1) % 4], quad[e]))
            else:
                first = quad
            previous = quad
        for e in range(4):
            bm.faces.new((previous[e], previous[(e + 1) % 4], first[(e + 1) % 4], first[e]))
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(mat("ARCH_Rope"))
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


# --------------------------------------------------------------------------- #
# measuring, so the rope is fitted to the sticks that exist
# --------------------------------------------------------------------------- #

def _stick_axis(obj):
    matrix = obj.matrix_world
    box = [V(c) for c in obj.bound_box]
    length_local = max(v.z for v in box) - min(v.z for v in box)
    radius = max((max(v.x for v in box) - min(v.x for v in box)) * 0.5 * obj.scale.x,
                 (max(v.y for v in box) - min(v.y for v in box)) * 0.5 * obj.scale.y)
    direction = (matrix.to_3x3() @ V((0, 0, 1))).normalized()
    start = matrix @ V((0, 0, min(v.z for v in box)))
    return start, direction, length_local * obj.scale.z, radius


def stick_section(root, z, name_filter="_stick_"):
    """Centre and half-extents of the stick bundle at height z, in root space."""
    stage = next(c for c in root.children if "BuildStage_1" in c.name)
    ox, oy, oz = root.location
    lo = [1e9] * 3
    hi = [-1e9] * 3
    hits = 0
    for obj in [c for c in stage.children if c.type == "MESH" and name_filter in c.name]:
        start, direction, length, radius = _stick_axis(obj)
        if abs(direction.z) < 1e-6:
            continue
        t = (z + oz - start.z) / direction.z
        if t < -0.02 or t > length + 0.02:
            continue
        point = start + direction * t
        for k in range(3):
            lo[k] = min(lo[k], point[k] - radius)
            hi[k] = max(hi[k], point[k] + radius)
        hits += 1
    if hits == 0:
        return None
    return ((lo[0] + hi[0]) * 0.5 - ox, (lo[1] + hi[1]) * 0.5 - oy,
            (hi[0] - lo[0]) * 0.5, (hi[1] - lo[1]) * 0.5, hits)


def fit_rope(root, prefix, seed, plan, sep_axis="x"):
    """Build the single rope resource of an element from measured sections.

    The update below is load-bearing: objects created earlier in the same run
    still carry a stale matrix_world, and measuring that reports the sticks as
    absent (or in the wrong place), which silently moves the rope off them."""
    bpy.context.view_layer.update()
    rng = random.Random(seed)
    stage = next(c for c in root.children if "BuildStage_3" in c.name)
    for child in list(stage.children):
        bpy.data.objects.remove(child, do_unlink=True)
    parts = []
    report = []
    for index, (z, turns, tilt, frap) in enumerate(plan):
        section = stick_section(root, z)
        if section is None:
            raise RuntimeError(f"{prefix}: no sticks at z={z}")
        cx, cy, half_x, half_y, count = section
        mesh = pair_lashing(f"{prefix}_bind_{index}", rng, half_x, half_y,
                            sep_axis=sep_axis, turns=turns, frap=frap)
        parts.append(new_obj(f"{prefix}_bind_{index}", mesh, stage,
                             loc=(cx, cy, z), rot=(math.radians(tilt), 0, 0)))
        report.append((z, cx, cy, half_x, half_y, count))
    join(parts, f"{prefix}_rope")
    return report
