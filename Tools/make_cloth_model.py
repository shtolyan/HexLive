"""Build the low-poly folded cloth prop ``resource.cloth``.

Run from the repository root:

    /Applications/Blender.app/Contents/MacOS/Blender -b \
        -P Tools/make_cloth_model.py

The previous prop was three bevelled boxes, so its silhouette read as a stack
of cushions.  This version is still deliberately cheap, but each folded layer
is an irregular, faceted fabric shell with a shallow crease field.  Two raised
selvedges make the material readable from the normal game camera.

The old 0.643521 x 0.465819 x 0.247500 bounding box is kept: ObjectFit uses a
single size for ground and hand presentation, so changing the authored extent
would change both.  The palette is derived from the canonical ``Rope``
material in ``Assets/HexLiveContent/source.blend``; the hues remain the indigo
and dark edge colours already established for this resource.
"""

from __future__ import annotations

import math
import os
import random

import bpy
from mathutils import Vector


REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOURCE_BLEND = os.path.join(REPO, "Assets/HexLiveContent/source.blend")
OUT_FBX = os.path.join(
    REPO,
    "Assets/HexLiveContent/RuntimeSource/Objects/resource.cloth.fbx",
)

# Preserve the previous model's authored bounds exactly (Blender axes).
TARGET_X = 0.643521
TARGET_Y = 0.465819
TARGET_Z = 0.247500

GRID_X = 4
GRID_Y = 4
SEED = 7321

# One stable colour block.  The shared Rope material supplies the shader
# topology; these values only derive its two cloth-specific colourways.
INDIGO = (0.070, 0.215, 0.290, 1.0)
EDGE = (0.032, 0.105, 0.155, 1.0)


def _principled(material):
    return next(
        node for node in material.node_tree.nodes
        if node.type == "BSDF_PRINCIPLED"
    )


def _set_principled(material, rgba, roughness):
    material.diffuse_color = rgba
    material.use_nodes = True
    shader = _principled(material)
    shader.inputs["Base Color"].default_value = rgba
    shader.inputs["Roughness"].default_value = roughness
    if "Specular" in shader.inputs:
        shader.inputs["Specular"].default_value = 0.12
    if "Specular IOR Level" in shader.inputs:
        shader.inputs["Specular IOR Level"].default_value = 0.12
    if "Sheen" in shader.inputs:
        shader.inputs["Sheen"].default_value = 0.08
    if "Sheen Weight" in shader.inputs:
        shader.inputs["Sheen Weight"].default_value = 0.08


def palette():
    """Copy the real project Rope shader and derive the cloth colour pair."""
    with bpy.data.libraries.load(SOURCE_BLEND, link=False) as (source, target):
        assert "Rope" in source.materials, "source.blend has no Rope material"
        target.materials = ["Rope"]

    reference = bpy.data.materials["Rope"]
    textile = reference.copy()
    textile.name = "Cloth.Indigo"
    _set_principled(textile, INDIGO, 0.96)

    edge = reference.copy()
    edge.name = "Cloth.Edge"
    _set_principled(edge, EDGE, 0.93)

    bpy.data.materials.remove(reference)
    return textile, edge


def _rotate_xy(x, y, radians):
    cosine = math.cos(radians)
    sine = math.sin(radians)
    return x * cosine - y * sine, x * sine + y * cosine


def _layer_mesh(
    name,
    *,
    centre,
    size,
    z_bottom,
    z_top,
    rotation_degrees,
    phase,
    ridge_height=0.0,
    ridge_v=-0.333333,
    ridge_width=0.50,
    preserve_outer_bounds=False,
    force_top_peak=False,
):
    """Make one closed folded layer from two matching, low-resolution grids."""
    rng = random.Random(SEED + phase * 101)
    cx, cy = centre
    width, depth = size
    angle = math.radians(rotation_degrees)
    vertices = []

    # Shared x/y coordinates keep the sides closed while both surfaces can
    # carry a different crease field.  Boundary points retreat by millimetres
    # in a deterministic pattern, avoiding a machine-cut rectangular outline.
    xy = []
    for iy in range(GRID_Y + 1):
        row = []
        v = -1.0 + 2.0 * iy / GRID_Y
        for ix in range(GRID_X + 1):
            u = -1.0 + 2.0 * ix / GRID_X
            x = u * width * 0.5
            y = v * depth * 0.5

            if preserve_outer_bounds:
                # The broad bottom fold alone defines the legacy bounding box.
                if ix == 0 and iy != 1:
                    x += 0.004 + 0.002 * ((iy + phase) % 2)
                elif ix == GRID_X and iy != 2:
                    x -= 0.004 + 0.002 * ((iy + phase) % 2)
                if iy == 0 and ix != 2:
                    y += 0.004 + 0.0015 * ((ix + phase) % 2)
                elif iy == GRID_Y and ix != 2:
                    y -= 0.004 + 0.0015 * ((ix + phase) % 2)
            else:
                if ix in (0, GRID_X):
                    x += (-u) * rng.uniform(0.0015, 0.0060)
                if iy in (0, GRID_Y):
                    y += (-v) * rng.uniform(0.0015, 0.0050)

            if 0 < ix < GRID_X and 0 < iy < GRID_Y:
                x += rng.uniform(-0.0050, 0.0050)
                y += rng.uniform(-0.0040, 0.0040)

            x, y = _rotate_xy(x, y, angle)
            row.append((cx + x, cy + y))
        xy.append(row)

    count_per_surface = (GRID_X + 1) * (GRID_Y + 1)
    for surface in (0, 1):
        for iy in range(GRID_Y + 1):
            v = -1.0 + 2.0 * iy / GRID_Y
            for ix in range(GRID_X + 1):
                u = -1.0 + 2.0 * ix / GRID_X
                x, y = xy[iy][ix]
                crease = (
                    math.sin((u * 1.55 + phase * 0.31) * math.pi)
                    * math.cos((v * 1.25 - phase * 0.23) * math.pi)
                )
                diagonal = math.sin((u + v * 0.65 + phase * 0.17) * math.pi)
                # Both surfaces follow the same broad fold, keeping the fabric
                # thin even where it rises high.  This is the visual difference
                # between a folded textile and three thick padded boxes.
                ridge = ridge_height * math.exp(
                    -((v - ridge_v) / ridge_width) ** 2
                ) * (0.90 + 0.10 * math.cos((u + phase * 0.11) * math.pi))
                if surface == 0:
                    z = z_bottom + ridge + 0.0022 * crease - 0.0010 * diagonal
                else:
                    z = z_top + ridge + 0.0065 * crease + 0.0025 * diagonal
                z = min(TARGET_Z, max(0.0, z))
                if preserve_outer_bounds and surface == 0 and ix == 2 and iy == 0:
                    z = 0.0
                if force_top_peak and surface == 1 and ix == 2 and iy == 1:
                    z = TARGET_Z
                vertices.append((x, y, z))

    def index(surface, ix, iy):
        return surface * count_per_surface + iy * (GRID_X + 1) + ix

    faces = []
    # Bottom and top quads point outwards; Unity triangulates these deterministically.
    for iy in range(GRID_Y):
        for ix in range(GRID_X):
            faces.append((
                index(0, ix, iy), index(0, ix, iy + 1),
                index(0, ix + 1, iy + 1), index(0, ix + 1, iy),
            ))
            faces.append((
                index(1, ix, iy), index(1, ix + 1, iy),
                index(1, ix + 1, iy + 1), index(1, ix, iy + 1),
            ))

    # Close all four fabric edges.
    for ix in range(GRID_X):
        faces.append((index(0, ix, 0), index(0, ix + 1, 0),
                      index(1, ix + 1, 0), index(1, ix, 0)))
        faces.append((index(0, ix + 1, GRID_Y), index(0, ix, GRID_Y),
                      index(1, ix, GRID_Y), index(1, ix + 1, GRID_Y)))
    for iy in range(GRID_Y):
        faces.append((index(0, 0, iy + 1), index(0, 0, iy),
                      index(1, 0, iy), index(1, 0, iy + 1)))
        faces.append((index(0, GRID_X, iy), index(0, GRID_X, iy + 1),
                      index(1, GRID_X, iy + 1), index(1, GRID_X, iy)))

    mesh = bpy.data.meshes.new(name + ".mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update(calc_edges=True)
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def _combine_layer_meshes(meshes):
    """Join authored layers into one renderer without applying any scaling."""
    objects = []
    for index, mesh in enumerate(meshes):
        obj = bpy.data.objects.new(f"cloth.fold.{index}", mesh)
        bpy.context.scene.collection.objects.link(obj)
        objects.append(obj)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.object.join()
    objects[0].name = "mesh.Cloth.Indigo"
    return objects[0]


def _selvedge_mesh():
    """Two dark, slightly wavy raised hems on the top fold."""
    vertices = []
    faces = []
    segments = 5
    half_width = 0.0065
    half_height = 0.0035
    for strip, (y_base, base_z) in enumerate(((-0.174, 0.106), (0.166, 0.086))):
        start = len(vertices)
        for i in range(segments + 1):
            t = i / segments
            x = -0.205 + t * 0.420
            y = y_base + math.sin((t * 2.0 + strip * 0.35) * math.pi) * 0.004
            z = base_z + math.sin((t * 1.55 + strip * 0.4) * math.pi) * 0.004
            vertices.extend((
                (x, y - half_width, z - half_height),
                (x, y + half_width, z - half_height),
                (x, y + half_width, z + half_height),
                (x, y - half_width, z + half_height),
            ))
        for i in range(segments):
            a = start + i * 4
            b = a + 4
            faces.extend((
                (a + 0, b + 0, b + 1, a + 1),
                (a + 1, b + 1, b + 2, a + 2),
                (a + 2, b + 2, b + 3, a + 3),
                (a + 3, b + 3, b + 0, a + 0),
            ))
        faces.append(tuple(start + i for i in (3, 2, 1, 0)))
        end = start + segments * 4
        faces.append(tuple(end + i for i in (0, 1, 2, 3)))

    mesh = bpy.data.meshes.new("mesh.Cloth.Edge.mesh")
    mesh.from_pydata(vertices, [], faces)
    mesh.update(calc_edges=True)
    for polygon in mesh.polygons:
        polygon.use_smooth = False
    return mesh


def _bounds(objects):
    lo = Vector((1e9, 1e9, 1e9))
    hi = Vector((-1e9, -1e9, -1e9))
    for obj in objects:
        for corner in obj.bound_box:
            point = obj.matrix_world @ Vector(corner)
            for axis in range(3):
                lo[axis] = min(lo[axis], point[axis])
                hi[axis] = max(hi[axis], point[axis])
    return lo, hi


def main():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    textile, edge = palette()

    layers = [
        _layer_mesh(
            "cloth.bottom",
            centre=(0.0, 0.0), size=(TARGET_X, TARGET_Y),
            z_bottom=0.001, z_top=0.028,
            rotation_degrees=0.0, phase=0,
            ridge_height=0.008, ridge_v=0.25, ridge_width=0.70,
            preserve_outer_bounds=True,
        ),
        _layer_mesh(
            "cloth.middle",
            centre=(-0.020, 0.008), size=(0.555, 0.414),
            z_bottom=0.033, z_top=0.056,
            rotation_degrees=2.3, phase=1,
            ridge_height=0.055, ridge_v=0.333333, ridge_width=0.58,
        ),
        _layer_mesh(
            "cloth.top",
            centre=(0.022, -0.012), size=(0.488, 0.368),
            z_bottom=0.062, z_top=0.082,
            rotation_degrees=-3.2, phase=2,
            ridge_height=0.164, ridge_v=-0.333333, ridge_width=0.48,
            force_top_peak=True,
        ),
    ]
    body = _combine_layer_meshes(layers)
    body.data.materials.append(textile)

    hem = bpy.data.objects.new("mesh.Cloth.Edge", _selvedge_mesh())
    bpy.context.scene.collection.objects.link(hem)
    hem.data.materials.append(edge)

    objects = [body, hem]
    lo, hi = _bounds(objects)
    dimensions = hi - lo
    expected = (TARGET_X, TARGET_Y, TARGET_Z)
    for measured, target in zip(dimensions, expected):
        assert abs(measured - target) < 0.00001, (tuple(dimensions), expected)

    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = body
    bpy.ops.export_scene.fbx(
        filepath=OUT_FBX,
        use_selection=True,
        object_types={"MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z",
        axis_up="Y",
        mesh_smooth_type="FACE",
        use_mesh_modifiers=False,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="STRIP",
        embed_textures=False,
    )

    vertices = sum(len(obj.data.vertices) for obj in objects)
    polygons = sum(len(obj.data.polygons) for obj in objects)
    triangles = sum(
        sum(len(poly.vertices) - 2 for poly in obj.data.polygons)
        for obj in objects
    )
    print(
        "CLOTH",
        "dims=", tuple(round(value, 6) for value in dimensions),
        "verts=", vertices,
        "polys=", polygons,
        "tris=", triangles,
        "materials=", [textile.name, edge.name],
    )
    print("WROTE", OUT_FBX)


if __name__ == "__main__":
    main()
