"""Author the island's compact three-leg stool with the shared HexLive palette.

Run from repository root: blender -b -t 1 -P Tools/blender/author_basic_stool.py
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import sys

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from author_atomic_world_props import collection, material, panel
from author_modern_apartment import (OBJECTS, ROOT, block, export_model, load_dimensions, render_icon,
                                     verify_export, write_unity_material)

RECEIPT = ROOT / "Build/Diagnostics/portal-furniture/chair-basic-verification.json"


def palette():
    values = (
        ("BasicStoolWood", "Sapwood", .53, .89),
        ("BasicStoolBark", "Bark", .71, .92),
        ("BasicStoolRope", "Rope", .67, .97),
    )
    with bpy.data.libraries.load(str(ROOT / "Assets/HexLiveContent/source.blend")) as (source, target):
        names = [entry[1] for entry in values]
        if any(name not in source.materials for name in names):
            raise ValueError("Missing island wood or rope source palette")
        target.materials = names
    shades = {}
    for name, original, factor, roughness in values:
        source = bpy.data.materials[original]
        shader = source.node_tree.nodes.get("Principled BSDF") if source.use_nodes else None
        original_color = shader.inputs["Base Color"].default_value if shader else source.diffuse_color
        rgb = tuple(min(.42, channel * factor) for channel in original_color[:3])
        shades[name] = (rgb, roughness)
    return shades


def pole(owner, name, base, top, base_radius, top_radius, mat, sides=8):
    points = [
        (x + radius * math.cos(i * math.tau / sides),
         y + radius * math.sin(i * math.tau / sides), z)
        for (x, y, z), radius in ((base, base_radius), (top, top_radius))
        for i in range(sides)
    ]
    faces = [tuple(reversed(range(sides))), tuple(range(sides, 2 * sides))]
    faces.extend((i, (i + 1) % sides, (i + 1) % sides + sides, i + sides) for i in range(sides))
    panel(owner, name, points, faces, mat)


def stool(mats):
    owner = collection("chair.basic")
    for i, (x, y, width, length) in enumerate((
        (-.128, -.002, .120, .378),
        (0, 0, .121, .410),
        (.128, .007, .120, .391),
    ), 1):
        block(owner, f"split_seat_board_{i}", (x, y, .274), (width, length, .052),
              mats["BasicStoolWood"], .006)
    for i, (lower, upper) in enumerate((
        ((-.147, -.140, 0), (-.104, -.101, .252)),
        ((.147, -.140, 0), (.104, -.101, .252)),
        ((0, .169, 0), (0, .124, .252)),
    ), 1):
        pole(owner, f"splayed_leg_{i}", lower, upper, .023, .021, mats["BasicStoolBark"])
        for knot, height in enumerate((.216, .230), 1):
            t = height / upper[2]
            x = lower[0] + (upper[0] - lower[0]) * t
            y = lower[1] + (upper[1] - lower[1]) * t
            pole(owner, f"rope_lashing_{i}_{knot}", (x, y, height), (x, y, height + .010),
                 .027, .027, mats["BasicStoolRope"], sides=6)
    block(owner, "front_cleat", (0, -.100, .236), (.259, .038, .030), mats["BasicStoolBark"])
    block(owner, "spine_cleat", (0, .020, .240), (.052, .257, .025), mats["BasicStoolBark"])
    return owner


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    shades = palette()
    mats = {name: material(name, (*rgb, 1), roughness) for name, (rgb, roughness) in shades.items()}
    for name in mats:
        write_unity_material(name, directory=OBJECTS, palette=shades)
    row = export_model("chair.basic", stool(mats), "low", True, material_directory=OBJECTS, origin="primal")
    row["placementSurfaceHeight"] = .300
    if row["triangles"] > 400:
        raise ValueError("Basic stool exceeded the primitive prop budget")
    radius, apothem, _ = load_dimensions()
    meshes = verify_export(row, radius, apothem)
    render_icon(row, meshes)
    RECEIPT.parent.mkdir(parents=True, exist_ok=True)
    RECEIPT.write_text(json.dumps(row, indent=2) + "\n", encoding="utf-8")
    print("VERIFIED basic stool geometry, seat height and rendered icon", flush=True)


if __name__ == "__main__":
    main()

