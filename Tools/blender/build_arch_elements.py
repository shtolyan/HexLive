"""Rebuild every §120 architecture element in the open HexLive building kit.

    exec(open("Tools/blender/build_arch_elements.py").read())

Deterministic: same seeds in, same meshes out. Run it, eyeball the result, then
run export_arch_elements.py to ship the five FBX files to Unity.
"""

import importlib
import math
import os
import random
import sys

import bpy

_here = os.path.dirname(bpy.data.filepath)
_tools = os.path.abspath(os.path.join(_here, "../../..", "Tools", "blender"))
if _tools not in sys.path:
    sys.path.insert(0, _tools)

import arch_elements_lib as lib  # noqa: E402
importlib.reload(lib)

from arch_elements_lib import (  # noqa: E402
    DECK_TOP, SEAM, SECTION, SLAB_LENGTH, SPLICE_OVERLAP, STICK_R, STICK_X,
    TAN30, TOTAL_HEIGHT, axis_lashing, bowed_stick, clear_collection,
    element_root, ensure_materials, fit_rope, join, new_empty, new_obj,
    plank_slab,
)

ensure_materials()
clear_collection()

BOUNDARY_ROPE_PLAN = [
    (0.13, 3, 0, True),
    (1.09, 3, 7, True),
    (1.21, 3, -7, True),
    (2.18, 3, 0, True),
]


def boundary_sticks(stage, prefix, rng):
    """Four sticks ON the section seam: a bowed pair front and back, lower and
    upper, overlapping at mid height. They straddle the wall plane, so the
    vertical joint between neighbouring sections is covered from both sides."""
    half = (TOTAL_HEIGHT + SPLICE_OVERLAP) * 0.5
    middle = TOTAL_HEIGHT * 0.5
    lean = math.radians(1.4)
    for side, x in (("f", STICK_X), ("b", -STICK_X)):
        new_obj(f"{prefix}_stick_lo_{side}",
                bowed_stick(f"{prefix}_stick_lo_{side}", half, STICK_R, rng), stage,
                loc=(x, SEAM - 0.021, 0.0),
                rot=(-lean, math.radians(rng.uniform(-0.3, 0.3)), rng.uniform(0, 3.0)))
        new_obj(f"{prefix}_stick_hi_{side}",
                bowed_stick(f"{prefix}_stick_hi_{side}", half, STICK_R, rng), stage,
                loc=(x, SEAM + 0.021, middle - SPLICE_OVERLAP * 0.5),
                rot=(lean, math.radians(rng.uniform(-0.3, 0.3)), rng.uniform(0, 3.0)))


# --------------------------------------------------------------------------- #
# wall
# --------------------------------------------------------------------------- #
rng = random.Random(801)
root, (s1, s2, s3) = element_root("HL_ARCH_WALL", 6.0)
boundary_sticks(s1, "WALL", rng)
for index, z in enumerate((0.085, 0.775, 1.465)):
    tones = (("ARCH_Sapwood", "ARCH_SapwoodLight") if index % 2 == 0
             else ("ARCH_SapwoodLight", "ARCH_Sapwood"))
    new_obj(f"WALL_slab_{index}",
            plank_slab(f"WALL_slab_{index}", SLAB_LENGTH, 0.70, 0.11, rng, tones), s2,
            loc=(rng.uniform(-0.004, 0.004), 0, z), rot=(0, 0, rng.uniform(-0.005, 0.005)))
wall_report = fit_rope(root, "WALL", 811, BOUNDARY_ROPE_PLAN)

# --------------------------------------------------------------------------- #
# window
# --------------------------------------------------------------------------- #
rng = random.Random(802)
root, (s1, s2, s3) = element_root("HL_ARCH_WINDOW", 10.0)
boundary_sticks(s1, "WIN", rng)
new_obj("WIN_slab_bottom",
        plank_slab("WIN_slab_bottom", SLAB_LENGTH, 0.70, 0.11, rng,
                   ("ARCH_Sapwood", "ARCH_SapwoodLight")), s2, loc=(0, 0, 0.085))
new_obj("WIN_slab_top",
        plank_slab("WIN_slab_top", SLAB_LENGTH, 0.70, 0.11, rng,
                   ("ARCH_SapwoodLight", "ARCH_Sapwood")), s2, loc=(0, 0, 1.465))
new_obj("WIN_sill",
        plank_slab("WIN_sill", SLAB_LENGTH, 0.115, 0.13, rng,
                   ("ARCH_SapwoodLight",), planks=1), s2, loc=(0, 0, 0.788))
window_report = fit_rope(root, "WIN", 812, BOUNDARY_ROPE_PLAN)

# --------------------------------------------------------------------------- #
# door — the leaf hangs on the same boundary pair
# --------------------------------------------------------------------------- #
rng = random.Random(803)
root, (s1, s2, s3) = element_root("HL_ARCH_DOOR", 8.0)
boundary_sticks(s1, "DOOR", rng)
new_obj("DOOR_lintel", bowed_stick("DOOR_lintel", SECTION + 0.02, 0.044, rng), s1,
        loc=(0, SEAM, 1.462), rot=(math.radians(90), 0, 0))
# The infill tops out level with the wall's third board (0.085 + 3 x 0.70 minus
# the laps = 2.165), so a door bay never stands proud of the wall line.
new_obj("DOOR_top_infill",
        plank_slab("DOOR_top_infill", SLAB_LENGTH, 0.675, 0.11, rng,
                   ("ARCH_Sapwood", "ARCH_SapwoodLight")), s2, loc=(0, 0, 1.49))
pivot = new_empty("HL_Door_Pivot", s2, loc=(0, SEAM, 0))
# plank_slab is authored lying down: length along Y, boards stacked in Z, origin
# at the LENGTH CENTRE. Rotating +90 deg about X stands it up (length -> world Z,
# board width -> -Y across the opening, thickness -> X across the wall), so the
# leaf has to be lifted by half its length or it sinks into the ground.
DOOR_LEAF_LENGTH = 1.37
DOOR_LEAF_FOOT = 0.06
new_obj("DOOR_leaf",
        plank_slab("DOOR_leaf", DOOR_LEAF_LENGTH, 0.455, 0.075, rng,
                   ("ARCH_SapwoodLight", "ARCH_Sapwood", "ARCH_SapwoodLight"),
                   planks=3, overlap=0.011), pivot,
        loc=(0, 0, DOOR_LEAF_FOOT + DOOR_LEAF_LENGTH * 0.5), rot=(math.radians(90), 0, 0))
for name, angle in (("HL_Door_State_Closed", 0.0), ("HL_Door_State_Open", math.radians(72))):
    new_empty(name, s2, loc=(0, SEAM, 0), rot=(0, 0, angle))
door_report = fit_rope(root, "DOOR", 813, BOUNDARY_ROPE_PLAN)

# --------------------------------------------------------------------------- #
# corner support — the pair separates along Y, so no frapping fits between them
# --------------------------------------------------------------------------- #
rng = random.Random(804)
root, (s1, s2, s3) = element_root("HL_ARCH_SUPPORT", 12.0)
for side, y, tilt in (("a", -0.048, 1.1), ("b", 0.048, -1.1)):
    new_obj(f"SUP_stick_{side}",
            bowed_stick(f"SUP_stick_{side}", TOTAL_HEIGHT, 0.052, rng, bow=0.010), s1,
            loc=(0, y, 0.0),
            rot=(math.radians(tilt), math.radians(rng.uniform(-0.4, 0.4)), rng.uniform(0, 3.0)))
support_report = fit_rope(root, "SUP", 805,
                          [(0.15, 4, 0, False), (1.15, 4, 0, False), (2.16, 4, 0, False)],
                          sep_axis="y")

# --------------------------------------------------------------------------- #
# floor sector — one sixth of a hex, deck top at DECK_TOP
# --------------------------------------------------------------------------- #
rng = random.Random(806)
root, (s1, s2, s3) = element_root("HL_ARCH_FLOOR", 14.0)
DECK_BOTTOM = DECK_TOP - 0.055
JOIST_R = 0.016
JOIST_Z = 0.031


def sector_boards(name, x0, x1, tones, wedge=False):
    """Two edge-parallel planks per resource, shiplapped so the deck has no
    holes to the ground; clipped to the sector triangle."""
    import bmesh
    from mathutils import Vector
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()
    materials = []
    indices = {}

    def material_index(material_name):
        if material_name not in indices:
            indices[material_name] = len(materials)
            materials.append(bpy.data.materials[material_name])
        return indices[material_name]

    overlap = 0.008
    thickness = 0.055
    width = (x1 - x0 + overlap) * 0.5
    for plank in range(2):
        xa = x0 + plank * (width - overlap)
        xb = xa + width
        ya, yb = xa * TAN30 * 0.985, xb * TAN30 * 0.985
        z0 = -(0.004 if plank == 1 else 0.0) + rng.uniform(-0.002, 0.002)
        tone = material_index(tones[plank % 2])
        heart = material_index("ARCH_Heartwood")
        bottom = [bm.verts.new(p) for p in ((xa, -ya, z0), (xb, -yb, z0),
                                            (xb, yb, z0), (xa, ya, z0))]
        top = [bm.verts.new(p) for p in ((xa, -ya, z0 + thickness), (xb, -yb, z0 + thickness),
                                         (xb, yb, z0 + thickness), (xa, ya, z0 + thickness))]
        bm.faces.new(list(reversed(bottom)))
        face = bm.faces.new(top)
        face.material_index = tone
        for a in range(4):
            b = (a + 1) % 4
            face = bm.faces.new((bottom[a], bottom[b], top[b], top[a]))
            face.material_index = heart if a in (0, 2) else tone
    if wedge:
        xa, xb = 0.02, x0 + 0.006
        ya, yb = xa * TAN30 * 0.95, xb * TAN30 * 0.985
        z0 = -0.003
        light = material_index("ARCH_SapwoodLight")
        heart = material_index("ARCH_Heartwood")
        bottom = [bm.verts.new(p) for p in ((xa, -ya, z0), (xb, -yb, z0),
                                            (xb, yb, z0), (xa, ya, z0))]
        top = [bm.verts.new(p) for p in ((xa, -ya, z0 + thickness), (xb, -yb, z0 + thickness),
                                         (xb, yb, z0 + thickness), (xa, ya, z0 + thickness))]
        bm.faces.new(list(reversed(bottom)))
        face = bm.faces.new(top)
        face.material_index = light
        for a in range(4):
            b = (a + 1) % 4
            face = bm.faces.new((bottom[a], bottom[b], top[b], top[a]))
            face.material_index = heart
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    for material in materials:
        mesh.materials.append(material)
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


for index, (x0, x1) in enumerate(((0.19, 0.57), (0.566, 0.93), (0.926, 1.292))):
    tones = (("ARCH_Sapwood", "ARCH_SapwoodLight") if index % 2 == 0
             else ("ARCH_SapwoodLight", "ARCH_Sapwood"))
    new_obj(f"FLR_board_{index}",
            sector_boards(f"FLR_board_{index}", x0, x1, tones, wedge=(index == 0)), s2,
            loc=(0, 0, DECK_BOTTOM))
# The frame follows the SECTOR TRIANGLE itself: one beam down the radial edge
# and one along the outer hex edge. Straight bars at constant y used to poke out
# through the slanted sides, because a sector only exists from x = |y|/tan30
# outwards. Each sector carries its own -30 deg radial edge, so on a finished
# hex the six sectors close the whole frame without ever doubling a beam.
EDGE_X = 1.272           # hex edge, inset a hair so the beam hides under the deck
EDGE_Y = 0.716           # corner, likewise
new_obj("FLR_edge_beam",
        bowed_stick("FLR_edge_beam", EDGE_Y * 2, JOIST_R, rng, bow=0.005), s1,
        loc=(EDGE_X, -EDGE_Y, JOIST_Z), rot=(math.radians(-90), 0, 0))
radial_length = math.hypot(EDGE_X - 0.03, EDGE_Y - 0.02)
new_obj("FLR_radial_beam",
        bowed_stick("FLR_radial_beam", radial_length, JOIST_R * 1.05, rng, bow=0.004), s1,
        loc=(0.03, -0.02, JOIST_Z - 0.0016), rot=(0, math.radians(90), math.radians(-30)))
# Lashed where the beams actually meet: the outer corner, the hex centre, and
# the far corner where the neighbouring sector's radial beam lands.
# Everything under the deck must STAY under it: the lashings are flattened and
# dropped so their top clears the board underside at 0.0465.
BIND_Z = 0.0255
binds = []
for index, (x, y) in enumerate(((EDGE_X - 0.03, -EDGE_Y + 0.05),
                                (0.075, -0.042),
                                (EDGE_X - 0.03, EDGE_Y - 0.05))):
    binds.append(new_obj(f"FLR_bind_{index}",
                         axis_lashing(f"FLR_bind_{index}", rng, JOIST_R, squash=0.5,
                                      tube=0.0055),
                         s3, loc=(x, y, BIND_Z),
                         rot=(0, 0, math.radians(-30 if index == 1 else 60))))
join(binds, "FLR_rope")

# --------------------------------------------------------------------------- #
# roof sector — one triangle per floor sector, so a room roofs itself
# --------------------------------------------------------------------------- #
# The slope lives INSIDE a hex: every panel rises from the outer hex edge to
# the hex centre. Two neighbouring hexes therefore meet along their shared edge
# at each one's LOW point, at the same height, whatever shape the room has — so
# panels tile by construction and no separate joiner element is needed. The
# frame repeats the floor's trick: each sector carries its own radial rafter
# plus the outer eave beam, so a finished hex closes the frame without doubles.
rng = random.Random(808)
root, (s1, s2, s3) = element_root("HL_ARCH_ROOF", 18.0)
ROOF_RISE = 0.135          # centre peak above the eave
ROOF_EDGE_X = 1.299        # hex apothem: the eave line
ROOF_CORNER_Y = 0.75
THATCH_T = 0.045


def roof_panel(name, x0, x1, tone):
    """One delivered leaf bundle: a thatch band across the sector, tilted so the
    inner end sits ROOF_RISE above the eave."""
    import bmesh
    mesh = bpy.data.meshes.new(name)
    bm = bmesh.new()

    def height(x):
        return ROOF_RISE * (1.0 - x / ROOF_EDGE_X)

    ya, yb = x0 * TAN30 * 0.995, x1 * TAN30 * 0.995
    za, zb = height(x0), height(x1)
    bottom = [bm.verts.new(p) for p in ((x0, -ya, za), (x1, -yb, zb),
                                        (x1, yb, zb), (x0, ya, za))]
    top = [bm.verts.new(p) for p in ((x0, -ya, za + THATCH_T), (x1, -yb, zb + THATCH_T),
                                     (x1, yb, zb + THATCH_T), (x0, ya, za + THATCH_T))]
    bm.faces.new(list(reversed(bottom)))
    bm.faces.new(top)
    for a in range(4):
        b = (a + 1) % 4
        bm.faces.new((bottom[a], bottom[b], top[b], top[a]))
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(bpy.data.materials[tone])
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


# Stage 1 — two sticks: the radial rafter and the eave beam, under the thatch.
new_obj("ROOF_eave_beam",
        bowed_stick("ROOF_eave_beam", ROOF_CORNER_Y * 2 - 0.05, 0.019, rng, bow=0.005), s1,
        loc=(ROOF_EDGE_X - 0.03, -ROOF_CORNER_Y + 0.025, -0.022),
        rot=(math.radians(-90), 0, 0))
rafter_len = math.hypot(ROOF_EDGE_X - 0.04, ROOF_CORNER_Y - 0.03)
new_obj("ROOF_rafter",
        bowed_stick("ROOF_rafter", rafter_len, 0.020, rng, bow=0.005), s1,
        loc=(0.04, -0.03, ROOF_RISE - 0.030),
        rot=(0, math.radians(90) + math.atan2(ROOF_RISE, rafter_len), math.radians(-30)))

# Stage 2 — three thatch bands, one per delivered leaf bundle.
for index, (x0, x1) in enumerate(((0.02, 0.45), (0.44, 0.88), (0.87, ROOF_EDGE_X))):
    tone = "ARCH_Leaf" if index % 2 == 0 else "ARCH_LeafLight"
    new_obj(f"ROOF_thatch_{index}", roof_panel(f"ROOF_thatch_{index}", x0, x1, tone),
            s2, loc=(0, 0, 0))

# Stage 3 — one rope: lashings where the rafter meets the eave and the peak.
binds = []
for index, (x, y, z) in enumerate(((ROOF_EDGE_X - 0.06, -ROOF_CORNER_Y + 0.08, -0.012),
                                   (0.10, -0.055, ROOF_RISE - 0.020))):
    binds.append(new_obj(f"ROOF_bind_{index}",
                         axis_lashing(f"ROOF_bind_{index}", rng, 0.020, squash=0.75,
                                      tube=0.0058),
                         s3, loc=(x, y, z), rot=(0, 0, math.radians(60 if index == 0 else -30))))
join(binds, "ROOF_rope")


# --------------------------------------------------------------------------- #
# indoor hearth — one junction, the colony's small fire
# --------------------------------------------------------------------------- #
# It must stay inside a single junction cell (0.375 wu spacing), so the stone
# ring is 0.235 wu and the spit posts tuck just inside 0.30. It is the same
# craft as the outdoor campfire, built smaller: 7 ring stones instead of 18 and
# 4 sticks instead of 12, with a real spit so meat can roast on it.
from arch_elements_lib import disc, faceted_stone  # noqa: E402

rng = random.Random(807)
root, (s1, s2, s3) = element_root("HL_ARCH_HEARTH", 16.0)
RING_R = 0.232
RING_STONES = 12
SPIT_X = 0.285
SPIT_TOP = 0.30

# Stage 1 — eight sticks: two forked posts, ONE cross bar (the roasting spit)
# and a five-stick pile, the same tepee the outdoor campfire has.
# The bar MUST stay named stick_bar: CampfireSpitMeat finds it by exact node
# name and hangs the six meat slots along its rendered span.
for side, x in (("l", -SPIT_X), ("r", SPIT_X)):
    new_obj(f"HEARTH_post_{side}",
            bowed_stick(f"HEARTH_post_{side}", SPIT_TOP, 0.019, rng, bow=0.004), s1,
            loc=(x, 0, 0.0), rot=(math.radians(rng.uniform(-2, 2)), 0, rng.uniform(0, 3.0)))
new_obj("stick_bar",
        bowed_stick("stick_bar", SPIT_X * 2 + 0.07, 0.017, rng, bow=0.004), s1,
        loc=(-SPIT_X - 0.035, 0, SPIT_TOP - 0.012), rot=(0, math.radians(90), 0))
for index in range(5):
    angle = 2 * math.pi * index / 5 + 0.4
    lean = math.radians(58)
    new_obj(f"HEARTH_pile_{index}",
            bowed_stick(f"HEARTH_pile_{index}", 0.215, 0.017, rng, bow=0.005, facets=6), s1,
            loc=(math.cos(angle) * 0.115, math.sin(angle) * 0.115, 0.018),
            rot=(math.sin(angle) * lean, -math.cos(angle) * lean,
                 rng.uniform(0, 3.0)))

# Stage 2 — a tight ring of stones, one per delivered stone. The arc each stone
# owns is 2*pi*R/12 = 0.121 wu, so radii of 0.062..0.078 make neighbours touch
# or slightly overlap: the joints read as a laid ring, not scattered pebbles.
for index in range(RING_STONES):
    angle = 2 * math.pi * index / RING_STONES + 0.22
    radius = RING_R + rng.uniform(-0.004, 0.004)
    stone = rng.uniform(0.071, 0.086)
    new_obj(f"HEARTH_stone_{index}",
            faceted_stone(f"HEARTH_stone_{index}", rng, stone,
                          flatten=rng.uniform(0.55, 0.72),
                          material="ARCH_StoneLight" if index % 3 == 0 else "ARCH_Stone"),
            s2, loc=(math.cos(angle) * radius, math.sin(angle) * radius,
                     0.036 + rng.uniform(-0.004, 0.006)),
            rot=(rng.uniform(-0.25, 0.25), rng.uniform(-0.25, 0.25),
                 rng.uniform(0, 6.28)))
# Ash, coals and charred logs ship with the ring and cost nothing: "_deco_"
# keeps them out of the per-resource stage reveal.
new_obj("HEARTH_deco_ash", disc("HEARTH_deco_ash", 0.175, 0.030, 9, "ARCH_Ash", rng, dip=0.014),
        s2, loc=(0, 0, 0.0))
for index in range(3):
    angle = 2 * math.pi * index / 3 + 0.6
    new_obj(f"HEARTH_deco_ember_{index}",
            faceted_stone(f"HEARTH_deco_ember_{index}", rng, 0.036, flatten=0.5,
                          material="ARCH_Ember"),
            s2, loc=(math.cos(angle) * 0.062, math.sin(angle) * 0.062, 0.030))
for index, angle in enumerate((0.5, 2.3)):
    new_obj(f"HEARTH_deco_log_{index}",
            bowed_stick(f"HEARTH_deco_log_{index}", 0.26, 0.026, rng, bow=0.004,
                        facets=6), s2,
            loc=(-math.cos(angle) * 0.13, -math.sin(angle) * 0.13, 0.052),
            rot=(0, math.radians(90), angle))
for stone in [c for c in s2.children if "_deco_log_" in c.name]:
    for slot, material in enumerate(stone.data.materials):
        stone.data.materials[slot] = bpy.data.materials["ARCH_Charcoal"]

# Stage 3 — one rope: a lashing where each post meets the bar.
binds = []
for index, x in enumerate((-SPIT_X, SPIT_X)):
    binds.append(new_obj(f"HEARTH_bind_{index}",
                         axis_lashing(f"HEARTH_bind_{index}", rng, 0.019, squash=1.0,
                                      loops=3, tube=0.0055),
                         s3, loc=(x, 0, SPIT_TOP - 0.012), rot=(0, 0, math.radians(90))))
join(binds, "HEARTH_rope")

# The renderer attaches CampfireEffect to this marker (Spec 120.2), so it must
# stay named exactly fire_point and sit at the flame centre, not at the origin.
new_empty("fire_point", root, loc=(0, 0, 0.055))

print("=== architecture elements rebuilt ===")
for name in ("HL_ARCH_WALL", "HL_ARCH_WINDOW", "HL_ARCH_DOOR",
             "HL_ARCH_SUPPORT", "HL_ARCH_FLOOR", "HL_ARCH_ROOF", "HL_ARCH_HEARTH"):
    root = bpy.data.objects[name]
    counts = []
    for stage in sorted(root.children, key=lambda c: c.name):
        units = [c for c in stage.children if c.type == "MESH"]
        nested = [n for c in stage.children if c.name.startswith("HL_Door_Pivot")
                  for n in c.children]
        counts.append(len(units) + len(nested))
    print(f"  {name}: stage units {counts}")
