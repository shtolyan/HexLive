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
for index, y in ((0, -0.40), (1, 0.40)):
    new_obj(f"FLR_joist_{index}",
            bowed_stick(f"FLR_joist_{index}", 1.19, JOIST_R, rng, bow=0.005), s1,
            loc=(0.07, y, JOIST_Z), rot=(0, math.radians(90), rng.uniform(0, 3.0)))
# One radial edge beam per sector: every sector carries its own -30 deg edge, so
# each of the six seams between sectors is covered exactly once.
new_obj("FLR_seam_beam",
        bowed_stick("FLR_seam_beam", 1.47, JOIST_R * 1.05, rng, bow=0.004), s1,
        loc=(0.02, -0.012, JOIST_Z), rot=(0, math.radians(90), math.radians(-30)))
binds = []
for index, y in ((0, -0.40), (1, 0.40)):
    for step, x in ((0, 0.34), (1, 0.94)):
        binds.append(new_obj(f"FLR_bind_{index}{step}",
                             axis_lashing(f"FLR_bind_{index}{step}", rng, JOIST_R, squash=0.55),
                             s3, loc=(x, y, JOIST_Z)))
join(binds, "FLR_rope")

print("=== architecture elements rebuilt ===")
for name in ("HL_ARCH_WALL", "HL_ARCH_WINDOW", "HL_ARCH_DOOR",
             "HL_ARCH_SUPPORT", "HL_ARCH_FLOOR"):
    root = bpy.data.objects[name]
    counts = []
    for stage in sorted(root.children, key=lambda c: c.name):
        units = [c for c in stage.children if c.type == "MESH"]
        nested = [n for c in stage.children if c.name.startswith("HL_Door_Pivot")
                  for n in c.children]
        counts.append(len(units) + len(nested))
    print(f"  {name}: stage units {counts}")
