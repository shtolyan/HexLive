"""Generate the low-poly `tool.sharp_stone` world/hand prop.

Run headlessly with the project's pinned Blender:

    /Applications/Blender.app/Contents/MacOS/Blender -b \
        -P Tools/make_sharp_stone_model.py

The prop is a deliberately primitive knapped flake: no handle and no lashing.
Its lower rounded end is the grip/pivot at Z=0 and the thin chipped point faces
+Z, the same working direction as the project's other tools. Runtime size is
controlled by ObjectFit, not by scaling this mesh.
"""

from __future__ import annotations

import os
import random

import bpy


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_BLEND = os.path.join(REPO, "Assets/HexLiveContent/source.blend")
OUT_FBX = os.path.join(
    REPO,
    "Assets/HexLiveContent/RuntimeSource/Objects/tool.sharp_stone.fbx",
)
SEED = 41731

# Five cross-sections make a palm-sized, leaf-shaped flake.  The mesh's unit
# dimensions are descriptive only; ObjectFit is the single runtime size knob.
ROWS = (
    ((0.000, 0.000),),
    ((-0.135, 0.115), (0.012, 0.105), (0.163, 0.128)),
    ((-0.202, 0.300), (-0.008, 0.315), (0.184, 0.292)),
    ((-0.142, 0.515), (0.005, 0.500), (0.126, 0.526)),
    ((0.000, 0.720),),
)


def _principled(material):
    return next(node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED")


def _base_colour(material):
    bsdf = _principled(material)
    return tuple(bsdf.inputs["Base Color"].default_value[:3])


def make_palette():
    """Derive every facet colour from the project's canonical StoneGrey."""
    with bpy.data.libraries.load(SOURCE_BLEND, link=False) as (source, target):
        assert "StoneGrey" in source.materials, "source.blend has no StoneGrey material"
        target.materials = ["StoneGrey"]

    stone = bpy.data.materials["StoneGrey"]
    stone.use_nodes = True
    base = _base_colour(stone)

    def shade(name, multiplier, warmth=0.0):
        material = stone.copy()
        material.name = name
        rgb = (
            min(1.0, base[0] * multiplier + warmth),
            min(1.0, base[1] * multiplier + warmth * 0.55),
            min(1.0, base[2] * multiplier),
        )
        bsdf = _principled(material)
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.94
        if "Specular" in bsdf.inputs:
            bsdf.inputs["Specular"].default_value = 0.12
        return material

    # StoneGrey itself is the anchor; the copies create readable knapping under
    # the shared four-sun item rig without introducing a foreign hue.
    palette = [
        shade("SharpStone_Mid", 0.82),
        shade("SharpStone_Light", 1.04, 0.012),
        shade("SharpStone_Dark", 0.61),
        shade("SharpStone_Edge", 0.90, 0.018),
    ]
    bpy.data.materials.remove(stone)
    return palette


def _front_z(row_index, column_index, column_count):
    if column_count == 1:
        return (0.032, 0.022, 0.014, 0.010, 0.003)[row_index]
    if column_index == 1:
        return (0.0, 0.082, 0.098, 0.067, 0.0)[row_index]
    return (0.0, 0.018, 0.012, 0.010, 0.0)[row_index]


def _back_z(row_index, column_index, column_count):
    if column_count == 1:
        return (-0.022, -0.019, -0.014, -0.009, -0.002)[row_index]
    if column_index == 1:
        return (0.0, -0.055, -0.063, -0.044, 0.0)[row_index]
    return (0.0, -0.017, -0.013, -0.010, 0.0)[row_index]


def build_mesh():
    rng = random.Random(SEED)
    vertices = []
    front_rows = []
    back_rows = []

    for row_index, row in enumerate(ROWS):
        front = []
        back = []
        for column_index, (x, y) in enumerate(row):
            # A millimetric deterministic wobble keeps the silhouette knapped,
            # while the point and grip remain centered for a stable hand pose.
            wobble = 0.0 if len(row) == 1 else rng.uniform(-0.006, 0.006)
            front.append(len(vertices))
            vertices.append((x + wobble, _front_z(row_index, column_index, len(row)), y))
            back.append(len(vertices))
            vertices.append((x - wobble * 0.35, _back_z(row_index, column_index, len(row)), y))
        front_rows.append(front)
        back_rows.append(back)

    faces = []
    materials = []

    def add(a, b, c, material):
        faces.append((a, b, c))
        materials.append(material)

    def skin(rows, reverse=False):
        # 1 -> 3 at the grip.
        r0, r1 = rows[0], rows[1]
        tris = ((r0[0], r1[0], r1[1]), (r0[0], r1[1], r1[2]))
        for i, tri in enumerate(tris):
            add(*(tri[::-1] if reverse else tri), (2 if reverse else i))

        # Two 3 -> 3 bands.  Alternating diagonals expose large chipped facets.
        for band in (1, 2):
            low, high = rows[band], rows[band + 1]
            for cell in (0, 1):
                if (band + cell) % 2 == 0:
                    tris = ((low[cell], high[cell], high[cell + 1]),
                            (low[cell], high[cell + 1], low[cell + 1]))
                else:
                    tris = ((low[cell], high[cell], low[cell + 1]),
                            (low[cell + 1], high[cell], high[cell + 1]))
                for tri_index, tri in enumerate(tris):
                    material = (band + cell + tri_index + (2 if reverse else 0)) % 3
                    add(*(tri[::-1] if reverse else tri), material)

        # 3 -> 1 at the working point.
        r3, r4 = rows[3], rows[4]
        tris = ((r3[0], r4[0], r3[1]), (r3[1], r4[0], r3[2]))
        for i, tri in enumerate(tris):
            add(*(tri[::-1] if reverse else tri), (1 if i == 0 else 3))

    skin(front_rows, reverse=False)
    skin(back_rows, reverse=True)

    # Stitch the perimeter.  Side triangles use the brightest edge shade so the
    # razor-thin chipped rim stays legible in the world and in a 512 px icon.
    perimeter = ((0, 0), (1, 0), (2, 0), (3, 0), (4, 0),
                 (3, 2), (2, 2), (1, 2))
    for edge_index, ((ra, ca), (rb, cb)) in enumerate(
            zip(perimeter, perimeter[1:] + perimeter[:1])):
        fa, fb = front_rows[ra][ca], front_rows[rb][cb]
        ba, bb = back_rows[ra][ca], back_rows[rb][cb]
        if edge_index % 2:
            add(fa, bb, ba, 3)
            add(fa, fb, bb, 3)
        else:
            add(fa, fb, ba, 3)
            add(fb, bb, ba, 3)

    mesh = bpy.data.meshes.new("tool.sharp_stone")
    mesh.from_pydata(vertices, [], faces)
    mesh.validate(verbose=True)
    mesh.update(calc_edges=True)
    for polygon, material_index in zip(mesh.polygons, materials):
        polygon.material_index = material_index
        polygon.use_smooth = False
    mesh.calc_normals()
    return mesh


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    materials = make_palette()
    mesh = build_mesh()
    for material in materials:
        mesh.materials.append(material)

    obj = bpy.data.objects.new("tool.sharp_stone", mesh)
    bpy.context.scene.collection.objects.link(obj)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)

    bpy.ops.export_scene.fbx(
        filepath=OUT_FBX,
        use_selection=True,
        apply_unit_scale=True,
        object_types={"MESH"},
        mesh_smooth_type="FACE",
        use_mesh_modifiers=False,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="STRIP",
        axis_forward="-Z",
        axis_up="Y",
    )

    triangles = sum(len(p.vertices) - 2 for p in mesh.polygons)
    print(
        "SHARP_STONE dims=",
        tuple(round(v, 4) for v in obj.dimensions),
        "verts=",
        len(mesh.vertices),
        "polys=",
        len(mesh.polygons),
        "tris=",
        triangles,
    )
    print("WROTE", OUT_FBX)


main()
