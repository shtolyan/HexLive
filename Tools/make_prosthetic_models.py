"""Build HexLive's prosthetic set inside the open Blender scene.

The wooden arm and leg are carved anatomical lofts (calf, ankle, foot with
toes; forearm, palm, fingers) with leather/rope/iron details (bug #740); the
mechanical set keeps the modular rails/pistons construction. Everything is
flat-shaded with separate material blocks, no textures.  Every limb is authored along local +Y with its joint
at Y=0 and organic hand/foot endpoint at Y=1, allowing Unity to fit one asset to
any actor by scaling it to the live bone-to-bone distance.

Run from the project checkout, on top of the committed source .blend (it
already holds the Marta reference, so --reference is only needed without it):
    blender -b Assets/ArtSource/Prosthetics/hexlive_prosthetics.blend \
        -P Tools/make_prosthetic_models.py -- --preview Build/ProstheticFit.png
"""

from __future__ import annotations

import argparse
import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix, Vector


PROJECT_ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--reference", type=Path)
parser.add_argument("--preview", type=Path, default=PROJECT_ROOT / "Build/ProstheticFit.png")
args = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
PREVIEW_PATH = args.preview.resolve()
EXPORT_ROOT = PROJECT_ROOT / "Assets/HexLiveContent/Prosthetics"
SOURCE_ROOT = PROJECT_ROOT / "Assets/ArtSource/Prosthetics"
COLLECTION_NAME = "PROSTHETIC_EXPORT"
REFERENCE_COLLECTION_NAME = "PROSTHETIC_REFERENCE"
WOOD_PREVIEW_COLLECTION_NAME = "PROSTHETIC_FIT_WOOD"
MECHANICAL_PREVIEW_COLLECTION_NAME = "PROSTHETIC_FIT_MECHANICAL"
FIT_SCENE_NAME = "ProstheticFit"
MARTA_FBX = args.reference or PROJECT_ROOT / "Assets/ImportedActors/Actors/Marta/Marta.new.fbx"

# Measured from Marta.new.fbx, rest pose. The exported assets remain normalised
# joint-to-end = 1; these numbers fix the *cross-section and distal overhang*
# which uniform runtime scaling cannot infer.
MARTA_ARM_LENGTH_M = 0.263512  # r/lForearmBend -> r/lHand (wrist)
MARTA_LEG_LENGTH_M = 0.435129  # r/lShin -> r/lFoot (ankle)
ARM_HAND_OVERHANG = 0.66       # Marta hand is 0.1825 m = 0.692 of forearm
ARM_HAND_WIDTH = 0.44          # Marta hand incl. thumb is 0.1255 m = 0.476
ARM_HAND_THICKNESS = 0.19      # Marta hand is 0.0620 m = 0.235
LEG_FOOT_LENGTH = 0.55         # Marta foot is 0.2396 m = 0.551 of shin
LEG_FOOT_WIDTH = 0.255         # Marta foot is 0.1107 m = 0.254
LEG_FOOT_HEIGHT = 0.225        # Marta foot is 0.1066 m = 0.245
LEG_FOOT_FORWARD_CENTER = 0.14 # heel -0.134, toe +0.416 from ankle

AUTHORING_SCENE = bpy.data.scenes.get("Scene") or bpy.context.scene
if bpy.context.window is not None:
    bpy.context.window.scene = AUTHORING_SCENE


def material(name: str, color: tuple[float, float, float, float], *, metallic=0.0, roughness=0.75):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.diffuse_color = color
    mat.use_nodes = True
    principled = next((node for node in mat.node_tree.nodes if node.type == "BSDF_PRINCIPLED"), None)
    if principled:
        base = principled.inputs.get("Base Color")
        metal = principled.inputs.get("Metallic")
        rough = principled.inputs.get("Roughness")
        alpha = principled.inputs.get("Alpha")
        if base:
            base.default_value = color
        if metal:
            metal.default_value = metallic
        if rough:
            rough.default_value = roughness
        if alpha:
            alpha.default_value = color[3]
    if color[3] < 0.999:
        if hasattr(mat, "surface_render_method"):
            try:
                mat.surface_render_method = "DITHERED"
            except (TypeError, ValueError):
                mat.surface_render_method = "BLENDED"
        elif hasattr(mat, "blend_method"):
            mat.blend_method = "BLEND"
    return mat


WOOD = material("Prosthetic_Wood", (0.55, 0.36, 0.17, 1.0), roughness=0.86)
WOOD_LIGHT = material("Prosthetic_WoodLight", (0.72, 0.53, 0.28, 1.0), roughness=0.82)
WOOD_DARK = material("Prosthetic_WoodDark", (0.25, 0.14, 0.065, 1.0), roughness=0.92)
ROPE = material("Prosthetic_Rope", (0.67, 0.57, 0.38, 1.0), roughness=0.96)
LEATHER = material("Prosthetic_Leather", (0.23, 0.105, 0.055, 1.0), roughness=0.9)
METAL = material("Prosthetic_Metal", (0.29, 0.32, 0.34, 1.0), metallic=0.72, roughness=0.48)
METAL_DARK = material("Prosthetic_MetalDark", (0.085, 0.095, 0.105, 1.0), metallic=0.82, roughness=0.38)
COPPER = material("Prosthetic_Copper", (0.45, 0.20, 0.075, 1.0), metallic=0.58, roughness=0.52)
REFERENCE_MATERIAL = material("Marta_ProstheticReference", (0.12, 0.42, 0.68, 0.22), roughness=0.9)


def collection():
    old = bpy.data.collections.get(COLLECTION_NAME)
    if old:
        for obj in list(old.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(old)
    made = bpy.data.collections.new(COLLECTION_NAME)
    AUTHORING_SCENE.collection.children.link(made)
    return made


TARGET_COLLECTION = collection()


def move_to_collection(obj):
    for owner in list(obj.users_collection):
        owner.objects.unlink(obj)
    TARGET_COLLECTION.objects.link(obj)


def finish(obj, parent, mat, name):
    obj.name = name
    obj.data.name = f"{name}_Mesh"
    obj.data.materials.clear()
    obj.data.materials.append(mat)
    for polygon in obj.data.polygons:
        polygon.use_smooth = False
    move_to_collection(obj)
    obj.parent = parent
    return obj


def cylinder(name, parent, radius, depth, y, mat, *, vertices=8, x=0.0, z=0.0):
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices,
        radius=radius,
        depth=depth,
        location=(x, y, z),
        rotation=(math.pi * 0.5, 0.0, 0.0),
    )
    obj = bpy.context.object
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def cone(name, parent, radius_start, radius_end, depth, y, mat, *, vertices=8, x=0.0, z=0.0):
    # The default cone runs along Z; after the X rotation its bottom/top radii
    # become the two ends along the prosthetic's +Y construction axis.
    bpy.ops.mesh.primitive_cone_add(
        vertices=vertices,
        radius1=radius_start,
        radius2=radius_end,
        depth=depth,
        location=(x, y, z),
        rotation=(math.pi * 0.5, 0.0, 0.0),
    )
    obj = bpy.context.object
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def box(name, parent, size, location, mat, *, bevel=0.0):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location)
    obj = bpy.context.object
    obj.dimensions = size
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel > 0.0:
        mod = obj.modifiers.new("OneFacetBevel", "BEVEL")
        mod.width = bevel
        mod.segments = 1
        mod.affect = "EDGES"
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=mod.name)
    return finish(obj, parent, mat, name)


def rod_between(name, parent, start, end, radius, mat, *, vertices=8):
    start_v = Vector(start)
    end_v = Vector(end)
    direction = end_v - start_v
    midpoint = (start_v + end_v) * 0.5
    bpy.ops.mesh.primitive_cylinder_add(
        vertices=vertices,
        radius=radius,
        depth=direction.length,
        location=midpoint,
    )
    obj = bpy.context.object
    obj.rotation_mode = "QUATERNION"
    obj.rotation_quaternion = direction.to_track_quat("Z", "Y")
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def torus_binding(name, parent, y, major_radius, minor_radius, mat, *, x=0.0, z=0.0):
    bpy.ops.mesh.primitive_torus_add(
        major_radius=major_radius,
        minor_radius=minor_radius,
        major_segments=12,
        minor_segments=4,
        location=(x, y, z),
        rotation=(math.pi * 0.5, 0.0, 0.0),
    )
    obj = bpy.context.object
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    return finish(obj, parent, mat, name)


def empty(name, parent, location, *, role=None):
    obj = bpy.data.objects.new(name, None)
    TARGET_COLLECTION.objects.link(obj)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.045
    obj.location = location
    obj.parent = parent
    if role:
        obj["hexlive_landmark"] = role
    return obj


def root(name, limb):
    obj = bpy.data.objects.new(name, None)
    TARGET_COLLECTION.objects.link(obj)
    obj.empty_display_type = "PLAIN_AXES"
    obj.empty_display_size = 0.075
    obj["hexlive_landmark"] = "root"
    obj["hexlive_limb"] = limb
    obj["hexlive_prosthetic_axis"] = "+Y"
    obj["hexlive_reference_length"] = 1.0
    obj["hexlive_marta_length_m"] = (
        MARTA_ARM_LENGTH_M if limb == "arm" else MARTA_LEG_LENGTH_M
    )
    return obj


def add_landmarks(root_obj, limb):
    """Stable authoring anchors shipped in every FBX.

    Joint is the amputated-limb attachment; End is the wrist/ankle bone target.
    Grip sits inside the primitive palm. It is authoring metadata today (the
    game recreates a non-collapsing live grip), but makes the asset independently
    inspectable and ready for a future skeleton-driven wearer.
    """
    prefix = root_obj.name
    empty(f"{prefix}__joint", root_obj, (0.0, 0.0, 0.0), role="joint")
    empty(f"{prefix}__end", root_obj, (0.0, 1.0, 0.0), role="end")
    grip_y = 1.20 if limb == "arm" else 1.0
    empty(f"{prefix}__grip", root_obj, (0.0, grip_y, 0.0), role="grip")


def lace_socket(parent, y, radius):
    for side in (-1, 1):
        rod_between("SocketLace", parent, (-0.062 * side, y, radius),
                    (0.062 * side, y + 0.12, radius - 0.027), 0.009, ROPE, vertices=6)


def piston_sleeve(parent, y, z):
    cylinder("PistonSleeve", parent, 0.035, 0.24, y, METAL_DARK, vertices=8, z=z)
    cylinder("PistonCollar", parent, 0.040, 0.036, y + 0.12, COPPER, vertices=8, z=z)


def tube_rings(points, radii_x, radii_z, segments, rng, side, jitter=0.035):
    """Rings along a polyline of centres (x, y, z) running mainly along +Y."""
    rings = []
    for (cx, cy, cz), rx, rz in zip(points, radii_x, radii_z):
        ring = []
        for k in range(segments):
            angle = 2.0 * math.pi * k / segments
            j = 1.0 + rng.uniform(-jitter, jitter)
            ring.append(Vector(((cx + math.sin(angle) * rx * j) * side, cy,
                                cz + math.cos(angle) * rz * j)))
        rings.append(ring)
    return rings


def carved_forearm(parent, side, rng):
    # (y, half-width X, half-depth Z): muscular below the elbow, flat at the
    # wrist (Marta: ~2.5 x 1.7 cm), like the shin's calf in reverse.
    profile = [
        (0.08, 0.135, 0.120),
        (0.25, 0.140, 0.118),
        (0.45, 0.122, 0.100),
        (0.70, 0.104, 0.080),
        (0.90, 0.096, 0.066),
        (1.00, 0.100, 0.064),
    ]
    count = 18
    points, rx, rz = [], [], []
    for r in range(count):
        y = profile[0][0] + (profile[-1][0] - profile[0][0]) * r / (count - 1)
        hx, hz = _lerp_table(profile, y)
        points.append((0.0, y, 0.0)); rx.append(hx); rz.append(hz)
    rings = tube_rings(points, rx, rz, 16, rng, side)
    return loft_mesh("ForearmCarved", parent, rings, [WOOD_CARVED, WOOD_GRAIN],
                     face_material=lambda r, i: 1 if i % 3 == 0 else 0)


def carved_hand(parent, side, rng):
    # Palm: wrist (Y=1) to knuckles, palm facing +Z, thumb lateral (+X*side).
    palm = [(0.98, 0.105, 0.066, 0.000), (1.06, 0.150, 0.072, 0.004),
            (1.18, 0.168, 0.066, 0.006), (1.28, 0.170, 0.056, 0.004),
            (1.32, 0.160, 0.048, 0.000)]
    rings = []
    for y, hx, hz, cz in palm:
        ring = []
        for k in range(14):
            angle = 2.0 * math.pi * k / 14
            j = 1.0 + rng.uniform(-0.03, 0.03)
            # Squarer section than a tube: the palm is a slab with rounded edges.
            s, c = math.sin(angle), math.cos(angle)
            sx = math.copysign(abs(s) ** 0.6, s)
            sz = math.copysign(abs(c) ** 0.8, c)
            ring.append(Vector((sx * hx * j * side, y, cz + sz * hz * j)))
        rings.append(ring)
    loft_mesh("PalmCarved", parent, rings, [WOOD_CARVED])

    # Four fingers, index next to the thumb; slight curl toward the palm.
    fingers = [(0.112, 0.300, 0.036), (0.038, 0.330, 0.037),
               (-0.036, 0.310, 0.035), (-0.106, 0.245, 0.031)]
    for index, (x, length, radius) in enumerate(fingers):
        stations = [0.0, 0.32, 0.36, 0.64, 0.68, 0.92, 1.0]
        points, rx, rz = [], [], []
        for t in stations:
            curl = 0.10 * t * t * length / 0.33
            points.append((x, 1.30 + t * length * 0.97, curl))
            knuckle = 1.12 if t in (0.32, 0.64) else 1.0  # carved joints
            taper = 1.0 - 0.25 * t if t < 1.0 else 0.45
            rx.append(radius * taper * knuckle)
            rz.append(radius * 0.9 * taper * knuckle)
        loft_mesh(f"Finger{index + 1}", parent,
                  tube_rings(points, rx, rz, 8, rng, side, jitter=0.03), [WOOD_CARVED])

    # Thumb from the base of the palm, angled out and toward the palm side.
    thumb = [(0.140, 1.04, 0.030), (0.185, 1.13, 0.055), (0.205, 1.20, 0.070),
             (0.222, 1.27, 0.085), (0.230, 1.33, 0.092)]
    radius = [0.048, 0.042, 0.044, 0.036, 0.016]
    loft_mesh("Thumb", parent, tube_rings(thumb, radius, [r * 0.9 for r in radius], 8, rng, side,
                                          jitter=0.03), [WOOD_CARVED])


def wooden_arm(side):
    import random

    suffix = "L" if side < 0 else "R"
    r = root(f"WoodArm{suffix}", "arm")
    cone("Socket", r, 0.180, 0.142, 0.22, 0.09, LEATHER, vertices=16)
    torus_binding("BindingUpper", r, -0.010, 0.175, 0.019, ROPE)
    torus_binding("BindingLower", r, 0.192, 0.140, 0.016, ROPE)
    for row in range(3):
        y = 0.01 + row * 0.055
        radius = 0.176 - row * 0.009
        for flip in (-1, 1):
            rod_between("SocketLace", r, (-0.042 * flip, y, radius),
                        (0.042 * flip, y + 0.05, radius - 0.005), 0.008, ROPE, vertices=6)
    for x in (-1, 1):
        rod_between("HingeRivet", r, (0.165 * x, 0.06, 0.0), (0.198 * x, 0.06, 0.0),
                    0.015, IRON, vertices=8)

    carved_forearm(r, side, random.Random(CARVE_SEED + 11))
    band("ForearmStrap", r, 0.45, 0.128, 0.106, 0.055, LEATHER)
    box("ForearmBuckle", r, (0.014, 0.07, 0.045), (0.128 * side, 0.45, 0.0), IRON, bevel=0.004)
    torus_binding("WristBinding", r, 0.93, 0.104, 0.014, ROPE)
    torus_binding("WristBinding2", r, 0.96, 0.104, 0.014, ROPE)
    carved_hand(r, side, random.Random(CARVE_SEED + 12))
    add_landmarks(r, "arm")
    return r


# --- §118.5 / bug #740: carved wooden leg ----------------------------------
#
# The first wooden leg was a cone on a box. The player read it as "some
# abstract thing" and asked for a leg that looks like a LEG cut out of wood:
# calf, ankle, a real foot with toes, straps and bindings. These helpers loft
# anatomical cross-sections (taken against Marta's shin/foot proportions in the
# same normalised units: knee Y=0, ankle Y=1, toes +Z, lateral +X*side) into
# faceted flat-shaded meshes. A fixed seed jitters every vertex a little so the
# facets read as knife cuts rather than a lathe; L and R share the jitter and
# are exact mirrors.

CARVE_SEED = 740
WOOD_CARVED = material("Prosthetic_WoodCarved", (0.54, 0.33, 0.15, 1.0), roughness=0.85)
WOOD_GRAIN = material("Prosthetic_WoodGrain", (0.38, 0.22, 0.09, 1.0), roughness=0.88)
IRON = material("Prosthetic_Iron", (0.17, 0.17, 0.18, 1.0), metallic=0.6, roughness=0.55)


def _lerp_table(table, t):
    """Smoothstep interpolation over [(t, a, b, ...)] keyframes."""
    if t <= table[0][0]:
        return table[0][1:]
    for lo, hi in zip(table, table[1:]):
        if lo[0] <= t <= hi[0]:
            u = (t - lo[0]) / (hi[0] - lo[0])
            u = u * u * (3.0 - 2.0 * u)
            return tuple(a + (b - a) * u for a, b in zip(lo[1:], hi[1:]))
    return table[-1][1:]


def loft_mesh(name, parent, rings, materials, face_material=None,
              cap_start=True, cap_end=True):
    """Quad strip through equal-sized vertex rings, optional n-gon caps."""
    import bmesh

    mesh = bpy.data.meshes.new(f"{name}_Mesh")
    obj = bpy.data.objects.new(name, mesh)
    TARGET_COLLECTION.objects.link(obj)
    bm = bmesh.new()
    verts = [[bm.verts.new(p) for p in ring] for ring in rings]
    count = len(rings[0])
    for r in range(len(rings) - 1):
        for i in range(count):
            face = bm.faces.new((verts[r][i], verts[r][(i + 1) % count],
                                 verts[r + 1][(i + 1) % count], verts[r + 1][i]))
            face.material_index = face_material(r, i) if face_material else 0
    caps = []
    if cap_start:
        caps.append(bm.faces.new(list(reversed(verts[0]))))
    if cap_end:
        caps.append(bm.faces.new(verts[-1]))
    if caps:
        # Ear-clip the caps now: a concave sole outline must not be left to
        # whichever triangulator the importer happens to use.
        bmesh.ops.triangulate(bm, faces=caps, quad_method="BEAUTY", ngon_method="EAR_CLIP")
    bmesh.ops.recalc_face_normals(bm, faces=list(bm.faces))
    bm.to_mesh(mesh)
    bm.free()
    for mat in materials:
        mesh.materials.append(mat)
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    obj.parent = parent
    return obj


def slab_mesh(name, parent, outline_xz, y_top, y_bottom, mat):
    """Extruded outline (sole plate). Outline is a closed (x, z) polygon."""
    ring_top = [Vector((x, y_top, z)) for x, z in outline_xz]
    ring_bottom = [Vector((x, y_bottom, z)) for x, z in outline_xz]
    return loft_mesh(name, parent, [ring_top, ring_bottom], [mat])


def carved_shin(parent, side, rng):
    # (y, half-width X, front depth +Z, back depth -Z, centre Z)
    # Back bulges at the calf (y≈0.3), front keeps the tibia ridge, both taper
    # to Marta's ankle (~0.07 normalised = 3 cm radius).
    profile = [
        (0.06, 0.128, 0.115, 0.125, 0.000),
        (0.18, 0.132, 0.112, 0.150, -0.010),
        (0.32, 0.130, 0.104, 0.162, -0.018),
        (0.48, 0.112, 0.094, 0.135, -0.014),
        (0.64, 0.090, 0.084, 0.100, -0.008),
        (0.80, 0.072, 0.074, 0.076, -0.004),
        (0.90, 0.068, 0.070, 0.070, 0.000),
        (0.97, 0.074, 0.074, 0.076, 0.000),
    ]
    sides = 18
    ring_count = 24
    rings = []
    for r in range(ring_count):
        y = profile[0][0] + (profile[-1][0] - profile[0][0]) * r / (ring_count - 1)
        half_x, front, back, centre = _lerp_table(profile, y)
        ring = []
        for i in range(sides):
            angle = 2.0 * math.pi * i / sides
            c, s = math.cos(angle), math.sin(angle)
            depth = front if c >= 0.0 else back
            ridge = 1.0 + 0.07 * max(0.0, c) ** 8  # tibia ridge at the front
            jitter = 1.0 + rng.uniform(-0.035, 0.035)
            x = s * half_x * jitter
            z = centre + c * depth * ridge * jitter
            ring.append(Vector((x * side, y, z)))
        rings.append(ring)

    # Alternate two close wood tones in long vertical strips: carved grain,
    # not a texture. Every third column is the darker grain.
    return loft_mesh("ShinCarved", parent, rings, [WOOD_CARVED, WOOD_GRAIN],
                     face_material=lambda r, i: 1 if i % 3 == 0 else 0)


FOOT_SECTION = [  # (u lateral, v 0=top .. 1=sole)
    (0.00, 0.00), (0.45, 0.05), (0.80, 0.25), (0.98, 0.55), (1.00, 0.85),
    (0.85, 1.00), (0.30, 1.00), (-0.30, 1.00), (-0.85, 1.00), (-1.00, 0.85),
    (-0.98, 0.55), (-0.80, 0.25), (-0.45, 0.05),
]
# (z, half-width, top Y, sole Y, lateral centre offset, medial arch lift)
FOOT_PROFILE = [
    (-0.150, 0.040, 1.050, 1.115, 0.000, 0.000),
    (-0.135, 0.074, 1.000, 1.150, 0.000, 0.000),
    (-0.090, 0.092, 0.962, 1.160, 0.000, 0.000),
    (0.000, 0.098, 0.930, 1.160, 0.002, 0.020),
    (0.100, 0.108, 0.962, 1.160, 0.000, 0.030),
    (0.200, 0.120, 1.012, 1.160, -0.004, 0.012),
    (0.270, 0.127, 1.050, 1.160, -0.007, 0.000),
    (0.300, 0.124, 1.068, 1.158, -0.008, 0.000),
]


def carved_foot(parent, side, rng):
    sections = 18
    rings = []
    z0, z1 = FOOT_PROFILE[0][0], FOOT_PROFILE[-1][0]
    for r in range(sections):
        z = z0 + (z1 - z0) * r / (sections - 1)
        half, top, sole, centre, arch = _lerp_table(FOOT_PROFILE, z)
        ring = []
        for u, v in FOOT_SECTION:
            jitter = 1.0 + rng.uniform(-0.03, 0.03)
            x = centre + u * half * jitter
            y = top + v * (sole - top)
            if u < -0.2 and v > 0.9:
                y -= arch  # medial longitudinal arch: the inner sole lifts
            ring.append(Vector((x * side, y, z)))
        rings.append(ring)
    return loft_mesh("FootCarved", parent, rings, [WOOD_CARVED, WOOD_GRAIN],
                     face_material=lambda r, i: 1 if i in (3, 9) else 0)


# (lateral X centre, half width, length, height) big toe is medial (-X).
TOES = [
    (-0.074, 0.036, 0.125, 0.060),
    (-0.026, 0.023, 0.112, 0.046),
    (0.014, 0.021, 0.100, 0.042),
    (0.050, 0.019, 0.088, 0.038),
    (0.084, 0.017, 0.070, 0.034),
]


def carved_toes(parent, side, rng):
    toes = []
    base_z = 0.275
    sole = 1.158
    for index, (cx, half, length, height) in enumerate(TOES):
        rings = []
        # A slight curl: knuckle highest, tip rounding down onto the sole.
        stations = [(0.00, 1.00, 1.00), (0.35, 1.00, 1.05), (0.70, 0.92, 0.88),
                    (0.90, 0.70, 0.62), (1.00, 0.30, 0.30)]
        for t, width_scale, height_scale in stations:
            z = base_z + t * length
            h = height * height_scale
            ring = []
            for k in range(8):
                angle = 2.0 * math.pi * k / 8
                jitter = 1.0 + rng.uniform(-0.04, 0.04)
                x = cx + math.sin(angle) * half * width_scale * jitter
                y = sole - h * 0.5 - math.cos(angle) * h * 0.5
                ring.append(Vector((x * side, y, z)))
            rings.append(ring)
        toes.append(loft_mesh(f"Toe{index + 1}", parent, rings, [WOOD_CARVED]))
    return toes


def foot_outline(side, inset=0.0):
    lateral, medial = [], []
    for z, half, _top, _sole, centre, _arch in FOOT_PROFILE:
        lateral.append(((centre + half - inset) * side, z))
        medial.append(((centre - half + inset) * side, z))
    # Toe arc: around the five toe tips, lateral -> medial.
    tips = [((cx + 0.6 * half) * side, 0.275 + length + 0.006)
            for cx, half, length, _h in reversed(TOES)]
    tips.append(((TOES[0][0] - TOES[0][1]) * side, 0.275 + TOES[0][2] * 0.8))
    return lateral + tips + list(reversed(medial))


def ankle_details(parent, side):
    # Malleoli: the outer ankle bone sits lower and further back than the inner.
    for name, x, y, z in (("MalleolusLateral", 0.070, 0.955, -0.012),
                          ("MalleolusMedial", -0.066, 0.925, 0.006)):
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=0.034,
                                              location=(x * side, y, z))
        obj = bpy.context.object
        obj.scale = (0.55, 1.0, 0.85)
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        finish(obj, parent, WOOD_GRAIN, name)


def band(name, parent, y, radius_x, radius_z, height, mat, *, z=0.0, segments=18):
    rings = []
    for yy in (y - height * 0.5, y + height * 0.5):
        ring = []
        for i in range(segments):
            angle = 2.0 * math.pi * i / segments
            ring.append(Vector((math.sin(angle) * radius_x, yy, z + math.cos(angle) * radius_z)))
        rings.append(ring)
    inner = [[Vector((p.x * 0.9, p.y, z + (p.z - z) * 0.9)) for p in ring] for ring in rings]
    # Closed tube: outer top -> outer bottom -> inner bottom -> inner top.
    return loft_mesh(name, parent, [rings[0], rings[1], inner[1], inner[0], rings[0]],
                     [mat], cap_start=False, cap_end=False)


def wooden_leg(side):
    import random

    suffix = "L" if side < 0 else "R"
    rng = random.Random(CARVE_SEED)
    r = root(f"WoodLeg{suffix}", "leg")

    # Leather socket around the stump, laced at the front, rope at both rims.
    cone("Socket", r, 0.185, 0.150, 0.24, 0.095, LEATHER, vertices=16)
    torus_binding("BindingUpper", r, -0.012, 0.180, 0.020, ROPE)
    torus_binding("BindingLower", r, 0.205, 0.148, 0.018, ROPE)
    # Criss-cross lacing down the front through iron eyelets.
    for row in range(3):
        y = 0.01 + row * 0.06
        radius = 0.181 - row * 0.009
        for flip in (-1, 1):
            rod_between("SocketLace", r, (-0.045 * flip, y, radius),
                        (0.045 * flip, y + 0.055, radius - 0.005), 0.008, ROPE, vertices=6)
    for row in range(4):
        for x in (-0.05, 0.05):
            rod_between("LaceEyelet", r, (x, 0.008 + row * 0.058, 0.176 - row * 0.009),
                        (x, 0.008 + row * 0.058, 0.192 - row * 0.009), 0.011, IRON, vertices=6)
    # Side hinge plates where a knee strap would pivot (iron, both sides).
    for x in (-1, 1):
        rod_between("HingePlate", r, (0.186 * x, 0.02, 0.0), (0.172 * x, 0.19, 0.0),
                    0.022, IRON, vertices=6)
        rod_between("HingeRivet", r, (0.17 * x, 0.06, 0.0), (0.205 * x, 0.06, 0.0),
                    0.016, IRON, vertices=8)

    rng_shin = random.Random(CARVE_SEED + 1)
    rng_foot = random.Random(CARVE_SEED + 2)
    rng_toes = random.Random(CARVE_SEED + 3)
    carved_shin(r, side, rng_shin)

    # Calf strap with an iron buckle on the outer side, rope at the ankle.
    band("CalfStrap", r, 0.42, 0.122, 0.150, 0.06, LEATHER, z=-0.016)
    box("CalfBuckle", r, (0.014, 0.075, 0.05), (0.124 * side, 0.42, 0.03), IRON, bevel=0.004)
    torus_binding("AnkleBinding", r, 0.855, 0.080, 0.014, ROPE)
    torus_binding("AnkleBinding2", r, 0.885, 0.078, 0.014, ROPE)
    # Dowels that pin the carved shin into the socket.
    for y, x in ((0.27, 0.05), (0.27, -0.05), (0.62, 0.0)):
        z_front = 0.104 if y < 0.5 else 0.086
        rod_between("Dowel", r, (x * side, y, z_front - 0.01), (x * side, y, z_front + 0.012),
                    0.012, WOOD_DARK, vertices=6)

    # Knots left in the carved wood.
    for x, y, z, radius in ((0.105, 0.36, -0.07, 0.026), (-0.06, 0.70, -0.07, 0.018)):
        bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=1, radius=radius,
                                              location=(x * side, y, z))
        obj = bpy.context.object
        obj.scale = (0.45, 1.4, 1.0)
        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
        finish(obj, r, WOOD_DARK, "Knot")

    carved_foot(r, side, rng_foot)
    carved_toes(r, side, rng_toes)
    ankle_details(r, side)
    slab_mesh("Sole", r, foot_outline(side), 1.150, 1.172, LEATHER)
    add_landmarks(r, "leg")
    return r


def mechanical_arm(side):
    suffix = "L" if side < 0 else "R"
    r = root(f"MechanicalArm{suffix}", "arm")
    cone("Socket", r, 0.18, 0.136, 0.20, 0.08, METAL_DARK, vertices=10)
    torus_binding("SocketBandUpper", r, -0.005, 0.174, 0.017, COPPER)
    torus_binding("SocketBandLower", r, 0.16, 0.132, 0.014, COPPER)
    rod_between("ElbowHub", r, (-0.19, 0.11, 0.0),
                (0.19, 0.11, 0.0), 0.066, METAL_DARK, vertices=12)
    rod_between("ElbowAxle", r, (-0.205, 0.11, 0.0),
                (0.205, 0.11, 0.0), 0.043, COPPER, vertices=12)
    rod_between("RailOuter", r, (0.075 * side, 0.20, 0.035),
                (0.060 * side, 0.91, 0.027), 0.028, METAL, vertices=8)
    rod_between("RailInner", r, (-0.075 * side, 0.20, -0.035),
                (-0.060 * side, 0.91, -0.027), 0.028, METAL, vertices=8)
    rod_between("Piston", r, (0.0, 0.23, -0.025),
                (0.0, 0.89, 0.025), 0.021, COPPER, vertices=8)
    piston_sleeve(r, 0.44, -0.008)
    cylinder("WristHub", r, 0.090, 0.11, 0.94, METAL_DARK, vertices=12)
    box("Palm", r, (0.34, 0.32, ARM_HAND_THICKNESS),
        (0.0, 1.15, -0.006), METAL, bevel=0.022)
    box("PalmPlate", r, (0.21, 0.18, 0.023),
        (0.0, 1.14, 0.091), METAL_DARK, bevel=0.010)
    rod_between("KnuckleAxle", r, (-0.137, 1.285, 0.027),
                (0.137, 1.285, 0.027), 0.022, COPPER, vertices=8)
    rod_between("FingerOuter", r, (0.115 * side, 1.27, 0.020),
                (0.145 * side, 1.0 + ARM_HAND_OVERHANG, 0.025),
                0.036, METAL_DARK, vertices=8)
    rod_between("FingerInner", r, (-0.105 * side, 1.27, -0.020),
                (-0.105 * side, 1.60, 0.018), 0.034, METAL_DARK, vertices=8)
    rod_between("FingerCenter", r, (0.0, 1.28, -0.045),
                (0.0, 1.64, -0.025), 0.029, COPPER, vertices=8)
    rod_between("Thumb", r, (0.17 * side, 1.12, -0.01),
                (0.22 * side, 1.38, 0.055), 0.034, METAL_DARK, vertices=8)
    add_landmarks(r, "arm")
    return r


def mechanical_leg(side):
    suffix = "L" if side < 0 else "R"
    r = root(f"MechanicalLeg{suffix}", "leg")
    cone("Socket", r, 0.19, 0.15, 0.22, 0.085, METAL_DARK, vertices=10)
    torus_binding("SocketBandUpper", r, -0.01, 0.184, 0.019, COPPER)
    torus_binding("SocketBandLower", r, 0.175, 0.146, 0.016, COPPER)
    rod_between("KneeHub", r, (-0.22, 0.13, 0.0),
                (0.22, 0.13, 0.0), 0.075, METAL_DARK, vertices=12)
    rod_between("KneeAxle", r, (-0.235, 0.13, 0.0),
                (0.235, 0.13, 0.0), 0.048, COPPER, vertices=12)
    rod_between("ShinRailOuter", r, (0.078 * side, 0.22, 0.035),
                (0.062 * side, 0.91, 0.025), 0.032, METAL, vertices=8)
    rod_between("ShinRailInner", r, (-0.078 * side, 0.22, -0.035),
                (-0.062 * side, 0.91, -0.025), 0.032, METAL, vertices=8)
    rod_between("ShinPiston", r, (0.0, 0.25, -0.03),
                (0.0, 0.89, 0.03), 0.023, COPPER, vertices=8)
    piston_sleeve(r, 0.45, -0.010)
    cylinder("AnkleHub", r, 0.094, 0.11, 0.92, METAL_DARK, vertices=12)
    box("FootFrame", r, (LEG_FOOT_WIDTH, LEG_FOOT_HEIGHT - 0.045, LEG_FOOT_LENGTH),
        (0.0, 1.035, LEG_FOOT_FORWARD_CENTER), METAL, bevel=0.022)
    for z in (-0.07, 0.04, 0.15):
        rod_between("FootRib", r, (-0.104, 0.947, LEG_FOOT_FORWARD_CENTER + z),
                    (0.104, 0.947, LEG_FOOT_FORWARD_CENTER + z), 0.014, METAL_DARK, vertices=6)
    box("Sole", r, (LEG_FOOT_WIDTH + 0.018, 0.042, LEG_FOOT_LENGTH + 0.02),
        (0.0, 1.145, LEG_FOOT_FORWARD_CENTER), METAL_DARK, bevel=0.010)
    rod_between("ToeBrace", r,
                (-0.105, 1.02, LEG_FOOT_FORWARD_CENTER + 0.24),
                (0.105, 1.02, LEG_FOOT_FORWARD_CENTER + 0.24),
                0.020, COPPER, vertices=8)
    add_landmarks(r, "leg")
    return r


def hierarchy(root_obj):
    found = [root_obj]
    stack = list(root_obj.children)
    while stack:
        child = stack.pop()
        found.append(child)
        stack.extend(child.children)
    return found


def export(root_obj, filename):
    for obj in bpy.context.scene.objects:
        obj.select_set(False)
    old_location = root_obj.location.copy()
    root_obj.location = (0.0, 0.0, 0.0)
    for obj in hierarchy(root_obj):
        obj.hide_set(False)
        obj.select_set(True)
    bpy.context.view_layer.objects.active = root_obj
    filepath = EXPORT_ROOT / filename
    bpy.ops.export_scene.fbx(
        filepath=str(filepath),
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
    root_obj.location = old_location


def replace_collection(name):
    old = bpy.data.collections.get(name)
    if old:
        for obj in list(old.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(old)
    return bpy.data.collections.new(name)


def ensure_marta_reference():
    reference = bpy.data.collections.get(REFERENCE_COLLECTION_NAME)
    if reference and any(obj.type == "ARMATURE" for obj in reference.objects):
        return reference

    if reference:
        for obj in list(reference.objects):
            bpy.data.objects.remove(obj, do_unlink=True)
        bpy.data.collections.remove(reference)

    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=str(MARTA_FBX), use_anim=False)
    imported = [obj for obj in bpy.data.objects if obj not in before]
    reference = bpy.data.collections.new(REFERENCE_COLLECTION_NAME)
    AUTHORING_SCENE.collection.children.link(reference)
    for obj in imported:
        for owner in list(obj.users_collection):
            owner.objects.unlink(obj)
        reference.objects.link(obj)
        obj["hexlive_reference_only"] = True

    return reference


def style_reference(reference):
    for obj in reference.objects:
        obj.hide_render = False
        if obj.type == "MESH":
            obj.display_type = "SOLID"
            obj.color = REFERENCE_MATERIAL.diffuse_color
            obj.data.materials.clear()
            obj.data.materials.append(REFERENCE_MATERIAL)
        elif obj.type == "ARMATURE":
            obj.show_in_front = True
            obj.data.display_type = "OCTAHEDRAL"


def duplicate_hierarchy(root_obj, target_collection, prefix):
    originals = hierarchy(root_obj)
    clones = {}
    for original in originals:
        clone = original.copy()
        clone.name = f"{prefix}_{original.name}"
        if original.data is not None:
            clone.data = original.data
        target_collection.objects.link(clone)
        clones[original] = clone

    for original, clone in clones.items():
        if original.parent in clones:
            clone.parent = clones[original.parent]
            clone.matrix_parent_inverse = original.matrix_parent_inverse.copy()
            clone.matrix_basis = original.matrix_basis.copy()
        else:
            clone.parent = None
            clone.matrix_basis = Matrix.Identity(4)
    return clones[root_obj]


def bone_origin(armature, bone_name):
    # The imported reference mesh is displayed in its authored pose, not raw
    # armature rest space. Use the evaluated pose origin so the visible skin and
    # the fitted preview cannot drift apart (Marta's shins are already rotated).
    return armature.matrix_world @ armature.pose.bones[bone_name].head


def fit_preview(root_obj, armature, start_name, end_name):
    start = bone_origin(armature, start_name)
    end = bone_origin(armature, end_name)
    direction = end - start
    length = direction.length
    local_y = direction.normalized()

    # Marta's imported rest pose faces -Y. This is the Blender equivalent of
    # ProstheticVisual's LookRotation(actorForward, boneDirection): local +Y
    # follows joint->end and local +Z follows the actor's forward direction.
    local_z = Vector((0.0, -1.0, 0.0))
    local_z -= local_y * local_z.dot(local_y)
    if local_z.length_squared < 0.000001:
        local_z = Vector((0.0, 0.0, 1.0))
        local_z -= local_y * local_z.dot(local_y)
    local_z.normalize()
    local_x = local_y.cross(local_z).normalized()
    local_z = local_x.cross(local_y).normalized()
    rotation = Matrix((local_x, local_y, local_z)).transposed().to_quaternion()

    root_obj.location = start
    root_obj.rotation_mode = "QUATERNION"
    root_obj.rotation_quaternion = rotation
    root_obj.scale = (length, length, length)
    root_obj["hexlive_fit_start_bone"] = start_name
    root_obj["hexlive_fit_end_bone"] = end_name
    root_obj["hexlive_fit_length_m"] = length


def link_once(scene, collection_to_link):
    if collection_to_link.name not in {child.name for child in scene.collection.children}:
        scene.collection.children.link(collection_to_link)


def look_at(obj, target):
    obj.rotation_euler = (Vector(target) - obj.location).to_track_quat("-Z", "Y").to_euler()


def add_fit_stage(scene):
    stage = replace_collection("PROSTHETIC_FIT_STAGE")
    scene.collection.children.link(stage)

    camera_data = bpy.data.cameras.new("ProstheticFitCamera")
    camera = bpy.data.objects.new("ProstheticFitCamera", camera_data)
    stage.objects.link(camera)
    camera.location = (2.55, -4.6, 1.48)
    camera.data.lens = 62.0
    look_at(camera, (0.0, 0.0, 0.86))
    scene.camera = camera

    key_data = bpy.data.lights.new("ProstheticFitKey", "AREA")
    key_data.energy = 900.0
    key_data.shape = "DISK"
    key_data.size = 4.0
    key = bpy.data.objects.new("ProstheticFitKey", key_data)
    stage.objects.link(key)
    key.location = (-2.6, -3.4, 4.0)
    look_at(key, (0.0, 0.0, 0.85))

    fill_data = bpy.data.lights.new("ProstheticFitFill", "AREA")
    fill_data.energy = 500.0
    fill_data.size = 3.0
    fill = bpy.data.objects.new("ProstheticFitFill", fill_data)
    stage.objects.link(fill)
    fill.location = (2.8, -1.2, 2.2)
    look_at(fill, (0.0, 0.0, 0.9))

    scene.render.resolution_x = 900
    scene.render.resolution_y = 1050
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    PREVIEW_PATH.parent.mkdir(parents=True, exist_ok=True)
    scene.render.filepath = str(PREVIEW_PATH)
    if scene.world is None:
        scene.world = bpy.data.worlds.new("ProstheticFitWorld")
    scene.world.use_nodes = True
    background = scene.world.node_tree.nodes.get("Background")
    if background:
        background.inputs["Color"].default_value = (0.018, 0.024, 0.032, 1.0)
        background.inputs["Strength"].default_value = 0.35


def build_fit_scene(model_roots):
    reference = ensure_marta_reference()
    style_reference(reference)
    armature = next(obj for obj in reference.objects if obj.type == "ARMATURE")

    wood = replace_collection(WOOD_PREVIEW_COLLECTION_NAME)
    mechanical = replace_collection(MECHANICAL_PREVIEW_COLLECTION_NAME)

    targets = {
        "WoodArmR": (wood, "rForearmBend", "rHand"),
        "WoodArmL": (wood, "lForearmBend", "lHand"),
        "WoodLegR": (wood, "rShin", "rFoot"),
        "WoodLegL": (wood, "lShin", "lFoot"),
        "MechanicalArmR": (mechanical, "rForearmBend", "rHand"),
        "MechanicalArmL": (mechanical, "lForearmBend", "lHand"),
        "MechanicalLegR": (mechanical, "rShin", "rFoot"),
        "MechanicalLegL": (mechanical, "lShin", "lFoot"),
    }
    for model_name, (target, start_name, end_name) in targets.items():
        preview = duplicate_hierarchy(
            model_roots[model_name], target, f"Fit_{model_name}")
        fit_preview(preview, armature, start_name, end_name)

    # Wood is the default review layer; the mechanical layer uses the same fit
    # and is one collection toggle away without double-drawing both devices.
    wood.hide_viewport = False
    wood.hide_render = False
    mechanical.hide_viewport = True
    mechanical.hide_render = True

    old_scene = bpy.data.scenes.get(FIT_SCENE_NAME)
    if old_scene:
        bpy.data.scenes.remove(old_scene)
    fit_scene = bpy.data.scenes.new(FIT_SCENE_NAME)
    link_once(fit_scene, reference)
    link_once(fit_scene, wood)
    link_once(fit_scene, mechanical)
    add_fit_stage(fit_scene)

    if bpy.context.window is not None:
        bpy.context.window.scene = fit_scene
    bpy.ops.render.render(write_still=True)
    return fit_scene


EXPORT_ROOT.mkdir(parents=True, exist_ok=True)
SOURCE_ROOT.mkdir(parents=True, exist_ok=True)

models = [
    (wooden_arm(1), "prosthetic_arm_wood_r.fbx", (-1.8, 0.0, 0.0)),
    (wooden_arm(-1), "prosthetic_arm_wood_l.fbx", (-0.6, 0.0, 0.0)),
    (wooden_leg(1), "prosthetic_leg_wood_r.fbx", (0.6, 0.0, 0.0)),
    (wooden_leg(-1), "prosthetic_leg_wood_l.fbx", (1.8, 0.0, 0.0)),
    (mechanical_arm(1), "prosthetic_arm_mechanical_r.fbx", (-1.8, 0.0, -1.4)),
    (mechanical_arm(-1), "prosthetic_arm_mechanical_l.fbx", (-0.6, 0.0, -1.4)),
    (mechanical_leg(1), "prosthetic_leg_mechanical_r.fbx", (0.6, 0.0, -1.4)),
    (mechanical_leg(-1), "prosthetic_leg_mechanical_l.fbx", (1.8, 0.0, -1.4)),
]

for model, filename, _ in models:
    export(model, filename)

for model, _, preview_location in models:
    model.location = preview_location

model_roots = {model.name: model for model, _, _ in models}
fit_scene = build_fit_scene(model_roots)

# Preserve the user's multi-item working file: save a project-owned COPY with
# the prosthetic collection, Marta reference and fitted review scene instead of
# changing the open working file's path.
bpy.ops.wm.save_as_mainfile(
    filepath=str(SOURCE_ROOT / "hexlive_prosthetics.blend"),
    copy=True,
)

for obj in fit_scene.objects:
    obj.select_set(False)
wood_preview = next(
    (obj for obj in fit_scene.objects if obj.get("hexlive_fit_start_bone") == "rForearmBend"),
    None,
)
if wood_preview:
    wood_preview.select_set(True)
    bpy.context.view_layer.objects.active = wood_preview

print(
    "HEX_PROSTHETICS_OK",
    len(models),
    str(EXPORT_ROOT),
    "MARTA_ARM_M", MARTA_ARM_LENGTH_M,
    "MARTA_LEG_M", MARTA_LEG_LENGTH_M,
    "PREVIEW", str(PREVIEW_PATH),
)
